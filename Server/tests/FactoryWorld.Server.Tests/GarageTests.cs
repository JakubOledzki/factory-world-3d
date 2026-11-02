using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Recycling;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Puts a bought sorting machine in a player's garage.</summary>
public static class SortingMachine
{
    /// <summary>The machine's id.</summary>
    public static async Task<string> InstallAsync(ServerFactory factory, GameClient client)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 100_000, "test", CancellationToken.None);
        }

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.SortingMachine, quantity = 1 })).Type());
        var garage = await client.SendAsync("garage.place", new { kind = Items.SortingMachine, x = 6, y = 3 });
        Assert.Equal("garage", garage.Type());
        return garage.Payload().GetProperty("objects").EnumerateArray()
            .Single(o => o.GetProperty("kind").GetString() == Items.SortingMachine).GetProperty("id").GetString()!;
    }

    /// <summary>Loads trash into the machine, lets it sort and takes everything out into the inventory.</summary>
    public static async Task SortAsync(ServerFactory factory, GameClient client, string sorter, int count)
    {
        Assert.Equal("sorter", (await client.SendAsync("sorter.load", new { sorter, quantity = count })).Type());
        factory.Time.Advance(RecyclingRules.SortTime * count);
        Assert.Equal("sorter", (await client.SendAsync("sorter.take", new { sorter })).Type());
    }
}

/// <summary>Sorts one bag of trash by hand at the bin, every piece into its bin; the garage reply after the last.</summary>
public static class HandSorting
{
    public static async Task<JsonElement> SortBagAsync(GameClient client)
    {
        var garage = await client.SendAsync("recycling.sort", new { count = 1 });
        Assert.Equal("garage", garage.Type());
        foreach (var piece in garage.Payload().GetProperty("round").GetProperty("pieces").EnumerateArray().ToList())
        {
            var bin = WasteSorting.ById[piece.GetProperty("kind").GetString()!].Bin;
            garage = await client.SendAsync("recycling.put", new { piece = piece.GetProperty("index").GetInt32(), bin });
        }

        return garage;
    }
}

public sealed class GarageTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly string[] Waste = ["waste_paper", "waste_plastic", "waste_aluminium", "waste_glass", "waste_copper"];

    [Fact]
    public async Task New_garage_has_50_trash_and_no_money()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var garage = await client.SendAsync("garage.get");

        Assert.Equal("garage", garage.Type());
        Assert.Equal(50, garage.Payload().GetProperty("trash").GetInt64());
        Assert.Equal(0, garage.Payload().GetProperty("queue").GetArrayLength());
        Assert.Equal(0, await client.BalanceAsync());
        Assert.Equal(50, (await client.InventoryAsync())["trash"]);
    }

    [Fact]
    public async Task Starting_trash_is_given_only_once()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        await client.SendAsync("garage.get");
        var again = await client.SendAsync("garage.get");

        Assert.Equal(50, again.Payload().GetProperty("trash").GetInt64());
    }

    [Fact]
    public async Task A_bag_holds_five_pieces_for_the_three_bins_and_gives_waste_and_xp_once_all_are_in()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var opened = (await client.SendAsync("recycling.sort", new { count = 1 })).Payload();
        Assert.Equal(49, opened.GetProperty("trash").GetInt64());
        Assert.Equal(["metal", "plastic", "glass"], opened.GetProperty("bins").EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        var pieces = opened.GetProperty("round").GetProperty("pieces").EnumerateArray().ToList();
        Assert.Equal(WasteSorting.Pieces, pieces.Count);
        Assert.True(pieces.Select(p => WasteSorting.ById[p.GetProperty("kind").GetString()!].Bin).Distinct().Count() > 1);

        // Every piece but the last: no reward yet.
        JsonElement garage = default;
        foreach (var piece in pieces)
        {
            Assert.Equal(0, garage.ValueKind == JsonValueKind.Undefined ? 0 : garage.GetProperty("results").GetArrayLength());
            garage = (await client.SendAsync("recycling.put", new { piece = piece.GetProperty("index").GetInt32(), bin = BinOf(piece) })).Payload();
            Assert.True(garage.GetProperty("put").GetProperty("correct").GetBoolean());
        }

        Assert.Equal(JsonValueKind.Null, garage.GetProperty("round").ValueKind);
        Assert.Equal(1, garage.GetProperty("results").GetArrayLength());
        Assert.Equal(RecyclingRules.XpPerSort, garage.GetProperty("xp").GetProperty("xp").GetInt64());
        var inventory = await client.InventoryAsync();
        Assert.Equal(1, Waste.Sum(w => inventory.GetValueOrDefault(w)));
        Assert.Equal(49, inventory["trash"]);
    }

    [Fact]
    public async Task A_piece_in_the_wrong_bin_comes_back_and_the_server_checks_every_piece()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        Assert.Equal("no_bag", (await client.SendAsync("recycling.put", new { piece = 0, bin = "metal" })).ErrorCode());

        var pieces = (await client.SendAsync("recycling.sort", new { count = 1 })).Payload().GetProperty("round").GetProperty("pieces").EnumerateArray().ToList();
        var first = pieces[0];
        var wrong = WasteSorting.Bins.First(b => b.Id != BinOf(first)).Id;
        var back = (await client.SendAsync("recycling.put", new { piece = 0, bin = wrong })).Payload();
        Assert.False(back.GetProperty("put").GetProperty("correct").GetBoolean());
        Assert.Equal(WasteSorting.Pieces, back.GetProperty("round").GetProperty("left").GetInt32());
        Assert.False(back.GetProperty("round").GetProperty("pieces")[0].GetProperty("sorted").GetBoolean());

        Assert.Equal("bad_bin", (await client.SendAsync("recycling.put", new { piece = 0, bin = "paper" })).ErrorCode());
        Assert.Equal("bad_piece", (await client.SendAsync("recycling.put", new { piece = WasteSorting.Pieces, bin = "metal" })).ErrorCode());
        Assert.Equal("garage", (await client.SendAsync("recycling.put", new { piece = 0, bin = BinOf(first) })).Type());
        // A piece in its bin stays there; it cannot be put in twice.
        Assert.Equal("bad_piece", (await client.SendAsync("recycling.put", new { piece = 0, bin = BinOf(first) })).ErrorCode());
        Assert.Equal(0, (await client.SendAsync("garage.get")).Payload().GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task By_hand_one_bag_is_sorted_at_a_time_and_walking_away_gives_it_back()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var garage = await client.SendAsync("recycling.sort", new { count = 1 });
        Assert.Equal(1, garage.Payload().GetProperty("maxQueue").GetInt32());
        Assert.Equal("queue_full", (await client.SendAsync("recycling.sort", new { count = 1 })).ErrorCode());
        Assert.Equal("bad_quantity", (await client.SendAsync("recycling.sort", new { count = 5 })).ErrorCode());

        // Half sorted, then walked away: the bag goes back whole, and nothing is given.
        var first = garage.Payload().GetProperty("round").GetProperty("pieces")[0];
        await client.SendAsync("recycling.put", new { piece = 0, bin = BinOf(first) });
        var stopped = (await client.SendAsync("recycling.stop")).Payload();
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("round").ValueKind);
        Assert.Equal(50, stopped.GetProperty("trash").GetInt64());
        Assert.Equal(0, stopped.GetProperty("results").GetArrayLength());

        // The open bag is still there when the garage is looked at again.
        await client.SendAsync("recycling.sort", new { count = 1 });
        Assert.Equal(WasteSorting.Pieces, (await client.SendAsync("garage.get")).Payload().GetProperty("round").GetProperty("left").GetInt32());
    }

    [Fact]
    public async Task The_sorting_machine_does_not_sort_at_the_bin()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await SortingMachine.InstallAsync(factory, client);

        Assert.Equal("garage", (await client.SendAsync("recycling.sort", new { count = 1 })).Type());
        var stopped = await client.SendAsync("recycling.stop");
        Assert.Equal(50, stopped.Payload().GetProperty("trash").GetInt64());
    }

    private static string BinOf(JsonElement piece) => WasteSorting.ById[piece.GetProperty("kind").GetString()!].Bin;

    [Fact]
    public async Task The_sorting_machine_sorts_its_own_queue_and_keeps_what_it_sorted()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var sorter = await SortingMachine.InstallAsync(factory, client);

        var loaded = (await client.SendAsync("sorter.load", new { sorter, quantity = 3 })).Payload();
        Assert.Equal(3, loaded.GetProperty("queued").GetInt32());
        Assert.Equal(50, loaded.GetProperty("maxQueue").GetInt32());
        Assert.Equal(50, loaded.GetProperty("maxStored").GetInt32());
        Assert.Equal(47, (await client.InventoryAsync())["trash"]);

        factory.Time.Advance(TimeSpan.FromSeconds(25));
        var state = (await client.SendAsync("sorter.get", new { sorter })).Payload();
        Assert.Equal(1, state.GetProperty("queued").GetInt32());
        Assert.Equal(2, state.GetProperty("storedCount").GetInt32());
        // Nothing goes to the inventory, or to the bin's results, until it is taken out.
        var before = await client.InventoryAsync();
        Assert.Equal(0, Waste.Sum(w => before.GetValueOrDefault(w)));
        Assert.Equal(0, (await client.SendAsync("garage.get")).Payload().GetProperty("results").GetArrayLength());

        factory.Time.Advance(TimeSpan.FromSeconds(5));
        var taken = (await client.SendAsync("sorter.take", new { sorter })).Payload();
        Assert.Equal(0, taken.GetProperty("storedCount").GetInt32());
        Assert.Equal(3, taken.GetProperty("taken").EnumerateArray().Sum(t => t.GetProperty("quantity").GetInt32()));
        Assert.Equal(15, taken.GetProperty("xp").GetProperty("xp").GetInt64());
        var inventory = await client.InventoryAsync();
        Assert.Equal(3, Waste.Sum(w => inventory.GetValueOrDefault(w)));
        Assert.Equal(47, inventory["trash"]);
    }

    [Fact]
    public async Task The_sorting_machine_queues_at_most_50_trash()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var sorter = await SortingMachine.InstallAsync(factory, client);

        Assert.Equal("sorter", (await client.SendAsync("sorter.load", new { sorter, quantity = 40 })).Type());
        Assert.Equal("sorter_full", (await client.SendAsync("sorter.load", new { sorter, quantity = 11 })).ErrorCode());
        Assert.Equal("sorter", (await client.SendAsync("sorter.load", new { sorter, quantity = 10 })).Type());
        Assert.Equal("bad_quantity", (await client.SendAsync("sorter.load", new { sorter, quantity = 0 })).ErrorCode());
        Assert.Equal("sorter_full", (await client.SendAsync("sorter.load", new { sorter, quantity = 1 })).ErrorCode());

        // Unsorted trash can be taken back.
        var back = (await client.SendAsync("sorter.take", new { sorter, item = "trash" })).Payload();
        Assert.Equal(0, back.GetProperty("queued").GetInt32());
        Assert.Equal(50, (await client.InventoryAsync())["trash"]);
    }

    [Fact]
    public async Task A_full_sorting_machine_stops_until_something_is_taken_out()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var sorter = await SortingMachine.InstallAsync(factory, client);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, Items.Trash, 10, CancellationToken.None);
        }

        await client.SendAsync("sorter.load", new { sorter, quantity = 50 });
        factory.Time.Advance(TimeSpan.FromSeconds(500));
        var full = (await client.SendAsync("sorter.get", new { sorter })).Payload();
        Assert.Equal(50, full.GetProperty("storedCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, full.GetProperty("startedAt").ValueKind);
        await client.SendAsync("sorter.load", new { sorter, quantity = 10 });

        // A full machine sorts nothing more however long it waits.
        factory.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(10, (await client.SendAsync("sorter.get", new { sorter })).Payload().GetProperty("queued").GetInt32());

        var item = full.GetProperty("stored")[0].GetProperty("id").GetString();
        var taken = (await client.SendAsync("sorter.take", new { sorter, item })).Payload();
        var room = 50 - taken.GetProperty("storedCount").GetInt32();
        Assert.True(room > 0);
        factory.Time.Advance(TimeSpan.FromSeconds(100));
        var after = (await client.SendAsync("sorter.get", new { sorter })).Payload();
        Assert.Equal(Math.Min(50, 50 - room + 10), after.GetProperty("storedCount").GetInt32());
        Assert.Equal(Math.Max(0, 10 - room), after.GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task Picking_up_the_sorting_machine_empties_it_into_the_inventory()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var sorter = await SortingMachine.InstallAsync(factory, client);

        await client.SendAsync("sorter.load", new { sorter, quantity = 5 });
        factory.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal("garage", (await client.SendAsync("garage.pickup", new { id = sorter })).Type());

        var inventory = await client.InventoryAsync();
        Assert.Equal(2, Waste.Sum(w => inventory.GetValueOrDefault(w)));
        Assert.Equal(48, inventory["trash"]);
        Assert.Equal(1, inventory[Items.SortingMachine]);
        Assert.Equal("no_sorter", (await client.SendAsync("sorter.get", new { sorter })).ErrorCode());
    }

    [Fact]
    public async Task Cannot_sort_without_trash()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var sorter = await SortingMachine.InstallAsync(factory, client);
        await SortingMachine.SortAsync(factory, client, sorter, 50);

        Assert.Equal("not_enough_items", (await client.SendAsync("recycling.sort", new { count = 1 })).ErrorCode());
        Assert.Equal("not_enough_items", (await client.SendAsync("sorter.load", new { sorter, quantity = 1 })).ErrorCode());
    }

    [Fact]
    public void Drop_chances_match_the_design()
    {
        var counts = Enumerable.Range(0, 100).GroupBy(RecyclingRules.DropForRoll).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(30, counts["waste_paper"]);
        Assert.Equal(25, counts["waste_plastic"]);
        Assert.Equal(20, counts["waste_aluminium"]);
        Assert.Equal(15, counts["waste_glass"]);
        Assert.Equal(10, counts["waste_copper"]);
    }
}

public sealed class MarketTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly Dictionary<string, long> SellPrices = new()
    {
        ["waste_paper"] = 100, ["waste_plastic"] = 200, ["waste_aluminium"] = 600, ["waste_glass"] = 200, ["waste_copper"] = 1500,
    };

    [Fact]
    public async Task Selling_sorted_waste_pays_npc_prices()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        // Its rewards for sorting and selling would add to the balance.
        await client.SendAsync("tutorial.skip");
        var sorter = await SortingMachine.InstallAsync(factory, client);
        await SortingMachine.SortAsync(factory, client, sorter, 5);

        long expected = 0;
        foreach (var (item, quantity) in (await client.InventoryAsync()).Where(i => i.Key != "trash"))
        {
            var reply = await client.SendAsync("market.sell", new { item, quantity });
            Assert.Equal("market.traded", reply.Type());
            expected += SellPrices[item] * quantity;
        }

        Assert.Equal(expected, await client.BalanceAsync());
        Assert.Equal(["trash"], (await client.InventoryAsync()).Keys);
    }

    [Fact]
    public async Task Buying_trash_costs_1_dollar_each()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var sorter = await SortingMachine.InstallAsync(factory, client);
        await SortingMachine.SortAsync(factory, client, sorter, 10);
        foreach (var (item, quantity) in (await client.InventoryAsync()).Where(i => i.Key != "trash"))
        {
            await client.SendAsync("market.sell", new { item, quantity });
        }

        // 10 sortings earn at least 10 × 1 $, enough for 10 trash.
        var before = await client.BalanceAsync();

        var reply = await client.SendAsync("market.buy", new { item = "trash", quantity = before / 100 });

        Assert.Equal("market.traded", reply.Type());
        Assert.Equal(before % 100, await client.BalanceAsync());
        Assert.Equal(40 + before / 100, (await client.InventoryAsync())["trash"]);
    }

    [Fact]
    public async Task Failed_trades_change_nothing()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await client.SendAsync("garage.get");

        Assert.Equal("not_enough_money", (await client.SendAsync("market.buy", new { item = "trash", quantity = 1 })).ErrorCode());
        Assert.Equal("not_enough_items", (await client.SendAsync("market.sell", new { item = "waste_copper", quantity = 1 })).ErrorCode());
        Assert.Equal("not_traded", (await client.SendAsync("market.sell", new { item = "trash", quantity = 1 })).ErrorCode());
        Assert.Equal("not_traded", (await client.SendAsync("market.buy", new { item = "furniture_shelf", quantity = 1 })).ErrorCode());
        Assert.Equal("bad_quantity", (await client.SendAsync("market.buy", new { item = "trash", quantity = 0 })).ErrorCode());

        Assert.Equal(0, await client.BalanceAsync());
        Assert.Equal(new Dictionary<string, long> { ["trash"] = 50 }, await client.InventoryAsync());
    }

    [Fact]
    public async Task Prices_list_trash_and_waste()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterAsync();

        var items = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(100, items.Single(i => i.GetProperty("id").GetString() == "trash").GetProperty("buyCents").GetInt64());
        Assert.Equal(1500, items.Single(i => i.GetProperty("id").GetString() == "waste_copper").GetProperty("sellCents").GetInt64());
        Assert.Equal(100_000, items.Single(i => i.GetProperty("id").GetString() == "furniture_sorting_machine").GetProperty("buyCents").GetInt64());
    }
}
