using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Bots in their towns' politics, on a database of their own.</summary>
public sealed class BotPoliticsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Towns AllTowns = Towns.Load();

    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    /// <summary>Three city plots of one big town.</summary>
    private static List<(int X, int Y)> TownPlots(int skip)
    {
        foreach (var town in AllTowns.All.Where(t => t.Population > 200_000).Skip(skip))
        {
            var found = new List<(int, int)>();
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var (x, y) = (town.X + dx, town.Y + dy);
                    if (Map.Contains(x, y) && Map.BiomeAt(x, y) == Biome.City && AllTowns.Of(x, y) == town)
                    {
                        found.Add((x, y));
                    }
                }
            }

            if (found.Count >= 3)
            {
                return found;
            }
        }

        throw new InvalidOperationException("No town.");
    }

    private async Task<long> BalanceAsync(Guid account)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(account, CancellationToken.None);
    }

    private async Task CreditAsync(Guid account, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(account, cents, "test", CancellationToken.None);
    }

    private async Task<bool> StepUntilAsync(Bot bot, string action, int steps = 300)
    {
        for (var i = 0; i < steps; i++)
        {
            if (await Engine.StepAsync(bot.Profile.Name, CancellationToken.None) == action)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<JsonElement> HallAsync(Bot bot, int x, int y) =>
        (await bot.Client.SendAsync("city.get", new { x, y }, CancellationToken.None)).Payload.GetProperty("state").GetProperty("hall");

    [Fact]
    public async Task A_civic_bot_stands_for_mayor_where_no_player_does_other_bots_vote_for_it_and_it_withdraws_when_a_player_stands()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var candidate = Engine.Bots.First(b => b.Traits.Civic >= BotRules.StandsFrom && b.Traits.Ambition >= BotRules.AmbitiousFrom);
        var voter = Engine.Bots.First(b => b != candidate && b.Traits.Civic >= BotRules.VotesFrom && b.Traits.Civic < BotRules.StandsFrom);
        var plots = TownPlots(5);
        foreach (var (bot, (x, y)) in new[] { (candidate, plots[0]), (voter, plots[1]) })
        {
            await CreditAsync(bot.Row.AccountId, 100_000_00);
            var bought = await bot.Client.SendAsync("world.buy", new { x, y }, CancellationToken.None);
            Assert.True(bought.Is("world.bought"), bought.Error);
        }

        Assert.True(await StepUntilAsync(candidate, "politics"));
        var hall = await HallAsync(candidate, plots[0].X, plots[0].Y);
        Assert.True(hall.GetProperty("politics").GetProperty("standing").GetBoolean());
        Assert.Equal(BotGoals.Office, candidate.Goals[BotGoals.Office]?.Kind);

        // Another bot of the town votes for it.
        Assert.True(await StepUntilAsync(voter, "politics"));
        Assert.Equal(candidate.Row.CharacterName, (await HallAsync(voter, plots[1].X, plots[1].Y)).GetProperty("myVote").GetString());

        // A player stands too: the bot gives way, and its votes are void (the deposit comes back, see below).
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await CreditAsync(player.AccountId, 100_000_00);
        Assert.Equal("world.bought", (await player.SendAsync("world.buy", new { x = plots[2].X, y = plots[2].Y })).Type());
        var stood = await player.SendAsync("city.stand", new { x = plots[2].X, y = plots[2].Y, taxPercent = 5, bonus = "demand", slogan = "Gracz" });
        Assert.True(stood.Type() == "city", stood.ToString());

        factory.Time.Advance(BotRules.PoliticsEvery + TimeSpan.FromSeconds(1));
        Assert.True(await StepUntilAsync(candidate, "politics"));
        hall = await HallAsync(candidate, plots[0].X, plots[0].Y);
        Assert.False(hall.GetProperty("politics").GetProperty("standing").GetBoolean());
        Assert.DoesNotContain(hall.GetProperty("politics").GetProperty("people").EnumerateArray(),
            p => p.GetProperty("name").GetString() == candidate.Row.CharacterName && p.GetProperty("votes").GetInt32() > 0);
        Assert.Null(candidate.Goals[BotGoals.Office]);

        // Where a player stands, bots do not vote at all.
        factory.Time.Advance(BotRules.PoliticsEvery + TimeSpan.FromSeconds(1));
        await StepUntilAsync(voter, "politics", 50);
        var voterHall = await HallAsync(voter, plots[1].X, plots[1].Y);
        Assert.NotEqual(await NameAsync(player), voterHall.GetProperty("myVote").GetString());
    }

    private static async Task<string> NameAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

    [Fact]
    public async Task A_player_who_withdraws_gets_the_deposit_back_whole()
    {
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await CreditAsync(player.AccountId, 100_000_00);
        var (x, y) = TownPlots(9)[0];
        Assert.Equal("world.bought", (await player.SendAsync("world.buy", new { x, y })).Type());
        var balance = await player.BalanceAsync();
        Assert.Equal("city", (await player.SendAsync("city.stand", new { x, y, taxPercent = 5 })).Type());
        Assert.Equal(balance - TownHallRules.DepositCents, await player.BalanceAsync());
        Assert.Equal("city", (await player.SendAsync("city.withdraw", new { x, y })).Type());
        Assert.Equal(balance, await player.BalanceAsync());
        Assert.Equal("not_standing", (await player.SendAsync("city.withdraw", new { x, y })).ErrorCode());
    }

    [Fact]
    public async Task A_player_who_withdraws_from_a_presidential_election_gets_the_deposit_back_whole()
    {
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await CreditAsync(player.AccountId, 100_000_00);
        var (x, y) = TownPlots(14)[0];
        Assert.Equal("world.bought", (await player.SendAsync("world.buy", new { x, y })).Type());
        string country;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            country = (await scope.ServiceProvider.GetRequiredService<ICountries>().CountryOfPlotAsync(x, y, CancellationToken.None))!;
        }

        Assert.Equal("country", (await player.SendAsync("country.citizenship", new { country })).Type());
        var balance = await player.BalanceAsync();
        var stood = await player.SendAsync("country.stand", new { country, plotTaxPerMille = 4, foreignerFeePercent = 5 });
        Assert.True(stood.Type() == "country", stood.ToString());
        Assert.True(await player.BalanceAsync() < balance);
        Assert.Equal("country", (await player.SendAsync("country.withdraw", new { country })).Type());
        Assert.Equal(balance, await player.BalanceAsync());
        Assert.Equal("not_standing", (await player.SendAsync("country.withdraw", new { country })).ErrorCode());
    }
}
