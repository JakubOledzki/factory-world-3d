using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Banks of companies in towns, with a database of their own: the banks of these tests are the only ones in their world.</summary>
public sealed class BankTests(ServerFactory factory) : ExportTestBase(factory), IClassFixture<ServerFactory>
{
    /// <summary>Where a bank's 5 × 5 ground fits by the station of a city plot.</summary>
    private static readonly (int X, int Y) BankTile = (PlotLayout.Spawn.X - 10, 30);

    private async Task<string> NameAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

    /// <summary>A city plot of the town.</summary>
    private static (int X, int Y) CityPlot(Town town, int skip = 0) =>
        Enumerable.Range(-4, 9).SelectMany(dy => Enumerable.Range(-4, 9).Select(dx => (X: town.X + dx, Y: town.Y + dy)))
            .Where(p => Map.BiomeAt(p.X, p.Y) == Biome.City && AllTowns.Of(p.X, p.Y) == town)
            .OrderBy(p => Math.Abs(p.X - town.X) + Math.Abs(p.Y - town.Y)).Skip(skip).First();

    /// <summary>A company of the client's that owns <paramref name="plots"/> and, unless <paramref name="research"/> is false, researched banking.</summary>
    private async Task<Guid> CompanyAsync(GameClient owner, bool research, params (int X, int Y)[] plots)
    {
        var company = (await owner.SendAsync("company.create", new { name = "Bank " + Guid.NewGuid().ToString("N")[..8] })).Payload().GetProperty("company");
        var id = company.GetProperty("id").GetGuid();
        await Factory.CompanyLevelAsync(owner.AccountId);
        foreach (var plot in plots)
        {
            await BuyAsync(owner, plot);
            Assert.Equal("company", (await owner.SendAsync("company.addPlot", new { x = plot.X, y = plot.Y })).Type());
        }

        if (research)
        {
            await ResearchAsync(id);
        }

        return id;
    }

    /// <summary>Marks banking (or another technology) as researched by the company, as if its members had brought its cost and waited.</summary>
    private async Task ResearchAsync(Guid company, string technology = TechTree.Banking)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                "INSERT INTO research.company_technologies (company_id, technology, starts_at, finishes_at) VALUES (@c, @t, @at, @at)", CancellationToken.None);
            command.Parameters.AddWithValue("c", company);
            command.Parameters.AddWithValue("t", technology);
            command.Parameters.AddWithValue("at", Factory.Time.GetUtcNow().AddMinutes(-1));
            await command.ExecuteNonQueryAsync();
        }, CancellationToken.None);
    }

    private static async Task<JsonElement> BankAsync(GameClient client, string type = "bank.get", object? payload = null)
    {
        var reply = await client.SendAsync(type, payload);
        Assert.True(reply.Type() == "bank", $"{type}: {reply}");
        return reply.Payload();
    }

    private static JsonElement Bank(JsonElement state, Guid id) =>
        state.GetProperty("banks").EnumerateArray().First(b => b.GetProperty("id").GetGuid() == id);

    private async Task ArriveAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<BankService>().ArriveAsync(CancellationToken.None);
    }

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void A_company_researches_banking_after_corporate_finance_and_puts_up_a_bank_of_5_by_5_and_branches_of_3_by_3_in_towns()
    {
        var banking = TechTree.ById[TechTree.Banking];
        Assert.True(banking.CompanyOnly);
        Assert.Contains(TechTree.CorporateFinance, banking.Requires);
        Assert.Equal([Items.Bank, Items.BankBranch], banking.Unlocks);
        Assert.Equal("company", TechTree.BranchOf[TechTree.Banking].Id);
        Assert.True(Items.IsCityOnly(Items.Bank));
        Assert.True(Items.IsCityOnly(Items.BankBranch));
        Assert.Equal(5, Footprints.Size(Items.Bank));
        Assert.Equal(3, Footprints.Size(Items.BankBranch));

        // A village has room for one bank building, a metropolis for five.
        Assert.Equal(1, BankRules.MaxBuildings(0));
        Assert.Equal(5, BankRules.MaxBuildings(CityRules.LevelNames.Count - 1));

        // Money travels like an export train: a tenth of a minute a plot over land, 30% quicker with the intercontinental railway.
        var route = new ExportRoute(new ExportPost(Guid.Empty, Items.Bank, 100, 0, Guid.Empty, 0), [], [new ExportLeg(0, 0, 100, 0, false, 100)]);
        Assert.Equal(TimeSpan.FromMinutes(10), BankRules.Trip(route, false));
        Assert.Equal(TimeSpan.FromMinutes(7), BankRules.Trip(route, true));
        Assert.Equal(TimeSpan.FromMinutes(1), BankRules.SameBank);
        Assert.Equal(TimeSpan.FromHours(6), BankRules.OtherBank);
        Assert.Equal(TimeSpan.FromMinutes(30), BankRules.NoPlot);
    }

    [Fact]
    public async Task Banking_costs_the_company_100_billion_brought_from_wallets_and_a_bank_is_only_bought_for_10_billion()
    {
        Assert.Equal(100_000_000_000_00, TechTree.ById[TechTree.Banking].CostCents);
        Assert.Equal(10_000_000_000_00, NpcPrices.Sells[Items.Bank]);
        Assert.Equal(5_000_000_000_00, NpcPrices.Sells[Items.BankBranch]);
        Assert.False(NpcPrices.Buys.ContainsKey(Items.Bank));
        Assert.DoesNotContain(Workshop.Recipes, r => Items.IsBank(r.Id));

        using var owner = await PlayerAsync();
        var company = await CompanyAsync(owner, false);
        await ResearchAsync(company, TechTree.CorporateFinance);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(owner.AccountId, 100_000_000_001_00, "test", CancellationToken.None);
        }

        async Task<long> BalanceAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(owner.AccountId, CancellationToken.None);
        }

        static JsonElement Money(JsonElement reply) =>
            reply.GetProperty("company").GetProperty("delivered").EnumerateArray().Single(d => d.GetProperty("id").GetString() == TechTree.Money);

        var before = await BalanceAsync();
        Assert.Equal("research", (await owner.SendAsync("research.companyStart", new { technology = TechTree.Banking })).Type());
        var part = (await owner.SendAsync("research.contribute", new { item = TechTree.Money, quantity = 40_000_000_000_00L })).Payload();
        Assert.Equal(40_000_000_000_00, Money(part).GetProperty("delivered").GetInt64());
        Assert.Equal(100_000_000_000_00, Money(part).GetProperty("needed").GetInt64());
        Assert.Equal(before - 40_000_000_000_00, await BalanceAsync());

        // Called off, the money goes back to the wallet.
        Assert.Equal("research", (await owner.SendAsync("research.companyCancel")).Type());
        Assert.Equal(before, await BalanceAsync());

        // No more is taken than it needs; with all of it in, the research starts.
        await owner.SendAsync("research.companyStart", new { technology = TechTree.Banking });
        var full = (await owner.SendAsync("research.contribute", new { item = TechTree.Money, quantity = 100_000_000_001_00L })).Payload();
        Assert.NotEqual(JsonValueKind.Null, full.GetProperty("company").GetProperty("finishesAt").ValueKind);
        Assert.Equal(before - 100_000_000_000_00, await BalanceAsync());
        Assert.Equal("no_research", (await owner.SendAsync("research.contribute", new { item = TechTree.Money, quantity = 1L })).ErrorCode());
    }

    // --- Banks, accounts and transfers ------------------------------------------------------------

    [Fact]
    public async Task Money_paid_in_and_out_travels_from_the_nearest_plot_to_the_nearest_branch_and_between_banks_takes_six_hours()
    {
        using var banker = await PlayerAsync();
        using var customer = await PlayerAsync();
        using var friend = await PlayerAsync();
        using var rival = await PlayerAsync();
        var (brussels, lille) = (TownNamed("Bruksela"), TownNamed("Lille"));
        var (brusselsPlot, lillePlot) = (CityPlot(brussels), CityPlot(lille));

        // Without banking researched, or off a company plot, no bank goes up.
        var company = await CompanyAsync(banker, false, brusselsPlot);
        await GiveAsync(banker, Items.Bank, 2);
        await GiveAsync(banker, Items.BankBranch, 2);
        Assert.Equal("locked", await PlaceErrorAsync(banker, brusselsPlot, Items.Bank, BankTile));
        await ResearchAsync(company);
        Assert.Equal("no_bank", await PlaceErrorAsync(banker, brusselsPlot, Items.BankBranch, BankTile));
        await BuyAsync(banker, lillePlot);
        Assert.Equal("bank_off_company", await PlaceErrorAsync(banker, lillePlot, Items.Bank, BankTile));
        Assert.Equal("company", (await banker.SendAsync("company.addPlot", new { x = lillePlot.X, y = lillePlot.Y })).Type());

        var head = await PlaceAsync(banker, brusselsPlot, Items.Bank, BankTile);
        var state = await BankAsync(banker);
        var bank = Bank(state, company);
        Assert.True(bank.GetProperty("mine").GetBoolean());
        Assert.True(state.GetProperty("companyBanks").GetBoolean());
        Assert.Equal("Bruksela", Assert.Single(bank.GetProperty("buildings").EnumerateArray()).GetProperty("town").GetString());
        Assert.Equal(company, (await banker.SendAsync("bank.building", new { id = head })).Payload().GetProperty("bank").GetGuid());

        // A company has one bank; a town has room for as many bank buildings as its level allows, a village for one.
        Assert.Equal("has_bank", await PlaceErrorAsync(banker, lillePlot, Items.Bank, BankTile));
        var rivalCompany = await CompanyAsync(rival, true, CityPlot(brussels, 1));
        await GiveAsync(rival, Items.Bank, 1);
        Assert.Equal("town_full", await PlaceErrorAsync(rival, CityPlot(brussels, 1), Items.Bank, BankTile));

        // A customer with a farm opens an account; what they pay in leaves the wallet now and gets there after the trip.
        var farm = Near(lille.X + 3, lille.Y, Biome.Farmland);
        await BuyAsync(customer, farm);
        Assert.Equal("no_account", (await customer.SendAsync("bank.deposit", new { bank = company, cents = 100 })).ErrorCode());
        state = await BankAsync(customer, "bank.open", new { bank = company });
        Assert.Equal(0, Bank(state, company).GetProperty("balanceCents").GetInt64());
        Assert.Equal("has_account", (await customer.SendAsync("bank.open", new { bank = company })).ErrorCode());
        var toBrussels = ExportRules.TripTime(CityRules.Distance(Map.Width, farm.X, farm.Y, brusselsPlot.X, brusselsPlot.Y), 0);
        var trip = Bank(state, company).GetProperty("trip");
        Assert.Equal((long)toBrussels.TotalSeconds, trip.GetProperty("seconds").GetInt64());
        Assert.Equal("Bruksela", trip.GetProperty("town").GetString());

        var wallet = await customer.BalanceAsync();
        Assert.Equal("bad_amount", (await customer.SendAsync("bank.deposit", new { bank = company, cents = 0 })).ErrorCode());
        state = await BankAsync(customer, "bank.deposit", new { bank = company, cents = 500_000_00L });
        Assert.Equal(wallet - 500_000_00, await customer.BalanceAsync());
        Assert.Equal(0, Bank(state, company).GetProperty("balanceCents").GetInt64());
        var transfer = Assert.Single(state.GetProperty("transfers").EnumerateArray());
        Assert.Equal("deposit", transfer.GetProperty("kind").GetString());
        Assert.Equal((Factory.Time.GetUtcNow() + toBrussels).ToUnixTimeMilliseconds(), transfer.GetProperty("arrivesAt").GetInt64());
        Factory.Time.Advance(toBrussels - TimeSpan.FromSeconds(1));
        Assert.Equal(0, Bank(await BankAsync(customer), company).GetProperty("balanceCents").GetInt64());
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        state = await BankAsync(customer);
        Assert.Equal(500_000_00, Bank(state, company).GetProperty("balanceCents").GetInt64());
        Assert.Empty(state.GetProperty("transfers").EnumerateArray());

        // A branch in Lille, nearer the farm, makes the trip shorter.
        await PlaceAsync(banker, lillePlot, Items.BankBranch, BankTile);
        var lilleAgain = CityPlot(lille, 2);
        await BuyAsync(banker, lilleAgain);
        Assert.Equal("company", (await banker.SendAsync("company.addPlot", new { x = lilleAgain.X, y = lilleAgain.Y })).Type());
        Assert.Equal("bank_in_town", await PlaceErrorAsync(banker, lilleAgain, Items.BankBranch, BankTile));
        var toLille = ExportRules.TripTime(CityRules.Distance(Map.Width, farm.X, farm.Y, lillePlot.X, lillePlot.Y), 0);
        Assert.True(toLille < toBrussels);
        trip = Bank(await BankAsync(customer), company).GetProperty("trip");
        Assert.Equal((long)toLille.TotalSeconds, trip.GetProperty("seconds").GetInt64());
        Assert.Equal("Lille", trip.GetProperty("town").GetString());

        // To an account in the same bank money takes a minute, to another bank six hours; the recipient needs an account there.
        var friendName = await NameAsync(friend);
        Assert.Equal("no_account_there", (await customer.SendAsync("bank.transfer",
            new { bank = company, to = friendName, toBank = company, cents = 1000_00L })).ErrorCode());
        await BankAsync(friend, "bank.open", new { bank = company });
        Assert.Equal("not_enough_money", (await customer.SendAsync("bank.transfer",
            new { bank = company, to = friendName, toBank = company, cents = 600_000_00L })).ErrorCode());
        state = await BankAsync(customer, "bank.transfer", new { bank = company, to = friendName, toBank = company, cents = 1000_00L });
        Assert.Equal(499_000_00, Bank(state, company).GetProperty("balanceCents").GetInt64());
        var incoming = Assert.Single((await BankAsync(friend)).GetProperty("transfers").EnumerateArray());
        Assert.True(incoming.GetProperty("incoming").GetBoolean());
        Assert.Equal(await NameAsync(customer), incoming.GetProperty("from").GetString());
        Factory.Time.Advance(BankRules.SameBank);
        await ArriveAsync();
        Assert.Equal(1000_00, Bank(await BankAsync(friend), company).GetProperty("balanceCents").GetInt64());

        // Lille, a village with the branch in it, has no room for the rival's bank either; Paris has.
        await BuyAsync(rival, CityPlot(lille, 1));
        Assert.Equal("company", (await rival.SendAsync("company.addPlot", new { x = CityPlot(lille, 1).X, y = CityPlot(lille, 1).Y })).Type());
        Assert.Equal("town_full", await PlaceErrorAsync(rival, CityPlot(lille, 1), Items.Bank, BankTile));
        var paris = CityPlot(TownNamed("Paryż"));
        await BuyAsync(rival, paris);
        Assert.Equal("company", (await rival.SendAsync("company.addPlot", new { x = paris.X, y = paris.Y })).Type());
        await PlaceAsync(rival, paris, Items.Bank, BankTile);
        await BankAsync(friend, "bank.open", new { bank = rivalCompany });
        await BankAsync(customer, "bank.transfer", new { bank = company, to = friendName, toBank = rivalCompany, cents = 2000_00L });
        Factory.Time.Advance(BankRules.OtherBank - TimeSpan.FromSeconds(1));
        await ArriveAsync();
        Assert.Equal(0, Bank(await BankAsync(friend), rivalCompany).GetProperty("balanceCents").GetInt64());
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2000_00, Bank(await BankAsync(friend), rivalCompany).GetProperty("balanceCents").GetInt64());

        // Taking out: the money leaves the account now and is in the wallet after the trip; the friend has no plot, so 30 minutes.
        Assert.Equal((long)BankRules.NoPlot.TotalSeconds, Bank(await BankAsync(friend), company).GetProperty("trip").GetProperty("seconds").GetInt64());
        Assert.Equal("not_empty", (await customer.SendAsync("bank.close", new { bank = company })).ErrorCode());
        wallet = await customer.BalanceAsync();
        state = await BankAsync(customer, "bank.withdraw", new { bank = company, cents = 497_000_00L });
        Assert.Equal(0, Bank(state, company).GetProperty("balanceCents").GetInt64());
        Assert.Equal(wallet, await customer.BalanceAsync());
        Factory.Time.Advance(toLille);
        await ArriveAsync();
        Assert.Equal(wallet + 497_000_00, await customer.BalanceAsync());
        state = await BankAsync(customer, "bank.close", new { bank = company });
        Assert.Equal(JsonValueKind.Null, Bank(state, company).GetProperty("balanceCents").ValueKind);

        // A building picked up leaves the accounts as they are; with none left money still gets out, by post.
        Assert.Equal("plot.pickedUp", (await banker.SendAsync("plot.pickup", new { x = brusselsPlot.X, y = brusselsPlot.Y, id = head })).Type());
        Assert.Equal(1000_00, Bank(await BankAsync(friend), company).GetProperty("balanceCents").GetInt64());
        Assert.Single(Bank(await BankAsync(friend), company).GetProperty("buildings").EnumerateArray());
    }

    [Fact]
    public async Task A_greedy_bot_opens_an_account_in_a_players_bank_and_keeps_part_of_its_savings_there()
    {
        using var banker = await PlayerAsync();
        var madrid = CityPlot(TownNamed("Madryt"));
        var company = await CompanyAsync(banker, true, madrid);
        await GiveAsync(banker, Items.Bank, 1);
        await PlaceAsync(banker, madrid, Items.Bank, BankTile);

        var engine = Factory.Services.GetRequiredService<BotEngine>();
        await engine.EnsureBotsAsync(CancellationToken.None);
        var bot = engine.Bots.First(b => b.Traits.Greed >= BotRules.BanksFrom);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 100_000_00, "test", CancellationToken.None);
        }

        var x = 100;
        while (!(await bot.Client.SendAsync("world.buy", new { x, y = 500 }, CancellationToken.None)).Is("world.bought"))
        {
            x++;
        }

        async Task<bool> StepUntilBankAsync()
        {
            for (var i = 0; i < 300; i++)
            {
                if (await engine.StepAsync(bot.Profile.Name, CancellationToken.None) == "bank")
                {
                    return true;
                }
            }

            return false;
        }

        // It opens an account first, and an hour later pays in.
        Assert.True(await StepUntilBankAsync());
        // Banks of the other tests may have more customers: it picks one of those then.
        var banks = (await bot.Client.SendAsync("bank.get", null, CancellationToken.None)).Payload.GetProperty("banks").EnumerateArray().ToList();
        var chosen = banks.Single(b => b.GetProperty("balanceCents").ValueKind == JsonValueKind.Number);
        Assert.Equal(banks.Max(b => b.GetProperty("customers").GetInt32()), chosen.GetProperty("customers").GetInt32());
        Factory.Time.Advance(BotRules.BankEvery + TimeSpan.FromSeconds(1));
        Assert.True(await StepUntilBankAsync());
        var view = (await bot.Client.SendAsync("bank.get", null, CancellationToken.None)).Payload;
        Assert.Contains(view.GetProperty("transfers").EnumerateArray(), t => t.GetProperty("kind").GetString() == "deposit");
    }
}
