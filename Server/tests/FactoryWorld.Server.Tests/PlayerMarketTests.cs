using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Trading between players. Tests share one database, so each uses its own item.</summary>
public sealed class PlayerMarketTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private async Task<GameClient> PlayerAsync(long cents = 0, string? item = null, long quantity = 0)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        if (cents > 0)
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        if (item is not null)
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
        }

        return client;
    }

    private static async Task<JsonElement> BoardAsync(GameClient client, string item)
    {
        var reply = await client.SendAsync("market.item", new { item });
        Assert.Equal("market.item", reply.Type());
        return reply.Payload();
    }

    private static long Count(Dictionary<string, long> inventory, string item) => inventory.GetValueOrDefault(item);

    [Fact]
    public async Task Listed_items_leave_the_inventory_and_another_player_buys_them()
    {
        using var seller = await PlayerAsync(item: Items.Wood, quantity: 30);
        using var buyer = await PlayerAsync(cents: 10_000);

        var listed = await seller.SendAsync("market.list", new { item = Items.Wood, quantity = 20, priceCents = 300 });
        Assert.Equal("market.done", listed.Type());
        Assert.Equal(10, Count(await seller.InventoryAsync(), Items.Wood));

        var listing = (await BoardAsync(buyer, Items.Wood)).GetProperty("listings").EnumerateArray().Single();
        Assert.Equal(20, listing.GetProperty("quantity").GetInt64());
        Assert.Equal(300, listing.GetProperty("priceCents").GetInt64());
        Assert.False(listing.GetProperty("mine").GetBoolean());

        var bought = await buyer.SendAsync("market.purchase", new { id = listing.GetProperty("id").GetInt64(), quantity = 5 });

        Assert.Equal("market.done", bought.Type());
        Assert.Equal(10_000 - 1500, bought.Payload().GetProperty("balanceCents").GetInt64());
        Assert.Equal(5, Count(await buyer.InventoryAsync(), Items.Wood));
        // The market keeps 3%: 45 of 1500.
        Assert.Equal(1455, await seller.BalanceAsync());
        Assert.Equal(15, (await BoardAsync(buyer, Items.Wood)).GetProperty("listings")[0].GetProperty("quantity").GetInt64());

        var mine = (await seller.SendAsync("market.mine")).Payload().GetProperty("listings")[0];
        Assert.Equal(5, mine.GetProperty("done").GetInt64());
        Assert.True(mine.GetProperty("open").GetBoolean());
    }

    [Fact]
    public async Task The_market_keeps_3_percent_of_every_sale_rounded_up_so_selling_piece_by_piece_saves_nothing()
    {
        using var seller = await PlayerAsync(item: Items.Clay, quantity: 3);
        using var buyer = await PlayerAsync(cents: 10_000);
        await seller.SendAsync("market.list", new { item = Items.Clay, quantity = 3, priceCents = 10 });
        var id = (await BoardAsync(buyer, Items.Clay)).GetProperty("listings")[0].GetProperty("id").GetInt64();

        await buyer.SendAsync("market.purchase", new { id, quantity = 1 });
        await buyer.SendAsync("market.purchase", new { id, quantity = 1 });
        await buyer.SendAsync("market.purchase", new { id, quantity = 1 });

        Assert.Equal(10_000 - 30, await buyer.BalanceAsync());
        Assert.Equal(3 * 9, await seller.BalanceAsync());
        Assert.Equal(97_000, MarketRules.AfterFee(100_000));
        Assert.Equal(0, MarketRules.AfterFee(1));
    }

    [Fact]
    public async Task Buying_the_rest_closes_the_listing_and_cancelling_returns_unsold_items()
    {
        using var seller = await PlayerAsync(item: Items.Stone, quantity: 10);
        using var buyer = await PlayerAsync(cents: 10_000);
        await seller.SendAsync("market.list", new { item = Items.Stone, quantity = 4, priceCents = 100 });
        await seller.SendAsync("market.list", new { item = Items.Stone, quantity = 6, priceCents = 200 });
        var listings = (await BoardAsync(buyer, Items.Stone)).GetProperty("listings").EnumerateArray().ToList();
        Assert.Equal([100L, 200L], listings.Select(l => l.GetProperty("priceCents").GetInt64()));

        await buyer.SendAsync("market.purchase", new { id = listings[0].GetProperty("id").GetInt64(), quantity = 4 });
        Assert.Equal("market.done", (await seller.SendAsync("market.unlist", new { id = listings[1].GetProperty("id").GetInt64() })).Type());

        Assert.Empty((await BoardAsync(buyer, Items.Stone)).GetProperty("listings").EnumerateArray());
        Assert.Equal(6, Count(await seller.InventoryAsync(), Items.Stone));
        Assert.Equal(388, await seller.BalanceAsync());
        var mine = (await seller.SendAsync("market.mine")).Payload().GetProperty("listings").EnumerateArray().ToList();
        Assert.All(mine, m => Assert.False(m.GetProperty("open").GetBoolean()));
        Assert.Equal("no_offer", (await buyer.SendAsync("market.purchase", new { id = listings[0].GetProperty("id").GetInt64(), quantity = 1 })).ErrorCode());
        Assert.Equal("no_offer", (await seller.SendAsync("market.unlist", new { id = listings[1].GetProperty("id").GetInt64() })).ErrorCode());
    }

    [Fact]
    public async Task Failed_purchases_change_nothing()
    {
        using var seller = await PlayerAsync(cents: 5_000, item: Items.Coal, quantity: 10);
        using var poor = await PlayerAsync(cents: 100);
        await seller.SendAsync("market.list", new { item = Items.Coal, quantity = 10, priceCents = 500 });
        var id = (await BoardAsync(poor, Items.Coal)).GetProperty("listings")[0].GetProperty("id").GetInt64();

        Assert.Equal("not_enough_money", (await poor.SendAsync("market.purchase", new { id, quantity = 1 })).ErrorCode());
        Assert.Equal("not_enough_offered", (await poor.SendAsync("market.purchase", new { id, quantity = 11 })).ErrorCode());
        Assert.Equal("own_offer", (await seller.SendAsync("market.purchase", new { id, quantity = 1 })).ErrorCode());
        Assert.Equal("no_offer", (await poor.SendAsync("market.unlist", new { id })).ErrorCode());

        Assert.Equal(100, await poor.BalanceAsync());
        Assert.Equal(0, Count(await poor.InventoryAsync(), Items.Coal));
        Assert.Equal(10, (await BoardAsync(poor, Items.Coal)).GetProperty("listings")[0].GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task Bad_listings_and_orders_are_refused()
    {
        using var player = await PlayerAsync(cents: 1_000, item: Items.Sand, quantity: 5);

        Assert.Equal("not_enough_items", (await player.SendAsync("market.list", new { item = Items.Sand, quantity = 6, priceCents = 10 })).ErrorCode());
        Assert.Equal("bad_price", (await player.SendAsync("market.list", new { item = Items.Sand, quantity = 1, priceCents = 0 })).ErrorCode());
        Assert.Equal("bad_quantity", (await player.SendAsync("market.list", new { item = Items.Sand, quantity = 0, priceCents = 10 })).ErrorCode());
        Assert.Equal("unknown_item", (await player.SendAsync("market.list", new { item = "gold", quantity = 1, priceCents = 10 })).ErrorCode());
        Assert.Equal("not_enough_money", (await player.SendAsync("market.order", new { item = Items.Sand, quantity = 11, priceCents = 100 })).ErrorCode());

        Assert.Equal(1_000, await player.BalanceAsync());
        Assert.Equal(5, Count(await player.InventoryAsync(), Items.Sand));
    }

    [Fact]
    public async Task A_player_has_at_most_20_open_listings()
    {
        using var player = await PlayerAsync(item: Items.Wheat, quantity: 30);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal("market.done", (await player.SendAsync("market.list", new { item = Items.Wheat, quantity = 1, priceCents = 100 })).Type());
        }

        Assert.Equal("too_many_offers", (await player.SendAsync("market.list", new { item = Items.Wheat, quantity = 1, priceCents = 100 })).ErrorCode());
        Assert.Equal(10, Count(await player.InventoryAsync(), Items.Wheat));
    }

    [Fact]
    public async Task A_buy_order_holds_the_money_and_pays_whoever_fills_it()
    {
        using var buyer = await PlayerAsync(cents: 10_000);
        using var seller = await PlayerAsync(item: Items.IronOre, quantity: 8);

        var ordered = await buyer.SendAsync("market.order", new { item = Items.IronOre, quantity = 10, priceCents = 700 });
        Assert.Equal(3_000, ordered.Payload().GetProperty("balanceCents").GetInt64());
        var order = (await BoardAsync(seller, Items.IronOre)).GetProperty("orders").EnumerateArray().Single();
        var id = order.GetProperty("id").GetInt64();

        Assert.Equal("not_enough_items", (await seller.SendAsync("market.fill", new { id, quantity = 9 })).ErrorCode());
        Assert.Equal("own_offer", (await buyer.SendAsync("market.fill", new { id, quantity = 1 })).ErrorCode());
        var filled = await seller.SendAsync("market.fill", new { id, quantity = 8 });

        Assert.Equal("market.done", filled.Type());
        Assert.Equal(5_600 - 168, filled.Payload().GetProperty("balanceCents").GetInt64());
        Assert.Equal(0, Count(await seller.InventoryAsync(), Items.IronOre));
        Assert.Equal(8, Count(await buyer.InventoryAsync(), Items.IronOre));

        // Cancelling refunds the 2 pieces nobody delivered.
        Assert.Equal(3_000 + 1_400, (await buyer.SendAsync("market.cancelOrder", new { id })).Payload().GetProperty("balanceCents").GetInt64());
        Assert.Empty((await BoardAsync(seller, Items.IronOre)).GetProperty("orders").EnumerateArray());
        var mine = (await buyer.SendAsync("market.mine")).Payload().GetProperty("orders")[0];
        Assert.Equal(8, mine.GetProperty("done").GetInt64());
        Assert.False(mine.GetProperty("open").GetBoolean());
    }

    [Fact]
    public async Task Overview_shows_best_prices_and_the_quick_sale_floor()
    {
        using var seller = await PlayerAsync(cents: 10_000, item: Items.Corn, quantity: 10);
        await seller.SendAsync("market.list", new { item = Items.Corn, quantity = 3, priceCents = 900 });
        await seller.SendAsync("market.list", new { item = Items.Corn, quantity = 4, priceCents = 800 });
        await seller.SendAsync("market.order", new { item = Items.Corn, quantity = 5, priceCents = 300 });
        await seller.SendAsync("market.order", new { item = Items.Corn, quantity = 2, priceCents = 400 });

        var items = (await seller.SendAsync("market.overview")).Payload().GetProperty("items").EnumerateArray().ToList();
        var corn = items.Single(i => i.GetProperty("id").GetString() == Items.Corn);

        // A new player sees everything but what they still have to research.
        Assert.Equal(Items.Names.Keys.Count(id => !TechTree.ByItem.ContainsKey(id)), items.Count);
        Assert.Equal(800, corn.GetProperty("lowestCents").GetInt64());
        Assert.Equal(7, corn.GetProperty("listed").GetInt64());
        Assert.Equal(400, corn.GetProperty("highestCents").GetInt64());
        Assert.Equal(7, corn.GetProperty("wanted").GetInt64());
        Assert.Equal(220, corn.GetProperty("quickSaleCents").GetInt64());
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("id").GetString() == Items.Trash).GetProperty("quickSaleCents").ValueKind);
    }

    [Fact]
    public async Task The_overview_shows_a_trade_at_once_though_it_was_just_looked_at()
    {
        using var seller = await PlayerAsync(item: Items.Egg, quantity: 10);
        using var buyer = await PlayerAsync(cents: 10_000);
        async Task<JsonElement> EggAsync() => (await buyer.SendAsync("market.overview")).Payload().GetProperty("items")
            .EnumerateArray().Single(i => i.GetProperty("id").GetString() == Items.Egg);
        await EggAsync();

        await seller.SendAsync("market.list", new { item = Items.Egg, quantity = 6, priceCents = 500 });
        var listed = await EggAsync();
        Assert.Equal(6, listed.GetProperty("listed").GetInt64());
        Assert.Equal(500, listed.GetProperty("lowestCents").GetInt64());

        var id = (await BoardAsync(buyer, Items.Egg)).GetProperty("listings")[0].GetProperty("id").GetInt64();
        await buyer.SendAsync("market.purchase", new { id, quantity = 6 });
        var sold = await EggAsync();
        Assert.Equal(0, sold.GetProperty("listed").GetInt64());
        Assert.Equal(JsonValueKind.Null, sold.GetProperty("lowestCents").ValueKind);
    }

    [Fact]
    public async Task My_offers_show_an_old_open_listing_behind_many_closed_ones()
    {
        using var player = await PlayerAsync(item: Items.Sunflower, quantity: 40);
        await player.SendAsync("market.list", new { item = Items.Sunflower, quantity = 10, priceCents = 700 });
        for (var i = 0; i < 25; i++)
        {
            await player.SendAsync("market.list", new { item = Items.Sunflower, quantity = 1, priceCents = 100 });
            var newest = (await player.SendAsync("market.mine")).Payload().GetProperty("listings")[0].GetProperty("id").GetInt64();
            Assert.Equal("market.done", (await player.SendAsync("market.unlist", new { id = newest })).Type());
        }

        var listings = (await player.SendAsync("market.mine")).Payload().GetProperty("listings").EnumerateArray().ToList();

        Assert.Equal(26, listings.Count);
        // Newest first: the open one, listed before all the others, comes last.
        Assert.True(listings[^1].GetProperty("open").GetBoolean());
        Assert.Equal(10, listings[^1].GetProperty("quantity").GetInt64());
        Assert.All(listings[..^1], l => Assert.False(l.GetProperty("open").GetBoolean()));
    }

    [Fact]
    public async Task Quick_sale_pays_the_floor_price_at_once()
    {
        using var player = await PlayerAsync(item: Items.Carrot, quantity: 10);

        var reply = await player.SendAsync("market.sell", new { item = Items.Carrot, quantity = 10 });

        Assert.Equal("market.traded", reply.Type());
        Assert.Equal(900, await player.BalanceAsync());
    }
}
