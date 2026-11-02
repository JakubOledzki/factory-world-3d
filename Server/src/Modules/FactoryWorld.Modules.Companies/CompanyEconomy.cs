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
