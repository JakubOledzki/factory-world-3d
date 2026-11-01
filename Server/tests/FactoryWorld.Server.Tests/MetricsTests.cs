using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Characters;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class MetricsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Metrics_count_answered_messages_by_kind_for_Prometheus()
    {
        using var client = await factory.ConnectAsync();
        await client.SendRawAsync("""{"type":"hello","id":"1","payload":{"protocol":1}}""");

        var text = await factory.CreateClient().GetStringAsync("/metrics");

        Assert.Contains("# TYPE factoryworld_message_duration_seconds histogram", text);
        Assert.Matches("""factoryworld_message_duration_seconds_count\{type="hello"\} [1-9]""", text);
        Assert.Contains("""factoryworld_message_duration_seconds_bucket{type="hello",le="+Inf"}""", text);
        Assert.Contains("factoryworld_players_online ", text);
        Assert.Contains("process_cpu_seconds_total ", text);
    }

    [Fact]
    public void Message_time_buckets_are_cumulative()
    {
        var load = new ServerLoad();
        load.Record("slow", ServerLoad.Start() - System.Diagnostics.Stopwatch.Frequency * 3);

        var total = Assert.Single(load.Totals());
        Assert.Equal(1, total.Count);
        Assert.InRange(total.Seconds, 3, 60);
        for (var i = 0; i < ServerLoad.Buckets.Length; i++)
        {
            Assert.Equal(ServerLoad.Buckets[i] >= 3 ? 1 : 0, total.InBucket[i]);
        }
    }

    [Fact]
    public async Task Metrics_count_items_made_and_money_paid_once_committed()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var counters = factory.Services.GetRequiredService<GameCounters>();
        long Made() => counters.Items().Where(t => t.Item == Items.Planks && !t.Bot).Sum(t => t.Produced);
        long Sold() => counters.MoneyTotals().Where(t => t.Reason == "sell" && !t.Bot).Sum(t => t.InCents);
        var (made, sold) = (Made(), Sold());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await services.GetRequiredService<GameDb>().InTransactionAsync(async () =>
            {
                await services.GetRequiredService<IProductionStats>().RecordAsync(client.AccountId,
                    [ProductionEntry.Made(Items.Planks, 3, DateTimeOffset.UtcNow)], CancellationToken.None);
                await services.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 250, "sell 3 planks", CancellationToken.None);
                Assert.Equal(made, Made());
            }, CancellationToken.None);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<GameDb>().InTransactionAsync(async () =>
            {
                await services.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1000, "sell 1 planks", CancellationToken.None);
                throw new InvalidOperationException("rolled back");
            }, CancellationToken.None));
        }

        Assert.Equal(made + 3, Made());
        Assert.Equal(sold + 250, Sold());
        var text = await factory.CreateClient().GetStringAsync("/metrics");
        Assert.Matches($$"""factoryworld_items_produced_total\{item="{{Items.Planks}}",who="player"\} [1-9]""", text);
        Assert.Matches("""factoryworld_money_in_cents_total\{reason="sell",who="player"\} [1-9]""", text);
    }

    [Fact]
    public async Task Metrics_show_market_trades_open_offers_prices_and_the_richest_players()
    {
        using var seller = await factory.ConnectAsync();
        await seller.RegisterWithCharacterAsync();
        using var buyer = await factory.ConnectAsync();
        await buyer.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(seller.AccountId, Items.Wood, 30, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(buyer.AccountId, 10_000, "test", CancellationToken.None);
        }

        Assert.Equal("market.done", (await seller.SendAsync("market.list", new { item = Items.Wood, quantity = 20, priceCents = 300 })).Type());
        var id = (await buyer.SendAsync("market.item", new { item = Items.Wood })).Payload().GetProperty("listings")[0].GetProperty("id").GetInt64();
        Assert.Equal("market.done", (await buyer.SendAsync("market.purchase", new { id, quantity = 5 })).Type());
        Assert.Equal("market.traded", (await seller.SendAsync("market.sell", new { item = Items.Wood, quantity = 2 })).Type());
        var traded = factory.Services.GetRequiredService<GameCounters>().Trades();
        Assert.Contains(traded, t => t is { Market: GameCounters.Players, Item: Items.Wood, Quantity: 5, Cents: 1500 });
        Assert.Contains(traded, t => t is { Market: GameCounters.TraderSell, Item: Items.Wood, Quantity: 2 });

        factory.Services.GetRequiredService<GaugeReadings>().Forget();
        var text = await factory.CreateClient().GetStringAsync("/metrics");

        Assert.Contains($$"""factoryworld_trade_pieces_total{market="players",item="{{Items.Wood}}"} 5""", text);
        Assert.Contains($$"""factoryworld_trade_cents_total{market="players",item="{{Items.Wood}}"} 1500""", text);
        Assert.Matches($$"""factoryworld_trade_pieces_total\{market="trader_sell",item="{{Items.Wood}}"\} [1-9]""", text);
        Assert.Matches("""factoryworld_market_offers\{kind="listing",who="player"\} [1-9]""", text);
        Assert.Contains($$"""factoryworld_market_lowest_listing_cents{item="{{Items.Wood}}"} 300""", text);
        Assert.Contains($$"""factoryworld_market_listed_pieces{item="{{Items.Wood}}"} 15""", text);
        Assert.Matches($$"""factoryworld_trader_price_cents\{item="{{Items.Wood}}",side="buys"\} [1-9]""", text);
        Assert.Contains("""factoryworld_market_event{event="calm"} 1""", text);
        Assert.Matches("""factoryworld_richest_money_cents\{who="player",name="[^"]+"\} [1-9]""", text);
        Assert.Matches("""factoryworld_wallet_money_cents\{who="player"\} [1-9]""", text);
        Assert.Matches("""factoryworld_characters\{who="player"\} [1-9]""", text);
        Assert.Matches("""factoryworld_characters_by_level\{who="player",levels="1"\} [1-9]""", text);
        Assert.Contains("""factoryworld_bots{trade="forester",state="resting"} 0""", text);
        Assert.Contains("# TYPE factoryworld_research_known gauge", text);
        Assert.Contains("# TYPE factoryworld_plots_owned gauge", text);
    }

    [Fact]
    public void Level_bands_are_labelled_from_their_lowest_to_their_highest_level()
    {
        var labels = Enumerable.Range(0, CharacterGauges.Bands.Length).Select(CharacterGauges.Band).ToList();
        Assert.Equal(["1", "2-4", "5-9", "10-14", "15-19", "20-29", "30-49", "50+"], labels);
    }

    [Theory]
    [InlineData("sell 5 iron_ore", "sell")]
    [InlineData("tax of Wola: market sold 1 plank", "tax")]
    [InlineData("company Acme research steel", "company")]
    [InlineData("company research steel", "company_research")]
    [InlineData("bank deposit Lokata", "bank_deposit")]
    [InlineData("survey 3:4", "survey")]
    [InlineData("", "other")]
    public void Ledger_reasons_are_grouped_by_their_first_word(string reason, string kind) =>
        Assert.Equal(kind, GameCounters.ReasonKind(reason));
}
