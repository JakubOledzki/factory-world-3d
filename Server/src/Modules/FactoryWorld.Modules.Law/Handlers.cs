using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Law;

public sealed record NameRequest(string? Name);

public sealed record RankRequest(string? Name, int Rank);

public sealed record LicensedRequest(string Item, long FeeCents);

public sealed record LawsRequest(List<string>? Banned, List<LicensedRequest>? Licensed, bool ForestProtection);

public sealed record LicenceRequest(string? Country, string? Item);

public sealed record ReportRequest(string? Name, string? Text);

public sealed record IdRequest(long Id);

public sealed record SentenceRequest(long Id, long FineCents, int TradeBanHours, int TravelBanHours, int WantedHours, bool Seize);

public sealed record DecideRequest(long Id, bool Yes);

public sealed record PlotRequest(int X, int Y);

public sealed record FenceSellRequest(string? Item, long Quantity);

public sealed record BribeRequest(string? Name, long Cents);

/// <summary>Everything the police window shows to one player: their country's laws, their own cases and bans, and for an officer
/// or a judge the work waiting for them. An officer's salary and share of fines are paid as they open it.</summary>
public sealed class LawView(
    LawData data, PoliceService police, CaseService cases, Underworld underworld, ICountries countries, ICharacterNames names)
{
    public async Task<object> BuildAsync(Guid player, CancellationToken cancellationToken)
    {
        await cases.ExpireAppealsAsync(cancellationToken);
        var paid = await police.PayAsync(player, cancellationToken);
        var country = await countries.CitizenshipAsync(player, cancellationToken);
        var officer = await data.OfficerAsync(player, cancellationToken);
        var offices = country is null ? new HashSet<string>() : await countries.OfficesAsync(player, country, cancellationToken);
        var judge = offices.Contains(CountryOffices.Justice) || offices.Contains(CountryOffices.President);
        var ids = new HashSet<Guid>();

        var rules = country is null ? [] : await data.RulesAsync(country, cancellationToken);
        var myCases = await cases.CasesOfAsync(player, cancellationToken);
        var licences = await data.QueryAsync("SELECT country, item, until FROM law.licences WHERE player_id = @p AND until > @now ORDER BY until",
            r => new { country = r.GetString(0), item = r.GetString(1), name = Items.Names.GetValueOrDefault(r.GetString(1)), untilMs = r.GetFieldValue<DateTimeOffset>(2).ToUnixTimeMilliseconds() },
            cancellationToken, ("p", player), ("now", data.Now));
        var applied = await data.ScalarAsync("SELECT country FROM law.applications WHERE player_id = @p", cancellationToken, ("p", player)) as string;

        object? work = null;
        if (officer is not null)
        {
            var officers = await data.OfficersAsync(officer.Country, cancellationToken);
            var applications = officer.Rank == Ranks.Chief
                ? await data.QueryAsync("SELECT player_id, applied_at FROM law.applications WHERE country = @c ORDER BY applied_at",
                    r => (Id: r.GetGuid(0), At: r.GetFieldValue<DateTimeOffset>(1)), cancellationToken, ("c", officer.Country))
                : [];
            var crossings = await cases.CrossingsAsync(officer.Country, cancellationToken);
            var reports = await cases.ReportsAsync(officer.Country, cancellationToken);
            var countryCases = await cases.CasesInAsync(officer.Country, cancellationToken);
            var warrants = officer.Rank >= Ranks.Detective ? await cases.WarrantsAsync(officer.Country, cancellationToken) : [];
            var wanted = await data.QueryAsync(
                "SELECT DISTINCT ON (player_id) player_id, until FROM law.sanctions WHERE country = @c AND kind = 'wanted' AND until > @now ORDER BY player_id, until DESC",
                r => (Id: r.GetGuid(0), Until: r.GetFieldValue<DateTimeOffset>(1)), cancellationToken, ("c", officer.Country), ("now", data.Now));
            ids.UnionWith(officers.Select(o => o.PlayerId).Concat(applications.Select(a => a.Id)).Concat(crossings.Select(c => c.Owner))
                .Concat(reports.SelectMany(r => new[] { r.Reporter, r.Suspect })).Concat(countryCases.SelectMany(CaseIds))
                .Concat(warrants.Select(w => w.RequestedBy)).Concat(wanted.Select(w => w.Id)));
            var named = await NamesAsync(ids, cancellationToken);
            work = new
            {
                country = officer.Country,
                rank = officer.Rank,
                rankName = Ranks.Names[officer.Rank],
                salaryCents = Ranks.SalaryCents[officer.Rank],
                inspectionsLeft = Math.Max(0, Ranks.Inspections[officer.Rank] - await data.ActionsTodayAsync(player, "inspect", cancellationToken)),
                investigationsLeft = officer.Rank >= Ranks.Detective
                    ? Math.Max(0, LawRules.Investigations - await data.ActionsTodayAsync(player, "investigate", cancellationToken)) : 0,
                officers = officers.Select(o => new { name = named(o.PlayerId), rank = o.Rank, rankName = Ranks.Names[o.Rank], me = o.PlayerId == player }),
                applications = applications.Select(a => new { name = named(a.Id), atMs = a.At.ToUnixTimeMilliseconds() }),
                crossings = crossings.Select(c => new
                {
                    id = c.Id,
                    owner = named(c.Owner),
                    from = c.FromCountry,
                    to = c.ToCountry,
                    atMs = c.At.ToUnixTimeMilliseconds(),
                    declared = c.Declared,
                    pieces = c.Pieces,
                    dutyCents = c.DutyCents,
                    goods = c.Goods is null ? null : Goods(c.Goods),
                    inspected = c.Inspected,
                    mine = c.Owner == player,
                }),
                reports = reports.Select(r => new { id = r.Id, reporter = named(r.Reporter), suspect = named(r.Suspect), text = r.Text, atMs = r.At.ToUnixTimeMilliseconds() }),
                cases = countryCases.Select(c => Case(c, named)),
                warrants = warrants.Select(w => new
                {
                    id = w.Id, x = w.X, y = w.Y, requestedBy = named(w.RequestedBy), requestedAtMs = w.RequestedAt.ToUnixTimeMilliseconds(),
                    granted = w.GrantedAt is not null,
                }),
                wanted = wanted.Select(w => new { name = named(w.Id), untilMs = w.Until.ToUnixTimeMilliseconds() }),
            };
        }

        object? court = null;
        if (judge && country is not null)
        {
            var appeals = await cases.AppealsInAsync(country, cancellationToken);
            var warrants = (await cases.WarrantsAsync(country, cancellationToken)).Where(w => w.GrantedAt is null).ToList();
            var named = await NamesAsync(appeals.SelectMany(CaseIds).Concat(warrants.Select(w => w.RequestedBy)), cancellationToken);
            court = new
            {
                appeals = appeals.Select(c => Case(c, named)),
                warrants = warrants.Select(w => new { id = w.Id, x = w.X, y = w.Y, requestedBy = named(w.RequestedBy), requestedAtMs = w.RequestedAt.ToUnixTimeMilliseconds() }),
            };
        }

        var mine = await NamesAsync(myCases.SelectMany(CaseIds), cancellationToken);
        return new
        {
            country,
            hasGovernment = country is not null && await countries.TreasuryAsync(country, cancellationToken) is not null,
            offices,
            mayAppointChief = offices.Contains(CountryOffices.President) || offices.Contains(CountryOffices.Interior),
            laws = new
            {
                banned = rules.Where(r => r.Kind == RuleKinds.Banned).Select(r => new { item = r.Item, name = Items.Names.GetValueOrDefault(r.Item) }),
                licensed = rules.Where(r => r.Kind == RuleKinds.Licensed).Select(r => new { item = r.Item, name = Items.Names.GetValueOrDefault(r.Item), feeCents = r.FeeCents }),
                forestProtection = country is not null && await data.ForestProtectedAsync(country, cancellationToken),
            },
            licences,
            applied,
            paidCents = paid,
            sanctions = (await data.SanctionsAsync(player, cancellationToken))
                .Select(s => new { kind = s.Kind, country = s.Country, untilMs = s.Until.ToUnixTimeMilliseconds(), caseId = s.CaseId }),
            cases = myCases.Select(c => Case(c, mine)),
            compartment = await underworld.HasCompartmentAsync(player, cancellationToken),
            police = work,
            court,
            rules = new
            {
                ranks = Ranks.Names.Select((name, i) => new { rank = i, name, salaryCents = Ranks.SalaryCents[i], inspections = Ranks.Inspections[i] }),
                protectedBelowLevel = LawRules.ProtectedBelowLevel,
                maxBanHours = LawRules.MaxBanHours,
                maxWantedHours = LawRules.MaxWantedHours,
                appealHours = LawRules.AppealWindow.TotalHours,
                bonusPercent = LawRules.BonusPercent,
                compartmentCents = LawRules.CompartmentCents,
            },
            serverTime = data.Now.ToUnixTimeMilliseconds(),
        };
    }

    private static IEnumerable<Guid> CaseIds(CaseView c) =>
        new[] { c.Suspect, c.OpenedBy }.Concat(c.DecidedBy is { } d ? [d] : []).Where(id => id != Guid.Empty);

    public async Task<Func<Guid, string?>> NamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var all = ids.Where(id => id != Guid.Empty).ToHashSet();
        var found = all.Count == 0 ? new Dictionary<Guid, string>() : await names.NamesAsync(all, cancellationToken);
        return id => found.GetValueOrDefault(id);
    }

    public static object Goods(IReadOnlyDictionary<string, long> goods) =>
        goods.Select(g => new { id = g.Key, name = Items.Names.GetValueOrDefault(g.Key, g.Key), quantity = g.Value });

    public static object Case(CaseView c, Func<Guid, string?> named) => new
    {
        id = c.Id,
        country = c.Country,
        suspect = named(c.Suspect),
        kind = c.Kind,
        detail = c.Detail,
        valueCents = c.ValueCents,
        maxFineCents = LawRules.FineCap(c.ValueCents),
        // Nobody opened it: customs at the border did.
        openedBy = c.OpenedBy == Guid.Empty ? null : named(c.OpenedBy),
        openedAtMs = c.OpenedAt.ToUnixTimeMilliseconds(),
        status = c.Status,
        fineCents = c.FineCents,
        collectedCents = c.CollectedCents,
        tradeBanHours = c.TradeBanHours,
        travelBanHours = c.TravelBanHours,
        wantedHours = c.WantedHours,
        decidedBy = c.DecidedBy is { } d ? named(d) : null,
        decidedAtMs = c.DecidedAt?.ToUnixTimeMilliseconds(),
        appealUntilMs = c.Status == CaseStatus.Sentenced ? (c.DecidedAt + LawRules.AppealWindow)?.ToUnixTimeMilliseconds() : null,
        seizable = c.Seizable,
    };
}

/// <summary>Every law message runs in its own scope and answers with what it did; most answer with the whole police window again.</summary>
public abstract class LawHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = client.RequireAccount();
        var result = await RunAsync(scope.ServiceProvider, player, payload, cancellationToken);
        return result ?? new Reply("law", await scope.ServiceProvider.GetRequiredService<LawView>().BuildAsync(player, cancellationToken));
    }

    /// <summary>Does the work; returns its own reply, or null for the police window.</summary>
    protected abstract Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetLawHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.get";

    protected override Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        Task.FromResult<Reply?>(null);
}

public sealed class ApplyHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.apply";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<PoliceService>().ApplyAsync(player, cancellationToken);
        return null;
    }
}

public sealed class ResignHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.resign";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<PoliceService>().ResignAsync(player, cancellationToken);
        return null;
    }
}

public sealed class HireHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.hire";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<DecideRequestByName>(payload);
        var police = services.GetRequiredService<PoliceService>();
        await (request.Yes
            ? police.HireAsync(player, request.Name ?? "", cancellationToken)
            : police.RejectAsync(player, request.Name ?? "", cancellationToken));
        return null;
    }
}

public sealed record DecideRequestByName(string? Name, bool Yes);

public sealed class SetRankHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.setRank";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<RankRequest>(payload);
        await services.GetRequiredService<PoliceService>().SetRankAsync(player, request.Name ?? "", request.Rank, cancellationToken);
        return null;
    }
}

public sealed class FireHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.fire";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<PoliceService>().FireAsync(player, Payload.Read<NameRequest>(payload).Name ?? "", cancellationToken);
        return null;
    }
}

public sealed class AppointChiefHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.appointChief";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<PoliceService>().AppointChiefAsync(player, Payload.Read<NameRequest>(payload).Name ?? "", cancellationToken);
        return null;
    }
}

public sealed class SetLawsHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.setLaws";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LawsRequest>(payload);
        var licensed = (request.Licensed ?? []).ToList();
        if (licensed.Select(l => l.Item).Distinct().Count() != licensed.Count)
        {
            throw new GameException("bad_law_items", "License each good once.");
        }

        await services.GetRequiredService<PoliceService>().SetLawsAsync(player, request.Banned ?? [],
            licensed.ToDictionary(l => l.Item ?? "", l => l.FeeCents), request.ForestProtection, cancellationToken);
        return null;
    }
}

public sealed class BuyLicenceHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.buyLicence";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LicenceRequest>(payload);
        await services.GetRequiredService<PoliceService>().BuyLicenceAsync(player, request.Country ?? "", request.Item ?? "", cancellationToken);
        return null;
    }
}

public sealed class ReportHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.report";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<ReportRequest>(payload);
        await services.GetRequiredService<CaseService>().ReportAsync(player, request.Name ?? "", request.Text ?? "", cancellationToken);
        return new Reply("law.reported");
    }
}

public sealed class CloseReportHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.closeReport";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<CaseService>().CloseReportAsync(player, Payload.Read<IdRequest>(payload).Id, cancellationToken);
        return null;
    }
}

/// <summary>The answer to an inspection or a search: what the officer found.</summary>
public abstract class FindingHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var found = await FindAsync(services, player, Payload.Read<IdRequest>(payload).Id, cancellationToken);
        var named = await services.GetRequiredService<LawView>().NamesAsync([found.Suspect], cancellationToken);
        return new Reply("law.found", new
        {
            suspect = named(found.Suspect),
            goods = LawView.Goods(found.Goods),
            objects = LawView.Goods(found.Objects),
            forbidden = LawView.Goods(found.Forbidden),
            caseId = found.CaseId,
            @protected = found.Protected,
        });
    }

    protected abstract Task<Finding> FindAsync(IServiceProvider services, Guid player, long id, CancellationToken cancellationToken);
}

public sealed class InspectHandler(IServiceScopeFactory scopes) : FindingHandler(scopes)
{
    public override string Type => "law.inspect";

    protected override Task<Finding> FindAsync(IServiceProvider services, Guid player, long id, CancellationToken cancellationToken) =>
        services.GetRequiredService<CaseService>().InspectAsync(player, id, cancellationToken);
}

public sealed class SearchHandler(IServiceScopeFactory scopes) : FindingHandler(scopes)
{
    public override string Type => "law.search";

    protected override Task<Finding> FindAsync(IServiceProvider services, Guid player, long id, CancellationToken cancellationToken) =>
        services.GetRequiredService<CaseService>().SearchAsync(player, id, cancellationToken);
}

public sealed class InvestigateHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.investigate";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var opened = await services.GetRequiredService<CaseService>().InvestigateAsync(player, Payload.Read<NameRequest>(payload).Name ?? "", cancellationToken);
        return new Reply("law.investigated", new { cases = opened });
    }
}

public sealed class SentenceHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.sentence";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<SentenceRequest>(payload);
        await services.GetRequiredService<CaseService>().SentenceAsync(
            player, r.Id, r.FineCents, r.TradeBanHours, r.TravelBanHours, r.WantedHours, r.Seize, cancellationToken);
        return null;
    }
}

public sealed class DismissHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.dismiss";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<CaseService>().DismissAsync(player, Payload.Read<IdRequest>(payload).Id, cancellationToken);
        return null;
    }
}

public sealed class AppealHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.appeal";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<CaseService>().AppealAsync(player, Payload.Read<IdRequest>(payload).Id, cancellationToken);
        return null;
    }
}

public sealed class JudgeHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.judge";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<DecideRequest>(payload);
        await services.GetRequiredService<CaseService>().JudgeAsync(player, request.Id, request.Yes, cancellationToken);
        return null;
    }
}

public sealed class RequestWarrantHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.requestWarrant";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotRequest>(payload);
        await services.GetRequiredService<CaseService>().RequestWarrantAsync(player, request.X, request.Y, cancellationToken);
        return null;
    }
}

public sealed class DecideWarrantHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.decideWarrant";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<DecideRequest>(payload);
        await services.GetRequiredService<CaseService>().DecideWarrantAsync(player, request.Id, request.Yes, cancellationToken);
        return null;
    }
}

public sealed class BribeHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "law.bribe";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<BribeRequest>(payload);
        var balance = await services.GetRequiredService<Underworld>().BribeAsync(player, request.Name ?? "", request.Cents, cancellationToken);
        return new Reply("law.bribed", new { balanceCents = balance });
    }
}

/// <summary>The fence: what he takes and for how much, and the hidden compartment.</summary>
public sealed class FenceHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "fence.get";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        await FenceReplyAsync(services, player, cancellationToken);

    public static async Task<Reply> FenceReplyAsync(IServiceProvider services, Guid player, CancellationToken cancellationToken)
    {
        var underworld = services.GetRequiredService<Underworld>();
        var inventory = services.GetRequiredService<IInventory>();
        var offers = new List<object>();
        foreach (var offer in await underworld.OffersAsync(player, cancellationToken))
        {
            offers.Add(new
            {
                id = offer.Item,
                name = Items.Names.GetValueOrDefault(offer.Item, offer.Item),
                priceCents = offer.PriceCents,
                traderCents = services.GetRequiredService<IMarketPrices>().BuysCents(offer.Item),
                have = await inventory.CountAsync(player, offer.Item, cancellationToken),
            });
        }

        return new Reply("fence", new
        {
            items = offers,
            compartment = await underworld.HasCompartmentAsync(player, cancellationToken),
            compartmentCents = LawRules.CompartmentCents,
            level = LawRules.FenceLevel,
            balanceCents = await services.GetRequiredService<IWallet>().BalanceAsync(player, cancellationToken),
        });
    }
}

public sealed class FenceSellHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "fence.sell";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FenceSellRequest>(payload);
        await services.GetRequiredService<Underworld>().SellAsync(player, request.Item ?? "", request.Quantity, cancellationToken);
        return await FenceHandler.FenceReplyAsync(services, player, cancellationToken);
    }
}

public sealed class FenceCompartmentHandler(IServiceScopeFactory scopes) : LawHandler(scopes)
{
    public override string Type => "fence.compartment";

    protected override async Task<Reply?> RunAsync(IServiceProvider services, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<Underworld>().BuyCompartmentAsync(player, cancellationToken);
        return await FenceHandler.FenceReplyAsync(services, player, cancellationToken);
    }
}

public static class LawHandlers
{
    public static readonly IReadOnlyList<Type> All =
    [
        typeof(GetLawHandler), typeof(ApplyHandler), typeof(ResignHandler), typeof(HireHandler), typeof(SetRankHandler), typeof(FireHandler),
        typeof(AppointChiefHandler), typeof(SetLawsHandler), typeof(BuyLicenceHandler), typeof(ReportHandler), typeof(CloseReportHandler),
        typeof(InspectHandler), typeof(SearchHandler), typeof(InvestigateHandler), typeof(SentenceHandler), typeof(DismissHandler),
        typeof(AppealHandler), typeof(JudgeHandler), typeof(RequestWarrantHandler), typeof(DecideWarrantHandler), typeof(BribeHandler),
        typeof(FenceHandler), typeof(FenceSellHandler), typeof(FenceCompartmentHandler),
    ];
}
