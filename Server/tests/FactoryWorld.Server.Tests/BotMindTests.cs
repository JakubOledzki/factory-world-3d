using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Bots;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>What bots are like and what they set their minds on, without a database.</summary>
public sealed class BotMindRulesTests
{
    [Fact]
    public void A_bots_traits_come_from_its_name_the_same_every_time_and_differ_from_bot_to_bot()
    {
        var roster = BotRoster.First(2000);
        Assert.Equal(BotTraits.Of("Kuba_Drwal"), BotTraits.Of("Kuba_Drwal"));
        var traits = roster.Select(b => BotTraits.Of(b.Name)).ToList();
        Assert.All(traits, t => Assert.All(new[] { t.Ambition, t.Civic, t.Greed, t.Risk, t.Loyalty }, v => Assert.InRange(v, 0, 1)));
        // Spread over the whole range: a few bots dare a lot, most do not.
        Assert.InRange(traits.Count(t => t.Risk > 0.9), 100, 300);
        Assert.InRange(traits.Count(t => t.Civic < 0.5), 800, 1200);
    }

    [Fact]
    public void Goals_hold_back_money_and_come_back_from_their_json()
    {
        var goals = new BotGoals();
        Assert.False(goals.Changed);
        var at = DateTimeOffset.UnixEpoch;
        goals.Set(new BotGoal(BotGoals.Plot, 10_000_00, "12:34", at));
        goals.Set(new BotGoal(BotGoals.Office, 0, "Kraków", at));
        Assert.True(goals.Changed);
        Assert.Equal(10_000_00, goals.SavingCents);

        var again = BotGoals.Load(goals.Save());
        Assert.False(goals.Changed);
        Assert.Equal("12:34", again[BotGoals.Plot]!.Target);
        Assert.Equal(10_000_00, again.SavingCents);
        again.Drop(BotGoals.Plot);
        Assert.Equal(0, again.SavingCents);
        Assert.Empty(BotGoals.Load("not json").All);
    }
}

/// <summary>Goals outlast a bot's session and the server, on a database of their own.</summary>
public sealed class BotMindTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    [Fact]
    public async Task A_bot_keeps_its_goals_in_the_database_when_its_session_ends()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First();
        bot.Goals.Set(new BotGoal(BotGoals.Company, 2_000_00, null, factory.Time.GetUtcNow()));

        // It logs in within a quarter of an hour, and out again after its session.
        factory.Time.Advance(BotEngine.FirstSessionWithin + TimeSpan.FromMinutes(1));
        await Engine.TickAsync(CancellationToken.None);
        factory.Time.Advance(TimeSpan.FromHours(4));
        await Engine.TickAsync(CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var command = await db.CommandAsync("SELECT goals::text FROM bots.bots WHERE name = @name", CancellationToken.None);
        command.Parameters.AddWithValue("name", bot.Row.Name);
        var saved = BotGoals.Load(await command.ExecuteScalarAsync(CancellationToken.None) as string);
        Assert.Equal(2_000_00, saved[BotGoals.Company]!.Cents);
    }
}
