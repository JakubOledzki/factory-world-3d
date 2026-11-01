using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Stacks: a tile of the inventory holds up to <see cref="Stacks.Of"/> pieces of one item.</summary>
public sealed class StackTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public void Every_item_has_one_of_the_five_stacks()
    {
        Assert.Equal(Items.Names.Count, Stacks.Sizes.Count);
        Assert.All(Stacks.Sizes.Values, size => Assert.Contains(size, new[] { Stacks.Bulk, Stacks.Material, Stacks.Part, Stacks.Machine, Stacks.Single }));
        Assert.Equal(200, Stacks.Of(Items.Stone));
        Assert.Equal(200, Stacks.Of(Items.Sapling));
        Assert.Equal(200, Stacks.Of(Items.CrudeOil));
        Assert.Equal(100, Stacks.Of(Items.IronIngot));
        Assert.Equal(100, Stacks.Of(Items.Belt));
        Assert.Equal(50, Stacks.Of(Items.Inserter));
        Assert.Equal(50, Stacks.Of(Items.Bread));
        Assert.Equal(10, Stacks.Of(Items.Assembler));
        Assert.Equal(1, Stacks.Of(Items.Axe));
        Assert.Equal(1, Stacks.Of(Items.SchoolBackpack));
        Assert.Equal(1, Stacks.Of(Items.RailPort));
    }

    [Fact]
    public void Tiles_hold_whole_stacks_and_one_more_for_the_rest()
    {
        Assert.Equal(0, Stacks.Tiles(Items.Stone, 0));
        Assert.Equal(1, Stacks.Tiles(Items.Stone, 200));
        Assert.Equal(3, Stacks.Tiles(Items.Stone, 450));
        // 20 tiles hold 4000 stone and no more.
        Assert.Equal(4000, InventoryRules.Room(Items.InventorySlots, [], Items.Stone));
        Assert.Equal(150, InventoryRules.Room(20, [(Items.Stone, 3850)], Items.Stone));
        // Wood gets the tiles the stone leaves: 450 stone lie on 3 of them.
        Assert.Equal(17 * 200, InventoryRules.Room(20, [(Items.Stone, 450)], Items.Wood));
        // With things past the tiles nothing more comes in.
        Assert.Equal(0, InventoryRules.Room(20, [(Items.Stone, 4100)], Items.Wood));
        Assert.Equal(0, InventoryRules.Room(20, [(Items.Stone, 4100)], Items.Stone));
    }

    [Fact]
    public void Items_keep_their_tiles_and_get_the_first_free_ones()
    {
        var wood = new Stack { ItemId = Items.Wood, Quantity = 10, Tiles = [0] };
        var stone = new Stack { ItemId = Items.Stone, Quantity = 450, Tiles = [5] };
        var coal = new Stack { ItemId = Items.Coal, Quantity = 10 };

        InventoryRules.Lay(20, [stone, wood, coal]);

        Assert.Equal([0], wood.Tiles);
        Assert.Equal([5, 1, 2], stone.Tiles);
        Assert.Equal([3], coal.Tiles);

        // Fewer pieces give back the last tiles; tiles off the grid (a backpack taken off) are given anew.
        stone.Quantity = 250;
        wood.Tiles = [30];
        InventoryRules.Lay(20, [stone, wood, coal]);

        Assert.Equal([5, 1], stone.Tiles);
        Assert.Equal([3], coal.Tiles);
        Assert.Equal([0], wood.Tiles);
    }

    [Fact]
    public void What_has_no_tile_lies_past_the_grid()
    {
        var stone = new Stack { ItemId = Items.Stone, Quantity = 5000 };
        var wood = new Stack { ItemId = Items.Wood, Quantity = 5, Tiles = [7] };

        InventoryRules.Lay(20, [stone, wood]);

        Assert.Equal([7], wood.Tiles);
        Assert.Equal(19, stone.Tiles.Length);
        Assert.Equal(5000 - 19 * 200, InventoryRules.Over(stone));
        Assert.Equal(0, InventoryRules.Over(wood));
    }

    [Fact]
    public async Task A_big_supply_lies_on_several_tiles_full_stacks_first()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Stone, 450);

        var stone = Item(await client.SendAsync("inventory.get"), Items.Stone);

        Assert.Equal(450, stone.GetProperty("quantity").GetInt64());
        Assert.Equal(200, stone.GetProperty("stack").GetInt32());
        Assert.Equal([0, 1, 2], Tiles(stone));
        Assert.Equal(0, stone.GetProperty("over").GetInt64());
        Assert.Equal(0, stone.GetProperty("slot").GetInt32());
    }

    [Fact]
    public async Task Stacks_move_by_tile_and_two_of_one_item_swap()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Stone, 450);
        await GiveAsync(client, Items.Wood, 5);

        // The 50 stone on tile 2 go onto tile 0 and the full stack comes to tile 2.
        var swapped = await client.SendAsync("inventory.move", new { from = 2, to = 0 });
        Assert.Equal([2, 1, 0], Tiles(Item(swapped, Items.Stone)));

        var moved = await client.SendAsync("inventory.move", new { from = 1, to = 7 });
        Assert.Equal([2, 7, 0], Tiles(Item(moved, Items.Stone)));

        // Onto another item: they swap.
        var wood = Tiles(Item(moved, Items.Wood))[0];
        var crossed = await client.SendAsync("inventory.move", new { from = wood, to = 7 });
        Assert.Equal([2, wood, 0], Tiles(Item(crossed, Items.Stone)));
        Assert.Equal([7], Tiles(Item(crossed, Items.Wood)));
        Assert.Equal([2, wood, 0], Tiles(Item(await client.SendAsync("inventory.get"), Items.Stone)));

        Assert.Equal("not_enough_items", (await client.SendAsync("inventory.move", new { from = 12, to = 3 })).ErrorCode());
        Assert.Equal("bad_slot", (await client.SendAsync("inventory.move", new { from = 0, to = Items.InventorySlots })).ErrorCode());
    }

    [Fact]
    public async Task Things_given_back_past_the_tiles_stop_new_ones_until_there_is_room()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Stone, 4100);

        var stone = Item(await client.SendAsync("inventory.get"), Items.Stone);
        Assert.Equal(Items.InventorySlots, Tiles(stone).Length);
        Assert.Equal(100, stone.GetProperty("over").GetInt64());

        await using var scope = factory.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        Assert.Equal(0, await inventory.RoomAsync(client.AccountId, Items.Wood, CancellationToken.None));
        var full = await Assert.ThrowsAsync<GameException>(() => inventory.ReceiveAsync(client.AccountId, Items.Wood, 1, CancellationToken.None));
        Assert.Equal("inventory_full", full.Code);

        // Sold down to 19 stacks, the last tile is free again.
        Assert.Equal("market.traded", (await client.SendAsync("market.sell", new { item = Items.Stone, quantity = 300 })).Type());
        Assert.Equal(200, await inventory.RoomAsync(client.AccountId, Items.Wood, CancellationToken.None));
        Assert.Equal(0, Item(await client.SendAsync("inventory.get"), Items.Stone).GetProperty("over").GetInt64());
    }

    [Fact]
    public async Task Without_a_pallet_a_purchase_bigger_than_the_room_is_refused_with_what_fits()
    {
        using var client = await PlayerAsync();
        await MoneyAsync(client, 100_000);
        await GiveAsync(client, Items.Stone, (Items.InventorySlots - 1) * 200);
        await GiveAsync(client, Items.Nails, 70);

        var refused = await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 50 });

        Assert.Equal("inventory_full", refused.ErrorCode());
        Assert.Equal(30, refused.Payload().GetProperty("details").GetProperty("room").GetInt64());
        Assert.Equal(70, (await client.InventoryAsync())[Items.Nails]);
        Assert.Equal(100_000, await client.BalanceAsync());

        var bought = await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 30 });
        Assert.Equal("market.traded", bought.Type());
        Assert.False(bought.Payload().GetProperty("toPallet").GetBoolean());
        Assert.Equal(100, (await client.InventoryAsync())[Items.Nails]);
    }

    [Fact]
    public async Task What_does_not_fit_of_a_purchase_waits_on_the_pallet_and_comes_off_as_far_as_it_fits()
    {
        using var client = await PlayerAsync();
        var pallet = (await client.SendAsync("garage.get")).Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == Items.Pallet).GetProperty("id").GetGuid();
        await MoneyAsync(client, 100_000);
        await FillAsync(client, leave: 1);
        await GiveAsync(client, Items.Nails, 70);

        var bought = (await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 50 })).Payload();

        Assert.True(bought.GetProperty("toPallet").GetBoolean());
        Assert.Equal(20, bought.GetProperty("palletQuantity").GetInt64());
        Assert.Equal(100, (await client.InventoryAsync())[Items.Nails]);
        Assert.Equal(20, Waiting(await client.SendAsync("garage.storage", new { id = pallet }), Items.Nails));

        // Nothing fits: nothing comes off. With room for 10, 10 come off and 10 stay.
        Assert.Equal("inventory_full", (await client.SendAsync("garage.storage.take", new { id = pallet, item = Items.Nails, quantity = 0 })).ErrorCode());
        await client.SendAsync("market.sell", new { item = Items.Nails, quantity = 10 });
        var taken = await client.SendAsync("garage.storage.take", new { id = pallet, item = Items.Nails, quantity = 0 });
        Assert.Equal(10, Waiting(taken, Items.Nails));
        Assert.Equal(100, (await client.InventoryAsync())[Items.Nails]);
    }

    [Fact]
    public async Task Ingots_that_do_not_fit_stay_in_the_furnace()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 30);
        await GiveAsync(client, Items.Coal, 6);
        Assert.Equal("garage", (await client.SendAsync("garage.place", new { kind = Items.Furnace, x = 7, y = 4 })).Type());
        var furnace = (await client.SendAsync("garage.get")).Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == Items.Furnace).GetProperty("id").GetString()!;
        await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 30 });
        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 6 });
        factory.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(30, (await client.SendAsync("furnace.get", new { furnace })).Payload().GetProperty("output").GetProperty("quantity").GetInt32());
        await FillAsync(client, leave: 1);
        await GiveAsync(client, Items.IronIngot, 90);

        var taken = (await client.SendAsync("furnace.take", new { furnace, what = "output" })).Payload();

        Assert.Equal(10, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(10, taken.GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal(20, taken.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(100, (await client.InventoryAsync())[Items.IronIngot]);
        Assert.Equal("inventory_full", (await client.SendAsync("furnace.take", new { furnace, what = "output" })).ErrorCode());
    }

    [Fact]
    public async Task The_game_tells_the_stacks_and_the_tiles_a_research_cost_takes()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var stacks = (await client.SendAsync("inventory.stacks")).Payload().GetProperty("stacks");
        Assert.Equal(200, stacks.GetProperty(Items.Stone).GetInt32());
        Assert.Equal(Items.Names.Count, stacks.EnumerateObject().Count());

        var compendium = (await client.SendAsync("compendium.get")).Payload().GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == Items.Assembler);
        Assert.Equal(10, compendium.GetProperty("stack").GetInt32());

        // 2000 steel ingots, 5000 crude oil and 1000 beams: 20 + 25 + 10 tiles.
        var offshore = (await client.SendAsync("research.get")).Payload().GetProperty("technologies").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == "offshore_drilling");
        Assert.Equal(55, offshore.GetProperty("costTiles").GetInt32());
    }

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterAsync();
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private async Task MoneyAsync(GameClient client, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<IWallet>();
        var balance = await wallet.BalanceAsync(client.AccountId, CancellationToken.None);
        if (balance > cents)
        {
            await wallet.DebitAsync(client.AccountId, balance - cents, "test", CancellationToken.None);
        }
        else if (balance < cents)
        {
            await wallet.CreditAsync(client.AccountId, cents - balance, "test", CancellationToken.None);
        }
    }

    /// <summary>Fills every free tile but <paramref name="leave"/> with full stacks of stone.</summary>
    private async Task FillAsync(GameClient client, int leave)
    {
        var inventory = (await client.SendAsync("inventory.get")).Payload();
        var used = inventory.GetProperty("items").EnumerateArray().Sum(i => i.GetProperty("tiles").GetArrayLength());
        var free = inventory.GetProperty("slots").GetInt32() - used;
        Assert.DoesNotContain(inventory.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.Stone);
        await GiveAsync(client, Items.Stone, (free - leave) * 200L);
    }

    private static JsonElement Item(JsonElement reply, string item)
    {
        Assert.Equal("inventory", reply.Type());
        return reply.Payload().GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == item);
    }

    private static int[] Tiles(JsonElement item) => item.GetProperty("tiles").EnumerateArray().Select(t => t.GetInt32()).ToArray();

    private static long Waiting(JsonElement storage, string item)
    {
        Assert.Equal("storage", storage.Type());
        return storage.Payload().GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() == item).Sum(i => i.GetProperty("quantity").GetInt64());
    }
}
