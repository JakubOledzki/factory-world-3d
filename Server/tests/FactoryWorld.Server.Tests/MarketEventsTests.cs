using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>A server whose trader's prices change, as in the game.</summary>
public sealed class ChangingPricesFactory : ServerFactory
{
    protected override bool PriceChanges => true;
}

/// <summary>The trader's events and changing prices.</summary>
public sealed class MarketEventsTests(ChangingPricesFactory factory) : IClassFixture<ChangingPricesFactory>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every hour of 90 days.</summary>
    private static IEnumerable<DateTimeOffset> Hours() => Enumerable.Range(0, 90 * 24).Select(h => Start.AddHours(h));

    [Fact]
    public void The_trader_never_pays_more_than_it_charges()
    {
        foreach (var at in Hours())
        {
            var prices = TraderPrices.At(at);
            foreach (var (item, pays) in prices.Buys)
            {
                Assert.True(pays >= 1, $"{item} at {at}");
                if (prices.Sells.TryGetValue(item, out var charges))
                {
                    Assert.True(pays < charges, $"{item} at {at}: pays {pays}, charges {charges}");
                }
            }
        }
    }

    [Fact]
    public void The_trader_sells_everything_it_buys_at_ten_times_the_price_unless_it_has_its_own()
    {
        Assert.Equal(10_000, NpcPrices.Sells[Items.IronIngot]);
        Assert.Equal(NpcPrices.Buys[Items.Wood] * NpcPrices.Markup, NpcPrices.Sells[Items.Wood]);
        Assert.Equal(300, NpcPrices.Sells[Items.Planks]);
        Assert.All(NpcPrices.Buys.Keys, item => Assert.True(NpcPrices.Sells.ContainsKey(item), item));
    }

    [Fact]
    public void Making_something_from_what_the_trader_sells_never_pays_more_than_it_cost()
    {
        var recipes = Workshop.Recipes.Select(r => (r.Id, r.Quantity, r.Ingredients))
            .Concat(Processing.Machines.SelectMany(m => m.Recipes).Select(r => (r.Id, r.Quantity, r.Ingredients)))
            .Where(r => NpcPrices.Buys.ContainsKey(r.Id) && r.Ingredients.Count > 0 && r.Ingredients.All(i => NpcPrices.Sells.ContainsKey(i.Item)))
            .ToList();
        Assert.Contains(recipes, r => r.Id == Items.Bread);

        foreach (var at in Hours())
        {
            var prices = TraderPrices.At(at);
            foreach (var (id, quantity, ingredients) in recipes)
            {
                var cost = ingredients.Sum(i => prices.Sells[i.Item] * i.Quantity);
                Assert.True(prices.Buys[id] * quantity <= cost, $"{id} at {at}: sells for {prices.Buys[id] * quantity}, costs {cost}");
            }
        }
    }

    [Fact]
    public void Prices_drift_a_little_from_the_usual_ones_outside_events()
    {
        var seen = new HashSet<long>();
        foreach (var at in Hours().Where(at => MarketEvents.At(at) is null))
        {
            var wood = TraderPrices.At(at).Buys[Items.Wood];
            Assert.InRange(wood, 180, 220);
            seen.Add(wood);
            Assert.Equal(NpcPrices.Sells[Items.Mill], TraderPrices.At(at).Sells[Items.Mill]);
        }

        Assert.True(seen.Count > 10, "The price of wood should change over the days.");
    }

    [Fact]
    public void Every_event_comes_up_and_none_runs_twice_in_a_row()
    {
        var slots = Enumerable.Range(0, 90 * 4).Select(i => MarketEvents.At(Start + i * MarketEvents.SlotLength)).ToList();

        Assert.Equal(MarketEvents.Events.Select(e => e.Id).Order(), slots.OfType<ScheduledEvent>().Select(s => s.Event.Id).Distinct().Order());
        Assert.Contains(null, slots);
        for (var i = 1; i < slots.Count; i++)
        {
            Assert.True(slots[i] is null || slots[i]!.Event != slots[i - 1]?.Event, $"slot {i}");
        }
    }

    [Fact]
    public void An_event_lasts_its_slot_and_the_next_one_is_known_in_advance()
    {
        var at = Start.AddMinutes(17);
        var next = MarketEvents.Next(at);

        Assert.NotNull(next);
        Assert.True(next.StartsAt > at);
        Assert.Equal(MarketEvents.SlotLength, next.EndsAt - next.StartsAt);
        Assert.Equal(next, MarketEvents.At(next.StartsAt));
        Assert.Equal(next, MarketEvents.At(next.EndsAt.AddTicks(-1)));
        Assert.NotEqual(next, MarketEvents.At(next.EndsAt));
    }

    [Fact]
    public async Task With_price_changes_off_the_trader_keeps_its_usual_prices()
    {
        await using var usual = new ServerFactory();
        using var client = await usual.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var prices = (await client.SendAsync("market.prices")).Payload();

        Assert.Equal(System.Text.Json.JsonValueKind.Null, prices.GetProperty("event").ValueKind);
        var wheat = prices.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == Items.Wheat);
        Assert.Equal(NpcPrices.Buys[Items.Wheat], wheat.GetProperty("sellCents").GetInt64());
        Assert.Equal(0, wheat.GetProperty("sellChange").GetInt32());
    }

    [Fact]
    public async Task During_a_drought_the_trader_shows_and_pays_more_for_wheat()
    {
        var drought = Enumerable.Range(1, 400)
            .Select(i => MarketEvents.At(factory.Time.GetUtcNow() + i * MarketEvents.SlotLength))
            .First(s => s?.Event.Id == "drought")!;
        factory.Time.Advance(drought.StartsAt - factory.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        var expected = TraderPrices.At(factory.Time.GetUtcNow()).Buys[Items.Wheat];
        Assert.True(expected > NpcPrices.Buys[Items.Wheat] * 14 / 10, $"wheat pays {expected}");

        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var prices = (await client.SendAsync("market.prices")).Payload();

        var e = prices.GetProperty("event");
        Assert.Equal("drought", e.GetProperty("id").GetString());
        Assert.Equal("Susza", e.GetProperty("name").GetString());
        Assert.Equal(drought.EndsAt.ToUnixTimeMilliseconds(), e.GetProperty("endsAtMs").GetInt64());
        var effect = e.GetProperty("effects").EnumerateArray().Single(x => x.GetProperty("item").GetString() == Items.Wheat);
        Assert.Equal(60, effect.GetProperty("sellChange").GetInt32());
        Assert.True(prices.GetProperty("next").GetProperty("startsAtMs").GetInt64() >= drought.EndsAt.ToUnixTimeMilliseconds());

        var wheat = prices.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == Items.Wheat);
        Assert.Equal(expected, wheat.GetProperty("sellCents").GetInt64());
        Assert.Equal(TraderPrices.Change(expected, NpcPrices.Buys[Items.Wheat]), wheat.GetProperty("sellChange").GetInt32());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, Items.Wheat, 10, CancellationToken.None);
        }

        var sold = await client.SendAsync("market.sell", new { item = Items.Wheat, quantity = 10 });
        Assert.Equal("market.traded", sold.Type());
        Assert.Equal(10 * expected, sold.Payload().GetProperty("balanceCents").GetInt64());

        var overview = (await client.SendAsync("market.overview")).Payload().GetProperty("items").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == Items.Wheat);
        Assert.Equal(expected, overview.GetProperty("quickSaleCents").GetInt64());
    }
}
