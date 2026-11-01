using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Law;

/// <summary>
/// A country's police: players ask to join, the chief (appointed by the president or the minister of the interior) takes them on
/// and promotes them, and the treasury pays them a salary by the day and a share of the fines they gave. The president and the
/// minister of the interior also say which goods the country bans or licenses, and whether its forests are protected.
/// </summary>
public sealed class PoliceService(LawData data, ICountries countries, IWallet wallet, ICharacterNames names, ICharacterXp xp)
{
    /// <summary>The country the player is a citizen of; fails with <c>no_country</c> without one.</summary>
    public async Task<string> CitizenshipAsync(Guid player, CancellationToken cancellationToken) =>
        await countries.CitizenshipAsync(player, cancellationToken)
        ?? throw new GameException("no_country", "You are not a citizen of any country.");

    /// <summary>The account of the country's treasury; fails with <c>no_government</c> while it has none.</summary>
    public async Task<Guid> TreasuryAsync(string country, CancellationToken cancellationToken) =>
        await countries.TreasuryAsync(country, cancellationToken)
        ?? throw new GameException("no_government", "The country has no government yet.");

    /// <summary>Fails with <c>not_allowed</c> unless the player holds one of the offices in the country.</summary>
    public async Task RequireOfficeAsync(Guid player, string country, CancellationToken cancellationToken, params string[] offices)
    {
        var held = await countries.OfficesAsync(player, country, cancellationToken);
        if (!offices.Any(held.Contains))
        {
            throw new GameException("not_government", "Only the country's government may do this.");
        }
    }

    public async Task<Guid> PlayerAsync(string? name, CancellationToken cancellationToken) =>
        await names.AccountAsync(name?.Trim() ?? "", cancellationToken) ?? throw new GameException("no_player", "There is no player of that name.");

    /// <summary>Fails unless the player may serve in the police: a citizen of the country, of the level for it, with a clean record.</summary>
    private async Task RequireFitAsync(Guid player, string country, CancellationToken cancellationToken)
    {
        if (await countries.CitizenshipAsync(player, cancellationToken) != country)
        {
            throw new GameException("not_citizen", "Only the country's citizens serve in its police.");
        }

        if (await xp.LevelAsync(player, cancellationToken) < LawRules.ProtectedBelowLevel)
        {
            throw new GameException("police_level_too_low", $"The police take players from level {LawRules.ProtectedBelowLevel}.");
        }

        if ((await data.SanctionsAsync(player, cancellationToken)).Count > 0
            || await data.ScalarAsync(
                "SELECT 1 FROM law.cases WHERE suspect_id = @p AND status IN ('sentenced', 'upheld', 'appealed') AND decided_at > @since LIMIT 1",
                cancellationToken, ("p", player), ("since", data.Now - TimeSpan.FromDays(7))) is not null)
        {
            throw new GameException("criminal_record", "Nobody sentenced in the last week serves in the police.");
        }
    }

    /// <summary>Asks to join the police of the player's country.</summary>
    public Task ApplyAsync(Guid player, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var country = await CitizenshipAsync(player, cancellationToken);
            await TreasuryAsync(country, cancellationToken);
            if (await data.OfficerAsync(player, cancellationToken) is not null)
            {
                throw new GameException("already_officer", "You are in the police already.");
            }

            await RequireFitAsync(player, country, cancellationToken);
            await data.ExecuteAsync(
                """
                INSERT INTO law.applications (player_id, country, applied_at) VALUES (@p, @c, @now)
                ON CONFLICT (player_id) DO UPDATE SET country = @c, applied_at = @now
                """, cancellationToken, ("p", player), ("c", country), ("now", data.Now));
        }, cancellationToken);

    /// <summary>Leaves the police, or takes back the application.</summary>
    public Task ResignAsync(Guid player, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var left = await data.ExecuteAsync("DELETE FROM law.applications WHERE player_id = @p", cancellationToken, ("p", player))
                + await data.ExecuteAsync("DELETE FROM law.officers WHERE player_id = @p", cancellationToken, ("p", player));
            if (left == 0)
            {
                throw new GameException("not_officer", "You are not in the police.");
            }
        }, cancellationToken);

    /// <summary>The chief takes on a player who applied, as a cadet.</summary>
    public Task HireAsync(Guid chief, string name, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var boss = await data.RequireOfficerAsync(chief, Ranks.Chief, cancellationToken);
            var player = await PlayerAsync(name, cancellationToken);
            if (await data.ScalarAsync("SELECT country FROM law.applications WHERE player_id = @p FOR UPDATE", cancellationToken, ("p", player))
                is not string country || country != boss.Country)
            {
                throw new GameException("no_application", "This player has not applied to your police.");
            }

            await RequireFitAsync(player, country, cancellationToken);
            await data.ExecuteAsync("DELETE FROM law.applications WHERE player_id = @p", cancellationToken, ("p", player));
            await data.ExecuteAsync(
                "INSERT INTO law.officers (player_id, country, rank, joined_at, paid_until) VALUES (@p, @c, 0, @now, @now)",
                cancellationToken, ("p", player), ("c", country), ("now", data.Now));
        }, cancellationToken);

    /// <summary>The chief turns down a player who applied.</summary>
    public Task RejectAsync(Guid chief, string name, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var boss = await data.RequireOfficerAsync(chief, Ranks.Chief, cancellationToken);
            var player = await PlayerAsync(name, cancellationToken);
            if (await data.ExecuteAsync("DELETE FROM law.applications WHERE player_id = @p AND country = @c", cancellationToken,
                ("p", player), ("c", boss.Country)) == 0)
            {
                throw new GameException("no_application", "This player has not applied to your police.");
            }
        }, cancellationToken);

    /// <summary>The chief promotes or demotes an officer of their police, up to detective.</summary>
    public Task SetRankAsync(Guid chief, string name, int rank, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            if (rank is < Ranks.Cadet or >= Ranks.Chief)
            {
                throw new GameException("bad_rank", "The chief gives the ranks from cadet to detective.");
            }

            var boss = await data.RequireOfficerAsync(chief, Ranks.Chief, cancellationToken);
            var player = await PlayerAsync(name, cancellationToken);
            if (player == chief || await data.OfficerAsync(player, cancellationToken, true) is not { } officer || officer.Country != boss.Country)
            {
                throw new GameException("not_officer", "This player is not an officer of your police.");
            }

            await data.ExecuteAsync("UPDATE law.officers SET rank = @r WHERE player_id = @p", cancellationToken, ("r", (short)rank), ("p", player));
        }, cancellationToken);

    /// <summary>The chief, the president or the minister of the interior throws an officer out of the police.</summary>
    public Task FireAsync(Guid actor, string name, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var player = await PlayerAsync(name, cancellationToken);
            if (player == actor || await data.OfficerAsync(player, cancellationToken, true) is not { } officer)
            {
                throw new GameException("not_officer", "This player is not an officer.");
            }

            if (await data.OfficerAsync(actor, cancellationToken) is not { Rank: Ranks.Chief } boss || boss.Country != officer.Country)
            {
                await RequireOfficeAsync(actor, officer.Country, cancellationToken, CountryOffices.President, CountryOffices.Interior);
            }

            await data.ExecuteAsync("DELETE FROM law.officers WHERE player_id = @p", cancellationToken, ("p", player));
        }, cancellationToken);

    /// <summary>The president or the minister of the interior makes a citizen the chief of the country's police; the old chief
    /// stays as a detective.</summary>
    public Task AppointChiefAsync(Guid actor, string name, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var country = await CitizenshipAsync(actor, cancellationToken);
            await RequireOfficeAsync(actor, country, cancellationToken, CountryOffices.President, CountryOffices.Interior);
            await TreasuryAsync(country, cancellationToken);
            var player = await PlayerAsync(name, cancellationToken);
            await RequireFitAsync(player, country, cancellationToken);
            await data.ExecuteAsync("UPDATE law.officers SET rank = @r WHERE country = @c AND rank = @chief", cancellationToken,
                ("r", Ranks.Detective), ("c", country), ("chief", Ranks.Chief));
            await data.ExecuteAsync("DELETE FROM law.applications WHERE player_id = @p", cancellationToken, ("p", player));
            await data.ExecuteAsync(
                """
                INSERT INTO law.officers (player_id, country, rank, joined_at, paid_until) VALUES (@p, @c, @chief, @now, @now)
                ON CONFLICT (player_id) DO UPDATE SET country = @c, rank = @chief
                """, cancellationToken, ("p", player), ("c", country), ("chief", Ranks.Chief), ("now", data.Now));
        }, cancellationToken);

    /// <summary>The president or the minister of the interior says which goods the country bans and which it lets only licence
    /// holders have (for a weekly fee), and whether its forests are protected.</summary>
    public Task SetLawsAsync(
        Guid actor, IReadOnlyList<string> banned, IReadOnlyDictionary<string, long> licensed, bool forestProtection, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            var country = await CitizenshipAsync(actor, cancellationToken);
            await RequireOfficeAsync(actor, country, cancellationToken, CountryOffices.President, CountryOffices.Interior);
            if (banned.Count + licensed.Count > LawRules.MaxRules || banned.Concat(licensed.Keys).Any(i => !Items.Exists(i ?? ""))
                || banned.Intersect(licensed.Keys).Any() || banned.Distinct().Count() != banned.Count)
            {
                throw new GameException("bad_law_items", $"Ban or license at most {LawRules.MaxRules} existing goods, each once.");
            }

            if (licensed.Values.Any(fee => fee is < 0 or > LawRules.MaxLicenceFeeCents))
            {
                throw new GameException("bad_licence_fee", $"A licence costs 0-{LawRules.MaxLicenceFeeCents / 100} $ a week.");
            }

            await data.ExecuteAsync("DELETE FROM law.country_rules WHERE country = @c", cancellationToken, ("c", country));
            foreach (var item in banned)
            {
                await data.ExecuteAsync("INSERT INTO law.country_rules (country, item, kind, fee_cents) VALUES (@c, @i, 'banned', 0)",
                    cancellationToken, ("c", country), ("i", item));
            }

            foreach (var (item, fee) in licensed)
            {
                await data.ExecuteAsync("INSERT INTO law.country_rules (country, item, kind, fee_cents) VALUES (@c, @i, 'licensed', @f)",
                    cancellationToken, ("c", country), ("i", item), ("f", fee));
            }

            await data.ExecuteAsync(
                """
                INSERT INTO law.country_laws (country, forest_protection) VALUES (@c, @f)
                ON CONFLICT (country) DO UPDATE SET forest_protection = @f
                """, cancellationToken, ("c", country), ("f", forestProtection));
        }, cancellationToken);

    /// <summary>Buys a week's licence for a good the country licenses; the fee goes to its treasury.</summary>
    public Task BuyLicenceAsync(Guid player, string country, string item, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            country = (country ?? "").Trim().ToUpperInvariant();
            var rule = (await data.RulesAsync(country, cancellationToken)).FirstOrDefault(r => r.Item == item && r.Kind == RuleKinds.Licensed)
                ?? throw new GameException("not_licensed", "The country gives no licence for this.");
            var treasury = await TreasuryAsync(country, cancellationToken);
            if (rule.FeeCents > 0)
            {
                await wallet.DebitAsync(player, rule.FeeCents, $"licence {item} {country}", cancellationToken);
                await wallet.CreditAsync(treasury, rule.FeeCents, $"licence fee {item}", cancellationToken);
            }

            await data.ExecuteAsync(
                """
                INSERT INTO law.licences (player_id, country, item, until) VALUES (@p, @c, @i, @until)
                ON CONFLICT (player_id, country, item) DO UPDATE SET until = GREATEST(licences.until, @now) + @lasts
                """, cancellationToken, ("p", player), ("c", country), ("i", item), ("until", data.Now + LawRules.LicenceLasts),
                ("now", data.Now), ("lasts", LawRules.LicenceLasts));
        }, cancellationToken);

    /// <summary>
    /// Pays the officer what the treasury owes them: the salary for the whole days since it was last paid (at most a week, as many
    /// as the treasury covers) and their share of the fines whose time to appeal is over. Returns what was paid.
    /// </summary>
    public Task<long> PayAsync(Guid player, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            if (await data.OfficerAsync(player, cancellationToken, true) is not { } officer
                || await countries.TreasuryAsync(officer.Country, cancellationToken) is not { } treasury)
            {
                return 0L;
            }

            long paid = 0;
            var now = data.Now;
            var elapsed = (int)Math.Floor((now - officer.PaidUntil) / TimeSpan.FromDays(1));
            var days = Math.Min(elapsed, LawRules.MaxPaidDays);
            var salary = Ranks.SalaryCents[officer.Rank];
            var covered = Math.Min(days, (int)(await wallet.BalanceAsync(treasury, cancellationToken) / salary));
            if (covered > 0)
            {
                await wallet.DebitAsync(treasury, salary * covered, $"police salary {covered} days", cancellationToken);
                await wallet.CreditAsync(player, salary * covered, $"police salary {covered} days", cancellationToken);
                paid += salary * covered;
            }

            if (elapsed > 0)
            {
                // Days the treasury could not pay are lost, so a poor country does not pile up debts to its police.
                await data.ExecuteAsync("UPDATE law.officers SET paid_until = @until WHERE player_id = @p", cancellationToken,
                    ("p", player), ("until", officer.PaidUntil + TimeSpan.FromDays(elapsed)));
            }

            var bonuses = await data.QueryAsync(
                """
                SELECT id, collected_cents FROM law.cases
                WHERE decided_by = @p AND NOT bonus_paid AND collected_cents > 0
                    AND ((status = 'sentenced' AND decided_at <= @window) OR status = 'upheld')
                FOR UPDATE
                """, r => (Id: r.GetInt64(0), Collected: r.GetInt64(1)), cancellationToken, ("p", player), ("window", now - LawRules.AppealWindow));
            foreach (var (id, collected) in bonuses)
            {
                var bonus = collected * LawRules.BonusPercent / 100;
                if (bonus > 0 && await wallet.BalanceAsync(treasury, cancellationToken) >= bonus)
                {
                    await wallet.DebitAsync(treasury, bonus, $"police bonus case {id}", cancellationToken);
                    await wallet.CreditAsync(player, bonus, $"police bonus case {id}", cancellationToken);
                    paid += bonus;
                }

                await data.ExecuteAsync("UPDATE law.cases SET bonus_paid = true WHERE id = @id", cancellationToken, ("id", id));
            }

            return paid;
        }, cancellationToken);
}
