using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class FootprintTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;
    private const int East = 1;

    // --- The rules, without a database ----------------------------------------------------------

    private static PlotObject Obj(string kind, int x, int y) => new(Guid.NewGuid(), kind, x, y, 0, Guid.Empty);

    [Fact]
    public void Machines_grow_with_their_level_and_an_even_square_reaches_right_and_down()
    {
        Assert.Equal(2, Footprints.Size(Items.Furnace));
        Assert.Equal(3, Footprints.Size(Items.BrickFurnace));
        Assert.Equal(4, Footprints.Size(Items.BlastFurnace));
        Assert.Equal(2, Footprints.Size(Items.Crusher));
        Assert.Equal(2, Footprints.Size(Items.Sawmill));
        Assert.Equal(3, Footprints.Size(Items.ElectricSawmill));
        Assert.Equal(4, Footprints.Size(Items.IndustrialSawmill));
        Assert.Equal(1, Footprints.Size(Items.Inserter));
        Assert.Equal(1, Footprints.Size(Items.Workbench));
        Assert.Equal([(5, 5), (6, 5), (5, 6), (6, 6)], Footprints.Tiles(Items.Furnace, 5, 5));
        Assert.Equal(9, Footprints.Tiles(Items.BrickFurnace, 5, 5).Distinct().Count());
        Assert.Contains((4, 4), Footprints.Tiles(Items.BrickFurnace, 5, 5));
        Assert.Equal((4, 4, 7, 7), Bounds(Footprints.Tiles(Items.BlastFurnace, 5, 5)));
        Assert.True(Footprints.Covers(Items.BlastFurnace, 5, 5, 7, 4));
        Assert.False(Footprints.Covers(Items.BlastFurnace, 5, 5, 3, 5));
        Assert.All(Footprints.Sizes.Keys, k => Assert.True(PlotObjects.Placeable.Contains(k), k));
    }

    private static (int, int, int, int) Bounds(IEnumerable<(int X, int Y)> tiles) =>
        (tiles.Min(t => t.X), tiles.Min(t => t.Y), tiles.Max(t => t.X), tiles.Max(t => t.Y));

    [Fact]
    public void A_machine_that_no_longer_fits_moves_as_little_as_it_takes_or_goes_back_to_its_owner()
    {
        // A furnace with a chest right of it, one boxed in by chests, and a mill with a belt (which never moves) at its bottom right.
        var squeezed = Obj(Items.Furnace, 11, 11);
        var boxed = Obj(Items.Furnace, 21, 11);
        var open = Obj(Items.Mill, 30, 30);
        var objects = new List<PlotObject> { squeezed, Obj(Items.WoodenChest, 12, 11), boxed, open, Obj(Items.Belt, 31, 31) };
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx, dy) != (0, 0))
                {
                    objects.Add(Obj(Items.WoodenChest, 21 + dx, 11 + dy));
                }
            }
        }

        var plan = FootprintFit.Fit(objects, (_, _) => true);

        // The squeezed furnace and the mill still cover their old tiles, so what reached them reaches them still; the boxed one goes back.
        var moves = plan.Moved.ToDictionary(m => m.Object.Id, m => (m.TileX, m.TileY));
        Assert.Equal(2, moves.Count);
        Assert.Equal((10, 11), moves[squeezed.Id]);
        Assert.Equal((30, 29), moves[open.Id]);
        Assert.Contains((11, 11), Footprints.Tiles(Items.Furnace, 10, 11));
        Assert.Equal(boxed.Id, Assert.Single(plan.Refunded).Id);
    }

    [Fact]
    public void Nature_off_the_ground_counts_too_and_a_fitting_machine_stays()
    {
        var furnace = Obj(Items.Furnace, 5, 5);
        // A rock right of it and below it to the left: it moves up.
        var plan = FootprintFit.Fit([furnace], (x, y) => (x, y) is not ((6, 6) or (4, 6)));
        Assert.Equal((5, 4), (plan.Moved.Single().TileX, plan.Moved.Single().TileY));
        Assert.Empty(FootprintFit.Fit([furnace], (_, _) => true).Moved);
        Assert.Single(FootprintFit.Fit([furnace], (x, _) => x != 5).Refunded);
    }

    [Fact]
    public void Assembling_machines_make_the_workbench_recipes_faster_by_level()
    {
        var plain = Processing.ByKind[Items.Assembler];
        var fast = Processing.ByKind[Items.FastAssembler];
        var express = Processing.ByKind[Items.ExpressAssembler];
        Assert.False(plain.Powered);
        Assert.True(fast.Powered);
        Assert.True(express.Powered);
        var nails = Workshop.Find(Items.Nails)!;
        Assert.Equal(nails.Time, plain.Recipe(Items.Nails)!.Time);
        Assert.Equal(nails.Time / 2, fast.Recipe(Items.Nails)!.Time);
        Assert.Equal(nails.Time / 4, express.Recipe(Items.Nails)!.Time);
        Assert.Equal(nails.Ingredients, express.Recipe(Items.Nails)!.Ingredients);
        // The workbench's buildings too, but not the anvil's tools or the tailor's clothes.
        Assert.NotNull(plain.Recipe(Items.Furnace));
        Assert.NotNull(plain.Recipe(Items.Inserter));
        Assert.All(Workshop.Recipes.Where(r => r.Station != Items.Workbench), r => Assert.Null(plain.Recipe(r.Id)));
        Assert.All(plain.Recipes, r => Assert.All(r.Ingredients, i => Assert.True(i.Quantity <= Processing.MaxInput, r.Id)));
        Assert.Equal([2, 3, 4], new[] { Items.Assembler, Items.FastAssembler, Items.ExpressAssembler }.Select(Footprints.Size));
        Assert.Contains(Items.Workbench, Workshop.Find(Items.Assembler)!.Ingredients.Select(i => i.Item));
        Assert.Contains(Items.Assembler, Workshop.Find(Items.FastAssembler)!.Ingredients.Select(i => i.Item));
        Assert.Contains(Items.FastAssembler, Workshop.Find(Items.ExpressAssembler)!.Ingredients.Select(i => i.Item));
    }

    // --- On a plot -------------------------------------------------------------------------------

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 15000;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= (biome == Biome.Grassland ? skip : skip - 15000))
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    /// <summary>Buys a plot (on grassland unless told otherwise) and finds open ground <paramref name="reach"/> tiles each way around
    /// a tile of it, by the station in a city.</summary>
    private static async Task<(int X, int Y, int TileX, int TileY)> PlotAsync(GameClient client, int reach, Biome biome = Biome.Grassland)
    {
        var (x, y) = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        for (var i = PlotLayout.Size * 20; i < bytes.Length; i++)
        {
            var (tx, ty) = (i % PlotLayout.Size, i / PlotLayout.Size);
            if (PlotTiles.Clear(bytes, tx, ty, reach)
                && (biome != Biome.City || (CityRules.InBuildArea(tx - reach, ty + reach) && CityRules.InBuildArea(tx + reach, ty + reach))))
            {
                return (x, y, tx, ty);
            }
        }

        throw new InvalidOperationException("No open ground.");
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = East)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.True(reply.Type() == "plot.placed", reply.ToString());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static int StoredIn(JsonElement storage, string item) =>
        storage.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("id").GetString() == item).Sum(i => i.GetProperty("quantity").GetInt32());

    [Fact]
    public async Task A_furnace_covers_2_x_2_tiles_and_two_inserters_on_one_side_both_reach_it()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client, 3);
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.WoodenChest, 3);
        await GiveAsync(client, Items.Inserter, 3);
        await GiveAsync(client, Items.Belt, 1);
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 21);
        await GiveAsync(client, Items.IronOre, 6);

        await PlaceAsync(client, x, y, Items.Furnace, tx, ty);
        foreach (var (bx, by) in new[] { (tx + 1, ty), (tx, ty + 1), (tx + 1, ty + 1) })
        {
            var blocked = await client.SendAsync("plot.place", new { x, y, kind = Items.Belt, tileX = bx, tileY = by });
            Assert.Equal("blocked", blocked.ErrorCode());
        }

        // Ore and coal come in from two chests on the furnace's west side, side by side; the ingots go out east into a third chest.
        var ore = await PlaceAsync(client, x, y, Items.WoodenChest, tx - 2, ty);
        var coal = await PlaceAsync(client, x, y, Items.WoodenChest, tx - 2, ty + 1);
        await PlaceAsync(client, x, y, Items.Inserter, tx - 1, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx - 1, ty + 1);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 2, ty + 1);
        var ingots = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 3, ty + 1);
        await PlaceAsync(client, x, y, Items.Pole, tx, ty + 2);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx - 1, ty + 2);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());
        await client.SendAsync("storage.put", new { x, y, id = ore, item = Items.IronOre, quantity = 6 });
        await client.SendAsync("storage.put", new { x, y, id = coal, item = Items.Coal, quantity = 1 });

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var storages = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("storages")
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(0, StoredIn(storages[ore], Items.IronOre));
        Assert.Equal(0, StoredIn(storages[coal], Items.Coal));
        Assert.Equal(5, StoredIn(storages[ingots], Items.IronIngot));
    }

    [Fact]
    public async Task An_assembling_machine_makes_a_workbench_recipe_by_itself()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client, 2, Biome.City);
        await GiveAsync(client, Items.Assembler, 1);
        await GiveAsync(client, Items.IronIngot, 3);
        var assembler = await PlaceAsync(client, x, y, Items.Assembler, tx, ty);
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = assembler, recipe = Items.Nails })).Type());
        var loaded = (await client.SendAsync("machine.load", new { machine = assembler, item = Items.IronIngot, quantity = 3 })).Payload();
        Assert.False(loaded.GetProperty("powered").GetBoolean());

        // In a city machines work 25% faster: 20 s here are 25 s of work.
        factory.Time.Advance(TimeSpan.FromSeconds(20));
        var state = (await client.SendAsync("machine.get", new { machine = assembler })).Payload();
        Assert.Equal(10, state.GetProperty("output").GetProperty("quantity").GetInt32());
        factory.Time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(15, (await client.SendAsync("machine.get", new { machine = assembler })).Payload().GetProperty("output").GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Assembling_machines_stand_in_a_city_only()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client, 2);
        foreach (var kind in new[] { Items.Assembler, Items.FastAssembler, Items.ExpressAssembler })
        {
            await GiveAsync(client, kind, 1);
            var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX = tx, tileY = ty, rotation = East });
            Assert.Equal("no_city", reply.ErrorCode());
        }
    }

    [Fact]
    public async Task Plots_built_before_machines_grew_are_brought_in_line_once()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client, 4);
        // As the plot stood before: a furnace with a chest right of it, a furnace boxed in by chests (with ore in it) and a mill in the open.
        var squeezed = Guid.NewGuid();
        var boxed = Guid.NewGuid();
        var mill = Guid.NewGuid();
        var assembler = Guid.NewGuid();
        var rows = new List<(Guid Id, string Kind, int TileX, int TileY)>
        {
            (squeezed, Items.Furnace, tx - 2, ty - 3), (Guid.NewGuid(), Items.WoodenChest, tx - 1, ty - 3),
            (boxed, Items.Furnace, tx + 2, ty), (mill, Items.Mill, tx - 3, ty + 2), (assembler, Items.Assembler, tx + 2, ty + 3),
        };
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx, dy) != (0, 0))
                {
                    rows.Add((Guid.NewGuid(), Items.WoodenChest, tx + 2 + dx, ty + dy));
                }
            }
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            foreach (var row in rows)
            {
                await using var insert = await db.CommandAsync(
                    """
                    INSERT INTO world.plot_objects (id, x, y, tile_x, tile_y, owner_id, kind, rotation)
                    VALUES (@id, @x, @y, @tx, @ty, @owner, @kind, 0)
                    """, CancellationToken.None);
                insert.Parameters.AddWithValue("id", row.Id);
                insert.Parameters.AddWithValue("x", x);
                insert.Parameters.AddWithValue("y", y);
                insert.Parameters.AddWithValue("tx", row.TileX);
                insert.Parameters.AddWithValue("ty", row.TileY);
                insert.Parameters.AddWithValue("owner", client.AccountId);
                insert.Parameters.AddWithValue("kind", row.Kind);
                await insert.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        await GiveAsync(client, Items.IronOre, 5);
        Assert.Equal("furnace", (await client.SendAsync("furnace.load", new { furnace = boxed, item = Items.IronOre, quantity = 5 })).Type());
        var before = await client.InventoryAsync();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await using (var reset = await db.CommandAsync("DELETE FROM world.footprint_versions", CancellationToken.None))
            {
                await reset.ExecuteNonQueryAsync(CancellationToken.None);
            }

            Assert.Equal((1, 2), await scope.ServiceProvider.GetRequiredService<FootprintMigration>().RunAsync(CancellationToken.None));
        }

        // The squeezed furnace moved left, the boxed one came back to its owner with its ore, the mill stayed; the assembling machine,
        // which has room but stands in a city only, came back too.
        var objects = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("objects").EnumerateArray()
            .ToDictionary(o => o.GetProperty("id").GetGuid(), o => (o.GetProperty("tileX").GetInt32(), o.GetProperty("tileY").GetInt32()));
        Assert.Equal((tx - 3, ty - 3), objects[squeezed]);
        Assert.False(objects.ContainsKey(boxed));
        Assert.False(objects.ContainsKey(assembler));
        Assert.Equal((tx - 3, ty + 2), objects[mill]);
        var after = await client.InventoryAsync();
        Assert.Equal(before.GetValueOrDefault(Items.Furnace) + 1, after.GetValueOrDefault(Items.Furnace));
        Assert.Equal(before.GetValueOrDefault(Items.IronOre) + 5, after.GetValueOrDefault(Items.IronOre));
        Assert.Equal(before.GetValueOrDefault(Items.Assembler) + 1, after.GetValueOrDefault(Items.Assembler));

        // Done once: the next start leaves the plots alone.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal((0, 0), await scope.ServiceProvider.GetRequiredService<FootprintMigration>().RunAsync(CancellationToken.None));
        }
    }
}
