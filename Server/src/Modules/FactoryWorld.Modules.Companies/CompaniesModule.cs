using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Companies;

public sealed record NameRequest(string? Name);

public sealed record CompanyRequest(Guid Company);

public sealed record RoleRequest(string? Name, string? Role);

public sealed record CompanyPlotRequest(int X, int Y);

public sealed record WarehouseRequest(string? Item, long Quantity);

public sealed record CentsRequest(long Cents);

public sealed record PercentRequest(int Percent);

public sealed record AssignRequest(int X, int Y, string? Name);

public sealed record ListRequest(bool Listed);

public sealed record SharesRequest(Guid Company, long Shares);

public sealed record ProjectRequest(string? Project, string? Item, long Quantity, bool FromWarehouse);

public sealed record OrderRequest(int Slot, long Quantity);

public sealed record TaskRequest(long Id, string? Item, long Quantity, long WageCents, string? Name);

public sealed record DealRequest(long Id, string? Company, string? Item, long PerDay, long PriceCents, int Days, bool Sell);

/// <summary>Every company message answers with the player's company as it is now.</summary>
public abstract class CompanyHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var view = await RunAsync(scope.ServiceProvider.GetRequiredService<CompanyService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("company", Json(view));
    }

    protected abstract Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken);

    public static object Json(CompanyView view) => new
    {
        company = view.Company is { } c ? new
        {
            id = c.Id,
            name = c.Name,
            role = c.Role,
            rights = Roles.Rights(c.Role).Order().Select(r => r.ToString().ToLowerInvariant()),
            members = c.Members.Select(m => new
            {
                name = m.Name,
                role = m.Role,
                you = m.AccountId == view.Player,
                joinedAt = m.JoinedAt.ToUnixTimeMilliseconds(),
                plots = c.Plots.Count(p => p.OwnerId == m.AccountId),
            }),
            invited = c.Invited,
            // A headquarters on one of its plots: more members and a bigger warehouse.
            hq = c.Hq,
            plots = c.Plots.Select(p => new { x = p.X, y = p.Y, biome = p.Biome, owner = p.OwnerName, mine = p.OwnerId == view.Player, manager = p.Manager }),
            money = Roles.HandlesMoney(c.Role),
            leads = Roles.Leads(c.Role),
            economy = c.Economy is { } e ? EconomyJson(e, view.Player) : null,
            warehouse = new
            {
                capacity = CompanyRules.CapacityFor(c.Hq),
                stored = c.Warehouse.Values.Sum(),
                items = c.Warehouse.Select(i => new { id = i.Key, name = Items.Names.GetValueOrDefault(i.Key, i.Key), quantity = i.Value }),
            },
            log = c.Log.Select(l => new
            {
                at = l.At.ToUnixTimeMilliseconds(),
                who = l.Who,
                action = l.Action,
                item = l.Item,
                name = l.Item is { } item ? Items.Names.GetValueOrDefault(item) : null,
                quantity = l.Quantity,
                other = l.Other,
            }),
        } : null,
        invitations = view.Invitations.Select(i => new { company = i.CompanyId, name = i.CompanyName, members = i.Members }),
        maxMembers = CompanyRules.MaxMembersFor(view.Company?.Hq == true),
        roles = Roles.All,
    };

    private static string ItemName(string item) => Items.Names.GetValueOrDefault(item, item);

    private static object EconomyJson(CompanyEconomy e, Guid player)
    {
        string NameOf(Guid id) => e.Names.GetValueOrDefault(id, "?");
        var total = e.Shares.Values.Sum();
        return new
        {
            cashCents = e.CashCents,
            incomeCents = e.IncomeCents,
            dividendPercent = e.DividendPercent,
            nextDividendAt = e.NextDividendAt.ToUnixTimeMilliseconds(),
            listed = e.Listed,
            mayList = e.MayList,
            mayBuild = e.MayBuild,
            valueCents = e.ValueCents,
            profitCents = e.ProfitCents,
            sharePriceCents = e.SharePriceCents,
            shares = new
            {
                total,
                yours = e.Shares.GetValueOrDefault(player),
                holders = e.Shares.OrderByDescending(s => s.Value).Select(s => new
                {
                    name = NameOf(s.Key),
                    shares = s.Value,
                    percent = total > 0 ? Math.Round(100.0 * s.Value / total, 1) : 0,
                    you = s.Key == player,
                }),
            },
            projects = e.Projects.Select(p => new
            {
                id = p.Project.Id,
                name = p.Project.Name,
                description = p.Project.Description,
                started = p.Started,
                buildSeconds = p.Project.Build.TotalSeconds,
                requires = (p.Project.Requires ?? []).Select(r => BigProjects.ById[r].Name),
                builtAt = p.BuiltAt?.ToUnixTimeMilliseconds(),
                parts = p.Project.Parts.Select(part => new
                {
                    id = part.Item,
                    name = ItemName(part.Item),
                    needed = part.Quantity,
                    delivered = p.Delivered.GetValueOrDefault(part.Item),
                }),
                reward = p.Project.Reward.Select(r => new { id = r.Item, name = ItemName(r.Item), quantity = r.Quantity }),
                // A wonder of the world: one on the server; takenBy names the company that built it first, when that is another one.
                wonder = p.Project.Wonder,
                takenBy = p.TakenBy,
            }),
            orders = e.Orders.Select(o => new
            {
                slot = o.Order.Slot,
                id = o.Order.Item,
                name = ItemName(o.Order.Item),
                quantity = o.Order.Quantity,
                delivered = o.Delivered,
                rewardCents = o.Order.RewardCents,
                endsAt = o.Order.EndsAt.ToUnixTimeMilliseconds(),
            }),
            tasks = e.Tasks.Select(t => new
            {
                id = t.Id,
                item = t.Item,
                name = ItemName(t.Item),
                quantity = t.Quantity,
                delivered = t.Delivered,
                wageCents = t.WageCents,
                paidCents = t.PaidCents,
                assignee = t.Assignee is { } a ? NameOf(a) : null,
                yours = t.Assignee is null || t.Assignee == player,
                by = NameOf(t.CreatedBy),
                done = t.DoneAt is not null,
            }),
            deals = e.Deals.Select(d => new
            {
                id = d.Id,
                weSupply = d.WeSupply,
                offeredByUs = d.OfferedByUs,
                other = d.Other,
                item = d.Item,
                name = ItemName(d.Item),
                perDay = d.PerDay,
                priceCents = d.PriceCents,
                days = d.Days,
                state = d.State,
                acceptedAt = d.AcceptedAt?.ToUnixTimeMilliseconds(),
                settled = d.Settled,
                missed = d.Missed,
            }),
            trophies = e.Trophies.Select(t => new { season = t.Season, place = t.Place, valueCents = t.ValueCents }),
        };
    }
}

/// <summary>A company message with its own payload; the reply is the player's company, like every other.</summary>
public sealed class CompanyAction<TRequest>(
    IServiceScopeFactory scopes, string type, Func<CompanyService, Guid, TRequest, CancellationToken, Task<CompanyView>> run) : CompanyHandler(scopes)
    where TRequest : class
{
    public override string Type => type;

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        run(companies, player, Payload.Read<TRequest>(payload), cancellationToken);
}

/// <summary>The stock exchange: listed companies and the player's shares.</summary>
public sealed class ExchangeHandler<TRequest>(
    IServiceScopeFactory scopes, string type, Func<CompanyService, Guid, TRequest, CancellationToken, Task<CompanyExchange>> run) : IMessageHandler
    where TRequest : class
{
    public string Type => type;

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var request = payload is null && new NoRequest() is TRequest none ? none : Payload.Read<TRequest>(payload);
        var exchange = await run(scope.ServiceProvider.GetRequiredService<CompanyService>(), client.RequireAccount(), request, cancellationToken);
        return new Reply("company.exchange", new
        {
            companies = exchange.Companies.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                listed = c.Listed,
                priceCents = c.PriceCents,
                shares = c.Shares,
                profitCents = c.ProfitCents,
                valueCents = c.ValueCents,
                yours = c.Yours,
                mine = c.Mine,
            }),
            buybackPercent = CompanyMoney.BuybackPercent,
        });
    }
}

public sealed record NoRequest;

/// <summary>Company rankings and this season's standing.</summary>
public sealed class RankingsHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "company.rankings";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var rankings = await scope.ServiceProvider.GetRequiredService<CompanyService>().RankingsAsync(client.RequireAccount(), cancellationToken);
        object List(IReadOnlyList<RankedCompany> list) =>
            list.Select((r, i) => new { place = i + 1, name = r.Name, value = r.Value, yours = r.Id == rankings.Yours });
        return new Reply("company.rankings", new
        {
            season = rankings.Season,
            endsAt = rankings.EndsAt.ToUnixTimeMilliseconds(),
            richest = List(rankings.Richest),
            production = List(rankings.Production),
            plots = List(rankings.Plots),
            lastSeason = List(rankings.LastSeason),
            prizesCents = Seasons.PrizesCents,
        });
    }
}

public sealed class GetCompanyHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.get";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.ViewAsync(player, cancellationToken);
}

public sealed class CreateCompanyHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.create";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.CreateAsync(player, Payload.Read<NameRequest>(payload).Name, cancellationToken);
}

public sealed class InviteHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.invite";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.InviteAsync(player, Payload.Read<NameRequest>(payload).Name, cancellationToken);
}

public sealed class UninviteHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.uninvite";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.UninviteAsync(player, Payload.Read<NameRequest>(payload).Name, cancellationToken);
}

public sealed class AcceptHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.accept";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.AcceptAsync(player, Payload.Read<CompanyRequest>(payload).Company, cancellationToken);
}

public sealed class DeclineHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.decline";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.DeclineAsync(player, Payload.Read<CompanyRequest>(payload).Company, cancellationToken);
}

public sealed class LeaveHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.leave";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.LeaveAsync(player, cancellationToken);
}

public sealed class KickHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.kick";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        companies.KickAsync(player, Payload.Read<NameRequest>(payload).Name, cancellationToken);
}

public sealed class RoleHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.role";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<RoleRequest>(payload);
        return companies.SetRoleAsync(player, request.Name, request.Role, cancellationToken);
    }
}

public sealed class AddPlotHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.addPlot";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<CompanyPlotRequest>(payload);
        return companies.AddPlotAsync(player, request.X, request.Y, cancellationToken);
    }
}

public sealed class RemovePlotHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.removePlot";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<CompanyPlotRequest>(payload);
        return companies.RemovePlotAsync(player, request.X, request.Y, cancellationToken);
    }
}

public sealed class PutHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.put";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WarehouseRequest>(payload);
        return companies.PutAsync(player, request.Item, request.Quantity, cancellationToken);
    }
}

public sealed class TakeHandler(IServiceScopeFactory scopes) : CompanyHandler(scopes)
{
    public override string Type => "company.take";

    protected override Task<CompanyView> RunAsync(CompanyService companies, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WarehouseRequest>(payload);
        return companies.TakeAsync(player, request.Item, request.Quantity, cancellationToken);
    }
}

/// <summary>Companies: players running plots together, with roles and a shared warehouse. Owns the <c>companies</c> schema.</summary>
public sealed class CompaniesModule : IGameModule
{
    public string Name => "Companies";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CompaniesDbContext>(CompaniesDbContext.Schema);
        services.AddScoped<CompanyService>();
        services.AddScoped<ICompanyCash>(provider => provider.GetRequiredService<CompanyService>());
        services.AddScoped<CompanyPlots>();
        services.AddScoped<ICompanyPlots>(provider => provider.GetRequiredService<CompanyPlots>());
        services.AddScoped<ICompanies>(provider => provider.GetRequiredService<CompanyPlots>());
        services.AddSingleton<IMessageHandler, GetCompanyHandler>();
        services.AddSingleton<IMessageHandler, CreateCompanyHandler>();
        services.AddSingleton<IMessageHandler, InviteHandler>();
        services.AddSingleton<IMessageHandler, UninviteHandler>();
        services.AddSingleton<IMessageHandler, AcceptHandler>();
        services.AddSingleton<IMessageHandler, DeclineHandler>();
        services.AddSingleton<IMessageHandler, LeaveHandler>();
        services.AddSingleton<IMessageHandler, KickHandler>();
        services.AddSingleton<IMessageHandler, RoleHandler>();
        services.AddSingleton<IMessageHandler, AddPlotHandler>();
        services.AddSingleton<IMessageHandler, RemovePlotHandler>();
        services.AddSingleton<IMessageHandler, PutHandler>();
        services.AddSingleton<IMessageHandler, TakeHandler>();
        services.AddSingleton<IMessageHandler, RankingsHandler>();
        services.AddSingleton<IMessageHandler, WondersHandler>();
        services.AddScoped<IPlayerBonuses, WonderBonuses>();

        void Action<T>(string type, Func<CompanyService, Guid, T, CancellationToken, Task<CompanyView>> run)
            where T : class =>
            services.AddSingleton<IMessageHandler>(p => new CompanyAction<T>(p.GetRequiredService<IServiceScopeFactory>(), type, run));
        void Exchange<T>(string type, Func<CompanyService, Guid, T, CancellationToken, Task<CompanyExchange>> run)
            where T : class =>
            services.AddSingleton<IMessageHandler>(p => new ExchangeHandler<T>(p.GetRequiredService<IServiceScopeFactory>(), type, run));

        Action<CentsRequest>("company.deposit", (c, player, r, ct) => c.DepositAsync(player, r.Cents, ct));
        Action<CentsRequest>("company.withdraw", (c, player, r, ct) => c.WithdrawAsync(player, r.Cents, ct));
        Action<WarehouseRequest>("company.sell", (c, player, r, ct) => c.SellAsync(player, r.Item, r.Quantity, ct));
        Action<WarehouseRequest>("company.buy", (c, player, r, ct) => c.BuyAsync(player, r.Item, r.Quantity, ct));
        Action<PercentRequest>("company.dividend", (c, player, r, ct) => c.SetDividendAsync(player, r.Percent, ct));
        Action<AssignRequest>("company.assign", (c, player, r, ct) => c.AssignAsync(player, r.X, r.Y, r.Name, ct));
        Action<ListRequest>("company.list", (c, player, r, ct) => c.ListAsync(player, r.Listed, ct));
        Action<CompanyRequest>("company.takeover", (c, player, r, ct) => c.TakeoverAsync(player, r.Company, ct));
        Action<ProjectRequest>("company.project.start", (c, player, r, ct) => c.StartProjectAsync(player, r.Project, ct));
        Action<ProjectRequest>("company.project.give", (c, player, r, ct) => c.GiveToProjectAsync(player, r.Project, r.Item, r.Quantity, r.FromWarehouse, ct));
        Action<OrderRequest>("company.order.deliver", (c, player, r, ct) => c.DeliverOrderAsync(player, r.Slot, r.Quantity, ct));
        Action<TaskRequest>("company.task.create", (c, player, r, ct) => c.CreateTaskAsync(player, r.Item, r.Quantity, r.WageCents, r.Name, ct));
        Action<TaskRequest>("company.task.deliver", (c, player, r, ct) => c.DeliverTaskAsync(player, r.Id, r.Quantity, ct));
        Action<TaskRequest>("company.task.cancel", (c, player, r, ct) => c.CancelTaskAsync(player, r.Id, ct));
        Action<DealRequest>("company.deal.offer", (c, player, r, ct) => c.OfferDealAsync(player, r.Company, r.Item, r.PerDay, r.PriceCents, r.Days, r.Sell, ct));
        Action<DealRequest>("company.deal.accept", (c, player, r, ct) => c.AcceptDealAsync(player, r.Id, ct));
        Action<DealRequest>("company.deal.cancel", (c, player, r, ct) => c.CancelDealAsync(player, r.Id, ct));
        Exchange<NoRequest>("company.exchange", (c, player, _, ct) => c.ExchangeAsync(player, ct));
        Exchange<SharesRequest>("company.shares.buy", (c, player, r, ct) => c.BuySharesAsync(player, r.Company, r.Shares, ct));
        Exchange<SharesRequest>("company.shares.sell", (c, player, r, ct) => c.SellSharesAsync(player, r.Company, r.Shares, ct));
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<CompaniesDbContext>(cancellationToken);
}
