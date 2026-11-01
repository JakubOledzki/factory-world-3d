using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Companies;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class CompanyEconomyTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private static int _plots;

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 3000;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private sealed record Player(GameClient Client, string Name) : IDisposable
    {
        public Guid Id => Client.AccountId;

        public Task<JsonElement> SendAsync(string type, object? payload = null) => Client.SendAsync(type, payload);

        public async Task<JsonElement> CompanyAsync(string type, object? payload = null)
        {
            var reply = await SendAsync(type, payload);
            Assert.True(reply.Type() == "company", $"{type}: {reply}");
            return reply.Payload().GetProperty("company");
        }

        public void Dispose() => Client.Dispose();
    }

    private async Task<Player> PlayerAsync(long cents = 1_000_000_00)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        var name = (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;
        return new Player(client, name);
    }

    private async Task GiveAsync(Player player, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player.Id, item, quantity, CancellationToken.None);
    }

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(sql, CancellationToken.None);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>Marks a company technology as researched, as if members had brought its cost and waited.</summary>
    private Task ResearchedAsync(Guid company, string technology) =>
        SqlAsync("INSERT INTO research.company_technologies (company_id, technology, starts_at, finishes_at) VALUES (@c, @t, @at, @at)",
            ("c", company), ("t", technology), ("at", factory.Time.GetUtcNow().AddMinutes(-1)));

    /// <summary>A company of an owner and members who joined it, with the roles given.</summary>
    private async Task<(Player Owner, Player[] Members, Guid Id, string Name)> CompanyAsync(params string[] roles)
    {
        var owner = await PlayerAsync();
        var name = "Spółka " + Guid.NewGuid().ToString("N")[..8];
        var company = await owner.CompanyAsync("company.create", new { name });
        var members = new List<Player>();
        foreach (var role in roles)
        {
            var member = await PlayerAsync();
            await owner.CompanyAsync("company.invite", new { name = member.Name });
            await member.CompanyAsync("company.accept", new { company = company.GetProperty("id").GetGuid() });
            if (role != Roles.Worker)
            {
                await owner.CompanyAsync("company.role", new { name = member.Name, role });
            }

            members.Add(member);
        }

        return (owner, members.ToArray(), company.GetProperty("id").GetGuid(), name);
    }

    private static JsonElement Economy(JsonElement company) => company.GetProperty("economy");

    private static long Cash(JsonElement company) => Economy(company).GetProperty("cashCents").GetInt64();

    [Fact]
    public async Task Members_pay_into_the_cash_for_shares_and_the_daily_dividend_goes_out_by_shares()
    {
        var (owner, members, _, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var worker = members[0];

        var founded = await owner.CompanyAsync("company.get");
        Assert.Equal(CompanyMoney.FounderShares, Economy(founded).GetProperty("shares").GetProperty("yours").GetInt64());
        Assert.Equal(CompanyMoney.MinSharePriceCents, Economy(founded).GetProperty("sharePriceCents").GetInt64());

        // An empty company's share costs the minimum, so 1000 $ buys the worker as many shares as the founder has.
        var paid = await worker.CompanyAsync("company.deposit", new { cents = 1000_00 });
        Assert.Equal(1000_00, Cash(paid));
        Assert.Equal(1000, Economy(paid).GetProperty("shares").GetProperty("yours").GetInt64());
        Assert.Equal(1_000_000_00 - 1000_00, await worker.Client.BalanceAsync());
        Assert.Equal("deposit", paid.GetProperty("log")[0].GetProperty("action").GetString());

        // The cash split over 2000 shares is 0.50 $ a share, still below the floor.
        Assert.Equal(CompanyMoney.MinSharePriceCents, Economy(paid).GetProperty("sharePriceCents").GetInt64());

        // A worker can't pay out; selling from the warehouse earns.
        Assert.Equal("not_allowed", (await worker.SendAsync("company.withdraw", new { cents = 100 })).ErrorCode());
        await GiveAsync(worker, Items.IronIngot, 100);
        await worker.CompanyAsync("company.put", new { item = Items.IronIngot, quantity = 100 });
        Assert.Equal("not_allowed", (await worker.SendAsync("company.sell", new { item = Items.IronIngot, quantity = 100 })).ErrorCode());
        var sold = await owner.CompanyAsync("company.sell", new { item = Items.IronIngot, quantity = 100 });
        var earned = NpcPrices.Buys[Items.IronIngot] * 100;
        Assert.Equal(1000_00 + earned, Cash(sold));
        Assert.Equal(earned, Economy(sold).GetProperty("incomeCents").GetInt64());

        // A day on, 20% of what it earned goes out, half to each.
        var before = await worker.Client.BalanceAsync();
        factory.Time.Advance(CompanyMoney.DividendEvery);
        var after = await owner.CompanyAsync("company.get");
        var dividend = earned * CompanyMoney.DefaultDividendPercent / 100;
        Assert.Equal(before + dividend / 2, await worker.Client.BalanceAsync());
        Assert.Equal(1000_00 + earned - dividend, Cash(after));
        Assert.Equal(0, Economy(after).GetProperty("incomeCents").GetInt64());
        Assert.Equal("dividend", after.GetProperty("log")[0].GetProperty("action").GetString());

        // The owner pays out and buys from the trader into the warehouse.
        var cash = Cash(after);
        var withdrawn = await owner.CompanyAsync("company.withdraw", new { cents = 100_00 });
        Assert.Equal(cash - 100_00, Cash(withdrawn));
        Assert.Equal("company_short", (await owner.SendAsync("company.withdraw", new { cents = 100_000_000 })).ErrorCode());
        var bought = await owner.CompanyAsync("company.buy", new { item = Items.Wood, quantity = 10 });
        Assert.Equal(cash - 100_00 - NpcPrices.Sells[Items.Wood] * 10, Cash(bought));
        Assert.Contains(bought.GetProperty("warehouse").GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.Wood);
        Assert.Equal("bad_percent", (await owner.SendAsync("company.dividend", new { percent = 150 })).ErrorCode());
        Assert.Equal(50, Economy(await owner.CompanyAsync("company.dividend", new { percent = 50 })).GetProperty("dividendPercent").GetInt32());
    }

    [Fact]
    public async Task New_roles_split_the_work_a_treasurer_pays_a_manager_builds_on_their_plot_a_logistician_runs_trains()
    {
        var (owner, members, _, _) = await CompanyAsync(Roles.Treasurer, Roles.Manager, Roles.Logistician);
        using var o = owner;
        using var treasurer = members[0];
        using var manager = members[1];
        using var logistician = members[2];

        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await owner.SendAsync("world.buy", new { x, y })).Type());
        await factory.CompanyLevelAsync(owner.Id);
        var added = await owner.CompanyAsync("company.addPlot", new { x, y });
        // The plot buys its owner shares worth what it cost.
        Assert.True(Economy(added).GetProperty("shares").GetProperty("yours").GetInt64() > CompanyMoney.FounderShares);

        async Task<string[]> RightsAsync(Player player) =>
            (await player.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("rights").EnumerateArray().Select(r => r.GetString()!).ToArray();

        Assert.Equal(["work", "take", "visit"], await RightsAsync(treasurer));
        Assert.Equal(["work", "visit"], await RightsAsync(manager));
        Assert.Equal(["work", "take", "routes", "visit"], await RightsAsync(logistician));

        Assert.Equal("not_member", (await owner.SendAsync("company.assign", new { x, y, name = "nobody_here" })).ErrorCode());
        var assigned = await owner.CompanyAsync("company.assign", new { x, y, name = manager.Name });
        Assert.Equal(manager.Name, assigned.GetProperty("plots")[0].GetProperty("manager").GetString());
        Assert.Equal(["work", "take", "build", "visit"], await RightsAsync(manager));
        Assert.Equal("not_allowed", (await manager.SendAsync("company.assign", new { x, y, name = "" })).ErrorCode());

        // The treasurer manages money; partners' and managers' hands are off the cash.
        await owner.CompanyAsync("company.deposit", new { cents = 500_00 });
        Assert.Equal(400_00, Cash(await treasurer.CompanyAsync("company.withdraw", new { cents = 100_00 })));
        Assert.Equal("not_allowed", (await manager.SendAsync("company.withdraw", new { cents = 100 })).ErrorCode());
        Assert.Equal("not_allowed", (await logistician.SendAsync("company.task.create", new { item = Items.Wood, quantity = 1, wageCents = 0 })).ErrorCode());

        // Only the owner and partners pick research and projects.
        Assert.Equal("not_allowed", (await treasurer.SendAsync("research.companyStart", new { technology = TechTree.Logistics })).ErrorCode());
        Assert.Equal("not_allowed", (await treasurer.SendAsync("company.project.start", new { project = CompanyPerks.Railway })).ErrorCode());
    }

    [Fact]
    public async Task A_company_researches_a_technology_once_for_every_member_who_brings_its_cost_together()
    {
        var (owner, members, id, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var worker = members[0];

        Assert.Equal("company_only", (await owner.SendAsync("research.start", new { technology = TechTree.Megaprojects })).ErrorCode());
        var started = await owner.SendAsync("research.companyStart", new { technology = TechTree.Logistics });
        Assert.Equal("research", started.Type());
        var company = started.Payload().GetProperty("company");
        Assert.Equal(TechTree.Logistics, company.GetProperty("technology").GetString());
        Assert.Equal(JsonValueKind.Null, company.GetProperty("startsAt").ValueKind);
        Assert.Equal("research_busy", (await owner.SendAsync("research.companyStart", new { technology = TechTree.Milling })).ErrorCode());

        // Each brings part of the cost; more than it needs stays with them.
        await GiveAsync(worker, Items.IronIngot, 15);
        await GiveAsync(owner, Items.Wood, 20);
        await worker.SendAsync("research.contribute", new { item = Items.IronIngot, quantity = 15 });
        Assert.Equal(5, (await worker.Client.InventoryAsync())[Items.IronIngot]);
        Assert.Equal("not_needed", (await worker.SendAsync("research.contribute", new { item = Items.IronIngot, quantity = 1 })).ErrorCode());
        var full = (await owner.SendAsync("research.contribute", new { item = Items.Wood, quantity = 20 })).Payload().GetProperty("company");
        Assert.NotEqual(JsonValueKind.Null, full.GetProperty("finishesAt").ValueKind);

        Assert.Equal("locked", (await worker.SendAsync("market.buy", new { item = Items.Belt, quantity = 1 })).ErrorCode());
        factory.Time.Advance(TechTree.ById[TechTree.Logistics].Time);
        var tree = (await worker.SendAsync("research.get")).Payload().GetProperty("technologies").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!);
        Assert.Equal("done", tree[TechTree.Logistics].GetProperty("status").GetString());
        Assert.True(tree[TechTree.Logistics].GetProperty("shared").GetBoolean());
        Assert.True(tree[TechTree.Megaprojects].GetProperty("companyOnly").GetBoolean());
        Assert.Equal("market.traded", (await worker.SendAsync("market.buy", new { item = Items.Belt, quantity = 1 })).Type());

        // Someone who leaves loses what the company researched.
        await worker.CompanyAsync("company.leave");
        Assert.Equal("locked", (await worker.SendAsync("market.buy", new { item = Items.Belt, quantity = 1 })).ErrorCode());

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ICompanyTechnologies>().ResearchedAsync(id, TechTree.Logistics, CancellationToken.None));
    }

    [Fact]
    public async Task Members_build_a_big_project_together_and_the_railway_makes_their_trains_cheaper()
    {
        var (owner, members, id, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var worker = members[0];

        Assert.Equal("research_needed", (await owner.SendAsync("company.project.start", new { project = CompanyPerks.Railway })).ErrorCode());
        await ResearchedAsync(id, TechTree.Megaprojects);
        var started = await owner.CompanyAsync("company.project.start", new { project = CompanyPerks.Railway });
        Assert.True(Economy(started).GetProperty("projects").EnumerateArray().First(p => p.GetProperty("id").GetString() == CompanyPerks.Railway)
            .GetProperty("started").GetBoolean());
        Assert.Equal("project_started", (await owner.SendAsync("company.project.start", new { project = CompanyPerks.Railway })).ErrorCode());
        Assert.Equal("project_closed", (await owner.SendAsync("company.project.give",
            new { project = CompanyPerks.Harbour, item = Items.Concrete, quantity = 1 })).ErrorCode());

        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await worker.SendAsync("world.buy", new { x, y })).Type());
        Assert.Equal(1.0, (await worker.SendAsync("station.get", new { x, y })).Payload().GetProperty("rates").GetProperty("feeFactor").GetDouble());

        var project = BigProjects.ById[CompanyPerks.Railway];
        foreach (var (item, quantity) in project.Parts)
        {
            await GiveAsync(worker, item, quantity + 5);
            await worker.CompanyAsync("company.project.give", new { project = project.Id, item, quantity = quantity + 5 });
            Assert.Equal((item, 5L), (item, (await worker.Client.InventoryAsync()).GetValueOrDefault(item)));
        }

        var building = Economy(await owner.CompanyAsync("company.get")).GetProperty("projects").EnumerateArray()
            .First(p => p.GetProperty("id").GetString() == project.Id);
        Assert.NotEqual(JsonValueKind.Null, building.GetProperty("builtAt").ValueKind);
        Assert.Equal(1.0, (await worker.SendAsync("station.get", new { x, y })).Payload().GetProperty("rates").GetProperty("feeFactor").GetDouble());

        factory.Time.Advance(project.Build);
        Assert.Equal(CompanyPerks.RailwayFactor, (await worker.SendAsync("station.get", new { x, y })).Payload().GetProperty("rates").GetProperty("feeFactor").GetDouble(), 3);
        Assert.Equal("projectDone", (await owner.CompanyAsync("company.get")).GetProperty("log")[0].GetProperty("action").GetString());
    }

    /// <summary>Marks a big project as built, as if members had brought its parts and waited.</summary>
    private Task BuiltAsync(Guid company, string project) =>
        SqlAsync("INSERT INTO companies.projects (company_id, project, started_at, built_at, rewarded) VALUES (@c, @p, @at, @at, true)",
            ("c", company), ("p", project), ("at", factory.Time.GetUtcNow().AddMinutes(-1)));

    [Fact]
    public async Task The_space_projects_go_one_after_another_and_the_orbit_and_station_help_every_member()
    {
        var (owner, members, id, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var worker = members[0];
        await ResearchedAsync(id, TechTree.Megaprojects);

        // The mission needs the spaceport, the station the mission.
        Assert.Equal("project_locked", (await owner.SendAsync("company.project.start", new { project = CompanyPerks.Orbit })).ErrorCode());
        Assert.Equal("project_locked", (await owner.SendAsync("company.project.start", new { project = CompanyPerks.Station })).ErrorCode());
        var projects = Economy(await owner.CompanyAsync("company.get")).GetProperty("projects").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal("Kosmodrom", projects[CompanyPerks.Orbit].GetProperty("requires")[0].GetString());

        await BuiltAsync(id, CompanyPerks.Spaceport);
        await owner.CompanyAsync("company.project.start", new { project = CompanyPerks.Orbit });
        Assert.Equal("project_locked", (await owner.SendAsync("company.project.start", new { project = CompanyPerks.Station })).ErrorCode());

        // A satellite on orbit: trains of members cost 20% less.
        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await worker.SendAsync("world.buy", new { x, y })).Type());
        await SqlAsync("UPDATE companies.projects SET built_at = @at WHERE company_id = @c AND project = @p",
            ("c", id), ("p", CompanyPerks.Orbit), ("at", factory.Time.GetUtcNow().AddMinutes(-1)));
        Assert.Equal(CompanyPerks.OrbitFactor, (await worker.SendAsync("station.get", new { x, y })).Payload().GetProperty("rates").GetProperty("feeFactor").GetDouble(), 3);

        // The station on orbit: research of members takes 20% less time.
        await owner.CompanyAsync("company.project.start", new { project = CompanyPerks.Station });
        await SqlAsync("UPDATE companies.projects SET built_at = @at WHERE company_id = @c AND project = @p",
            ("c", id), ("p", CompanyPerks.Station), ("at", factory.Time.GetUtcNow().AddMinutes(-1)));
        foreach (var (item, quantity) in TechTree.ById[TechTree.Logistics].Cost)
        {
            await GiveAsync(worker, item, quantity);
        }

        var started = (await worker.SendAsync("research.start", new { technology = TechTree.Logistics })).Payload();
        var logistics = started.GetProperty("technologies").EnumerateArray().First(t => t.GetProperty("id").GetString() == TechTree.Logistics);
        Assert.Equal(TechTree.ById[TechTree.Logistics].Time.TotalMilliseconds * CompanyPerks.StationResearchFactor,
            logistics.GetProperty("finishesAt").GetInt64() - logistics.GetProperty("startsAt").GetInt64());
    }

    [Fact]
    public async Task A_finished_rig_lands_in_the_warehouse()
    {
        var (owner, _, id, _) = await CompanyAsync();
        using var o = owner;
        await ResearchedAsync(id, TechTree.Megaprojects);
        await owner.CompanyAsync("company.project.start", new { project = "offshore_rig" });
        var project = BigProjects.ById["offshore_rig"];
        foreach (var (item, quantity) in project.Parts)
        {
            // Partners (and the owner) may bring parts from the warehouse too.
            await GiveAsync(owner, item, quantity);
            await owner.CompanyAsync("company.put", new { item, quantity });
            await owner.CompanyAsync("company.project.give", new { project = project.Id, item, quantity, fromWarehouse = true });
        }

        factory.Time.Advance(project.Build);
        var items = (await owner.CompanyAsync("company.get")).GetProperty("warehouse").GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());
        Assert.Equal(2, items[Items.OffshorePlatform]);
    }

    [Fact]
    public async Task Weekly_orders_pay_the_cash_once_the_whole_quantity_is_delivered_from_the_warehouse()
    {
        var (owner, members, _, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var worker = members[0];
        var orders = Economy(await owner.CompanyAsync("company.get")).GetProperty("orders");
        Assert.Equal(CompanyOrders.PerWeek, orders.GetArrayLength());
        var order = orders[0];
        var item = order.GetProperty("id").GetString()!;
        var quantity = order.GetProperty("quantity").GetInt64();
        Assert.Equal((long)Math.Ceiling(quantity * NpcPrices.Buys[item] * CompanyOrders.RewardFactor), order.GetProperty("rewardCents").GetInt64());

        await GiveAsync(owner, item, quantity);
        await owner.CompanyAsync("company.put", new { item, quantity });
        Assert.Equal("not_allowed", (await worker.SendAsync("company.order.deliver", new { slot = 0, quantity = 1 })).ErrorCode());
        var part = await owner.CompanyAsync("company.order.deliver", new { slot = 0, quantity = quantity - 1 });
        Assert.Equal(0, Cash(part));
        var done = await owner.CompanyAsync("company.order.deliver", new { slot = 0, quantity = 10 });
        Assert.Equal(order.GetProperty("rewardCents").GetInt64(), Cash(done));
        Assert.Equal(quantity, Economy(done).GetProperty("orders")[0].GetProperty("delivered").GetInt64());
        Assert.Equal("order_done", (await owner.SendAsync("company.order.deliver", new { slot = 0, quantity = 1 })).ErrorCode());
    }

    [Fact]
    public async Task A_task_pays_its_wage_piece_by_piece_and_a_cancelled_one_gives_back_the_rest()
    {
        var (owner, members, _, _) = await CompanyAsync(Roles.Worker, Roles.Worker);
        using var o = owner;
        using var worker = members[0];
        using var other = members[1];
        await owner.CompanyAsync("company.deposit", new { cents = 1000_00 });

        Assert.Equal("company_short", (await owner.SendAsync("company.task.create",
            new { item = Items.Stone, quantity = 100, wageCents = 2000_00, name = worker.Name })).ErrorCode());
        var created = await owner.CompanyAsync("company.task.create", new { item = Items.Stone, quantity = 100, wageCents = 500_00, name = worker.Name });
        Assert.Equal(500_00, Cash(created));
        var task = Economy(created).GetProperty("tasks")[0];
        var taskId = task.GetProperty("id").GetInt64();
        Assert.Equal(worker.Name, task.GetProperty("assignee").GetString());

        await GiveAsync(worker, Items.Stone, 100);
        await GiveAsync(other, Items.Stone, 100);
        Assert.Equal("not_your_task", (await other.SendAsync("company.task.deliver", new { id = taskId, quantity = 10 })).ErrorCode());
        var before = await worker.Client.BalanceAsync();
        await worker.CompanyAsync("company.task.deliver", new { id = taskId, quantity = 40 });
        Assert.Equal(before + 200_00, await worker.Client.BalanceAsync());
        var done = await worker.CompanyAsync("company.task.deliver", new { id = taskId, quantity = 100 });
        Assert.Equal(before + 500_00, await worker.Client.BalanceAsync());
        Assert.False((await worker.Client.InventoryAsync()).ContainsKey(Items.Stone));
        Assert.True(Economy(done).GetProperty("tasks")[0].GetProperty("done").GetBoolean());
        Assert.Equal(-500_00, Economy(done).GetProperty("incomeCents").GetInt64());

        // An open task anyone may do; called off, the wage comes back.
        var open = await owner.CompanyAsync("company.task.create", new { item = Items.Stone, quantity = 10, wageCents = 100_00 });
        var openId = Economy(open).GetProperty("tasks").EnumerateArray().First(t => !t.GetProperty("done").GetBoolean()).GetProperty("id").GetInt64();
        await other.CompanyAsync("company.task.deliver", new { id = openId, quantity = 5 });
        var cancelled = await owner.CompanyAsync("company.task.cancel", new { id = openId });
        Assert.Equal(500_00 - 50_00, Cash(cancelled));
        Assert.Equal("no_task", (await other.SendAsync("company.task.deliver", new { id = openId, quantity = 1 })).ErrorCode());
    }

    [Fact]
    public async Task A_deal_between_companies_moves_goods_and_money_each_day_and_a_short_supplier_pays_a_penalty()
    {
        var (supplier, _, _, supplierName) = await CompanyAsync();
        var (buyer, _, _, buyerName) = await CompanyAsync();
        using var s = supplier;
        using var b = buyer;
        await buyer.CompanyAsync("company.deposit", new { cents = 1000_00 });
        await supplier.CompanyAsync("company.deposit", new { cents = 100_00 });
        // No dividends, so the cash shows only the deal.
        await buyer.CompanyAsync("company.dividend", new { percent = 0 });
        await supplier.CompanyAsync("company.dividend", new { percent = 0 });
        await GiveAsync(supplier, Items.Coal, 150);
        await supplier.CompanyAsync("company.put", new { item = Items.Coal, quantity = 150 });

        Assert.Equal("no_such_company", (await supplier.SendAsync("company.deal.offer",
            new { company = supplierName, item = Items.Coal, perDay = 100, priceCents = 300_00, days = 3, sell = true })).ErrorCode());
        var offered = await supplier.CompanyAsync("company.deal.offer",
            new { company = buyerName.ToUpperInvariant(), item = Items.Coal, perDay = 100, priceCents = 300_00, days = 3, sell = true });
        var deal = Economy(offered).GetProperty("deals")[0];
        Assert.Equal("offered", deal.GetProperty("state").GetString());
        var dealId = deal.GetProperty("id").GetInt64();
        Assert.Equal("no_deal", (await supplier.SendAsync("company.deal.accept", new { id = dealId })).ErrorCode());
        var accepted = await buyer.CompanyAsync("company.deal.accept", new { id = dealId });
        Assert.Equal("active", Economy(accepted).GetProperty("deals")[0].GetProperty("state").GetString());
        Assert.False(Economy(accepted).GetProperty("deals")[0].GetProperty("weSupply").GetBoolean());

        // Day one: 100 coal for 300 $. Day two: only 50 left, so the supplier pays half the price instead.
        factory.Time.Advance(TimeSpan.FromDays(2));
        var bought = await buyer.CompanyAsync("company.get");
        var stock = bought.GetProperty("warehouse").GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());
        Assert.Equal(100, stock[Items.Coal]);
        Assert.Equal(1000_00 - 300_00 + 150_00, Cash(bought));
        var dealNow = Economy(bought).GetProperty("deals")[0];
        Assert.Equal((2, 1), (dealNow.GetProperty("settled").GetInt32(), dealNow.GetProperty("missed").GetInt32()));
        Assert.Equal(100_00 + 300_00 - 150_00, Cash(await supplier.CompanyAsync("company.get")));

        factory.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal("done", Economy(await supplier.CompanyAsync("company.get")).GetProperty("deals")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Outsiders_buy_shares_of_a_listed_company_sell_them_back_and_a_majority_takes_it_over()
    {
        var (owner, _, id, _) = await CompanyAsync();
        using var o = owner;
        using var investor = await PlayerAsync();

        Assert.Equal("research_needed", (await owner.SendAsync("company.list", new { listed = true })).ErrorCode());
        Assert.Equal("not_listed", (await investor.SendAsync("company.shares.buy", new { company = id, shares = 10 })).ErrorCode());
        await ResearchedAsync(id, TechTree.CorporateFinance);

        // The debut is celebrated with a banquet of fine dishes from the warehouse, roasts first.
        Assert.Equal("banquet_needed", (await owner.SendAsync("company.list", new { listed = true })).ErrorCode());
        await SqlAsync("INSERT INTO companies.warehouse (company_id, item, quantity) VALUES (@c, @i, 120)", ("c", id), ("i", Items.RoastBeef));
        await SqlAsync("INSERT INTO companies.warehouse (company_id, item, quantity) VALUES (@c, @i, 100)", ("c", id), ("i", Items.Cheesecake));
        var debut = Economy(await owner.CompanyAsync("company.list", new { listed = true }));
        Assert.True(debut.GetProperty("listed").GetBoolean());
        var left = (await owner.CompanyAsync("company.get")).GetProperty("warehouse").GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());
        Assert.False(left.ContainsKey(Items.RoastBeef));
        Assert.Equal(20, left[Items.Cheesecake]);

        var exchange = await investor.SendAsync("company.exchange");
        Assert.Equal("company.exchange", exchange.Type());
        var listed = exchange.Payload().GetProperty("companies").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == id);
        var price = listed.GetProperty("priceCents").GetInt64();

        Assert.Equal("no_majority", (await investor.SendAsync("company.takeover", new { company = id })).ErrorCode());
        var bought = await investor.SendAsync("company.shares.buy", new { company = id, shares = 1500 });
        Assert.Equal(1500, bought.Payload().GetProperty("companies").EnumerateArray().First(c => c.GetProperty("id").GetGuid() == id).GetProperty("yours").GetInt64());
        Assert.Equal(1_000_000_00 - 1500 * price, await investor.Client.BalanceAsync());

        // Selling 100 back pays 95% of the price a share has now.
        var priceNow = (await investor.SendAsync("company.exchange")).Payload().GetProperty("companies").EnumerateArray()
            .First(c => c.GetProperty("id").GetGuid() == id).GetProperty("priceCents").GetInt64();
        var balance = await investor.Client.BalanceAsync();
        await investor.SendAsync("company.shares.sell", new { company = id, shares = 100 });
        Assert.Equal(balance + priceNow * 100 * CompanyMoney.BuybackPercent / 100, await investor.Client.BalanceAsync());
        Assert.Equal("not_enough_shares", (await investor.SendAsync("company.shares.sell", new { company = id, shares = 5000 })).ErrorCode());

        // 1400 of 2400 is a majority: the investor takes over and the founder becomes a partner.
        var taken = await investor.CompanyAsync("company.takeover", new { company = id });
        Assert.Equal("owner", taken.GetProperty("role").GetString());
        Assert.Equal("partner", (await owner.CompanyAsync("company.get")).GetProperty("role").GetString());
        Assert.Equal("already_owner", (await investor.SendAsync("company.takeover", new { company = id })).ErrorCode());
    }

    [Fact]
    public async Task Rankings_list_the_richest_companies_and_a_season_pays_its_winners_once_it_is_over()
    {
        var (owner, _, id, name) = await CompanyAsync();
        using var o = owner;
        await GiveAsync(owner, Items.SteelIngot, 50_000);
        await owner.CompanyAsync("company.put", new { item = Items.SteelIngot, quantity = 50_000 });
        await owner.CompanyAsync("company.sell", new { item = Items.SteelIngot, quantity = 50_000 });

        var rankings = await owner.SendAsync("company.rankings");
        Assert.Equal("company.rankings", rankings.Type());
        var production = rankings.Payload().GetProperty("production")[0];
        Assert.Equal((name, true), (production.GetProperty("name").GetString(), production.GetProperty("yours").GetBoolean()));
        Assert.Equal(name, rankings.Payload().GetProperty("richest")[0].GetProperty("name").GetString());

        await owner.CompanyAsync("company.dividend", new { percent = 0 });
        var cash = Cash(await owner.CompanyAsync("company.get"));
        var endsAt = DateTimeOffset.FromUnixTimeMilliseconds(rankings.Payload().GetProperty("endsAt").GetInt64());
        factory.Time.Advance(endsAt - factory.Time.GetUtcNow() + TimeSpan.FromHours(1));

        var after = await owner.CompanyAsync("company.get");
        Assert.Equal(cash + Seasons.PrizesCents[0], Cash(after));
        var trophy = Economy(after).GetProperty("trophies")[0];
        Assert.Equal(1, trophy.GetProperty("place").GetInt32());
        var last = (await owner.SendAsync("company.rankings")).Payload().GetProperty("lastSeason")[0];
        Assert.Equal(name, last.GetProperty("name").GetString());
        Assert.Single(Economy(await owner.CompanyAsync("company.get")).GetProperty("trophies").EnumerateArray());
    }

    [Fact]
    public async Task Only_the_first_company_to_bring_every_part_builds_a_wonder_and_the_others_get_their_parts_back()
    {
        var (first, _, firstId, _) = await CompanyAsync();
        var (second, _, secondId, secondName) = await CompanyAsync();
        using var a = first;
        using var b = second;
        await ResearchedAsync(firstId, TechTree.Megaprojects);
        await ResearchedAsync(secondId, TechTree.Megaprojects);
        var wonder = BigProjects.ById[CompanyPerks.Library];
        Assert.True(wonder.Wonder);
        await a.CompanyAsync("company.project.start", new { project = wonder.Id });
        await b.CompanyAsync("company.project.start", new { project = wonder.Id });

        // The first company brings all but one piece.
        var (lastItem, _) = wonder.Parts[^1];
        foreach (var (item, quantity) in wonder.Parts)
        {
            var brought = item == lastItem ? quantity - 1 : quantity;
            await GiveAsync(a, item, brought);
            await a.CompanyAsync("company.project.give", new { project = wonder.Id, item, quantity = brought });
        }

        var race = (await b.SendAsync("company.wonders")).Payload().GetProperty("wonders").EnumerateArray().First(w => w.GetProperty("id").GetString() == wonder.Id);
        Assert.Equal(JsonValueKind.Null, race.GetProperty("owner").ValueKind);
        Assert.True(race.GetProperty("racers")[0].GetProperty("percent").GetDouble() > 99);
        Assert.False(race.GetProperty("racers")[0].GetProperty("ours").GetBoolean());

        // The second one brings everything first and claims it.
        foreach (var (item, quantity) in wonder.Parts)
        {
            await GiveAsync(b, item, quantity);
            await b.CompanyAsync("company.project.give", new { project = wonder.Id, item, quantity });
        }

        await GiveAsync(a, lastItem, 1);
        Assert.Equal("wonder_taken", (await a.SendAsync("company.project.give", new { project = wonder.Id, item = lastItem, quantity = 1 })).ErrorCode());
        Assert.Equal("wonder_taken", (await a.SendAsync("company.project.start", new { project = wonder.Id })).ErrorCode());
        Assert.Equal(1, (await a.Client.InventoryAsync())[lastItem]);

        var mine = await a.CompanyAsync("company.get");
        var lost = Economy(mine).GetProperty("projects").EnumerateArray().First(p => p.GetProperty("id").GetString() == wonder.Id);
        Assert.Equal(secondName, lost.GetProperty("takenBy").GetString());
        Assert.False(lost.GetProperty("started").GetBoolean());
        var warehouse = mine.GetProperty("warehouse").GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());
        foreach (var (item, quantity) in wonder.Parts)
        {
            Assert.Equal(item == lastItem ? quantity - 1 : quantity, warehouse.GetValueOrDefault(item));
        }

        Assert.Equal("wonderLost", mine.GetProperty("log")[0].GetProperty("action").GetString());
        var built = (await a.SendAsync("company.wonders")).Payload().GetProperty("wonders").EnumerateArray().First(w => w.GetProperty("id").GetString() == wonder.Id);
        Assert.Equal(secondName, built.GetProperty("owner").GetString());
        Assert.False(built.GetProperty("ours").GetBoolean());
        Assert.Contains((await a.SendAsync("chat.get")).Payload().GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("text").GetString()!.Contains(secondName));

        factory.Time.Advance(wonder.Build);
        await using var scope = factory.Services.CreateAsyncScope();
        var projects = await scope.ServiceProvider.GetRequiredService<ICompanies>().ProjectsAsync(b.Id, CancellationToken.None);
        Assert.Equal(CompanyPerks.LibraryResearchFactor, CompanyPerks.ResearchFactor(projects), 6);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ICompanies>().ProjectsAsync(a.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_colossus_gives_every_member_more_experience()
    {
        var (owner, _, id, _) = await CompanyAsync();
        using var o = owner;
        await using var scope = factory.Services.CreateAsyncScope();
        var progress = scope.ServiceProvider.GetRequiredService<ICharacterProgress>();
        var before = (await progress.AddXpAsync(owner.Id, 100, CancellationToken.None)).Xp;
        await BuiltAsync(id, CompanyPerks.Colossus);
        var after = (await progress.AddXpAsync(owner.Id, 100, CancellationToken.None)).Xp;
        Assert.Equal(100 + CompanyPerks.ColossusXpPercent, after - before);
    }

    [Fact]
    public async Task A_plot_taken_out_of_the_company_takes_the_shares_it_bought_with_it()
    {
        var (owner, members, id, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var member = members[0];
        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await member.SendAsync("world.buy", new { x, y })).Type());
        await factory.CompanyLevelAsync(member.Id);

        // Bringing the same plot in and out again buys no more than bringing it in once.
        long Yours(JsonElement company) => Economy(company).GetProperty("shares").GetProperty("yours").GetInt64();
        var first = Yours(await member.CompanyAsync("company.addPlot", new { x, y }));
        Assert.True(first > 0);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0, Yours(await member.CompanyAsync("company.removePlot", new { x, y })));
            Assert.Equal(first, Yours(await member.CompanyAsync("company.addPlot", new { x, y })));
        }

        Assert.Equal("no_majority", (await member.SendAsync("company.takeover", new { company = id })).ErrorCode());

        // A member who is kicked takes their plot out, and its shares with it.
        await owner.CompanyAsync("company.kick", new { name = member.Name });
        var holders = Economy(await owner.CompanyAsync("company.get")).GetProperty("shares").GetProperty("holders");
        Assert.DoesNotContain(holders.EnumerateArray(), h => h.GetProperty("name").GetString() == member.Name);
    }

    [Fact]
    public async Task Company_research_called_off_gives_each_member_back_what_they_brought()
    {
        var (owner, members, id, _) = await CompanyAsync(Roles.Worker);
        using var o = owner;
        using var worker = members[0];
        await ResearchedAsync(id, TechTree.CorporateFinance);
        Assert.Equal("research", (await owner.SendAsync("research.companyStart", new { technology = TechTree.Banking })).Type());

        var workerBefore = await worker.Client.BalanceAsync();
        var ownerBefore = await owner.Client.BalanceAsync();
        Assert.Equal("research", (await worker.SendAsync("research.contribute", new { item = TechTree.Money, quantity = 500_00L })).Type());
        Assert.Equal("research", (await owner.SendAsync("research.contribute", new { item = TechTree.Money, quantity = 200_00L })).Type());
        Assert.Equal(workerBefore - 500_00, await worker.Client.BalanceAsync());

        Assert.Equal("research", (await owner.SendAsync("research.companyCancel")).Type());
        Assert.Equal(workerBefore, await worker.Client.BalanceAsync());
        Assert.Equal(ownerBefore, await owner.Client.BalanceAsync());
    }

    [Fact]
    public async Task With_shareholders_outside_the_company_its_money_goes_out_by_shares_only()
    {
        var (owner, _, id, _) = await CompanyAsync();
        using var o = owner;
        using var investor = await PlayerAsync();
        await owner.CompanyAsync("company.deposit", new { cents = 1000_00 });
        Assert.Equal("company", (await owner.SendAsync("company.withdraw", new { cents = 100_00 })).Type());

        await ResearchedAsync(id, TechTree.CorporateFinance);
        await SqlAsync("INSERT INTO companies.warehouse (company_id, item, quantity) VALUES (@c, @i, 200)", ("c", id), ("i", Items.RoastBeef));
        await owner.CompanyAsync("company.list", new { listed = true });
        Assert.Equal("company.exchange", (await investor.SendAsync("company.shares.buy", new { company = id, shares = 100 })).Type());

        // The owner can no longer empty the cash, neither straight nor through a campaign.
        Assert.Equal("outside_shareholders", (await owner.SendAsync("company.withdraw", new { cents = 100_00 })).ErrorCode());

        // Closed, the company shares its cash out by shares, the investor's part too.
        var shares = Economy(await owner.CompanyAsync("company.get")).GetProperty("shares");
        var total = shares.GetProperty("total").GetInt64();
        var cash = Cash(await owner.CompanyAsync("company.get"));
        var before = await investor.Client.BalanceAsync();
        await owner.SendAsync("company.leave");
        Assert.Equal(before + cash * 100 / total, await investor.Client.BalanceAsync());
    }
}
