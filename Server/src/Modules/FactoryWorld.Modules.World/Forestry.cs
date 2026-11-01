using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>
/// A sapling bought on the market is planted on a free tile of a plot and grows into a tree, which
/// is cut with an axe like the trees that grew there by themselves: wood and, now and then, a new sapling.
/// </summary>
public static class Forestry
{
    /// <summary>How long a sapling grows on a plot without a forestry bonus.</summary>
    public static readonly TimeSpan Grow = TimeSpan.FromMinutes(10);

    /// <summary>Chance (percent) that a cut tree drops a sapling.</summary>
    public const int SaplingChance = 30;

    /// <summary>Of pines cut, this many in a thousand give resin too.</summary>
    public const int ResinPerMille = 400;

    public const int XpPerPlanting = 1;

    /// <summary>
    /// The biome's forestry bonus in percent, as the map lists it: a forest grows trees 25% faster,
    /// a city 50% slower, and nothing grows on ice or in the mountains (null).
    /// </summary>
    public static int? BonusPercent(Biome biome) => biome switch
    {
        Biome.Forest => 25,
        Biome.City => -50,
        Biome.Ice or Biome.Mountains => null,
        _ => 0,
    };

    /// <summary>How long a sapling grows on a plot of a biome; null when trees cannot be planted there.</summary>
    public static TimeSpan? GrowTime(Biome biome) => BonusPercent(biome) is { } bonus ? Grow * (100 - bonus) / 100.0 : null;

    /// <summary>Where mushrooms come up in an old forest: the forest, the lakeshore.</summary>
    public static bool HasMushrooms(Biome biome) => biome is Biome.Forest or Biome.Lakeshore;

    /// <summary>Mushrooms picked come up again after this long.</summary>
    public static readonly TimeSpan MushroomRegrow = TimeSpan.FromHours(12);

    /// <summary>Of open tiles in an old forest (at least <see cref="OldForest"/> trees in the 5 × 5 around), this many in a hundred have mushrooms.</summary>
    public const int MushroomChance = 6;

    public const int OldForest = 4;

    /// <summary>Of mushrooms picked, this many in a hundred bring herbs with them.</summary>
    public const int HerbChance = 35;

    /// <summary>Puts mushrooms on the forest floor of a freshly generated plot.</summary>
    public static void AddMushrooms(byte[] tiles, uint seed, Biome biome)
    {
        if (!HasMushrooms(biome))
        {
            return;
        }

        const int size = PlotLayout.Size;
        for (var y = PlotLayout.ClearRows + 2; y < size - 2; y++)
        {
            for (var x = 2; x < size - 2; x++)
            {
                if (tiles[y * size + x] != (byte)TileObject.None || PlotLayout.Hash(seed + 31, x, y) % 100 >= MushroomChance)
                {
                    continue;
                }

                var trees = 0;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        if (Trees.IsTree(tiles[(y + dy) * size + x + dx]))
                        {
                            trees++;
                        }
                    }
                }

                if (trees >= OldForest)
                {
                    tiles[y * size + x] = (byte)TileObject.Mushrooms;
                }
            }
        }
    }

    /// <summary>What a bush pulled out gives: berries in the woods and on the tundra, herbs now and then on the grass.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> BushYield(Biome biome, int roll) => biome switch
    {
        Biome.Forest or Biome.Lakeshore or Biome.Tundra => [(Items.Berries, 2)],
        Biome.Grassland or Biome.Farmland when roll < 50 => [(Items.Herbs, 1)],
        _ => [],
    };

    /// <summary>Over the last <see cref="CertificateDays"/> days a player must plant at least as many trees as they cut, and
    /// at least <see cref="CertificateTrees"/>, to sell timber <see cref="CertificatePercent"/>% dearer.</summary>
    public const int CertificateDays = 7;

    public const int CertificateTrees = 20;

    public const int CertificatePercent = ForestCertificate.BonusPercent;

    public static bool Certified(int planted, int cut) => planted >= CertificateTrees && planted >= cut;
}

/// <summary>A kind of tree: the tile it grows on wild, its sapling, how much longer than a pine it takes to grow, whether bees
/// feed on its flowers, and the wood it gives.</summary>
public sealed record TreeKind(string Id, TileObject Tile, string Sapling, double Grow, bool Flowers, string Wood, int WoodQuantity);

/// <summary>
/// Five kinds of tree grow on the plots: the pine (a palm in an oasis), which gives resin now and then, the spruce,
/// which grows twice as fast on the tundra, the quick birch, the slow oak with its dear wood, and the linden, whose
/// flowers feed bees. Each drops saplings of its own kind.
/// </summary>
public static class Trees
{
    public const string Pine = "pine";
    public const string Spruce = "spruce";
    public const string Birch = "birch";
    public const string Oak = "oak";
    public const string Linden = "linden";

    public static readonly IReadOnlyList<TreeKind> Kinds =
    [
        new(Pine, TileObject.Tree, Items.Sapling, 1, false, Items.Wood, 1),
        new(Spruce, TileObject.SpruceTree, Items.SaplingSpruce, 1, false, Items.Wood, 1),
        new(Birch, TileObject.BirchTree, Items.SaplingBirch, 0.8, false, Items.Wood, 1),
        new(Oak, TileObject.OakTree, Items.SaplingOak, 3, false, Items.OakWood, 2),
        new(Linden, TileObject.LindenTree, Items.SaplingLinden, 1.5, true, Items.Wood, 1),
    ];

    public static readonly IReadOnlyDictionary<string, TreeKind> ById = Kinds.ToDictionary(k => k.Id);

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [Pine] = "Sosna",
        [Spruce] = "Świerk",
        [Birch] = "Brzoza",
        [Oak] = "Dąb",
        [Linden] = "Lipa",
    };

    /// <summary>What sets each kind apart, in a few words for the player.</summary>
    public static readonly IReadOnlyDictionary<string, string> Notes = new Dictionary<string, string>
    {
        [Pine] = "Ścięta ręcznie daje czasem żywicę",
        [Spruce] = "Na tundrze rośnie dwa razy szybciej",
        [Birch] = "Rośnie najszybciej",
        [Oak] = "Rośnie 3 razy dłużej, ale drewno dębowe jest droższe",
        [Linden] = "Kwitnie i przyspiesza pszczoły z uli obok",
    };

    public static readonly IReadOnlyDictionary<TileObject, TreeKind> ByTile = Kinds.ToDictionary(k => k.Tile);

    public static readonly IReadOnlyDictionary<string, TreeKind> BySapling = Kinds.ToDictionary(k => k.Sapling);

    public static bool IsTree(TileObject obj) => ByTile.ContainsKey(obj);

    public static bool IsTree(byte tile) => IsTree((TileObject)tile);

    /// <summary>The kind of a planted tree, a pine when the id is unknown.</summary>
    public static TreeKind Of(string? id) => ById.GetValueOrDefault(id ?? "", Kinds[0]);

    /// <summary>Of the wild trees of a biome, how many in a hundred are of each kind but the pine.</summary>
    private static (TileObject Tile, int Percent)[] Mix(Biome biome) => biome switch
    {
        Biome.Forest => [(TileObject.SpruceTree, 25), (TileObject.BirchTree, 15), (TileObject.OakTree, 10), (TileObject.LindenTree, 10)],
        Biome.Farmland or Biome.Grassland => [(TileObject.BirchTree, 30), (TileObject.OakTree, 15), (TileObject.LindenTree, 15)],
        Biome.Lakeshore => [(TileObject.BirchTree, 30), (TileObject.SpruceTree, 15), (TileObject.LindenTree, 15)],
        Biome.Tundra => [(TileObject.SpruceTree, 50), (TileObject.BirchTree, 20)],
        _ => [],
    };

    /// <summary>Turns the wild pines of a freshly generated plot into the biome's mix of trees, in groves.</summary>
    public static void Assign(byte[] tiles, uint seed, Biome biome)
    {
        var mix = Mix(biome);
        if (mix.Length == 0)
        {
            return;
        }

        for (var i = 0; i < tiles.Length; i++)
        {
            if (tiles[i] != (byte)TileObject.Tree)
            {
                continue;
            }

            // Trees of a kind stand together: the roll is shared by neighbours and only now and then differs.
            var (x, y) = (i % PlotLayout.Size, i / PlotLayout.Size);
            var grove = PlotLayout.Noise(seed + 101, x / 6.0, y / 6.0);
            var roll = (int)((grove * 0.8 + (PlotLayout.Hash(seed + 103, x, y) % 1000) / 1000.0 * 0.2) * 100);
            var at = 0;
            foreach (var (tile, percent) in mix)
            {
                at += percent;
                if (roll < at)
                {
                    tiles[i] = (byte)tile;
                    break;
                }
            }
        }
    }

    /// <summary>How long a sapling of <paramref name="kind"/> grows on a plot of <paramref name="biome"/>; null where nothing grows.</summary>
    public static TimeSpan? GrowTime(Biome biome, TreeKind kind) =>
        Forestry.GrowTime(biome) * (kind.Id == Spruce && biome == Biome.Tundra ? 0.5 : kind.Grow);

    /// <summary>What a tree of <paramref name="kind"/> gives when cut with <paramref name="saw"/> (null for an axe), for a roll of 0-99
    /// and a <paramref name="rare"/> one of 0-999; a tree the bark beetle ate gives a log and nothing more, unless the saw gets
    /// all the wood out of it all the same. A better saw gets more wood out of a tree, and some make saplings fall twice as often.</summary>
    public static IReadOnlyList<(string Item, int Quantity)> Yield(TreeKind kind, int roll, int rare = 999, bool infested = false, Saw? saw = null)
    {
        if (infested && saw is not { ThroughBeetle: true })
        {
            return [(Items.Wood, 1)];
        }

        var items = new List<(string, int)> { (kind.Wood, kind.WoodQuantity + (saw?.ExtraWood ?? 0)) };
        if (roll < Forestry.SaplingChance * (saw is { MoreSaplings: true } ? 2 : 1))
        {
            items.Add((kind.Sapling, 1));
        }

        if (kind.Id == Pine && rare < Forestry.ResinPerMille)
        {
            items.Add((Items.Resin, 1));
        }

        return items;
    }
}

/// <param name="ReadyAt">When the tree can be cut.</param>
public sealed record PlantedTree(int TileX, int TileY, DateTimeOffset ReadyAt, string Species = Trees.Pine);

/// <summary>Trees planted and cut by hand, day by day, for the forest certificate and the greenest players' ranking.</summary>
public sealed class ForestLedger(GameDb db, TimeProvider time) : IForestCertificate
{
    private static int Day(DateTimeOffset at) => (int)(at.ToUnixTimeSeconds() / 86_400);

    public async Task RecordAsync(Guid player, int planted, int cut, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.forest_ledger (player_id, day, planted, cut) VALUES (@player, @day, @planted, @cut)
            ON CONFLICT (player_id, day) DO UPDATE SET planted = forest_ledger.planted + @planted, cut = forest_ledger.cut + @cut
            """, cancellationToken);
        command.Parameters.AddWithValue("player", player);
        command.Parameters.AddWithValue("day", Day(time.GetUtcNow()));
        command.Parameters.AddWithValue("planted", planted);
        command.Parameters.AddWithValue("cut", cut);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Trees the player planted and cut by hand over the last <see cref="Forestry.CertificateDays"/> days, today with them.</summary>
    public async Task<(int Planted, int Cut)> WeekAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT COALESCE(sum(planted), 0)::int, COALESCE(sum(cut), 0)::int FROM world.forest_ledger WHERE player_id = @player AND day > @from",
            cancellationToken);
        command.Parameters.AddWithValue("player", player);
        command.Parameters.AddWithValue("from", Day(time.GetUtcNow()) - Forestry.CertificateDays);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task<bool> CertifiedAsync(Guid player, CancellationToken cancellationToken)
    {
        var (planted, cut) = await WeekAsync(player, cancellationToken);
        return Forestry.Certified(planted, cut);
    }

    public async Task<object> JsonAsync(Guid player, CancellationToken cancellationToken)
    {
        var (planted, cut) = await WeekAsync(player, cancellationToken);
        return new
        {
            certified = Forestry.Certified(planted, cut),
            planted,
            cut,
            days = Forestry.CertificateDays,
            needed = Forestry.CertificateTrees,
            bonusPercent = Forestry.CertificatePercent,
        };
    }
}

/// <summary>The players who planted the most trees by hand.</summary>
public sealed class GreenestRanking(GameDb db) : IPlayerRanking
{
    public string Id => "greenest";

    public string Name => "Najbardziej zieloni";

    public int Order => 60;

    public string Describe(long value) => $"{value} drzew posadzonych";

    public async Task<IReadOnlyList<RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT player_id, sum(planted)::bigint AS total FROM world.forest_ledger GROUP BY player_id
            HAVING sum(planted) > 0 ORDER BY total DESC, player_id LIMIT @count
            """, cancellationToken);
        command.Parameters.AddWithValue("count", count);
        var top = new List<RankedPlayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            top.Add(new(reader.GetGuid(0), reader.GetInt64(1)));
        }

        return top;
    }

    public async Task<long?> ValueAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT sum(planted)::bigint FROM world.forest_ledger WHERE player_id = @player HAVING sum(planted) > 0", cancellationToken);
        command.Parameters.AddWithValue("player", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<long> CountAboveAsync(long value, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT count(*) FROM (SELECT player_id FROM world.forest_ledger GROUP BY player_id HAVING sum(planted) > @value) t", cancellationToken);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

public sealed class ForestService(
    GameDb db, PlotService plots, IInventory inventory, ICharacterProgress progress, IProductionStats stats, TimeProvider time, OilService oil,
    ForestLedger ledger, PestService pests)
{
    public Task<IReadOnlyList<PlantedTree>> TreesAsync(int x, int y, CancellationToken cancellationToken) => ReadAsync(db, x, y, cancellationToken);

    /// <summary>Every tree planted on the plot, by hand or by a forester.</summary>
    public static async Task<IReadOnlyList<PlantedTree>> ReadAsync(GameDb db, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, ready_at, species FROM world.plot_trees WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var trees = new List<PlantedTree>();
        while (await reader.ReadAsync(cancellationToken))
        {
            trees.Add(new PlantedTree(reader.GetInt32(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3)));
        }

        return trees;
    }

    /// <summary>Plants a sapling from the inventory on a free tile of the player's plot: a pine's unless another kind is named.</summary>
    public Task<(PlantedTree Tree, XpGain Xp)> PlantAsync(
        Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken, string? sapling = null) =>
        db.InTransactionAsync(async () =>
        {
            var kind = Trees.BySapling.GetValueOrDefault(sapling ?? Items.Sapling) ?? throw new GameException("bad_sapling", "That is not a sapling.");
            var biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var grow = Trees.GrowTime(biome, kind) ?? throw new GameException("no_forestry", "No trees grow here.");
            if (!await plots.FreeAsync(player, x, y, tileX, tileY, cancellationToken))
            {
                throw new GameException("blocked", "Something stands here.");
            }

            await oil.RequireCleanAsync(x, y, tileX, tileY, cancellationToken);
            await inventory.RemoveAsync(player, kind.Sapling, 1, cancellationToken);
            await stats.RecordAsync(player, [ProductionEntry.Used(kind.Sapling, 1, time.GetUtcNow())], cancellationToken);
            var tree = new PlantedTree(tileX, tileY, time.GetUtcNow() + grow, kind.Id);
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.plot_trees (x, y, tile_x, tile_y, ready_at, species) VALUES (@x, @y, @tx, @ty, @ready, @species)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("x", x);
                insert.Parameters.AddWithValue("y", y);
                insert.Parameters.AddWithValue("tx", tileX);
                insert.Parameters.AddWithValue("ty", tileY);
                insert.Parameters.AddWithValue("ready", tree.ReadyAt);
                insert.Parameters.AddWithValue("species", kind.Id);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("blocked", "A tree is already growing here.");
                }
            }

            await ledger.RecordAsync(player, 1, 0, cancellationToken);
            var xp = await progress.AddXpAsync(player, Forestry.XpPerPlanting, cancellationToken);
            return (tree, xp);
        }, cancellationToken);

    /// <summary>Sprays the trees planted on the player's plot against the bark beetle: one can keeps it off for <see cref="Pests.SprayLasts"/>.</summary>
    public Task<PestState> SprayAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await inventory.RemoveAsync(player, Items.Insecticide, 1, cancellationToken);
            await stats.RecordAsync(player, [ProductionEntry.Used(Items.Insecticide, 1, time.GetUtcNow())], cancellationToken);
            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO world.plot_sprays (x, y, until) VALUES (@x, @y, @until)
                ON CONFLICT (x, y) DO UPDATE SET until = @until
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("x", x);
                upsert.Parameters.AddWithValue("y", y);
                upsert.Parameters.AddWithValue("until", time.GetUtcNow() + Pests.SprayLasts);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await pests.StateAsync(x, y, cancellationToken);
        }, cancellationToken);
}

public sealed record PlantTreeRequest(int X, int Y, int TileX, int TileY, string? Sapling = null);

public sealed class PlantTreeHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.plantTree";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlantTreeRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (tree, xp) = await scope.ServiceProvider.GetRequiredService<ForestService>()
            .PlantAsync(client.RequireAccount(), request.X, request.Y, request.TileX, request.TileY, cancellationToken, request.Sapling);
        return new Reply("plot.tree", new
        {
            tree = TreeJson(tree),
            xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp },
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    public static object TreeJson(PlantedTree tree) => new
    {
        tileX = tree.TileX,
        tileY = tree.TileY,
        readyAt = tree.ReadyAt.ToUnixTimeMilliseconds(),
        species = tree.Species,
    };

    /// <summary>The kinds of tree, for the client: their saplings and how long each grows on a plot of <paramref name="biome"/>.</summary>
    public static IEnumerable<object> KindsJson(Biome biome) => Trees.Kinds.Select(k => new
    {
        id = k.Id,
        tile = (int)k.Tile,
        sapling = k.Sapling,
        saplingName = Items.Names[k.Sapling],
        growSeconds = Trees.GrowTime(biome, k)?.TotalSeconds,
        wood = k.Wood,
        woodQuantity = k.WoodQuantity,
        flowers = k.Flowers,
    });
}

public sealed record SprayRequest(int X, int Y);

/// <summary>Sprays the trees planted on a plot against the bark beetle.</summary>
public sealed class SprayHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "forest.spray";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<SprayRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var state = await scope.ServiceProvider.GetRequiredService<ForestService>().SprayAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        return new Reply("forest.pests", PestService.Json(state));
    }
}
