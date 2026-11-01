using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// The bot and the law: a civic bot joins its country's police and, as a detective, investigates anyone it has come across,
/// players and bots alike, within the same daily limits as players and never a player the game protects (below level 5);
/// as a constable it sentences open cases, and as the president it appoints a chief, who takes on applicants. A bot that
/// dares sells what only the fence buys.
/// </summary>
public sealed partial class Bot
{
    private DateTimeOffset _lawAt;

    private void LawCandidates(BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (_lawAt <= world.Now && _plots.Count > 0 && (Traits.Civic >= BotRules.PoliceFrom || Traits.Risk >= BotRules.FenceFrom))
        {
            add(4, "law", ct => LawAsync(world, ct));
        }
    }

    private async Task<bool> LawAsync(BotWorld world, CancellationToken cancellationToken)
    {
        _lawAt = world.Now + BotRules.LawEvery;
        var acted = Traits.Risk >= BotRules.FenceFrom && await FenceAsync(cancellationToken);
        if (Traits.Civic < BotRules.PoliceFrom)
        {
            return acted;
        }

        var reply = await SendAsync("law.get", null, cancellationToken);
        if (!reply.Is("law"))
        {
            return acted;
        }

        var law = reply.Payload;
        if (law.Get("police") is not { ValueKind: JsonValueKind.Object } police)
        {
            if (law.True("mayAppointChief"))
            {
                // A president with no police of its own heads it until it hires others.
                return (await SendAsync("law.appointChief", new { name = Row.CharacterName }, cancellationToken)).Is("law") || acted;
            }

            if (law.Text("country") is not null && law.True("hasGovernment") && law.Get("applied") is not { ValueKind: JsonValueKind.String })
            {
                return (await SendAsync("law.apply", null, cancellationToken)).Is("law") || acted;
            }

            return acted;
        }

        foreach (var crossing in police.Each("crossings"))
        {
            _commons.Seen(crossing.Text("owner"));
        }

        foreach (var report in police.Each("reports"))
        {
            _commons.Seen(report.Text("suspect"));
        }

        var rank = (int)police.Long("rank");
        if (rank == BotRules.ChiefRank)
        {
            foreach (var applicant in police.Each("applications").Select(a => a.Text("name")).OfType<string>().ToList())
            {
                acted |= (await SendAsync("law.hire", new { name = applicant, yes = true }, cancellationToken)).Is("law");
            }

            // Its officers become detectives: they are the ones who investigate.
            foreach (var officer in police.Each("officers").Where(o => !o.True("me") && o.Long("rank") < BotRules.DetectiveRank))
            {
                acted |= (await SendAsync("law.setRank", new { name = officer.Text("name"), rank = BotRules.DetectiveRank }, cancellationToken)).Is("law");
            }
        }

        if (police.Long("investigationsLeft") > 0)
        {
            var officers = police.Each("officers").Select(o => o.Text("name")).ToHashSet();
            var suspects = _commons.People().Where(n => n != Row.CharacterName && !officers.Contains(n) && !_commons.IsProtected(n, world.Now)).ToList();
            // Someone the law protects (below level 5) it cannot investigate; it tries a few others then.
            foreach (var name in suspects.OrderBy(_ => world.Random.Next()).Take(BotRules.SuspectsPerLook))
            {
                var investigated = await SendAsync("law.investigate", new { name }, cancellationToken);
                if (!investigated.Is("law.investigated"))
                {
                    if (investigated.Error == "protected_player")
                    {
                        // Below level 5: no bot tries again for a day.
                        _commons.Protect(name, world.Now + TimeSpan.FromDays(1));
                    }

                    continue;
                }

                acted = true;
                if (investigated.Payload.Each("cases").Any())
                {
                    _logger.LogInformation("Bot {Bot} found something on {Suspect}", Row.CharacterName, name);
                }

                break;
            }
        }

        if (rank >= BotRules.ConstableRank)
        {
            // An open case it did not open itself gets a fine as big as what was done, within the law's bounds.
            foreach (var open in police.Each("cases").Where(c => c.Text("status") == "open" && c.Text("suspect") != Row.CharacterName
                         && c.Text("openedBy") != Row.CharacterName).Take(BotRules.SentencesPerLook))
            {
                var fineCents = Math.Min(Math.Max(open.Long("valueCents"), BotRules.MinFine), open.Long("maxFineCents"));
                acted |= (await SendAsync("law.sentence", new
                {
                    id = open.Long("id"), fineCents, tradeBanHours = 0, travelBanHours = 0, wantedHours = 0, seize = open.True("seizable"),
                }, cancellationToken)).Is("law");
            }
        }

        return acted;
    }

    /// <summary>Sells the fence what it buys and the bot has, within what the bot may take from NPCs in a session.</summary>
    private async Task<bool> FenceAsync(CancellationToken cancellationToken)
    {
        var fence = await SendAsync("fence.get", null, cancellationToken);
        if (!fence.Is("fence"))
        {
            return false;
        }

        foreach (var item in fence.Payload.Each("items").Where(i => i.Long("have") > 0))
        {
            var price = Math.Max(item.Long("priceCents"), 1);
            var quantity = Math.Min(item.Long("have"), (BotRules.MaxTraderIncome - _traderIncome) / price);
            if (quantity <= 0)
            {
                return false;
            }

            var before = _money;
            var sold = await SendAsync("fence.sell", new { item = item.Text("id"), quantity }, cancellationToken);
            if (sold.Is("fence"))
            {
                _traderIncome += Math.Max(0, _money - before);
                await InventoryAsync(cancellationToken);
                return true;
            }
        }

        return false;
    }
}
