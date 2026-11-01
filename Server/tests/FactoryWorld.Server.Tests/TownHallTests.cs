using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class TownHallTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Towns AllTowns = Towns.Load();
    private static int _towns;

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        return client;
    }

    private static async Task<string> NameAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

    /// <summary>City plots of one town of the map, a different town each time.</summary>
    private static (List<(int X, int Y)> Plots, Town Town) TownPlots(int count)
    {
        var skip = Interlocked.Increment(ref _towns) * 11 + 300;
        foreach (var town in AllTowns.All.Where(t => t.Population > 100_000).Skip(skip))
        {
            var plots = new List<(int X, int Y)>();
            for (var dy = -4; dy <= 4 && plots.Count < count; dy++)
            {
                for (var dx = -4; dx <= 4 && plots.Count < count; dx++)
                {
                    var (x, y) = (town.X + dx, town.Y + dy);
                    if (Map.Contains(x, y) && Map.BiomeAt(x, y) == Biome.City && AllTowns.Of(x, y) == town)
                    {
                        plots.Add((x, y));
                    }
                }
            }

            if (plots.Count == count)
            {
                return (plots, town);
            }
        }

        throw new InvalidOperationException("No town.");
    }

    private static async Task BuyAsync(GameClient client, int x, int y)
    {
        var bought = await client.SendAsync("world.buy", new { x, y });
        Assert.True(bought.Type() == "world.bought", bought.ToString());
    }

    private static JsonElement Hall(JsonElement reply)
    {
        Assert.True(reply.Type() == "city", reply.ToString());
        return reply.Payload().GetProperty("state").GetProperty("hall");
    }

    private static JsonElement Politics(JsonElement reply) => Hall(reply).GetProperty("politics");

    /// <summary>Three townsfolk, the first of them mayor from the next week on.</summary>
    private async Task<(GameClient Mayor, GameClient Second, GameClient Third, List<(int X, int Y)> Plots, Town Town)> TownWithMayorAsync()
    {
        var (plots, town) = TownPlots(4);
        var mayor = await PlayerAsync();
        var second = await PlayerAsync();
        var third = await PlayerAsync();
        await BuyAsync(mayor, plots[0].X, plots[0].Y);
        await BuyAsync(second, plots[1].X, plots[1].Y);
        await BuyAsync(third, plots[2].X, plots[2].Y);
        var name = await NameAsync(mayor);
        foreach (var (client, plot) in new[] { (mayor, plots[0]), (second, plots[1]), (third, plots[2]) })
        {
            Hall(await client.SendAsync("city.vote", new { x = plot.X, y = plot.Y, candidate = name }));
        }

        NextWeek();
        Assert.True(Hall(await mayor.SendAsync("city.get", new { x = plots[0].X, y = plots[0].Y })).GetProperty("isMayor").GetBoolean());
        return (mayor, second, third, plots, town);
    }

    private void NextWeek()
    {
        var week = CityRules.WeekOf(factory.Time.GetUtcNow());
        factory.Time.Advance(CityRules.WeekStart(week + 1) - factory.Time.GetUtcNow() + TimeSpan.FromHours(1));
    }

    private async Task FillTreasuryAsync(Town town, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                """
                INSERT INTO world.town_treasuries (town_x, town_y, cents) VALUES (@x, @y, @cents)
                ON CONFLICT (town_x, town_y) DO UPDATE SET cents = world.town_treasuries.cents + excluded.cents
                """, CancellationToken.None);
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("cents", cents);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_mayor_builds_with_the_treasury_and_two_thirds_of_the_townsfolk_recall_them()
    {
        var (mayor, second, third, plots, town) = await TownWithMayorAsync();
        var (a, b, c) = (plots[0], plots[1], plots[2]);
        Assert.Equal("not_mayor", (await second.SendAsync("city.invest", new { x = b.X, y = b.Y, kind = "park" })).ErrorCode());
        Assert.Equal("treasury_short", (await mayor.SendAsync("city.invest", new { x = a.X, y = a.Y, kind = "park" })).ErrorCode());
        Assert.Equal("bad_investment", (await mayor.SendAsync("city.invest", new { x = a.X, y = a.Y, kind = "casino" })).ErrorCode());

        await FillTreasuryAsync(town, 60_000_00);
        var built = Politics(await mayor.SendAsync("city.invest", new { x = a.X, y = a.Y, kind = "park" }));
        Assert.Equal(10_000_00, built.GetProperty("treasuryCents").GetInt64());
        var park = built.GetProperty("investments").EnumerateArray().First(i => i.GetProperty("id").GetString() == "park");
        Assert.Equal(1, park.GetProperty("level").GetInt32());
        Assert.Equal(TownHallRules.InvestmentCents[1], park.GetProperty("nextCents").GetInt64());
        Assert.Equal(-50_000_00, built.GetProperty("ledger")[0].GetProperty("cents").GetInt64());

        // One signature of three is not enough; the second makes two thirds, and the mayor is out with their tax and bonus.
        using var stranger = await PlayerAsync();
        Assert.Equal("not_owner", (await stranger.SendAsync("city.recall", new { x = a.X, y = a.Y })).ErrorCode());
        var first = (await second.SendAsync("city.recall", new { x = b.X, y = b.Y })).Payload();
        Assert.False(first.GetProperty("result").GetProperty("recalled").GetBoolean());
        Assert.Equal(1, first.GetProperty("state").GetProperty("hall").GetProperty("politics").GetProperty("recall").GetProperty("signatures").GetInt32());
        Assert.Equal(2, first.GetProperty("state").GetProperty("hall").GetProperty("politics").GetProperty("recall").GetProperty("needed").GetInt32());
        var recalled = await third.SendAsync("city.recall", new { x = c.X, y = c.Y });
        Assert.True(recalled.Payload().GetProperty("result").GetProperty("recalled").GetBoolean());
        var hall = Hall(recalled);
        Assert.Equal(JsonValueKind.Null, hall.GetProperty("mayor").ValueKind);
        Assert.True(hall.GetProperty("politics").GetProperty("recalled").GetBoolean());
        Assert.Equal("not_mayor", (await mayor.SendAsync("city.policy", new { x = a.X, y = a.Y, taxPercent = 5, bonus = "rail" })).ErrorCode());
        Assert.Equal("no_mayor", (await mayor.SendAsync("city.recall", new { x = a.X, y = a.Y })).ErrorCode());
    }

    [Fact]
    public async Task Candidates_stand_with_a_programme_and_a_deposit_and_companies_back_them_for_all_to_see()
    {
        var (plots, _) = TownPlots(3);
        using var candidate = await PlayerAsync();
        using var rival = await PlayerAsync();
        using var backer = await PlayerAsync();
        var (a, b, c) = (plots[0], plots[1], plots[2]);
        await BuyAsync(candidate, a.X, a.Y);
        await BuyAsync(rival, b.X, b.Y);
        var name = await NameAsync(candidate);

        var before = await candidate.BalanceAsync();
        Assert.Equal("bad_tax", (await candidate.SendAsync("city.stand", new { x = a.X, y = a.Y, taxPercent = 11, bonus = "rail", slogan = "" })).ErrorCode());
        Assert.Equal("bad_slogan", (await candidate.SendAsync("city.stand", new { x = a.X, y = a.Y, taxPercent = 3, bonus = "rail", slogan = new string('x', 81) })).ErrorCode());
        Hall(await candidate.SendAsync("city.stand", new { x = a.X, y = a.Y, taxPercent = 5, bonus = "rail", slogan = "Tania kolej" }));
        // Standing again changes the programme; the deposit is paid once.
        var stood = Politics(await candidate.SendAsync("city.stand", new { x = a.X, y = a.Y, taxPercent = 3, bonus = "rail", slogan = "Tania kolej!" }));
        Assert.Equal(before - TownHallRules.DepositCents, await candidate.BalanceAsync());
        Assert.True(stood.GetProperty("standing").GetBoolean());
        var me = stood.GetProperty("people").EnumerateArray().First(p => p.GetProperty("me").GetBoolean());
        Assert.Equal(3, me.GetProperty("taxPercent").GetInt32());
        Assert.Equal("Tania kolej!", me.GetProperty("slogan").GetString());
        Hall(await rival.SendAsync("city.stand", new { x = b.X, y = b.Y, taxPercent = 10, bonus = "", slogan = "" }));

        // A company backs the candidate from its cash; the candidate gets the money and everyone sees who paid.
        Assert.Equal("not_owner", (await backer.SendAsync("city.sponsor", new { x = a.X, y = a.Y, candidate = name, cents = 500_00 })).ErrorCode());
        await BuyAsync(backer, c.X, c.Y);
        Assert.Equal("no_company", (await backer.SendAsync("city.sponsor", new { x = c.X, y = c.Y, candidate = name, cents = 500_00 })).ErrorCode());
        Assert.Equal("company", (await backer.SendAsync("company.create", new { name = "Lobby " + Guid.NewGuid().ToString("N")[..6] })).Type());
        Assert.Equal("company", (await backer.SendAsync("company.deposit", new { cents = 2_000_00 })).Type());
        Assert.Equal("not_candidate", (await backer.SendAsync("city.sponsor", new { x = c.X, y = c.Y, candidate = "Nikt", cents = 500_00 })).ErrorCode());
        Assert.Equal("bad_amount", (await backer.SendAsync("city.sponsor", new { x = c.X, y = c.Y, candidate = name, cents = 0 })).ErrorCode());
        var candidateBefore = await candidate.BalanceAsync();
        var sponsored = Politics(await backer.SendAsync("city.sponsor", new { x = c.X, y = c.Y, candidate = name, cents = 500_00 }));
        Assert.Equal(candidateBefore + 500_00, await candidate.BalanceAsync());
        var backed = sponsored.GetProperty("people").EnumerateArray().First(p => p.GetProperty("name").GetString() == name);
        Assert.Equal(500_00, backed.GetProperty("sponsors")[0].GetProperty("cents").GetInt64());

        // Both vote for the candidate: the rival has no votes, so their deposit goes into the treasury; the winner's comes back.
        // (The backer, a townsman too, does not vote.)
        Hall(await candidate.SendAsync("city.vote", new { x = a.X, y = a.Y, candidate = name }));
        Hall(await rival.SendAsync("city.vote", new { x = b.X, y = b.Y, candidate = name }));
        candidateBefore = await candidate.BalanceAsync();
        NextWeek();
        var hall = Hall(await candidate.SendAsync("city.get", new { x = a.X, y = a.Y }));
        Assert.True(hall.GetProperty("isMayor").GetBoolean());
        Assert.Equal(candidateBefore + TownHallRules.DepositCents, await candidate.BalanceAsync());
        Assert.Equal(TownHallRules.DepositCents, hall.GetProperty("politics").GetProperty("treasuryCents").GetInt64());

        // The mayor keeps the promise; once the week is over it counts next to their name.
        Hall(await candidate.SendAsync("city.policy", new { x = a.X, y = a.Y, taxPercent = 3, bonus = "rail" }));
        NextWeek();
        var people = Politics(await candidate.SendAsync("city.get", new { x = a.X, y = a.Y })).GetProperty("people");
        var kept = people.EnumerateArray().First(p => p.GetProperty("name").GetString() == name);
        Assert.Equal(1, kept.GetProperty("promisesKept").GetInt32());
        Assert.Equal(1, kept.GetProperty("promisesMade").GetInt32());
    }

    [Fact]
    public async Task The_mayor_puts_ordinances_to_a_vote_and_those_that_pass_hold_for_the_week()
    {
        var (mayor, second, third, plots, _) = await TownWithMayorAsync();
        var (a, b, c, free) = (plots[0], plots[1], plots[2], plots[3]);
        var price = (await mayor.SendAsync("world.plot", new { x = free.X, y = free.Y })).Payload().GetProperty("priceCents").GetInt64();

        Assert.Equal("not_mayor", (await second.SendAsync("city.propose", new { x = b.X, y = b.Y, kind = "settlers" })).ErrorCode());
        Assert.Equal("bad_ordinance", (await mayor.SendAsync("city.propose", new { x = a.X, y = a.Y, kind = "curfew" })).ErrorCode());
        var proposed = Politics(await mayor.SendAsync("city.propose", new { x = a.X, y = a.Y, kind = "settlers" }));
        var ordinance = proposed.GetProperty("ordinances")[0];
        Assert.True(ordinance.GetProperty("open").GetBoolean());
        Assert.Equal(1, ordinance.GetProperty("yes").GetInt32());
        Assert.Equal("vote_going_on", (await mayor.SendAsync("city.propose", new { x = a.X, y = a.Y, kind = "recycling" })).ErrorCode());

        var id = ordinance.GetProperty("id").GetString();
        using var stranger = await PlayerAsync();
        Assert.Equal("not_owner", (await stranger.SendAsync("city.ordinance", new { x = a.X, y = a.Y, id, yes = true })).ErrorCode());
        Politics(await second.SendAsync("city.ordinance", new { x = b.X, y = b.Y, id, yes = false }));
        // A new vote replaces the old one.
        Politics(await third.SendAsync("city.ordinance", new { x = c.X, y = c.Y, id, yes = false }));
        var counted = Politics(await third.SendAsync("city.ordinance", new { x = c.X, y = c.Y, id, yes = true })).GetProperty("ordinances")[0];
        Assert.Equal(2, counted.GetProperty("yes").GetInt32());
        Assert.Equal(1, counted.GetProperty("no").GetInt32());
        // Until the vote ends, nothing changes.
        Assert.Equal(price, (await mayor.SendAsync("world.plot", new { x = free.X, y = free.Y })).Payload().GetProperty("priceCents").GetInt64());

        factory.Time.Advance(TownHallRules.OrdinanceVote + TimeSpan.FromMinutes(1));
        Assert.Equal("no_vote", (await second.SendAsync("city.ordinance", new { x = b.X, y = b.Y, id, yes = false })).ErrorCode());
        var passed = Politics(await mayor.SendAsync("city.get", new { x = a.X, y = a.Y })).GetProperty("ordinances")[0];
        Assert.True(passed.GetProperty("inForce").GetBoolean());
        Assert.Equal((long)Math.Round(price * TownHallRules.SettlersPlots),
            (await mayor.SendAsync("world.plot", new { x = free.X, y = free.Y })).Payload().GetProperty("priceCents").GetInt64());

        // An ordinance most vote against does not hold, and each kind comes to the vote once a week.
        Assert.Equal("ordinance_done", (await mayor.SendAsync("city.propose", new { x = a.X, y = a.Y, kind = "settlers" })).ErrorCode());
        var recycling = Politics(await mayor.SendAsync("city.propose", new { x = a.X, y = a.Y, kind = "recycling" })).GetProperty("ordinances")[1].GetProperty("id").GetString();
        Politics(await second.SendAsync("city.ordinance", new { x = b.X, y = b.Y, id = recycling, yes = false }));
        Politics(await third.SendAsync("city.ordinance", new { x = c.X, y = c.Y, id = recycling, yes = false }));
        factory.Time.Advance(TownHallRules.OrdinanceVote + TimeSpan.FromMinutes(1));
        var rejected = Politics(await mayor.SendAsync("city.get", new { x = a.X, y = a.Y })).GetProperty("ordinances")[1];
        Assert.False(rejected.GetProperty("inForce").GetBoolean());
        Assert.False(rejected.GetProperty("open").GetBoolean());
    }
}
