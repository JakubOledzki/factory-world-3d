using System.Collections.Concurrent;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.Characters;
using FactoryWorld.Modules.Law;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.World;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Countries as a test sets them: which plot lies where, who is a citizen and holds which office, duties and embargoes.</summary>
public sealed class TestCountries : ICountries
{
    public ConcurrentDictionary<(int X, int Y), string> Plots { get; } = new();

    public ConcurrentDictionary<Guid, string> Citizens { get; } = new();

    public ConcurrentDictionary<(Guid Player, string Country), HashSet<string>> Offices { get; } = new();

    public ConcurrentDictionary<string, bool> Governed { get; } = new();

    public ConcurrentDictionary<(string From, string To), CountryTrade> Trade { get; } = new();

    public Task<string?> CountryOfPlotAsync(int x, int y, CancellationToken cancellationToken) =>
        Task.FromResult(Plots.TryGetValue((x, y), out var c) ? c : null);

    public Task<string?> CitizenshipAsync(Guid player, CancellationToken cancellationToken) =>
        Task.FromResult(Citizens.TryGetValue(player, out var c) ? c : null);

    public Task<IReadOnlySet<string>> OfficesAsync(Guid player, string country, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(Offices.TryGetValue((player, country), out var o) ? o : new HashSet<string>());

    public Task<Guid?> TreasuryAsync(string country, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(Governed.ContainsKey(country) ? CountryTreasury.Account(country) : null);

    public Task<CountryTrade> TradeAsync(string from, string to, CancellationToken cancellationToken) =>
        Task.FromResult(Trade.TryGetValue((from, to), out var t) ? t : CountryTrade.Free);
}

/// <summary>The real server with <see cref="TestCountries"/> in place of the countries players govern.</summary>
public sealed class LawServerFactory : ServerFactory
{
    public TestCountries Countries { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<ICountries>(Countries));
    }
}

public sealed class LawTests(LawServerFactory factory) : IClassFixture<LawServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private static int _plots;

    private static int _countries;

    /// <summary>Countries of this test alone: the tests of the class share one database.</summary>
    private readonly string Home = NewCountry();

    private readonly string Abroad = NewCountry();

    private static string NewCountry()
    {
        var n = Interlocked.Increment(ref _countries);
        return $"T{(char)('A' + n / 26 % 26)}{(char)('A' + n % 26)}";
    }

    private TestCountries Countries => factory.Countries;

    private static (int X, int Y) FreePlot()
    {
        var skip = Interlocked.Increment(ref _plots) + 3000;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == Biome.Grassland && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    /// <summary>A citizen of <paramref name="country"/> at the level the law takes seriously, with money.</summary>
    private async Task<(GameClient Client, string Name)> CitizenAsync(string? country = null, int level = LawRules.ProtectedBelowLevel)
    {
        country ??= Home;
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        if (level > 1)
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>()
                .AddXpAsync(client.AccountId, Leveling.TotalXpForLevel(level), CancellationToken.None);
        }

        Countries.Citizens[client.AccountId] = country;
        var name = (await scope.ServiceProvider.GetRequiredService<ICharacterNames>().NamesAsync([client.AccountId], CancellationToken.None))[client.AccountId];
        return (client, name);
    }

    private async Task<(int X, int Y)> PlotAsync(GameClient client, string country)
    {
        var (x, y) = FreePlot();
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        Countries.Plots[(x, y)] = country;
        return (x, y);
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private async Task<long> TreasuryAsync(string country)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(CountryTreasury.Account(country), CancellationToken.None);
    }

    private async Task FundAsync(string country, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(CountryTreasury.Account(country), cents, "test", CancellationToken.None);
    }

    private void Govern(params string[] countries)
    {
        foreach (var country in countries)
        {
            Countries.Governed[country] = true;
        }
    }

    private void Office(GameClient client, string country, params string[] offices) =>
        Countries.Offices[(client.AccountId, country)] = offices.ToHashSet();

    private static async Task<JsonElement> LawAsync(GameClient client, string type = "law.get", object? payload = null)
    {
        var reply = await client.SendAsync(type, payload ?? new { });
        Assert.True(reply.Type() == "law", reply.ToString());
        return reply.Payload();
    }

    /// <summary>A country with a government, a president and a police: the chief and officers of the given ranks.</summary>
    private async Task<(GameClient President, List<(GameClient Client, string Name)> Officers)> PoliceAsync(string country, params short[] ranks)
    {
        Govern(country);
        var (president, _) = await CitizenAsync(country);
        Office(president, country, CountryOffices.President);
        var officers = new List<(GameClient, string)>();
        var (chief, chiefName) = await CitizenAsync(country);
        await LawAsync(president, "law.appointChief", new { name = chiefName });
        officers.Add((chief, chiefName));
        foreach (var rank in ranks)
        {
            var (officer, name) = await CitizenAsync(country);
            await LawAsync(officer, "law.apply");
            await LawAsync(chief, "law.hire", new { name, yes = true });
            if (rank > Ranks.Cadet)
            {
                await LawAsync(chief, "law.setRank", new { name, rank });
            }

            officers.Add((officer, name));
        }

        return (president, officers);
    }

    private static async Task<JsonElement> SendAsync(GameClient client, (int X, int Y) from, (int X, int Y) to, string item, long quantity, bool smuggle = false)
    {
        Assert.Equal("station", (await client.SendAsync("station.load", new { x = from.X, y = from.Y, item, quantity })).Type());
        return await client.SendAsync("station.send", new { x = from.X, y = from.Y, toX = to.X, toY = to.Y, item, quantity, smuggle });
    }

    [Fact]
    public async Task A_declared_train_into_another_country_pays_its_duty_into_the_treasury_and_an_embargo_or_banned_goods_stop_it()
    {
        Govern(Home, Abroad);
        var (client, _) = await CitizenAsync(Abroad);
        var from = await PlotAsync(client, Abroad);
        var to = await PlotAsync(client, Home);
        Countries.Trade[(Abroad, Home)] = new CountryTrade(false, 20, new Dictionary<string, int> { [Items.Stone] = 0 });
        await GiveAsync(client, Items.Wood, 100);
        await GiveAsync(client, Items.Stone, 100);

        // 20% of what the trader pays for 100 wood goes to the treasury of the country the train goes to; stone is free of duty.
        var treasury = await TreasuryAsync(Home);
        var balance = await client.BalanceAsync();
        var sent = await SendAsync(client, from, to, Items.Wood, 100);
        Assert.Equal("station", sent.Type());
        var duty = NpcPrices.Buys[Items.Wood] * 100 * 20 / 100;
        Assert.Equal(treasury + duty, await TreasuryAsync(Home));
        Assert.True(balance - await client.BalanceAsync() > duty);
        Assert.Equal("station", (await SendAsync(client, from, to, Items.Stone, 50)).Type());
        Assert.Equal(treasury + duty, await TreasuryAsync(Home));

        // A train within one country pays nothing.
        var other = await PlotAsync(client, Home);
        await GiveAsync(client, Items.Wood, 10);
        Assert.Equal("station", (await SendAsync(client, other, to, Items.Wood, 10)).Type());
        Assert.Equal(treasury + duty, await TreasuryAsync(Home));

        // The government bans stone: a declared train may not bring it, unless the owner has a licence.
        var (president, _) = await CitizenAsync(Home);
        Office(president, Home, CountryOffices.President);
        await LawAsync(president, "law.setLaws", new { banned = Array.Empty<string>(), licensed = new[] { new { item = Items.Stone, feeCents = 1000L } }, forestProtection = false });
        Assert.Equal("banned_goods", (await SendAsync(client, from, to, Items.Stone, 10)).ErrorCode());
        Assert.Equal("station", (await client.SendAsync("station.unload", new { x = from.X, y = from.Y, item = Items.Stone, quantity = 10 })).Type());
        var law = await LawAsync(client, "law.buyLicence", new { country = Home, item = Items.Stone });
        Assert.Equal(Items.Stone, law.GetProperty("licences")[0].GetProperty("item").GetString());
        Assert.Equal(treasury + duty + 1000, await TreasuryAsync(Home));
        Assert.Equal("station", (await SendAsync(client, from, to, Items.Stone, 10)).Type());

        // Under an embargo nothing goes, declared.
        await GiveAsync(client, Items.Wood, 1);
        Countries.Trade[(Abroad, Home)] = new CountryTrade(true, 0, new Dictionary<string, int>());
        Assert.Equal("embargo", (await SendAsync(client, from, to, Items.Wood, 1)).ErrorCode());
    }

    [Fact]
    public async Task Smuggling_pays_no_duty_but_customs_catch_some_trains_seize_their_goods_for_the_treasury_and_open_a_case()
    {
        Govern(Home, Abroad);
        var (client, _) = await CitizenAsync(Abroad);
        var from = await PlotAsync(client, Abroad);
        var to = await PlotAsync(client, Home);
        Countries.Trade[(Abroad, Home)] = new CountryTrade(false, 30, new Dictionary<string, int>());
        await GiveAsync(client, Items.Wood, 1000);

        var treasury = await TreasuryAsync(Home);
        JsonElement station = default;
        var caught = false;
        for (var i = 0; i < 200 && !caught; i++)
        {
            station = await SendAsync(client, from, to, Items.Wood, 1, smuggle: true);
            Assert.Equal("station", station.Type());
            caught = (await LawAsync(client)).GetProperty("cases").GetArrayLength() > 0;
        }

        // Customs at the border (10% a train) caught one: no duty was ever paid, the wood is listed on the market for the treasury.
        Assert.True(caught);
        Assert.Equal(treasury, await TreasuryAsync(Home));
        var myCase = (await LawAsync(client)).GetProperty("cases")[0];
        Assert.Equal(Crimes.Smuggling, myCase.GetProperty("kind").GetString());
        Assert.Equal(CaseStatus.Open, myCase.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, myCase.GetProperty("openedBy").ValueKind);
        var board = (await client.SendAsync("market.item", new { item = Items.Wood })).Payload();
        Assert.Contains(board.GetProperty("listings").EnumerateArray(), l => l.GetProperty("quantity").GetInt64() == 1);
    }

    [Fact]
    public async Task The_chief_takes_on_players_who_apply_and_the_treasury_pays_officers_by_the_day()
    {
        var (president, officers) = await PoliceAsync(Home, Ranks.Constable);
        var (chief, chiefName) = officers[0];
        var (constable, constableName) = officers[1];
        var law = await LawAsync(chief);
        Assert.Equal("chief", law.GetProperty("police").GetProperty("rankName").GetString());
        Assert.Equal(2, law.GetProperty("police").GetProperty("officers").GetArrayLength());
        Assert.Equal("constable", (await LawAsync(constable)).GetProperty("police").GetProperty("rankName").GetString());

        // Only the chief hires, and nobody below the level, a foreigner or a player who did not apply.
        var (young, youngName) = await CitizenAsync(Home, level: 1);
        Assert.Equal("police_level_too_low", (await young.SendAsync("law.apply")).ErrorCode());
        var (foreigner, foreignerName) = await CitizenAsync(Abroad);
        Assert.Equal("no_government", (await foreigner.SendAsync("law.apply")).ErrorCode());
        Assert.Equal("no_application", (await chief.SendAsync("law.hire", new { name = foreignerName, yes = true })).ErrorCode());
        Assert.Equal("rank_too_low", (await constable.SendAsync("law.hire", new { name = youngName, yes = true })).ErrorCode());
        Assert.Equal("not_government", (await constable.SendAsync("law.appointChief", new { name = constableName })).ErrorCode());
        Assert.Equal("bad_rank", (await chief.SendAsync("law.setRank", new { name = constableName, rank = 3 })).ErrorCode());

        // A day later the treasury pays the salaries; with no money in it, nothing.
        factory.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await LawAsync(constable)).GetProperty("paidCents").GetInt64());
        await FundAsync(Home, 1000_00);
        factory.Time.Advance(TimeSpan.FromDays(1));
        var balance = await constable.BalanceAsync();
        Assert.Equal(Ranks.SalaryCents[Ranks.Constable], (await LawAsync(constable)).GetProperty("paidCents").GetInt64());
        Assert.Equal(balance + Ranks.SalaryCents[Ranks.Constable], await constable.BalanceAsync());
        Assert.Equal(0, (await LawAsync(constable)).GetProperty("paidCents").GetInt64());

        // The president fires an officer; one may also resign.
        await LawAsync(president, "law.fire", new { name = constableName });
        Assert.Equal(JsonValueKind.Null, (await LawAsync(constable)).GetProperty("police").ValueKind);
        await LawAsync(chief, "law.resign");
        Assert.Equal("not_officer", (await chief.SendAsync("law.resign")).ErrorCode());
        Assert.NotEqual(chiefName, youngName);
    }

    [Fact]
    public async Task A_player_the_police_leave_alone_may_not_smuggle_but_declares_and_pays_the_duty()
    {
        Govern(Home, Abroad);
        var (client, _) = await CitizenAsync(Abroad, level: 1);
        var from = await PlotAsync(client, Abroad);
        var to = await PlotAsync(client, Home);
        Countries.Trade[(Abroad, Home)] = new CountryTrade(false, 30, new Dictionary<string, int>());
        await GiveAsync(client, Items.Wood, 10);

        Assert.Equal("smuggling_protected", (await SendAsync(client, from, to, Items.Wood, 1, smuggle: true)).ErrorCode());
        Assert.Equal("station", (await SendAsync(client, from, to, Items.Wood, 1)).Type());
    }

    [Fact]
    public async Task An_officer_inspects_a_smuggling_train_a_constable_sentences_it_and_a_judge_overturns_the_sentence_on_appeal()
    {
        var (president, officers) = await PoliceAsync(Home, Ranks.Cadet, Ranks.Constable);
        Govern(Abroad);
        var (smuggler, smugglerName) = await CitizenAsync(Abroad);
        var from = await PlotAsync(smuggler, Abroad);
        var to = await PlotAsync(smuggler, Home);
        Countries.Trade[(Abroad, Home)] = new CountryTrade(false, 50, new Dictionary<string, int>());
        await GiveAsync(smuggler, Items.Wood, 1000);

        // A train customs let through waits for the police: they see it came undeclared, not what it carried.
        long crossing = 0;
        for (var i = 0; i < 50 && crossing == 0; i++)
        {
            await SendAsync(smuggler, from, to, Items.Wood, 100, smuggle: true);
            var waiting = (await LawAsync(officers[1].Client)).GetProperty("police").GetProperty("crossings").EnumerateArray()
                .FirstOrDefault(c => !c.GetProperty("inspected").GetBoolean());
            if (waiting.ValueKind == JsonValueKind.Object)
            {
                Assert.False(waiting.GetProperty("declared").GetBoolean());
                Assert.Equal(JsonValueKind.Null, waiting.GetProperty("goods").ValueKind);
                crossing = waiting.GetProperty("id").GetInt64();
            }
        }

        var found = await officers[1].Client.SendAsync("law.inspect", new { id = crossing });
        Assert.Equal("law.found", found.Type());
        Assert.Equal(smugglerName, found.Payload().GetProperty("suspect").GetString());
        var caseId = found.Payload().GetProperty("caseId").GetInt64();
        Assert.Equal("no_crossing", (await officers[1].Client.SendAsync("law.inspect", new { id = crossing })).ErrorCode());

        // A cadet may not sentence; a constable fines within the cap and bans trading.
        Assert.Equal("rank_too_low", (await officers[1].Client.SendAsync("law.sentence", new { id = caseId, fineCents = 100 })).ErrorCode());
        var cap = (await LawAsync(officers[2].Client)).GetProperty("police").GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("id").GetInt64() == caseId).GetProperty("maxFineCents").GetInt64();
        Assert.Equal(LawRules.FineCap(NpcPrices.Buys[Items.Wood] * 100 * 50 / 100), cap);
        Assert.Equal("bad_fine", (await officers[2].Client.SendAsync("law.sentence", new { id = caseId, fineCents = cap + 1 })).ErrorCode());
        var treasury = await TreasuryAsync(Home);
        var balance = await smuggler.BalanceAsync();
        await LawAsync(officers[2].Client, "law.sentence", new { id = caseId, fineCents = cap, tradeBanHours = 12, travelBanHours = 0, wantedHours = 24 });
        Assert.Equal(balance - cap, await smuggler.BalanceAsync());
        Assert.Equal(treasury + cap, await TreasuryAsync(Home));
        Assert.Equal("case_closed", (await officers[2].Client.SendAsync("law.dismiss", new { id = caseId })).ErrorCode());

        // Banned from trading: no trader, no market; on the wanted list.
        await GiveAsync(smuggler, Items.Stone, 1);
        Assert.Equal("trade_banned", (await smuggler.SendAsync("market.sell", new { item = Items.Stone, quantity = 1 })).ErrorCode());
        Assert.Equal("trade_banned", (await smuggler.SendAsync("market.list", new { item = Items.Stone, quantity = 1, priceCents = 100 })).ErrorCode());
        Assert.Contains((await LawAsync(officers[0].Client)).GetProperty("police").GetProperty("wanted").EnumerateArray(),
            w => w.GetProperty("name").GetString() == smugglerName);

        // The smuggler appeals; the president overturns it: the fine comes back and the bans end.
        await LawAsync(smuggler, "law.appeal", new { id = caseId });
        Assert.Equal("not_government", (await officers[0].Client.SendAsync("law.judge", new { id = caseId, yes = false })).ErrorCode());
        Assert.Single((await LawAsync(president)).GetProperty("court").GetProperty("appeals").EnumerateArray());
        await LawAsync(president, "law.judge", new { id = caseId, yes = false });
        Assert.Equal(balance, await smuggler.BalanceAsync());
        Assert.Equal(treasury, await TreasuryAsync(Home));
        Assert.Equal("market.traded", (await smuggler.SendAsync("market.sell", new { item = Items.Stone, quantity = 1 })).Type());
        Assert.Empty((await LawAsync(smuggler)).GetProperty("sanctions").EnumerateArray());
    }

    [Fact]
    public async Task The_fence_pays_more_than_the_trader_and_less_than_it_asks_and_a_detective_investigating_finds_the_sales()
    {
        var (_, officers) = await PoliceAsync(Home, Ranks.Detective);
        var detective = officers[1];
        var (dealer, dealerName) = await CitizenAsync(Home);
        await GiveAsync(dealer, Items.Dynamite, 20);

        var fence = await dealer.SendAsync("fence.get");
        var offer = fence.Payload().GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == Items.Dynamite);
        var price = offer.GetProperty("priceCents").GetInt64();
        Assert.True(price > NpcPrices.Buys[Items.Dynamite]);
        Assert.True(price < factory.Services.GetRequiredService<IMarketPrices>().SellsCents(Items.Dynamite));
        // Not even in the trader's cheapest sale does buying from him to sell to the fence pay.
        Assert.True(price <= new MarketPrices().CheapestSellsCents(Items.Dynamite) * 90 / 100);
        Assert.Equal("fence_not_traded", (await dealer.SendAsync("fence.sell", new { item = Items.Wood, quantity = 1 })).ErrorCode());

        var balance = await dealer.BalanceAsync();
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal("fence", (await dealer.SendAsync("fence.sell", new { item = Items.Dynamite, quantity = 1 })).Type());
        }

        Assert.Equal(balance + 20 * price, await dealer.BalanceAsync());

        // Each sale is a trace in the dealer's country: half of them come to light in an investigation.
        var investigated = await detective.Client.SendAsync("law.investigate", new { name = dealerName });
        Assert.Equal("law.investigated", investigated.Type());
        Assert.NotEmpty(investigated.Payload().GetProperty("cases").EnumerateArray());
        Assert.All((await LawAsync(dealer)).GetProperty("cases").EnumerateArray(), c => Assert.Equal(Crimes.BlackMarket, c.GetProperty("kind").GetString()));
        Assert.Equal("own_case", (await detective.Client.SendAsync("law.investigate", new { name = detective.Name })).ErrorCode());
    }

    [Fact]
    public async Task A_bribe_goes_to_the_officer_and_internal_affairs_find_it_and_a_hidden_compartment_costs_money()
    {
        var (_, officers) = await PoliceAsync(Home, Ranks.Constable, Ranks.Detective, Ranks.Detective);
        var corrupt = officers[1];
        var (briber, _) = await CitizenAsync(Home);
        var balance = await corrupt.Client.BalanceAsync();
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal("law.bribed", (await briber.SendAsync("law.bribe", new { name = corrupt.Name, cents = 100_00L })).Type());
        }

        Assert.Equal(balance + 12 * 100_00, await corrupt.Client.BalanceAsync());
        Assert.Equal("not_officer", (await briber.SendAsync("law.bribe", new { name = "nobody-at-all", cents = 100L })).ErrorCode());

        // A detective looks into the officer: the bribes come to light against both.
        var cases = new List<long>();
        foreach (var detective in officers.Skip(2))
        {
            cases.AddRange((await detective.Client.SendAsync("law.investigate", new { name = corrupt.Name })).Payload().GetProperty("cases")
                .EnumerateArray().Select(c => c.GetInt64()));
        }

        Assert.NotEmpty(cases);
        Assert.Contains((await LawAsync(corrupt.Client)).GetProperty("cases").EnumerateArray(), c => c.GetProperty("kind").GetString() == Crimes.Bribe);
        Assert.Contains((await LawAsync(briber)).GetProperty("cases").EnumerateArray(), c => c.GetProperty("kind").GetString() == Crimes.Bribe);

        balance = await briber.BalanceAsync();
        var fence = await briber.SendAsync("fence.compartment");
        Assert.True(fence.Payload().GetProperty("compartment").GetBoolean());
        Assert.Equal(balance - LawRules.CompartmentCents, await briber.BalanceAsync());
        Assert.Equal("already_bought", (await briber.SendAsync("fence.compartment")).ErrorCode());
    }

    [Fact]
    public async Task A_judge_grants_a_warrant_and_a_detective_searching_the_plot_finds_banned_goods_and_seizes_them()
    {
        var (president, officers) = await PoliceAsync(Home, Ranks.Detective);
        Office(president, Home, CountryOffices.President);
        var detective = officers[1];
        await LawAsync(president, "law.setLaws", new { banned = new[] { Items.UraniumOre }, licensed = Array.Empty<object>(), forestProtection = true });
        var (owner, ownerName) = await CitizenAsync(Home);
        var plot = await PlotAsync(owner, Home);
        await GiveAsync(owner, Items.UraniumOre, 7);

        // Without a granted warrant there is no search; only a detective asks for one.
        Assert.Equal("not_officer", (await owner.SendAsync("law.requestWarrant", new { x = plot.X, y = plot.Y })).ErrorCode());
        var law = await LawAsync(detective.Client, "law.requestWarrant", new { x = plot.X, y = plot.Y });
        var warrant = law.GetProperty("police").GetProperty("warrants")[0];
        Assert.False(warrant.GetProperty("granted").GetBoolean());
        var id = warrant.GetProperty("id").GetInt64();
        Assert.Equal("no_warrant", (await detective.Client.SendAsync("law.search", new { id })).ErrorCode());
        Assert.Equal("not_government", (await detective.Client.SendAsync("law.decideWarrant", new { id, yes = true })).ErrorCode());
        await LawAsync(president, "law.decideWarrant", new { id, yes = true });

        var found = await detective.Client.SendAsync("law.search", new { id });
        Assert.Equal("law.found", found.Type());
        Assert.Equal(ownerName, found.Payload().GetProperty("suspect").GetString());
        Assert.Equal(7, found.Payload().GetProperty("forbidden")[0].GetProperty("quantity").GetInt64());
        var caseId = found.Payload().GetProperty("caseId").GetInt64();
        Assert.Equal("no_warrant", (await detective.Client.SendAsync("law.search", new { id })).ErrorCode());

        await LawAsync(detective.Client, "law.sentence", new { id = caseId, fineCents = 0, seize = true });
        Assert.False((await owner.InventoryAsync()).ContainsKey(Items.UraniumOre));
        var board = (await owner.SendAsync("market.item", new { item = Items.UraniumOre })).Payload();
        Assert.Contains(board.GetProperty("listings").EnumerateArray(), l => l.GetProperty("quantity").GetInt64() == 7);
    }

    [Fact]
    public async Task Bad_requests_are_refused()
    {
        var (president, officers) = await PoliceAsync(Home, Ranks.Constable);
        var (stranger, _) = await CitizenAsync(Home);
        Assert.Equal("not_officer", (await stranger.SendAsync("law.inspect", new { id = 1 })).ErrorCode());
        Assert.Equal("no_case", (await officers[1].Client.SendAsync("law.sentence", new { id = 999_999 })).ErrorCode());
        Assert.Equal("bad_text", (await stranger.SendAsync("law.report", new { name = officers[0].Name, text = "" })).ErrorCode());
        Assert.Equal("no_player", (await stranger.SendAsync("law.report", new { name = "nobody-at-all", text = "x" })).ErrorCode());
        Assert.Equal("law.reported", (await stranger.SendAsync("law.report", new { name = officers[0].Name, text = "Przemyca węgiel." })).Type());
        Assert.Single((await LawAsync(officers[1].Client)).GetProperty("police").GetProperty("reports").EnumerateArray());
        Assert.Equal("bad_law_items", (await president.SendAsync("law.setLaws", new { banned = new[] { "no_such_item" } })).ErrorCode());
        Assert.Equal("not_government", (await stranger.SendAsync("law.setLaws", new { banned = Array.Empty<string>() })).ErrorCode());
        Assert.Equal("bad_amount", (await stranger.SendAsync("law.bribe", new { name = officers[0].Name, cents = 0L })).ErrorCode());
        Assert.Equal("bad_payload", (await stranger.SendAsync("law.sentence", "nope")).ErrorCode());
        var (young, _) = await CitizenAsync(Home, level: 1);
        Assert.Equal("fence_level_too_low", (await young.SendAsync("fence.sell", new { item = Items.Dynamite, quantity = 1 })).ErrorCode());
        var (nobody, _) = await CitizenAsync(Home);
        Countries.Citizens.TryRemove(nobody.AccountId, out _);
        Assert.Equal("no_country", (await nobody.SendAsync("law.apply")).ErrorCode());
    }

    [Fact]
    public async Task A_civic_bot_joins_the_police_and_as_a_detective_investigates_players_and_bots_alike()
    {
        // A strong player, so the bots' level cap lets a bot reach the level the police take.
        await CitizenAsync(Abroad, 20);
        var (president, officers) = await PoliceAsync(Home);
        var (chief, _) = officers[0];

        var engine = factory.Services.GetRequiredService<BotEngine>();
        await engine.EnsureBotsAsync(CancellationToken.None);
        var bot = engine.Bots.First(b => b.Traits.Civic >= BotRules.PoliceFrom);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>()
                .AddXpAsync(bot.Row.AccountId, Leveling.TotalXpForLevel(10), CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 100_000_00, "test", CancellationToken.None);
        }

        Countries.Citizens[bot.Row.AccountId] = Home;
        var (x, y) = FreePlot();
        Assert.True((await bot.Client.SendAsync("world.buy", new { x, y }, CancellationToken.None)).Is("world.bought"));
        Countries.Plots[(x, y)] = Home;

        async Task<bool> StepUntilLawAsync()
        {
            for (var i = 0; i < 300; i++)
            {
                if (await engine.StepAsync(bot.Profile.Name, CancellationToken.None) == "law")
                {
                    return true;
                }
            }

            return false;
        }

        // It applies; the chief takes it on as a detective.
        Assert.True(await StepUntilLawAsync());
        Assert.Contains((await LawAsync(chief)).GetProperty("police").GetProperty("applications").EnumerateArray(),
            a => a.GetProperty("name").GetString() == bot.Row.CharacterName);
        await LawAsync(chief, "law.hire", new { name = bot.Row.CharacterName, yes = true });
        await LawAsync(chief, "law.setRank", new { name = bot.Row.CharacterName, rank = Ranks.Detective });

        // It investigates someone it came across: a player of the town hall, or another bot.
        var (_, suspect) = await CitizenAsync(Home);
        engine.Commons.Seen(suspect);
        factory.Time.Advance(BotRules.LawEvery + TimeSpan.FromSeconds(1));
        // The bots below level 5 are protected: it finds that out and investigates the player in a few looks.
        for (var look = 0; look < 6; look++)
        {
            await StepUntilLawAsync();
            factory.Time.Advance(BotRules.LawEvery + TimeSpan.FromSeconds(1));
        }

        var desk = (await bot.Client.SendAsync("law.get", null, CancellationToken.None)).Payload.GetProperty("police");
        Assert.True(desk.GetProperty("investigationsLeft").GetInt64() < LawRules.Investigations, desk.ToString());
    }
}
