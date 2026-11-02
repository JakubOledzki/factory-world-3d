using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

public sealed record StationRequest(int X, int Y);

public sealed record StockRequest(int X, int Y, string Item, long Quantity);

public sealed record SendRequest(int X, int Y, int ToX, int ToY, string Item, long Quantity, bool Sea = false, bool Sled = false, bool Smuggle = false,
    bool Truck = false);

public sealed record RouteRequest(int X, int Y, int ToX, int ToY, List<string>? Items, int EveryMinutes, bool Smuggle = false, bool Truck = false);

public sealed record RemoveRouteRequest(int X, int Y, long Id);

public sealed record FindRequest(string? Query);

public abstract class StationHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<StationService>(), client.RequireAccount(), payload, cancellationToken);
        object Trip(Shipment s) => new
        {
            fromX = s.FromX,
            fromY = s.FromY,
            toX = s.ToX,
            toY = s.ToY,
            item = s.Item,
            name = Items.Names.GetValueOrDefault(s.Item, s.Item),
            quantity = s.Quantity,
            arrivesAt = s.ArrivesAt.ToUnixTimeMilliseconds(),
            sender = s.Sender,
            receiver = s.Receiver,
        };
        return new Reply("station", new
        {
            x = state.X,
            y = state.Y,
            items = state.Items.Select(i => new { id = i.Key, name = Items.Names.GetValueOrDefault(i.Key, i.Key), quantity = i.Value }),
            incoming = state.Incoming.Select(Trip),
            outgoing = state.Outgoing.Select(Trip),
            routes = state.Routes.Select(r => new
            {
                id = r.Id,
                toX = r.ToX,
                toY = r.ToY,
                receiver = r.Receiver,
                items = r.Items.Select(i => new { id = i, name = Items.Names.GetValueOrDefault(i, i) }),
                everyMinutes = r.EveryMinutes,
                nextAt = r.NextAt.ToUnixTimeMilliseconds(),
                lastAt = r.LastAt?.ToUnixTimeMilliseconds(),
                lastQuantity = r.LastQuantity,
                problem = r.Problem,
                smuggle = r.Smuggle,
                // Driven by the plot's trucks rather than a train.
                truck = r.ByTruck,
            }),
            serverTime = state.Now.ToUnixTimeMilliseconds(),
            rates = new
            {
                baseFeeCents = RailRules.BaseFeeCents,
                centsPerItemPer100Km = RailRules.CentsPerItemPer100Km,
                secondsPer100Km = RailRules.SecondsPer100Km,
                minTripSeconds = RailRules.MinTrip.TotalSeconds,
                routeCapacity = RailRules.RouteCapacity,
                routeMinutes = RailRules.RouteMinutes,
                maxRoutes = state.MaxRoutes,
                // What fees from this station are multiplied by: below 1 in a city.
                feeFactor = state.FeeFactor,
                maxRouteItems = RailRules.MaxRouteItems,
                // With diesel in the station a train goes as a diesel locomotive: it burns a unit per 250 km begun and gets there sooner.
                dieselTripFactor = DieselRules.TripFactor,
                dieselKmPerUnit = 250,
                // From the glacier or the tundra a sled carries goods up to sledMaxKm free: dogs take sledDogTrip of a train's time,
                // a snowmobile sledSnowmobileTrip on a unit of petrol per 50 km; trains on ice without a foundation take iceTrack times as long.
                sledMaxKm = SledRules.MaxKm,
                sledDogTrip = SledRules.DogTrip,
                sledSnowmobileTrip = SledRules.SnowmobileTrip,
                sledKmPerPetrol = 50,
                horseMaxKm = SledRules.HorseMaxKm,
                horseTrip = SledRules.HorseTrip,
                horseKmPerFeed = 10,
                iceTrack = SledRules.IceTrack,
                // From a truck depot trucks drive up to truckMaxKm, free, in truckTrip of a train's time (truckMinTripSeconds at least),
                // truckCapacity pieces at once, on a unit of diesel per truckKmPerDiesel km begun.
                truckMaxKm = TruckRules.MaxKm,
                truckTrip = TruckRules.TripFactor,
                truckMinTripSeconds = TruckRules.MinTrip.TotalSeconds,
                truckCapacity = TruckRules.Capacity,
                truckKmPerDiesel = TruckRules.KmPerDiesel,
            },
        });
    }

    protected abstract Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetStationHandler(IServiceScopeFactory scopes) : StationHandler(scopes)
{
    public override string Type => "station.get";

    protected override Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<StationRequest>(payload);
        return station.StateAsync(player, r.X, r.Y, cancellationToken);
    }
}

public sealed class LoadStationHandler(IServiceScopeFactory scopes) : StationHandler(scopes)
{
    public override string Type => "station.load";

    protected override Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<StockRequest>(payload);
        return station.LoadAsync(player, r.X, r.Y, r.Item, r.Quantity, cancellationToken);
    }
}

public sealed class UnloadStationHandler(IServiceScopeFactory scopes) : StationHandler(scopes)
{
    public override string Type => "station.unload";

    protected override Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<StockRequest>(payload);
        return station.UnloadAsync(player, r.X, r.Y, r.Item, r.Quantity, cancellationToken);
    }
}

public sealed class SendHandler(IServiceScopeFactory scopes) : StationHandler(scopes)
{
    public override string Type => "station.send";

    protected override Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<SendRequest>(payload);
        return station.SendAsync(player, r.X, r.Y, r.ToX, r.ToY, r.Item, r.Quantity, cancellationToken, r.Sea, r.Sled, r.Smuggle, r.Truck);
    }
}

public sealed class AddRouteHandler(IServiceScopeFactory scopes) : StationHandler(scopes)
{
    public override string Type => "station.route";

    protected override Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<RouteRequest>(payload);
        return station.AddRouteAsync(player, r.X, r.Y, r.ToX, r.ToY, r.Items ?? [], r.EveryMinutes, cancellationToken, r.Smuggle, r.Truck);
    }
}

public sealed class RemoveRouteHandler(IServiceScopeFactory scopes) : StationHandler(scopes)
{
    public override string Type => "station.unroute";

    protected override Task<StationState> RunAsync(StationService station, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<RemoveRouteRequest>(payload);
        return station.RemoveRouteAsync(player, r.X, r.Y, r.Id, cancellationToken);
    }
}

/// <summary>Other players by name, with their plots, to send trains to.</summary>
public sealed class FindPartnersHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "station.find";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var r = Payload.Read<FindRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<StationService>().FindAsync(client.RequireAccount(), r.Query ?? "", cancellationToken);
        return new Reply("station.found", new
        {
            query = r.Query ?? "",
            players = found.Select(p => new
            {
                name = p.Name,
                plots = p.Plots.Select(plot => new { x = plot.X, y = plot.Y, biome = (int)plot.Biome }),
            }),
        });
    }
}
