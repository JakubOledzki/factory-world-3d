using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Contracts;
using FactoryWorld.Modules.Market;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Orders with a reward and a deadline, from NPC companies and between players.</summary>
public sealed class ContractTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public void An_npc_order_never_pays_more_than_the_trader_charges_for_the_goods_even_in_a_sale()
    {
        foreach (var template in NpcContracts.Templates.Where(t => NpcPrices.Sells.ContainsKey(t.Item)))
        {
            var cheapest = MarketEvents.Events.Select(e => e.Charges.GetValueOrDefault(template.Item)).Append(0)
                .Min(percent => NpcPrices.Sells[template.Item] * (100 + percent) / 100);
            Assert.True(template.CentsPerPiece < cheapest, $"{template.Item}: {template.CentsPerPiece} >= {cheapest}");
        }
    }

    private async Task<GameClient> PlayerAsync(long cents = 0, string? item = null, long quantity = 0)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await GiveAsync(client, cents, item, quantity);
        return client;
    }

    private async Task GiveAsync(GameClient client, long cents = 0, string? item = null, long quantity = 0)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        if (cents > 0)
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        if (item is not null)
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
        }
    }

    private static async Task<JsonElement> ViewAsync(GameClient client)
    {
        var reply = await client.SendAsync("contracts.get");
        Assert.Equal("contracts", reply.Type());
        return reply.Payload();
    }

    private static List<JsonElement> List(JsonElement view, string part) => view.GetProperty(part).EnumerateArray().ToList();

    private static JsonElement? Find(JsonElement view, string part, long id) =>
        List(view, part).Cast<JsonElement?>().FirstOrDefault(c => c!.Value.GetProperty("id").GetInt64() == id);

    private static long Id(JsonElement contract) => contract.GetProperty("id").GetInt64();

    /// <summary>Gives an order on the board and returns its id.</summary>
    private static async Task<long> CreateAsync(GameClient issuer, string item, long quantity, long rewardCents, int hours = 24)
    {
        var reply = await issuer.SendAsync("contracts.create", new { item, quantity, rewardCents, hours });
        Assert.Equal("contracts.done", reply.Type());
        return Id(List(await ViewAsync(issuer), "issued")[0]);
    }

    [Fact]
    public async Task Every_player_gets_three_npc_orders_and_finishing_one_pays_the_reward_and_xp()
    {
        using var player = await PlayerAsync();
        var npc = List(await ViewAsync(player), "npc");

        Assert.Equal(3, npc.Count);
        Assert.Equal(3, npc.Select(c => c.GetProperty("item").GetString()).Distinct().Count());
        Assert.All(npc, c =>
        {
            Assert.True(c.GetProperty("npc").GetBoolean());
            Assert.True(c.GetProperty("delivering").GetBoolean());
            Assert.False(string.IsNullOrEmpty(c.GetProperty("issuer").GetString()));
            Assert.True(c.GetProperty("rewardCents").GetInt64() > 0);
            Assert.True(c.GetProperty("deadline").GetInt64() > factory.Time.GetUtcNow().ToUnixTimeMilliseconds());
        });
        // Looking again gives the same orders, not new ones.
        Assert.Equal(npc.Select(Id), List(await ViewAsync(player), "npc").Select(Id));

        // A wooden chest may be ordered alone; this one is delivered in two parts.
        var order = npc.First(c => c.GetProperty("quantity").GetInt64() > 1);
        var item = order.GetProperty("item").GetString()!;
        var quantity = order.GetProperty("quantity").GetInt64();
        var reward = order.GetProperty("rewardCents").GetInt64();
        await GiveAsync(player, item: item, quantity: quantity + 2);

        var part = await player.SendAsync("contracts.deliver", new { id = Id(order), quantity = 1 });
        Assert.Equal("contracts.done", part.Type());
        Assert.False(part.Payload().GetProperty("completed").GetBoolean());
        Assert.Equal(1, Find(await ViewAsync(player), "npc", Id(order))!.Value.GetProperty("delivered").GetInt64());

        var rest = await player.SendAsync("contracts.deliver", new { id = Id(order), quantity = quantity - 1 });
        Assert.True(rest.Payload().GetProperty("completed").GetBoolean());
        Assert.Equal(reward, rest.Payload().GetProperty("balanceCents").GetInt64());
        Assert.Equal(order.GetProperty("xp").GetInt64(), rest.Payload().GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal(2, (await player.InventoryAsync()).GetValueOrDefault(item));

        // The next order comes a few minutes later.
        var view = await ViewAsync(player);
        Assert.Equal(2, List(view, "npc").Count);
        Assert.Equal(Id(order), Id(List(view, "finished").Single()));
        var next = view.GetProperty("nextNpcAt").GetInt64();
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds(), next);
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        view = await ViewAsync(player);
        Assert.Equal(3, List(view, "npc").Count);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("nextNpcAt").ValueKind);
    }

    [Fact]
    public async Task Giving_up_an_npc_order_returns_the_delivered_pieces()
    {
        using var player = await PlayerAsync();
        // Not an order of one piece, which delivering one would finish.
        var order = List(await ViewAsync(player), "npc").First(c => c.GetProperty("quantity").GetInt64() > 1);
        var item = order.GetProperty("item").GetString()!;
        await GiveAsync(player, item: item, quantity: 1);
        await player.SendAsync("contracts.deliver", new { id = Id(order), quantity = 1 });
        Assert.Equal(0, (await player.InventoryAsync()).GetValueOrDefault(item));

        Assert.Equal("contracts.done", (await player.SendAsync("contracts.abandon", new { id = Id(order) })).Type());

        Assert.Equal(1, (await player.InventoryAsync()).GetValueOrDefault(item));
        var view = await ViewAsync(player);
        Assert.Null(Find(view, "npc", Id(order)));
        Assert.Equal(2, List(view, "npc").Count);
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("nextNpcAt").ValueKind);
        Assert.Equal("no_contract", (await player.SendAsync("contracts.deliver", new { id = Id(order), quantity = 1 })).ErrorCode());

        // Giving up is no quick way to draw a better order: the next one takes longer than after a delivery.
        factory.Time.Advance(ContractRules.NpcRefill);
        Assert.Equal(2, List(await ViewAsync(player), "npc").Count);
        factory.Time.Advance(ContractRules.NpcRefillAbandoned - ContractRules.NpcRefill);
        Assert.Equal(3, List(await ViewAsync(player), "npc").Count);
    }

    [Fact]
    public async Task A_player_order_holds_the_reward_and_pays_the_one_who_delivers_everything()
    {
        using var issuer = await PlayerAsync(cents: 100_000);
        using var contractor = await PlayerAsync(item: Items.IronIngot, quantity: 60);
        using var late = await PlayerAsync();

        var id = await CreateAsync(issuer, Items.IronIngot, 50, 40_000);
        Assert.Equal(60_000, await issuer.BalanceAsync());

        var offered = Find(await ViewAsync(contractor), "board", id)!.Value;
        Assert.Equal(50, offered.GetProperty("quantity").GetInt64());
        Assert.Equal(40_000, offered.GetProperty("rewardCents").GetInt64());
        Assert.Equal("open", offered.GetProperty("state").GetString());
        Assert.Null(Find(await ViewAsync(issuer), "board", id));

        Assert.Equal("not_contractor", (await contractor.SendAsync("contracts.deliver", new { id, quantity = 1 })).ErrorCode());
        Assert.Equal("contracts.done", (await contractor.SendAsync("contracts.accept", new { id })).Type());
        Assert.Equal("contract_taken", (await late.SendAsync("contracts.accept", new { id })).ErrorCode());
        Assert.Null(Find(await ViewAsync(late), "board", id));
        Assert.Equal("contract_taken", (await issuer.SendAsync("contracts.cancel", new { id })).ErrorCode());

        await contractor.SendAsync("contracts.deliver", new { id, quantity = 20 });
        Assert.Equal(20, Find(await ViewAsync(issuer), "issued", id)!.Value.GetProperty("delivered").GetInt64());
        Assert.Equal("bad_quantity", (await contractor.SendAsync("contracts.deliver", new { id, quantity = 31 })).ErrorCode());
        var done = await contractor.SendAsync("contracts.deliver", new { id, quantity = 30 });

        Assert.True(done.Payload().GetProperty("completed").GetBoolean());
        Assert.Equal(40_000, await contractor.BalanceAsync());
        Assert.Equal(10, (await contractor.InventoryAsync()).GetValueOrDefault(Items.IronIngot));
        Assert.Equal(50, (await issuer.InventoryAsync()).GetValueOrDefault(Items.IronIngot));
        var issued = Find(await ViewAsync(issuer), "issued", id)!.Value;
        Assert.Equal("done", issued.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(issued.GetProperty("contractor").GetString()));
    }

    [Fact]
    public async Task A_missed_deadline_gives_the_pieces_and_the_reward_back()
    {
        using var issuer = await PlayerAsync(cents: 10_000);
        using var contractor = await PlayerAsync(item: Items.Wood, quantity: 10);
        var id = await CreateAsync(issuer, Items.Wood, 20, 5_000, hours: 2);
        await contractor.SendAsync("contracts.accept", new { id });
        await contractor.SendAsync("contracts.deliver", new { id, quantity = 10 });
        Assert.Equal(0, (await contractor.InventoryAsync()).GetValueOrDefault(Items.Wood));

        factory.Time.Advance(TimeSpan.FromHours(2));

        Assert.Equal("contract_expired", (await contractor.SendAsync("contracts.deliver", new { id, quantity = 1 })).ErrorCode());
        Assert.Equal("expired", Find(await ViewAsync(issuer), "issued", id)!.Value.GetProperty("state").GetString());
        Assert.Equal(10, (await contractor.InventoryAsync()).GetValueOrDefault(Items.Wood));
        Assert.Equal(10_000, await issuer.BalanceAsync());
        Assert.Equal(0, (await issuer.InventoryAsync()).GetValueOrDefault(Items.Wood));
        Assert.Null(Find(await ViewAsync(contractor), "taken", id));
    }

    [Fact]
    public async Task Giving_up_puts_the_order_back_on_the_board_and_the_issuer_may_then_cancel_it()
    {
        using var issuer = await PlayerAsync(cents: 10_000);
        using var contractor = await PlayerAsync(item: Items.Stone, quantity: 5);
        var id = await CreateAsync(issuer, Items.Stone, 10, 2_000);
        await contractor.SendAsync("contracts.accept", new { id });
        await contractor.SendAsync("contracts.deliver", new { id, quantity = 5 });
        Assert.Equal(id, Id(List(await ViewAsync(contractor), "taken").Single()));

        await contractor.SendAsync("contracts.abandon", new { id });

        Assert.Equal(5, (await contractor.InventoryAsync()).GetValueOrDefault(Items.Stone));
        var back = Find(await ViewAsync(contractor), "board", id)!.Value;
        Assert.Equal(0, back.GetProperty("delivered").GetInt64());
        Assert.Equal("open", back.GetProperty("state").GetString());

        var cancelled = await issuer.SendAsync("contracts.cancel", new { id });
        Assert.Equal(10_000, cancelled.Payload().GetProperty("balanceCents").GetInt64());
        Assert.Equal("cancelled", Find(await ViewAsync(issuer), "issued", id)!.Value.GetProperty("state").GetString());
        Assert.Equal("no_contract", (await contractor.SendAsync("contracts.accept", new { id })).ErrorCode());
    }

    [Fact]
    public async Task Bad_orders_are_refused_and_change_nothing()
    {
        using var player = await PlayerAsync(cents: 1_000);

        Assert.Equal("unknown_item", (await player.SendAsync("contracts.create", new { item = "gold", quantity = 1, rewardCents = 100, hours = 1 })).ErrorCode());
        Assert.Equal("bad_quantity", (await player.SendAsync("contracts.create", new { item = Items.Sand, quantity = 0, rewardCents = 100, hours = 1 })).ErrorCode());
        Assert.Equal("bad_reward", (await player.SendAsync("contracts.create", new { item = Items.Sand, quantity = 1, rewardCents = 0, hours = 1 })).ErrorCode());
        Assert.Equal("bad_deadline", (await player.SendAsync("contracts.create", new { item = Items.Sand, quantity = 1, rewardCents = 100, hours = 0 })).ErrorCode());
        Assert.Equal("bad_deadline", (await player.SendAsync("contracts.create", new { item = Items.Sand, quantity = 1, rewardCents = 100, hours = 169 })).ErrorCode());
        Assert.Equal("not_enough_money", (await player.SendAsync("contracts.create", new { item = Items.Sand, quantity = 1, rewardCents = 1_001, hours = 1 })).ErrorCode());
        Assert.Equal(1_000, await player.BalanceAsync());
        Assert.Empty(List(await ViewAsync(player), "issued"));

        var id = await CreateAsync(player, Items.Sand, 1, 100);
        Assert.Equal("own_contract", (await player.SendAsync("contracts.accept", new { id })).ErrorCode());
        Assert.Equal("no_contract", (await player.SendAsync("contracts.accept", new { id = 999_999 })).ErrorCode());
    }

    [Fact]
    public async Task A_player_gives_at_most_ten_orders_and_delivers_at_most_five()
    {
        using var issuer = await PlayerAsync(cents: 10_000);
        using var contractor = await PlayerAsync();
        var ids = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(await CreateAsync(issuer, Items.Coal, 1, 100));
        }

        Assert.Equal("too_many_contracts", (await issuer.SendAsync("contracts.create", new { item = Items.Coal, quantity = 1, rewardCents = 100, hours = 1 })).ErrorCode());
        foreach (var id in ids.Take(5))
        {
            Assert.Equal("contracts.done", (await contractor.SendAsync("contracts.accept", new { id })).Type());
        }

        Assert.Equal("too_many_taken", (await contractor.SendAsync("contracts.accept", new { id = ids[5] })).ErrorCode());
        Assert.Equal(9_000, await issuer.BalanceAsync());
    }
}
