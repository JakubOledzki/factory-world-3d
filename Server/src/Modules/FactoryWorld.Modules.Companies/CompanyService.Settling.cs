using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Npgsql;

namespace FactoryWorld.Modules.Companies;

/// <summary>The daily settling: income, dividends, deals and finished projects, and the season's rankings.</summary>
public sealed partial class CompanyService
{
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
}
