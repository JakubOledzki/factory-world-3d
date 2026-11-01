using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace FactoryWorld.Modules.Logistics;

/// <summary>Up to when a plot's belts and inserters have been worked out.</summary>
public sealed class LogisticsPlotRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public DateTimeOffset SimulatedAt { get; set; }
}

/// <summary>What a belt, an inserter or a storage on a plot holds (<see cref="Contents"/> as JSON).</summary>
public sealed class ContentsRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public string Data { get; set; } = "{}";
}

public sealed class LogisticsDbContext(DbContextOptions<LogisticsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "logistics";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<LogisticsPlotRow>(e =>
        {
            e.ToTable("plots");
            e.HasKey(p => new { p.X, p.Y });
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.SimulatedAt).HasColumnName("simulated_at");
        });
        model.Entity<ContentsRow>(e =>
        {
            e.ToTable("contents");
            e.HasKey(c => c.ObjectId);
            e.Property(c => c.ObjectId).HasColumnName("object_id");
            e.Property(c => c.X).HasColumnName("x");
            e.Property(c => c.Y).HasColumnName("y");
            e.Property(c => c.Data).HasColumnName("data").HasColumnType("jsonb");
            e.HasIndex(c => new { c.X, c.Y });
        });
    }
}

/// <summary>A plot's logistics just after being brought up to now, with the power grid that runs its inserters.</summary>
public sealed record LogisticsState(
    DateTimeOffset SimulatedAt, DateTimeOffset ServerTime, IReadOnlyList<PlotObject> Objects, IReadOnlyDictionary<Guid, Contents> Contents, IPowerGrid Power);

public sealed class LogisticsService(
    GameDb db, IPlotObjects plots, IEnumerable<IMachinePorts> machines, IPlotPower power, IInventory inventory, ITechnologies technologies, TimeProvider time,
    ILogger<LogisticsService> logger)
    : IPlotAutomation
{
    /// <summary>A catch-up taking longer than this is logged with where its time went.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    public async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken) =>
        await db.InTransactionAsync(() => RunAsync(x, y, cancellationToken), cancellationToken);

    /// <summary>The player's plot with its belts, inserters and storage as they are now.</summary>
    public Task<LogisticsState> StateAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        StateAsync(player, x, y, PlotRight.Work, cancellationToken);

    /// <summary>The plot as <see cref="StateAsync(Guid, int, int, CancellationToken)"/> has it, for anyone who may do
    /// <paramref name="right"/> there (a visitor of a public plot only sees the belts move).</summary>
    public Task<LogisticsState> StateAsync(Guid player, int x, int y, PlotRight right, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
            return await RunAsync(x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts items from the inventory into a storage on the player's plot.</summary>
    public Task<(LogisticsState State, PlotObject Storage)> PutAsync(
        Guid player, int x, int y, Guid id, string item, int quantity, CancellationToken cancellationToken) =>
        ChangeStorageAsync(player, x, y, id, PlotRight.Work, async (kind, stacks, only) =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            if (!kind.Takes(item) || (only is not null && item != only))
            {
                throw new GameException("not_stored_here", $"{Items.Names[kind.Id]} does not take this.");
            }

            if (Storages.Room(kind, stacks, item) < quantity)
            {
                throw new GameException("storage_full", $"There is no room for that much in the {Items.Names[kind.Id].ToLowerInvariant()}.");
            }

            await inventory.RemoveAsync(player, item, quantity, cancellationToken);
            Storages.Add(stacks, item, quantity);
        }, cancellationToken);

    /// <summary>Takes items out of a storage into the inventory: <paramref name="quantity"/> of them, or all when 0. Only as many
    /// as the inventory has room for come out; the rest stays in.</summary>
    public Task<(LogisticsState State, PlotObject Storage)> TakeAsync(
        Guid player, int x, int y, Guid id, string item, int quantity, CancellationToken cancellationToken) =>
        ChangeStorageAsync(player, x, y, id, PlotRight.Take, async (_, stacks, _) =>
        {
            if (quantity < 0)
            {
                throw new GameException("bad_quantity", "Take out at least one.");
            }

            var held = stacks.FirstOrDefault(s => s.Item == item)?.Quantity ?? 0;
            if (held == 0 || quantity > held)
            {
                throw new GameException("not_enough_items", "There is not that much in it.");
            }

            var taken = (int)await inventory.FitAsync(player, item, quantity == 0 ? held : quantity, cancellationToken);
            Storages.Remove(stacks, item, taken);
            await inventory.AddAsync(player, item, taken, cancellationToken);
        }, cancellationToken);

    /// <summary>Most items a splitter can be set to sort out.</summary>
    public const int MaxFilter = 8;

    /// <summary>Sets which items a splitter on the player's plot sends to one side (0 left, 1 right) and
    /// the rest to the other; no items, and it sends items left and right in turn.</summary>
    public Task<LogisticsState> SetSplitterAsync(
        Guid player, int x, int y, Guid id, int side, IReadOnlyList<string> items, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (side is not (0 or 1))
            {
                throw new GameException("bad_side", "A splitter sends items left (0) or right (1).");
            }

            if (items.Count > MaxFilter || items.Any(i => !Items.Names.ContainsKey(i)))
            {
                throw new GameException("bad_filter", $"Choose at most {MaxFilter} items to sort out.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            var state = await RunAsync(x, y, cancellationToken);
            if (!state.Objects.Any(o => o.Id == id && o.Kind == Items.Splitter))
            {
                throw new GameException("no_object", "There is no such splitter on this plot.");
            }

            var contents = state.Contents.GetValueOrDefault(id) ?? new Contents();
            contents.Filter = items.Count == 0 ? null : items.Distinct().ToList();
            contents.FilterSide = side;
            await WriteAsync(id, x, y, contents, cancellationToken);
            var all = new Dictionary<Guid, Contents>(state.Contents) { [id] = contents };
            return state with { Contents = all };
        }, cancellationToken);

    /// <summary>Sets the one item an inserter, a storage or a machine on the player's plot takes (its
    /// filter): an inserter then picks up nothing else, a storage or a machine lets nothing else in.
    /// No item, and it takes anything again.</summary>
    public Task<LogisticsState> SetFilterAsync(Guid player, int x, int y, Guid id, string? item, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            item = string.IsNullOrEmpty(item) ? null : item;
            if (item is not null && !Items.Exists(item))
            {
                throw new GameException("bad_filter", "There is no such item.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            var state = await RunAsync(x, y, cancellationToken);
            var o = state.Objects.FirstOrDefault(o => o.Id == id)
                ?? throw new GameException("no_object", "There is no such object on this plot.");
            if (!await FilterableAsync(o, state.Power, cancellationToken))
            {
                throw new GameException("not_filterable", $"{Items.Names.GetValueOrDefault(o.Kind, o.Kind)} has no filter.");
            }

            if (item is not null && Storages.Find(o.Kind) is { } kind && !kind.Takes(item))
            {
                throw new GameException("not_stored_here", $"{Items.Names[kind.Id]} does not take this.");
            }

            var contents = state.Contents.GetValueOrDefault(id) ?? new Contents();
            contents.Only = item;
            await WriteAsync(id, x, y, contents, cancellationToken);
            var all = new Dictionary<Guid, Contents>(state.Contents) { [id] = contents };
            return state with { Contents = all };
        }, cancellationToken);

    /// <summary>Most pieces a condition counts to.</summary>
    public const int MaxCondition = 1_000_000;

    /// <summary>Sets the condition an inserter or a pump on the player's plot works on: it fills the storage in front up to
    /// <paramref name="below"/> pieces of an item and leaves <paramref name="keep"/> of each in the storage behind it; 0 for no limit.</summary>
    public Task<LogisticsState> SetConditionAsync(Guid player, int x, int y, Guid id, int below, int keep, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (below is < 0 or > MaxCondition || keep is < 0 or > MaxCondition)
            {
                throw new GameException("bad_condition", $"A condition counts from 0 to {MaxCondition} pieces.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            var state = await RunAsync(x, y, cancellationToken);
            if (!state.Objects.Any(o => o.Id == id && Items.IsInserter(o.Kind)))
            {
                throw new GameException("no_object", "There is no such inserter on this plot.");
            }

            var contents = state.Contents.GetValueOrDefault(id) ?? new Contents();
            contents.Below = below;
            contents.Keep = keep;
            await WriteAsync(id, x, y, contents, cancellationToken);
            var all = new Dictionary<Guid, Contents>(state.Contents) { [id] = contents };
            return state with { Contents = all };
        }, cancellationToken);

    /// <summary>Whether an object can have a filter: an inserter, a storage, or a machine inserters put things into.</summary>
    private async Task<bool> FilterableAsync(PlotObject o, IPowerGrid grid, CancellationToken cancellationToken)
    {
        if (Items.IsInserter(o.Kind) || Storages.Find(o.Kind) is not null || grid.Port(o.Id) is not null)
        {
            return true;
        }

        foreach (var module in machines)
        {
            if (await module.OpenAsync(o.Owner, o.Id, o.Kind, cancellationToken) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private Task<(LogisticsState, PlotObject)> ChangeStorageAsync(
        Guid player, int x, int y, Guid id, PlotRight right, Func<StorageKind, List<ItemStack>, string?, Task> change, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
            var state = await RunAsync(x, y, cancellationToken);
            var storage = state.Objects.FirstOrDefault(o => o.Id == id);
            if (storage is null || Storages.Find(storage.Kind) is not { } kind)
            {
                throw new GameException("no_object", "There is no such storage on this plot.");
            }

            var contents = state.Contents.GetValueOrDefault(id) ?? new Contents();
            await change(kind, contents.Items ??= [], contents.Only);
            await WriteAsync(id, x, y, contents, cancellationToken);
            var all = new Dictionary<Guid, Contents>(state.Contents) { [id] = contents };
            return (state with { Contents = all }, storage);
        }, cancellationToken);

    /// <summary>Works out the plot's logistics from where they were left up to now, under a lock on the plot.</summary>
    internal async Task<LogisticsState> RunAsync(int x, int y, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using (var lockPlot = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
        {
            lockPlot.Parameters.AddWithValue("key", ((long)x << 32 | (uint)y) ^ 0x6c6f6769_73746963);
            await lockPlot.ExecuteNonQueryAsync(cancellationToken);
        }

        var locked = Stopwatch.GetTimestamp();
        var now = time.GetUtcNow();
        var objects = await plots.ObjectsAsync(x, y, cancellationToken);
        var from = await SimulatedAtAsync(x, y, cancellationToken) ?? now;
        var contents = await ReadAsync(x, y, cancellationToken);
        var grid = await power.OpenAsync(x, y, cancellationToken);

        // Storages are this module's own; every other machine within an inserter's reach comes from its
        // module, and so does the plot's station, which inserters can load and unload too.
        // A machine may cover several tiles, and an inserter next to any of them reaches it. An object's own tile goes first,
        // so a plot not yet brought in line with grown machines (see the world's footprint migration) still works as it was built.
        var fixtures = await plots.FixturesAsync(x, y, cancellationToken);
        var at = new Dictionary<(int, int), PlotObject>();
        foreach (var o in objects.Concat(fixtures).Concat(objects.SelectMany(Footprints.Spread)))
        {
            at.TryAdd((o.TileX, o.TileY), o);
        }

        var ports = new Dictionary<Guid, IMachinePort>();
        var opened = new List<(IMachinePorts Module, IMachinePort Port)>();
        var storageChanged = new HashSet<Guid>();
        var reached = new List<PlotObject>();
        foreach (var tile in PlotLogistics.Reach(objects).Distinct())
        {
            if (at.TryGetValue(tile, out var o) && !reached.Any(r => r.Id == o.Id))
            {
                reached.Add(o);
            }
        }

        foreach (var module in machines)
        {
            await module.PrepareAsync(reached, cancellationToken);
        }

        foreach (var o in reached)
        {

            if (Storages.Find(o.Kind) is { } kind)
            {
                if (!contents.TryGetValue(o.Id, out var c))
                {
                    contents[o.Id] = c = new Contents();
                }

                ports[o.Id] = new StoragePort(kind, c, () => storageChanged.Add(o.Id));
                continue;
            }

            if (grid.Port(o.Id) is { } generator)
            {
                ports[o.Id] = generator;
                continue;
            }

            foreach (var module in machines)
            {
                if (await module.OpenAsync(o.Owner, o.Id, o.Kind, cancellationToken) is { } port)
                {
                    ports[o.Id] = port;
                    opened.Add((module, port));
                    break;
                }
            }
        }

        var logistics = new PlotLogistics(at.Values.ToList(), contents, ports, grid, await GripsAsync(objects, cancellationToken));
        var loaded = Stopwatch.GetTimestamp();
        var simulated = logistics.Run(from, now);
        var ran = Stopwatch.GetTimestamp();
        await WriteAsync(x, y, logistics.Changed.Concat(storageChanged).Distinct().Select(id => (id, contents[id])).ToList(), cancellationToken);
        foreach (var module in opened.GroupBy(o => o.Module, o => o.Port))
        {
            await module.Key.SaveAsync(module.ToList(), cancellationToken);
        }

        await grid.SaveAsync(cancellationToken);

        await using (var save = await db.CommandAsync(
            """
            INSERT INTO logistics.plots (x, y, simulated_at) VALUES (@x, @y, @at)
            ON CONFLICT (x, y) DO UPDATE SET simulated_at = @at
            """, cancellationToken))
        {
            save.Parameters.AddWithValue("x", x);
            save.Parameters.AddWithValue("y", y);
            save.Parameters.AddWithValue("at", simulated);
            await save.ExecuteNonQueryAsync(cancellationToken);
        }

        if (Stopwatch.GetElapsedTime(started) > Slow)
        {
            logger.LogWarning(
                "Slow logistics on plot {X}:{Y}: waited {Lock:0} ms for the plot, loaded {Objects} objects in {Load:0} ms, worked out {Ticks} of {Span:0} min in {Run:0} ms, saved {Saved} in {Save:0} ms",
                x, y, Stopwatch.GetElapsedTime(started, locked).TotalMilliseconds, objects.Count, Stopwatch.GetElapsedTime(locked, loaded).TotalMilliseconds,
                logistics.Steps, (now - from).TotalMinutes, Stopwatch.GetElapsedTime(loaded, ran).TotalMilliseconds, logistics.Changed.Count + storageChanged.Count,
                Stopwatch.GetElapsedTime(ran).TotalMilliseconds);
        }

        return new LogisticsState(simulated, now, objects, contents, grid);
    }

    /// <summary>Pieces each inserter picks up at once, by the grippers its owner researched; one for those not listed.</summary>
    private async Task<Dictionary<Guid, int>> GripsAsync(IReadOnlyList<PlotObject> objects, CancellationToken cancellationToken)
    {
        var grips = new Dictionary<Guid, int>();
        foreach (var owned in objects.Where(o => Items.IsInserter(o.Kind) && o.Kind != Items.Pump).GroupBy(o => o.Owner))
        {
            var researched = await technologies.ResearchedAsync(owned.Key, cancellationToken);
            var (bigger, industrial) = (researched.Contains(GripperTechs.Bigger), researched.Contains(GripperTechs.Industrial));
            foreach (var o in owned.Where(_ => bigger || industrial))
            {
                grips[o.Id] = LogisticsRules.Grip(o.Kind, bigger, industrial);
            }
        }

        return grips;
    }

    private async Task<DateTimeOffset?> SimulatedAtAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT simulated_at FROM logistics.plots WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        return await command.ExecuteScalarAsync(cancellationToken) is DateTime at ? new DateTimeOffset(at, TimeSpan.Zero) : null;
    }

    private async Task<Dictionary<Guid, Contents>> ReadAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT object_id, data FROM logistics.contents WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var contents = new Dictionary<Guid, Contents>();
        while (await reader.ReadAsync(cancellationToken))
        {
            contents[reader.GetGuid(0)] = JsonSerializer.Deserialize<Contents>(reader.GetString(1), Json) ?? new Contents();
        }

        return contents;
    }

    private Task WriteAsync(Guid id, int x, int y, Contents contents, CancellationToken cancellationToken) =>
        WriteAsync(x, y, [(id, contents)], cancellationToken);

    /// <summary>Writes what belts, inserters and storages on the plot hold, all in one query.</summary>
    internal async Task WriteAsync(int x, int y, IReadOnlyList<(Guid Id, Contents Contents)> changed, CancellationToken cancellationToken)
    {
        if (changed.Count == 0)
        {
            return;
        }

        await using var command = await db.CommandAsync(
            """
            INSERT INTO logistics.contents (object_id, x, y, data)
            SELECT t.id, @x, @y, t.data FROM unnest(@ids, @data) AS t(id, data)
            ON CONFLICT (object_id) DO UPDATE SET data = excluded.data
            """, cancellationToken);
        command.Parameters.AddWithValue("ids", changed.Select(c => c.Id).ToArray());
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb | NpgsqlDbType.Array, changed.Select(c => JsonSerializer.Serialize(c.Contents, Json)).ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Contents Parse(string data) => JsonSerializer.Deserialize<Contents>(data, Json) ?? new Contents();
}

/// <summary>Empties a belt, inserter or storage that is being picked up into the owner's inventory.</summary>
public sealed class LogisticsContents(GameDb db, IInventory inventory) : IMachineContents
{
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM logistics.contents WHERE object_id = @id RETURNING data", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        if (await delete.ExecuteScalarAsync(cancellationToken) is not string data)
        {
            return;
        }

        foreach (var stack in LogisticsService.Parse(data).All().GroupBy(i => i.Item, i => i.Quantity))
        {
            await inventory.AddAsync(owner, stack.Key, stack.Sum(), cancellationToken);
        }
    }
}

/// <param name="Names">Send what every item is called too, for choosing a filter.</param>
/// <param name="Compact">The short form of the answer (see <see cref="GetLogisticsHandler.Json"/>), which a client asking every second wants.</param>
public sealed record LogisticsRequest(int X, int Y, bool Names = false, bool Compact = false);

public sealed record StorageRequest(int X, int Y, Guid Id);

public sealed record SplitterRequest(int X, int Y, Guid Id, int Side, List<string>? Items);

public sealed record FilterRequest(int X, int Y, Guid Id, string? Item);

public sealed record ConditionRequest(int X, int Y, Guid Id, int Below, int Keep);

public sealed record StorageItemRequest(int X, int Y, Guid Id, string Item, int Quantity);

/// <summary>The belts, inserters and storage of a plot; the client asks again every moment it watches them.</summary>
public sealed class GetLogisticsHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "logistics.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LogisticsRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var logistics = scope.ServiceProvider.GetRequiredService<LogisticsService>();
        var player = client.RequireAccount();
        try
        {
            var state = await logistics.StateAsync(player, request.X, request.Y, cancellationToken);
            return new Reply("logistics", Json(request.X, request.Y, state, request.Names, compact: request.Compact));
        }
        catch (GameException e) when (e.Code == "not_owner")
        {
            // A visitor of a public plot sees the belts and inserters at work, but not what lies in the storages.
            var state = await logistics.StateAsync(player, request.X, request.Y, PlotRight.Visit, cancellationToken);
            return new Reply("logistics", Json(request.X, request.Y, state, request.Names, visitor: true, request.Compact));
        }
    }

    /// <param name="visitor">Leaves out what the storages hold, for someone only looking around the plot.</param>
    /// <param name="compact">The short form, a few kilobytes instead of a few hundred on a big factory: only belts that carry
    /// something or are splitters (any other is empty), with their items and a splitter's settings; inserters with what
    /// changes only (the hand when full, the pieces in it when more than one, the speed when not 1, the charge when not 0);
    /// what the client knows from the kinds (speeds, reach, swing ticks, cables, tunnels) left out; the item names only when asked.</param>
    public static object Json(int x, int y, LogisticsState state, bool names = false, bool visitor = false, bool compact = false)
    {
        Contents Of(Guid id) => state.Contents.GetValueOrDefault(id) ?? new Contents();
        if (compact)
        {
            return CompactJson(x, y, state, names, visitor, Of);
        }

        var cables = LogisticsRules.CableLinks(state.Objects);
        var tunnels = LogisticsRules.TunnelLinks(state.Objects);
        return new
        {
            x,
            y,
            // Unix milliseconds: the client goes on from simulatedAt with the same rules for its animation.
            simulatedAt = state.SimulatedAt.ToUnixTimeMilliseconds(),
            serverTime = state.ServerTime.ToUnixTimeMilliseconds(),
            tickMs = LogisticsRules.Tick.TotalMilliseconds,
            beltSlots = LogisticsRules.BeltSlots,
            beltLanes = LogisticsRules.BeltLanes,
            swingTicks = LogisticsRules.SwingTicks,
            belts = state.Objects.Where(o => Items.IsConveyor(o.Kind))
                .Select(o => new
                {
                    id = o.Id,
                    // Places an item moves along it a tick; a splitter goes as fast as the belt behind it, which the client works out.
                    speed = LogisticsRules.BeltSpeed(o.Kind),
                    items = Of(o.Id).Belt ?? new string?[LogisticsRules.BeltSlots * LogisticsRules.BeltLanes],
                    // Splitters only: the items sorted out and the side they go to.
                    filter = o.Kind == Items.Splitter ? Of(o.Id).Filter ?? [] : null,
                    filterSide = Of(o.Id).FilterSide,
                    turn = Of(o.Id).Turn,
                    // Cable stations only: the station ahead that items ride the cable to.
                    cable = cables.TryGetValue(o.Id, out var to) ? to : (Guid?)null,
                    // Underground belts going down only: the one that brings their items up.
                    tunnel = tunnels.TryGetValue(o.Id, out var exit) ? exit : (Guid?)null,
                }),
            // What every item is called, for choosing a splitter's filter (and any filter, when the client asks).
            names = names || state.Objects.Any(o => o.Kind == Items.Splitter) ? Items.Names : null,
            // The one item each inserter, storage or machine with a filter takes, by object id.
            filters = state.Objects.Where(o => Of(o.Id).Only is not null).Select(o => new { id = o.Id, item = Of(o.Id).Only }),
            // The conditions inserters and pumps work on: fill the storage in front up to `below`, keep `keep` in the one behind.
            conditions = Conditions(state, Of),
            inserters = state.Objects.Where(o => Items.IsInserter(o.Kind))
                .Select(o => new
                {
                    id = o.Id,
                    hand = Of(o.Id).Hand,
                    // How many pieces of it, with bigger grippers.
                    count = Of(o.Id).Held(),
                    // Tiles away it picks up behind it and drops in front.
                    reach = LogisticsRules.Reach(o.Kind),
                    swing = Of(o.Id).Swing,
                    swingTicks = LogisticsRules.Swing(o.Kind),
                    // How fast it works on the power it has (0 stands still), and the power gathered for its next move.
                    speed = state.Power.Clock(o.Id).Speed(state.SimulatedAt),
                    charge = Of(o.Id).Charge,
                }),
            // Drone ports: how fast their drones fly on the power they have (0 stays put).
            drones = state.Objects.Where(o => o.Kind == Items.DronePort)
                .Select(o => new { id = o.Id, speed = state.Power.Clock(o.Id).Speed(state.SimulatedAt) }),
            storages = visitor ? [] : state.Objects.Where(o => Storages.Find(o.Kind) is not null)
                .Select(o => StorageSummary(Storages.Find(o.Kind)!, o, Of(o.Id))),
            power = state.Power.Summary(state.ServerTime),
        };
    }

    private static object CompactJson(int x, int y, LogisticsState state, bool names, bool visitor, Func<Guid, Contents> of)
    {
        var belts = new List<Dictionary<string, object?>>();
        var inserters = new List<Dictionary<string, object?>>();
        foreach (var o in state.Objects)
        {
            if (Items.IsConveyor(o.Kind))
            {
                var c = of(o.Id);
                var splitter = o.Kind == Items.Splitter;
                if (!splitter && (c.Belt is null || c.Belt.All(i => i is null)))
                {
                    continue;
                }

                var belt = new Dictionary<string, object?> { ["id"] = o.Id, ["items"] = c.Belt ?? new string?[LogisticsRules.BeltSlots * LogisticsRules.BeltLanes] };
                if (splitter)
                {
                    belt["filter"] = c.Filter ?? [];
                    belt["filterSide"] = c.FilterSide;
                    belt["turn"] = c.Turn;
                }

                belts.Add(belt);
            }
            else if (Items.IsInserter(o.Kind))
            {
                var c = of(o.Id);
                var inserter = new Dictionary<string, object?> { ["id"] = o.Id, ["swing"] = c.Swing };
                if (c.Hand is not null)
                {
                    inserter["hand"] = c.Hand;
                }

                if (c.Held() > 1)
                {
                    inserter["count"] = c.Held();
                }

                if (state.Power.Clock(o.Id).Speed(state.SimulatedAt) is var speed && speed != 1)
                {
                    inserter["speed"] = speed;
                }

                if (c.Charge != 0)
                {
                    inserter["charge"] = c.Charge;
                }

                inserters.Add(inserter);
            }
        }

        return new
        {
            x,
            y,
            compact = true,
            simulatedAt = state.SimulatedAt.ToUnixTimeMilliseconds(),
            serverTime = state.ServerTime.ToUnixTimeMilliseconds(),
            tickMs = LogisticsRules.Tick.TotalMilliseconds,
            beltSlots = LogisticsRules.BeltSlots,
            beltLanes = LogisticsRules.BeltLanes,
            swingTicks = LogisticsRules.SwingTicks,
            belts,
            names = names ? Items.Names : null,
            filters = state.Objects.Where(o => of(o.Id).Only is not null).Select(o => new { id = o.Id, item = of(o.Id).Only }),
            conditions = Conditions(state, of),
            inserters,
            drones = state.Objects.Where(o => o.Kind == Items.DronePort)
                .Select(o => new { id = o.Id, speed = state.Power.Clock(o.Id).Speed(state.SimulatedAt) }),
            storages = visitor ? [] : state.Objects.Where(o => Storages.Find(o.Kind) is not null)
                .Select(o => CompactStorage(Storages.Find(o.Kind)!, o, of(o.Id))),
            power = CompactPower(state.Power.Summary(state.ServerTime)),
        };
    }

    private static IEnumerable<object> Conditions(LogisticsState state, Func<Guid, Contents> of) =>
        state.Objects.Where(o => Items.IsInserter(o.Kind) && (of(o.Id).Below > 0 || of(o.Id).Keep > 0))
            .Select(o => new { id = o.Id, below = of(o.Id).Below, keep = of(o.Id).Keep });

    /// <summary>A storage as the plot shows it: how full it is and what it holds (ids only), the filter and what it takes when set.</summary>
    private static Dictionary<string, object?> CompactStorage(StorageKind kind, PlotObject o, Contents contents)
    {
        var stacks = contents.Items ?? [];
        var storage = new Dictionary<string, object?>
        {
            ["id"] = o.Id,
            ["slots"] = kind.Slots,
            ["usedSlots"] = Storages.UsedSlots(kind, stacks),
            ["capacity"] = kind.Capacity,
            ["stored"] = stacks.Sum(s => s.Quantity),
            ["items"] = stacks.Select(s => new { id = s.Item, quantity = s.Quantity }),
        };
        if (kind.Only is { } only)
        {
            storage["only"] = only.Select(i => new { id = i, name = Items.Names[i] });
        }

        if (contents.Only is { } filter)
        {
            storage["filter"] = filter;
        }

        return storage;
    }

    /// <summary>The power summary without the pairs of wired poles, which the client works out itself.</summary>
    private static JsonNode? CompactPower(object summary)
    {
        var node = JsonSerializer.SerializeToNode(summary, Payload.Json);
        (node as JsonObject)?.Remove("wires");
        return node;
    }

    public static object StorageSummary(StorageKind kind, PlotObject o, Contents contents)
    {
        var stacks = contents.Items ?? [];
        return new
        {
            id = o.Id,
            kind = o.Kind,
            name = Items.Names[o.Kind],
            slots = kind.Slots,
            slotSize = kind.SlotSize,
            usedSlots = Storages.UsedSlots(kind, stacks),
            capacity = kind.Capacity,
            stored = stacks.Sum(s => s.Quantity),
            only = kind.Only?.Select(i => new { id = i, name = Items.Names[i] }),
            // The item it is set to take, or null.
            filter = contents.Only,
            items = stacks.Select(s => new { id = s.Item, name = Items.Names.GetValueOrDefault(s.Item, s.Item), quantity = s.Quantity }),
        };
    }
}

/// <summary>Sets a splitter's filter; answers with the plot's logistics like "logistics.get".</summary>
public sealed class SetSplitterHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "splitter.set";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<SplitterRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var state = await scope.ServiceProvider.GetRequiredService<LogisticsService>().SetSplitterAsync(
            client.RequireAccount(), request.X, request.Y, request.Id, request.Side, request.Items ?? [], cancellationToken);
        return new Reply("logistics", GetLogisticsHandler.Json(request.X, request.Y, state));
    }
}

/// <summary>Sets the condition of an inserter or a pump; answers with the plot's logistics like "logistics.get".</summary>
public sealed class SetConditionHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "inserter.condition";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<ConditionRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var state = await scope.ServiceProvider.GetRequiredService<LogisticsService>().SetConditionAsync(
            client.RequireAccount(), request.X, request.Y, request.Id, request.Below, request.Keep, cancellationToken);
        return new Reply("logistics", GetLogisticsHandler.Json(request.X, request.Y, state));
    }
}

/// <summary>Sets the filter of an inserter, a storage or a machine; answers with the plot's logistics like "logistics.get".</summary>
public sealed class SetFilterHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "filter.set";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FilterRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var state = await scope.ServiceProvider.GetRequiredService<LogisticsService>().SetFilterAsync(
            client.RequireAccount(), request.X, request.Y, request.Id, request.Item, cancellationToken);
        return new Reply("logistics", GetLogisticsHandler.Json(request.X, request.Y, state, true));
    }
}

public abstract class StorageHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var (state, storage) = await RunAsync(scope.ServiceProvider.GetRequiredService<LogisticsService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("storage", GetLogisticsHandler.StorageSummary(
            Storages.Find(storage.Kind)!, storage, state.Contents.GetValueOrDefault(storage.Id) ?? new Contents()));
    }

    protected abstract Task<(LogisticsState, PlotObject)> RunAsync(LogisticsService logistics, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What a storage holds now.</summary>
public sealed class GetStorageHandler(IServiceScopeFactory scopes) : StorageHandler(scopes)
{
    public override string Type => "storage.get";

    protected override Task<(LogisticsState, PlotObject)> RunAsync(LogisticsService logistics, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<StorageRequest>(payload);
        return ReadAsync(logistics, player, request, cancellationToken);
    }

    private static async Task<(LogisticsState, PlotObject)> ReadAsync(LogisticsService logistics, Guid player, StorageRequest request, CancellationToken cancellationToken)
    {
        var state = await logistics.StateAsync(player, request.X, request.Y, cancellationToken);
        var storage = state.Objects.FirstOrDefault(o => o.Id == request.Id && Storages.Find(o.Kind) is not null)
            ?? throw new GameException("no_object", "There is no such storage on this plot.");
        return (state, storage);
    }
}

public sealed class PutStorageHandler(IServiceScopeFactory scopes) : StorageHandler(scopes)
{
    public override string Type => "storage.put";

    protected override Task<(LogisticsState, PlotObject)> RunAsync(LogisticsService logistics, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<StorageItemRequest>(payload);
        return logistics.PutAsync(player, request.X, request.Y, request.Id, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class TakeStorageHandler(IServiceScopeFactory scopes) : StorageHandler(scopes)
{
    public override string Type => "storage.take";

    protected override Task<(LogisticsState, PlotObject)> RunAsync(LogisticsService logistics, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<StorageItemRequest>(payload);
        return logistics.TakeAsync(player, request.X, request.Y, request.Id, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

/// <summary>Belts and inserters that move items around a plot, and chests, piles, heaps and silos
/// that store them. Owns the <c>logistics</c> schema.</summary>
public sealed class LogisticsModule : IGameModule
{
    public string Name => "Logistics";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<LogisticsDbContext>(LogisticsDbContext.Schema);
        services.AddScoped<LogisticsService>();
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<LogisticsService>());
        services.AddScoped<IMachineContents, LogisticsContents>();
        services.AddScoped<IFreightChests, FreightChests>();
        services.AddScoped<IPlotStorage, PlotStorage>();
        services.AddSingleton<IMessageHandler, GetLogisticsHandler>();
        services.AddSingleton<IMessageHandler, SetSplitterHandler>();
        services.AddSingleton<IMessageHandler, SetFilterHandler>();
        services.AddSingleton<IMessageHandler, SetConditionHandler>();
        services.AddSingleton<IMessageHandler, GetStorageHandler>();
        services.AddSingleton<IMessageHandler, PutStorageHandler>();
        services.AddSingleton<IMessageHandler, TakeStorageHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<LogisticsDbContext>(cancellationToken);
}
