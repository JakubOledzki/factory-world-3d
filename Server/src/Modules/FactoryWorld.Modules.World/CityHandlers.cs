using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

public sealed record CityRequest(int X, int Y);

public sealed record DeliverRequest(int X, int Y, string Item, long Quantity, bool FromStation = false);

public sealed record TrashRequest(int X, int Y, long Quantity);

public sealed record ServiceRequest(int X, int Y, Guid Id);

public sealed record VoteRequest(int X, int Y, string Candidate);

public sealed record PolicyRequest(int X, int Y, int TaxPercent, string? Bonus, int? PlotTaxPerMille = null);

public sealed record ShopRequest(Guid Id, string? Item = null, long Quantity = 0, long PriceCents = 0);

public abstract class CityHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var cities = scope.ServiceProvider.GetRequiredService<CityService>();
        var player = client.RequireAccount();
        var done = await RunAsync(cities, player, payload, cancellationToken);
        var at = Payload.Read<CityRequest>(payload);
        return new Reply("city", new { result = done, state = await cities.ViewAsync(player, at.X, at.Y, cancellationToken) });
    }

    protected abstract Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetCityHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.get";

    protected override Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
}

public sealed class DeliverToCityHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.deliver";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<DeliverRequest>(payload);
        var (quantity, cents, subsidy, xp, level) = await cities.DeliverAsync(player, r.X, r.Y, r.Item ?? "", r.Quantity, r.FromStation, cancellationToken);
        return new { item = r.Item, quantity, cents, subsidyCents = subsidy, level, xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } };
    }
}

public sealed class CityTrashHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.trash";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<TrashRequest>(payload);
        var (quantity, cents) = await cities.TakeTrashAsync(player, r.X, r.Y, r.Quantity, cancellationToken);
        return new { quantity, cents };
    }
}

public sealed class CityServiceHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.service";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ServiceRequest>(payload);
        var (item, quantity, cents) = await cities.BuyServiceAsync(player, r.X, r.Y, r.Id, cancellationToken);
        return new { item, name = Items.Names[item], quantity, cents };
    }
}

public sealed class CityVoteHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.vote";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<VoteRequest>(payload);
        await cities.VoteAsync(player, r.X, r.Y, r.Candidate, cancellationToken);
        return null;
    }
}

public sealed class CityPolicyHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.policy";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<PolicyRequest>(payload);
        await cities.SetPolicyAsync(player, r.X, r.Y, r.TaxPercent, string.IsNullOrEmpty(r.Bonus) ? null : r.Bonus, r.PlotTaxPerMille, cancellationToken);
        return null;
    }
}

public abstract class ShopHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var cities = scope.ServiceProvider.GetRequiredService<CityService>();
        var request = Payload.Read<ShopRequest>(payload);
        return new Reply("shop", await RunAsync(cities, client.RequireAccount(), request, cancellationToken));
    }

    protected abstract Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken);
}

public sealed class GetShopHandler(IServiceScopeFactory scopes) : ShopHandler(scopes)
{
    public override string Type => "shop.get";

    protected override Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken) =>
        cities.ShopAsync(player, request.Id, cancellationToken);
}

public sealed class StockShopHandler(IServiceScopeFactory scopes) : ShopHandler(scopes)
{
    public override string Type => "shop.stock";

    protected override Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken) =>
        cities.StockShopAsync(player, request.Id, request.Item ?? "", request.Quantity, request.PriceCents, cancellationToken);
}

public sealed class TakeFromShopHandler(IServiceScopeFactory scopes) : ShopHandler(scopes)
{
    public override string Type => "shop.take";

    protected override Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken) =>
        cities.TakeFromShopAsync(player, request.Id, request.Item ?? "", request.Quantity, cancellationToken);
}

public sealed class CityFestivalHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.festival";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<CityRequest>(payload);
        var until = await cities.FestivalAsync(player, r.X, r.Y, cancellationToken);
        return new { festivalUntil = until.ToUnixTimeMilliseconds() };
    }
}
