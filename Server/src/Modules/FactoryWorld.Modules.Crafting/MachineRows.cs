using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Crafting;

/// <summary>
/// Machines wear: <see cref="Every"/> of their own time after they first work, or after their last service, they work
/// <see cref="SlowerPercent"/>% slower until the owner pays for a service, a share of what the trader asks for the machine.
/// </summary>
public static class MachineServicing
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(3);

    public const int SlowerPercent = 20;

    /// <summary>A service costs this share of the machine's price at the trader.</summary>
    public const int PricePercent = 10;

    /// <summary>For machines the trader does not sell.</summary>
    public const long MinCents = 100;

    public static long Cents(string kind, IMarketPrices prices) =>
        Math.Max(MinCents, (prices.SellsCents(kind) ?? 0) * PricePercent / 100);

    /// <summary>A batch at <see cref="SlowerPercent"/>% less speed.</summary>
    public static TimeSpan WornTime(ProcessingRecipe recipe) => recipe.Time * 100 / (100 - SlowerPercent);

    /// <summary>Whether a batch started at <paramref name="start"/> (machine time) is made by a machine that waits for its service.</summary>
    public static bool Worn(MachineState s, DateTimeOffset start) => s.ServiceDue is { } due && start >= due;

    public static TimeSpan BatchTime(ProcessingRecipe recipe, MachineState s, DateTimeOffset start) => Worn(s, start) ? WornTime(recipe) : recipe.Time;

    /// <summary>How many batches from <paramref name="started"/> on start before the service is due: <see cref="int.MaxValue"/> without a due date.</summary>
    public static int FreshBatches(ProcessingRecipe recipe, MachineState s, DateTimeOffset started) =>
        s.ServiceDue is not { } due ? int.MaxValue
        : due <= started ? 0
        : (int)Math.Min(int.MaxValue, ((due - started).Ticks + recipe.Time.Ticks - 1) / recipe.Time.Ticks);
}

/// <param name="Recipe">The recipe the player set, or null.</param>
/// <param name="Inputs">How much of each of the recipe's ingredients is loaded, in the recipe's order.</param>
/// <param name="FuelItem">What lies by the fire (coal or wood), or null.</param>
/// <param name="Heat">Batches left from fuel already burning.</param>
/// <param name="Output">Products made from the recipe, waiting to be taken out.</param>
/// <param name="StartedAt">When the batch being made now started; null while the machine stands idle.</param>
/// <param name="Extras">How much of each of the recipe's <see cref="ProcessingRecipe.Extras"/> waits to be taken out, in the recipe's order.</param>
/// <param name="ServiceDue">In the machine's own time, when it needs a service; batches started from then on take longer. Null until it first works.</param>
public sealed record MachineState(
    string? Recipe, IReadOnlyList<int> Inputs, string? FuelItem, int Fuel, int Heat, int Output, DateTimeOffset? StartedAt,
    IReadOnlyList<int>? Extras = null, DateTimeOffset? ServiceDue = null)
{
    public static readonly MachineState Empty = new(null, [], null, 0, 0, 0, null);

    public int Input(int i) => i < Inputs.Count ? Inputs[i] : 0;

    public int Extra(int i) => Extras is { } extras && i < extras.Count ? extras[i] : 0;

    /// <summary>Whether anything made waits in the machine: the product or what came with it.</summary>
    public bool HasOutput => Output > 0 || (Extras?.Any(e => e > 0) ?? false);
}

/// <param name="MachineTime">The machine's own time at <paramref name="ServerTime"/>, which <see cref="MachineState.StartedAt"/> is in.</param>
/// <param name="Speed">How fast it works on the power it has: 1 for machines that need none.</param>
/// <param name="ServiceCents">What a service of the machine costs.</param>
public sealed record MachineReply(
    Guid Id, ProcessingMachine Machine, MachineState State, DateTimeOffset ServerTime, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp,
    DateTimeOffset MachineTime = default, double Speed = 1, long ServiceCents = 0, long? BalanceCents = null);

/// <summary>Rows of <c>crafting.machines</c>; a machine gets one when it is first used.</summary>
internal static class MachineRows
{
    private const string Columns = "kind, recipe, inputs, fuel_item, fuel, heat, output, started_at, extras, service_due";

    private const string Write =
        """
        INSERT INTO crafting.machines (object_id, owner_id, kind, recipe, inputs, fuel_item, fuel, heat, output, started_at, extras, service_due)
        VALUES (@id, @owner, @kind, @recipe, @inputs, @fuelItem, @fuel, @heat, @output, @started, @extras, @serviceDue)
        ON CONFLICT (object_id) DO UPDATE SET
            recipe = @recipe, inputs = @inputs, fuel_item = @fuelItem, fuel = @fuel, heat = @heat,
            output = @output, started_at = @started, extras = @extras, service_due = @serviceDue
        """;

    internal static (Type, Guid) Key(Guid id) => (typeof(MachineRows), id);

    /// <summary>The machine's kind and what it holds, locked until the transaction ends.</summary>
    public static async Task<(string Kind, MachineState State)?> ReadAsync(GameDb db, Guid id, CancellationToken cancellationToken)
    {
        if (db.TakeReadAhead<(string, MachineState)?>(Key(id), out var known))
        {
            return known;
        }

        await using var command = await db.CommandAsync($"SELECT {Columns} FROM crafting.machines WHERE object_id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Row(reader) : null;
    }

    /// <summary>What the machine holds, locked; a machine used for the first time is empty, and takes over the wear of the
    /// owner's machine of its kind that was picked up nearest its service.</summary>
    public static async Task<MachineState> ReadOrNewAsync(GameDb db, Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (await ReadAsync(db, id, cancellationToken) is { } row)
        {
            return row.State;
        }

        DateTimeOffset? due;
        await using (var take = await db.CommandAsync(
            """
            DELETE FROM crafting.machine_wear WHERE id = (
                SELECT id FROM crafting.machine_wear WHERE owner_id = @owner AND kind = @kind ORDER BY service_due LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING service_due
            """, cancellationToken))
        {
            take.Parameters.AddWithValue("owner", owner);
            take.Parameters.AddWithValue("kind", kind);
            await using var reader = await take.ExecuteReaderAsync(cancellationToken);
            due = await reader.ReadAsync(cancellationToken) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
        }

        if (due is null)
        {
            return MachineState.Empty;
        }

        var state = MachineState.Empty with { ServiceDue = due };
        await WriteAsync(db, owner, id, kind, state, cancellationToken);
        return state;
    }

    /// <summary>Reads (and locks) the rows of these machines in one query, for <see cref="ReadAsync"/> to take one by one.</summary>
    public static async Task ReadAheadAsync(GameDb db, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 2 || db.Transaction is null)
        {
            return;
        }

        // Locked in the order of their ids, like any other transaction locking several of them.
        await using var command = await db.CommandAsync(
            $"SELECT object_id, {Columns} FROM crafting.machines WHERE object_id = ANY(@ids) ORDER BY object_id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        var found = new Dictionary<Guid, (string, MachineState)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                found[reader.GetGuid(0)] = Row(reader, 1);
            }
        }

        foreach (var id in ids)
        {
            db.ReadAhead(Key(id), found.TryGetValue(id, out var row) ? row : null);
        }
    }

    private static (string Kind, MachineState State) Row(NpgsqlDataReader reader, int at = 0) =>
        (reader.GetString(at), new MachineState(
            reader.IsDBNull(at + 1) ? null : reader.GetString(at + 1), reader.GetFieldValue<int[]>(at + 2),
            reader.IsDBNull(at + 3) ? null : reader.GetString(at + 3), reader.GetInt32(at + 4), reader.GetInt32(at + 5), reader.GetInt32(at + 6),
            reader.IsDBNull(at + 7) ? null : reader.GetFieldValue<DateTimeOffset>(at + 7), reader.GetFieldValue<int[]>(at + 8),
            reader.IsDBNull(at + 9) ? null : reader.GetFieldValue<DateTimeOffset>(at + 9)));

    public static async Task WriteAsync(GameDb db, Guid owner, Guid id, string kind, MachineState s, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(Write, cancellationToken);
        Fill(command.Parameters, owner, id, kind, s);
        db.ForgetReadAhead(Key(id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes several machines in one round trip.</summary>
    public static async Task WriteAsync(GameDb db, IReadOnlyList<(Guid Owner, Guid Id, string Kind, MachineState State)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var batch = await db.BatchAsync(cancellationToken);
        foreach (var (owner, id, kind, state) in rows)
        {
            var command = new NpgsqlBatchCommand(Write);
            Fill(command.Parameters, owner, id, kind, state);
            batch.BatchCommands.Add(command);
            db.ForgetReadAhead(Key(id));
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Fill(NpgsqlParameterCollection parameters, Guid owner, Guid id, string kind, MachineState s)
    {
        parameters.AddWithValue("id", id);
        parameters.AddWithValue("owner", owner);
        parameters.AddWithValue("kind", kind);
        parameters.AddWithValue("recipe", (object?)s.Recipe ?? DBNull.Value);
        parameters.AddWithValue("inputs", s.Inputs.ToArray());
        parameters.AddWithValue("fuelItem", (object?)s.FuelItem ?? DBNull.Value);
        parameters.AddWithValue("fuel", s.Fuel);
        parameters.AddWithValue("heat", s.Heat);
        parameters.AddWithValue("output", s.Output);
        parameters.AddWithValue("extras", (s.Extras ?? []).ToArray());
        parameters.Add(new NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)s.StartedAt ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter("serviceDue", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)s.ServiceDue ?? DBNull.Value });
    }

    /// <summary>Everything the machine holds, as item stacks.</summary>
    public static IEnumerable<(string Item, int Quantity)> Contents(ProcessingMachine machine, MachineState s)
    {
        if (machine.Recipe(s.Recipe) is { } recipe)
        {
            if (s.Output > 0)
            {
                yield return (recipe.Id, s.Output);
            }

            for (var i = 0; i < recipe.Extras.Count; i++)
            {
                if (s.Extra(i) > 0)
                {
                    yield return (recipe.Extras[i].Item, s.Extra(i));
                }
            }

            for (var i = 0; i < recipe.Ingredients.Count; i++)
            {
                if (s.Input(i) > 0)
                {
                    yield return (recipe.Ingredients[i].Item, s.Input(i));
                }
            }
        }

        if (s.FuelItem is { } fuel && s.Fuel > 0)
        {
            yield return (fuel, s.Fuel);
        }
    }

    /// <summary>The machine is being picked up: what it holds goes to the owner's inventory.</summary>
    public static async Task ReleaseAsync(
        GameDb db, IInventory inventory, IWallet wallet, IProductionStats stats, IPlotPower power, Guid owner, Guid id, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await ReadAsync(db, id, cancellationToken) is not { } row || Processing.ForState(row.Kind, row.State) is not { } machine)
        {
            return;
        }

        var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
        var state = Processing.Advance(machine, row.State, clock.At(now));
        await stats.RecordAsync(owner, MachineProduction.Processed(machine, row.State, state, clock, now), cancellationToken);
        foreach (var (item, quantity) in Contents(machine, state))
        {
            if (Items.IsMoney(item))
            {
                await wallet.CreditAsync(owner, quantity * Processing.TakingsCents, $"takings {id}", cancellationToken);
            }
            else
            {
                await inventory.AddAsync(owner, item, quantity, cancellationToken);
            }
        }

        // Picking a machine up and putting it down again does not service it.
        if (state.ServiceDue is { } due)
        {
            await using var wear = await db.CommandAsync(
                "INSERT INTO crafting.machine_wear (owner_id, kind, service_due) VALUES (@owner, @kind, @due)", cancellationToken);
            wear.Parameters.AddWithValue("owner", owner);
            wear.Parameters.AddWithValue("kind", machine.Kind);
            wear.Parameters.AddWithValue("due", due);
            await wear.ExecuteNonQueryAsync(cancellationToken);
        }

        db.ForgetReadAhead(MachineRows.Key(id));
        await using var delete = await db.CommandAsync("DELETE FROM crafting.machines WHERE object_id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}
