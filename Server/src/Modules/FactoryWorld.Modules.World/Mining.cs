using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>What a mine of one level gives, and what it takes to build it up to that level.</summary>
/// <param name="Units">How many times it can be worked before it runs dry.</param>
/// <param name="Refill">How long it takes to fill up again when dry, counted from its last load.</param>
/// <param name="Time">How long one load takes with the pickaxe.</param>
/// <param name="Cost">Items the owner spends to build the mine up to this level from the one below.</param>
/// <param name="Rare">How many times more often silver and gold drop: a deep mine reaches richer veins.</param>
/// <param name="Uranium">The chance in thousandths that uranium ore drops: only the deep levels reach it.</param>
public sealed record MineLevel(
    int Level, int Units, TimeSpan Refill, TimeSpan Time, IReadOnlyList<(string Item, int Quantity)> Cost, int Xp, int Rare = 1, int Uranium = 0);

/// <summary>
/// Every mountain plot has 1-5 mines. A player with a pickaxe in hand works a mine for 5 s and
/// gets up to two ores. A mine gives 50 loads in a row; 10 minutes after the last one it is full again.
/// The owner can build a mine up four times, with ingots and timber and, for the deep levels 4 and 5,
/// mine props and alloys: it then gives more loads, quicker, fills up again sooner and, deep down,
/// silver and gold more often, and uranium ore for nuclear power. Mining in the mountains goes 30% faster (<see cref="BonusPercent"/>), and
/// a bronze or steel pickaxe faster still. Some mountain plots have a mineral (sulfur, salt, marble or
/// quartz, see <see cref="Deposits"/>) that comes up with the ore. A stick of dynamite blasts
/// <see cref="BlastLoads"/> loads out at once, and a drill standing at a mine digs it by itself (<see cref="Drills"/>).
/// </summary>
public static class Mining
{
    public static readonly IReadOnlyList<MineLevel> Levels =
    [
        new(1, 50, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5), [], 0),
        new(2, 100, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(4), [(Items.IronIngot, 30), (Items.Wood, 40)], 30),
        new(3, 200, TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(3), [(Items.IronIngot, 80), (Items.CopperIngot, 30), (Items.TinIngot, 10), (Items.Provisions, 10)], 80),
        new(4, 400, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(2.5),
            [(Items.MineSupport, 20), (Items.SteelIngot, 40), (Items.BronzeIngot, 20), (Items.StoneBlocks, 40), (Items.Provisions, 20)], 150, 2, 10),
        new(5, 800, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2),
            [(Items.MineSupport, 50), (Items.SteelIngot, 100), (Items.BronzeIngot, 50), (Items.Bricks, 100), (Items.Provisions, 20)], 300, 3, 20),
    ];

    /// <summary>How much faster mining goes on a plot of the biome, in percent: mines, drills and breaking rocks.</summary>
    public static int BonusPercent(Biome biome) => biome == Biome.Mountains ? 30 : 0;

    /// <summary>How many times faster than the plain rate mining goes on a plot of the biome.</summary>
    public static double Speed(Biome biome) => 1 + BonusPercent(biome) / 100.0;

    /// <summary>Loads one stick of dynamite blasts out of a mine at once.</summary>
    public const int BlastLoads = 5;

    /// <summary>A mineral of the plot comes up in a load with this chance in thousandths, by the size of the deposit.</summary>
    public static int MineralPerMille(DepositSize size) => size switch
    {
        DepositSize.Small => 20,
        DepositSize.Medium => 40,
        DepositSize.Large => 80,
        _ => 0,
    };

    /// <summary>A mine's level by number; out of range counts as the nearest one.</summary>
    public static MineLevel Level(int level) => Levels[Math.Clamp(level, 1, Levels.Count) - 1];

    /// <summary>How long a load takes in a mine nobody has built up.</summary>
    public static TimeSpan Time => Levels[0].Time;

    /// <summary>How many times a mine nobody has built up can be worked before it runs dry.</summary>
    public static int Units => Levels[0].Units;

    /// <summary>How long a dry mine nobody has built up takes to fill up again, counted from its last load.</summary>
    public static TimeSpan Refill => Levels[0].Refill;

    /// <summary>A load holds at most this many ores; when more drop, the rarest are kept.</summary>
    public const int MaxOres = 2;

    public const int XpPerLoad = 2;

    /// <summary>Each ore drops on its own roll, with this chance in thousandths; rarest first.</summary>
    public static readonly IReadOnlyList<(string Item, int PerMille)> Drops =
    [
        (Items.GoldOre, 1),
        (Items.SilverOre, 50),
        (Items.TinOre, 100),
        (Items.CopperOre, 200),
        (Items.IronOre, 200),
        (Items.Coal, 350),
        (Items.Stone, 990),
    ];

    /// <summary>Plots of these biomes have mines.</summary>
    public static bool HasMines(Biome biome) => biome == Biome.Mountains;

    /// <summary>What one load gives, for one roll (0-999) per entry of <see cref="Drops"/>.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> Load(IReadOnlyList<int> rolls) => Load(Drops, rolls);

    public static IReadOnlyList<(string Item, int Quantity)> Load() =>
        Load(Drops.Select(_ => Random.Shared.Next(1000)).ToList());

    /// <summary>One load of a mine of <paramref name="level"/> on a plot with <paramref name="mineral"/> in it.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> Load(MineLevel level, Mineral mineral)
    {
        var drops = DropsFor(level, mineral);
        return Load(drops, drops.Select(_ => Random.Shared.Next(1000)).ToList());
    }

    /// <summary>What may drop from a mine of <paramref name="level"/> with <paramref name="mineral"/>, rarest first.</summary>
    public static IReadOnlyList<(string Item, int PerMille)> DropsFor(MineLevel level, Mineral mineral)
    {
        // Ranked by how rare each is in a plain mine, so a deep mine still keeps silver over tin.
        var drops = Drops.Select(d => (d.Item, d.PerMille, Rank: d.PerMille)).ToList();
        if (mineral.Item is { } item && MineralPerMille(mineral.Size) * (mineral.Rich ? GeologyTech.MineralFactor : 1) is > 0 and var chance)
        {
            drops.Add((item, chance, chance));
        }

        if (level.Uranium > 0)
        {
            drops.Add((Items.UraniumOre, level.Uranium, level.Uranium));
        }

        return drops
            .OrderBy(d => d.Rank)
            .Select(d => (d.Item, d.Item is Items.SilverOre or Items.GoldOre ? d.PerMille * level.Rare : d.PerMille))
            .ToList();
    }

    private static IReadOnlyList<(string Item, int Quantity)> Load(IReadOnlyList<(string Item, int PerMille)> drops, IReadOnlyList<int> rolls) =>
        drops.Where((drop, i) => rolls[i] < drop.PerMille).Take(MaxOres).Select(drop => (drop.Item, 1)).ToList();
}

/// <summary>A mine's place on its plot: it covers <see cref="Width"/> × <see cref="Height"/> tiles from (X, Y).</summary>
public sealed record MineSite(int Id, int X, int Y)
{
    public const int Width = 4;

    public const int Height = 3;
}

/// <param name="Units">Loads left until it runs dry.</param>
/// <param name="RefillAt">When a dry mine is full again; null while it is not dry.</param>
/// <param name="Speed">How many times faster than its level's rate it is worked on its plot (<see cref="Mining.Speed"/>).</param>
public sealed record MineState(MineSite Site, int Units, DateTimeOffset? RefillAt, MineLevel Level, double Speed = 1)
{
    /// <summary>How long one load takes here with an iron pickaxe.</summary>
    public TimeSpan Time => Level.Time / Speed;
}

public sealed record MineLoad(MineState Mine, IReadOnlyList<(string Item, int Quantity)> Items, XpGain Xp);

public sealed record MineUpgrade(MineState Mine, XpGain Xp);

public sealed class MineService(
    GameDb db, PlotService plots, IInventory inventory, ICharacterProgress progress, IProductionStats stats, DrillService drills, TimeProvider time,
    ITechnologies technologies)
{
    /// <summary>The mines of a plot and how many loads each has left.</summary>
    public async Task<IReadOnlyList<MineState>> MinesAsync(int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        var used = new Dictionary<int, (int Used, DateTimeOffset? EmptyAt, int Level)>();
        await using (var command = await db.CommandAsync(
            "SELECT mine, used, empty_at, level FROM world.plot_mines WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                used[reader.GetInt32(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3));
            }
        }

        var now = time.GetUtcNow();
        return PlotLayout.Mines(x, y, biome)
            .Select(site => used.TryGetValue(site.Id, out var row)
                ? MineRows.State(site, row.Used, row.EmptyAt, row.Level, biome, now)
                : MineRows.State(site, 0, null, 1, biome, now))
            .ToList();
    }

    /// <summary>Works a mine of the player's plot with the pickaxe in hand: one load of ore. A bronze or steel pickaxe works faster.</summary>
    public Task<MineLoad> MineAsync(Guid player, int x, int y, int mine, Action<TimeSpan> pace, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (site, biome) = await SiteAsync(player, x, y, mine, PlotRight.Work, cancellationToken);
            var pickaxe = await plots.RequireToolAsync(player, Items.Pickaxe, cancellationToken);
            // A drill at the mine has dug its share by now.
            await drills.CatchUpAsync(x, y, cancellationToken);
            var (used, emptyAt, level) = await MineRows.LockAsync(db, x, y, mine, cancellationToken);
            var before = MineRows.State(site, used, emptyAt, level, biome, time.GetUtcNow());
            if (before.Units == 0)
            {
                throw new GameException("mine_empty", "The mine is worked out; it fills up again in a while.");
            }

            pace(before.Time / Items.Pickaxes[pickaxe] / await plots.SpeedAsync(player, Industries.Mining, cancellationToken, (x, y, null, null)));
            var (after, items) = await DigAsync(player, x, y, biome, before, 1, cancellationToken);
            var xp = await progress.AddXpAsync(player, Mining.XpPerLoad, cancellationToken);
            return new MineLoad(after, items, xp);
        }, cancellationToken);

    /// <summary>Blasts a mine of the player's plot with a stick of dynamite from the inventory: up to
    /// <see cref="Mining.BlastLoads"/> loads at once, no pickaxe needed.</summary>
    public Task<MineLoad> BlastAsync(Guid player, int x, int y, int mine, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (site, biome) = await SiteAsync(player, x, y, mine, PlotRight.Work, cancellationToken);
            await drills.CatchUpAsync(x, y, cancellationToken);
            var (used, emptyAt, level) = await MineRows.LockAsync(db, x, y, mine, cancellationToken);
            var before = MineRows.State(site, used, emptyAt, level, biome, time.GetUtcNow());
            if (before.Units == 0)
            {
                throw new GameException("mine_empty", "The mine is worked out; it fills up again in a while.");
            }

            await inventory.RemoveAsync(player, Items.Dynamite, 1, cancellationToken);
            var loads = Math.Min(Mining.BlastLoads, before.Units);
            var (after, items) = await DigAsync(player, x, y, biome, before, loads, cancellationToken);
            await stats.RecordAsync(player, [ProductionEntry.Used(Items.Dynamite, 1, time.GetUtcNow())], cancellationToken);
            var xp = await progress.AddXpAsync(player, Mining.XpPerLoad * loads, cancellationToken);
            return new MineLoad(after, items, xp);
        }, cancellationToken);

    /// <summary>Takes <paramref name="loads"/> loads out of a mine (locked, with that many left) into the player's inventory.</summary>
    private async Task<(MineState Mine, IReadOnlyList<(string Item, int Quantity)> Items)> DigAsync(
        Guid player, int x, int y, Biome biome, MineState before, int loads, CancellationToken cancellationToken)
    {
        var units = before.Level.Units;
        var used = units - before.Units + loads;
        DateTimeOffset? emptyAt = used == units ? time.GetUtcNow() : null;
        await MineRows.WriteAsync(db, x, y, before.Site.Id, used, emptyAt, before.Level.Level, cancellationToken);

        var mineral = Deposits.MineralIn(x, y, biome) with
        {
            Rich = (await technologies.ResearchedAsync(player, cancellationToken)).Contains(GeologyTech.Id),
        };
        var items = Enumerable.Range(0, loads)
            .SelectMany(_ => Mining.Load(before.Level, mineral))
            .GroupBy(i => i.Item)
            .Select(g => (g.Key, g.Sum(i => i.Quantity)))
            .ToList();
        foreach (var (item, quantity) in items)
        {
            await inventory.ReceiveAsync(player, item, quantity, cancellationToken);
        }

        await stats.RecordAsync(player, items.Select(i => ProductionEntry.Made(i.Item1, i.Item2, time.GetUtcNow())).ToList(), cancellationToken);
        return (MineRows.State(before.Site, used, emptyAt, before.Level.Level, biome, time.GetUtcNow()), items);
    }

    /// <summary>Builds a mine of the player's plot up one level with items from the inventory. What
    /// has been dug out stays dug out, so the mine has the extra loads of the new level left over.</summary>
    public Task<MineUpgrade> UpgradeAsync(Guid player, int x, int y, int mine, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (site, biome) = await SiteAsync(player, x, y, mine, PlotRight.Build, cancellationToken);
            await drills.CatchUpAsync(x, y, cancellationToken);
            var (used, emptyAt, level) = await MineRows.LockAsync(db, x, y, mine, cancellationToken);
            if (level >= Mining.Levels.Count)
            {
                throw new GameException("mine_max_level", "This mine is built up as far as it goes.");
            }

            var next = Mining.Level(level + 1);
            foreach (var (item, quantity) in next.Cost)
            {
                if (await inventory.CountAsync(player, item, cancellationToken) < quantity)
                {
                    throw new GameException("not_enough_items", $"Building the mine up takes {quantity} × {Items.Names[item]}.");
                }
            }

            foreach (var (item, quantity) in next.Cost)
            {
                await inventory.RemoveAsync(player, item, quantity, cancellationToken);
            }

            // A mine that has rested long enough counts as full from now on.
            var now = time.GetUtcNow();
            var state = MineRows.State(site, used, emptyAt, level, biome, now);
            (used, emptyAt) = state.Units > 0 ? (state.Level.Units - state.Units, (DateTimeOffset?)null) : (used, emptyAt);
            await MineRows.WriteAsync(db, x, y, mine, used, emptyAt, next.Level, cancellationToken);
            var xp = await progress.AddXpAsync(player, next.Xp, cancellationToken);
            return new MineUpgrade(MineRows.State(site, used, emptyAt, next.Level, biome, now), xp);
        }, cancellationToken);

    private async Task<(MineSite Site, Biome Biome)> SiteAsync(Guid player, int x, int y, int mine, PlotRight right, CancellationToken cancellationToken)
    {
        var biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
        var site = PlotLayout.Mines(x, y, biome).FirstOrDefault(m => m.Id == mine)
            ?? throw new GameException("no_mine", "There is no such mine on this plot.");
        return (site, biome);
    }
}

/// <summary>Rows of <c>world.plot_mines</c>, which the player's pickaxe and the drills at a mine share.</summary>
internal static class MineRows
{
    /// <summary>The mine's row, made when it is first touched and locked until the transaction ends.</summary>
    public static async Task<(int Used, DateTimeOffset? EmptyAt, int Level)> LockAsync(GameDb db, int x, int y, int mine, CancellationToken cancellationToken)
    {
        await using (var insert = await db.CommandAsync(
            "INSERT INTO world.plot_mines (x, y, mine, used) VALUES (@x, @y, @mine, 0) ON CONFLICT DO NOTHING", cancellationToken))
        {
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("mine", mine);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = await db.CommandAsync(
            "SELECT used, empty_at, level FROM world.plot_mines WHERE x = @x AND y = @y AND mine = @mine FOR UPDATE", cancellationToken);
        select.Parameters.AddWithValue("x", x);
        select.Parameters.AddWithValue("y", y);
        select.Parameters.AddWithValue("mine", mine);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2));
    }

    public static async Task WriteAsync(GameDb db, int x, int y, int mine, int used, DateTimeOffset? emptyAt, int level, CancellationToken cancellationToken)
    {
        await using var update = await db.CommandAsync(
            "UPDATE world.plot_mines SET used = @used, empty_at = @empty, level = @level WHERE x = @x AND y = @y AND mine = @mine", cancellationToken);
        update.Parameters.AddWithValue("x", x);
        update.Parameters.AddWithValue("y", y);
        update.Parameters.AddWithValue("mine", mine);
        update.Parameters.AddWithValue("used", used);
        update.Parameters.AddWithValue("empty", (object?)emptyAt ?? DBNull.Value);
        update.Parameters.AddWithValue("level", level);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A mine with <paramref name="used"/> loads taken; a dry one is full again once it has rested.</summary>
    public static MineState State(MineSite site, int used, DateTimeOffset? emptyAt, int level, Biome biome, DateTimeOffset now)
    {
        var l = Mining.Level(level);
        var speed = Mining.Speed(biome);
        if (emptyAt is { } at)
        {
            return at + l.Refill <= now ? new MineState(site, l.Units, null, l, speed) : new MineState(site, 0, at + l.Refill, l, speed);
        }

        return new MineState(site, Math.Max(l.Units - used, 0), null, l, speed);
    }
}

public sealed record MineRequest(int X, int Y, int Mine);

/// <summary>Builds a mine up one level: "plot.mineUpgraded" with the mine and the experience gained.</summary>
public sealed class UpgradeMineHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.upgradeMine";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<MineRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var upgrade = await scope.ServiceProvider.GetRequiredService<MineService>().UpgradeAsync(
            client.RequireAccount(), request.X, request.Y, request.Mine, cancellationToken);
        return new Reply("plot.mineUpgraded", new
        {
            mine = MineHandler.MineJson(upgrade.Mine),
            xp = new { xp = upgrade.Xp.Xp, level = upgrade.Xp.Level, leveledUp = upgrade.Xp.LeveledUp },
        });
    }
}

/// <summary>Blasts a mine with dynamite: "plot.mined", as for a load dug with the pickaxe.</summary>
public sealed class BlastMineHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.blast";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<MineRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var load = await scope.ServiceProvider.GetRequiredService<MineService>().BlastAsync(
            client.RequireAccount(), request.X, request.Y, request.Mine, cancellationToken);
        return MineHandler.MinedReply(load, time, Mining.BlastLoads);
    }
}

public sealed class MineHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.mine";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<MineRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var load = await scope.ServiceProvider.GetRequiredService<MineService>().MineAsync(
            client.RequireAccount(), request.X, request.Y, request.Mine, work => HarvestHandler.CheckPace(client, time, work), cancellationToken);
        HarvestHandler.Swung(client, time);
        return MinedReply(load, time, 0);
    }

    /// <param name="blasted">Loads a stick of dynamite blasted out at most, or 0 for a load dug by hand.</param>
    public static Reply MinedReply(MineLoad load, TimeProvider time, int blasted) => new("plot.mined", new
    {
        mine = MineJson(load.Mine),
        items = load.Items.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
        xp = new { xp = load.Xp.Xp, level = load.Xp.Level, leveledUp = load.Xp.LeveledUp },
        blasted,
        serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
    });

    public static object MineJson(MineState mine) => new
    {
        id = mine.Site.Id,
        x = mine.Site.X,
        y = mine.Site.Y,
        width = MineSite.Width,
        height = MineSite.Height,
        units = mine.Units,
        refillAt = mine.RefillAt?.ToUnixTimeMilliseconds(),
        level = mine.Level.Level,
        // What this level gives: loads when full, seconds a load takes here with an iron pickaxe and seconds to fill up again.
        capacity = mine.Level.Units,
        seconds = mine.Time.TotalSeconds,
        refillSeconds = mine.Level.Refill.TotalSeconds,
        // How many times more often silver and gold come up than in a plain mine.
        rare = mine.Level.Rare,
        // The next level and what it takes, or null when the mine is built up all the way.
        upgrade = mine.Level.Level < Mining.Levels.Count && Mining.Level(mine.Level.Level + 1) is var next
            ? new
            {
                level = next.Level,
                capacity = next.Units,
                seconds = (next.Time / mine.Speed).TotalSeconds,
                refillSeconds = next.Refill.TotalSeconds,
                rare = next.Rare,
                cost = next.Cost.Select(c => new { id = c.Item, name = Items.Names[c.Item], quantity = c.Quantity }),
            }
            : null,
    };
}
