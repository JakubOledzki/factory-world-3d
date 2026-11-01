using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Crafting;

/// <summary>Ore smelted into an ingot, one at a time.</summary>
public sealed record SmeltingRecipe(string Input, string Output, TimeSpan Time);

/// <summary>How a furnace of one level works.</summary>
/// <param name="Speed">How many times faster than the stone furnace it smelts.</param>
/// <param name="MaxInput">Most ore it holds.</param>
/// <param name="MaxFuel">Most coal it holds.</param>
/// <param name="SmeltsPerCoal">Smelts one coal lasts for.</param>
public sealed record FurnaceLevel(string Kind, int Level, int Speed, int MaxInput, int MaxFuel, int SmeltsPerCoal)
{
    /// <summary>How many times faster still it smelts where it stands, e.g. with a leather bellows on the plot.</summary>
    public double Boost { get; init; } = 1;

    /// <summary>How long one piece of ore takes in this furnace.</summary>
    public TimeSpan Time(SmeltingRecipe recipe) => recipe.Time / (Speed * Boost);
}

/// <summary>
/// A stone furnace, in the garage or on a plot, burns coal and smelts the ore put into it, one
/// piece after another, while the player is away too. One coal lasts for <see cref="FurnaceLevel.SmeltsPerCoal"/>
/// smelts. It holds one kind of ore at a time; ingots wait in it until taken out. The brick and the
/// blast furnace, which stand on plots only, are the same but smelt faster and hold more.
/// </summary>
public static class Smelting
{
    public static readonly IReadOnlyList<FurnaceLevel> Levels =
    [
        new(Items.Furnace, 1, 1, 50, 50, 5),
        new(Items.BrickFurnace, 2, 2, 100, 100, 5),
        new(Items.BlastFurnace, 3, 4, 200, 200, 8),
    ];

    /// <summary>The stone furnace.</summary>
    public static FurnaceLevel Stone => Levels[0];

    public static FurnaceLevel? Level(string kind) => Levels.FirstOrDefault(l => l.Kind == kind);

    /// <summary>The furnace <paramref name="level"/> as it smelts where the object <paramref name="id"/> stands: faster by
    /// <see cref="Processing.BellowsBonus"/> on a plot with a leather bellows.</summary>
    public static async Task<FurnaceLevel> OnPlotAsync(FurnaceLevel level, IPlotObjects plots, Guid id, CancellationToken cancellationToken) =>
        await plots.PlotOfAsync(id, cancellationToken) is { } at && (await plots.ObjectsAsync(at.X, at.Y, cancellationToken)).Any(o => o.Kind == Items.Bellows)
            ? level with { Boost = Processing.BellowsBonus }
            : level;

    /// <summary>Experience for each ingot taken out of the furnace.</summary>
    public const int XpPerIngot = 1;

    public static readonly IReadOnlyList<SmeltingRecipe> Recipes =
    [
        new(Items.IronOre, Items.IronIngot, TimeSpan.FromSeconds(10)),
        new(Items.CopperOre, Items.CopperIngot, TimeSpan.FromSeconds(10)),
        new(Items.TinOre, Items.TinIngot, TimeSpan.FromSeconds(10)),
        new(Items.SilverOre, Items.SilverIngot, TimeSpan.FromSeconds(15)),
        new(Items.GoldOre, Items.GoldIngot, TimeSpan.FromSeconds(20)),
        // Crushed ore smelts quicker; a crusher makes three of it from two ores.
        new(Items.CrushedIronOre, Items.IronIngot, TimeSpan.FromSeconds(8)),
        new(Items.CrushedCopperOre, Items.CopperIngot, TimeSpan.FromSeconds(8)),
        new(Items.CrushedTinOre, Items.TinIngot, TimeSpan.FromSeconds(8)),
        new(Items.CrushedSilverOre, Items.SilverIngot, TimeSpan.FromSeconds(12)),
        new(Items.CrushedGoldOre, Items.GoldIngot, TimeSpan.FromSeconds(16)),
    ];

    private static readonly Dictionary<string, SmeltingRecipe> ByInput = Recipes.ToDictionary(r => r.Input);

    public static SmeltingRecipe? For(string? input) => input is not null ? ByInput.GetValueOrDefault(input) : null;

    /// <summary>What a furnace of <paramref name="level"/> holds after smelting until <paramref name="now"/>.</summary>
    public static FurnaceState Advance(FurnaceState s, FurnaceLevel level, DateTimeOffset now)
    {
        if (s.StartedAt is not { } started || For(s.Input) is not { } recipe)
        {
            return s with { StartedAt = null };
        }

        var time = level.Time(recipe);
        var perCoal = level.SmeltsPerCoal;
        var possible = (int)Math.Min(int.MaxValue, Math.Max(0, (now - started).Ticks / time.Ticks));
        var n = Math.Min(possible, Math.Min(s.InputQuantity, s.Heat + s.Fuel * perCoal));
        var coal = n > s.Heat ? (n - s.Heat + perCoal - 1) / perCoal : 0;
        var after = s with
        {
            InputQuantity = s.InputQuantity - n,
            Fuel = s.Fuel - coal,
            Heat = s.Heat + coal * perCoal - n,
            Output = n > 0 ? recipe.Output : s.Output,
            OutputQuantity = s.OutputQuantity + n,
        };
        if (after.InputQuantity == 0)
        {
            after = after with { Input = null };
        }

        return after.InputQuantity > 0 && after.Heat + after.Fuel > 0
            ? after with { StartedAt = started + time * n }
            : after with { StartedAt = null };
    }

    /// <summary>Lights an idle furnace that has both ore and coal.</summary>
    /// <summary>Smelts one piece of coke gives a furnace of <paramref name="level"/>: as many as two coal. It catches fire at once, so it
    /// cannot be taken out again.</summary>
    public static int CokeHeat(FurnaceLevel level) => 2 * level.SmeltsPerCoal;

    /// <summary>Whether <paramref name="quantity"/> coke still fits: a furnace holds as much heat as a full load of coal.</summary>
    public static bool CokeFits(FurnaceState s, FurnaceLevel level, int quantity) =>
        s.Heat + (long)quantity * CokeHeat(level) <= (long)level.MaxFuel * level.SmeltsPerCoal;

    public static FurnaceState Start(FurnaceState s, DateTimeOffset now) =>
        s.StartedAt is null && s.InputQuantity > 0 && s.Heat + s.Fuel > 0 ? s with { StartedAt = now } : s;
}

/// <param name="Heat">Smelts left from coal already burning.</param>
/// <param name="StartedAt">When the piece being smelted now went in; null while the furnace is cold.</param>
public sealed record FurnaceState(
    string? Input, int InputQuantity, int Fuel, int Heat, string? Output, int OutputQuantity, DateTimeOffset? StartedAt)
{
    public static readonly FurnaceState Empty = new(null, 0, 0, 0, null, 0, null);
}

public sealed record FurnaceReply(Guid Id, FurnaceLevel Level, FurnaceState State, DateTimeOffset ServerTime, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp);

/// <summary>Rows of <c>crafting.furnaces</c>; a furnace gets one when it is first used.</summary>
internal static class FurnaceRows
{
    private const string Columns = "input_item, input_quantity, fuel, heat, output_item, output_quantity, started_at, kind";

    private const string Write =
        """
        INSERT INTO crafting.furnaces (object_id, owner_id, kind, input_item, input_quantity, fuel, heat, output_item, output_quantity, started_at)
        VALUES (@id, @owner, @kind, @input, @inputQuantity, @fuel, @heat, @output, @outputQuantity, @started)
        ON CONFLICT (object_id) DO UPDATE SET
            input_item = @input, input_quantity = @inputQuantity, fuel = @fuel, heat = @heat,
            output_item = @output, output_quantity = @outputQuantity, started_at = @started
        """;

    internal static (Type, Guid) Key(Guid id) => (typeof(FurnaceRows), id);

    /// <summary>The furnace's contents and its kind, which the row keeps so it can be emptied after the object is gone.</summary>
    public static async Task<(FurnaceState State, FurnaceLevel Level)?> ReadAsync(GameDb db, Guid id, CancellationToken cancellationToken)
    {
        if (db.TakeReadAhead<(FurnaceState, FurnaceLevel)?>(Key(id), out var known))
        {
            return known;
        }

        await using var command = await db.CommandAsync($"SELECT {Columns} FROM crafting.furnaces WHERE object_id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Row(reader) : null;
    }

    /// <summary>Reads (and locks) the rows of these furnaces in one query, for <see cref="ReadAsync"/> to take one by one.</summary>
    public static async Task ReadAheadAsync(GameDb db, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 2 || db.Transaction is null)
        {
            return;
        }

        // Locked in the order of their ids, like any other transaction locking several of them.
        await using var command = await db.CommandAsync(
            $"SELECT object_id, {Columns} FROM crafting.furnaces WHERE object_id = ANY(@ids) ORDER BY object_id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        var found = new Dictionary<Guid, (FurnaceState, FurnaceLevel)>();
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

    private static (FurnaceState State, FurnaceLevel Level) Row(NpgsqlDataReader reader, int at = 0)
    {
        var state = new FurnaceState(
            reader.IsDBNull(at) ? null : reader.GetString(at), reader.GetInt32(at + 1), reader.GetInt32(at + 2), reader.GetInt32(at + 3),
            reader.IsDBNull(at + 4) ? null : reader.GetString(at + 4), reader.GetInt32(at + 5),
            reader.IsDBNull(at + 6) ? null : reader.GetFieldValue<DateTimeOffset>(at + 6));
        return (state, Smelting.Level(reader.GetString(at + 7)) ?? Smelting.Stone);
    }

    public static async Task WriteAsync(GameDb db, Guid owner, Guid id, FurnaceLevel level, FurnaceState s, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(Write, cancellationToken);
        Fill(command.Parameters, owner, id, level, s);
        db.ForgetReadAhead(Key(id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes several furnaces in one round trip.</summary>
    public static async Task WriteAsync(GameDb db, IReadOnlyList<(Guid Owner, Guid Id, FurnaceLevel Level, FurnaceState State)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var batch = await db.BatchAsync(cancellationToken);
        foreach (var (owner, id, level, state) in rows)
        {
            var command = new NpgsqlBatchCommand(Write);
            Fill(command.Parameters, owner, id, level, state);
            batch.BatchCommands.Add(command);
            db.ForgetReadAhead(Key(id));
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Fill(NpgsqlParameterCollection parameters, Guid owner, Guid id, FurnaceLevel level, FurnaceState s)
    {
        parameters.AddWithValue("id", id);
        parameters.AddWithValue("owner", owner);
        parameters.AddWithValue("kind", level.Kind);
        parameters.AddWithValue("input", (object?)s.Input ?? DBNull.Value);
        parameters.AddWithValue("inputQuantity", s.InputQuantity);
        parameters.AddWithValue("fuel", s.Fuel);
        parameters.AddWithValue("heat", s.Heat);
        parameters.AddWithValue("output", (object?)s.Output ?? DBNull.Value);
        parameters.AddWithValue("outputQuantity", s.OutputQuantity);
        parameters.Add(new NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)s.StartedAt ?? DBNull.Value });
    }
}

public sealed class FurnaceService(
    GameDb db, IEnumerable<IPlacedObjects> places, IInventory inventory, ICharacterProgress progress, IProductionStats stats, IPlotObjects plots, TimeProvider time)
{
    public Task<FurnaceReply> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (state, level) = await OpenAsync(owner, id, cancellationToken);
            return new FurnaceReply(id, level, state, time.GetUtcNow(), [], null);
        }, cancellationToken);

    /// <summary>Puts items from the inventory into the furnace: coal on the fire, ore to smelt.
    /// Ingots of another ore still waiting inside are handed to the player first.</summary>
    public Task<FurnaceReply> LoadAsync(Guid owner, Guid id, string item, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var (state, level) = await OpenAsync(owner, id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            if (Items.IsCoal(item))
            {
                if (state.Fuel + quantity > level.MaxFuel)
                {
                    throw new GameException("furnace_full", $"The furnace holds at most {level.MaxFuel} coal.");
                }

                state = state with { Fuel = state.Fuel + quantity };
            }
            else if (item == Items.Coke)
            {
                if (!Smelting.CokeFits(state, level, quantity))
                {
                    throw new GameException("furnace_full", $"The furnace holds at most as much coke as {level.MaxFuel} coal burns.");
                }

                state = state with { Heat = state.Heat + quantity * Smelting.CokeHeat(level) };
            }
            else if (Smelting.For(item) is { } recipe)
            {
                if (state.Input is { } input && input != item)
                {
                    throw new GameException("furnace_busy", $"{Items.Names[input]} is being smelted; take it out first.");
                }

                if (state.InputQuantity + quantity > level.MaxInput)
                {
                    throw new GameException("furnace_full", $"The furnace holds at most {level.MaxInput} ore.");
                }

                if (state.Output is { } output && output != recipe.Output && state.OutputQuantity > 0)
                {
                    (state, xp) = await TakeOutputAsync(owner, state, taken, partial: false, cancellationToken);
                }

                state = state with { Input = item, InputQuantity = state.InputQuantity + quantity };
            }
            else
            {
                throw new GameException("not_smeltable", "This cannot go into the furnace.");
            }

            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            state = Smelting.Start(state, time.GetUtcNow());
            await FurnaceRows.WriteAsync(db, owner, id, level, state, cancellationToken);
            return new FurnaceReply(id, level, state, time.GetUtcNow(), taken, xp);
        }, cancellationToken);

    /// <summary>Takes the ingots (<c>output</c>), the ore not smelted yet (<c>input</c>) or the coal (<c>fuel</c>) out, as much as
    /// fits in the inventory; the rest stays in.</summary>
    public Task<FurnaceReply> TakeAsync(Guid owner, Guid id, string what, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (state, level) = await OpenAsync(owner, id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            switch (what)
            {
                case "output":
                    (state, xp) = await TakeOutputAsync(owner, state, taken, partial: true, cancellationToken);
                    break;
                case "input" when state.Input is { } input && state.InputQuantity > 0:
                    var ore = (int)await inventory.FitAsync(owner, input, state.InputQuantity, cancellationToken);
                    await inventory.AddAsync(owner, input, ore, cancellationToken);
                    taken.Add((input, ore));
                    // Ore left inside keeps smelting.
                    state = ore < state.InputQuantity
                        ? state with { InputQuantity = state.InputQuantity - ore }
                        : state with { Input = null, InputQuantity = 0, StartedAt = null };
                    break;
                case "fuel" when state.Fuel > 0:
                    var coal = (int)await inventory.FitAsync(owner, Items.Coal, state.Fuel, cancellationToken);
                    await inventory.AddAsync(owner, Items.Coal, coal, cancellationToken);
                    taken.Add((Items.Coal, coal));
                    // The piece in the fire keeps burning on the coal left or already lit, if there is any.
                    state = coal < state.Fuel ? state with { Fuel = state.Fuel - coal }
                        : state.Heat > 0 ? state with { Fuel = 0 }
                        : state with { Fuel = 0, StartedAt = null };
                    break;
                case "input" or "fuel":
                    break;
                default:
                    throw new GameException("bad_payload", "Take out output, input or fuel.");
            }

            await FurnaceRows.WriteAsync(db, owner, id, level, state, cancellationToken);
            return new FurnaceReply(id, level, state, time.GetUtcNow(), taken, xp);
        }, cancellationToken);

    /// <summary>Hands the ingots to the player: as many as fit (<paramref name="partial"/>), the rest staying in, or all of them,
    /// failing with <c>inventory_full</c> when they do not fit (before ore of another kind goes in).</summary>
    private async Task<(FurnaceState, XpGain?)> TakeOutputAsync(
        Guid owner, FurnaceState state, List<(string, int)> taken, bool partial, CancellationToken cancellationToken)
    {
        if (state.Output is not { } output || state.OutputQuantity == 0)
        {
            return (state, null);
        }

        var quantity = state.OutputQuantity;
        if (partial)
        {
            quantity = (int)await inventory.FitAsync(owner, output, quantity, cancellationToken);
            await inventory.AddAsync(owner, output, quantity, cancellationToken);
        }
        else
        {
            await inventory.ReceiveAsync(owner, output, quantity, cancellationToken);
        }

        taken.Add((output, quantity));
        var xp = await progress.AddXpAsync(owner, quantity * Smelting.XpPerIngot, cancellationToken);
        return (quantity < state.OutputQuantity ? state with { OutputQuantity = state.OutputQuantity - quantity } : state with { Output = null, OutputQuantity = 0 }, xp);
    }

    /// <summary>The player's furnace, locked until the transaction ends and smelted up to now.</summary>
    private async Task<(FurnaceState, FurnaceLevel)> OpenAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        var kind = await Workshop.RequireAsync(places, owner, id, Items.IsFurnace, Items.Furnace, cancellationToken);
        var level = await Smelting.OnPlotAsync(Smelting.Level(kind)!, plots, id, cancellationToken);
        var stored = (await FurnaceRows.ReadAsync(db, id, cancellationToken))?.State ?? FurnaceState.Empty;
        var state = Smelting.Advance(stored, level, time.GetUtcNow());
        var made = MachineProduction.Smelted(level, stored, state);
        if (made.Count > 0)
        {
            await stats.RecordAsync(owner, made, cancellationToken);
            await FurnaceRows.WriteAsync(db, owner, id, level, state, cancellationToken);
        }

        return (state, level);
    }
}

/// <summary>Empties a furnace that is being picked up: what it smelted, its ore and its coal go to the inventory.
/// Processing machines (mill, bakery, ...) are emptied the same way.</summary>
public sealed class FurnaceContents(GameDb db, IInventory inventory, IWallet wallet, IProductionStats stats, IPlotPower power, TimeProvider time) : IMachineContents
{
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await MachineRows.ReleaseAsync(db, inventory, wallet, stats, power, owner, id, time.GetUtcNow(), cancellationToken);
        if (await FurnaceRows.ReadAsync(db, id, cancellationToken) is not { } row)
        {
            return;
        }

        var state = Smelting.Advance(row.State, row.Level, time.GetUtcNow());
        await stats.RecordAsync(owner, MachineProduction.Smelted(row.Level, row.State, state), cancellationToken);
        if (state.Output is { } output && state.OutputQuantity > 0)
        {
            await inventory.AddAsync(owner, output, state.OutputQuantity, cancellationToken);
        }

        if (state.Input is { } input && state.InputQuantity > 0)
        {
            await inventory.AddAsync(owner, input, state.InputQuantity, cancellationToken);
        }

        if (state.Fuel > 0)
        {
            await inventory.AddAsync(owner, Items.Coal, state.Fuel, cancellationToken);
        }

        db.ForgetReadAhead(FurnaceRows.Key(id));
        await using var delete = await db.CommandAsync("DELETE FROM crafting.furnaces WHERE object_id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Furnaces as inserters see them: coal and ore go in, ingots come out.</summary>
public sealed class FurnacePorts(GameDb db, IProductionStats stats, IPlotObjects plots) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (Smelting.Level(kind) is not { } plain)
        {
            return null;
        }

        var level = await Smelting.OnPlotAsync(plain, plots, id, cancellationToken);
        return new Port(db, stats, owner, id, level, (await FurnaceRows.ReadAsync(db, id, cancellationToken))?.State ?? FurnaceState.Empty);
    }

    public Task PrepareAsync(IReadOnlyList<PlotObject> objects, CancellationToken cancellationToken) =>
        FurnaceRows.ReadAheadAsync(db, objects.Where(o => Smelting.Level(o.Kind) is not null).Select(o => o.Id).Distinct().ToList(), cancellationToken);

    public async Task SaveAsync(IReadOnlyList<IMachinePort> ports, CancellationToken cancellationToken)
    {
        var changed = ports.OfType<Port>().Where(p => p.Changed).ToList();
        foreach (var owner in changed.GroupBy(p => p.Owner))
        {
            await stats.RecordAsync(owner.Key, owner.SelectMany(p => p.Made).ToList(), cancellationToken);
        }

        await FurnaceRows.WriteAsync(db, changed.Select(p => (p.Owner, p.Id, p.Level, p.State)).ToList(), cancellationToken);
    }

    private sealed class Port(GameDb db, IProductionStats stats, Guid owner, Guid id, FurnaceLevel level, FurnaceState state) : IMachinePort
    {
        private readonly List<ProductionEntry> _made = [];
        private bool _changed;

        public Guid Owner => owner;
        public Guid Id => id;
        public FurnaceLevel Level => level;
        public FurnaceState State => state;
        public bool Changed => _changed;
        public IReadOnlyList<ProductionEntry> Made => _made;

        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Advance(now);
            return state.Output is { } output && state.OutputQuantity > 0 && wanted(output) ? output : null;
        }

        public void Take(string item, DateTimeOffset now)
        {
            Advance(now);
            if (state.Output == item && state.OutputQuantity > 0)
            {
                state = state.OutputQuantity == 1 ? state with { Output = null, OutputQuantity = 0 } : state with { OutputQuantity = state.OutputQuantity - 1 };
                _changed = true;
            }
        }

        public bool Accepts(string item, DateTimeOffset now)
        {
            Advance(now);
            if (Items.IsCoal(item))
            {
                return state.Fuel < level.MaxFuel;
            }

            if (item == Items.Coke)
            {
                return Smelting.CokeFits(state, level, 1);
            }

            // Ingots of another metal must be taken out before another ore goes in.
            return Smelting.For(item) is { } recipe
                && (state.Input is null || state.Input == item) && state.InputQuantity < level.MaxInput
                && (state.Output is null || state.Output == recipe.Output || state.OutputQuantity == 0);
        }

        public void Put(string item, DateTimeOffset now)
        {
            state = Items.IsCoal(item) ? state with { Fuel = state.Fuel + 1 }
                : item == Items.Coke ? state with { Heat = state.Heat + Smelting.CokeHeat(level) }
                : state with { Input = item, InputQuantity = state.InputQuantity + 1 };
            state = Smelting.Start(state, now);
            _changed = true;
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            Advance(now);
            return state.StartedAt is { } started && Smelting.For(state.Input) is { } recipe ? started + level.Time(recipe) : null;
        }

        public bool Predictable => true;

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_changed)
            {
                await stats.RecordAsync(owner, _made, cancellationToken);
                await FurnaceRows.WriteAsync(db, owner, id, level, state, cancellationToken);
            }
        }

        private void Advance(DateTimeOffset now)
        {
            if (state.StartedAt is not { } started || (Smelting.For(state.Input) is { } recipe && now - started < level.Time(recipe)))
            {
                // Cold, or the piece in it not done yet: nothing to work out.
                return;
            }

            var before = state;
            state = Smelting.Advance(state, level, now);
            var made = MachineProduction.Smelted(level, before, state);
            if (made.Count > 0)
            {
                _made.AddRange(made);
                _changed = true;
            }
        }
    }
}

public sealed record FurnaceRequest(Guid Furnace);

public sealed record LoadFurnaceRequest(Guid Furnace, string Item, int Quantity);

public sealed record TakeFurnaceRequest(Guid Furnace, string What);

public abstract class FurnaceHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<FurnaceService>(), client.RequireAccount(), payload, cancellationToken);
        var s = reply.State;
        var level = reply.Level;
        return new Reply("furnace", new
        {
            id = reply.Id,
            kind = level.Kind,
            name = Items.Names[level.Kind],
            level = level.Level,
            speed = level.Speed * level.Boost,
            input = Stack(s.Input, s.InputQuantity),
            output = Stack(s.Output, s.OutputQuantity),
            fuel = s.Fuel,
            heat = s.Heat,
            // Unix milliseconds of when the piece in the fire went in, and how long it takes.
            startedAt = s.StartedAt?.ToUnixTimeMilliseconds(),
            smeltSeconds = Smelting.For(s.Input) is { } smelting ? level.Time(smelting).TotalSeconds : (double?)null,
            serverTime = reply.ServerTime.ToUnixTimeMilliseconds(),
            recipes = Smelting.Recipes.Select(r => new
            {
                input = r.Input,
                inputName = Items.Names[r.Input],
                output = r.Output,
                outputName = Items.Names[r.Output],
                seconds = level.Time(r).TotalSeconds,
            }),
            smeltsPerCoal = level.SmeltsPerCoal,
            maxInput = level.MaxInput,
            maxFuel = level.MaxFuel,
            taken = reply.Taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = reply.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
        });
    }

    private static object? Stack(string? item, int quantity) =>
        item is null || quantity == 0 ? null : new { id = item, name = Items.Names[item], quantity };

    protected abstract Task<FurnaceReply> RunAsync(FurnaceService furnaces, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What the furnace holds now; the client asks again when the next ingot is due.</summary>
public sealed class GetFurnaceHandler(IServiceScopeFactory scopes) : FurnaceHandler(scopes)
{
    public override string Type => "furnace.get";

    protected override Task<FurnaceReply> RunAsync(FurnaceService furnaces, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        furnaces.StateAsync(owner, Payload.Read<FurnaceRequest>(payload).Furnace, cancellationToken);
}

public sealed class LoadFurnaceHandler(IServiceScopeFactory scopes) : FurnaceHandler(scopes)
{
    public override string Type => "furnace.load";

    protected override Task<FurnaceReply> RunAsync(FurnaceService furnaces, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LoadFurnaceRequest>(payload);
        return furnaces.LoadAsync(owner, request.Furnace, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class TakeFurnaceHandler(IServiceScopeFactory scopes) : FurnaceHandler(scopes)
{
    public override string Type => "furnace.take";

    protected override Task<FurnaceReply> RunAsync(FurnaceService furnaces, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TakeFurnaceRequest>(payload);
        return furnaces.TakeAsync(owner, request.Furnace, request.What ?? "", cancellationToken);
    }
}
