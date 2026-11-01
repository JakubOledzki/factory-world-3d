using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class CraftingTests(ServerFactory factory) : IClassFixture<ServerFactory>
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

    private static async Task<string> GarageObjectAsync(GameClient client, string kind)
    {
        var garage = await client.SendAsync("garage.get");
        return garage.Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == kind).GetProperty("id").GetString()!;
    }

    private static long Count(Dictionary<string, long> inventory, string item) => inventory.GetValueOrDefault(item);

    [Fact]
    public async Task The_garage_workbench_makes_a_workbench_from_20_wood_and_5_nails_in_30_seconds()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.Wood, 25);
        await GiveAsync(client, Items.Nails, 5);

        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal(30, recipes[Items.Workbench].GetProperty("seconds").GetDouble());
        Assert.Equal(45, recipes[Items.Furnace].GetProperty("seconds").GetDouble());

        var ordered = await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Workbench });
        Assert.Equal("crafting", ordered.Type());
        Assert.Equal(1, ordered.Payload().GetProperty("queue").GetArrayLength());
        var inventory = await client.InventoryAsync();
        Assert.Equal(5, Count(inventory, Items.Wood));
        Assert.Equal(0, Count(inventory, Items.Nails));

        factory.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, (await client.SendAsync("crafting.get")).Payload().GetProperty("done").GetArrayLength());

        factory.Time.Advance(TimeSpan.FromSeconds(1));
        var done = (await client.SendAsync("crafting.get")).Payload();
        Assert.Equal(Items.Workbench, done.GetProperty("done")[0].GetProperty("id").GetString());
        Assert.Equal(10, done.GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal(0, done.GetProperty("queue").GetArrayLength());
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Workbench));
    }

    [Fact]
    public async Task Without_enough_ingredients_nothing_is_taken()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.Wood, 20);

        var reply = await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Workbench });

        Assert.Equal("not_enough_items", reply.ErrorCode());
        Assert.Equal(20, Count(await client.InventoryAsync(), Items.Wood));
    }

    [Fact]
    public async Task Crafting_needs_a_workbench_of_ones_own()
    {
        using var client = await PlayerAsync();
        using var other = await PlayerAsync();
        var bin = await GarageObjectAsync(client, Items.TrashBin);
        var othersBench = await GarageObjectAsync(other, Items.Workbench);
        await GiveAsync(client, Items.Stone, 20);

        Assert.Equal("no_object", (await client.SendAsync("crafting.craft", new { workbench = bin, recipe = Items.Furnace })).ErrorCode());
        Assert.Equal("no_object", (await client.SendAsync("crafting.craft", new { workbench = othersBench, recipe = Items.Furnace })).ErrorCode());
        Assert.Equal(20, Count(await client.InventoryAsync(), Items.Stone));
    }

    [Fact]
    public async Task Nails_come_from_the_trader_or_from_an_iron_ingot()
    {
        using var client = await PlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 3000, "test", CancellationToken.None);
        }

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 5 })).Type());
        Assert.Equal(500, await client.BalanceAsync());

        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.IronIngot, 1);
        await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Nails });
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        await client.SendAsync("crafting.get");
        Assert.Equal(10, Count(await client.InventoryAsync(), Items.Nails));
    }

    [Fact]
    public async Task The_trader_buys_workbenches_and_furnaces()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Workbench, 1);
        await GiveAsync(client, Items.Furnace, 1);

        Assert.Equal("market.traded", (await client.SendAsync("market.sell", new { item = Items.Workbench, quantity = 1 })).Type());
        Assert.Equal("market.traded", (await client.SendAsync("market.sell", new { item = Items.Furnace, quantity = 1 })).Type());

        Assert.Equal(11_000, await client.BalanceAsync());
        var inventory = await client.InventoryAsync();
        Assert.Equal(0, Count(inventory, Items.Workbench));
        Assert.Equal(0, Count(inventory, Items.Furnace));
    }

    [Fact]
    public async Task The_trader_sells_a_workbench_for_500_dollars()
    {
        using var client = await PlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 60_000, "test", CancellationToken.None);
        }

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Workbench, quantity = 1 })).Type());
        Assert.Equal(10_000, await client.BalanceAsync());
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Workbench));
    }

    [Fact]
    public async Task The_trader_sells_tools_for_100_dollars_and_an_anvil_for_500_dollars()
    {
        using var client = await PlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 100_000, "test", CancellationToken.None);
        }

        foreach (var tool in new[] { Items.Axe, Items.Shovel, Items.Pickaxe, Items.Hoe })
        {
            Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = tool, quantity = 1 })).Type());
            Assert.Equal(1, Count(await client.InventoryAsync(), tool));
        }

        Assert.Equal(60_000, await client.BalanceAsync());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Anvil, quantity = 1 })).Type());
        Assert.Equal(10_000, await client.BalanceAsync());
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Anvil));
    }

    [Fact]
    public async Task An_anvil_made_at_the_workbench_forges_tools_and_only_tools()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.IronIngot, 30);
        await GiveAsync(client, Items.Stone, 10);
        await GiveAsync(client, Items.Wood, 10);

        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal(Items.Workbench, recipes[Items.Anvil].GetProperty("station").GetString());
        Assert.Equal(Items.Anvil, recipes[Items.Hoe].GetProperty("station").GetString());

        // Tools are forged at an anvil, not at the workbench.
        Assert.Equal("no_object", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Axe })).ErrorCode());
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Anvil })).Type());
        factory.Time.Advance(TimeSpan.FromSeconds(60));
        await client.SendAsync("crafting.get");
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Anvil));

        Assert.Equal("garage", (await client.SendAsync("garage.place", new { kind = Items.Anvil, x = 7, y = 4 })).Type());
        var anvil = await GarageObjectAsync(client, Items.Anvil);
        Assert.Equal("no_object", (await client.SendAsync("crafting.craft", new { workbench = anvil, recipe = Items.Nails })).ErrorCode());
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = anvil, recipe = Items.Axe })).Type());
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = anvil, recipe = Items.Hoe })).Type());
        factory.Time.Advance(TimeSpan.FromSeconds(40));
        await client.SendAsync("crafting.get");
        var inventory = await client.InventoryAsync();
        Assert.Equal(1, Count(inventory, Items.Axe));
        Assert.Equal(1, Count(inventory, Items.Hoe));
        Assert.Equal(30 - 15 - 3 - 2, Count(inventory, Items.IronIngot));
        Assert.Equal(6, Count(inventory, Items.Wood));
    }

    [Fact]
    public async Task Orders_are_made_one_after_another_and_a_cancelled_one_gives_its_ingredients_back()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.Stone, 60);

        await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Furnace });
        await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Furnace });
        var three = (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Furnace })).Payload().GetProperty("queue");
        var ends = three.EnumerateArray().Select(j => j.GetProperty("finishesAt").GetInt64()).ToList();
        Assert.Equal(45_000, ends[1] - ends[0]);
        Assert.Equal(45_000, ends[2] - ends[1]);

        // Calling off the middle one moves the last one up by 45 s.
        var cancelled = (await client.SendAsync("crafting.cancel", new { id = three[1].GetProperty("id").GetInt64() })).Payload().GetProperty("queue");
        Assert.Equal(2, cancelled.GetArrayLength());
        Assert.Equal(ends[1], cancelled[1].GetProperty("finishesAt").GetInt64());
        Assert.Equal(20, Count(await client.InventoryAsync(), Items.Stone));

        factory.Time.Advance(TimeSpan.FromSeconds(90));
        await client.SendAsync("crafting.get");
        Assert.Equal(2, Count(await client.InventoryAsync(), Items.Furnace));
    }

    [Fact]
    public async Task At_most_5_orders_wait_at_the_workbench()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.IronIngot, 6);
        for (var i = 0; i < Workshop.MaxQueue; i++)
        {
            Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Nails })).Type());
        }

        Assert.Equal("crafting_queue_full", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Nails })).ErrorCode());
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.IronIngot));
    }

    private static async Task<string> PlaceFurnaceInGarageAsync(GameClient client)
    {
        var placed = await client.SendAsync("garage.place", new { kind = Items.Furnace, x = 7, y = 4 });
        Assert.Equal("garage", placed.Type());
        return await GarageObjectAsync(client, Items.Furnace);
    }

    [Fact]
    public async Task A_furnace_smelts_5_ores_per_coal_and_waits_for_more_coal()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 6);
        await GiveAsync(client, Items.Coal, 2);
        var furnace = await PlaceFurnaceInGarageAsync(client);

        Assert.Equal("furnace", (await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 6 })).Type());
        var lit = (await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 1 })).Payload();
        Assert.Equal(10, lit.GetProperty("smeltSeconds").GetDouble());
        Assert.NotEqual(JsonValueKind.Null, lit.GetProperty("startedAt").ValueKind);

        factory.Time.Advance(TimeSpan.FromSeconds(25));
        var half = (await client.SendAsync("furnace.get", new { furnace })).Payload();
        Assert.Equal(2, half.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(4, half.GetProperty("input").GetProperty("quantity").GetInt32());
        Assert.Equal(0, half.GetProperty("fuel").GetInt32());
        Assert.Equal(3, half.GetProperty("heat").GetInt32());

        factory.Time.Advance(TimeSpan.FromSeconds(60));
        var cold = (await client.SendAsync("furnace.get", new { furnace })).Payload();
        Assert.Equal(5, cold.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(1, cold.GetProperty("input").GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, cold.GetProperty("startedAt").ValueKind);

        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 1 });
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        var taken = (await client.SendAsync("furnace.take", new { furnace, what = "output" })).Payload();
        Assert.Equal(6, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(6, taken.GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal(JsonValueKind.Null, taken.GetProperty("output").ValueKind);
        Assert.Equal(6, Count(await client.InventoryAsync(), Items.IronIngot));
    }

    [Fact]
    public async Task A_furnace_holds_one_ore_at_a_time_and_takes_only_coal_and_ore()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 2);
        await GiveAsync(client, Items.CopperOre, 2);
        await GiveAsync(client, Items.Wood, 2);
        var furnace = await PlaceFurnaceInGarageAsync(client);

        await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 2 });
        Assert.Equal("furnace_busy", (await client.SendAsync("furnace.load", new { furnace, item = Items.CopperOre, quantity = 1 })).ErrorCode());
        Assert.Equal("not_smeltable", (await client.SendAsync("furnace.load", new { furnace, item = Items.Wood, quantity = 1 })).ErrorCode());

        var back = (await client.SendAsync("furnace.take", new { furnace, what = "input" })).Payload();
        Assert.Equal(JsonValueKind.Null, back.GetProperty("input").ValueKind);
        Assert.Equal("furnace", (await client.SendAsync("furnace.load", new { furnace, item = Items.CopperOre, quantity = 2 })).Type());
        Assert.Equal(2, Count(await client.InventoryAsync(), Items.IronOre));
    }

    [Fact]
    public async Task Picking_a_furnace_up_gives_back_what_is_in_it()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.GoldOre, 3);
        await GiveAsync(client, Items.Coal, 3);
        var furnace = await PlaceFurnaceInGarageAsync(client);
        await client.SendAsync("furnace.load", new { furnace, item = Items.GoldOre, quantity = 3 });
        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 3 });

        factory.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal("garage", (await client.SendAsync("garage.pickup", new { id = furnace })).Type());

        var inventory = await client.InventoryAsync();
        Assert.Equal(1, Count(inventory, Items.Furnace));
        Assert.Equal(1, Count(inventory, Items.GoldIngot));
        Assert.Equal(2, Count(inventory, Items.GoldOre));
        Assert.Equal(2, Count(inventory, Items.Coal));
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 3000;
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

    private async Task<(int X, int Y, int TileX, int TileY)> PlotWithFreeTileAsync(GameClient client)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        for (var i = PlotLayout.Size * 20; i < bytes.Length; i++)
        {
            if (PlotTiles.Clear(bytes, i % PlotLayout.Size, i / PlotLayout.Size))
            {
                return (x, y, i % PlotLayout.Size, i / PlotLayout.Size);
            }
        }

        throw new InvalidOperationException("No free tile.");
    }

    [Fact]
    public async Task A_workbench_put_on_a_plot_crafts_there_and_blocks_its_tile()
    {
        using var client = await PlayerAsync();
        var (x, y, tileX, tileY) = await PlotWithFreeTileAsync(client);
        await GiveAsync(client, Items.Workbench, 1);
        await GiveAsync(client, Items.Stone, 20);
        await GiveAsync(client, Items.Sapling, 1);

        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Workbench, tileX, tileY });
        Assert.Equal("plot.placed", placed.Type());
        var bench = placed.Payload().GetProperty("object").GetProperty("id").GetString();
        Assert.Equal(0, Count(await client.InventoryAsync(), Items.Workbench));

        var objects = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("objects");
        Assert.Equal(bench, objects[0].GetProperty("id").GetString());
        Assert.Equal("blocked", (await client.SendAsync("plot.plantTree", new { x, y, tileX, tileY })).ErrorCode());
        Assert.Equal("blocked", (await client.SendAsync("plot.place", new { x, y, kind = Items.Workbench, tileX, tileY })).ErrorCode());
        Assert.Equal("not_placeable", (await client.SendAsync("plot.place", new { x, y, kind = Items.Stone, tileX = tileX + 1, tileY })).ErrorCode());

        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Furnace })).Type());

        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = bench })).Type());
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Workbench));
        Assert.Equal(0, (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("objects").GetArrayLength());
    }

    [Fact]
    public async Task A_computer_bought_from_the_trader_stands_on_a_plot_and_goes_back_into_the_inventory()
    {
        using var client = await PlayerAsync();
        var (x, y, tileX, tileY) = await PlotWithFreeTileAsync(client);

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Computer, quantity = 1 })).Type());
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Computer, tileX, tileY });
        Assert.Equal("plot.placed", placed.Type());
        var computer = placed.Payload().GetProperty("object");
        Assert.Equal("Komputer", computer.GetProperty("name").GetString());
        Assert.Equal(0, Count(await client.InventoryAsync(), Items.Computer));

        var objects = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("objects");
        Assert.Equal(Items.Computer, objects[0].GetProperty("kind").GetString());
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = computer.GetProperty("id").GetString() })).Type());
        Assert.Equal(1, Count(await client.InventoryAsync(), Items.Computer));
    }

    [Fact]
    public async Task A_furnace_on_a_plot_smelts_too_and_only_its_owner_can_use_it()
    {
        using var client = await PlayerAsync();
        using var other = await PlayerAsync();
        var (x, y, tileX, tileY) = await PlotWithFreeTileAsync(client);
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.TinOre, 1);
        await GiveAsync(client, Items.Coal, 1);
        var furnace = (await client.SendAsync("plot.place", new { x, y, kind = Items.Furnace, tileX, tileY }))
            .Payload().GetProperty("object").GetProperty("id").GetString();

        await client.SendAsync("furnace.load", new { furnace, item = Items.TinOre, quantity = 1 });
        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 1 });
        Assert.Equal("no_object", (await other.SendAsync("furnace.get", new { furnace })).ErrorCode());

        factory.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(Items.TinIngot, (await client.SendAsync("furnace.get", new { furnace })).Payload()
            .GetProperty("output").GetProperty("id").GetString());
    }

    [Fact]
    public void Advancing_a_furnace_burns_coal_only_when_its_heat_runs_out()
    {
        var start = DateTimeOffset.UnixEpoch;
        var s = Smelting.Start(FurnaceState.Empty with { Input = Items.IronOre, InputQuantity = 12, Fuel = 3 }, start);

        var after = Smelting.Advance(s, Smelting.Stone, start + TimeSpan.FromSeconds(61));

        Assert.Equal(6, after.OutputQuantity);
        Assert.Equal(6, after.InputQuantity);
        Assert.Equal(1, after.Fuel);
        Assert.Equal(4, after.Heat);
        Assert.Equal(start + TimeSpan.FromSeconds(60), after.StartedAt);
    }
}
