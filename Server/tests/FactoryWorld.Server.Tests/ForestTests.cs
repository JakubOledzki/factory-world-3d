using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class ForestTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void Wild_forests_grow_five_kinds_of_tree_in_groves_with_mushrooms_beneath()
    {
        var forest = PlotLayout.Generate(10, 10, Biome.Forest);
        foreach (var kind in Trees.Kinds)
        {
            Assert.True(forest.Count(t => t == (byte)kind.Tile) > 500, kind.Id);
        }

        Assert.Contains((byte)TileObject.Mushrooms, forest);
        Assert.True(PlotLayout.Gatherable(TileObject.Mushrooms));
        Assert.Null(PlotLayout.ToolFor(TileObject.Mushrooms));
        Assert.Equal(Items.Axe, PlotLayout.ToolFor(TileObject.OakTree));

        // Spruce and birch on the tundra; pines (palms) only on the coast and in an oasis.
        var tundra = PlotLayout.Generate(10, 10, Biome.Tundra);
        Assert.Contains((byte)TileObject.SpruceTree, tundra);
        Assert.DoesNotContain((byte)TileObject.OakTree, tundra);
        Assert.DoesNotContain((byte)TileObject.Mushrooms, tundra);
        Assert.All(PlotLayout.Generate(10, 10, Biome.Oasis).Where(Trees.IsTree), t => Assert.Equal((byte)TileObject.Tree, t));
    }

    [Fact]
    public void Every_kind_of_tree_gives_its_own_wood_and_saplings_and_the_pine_resin()
    {
        Assert.Equal([(Items.OakWood, 2), (Items.SaplingOak, 1)], PlotLayout.Yield(TileObject.OakTree, 0));
        Assert.Equal([(Items.Wood, 1), (Items.SaplingLinden, 1)], PlotLayout.Yield(TileObject.LindenTree, 10));
        Assert.Equal([(Items.Wood, 1)], PlotLayout.Yield(TileObject.BirchTree, 30));
        Assert.Equal([(Items.Wood, 1), (Items.Resin, 1)], PlotLayout.Yield(TileObject.Tree, 50, Biome.Forest, 100));
        Assert.Equal([(Items.Wood, 1)], PlotLayout.Yield(TileObject.SpruceTree, 50, Biome.Forest, 100));
        Assert.Equal([(Items.Wood, 1)], Trees.Yield(Trees.ById[Trees.Oak], 0, 0, infested: true));
        Assert.Equal(HarvestRules.ChopTime, HarvestRules.TimeFor(TileObject.OakTree));

        // The forest floor: mushrooms (and herbs now and then), berries on bushes in the woods, herbs on the grass.
        Assert.Equal([(Items.Mushrooms, 2), (Items.Herbs, 1)], PlotLayout.Yield(TileObject.Mushrooms, 0));
        Assert.Equal([(Items.Mushrooms, 2)], PlotLayout.Yield(TileObject.Mushrooms, 50));
        Assert.Equal([(Items.Berries, 2)], PlotLayout.Yield(TileObject.Bush, 80, Biome.Forest));
        Assert.Equal([(Items.Herbs, 1)], PlotLayout.Yield(TileObject.Bush, 10, Biome.Grassland));
        Assert.Empty(PlotLayout.Yield(TileObject.Bush, 10, Biome.Desert));

        // An oak takes three times as long as a pine, a birch less; a spruce grows twice as fast on the tundra.
        Assert.Equal(Forestry.GrowTime(Biome.Grassland) * 3, Trees.GrowTime(Biome.Grassland, Trees.ById[Trees.Oak]));
        Assert.True(Trees.GrowTime(Biome.Grassland, Trees.ById[Trees.Birch]) < Forestry.GrowTime(Biome.Grassland));
        Assert.Equal(Forestry.GrowTime(Biome.Tundra) / 2, Trees.GrowTime(Biome.Tundra, Trees.ById[Trees.Spruce]));
        Assert.Null(Trees.GrowTime(Biome.Ice, Trees.ById[Trees.Linden]));
    }

    [Fact]
    public void Wood_becomes_paper_furniture_charcoal_and_tar_and_charcoal_burns_like_coal()
    {
        Assert.NotNull(Processing.ByKind[Items.PaperMill].Recipe(Items.Paper));
        Assert.True(Processing.ByKind[Items.PaperMill].Powered);
        Assert.NotNull(Processing.ByKind[Items.Carpentry].Recipe(Items.OakDresser));
        Assert.Contains((Items.OakWood, 6), Processing.ByKind[Items.Carpentry].Recipe(Items.OakDresser)!.Ingredients);
        Assert.NotNull(Processing.ByKind[Items.CharcoalKiln].Recipe(Items.Charcoal));
        Assert.True(Processing.ByKind[Items.TarKiln].Fired);
        Assert.NotNull(Processing.ByKind[Items.Kitchen].Recipe(Items.MushroomSoup));
        Assert.NotNull(Processing.ByKind[Items.Bakery].Recipe(Items.Gingerbread));
        Assert.NotNull(Processing.ByKind[Items.ChemicalPlant].Recipe(Items.Insecticide));

        Assert.Equal(Processing.Fuels[Items.Coal], Processing.Fuels[Items.Charcoal]);
        Assert.True(Items.IsCoal(Items.Charcoal));
        Assert.True(PowerRules.Burns(Items.Generator, Items.Charcoal));
        Assert.True(PowerRules.Burns(Items.Heater, Items.Charcoal));
        Assert.Equal(NpcPrices.Buys[Items.Coal], NpcPrices.Buys[Items.Charcoal]);
        Assert.Contains(CityRules.Goods, g => g.Item == Items.OakDresser);
    }

    [Fact]
    public void The_bark_beetle_comes_to_a_monoculture_of_thirty_trees_and_eats_a_fifth_of_it_a_day()
    {
        Assert.Null(Pests.Monoculture(Enumerable.Repeat(Trees.Pine, 29).ToList()));
        Assert.Equal(Trees.Pine, Pests.Monoculture(Enumerable.Repeat(Trees.Pine, 30).ToList()));
        Assert.Equal(Trees.Pine, Pests.Monoculture([.. Enumerable.Repeat(Trees.Pine, 28), .. Enumerable.Repeat(Trees.Oak, 12)]));
        Assert.Null(Pests.Monoculture([.. Enumerable.Repeat(Trees.Pine, 20), .. Enumerable.Repeat(Trees.Birch, 10)]));

        var day = DateTimeOffset.UnixEpoch.AddDays(20_000);
        var tiles = Enumerable.Range(0, 1000).Select(i => (i % 100, i / 100)).ToList();
        var infested = tiles.Count(t => Pests.Infested(5, 5, t.Item1, t.Item2, day));
        Assert.InRange(infested, 150, 250);
        // Another day, other trees.
        Assert.NotEqual(tiles.Where(t => Pests.Infested(5, 5, t.Item1, t.Item2, day)),
            tiles.Where(t => Pests.Infested(5, 5, t.Item1, t.Item2, day.AddDays(1))));
    }

    [Fact]
    public void A_fire_spreads_from_tree_to_tree_until_a_clearing_its_reach_or_a_fire_station()
    {
        // Two groves 3 tiles apart: the fire stays in the first.
        var trees = new HashSet<(int X, int Y)>();
        for (var x = 0; x < 30; x++)
        {
            if (x is not (10 or 11 or 12))
            {
                trees.Add((x, 0));
            }
        }

        Assert.Equal(10, Wildfires.Spread((0, 0), trees, Wildfires.Radius, _ => false).Count);
        // A watchtower sees it early.
        Assert.Equal(5, Wildfires.Spread((0, 0), trees, Wildfires.WatchtowerRadius, _ => false).Count);
        // Within a fire station's reach nothing burns.
        Assert.Equal(5, Wildfires.Spread((15, 0), trees, Wildfires.Radius, t => t.X >= 18).Count);
        Assert.Empty(Wildfires.Spread((20, 0), trees, Wildfires.Radius, t => t.X >= 18));

        // Fires come on some days only, at a time of each plot's own.
        var days = Enumerable.Range(20_000, 1000).Select(d => Wildfires.OnDay(3, 4, d)).ToList();
        Assert.InRange(days.Count(d => d is not null), 100, 200);
        Assert.Equal(days, Enumerable.Range(20_000, 1000).Select(d => Wildfires.OnDay(3, 4, d)).ToList());
        Assert.True(Wildfires.Burns(Biome.Forest));
        Assert.False(Wildfires.Burns(Biome.Desert));
    }

    [Fact]
    public void Bees_make_honey_and_wax_faster_among_lindens_and_stop_when_the_hive_is_full()
    {
        var start = DateTimeOffset.UnixEpoch;
        var hive = new Hive(Guid.NewGuid(), 0, 0, Guid.Empty, 0, 0, 0, 0, start);
        var hour = hive.Advance(start.AddHours(1), Bees.Speed(0));
        Assert.Equal(6, hour.Honey);
        Assert.Equal(2, hour.Wax);
        // Five lindens make them half as fast again, ten or more twice as fast.
        Assert.Equal(9, hive.Advance(start.AddHours(1), Bees.Speed(5)).Honey);
        Assert.Equal(2, Bees.Speed(30));
        var full = hive.Advance(start.AddDays(1), Bees.Speed(0));
        Assert.Equal(Bees.MaxHoney, full.Honey);
        Assert.Equal(Bees.MaxHoney / Bees.HoneyPerWax, full.Wax);
        Assert.Equal(full.Honey, full.Advance(start.AddDays(2), Bees.Speed(0)).Honey);
        Assert.False(Bees.Fly(Biome.Ice));
    }

    // --- On a plot -------------------------------------------------------------------------------

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 9500;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private async Task<(int X, int Y)> BuyAsync(GameClient client, Biome biome)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var plot = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = plot.X, y = plot.Y })).Type());
        return plot;
    }

    private static async Task<(JsonElement Plot, byte[] Tiles)> EnterAsync(GameClient client, (int X, int Y) plot)
    {
        var reply = await client.SendAsync("plot.enter", new { x = plot.X, y = plot.Y });
        Assert.Equal("plot", reply.Type());
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(reply.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return (reply.Payload(), tiles.ToArray());
    }

    /// <summary>Free tiles of the plot, away from the station.</summary>
    private static IEnumerable<(int X, int Y)> Free(byte[] tiles) =>
        Enumerable.Range(0, tiles.Length).Where(i => tiles[i] == (byte)TileObject.None && i / PlotLayout.Size > 20)
            .Select(i => (i % PlotLayout.Size, i / PlotLayout.Size)).Where(t => t != PlotLayout.Spawn);

    [Fact]
    public async Task Saplings_of_each_kind_grow_their_own_tree_and_planting_by_hand_earns_the_certificate()
    {
        using var client = await PlayerAsync();
        // A forest comes with an axe in hand.
        var plot = await BuyAsync(client, Biome.Forest);
        var (entered, tiles) = await EnterAsync(client, plot);
        Assert.Equal(Trees.Kinds.Count, entered.GetProperty("treeKinds").GetArrayLength());
        Assert.False(entered.GetProperty("certificate").GetProperty("certified").GetBoolean());

        var free = Free(tiles).Take(Forestry.CertificateTrees + 1).ToList();
        await GiveAsync(client, Items.SaplingOak, 1);
        var oak = await client.SendAsync("plot.plantTree", new { x = plot.X, y = plot.Y, tileX = free[0].X, tileY = free[0].Y, sapling = Items.SaplingOak });
        Assert.Equal("plot.tree", oak.Type());
        var tree = oak.Payload().GetProperty("tree");
        Assert.Equal(Trees.Oak, tree.GetProperty("species").GetString());
        Assert.Equal("bad_sapling", (await client.SendAsync("plot.plantTree", new { x = plot.X, y = plot.Y, tileX = free[1].X, tileY = free[1].Y, sapling = Items.Wood })).ErrorCode());

        // The oak takes 22.5 minutes in a forest, three times as long as a pine, and gives oak wood.
        factory.Time.Advance(TimeSpan.FromMinutes(22));
        Assert.Equal("not_grown", (await client.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = free[0].X, tileY = free[0].Y })).ErrorCode());
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var cut = await client.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = free[0].X, tileY = free[0].Y });
        Assert.Equal("plot.harvested", cut.Type());
        Assert.Contains(cut.Payload().GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.OakWood);

        // Twenty trees planted against one cut: the timber sells 15% dearer.
        await GiveAsync(client, Items.Wood, 20);
        var before = (await client.SendAsync("market.sell", new { item = Items.Wood, quantity = 10 })).Payload().GetProperty("balanceCents").GetInt64();
        await GiveAsync(client, Items.SaplingBirch, Forestry.CertificateTrees);
        foreach (var (tx, ty) in free.Skip(1))
        {
            Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", new { x = plot.X, y = plot.Y, tileX = tx, tileY = ty, sapling = Items.SaplingBirch })).Type());
        }

        var certificate = (await EnterAsync(client, plot)).Plot.GetProperty("certificate");
        Assert.True(certificate.GetProperty("certified").GetBoolean());
        Assert.Equal(Forestry.CertificateTrees + 1, certificate.GetProperty("planted").GetInt32());
        Assert.Equal(1, certificate.GetProperty("cut").GetInt32());
        var price = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == Items.Wood).GetProperty("sellCents").GetInt64();
        var after = (await client.SendAsync("market.sell", new { item = Items.Wood, quantity = 10 })).Payload().GetProperty("balanceCents").GetInt64();
        Assert.Equal(price * 115 / 100 * 10, after - before);

        var ranking = (await client.SendAsync("rankings.get", new { ranking = "greenest" })).Payload();
        Assert.Equal(Forestry.CertificateTrees + 1, ranking.GetProperty("me").GetProperty("value").GetInt64());
    }

    [Fact]
    public async Task Mushrooms_picked_in_an_old_forest_come_up_again_after_twelve_hours()
    {
        using var client = await PlayerAsync();
        var plot = await BuyAsync(client, Biome.Forest);
        var (_, tiles) = await EnterAsync(client, plot);
        var at = Array.IndexOf(tiles, (byte)TileObject.Mushrooms);
        Assert.True(at >= 0);
        var (tx, ty) = (at % PlotLayout.Size, at / PlotLayout.Size);

        var picked = await client.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = tx, tileY = ty });
        Assert.Equal("plot.harvested", picked.Type());
        Assert.Contains(picked.Payload().GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.Mushrooms);
        Assert.Equal((byte)TileObject.None, (await EnterAsync(client, plot)).Tiles[at]);

        factory.Time.Advance(Forestry.MushroomRegrow + TimeSpan.FromMinutes(1));
        Assert.Equal((byte)TileObject.Mushrooms, (await EnterAsync(client, plot)).Tiles[at]);
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = tx, tileY = ty })).Type());
    }

    [Fact]
    public async Task A_monoculture_planted_by_hand_draws_the_bark_beetle_until_it_is_sprayed()
    {
        using var client = await PlayerAsync();
        var plot = await BuyAsync(client, Biome.Forest);
        var (entered, tiles) = await EnterAsync(client, plot);
        Assert.Null(entered.GetProperty("pests").GetProperty("monoculture").GetString());

        await GiveAsync(client, Items.Sapling, Pests.MinTrees);
        var planted = Free(tiles).Take(Pests.MinTrees).ToList();
        foreach (var (tx, ty) in planted)
        {
            Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", new { x = plot.X, y = plot.Y, tileX = tx, tileY = ty })).Type());
        }

        // Grown, the beetle is in some of them: one of those gives only a log.
        factory.Time.Advance(TimeSpan.FromMinutes(8));
        var pests = (await EnterAsync(client, plot)).Plot.GetProperty("pests");
        Assert.Equal(Trees.Pine, pests.GetProperty("monoculture").GetString());
        Assert.Equal(Pests.MinTrees, pests.GetProperty("trees").GetInt32());
        var infested = pests.GetProperty("infested").EnumerateArray().Select(t => (t.GetProperty("tileX").GetInt32(), t.GetProperty("tileY").GetInt32())).ToList();
        if (infested.Count > 0)
        {
            var (ix, iy) = infested[0];
            var cut = (await client.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = ix, tileY = iy })).Payload();
            Assert.Equal([Items.Wood], cut.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        }

        Assert.Equal("not_enough_items", (await client.SendAsync("forest.spray", new { x = plot.X, y = plot.Y })).ErrorCode());
        await GiveAsync(client, Items.Insecticide, 1);
        var sprayed = await client.SendAsync("forest.spray", new { x = plot.X, y = plot.Y });
        Assert.Equal("forest.pests", sprayed.Type());
        Assert.True(sprayed.Payload().GetProperty("sprayedUntil").GetInt64() > 0);
        Assert.Equal(0, sprayed.Payload().GetProperty("infested").GetArrayLength());
    }

    [Fact]
    public async Task A_beehive_fills_with_honey_and_wax_by_itself_and_gives_them_up_when_picked_up()
    {
        using var client = await PlayerAsync();
        var plot = await BuyAsync(client, Biome.Grassland);
        var (_, tiles) = await EnterAsync(client, plot);
        var (tx, ty) = Free(tiles).First();
        await GiveAsync(client, Items.Beehive, 1);
        var placed = await client.SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind = Items.Beehive, tileX = tx, tileY = ty, rotation = 0 });
        Assert.Equal("plot.placed", placed.Type());
        var id = placed.Payload().GetProperty("object").GetProperty("id").GetString();

        var hive = (await client.SendAsync("hive.get", new { id })).Payload().GetProperty("hive");
        Assert.Equal(0, hive.GetProperty("honey").GetInt32());
        factory.Time.Advance(TimeSpan.FromHours(1));
        hive = (await client.SendAsync("hive.get", new { id })).Payload().GetProperty("hive");
        var honey = hive.GetProperty("honey").GetInt32();
        Assert.True(honey >= 6, $"{honey} jars");
        Assert.True(hive.GetProperty("wax").GetInt32() >= 2);

        var taken = (await client.SendAsync("hive.take", new { id })).Payload();
        Assert.Equal(0, taken.GetProperty("hive").GetProperty("honey").GetInt32());
        Assert.Equal(honey, (await client.InventoryAsync())[Items.Honey]);

        factory.Time.Advance(TimeSpan.FromMinutes(30));
        await client.SendAsync("hive.get", new { id });
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x = plot.X, y = plot.Y, id })).Type());
        Assert.True((await client.InventoryAsync())[Items.Honey] > honey);
        Assert.Equal("no_object", (await client.SendAsync("hive.get", new { id })).ErrorCode());
    }

    [Fact]
    public async Task A_forest_fire_burns_the_trees_it_reaches_on_a_dry_day_after_the_first_visit()
    {
        using var client = await PlayerAsync();
        var plot = await BuyAsync(client, Biome.Forest);
        var (entered, tiles) = await EnterAsync(client, plot);
        Assert.True(entered.GetProperty("fires").GetProperty("burns").GetBoolean());
        var now = factory.Time.GetUtcNow();
        var fire = Enumerable.Range(0, 60).Select(d => Wildfires.OnDay(plot.X, plot.Y, now.ToUnixTimeSeconds() / 86_400 + d))
            .OfType<DateTimeOffset>().First(at => at > now);

        factory.Time.Advance(fire - now + TimeSpan.FromMinutes(1));
        var (after, burnt) = await EnterAsync(client, plot);
        var report = after.GetProperty("fire");
        Assert.Equal(fire.ToUnixTimeMilliseconds(), report.GetProperty("at").GetInt64());
        var count = report.GetProperty("burnt").GetInt32();
        Assert.True(count >= 1);
        Assert.Equal(tiles.Count(Trees.IsTree) - count, burnt.Count(Trees.IsTree));
        Assert.False(Trees.IsTree(burnt[report.GetProperty("tileY").GetInt32() * PlotLayout.Size + report.GetProperty("tileX").GetInt32()]));

        // Seen once, it is not burnt again.
        var (_, again) = await EnterAsync(client, plot);
        Assert.Equal(burnt, again);
    }
}
