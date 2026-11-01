using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Politics;

/// <summary>A country's government in a term: the president (null when nobody was elected or they were recalled) and the laws.</summary>
public sealed record Government(
    string Country, int Term, Guid? President, int PlotTaxPerMille, int ForeignerFeePercent, int TariffPercent, string? Subsidy, bool Recalled);

public sealed partial class NationService(
    GameDb db, ICountryMap map, IWallet wallet, ICharacterNames names, IServiceProvider services, TimeProvider time)
    : INationalPolicy, ICountries
{
    private int Term => NationRules.TermOf(time.GetUtcNow());

    public CountryInfo RequireCountry(string? code) =>
        map.Find(code) ?? throw new GameException("no_country", "There is no such country.");

    // --- Citizenship -----------------------------------------------------------------------------

    public async Task<(string? Country, DateTimeOffset Since)> CitizenAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT country, since FROM politics.citizens WHERE player_id = @player", cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)) : (null, default);
    }

    public async Task<string?> CitizenshipAsync(Guid player, CancellationToken cancellationToken) => (await CitizenAsync(player, cancellationToken)).Country;

    /// <summary>Citizens of a country, by account id.</summary>
    private async Task<List<Guid>> CitizensAsync(string country, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT player_id FROM politics.citizens WHERE country = @country", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var citizens = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            citizens.Add(reader.GetGuid(0));
        }

        return citizens;
    }

    /// <summary>A player who owns a plot in the country becomes its citizen; citizenship changes once a week at most.</summary>
    public Task ChooseCitizenshipAsync(Guid player, string? code, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            var (current, since) = await CitizenAsync(player, cancellationToken);
            if (current == country.Code)
            {
                return;
            }

            if ((await map.PlotsAsync(player, cancellationToken)).GetValueOrDefault(country.Code) == 0)
            {
                throw new GameException("no_plot_here", "Own a plot in a country to become its citizen.");
            }

            var now = time.GetUtcNow();
            if (current is not null && now - since < NationRules.CitizenshipChange)
            {
                throw new GameException("citizenship_recent", "Citizenship changes once a week at most.");
            }

            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO politics.citizens (player_id, country, since) VALUES (@player, @country, @now)
                ON CONFLICT (player_id) DO UPDATE SET country = excluded.country, since = excluded.since
                """, cancellationToken);
            upsert.Parameters.AddWithValue("player", player);
            upsert.Parameters.AddWithValue("country", country.Code);
            upsert.Parameters.AddWithValue("now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Fails unless the player is a citizen of the country and still owns a plot there.</summary>
    private async Task RequireVoterAsync(Guid player, string country, CancellationToken cancellationToken)
    {
        if (await CitizenshipAsync(player, cancellationToken) != country)
        {
            throw new GameException("not_citizen", "Only the country's citizens take part in its politics.");
        }

        if ((await map.PlotsAsync(player, cancellationToken)).GetValueOrDefault(country) == 0)
        {
            throw new GameException("no_plot_here", "Citizens who own no plot in the country any more take no part in its politics.");
        }
    }

    // --- The government --------------------------------------------------------------------------

    /// <summary>The government of the term: the winner of the last term's election, worked out once and kept. The president starts with the
    /// plot tax and fee they promised; the tariff and the subsidy stay as they were.</summary>
    public Task<Government> GovernmentAsync(string country, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var term = Term;
            if (await ReadGovernmentAsync(country, term, cancellationToken) is { } government)
            {
                return government;
            }

            var before = await ReadGovernmentAsync(country, term - 1, cancellationToken);
            (Guid Id, int Tax, int Fee)? winner = null;
            await using (var count = await db.CommandAsync(
                """
                SELECT c.player_id, c.plot_tax_per_mille, c.foreigner_fee_percent
                FROM politics.candidates c JOIN politics.votes v ON v.country = c.country AND v.term = c.term AND v.candidate_id = c.player_id
                WHERE c.country = @country AND c.term = @term
                GROUP BY c.player_id, c.plot_tax_per_mille, c.foreigner_fee_percent ORDER BY count(*) DESC, c.player_id LIMIT 1
                """, cancellationToken))
            {
                count.Parameters.AddWithValue("country", country);
                count.Parameters.AddWithValue("term", term - 1);
                await using var reader = await count.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    winner = (reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2));
                }
            }

            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO politics.governments (country, term, president_id, plot_tax_per_mille, foreigner_fee_percent, tariff_percent, subsidy, recalled)
                VALUES (@country, @term, @president, @tax, @fee, @tariff, @subsidy, FALSE)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("country", country);
                insert.Parameters.AddWithValue("term", term);
                insert.Parameters.AddWithValue("president", (object?)winner?.Id ?? DBNull.Value);
                insert.Parameters.AddWithValue("tax", winner?.Tax ?? NationRules.DefaultPlotTaxPerMille);
                insert.Parameters.AddWithValue("fee", winner?.Fee ?? 0);
                insert.Parameters.AddWithValue("tariff", before?.TariffPercent ?? 0);
                insert.Parameters.AddWithValue("subsidy", (object?)before?.Subsidy ?? DBNull.Value);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    await SettleCandidatesAsync(country, term, cancellationToken);
                }
            }

            return (await ReadGovernmentAsync(country, term, cancellationToken))!;
        }, cancellationToken);

    private async Task<Government?> ReadGovernmentAsync(string country, int term, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT president_id, plot_tax_per_mille, foreigner_fee_percent, tariff_percent, subsidy, recalled
            FROM politics.governments WHERE country = @country AND term = @term
            """, cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("term", term);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Government(country, term, reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5))
            : null;
    }

    private async Task<Dictionary<string, Guid>> MinistersAsync(string country, int term, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT office, player_id FROM politics.ministers WHERE country = @country AND term = @term", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("term", term);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ministers = new Dictionary<string, Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ministers[reader.GetString(0)] = reader.GetGuid(1);
        }

        return ministers;
    }

    public async Task<IReadOnlySet<string>> OfficesAsync(Guid player, string country, CancellationToken cancellationToken)
    {
        var government = await GovernmentAsync(country, cancellationToken);
        var offices = (await MinistersAsync(country, government.Term, cancellationToken)).Where(m => m.Value == player).Select(m => m.Key).ToHashSet();
        if (government.President == player)
        {
            offices.Add(CountryOffices.President);
        }

        return offices;
    }

    /// <summary>Fails unless the player is the president or holds the office.</summary>
    private async Task<Government> RequireOfficeAsync(Guid player, string country, string office, CancellationToken cancellationToken)
    {
        var offices = await OfficesAsync(player, country, cancellationToken);
        if (!offices.Contains(CountryOffices.President) && !offices.Contains(office))
        {
            throw new GameException("not_in_office", $"Only the president or the {office} minister does this.");
        }

        return await GovernmentAsync(country, cancellationToken);
    }

    /// <summary>The president names a citizen to a ministry, or nobody when <paramref name="name"/> is empty.</summary>
    public Task AppointAsync(Guid player, string? code, string? office, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            var government = await GovernmentAsync(country.Code, cancellationToken);
            if (government.President != player)
            {
                throw new GameException("not_in_office", "Only the president names ministers.");
            }

            if (office is null || !CountryOffices.Ministries.Contains(office))
            {
                throw new GameException("bad_office", "There is no such ministry.");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                await using var remove = await db.CommandAsync(
                    "DELETE FROM politics.ministers WHERE country = @country AND term = @term AND office = @office", cancellationToken);
                remove.Parameters.AddWithValue("country", country.Code);
                remove.Parameters.AddWithValue("term", government.Term);
                remove.Parameters.AddWithValue("office", office);
                await remove.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            var minister = await names.AccountAsync(name, cancellationToken);
            if (minister is not { } id || await CitizenshipAsync(id, cancellationToken) != country.Code)
            {
                throw new GameException("not_citizen", "Ministers are citizens of the country.");
            }

            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO politics.ministers (country, term, office, player_id) VALUES (@country, @term, @office, @player)
                ON CONFLICT (country, term, office) DO UPDATE SET player_id = excluded.player_id
                """, cancellationToken);
            upsert.Parameters.AddWithValue("country", country.Code);
            upsert.Parameters.AddWithValue("term", government.Term);
            upsert.Parameters.AddWithValue("office", office);
            upsert.Parameters.AddWithValue("player", id);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>The president or the minister in charge sets one law within its bounds.</summary>
    public Task SetLawAsync(Guid player, string? code, string? law, int value, string? item, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            if (law is null || !NationRules.Laws.TryGetValue(law, out var office))
            {
                throw new GameException("bad_law", "There is no such law.");
            }

            var government = await RequireOfficeAsync(player, country.Code, office, cancellationToken);
            string column;
            object stored = value;
            switch (law)
            {
                case "plotTax":
                    Check(value, NationRules.MinPlotTaxPerMille, NationRules.MaxPlotTaxPerMille);
                    column = "plot_tax_per_mille";
                    break;
                case "foreignerFee":
                    Check(value, 0, NationRules.MaxForeignerFeePercent);
                    column = "foreigner_fee_percent";
                    break;
                case "tariff":
                    Check(value, 0, NationRules.MaxTariffPercent);
                    column = "tariff_percent";
                    break;
                case "subsidy":
                    // The group of goods comes as the item; none takes the subsidy off.
                    if (!string.IsNullOrEmpty(item) && !NationRules.Subsidies.ContainsKey(item))
                    {
                        throw new GameException("bad_law_value", "There is no such group of goods.");
                    }

                    column = "subsidy";
                    stored = string.IsNullOrEmpty(item) ? DBNull.Value : item;
                    break;
                default:
                    await SetItemTariffAsync(country.Code, item, value, cancellationToken);
                    return;
            }

            await using var update = await db.CommandAsync(
                $"UPDATE politics.governments SET {column} = @value WHERE country = @country AND term = @term", cancellationToken);
            update.Parameters.AddWithValue("country", country.Code);
            update.Parameters.AddWithValue("term", government.Term);
            update.Parameters.AddWithValue("value", stored);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private static void Check(int value, int low, int high)
    {
        if (value < low || value > high)
        {
            throw new GameException("bad_law_value", $"This law takes {low}-{high}.");
        }
    }

    /// <summary>A tariff on one item instead of the general one; a negative value takes it off.</summary>
    private async Task SetItemTariffAsync(string country, string? item, int percent, CancellationToken cancellationToken)
    {
        if (item is null || !Items.Names.ContainsKey(item))
        {
            throw new GameException("unknown_item", "There is no such item.");
        }

        if (percent > NationRules.MaxTariffPercent)
        {
            throw new GameException("bad_law_value", $"This law takes 0-{NationRules.MaxTariffPercent}.");
        }

        var tariffs = await ItemTariffsAsync(country, cancellationToken);
        if (percent >= 0 && !tariffs.ContainsKey(item) && tariffs.Count >= NationRules.MaxItemTariffs)
        {
            throw new GameException("too_many_tariffs", $"A country sets its own tariff for {NationRules.MaxItemTariffs} items at most.");
        }

        await using var command = await db.CommandAsync(
            percent < 0
                ? "DELETE FROM politics.item_tariffs WHERE country = @country AND item = @item"
                : """
                  INSERT INTO politics.item_tariffs (country, item, percent) VALUES (@country, @item, @percent)
                  ON CONFLICT (country, item) DO UPDATE SET percent = excluded.percent
                  """,
            cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("percent", percent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Dictionary<string, int>> ItemTariffsAsync(string country, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT item, percent FROM politics.item_tariffs WHERE country = @country", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tariffs = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tariffs[reader.GetString(0)] = reader.GetInt32(1);
        }

        return tariffs;
    }

    // --- The treasury ----------------------------------------------------------------------------

    public Task<long> TreasuryCentsAsync(string country, CancellationToken cancellationToken) =>
        wallet.BalanceAsync(CountryTreasury.Account(country), cancellationToken);

    /// <summary>Money the treasury takes in: the president's pay comes off it first.</summary>
    private async Task TakeInAsync(string country, long cents, string reason, CancellationToken cancellationToken)
    {
        if (cents <= 0)
        {
            return;
        }

        var president = (await GovernmentAsync(country, cancellationToken)).President;
        var pay = president is null ? 0 : cents * NationRules.PresidentSharePercent / 100;
        if (pay > 0)
        {
            await wallet.CreditAsync(president!.Value, pay, $"president's pay ({country}): {reason}", cancellationToken);
        }

        if (cents - pay > 0)
        {
            var balance = await wallet.CreditAsync(CountryTreasury.Account(country), cents - pay, reason, cancellationToken);
            await LedgerAsync(country, cents - pay, balance, reason, cancellationToken);
        }
    }

    /// <summary>Takes money out of the treasury; fails with <c>treasury_short</c> when it has less.</summary>
    private async Task SpendAsync(string country, long cents, string reason, CancellationToken cancellationToken)
    {
        long balance;
        try
        {
            balance = await wallet.DebitAsync(CountryTreasury.Account(country), cents, reason, cancellationToken);
        }
        catch (GameException e) when (e.Code == "not_enough_money")
        {
            throw new GameException("treasury_short", "The country's treasury doesn't have that much money.");
        }

        await LedgerAsync(country, -cents, balance, reason, cancellationToken);
    }

    private async Task LedgerAsync(string country, long cents, long balance, string reason, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO politics.treasury_ledger (country, amount_cents, balance_after_cents, reason, created_at)
            VALUES (@country, @cents, @balance, @reason, @at)
            """, cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("cents", cents);
        command.Parameters.AddWithValue("balance", balance);
        command.Parameters.AddWithValue("reason", reason.Length > 200 ? reason[..200] : reason);
        command.Parameters.AddWithValue("at", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Dictionary<string, int>> InvestmentsAsync(string country, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT kind, level FROM politics.investments WHERE country = @country", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var levels = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            levels[reader.GetString(0)] = reader.GetInt32(1);
        }

        return levels;
    }

    /// <summary>The president or the minister in charge builds the next level of a national investment, paid from the treasury.</summary>
    public Task InvestAsync(Guid player, string? code, string? kind, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            if (kind is null || !NationRules.Investments.TryGetValue(kind, out var investment))
            {
                throw new GameException("bad_investment", "There is no such investment.");
            }

            await RequireOfficeAsync(player, country.Code, investment.Office, cancellationToken);
            await LockAsync(country.Code, cancellationToken);
            var level = (await InvestmentsAsync(country.Code, cancellationToken)).GetValueOrDefault(kind);
            if (level >= investment.MaxLevel)
            {
                throw new GameException("investment_done", "This investment is as big as it gets.");
            }

            await SpendAsync(country.Code, NationRules.InvestmentCents[level], $"{investment.Name}: poziom {level + 1}", cancellationToken);
            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO politics.investments (country, kind, level) VALUES (@country, @kind, 1)
                ON CONFLICT (country, kind) DO UPDATE SET level = politics.investments.level + 1
                """, cancellationToken);
            upsert.Parameters.AddWithValue("country", country.Code);
            upsert.Parameters.AddWithValue("kind", kind);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>One change of a country at a time.</summary>
    private async Task LockAsync(string country, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT pg_advisory_xact_lock(hashtext('politics/' || @country))", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Elections -------------------------------------------------------------------------------

    /// <summary>A citizen stands for president in the next term with a programme; the first time this term they put the deposit down.</summary>
    public Task StandAsync(Guid player, string? code, int plotTaxPerMille, int foreignerFeePercent, string? slogan, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            await RequireVoterAsync(player, country.Code, cancellationToken);
            if (plotTaxPerMille < NationRules.MinPlotTaxPerMille || plotTaxPerMille > NationRules.MaxPlotTaxPerMille
                || foreignerFeePercent < 0 || foreignerFeePercent > NationRules.MaxForeignerFeePercent)
            {
                throw new GameException("bad_law_value", "The programme's laws are out of bounds.");
            }

            slogan = (slogan ?? "").Trim();
            if (slogan.Length > NationRules.MaxSloganLength)
            {
                throw new GameException("bad_slogan", $"A slogan is at most {NationRules.MaxSloganLength} characters.");
            }

            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO politics.candidates (country, term, player_id, plot_tax_per_mille, foreigner_fee_percent, slogan, deposit_cents, settled)
                VALUES (@country, @term, @player, @tax, @fee, @slogan, @deposit, FALSE)
                ON CONFLICT (country, term, player_id) DO UPDATE SET plot_tax_per_mille = @tax, foreigner_fee_percent = @fee, slogan = @slogan
                RETURNING xmax = 0
                """, cancellationToken);
            upsert.Parameters.AddWithValue("country", country.Code);
            upsert.Parameters.AddWithValue("term", Term);
            upsert.Parameters.AddWithValue("player", player);
            upsert.Parameters.AddWithValue("tax", plotTaxPerMille);
            upsert.Parameters.AddWithValue("fee", foreignerFeePercent);
            upsert.Parameters.AddWithValue("slogan", slogan);
            upsert.Parameters.AddWithValue("deposit", NationRules.DepositCents);
            if ((bool)(await upsert.ExecuteScalarAsync(cancellationToken))!)
            {
                await wallet.DebitAsync(player, NationRules.DepositCents, $"election deposit in {country.Code}", cancellationToken);
            }
        }, cancellationToken);

    /// <summary>A candidate withdraws from this term's election: the deposit comes back whole, and the votes cast for them void.</summary>
    public Task WithdrawAsync(Guid player, string? code, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            await using var command = await db.CommandAsync(
                """
                DELETE FROM politics.candidates WHERE country = @country AND term = @term AND player_id = @player AND NOT settled
                RETURNING deposit_cents;
                DELETE FROM politics.votes WHERE country = @country AND term = @term AND candidate_id = @player;
                """, cancellationToken);
            command.Parameters.AddWithValue("country", country.Code);
            command.Parameters.AddWithValue("term", Term);
            command.Parameters.AddWithValue("player", player);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long deposit)
            {
                throw new GameException("not_standing", "You do not stand in this term's election.");
            }

            await wallet.CreditAsync(player, deposit, $"election deposit back in {country.Code}", cancellationToken);
        }, cancellationToken);

    /// <summary>A citizen votes for a candidate of the election going on (a new vote replaces the old).</summary>
    public Task VoteAsync(Guid player, string? code, string? candidate, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            await RequireVoterAsync(player, country.Code, cancellationToken);
            var chosen = await names.AccountAsync(candidate ?? "", cancellationToken);
            if (chosen is not { } id || !(await CandidatesAsync(country.Code, Term, cancellationToken)).Any(c => c.PlayerId == id))
            {
                throw new GameException("not_candidate", "Vote for a player who stands in this term's election.");
            }

            await using var vote = await db.CommandAsync(
                """
                INSERT INTO politics.votes (country, term, voter_id, candidate_id) VALUES (@country, @term, @voter, @candidate)
                ON CONFLICT (country, term, voter_id) DO UPDATE SET candidate_id = excluded.candidate_id
                """, cancellationToken);
            vote.Parameters.AddWithValue("country", country.Code);
            vote.Parameters.AddWithValue("term", Term);
            vote.Parameters.AddWithValue("voter", player);
            vote.Parameters.AddWithValue("candidate", id);
            await vote.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private async Task<List<CandidateRow>> CandidatesAsync(string country, int term, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT player_id, plot_tax_per_mille, foreigner_fee_percent, slogan FROM politics.candidates WHERE country = @country AND term = @term",
            cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("term", term);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<CandidateRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new CandidateRow
            {
                PlayerId = reader.GetGuid(0), PlotTaxPerMille = reader.GetInt32(1), ForeignerFeePercent = reader.GetInt32(2), Slogan = reader.GetString(3),
            });
        }

        return found;
    }

    /// <summary>Counts the votes of past terms for those who stood: the deposit comes back with a tenth of the votes and goes into the treasury without.</summary>
    private async Task SettleCandidatesAsync(string country, int term, CancellationToken cancellationToken)
    {
        var settled = new List<(int Term, Guid Player, long Deposit, bool Back)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT c.term, c.player_id, c.deposit_cents,
                   (SELECT count(*) FROM politics.votes v WHERE v.country = c.country AND v.term = c.term AND v.candidate_id = c.player_id),
                   (SELECT count(*) FROM politics.votes v WHERE v.country = c.country AND v.term = c.term)
            FROM politics.candidates c WHERE c.country = @country AND c.term < @term AND NOT c.settled
            FOR UPDATE OF c
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("country", country);
            command.Parameters.AddWithValue("term", term);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long votes = reader.GetInt64(3), all = reader.GetInt64(4);
                settled.Add((reader.GetInt32(0), reader.GetGuid(1), reader.GetInt64(2), all > 0 && votes * 100 >= all * NationRules.DepositBackPercent));
            }
        }

        foreach (var (candidateTerm, player, deposit, back) in settled)
        {
            await using var mark = await db.CommandAsync(
                "UPDATE politics.candidates SET settled = TRUE WHERE country = @country AND term = @term AND player_id = @player", cancellationToken);
            mark.Parameters.AddWithValue("country", country);
            mark.Parameters.AddWithValue("term", candidateTerm);
            mark.Parameters.AddWithValue("player", player);
            await mark.ExecuteNonQueryAsync(cancellationToken);
            if (deposit <= 0)
            {
                continue;
            }

            if (back)
            {
                await wallet.CreditAsync(player, deposit, $"election deposit back in {country}", cancellationToken);
            }
            else
            {
                // Straight into the treasury: the new president gets no pay from lost deposits.
                var balance = await wallet.CreditAsync(CountryTreasury.Account(country), deposit, "kaucja wyborcza przepadła", cancellationToken);
                await LedgerAsync(country, deposit, balance, "kaucja wyborcza przepadła", cancellationToken);
            }
        }
    }

    /// <summary>The owner or the treasurer of a company backs a candidate with money from its cash, which the candidate gets, for all to see.</summary>
    public Task SponsorAsync(Guid player, string? code, string? candidate, long cents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            if (cents < 1_00 || cents > NationRules.MaxSponsorCents)
            {
                throw new GameException("bad_amount", "Back a candidate with 1 $ to 1 000 000 $.");
            }

            var chosen = await names.AccountAsync(candidate ?? "", cancellationToken);
            if (chosen is not { } id || !(await CandidatesAsync(country.Code, Term, cancellationToken)).Any(c => c.PlayerId == id))
            {
                throw new GameException("not_candidate", "Back a player who stands in this term's election.");
            }

            // The company's money would only go round into its own owner's or treasurer's wallet.
            if (id == player)
            {
                throw new GameException("own_campaign", "A company you run does not back you yourself.");
            }

            // Looked up only here: the companies module needs the world's plots, which need this service.
            var company = await services.GetRequiredService<ICompanyCash>().SpendAsync(player, cents, $"kampania: {candidate} ({country.Name})", cancellationToken);
            await wallet.CreditAsync(id, cents, $"campaign money from {company}", cancellationToken);
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO politics.sponsorships (country, term, candidate_id, company, cents, created_at)
                VALUES (@country, @term, @candidate, @company, @cents, @at)
                """, cancellationToken);
            insert.Parameters.AddWithValue("country", country.Code);
            insert.Parameters.AddWithValue("term", Term);
            insert.Parameters.AddWithValue("candidate", id);
            insert.Parameters.AddWithValue("company", company);
            insert.Parameters.AddWithValue("cents", cents);
            insert.Parameters.AddWithValue("at", time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>A citizen signs the motion to recall the president; with two thirds of the citizens signed the president and the ministers
    /// are out, and the laws go back to the defaults.</summary>
    public Task<bool> RecallAsync(Guid player, string? code, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var country = RequireCountry(code);
            await RequireVoterAsync(player, country.Code, cancellationToken);
            await LockAsync(country.Code, cancellationToken);
            var government = await GovernmentAsync(country.Code, cancellationToken);
            if (government.President is null)
            {
                throw new GameException("no_president", "The country has no president to recall.");
            }

            await using (var sign = await db.CommandAsync(
                "INSERT INTO politics.recalls (country, term, signer_id) VALUES (@country, @term, @player) ON CONFLICT DO NOTHING", cancellationToken))
            {
                sign.Parameters.AddWithValue("country", country.Code);
                sign.Parameters.AddWithValue("term", government.Term);
                sign.Parameters.AddWithValue("player", player);
                await sign.ExecuteNonQueryAsync(cancellationToken);
            }

            var citizens = await CitizensAsync(country.Code, cancellationToken);
            var signatures = (await RecallSignersAsync(country.Code, government.Term, cancellationToken)).Count(citizens.Contains);
            if (!NationRules.Recalls(signatures, citizens.Count))
            {
                return false;
            }

            await using var recall = await db.CommandAsync(
                """
                UPDATE politics.governments SET president_id = NULL, plot_tax_per_mille = @tax, foreigner_fee_percent = 0, tariff_percent = 0,
                    subsidy = NULL, recalled = TRUE
                WHERE country = @country AND term = @term;
                DELETE FROM politics.ministers WHERE country = @country AND term = @term
                """, cancellationToken);
            recall.Parameters.AddWithValue("country", country.Code);
            recall.Parameters.AddWithValue("term", government.Term);
            recall.Parameters.AddWithValue("tax", NationRules.DefaultPlotTaxPerMille);
            await recall.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<List<Guid>> RecallSignersAsync(string country, int term, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT signer_id FROM politics.recalls WHERE country = @country AND term = @term", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("term", term);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var signers = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            signers.Add(reader.GetGuid(0));
        }

        return signers;
    }

    // --- What the government changes in the world (INationalPolicy, ICountries) ---------------------

    public async Task<int> PlotTaxPerMilleAsync(string country, CancellationToken cancellationToken) =>
        (await GovernmentAsync(country, cancellationToken)).PlotTaxPerMille;

    public Task CollectPlotTaxAsync(string country, long taxCents, CancellationToken cancellationToken) =>
        TakeInAsync(country, taxCents * NationRules.TreasurySharePercent / 100, "podatek od działek", cancellationToken);

    public async Task<(long PriceCents, long FeeCents)> PlotPriceAsync(Guid buyer, string country, long priceCents, CancellationToken cancellationToken)
    {
        var home = await CitizenshipAsync(buyer, cancellationToken);
        if (home == country)
        {
            var housing = (await InvestmentsAsync(country, cancellationToken)).GetValueOrDefault("housing");
            return ((long)Math.Round(priceCents * (1 - NationRules.HousingPerLevel * housing)), 0);
        }

        if (home is not null && await HasRelationAsync(country, home, "embargo", cancellationToken))
        {
            throw new GameException("embargo", "This country sells no plots to your country's citizens.");
        }

        if (home is not null && await HasRelationAsync(country, home, "alliance", cancellationToken))
        {
            return (priceCents, 0);
        }

        var fee = priceCents * (await GovernmentAsync(country, cancellationToken)).ForeignerFeePercent / 100;
        return (priceCents + fee, fee);
    }

    public async Task ChargePlotFeeAsync(Guid buyer, string country, long feeCents, int x, int y, CancellationToken cancellationToken)
    {
        if (feeCents > 0)
        {
            await wallet.DebitAsync(buyer, feeCents, $"foreigner's fee for plot {x}:{y}", cancellationToken);
            await TakeInAsync(country, feeCents, "opłata od cudzoziemca za działkę", cancellationToken);
        }
    }

    public async Task<double> RailFactorAsync(string country, CancellationToken cancellationToken) =>
        1 - NationRules.RailwayPerLevel * (await InvestmentsAsync(country, cancellationToken)).GetValueOrDefault("railway");

    public async Task<long> SubsidizeAsync(Guid player, string country, string item, long cents, CancellationToken cancellationToken)
    {
        var subsidy = (await GovernmentAsync(country, cancellationToken)).Subsidy;
        if (subsidy is null || !NationRules.Subsidies[subsidy].Items.Contains(item))
        {
            return 0;
        }

        var paid = Math.Min(cents * NationRules.SubsidyPercent / 100, await TreasuryCentsAsync(country, cancellationToken));
        if (paid <= 0)
        {
            return 0;
        }

        await SpendAsync(country, paid, $"dotacja: {Items.Names.GetValueOrDefault(item, item)}", cancellationToken);
        await wallet.CreditAsync(player, paid, $"subsidy of {country}: {item}", cancellationToken);
        return paid;
    }

    public Task<string?> CountryOfPlotAsync(int x, int y, CancellationToken cancellationToken) => Task.FromResult(map.At(x, y)?.Code);

    public async Task<Guid?> TreasuryAsync(string country, CancellationToken cancellationToken) =>
        map.Find(country) is not null && (await GovernmentAsync(country, cancellationToken)).President is not null ? CountryTreasury.Account(country) : null;

    public async Task<CountryTrade> TradeAsync(string from, string to, CancellationToken cancellationToken)
    {
        if (from == to || map.Find(to) is null)
        {
            return new CountryTrade(false, 0, new Dictionary<string, int>());
        }

        if (await HasRelationAsync(to, from, "embargo", cancellationToken))
        {
            return new CountryTrade(true, 0, new Dictionary<string, int>());
        }

        if (await HasRelationAsync(to, from, "alliance", cancellationToken))
        {
            return new CountryTrade(false, 0, new Dictionary<string, int>());
        }

        return new CountryTrade(false, (await GovernmentAsync(to, cancellationToken)).TariffPercent, await ItemTariffsAsync(to, cancellationToken));
    }
}
