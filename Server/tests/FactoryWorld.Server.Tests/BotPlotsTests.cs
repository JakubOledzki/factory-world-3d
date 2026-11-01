using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>How many plots a bot wants and where, without a database.</summary>
public sealed class BotPlotsRulesTests
{
    [Fact]
    public void An_ambitious_bot_wants_more_plots_and_one_in_a_town()
    {
        Assert.Equal(3, BotRules.MaxPlots(BotTrade.Miner, 0));
        Assert.Equal(5, BotRules.MaxPlots(BotTrade.Miner, 1));
        Assert.Equal(1, BotRules.MaxPlots(BotTrade.Trader, 0.2));
        Assert.Equal(2, BotRules.MaxPlots(BotTrade.Trader, 0.9));

        // The first plot is always one of its trade; a town plot comes after, for an ambitious bot without one.
        Assert.DoesNotContain((int)Biome.City, BotRules.PlotBiomes(BotTrade.Miner, 0, 1, false));
        Assert.Contains((int)Biome.City, BotRules.PlotBiomes(BotTrade.Miner, 1, 0.8, false));
        Assert.DoesNotContain((int)Biome.City, BotRules.PlotBiomes(BotTrade.Miner, 1, 0.8, true));
        Assert.DoesNotContain((int)Biome.City, BotRules.PlotBiomes(BotTrade.Miner, 1, 0.2, false));
    }
}

/// <summary>Bots on their plots: saving up for the next one and supplying towns, on a database of their own.</summary>
public sealed class BotPlotsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Towns AllTowns = Towns.Load();

    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    private async Task CreditAsync(Guid account, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(account, cents, "test", CancellationToken.None);
    }

    [Fact]
    public async Task A_bot_with_a_plot_in_a_town_delivers_the_town_what_it_wants()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Miner);
        await CreditAsync(bot.Row.AccountId, 1_000_000_00);

        var town = AllTowns.All.Where(t => t.Population > 100_000).Skip(30).First(t => Map.BiomeAt(t.X, t.Y) == Biome.City);
        var bought = await bot.Client.SendAsync("world.buy", new { x = town.X, y = town.Y }, CancellationToken.None);
        Assert.True(bought.Is("world.bought"), bought.Error);
        var need = (await bot.Client.SendAsync("city.get", new { x = town.X, y = town.Y }, CancellationToken.None)).Payload
            .GetProperty("state").GetProperty("needs")[0];
        var item = need.GetProperty("id").GetString()!;
        var wanted = need.GetProperty("wanted").GetInt64();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(bot.Row.AccountId, item, wanted * 3, CancellationToken.None);
        }

        var supplied = false;
        for (var i = 0; i < 400 && !supplied; i++)
        {
            supplied = await Engine.StepAsync(bot.Profile.Name, CancellationToken.None) == "supply town";
        }

        Assert.True(supplied, "The bot never supplied its town.");
        var after = (await bot.Client.SendAsync("city.get", new { x = town.X, y = town.Y }, CancellationToken.None)).Payload
            .GetProperty("state").GetProperty("needs").EnumerateArray().First(n => n.GetProperty("id").GetString() == item);
        Assert.True(after.GetProperty("delivered").GetInt64() > 0);
    }

    [Fact]
    public async Task A_bot_saves_up_for_its_next_plot_once_it_has_a_quarter_of_the_price()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Forester);
        await CreditAsync(bot.Row.AccountId, 45_000_00);
        var owned = 0;
        for (var x = 100; owned < 2; x++)
        {
            if (Map.BiomeAt(x, 500) == Biome.Forest && (await bot.Client.SendAsync("world.buy", new { x, y = 500 }, CancellationToken.None)).Is("world.bought"))
            {
                owned++;
            }
        }

        // The third plot costs 100 000 $, far more than it has, but it has a good part of it: it holds that back.
        await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        Assert.Equal(PlotPrices.For(2, Biome.Forest), bot.Goals[BotGoals.Plot]?.Cents);
    }
}
