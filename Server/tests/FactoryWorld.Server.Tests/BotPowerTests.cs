using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.Research;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>A bot selling the power of its line, on a database of its own.</summary>
public sealed class BotPowerTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    [Fact]
    public async Task A_greedy_bot_with_a_line_puts_up_a_grid_tie_by_its_poles_and_opens_it_to_players()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        var bot = Engine.Bots.First(b => b.Profile.Trade is BotTrade.Smelter or BotTrade.Miner or BotTrade.Industrialist && b.Traits.Greed >= BotRules.SellsPowerFrom);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 100_000, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 20_000_00, "test", CancellationToken.None);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            foreach (var (item, quantity) in new[]
                     {
                         (Items.IronOre, 300L), (Items.Coal, 150L), (Items.Wood, 300L), (Items.Stone, 200L),
                         (Items.IronIngot, 120L), (Items.CopperIngot, 100L), (Items.Nails, 60L),
                     })
            {
                await inventory.AddAsync(bot.Row.AccountId, item, quantity, CancellationToken.None);
            }
        }

        for (var i = 0; i < 600 && !bot.HasLine; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        Assert.True(bot.HasLine);

        // It has researched power distribution and has a grid tie at hand.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await using var insert = await db.CommandAsync(
                "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @tech, @at, @at, true)",
                CancellationToken.None);
            insert.Parameters.AddWithValue("owner", bot.Row.AccountId);
            insert.Parameters.AddWithValue("tech", TechTree.PowerDistribution);
            insert.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromHours(1));
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(bot.Row.AccountId, Items.GridTie, 1, CancellationToken.None);
        }

        bot.EndSession();
        var actions = new List<string?>();
        for (var i = 0; i < 300 && !actions.Contains("sell power"); i++)
        {
            actions.Add(await Engine.StepAsync(bot.Profile.Name, CancellationToken.None));
        }

        Assert.Contains("put up grid tie", actions);
        Assert.Contains("sell power", actions);
        var info = (await bot.Client.SendAsync("world.info", null, CancellationToken.None)).Payload;
        foreach (var plot in info.GetProperty("plots").EnumerateArray())
        {
            var (x, y) = (plot.GetProperty("x").GetInt32(), plot.GetProperty("y").GetInt32());
            var inside = (await bot.Client.SendAsync("plot.enter", new { x, y }, CancellationToken.None)).Payload;
            if (inside.GetProperty("objects").EnumerateArray().FirstOrDefault(o => o.GetProperty("kind").GetString() == Items.GridTie) is { ValueKind: System.Text.Json.JsonValueKind.Object } tie)
            {
                // Players nearby see it among the ties that sell power.
                var offers = await player.SendAsync("powertie.offers", new { x, y, id = tie.GetProperty("id").GetString() });
                var view = await bot.Client.SendAsync("powertie.get", new { x, y, id = tie.GetProperty("id").GetString() }, CancellationToken.None);
                Assert.True(view.Payload.GetProperty("open").GetBoolean(), view.Payload.ToString() + offers);
                Assert.Equal(BotRules.PowerPriceCents, view.Payload.GetProperty("priceCents").GetInt64());
                return;
            }
        }

        Assert.Fail("No grid tie on the bot's plots.");
    }
}
