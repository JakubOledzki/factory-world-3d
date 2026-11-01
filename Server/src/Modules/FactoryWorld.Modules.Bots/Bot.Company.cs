using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>The bot in a company: founding one, inviting other bots, joining, paying in and handing out roles.</summary>
public sealed partial class Bot
{
    /// <summary>The bot's company as <c>company.get</c> last showed it; null while it is in none.</summary>
    private JsonElement? _company;

    /// <summary>Invitations from companies a bot founded, which the bot may accept.</summary>
    private readonly List<Guid> _invitations = [];

    private DateTimeOffset _companyAt;
    private bool _deposited;

    private Guid? CompanyId => _company is { } c && Guid.TryParse(c.Text("id"), out var id) ? id : null;

    private string? CompanyRole => _company?.Text("role");

    /// <summary>Looks at its company and its invitations, and tells the other bots whether it is free to be invited.</summary>
    private async Task<bool> CompanyLookAsync(BotWorld world, CancellationToken cancellationToken)
    {
        _companyAt = world.Now + BotRules.CompanyEvery;
        var reply = await SendAsync("company.get", null, cancellationToken);
        if (!ParseCompany(reply))
        {
            return false;
        }

        // What the company may want is looked for in a fresh inventory.
        await InventoryAsync(cancellationToken);
        return true;
    }

    private bool ParseCompany(BotReply reply)
    {
        if (!reply.Is("company"))
        {
            return false;
        }

        _company = reply.Payload.Get("company") is { ValueKind: JsonValueKind.Object } company ? company.Clone() : null;
        _invitations.Clear();
        foreach (var invitation in reply.Payload.Each("invitations"))
        {
            if (Guid.TryParse(invitation.Text("company"), out var id) && _commons.FoundedByBot(id))
            {
                _invitations.Add(id);
            }
        }

        if (CompanyId is { } mine)
        {
            _commons.Joined(Row.CharacterName);
            if (CompanyRole == "owner")
            {
                _commons.Founded(mine, Row.Name);
            }

            Goals.Drop(BotGoals.Company);
        }
        else if (_invitations.Count == 0 && Traits.Loyalty >= BotRules.JoinsFrom)
        {
            _commons.Unattached(Row.CharacterName);
        }

        return true;
    }

    /// <summary>What the bot may do about companies now, as candidates of a step.</summary>
    private void CompanyCandidates(BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (_companyAt <= world.Now)
        {
            add(4, "look at company", ct => CompanyLookAsync(world, ct));
            return;
        }

        if (CompanyId is null)
        {
            if (_invitations.Count > 0 && Traits.Loyalty >= BotRules.JoinsFrom)
            {
                add(20, "join company", JoinCompanyAsync);
            }
            else if (_invitations.Count == 0 && _plots.Count > 0 && Traits.Ambition >= BotRules.FoundsFrom && _money >= BotRules.FoundCompanyFrom)
            {
                add(6, "found company", ct => FoundCompanyAsync(world, ct));
            }

            return;
        }

        var company = _company!.Value;
        if (CompanyRole == "owner")
        {
            var size = company.Each("members").Count() + company.Each("invited").Count();
            if (size < BotRules.CompanySize(Traits.Ambition) && _commons.Unattached().Count > 0)
            {
                add(8, "invite to company", ct => InviteAsync(world, ct));
            }

            if (company.Each("members").FirstOrDefault(m => m.Text("role") == "worker") is { ValueKind: JsonValueKind.Object } worker
                && !company.Each("members").Any(m => m.Text("role") == "partner"))
            {
                add(4, "give role", ct => GiveRoleAsync(worker.Text("name")!, "partner", ct));
            }
        }

        CompanyWork(company, add);

        if (!_deposited && Spare >= BotRules.DepositFrom && Traits.Greed < BotRules.DepositsBelowGreed)
        {
            add(3, "pay into company", PayInAsync);
        }
    }

    private async Task<bool> FoundCompanyAsync(BotWorld world, CancellationToken cancellationToken)
    {
        Goals.Set(new BotGoal(BotGoals.Company, 0, null, Goals[BotGoals.Company]?.Since ?? world.Now));
        foreach (var name in BotRules.CompanyNames(Row.CharacterName, Traits))
        {
            var reply = await SendAsync("company.create", new { name }, cancellationToken);
            if (reply.Is("company"))
            {
                ParseCompany(reply);
                _logger.LogInformation("Bot {Bot} founded the company {Company}", Row.CharacterName, name);
                return true;
            }

            if (reply.Error != "company_name_taken")
            {
                return false;
            }
        }

        return false;
    }

    private async Task<bool> InviteAsync(BotWorld world, CancellationToken cancellationToken)
    {
        var free = _commons.Unattached().Where(n => n != Row.CharacterName).ToList();
        if (free.Count == 0)
        {
            return false;
        }

        var name = free[world.Random.Next(free.Count)];
        var reply = await SendAsync("company.invite", new { name }, cancellationToken);
        if (!reply.Is("company"))
        {
            // Someone else's already, or gone: the bot does not try it again.
            _commons.Joined(name);
            return false;
        }

        _commons.Joined(name);
        return ParseCompany(reply);
    }

    private async Task<bool> JoinCompanyAsync(CancellationToken cancellationToken)
    {
        var company = _invitations[0];
        var reply = await SendAsync("company.accept", new { company }, cancellationToken);
        if (!reply.Is("company"))
        {
            _invitations.RemoveAt(0);
            return false;
        }

        _logger.LogInformation("Bot {Bot} joined a company", Row.CharacterName);
        return ParseCompany(reply);
    }

    private async Task<bool> GiveRoleAsync(string name, string role, CancellationToken cancellationToken) =>
        ParseCompany(await SendAsync("company.role", new { name, role }, cancellationToken));

    /// <summary>Pays a little into the company's cash, for shares, once a session: what a member who believes in it does.</summary>
    private async Task<bool> PayInAsync(CancellationToken cancellationToken)
    {
        _deposited = true;
        var cents = Math.Min(Spare / 20, BotRules.MaxDeposit);
        var reply = await SendAsync("company.deposit", new { cents }, cancellationToken);
        if (!reply.Is("company"))
        {
            return false;
        }

        _money -= cents;
        return ParseCompany(reply);
    }

    /// <summary>What the bot has of an item beyond what it keeps for its own work.</summary>
    private long SpareOf(string item) => Math.Max(0, Have(item) - KeepForWork(item));

    private static long Stored(JsonElement company, string item) =>
        company.Get("warehouse")?.Each("items").FirstOrDefault(i => i.Text("id") == item) is { ValueKind: JsonValueKind.Object } stored
            ? stored.Long("quantity")
            : 0;

    /// <summary>
    /// Joint work in its company: bringing what tasks ask for (the wage comes from the company's cash), gathering the
    /// week's orders in the warehouse together and, for those who may take from it, delivering them; the owner and the
    /// treasurer set tasks for what the founder's trade needs and take what the members brought.
    /// </summary>
    private void CompanyWork(JsonElement company, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (company.Get("economy") is not { ValueKind: JsonValueKind.Object } economy)
        {
            return;
        }

        var role = CompanyRole ?? "worker";
        var mayTake = role != "worker";
        foreach (var task in economy.Each("tasks"))
        {
            var item = task.Text("item") ?? "";
            var left = task.Long("quantity") - task.Long("delivered");
            if (!task.True("done") && task.True("yours") && left > 0 && SpareOf(item) > 0 && task.Text("by") != Row.CharacterName)
            {
                var id = task.Long("id");
                var quantity = Math.Min(Math.Min(left, SpareOf(item)), BotRules.CompanyBatch);
                add(10, "company task", ct => CompanySendAsync("company.task.deliver", new { id, quantity }, ct));
                break;
            }
        }

        foreach (var order in economy.Each("orders"))
        {
            var item = order.Text("id") ?? "";
            var slot = order.Long("slot");
            var left = order.Long("quantity") - order.Long("delivered");
            if (left <= 0)
            {
                continue;
            }

            if (SpareOf(item) > 0 && Stored(company, item) < left)
            {
                var quantity = Math.Min(Math.Min(left - Stored(company, item), SpareOf(item)), BotRules.CompanyBatch);
                add(6, "company order", ct => CompanySendAsync("company.put", new { item, quantity }, ct));
            }

            if (mayTake && Stored(company, item) > 0)
            {
                var quantity = Math.Min(left, Stored(company, item));
                add(6, "company order", ct => CompanySendAsync("company.order.deliver", new { slot, quantity }, ct));
            }
        }

        if (role is "owner" or "treasurer")
        {
            var mine = economy.Each("tasks").Count(t => !t.True("done") && t.Text("by") == Row.CharacterName);
            var cash = economy.Long("cashCents");
            var want = BotRules.Buys(Profile.Trade).Concat(_line?.Inputs ?? []).FirstOrDefault(i => Floor(i) > 0);
            if (mine == 0 && want is not null)
            {
                // The wage is what the trader pays for the goods: a member gets no more than he would selling them there.
                var quantity = Math.Min(BotRules.CompanyBatch, cash / 2 / Floor(want));
                if (quantity >= 10)
                {
                    var wageCents = quantity * Floor(want);
                    add(3, "company task create", ct => CompanySendAsync("company.task.create", new { item = want, quantity, wageCents, name = "" }, ct));
                }
            }
        }

        if (mayTake)
        {
            foreach (var item in BotRules.Buys(Profile.Trade).Concat(_line?.Inputs ?? []).Distinct())
            {
                // What members brought for its tasks and no order wants: the bot takes it to work with.
                var stored = Stored(company, item);
                if (stored > 0 && !economy.Each("orders").Any(o => o.Text("id") == item))
                {
                    var quantity = Math.Min(stored, BotRules.CompanyBatch);
                    add(5, "take from company", ct => CompanySendAsync("company.take", new { item, quantity }, ct));
                    break;
                }
            }
        }
    }

    private async Task<bool> CompanySendAsync(string type, object payload, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(type, payload, cancellationToken);
        if (!reply.Is("company"))
        {
            return false;
        }

        ParseCompany(reply);
        await InventoryAsync(cancellationToken);
        return true;
    }
}
