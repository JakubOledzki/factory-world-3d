using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// The bot in its town and its country: votes, stands for mayor or president where no player stands (and withdraws once one
/// does), votes for ordinances, and as mayor keeps its programme, puts ordinances to the vote and builds from the treasury.
/// </summary>
public sealed partial class Bot
{
    /// <summary>A plot of the bot's in a town's county, once it has looked; (int.MinValue, 0) when none of its plots is in one.</summary>
    private (int X, int Y)? _townPlot;

    private string? _country;
    private bool _countryLooked;
    private DateTimeOffset _politicsAt;

    private static readonly string[] Slogans =
    [
        "Praca, porządek, postęp!", "Taśmociąg w każdym domu", "Niższe podatki, więcej fabryk", "Miasto dla tych, którzy budują",
        "Mniej gadania, więcej roboty", "Węgiel, stal i dobrobyt", "Rzetelnie jak w księdze portfela",
    ];

    private void PoliticsCandidates(BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (_politicsAt > world.Now || _plots.Count == 0 || Traits.Civic < BotRules.VotesFrom)
        {
            return;
        }

        add(5, "politics", ct => PoliticsAsync(world, ct));
    }

    /// <summary>One look at the town hall and the country's government, and whatever the bot means to do there.</summary>
    private async Task<bool> PoliticsAsync(BotWorld world, CancellationToken cancellationToken)
    {
        _politicsAt = world.Now + BotRules.PoliticsEvery;
        var acted = await TownPoliticsAsync(world, cancellationToken);
        return await CountryPoliticsAsync(world, cancellationToken) || acted;
    }

    private async Task<JsonElement?> TownAsync(CancellationToken cancellationToken)
    {
        if (_townPlot is null)
        {
            _townPlot = (int.MinValue, 0);
            foreach (var plot in _plots.OrderByDescending(p => p.Biome == Biomes.City))
            {
                var reply = await SendAsync("city.get", new { x = plot.X, y = plot.Y }, cancellationToken);
                if (reply.Is("city"))
                {
                    _townPlot = (plot.X, plot.Y);
                    return reply.Payload.Get("state");
                }
            }

            return null;
        }

        if (_townPlot.Value.X == int.MinValue)
        {
            return null;
        }

        var town = await SendAsync("city.get", new { x = _townPlot.Value.X, y = _townPlot.Value.Y }, cancellationToken);
        return town.Is("city") ? town.Payload.Get("state") : null;
    }

    private async Task<bool> TownPoliticsAsync(BotWorld world, CancellationToken cancellationToken)
    {
        if (await TownAsync(cancellationToken) is not { } state || state.Get("hall") is not { } town || town.Get("politics") is not { } politics)
        {
            return false;
        }

        var name = state.Get("town")?.Text("name");

        var (x, y) = _townPlot!.Value;
        var people = politics.Each("people").ToList();
        foreach (var person in people)
        {
            _commons.Seen(person.Text("name"));
        }

        var playerStands = people.Any(p => p.True("stands") && !_commons.IsBot(p.Text("name")));
        var standing = politics.True("standing");

        // A bot gives way to a player: it stands only where no player does, and withdraws once one does.
        if (standing && playerStands)
        {
            _logger.LogInformation("Bot {Bot} withdraws from the election in {Town}", Row.CharacterName, name);
            Goals.Drop(BotGoals.Office);
            return (await SendAsync("city.withdraw", new { x, y }, cancellationToken)).Is("city");
        }

        if (town.True("isMayor"))
        {
            return await MayorAsync(x, y, town, politics, world, cancellationToken);
        }

        var botsStanding = people.Where(p => p.True("stands") && _commons.IsBot(p.Text("name"))).ToList();
        var deposit = politics.Long("depositCents");
        if (!standing && !playerStands && botsStanding.Count == 0 && Traits.Civic >= BotRules.StandsFrom && Traits.Ambition >= BotRules.AmbitiousFrom
            && _money >= deposit + BotRules.Reserve + BotRules.StandSpare)
        {
            var taxPercent = BotRules.TaxPromise(Traits);
            var bonus = BotRules.MayorBonuses[(int)(Traits.Risk * BotRules.MayorBonuses.Length) % BotRules.MayorBonuses.Length];
            var slogan = Slogans[(int)(Traits.Loyalty * Slogans.Length) % Slogans.Length];
            var stood = await SendAsync("city.stand", new { x, y, taxPercent, bonus, slogan }, cancellationToken);
            if (stood.Is("city"))
            {
                _money -= deposit;
                Goals.Set(new BotGoal(BotGoals.Office, 0, name, world.Now));
                _logger.LogInformation("Bot {Bot} stands for mayor of {Town}", Row.CharacterName, name);
                return true;
            }
        }

        var acted = false;
        // Bots vote only between bots: where a player stands, the townsfolk who play decide.
        if (!playerStands && town.True("canVote"))
        {
            var choice = (standing ? people.Where(p => p.True("me")) : botsStanding)
                .OrderBy(p => p.LongOrNull("taxPercent") ?? 100).ThenByDescending(p => p.Long("votes")).Select(p => p.Text("name")).FirstOrDefault();
            if (choice is not null && town.Text("myVote") != choice)
            {
                acted |= (await SendAsync("city.vote", new { x, y, candidate = choice }, cancellationToken)).Is("city");
            }
        }

        // It backs the town's government: yes to the mayor's ordinances.
        foreach (var ordinance in politics.Each("ordinances").Where(o => o.True("open") && o.Get("myVote") is not { ValueKind: JsonValueKind.True or JsonValueKind.False }))
        {
            acted |= (await SendAsync("city.ordinance", new { x, y, id = ordinance.Long("id"), yes = true }, cancellationToken)).Is("city");
        }

        return acted;
    }

    /// <summary>The bot as mayor: keeps the programme it was elected on, puts an ordinance to the vote, builds from the treasury.</summary>
    private async Task<bool> MayorAsync(int x, int y, JsonElement town, JsonElement politics, BotWorld world, CancellationToken cancellationToken)
    {
        Goals.Drop(BotGoals.Office);
        var acted = false;
        var taxPercent = BotRules.TaxPromise(Traits);
        var bonus = BotRules.MayorBonuses[(int)(Traits.Risk * BotRules.MayorBonuses.Length) % BotRules.MayorBonuses.Length];
        if (town.Long("taxPercent") != taxPercent || town.Text("bonus") != bonus)
        {
            acted |= (await SendAsync("city.policy", new { x, y, taxPercent, bonus }, cancellationToken)).Is("city");
        }

        if (!politics.Each("ordinances").Any(o => o.True("open")))
        {
            var kinds = politics.Each("ordinanceKinds").Select(k => k.Text("id")).OfType<string>().ToList();
            if (kinds.Count > 0)
            {
                var kind = kinds[world.Random.Next(kinds.Count)];
                acted |= (await SendAsync("city.propose", new { x, y, kind }, cancellationToken)).Is("city");
            }
        }

        var treasury = politics.Long("treasuryCents");
        if (politics.Each("investments").Where(i => i.LongOrNull("nextCents") is { } next && next * 2 <= treasury)
                .OrderBy(i => i.Long("nextCents")).Select(i => i.Text("id")).FirstOrDefault() is { } invest)
        {
            acted |= (await SendAsync("city.invest", new { x, y, kind = invest }, cancellationToken)).Is("city");
        }

        return acted;
    }

    /// <summary>The bot's country: citizenship where its plots are, a vote for a bot standing for president where no player
    /// stands, and, rarely, standing itself.</summary>
    private async Task<bool> CountryPoliticsAsync(BotWorld world, CancellationToken cancellationToken)
    {
        if (!_countryLooked)
        {
            _countryLooked = true;
            foreach (var plot in _plots)
            {
                if (await _commons.CountryOf(plot.X, plot.Y, cancellationToken) is { } code)
                {
                    _country = code;
                    break;
                }
            }
        }

        if (_country is not { } country)
        {
            return false;
        }

        var reply = await SendAsync("country.get", new { country }, cancellationToken);
        if (!reply.Is("country") || reply.Payload.Get("state") is not { } state)
        {
            return false;
        }

        if (state.Get("me")?.Text("citizenship") != country)
        {
            if (state.Get("me")?.Text("citizenship") is null)
            {
                return (await SendAsync("country.citizenship", new { country }, cancellationToken)).Is("country");
            }

            return false;
        }

        var election = state.Get("election");
        var candidates = election?.Each("candidates").ToList() ?? [];
        var playerStands = candidates.Any(c => !_commons.IsBot(c.Text("name")));
        var standing = election?.True("standing") == true;
        if (standing && playerStands)
        {
            Goals.Drop(BotGoals.Office);
            return (await SendAsync("country.withdraw", new { country }, cancellationToken)).Is("country");
        }

        var deposit = election?.Long("depositCents") ?? long.MaxValue;
        if (!standing && candidates.Count == 0 && Traits.Civic >= BotRules.RunsForPresidentFrom && Traits.Ambition >= BotRules.AmbitiousFrom
            && _money >= deposit + BotRules.Reserve + BotRules.StandSpare)
        {
            var stood = await SendAsync("country.stand", new
            {
                country,
                plotTaxPerMille = 3 + (int)Math.Round(Traits.Greed * 4),
                foreignerFeePercent = (int)Math.Round(Traits.Greed * 10),
                slogan = Slogans[(int)(Traits.Civic * Slogans.Length) % Slogans.Length],
            }, cancellationToken);
            if (stood.Is("country"))
            {
                _money -= deposit;
                Goals.Set(new BotGoal(BotGoals.Office, 0, country, world.Now));
                _logger.LogInformation("Bot {Bot} runs for president of {Country}", Row.CharacterName, country);
                return true;
            }
        }

        if (!playerStands && candidates.Count > 0)
        {
            var choice = (standing ? candidates.Where(c => c.True("me")) : candidates)
                .OrderBy(c => c.Long("plotTaxPerMille")).ThenByDescending(c => c.Long("votes")).Select(c => c.Text("name")).First();
            if (election?.Text("myVote") != choice)
            {
                return (await SendAsync("country.vote", new { country, candidate = choice }, cancellationToken)).Is("country");
            }
        }

        return false;
    }
}
