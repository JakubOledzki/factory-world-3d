using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Politics;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class PoliticsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Countries AllCountries = Countries.Load();
    private static readonly Lazy<Dictionary<string, List<(int X, int Y)>>> Land = new(() =>
    {
        // Plain land (no towns, which change prices) of every country, up to a few hundred plots each.
        var land = new Dictionary<string, List<(int, int)>>();
        for (var y = 0; y < Map.Height; y += 2)
        {
            for (var x = 0; x < Map.Width; x += 2)
            {
                if (Map.BiomeAt(x, y) is Biome.Forest or Biome.Farmland or Biome.Grassland && AllCountries.At(x, y) is { } country)
                {
                    var plots = land.TryGetValue(country.Code, out var found) ? found : land[country.Code] = [];
                    if (plots.Count < 300)
                    {
                        plots.Add((x, y));
                    }
                }
            }
        }

        return land;
    });

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

    /// <summary>Buys a free plot of plain land in the country.</summary>
    private static async Task<(int X, int Y)> BuyInAsync(GameClient client, string country)
    {
        foreach (var (x, y) in Land.Value[country])
        {
            var bought = await client.SendAsync("world.buy", new { x, y });
            if (bought.Type() == "world.bought")
            {
                return (x, y);
            }

            Assert.Equal("plot_taken", bought.ErrorCode());
        }

        throw new InvalidOperationException("No free plot in " + country);
    }

    private static JsonElement State(JsonElement reply)
    {
        Assert.True(reply.Type() == "country", reply.ToString());
        return reply.Payload().GetProperty("state");
    }

    private void NextTerm()
    {
        var term = NationRules.TermOf(factory.Time.GetUtcNow());
        factory.Time.Advance(NationRules.TermStart(term + 1) - factory.Time.GetUtcNow() + TimeSpan.FromHours(1));
    }

    private async Task<long> TreasuryAsync(string country)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(CountryTreasury.Account(country), CancellationToken.None);
    }

    private async Task<T> CountriesAsync<T>(Func<ICountries, Task<T>> ask)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await ask(scope.ServiceProvider.GetRequiredService<ICountries>());
    }

    /// <summary>A citizen of the country who is its president from the next term on.</summary>
    private async Task<GameClient> PresidentAsync(string country)
    {
        var president = await PlayerAsync();
        await BuyInAsync(president, country);
        State(await president.SendAsync("country.citizenship", new { country }));
        State(await president.SendAsync("country.stand", new { country, plotTaxPerMille = 5, foreignerFeePercent = 0, slogan = "" }));
        State(await president.SendAsync("country.vote", new { country, candidate = await NameAsync(president) }));
        return president;
    }

    [Fact]
    public void Countries_come_from_the_map_with_their_real_borders()
    {
        var warsaw = Map.PlotAt(52.23, 21.01);
        Assert.Equal("POL", AllCountries.At(warsaw.X, warsaw.Y)?.Code);
        Assert.Equal("Polska", AllCountries.Find("POL")?.Name);
        var berlin = Map.PlotAt(52.52, 13.40);
        Assert.Equal("DEU", AllCountries.At(berlin.X, berlin.Y)?.Code);
        var paris = Map.PlotAt(48.86, 2.35);
        Assert.Equal("FRA", AllCountries.At(paris.X, paris.Y)?.Code);
        // The sea and Antarctica belong to nobody.
        var atlantic = Map.PlotAt(30, -40);
        Assert.Null(AllCountries.At(atlantic.X, atlantic.Y));
        var pole = Map.PlotAt(-85, 0);
        Assert.Null(AllCountries.At(pole.X, pole.Y));
        Assert.True(AllCountries.All.Count > 150);
        Assert.True(AllCountries.Find("RUS")!.Plots > AllCountries.Find("POL")!.Plots);
    }

    [Fact]
    public async Task Citizens_elect_a_president_who_names_ministers_and_sets_the_laws_within_bounds()
    {
        using var president = await PlayerAsync();
        using var minister = await PlayerAsync();
        using var voter = await PlayerAsync();
        using var foreigner = await PlayerAsync();
        foreach (var citizen in new[] { president, minister, voter })
        {
            await BuyInAsync(citizen, "CZE");
        }

        await BuyInAsync(foreigner, "AUT");
        Assert.Equal("no_plot_here", (await foreigner.SendAsync("country.citizenship", new { country = "CZE" })).ErrorCode());
        Assert.Equal("no_country", (await foreigner.SendAsync("country.citizenship", new { country = "XXX" })).ErrorCode());
        State(await foreigner.SendAsync("country.citizenship", new { country = "AUT" }));
        foreach (var citizen in new[] { president, minister, voter })
        {
            Assert.Equal("CZE", State(await citizen.SendAsync("country.citizenship", new { country = "CZE" })).GetProperty("me").GetProperty("citizenship").GetString());
        }

        // Only citizens stand and vote, and only for those who stand; the deposit is paid once.
        var name = await NameAsync(president);
        Assert.Equal("not_citizen", (await foreigner.SendAsync("country.stand", new { country = "CZE", plotTaxPerMille = 4, foreignerFeePercent = 10, slogan = "" })).ErrorCode());
        Assert.Equal("not_candidate", (await voter.SendAsync("country.vote", new { country = "CZE", candidate = name })).ErrorCode());
        Assert.Equal("bad_law_value", (await president.SendAsync("country.stand", new { country = "CZE", plotTaxPerMille = 9, foreignerFeePercent = 10, slogan = "" })).ErrorCode());
        var before = await president.BalanceAsync();
        State(await president.SendAsync("country.stand", new { country = "CZE", plotTaxPerMille = 4, foreignerFeePercent = 10, slogan = "Niższe podatki" }));
        Assert.Equal(before - NationRules.DepositCents, await president.BalanceAsync());
        foreach (var citizen in new[] { president, minister, voter })
        {
            State(await citizen.SendAsync("country.vote", new { country = "CZE", candidate = name }));
        }

        Assert.Equal("not_citizen", (await foreigner.SendAsync("country.vote", new { country = "CZE", candidate = name })).ErrorCode());
        before = await president.BalanceAsync();
        NextTerm();

        // The winner rules the next term with the laws they promised, and gets the deposit back.
        var state = State(await voter.SendAsync("country.get", new { country = "CZE" }));
        var government = state.GetProperty("government");
        Assert.Equal(name, government.GetProperty("president").GetString());
        Assert.Equal(4, government.GetProperty("laws").GetProperty("plotTaxPerMille").GetInt32());
        Assert.Equal(10, government.GetProperty("laws").GetProperty("foreignerFeePercent").GetInt32());
        Assert.Equal(before + NationRules.DepositCents, await president.BalanceAsync());

        // The president names a finance minister, who sets the tariff but not the subsidy.
        Assert.Equal("not_in_office", (await minister.SendAsync("country.law", new { country = "CZE", law = "tariff", value = 15 })).ErrorCode());
        Assert.Equal("not_citizen", (await president.SendAsync("country.appoint", new { country = "CZE", office = CountryOffices.Finance, name = await NameAsync(foreigner) })).ErrorCode());
        Assert.Equal("bad_office", (await president.SendAsync("country.appoint", new { country = "CZE", office = "king", name = await NameAsync(minister) })).ErrorCode());
        State(await president.SendAsync("country.appoint", new { country = "CZE", office = CountryOffices.Finance, name = await NameAsync(minister) }));
        State(await minister.SendAsync("country.law", new { country = "CZE", law = "tariff", value = 15 }));
        State(await minister.SendAsync("country.law", new { country = "CZE", law = "itemTariff", value = 25, item = Items.Coal }));
        Assert.Equal("bad_law_value", (await minister.SendAsync("country.law", new { country = "CZE", law = "tariff", value = 31 })).ErrorCode());
        Assert.Equal("not_in_office", (await minister.SendAsync("country.law", new { country = "CZE", law = "subsidy", value = 0, item = "food" })).ErrorCode());
        var laws = State(await president.SendAsync("country.law", new { country = "CZE", law = "subsidy", value = 0, item = "food" }))
            .GetProperty("government").GetProperty("laws");
        Assert.Equal("food", laws.GetProperty("subsidy").GetString());
        Assert.Equal(15, laws.GetProperty("tariffPercent").GetInt32());

        // The police reads the tariffs, the offices and the treasury from here.
        var trade = await CountriesAsync(c => c.TradeAsync("AUT", "CZE", CancellationToken.None));
        Assert.False(trade.Embargo);
        Assert.Equal(15, trade.TariffPercent);
        Assert.Equal(25, trade.ItemTariffPercent[Items.Coal]);
        Assert.Contains(CountryOffices.Finance, await CountriesAsync(c => c.OfficesAsync(minister.AccountId, "CZE", CancellationToken.None)));
        Assert.Equal(CountryTreasury.Account("CZE"), await CountriesAsync(c => c.TreasuryAsync("CZE", CancellationToken.None)));
        Assert.Null(await CountriesAsync(c => c.TreasuryAsync("AUT", CancellationToken.None)));
        Assert.Equal("CZE", await CountriesAsync(c => c.CitizenshipAsync(voter.AccountId, CancellationToken.None)));

        // A foreigner pays the fee on top of a plot's price; it goes into the treasury, less the president's pay.
        (int X, int Y) free = default;
        var quote = 0L;
        foreach (var (x, y) in Land.Value["CZE"])
        {
            var plot = (await foreigner.SendAsync("world.plot", new { x, y })).Payload();
            if (plot.GetProperty("owner").ValueKind == JsonValueKind.Null)
            {
                (free, quote) = ((x, y), plot.GetProperty("priceCents").GetInt64());
                break;
            }
        }

        var price = PlotPrices.For(1, Map.BiomeAt(free.X, free.Y));
        Assert.Equal(price + price / 10, quote);
        var (foreignerBefore, presidentBefore, treasuryBefore) = (await foreigner.BalanceAsync(), await president.BalanceAsync(), await TreasuryAsync("CZE"));
        Assert.Equal("world.bought", (await foreigner.SendAsync("world.buy", new { x = free.X, y = free.Y })).Type());
        Assert.Equal(foreignerBefore - quote, await foreigner.BalanceAsync());
        var fee = price / 10;
        Assert.Equal(presidentBefore + fee * NationRules.PresidentSharePercent / 100, await president.BalanceAsync());
        Assert.Equal(treasuryBefore + fee - fee * NationRules.PresidentSharePercent / 100, await TreasuryAsync("CZE"));

        // Two thirds of the citizens recall the president: the ministers go with them and the laws are back to the defaults.
        var signed = (await minister.SendAsync("country.recall", new { country = "CZE" })).Payload();
        Assert.False(signed.GetProperty("result").GetProperty("recalled").GetBoolean());
        var recalled = (await voter.SendAsync("country.recall", new { country = "CZE" })).Payload();
        Assert.True(recalled.GetProperty("result").GetProperty("recalled").GetBoolean());
        government = recalled.GetProperty("state").GetProperty("government");
        Assert.Equal(JsonValueKind.Null, government.GetProperty("president").ValueKind);
        Assert.True(government.GetProperty("recalled").GetBoolean());
        Assert.Equal(NationRules.DefaultPlotTaxPerMille, government.GetProperty("laws").GetProperty("plotTaxPerMille").GetInt32());
        Assert.All(government.GetProperty("ministers").EnumerateArray(), m => Assert.Equal(JsonValueKind.Null, m.GetProperty("name").ValueKind));
        Assert.Equal("not_in_office", (await minister.SendAsync("country.law", new { country = "CZE", law = "tariff", value = 5 })).ErrorCode());
    }

    [Fact]
    public async Task Half_the_plot_tax_goes_into_the_treasury_which_pays_for_the_railway_and_subsidies()
    {
        using var owner = await PlayerAsync();
        await BuyInAsync(owner, "CHE");
        var (x, y) = await BuyInAsync(owner, "CHE");
        var worth = PlotPrices.For(1, Map.BiomeAt(x, y));
        Assert.Equal(0, await TreasuryAsync("CHE"));
        factory.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var info = (await owner.SendAsync("world.info")).Payload();
        var daily = worth * NationRules.DefaultPlotTaxPerMille / 1000;
        Assert.Equal(daily, info.GetProperty("tax").GetProperty("dailyCents").GetInt64());
        Assert.Equal(daily * NationRules.TreasurySharePercent / 100, await TreasuryAsync("CHE"));

        // The president builds the national railway with the treasury, which makes rail cheaper everywhere in the country.
        using var president = await PresidentAsync("CHE");
        NextTerm();
        Assert.Equal("treasury_short", (await president.SendAsync("country.invest", new { country = "CHE", kind = "railway" })).ErrorCode());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(CountryTreasury.Account("CHE"), 300_000_00, "test", CancellationToken.None);
        }

        var treasury = await TreasuryAsync("CHE");
        var state = State(await president.SendAsync("country.invest", new { country = "CHE", kind = "railway" }));
        Assert.Equal(1, state.GetProperty("investments").EnumerateArray().First(i => i.GetProperty("id").GetString() == "railway").GetProperty("level").GetInt32());
        Assert.Equal(treasury - NationRules.InvestmentCents[0], state.GetProperty("treasury").GetProperty("cents").GetInt64());
        var station = (await owner.SendAsync("station.get", new { x, y })).Payload();
        Assert.Equal(1 - NationRules.RailwayPerLevel, station.GetProperty("rates").GetProperty("feeFactor").GetDouble(), 6);

        // A subsidy pays a tenth more for the goods, from the treasury, while it has money.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var policy = scope.ServiceProvider.GetRequiredService<INationalPolicy>();
            Assert.Equal(0, await policy.SubsidizeAsync(owner.AccountId, "CHE", Items.Bread, 1000_00, CancellationToken.None));
            State(await president.SendAsync("country.law", new { country = "CHE", law = "subsidy", value = 0, item = "food" }));
            var before = await owner.BalanceAsync();
            Assert.Equal(100_00, await policy.SubsidizeAsync(owner.AccountId, "CHE", Items.Bread, 1000_00, CancellationToken.None));
            Assert.Equal(before + 100_00, await owner.BalanceAsync());
            Assert.Equal(0, await policy.SubsidizeAsync(owner.AccountId, "CHE", Items.Coal, 1000_00, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Countries_ally_and_put_embargoes_on_each_other()
    {
        using var belgium = await PresidentAsync("BEL");
        using var netherlands = await PresidentAsync("NLD");
        NextTerm();
        State(await netherlands.SendAsync("country.law", new { country = "NLD", law = "foreignerFee", value = 20 }));
        Assert.Equal("not_in_office", (await netherlands.SendAsync("country.alliance", new { country = "BEL", other = "NLD", on = true })).ErrorCode());
        Assert.Equal("same_country", (await belgium.SendAsync("country.alliance", new { country = "BEL", other = "BEL", on = true })).ErrorCode());

        // One offers, the other accepts by offering back.
        var offered = (await belgium.SendAsync("country.alliance", new { country = "BEL", other = "NLD", on = true })).Payload();
        Assert.False(offered.GetProperty("result").GetProperty("allied").GetBoolean());
        var got = State(await netherlands.SendAsync("country.get", new { country = "NLD" })).GetProperty("relations").GetProperty("offersGot");
        Assert.Equal("BEL", got[0].GetProperty("code").GetString());
        var accepted = (await netherlands.SendAsync("country.alliance", new { country = "NLD", other = "BEL", on = true })).Payload();
        Assert.True(accepted.GetProperty("result").GetProperty("allied").GetBoolean());
        Assert.Equal("BEL", accepted.GetProperty("state").GetProperty("relations").GetProperty("allies")[0].GetProperty("code").GetString());

        // Allies pay no fee for plots and no tariff.
        State(await netherlands.SendAsync("country.law", new { country = "NLD", law = "tariff", value = 20 }));
        Assert.Equal(0, (await CountriesAsync(c => c.TradeAsync("BEL", "NLD", CancellationToken.None))).TariffPercent);
        Assert.Equal(20, (await CountriesAsync(c => c.TradeAsync("DEU", "NLD", CancellationToken.None))).TariffPercent);
        var (x, y) = Land.Value["NLD"].Last();
        var price = (await belgium.SendAsync("world.plot", new { x, y })).Payload().GetProperty("priceCents").GetInt64();
        Assert.Equal(PlotPrices.For(1, Map.BiomeAt(x, y)), price);

        // An embargo waits until the alliance is broken; then the other's citizens buy no plots and send no goods.
        Assert.Equal("allied", (await netherlands.SendAsync("country.embargo", new { country = "NLD", other = "BEL", on = true })).ErrorCode());
        State(await netherlands.SendAsync("country.alliance", new { country = "NLD", other = "BEL", on = false }));
        State(await netherlands.SendAsync("country.embargo", new { country = "NLD", other = "BEL", on = true }));
        Assert.Equal("embargo", (await belgium.SendAsync("world.buy", new { x, y })).ErrorCode());
        Assert.True((await CountriesAsync(c => c.TradeAsync("BEL", "NLD", CancellationToken.None))).Embargo);
        Assert.Equal("embargo", (await belgium.SendAsync("country.alliance", new { country = "BEL", other = "NLD", on = true })).ErrorCode());
        var ranking = (await belgium.SendAsync("country.list")).Payload();
        Assert.Equal("BEL", ranking.GetProperty("citizenship").GetString());
        Assert.Contains(ranking.GetProperty("countries").EnumerateArray(), c => c.GetProperty("code").GetString() == "NLD" && c.GetProperty("citizens").GetInt64() == 1);
    }

    [Fact]
    public async Task Players_found_and_join_parties_whose_names_show_in_elections()
    {
        using var leader = await PlayerAsync();
        using var member = await PlayerAsync();
        var name = "Partia " + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal("bad_party_name", (await leader.SendAsync("party.create", new { name = "X" })).ErrorCode());
        var before = await leader.BalanceAsync();
        var party = (await leader.SendAsync("party.create", new { name })).Payload();
        Assert.Equal(before - NationRules.PartyCents, await leader.BalanceAsync());
        Assert.True(party.GetProperty("mine").GetProperty("isLeader").GetBoolean());
        Assert.Equal("in_party", (await leader.SendAsync("party.create", new { name = name + "2" })).ErrorCode());
        Assert.Equal("name_taken", (await member.SendAsync("party.create", new { name = name.ToUpperInvariant() })).ErrorCode());
        Assert.Equal("no_party", (await member.SendAsync("party.join", new { name = "Nie ma takiej" })).ErrorCode());
        var joined = (await member.SendAsync("party.join", new { name })).Payload();
        Assert.Equal(2, joined.GetProperty("mine").GetProperty("members").GetArrayLength());

        // The party shows next to a candidate's name.
        await BuyInAsync(leader, "DNK");
        State(await leader.SendAsync("country.citizenship", new { country = "DNK" }));
        var candidates = State(await leader.SendAsync("country.stand", new { country = "DNK", plotTaxPerMille = 5, foreignerFeePercent = 0, slogan = "" }))
            .GetProperty("election").GetProperty("candidates");
        Assert.Equal(name, candidates[0].GetProperty("party").GetString());

        // When the leader leaves, the member leads; when nobody is left, the party is gone.
        (await leader.SendAsync("party.leave", new { })).Payload();
        var left = (await member.SendAsync("party.get")).Payload();
        Assert.True(left.GetProperty("mine").GetProperty("isLeader").GetBoolean());
        var gone = (await member.SendAsync("party.leave", new { })).Payload();
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("mine").ValueKind);
        Assert.DoesNotContain(gone.GetProperty("parties").EnumerateArray(), p => p.GetProperty("name").GetString() == name);
        Assert.Equal("no_party", (await member.SendAsync("party.leave", new { })).ErrorCode());
    }
}
