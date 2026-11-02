using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

public sealed record ExportRequest(int X, int Y);

public sealed record ExportStockRequest(int X, int Y, string Item, long Quantity);

public sealed record ExportPriceRequest(int X, int Y, string Item, long PriceCents);

public sealed record PostRequest(Guid Id, long Value = 0);

public abstract class ExportHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var s = await RunAsync(scope.ServiceProvider.GetRequiredService<ExportService>(), client.RequireAccount(), payload, cancellationToken);
        string Name(string item) => Items.Names.GetValueOrDefault(item, item);
        var towns = scope.ServiceProvider.GetRequiredService<Towns>();
        object Post(ExportPost p) => new { id = p.Id, x = p.X, y = p.Y, owner = s.Names.GetValueOrDefault(p.Owner, "?"), value = p.Value };
        return new Reply("export", new
        {
            id = s.Id,
            x = s.X,
            y = s.Y,
            items = s.Items.Select(i => new { id = i.Key, name = Name(i.Key), quantity = i.Value, priceCents = s.Prices.TryGetValue(i.Key, out var p) ? p : (long?)null }),
            // Prices of items not in the chest now: the next ones in get them too.
            prices = s.Prices.Where(p => !s.Items.ContainsKey(p.Key)).Select(p => new { id = p.Key, name = Name(p.Key), priceCents = p.Value }),
            capacity = ExportRules.Capacity,
            maxKinds = ExportRules.MaxKinds,
            nextAt = s.NextAt.ToUnixTimeMilliseconds(),
            lastAt = s.LastAt?.ToUnixTimeMilliseconds(),
            lastQuantity = s.LastQuantity,
            problem = s.Problem,
            route = s.Route is { } r ? new
            {
                market = Post(r.Market),
                town = towns.Of(r.Market.X, r.Market.Y).Name,
                commissionPercent = r.Market.Value,
                ports = r.Ports.Select(Post),
                legs = r.Legs.Select(l => new { fromX = l.FromX, fromY = l.FromY, toX = l.ToX, toY = l.ToY, sea = l.BySea, plots = Math.Round(l.Plots, 1) }),
                landPlots = Math.Round(r.LandPlots, 1),
                seaPlots = Math.Round(r.SeaPlots, 1),
                tripSeconds = (long)r.Trip.TotalSeconds,
                portFeesCents = s.FeeCents,
            } : null,
            shipments = s.Shipments.Select(t => new
            {
                id = t.Id,
                item = t.Item,
                name = Name(t.Item),
                quantity = t.Quantity,
                priceCents = t.PriceCents,
                town = towns.Of(t.MarketX, t.MarketY).Name,
                arrivesAt = t.ArrivesAt.ToUnixTimeMilliseconds(),
            }),
            rates = new
            {
                everyMinutes = (int)ExportRules.Every.TotalMinutes,
                minutesPerPlot = ExportRules.MinutesPerPlot,
                seaFactor = ExportRules.SeaFactor,
                minTripSeconds = (long)ExportRules.MinTrip.TotalSeconds,
            },
            // Buying markets in the world: with none, nothing can be exported; with some but no route, a port is missing.
            markets = s.Markets,
            serverTime = s.Now.ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<ExportChestState> RunAsync(ExportService export, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetExportHandler(IServiceScopeFactory scopes) : ExportHandler(scopes)
{
    public override string Type => "export.get";

    protected override Task<ExportChestState> RunAsync(ExportService export, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ExportRequest>(payload);
        return export.StateAsync(player, r.X, r.Y, cancellationToken);
    }
}

public sealed class LoadExportHandler(IServiceScopeFactory scopes) : ExportHandler(scopes)
{
    public override string Type => "export.load";

    protected override Task<ExportChestState> RunAsync(ExportService export, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ExportStockRequest>(payload);
        return export.LoadAsync(player, r.X, r.Y, r.Item, r.Quantity, cancellationToken);
    }
}

public sealed class UnloadExportHandler(IServiceScopeFactory scopes) : ExportHandler(scopes)
{
    public override string Type => "export.unload";

    protected override Task<ExportChestState> RunAsync(ExportService export, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ExportStockRequest>(payload);
        return export.UnloadAsync(player, r.X, r.Y, r.Item, r.Quantity, cancellationToken);
    }
}

public sealed class PriceExportHandler(IServiceScopeFactory scopes) : ExportHandler(scopes)
{
    public override string Type => "export.price";

    protected override Task<ExportChestState> RunAsync(ExportService export, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ExportPriceRequest>(payload);
        return export.PriceAsync(player, r.X, r.Y, r.Item, r.PriceCents, cancellationToken);
    }
}

/// <summary>A buying market or a rail port, as anyone sees it, with what it earned for its owner.</summary>
public sealed class GetExportPostHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "export.post";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var r = Payload.Read<PostRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("export.post", await scope.ServiceProvider.GetRequiredService<ExportService>().PostAsync(client.RequireAccount(), r.Id, cancellationToken));
    }
}

/// <summary>The owner sets a buying market's commission (percent) or a rail port's fee (cents).</summary>
public sealed class SetExportPostHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "export.setPost";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var r = Payload.Read<PostRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("export.post",
            await scope.ServiceProvider.GetRequiredService<ExportService>().SetPostAsync(client.RequireAccount(), r.Id, r.Value, cancellationToken));
    }
}
