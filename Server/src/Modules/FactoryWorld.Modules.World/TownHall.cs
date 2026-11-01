using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>
/// The politics of a town hall. The mayor's tax goes into the town's treasury, and the mayor keeps <see cref="MayorSharePercent"/>%
/// of it as pay; the mayor spends the treasury on buildings that make the town better for good (<see cref="Investments"/>).
/// Players who want to be mayor stand with a programme (the tax and bonus they promise) and a deposit, which comes back with
/// a tenth of the votes and goes into the treasury without; whether a mayor kept their promises shows next to their name.
/// Companies back candidates with money from their cash, for all to see. The mayor puts ordinances (<see cref="Ordinances"/>)
/// to a vote of the townsfolk for the rest of the week, and two thirds of the townsfolk recall a mayor before their week is out.
/// </summary>
public static class TownHallRules
{
    /// <summary>The part of the town's tax the mayor gets as pay; the rest goes into the treasury.</summary>
    public const int MayorSharePercent = 20;

    /// <summary>What a candidate puts down to stand; it comes back with <see cref="DepositBackPercent"/>% of the votes.</summary>
    public const long DepositCents = 1_000_00;

    public const int DepositBackPercent = 10;

    public const int MaxSloganLength = 80;

    /// <summary>A company backs a candidate with this much at most at once.</summary>
    public const long MaxSponsorCents = 1_000_000_00;

    /// <summary>Buildings the mayor puts up with the treasury: name, what each level does, most levels.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Effect, int MaxLevel)> Investments =
        new Dictionary<string, (string, string, int)>
        {
            ["park"] = ("Park miejski", "o 10% więcej klientów w sklepach na poziom", 3),
            ["station"] = ("Dworzec", "opłaty kolejowe z działek powiatu o 5% niższe na poziom", 3),
            ["market_hall"] = ("Hala targowa", "miasto płaci za dostawy o 5% więcej na poziom", 3),
        };

    /// <summary>What the next level of a building costs, by the level it has.</summary>
    public static readonly IReadOnlyList<long> InvestmentCents = [50_000_00, 150_000_00, 450_000_00];

    public const double ParkCustomers = 0.1;

    public const double StationRail = 0.05;

    public const double MarketHallPremium = 0.05;

    /// <summary>Ordinances the mayor puts to the vote; one that passes holds for the rest of the week.</summary>
    public static readonly IReadOnlyDictionary<string, string> Ordinances = new Dictionary<string, string>
    {
        ["settlers"] = "Ulga osiedleńcza: działki w powiecie o 10% tańsze",
        ["late_shops"] = "Handel do późna: o 15% więcej klientów w sklepach",
        ["busy_offices"] = "Więcej petentów: biura obsługują 30 klientów dziennie zamiast 20",
        ["recycling"] = "Program recyklingu: kasa miasta dopłaca połowę do wywozu śmieci",
    };

    public static readonly TimeSpan OrdinanceVote = TimeSpan.FromHours(24);

    public const double SettlersPlots = 0.9;

    public const double LateShops = 1.15;

    public const int BusyOfficeSales = 30;

    public const int RecyclingPercent = 50;

    /// <summary>Signatures that recall a mayor: two thirds of the townsfolk, and at least two.</summary>
    public static bool Recalls(int signatures, int townsfolk) => signatures >= 2 && signatures * 3 >= townsfolk * 2;

    /// <summary>Whether a mayor kept what they promised: no higher tax, and the bonus they named.</summary>
    public static bool Kept(int promisedTax, string? promisedBonus, int tax, string? bonus) =>
        tax <= promisedTax && (promisedBonus is null || promisedBonus == bonus);
}

public sealed class TownTreasuryRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public long Cents { get; set; }
}

/// <summary>Every change of a town's treasury, so the townsfolk see where the money went.</summary>
public sealed class TownLedgerRow
{
    public long Id { get; set; }

    public int TownX { get; set; }

    public int TownY { get; set; }

    public long AmountCents { get; set; }

    public long BalanceAfterCents { get; set; }

    public string Reason { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TownInvestmentRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public string Kind { get; set; } = "";

    public int Level { get; set; }
}

public sealed class TownRecallRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid SignerId { get; set; }
}

/// <summary>A player standing in a week's election, with what they promise and the deposit they put down (held until the vote is counted).</summary>
public sealed class TownCandidateRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid PlayerId { get; set; }

    public int TaxPercent { get; set; }

    public string? Bonus { get; set; }

    public string Slogan { get; set; } = "";

    public long DepositCents { get; set; }

    public bool Settled { get; set; }
}

public sealed class TownSponsorshipRow
{
    public long Id { get; set; }

    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid CandidateId { get; set; }

    public string Company { get; set; } = "";

    public long Cents { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TownOrdinanceRow
{
    public Guid Id { get; set; }

    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public string Kind { get; set; } = "";

    public Guid ProposedBy { get; set; }

    public DateTimeOffset VotingEnds { get; set; }
}

public sealed class TownOrdinanceVoteRow
{
    public Guid OrdinanceId { get; set; }

    public Guid VoterId { get; set; }

    public bool Yes { get; set; }
}

/// <param name="Open">Still being voted on.</param>
public sealed record Ordinance(Guid Id, string Kind, Guid ProposedBy, DateTimeOffset VotingEnds, int Yes, int No, bool Open)
{
    public bool InForce => !Open && Yes > No;
}

public sealed partial class CityService
{
    public async Task<long> TreasuryAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT cents FROM world.town_treasuries WHERE town_x = @x AND town_y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        return await command.ExecuteScalarAsync(cancellationToken) is long cents ? cents : 0;
    }

    /// <summary>Puts money into the treasury or takes it out (fails with <c>treasury_short</c> when it has less), with a line in its ledger.</summary>
    /// <summary>The mayor's tax on the plots of the town's county (<see cref="TownPolicy.PlotTaxPerMille"/>), paid with the plot tax (<see cref="PlotTaxService"/>): the mayor
    /// gets their pay out of it and the rest goes into the treasury. Nothing without a mayor.</summary>
    public async Task CollectPlotTaxAsync(Town town, long cents, string reason, CancellationToken cancellationToken)
    {
        if (cents <= 0 || (await PolicyAsync(town, cancellationToken)).Mayor is not { } mayor)
        {
            return;
        }

        var pay = cents * TownHallRules.MayorSharePercent / 100;
        if (pay > 0)
        {
            await wallet.CreditAsync(mayor, pay, $"mayor's pay in {town.Name}: {reason}", cancellationToken);
        }

        await ChangeTreasuryAsync(town, cents - pay, reason, cancellationToken);
    }

    private async Task ChangeTreasuryAsync(Town town, long cents, string reason, CancellationToken cancellationToken)
    {
        if (cents == 0)
        {
            return;
        }

        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.town_treasuries (town_x, town_y, cents) SELECT @x, @y, 0 WHERE @cents > 0 ON CONFLICT DO NOTHING;
            UPDATE world.town_treasuries SET cents = cents + @cents WHERE town_x = @x AND town_y = @y AND cents + @cents >= 0 RETURNING cents
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("cents", cents);
        if (await command.ExecuteScalarAsync(cancellationToken) is not long balance)
        {
            throw new GameException("treasury_short", "The town's treasury doesn't have that much money.");
        }

        await using var ledger = await db.CommandAsync(
            """
            INSERT INTO world.town_ledger (town_x, town_y, amount_cents, balance_after_cents, reason, created_at)
            VALUES (@x, @y, @cents, @balance, @reason, @at)
            """, cancellationToken);
        ledger.Parameters.AddWithValue("x", town.X);
        ledger.Parameters.AddWithValue("y", town.Y);
        ledger.Parameters.AddWithValue("cents", cents);
        ledger.Parameters.AddWithValue("balance", balance);
        ledger.Parameters.AddWithValue("reason", reason.Length > 200 ? reason[..200] : reason);
        ledger.Parameters.AddWithValue("at", time.GetUtcNow());
        await ledger.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Dictionary<string, int>> InvestmentsAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT kind, level FROM world.town_investments WHERE town_x = @x AND town_y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var levels = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            levels[reader.GetString(0)] = reader.GetInt32(1);
        }

        return levels;
    }

    private async Task<int> InvestmentAsync(Town town, string kind, CancellationToken cancellationToken) =>
        (await InvestmentsAsync(town, cancellationToken)).GetValueOrDefault(kind);

    private async Task RequireMayorAsync(Guid player, Town town, string what, CancellationToken cancellationToken)
    {
        if ((await PolicyAsync(town, cancellationToken)).Mayor != player)
        {
            throw new GameException("not_mayor", $"Only this week's mayor {what}.");
        }
    }

    private async Task RequireTownsmanAsync(Guid player, Town town, CancellationToken cancellationToken)
    {
        if (!(await TownsfolkAsync(town, cancellationToken)).Contains(player))
        {
            throw new GameException("not_townsman", "Only players who own a plot in the town's county take part in its politics.");
        }
    }

    /// <summary>The mayor puts up the next level of a building, paid from the treasury.</summary>
    public Task InvestAsync(Guid player, int x, int y, string? kind, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await RequireMayorAsync(player, town, "builds for the town", cancellationToken);
            if (kind is null || !TownHallRules.Investments.TryGetValue(kind, out var building))
            {
                throw new GameException("bad_investment", "There is no such building.");
            }

            await LockTownAsync(town, cancellationToken);
            var level = await InvestmentAsync(town, kind, cancellationToken);
            if (level >= building.MaxLevel)
            {
                throw new GameException("investment_done", "This building is as big as it gets.");
            }

            await ChangeTreasuryAsync(town, -TownHallRules.InvestmentCents[level], $"{building.Name}: poziom {level + 1}", cancellationToken);
            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO world.town_investments (town_x, town_y, kind, level) VALUES (@x, @y, @kind, 1)
                ON CONFLICT (town_x, town_y, kind) DO UPDATE SET level = world.town_investments.level + 1
                """, cancellationToken);
            upsert.Parameters.AddWithValue("x", town.X);
            upsert.Parameters.AddWithValue("y", town.Y);
            upsert.Parameters.AddWithValue("kind", kind);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>A townsman signs the motion to recall this week's mayor; with two thirds of the townsfolk signed the mayor is out,
    /// and the tax and the bonus are gone with them.</summary>
    public Task<bool> RecallAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var folk = await TownsfolkAsync(town, cancellationToken);
            if (!folk.Contains(player))
            {
                throw new GameException("not_townsman", "Only players who own a plot in the town's county take part in its politics.");
            }

            await LockTownAsync(town, cancellationToken);
            if ((await PolicyAsync(town, cancellationToken)).Mayor is null)
            {
                throw new GameException("no_mayor", "The town has no mayor to recall.");
            }

            var week = CityRules.WeekOf(time.GetUtcNow());
            await using (var sign = await db.CommandAsync(
                "INSERT INTO world.town_recalls (town_x, town_y, week, signer_id) VALUES (@x, @y, @week, @player) ON CONFLICT DO NOTHING", cancellationToken))
            {
                sign.Parameters.AddWithValue("x", town.X);
                sign.Parameters.AddWithValue("y", town.Y);
                sign.Parameters.AddWithValue("week", week);
                sign.Parameters.AddWithValue("player", player);
                await sign.ExecuteNonQueryAsync(cancellationToken);
            }

            var signatures = (await RecallSignersAsync(town, week, cancellationToken)).Count(folk.Contains);
            if (!TownHallRules.Recalls(signatures, folk.Count))
            {
                return false;
            }

            await using var recall = await db.CommandAsync(
                """
                UPDATE world.town_policies SET mayor_id = NULL, tax_percent = 0, bonus = NULL, plot_tax_per_mille = 0, recalled = TRUE
                WHERE town_x = @x AND town_y = @y AND week = @week
                """, cancellationToken);
            recall.Parameters.AddWithValue("x", town.X);
            recall.Parameters.AddWithValue("y", town.Y);
            recall.Parameters.AddWithValue("week", week);
            await recall.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<List<Guid>> RecallSignersAsync(Town town, int week, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT signer_id FROM world.town_recalls WHERE town_x = @x AND town_y = @y AND week = @week", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var signers = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            signers.Add(reader.GetGuid(0));
        }

        return signers;
    }

    /// <summary>A townsman stands for next week's mayor with a programme; the first time this week they put the deposit down.</summary>
    public Task StandAsync(Guid player, int x, int y, int taxPercent, string? bonus, string? slogan, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await RequireTownsmanAsync(player, town, cancellationToken);
            if (taxPercent < 0 || taxPercent > CityRules.MaxTaxPercent)
            {
                throw new GameException("bad_tax", $"The tax is 0-{CityRules.MaxTaxPercent}%.");
            }

            if (bonus is not null && !CityRules.Bonuses.ContainsKey(bonus))
            {
                throw new GameException("bad_bonus", "There is no such bonus.");
            }

            slogan = (slogan ?? "").Trim();
            if (slogan.Length > TownHallRules.MaxSloganLength)
            {
                throw new GameException("bad_slogan", $"A slogan is at most {TownHallRules.MaxSloganLength} characters.");
            }

            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO world.town_candidates (town_x, town_y, week, player_id, tax_percent, bonus, slogan, deposit_cents, settled)
                VALUES (@x, @y, @week, @player, @tax, @bonus, @slogan, @deposit, FALSE)
                ON CONFLICT (town_x, town_y, week, player_id) DO UPDATE SET tax_percent = @tax, bonus = @bonus, slogan = @slogan
                RETURNING xmax = 0
                """, cancellationToken);
            upsert.Parameters.AddWithValue("x", town.X);
            upsert.Parameters.AddWithValue("y", town.Y);
            upsert.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
            upsert.Parameters.AddWithValue("player", player);
            upsert.Parameters.AddWithValue("tax", taxPercent);
            upsert.Parameters.AddWithValue("bonus", (object?)bonus ?? DBNull.Value);
            upsert.Parameters.AddWithValue("slogan", slogan);
            upsert.Parameters.AddWithValue("deposit", TownHallRules.DepositCents);
            if ((bool)(await upsert.ExecuteScalarAsync(cancellationToken))!)
            {
                await wallet.DebitAsync(player, TownHallRules.DepositCents, $"election deposit in {town.Name}", cancellationToken);
            }
        }, cancellationToken);

    /// <summary>A candidate withdraws from this week's election: the deposit comes back whole, and the votes cast for them
    /// this week are void, so that a candidate who withdrew cannot become mayor.</summary>
    public Task WithdrawAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            var week = CityRules.WeekOf(time.GetUtcNow());
            await using var command = await db.CommandAsync(
                """
                DELETE FROM world.town_candidates WHERE town_x = @x AND town_y = @y AND week = @week AND player_id = @player AND NOT settled
                RETURNING deposit_cents;
                DELETE FROM world.town_votes WHERE town_x = @x AND town_y = @y AND week = @week AND candidate_id = @player;
                """, cancellationToken);
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("week", week);
            command.Parameters.AddWithValue("player", player);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long deposit)
            {
                throw new GameException("not_standing", "You do not stand in this week's election.");
            }

            await wallet.CreditAsync(player, deposit, $"election deposit back in {town.Name}", cancellationToken);
        }, cancellationToken);

    /// <summary>Counts the votes of past weeks for those who stood: the deposit comes back with a tenth of the votes and goes into the treasury without.</summary>
    private async Task SettleCandidatesAsync(Town town, int week, CancellationToken cancellationToken)
    {
        var settled = new List<(int Week, Guid Player, long Deposit, bool Back)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT c.week, c.player_id, c.deposit_cents,
                   (SELECT count(*) FROM world.town_votes v WHERE v.town_x = c.town_x AND v.town_y = c.town_y AND v.week = c.week AND v.candidate_id = c.player_id),
                   (SELECT count(*) FROM world.town_votes v WHERE v.town_x = c.town_x AND v.town_y = c.town_y AND v.week = c.week)
            FROM world.town_candidates c WHERE c.town_x = @x AND c.town_y = @y AND c.week < @week AND NOT c.settled
            FOR UPDATE OF c
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("week", week);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long votes = reader.GetInt64(3), all = reader.GetInt64(4);
                settled.Add((reader.GetInt32(0), reader.GetGuid(1), reader.GetInt64(2), all > 0 && votes * 100 >= all * TownHallRules.DepositBackPercent));
            }
        }

        foreach (var (candidateWeek, player, deposit, back) in settled)
        {
            await using var mark = await db.CommandAsync(
                "UPDATE world.town_candidates SET settled = TRUE WHERE town_x = @x AND town_y = @y AND week = @week AND player_id = @player", cancellationToken);
            mark.Parameters.AddWithValue("x", town.X);
            mark.Parameters.AddWithValue("y", town.Y);
            mark.Parameters.AddWithValue("week", candidateWeek);
            mark.Parameters.AddWithValue("player", player);
            await mark.ExecuteNonQueryAsync(cancellationToken);
            if (deposit <= 0)
            {
                continue;
            }

            if (back)
            {
                await wallet.CreditAsync(player, deposit, $"election deposit back in {town.Name}", cancellationToken);
            }
            else
            {
                await ChangeTreasuryAsync(town, deposit, "kaucja wyborcza przepadła", cancellationToken);
            }
        }
    }

    /// <summary>The owner or the treasurer of a company, on a plot of theirs in the town, backs a candidate of this week's election with
    /// money from its cash, which the candidate gets.</summary>
    public Task SponsorAsync(Guid player, int x, int y, string? candidate, long cents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            if (cents < 1_00 || cents > TownHallRules.MaxSponsorCents)
            {
                throw new GameException("bad_amount", "Back a candidate with 1 $ to 1 000 000 $.");
            }

            var week = CityRules.WeekOf(time.GetUtcNow());
            var chosen = await names.AccountAsync(candidate ?? "", cancellationToken);
            if (chosen is not { } id || !(await CandidatesAsync(town, week, cancellationToken)).Any(c => c.PlayerId == id))
            {
                throw new GameException("not_candidate", "Back a player who stands in this week's election.");
            }

            // The company's money would only go round into its own owner's or treasurer's wallet.
            if (id == player)
            {
                throw new GameException("own_campaign", "A company you run does not back you yourself.");
            }

            // Looked up only here: the companies module needs the world's plots, which need this service.
            var company = await services.GetRequiredService<ICompanyCash>().SpendAsync(player, cents, $"kampania: {candidate} ({town.Name})", cancellationToken);
            await wallet.CreditAsync(id, cents, $"campaign money from {company}", cancellationToken);
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO world.town_sponsorships (town_x, town_y, week, candidate_id, company, cents, created_at)
                VALUES (@x, @y, @week, @candidate, @company, @cents, @at)
                """, cancellationToken);
            insert.Parameters.AddWithValue("x", town.X);
            insert.Parameters.AddWithValue("y", town.Y);
            insert.Parameters.AddWithValue("week", week);
            insert.Parameters.AddWithValue("candidate", id);
            insert.Parameters.AddWithValue("company", company);
            insert.Parameters.AddWithValue("cents", cents);
            insert.Parameters.AddWithValue("at", time.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private async Task<List<TownCandidateRow>> CandidatesAsync(Town town, int week, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT player_id, tax_percent, bonus, slogan FROM world.town_candidates WHERE town_x = @x AND town_y = @y AND week = @week", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<TownCandidateRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new TownCandidateRow
            {
                PlayerId = reader.GetGuid(0), TaxPercent = reader.GetInt32(1), Bonus = reader.IsDBNull(2) ? null : reader.GetString(2), Slogan = reader.GetString(3),
            });
        }

        return found;
    }

    private async Task<List<(Guid Candidate, string Company, long Cents)>> SponsorshipsAsync(Town town, int week, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT candidate_id, company, sum(cents)::bigint FROM world.town_sponsorships WHERE town_x = @x AND town_y = @y AND week = @week
            GROUP BY candidate_id, company ORDER BY 3 DESC
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<(Guid, string, long)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return found;
    }

    /// <summary>How many of the promises the players made as candidates they kept as mayors, in any town, in weeks that are over.</summary>
    private async Task<Dictionary<Guid, (int Kept, int Made)>> PromisesAsync(IReadOnlyCollection<Guid> players, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT c.player_id, c.tax_percent, c.bonus, p.tax_percent, p.bonus, p.recalled
            FROM world.town_candidates c
            JOIN world.town_policies p ON p.town_x = c.town_x AND p.town_y = c.town_y AND p.week = c.week + 1 AND p.mayor_id = c.player_id
            WHERE c.player_id = ANY(@players) AND c.week + 1 < @week
            """, cancellationToken);
        command.Parameters.AddWithValue("players", players.ToArray());
        command.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var promises = new Dictionary<Guid, (int Kept, int Made)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var kept = !reader.GetBoolean(5) && TownHallRules.Kept(
                reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            var (k, m) = promises.GetValueOrDefault(reader.GetGuid(0));
            promises[reader.GetGuid(0)] = (k + (kept ? 1 : 0), m + 1);
        }

        return promises;
    }

    // --- Ordinances ---------------------------------------------------------------------------------

    private async Task<List<Ordinance>> OrdinancesAsync(Town town, int week, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT o.id, o.kind, o.proposed_by, o.voting_ends,
                   (SELECT count(*) FROM world.town_ordinance_votes v WHERE v.ordinance_id = o.id AND v.yes)::int,
                   (SELECT count(*) FROM world.town_ordinance_votes v WHERE v.ordinance_id = o.id AND NOT v.yes)::int
            FROM world.town_ordinances o WHERE o.town_x = @x AND o.town_y = @y AND o.week = @week ORDER BY o.voting_ends
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var now = time.GetUtcNow();
        var found = new List<Ordinance>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var ends = reader.GetFieldValue<DateTimeOffset>(3);
            found.Add(new Ordinance(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), ends, reader.GetInt32(4), reader.GetInt32(5), ends > now));
        }

        return found;
    }

    /// <summary>Whether an ordinance of this kind passed in the town this week.</summary>
    private async Task<bool> InForceAsync(Town town, string kind, CancellationToken cancellationToken) =>
        (await OrdinancesAsync(town, CityRules.WeekOf(time.GetUtcNow()), cancellationToken)).Any(o => o.Kind == kind && o.InForce);

    /// <summary>The mayor puts an ordinance to the townsfolk's vote, which lasts a day; one at a time, each kind once a week.</summary>
    public Task ProposeAsync(Guid player, int x, int y, string? kind, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await RequireMayorAsync(player, town, "proposes ordinances", cancellationToken);
            if (kind is null || !TownHallRules.Ordinances.ContainsKey(kind))
            {
                throw new GameException("bad_ordinance", "There is no such ordinance.");
            }

            await LockTownAsync(town, cancellationToken);
            var now = time.GetUtcNow();
            var week = CityRules.WeekOf(now);
            var ordinances = await OrdinancesAsync(town, week, cancellationToken);
            if (ordinances.Any(o => o.Open))
            {
                throw new GameException("vote_going_on", "The townsfolk are voting on an ordinance already.");
            }

            if (ordinances.Any(o => o.Kind == kind))
            {
                throw new GameException("ordinance_done", "This ordinance was put to the vote this week already.");
            }

            var id = Guid.NewGuid();
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO world.town_ordinances (id, town_x, town_y, week, kind, proposed_by, voting_ends) VALUES (@id, @x, @y, @week, @kind, @player, @ends);
                INSERT INTO world.town_ordinance_votes (ordinance_id, voter_id, yes) VALUES (@id, @player, TRUE)
                """, cancellationToken);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("x", town.X);
            insert.Parameters.AddWithValue("y", town.Y);
            insert.Parameters.AddWithValue("week", week);
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("player", player);
            insert.Parameters.AddWithValue("ends", now + TownHallRules.OrdinanceVote);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>A townsman votes for or against the ordinance being voted on (a new vote replaces the old).</summary>
    public Task VoteOrdinanceAsync(Guid player, int x, int y, Guid id, bool yes, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await RequireTownsmanAsync(player, town, cancellationToken);
            var ordinance = (await OrdinancesAsync(town, CityRules.WeekOf(time.GetUtcNow()), cancellationToken)).FirstOrDefault(o => o.Id == id);
            if (ordinance is not { Open: true })
            {
                throw new GameException("no_vote", "There is no such vote going on in this town.");
            }

            await using var vote = await db.CommandAsync(
                """
                INSERT INTO world.town_ordinance_votes (ordinance_id, voter_id, yes) VALUES (@id, @player, @yes)
                ON CONFLICT (ordinance_id, voter_id) DO UPDATE SET yes = excluded.yes
                """, cancellationToken);
            vote.Parameters.AddWithValue("id", id);
            vote.Parameters.AddWithValue("player", player);
            vote.Parameters.AddWithValue("yes", yes);
            await vote.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>What the town window's hall shows of the treasury, the buildings, the recall, the candidates and the ordinances.</summary>
    private async Task<object> HallPoliticsAsync(Guid player, Town town, TownPolicy policy, IReadOnlyList<Guid> folk, Dictionary<Guid, int> votes,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var week = CityRules.WeekOf(now);
        var investments = await InvestmentsAsync(town, cancellationToken);
        var signers = await RecallSignersAsync(town, week, cancellationToken);
        var candidates = await CandidatesAsync(town, week, cancellationToken);
        var sponsors = await SponsorshipsAsync(town, week, cancellationToken);
        var ordinances = await OrdinancesAsync(town, week, cancellationToken);
        var ledger = new List<(long Cents, string Reason, DateTimeOffset At)>();
        await using (var command = await db.CommandAsync(
            "SELECT amount_cents, reason, created_at FROM world.town_ledger WHERE town_x = @x AND town_y = @y ORDER BY id DESC LIMIT 10", cancellationToken))
        {
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ledger.Add((reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        var myVotes = new Dictionary<Guid, bool>();
        await using (var command = await db.CommandAsync(
            "SELECT ordinance_id, yes FROM world.town_ordinance_votes WHERE voter_id = @player AND ordinance_id = ANY(@ids)", cancellationToken))
        {
            command.Parameters.AddWithValue("player", player);
            command.Parameters.AddWithValue("ids", ordinances.Select(o => o.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                myVotes[reader.GetGuid(0)] = reader.GetBoolean(1);
            }
        }

        var people = folk.Concat(candidates.Select(c => c.PlayerId)).Concat(policy.Mayor is { } m ? [m] : []).Distinct().ToList();
        var named = await names.NamesAsync(people, cancellationToken);
        var parties = await nation.PartiesAsync(people, cancellationToken);
        var promises = await PromisesAsync(people, cancellationToken);
        string Name(Guid id) => named.GetValueOrDefault(id, "?");
        var program = candidates.ToDictionary(c => c.PlayerId);
        return new
        {
            treasuryCents = await TreasuryAsync(town, cancellationToken),
            mayorSharePercent = TownHallRules.MayorSharePercent,
            ledger = ledger.Select(l => new { cents = l.Cents, reason = l.Reason, at = l.At.ToUnixTimeMilliseconds() }),
            mayorParty = policy.Mayor is { } mayor ? parties.GetValueOrDefault(mayor) : null,
            recalled = policy.Recalled,
            investments = TownHallRules.Investments.Select(i => new
            {
                id = i.Key,
                name = i.Value.Name,
                effect = i.Value.Effect,
                level = investments.GetValueOrDefault(i.Key),
                maxLevel = i.Value.MaxLevel,
                nextCents = investments.GetValueOrDefault(i.Key) < i.Value.MaxLevel ? TownHallRules.InvestmentCents[investments.GetValueOrDefault(i.Key)] : (long?)null,
            }),
            recall = new
            {
                signatures = signers.Count(folk.Contains),
                needed = Math.Max(2, (folk.Count * 2 + 2) / 3),
                signed = signers.Contains(player),
            },
            depositCents = TownHallRules.DepositCents,
            depositBackPercent = TownHallRules.DepositBackPercent,
            standing = program.ContainsKey(player),
            // Everyone who may be voted for: those who stand show their programme, the money companies gave them and the promises they kept.
            people = people.Where(folk.Contains).Select(p => new
            {
                name = Name(p),
                me = p == player,
                votes = votes.GetValueOrDefault(p),
                party = parties.GetValueOrDefault(p),
                stands = program.ContainsKey(p),
                taxPercent = program.TryGetValue(p, out var c) ? c.TaxPercent : (int?)null,
                bonus = program.TryGetValue(p, out c) ? c.Bonus : null,
                slogan = program.TryGetValue(p, out c) ? c.Slogan : null,
                promisesKept = promises.GetValueOrDefault(p).Kept,
                promisesMade = promises.GetValueOrDefault(p).Made,
                sponsors = sponsors.Where(s => s.Candidate == p).Select(s => new { company = s.Company, cents = s.Cents }),
            }).OrderByDescending(p => p.stands).ThenByDescending(p => p.votes).ThenBy(p => p.name),
            ordinances = ordinances.Select(o => new
            {
                id = o.Id,
                kind = o.Kind,
                name = TownHallRules.Ordinances[o.Kind],
                yes = o.Yes,
                no = o.No,
                open = o.Open,
                inForce = o.InForce,
                votingEnds = o.VotingEnds.ToUnixTimeMilliseconds(),
                myVote = myVotes.TryGetValue(o.Id, out var yes) ? yes : (bool?)null,
            }),
            ordinanceKinds = TownHallRules.Ordinances.Select(o => new { id = o.Key, name = o.Value }),
            ordinanceHours = TownHallRules.OrdinanceVote.TotalHours,
        };
    }
}

public sealed record InvestRequest(int X, int Y, string? Kind);

public sealed record StandRequest(int X, int Y, int TaxPercent, string? Bonus, string? Slogan);

public sealed record SponsorRequest(int X, int Y, string? Candidate, long Cents);

public sealed record OrdinanceRequest(int X, int Y, string? Kind);

public sealed record OrdinanceVoteRequest(int X, int Y, Guid Id, bool Yes);

public sealed class CityInvestHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.invest";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<InvestRequest>(payload);
        await cities.InvestAsync(player, r.X, r.Y, r.Kind, cancellationToken);
        return null;
    }
}

public sealed class CityRecallHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.recall";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<CityRequest>(payload);
        return new { recalled = await cities.RecallAsync(player, r.X, r.Y, cancellationToken) };
    }
}

public sealed class CityStandHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.stand";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<StandRequest>(payload);
        await cities.StandAsync(player, r.X, r.Y, r.TaxPercent, string.IsNullOrEmpty(r.Bonus) ? null : r.Bonus, r.Slogan, cancellationToken);
        return null;
    }
}

public sealed class CityWithdrawHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.withdraw";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<CityRequest>(payload);
        await cities.WithdrawAsync(player, r.X, r.Y, cancellationToken);
        return null;
    }
}

public sealed class CitySponsorHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.sponsor";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<SponsorRequest>(payload);
        await cities.SponsorAsync(player, r.X, r.Y, r.Candidate, r.Cents, cancellationToken);
        return null;
    }
}

public sealed class CityProposeHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.propose";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<OrdinanceRequest>(payload);
        await cities.ProposeAsync(player, r.X, r.Y, r.Kind, cancellationToken);
        return null;
    }
}

public sealed class CityOrdinanceVoteHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.ordinance";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<OrdinanceVoteRequest>(payload);
        await cities.VoteOrdinanceAsync(player, r.X, r.Y, r.Id, r.Yes, cancellationToken);
        return null;
    }
}
