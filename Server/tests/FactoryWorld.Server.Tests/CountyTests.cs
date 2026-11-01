using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class CountyTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Towns AllTowns = Towns.Load();

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        return client;
    }

    /// <summary>A big town with a city plot of its own and two plots of the countryside its county takes in.</summary>
    private static (Town Town, (int X, int Y) City, (int X, int Y) Field, (int X, int Y) Other) County()
    {
        foreach (var town in AllTowns.All.Where(t => t.Population > 100_000).Skip(40))
        {
            (int, int)? city = null;
            var fields = new List<(int, int)>();
            for (var dy = -12; dy <= 12; dy++)
            {
                for (var dx = -12; dx <= 12; dx++)
                {
                    var (x, y) = (town.X + dx, town.Y + dy);
                    if (!Map.Contains(x, y) || Map.IsWater(x, y))
                    {
                        continue;
                    }

                    if (Map.BiomeAt(x, y) == Biome.City)
                    {
                        city ??= AllTowns.Of(x, y) == town ? (x, y) : null;
                    }
                    else if (AllTowns.CountyOf(x, y) == town)
                    {
                        fields.Add((x, y));
                    }
                }
            }

            if (city is { } c && fields.Count >= 2)
            {
                return (town, c, fields[0], fields[^1]);
            }
        }

        throw new InvalidOperationException("No county.");
    }

    private static JsonElement State(JsonElement reply)
    {
        Assert.True(reply.Type() == "city", reply.ToString());
        return reply.Payload().GetProperty("state");
    }

    private static async Task<long> PriceAsync(GameClient client, (int X, int Y) plot) =>
        (await client.SendAsync("world.plot", new { x = plot.X, y = plot.Y })).Payload().GetProperty("priceCents").GetInt64();

    [Fact]
    public async Task A_plot_in_the_countryside_belongs_to_the_county_of_its_town_and_its_owner_takes_part_in_the_town_hall()
    {
        var (town, city, field, other) = County();
        using var townsman = await PlayerAsync();
        using var farmer = await PlayerAsync();
        Assert.Equal("world.bought", (await townsman.SendAsync("world.buy", new { x = city.X, y = city.Y })).Type());
        Assert.Equal("world.bought", (await farmer.SendAsync("world.buy", new { x = field.X, y = field.Y })).Type());

        // The plot shows the county it lies in, and the town window opens there on the town hall alone.
        var plot = (await farmer.SendAsync("plot.enter", new { x = field.X, y = field.Y })).Payload();
        Assert.Equal(JsonValueKind.Null, plot.GetProperty("city").ValueKind);
        Assert.Equal(town.Name, plot.GetProperty("county").GetProperty("name").GetString());
        var state = State(await farmer.SendAsync("city.get", new { x = field.X, y = field.Y }));
        Assert.False(state.GetProperty("town").GetProperty("inCity").GetBoolean());
        Assert.Equal(town.Name, state.GetProperty("town").GetProperty("name").GetString());
        Assert.Equal("not_city", (await farmer.SendAsync("city.trash", new { x = field.X, y = field.Y, quantity = 1 })).ErrorCode());

        // The farmer votes and is elected like a townsman, and the mayor's laws hold on the countryside too.
        var name = (await farmer.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;
        State(await farmer.SendAsync("city.vote", new { x = field.X, y = field.Y, candidate = name }));
        State(await townsman.SendAsync("city.vote", new { x = city.X, y = city.Y, candidate = name }));
        var week = CityRules.WeekOf(factory.Time.GetUtcNow());
        factory.Time.Advance(CityRules.WeekStart(week + 1) - factory.Time.GetUtcNow() + TimeSpan.FromHours(1));
        Assert.True(State(await farmer.SendAsync("city.get", new { x = field.X, y = field.Y })).GetProperty("hall").GetProperty("isMayor").GetBoolean());

        var before = await PriceAsync(townsman, other);
        State(await farmer.SendAsync("city.policy", new { x = field.X, y = field.Y, taxPercent = 0, bonus = "plots" }));
        var after = await PriceAsync(townsman, other);
        Assert.InRange(after, (long)(before * CityRules.CheapPlots) - 1, (long)(before * CityRules.CheapPlots) + 1);

        // The mayor's own tax on the county's plots comes on top of the country's: a fifth is the mayor's pay, the rest the treasury's.
        Assert.Equal("bad_plot_tax", (await farmer.SendAsync("city.policy",
            new { x = field.X, y = field.Y, taxPercent = 0, bonus = "", plotTaxPerMille = CityRules.MaxPlotTaxPerMille + 1 })).ErrorCode());
        var hall = State(await farmer.SendAsync("city.policy", new { x = field.X, y = field.Y, taxPercent = 0, bonus = "", plotTaxPerMille = 2 }))
            .GetProperty("hall");
        Assert.Equal(2, hall.GetProperty("plotTaxPerMille").GetInt32());
        // Setting the rest of the policy without it keeps the plot tax.
        hall = State(await farmer.SendAsync("city.policy", new { x = field.X, y = field.Y, taxPercent = 5, bonus = "" })).GetProperty("hall");
        Assert.Equal(2, hall.GetProperty("plotTaxPerMille").GetInt32());
        Assert.Equal("world.bought", (await townsman.SendAsync("world.buy", new { x = other.X, y = other.Y })).Type());
        var pay = await farmer.BalanceAsync();
        factory.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.Equal("plot", (await townsman.SendAsync("plot.enter", new { x = city.X, y = city.Y })).Type());
        var treasury = State(await farmer.SendAsync("city.get", new { x = field.X, y = field.Y }))
            .GetProperty("hall").GetProperty("politics").GetProperty("treasuryCents").GetInt64();
        var earned = await farmer.BalanceAsync() - pay;
        Assert.True(treasury > 0, $"treasury {treasury}");
        Assert.Equal((treasury + earned) * TownHallRules.MayorSharePercent / 100, earned);
    }
}
