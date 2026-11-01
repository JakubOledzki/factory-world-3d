using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Law;

public sealed record CaseView(
    long Id, string Country, Guid Suspect, string Kind, string Detail, long ValueCents, Guid OpenedBy, DateTimeOffset OpenedAt, string Status,
    long FineCents, long CollectedCents, int TradeBanHours, int TravelBanHours, int WantedHours, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    DateTimeOffset? AppealedAt, bool Seizable);

public sealed record CrossingView(
    long Id, Guid Owner, string FromCountry, string ToCountry, DateTimeOffset At, bool Declared, long Pieces, long DutyCents,
    IReadOnlyDictionary<string, long>? Goods, bool Inspected);

/// <summary>What an inspection or a search found.</summary>
/// <param name="Goods">The goods the officer saw.</param>
/// <param name="Forbidden">Of them, what the law forbids.</param>
/// <param name="CaseId">The case opened, or null when there is nothing to prosecute.</param>
public sealed record Finding(Guid Suspect, IReadOnlyDictionary<string, long> Goods, IReadOnlyDictionary<string, long> Forbidden, long? CaseId, bool Protected)
{
    public IReadOnlyDictionary<string, long> Objects { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// The police at work: trains that crossed the border are inspected, suspects investigated (their hidden traces may come to
/// light), plots searched with a judge's warrant; cases are sentenced (fine, bans, the wanted list, seizing the goods) or
/// dismissed, and the sentenced may appeal to a judge, who upholds the sentence or overturns it and gives everything back.
/// </summary>
public sealed class CaseService(
    LawData data, PoliceService police, ICountries countries, IWallet wallet, IInventory inventory, ICharacterXp xp, IMarketPrices prices,
    IExportMarket market, IPlotRegistry plots, IPlotObjects objects, IPlotStorage storage)
{
    private const string CaseColumns =
        "id, country, suspect_id, kind, detail, value_cents, opened_by, opened_at, status, fine_cents, collected_cents, trade_ban_hours, " +
        "travel_ban_hours, wanted_hours, decided_by, decided_at, appealed_at, seize_x IS NOT NULL";

    private static CaseView ReadCase(Npgsql.NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7),
        r.GetString(8), r.GetInt64(9), r.GetInt64(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
        r.IsDBNull(14) ? null : r.GetGuid(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16), r.GetBoolean(17));

    public async Task<CaseView> CaseAsync(long id, CancellationToken cancellationToken, bool forUpdate = false) =>
        (await data.QueryAsync($"SELECT {CaseColumns} FROM law.cases WHERE id = @id{(forUpdate ? " FOR UPDATE" : "")}", ReadCase,
            cancellationToken, ("id", id))).FirstOrDefault() ?? throw new GameException("no_case", "There is no such case.");

    public Task<List<CaseView>> CasesOfAsync(Guid suspect, CancellationToken cancellationToken) =>
        data.QueryAsync($"SELECT {CaseColumns} FROM law.cases WHERE suspect_id = @s ORDER BY id DESC LIMIT 30", ReadCase, cancellationToken, ("s", suspect));

    /// <summary>The country's open cases and those decided over the last week.</summary>
    public Task<List<CaseView>> CasesInAsync(string country, CancellationToken cancellationToken) =>
        data.QueryAsync(
            $"""
            SELECT {CaseColumns} FROM law.cases WHERE country = @c AND (status IN ('open', 'appealed') OR decided_at > @since)
            ORDER BY (status = 'open') DESC, id DESC LIMIT 60
            """, ReadCase, cancellationToken, ("c", country), ("since", data.Now - TimeSpan.FromDays(7)));

    public Task<List<CaseView>> AppealsInAsync(string country, CancellationToken cancellationToken) =>
        data.QueryAsync($"SELECT {CaseColumns} FROM law.cases WHERE country = @c AND status = 'appealed' ORDER BY appealed_at", ReadCase,
            cancellationToken, ("c", country));

    /// <summary>Appeals no judge looked at in time stand as sentenced.</summary>
    public Task ExpireAppealsAsync(CancellationToken cancellationToken) =>
        data.ExecuteAsync("UPDATE law.cases SET status = 'upheld' WHERE status = 'appealed' AND appealed_at <= @expired", cancellationToken,
            ("expired", data.Now - LawRules.AppealExpires));

    /// <summary>The trains that came into the officer's country over the last two days; the goods of a smuggling one only once inspected.</summary>
    public Task<List<CrossingView>> CrossingsAsync(string country, CancellationToken cancellationToken) =>
        data.QueryAsync(
            """
            SELECT id, owner_id, from_country, to_country, at, declared, quantities, duty_cents, items, inspected_by IS NOT NULL
            FROM law.crossings WHERE to_country = @c AND at > @since ORDER BY at DESC LIMIT 60
            """, r =>
            {
                var quantities = r.GetFieldValue<long[]>(6);
                var declared = r.GetBoolean(5);
                var inspected = r.GetBoolean(9);
                var goods = r.GetFieldValue<string[]>(8).Zip(quantities).ToDictionary(p => p.First, p => p.Second);
                return new CrossingView(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), declared,
                    quantities.Sum(), r.GetInt64(7), declared || inspected ? goods : null, inspected);
            }, cancellationToken, ("c", country), ("since", data.Now - LawRules.CrossingsKept));

    /// <summary>An officer opens a train that came into their country. Smuggled goods in a hidden compartment only a detective finds.</summary>
    public Task<Finding> InspectAsync(Guid player, long crossingId, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Cadet, cancellationToken);
            var crossing = (await data.QueryAsync(
                """
                SELECT owner_id, items, quantities, declared, hidden, evaded_cents, from_country FROM law.crossings
                WHERE id = @id AND to_country = @c AND at > @since AND inspected_by IS NULL FOR UPDATE
                """, r => (Owner: r.GetGuid(0), Goods: r.GetFieldValue<string[]>(1).Zip(r.GetFieldValue<long[]>(2)).ToDictionary(p => p.First, p => p.Second),
                    Declared: r.GetBoolean(3), Hidden: r.GetBoolean(4), Evaded: r.GetInt64(5), From: r.GetString(6)),
                cancellationToken, ("id", crossingId), ("c", officer.Country), ("since", data.Now - LawRules.CrossingsKept))).FirstOrDefault();
            if (crossing.Goods is null)
            {
                throw new GameException("no_crossing", "There is no such train left to inspect.");
            }

            if (crossing.Owner == player)
            {
                throw new GameException("own_case", "You may not inspect your own train.");
            }

            await data.CountActionAsync(player, "inspect", Ranks.Inspections[officer.Rank], cancellationToken);
            await data.ExecuteAsync("UPDATE law.crossings SET inspected_by = @p WHERE id = @id", cancellationToken, ("p", player), ("id", crossingId));
            if (crossing.Declared || crossing.Evaded == 0 || (crossing.Hidden && officer.Rank < Ranks.Detective))
            {
                // Declared and paid for, or nothing to find: the hidden compartment fools a cadet and a constable.
                var shown = crossing.Hidden && !crossing.Declared && officer.Rank < Ranks.Detective ? new Dictionary<string, long>() : crossing.Goods;
                return new Finding(crossing.Owner, shown, new Dictionary<string, long>(), null, false);
            }

            var forbidden = await data.ForbiddenAsync(crossing.Owner, officer.Country, crossing.Goods, cancellationToken);
            if (await xp.LevelAsync(crossing.Owner, cancellationToken) < LawRules.ProtectedBelowLevel)
            {
                return new Finding(crossing.Owner, crossing.Goods, forbidden, null, true);
            }

            var id = await data.OpenCaseAsync(officer.Country, crossing.Owner, Crimes.Smuggling,
                $"Train from {crossing.From} smuggled: {BorderControl.Describe(crossing.Goods)}", crossing.Evaded, player, cancellationToken);
            return new Finding(crossing.Owner, crossing.Goods, forbidden, id, false);
        }, cancellationToken);

    /// <summary>Anyone tells the police of the suspect's country (or their own) about a player.</summary>
    public Task ReportAsync(Guid reporter, string name, string text, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            text = (text ?? "").Trim();
            if (text.Length is 0 or > LawRules.ReportLength || text.Any(char.IsControl))
            {
                throw new GameException("bad_text", $"Say what happened in at most {LawRules.ReportLength} characters.");
            }

            var suspect = await police.PlayerAsync(name, cancellationToken);
            if (suspect == reporter)
            {
                throw new GameException("bad_suspect", "You may not report yourself.");
            }

            var country = await countries.CitizenshipAsync(suspect, cancellationToken) ?? await police.CitizenshipAsync(reporter, cancellationToken);
            await data.CountActionAsync(reporter, "report", LawRules.ReportsPerDay, cancellationToken);
            await data.ExecuteAsync(
                "INSERT INTO law.reports (country, reporter_id, suspect_id, text, at, closed) VALUES (@c, @r, @s, @t, @now, false)",
                cancellationToken, ("c", country), ("r", reporter), ("s", suspect), ("t", text), ("now", data.Now));
        }, cancellationToken);

    public Task<List<(long Id, Guid Reporter, Guid Suspect, string Text, DateTimeOffset At)>> ReportsAsync(string country, CancellationToken cancellationToken) =>
        data.QueryAsync("SELECT id, reporter_id, suspect_id, text, at FROM law.reports WHERE country = @c AND NOT closed ORDER BY at DESC LIMIT 50",
            r => (r.GetInt64(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)), cancellationToken, ("c", country));

    public Task CloseReportAsync(Guid player, long id, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Cadet, cancellationToken);
            if (await data.ExecuteAsync("UPDATE law.reports SET closed = true WHERE id = @id AND country = @c", cancellationToken,
                ("id", id), ("c", officer.Country)) == 0)
            {
                throw new GameException("no_report", "There is no such report.");
            }
        }, cancellationToken);

    /// <summary>
    /// A detective looks into a player (an officer too: bribes): each of their hidden traces in the country, and each smuggling train
    /// of theirs nobody inspected, comes to light with a chance and opens a case. Returns the cases opened.
    /// </summary>
    public Task<IReadOnlyList<long>> InvestigateAsync(Guid player, string name, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Detective, cancellationToken);
            var suspect = await police.PlayerAsync(name, cancellationToken);
            if (suspect == player)
            {
                throw new GameException("own_case", "You may not investigate yourself.");
            }

            await data.CountActionAsync(player, "investigate", LawRules.Investigations, cancellationToken);
            await data.ExecuteAsync("UPDATE law.reports SET closed = true WHERE country = @c AND suspect_id = @s", cancellationToken,
                ("c", officer.Country), ("s", suspect));
            if (await xp.LevelAsync(suspect, cancellationToken) < LawRules.ProtectedBelowLevel)
            {
                throw new GameException("protected_player", $"Players below level {LawRules.ProtectedBelowLevel} are not prosecuted.");
            }

            var opened = new List<long>();
            var traces = await data.QueryAsync(
                """
                SELECT id, kind, detail, value_cents, accomplice_id FROM law.traces
                WHERE country = @c AND suspect_id = @s AND NOT found FOR UPDATE
                """, r => (Id: r.GetInt64(0), Kind: r.GetString(1), Detail: r.GetString(2), Value: r.GetInt64(3), Accomplice: r.IsDBNull(4) ? (Guid?)null : r.GetGuid(4)),
                cancellationToken, ("c", officer.Country), ("s", suspect));
            foreach (var trace in traces.Where(_ => Random.Shared.Next(100) < LawRules.InvestigationPercent))
            {
                await data.ExecuteAsync("UPDATE law.traces SET found = true WHERE id = @id", cancellationToken, ("id", trace.Id));
                opened.Add(await data.OpenCaseAsync(officer.Country, suspect, trace.Kind, trace.Detail, trace.Value, player, cancellationToken));
                if (trace.Accomplice is { } accomplice && accomplice != player
                    && await xp.LevelAsync(accomplice, cancellationToken) >= LawRules.ProtectedBelowLevel)
                {
                    opened.Add(await data.OpenCaseAsync(officer.Country, accomplice, trace.Kind, trace.Detail, trace.Value, player, cancellationToken));
                }
            }

            var trains = await data.QueryAsync(
                """
                SELECT id, items, quantities, evaded_cents, from_country FROM law.crossings
                WHERE to_country = @c AND owner_id = @s AND NOT declared AND evaded_cents > 0 AND inspected_by IS NULL AND at > @since FOR UPDATE
                """, r => (Id: r.GetInt64(0), Goods: r.GetFieldValue<string[]>(1).Zip(r.GetFieldValue<long[]>(2)).ToDictionary(p => p.First, p => p.Second),
                    Evaded: r.GetInt64(3), From: r.GetString(4)),
                cancellationToken, ("c", officer.Country), ("s", suspect), ("since", data.Now - LawRules.CrossingsKept));
            foreach (var train in trains.Where(_ => Random.Shared.Next(100) < LawRules.InvestigationPercent))
            {
                await data.ExecuteAsync("UPDATE law.crossings SET inspected_by = @p WHERE id = @id", cancellationToken, ("p", player), ("id", train.Id));
                opened.Add(await data.OpenCaseAsync(officer.Country, suspect, Crimes.Smuggling,
                    $"Train from {train.From} smuggled: {BorderControl.Describe(train.Goods)}", train.Evaded, player, cancellationToken));
            }

            return (IReadOnlyList<long>)opened;
        }, cancellationToken);

    /// <summary>
    /// A constable or above sentences an open case of their country: a fine up to <see cref="LawRules.FineCap"/> (taken from the wallet as
    /// far as it goes, into the treasury), bans from trading and from crossing borders, the wanted list, and seizing goods a search found.
    /// </summary>
    public Task<CaseView> SentenceAsync(
        Guid player, long caseId, long fineCents, int tradeBanHours, int travelBanHours, int wantedHours, bool seize, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Constable, cancellationToken);
            var c = await OpenCaseOfAsync(officer, caseId, cancellationToken);
            if (fineCents < 0 || fineCents > LawRules.FineCap(c.ValueCents))
            {
                throw new GameException("bad_fine", $"The fine may be 0-{LawRules.FineCap(c.ValueCents) / 100} $ for this case.");
            }

            if (tradeBanHours is < 0 or > LawRules.MaxBanHours || travelBanHours is < 0 or > LawRules.MaxBanHours
                || wantedHours is < 0 or > LawRules.MaxWantedHours)
            {
                throw new GameException("bad_sentence_hours", $"Bans last 0-{LawRules.MaxBanHours} hours, the wanted list 0-{LawRules.MaxWantedHours}.");
            }

            if (await xp.LevelAsync(c.Suspect, cancellationToken) < LawRules.ProtectedBelowLevel)
            {
                throw new GameException("protected_player", $"Players below level {LawRules.ProtectedBelowLevel} are not prosecuted.");
            }

            var today = (long)(await data.ScalarAsync(
                "SELECT count(*) FROM law.cases WHERE country = @c AND suspect_id = @s AND status <> 'dismissed' AND decided_at > @since",
                cancellationToken, ("c", officer.Country), ("s", c.Suspect), ("since", data.Now - TimeSpan.FromDays(1))))!;
            if (today >= LawRules.SentencesPerSuspectPerDay)
            {
                throw new GameException("daily_limit", $"A player is sentenced at most {LawRules.SentencesPerSuspectPerDay} times a day in one country.");
            }

            var treasury = await police.TreasuryAsync(officer.Country, cancellationToken);
            var collected = Math.Min(fineCents, await wallet.BalanceAsync(c.Suspect, cancellationToken));
            if (collected > 0)
            {
                await wallet.DebitAsync(c.Suspect, collected, $"police fine case {c.Id}", cancellationToken);
                await wallet.CreditAsync(treasury, collected, $"police fine case {c.Id}", cancellationToken);
            }

            foreach (var (kind, hours) in new[] { (SanctionKinds.Trade, tradeBanHours), (SanctionKinds.Travel, travelBanHours), (SanctionKinds.Wanted, wantedHours) })
            {
                if (hours > 0)
                {
                    await data.ExecuteAsync(
                        "INSERT INTO law.sanctions (player_id, kind, country, until, case_id) VALUES (@p, @k, @c, @until, @case)",
                        cancellationToken, ("p", c.Suspect), ("k", kind), ("c", officer.Country), ("until", data.Now + TimeSpan.FromHours(hours)), ("case", c.Id));
                }
            }

            if (seize && c.Seizable)
            {
                await SeizeAsync(c, treasury, cancellationToken);
            }

            await data.ExecuteAsync(
                """
                UPDATE law.cases SET status = 'sentenced', fine_cents = @fine, collected_cents = @collected, trade_ban_hours = @trade,
                    travel_ban_hours = @travel, wanted_hours = @wanted, decided_by = @p, decided_at = @now
                WHERE id = @id
                """, cancellationToken, ("fine", fineCents), ("collected", collected), ("trade", tradeBanHours), ("travel", travelBanHours),
                ("wanted", wantedHours), ("p", player), ("now", data.Now), ("id", c.Id));
            return await CaseAsync(c.Id, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes the goods a search found: from the plot's storages first, the rest from the owner's inventory; the treasury sells them.</summary>
    private async Task SeizeAsync(CaseView c, Guid treasury, CancellationToken cancellationToken)
    {
        var plan = (await data.QueryAsync("SELECT seize_x, seize_y, seize_items, seize_quantities FROM law.cases WHERE id = @id",
            r => (X: r.GetInt32(0), Y: r.GetInt32(1), Goods: r.GetFieldValue<string[]>(2).Zip(r.GetFieldValue<long[]>(3)).ToDictionary(p => p.First, p => p.Second)),
            cancellationToken, ("id", c.Id))).Single();
        var fromPlot = (await plots.FindAsync(plan.X, plan.Y, cancellationToken))?.Owner == c.Suspect
            ? await storage.SeizeAsync(plan.X, plan.Y, plan.Goods, cancellationToken)
            : new Dictionary<string, long>();
        var taken = fromPlot.ToDictionary(g => g.Key, g => g.Value);
        foreach (var (item, quantity) in plan.Goods)
        {
            var rest = Math.Min(quantity - fromPlot.GetValueOrDefault(item), await inventory.CountAsync(c.Suspect, item, cancellationToken));
            if (rest > 0)
            {
                await inventory.RemoveAsync(c.Suspect, item, rest, cancellationToken);
                taken[item] = taken.GetValueOrDefault(item) + rest;
            }
        }

        await Seized.SellAsync(market, prices, treasury, taken, cancellationToken);
    }

    /// <summary>A constable or above drops an open case of their country.</summary>
    public Task DismissAsync(Guid player, long caseId, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Constable, cancellationToken);
            var c = await OpenCaseOfAsync(officer, caseId, cancellationToken);
            await data.ExecuteAsync("UPDATE law.cases SET status = 'dismissed', decided_by = @p, decided_at = @now WHERE id = @id",
                cancellationToken, ("p", player), ("now", data.Now), ("id", c.Id));
        }, cancellationToken);

    private async Task<CaseView> OpenCaseOfAsync(Officer officer, long caseId, CancellationToken cancellationToken)
    {
        var c = await CaseAsync(caseId, cancellationToken, true);
        if (c.Country != officer.Country)
        {
            throw new GameException("no_case", "There is no such case in your country.");
        }

        if (c.Suspect == officer.PlayerId)
        {
            throw new GameException("own_case", "You may not decide your own case.");
        }

        if (c.Status != CaseStatus.Open)
        {
            throw new GameException("case_closed", "The case is decided already.");
        }

        return c;
    }

    /// <summary>The sentenced player appeals, within a day of the sentence.</summary>
    public Task AppealAsync(Guid player, long caseId, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var c = await CaseAsync(caseId, cancellationToken, true);
            if (c.Suspect != player)
            {
                throw new GameException("no_case", "There is no such case against you.");
            }

            if (c.Status != CaseStatus.Sentenced || c.DecidedAt <= data.Now - LawRules.AppealWindow)
            {
                throw new GameException("no_appeal", "A sentence may be appealed within a day.");
            }

            await data.ExecuteAsync("UPDATE law.cases SET status = 'appealed', appealed_at = @now WHERE id = @id", cancellationToken,
                ("now", data.Now), ("id", c.Id));
        }, cancellationToken);

    /// <summary>The minister of justice or the president hears an appeal: the sentence stands, or is overturned and the fine comes
    /// back from the treasury (as far as it has the money) and the bans end.</summary>
    public Task<CaseView> JudgeAsync(Guid judge, long caseId, bool uphold, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var c = await CaseAsync(caseId, cancellationToken, true);
            await police.RequireOfficeAsync(judge, c.Country, cancellationToken, CountryOffices.Justice, CountryOffices.President);
            if (c.Status != CaseStatus.Appealed)
            {
                throw new GameException("no_appeal", "There is no appeal in this case.");
            }

            if (c.Suspect == judge)
            {
                throw new GameException("own_case", "You may not judge your own case.");
            }

            if (!uphold)
            {
                var treasury = await police.TreasuryAsync(c.Country, cancellationToken);
                var refund = Math.Min(c.CollectedCents, await wallet.BalanceAsync(treasury, cancellationToken));
                if (refund > 0)
                {
                    await wallet.DebitAsync(treasury, refund, $"overturned case {c.Id}", cancellationToken);
                    await wallet.CreditAsync(c.Suspect, refund, $"overturned case {c.Id}", cancellationToken);
                }

                await data.ExecuteAsync("DELETE FROM law.sanctions WHERE case_id = @id", cancellationToken, ("id", c.Id));
            }

            await data.ExecuteAsync("UPDATE law.cases SET status = @s, judged_by = @j WHERE id = @id", cancellationToken,
                ("s", uphold ? CaseStatus.Upheld : CaseStatus.Overturned), ("j", judge), ("id", c.Id));
            return await CaseAsync(c.Id, cancellationToken);
        }, cancellationToken);

    /// <summary>A detective asks a judge for a warrant to search a plot in their country.</summary>
    public Task<long> RequestWarrantAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Detective, cancellationToken);
            if (await countries.CountryOfPlotAsync(x, y, cancellationToken) != officer.Country
                || await plots.FindAsync(x, y, cancellationToken) is not { } plot)
            {
                throw new GameException("bad_warrant_plot", "A warrant is for a bought plot in your country.");
            }

            if (plot.Owner == player)
            {
                throw new GameException("own_case", "You may not search your own plot.");
            }

            return (long)(await data.ScalarAsync(
                "INSERT INTO law.warrants (country, x, y, requested_by, requested_at, used) VALUES (@c, @x, @y, @p, @now, false) RETURNING id",
                cancellationToken, ("c", officer.Country), ("x", x), ("y", y), ("p", player), ("now", data.Now)))!;
        }, cancellationToken);

    public Task<List<(long Id, int X, int Y, Guid RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset? GrantedAt)>> WarrantsAsync(
        string country, CancellationToken cancellationToken) =>
        data.QueryAsync(
            """
            SELECT id, x, y, requested_by, requested_at, granted_at FROM law.warrants
            WHERE country = @c AND NOT used AND (granted_at IS NULL OR granted_at > @since) ORDER BY id DESC LIMIT 30
            """, r => (r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4),
                r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(5)),
            cancellationToken, ("c", country), ("since", data.Now - LawRules.WarrantLasts));

    /// <summary>The minister of justice or the president grants a warrant, or turns it down.</summary>
    public Task DecideWarrantAsync(Guid judge, long id, bool grant, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var warrant = (await data.QueryAsync("SELECT country, requested_by FROM law.warrants WHERE id = @id AND granted_at IS NULL AND NOT used FOR UPDATE",
                r => (Country: r.GetString(0), By: r.GetGuid(1)), cancellationToken, ("id", id))).FirstOrDefault();
            if (warrant.Country is null)
            {
                throw new GameException("no_warrant", "There is no such warrant to decide.");
            }

            await police.RequireOfficeAsync(judge, warrant.Country, cancellationToken, CountryOffices.Justice, CountryOffices.President);
            await data.ExecuteAsync(
                grant ? "UPDATE law.warrants SET granted_by = @j, granted_at = @now WHERE id = @id" : "DELETE FROM law.warrants WHERE id = @id",
                cancellationToken, ("j", judge), ("now", data.Now), ("id", id));
        }, cancellationToken);

    /// <summary>
    /// A detective of the country searches a plot on a granted warrant, which it uses up: everything in its storages and standing on it,
    /// and what its owner carries of the goods the country bans or licenses. Forbidden goods open a case that can seize them.
    /// </summary>
    public Task<Finding> SearchAsync(Guid player, long warrantId, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var officer = await data.RequireOfficerAsync(player, Ranks.Detective, cancellationToken);
            var warrant = (await data.QueryAsync(
                "SELECT x, y FROM law.warrants WHERE id = @id AND country = @c AND NOT used AND granted_at > @since FOR UPDATE",
                r => (X: r.GetInt32(0), Y: r.GetInt32(1)), cancellationToken, ("id", warrantId), ("c", officer.Country),
                ("since", data.Now - LawRules.WarrantLasts))).Cast<(int X, int Y)?>().FirstOrDefault()
                ?? throw new GameException("no_warrant", "There is no granted warrant of that number.");
            await data.ExecuteAsync("UPDATE law.warrants SET used = true WHERE id = @id", cancellationToken, ("id", warrantId));
            var owner = (await plots.FindAsync(warrant.X, warrant.Y, cancellationToken))?.Owner
                ?? throw new GameException("bad_warrant_plot", "Nobody owns this plot any more.");
            if (owner == player)
            {
                throw new GameException("own_case", "You may not search your own plot.");
            }

            var stored = await storage.ContentsAsync(warrant.X, warrant.Y, cancellationToken);
            var standing = (await objects.ObjectsAsync(warrant.X, warrant.Y, cancellationToken))
                .GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => (long)g.Count());
            var carried = new Dictionary<string, long>();
            foreach (var rule in await data.RulesAsync(officer.Country, cancellationToken))
            {
                if (await inventory.CountAsync(owner, rule.Item, cancellationToken) is var n and > 0)
                {
                    carried[rule.Item] = n;
                }
            }

            var goods = stored.Concat(carried).GroupBy(g => g.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
            var forbidden = await data.ForbiddenAsync(owner, officer.Country, goods, cancellationToken);
            var forbiddenObjects = await data.ForbiddenAsync(owner, officer.Country, standing, cancellationToken);
            var all = forbidden.Concat(forbiddenObjects).GroupBy(g => g.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
            if (all.Count == 0)
            {
                return new Finding(owner, goods, all, null, false) { Objects = standing };
            }

            if (await xp.LevelAsync(owner, cancellationToken) < LawRules.ProtectedBelowLevel)
            {
                return new Finding(owner, goods, all, null, true) { Objects = standing };
            }

            var worth = all.Sum(g => LawRules.Worth(prices, g.Key, g.Value));
            var id = await data.OpenCaseAsync(officer.Country, owner, Crimes.Contraband,
                $"Search of plot {warrant.X}:{warrant.Y}: {BorderControl.Describe(all)}", worth, player, cancellationToken,
                forbidden.Count > 0 ? (warrant.X, warrant.Y, forbidden) : null);
            return new Finding(owner, goods, all, id, false) { Objects = standing };
        }, cancellationToken);
}
