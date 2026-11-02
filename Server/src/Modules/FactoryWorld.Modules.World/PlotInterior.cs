using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

public sealed class PlotService(
    GameDb db, WorldMap map, IInventory inventory, IEquipment equipment, ICharacterProgress progress, IProductionStats stats,
    IEnumerable<ICompanyPlots> companies, BeachService beach, PestService pests, ForestLedger ledger, IOutfit outfit, PlotTaxService taxes,
    TimeProvider time, IServiceProvider services, PlotPermits permits, IPlotClock plotClock)
{
    /// <summary>The tiles of a plot as they are now; only its owner and members of its company may enter, or anyone when it is public.</summary>
    public async Task<byte[]> TilesAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        var biome = await RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Visit);
        return await TilesAsync(x, y, biome, cancellationToken);
    }

    /// <summary>The tiles of a plot of <paramref name="biome"/> as they are now, whoever asks.</summary>
    public async Task<byte[]> TilesAsync(int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        var tiles = PlotLayout.Generate(x, y, biome);
        // On sand the last sandstorm blew the dunes dug before it back, where nothing stands and no reed fence is near; on the
        // glacier the ice cut before the last blizzard froze again, and its avalanches left snow about.
        var blizzard = biome == Biome.Ice ? Blizzard.LastEnded(time.GetUtcNow()) : null;
        var storm = Oasis.Sandy((int)biome) ? SandWeather.LastEnded(time.GetUtcNow()) : blizzard?.End;
        var dug = new HashSet<int>();
        var now = time.GetUtcNow();
        var picked = now - Forestry.MushroomRegrow;
        var harvests = new List<(int TileX, int TileY, DateTimeOffset At)>();
        // What stands on a tile keeps what was gathered there from coming back; a reed fence shelters the tiles around it.
        // Each table is read once and matched here, as a plot may have hundreds of objects and of tiles gathered.
        var standing = new HashSet<(int, int)>();
        var fences = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT 0, square, 0, harvested_at, NULL FROM world.plot_harvests WHERE x = @x AND y = @y
            UNION ALL SELECT 1, tile_x, tile_y, NULL, kind FROM world.plot_objects WHERE x = @x AND y = @y
            UNION ALL SELECT 2, tile_x, tile_y, NULL, NULL FROM world.plot_fields WHERE x = @x AND y = @y
            UNION ALL SELECT 2, tile_x, tile_y, NULL, NULL FROM world.plot_trees WHERE x = @x AND y = @y
            UNION ALL SELECT 2, tile_x, tile_y, NULL, NULL FROM world.plot_animals WHERE x = @x AND y = @y
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var (tx, ty) = (reader.GetInt32(1), reader.GetInt32(2));
                switch (reader.GetInt32(0))
                {
                    case 0:
                        harvests.AddRange(PlotHarvests.Gathered(tx, reader.GetFieldValue<DateTimeOffset?[]>(3)));
                        break;
                    case 1:
                        var kind = reader.GetString(4);
                        standing.UnionWith(Footprints.Tiles(kind, tx, ty));
                        if (kind == Items.ReedFence)
                        {
                            fences.Add((tx, ty));
                        }

                        break;
                    default:
                        standing.Add((tx, ty));
                        break;
                }
            }
        }

        foreach (var (tx, ty) in fences)
        {
            for (var dy = -PlotLayout.FenceShelter; dy <= PlotLayout.FenceShelter; dy++)
            {
                for (var dx = -PlotLayout.FenceShelter; dx <= PlotLayout.FenceShelter; dx++)
                {
                    standing.Add((tx + dx, ty + dy));
                }
            }
        }

        foreach (var (tileX, tileY, harvestedAt) in harvests)
        {
            // A rock broken before the mines came may have stood where a mine is now, and the
            // lake or sea came over what had been gathered on the shore.
            var at = tileY * PlotLayout.Size + tileX;
            // Sand and ice come back with the storm; mushrooms come up again by themselves.
            // Gathered before the storm or long enough ago, with nothing standing there since.
            var beforeStorm = harvestedAt < storm;
            var due = (beforeStorm || harvestedAt < picked) && !standing.Contains((tileX, tileY));
            var regrown = due && ((beforeStorm && tiles[at] is (byte)TileObject.Dune or (byte)TileObject.IceBlock)
                || tiles[at] == (byte)TileObject.Mushrooms);
            if (harvestedAt >= storm)
            {
                dug.Add(at);
            }

            if (tiles[at] == (byte)TileObject.Crevasse)
            {
                tiles[at] = (byte)TileObject.Bridge;
            }
            else if (tiles[at] is not ((byte)TileObject.Mine or (byte)TileObject.Water) && !regrown)
            {
                tiles[at] = (byte)TileObject.None;
            }
        }

        // Snow the avalanches of the last blizzard left on open ice and on whatever stands there, where nobody dug it away since.
        if (blizzard is { } last)
        {
            for (var ty = PlotLayout.ClearRows; ty < PlotLayout.Size; ty++)
            {
                for (var tx = 0; tx < PlotLayout.Size; tx++)
                {
                    var at = ty * PlotLayout.Size + tx;
                    if (tiles[at] == (byte)TileObject.None && !dug.Contains(at) && Blizzard.Drift(last.Window, x, y, tx, ty))
                    {
                        tiles[at] = (byte)TileObject.Snowdrift;
                    }
                }
            }
        }

        // What the sea threw up today on the beach, where nothing stands.
        foreach (var (tx, ty) in await beach.FindsAsync(x, y, biome, tiles, time.GetUtcNow(), cancellationToken))
        {
            tiles[ty * PlotLayout.Size + tx] = (byte)TileObject.Flotsam;
        }

        return tiles;
    }

    /// <summary>Whether nothing stands on a tile of the plot: no nature, field, planted tree, animal or placed object
    /// (a forester's building covers the tiles around its own too).</summary>
    public async Task<bool> FreeAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        if (!PlotLayout.Inside(tileX, tileY)
            || (await TilesAsync(player, x, y, cancellationToken))[tileY * PlotLayout.Size + tileX] is not ((byte)TileObject.None or (byte)TileObject.Bridge))
        {
            return false;
        }

        await using var command = await db.CommandAsync(
            $$"""
            SELECT EXISTS (SELECT 1 FROM world.plot_fields WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty)
                OR EXISTS (SELECT 1 FROM world.plot_trees WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty)
                OR EXISTS (SELECT 1 FROM world.plot_animals WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty)
                OR EXISTS (SELECT 1 FROM world.plot_objects o WHERE o.x = @x AND o.y = @y AND {{Footprints.CoversSql("o", "@tx", "@ty")}})
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("tx", tileX);
        command.Parameters.AddWithValue("ty", tileY);
        // Objects on more than one tile.
        command.Parameters.AddWithValue("sizedKinds", Footprints.SizedKinds);
        command.Parameters.AddWithValue("sizedSizes", Footprints.SizedSizes);
        return !(bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Cuts a tree (grown by itself or planted), breaks a rock or digs a dune with the tool in the
    /// player's slot, or pulls out a bush by hand. <paramref name="pace"/> gets how long the work takes and whether it is struck
    /// (a tree with an axe or saw, a rock with a pickaxe: <see cref="StrikeRules"/>), and refuses it if too quick.
    /// </summary>
    public Task<Harvest> HarvestAsync(
        Guid player, int x, int y, int tileX, int tileY, Action<TimeSpan, bool> pace, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var biome = await RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            if (!PlotLayout.Inside(tileX, tileY))
            {
                throw new GameException("nothing_here", "There is nothing to gather here.");
            }

            if (await PlantedAtAsync(x, y, tileX, tileY, cancellationToken) is { } planted)
            {
                return await CutPlantedAsync(player, x, y, tileX, tileY, planted, pace, cancellationToken);
            }

            var obj = (TileObject)PlotLayout.Generate(x, y, biome)[tileY * PlotLayout.Size + tileX];
            var current = (TileObject)(await TilesAsync(x, y, biome, cancellationToken))[tileY * PlotLayout.Size + tileX];
            if (current == TileObject.Flotsam)
            {
                pace(HarvestRules.Time, false);
                return await beach.GatherAsync(player, x, y, tileX, tileY, async items => await GiveAsync(player, TileObject.Flotsam, items, cancellationToken), cancellationToken);
            }

            if (current == TileObject.Snowdrift)
            {
                // Snow dug away, maybe off something it buried; it comes back only with the next blizzard's avalanches.
                pace(HarvestRules.TimeFor(current, await RequireToolAsync(player, Items.Shovel, cancellationToken), biome)
                     / await SpeedAsync(player, Industries.Mining, cancellationToken, (x, y, tileX, tileY)), false);
                await MarkHarvestedAsync(x, y, tileX, tileY, always: true, cancellationToken);
                return await GiveAsync(player, current, PlotLayout.Yield(current, Random.Shared.Next(100), biome), cancellationToken);
            }

            if (current == TileObject.Crevasse)
            {
                pace(HarvestRules.Time, false);
                await inventory.RemoveAsync(player, Items.Beams, PlotLayout.BridgeBeams, cancellationToken);
                await MarkHarvestedAsync(x, y, tileX, tileY, always: false, cancellationToken);
                return new Harvest(TileObject.Bridge, [], await progress.AddXpAsync(player, HarvestRules.XpPerHarvest, cancellationToken));
            }

            if (current == TileObject.Bridge)
            {
                throw new GameException("nothing_here", "A bridge lies over the crevasse already.");
            }

            if (!PlotLayout.Gatherable(obj))
            {
                throw new GameException("nothing_here", "There is nothing to gather here.");
            }

            var held = PlotLayout.ToolFor(obj) is { } tool ? await RequireToolAsync(player, tool, cancellationToken) : null;
            pace(HarvestRules.TimeFor(obj, held, biome) / await SpeedAsync(player, HarvestRules.Industry(obj), cancellationToken, (x, y, tileX, tileY)),
                Trees.IsTree(obj) || held is not null && Items.Pickaxes.ContainsKey(held));
            if (obj == TileObject.Water)
            {
                // The water is never fished out; a storm at sea or on a lake keeps everyone ashore but those on a breakwater.
                if (Coast.HasWater(biome))
                {
                    await beach.RequireCalmAsync(x, y, cancellationToken);
                }

                return await GiveAsync(player, obj, Coast.Catch(biome, Random.Shared.Next(100)), cancellationToken);
            }

            // A dune the sandstorm blew back is dug again, ice frozen again after a blizzard is cut again, and mushrooms come up again.
            await MarkHarvestedAsync(x, y, tileX, tileY, obj is TileObject.Dune or TileObject.IceBlock or TileObject.Mushrooms && current == obj, cancellationToken);
            if (Trees.IsTree(obj))
            {
                var saw = Items.Saws.GetValueOrDefault(held!);
                var cut = PlotLayout.Yield(obj, Random.Shared.Next(100), biome, Random.Shared.Next(1000), saw);
                return await GiveTreesAsync(player, x, y, obj, cut, await FellAroundAsync(x, y, biome, tileX, tileY, saw, cancellationToken), cancellationToken);
            }

            return await GiveAsync(player, obj, PlotLayout.Yield(obj, Random.Shared.Next(100), biome, Random.Shared.Next(1000)), cancellationToken);
        }, cancellationToken);

    /// <summary>Records that a tile not gathered before was gathered now; false when it was.</summary>
    private Task<bool> TryMarkHarvestedAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        PlotHarvests.MarkAsync(db, x, y, tileX, tileY, time.GetUtcNow(), again: false, cancellationToken);

    /// <summary>Records that a tile was gathered now; <paramref name="always"/> also when it was before. Fails when it was and may not be again.</summary>
    private async Task MarkHarvestedAsync(int x, int y, int tileX, int tileY, bool always, CancellationToken cancellationToken)
    {
        if (!await PlotHarvests.MarkAsync(db, x, y, tileX, tileY, time.GetUtcNow(), always, cancellationToken))
        {
            throw new GameException("nothing_here", "This has already been gathered.");
        }
    }

    /// <summary>Whether snow an avalanche left on the glacier buries the tile an object stands on: it cannot be used until dug out.</summary>
    public async Task<bool> BuriedAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        map.BiomeAt(x, y) == Biome.Ice && PlotLayout.Inside(tileX, tileY)
        && (await TilesAsync(x, y, Biome.Ice, cancellationToken))[tileY * PlotLayout.Size + tileX] == (byte)TileObject.Snowdrift;

    /// <summary>Fails with <c>buried</c> when snow buries the object.</summary>
    public async Task RequireDugOutAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        if (await BuriedAsync(x, y, tileX, tileY, cancellationToken))
        {
            throw new GameException("buried", "Snow buries it: dig it out with a shovel first.");
        }
    }

    /// <summary>The tree planted on a tile: when it is grown, its kind and whether a player planted it by hand (locked until the
    /// transaction ends), or null when none is.</summary>
    private async Task<(DateTimeOffset ReadyAt, TreeKind Kind, bool ByHand)?> PlantedAtAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT ready_at, species, forester_id IS NULL FROM world.plot_trees WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty FOR UPDATE",
            cancellationToken);
        select.Parameters.AddWithValue("x", x);
        select.Parameters.AddWithValue("y", y);
        select.Parameters.AddWithValue("tx", tileX);
        select.Parameters.AddWithValue("ty", tileY);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetFieldValue<DateTimeOffset>(0), Trees.Of(reader.GetString(1)), reader.GetBoolean(2))
            : null;
    }

    private async Task<Harvest> CutPlantedAsync(
        Guid player, int x, int y, int tileX, int tileY, (DateTimeOffset ReadyAt, TreeKind Kind, bool ByHand) tree, Action<TimeSpan, bool> pace,
        CancellationToken cancellationToken)
    {
        if (tree.ReadyAt > await plotClock.NowAsync(x, y, cancellationToken))
        {
            throw new GameException("not_grown", "The tree has not grown yet.");
        }

        // The bark beetle may be in a tree of a monoculture planted by hand.
        var tool = await RequireToolAsync(player, Items.Axe, cancellationToken);
        pace(HarvestRules.TimeFor(tree.Kind.Tile, tool) / await SpeedAsync(player, Industries.Forestry, cancellationToken, (x, y, tileX, tileY)), true);
        var saw = Items.Saws.GetValueOrDefault(tool);
        var cut = await CutDownAsync(x, y, tileX, tileY, tree, saw, cancellationToken);
        return await GiveTreesAsync(player, x, y, tree.Kind.Tile, cut, await FellAroundAsync(x, y, map.BiomeAt(x, y), tileX, tileY, saw, cancellationToken), cancellationToken);
    }

    /// <summary>Takes a grown planted tree off its tile and says what it gives cut with <paramref name="saw"/>.</summary>
    private async Task<IReadOnlyList<(string Item, int Quantity)>> CutDownAsync(
        int x, int y, int tileX, int tileY, (DateTimeOffset ReadyAt, TreeKind Kind, bool ByHand) tree, Saw? saw, CancellationToken cancellationToken)
    {
        // The bark beetle may be in a tree of a monoculture planted by hand.
        var infested = tree.ByHand && (await pests.StateAsync(x, y, cancellationToken)).Infested.Contains((tileX, tileY));
        await using (var delete = await db.CommandAsync(
            "DELETE FROM world.plot_trees WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty", cancellationToken))
        {
            delete.Parameters.AddWithValue("x", x);
            delete.Parameters.AddWithValue("y", y);
            delete.Parameters.AddWithValue("tx", tileX);
            delete.Parameters.AddWithValue("ty", tileY);
            // Cut by someone else in the meantime: one tree never gives wood twice.
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("nothing_here", "This has already been gathered.");
            }
        }

        return Trees.Yield(tree.Kind, Random.Shared.Next(100), Random.Shared.Next(1000), infested, saw);
    }

    /// <summary>The tiles next to a tile, the four at its sides before the four at its corners.</summary>
    private static readonly (int X, int Y)[] Around = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1)];

    /// <summary>Fells as many grown trees next to the one cut on (<paramref name="tileX"/>, <paramref name="tileY"/>) as
    /// <paramref name="saw"/> takes down at a cut, planted or wild: the tiles they stood on and what each gave.</summary>
    private async Task<IReadOnlyList<((int X, int Y) Tile, IReadOnlyList<(string Item, int Quantity)> Items)>> FellAroundAsync(
        int x, int y, Biome biome, int tileX, int tileY, Saw? saw, CancellationToken cancellationToken)
    {
        var felled = new List<((int, int), IReadOnlyList<(string, int)>)>();
        if (saw is not { Fells: > 0 })
        {
            return felled;
        }

        var tiles = await TilesAsync(x, y, biome, cancellationToken);
        foreach (var (dx, dy) in Around)
        {
            if (felled.Count == saw.Fells)
            {
                break;
            }

            var (tx, ty) = (tileX + dx, tileY + dy);
            if (!PlotLayout.Inside(tx, ty))
            {
                continue;
            }

            if (await PlantedAtAsync(x, y, tx, ty, cancellationToken) is { } planted)
            {
                if (planted.ReadyAt <= await plotClock.NowAsync(x, y, cancellationToken))
                {
                    felled.Add(((tx, ty), await CutDownAsync(x, y, tx, ty, planted, saw, cancellationToken)));
                }

                continue;
            }

            var obj = (TileObject)tiles[ty * PlotLayout.Size + tx];
            if (Trees.IsTree(obj) && await TryMarkHarvestedAsync(x, y, tx, ty, cancellationToken))
            {
                felled.Add(((tx, ty), PlotLayout.Yield(obj, Random.Shared.Next(100), biome, Random.Shared.Next(1000), saw)));
            }
        }

        return felled;
    }

    /// <summary>Gives what a cut tree and those felled with it gave, and counts them all as cut for the forest certificate.</summary>
    private async Task<Harvest> GiveTreesAsync(
        Guid player, int x, int y, TileObject obj, IReadOnlyList<(string Item, int Quantity)> cut,
        IReadOnlyList<((int X, int Y) Tile, IReadOnlyList<(string Item, int Quantity)> Items)> felled, CancellationToken cancellationToken)
    {
        await ledger.RecordAsync(player, 0, 1 + felled.Count, cancellationToken);
        // Where the forest is protected, cutting without the certificate is against the law (resolved here: the law asks about plots).
        foreach (var watch in services.GetServices<ILawWatch>())
        {
            await watch.TreesCutAsync(player, x, y, 1 + felled.Count, cancellationToken);
        }

        var items = cut.Concat(felled.SelectMany(f => f.Items)).GroupBy(i => i.Item).Select(g => (g.Key, g.Sum(i => i.Quantity))).ToList();
        var harvest = await GiveAsync(player, obj, items, cancellationToken, 1 + felled.Count);
        return harvest with { Felled = felled.Select(f => f.Tile).ToList() };
    }

    /// <summary>How many times faster the player's clothes and last meal make work in an industry (1 for none); for forestry and mining
    /// on a plot (<paramref name="at"/>, with the tile when the work has one), faster still with a fed work horse at hand.</summary>
    public async Task<double> SpeedAsync(Guid player, string? industry, CancellationToken cancellationToken, (int X, int Y, int? TileX, int? TileY)? at = null)
    {
        if (industry is null)
        {
            return 1;
        }

        var speed = Industries.Speed(await outfit.BonusAsync(player, industry, cancellationToken));
        if (industry is Industries.Forestry or Industries.Mining && at is { } plot
            && await Husbandry.HorseAtWorkAsync(db, plot.X, plot.Y, plot.TileX is { } tx && plot.TileY is { } ty ? (tx, ty) : null, await plotClock.NowAsync(plot.X, plot.Y, cancellationToken), cancellationToken))
        {
            speed *= Husbandry.HorseBonus;
        }

        return speed;
    }

    /// <summary>The tool in the player's tool bar that does the work of <paramref name="tool"/>: a saw does an axe's,
    /// and a bronze or steel pickaxe an iron one's, faster, so the fastest goes first.</summary>
    public async Task<string> RequireToolAsync(Guid player, string tool, CancellationToken cancellationToken)
    {
        var held = await equipment.ToolsAsync(player, cancellationToken);
        if (tool == Items.Axe && Items.BestSaw(held) is { } saw)
        {
            return saw;
        }

        if (tool == Items.Pickaxe && Items.Pickaxes.Where(p => held.Contains(p.Key)).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() is { } best)
        {
            return best;
        }

        return held.Contains(tool) ? tool : throw new GameException("wrong_tool", $"You need {Items.Names[tool]} in your hand.");
    }

    /// <summary>Gives what was gathered and the experience for it, for each of <paramref name="times"/> objects gathered at once.</summary>
    private async Task<Harvest> GiveAsync(
        Guid player, TileObject obj, IReadOnlyList<(string Item, int Quantity)> items, CancellationToken cancellationToken, int times = 1)
    {
        foreach (var (item, quantity) in items)
        {
            await inventory.ReceiveAsync(player, item, quantity, cancellationToken);
        }

        await stats.RecordAsync(player, items.Select(i => ProductionEntry.Made(i.Item, i.Quantity, time.GetUtcNow())).ToList(), cancellationToken);
        var xp = await progress.AddXpAsync(player, HarvestRules.XpPerHarvest * times, cancellationToken);
        return new Harvest(obj, items, xp);
    }

    /// <summary>Fails with <c>not_owner</c> unless the player owns the plot, or may do <paramref name="right"/>
    /// there as a member of the company the owner brought it into.</summary>
    public async Task<Biome> RequireOwnerAsync(
        Guid player, int x, int y, CancellationToken cancellationToken, PlotRight right = PlotRight.Build)
    {
        if (!(await RightsAsync(player, x, y, cancellationToken)).Contains(right))
        {
            throw new GameException("not_owner", "This is not your plot.");
        }

        // The owner's plot tax is paid whenever the plot is entered; while it is owed, nothing is built there.
        if (right == PlotRight.Build)
        {
            await taxes.RequirePaidAsync(x, y, cancellationToken);
        }
        else if (right == PlotRight.Visit)
        {
            await taxes.SettlePlotAsync(x, y, cancellationToken);
        }

        return map.BiomeAt(x, y);
    }

    /// <summary>Everything for the owner; for anyone else what their role in the plot's company allows, if anything,
    /// and on a public plot at least a visit.</summary>
    public async Task<IReadOnlySet<PlotRight>> RightsAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        if (!map.Contains(x, y))
        {
            throw new GameException("bad_plot", "There is no such plot.");
        }

        bool open;
        await using (var command = await db.CommandAsync("SELECT owner_id, public FROM world.plots WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return NoRights;
            }

            if (reader.GetGuid(0) == player)
            {
                return AllRights;
            }

            open = reader.GetBoolean(1);
        }

        var rights = new HashSet<PlotRight>();
        foreach (var company in companies)
        {
            rights.UnionWith(await company.RightsAsync(player, x, y, cancellationToken));
        }

        rights.Remove(PlotRight.Owner);
        // Members of the plot's company may always come; anyone else only when the owner made it public, and then only to look,
        // or to help with what the owner asked (IPlotPermits).
        if (rights.Count > 0 || open || permits.For(player, x, y) is not null)
        {
            rights.Add(PlotRight.Visit);
        }

        return rights;
    }

    /// <summary>Lets anyone come onto the owner's plot to look around, or only the owner and their company.</summary>
    public async Task SetPublicAsync(Guid player, int x, int y, bool open, CancellationToken cancellationToken)
    {
        await RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Owner);
        await using var command = await db.CommandAsync("UPDATE world.plots SET public = @open WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("open", open);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static readonly IReadOnlySet<PlotRight> AllRights = Enum.GetValues<PlotRight>().ToHashSet();

    private static readonly IReadOnlySet<PlotRight> NoRights = new HashSet<PlotRight>();
}
