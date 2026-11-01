using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class ForestryIndustryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static ForesterLevel Hut => Foresters.Level(Items.ForesterHut)!;

    [Fact]
    public void A_forester_plants_around_its_clearing_but_not_on_the_path_from_its_door()
    {
        // 11 × 11 tiles, less the building with its clearing (5 × 5) and the path south of it (3 wide, 3 long).
        var tiles = Enumerable.Range(-5, 11).SelectMany(dy => Enumerable.Range(-5, 11).Select(dx => (dx, dy)))
            .Where(t => Foresters.Plants(Hut, 0, t.dx, t.dy)).ToList();
        Assert.Equal(121 - 25 - 9, tiles.Count);
        Assert.DoesNotContain((0, 4), tiles);
        Assert.DoesNotContain((1, 5), tiles);
        Assert.Contains((2, 5), tiles);
        Assert.Contains((0, -4), tiles);
        Assert.DoesNotContain((2, 2), tiles);

        // Facing east, the path runs east.
        Assert.False(Foresters.Plants(Hut, 1, 4, 0));
        Assert.True(Foresters.Plants(Hut, 1, 0, 4));

        // Bigger levels keep bigger forests.
        Assert.True(Foresters.Plants(Foresters.Level(Items.ForestryStation)!, 0, -7, -7));
        Assert.False(Foresters.Plants(Hut, 0, -6, -6));
    }

    [Fact]
    public void Two_foresters_share_where_their_forests_meet_and_keep_off_each_others_clearings()
    {
        var a = new PlotObject(Guid.NewGuid(), Items.ForesterHut, 50, 50, 0, Guid.Empty);
        var b = new PlotObject(Guid.NewGuid(), Items.ForesterHut, 58, 50, 0, Guid.Empty);
        var areas = Foresters.Areas([b, a]);
        Assert.Empty(areas[a.Id].Intersect(areas[b.Id]));
        // (55, 48) is in both squares: the first from the west gets it.
        Assert.Contains((55, 48), areas[a.Id]);
        // (56, 50) is in b's clearing, so a leaves it free though it is in a's square.
        Assert.DoesNotContain((56, 50), areas[a.Id]);
        Assert.DoesNotContain((56, 50), areas[b.Id]);
    }

    [Fact]
    public void A_forester_cuts_grown_trees_one_after_another_and_plants_again_until_its_store_is_full()
    {
        var grow = TimeSpan.FromMinutes(10);
        var patch = new ForestPatch(Hut, grow, 0, Start);
        for (var i = 0; i < 20; i++)
        {
            patch.Open.Add((i, 0));
        }

        patch.Wild[(0, 1)] = Trees.Kinds[0];
        patch.Advance(Start);
        Assert.Equal(20, patch.Trees.Count);
        Assert.All(patch.Trees.Values, ready => Assert.Equal(Start + grow, ready));

        // The wild tree is cut first, 6 s after it starts.
        patch.Advance(Start + TimeSpan.FromSeconds(5));
        Assert.Equal(0, patch.Wood);
        patch.Advance(Start + TimeSpan.FromSeconds(6));
        Assert.Equal(1, patch.Wood);
        Assert.Contains((0, 1), patch.Cleared);
        Assert.Equal(Start + TimeSpan.FromSeconds(6) + grow, patch.Trees[(0, 1)]);

        // Planted trees grow in 10 minutes, then go one every 6 s.
        patch.Advance(Start + grow + TimeSpan.FromSeconds(60));
        Assert.Equal(11, patch.Wood);
        Assert.Equal(Start + grow + TimeSpan.FromSeconds(60), patch.BusyUntil);
        Assert.Equal(11, patch.Made.Count);

        // Full, it stands until wood is taken out, and then goes on from that moment.
        var full = new ForestPatch(Hut, grow, Hut.Capacity - 1, Start);
        foreach (var tile in new[] { (0, 0), (1, 0), (2, 0) })
        {
            full.Wild[tile] = Trees.Kinds[0];
        }

        full.Advance(Start + TimeSpan.FromHours(1));
        Assert.Equal(Hut.Capacity, full.Wood);
        Assert.Equal(2, full.Wild.Count);
        Assert.Equal(Hut.Capacity, full.Take(int.MaxValue, Start + TimeSpan.FromHours(2)));
        full.Advance(Start + TimeSpan.FromHours(2) + TimeSpan.FromSeconds(11));
        Assert.Equal(1, full.Wood);
        full.Advance(Start + TimeSpan.FromHours(2) + TimeSpan.FromSeconds(12));
        Assert.Equal(2, full.Wood);
    }

    [Fact]
    public void Better_foresters_have_bigger_stores_grow_faster_and_cut_quicker()
    {
        var levels = Foresters.Levels;
        Assert.Equal([1, 2, 3], levels.Select(l => l.Level));
        Assert.Equal(100, levels[0].Capacity);
        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(levels[i].Capacity > levels[i - 1].Capacity);
            Assert.True(levels[i].Radius > levels[i - 1].Radius);
            Assert.True(levels[i].CutTime < levels[i - 1].CutTime);
            Assert.True(Foresters.GrowTime(levels[i], Biome.Forest) < Foresters.GrowTime(levels[i - 1], Biome.Forest));
        }

        Assert.Null(Foresters.GrowTime(levels[0], Biome.Ice));
        Assert.Null(Foresters.GrowTime(levels[0], Biome.Mountains));
    }

    [Fact]
    public void Sawmills_of_every_level_work_wood_and_the_better_ones_run_on_power_and_glue_plywood()
    {
        var plain = Processing.ByKind[Items.Sawmill];
        var electric = Processing.ByKind[Items.ElectricSawmill];
        var industrial = Processing.ByKind[Items.IndustrialSawmill];
        Assert.False(plain.Powered);
        Assert.True(electric.Powered);
        Assert.True(industrial.Powered);
        Assert.Null(plain.Recipe(Items.Plywood));
        Assert.NotNull(electric.Recipe(Items.Plywood));
        Assert.True(electric.Recipe(Items.Planks)!.Time < plain.Recipe(Items.Planks)!.Time);
        Assert.True(industrial.Recipe(Items.Planks)!.Quantity > electric.Recipe(Items.Planks)!.Quantity);

        // A better sawmill both works faster and gets more out of a log, recipe by recipe, so it pays for itself
        // over as many plain sawmills as it is faster: on the same wood it makes more.
        foreach (var product in new[] { Items.Planks, Items.Beams, Items.WoodPellets })
        {
            double PerWood(ProcessingMachine m) => (double)m.Recipe(product)!.Quantity / m.Recipe(product)!.Ingredients.Single().Quantity;
            double WoodPerSecond(ProcessingMachine m) => m.Recipe(product)!.Ingredients.Single().Quantity / m.Recipe(product)!.Time.TotalSeconds;
            Assert.True(PerWood(electric) > PerWood(plain));
            Assert.True(PerWood(industrial) > PerWood(electric));
            Assert.True(WoodPerSecond(electric) >= 2.5 * WoodPerSecond(plain));
            Assert.True(WoodPerSecond(industrial) >= 1.7 * WoodPerSecond(electric));
        }

        // Pellets burn in fired machines too.
        Assert.True(Processing.Fuels[Items.WoodPellets] > Processing.Fuels[Items.Wood]);
    }

    [Fact]
    public void The_technology_tree_unlocks_forestry_level_by_level()
    {
        Assert.Equal(TechTree.ForestryIndustry, TechTree.ByItem[Items.ForesterHut].Id);
        Assert.Equal(TechTree.ForestryIndustry, TechTree.ByItem[Items.Sawmill].Id);
        Assert.Equal(TechTree.Woodworking, TechTree.ByItem[Items.ForesterLodge].Id);
        Assert.Equal(TechTree.Woodworking, TechTree.ByItem[Items.Plywood].Id);
        Assert.Equal(TechTree.IndustrialForestry, TechTree.ByItem[Items.ForestryStation].Id);
        Assert.Equal(TechTree.IndustrialForestry, TechTree.ByItem[Items.IndustrialSawmill].Id);
        Assert.Contains(Items.ForesterHut, Workshop.Find(Items.ForesterLodge)!.Ingredients.Select(i => i.Item));
        Assert.Contains(Items.ForesterLodge, Workshop.Find(Items.ForestryStation)!.Ingredients.Select(i => i.Item));
        Assert.Contains(Items.Sawmill, Workshop.Find(Items.ElectricSawmill)!.Ingredients.Select(i => i.Item));
        Assert.Contains(Items.ElectricSawmill, Workshop.Find(Items.IndustrialSawmill)!.Ingredients.Select(i => i.Item));
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
        var skip = Interlocked.Increment(ref _plots) + 9000;
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

    /// <summary>Buys a plot of <paramref name="biome"/> and finds a tile with a free square around it, <paramref name="reach"/> tiles each way.</summary>
    private async Task<(int X, int Y, int TileX, int TileY, byte[] Tiles)> PlotAsync(GameClient client, Biome biome = Biome.Grassland, int reach = 3)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var (x, y) = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        for (var ty = 25; ty < PlotLayout.Size - 10; ty++)
        {
            for (var tx = 10; tx < PlotLayout.Size - 10; tx++)
            {
                var free = true;
                for (var dy = -reach; dy <= reach && free; dy++)
                {
                    for (var dx = -reach; dx <= reach && free; dx++)
                    {
                        free = bytes[(ty + dy) * PlotLayout.Size + tx + dx] == (byte)TileObject.None;
                    }
                }

                if (free)
                {
                    return (x, y, tx, ty, bytes);
                }
            }
        }

        throw new InvalidOperationException("No free square.");
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = 0)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static JsonElement Forester(JsonElement forestry, string id) =>
        forestry.GetProperty("foresters").EnumerateArray().First(f => f.GetProperty("id").GetString() == id);

    [Fact]
    public async Task A_forester_on_a_plot_plants_its_forest_cuts_it_and_keeps_the_wood_until_taken_out()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty, tiles) = await PlotAsync(client);

        // A tree the player planted away from the forest is theirs, not the forester's.
        var own = OutsideForest(tiles, tx, ty);
        await GiveAsync(client, Items.Sapling, 1);
        Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", new { x, y, tileX = own.X, tileY = own.Y })).Type());

        await GiveAsync(client, Items.ForesterHut, 1);
        var hut = await PlaceAsync(client, x, y, Items.ForesterHut, tx, ty);

        // It covers 3 × 3 tiles: nothing else goes on them.
        await GiveAsync(client, Items.WoodenChest, 1);
        Assert.Equal("blocked", (await client.SendAsync("plot.place", new { x, y, kind = Items.WoodenChest, tileX = tx + 1, tileY = ty - 1 })).ErrorCode());

        var planted = (await client.SendAsync("forestry.get", new { x, y })).Payload();
        var forester = Forester(planted, hut);
        Assert.Equal(1, forester.GetProperty("level").GetInt32());
        Assert.Equal(100, forester.GetProperty("capacity").GetInt32());
        var trees = forester.GetProperty("trees").GetInt32();
        Assert.True(trees > 50, $"only {trees} trees");
        Assert.Equal(0, forester.GetProperty("wood").GetInt32());
        Assert.True(planted.GetProperty("trees").GetArrayLength() >= trees);

        // A sapling under its care grows as on its own (10 min on grassland), then it cuts one every 6 s.
        factory.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(60));
        var cut = Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), hut);
        Assert.Equal(10, cut.GetProperty("wood").GetInt32());

        var taken = (await client.SendAsync("forester.take", new { x, y, id = hut })).Payload();
        Assert.Equal(10, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0, Forester(taken, hut).GetProperty("wood").GetInt32());
        Assert.Equal(10, (await client.InventoryAsync()).GetValueOrDefault(Items.Wood));

        // Left alone it fills up and stops.
        factory.Time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(100, Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), hut).GetProperty("wood").GetInt32());
        var stats = (await client.SendAsync("stats.production", new { minutes = 1440 })).Payload();
        Assert.Contains(stats.GetProperty("stalls").EnumerateArray(), s => s.GetProperty("reason").GetString() == "output_full"
            && s.GetProperty("kind").GetString() == Items.ForesterHut);

        // Picked up, it gives its wood back and its forest goes with it; the player's own tree stays.
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = hut })).Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(110, inventory.GetValueOrDefault(Items.Wood));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.ForesterHut));
        var left = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("trees").EnumerateArray()
            .Select(t => (t.GetProperty("tileX").GetInt32(), t.GetProperty("tileY").GetInt32())).ToList();
        Assert.Equal([own], left);
    }

    /// <summary>A free tile just beyond the forest of a hut on (tx, ty).</summary>
    private static (int X, int Y) OutsideForest(byte[] tiles, int tx, int ty)
    {
        for (var d = 7; d < 40; d++)
        {
            for (var dx = -d; dx <= d; dx++)
            {
                var (x, y) = (tx + dx, ty - d);
                if (PlotLayout.Inside(x, y) && tiles[y * PlotLayout.Size + x] == (byte)TileObject.None && (x, y) != PlotLayout.Spawn)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No free tile.");
    }

    [Fact]
    public async Task An_inserter_next_to_a_forester_takes_its_wood_into_a_chest()
    {
        using var client = await PlayerAsync();
        // No wild tree in its forest: it would be cut first and bring a piece of wood more.
        var (x, y, tx, ty, _) = await PlotAsync(client, reach: 5);
        await GiveAsync(client, Items.ForesterHut, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 20);

        // The door faces south; the inserter stands under the building in the clearing, facing south onto the chest on the path.
        var hut = await PlaceAsync(client, x, y, Items.ForesterHut, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx, ty + 2, 0);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty + 3);
        await PlaceAsync(client, x, y, Items.Pole, tx + 1, ty + 3);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx - 1, ty + 3);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());

        factory.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(60));
        var logistics = (await client.SendAsync("logistics.get", new { x, y })).Payload();
        var storages = logistics.GetProperty("storages").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        var stored = storages[chest].GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() == Items.Wood).Sum(i => i.GetProperty("quantity").GetInt32());
        // A piece may be on its way, in the inserter's hand.
        var held = logistics.GetProperty("inserters").EnumerateArray()
            .Where(i => i.GetProperty("hand").ValueKind == JsonValueKind.String && i.GetProperty("hand").GetString() == Items.Wood)
            .Sum(i => i.GetProperty("count").GetInt32());
        var left = Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), hut).GetProperty("wood").GetInt32();
        Assert.True(stored >= 9, $"{stored} in the chest");
        Assert.Equal(10, stored + held + left);
    }

    [Fact]
    public async Task A_forestry_station_feeding_a_long_belt_keeps_working_while_nobody_looks()
    {
        using var client = await PlayerAsync();
        var (x, y, _, _, tiles) = await PlotAsync(client);
        // A free square for the station with its clearing, and a free strip three tiles wide south of it for the belt.
        var (tx, ty) = (0, 0);
        for (var cy = 8; cy < PlotLayout.Size - 20 && tx == 0; cy++)
        {
            for (var cx = 8; cx < PlotLayout.Size - 8 && tx == 0; cx++)
            {
                var free = true;
                for (var dy = -3; dy <= 14 && free; dy++)
                {
                    for (var dx = dy > 3 ? -1 : -3; dx <= (dy > 3 ? 1 : 3) && free; dx++)
                    {
                        free = tiles[(cy + dy) * PlotLayout.Size + cx + dx] == (byte)TileObject.None && (cx + dx, cy + dy) != PlotLayout.Spawn;
                    }
                }

                if (free)
                {
                    (tx, ty) = (cx, cy);
                }
            }
        }

        Assert.NotEqual(0, tx);
        await GiveAsync(client, Items.ForestryStation, 1);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.IronChest, 1);
        await GiveAsync(client, Items.Belt, 9);
        await GiveAsync(client, Items.Pole, 2);
        await GiveAsync(client, Items.Generator, 2);
        await GiveAsync(client, Items.Coal, 100);

        // Station, inserter, nine belts south along its path, inserter, chest; a generator by each inserter.
        var station = await PlaceAsync(client, x, y, Items.ForestryStation, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx, ty + 2);
        for (var by = ty + 3; by <= ty + 11; by++)
        {
            await PlaceAsync(client, x, y, Items.Belt, tx, by);
        }

        await PlaceAsync(client, x, y, Items.Inserter, tx, ty + 12);
        var chest = await PlaceAsync(client, x, y, Items.IronChest, tx, ty + 13);
        foreach (var gy in new[] { ty + 3, ty + 12 })
        {
            await PlaceAsync(client, x, y, Items.Pole, tx + 1, gy);
            var generator = await PlaceAsync(client, x, y, Items.Generator, tx - 1, gy);
            Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 50 })).Type());
        }

        // An hour away: wood keeps coming down the belt the whole time, not only for the first half hour.
        factory.Time.Advance(TimeSpan.FromHours(1));
        var storages = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("storages")
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        var stored = storages[chest].GetProperty("stored").GetInt32();
        Assert.True(stored > 1400, $"{stored} in the chest");
        Assert.False(Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), station).GetProperty("full").GetBoolean());
    }

    [Fact]
    public async Task Inserters_next_to_two_foresters_of_a_plot_each_take_its_wood()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty, _) = await PlotAsync(client, reach: 6);
        await GiveAsync(client, Items.ForesterHut, 2);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.WoodenChest, 2);
        await GiveAsync(client, Items.Pole, 2);
        await GiveAsync(client, Items.Generator, 2);
        await GiveAsync(client, Items.Coal, 40);

        // Both are opened once for the plot's logistics, and each inserter still takes from its own.
        var huts = new List<string>();
        var chests = new List<string>();
        foreach (var hx in new[] { tx - 4, tx + 4 })
        {
            huts.Add(await PlaceAsync(client, x, y, Items.ForesterHut, hx, ty));
            await PlaceAsync(client, x, y, Items.Inserter, hx, ty + 2, 0);
            chests.Add(await PlaceAsync(client, x, y, Items.WoodenChest, hx, ty + 3));
            await PlaceAsync(client, x, y, Items.Pole, hx + 1, ty + 3);
            var generator = await PlaceAsync(client, x, y, Items.Generator, hx - 1, ty + 3);
            Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());
        }

        factory.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(60));
        var storages = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("storages")
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        var forestry = (await client.SendAsync("forestry.get", new { x, y })).Payload();
        for (var i = 0; i < 2; i++)
        {
            var stored = storages[chests[i]].GetProperty("items").EnumerateArray()
                .Where(it => it.GetProperty("id").GetString() == Items.Wood).Sum(it => it.GetProperty("quantity").GetInt32());
            Assert.True(stored >= 5, $"{stored} in chest {i}");
            Assert.InRange(Forester(forestry, huts[i]).GetProperty("wood").GetInt32(), 0, 1);
        }
    }

    [Fact]
    public async Task A_forester_in_a_forest_cuts_the_wild_trees_first_and_plants_where_they_stood()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty, tiles) = await PlotAsync(client, Biome.Forest, 1);
        await GiveAsync(client, Items.ForesterHut, 1);
        var hut = await PlaceAsync(client, x, y, Items.ForesterHut, tx, ty);
        var wild = Enumerable.Range(-5, 11).SelectMany(dy => Enumerable.Range(-5, 11).Select(dx => (dx, dy)))
            .Count(t => Foresters.Plants(Hut, 0, t.dx, t.dy) && Trees.IsTree(tiles[(ty + t.dy) * PlotLayout.Size + tx + t.dx]));
        Assert.True(wild > 0);

        factory.Time.Advance(TimeSpan.FromSeconds(6 * Math.Min(wild, 5)));
        var forestry = (await client.SendAsync("forestry.get", new { x, y })).Payload();
        // An oak gives two pieces of oak wood, every other tree one of wood.
        var wood = Forester(forestry, hut).GetProperty("wood").GetInt32();
        Assert.InRange(wood, Math.Min(wild, 5), 2 * Math.Min(wild, 5));
        Assert.Equal(Math.Min(wild, 5), forestry.GetProperty("cleared").GetArrayLength());
    }

    private static int Standing(JsonElement forester, string species) =>
        forester.GetProperty("kinds").EnumerateArray().First(k => k.GetProperty("id").GetString() == species).GetProperty("standing").GetInt32();

    private static int Stored(JsonElement forester, string item) =>
        forester.GetProperty("store").EnumerateArray().Where(s => s.GetProperty("id").GetString() == item).Sum(s => s.GetProperty("quantity").GetInt32());

    [Fact]
    public async Task A_forester_plants_the_kind_of_tree_picked_where_it_cuts_one_and_an_oak_forest_gives_oak_wood()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty, _) = await PlotAsync(client);
        await GiveAsync(client, Items.ForesterHut, 1);
        var hut = await PlaceAsync(client, x, y, Items.ForesterHut, tx, ty);
        var placed = Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), hut);
        Assert.Equal(Trees.Pine, placed.GetProperty("species").GetString());
        Assert.Equal(5, placed.GetProperty("kinds").GetArrayLength());
        var all = placed.GetProperty("trees").GetInt32();
        Assert.Equal(all, Standing(placed, Trees.Pine));

        Assert.Equal("bad_species", (await client.SendAsync("forester.plant", new { x, y, id = hut, species = "palm" })).ErrorCode());
        var oak = Forester((await client.SendAsync("forester.plant", new { x, y, id = hut, species = Trees.Oak })).Payload(), hut);
        Assert.Equal(Trees.Oak, oak.GetProperty("species").GetString());
        // An oak grows three times as long as a pine: 30 min on grassland.
        Assert.Equal(1800, oak.GetProperty("growSeconds").GetDouble());

        // The pines growing already are cut as they are, and oaks go in where they stood.
        factory.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(60));
        var cut = Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), hut);
        Assert.Equal(10, Stored(cut, Items.Wood));
        Assert.Equal(10, Standing(cut, Trees.Oak));
        Assert.Equal(all - 10, Standing(cut, Trees.Pine));

        // Room for all the pines and the first oaks, however big the forest is.
        Assert.Equal("forestry", (await client.SendAsync("forester.take", new { x, y, id = hut })).Type());

        // Grown oaks give oak wood.
        factory.Time.Advance(TimeSpan.FromMinutes(30));
        var oaks = Forester((await client.SendAsync("forestry.get", new { x, y })).Payload(), hut);
        Assert.Equal(all - 10, Stored(oaks, Items.Wood));
        Assert.Equal(18, Stored(oaks, Items.OakWood));
        Assert.Equal(all, Standing(oaks, Trees.Oak));

        var taken = (await client.SendAsync("forester.take", new { x, y, id = hut })).Payload().GetProperty("taken").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("quantity").GetInt32());
        Assert.Equal(new Dictionary<string, int> { [Items.Wood] = all - 10, [Items.OakWood] = 18 }, taken);
        var inventory = await client.InventoryAsync();
        Assert.Equal(18, inventory.GetValueOrDefault(Items.OakWood));

        // An oak cut by hand from its forest is an oak too.
        var trees = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("trees").EnumerateArray().ToList();
        Assert.Equal(all, trees.Count(t => t.GetProperty("species").GetString() == Trees.Oak));
    }

    [Fact]
    public async Task A_forester_cannot_stand_on_ice_or_on_half_a_free_square()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty, _) = await PlotAsync(client);
        await GiveAsync(client, Items.ForesterHut, 2);
        await GiveAsync(client, Items.WoodenChest, 1);
        await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        Assert.Equal("blocked", (await client.SendAsync("plot.place", new { x, y, kind = Items.ForesterHut, tileX = tx + 1, tileY = ty + 1, rotation = 0 })).ErrorCode());
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.ForesterHut, tileX = tx + 2, tileY = ty + 2, rotation = 0 })).Type());
    }

    [Fact]
    public async Task A_sawmill_on_a_plot_saws_wood_into_planks()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty, _) = await PlotAsync(client);
        await GiveAsync(client, Items.Sawmill, 1);
        await GiveAsync(client, Items.Wood, 5);
        var sawmill = await PlaceAsync(client, x, y, Items.Sawmill, tx, ty);

        // Planks are researched first.
        Assert.Equal("locked", (await client.SendAsync("machine.recipe", new { machine = sawmill, recipe = Items.Planks })).ErrorCode());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryWorld.Modules.Abstractions.GameDb>();
            await using var command = await db.CommandAsync(
                "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
            command.Parameters.AddWithValue("owner", client.AccountId);
            command.Parameters.AddWithValue("id", TechTree.ForestryIndustry);
            command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = sawmill, recipe = Items.Planks })).Type());
        Assert.Equal("machine", (await client.SendAsync("machine.load", new { machine = sawmill, item = Items.Wood, quantity = 5 })).Type());
        factory.Time.Advance(TimeSpan.FromSeconds(40));
        var taken = (await client.SendAsync("machine.take", new { machine = sawmill, what = "output" })).Payload();
        Assert.Equal(10, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(10, (await client.InventoryAsync()).GetValueOrDefault(Items.Planks));
    }
}
