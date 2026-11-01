using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Politics;

public sealed partial class NationService
{
    /// <summary>Everything the country window shows: the government and its laws, the treasury, the election, relations.</summary>
    public async Task<object> ViewAsync(Guid player, string? code, CancellationToken cancellationToken)
    {
        var country = RequireCountry(code);
        var now = time.GetUtcNow();
        var term = Term;
        var government = await GovernmentAsync(country.Code, cancellationToken);
        var ministers = await MinistersAsync(country.Code, term, cancellationToken);
        var citizens = await CitizensAsync(country.Code, cancellationToken);
        var candidates = await CandidatesAsync(country.Code, term, cancellationToken);
        var (home, since) = await CitizenAsync(player, cancellationToken);
        var myPlots = (await map.PlotsAsync(player, cancellationToken)).GetValueOrDefault(country.Code);
        var signers = await RecallSignersAsync(country.Code, term, cancellationToken);
        var investments = await InvestmentsAsync(country.Code, cancellationToken);

        var votes = new Dictionary<Guid, int>();
        Guid? myVote = null;
        await using (var command = await db.CommandAsync(
            "SELECT voter_id, candidate_id FROM politics.votes WHERE country = @country AND term = @term", cancellationToken))
        {
            command.Parameters.AddWithValue("country", country.Code);
            command.Parameters.AddWithValue("term", term);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                votes[reader.GetGuid(1)] = votes.GetValueOrDefault(reader.GetGuid(1)) + 1;
                if (reader.GetGuid(0) == player)
                {
                    myVote = reader.GetGuid(1);
                }
            }
        }

        var sponsors = new List<(Guid Candidate, string Company, long Cents)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT candidate_id, company, sum(cents)::bigint FROM politics.sponsorships WHERE country = @country AND term = @term
            GROUP BY candidate_id, company ORDER BY 3 DESC
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("country", country.Code);
            command.Parameters.AddWithValue("term", term);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sponsors.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        var ledger = new List<(long Cents, string Reason, DateTimeOffset At)>();
        await using (var command = await db.CommandAsync(
            "SELECT amount_cents, reason, created_at FROM politics.treasury_ledger WHERE country = @country ORDER BY id DESC LIMIT 10", cancellationToken))
        {
            command.Parameters.AddWithValue("country", country.Code);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ledger.Add((reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        var people = candidates.Select(c => c.PlayerId).Concat(ministers.Values).Concat(government.President is { } p ? [p] : []).Distinct().ToList();
        var named = await names.NamesAsync(people, cancellationToken);
        var parties = await PartiesAsync(people, cancellationToken);
        string Name(Guid id) => named.GetValueOrDefault(id, "?");
        var offices = await OfficesAsync(player, country.Code, cancellationToken);
        var voter = home == country.Code && myPlots > 0;
        return new
        {
            code = country.Code,
            name = country.Name,
            plots = country.Plots,
            citizens = citizens.Count,
            term,
            termEndsAt = NationRules.TermStart(term + 1).ToUnixTimeMilliseconds(),
            me = new
            {
                citizenship = home,
                citizenshipName = map.Find(home)?.Name,
                plotsHere = myPlots,
                // When the player may change citizenship again (Unix milliseconds), or null if they may now.
                changeAt = home is not null && now - since < NationRules.CitizenshipChange ? (since + NationRules.CitizenshipChange).ToUnixTimeMilliseconds() : (long?)null,
                voter,
                offices,
                party = (await PartyOfAsync(player, cancellationToken))?.Name,
            },
            government = new
            {
                president = government.President is { } president ? Name(president) : null,
                presidentParty = government.President is { } pp ? parties.GetValueOrDefault(pp) : null,
                recalled = government.Recalled,
                ministers = CountryOffices.Ministries.Select(o => new
                {
                    office = o,
                    title = NationRules.OfficeNames[o],
                    name = ministers.TryGetValue(o, out var m) ? Name(m) : null,
                }),
                laws = new
                {
                    plotTaxPerMille = government.PlotTaxPerMille,
                    minPlotTaxPerMille = NationRules.MinPlotTaxPerMille,
                    maxPlotTaxPerMille = NationRules.MaxPlotTaxPerMille,
                    treasurySharePercent = NationRules.TreasurySharePercent,
                    foreignerFeePercent = government.ForeignerFeePercent,
                    maxForeignerFeePercent = NationRules.MaxForeignerFeePercent,
                    tariffPercent = government.TariffPercent,
                    maxTariffPercent = NationRules.MaxTariffPercent,
                    itemTariffs = (await ItemTariffsAsync(country.Code, cancellationToken))
                        .Select(t => new { item = t.Key, name = Items.Names.GetValueOrDefault(t.Key, t.Key), percent = t.Value }),
                    subsidy = government.Subsidy,
                    subsidyPercent = NationRules.SubsidyPercent,
                    subsidies = NationRules.Subsidies.Select(s => new { id = s.Key, name = s.Value.Name }),
                },
            },
            treasury = new
            {
                cents = await TreasuryCentsAsync(country.Code, cancellationToken),
                presidentSharePercent = NationRules.PresidentSharePercent,
                ledger = ledger.Select(l => new { cents = l.Cents, reason = l.Reason, at = l.At.ToUnixTimeMilliseconds() }),
            },
            investments = NationRules.Investments.Select(i => new
            {
                id = i.Key,
                name = i.Value.Name,
                effect = i.Value.Effect,
                office = i.Value.Office,
                level = investments.GetValueOrDefault(i.Key),
                maxLevel = i.Value.MaxLevel,
                nextCents = investments.GetValueOrDefault(i.Key) < i.Value.MaxLevel ? NationRules.InvestmentCents[investments.GetValueOrDefault(i.Key)] : (long?)null,
            }),
            election = new
            {
                depositCents = NationRules.DepositCents,
                depositBackPercent = NationRules.DepositBackPercent,
                standing = candidates.Any(c => c.PlayerId == player),
                myVote = myVote is { } v ? Name(v) : null,
                candidates = candidates.Select(c => new
                {
                    name = Name(c.PlayerId),
                    me = c.PlayerId == player,
                    party = parties.GetValueOrDefault(c.PlayerId),
                    slogan = c.Slogan,
                    plotTaxPerMille = c.PlotTaxPerMille,
                    foreignerFeePercent = c.ForeignerFeePercent,
                    votes = votes.GetValueOrDefault(c.PlayerId),
                    sponsors = sponsors.Where(s => s.Candidate == c.PlayerId).Select(s => new { company = s.Company, cents = s.Cents }),
                }).OrderByDescending(c => c.votes).ThenBy(c => c.name),
            },
            recall = new
            {
                signatures = signers.Count(citizens.Contains),
                needed = Math.Max(2, (citizens.Count * 2 + 2) / 3),
                signed = signers.Contains(player),
            },
            relations = await RelationsAsync(country.Code, cancellationToken),
        };
    }

    /// <summary>Countries where anything goes on, ranked by the plots bought in them, with their citizens, treasuries and presidents.</summary>
    public async Task<object> RankingAsync(Guid player, CancellationToken cancellationToken)
    {
        var bought = await map.BoughtPlotsAsync(cancellationToken);
        var citizens = new Dictionary<string, long>();
        await using (var command = await db.CommandAsync("SELECT country, count(*) FROM politics.citizens GROUP BY country", cancellationToken))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                citizens[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        var presidents = new Dictionary<string, Guid>();
        await using (var command = await db.CommandAsync(
            "SELECT country, president_id FROM politics.governments WHERE term = @term AND president_id IS NOT NULL", cancellationToken))
        {
            command.Parameters.AddWithValue("term", Term);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                presidents[reader.GetString(0)] = reader.GetGuid(1);
            }
        }

        var named = await names.NamesAsync(presidents.Values.Distinct().ToList(), cancellationToken);
        var rows = new List<(CountryInfo Country, int Bought, long Citizens, long Treasury, string? President)>();
        foreach (var country in map.All.Where(c => bought.ContainsKey(c.Code) || citizens.ContainsKey(c.Code)))
        {
            rows.Add((country, bought.GetValueOrDefault(country.Code), citizens.GetValueOrDefault(country.Code),
                await TreasuryCentsAsync(country.Code, cancellationToken),
                presidents.TryGetValue(country.Code, out var president) ? named.GetValueOrDefault(president, "?") : null));
        }

        var home = await CitizenshipAsync(player, cancellationToken);
        return new
        {
            citizenship = home,
            citizenshipName = map.Find(home)?.Name,
            countries = rows.OrderByDescending(r => r.Bought).ThenByDescending(r => r.Citizens).ThenBy(r => r.Country.Name).Take(100).Select(r => new
            {
                code = r.Country.Code,
                name = r.Country.Name,
                boughtPlots = r.Bought,
                citizens = r.Citizens,
                treasuryCents = r.Treasury,
                president = r.President,
                mine = r.Country.Code == home,
            }),
        };
    }
}

public sealed record CountryRequest(string? Country);

public sealed record CitizenshipRequest(string? Country);

public sealed record CountryStandRequest(string? Country, int PlotTaxPerMille, int ForeignerFeePercent, string? Slogan);

public sealed record CountryVoteRequest(string? Country, string? Candidate);

public sealed record LawRequest(string? Country, string? Law, int Value, string? Item);

public sealed record AppointRequest(string? Country, string? Office, string? Name);

public sealed record CountryInvestRequest(string? Country, string? Kind);

public sealed record CountrySponsorRequest(string? Country, string? Candidate, long Cents);

public sealed record RelationRequest(string? Country, string? Other, bool On);

public sealed record PartyRequest(string? Name);

/// <summary>Every message of the module, by type: what it does with the payload and what it replies.</summary>
public static class PoliticsHandlers
{
    public delegate Task<Reply> Run(NationService nation, Guid player, JsonElement? payload, CancellationToken cancellationToken);

    /// <summary>Does something to a country and replies with the country window's state, and what came of it.</summary>
    private static (string, Run) Country<T>(string type, Func<NationService, Guid, T, CancellationToken, Task<object?>> act)
        where T : class =>
        (type, async (nation, player, payload, cancellationToken) =>
        {
            var request = Payload.Read<T>(payload);
            var result = await act(nation, player, request, cancellationToken);
            var country = request switch
            {
                CountryRequest r => r.Country,
                CitizenshipRequest r => r.Country,
                CountryStandRequest r => r.Country,
                CountryVoteRequest r => r.Country,
                LawRequest r => r.Country,
                AppointRequest r => r.Country,
                CountryInvestRequest r => r.Country,
                CountrySponsorRequest r => r.Country,
                RelationRequest r => r.Country,
                _ => null,
            };
            return new Reply("country", new { result, state = await nation.ViewAsync(player, country, cancellationToken) });
        });

    private static (string, Run) Party<T>(string type, Func<NationService, Guid, T, CancellationToken, Task> act)
        where T : class =>
        (type, async (nation, player, payload, cancellationToken) =>
        {
            await act(nation, player, Payload.Read<T>(payload), cancellationToken);
            return new Reply("party", await nation.PartiesViewAsync(player, cancellationToken));
        });

    public static readonly IReadOnlyList<(string Type, Run Run)> All =
    [
        Country<CountryRequest>("country.get", (_, _, _, _) => Task.FromResult<object?>(null)),
        ("country.list", async (nation, player, _, cancellationToken) => new Reply("countries", await nation.RankingAsync(player, cancellationToken))),
        Country<CitizenshipRequest>("country.citizenship", async (n, p, r, c) =>
        {
            await n.ChooseCitizenshipAsync(p, r.Country, c);
            return null;
        }),
        Country<CountryStandRequest>("country.stand", async (n, p, r, c) =>
        {
            await n.StandAsync(p, r.Country, r.PlotTaxPerMille, r.ForeignerFeePercent, r.Slogan, c);
            return null;
        }),
        Country<CountryRequest>("country.withdraw", async (n, p, r, c) =>
        {
            await n.WithdrawAsync(p, r.Country, c);
            return null;
        }),
        Country<CountryVoteRequest>("country.vote", async (n, p, r, c) =>
        {
            await n.VoteAsync(p, r.Country, r.Candidate, c);
            return null;
        }),
        Country<LawRequest>("country.law", async (n, p, r, c) =>
        {
            await n.SetLawAsync(p, r.Country, r.Law, r.Value, r.Item, c);
            return null;
        }),
        Country<AppointRequest>("country.appoint", async (n, p, r, c) =>
        {
            await n.AppointAsync(p, r.Country, r.Office, r.Name, c);
            return null;
        }),
        Country<CountryInvestRequest>("country.invest", async (n, p, r, c) =>
        {
            await n.InvestAsync(p, r.Country, r.Kind, c);
            return null;
        }),
        Country<CountryRequest>("country.recall", async (n, p, r, c) => new { recalled = await n.RecallAsync(p, r.Country, c) }),
        Country<CountrySponsorRequest>("country.sponsor", async (n, p, r, c) =>
        {
            await n.SponsorAsync(p, r.Country, r.Candidate, r.Cents, c);
            return null;
        }),
        Country<RelationRequest>("country.alliance", async (n, p, r, c) => new { allied = await n.AllianceAsync(p, r.Country, r.Other, r.On, c) }),
        Country<RelationRequest>("country.embargo", async (n, p, r, c) =>
        {
            await n.EmbargoAsync(p, r.Country, r.Other, r.On, c);
            return null;
        }),
        ("party.get", async (nation, player, _, cancellationToken) => new Reply("party", await nation.PartiesViewAsync(player, cancellationToken))),
        Party<PartyRequest>("party.create", (n, p, r, c) => n.CreatePartyAsync(p, r.Name, c)),
        Party<PartyRequest>("party.join", (n, p, r, c) => n.JoinPartyAsync(p, r.Name, c)),
        Party<PartyRequest>("party.leave", (n, p, _, c) => n.LeavePartyAsync(p, c)),
    ];
}

public sealed class PoliticsHandler(IServiceScopeFactory scopes, string type, PoliticsHandlers.Run run) : IMessageHandler
{
    public string Type => type;

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await run(scope.ServiceProvider.GetRequiredService<NationService>(), client.RequireAccount(), payload, cancellationToken);
    }
}
