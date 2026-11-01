using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Npgsql;

namespace FactoryWorld.Modules.Companies;

/// <param name="Started">Whether the company took it on; members bring parts only then.</param>
/// <param name="BuiltAt">When it is (or was) finished: set once every part is in.</param>
/// <param name="TakenBy">For a wonder of the world another company built first: that company's name.</param>
public sealed record ProjectState(BigProject Project, bool Started, IReadOnlyDictionary<string, long> Delivered, DateTimeOffset? BuiltAt, string? TakenBy = null);

public sealed record OrderState(CompanyOrder Order, long Delivered);

/// <param name="Assignee">Only they may do it; anyone in the company when null.</param>
public sealed record TaskState(
    long Id, string Item, long Quantity, long Delivered, long WageCents, long PaidCents, Guid? Assignee, Guid CreatedBy, DateTimeOffset? DoneAt);

/// <param name="WeSupply">Whether the company the player is in delivers the goods (rather than buys them).</param>
/// <param name="State"><c>offered</c>, <c>active</c>, <c>done</c>, <c>broken</c> or <c>cancelled</c>.</param>
public sealed record DealState(
    long Id, bool WeSupply, bool OfferedByUs, string Other, string Item, long PerDay, long PriceCents, int Days, string State,
    DateTimeOffset? AcceptedAt, int Settled, int Missed);

public sealed record Trophy(int Season, int Place, long ValueCents);

/// <summary>The company's money and everything it works on together, as members see it.</summary>
/// <param name="IncomeCents">What it earned (less what it spent on goods and wages) since the last dividend.</param>
/// <param name="MayList">Whether it researched corporate finance, so it may go public.</param>
/// <param name="MayBuild">Whether it researched big projects.</param>
/// <param name="ProfitCents">What it earned in the last <see cref="CompanyMoney.ProfitDays"/> days.</param>
public sealed record CompanyEconomy(
    long CashCents, long IncomeCents, int DividendPercent, DateTimeOffset NextDividendAt, bool Listed, bool MayList, bool MayBuild,
    long ValueCents, long ProfitCents, long SharePriceCents, IReadOnlyDictionary<Guid, long> Shares,
    IReadOnlyList<ProjectState> Projects, IReadOnlyList<OrderState> Orders, IReadOnlyList<TaskState> Tasks, IReadOnlyList<DealState> Deals,
    IReadOnlyList<Trophy> Trophies)
{
    /// <summary>Everyone whose name the view shows.</summary>
    public IEnumerable<Guid> People => Shares.Keys.Concat(Tasks.Select(t => t.CreatedBy)).Concat(Tasks.Where(t => t.Assignee is not null).Select(t => t.Assignee!.Value));

    public IReadOnlyDictionary<Guid, string> Names { get; init; } = new Dictionary<Guid, string>();
}

/// <param name="Yours">How many of its shares the player holds.</param>
/// <param name="Mine">Whether it is the player's own company.</param>
public sealed record ListedCompany(Guid Id, string Name, bool Listed, long PriceCents, long Shares, long ProfitCents, long ValueCents, long Yours, bool Mine);

public sealed record CompanyExchange(IReadOnlyList<ListedCompany> Companies);

public sealed record RankedCompany(Guid Id, string Name, long Value);

/// <param name="Production">By what they earned this season.</param>
/// <param name="LastSeason">The previous season's winners.</param>
public sealed record CompanyRankings(
    int Season, DateTimeOffset EndsAt, IReadOnlyList<RankedCompany> Richest, IReadOnlyList<RankedCompany> Production,
    IReadOnlyList<RankedCompany> Plots, IReadOnlyList<RankedCompany> LastSeason, Guid? Yours);

public sealed partial class CompanyService : ICompanyCash
{
    // ---- Cash ----

    /// <summary>A member pays money into the cash and gets new shares for it, at the price a share has now.</summary>
    public Task<CompanyView> DepositAsync(Guid player, long cents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckCents(cents);
            var (company, _) = await RequireMemberAsync(player, cancellationToken);
            await SettleAsync(company, cancellationToken);
            var price = await SharePriceAsync(company, cancellationToken);
            await wallet.DebitAsync(player, cents, $"company {company} deposit", cancellationToken);
            await ChangeMoneyAsync(company, cents, 0, cancellationToken);
            await IssueSharesAsync(company, player, cents / price, cancellationToken);
            await LogAsync(company, player, "deposit", cancellationToken, quantity: cents);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or the treasurer pays money out of the cash into their own wallet, e.g. to buy a plot for the company.</summary>
    public Task<CompanyView> WithdrawAsync(Guid player, long cents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckCents(cents);
            var company = await RequireMoneyAsync(player, cancellationToken);
            await RequireNoOutsideShareholdersAsync(company, cancellationToken);
            await ChangeMoneyAsync(company, -cents, 0, cancellationToken);
            await wallet.CreditAsync(player, cents, $"company {company} withdrawal", cancellationToken);
            await LogAsync(company, player, "withdraw", cancellationToken, quantity: cents);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or the treasurer spends money from the cash outside the company, e.g. to back a candidate in an election;
    /// <paramref name="reason"/> goes into the company's log.</summary>
    public Task<string> SpendAsync(Guid player, long cents, string reason, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckCents(cents);
            var company = await RequireMoneyAsync(player, cancellationToken);
            await RequireNoOutsideShareholdersAsync(company, cancellationToken);
            await ChangeMoneyAsync(company, -cents, 0, cancellationToken);
            await LogAsync(company, player, "lobby", cancellationToken, reason.Length > 64 ? reason[..64] : reason, cents);
            await using var command = await db.CommandAsync("SELECT name FROM companies.companies WHERE id = @company", cancellationToken);
            command.Parameters.AddWithValue("company", company);
            return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }, cancellationToken);

    /// <summary>Sells from the warehouse to the trader at its usual price; the money goes into the cash as earnings.</summary>
    public Task<CompanyView> SellAsync(Guid player, string? item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            if (!Roles.MayTake(role))
            {
                throw new GameException("not_allowed", "Workers do not take from the warehouse.");
            }

            var price = prices.BuysCents(item!) ?? throw new GameException("not_bought", "The trader doesn't buy this item.");
            await ChangeStockAsync(company, item!, -quantity, cancellationToken);
            await ChangeMoneyAsync(company, price * quantity, price * quantity, cancellationToken);
            await LogAsync(company, player, "sell", cancellationToken, item, quantity);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or the treasurer buys from the trader into the warehouse, paying from the cash.</summary>
    public Task<CompanyView> BuyAsync(Guid player, string? item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            var company = await RequireMoneyAsync(player, cancellationToken);
            var price = prices.SellsCents(item!) ?? throw new GameException("not_sold", "The trader doesn't sell this item.");
            await technologies.RequireUnlockedAsync(player, item!, cancellationToken);
            await RequireRoomAsync(company, quantity, cancellationToken);
            await ChangeMoneyAsync(company, -price * quantity, -price * quantity, cancellationToken);
            await ChangeStockAsync(company, item!, quantity, cancellationToken);
            await LogAsync(company, player, "buy", cancellationToken, item, quantity);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner sets how much of what the company earns goes out to shareholders each day.</summary>
    public Task<CompanyView> SetDividendAsync(Guid player, int percent, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (percent is < 0 or > 100)
            {
                throw new GameException("bad_percent", "A dividend is 0-100% of the earnings.");
            }

            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            await SettleAsync(company, cancellationToken);
            await using (var update = await db.CommandAsync("UPDATE companies.companies SET dividend_percent = @percent WHERE id = @company", cancellationToken))
            {
                update.Parameters.AddWithValue("company", company);
                update.Parameters.AddWithValue("percent", percent);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "dividendSet", cancellationToken, quantity: percent);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner puts a member in charge of a company plot (a manager builds there), or nobody when <paramref name="name"/> is empty.</summary>
    public Task<CompanyView> AssignAsync(Guid player, int x, int y, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            Guid? manager = string.IsNullOrWhiteSpace(name) ? null : await MemberByNameAsync(company, name, cancellationToken);
            await using (var update = await db.CommandAsync(
                "UPDATE companies.plots SET manager_id = @manager WHERE x = @x AND y = @y AND company_id = @company", cancellationToken))
            {
                update.Parameters.AddWithValue("company", company);
                update.Parameters.AddWithValue("x", x);
                update.Parameters.AddWithValue("y", y);
                update.Parameters.Add(new NpgsqlParameter("manager", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)manager ?? DBNull.Value });
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("not_company_plot", "This plot is not in your company.");
                }
            }

            await LogAsync(company, player, "assigned", cancellationToken, item: $"{x}:{y}", other: manager);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Stock exchange ----

    /// <summary>The owner takes the company public, so anyone may buy its new shares, or off the exchange again.</summary>
    public Task<CompanyView> ListAsync(Guid player, bool listed, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            if (listed && !await companyTechnologies.ResearchedAsync(company, CompanyTechs.CorporateFinance, cancellationToken))
            {
                throw new GameException("research_needed", "The company researches corporate finance first.");
            }

            // The debut on the exchange is celebrated with a banquet: fine dishes from the warehouse, roasts first.
            if (listed && !await ListedAsync(company, cancellationToken))
            {
                var roasts = await StockAsync(company, Items.RoastBeef, cancellationToken);
                var cakes = await StockAsync(company, Items.Cheesecake, cancellationToken);
                if (roasts + cakes < CompanyMoney.ListingBanquet)
                {
                    throw new GameException("banquet_needed",
                        $"A listing is celebrated with a banquet: {CompanyMoney.ListingBanquet} fine dishes (roast beef or cheesecake) in the warehouse.");
                }

                var fromRoasts = Math.Min(roasts, CompanyMoney.ListingBanquet);
                if (fromRoasts > 0)
                {
                    await ChangeStockAsync(company, Items.RoastBeef, -fromRoasts, cancellationToken);
                }

                if (CompanyMoney.ListingBanquet - fromRoasts > 0)
                {
                    await ChangeStockAsync(company, Items.Cheesecake, -(CompanyMoney.ListingBanquet - fromRoasts), cancellationToken);
                }
            }

            await using (var update = await db.CommandAsync("UPDATE companies.companies SET listed = @listed WHERE id = @company", cancellationToken))
            {
                update.Parameters.AddWithValue("company", company);
                update.Parameters.AddWithValue("listed", listed);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, listed ? "listed" : "delisted", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Every company on the exchange, and every one whose shares the player holds.</summary>
    public Task<CompanyExchange> ExchangeAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var own = (await MembershipAsync(player, false, cancellationToken))?.Company;
            var values = await ValuesAsync(cancellationToken);
            var held = new Dictionary<Guid, long>();
            var totals = new Dictionary<Guid, long>();
            await using (var select = await db.CommandAsync(
                "SELECT company_id, sum(shares), sum(shares) FILTER (WHERE account_id = @player) FROM companies.shares GROUP BY company_id", cancellationToken))
            {
                select.Parameters.AddWithValue("player", player);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    totals[reader.GetGuid(0)] = Convert.ToInt64(reader.GetValue(1));
                    if (!reader.IsDBNull(2))
                    {
                        held[reader.GetGuid(0)] = Convert.ToInt64(reader.GetValue(2));
                    }
                }
            }

            return new CompanyExchange(values.Values
                .Where(v => v.Listed || held.GetValueOrDefault(v.Id) > 0 || v.Id == own)
                .Select(v =>
                {
                    var shares = totals.GetValueOrDefault(v.Id);
                    return new ListedCompany(v.Id, v.Name, v.Listed, CompanyMoney.SharePrice(v.Value, shares), shares, v.Profit, v.Value,
                        held.GetValueOrDefault(v.Id), v.Id == own);
                })
                .OrderByDescending(c => c.ValueCents).ToList());
        }, cancellationToken);

    /// <summary>Anyone buys new shares of a listed company; what they pay goes into its cash.</summary>
    public Task<CompanyExchange> BuySharesAsync(Guid player, Guid company, long shares, CancellationToken cancellationToken) =>
        TradeSharesAsync(player, company, shares, true, cancellationToken);

    /// <summary>A shareholder sells shares back to the company, which pays <see cref="CompanyMoney.BuybackPercent"/>% of their price from its cash.</summary>
    public Task<CompanyExchange> SellSharesAsync(Guid player, Guid company, long shares, CancellationToken cancellationToken) =>
        TradeSharesAsync(player, company, shares, false, cancellationToken);

    private async Task<CompanyExchange> TradeSharesAsync(Guid player, Guid company, long shares, bool buy, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(async () =>
        {
            if (shares is < 1 or > 1_000_000)
            {
                throw new GameException("bad_quantity", "Trade 1-1000000 shares.");
            }

            await RequireCharacterAsync(player, cancellationToken);
            await LockAsync(company, cancellationToken);
            await SettleAsync(company, cancellationToken);
            var price = await SharePriceAsync(company, cancellationToken);
            if (buy)
            {
                if (!await ListedAsync(company, cancellationToken))
                {
                    throw new GameException("not_listed", "This company is not on the exchange.");
                }

                var cost = price * shares;
                await wallet.DebitAsync(player, cost, $"company {company} shares", cancellationToken);
                await ChangeMoneyAsync(company, cost, 0, cancellationToken);
                await IssueSharesAsync(company, player, shares, cancellationToken);
                await LogAsync(company, player, "sharesBought", cancellationToken, quantity: shares);
            }
            else
            {
                if ((await SharesAsync(company, cancellationToken)).GetValueOrDefault(player) < shares)
                {
                    throw new GameException("not_enough_shares", "You don't hold that many shares.");
                }

                var payout = price * shares * CompanyMoney.BuybackPercent / 100;
                await ChangeMoneyAsync(company, -payout, 0, cancellationToken);
                await IssueSharesAsync(company, player, -shares, cancellationToken);
                await wallet.CreditAsync(player, payout, $"company {company} shares sold", cancellationToken);
                await LogAsync(company, player, "sharesSold", cancellationToken, quantity: shares);
            }
        }, cancellationToken);

        return await ExchangeAsync(player, cancellationToken);
    }

    /// <summary>A player holding more than half of a company's shares makes themselves its owner; the old owner becomes a partner.</summary>
    public Task<CompanyView> TakeoverAsync(Guid player, Guid company, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await RequireCharacterAsync(player, cancellationToken);
            await LockAsync(company, cancellationToken);
            var shares = await SharesAsync(company, cancellationToken);
            if (shares.GetValueOrDefault(player) * 2 <= shares.Values.Sum())
            {
                throw new GameException("no_majority", "You need more than half of the company's shares.");
            }

            var membership = await MembershipAsync(player, true, cancellationToken);
            if (membership is { } m && m.Company != company)
            {
                throw new GameException("already_in_company", "Leave your company first.");
            }

            if (membership?.Role == Roles.Owner)
            {
                throw new GameException("already_owner", "You run this company already.");
            }

            await using (var demote = await db.CommandAsync(
                "UPDATE companies.members SET role = @partner WHERE company_id = @company AND role = @owner", cancellationToken))
            {
                demote.Parameters.AddWithValue("company", company);
                demote.Parameters.AddWithValue("partner", Roles.Partner);
                demote.Parameters.AddWithValue("owner", Roles.Owner);
                await demote.ExecuteNonQueryAsync(cancellationToken);
            }

            if (membership is null)
            {
                await DeleteInvitesAsync(player, null, cancellationToken);
                await AddMemberAsync(company, player, Roles.Owner, cancellationToken);
            }
            else
            {
                await UpdateRoleAsync(player, Roles.Owner, cancellationToken);
            }

            await LogAsync(company, player, "takeover", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Big projects ----

    /// <summary>The owner or a partner takes on a big project; the company must have researched big projects.</summary>
    public Task<CompanyView> StartProjectAsync(Guid player, string? id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var project = BigProjects.ById.GetValueOrDefault(id ?? "") ?? throw new GameException("no_project", "There is no such project.");
            var company = await RequireLeadAsync(player, cancellationToken);
            if (!await companyTechnologies.ResearchedAsync(company, CompanyTechs.Megaprojects, cancellationToken))
            {
                throw new GameException("research_needed", "The company researches big projects first.");
            }

            var states = await ProjectsAsync(company, cancellationToken);
            if (states.First(p => p.Project.Id == project.Id).TakenBy is not null)
            {
                throw new GameException("wonder_taken", "Another company built this wonder of the world first.");
            }

            var now = time.GetUtcNow();
            if (project.Requires?.Any(r => states.FirstOrDefault(p => p.Project.Id == r)?.BuiltAt is not { } built || built > now) == true)
            {
                throw new GameException("project_locked", "The company builds the projects this one needs first.");
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO companies.projects (company_id, project, started_at, rewarded) VALUES (@company, @project, @at, false) ON CONFLICT DO NOTHING",
                cancellationToken))
            {
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("project", project.Id);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("project_started", "The company took this project on already.");
                }
            }

            await LogAsync(company, player, "projectStarted", cancellationToken, item: project.Id);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>A member brings parts for a big project, from their inventory or (when they may take) from the warehouse; with all parts in, building starts.</summary>
    public Task<CompanyView> GiveToProjectAsync(
        Guid player, string? id, string? item, long quantity, bool fromWarehouse, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            var project = BigProjects.ById.GetValueOrDefault(id ?? "") ?? throw new GameException("no_project", "There is no such project.");
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            var state = (await ProjectsAsync(company, cancellationToken)).First(p => p.Project.Id == project.Id);
            if (state.TakenBy is not null)
            {
                throw new GameException("wonder_taken", "Another company built this wonder of the world first.");
            }

            if (!state.Started || state.BuiltAt is not null)
            {
                throw new GameException("project_closed", "The company is not collecting parts for this project.");
            }

            var needed = project.Parts.Where(p => p.Item == item).Sum(p => p.Quantity) - state.Delivered.GetValueOrDefault(item!);
            if (needed <= 0)
            {
                throw new GameException("not_needed", "The project needs no more of this.");
            }

            var used = Math.Min(quantity, needed);
            if (fromWarehouse)
            {
                if (!Roles.MayTake(role))
                {
                    throw new GameException("not_allowed", "Workers do not take from the warehouse.");
                }

                await ChangeStockAsync(company, item!, -used, cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(player, item!, used, cancellationToken);
            }

            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO companies.project_parts (company_id, project, item, delivered) VALUES (@company, @project, @item, @used)
                ON CONFLICT (company_id, project, item) DO UPDATE SET delivered = project_parts.delivered + excluded.delivered
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("company", company);
                upsert.Parameters.AddWithValue("project", project.Id);
                upsert.Parameters.AddWithValue("item", item!);
                upsert.Parameters.AddWithValue("used", used);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            var delivered = new Dictionary<string, long>(state.Delivered) { [item!] = state.Delivered.GetValueOrDefault(item!) + used };
            if (project.Parts.All(p => delivered.GetValueOrDefault(p.Item) >= p.Quantity))
            {
                if (project.Wonder)
                {
                    await ClaimWonderAsync(company, project, cancellationToken);
                }

                await using var build = await db.CommandAsync(
                    "UPDATE companies.projects SET built_at = @at WHERE company_id = @company AND project = @project", cancellationToken);
                build.Parameters.AddWithValue("company", company);
                build.Parameters.AddWithValue("project", project.Id);
                build.Parameters.AddWithValue("at", time.GetUtcNow() + project.Build);
                await build.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "projectPart", cancellationToken, item, used);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Orders ----

    /// <summary>Delivers from the warehouse towards one of this week's orders; the reward comes with the last piece.</summary>
    public Task<CompanyView> DeliverOrderAsync(Guid player, int slot, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            if (!Roles.MayTake(role))
            {
                throw new GameException("not_allowed", "Workers do not take from the warehouse.");
            }

            var week = CompanyOrders.Week(time.GetUtcNow());
            var order = (await OrdersAsync(company, cancellationToken)).FirstOrDefault(o => o.Order.Slot == slot)
                ?? throw new GameException("no_order", "There is no such order this week.");
            var left = order.Order.Quantity - order.Delivered;
            if (left <= 0)
            {
                throw new GameException("order_done", "This order is complete.");
            }

            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Deliver at least one.");
            }

            var used = Math.Min(quantity, left);
            await ChangeStockAsync(company, order.Order.Item, -used, cancellationToken);
            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO companies.orders (company_id, week, slot, delivered) VALUES (@company, @week, @slot, @used)
                ON CONFLICT (company_id, week, slot) DO UPDATE SET delivered = orders.delivered + excluded.delivered
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("company", company);
                upsert.Parameters.AddWithValue("week", week);
                upsert.Parameters.AddWithValue("slot", slot);
                upsert.Parameters.AddWithValue("used", used);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            if (used == left)
            {
                await ChangeMoneyAsync(company, order.Order.RewardCents, order.Order.RewardCents, cancellationToken);
                await LogAsync(company, player, "order", cancellationToken, order.Order.Item, order.Order.RewardCents);
            }
            else
            {
                await LogAsync(company, player, "orderPart", cancellationToken, order.Order.Item, used);
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Tasks and wages ----

    /// <summary>The owner or the treasurer sets a task: bring so many of an item into the warehouse, for a wage set aside from the cash now.</summary>
    public Task<CompanyView> CreateTaskAsync(
        Guid player, string? item, long quantity, long wageCents, string? assignee, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            if (wageCents is < 0 or > CompanyMoney.MaxCents)
            {
                throw new GameException("bad_amount", "That is not a wage.");
            }

            var company = await RequireMoneyAsync(player, cancellationToken);
            Guid? who = string.IsNullOrWhiteSpace(assignee) ? null : await MemberByNameAsync(company, assignee, cancellationToken);
            if (await CountAsync("SELECT count(*) FROM companies.tasks WHERE company_id = @company AND done_at IS NULL", company, cancellationToken)
                >= CompanyTasks.MaxOpen)
            {
                throw new GameException("too_many_tasks", $"A company has at most {CompanyTasks.MaxOpen} open tasks.");
            }

            await ChangeMoneyAsync(company, -wageCents, 0, cancellationToken);
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO companies.tasks (company_id, item, quantity, delivered, wage_cents, paid_cents, assignee_id, created_by, created_at)
                VALUES (@company, @item, @quantity, 0, @wage, 0, @assignee, @player, @at)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("item", item!);
                insert.Parameters.AddWithValue("quantity", quantity);
                insert.Parameters.AddWithValue("wage", wageCents);
                insert.Parameters.Add(new NpgsqlParameter("assignee", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)who ?? DBNull.Value });
                insert.Parameters.AddWithValue("player", player);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "task", cancellationToken, item, quantity, who);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>A member brings items for a task from their inventory into the warehouse and is paid the wage for them at once.</summary>
    public Task<CompanyView> DeliverTaskAsync(Guid player, long id, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireMemberAsync(player, cancellationToken);
            var task = await TaskAsync(company, id, cancellationToken);
            if (task.Assignee is { } who && who != player)
            {
                throw new GameException("not_your_task", "This task is somebody else's.");
            }

            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Bring at least one.");
            }

            var used = Math.Min(quantity, task.Quantity - task.Delivered);
            await RequireRoomAsync(company, used, cancellationToken);
            await inventory.RemoveAsync(player, task.Item, used, cancellationToken);
            await ChangeStockAsync(company, task.Item, used, cancellationToken);
            var done = task.Delivered + used == task.Quantity;
            var pay = done ? task.WageCents - task.PaidCents : (long)(task.WageCents * (decimal)used / task.Quantity);
            await using (var update = await db.CommandAsync(
                """
                UPDATE companies.tasks SET delivered = delivered + @used, paid_cents = paid_cents + @pay, done_at = CASE WHEN @done THEN @now END
                WHERE id = @id
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("used", used);
                update.Parameters.AddWithValue("pay", pay);
                update.Parameters.AddWithValue("done", done);
                update.Parameters.AddWithValue("now", time.GetUtcNow());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (pay > 0)
            {
                await wallet.CreditAsync(player, pay, $"company {company} task {id}", cancellationToken);
                // A wage is spent already (set aside from the cash); now it counts against the earnings.
                await ChangeMoneyAsync(company, 0, -pay, cancellationToken);
            }

            await LogAsync(company, player, done ? "taskDone" : "taskPart", cancellationToken, task.Item, used);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or the treasurer calls a task off; the wage nobody earned goes back into the cash.</summary>
    public Task<CompanyView> CancelTaskAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var company = await RequireMoneyAsync(player, cancellationToken);
            var task = await TaskAsync(company, id, cancellationToken);
            await ChangeMoneyAsync(company, task.WageCents - task.PaidCents, 0, cancellationToken);
            await using (var delete = await db.CommandAsync("DELETE FROM companies.tasks WHERE id = @id", cancellationToken))
            {
                delete.Parameters.AddWithValue("id", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "taskCancelled", cancellationToken, task.Item, task.Quantity);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    private async Task<TaskState> TaskAsync(Guid company, long id, CancellationToken cancellationToken) =>
        (await TasksAsync(company, cancellationToken, id)).FirstOrDefault(t => t.DoneAt is null)
            ?? throw new GameException("no_task", "There is no such open task.");

    // ---- Deals between companies ----

    /// <summary>The owner or the treasurer offers another company a standing deal: <paramref name="sell"/> when this company delivers.</summary>
    public Task<CompanyView> OfferDealAsync(
        Guid player, string? other, string? item, long perDay, long priceCents, int days, bool sell, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, perDay);
            if (perDay > Deals.MaxPerDay || days is < 1 or > Deals.MaxDays || priceCents is < 0 or > CompanyMoney.MaxCents)
            {
                throw new GameException("bad_deal", $"A deal runs 1-{Deals.MaxDays} days for at most {Deals.MaxPerDay} a day.");
            }

            var company = await RequireMoneyAsync(player, cancellationToken);
            Guid partner;
            await using (var find = await db.CommandAsync("SELECT id FROM companies.companies WHERE normalized_name = @name", cancellationToken))
            {
                find.Parameters.AddWithValue("name", (other ?? "").Trim().ToLowerInvariant());
                partner = await find.ExecuteScalarAsync(cancellationToken) is Guid id && id != company
                    ? id
                    : throw new GameException("no_such_company", "There is no other company with this name.");
            }

            if (await CountAsync(
                    "SELECT count(*) FROM companies.deals WHERE (supplier_id = @company OR buyer_id = @company) AND state IN ('offered', 'active')",
                    company, cancellationToken) >= Deals.MaxOpen)
            {
                throw new GameException("too_many_deals", $"A company has at most {Deals.MaxOpen} offers and deals at once.");
            }

            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO companies.deals (supplier_id, buyer_id, offered_by, item, per_day, price_cents, days, state, created_at, settled_days, missed_days)
                VALUES (@supplier, @buyer, @company, @item, @perDay, @price, @days, 'offered', @at, 0, 0)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("supplier", sell ? company : partner);
                insert.Parameters.AddWithValue("buyer", sell ? partner : company);
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("item", item!);
                insert.Parameters.AddWithValue("perDay", perDay);
                insert.Parameters.AddWithValue("price", priceCents);
                insert.Parameters.AddWithValue("days", days);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "dealOffered", cancellationToken, item, perDay);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The other company's owner or treasurer accepts an offer; the first day is settled a day later.</summary>
    public Task<CompanyView> AcceptDealAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var company = await RequireMoneyAsync(player, cancellationToken);
            await using (var update = await db.CommandAsync(
                """
                UPDATE companies.deals SET state = 'active', accepted_at = @now
                WHERE id = @id AND state = 'offered' AND offered_by <> @company AND (supplier_id = @company OR buyer_id = @company)
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("company", company);
                update.Parameters.AddWithValue("now", time.GetUtcNow());
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("no_deal", "There is no such offer for your company.");
                }
            }

            await LogAsync(company, player, "dealAccepted", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Either company's owner or treasurer withdraws or declines an offer nobody accepted yet.</summary>
    public Task<CompanyView> CancelDealAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var company = await RequireMoneyAsync(player, cancellationToken);
            await using (var update = await db.CommandAsync(
                "UPDATE companies.deals SET state = 'cancelled' WHERE id = @id AND state = 'offered' AND (supplier_id = @company OR buyer_id = @company)",
                cancellationToken))
            {
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("company", company);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("no_deal", "There is no such offer for your company.");
                }
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Rankings and seasons ----

    public Task<CompanyRankings> RankingsAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await CloseSeasonAsync(cancellationToken);
            var now = time.GetUtcNow();
            var season = Seasons.Of(now);
            var values = (await ValuesAsync(cancellationToken)).Values.ToList();
            var seasonIncome = await IncomeBetweenAsync(Seasons.Start(season), Seasons.End(season), cancellationToken);
            List<RankedCompany> Top(Func<CompanyValue, long> by) =>
                values.Select(v => new RankedCompany(v.Id, v.Name, by(v))).Where(r => r.Value > 0)
                    .OrderByDescending(r => r.Value).ThenBy(r => r.Name).Take(Seasons.Top).ToList();

            var last = new List<RankedCompany>();
            await using (var select = await db.CommandAsync(
                "SELECT company_id, name, value_cents FROM companies.seasons WHERE season = @season AND place > 0 ORDER BY place", cancellationToken))
            {
                select.Parameters.AddWithValue("season", Seasons.Previous(season));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    last.Add(new RankedCompany(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            return new CompanyRankings(
                season, new DateTimeOffset(Seasons.End(season).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                Top(v => v.Value), Top(v => seasonIncome.GetValueOrDefault(v.Id)), Top(v => v.Plots), last,
                (await MembershipAsync(player, false, cancellationToken))?.Company);
        }, cancellationToken);

    /// <summary>Once a season is over, its three best earners get their prize, once.</summary>
    private async Task CloseSeasonAsync(CancellationToken cancellationToken)
    {
        var previous = Seasons.Previous(Seasons.Of(time.GetUtcNow()));
        await using (var exists = await db.CommandAsync("SELECT EXISTS (SELECT 1 FROM companies.seasons WHERE season = @season)", cancellationToken))
        {
            exists.Parameters.AddWithValue("season", previous);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                return;
            }
        }

        var income = await IncomeBetweenAsync(Seasons.Start(previous), Seasons.End(previous), cancellationToken);
        var names = new Dictionary<Guid, string>();
        await using (var select = await db.CommandAsync("SELECT id, name FROM companies.companies WHERE id = ANY(@ids)", cancellationToken))
        {
            select.Parameters.AddWithValue("ids", income.Keys.ToArray());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                names[reader.GetGuid(0)] = reader.GetString(1);
            }
        }

        var winners = income.Where(i => i.Value > 0 && names.ContainsKey(i.Key)).OrderByDescending(i => i.Value).Take(Seasons.PrizesCents.Length).ToList();
        // Place 0 marks the season closed, also when nobody won it.
        var rows = winners.Select((w, i) => (Place: i + 1, Company: w.Key, Name: names[w.Key], Value: w.Value)).Prepend((0, Guid.Empty, "", 0L));
        foreach (var (place, company, name, value) in rows)
        {
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO companies.seasons (season, place, company_id, name, value_cents) VALUES (@season, @place, @company, @name, @value)
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            insert.Parameters.AddWithValue("season", previous);
            insert.Parameters.AddWithValue("place", place);
            insert.Parameters.AddWithValue("company", company);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("value", value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return;
            }

            if (place > 0)
            {
                await ChangeMoneyAsync(company, Seasons.PrizesCents[place - 1], 0, cancellationToken);
                await LogAsync(company, Guid.Empty, "season", cancellationToken, item: previous.ToString(), quantity: place);
            }
        }
    }

    private async Task<Dictionary<Guid, long>> IncomeBetweenAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT company_id, sum(cents) FROM companies.income WHERE day >= @from AND day < @to GROUP BY company_id", cancellationToken);
        select.Parameters.AddWithValue("from", from);
        select.Parameters.AddWithValue("to", to);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var income = new Dictionary<Guid, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            income[reader.GetGuid(0)] = Convert.ToInt64(reader.GetValue(1));
        }

        return income;
    }

    // ---- Settling what happens by itself ----

    /// <summary>Brings the company up to now: the dividend, deals, finished projects and a season that ended.</summary>
    private async Task SettleAsync(Guid company, CancellationToken cancellationToken)
    {
        await PayDividendAsync(company, cancellationToken);
        await SettleDealsAsync(company, cancellationToken);
        await FinishProjectsAsync(company, cancellationToken);
        await CloseSeasonAsync(cancellationToken);
    }

    private async Task PayDividendAsync(Guid company, CancellationToken cancellationToken)
    {
        long cash, income;
        int percent;
        DateTimeOffset at;
        await using (var select = await db.CommandAsync(
            "SELECT cash_cents, income_cents, dividend_percent, dividend_at FROM companies.companies WHERE id = @company FOR UPDATE", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            (cash, income, percent, at) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        var now = time.GetUtcNow();
        if (now < at + CompanyMoney.DividendEvery)
        {
            return;
        }

        var next = at + CompanyMoney.DividendEvery * Math.Floor((now - at) / CompanyMoney.DividendEvery);
        var payout = income > 0 ? Math.Min(cash, income * percent / 100) : 0;
        long paid = 0;
        if (payout > 0)
        {
            foreach (var (holder, cents) in CompanyMoney.Split(payout, await SharesAsync(company, cancellationToken)))
            {
                if (cents > 0)
                {
                    await wallet.CreditAsync(holder, cents, $"company {company} dividend", cancellationToken);
                    paid += cents;
                }
            }
        }

        await using (var update = await db.CommandAsync(
            "UPDATE companies.companies SET cash_cents = cash_cents - @paid, income_cents = 0, dividend_at = @next WHERE id = @company", cancellationToken))
        {
            update.Parameters.AddWithValue("company", company);
            update.Parameters.AddWithValue("paid", paid);
            update.Parameters.AddWithValue("next", next);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (paid > 0)
        {
            await LogAsync(company, Guid.Empty, "dividend", cancellationToken, quantity: paid);
        }
    }

    private async Task SettleDealsAsync(Guid company, CancellationToken cancellationToken)
    {
        var deals = new List<(long Id, Guid Supplier, Guid Buyer, string Item, long PerDay, long Price, int Days, DateTimeOffset AcceptedAt, int Settled, int Missed)>();
        await using (var select = await db.CommandAsync(
            """
            SELECT id, supplier_id, buyer_id, item, per_day, price_cents, days, accepted_at, settled_days, missed_days FROM companies.deals
            WHERE state = 'active' AND (supplier_id = @company OR buyer_id = @company) ORDER BY id FOR UPDATE
            """, cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                deals.Add((reader.GetInt64(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt32(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(8), reader.GetInt32(9)));
            }
        }

        var now = time.GetUtcNow();
        foreach (var deal in deals)
        {
            var due = Math.Min(deal.Days, (int)Math.Floor((now - deal.AcceptedAt) / Deals.Day));
            var (settled, missed, state) = (deal.Settled, deal.Missed, "active");
            while (settled < due)
            {
                if (await CashAsync(deal.Buyer, cancellationToken) < deal.Price)
                {
                    state = "broken";
                    await LogAsync(deal.Buyer, Guid.Empty, "dealBroken", cancellationToken, deal.Item);
                    await LogAsync(deal.Supplier, Guid.Empty, "dealBroken", cancellationToken, deal.Item);
                    break;
                }

                if (await StockAsync(deal.Supplier, deal.Item, cancellationToken) >= deal.PerDay)
                {
                    await ChangeStockAsync(deal.Supplier, deal.Item, -deal.PerDay, cancellationToken);
                    await ChangeStockAsync(deal.Buyer, deal.Item, deal.PerDay, cancellationToken);
                    // Money between companies is not earnings: two companies of one person dealing in a circle would win seasons
                    // and pump their share price out of nothing.
                    await ChangeMoneyAsync(deal.Buyer, -deal.Price, 0, cancellationToken);
                    await ChangeMoneyAsync(deal.Supplier, deal.Price, 0, cancellationToken);
                }
                else
                {
                    var penalty = Math.Min(await CashAsync(deal.Supplier, cancellationToken), deal.Price * Deals.MissPenaltyPercent / 100);
                    await ChangeMoneyAsync(deal.Supplier, -penalty, 0, cancellationToken);
                    await ChangeMoneyAsync(deal.Buyer, penalty, 0, cancellationToken);
                    await LogAsync(deal.Supplier, Guid.Empty, "dealMissed", cancellationToken, deal.Item, penalty);
                    missed++;
                }

                settled++;
            }

            if (state == "active" && settled == deal.Days)
            {
                state = "done";
            }

            await using var update = await db.CommandAsync(
                "UPDATE companies.deals SET settled_days = @settled, missed_days = @missed, state = @state WHERE id = @id", cancellationToken);
            update.Parameters.AddWithValue("id", deal.Id);
            update.Parameters.AddWithValue("settled", settled);
            update.Parameters.AddWithValue("missed", missed);
            update.Parameters.AddWithValue("state", state);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task FinishProjectsAsync(Guid company, CancellationToken cancellationToken)
    {
        await ReturnLostWondersAsync(company, cancellationToken);
        var built = new List<string>();
        await using (var select = await db.CommandAsync(
            """
            UPDATE companies.projects SET rewarded = true
            WHERE company_id = @company AND NOT rewarded AND built_at <= @now RETURNING project
            """, cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            select.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                built.Add(reader.GetString(0));
            }
        }

        foreach (var id in built)
        {
            if (BigProjects.ById.TryGetValue(id, out var project))
            {
                foreach (var (item, quantity) in project.Reward)
                {
                    await ChangeStockAsync(company, item, quantity, cancellationToken);
                }
            }

            await LogAsync(company, Guid.Empty, "projectDone", cancellationToken, item: id);
        }
    }

    // ---- Reading ----

    private async Task<CompanyEconomy> EconomyAsync(Guid company, CancellationToken cancellationToken)
    {
        long cash, income;
        int percent;
        DateTimeOffset at;
        bool listed;
        await using (var select = await db.CommandAsync(
            "SELECT cash_cents, income_cents, dividend_percent, dividend_at, listed FROM companies.companies WHERE id = @company", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (cash, income, percent, at, listed) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4));
        }

        var shares = await SharesAsync(company, cancellationToken);
        var profit = await ProfitAsync(company, cancellationToken);
        var value = CompanyMoney.Value(cash, await WarehouseCentsAsync(company, cancellationToken), profit);

        var deals = new List<DealState>();
        await using (var select = await db.CommandAsync(
            """
            SELECT d.id, d.supplier_id = @company, d.offered_by = @company, coalesce(o.name, '?'), d.item, d.per_day, d.price_cents, d.days, d.state,
                   d.accepted_at, d.settled_days, d.missed_days
            FROM companies.deals d
            LEFT JOIN companies.companies o ON o.id = CASE WHEN d.supplier_id = @company THEN d.buyer_id ELSE d.supplier_id END
            WHERE d.supplier_id = @company OR d.buyer_id = @company
            ORDER BY d.state IN ('offered', 'active') DESC, d.id DESC LIMIT 20
            """, cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                deals.Add(new DealState(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9), reader.GetInt32(10), reader.GetInt32(11)));
            }
        }

        var trophies = new List<Trophy>();
        await using (var select = await db.CommandAsync(
            "SELECT season, place, value_cents FROM companies.seasons WHERE company_id = @company AND place > 0 ORDER BY season DESC", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                trophies.Add(new Trophy(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2)));
            }
        }

        return new CompanyEconomy(
            cash, income, percent, at + CompanyMoney.DividendEvery, listed,
            await companyTechnologies.ResearchedAsync(company, CompanyTechs.CorporateFinance, cancellationToken),
            await companyTechnologies.ResearchedAsync(company, CompanyTechs.Megaprojects, cancellationToken),
            value, profit, CompanyMoney.SharePrice(value, shares.Values.Sum()), shares,
            await ProjectsAsync(company, cancellationToken), await OrdersAsync(company, cancellationToken),
            await TasksAsync(company, cancellationToken), deals, trophies);
    }

    private async Task<IReadOnlyList<ProjectState>> ProjectsAsync(Guid company, CancellationToken cancellationToken)
    {
        var started = new Dictionary<string, DateTimeOffset?>();
        await using (var select = await db.CommandAsync("SELECT project, built_at FROM companies.projects WHERE company_id = @company", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                started[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        var parts = new Dictionary<string, Dictionary<string, long>>();
        await using (var select = await db.CommandAsync(
            "SELECT project, item, delivered FROM companies.project_parts WHERE company_id = @company", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                (parts.TryGetValue(reader.GetString(0), out var p) ? p : parts[reader.GetString(0)] = [])[reader.GetString(1)] = reader.GetInt64(2);
            }
        }

        var taken = (await WonderOwnersAsync(cancellationToken)).Where(w => w.Value.Company != company).ToDictionary(w => w.Key, w => w.Value.Name);
        return BigProjects.All.Select(p => new ProjectState(p, started.ContainsKey(p.Id),
            parts.GetValueOrDefault(p.Id) ?? new Dictionary<string, long>(), started.GetValueOrDefault(p.Id), taken.GetValueOrDefault(p.Id))).ToList();
    }

    private async Task<IReadOnlyList<OrderState>> OrdersAsync(Guid company, CancellationToken cancellationToken)
    {
        var week = CompanyOrders.Week(time.GetUtcNow());
        var delivered = new Dictionary<int, long>();
        await using (var select = await db.CommandAsync(
            "SELECT slot, delivered FROM companies.orders WHERE company_id = @company AND week = @week", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            select.Parameters.AddWithValue("week", week);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                delivered[reader.GetInt32(0)] = reader.GetInt64(1);
            }
        }

        return CompanyOrders.For(company, week, prices).Select(o => new OrderState(o, delivered.GetValueOrDefault(o.Slot))).ToList();
    }

    /// <summary>Open tasks and those done in the last day; just one when <paramref name="id"/> is given.</summary>
    private async Task<IReadOnlyList<TaskState>> TasksAsync(Guid company, CancellationToken cancellationToken, long? id = null)
    {
        await using var select = await db.CommandAsync(
            """
            SELECT id, item, quantity, delivered, wage_cents, paid_cents, assignee_id, created_by, done_at FROM companies.tasks
            WHERE company_id = @company AND (@id::bigint IS NULL OR id = @id) AND (done_at IS NULL OR done_at > @since)
            ORDER BY id
            """ + (id is null ? "" : " FOR UPDATE"), cancellationToken);
        select.Parameters.AddWithValue("company", company);
        select.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)id ?? DBNull.Value });
        select.Parameters.AddWithValue("since", time.GetUtcNow() - TimeSpan.FromDays(1));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var tasks = new List<TaskState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new TaskState(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetGuid(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return tasks;
    }

    private sealed record CompanyValue(Guid Id, string Name, bool Listed, long Value, long Profit, long Plots);

    /// <summary>Every company's value, for the exchange and the rankings.</summary>
    private async Task<Dictionary<Guid, CompanyValue>> ValuesAsync(CancellationToken cancellationToken)
    {
        var stock = new Dictionary<Guid, long>();
        await using (var select = await db.CommandAsync("SELECT company_id, item, quantity FROM companies.warehouse", cancellationToken))
        {
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stock[reader.GetGuid(0)] = stock.GetValueOrDefault(reader.GetGuid(0)) + (prices.BuysCents(reader.GetString(1)) ?? 0) * reader.GetInt64(2);
            }
        }

        var values = new Dictionary<Guid, CompanyValue>();
        await using (var select = await db.CommandAsync(
            """
            SELECT c.id, c.name, c.listed, c.cash_cents,
                   coalesce((SELECT sum(i.cents) FROM companies.income i WHERE i.company_id = c.id AND i.day > @since), 0),
                   (SELECT count(*) FROM companies.plots p WHERE p.company_id = c.id)
            FROM companies.companies c
            """, cancellationToken))
        {
            select.Parameters.AddWithValue("since", Today().AddDays(-CompanyMoney.ProfitDays));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                var profit = Convert.ToInt64(reader.GetValue(4));
                values[id] = new CompanyValue(id, reader.GetString(1), reader.GetBoolean(2),
                    CompanyMoney.Value(reader.GetInt64(3), stock.GetValueOrDefault(id), profit), profit, reader.GetInt64(5));
            }
        }

        return values;
    }

    // ---- Money and shares ----

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static void CheckCents(long cents)
    {
        if (cents is < 1 or > CompanyMoney.MaxCents)
        {
            throw new GameException("bad_amount", "That is not an amount of money.");
        }
    }

    /// <summary>The player's company when they may pay from its cash (the owner or the treasurer), brought up to now.</summary>
    private async Task<Guid> RequireMoneyAsync(Guid player, CancellationToken cancellationToken)
    {
        var (company, role) = await RequireMemberAsync(player, cancellationToken);
        if (!Roles.HandlesMoney(role))
        {
            throw new GameException("not_allowed", "Only the owner and the treasurer manage the company's money.");
        }

        await SettleAsync(company, cancellationToken);
        return company;
    }

    /// <summary>Money leaves the cash for a member's own ends only while all shareholders are members: investors from the exchange,
    /// and members who left, are paid by dividends and buybacks, never emptied out by the owner.</summary>
    private async Task RequireNoOutsideShareholdersAsync(Guid company, CancellationToken cancellationToken)
    {
        if (await CountAsync(
                """
                SELECT count(*) FROM companies.shares s
                WHERE s.company_id = @company AND s.shares > 0
                  AND NOT EXISTS (SELECT 1 FROM companies.members m WHERE m.company_id = s.company_id AND m.account_id = s.account_id)
                """, company, cancellationToken) > 0)
        {
            throw new GameException("outside_shareholders",
                "Shareholders outside the company own part of the cash: it goes out to them as dividends, not to members.");
        }
    }

    private async Task<Guid> RequireLeadAsync(Guid player, CancellationToken cancellationToken)
    {
        var (company, role) = await RequireMemberAsync(player, cancellationToken);
        return Roles.Leads(role) ? company : throw new GameException("not_allowed", "Only the owner and partners choose big projects.");
    }

    private async Task RequireRoomAsync(Guid company, long quantity, CancellationToken cancellationToken)
    {
        var stored = await CountAsync("SELECT coalesce(sum(quantity), 0) FROM companies.warehouse WHERE company_id = @company", company, cancellationToken);
        var capacity = CompanyRules.CapacityFor(await HasHqAsync(company, cancellationToken));
        if (stored + quantity > capacity)
        {
            throw new GameException("warehouse_full", $"The warehouse holds {capacity} items.");
        }
    }

    /// <summary>
    /// Moves the cash by <paramref name="cash"/> (failing with <c>company_short</c> below zero) and what it earned by
    /// <paramref name="earned"/>: sales and orders earn, goods and wages it pays for count against it, deposits do neither.
    /// </summary>
    private async Task ChangeMoneyAsync(Guid company, long cash, long earned, CancellationToken cancellationToken)
    {
        await using (var update = await db.CommandAsync(
            """
            UPDATE companies.companies SET cash_cents = cash_cents + @cash, income_cents = income_cents + @earned
            WHERE id = @company AND cash_cents + @cash >= 0
            """, cancellationToken))
        {
            update.Parameters.AddWithValue("company", company);
            update.Parameters.AddWithValue("cash", cash);
            update.Parameters.AddWithValue("earned", earned);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("company_short", "The company doesn't have that much money.");
            }
        }

        if (earned != 0)
        {
            await using var daily = await db.CommandAsync(
                """
                INSERT INTO companies.income (company_id, day, cents) VALUES (@company, @day, @earned)
                ON CONFLICT (company_id, day) DO UPDATE SET cents = income.cents + excluded.cents
                """, cancellationToken);
            daily.Parameters.AddWithValue("company", company);
            daily.Parameters.AddWithValue("day", Today());
            daily.Parameters.AddWithValue("earned", earned);
            await daily.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<long> CashAsync(Guid company, CancellationToken cancellationToken) =>
        await CountAsync("SELECT coalesce((SELECT cash_cents FROM companies.companies WHERE id = @company), 0)", company, cancellationToken);

    private async Task<long> StockAsync(Guid company, string item, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT coalesce((SELECT quantity FROM companies.warehouse WHERE company_id = @company AND item = @item), 0)", cancellationToken);
        select.Parameters.AddWithValue("company", company);
        select.Parameters.AddWithValue("item", item);
        return Convert.ToInt64(await select.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> ListedAsync(Guid company, CancellationToken cancellationToken) =>
        await CountAsync("SELECT count(*) FROM companies.companies WHERE id = @company AND listed", company, cancellationToken) > 0;

    private async Task<long> ProfitAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT coalesce(sum(cents), 0) FROM companies.income WHERE company_id = @company AND day > @since", cancellationToken);
        select.Parameters.AddWithValue("company", company);
        select.Parameters.AddWithValue("since", Today().AddDays(-CompanyMoney.ProfitDays));
        return Convert.ToInt64(await select.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<long> WarehouseCentsAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync("SELECT item, quantity FROM companies.warehouse WHERE company_id = @company", cancellationToken);
        select.Parameters.AddWithValue("company", company);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        long cents = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            cents += (prices.BuysCents(reader.GetString(0)) ?? 0) * reader.GetInt64(1);
        }

        return cents;
    }

    private async Task<long> SharePriceAsync(Guid company, CancellationToken cancellationToken)
    {
        var cash = await CashAsync(company, cancellationToken);
        var value = CompanyMoney.Value(cash, await WarehouseCentsAsync(company, cancellationToken), await ProfitAsync(company, cancellationToken));
        return CompanyMoney.SharePrice(value, (await SharesAsync(company, cancellationToken)).Values.Sum());
    }

    private async Task<IReadOnlyDictionary<Guid, long>> SharesAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT account_id, shares FROM companies.shares WHERE company_id = @company AND shares > 0", cancellationToken);
        select.Parameters.AddWithValue("company", company);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var shares = new Dictionary<Guid, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shares[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return shares;
    }

    /// <summary>Gives the player <paramref name="shares"/> more (fewer when negative) of the company.</summary>
    private async Task IssueSharesAsync(Guid company, Guid player, long shares, CancellationToken cancellationToken)
    {
        if (shares == 0)
        {
            return;
        }

        await using var upsert = await db.CommandAsync(
            """
            INSERT INTO companies.shares (company_id, account_id, shares) VALUES (@company, @player, @shares)
            ON CONFLICT (company_id, account_id) DO UPDATE SET shares = shares.shares + excluded.shares;
            DELETE FROM companies.shares WHERE company_id = @company AND account_id = @player AND shares <= 0;
            """, cancellationToken);
        upsert.Parameters.AddWithValue("company", company);
        upsert.Parameters.AddWithValue("player", player);
        upsert.Parameters.AddWithValue("shares", shares);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }
}
