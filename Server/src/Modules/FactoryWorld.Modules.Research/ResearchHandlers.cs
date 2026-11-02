using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Research;

public sealed record StartResearchRequest(string Technology);

public abstract class ResearchHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<ResearchService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("research", new
        {
            // Unix milliseconds: the client compares them with serverTime, not its own clock.
            technologies = TechTree.Technologies.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                description = t.Description,
                tier = TechTree.Tier(t),
                branch = TechTree.BranchOf[t.Id].Id,
                requires = t.Requires,
                cost = t.Cost.Select(c => new { id = c.Item, name = Items.Names[c.Item], quantity = c.Quantity }),
                // Tiles of the inventory the cost takes: it is all used up at once, so it must all fit in the inventory.
                costTiles = t.Cost.Sum(c => Stacks.Tiles(c.Item, c.Quantity)),
                // Money it uses up besides the items.
                costCents = t.CostCents,
                seconds = t.Time.TotalSeconds,
                xp = t.Xp,
                unlocks = t.Unlocks.Select(u => new { id = u, name = Items.Names[u] }),
                // The item whose icon it shows when it unlocks none, or null.
                icon = t.Icon,
                status = state.Status(t) switch
                {
                    ResearchStatus.Done => "done",
                    ResearchStatus.Active => "active",
                    ResearchStatus.Available => "available",
                    _ => "locked",
                },
                startsAt = state.Started.TryGetValue(t.Id, out var r) ? r.StartsAt.ToUnixTimeMilliseconds() : (long?)null,
                finishesAt = state.Started.TryGetValue(t.Id, out var f) ? f.FinishesAt.ToUnixTimeMilliseconds() : (long?)null,
                companyOnly = t.CompanyOnly,
                // Researched by the player's company, not by them.
                shared = state.Shared?.Contains(t.Id) == true,
            }),
            company = state.Company is { } c ? new
            {
                name = c.Name,
                leads = c.Leads,
                technology = c.Technology,
                startsAt = c.StartsAt?.ToUnixTimeMilliseconds(),
                finishesAt = c.FinishesAt?.ToUnixTimeMilliseconds(),
                delivered = c.Technology is { } id
                    ? TechTree.ById[id].Cost.Select(p => new { id = p.Item, name = Items.Names[p.Item], needed = (long)p.Quantity, delivered = c.Delivered.GetValueOrDefault(p.Item) })
                        // Money in cents, under the id "money".
                        .Concat(TechTree.ById[id].CostCents > 0
                            ? [new { id = TechTree.Money, name = "Pieniądze", needed = TechTree.ById[id].CostCents, delivered = c.Delivered.GetValueOrDefault(TechTree.Money) }]
                            : [])
                    : null,
            } : null,
            // What speeds research up, and by how many seconds each piece.
            boosts = Items.ResearchBoost.Select(b => new { id = b.Key, name = Items.Names[b.Key], seconds = b.Value }),
            branches = TechTree.Branches.Select(b => new { id = b.Id, name = b.Name, icon = b.Icon }),
            serverTime = state.ServerTime.ToUnixTimeMilliseconds(),
            finished = state.Finished.Where(TechTree.ById.ContainsKey).Select(id => new { id, name = TechTree.ById[id].Name }),
            xp = state.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
        });
    }

    protected abstract Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>The tree and the player's research; also hands out XP for finished research, so the client calls it when a timer runs out.</summary>
public sealed class GetResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.get";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.StateAsync(owner, cancellationToken);
}

public sealed class StartResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.start";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.StartAsync(owner, Payload.Read<StartResearchRequest>(payload).Technology ?? "", cancellationToken);
}

public sealed record BoostResearchRequest(string Item, int Quantity);

/// <summary>Speeds the research being done up with research data or computing power.</summary>
public sealed class BoostResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.boost";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<BoostResearchRequest>(payload);
        return research.BoostAsync(owner, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class CancelResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.cancel";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.CancelAsync(owner, cancellationToken);
}

/// <summary>The company picks what it researches together.</summary>
public sealed class StartCompanyResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.companyStart";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.StartCompanyAsync(owner, Payload.Read<StartResearchRequest>(payload).Technology ?? "", cancellationToken);
}

public sealed record ContributeRequest(string Item, long Quantity);

/// <summary>A member brings part of the cost of the company's research.</summary>
public sealed class ContributeResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.contribute";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<ContributeRequest>(payload);
        return research.ContributeAsync(owner, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class CancelCompanyResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.companyCancel";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.CancelCompanyAsync(owner, cancellationToken);
}
