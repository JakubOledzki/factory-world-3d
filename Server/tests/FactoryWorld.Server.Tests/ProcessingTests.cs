using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class ProcessingTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

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

    private static long Count(Dictionary<string, long> inventory, string item) => inventory.GetValueOrDefault(item);

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 6000;
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

    /// <summary>Buys a plot and puts a machine of <paramref name="kind"/> on its first free tile; one that runs
    /// on power gets a pole and a generator with coal next to it, unless <paramref name="powered"/> says no.</summary>
    private async Task<(int X, int Y, string Id)> MachineOnPlotAsync(GameClient client, string kind, bool? powered = null)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        powered ??= Processing.ByKind[kind].Powered;
        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        // Free ground for the machine with two tiles more east of it, for a pole and a generator.
        var size = Footprints.Size(kind);
        bool Free(int i) => bytes[i] == (byte)TileObject.None;
        var free = Enumerable.Range(PlotLayout.Size * 20, bytes.Length - PlotLayout.Size * 30)
            .First(i => i % PlotLayout.Size < PlotLayout.Size - size - 2
                && Enumerable.Range(0, size + 2).All(dx => Enumerable.Range(0, size).All(dy => Free(i + dx + dy * PlotLayout.Size))));
        var (tileX, tileY) = (free % PlotLayout.Size + Footprints.Low(kind), free / PlotLayout.Size + Footprints.Low(kind));

        await GiveAsync(client, kind, 1);
        var placed = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY });
        Assert.Equal("plot.placed", placed.Type());
        if (powered == true)
        {
            await GiveAsync(client, Items.Pole, 1);
            await GiveAsync(client, Items.Generator, 1);
            await GiveAsync(client, Items.Coal, 10);
            var east = tileX + Footprints.High(kind) + 1;
            Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.Pole, tileX = east, tileY })).Type());
            var generator = (await client.SendAsync("plot.place", new { x, y, kind = Items.Generator, tileX = east + 1, tileY }))
                .Payload().GetProperty("object").GetProperty("id").GetString();
            Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 10 })).Type());
        }

        return (x, y, placed.Payload().GetProperty("object").GetProperty("id").GetString()!);
    }

    [Fact]
    public async Task A_machine_works_20_percent_slower_3_days_after_it_first_worked_until_its_owner_pays_for_a_service()
    {
        using var client = await PlayerAsync();
        var (_, _, sawmill) = await MachineOnPlotAsync(client, Items.Sawmill);
        await GiveAsync(client, Items.Wood, 3);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await using var command = await db.CommandAsync(
                "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
            command.Parameters.AddWithValue("owner", client.AccountId);
            command.Parameters.AddWithValue("id", TechTree.ForestryIndustry);
            command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = sawmill, recipe = Items.Planks })).Type());
        var fresh = (await client.SendAsync("machine.load", new { machine = sawmill, item = Items.Wood, quantity = 1 })).Payload();
        Assert.False(fresh.GetProperty("worn").GetBoolean());
        Assert.Equal(8, fresh.GetProperty("seconds").GetDouble());

        // Three days on, a batch of planks takes 10 s instead of 8.
        factory.Time.Advance(MachineServicing.Every);
        var worn = (await client.SendAsync("machine.load", new { machine = sawmill, item = Items.Wood, quantity = 2 })).Payload();
        Assert.True(worn.GetProperty("worn").GetBoolean());
        Assert.Equal(10, worn.GetProperty("seconds").GetDouble());
        factory.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(2, (await client.SendAsync("machine.get", new { machine = sawmill })).Payload().GetProperty("output").GetProperty("quantity").GetInt32());
        factory.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(4, (await client.SendAsync("machine.get", new { machine = sawmill })).Payload().GetProperty("output").GetProperty("quantity").GetInt32());

        // A service costs a tenth of what the trader asks for a sawmill; the batch being made goes at full speed again.
        long cents;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            cents = scope.ServiceProvider.GetRequiredService<IMarketPrices>().SellsCents(Items.Sawmill)!.Value / 10;
        }

        var before = await client.BalanceAsync();
        var serviced = (await client.SendAsync("machine.service", new { machine = sawmill })).Payload();
        Assert.Equal(cents, serviced.GetProperty("serviceCents").GetInt64());
        Assert.Equal(before - cents, serviced.GetProperty("balanceCents").GetInt64());
        Assert.Equal(before - cents, await client.BalanceAsync());
        Assert.False(serviced.GetProperty("worn").GetBoolean());
        Assert.Equal(8, serviced.GetProperty("seconds").GetDouble());
        factory.Time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal(6, (await client.SendAsync("machine.get", new { machine = sawmill })).Payload().GetProperty("output").GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Picking_a_worn_machine_up_and_putting_it_down_again_does_not_service_it()
    {
        using var client = await PlayerAsync();
        var (x, y, sawmill) = await MachineOnPlotAsync(client, Items.Sawmill);
        await GiveAsync(client, Items.Wood, 3);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await using var command = await db.CommandAsync(
                "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
            command.Parameters.AddWithValue("owner", client.AccountId);
            command.Parameters.AddWithValue("id", TechTree.ForestryIndustry);
            command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await client.SendAsync("machine.recipe", new { machine = sawmill, recipe = Items.Planks });
        await client.SendAsync("machine.load", new { machine = sawmill, item = Items.Wood, quantity = 1 });
        factory.Time.Advance(MachineServicing.Every);
        int tileX, tileY;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await using var command = await scope.ServiceProvider.GetRequiredService<GameDb>().CommandAsync(
                "SELECT tile_x, tile_y FROM world.plot_objects WHERE id = @id", CancellationToken.None);
            command.Parameters.AddWithValue("id", Guid.Parse(sawmill));
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            await reader.ReadAsync(CancellationToken.None);
            (tileX, tileY) = (reader.GetInt32(0), reader.GetInt32(1));
        }

        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = sawmill })).Type());

        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Sawmill, tileX, tileY });
        var again = placed.Payload().GetProperty("object").GetProperty("id").GetString();
        await client.SendAsync("machine.recipe", new { machine = again, recipe = Items.Planks });
        var loaded = (await client.SendAsync("machine.load", new { machine = again, item = Items.Wood, quantity = 1 })).Payload();
        Assert.True(loaded.GetProperty("worn").GetBoolean());
        Assert.Equal(10, loaded.GetProperty("seconds").GetDouble());
    }

    [Fact]
    public async Task A_service_needs_the_money_and_only_the_owner_may_pay_for_it()
    {
        using var owner = await PlayerAsync();
        using var stranger = await PlayerAsync();
        var (_, _, mill) = await MachineOnPlotAsync(owner, Items.Sawmill);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var wallet = scope.ServiceProvider.GetRequiredService<IWallet>();
            await wallet.DebitAsync(owner.AccountId, await wallet.BalanceAsync(owner.AccountId, CancellationToken.None), "test", CancellationToken.None);
        }

        Assert.Equal("not_enough_money", (await owner.SendAsync("machine.service", new { machine = mill })).ErrorCode());
        Assert.Equal("no_object", (await stranger.SendAsync("machine.service", new { machine = mill })).ErrorCode());
    }

    [Fact]
    public async Task A_mill_grinds_two_wheat_into_one_flour_every_6_seconds()
    {
        using var client = await PlayerAsync();
        var (_, _, mill) = await MachineOnPlotAsync(client, Items.Mill);
        await GiveAsync(client, Items.Wheat, 7);

        Assert.Equal("choose_recipe", (await client.SendAsync("machine.load", new { machine = mill, item = Items.Wheat, quantity = 7 })).ErrorCode());
        var set = (await client.SendAsync("machine.recipe", new { machine = mill, recipe = Items.Flour })).Payload();
        Assert.Equal(Items.Flour, set.GetProperty("recipe").GetString());
        Assert.False(set.GetProperty("fired").GetBoolean());
        Assert.Equal("no_recipe", (await client.SendAsync("machine.recipe", new { machine = mill, recipe = Items.Bread })).ErrorCode());

        var loaded = (await client.SendAsync("machine.load", new { machine = mill, item = Items.Wheat, quantity = 7 })).Payload();
        Assert.Equal(7, loaded.GetProperty("inputs")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(3, loaded.GetProperty("batches").GetInt32());
        Assert.Equal(0, Count(await client.InventoryAsync(), Items.Wheat));

        factory.Time.Advance(TimeSpan.FromSeconds(13));
        var state = (await client.SendAsync("machine.get", new { machine = mill })).Payload();
        Assert.Equal(2, state.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(3, state.GetProperty("inputs")[0].GetProperty("quantity").GetInt32());

        factory.Time.Advance(TimeSpan.FromSeconds(60));
        var taken = (await client.SendAsync("machine.take", new { machine = mill, what = "output" })).Payload();
        Assert.Equal(3, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, taken.GetProperty("startedAt").ValueKind);
        var inventory = await client.InventoryAsync();
        Assert.Equal(3, Count(inventory, Items.Flour));

        // The odd wheat stays until taken out.
        await client.SendAsync("machine.take", new { machine = mill, what = "input" });
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Wheat));
    }

    [Fact]
    public async Task A_bakery_bakes_bread_only_while_it_has_fuel()
    {
        using var client = await PlayerAsync();
        var (_, _, bakery) = await MachineOnPlotAsync(client, Items.Bakery);
        await GiveAsync(client, Items.Flour, 10);
        await GiveAsync(client, Items.Wood, 1);
        await client.SendAsync("machine.recipe", new { machine = bakery, recipe = Items.Bread });

        var cold = (await client.SendAsync("machine.load", new { machine = bakery, item = Items.Flour, quantity = 10 })).Payload();
        Assert.Equal(JsonValueKind.Null, cold.GetProperty("startedAt").ValueKind);
        Assert.Equal("not_processable", (await client.SendAsync("machine.load", new { machine = bakery, item = Items.Stone, quantity = 1 })).ErrorCode());

        // One wood bakes one loaf.
        await client.SendAsync("machine.load", new { machine = bakery, item = Items.Wood, quantity = 1 });
        factory.Time.Advance(TimeSpan.FromSeconds(100));
        var state = (await client.SendAsync("machine.get", new { machine = bakery })).Payload();
        Assert.Equal(1, state.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(8, state.GetProperty("inputs")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("fuel").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("startedAt").ValueKind);

        await GiveAsync(client, Items.Coal, 1);
        await client.SendAsync("machine.load", new { machine = bakery, item = Items.Coal, quantity = 1 });
        factory.Time.Advance(TimeSpan.FromSeconds(200));
        var after = (await client.SendAsync("machine.take", new { machine = bakery, what = "output" })).Payload();
        Assert.Equal(5, after.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(5, Count(await client.InventoryAsync(), Items.Bread));
    }

    [Fact]
    public async Task A_foundry_makes_steel_of_charcoal_where_the_recipe_asks_for_coal()
    {
        using var client = await PlayerAsync();
        var (_, _, foundry) = await MachineOnPlotAsync(client, Items.Foundry);
        await GiveAsync(client, Items.IronIngot, 4);
        await GiveAsync(client, Items.Charcoal, 2);
        await GiveAsync(client, Items.Wood, 2);
        var set = (await client.SendAsync("machine.recipe", new { machine = foundry, recipe = Items.SteelIngot })).Payload();
        var coal = set.GetProperty("inputs")[1];
        Assert.Equal(Items.Coal, coal.GetProperty("id").GetString());
        Assert.Equal(Items.Charcoal, coal.GetProperty("also")[0].GetProperty("id").GetString());

        await client.SendAsync("machine.load", new { machine = foundry, item = Items.IronIngot, quantity = 4 });
        // Charcoal goes where the coal goes, not by the fire.
        var loaded = (await client.SendAsync("machine.load", new { machine = foundry, item = Items.Charcoal, quantity = 2 })).Payload();
        Assert.Equal(2, loaded.GetProperty("inputs")[1].GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, loaded.GetProperty("fuel").ValueKind);
        await client.SendAsync("machine.load", new { machine = foundry, item = Items.Wood, quantity = 2 });

        factory.Time.Advance(TimeSpan.FromSeconds(60));
        var taken = (await client.SendAsync("machine.take", new { machine = foundry, what = "output" })).Payload();
        Assert.Equal(2, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        var inventory = await client.InventoryAsync();
        Assert.Equal(2, Count(inventory, Items.SteelIngot));
        Assert.Equal(0, Count(inventory, Items.Charcoal));
        Assert.True(Items.Fills(Items.Coal, Items.Charcoal));
        Assert.False(Items.Fills(Items.Charcoal, Items.Coal));
    }

    [Fact]
    public async Task Coal_and_charcoal_go_by_the_foundry_fire_even_while_it_makes_steel()
    {
        using var client = await PlayerAsync();
        var (_, _, foundry) = await MachineOnPlotAsync(client, Items.Foundry);
        await GiveAsync(client, Items.IronIngot, 4);
        await GiveAsync(client, Items.Coal, 3);
        await GiveAsync(client, Items.Charcoal, 1);
        await client.SendAsync("machine.recipe", new { machine = foundry, recipe = Items.SteelIngot });
        await client.SendAsync("machine.load", new { machine = foundry, item = Items.IronIngot, quantity = 4 });

        // Coal goes where the recipe wants it unless the player puts it by the fire.
        var ingredient = (await client.SendAsync("machine.load", new { machine = foundry, item = Items.Coal, quantity = 2 })).Payload();
        Assert.Equal(2, ingredient.GetProperty("inputs")[1].GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, ingredient.GetProperty("fuel").ValueKind);
        var fired = (await client.SendAsync("machine.load", new { machine = foundry, item = Items.Coal, quantity = 1, fuel = true })).Payload();
        Assert.Equal(2, fired.GetProperty("inputs")[1].GetProperty("quantity").GetInt32());
        Assert.Equal(Items.Coal, fired.GetProperty("fuel").GetProperty("id").GetString());
        Assert.Equal(1, fired.GetProperty("fuel").GetProperty("quantity").GetInt32());
        Assert.Equal(2, fired.GetProperty("batches").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, fired.GetProperty("startedAt").ValueKind);
        Assert.Equal("machine_busy", (await client.SendAsync("machine.load", new { machine = foundry, item = Items.Charcoal, quantity = 1, fuel = true })).ErrorCode());

        factory.Time.Advance(TimeSpan.FromSeconds(60));
        var taken = (await client.SendAsync("machine.take", new { machine = foundry, what = "output" })).Payload();
        Assert.Equal(2, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        // The coal by the fire is burning, good for two more batches.
        Assert.Equal(JsonValueKind.Null, taken.GetProperty("fuel").ValueKind);
        Assert.Equal(2, taken.GetProperty("heat").GetInt32());

        // Charcoal burns there just the same.
        var charcoal = (await client.SendAsync("machine.load", new { machine = foundry, item = Items.Charcoal, quantity = 1, fuel = true })).Payload();
        Assert.Equal(Items.Charcoal, charcoal.GetProperty("fuel").GetProperty("id").GetString());
        Assert.Equal(0, charcoal.GetProperty("inputs")[1].GetProperty("quantity").GetInt32());
        Assert.Equal("not_fuel", (await client.SendAsync("machine.load", new { machine = foundry, item = Items.IronIngot, quantity = 1, fuel = true })).ErrorCode());
    }

    [Fact]
    public async Task Changing_the_recipe_returns_ingredients_and_products()
    {
        using var client = await PlayerAsync();
        var (_, _, kitchen) = await MachineOnPlotAsync(client, Items.Kitchen);
        await GiveAsync(client, Items.Potato, 3);
        await GiveAsync(client, Items.SunflowerOil, 1);
        await GiveAsync(client, Items.Coal, 1);
        await client.SendAsync("machine.recipe", new { machine = kitchen, recipe = Items.Fries });
        await client.SendAsync("machine.load", new { machine = kitchen, item = Items.Potato, quantity = 3 });
        await client.SendAsync("machine.load", new { machine = kitchen, item = Items.SunflowerOil, quantity = 1 });
        await client.SendAsync("machine.load", new { machine = kitchen, item = Items.Coal, quantity = 1 });
        factory.Time.Advance(TimeSpan.FromSeconds(15));

        var changed = (await client.SendAsync("machine.recipe", new { machine = kitchen, recipe = Items.VegetableSoup })).Payload();

        Assert.Equal(Items.VegetableSoup, changed.GetProperty("recipe").GetString());
        Assert.Equal(3, changed.GetProperty("inputs").GetArrayLength());
        // The coal caught fire for the fries and still has heat for three more batches.
        Assert.Equal(JsonValueKind.Null, changed.GetProperty("fuel").ValueKind);
        Assert.Equal(3, changed.GetProperty("heat").GetInt32());
        var inventory = await client.InventoryAsync();
        Assert.Equal(2, Count(inventory, Items.Fries));
        Assert.Equal(0, Count(inventory, Items.Potato));
    }

    [Fact]
    public async Task Picking_up_a_machine_returns_what_it_holds_and_others_cannot_use_it()
    {
        using var client = await PlayerAsync();
        using var other = await PlayerAsync();
        var (x, y, press) = await MachineOnPlotAsync(client, Items.OilPress);
        await GiveAsync(client, Items.Sunflower, 5);
        await client.SendAsync("machine.recipe", new { machine = press, recipe = Items.SunflowerOil });
        await client.SendAsync("machine.load", new { machine = press, item = Items.Sunflower, quantity = 5 });
        factory.Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal("no_object", (await other.SendAsync("machine.get", new { machine = press })).ErrorCode());
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = press })).Type());

        var inventory = await client.InventoryAsync();
        Assert.Equal(1, Count(inventory, Items.OilPress));
        Assert.Equal(1, Count(inventory, Items.SunflowerOil));
        Assert.Equal(3, Count(inventory, Items.Sunflower));
    }

    [Fact]
    public async Task A_workbench_makes_the_machines_and_the_trader_deals_in_food()
    {
        using var client = await PlayerAsync();
        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { Items.Mill, Items.OilPress, Items.PicklingBarrel, Items.Bakery, Items.Kitchen }, recipes);

        await GiveAsync(client, Items.Bread, 2);
        var sold = await client.SendAsync("market.sell", new { item = Items.Bread, quantity = 2 });
        Assert.Equal("market.traded", sold.Type());
        Assert.Equal(1400, sold.Payload().GetProperty("balanceCents").GetInt64());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Flour, quantity = 3 })).Type());
        Assert.Equal(3, Count(await client.InventoryAsync(), Items.Flour));
    }

    [Fact]
    public void A_full_machine_stops_until_its_products_are_taken()
    {
        var barrel = Processing.ByKind[Items.PicklingBarrel];
        var start = DateTimeOffset.UnixEpoch;
        var s = Processing.Start(barrel, MachineState.Empty with { Recipe = Items.Sauerkraut, Inputs = [10], Output = Processing.MaxOutput - 4 }, start);

        var after = Processing.Advance(barrel, s, start + TimeSpan.FromMinutes(10));

        Assert.Equal(Processing.MaxOutput, after.Output);
        Assert.Equal(6, after.Inputs[0]);
        Assert.Null(after.StartedAt);
    }

    [Fact]
    public void Every_recipe_makes_a_known_item_from_known_items()
    {
        foreach (var machine in Processing.Machines)
        {
            Assert.True(Items.Exists(machine.Kind));
            Assert.Contains(machine.Kind, PlotObjects.Placeable);
            Assert.All(machine.Recipes, r =>
            {
                Assert.True(Items.Exists(r.Id));
                Assert.All(r.Ingredients, i => Assert.True(Items.Exists(i.Item)));
            });
        }
    }
}
