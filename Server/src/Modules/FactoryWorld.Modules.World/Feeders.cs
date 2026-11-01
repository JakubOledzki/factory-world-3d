using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>
/// A feeder stands on one tile by the pasture and feeds every hungry animal within <see cref="Reach"/> tiles of it by itself,
/// <see cref="Husbandry.FeedPerMeal"/> pieces of a feed the animal eats, as if fed by hand. The feed goes in by hand or by an
/// inserter, up to <see cref="Capacity"/> pieces of any feed together. An animal is fed the moment it is hungry: when let out,
/// when its product is collected, or when feed comes in for it. Products are still collected by hand. An automatic barn on 2 × 2
/// tiles feeds the animals within <see cref="BarnReach"/> of its tile the same way and collects what they give as well, the moment
/// they give it (eggs, wool, milk, meat, manure and the young of two of a kind side by side), up to <see cref="BarnOutput"/> pieces,
/// and feeds them again at once. Its products are taken out by hand or by an inserter.
/// </summary>
public static class Feeders
{
    public const int Capacity = 60;

    public const int Reach = 3;

    /// <summary>Feed an automatic barn holds.</summary>
    public const int BarnCapacity = 100;

    /// <summary>An automatic barn feeds and collects from the animals this many tiles from its tile each way.</summary>
    public const int BarnReach = 4;

    /// <summary>Products an automatic barn keeps; full, it leaves the animals be until its products are taken out.</summary>
    public const int BarnOutput = 200;

    /// <summary>A feeder or an automatic barn.</summary>
    public static bool IsFeeder(string kind) => kind is Items.Feeder or Items.AutoBarn;

    public static int CapacityOf(string kind) => kind == Items.AutoBarn ? BarnCapacity : Capacity;

    public static int ReachOf(string kind) => kind == Items.AutoBarn ? BarnReach : Reach;

    public static bool Reaches(int feederX, int feederY, int tileX, int tileY) =>
        Math.Abs(tileX - feederX) <= Reach && Math.Abs(tileY - feederY) <= Reach;

    public static bool Reaches(string kind, int feederX, int feederY, int tileX, int tileY) =>
        Math.Abs(tileX - feederX) <= ReachOf(kind) && Math.Abs(tileY - feederY) <= ReachOf(kind);
}

/// <summary>One feeder or automatic barn in memory: where it stands, the feed in it and, in a barn, what it collected.</summary>
public sealed class Trough(Guid id, Guid owner, string kind, int tileX, int tileY, IReadOnlyDictionary<string, int> store, IReadOnlyDictionary<string, int>? output = null)
{
    public Guid Id { get; } = id;

    public Guid Owner { get; } = owner;

    public string Kind { get; } = kind;

    public int TileX { get; } = tileX;

    public int TileY { get; } = tileY;

    public Dictionary<string, int> Store { get; } = store.Where(s => s.Value > 0).ToDictionary();

    /// <summary>What an automatic barn collected; always empty in a plain feeder.</summary>
    public Dictionary<string, int> Output { get; } = (output ?? new Dictionary<string, int>()).Where(s => s.Value > 0).ToDictionary();

    /// <summary>Whether it collects what the animals give: an automatic barn.</summary>
    public bool Collects => Kind == Items.AutoBarn;

    public int Capacity => Feeders.CapacityOf(Kind);

    public int Reach => Feeders.ReachOf(Kind);

    public int Feed => Store.Values.Sum();

    public int Room => Math.Max(0, Capacity - Feed);

    public int Collected => Output.Values.Sum();

    public bool Changed { get; set; }

    public bool Reaches(int tileX, int tileY) => Feeders.Reaches(Kind, TileX, TileY, tileX, tileY);

    /// <summary>Puts feed in, as much as there is room for; returns how much went in.</summary>
    public int Add(string item, int quantity)
    {
        var added = Math.Clamp(quantity, 0, Room);
        if (added > 0)
        {
            Store[item] = Store.GetValueOrDefault(item) + added;
            Changed = true;
        }

        return added;
    }

    /// <summary>What is in it, feed and what it collected together, by item.</summary>
    public IReadOnlyList<(string Item, int Quantity)> Contents() =>
        Store.Concat(Output).GroupBy(s => s.Key).OrderBy(g => g.Key).Select(g => (g.Key, g.Sum(s => s.Value))).ToList();

    /// <summary>Takes up to <paramref name="quantity"/> pieces of an item out, of the feed first, then of what it collected;
    /// returns how many.</summary>
    public int Take(string item, int quantity)
    {
        var taken = 0;
        foreach (var bin in new[] { Store, Output })
        {
            var some = Math.Min(quantity - taken, bin.GetValueOrDefault(item));
            if (some > 0)
            {
                bin[item] -= some;
                if (bin[item] == 0)
                {
                    bin.Remove(item);
                }

                taken += some;
                Changed = true;
            }
        }

        return taken;
    }

    /// <summary>Takes one piece of a product out of an automatic barn.</summary>
    public void TakeOne(string item)
    {
        if (Output.GetValueOrDefault(item) > 0)
        {
            Output[item]--;
            if (Output[item] == 0)
            {
                Output.Remove(item);
            }

            Changed = true;
        }
    }

    /// <summary>A meal for an animal that eats <paramref name="eats"/>, taken out of the feeder: the first feed it has enough of, in that order.</summary>
    public string? Serve(IReadOnlyList<string> eats)
    {
        var feed = eats.FirstOrDefault(f => Store.GetValueOrDefault(f) >= Husbandry.FeedPerMeal);
        if (feed is null)
        {
            return null;
        }

        Store[feed] -= Husbandry.FeedPerMeal;
        if (Store[feed] == 0)
        {
            Store.Remove(feed);
        }

        Changed = true;
        return feed;
    }

    /// <summary>Puts what an animal gave into an automatic barn, if all of it fits.</summary>
    public bool Collect(IReadOnlyList<(string Item, int Quantity)> gives)
    {
        if (Collected + gives.Sum(g => g.Quantity) > Feeders.BarnOutput)
        {
            return false;
        }

        foreach (var (item, quantity) in gives)
        {
            Output[item] = Output.GetValueOrDefault(item) + quantity;
        }

        Changed = true;
        return true;
    }
}

/// <summary>An animal of a plot as its feeders and barns see it.</summary>
public sealed class Beast(int x, int y, string kind, DateTimeOffset? readyAt, int meals)
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public string Kind { get; } = kind;

    public DateTimeOffset? ReadyAt { get; set; } = readyAt;

    public int Meals { get; set; } = meals;

    public bool Changed { get; set; }
}

/// <summary>The feeders and barns of a plot and its animals, in memory: fed and collected from up to a moment, then written back.</summary>
public sealed class Yard(Biome biome, IReadOnlyList<Trough> troughs, IEnumerable<Beast> animals)
{
    public IReadOnlyList<Trough> Troughs { get; } = troughs.OrderBy(t => t.TileY).ThenBy(t => t.TileX).ThenBy(t => t.Id).ToList();

    /// <summary>The animals read: all of them when a barn stands on the plot, otherwise the hungry ones; north to south, west to east.</summary>
    private readonly List<Beast> _animals = animals.OrderBy(a => a.Y).ThenBy(a => a.X).ToList();

    /// <summary>Animals fed since the yard was read, and when each will give its product.</summary>
    public Dictionary<(int X, int Y), DateTimeOffset> Fed { get; } = [];

    /// <summary>The feed eaten and the products collected, by the owner of the feeder or barn.</summary>
    public List<(Guid Owner, ProductionEntry Entry)> Eaten { get; } = [];

    /// <summary>The animals whose time or meals changed, to be written back.</summary>
    public IEnumerable<Beast> Changed => _animals.Where(a => a.Changed);

    /// <summary>When the next animal in reach of <paramref name="barn"/> gives something after <paramref name="now"/>, or null.</summary>
    public DateTimeOffset? NextReady(Trough barn, DateTimeOffset now) =>
        _animals.Where(a => a.ReadyAt > now && barn.Reaches(a.X, a.Y)).Select(a => a.ReadyAt).Min();

    /// <summary>
    /// Collects what the animals in reach of a barn gave up to <paramref name="now"/>, each fed again from a feeder in reach at the
    /// moment it gave, then feeds every hungry animal in reach of a feeder that has what it eats; the first feeder in reach serves.
    /// </summary>
    public void Feed(DateTimeOffset now)
    {
        if (Troughs.Count == 0)
        {
            return;
        }

        foreach (var animal in _animals)
        {
            if (!Husbandry.ById.TryGetValue(animal.Kind, out var kind) || Troughs.FirstOrDefault(t => t.Collects && t.Reaches(animal.X, animal.Y)) is not { } barn)
            {
                continue;
            }

            while (animal.ReadyAt is { } ready && ready <= now)
            {
                var gives = kind.Gives.ToList();
                var meals = animal.Meals + 1;
                if (kind.BreedEvery > 0 && meals % kind.BreedEvery == 0 && _animals.Any(o => o != animal && o.Kind == animal.Kind
                    && Math.Abs(o.X - animal.X) <= 1 && Math.Abs(o.Y - animal.Y) <= 1))
                {
                    gives.Add((animal.Kind, 1));
                }

                if (!barn.Collect(gives))
                {
                    break;
                }

                Eaten.AddRange(gives.Select(g => (barn.Owner, ProductionEntry.Made(g.Item, g.Quantity, ready))));
                animal.Meals = meals;
                animal.ReadyAt = null;
                animal.Changed = true;
                if (!Serve(animal, kind, ready))
                {
                    break;
                }
            }
        }

        foreach (var animal in _animals.Where(a => a.ReadyAt is null))
        {
            if (Husbandry.ById.TryGetValue(animal.Kind, out var kind))
            {
                Serve(animal, kind, now);
            }
        }
    }

    /// <summary>Feeds a hungry animal at <paramref name="at"/> from the first feeder in reach that has what it eats.</summary>
    private bool Serve(Beast animal, Animal kind, DateTimeOffset at)
    {
        foreach (var trough in Troughs.Where(t => t.Reaches(animal.X, animal.Y)))
        {
            if (trough.Serve(kind.Eats) is { } feed)
            {
                animal.ReadyAt = at + (Husbandry.Time(kind, biome) ?? kind.Time);
                animal.Changed = true;
                Fed[(animal.X, animal.Y)] = animal.ReadyAt.Value;
                Eaten.Add((trough.Owner, ProductionEntry.Used(feed, Husbandry.FeedPerMeal, at)));
                return true;
            }
        }

        return false;
    }
}

public sealed class FeederService(
    GameDb db, WorldMap map, PlotService plots, IPlotObjects objects, IInventory inventory, IProductionStats stats, TimeProvider time)
    : IPlacedHook, IPlotAutomation, IMachineContents, IMachinePorts
{
    /// <summary>The feeder as it is now, its hungry animals in reach fed.</summary>
    public Task<(Trough Feeder, IReadOnlyList<PlotAnimal> Animals)> StateAsync(
        Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await CatchUpAllAsync(x, y, automation, cancellationToken);
            var yard = await OpenAsync(x, y, cancellationToken);
            var feeder = Find(yard, id);
            yard.Feed(time.GetUtcNow());
            await SaveAsync(x, y, yard, cancellationToken);
            return (feeder, await AnimalsAsync(x, y, cancellationToken));
        }, cancellationToken);

    /// <summary>Puts up to <paramref name="quantity"/> pieces of a feed from the inventory into a feeder, as much as there is room for, and feeds the hungry animals.</summary>
    public Task<(Trough Feeder, IReadOnlyList<PlotAnimal> Animals)> FillAsync(
        Guid player, int x, int y, Guid id, string item, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Husbandry.AnyFeed.Contains(item))
            {
                throw new GameException("bad_feed", "Animals eat wheat, corn, carrots, cabbage, fish meal, dates, beet pulp and concentrate.");
            }

            if (quantity is < 1 or > Feeders.BarnCapacity)
            {
                throw new GameException("bad_quantity", $"Put in 1 to {Feeders.BarnCapacity} pieces.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var yard = await OpenAsync(x, y, cancellationToken);
            var feeder = Find(yard, id);
            var added = feeder.Add(item, quantity);
            if (added == 0)
            {
                throw new GameException("feeder_full", $"It holds at most {feeder.Capacity} pieces of feed.");
            }

            await inventory.RemoveAsync(player, item, added, cancellationToken);
            yard.Feed(time.GetUtcNow());
            await SaveAsync(x, y, yard, cancellationToken);
            return (feeder, await AnimalsAsync(x, y, cancellationToken));
        }, cancellationToken);

    /// <summary>Takes the feed out of a feeder into the inventory, and from an automatic barn what it collected too, each as far
    /// as it fits; the rest stays in.</summary>
    public Task<(Trough Feeder, IReadOnlyList<PlotAnimal> Animals, IReadOnlyList<(string Item, int Quantity)> Taken)> TakeAsync(
        Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            var yard = await OpenAsync(x, y, cancellationToken);
            var feeder = Find(yard, id);
            yard.Feed(time.GetUtcNow());
            var inside = feeder.Contents();
            var taken = new List<(string Item, int Quantity)>();
            foreach (var (item, quantity) in inside)
            {
                var fits = (int)await inventory.RoomForAsync(player, item, quantity, cancellationToken);
                if (fits > 0)
                {
                    feeder.Take(item, fits);
                    await inventory.AddAsync(player, item, fits, cancellationToken);
                    taken.Add((item, fits));
                }
            }

            if (taken.Count == 0 && inside.Count > 0)
            {
                throw InventoryExtensions.InventoryFull();
            }

            await SaveAsync(x, y, yard, cancellationToken);
            return (feeder, await AnimalsAsync(x, y, cancellationToken), (IReadOnlyList<(string, int)>)taken);
        }, cancellationToken);

    /// <summary>The plot's animals after its feeders fed the hungry ones; the client asks every few seconds while a feeder stands there.</summary>
    public Task<IReadOnlyList<PlotAnimal>> FeedAnimalsAsync(Guid player, int x, int y, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await CatchUpAllAsync(x, y, automation, cancellationToken);
            return await AnimalsAsync(x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Works the plot's machinery up to now, inserters filling feeders among it, then feeds the hungry animals.</summary>
    private async Task CatchUpAllAsync(int x, int y, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken)
    {
        foreach (var machinery in automation)
        {
            await machinery.CatchUpAsync(x, y, cancellationToken);
        }

        await CatchUpAsync(x, y, cancellationToken);
    }

    /// <summary>
    /// Locks the plot's feeders before an animal there is locked, so that an inserter filling a feeder and a player
    /// collecting from an animal always lock in the same order.
    /// </summary>
    public async Task LockAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT object_id FROM world.plot_feeders WHERE x = @x AND y = @y ORDER BY object_id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
        }
    }

    /// <summary>Feeds the plot's hungry animals now; returns when each fed animal will give its product.</summary>
    public async Task<IReadOnlyDictionary<(int X, int Y), DateTimeOffset>> FeedAsync(int x, int y, CancellationToken cancellationToken)
    {
        var yard = await OpenAsync(x, y, cancellationToken);
        yard.Feed(time.GetUtcNow());
        var fed = yard.Fed.ToDictionary();
        await SaveAsync(x, y, yard, cancellationToken);
        return fed;
    }

    public async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken) => await FeedAsync(x, y, cancellationToken);

    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (!Feeders.IsFeeder(placed.Kind))
        {
            return;
        }

        await using var insert = await db.CommandAsync(
            "INSERT INTO world.plot_feeders (object_id, x, y, owner_id, store) VALUES (@id, @x, @y, @owner, '{}') ON CONFLICT DO NOTHING", cancellationToken);
        insert.Parameters.AddWithValue("id", placed.Id);
        insert.Parameters.AddWithValue("x", x);
        insert.Parameters.AddWithValue("y", y);
        insert.Parameters.AddWithValue("owner", owner);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The feeder is being picked up: the feed in it goes back to the player.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM world.plot_feeders WHERE object_id = @id RETURNING store, output", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        var back = new List<KeyValuePair<string, int>>();
        await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                back = [.. ReadStore(reader.GetString(0)), .. ReadStore(reader.GetString(1))];
            }
        }

        foreach (var (item, quantity) in back.Where(b => b.Value > 0))
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }
    }

    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Feeders.IsFeeder(kind) || await objects.PlotOfAsync(id, cancellationToken) is not var (x, y))
        {
            return null;
        }

        var yard = _prepared is { } prepared && prepared.X == x && prepared.Y == y ? prepared.Yard : await OpenAsync(x, y, cancellationToken);
        _prepared = (x, y, yard);
        return yard.Troughs.FirstOrDefault(t => t.Id == id) is { } trough ? new Port(this, x, y, yard, trough) : null;
    }

    /// <summary>The plot's yard opened once for all the feeders of one run of its logistics, as they share its animals.</summary>
    private (int X, int Y, Yard Yard)? _prepared;

    public async Task PrepareAsync(IReadOnlyList<PlotObject> reached, CancellationToken cancellationToken)
    {
        _prepared = null;
        if (reached.FirstOrDefault(o => Feeders.IsFeeder(o.Kind)) is { } first && await objects.PlotOfAsync(first.Id, cancellationToken) is var (x, y))
        {
            _prepared = (x, y, await OpenAsync(x, y, cancellationToken));
        }
    }

    private static Trough Find(Yard yard, Guid id) =>
        yard.Troughs.FirstOrDefault(t => t.Id == id) ?? throw new GameException("no_object", "There is no such feeder on this plot.");

    private static Dictionary<string, int> ReadStore(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(json) is { } store ? store.Where(s => Items.Exists(s.Key)).ToDictionary() : [];

    /// <summary>The feeders and barns of a plot and its animals, both locked until the transaction ends (feeders first): the hungry
    /// animals only, unless a barn stands there to collect from the others.</summary>
    private async Task<Yard> OpenAsync(int x, int y, CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, Guid Owner, string Store, string Output)>();
        await using (var command = await db.CommandAsync(
            "SELECT object_id, owner_id, store, output FROM world.plot_feeders WHERE x = @x AND y = @y ORDER BY object_id FOR UPDATE", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        // Most plots have no feeder: then there is nothing more to read.
        var troughs = new List<Trough>();
        if (rows.Count > 0)
        {
            var placed = (await objects.ObjectsAsync(x, y, cancellationToken)).Where(o => Feeders.IsFeeder(o.Kind))
                .GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
            troughs.AddRange(rows.Where(r => placed.ContainsKey(r.Id)).Select(r =>
                new Trough(r.Id, r.Owner, placed[r.Id].Kind, placed[r.Id].TileX, placed[r.Id].TileY, ReadStore(r.Store), ReadStore(r.Output))));
        }

        var animals = new List<Beast>();
        if (troughs.Count > 0)
        {
            await using var command = await db.CommandAsync(
                """
                SELECT tile_x, tile_y, kind, ready_at, meals FROM world.plot_animals WHERE x = @x AND y = @y AND (@all OR ready_at IS NULL)
                ORDER BY tile_y, tile_x FOR UPDATE
                """, cancellationToken);
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("all", troughs.Any(t => t.Collects));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                animals.Add(new Beast(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4)));
            }
        }

        return new Yard(map.BiomeAt(x, y), troughs, animals);
    }

    /// <summary>Writes back the feeders that changed and the animals they fed.</summary>
    private async Task SaveAsync(int x, int y, Yard yard, CancellationToken cancellationToken)
    {
        foreach (var trough in yard.Troughs.Where(t => t.Changed))
        {
            await using var update = await db.CommandAsync("UPDATE world.plot_feeders SET store = @store, output = @output WHERE object_id = @id", cancellationToken);
            update.Parameters.AddWithValue("id", trough.Id);
            update.Parameters.AddWithValue("store", JsonSerializer.Serialize(trough.Store));
            update.Parameters.AddWithValue("output", JsonSerializer.Serialize(trough.Output));
            await update.ExecuteNonQueryAsync(cancellationToken);
            trough.Changed = false;
        }

        // The animals fed, and those a barn collected from: their time and meals, a hungry one with no time.
        var changed = yard.Changed.ToList();
        if (changed.Count > 0)
        {
            await using var update = await db.CommandAsync(
                """
                UPDATE world.plot_animals AS a SET ready_at = CASE WHEN t.fed THEN t.ready END, meals = t.meals
                FROM unnest(@txs, @tys, @fed, @ready, @meals) AS t(tx, ty, fed, ready, meals)
                WHERE a.x = @x AND a.y = @y AND a.tile_x = t.tx AND a.tile_y = t.ty
                """, cancellationToken);
            update.Parameters.AddWithValue("x", x);
            update.Parameters.AddWithValue("y", y);
            update.Parameters.AddWithValue("txs", changed.Select(a => a.X).ToArray());
            update.Parameters.AddWithValue("tys", changed.Select(a => a.Y).ToArray());
            update.Parameters.AddWithValue("fed", changed.Select(a => a.ReadyAt is not null).ToArray());
            update.Parameters.Add(new Npgsql.NpgsqlParameter("ready", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz)
            {
                Value = changed.Select(a => a.ReadyAt ?? DateTimeOffset.UnixEpoch).ToArray(),
            });
            update.Parameters.AddWithValue("meals", changed.Select(a => a.Meals).ToArray());
            await update.ExecuteNonQueryAsync(cancellationToken);
            foreach (var animal in changed)
            {
                animal.Changed = false;
            }
        }

        yard.Fed.Clear();

        foreach (var owner in yard.Eaten.GroupBy(e => e.Owner))
        {
            await stats.RecordAsync(owner.Key, owner.Select(e => e.Entry).ToList(), cancellationToken);
        }

        yard.Eaten.Clear();
    }

    private async Task<IReadOnlyList<PlotAnimal>> AnimalsAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, kind, ready_at, meals FROM world.plot_animals WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var animals = new List<PlotAnimal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            animals.Add(new PlotAnimal(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4)));
        }

        return animals;
    }

    /// <summary>A feeder as inserters see it: feed goes in and the hungry animals around it eat at once; out of an automatic barn come
    /// what it collected.</summary>
    private sealed class Port(FeederService service, int x, int y, Yard yard, Trough trough) : IMachinePort
    {
        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            if (!trough.Collects)
            {
                return null;
            }

            yard.Feed(now);
            return trough.Output.Where(o => o.Value > 0).Select(o => o.Key).OrderBy(k => k).FirstOrDefault(wanted);
        }

        public void Take(string item, DateTimeOffset now) => trough.TakeOne(item);

        public bool Accepts(string item, DateTimeOffset now) => Husbandry.AnyFeed.Contains(item) && trough.Room > 0;

        public void Put(string item, DateTimeOffset now)
        {
            trough.Add(item, 1);
            yard.Feed(now);
        }

        /// <summary>An automatic barn has something new to give when the next animal in its reach does.</summary>
        public DateTimeOffset? NextChange(DateTimeOffset now) => trough.Collects ? yard.NextReady(trough, now) : null;

        public bool Predictable => true;

        public Task SaveAsync(CancellationToken cancellationToken) => service.SaveAsync(x, y, yard, cancellationToken);
    }
}

public sealed record FeederRequest(int X, int Y, Guid Id, string? Item = null, int Quantity = 0);

/// <summary>feeder.get, feeder.fill and feeder.take answer with the feeder and the plot's animals.</summary>
public abstract class FeederHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FeederRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (feeder, animals, taken) = await RunAsync(
            scope.ServiceProvider.GetRequiredService<FeederService>(), scope.ServiceProvider.GetServices<IPlotAutomation>(), client.RequireAccount(), request, cancellationToken);
        var reached = animals.Where(a => feeder.Reaches(a.TileX, a.TileY)).ToList();
        return new Reply("feeder", new
        {
            x = request.X,
            y = request.Y,
            feeder = new
            {
                id = feeder.Id,
                tileX = feeder.TileX,
                tileY = feeder.TileY,
                store = feeder.Store.OrderBy(s => s.Key).Select(s => new { id = s.Key, name = Items.Names[s.Key], quantity = s.Value }),
                kind = feeder.Kind,
                feed = feeder.Feed,
                capacity = feeder.Capacity,
                reach = feeder.Reach,
                // An automatic barn collects what the animals in its reach give: here, up to maxOutput pieces.
                collects = feeder.Collects,
                output = feeder.Output.OrderBy(s => s.Key).Select(s => new { id = s.Key, name = Items.Names[s.Key], quantity = s.Value }),
                maxOutput = feeder.Collects ? Feeders.BarnOutput : 0,
                feedPerMeal = Husbandry.FeedPerMeal,
                animals = reached.Count,
                hungry = reached.Count(a => a.ReadyAt is null),
            },
            animals = animals.Select(AnimalHandler.AnimalJson),
            taken = taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<(Trough, IReadOnlyList<PlotAnimal>, IReadOnlyList<(string Item, int Quantity)>)> RunAsync(
        FeederService feeders, IEnumerable<IPlotAutomation> automation, Guid player, FeederRequest request, CancellationToken cancellationToken);
}

public sealed class GetFeederHandler(IServiceScopeFactory scopes, TimeProvider time) : FeederHandler(scopes, time)
{
    public override string Type => "feeder.get";

    protected override async Task<(Trough, IReadOnlyList<PlotAnimal>, IReadOnlyList<(string Item, int Quantity)>)> RunAsync(
        FeederService feeders, IEnumerable<IPlotAutomation> automation, Guid player, FeederRequest r, CancellationToken cancellationToken)
    {
        var (feeder, animals) = await feeders.StateAsync(player, r.X, r.Y, r.Id, automation, cancellationToken);
        return (feeder, animals, []);
    }
}

public sealed class FillFeederHandler(IServiceScopeFactory scopes, TimeProvider time) : FeederHandler(scopes, time)
{
    public override string Type => "feeder.fill";

    protected override async Task<(Trough, IReadOnlyList<PlotAnimal>, IReadOnlyList<(string Item, int Quantity)>)> RunAsync(
        FeederService feeders, IEnumerable<IPlotAutomation> automation, Guid player, FeederRequest r, CancellationToken cancellationToken)
    {
        var (feeder, animals) = await feeders.FillAsync(player, r.X, r.Y, r.Id, r.Item ?? "", r.Quantity, cancellationToken);
        return (feeder, animals, []);
    }
}

public sealed class TakeFromFeederHandler(IServiceScopeFactory scopes, TimeProvider time) : FeederHandler(scopes, time)
{
    public override string Type => "feeder.take";

    protected override Task<(Trough, IReadOnlyList<PlotAnimal>, IReadOnlyList<(string Item, int Quantity)>)> RunAsync(
        FeederService feeders, IEnumerable<IPlotAutomation> automation, Guid player, FeederRequest r, CancellationToken cancellationToken) =>
        feeders.TakeAsync(player, r.X, r.Y, r.Id, cancellationToken);
}

public sealed record AnimalsRequest(int X, int Y);

/// <summary>The plot's animals once its feeders fed the hungry ones, for the client to redraw them while a feeder stands there.</summary>
public sealed class GetAnimalsHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.animals";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<AnimalsRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var animals = await scope.ServiceProvider.GetRequiredService<FeederService>().FeedAnimalsAsync(
            client.RequireAccount(), request.X, request.Y, scope.ServiceProvider.GetServices<IPlotAutomation>(), cancellationToken);
        return new Reply("plot.animals", new
        {
            x = request.X,
            y = request.Y,
            animals = animals.Select(AnimalHandler.AnimalJson),
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }
}
