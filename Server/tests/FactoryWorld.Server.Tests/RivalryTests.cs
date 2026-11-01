using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Rivalry;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class RivalryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private async Task<GameClient> PlayerAsync(long cents = 0)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        if (cents > 0)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        return client;
    }

    private static async Task<string> NameAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

    private static JsonElement First(JsonElement rivalry, string id) =>
        rivalry.GetProperty("firsts").EnumerateArray().First(f => f.GetProperty("id").GetString() == id);

    // --- Firsts in the world ------------------------------------------------------------------------

    [Fact]
    public void Firsts_are_read_from_what_the_server_answered()
    {
        JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, Payload.Json);

        Assert.Equal([Firsts.NuclearPlant], Firsts.Reached("plot.place", null, "plot.placed", Json(new { @object = new { kind = Items.NuclearPlant } })));
        Assert.Equal([Firsts.FusionPlant], Firsts.Reached("plot.placeMany", null, "plot.placedMany",
            Json(new { objects = new[] { new { kind = Items.WoodenChest }, new { kind = Items.FusionPlant } } })));
        Assert.Equal([Firsts.Rocket], Firsts.Reached("machine.take", Json(new { what = "output" }), "machine",
            Json(new { taken = new[] { new { id = Items.LaunchRocket, quantity = 1 } } })));
        Assert.Empty(Firsts.Reached("machine.take", Json(new { what = "input" }), "machine",
            Json(new { taken = new[] { new { id = Items.LaunchRocket, quantity = 1 } } })));
        Assert.Equal([Firsts.Metropolis], Firsts.Reached("city.deliver", null, "city", Json(new { result = new { level = 4 } })));
        Assert.Empty(Firsts.Reached("city.deliver", null, "city", Json(new { result = new { level = 3 } })));
        Assert.Equal([Firsts.Level(10), Firsts.Level(25)], Firsts.Reached("plot.harvest", null, "plot.harvested", Json(new { xp = new { level = 30 } })));
        Assert.Equal([Firsts.Millionaire], Firsts.Reached("wallet.get", null, "wallet", Json(new { balanceCents = Firsts.MillionaireCents })));
        Assert.Empty(Firsts.Reached("wallet.get", null, "wallet", Json(new { balanceCents = Firsts.MillionaireCents - 1 })));
        Assert.Equal([Firsts.WholeTree], Firsts.Reached("research.get", null, "research", Json(new
        {
            technologies = new object[] { new { status = "done", companyOnly = false }, new { status = "locked", companyOnly = true } },
        })));
        Assert.Empty(Firsts.Reached("research.get", null, "research", Json(new
        {
            technologies = new object[] { new { status = "done", companyOnly = false }, new { status = "available", companyOnly = false } },
        })));
        Assert.All(Firsts.All, f => Assert.Same(f, Firsts.ById[f.Id]));
    }

    [Fact]
    public async Task Only_the_first_player_to_get_a_million_is_the_first_millionaire_and_everybody_hears_of_it()
    {
        using var first = await PlayerAsync(Firsts.MillionaireCents);
        using var second = await PlayerAsync(Firsts.MillionaireCents * 2);
        var name = await NameAsync(first);

        Assert.Equal("wallet", (await first.SendAsync("wallet.get")).Type());
        // The badge-like notice comes before the next reply.
        await first.SendAsync("rivalry.get");
        var notice = Assert.Single(first.Notices, n => n.Type() == "achievement.unlocked");
        Assert.Equal("Pierwszy na świecie!", notice.Payload().GetProperty("title").GetString());
        Assert.Equal("Pierwszy milioner", notice.Payload().GetProperty("name").GetString());

        Assert.Equal("wallet", (await second.SendAsync("wallet.get")).Type());
        var rivalry = (await second.SendAsync("rivalry.get")).Payload();
        Assert.DoesNotContain(second.Notices, n => n.Type() == "achievement.unlocked");
        var millionaire = First(rivalry, Firsts.Millionaire);
        Assert.Equal(name, millionaire.GetProperty("owner").GetString());
        Assert.False(millionaire.GetProperty("mine").GetBoolean());
        Assert.True(First((await first.SendAsync("rivalry.get")).Payload(), Firsts.Millionaire).GetProperty("mine").GetBoolean());
        Assert.Equal(JsonValueKind.Null, First(rivalry, Firsts.FusionPlant).GetProperty("owner").ValueKind);

        var chat = (await second.SendAsync("chat.get")).Payload().GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(chat, m => m.GetProperty("name").GetString() == "Serwer" && m.GetProperty("text").GetString()!.Contains(name)
            && m.GetProperty("from").GetGuid() == Guid.Empty);
    }

    // --- The challenge of the week -----------------------------------------------------------------

    [Fact]
    public void Each_week_has_the_next_challenge_and_it_counts_from_the_replies()
    {
        JsonElement Json(object o) => JsonSerializer.SerializeToElement(o, Payload.Json);
        var week = Challenges.Week(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), Challenges.WeekStart(week));
        Assert.Equal(week, Challenges.Week(new DateTimeOffset(2026, 11, 8, 23, 59, 59, TimeSpan.Zero)));
        Assert.NotEqual(Challenges.For(week).Id, Challenges.For(week + 1).Id);
        Assert.Equal(Challenges.For(week).Id, Challenges.For(week + Challenges.All.Count).Id);

        Challenge Of(string id) => Challenges.All.First(c => c.Id == id);
        Assert.Equal(7, Challenges.Score(Of("ingots"), "furnace.take", null, Json(new
        {
            taken = new object[] { new { id = Items.IronIngot, quantity = 5 }, new { id = Items.CopperIngot, quantity = 2 }, new { id = Items.Coal, quantity = 9 } },
        })));
        Assert.Equal(1, Challenges.Score(Of("trees"), "plot.plantTree", null, Json(new { })));
        Assert.Equal(0, Challenges.Score(Of("trees"), "plot.harvest", null, Json(new { })));
        Assert.Equal(12, Challenges.Score(Of("city"), "city.tenderDeliver", null, Json(new { result = new { quantity = 12 } })));
        Assert.Equal(4, Challenges.Score(Of("wood"), "plot.harvest", null, Json(new
        {
            items = new object[] { new { id = Items.Wood, quantity = 3 }, new { id = Items.OakWood, quantity = 1 }, new { id = Items.Mushrooms, quantity = 2 } },
        })));
        Assert.Equal(30, Challenges.Score(Of("trash"), "city.trash", null, Json(new { result = new { quantity = 30 } })));
        Assert.Equal(0, Challenges.Score(Of("trash"), "market.purchase", null, Json(new { })));
    }

    [Fact]
    public async Task The_best_three_of_the_week_get_their_prizes_once_the_week_is_over()
    {
        using var gold = await PlayerAsync();
        using var silver = await PlayerAsync();
        using var bronze = await PlayerAsync();
        using var fourth = await PlayerAsync();
        var week = Challenges.Week(factory.Time.GetUtcNow());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rivalry = scope.ServiceProvider.GetRequiredService<RivalryService>();
            await rivalry.RecordAsync(gold.AccountId, [], 50, CancellationToken.None);
            await rivalry.RecordAsync(silver.AccountId, [], 30, CancellationToken.None);
            await rivalry.RecordAsync(bronze.AccountId, [], 20, CancellationToken.None);
            await rivalry.RecordAsync(fourth.AccountId, [], 10, CancellationToken.None);
            await rivalry.RecordAsync(gold.AccountId, [], 1, CancellationToken.None);
        }

        var now = (await silver.SendAsync("rivalry.get")).Payload().GetProperty("challenge");
        Assert.Equal(Challenges.For(week).Name, now.GetProperty("name").GetString());
        Assert.Equal(2, now.GetProperty("me").GetProperty("place").GetInt64());
        Assert.Equal(await NameAsync(gold), now.GetProperty("top")[0].GetProperty("name").GetString());
        Assert.Equal(51, now.GetProperty("top")[0].GetProperty("value").GetInt64());
        var ranking = (await gold.SendAsync("rankings.get", new { ranking = "challenge" })).Payload();
        Assert.Equal(1, ranking.GetProperty("me").GetProperty("place").GetInt64());
        var before = new[] { await gold.BalanceAsync(), await silver.BalanceAsync(), await bronze.BalanceAsync(), await fourth.BalanceAsync() };

        factory.Time.SetUtcNow(Challenges.WeekStart(week + 1) + TimeSpan.FromMinutes(1));
        var after = (await fourth.SendAsync("rivalry.get")).Payload();
        // Asking again pays nothing more.
        await gold.SendAsync("rivalry.get");
        Assert.Equal(before[0] + Challenges.PrizesCents[0], await gold.BalanceAsync());
        Assert.Equal(before[1] + Challenges.PrizesCents[1], await silver.BalanceAsync());
        Assert.Equal(before[2] + Challenges.PrizesCents[2], await bronze.BalanceAsync());
        Assert.Equal(before[3], await fourth.BalanceAsync());

        var winners = after.GetProperty("lastWeek").GetProperty("winners").EnumerateArray().ToList();
        Assert.Equal(3, winners.Count);
        Assert.Equal(await NameAsync(silver), winners[1].GetProperty("name").GetString());
        Assert.Empty(after.GetProperty("challenge").GetProperty("top").EnumerateArray());
        Assert.Equal(1, (await gold.SendAsync("rivalry.get")).Payload().GetProperty("trophies")[0].GetProperty("place").GetInt32());
    }

    // --- The efficiency ranking ---------------------------------------------------------------------

    [Fact]
    public async Task The_efficiency_ranking_counts_the_value_a_factory_added_in_the_last_hour_per_plot()
    {
        using var maker = await PlayerAsync();
        using var waster = await PlayerAsync();
        var now = factory.Time.GetUtcNow();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var stats = scope.ServiceProvider.GetRequiredService<IProductionStats>();
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await db.InTransactionAsync(async () =>
            {
                await stats.RecordAsync(maker.AccountId, [ProductionEntry.Made(Items.IronIngot, 100, now), ProductionEntry.Used(Items.IronOre, 100, now)], CancellationToken.None);
                // Used up more than it made: no value added, so not in the ranking.
                await stats.RecordAsync(waster.AccountId, [ProductionEntry.Used(Items.IronIngot, 10, now)], CancellationToken.None);
                return 0;
            }, CancellationToken.None);
        }

        factory.Time.Advance(TimeSpan.FromMinutes(2));
        var expected = 100 * (NpcPrices.Buys[Items.IronIngot] - NpcPrices.Buys[Items.IronOre]);
        var ranking = (await maker.SendAsync("rankings.get", new { ranking = "efficiency" })).Payload();
        Assert.Equal(expected, ranking.GetProperty("me").GetProperty("value").GetInt64());
        Assert.EndsWith("/h na działkę", ranking.GetProperty("me").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, (await waster.SendAsync("rankings.get", new { ranking = "efficiency" })).Payload().GetProperty("me").ValueKind);

        // Two plots share the value.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(maker.AccountId, 1_000_000_00, "test", CancellationToken.None);
        }

        var bought = 0;
        for (var y = 300; bought < 2; y++)
        {
            for (var x = 0; x < Map.Width && bought < 2; x += 7)
            {
                if (Map.BiomeAt(x, y) == Biome.Grassland && (await maker.SendAsync("world.buy", new { x, y })).Type() == "world.bought")
                {
                    bought++;
                }
            }
        }

        factory.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(expected / 2, (await maker.SendAsync("rankings.get", new { ranking = "efficiency" })).Payload().GetProperty("me").GetProperty("value").GetInt64());

        // Older than an hour no longer counts.
        factory.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(JsonValueKind.Null, (await maker.SendAsync("rankings.get", new { ranking = "efficiency" })).Payload().GetProperty("me").ValueKind);
    }
}
