using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Crafting;

/// <summary>What furnaces and processing machines made and burnt between two of their states, for the production statistics.
/// A state worked further is always saved after this is counted, so the same batch is never counted twice.</summary>
internal static class MachineProduction
{
    public static List<ProductionEntry> Smelted(FurnaceLevel level, FurnaceState before, FurnaceState after)
    {
        var entries = new List<ProductionEntry>();
        var n = before.InputQuantity - after.InputQuantity;
        if (n <= 0 || before.StartedAt is not { } started || Smelting.For(before.Input) is not { } recipe)
        {
            return entries;
        }

        var time = level.Time(recipe);
        var first = started + time;
        entries.AddRange(ProductionEntry.Batches(recipe.Output, 1, true, first, time, n));
        entries.AddRange(ProductionEntry.Batches(recipe.Input, 1, false, first, time, n));
        entries.AddRange(Burnt(Items.Coal, before.Heat, level.SmeltsPerCoal, first, time, n));
        return entries;
    }

    public static List<ProductionEntry> Processed(ProcessingMachine machine, MachineState before, MachineState after)
    {
        var entries = new List<ProductionEntry>();
        if (before.StartedAt is not { } started || machine.Recipe(before.Recipe) is not { } recipe)
        {
            return entries;
        }

        var n = (after.Output - before.Output) / recipe.Quantity;
        if (n <= 0)
        {
            return entries;
        }

        // Spread evenly over the time they took, slower ones of a machine waiting for its service included.
        var step = (after.StartedAt ?? started + MachineServicing.BatchTime(recipe, before, started) * n) - started;
        step = step > TimeSpan.Zero ? step / n : recipe.Time;
        var first = started + step;
        entries.AddRange(ProductionEntry.Batches(recipe.Id, recipe.Quantity, true, first, step, n));
        foreach (var (item, quantity) in recipe.Extras)
        {
            entries.AddRange(ProductionEntry.Batches(item, quantity, true, first, step, n));
        }

        foreach (var (item, quantity) in recipe.Ingredients)
        {
            entries.AddRange(ProductionEntry.Batches(item, quantity, false, first, step, n));
        }

        if (machine.Fired && before.FuelItem is { } fuel && Processing.Fuels.TryGetValue(fuel, out var perFuel))
        {
            entries.AddRange(Burnt(fuel, before.Heat, perFuel, first, step, n));
        }

        return entries;
    }

    /// <summary>As above for a machine that works in its own time (<paramref name="clock"/>, on power): its batches
    /// are moved to the time on the plot by how far the machine is behind at <paramref name="now"/>.</summary>
    public static List<ProductionEntry> Processed(ProcessingMachine machine, MachineState before, MachineState after, IPowerClock clock, DateTimeOffset now)
    {
        var entries = Processed(machine, before, after);
        var behind = now - clock.At(now);
        return behind <= TimeSpan.Zero ? entries : entries.Select(e => e with { At = e.At + behind }).ToList();
    }

    /// <summary>One piece of fuel goes on the fire for the batch that finds no heat left, then lasts <paramref name="perFuel"/> batches.</summary>
    private static IEnumerable<ProductionEntry> Burnt(string fuel, int heat, int perFuel, DateTimeOffset first, TimeSpan step, int batches)
    {
        for (var i = heat; i < batches; i += perFuel)
        {
            yield return ProductionEntry.Used(fuel, 1, first + step * i);
        }
    }
}

/// <summary>What each of the player's furnaces and processing machines is doing now.</summary>
public sealed class CraftingMachineStatuses(GameDb db, IPlotPower power, IPlotObjects plots, IPlotRegistry registry, TimeProvider time) : IMachineStatuses
{
    public Task<IReadOnlyList<MachineStatus>> StatusesAsync(Guid owner, CancellationToken cancellationToken) =>
        ReadAsync("owner_id = @owner", "owner", owner, cancellationToken);

    public Task<IReadOnlyList<MachineStatus>> StatusesOnPlotAsync(int x, int y, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? Task.FromResult<IReadOnlyList<MachineStatus>>([])
            : ReadAsync("object_id = ANY(@ids)", "ids", ids.ToArray(), cancellationToken);

    /// <summary>The furnaces and machines <paramref name="where"/> picks, with <paramref name="value"/> as its one parameter.</summary>
    private async Task<IReadOnlyList<MachineStatus>> ReadAsync(string where, string parameter, object value, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var statuses = new List<MachineStatus>();
        await using (var furnaces = await db.CommandAsync(
            $"""
            SELECT object_id, input_item, input_quantity, fuel, heat, output_item, output_quantity, started_at, kind
            FROM crafting.furnaces WHERE {where}
            """, cancellationToken))
        {
            furnaces.Parameters.AddWithValue(parameter, value);
            await using var reader = await furnaces.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(8);
                var level = Smelting.Level(kind) ?? Smelting.Stone;
                var state = Smelting.Advance(new FurnaceState(
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)), level, now);
                statuses.Add(Furnace(reader.GetGuid(0), state, level.Kind) with { Fired = true });
            }
        }

        var stored = new List<(Guid Id, ProcessingMachine Machine, MachineState State)>();
        await using (var machines = await db.CommandAsync(
            $"""
            SELECT object_id, kind, recipe, inputs, fuel_item, fuel, heat, output, started_at, extras, service_due
            FROM crafting.machines WHERE {where}
            """, cancellationToken))
        {
            machines.Parameters.AddWithValue(parameter, value);
            await using var reader = await machines.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!Processing.ByKind.TryGetValue(reader.GetString(1), out var machine))
                {
                    continue;
                }

                stored.Add((reader.GetGuid(0), machine, new MachineState(
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<int[]>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<int[]>(9),
                    reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10))));
            }
        }

        // Machines on power work in their own time; one at work with no power at all stalls for it.
        foreach (var (id, plain, state) in stored)
        {
            var machine = (await Processing.OnPlotAsync(plain.Kind, db, plots, registry, id, now, cancellationToken))!;
            var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
            var status = Machine(id, machine, Processing.Advance(machine, state, clock.At(now))) with { Fired = machine.Fired };
            statuses.Add(status.Activity == MachineActivity.Working && machine.Powered && clock.Speed(now) <= 0
                ? status with { Activity = MachineActivity.NoPower }
                : status);
        }

        return statuses;
    }

    public static MachineStatus Furnace(Guid id, FurnaceState s, string kind = Items.Furnace)
    {
        var product = Smelting.For(s.Input)?.Output ?? s.Output;
        if (s.StartedAt is not null)
        {
            return new(id, kind, product, MachineActivity.Working, []);
        }

        // Without ore the furnace does not say which one it waits for.
        return s.InputQuantity == 0
            ? new(id, kind, product, MachineActivity.NoInput, s.Input is { } ore ? [ore] : [])
            : new(id, kind, product, MachineActivity.NoFuel, [Items.Coal]);
    }

    public static MachineStatus Machine(Guid id, ProcessingMachine machine, MachineState s)
    {
        if (machine.Recipe(s.Recipe) is not { } recipe)
        {
            return new(id, machine.Kind, null, MachineActivity.NoRecipe, []);
        }

        if (s.StartedAt is not null)
        {
            return new(id, machine.Kind, recipe.Id, MachineActivity.Working, []);
        }

        if (s.Output + recipe.Quantity > Processing.MaxOutput || recipe.Extras.Where((e, i) => s.Extra(i) + e.Quantity > Processing.MaxOutput).Any())
        {
            return new(id, machine.Kind, recipe.Id, MachineActivity.OutputFull, []);
        }

        var missing = recipe.Ingredients.Where((ingredient, i) => s.Input(i) < ingredient.Quantity).Select(i => i.Item).ToList();
        if (missing.Count > 0)
        {
            return new(id, machine.Kind, recipe.Id, MachineActivity.NoInput, missing);
        }

        return new(id, machine.Kind, recipe.Id, MachineActivity.NoFuel, Processing.Fuels.Keys.ToList());
    }
}
