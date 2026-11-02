using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Every plot has a station on the global railway. Goods loaded into a station can be sent to the
/// station of any other owned plot (the player's own or another player's factory); the train takes
/// time and costs a fee by distance, and the goods wait in the destination station until unloaded.
/// A route sends a train by itself every few minutes with whatever the station holds, and
/// inserters load and unload the station, so factories on different plots work together.
/// </summary>
public static class RailRules
{
    /// <summary>Real seconds a train needs per 100 km, and the shortest trip.</summary>
    public const double SecondsPer100Km = 5;

    public static readonly TimeSpan MinTrip = TimeSpan.FromSeconds(10);

    /// <summary>Fee: a fixed part plus cents per item per 100 km.</summary>
    public const long BaseFeeCents = 100;

    public const long CentsPerItemPer100Km = 1;

    public const int MaxQuantity = 100_000;

    /// <summary>Most items one train of a route carries.</summary>
    public const int RouteCapacity = 2000;

    /// <summary>How often a route's train may leave, in minutes.</summary>
    public static readonly IReadOnlyList<int> RouteMinutes = [1, 5, 15, 60];

    /// <summary>Most routes leaving one station.</summary>
    public const int MaxRoutes = 5;

    /// <summary>Most items a route can be limited to.</summary>
    public const int MaxRouteItems = 8;

    /// <summary>Inserters stop putting an item into the station when it holds this many of it.</summary>
    public const long InserterStockLimit = 10_000;

    /// <summary>The kind of the station as a fixture of the plot (see <see cref="IPlotObjects.FixturesAsync"/>).</summary>
    public const string StationKind = "rail_station";

    private const int IdMark = 0x57a7_1011;
    private static readonly byte[] IdTail = "station!"u8.ToArray();

    /// <summary>The station of a plot as one object, the same id every time.</summary>
    public static Guid StationId(int x, int y) => new(IdMark, (short)x, (short)y, IdTail);

    /// <summary>The plot of a station id made by <see cref="StationId"/>.</summary>
    public static (int X, int Y)? StationAt(Guid id)
    {
        var bytes = id.ToByteArray();
        if (BitConverter.ToInt32(bytes, 0) != IdMark || !bytes.AsSpan(8).SequenceEqual(IdTail))
        {
            return null;
        }

        return ((ushort)BitConverter.ToInt16(bytes, 4), (ushort)BitConverter.ToInt16(bytes, 6));
    }

    /// <summary>The tiles inserters reach the station on: the building and the platform track in front of it.</summary>
    public static IEnumerable<(int TileX, int TileY)> StationTiles()
    {
        var (sx, sy, sw, sh) = PlotLayout.Station;
        for (var y = sy; y < sy + sh; y++)
        {
            for (var x = sx; x < sx + sw; x++)
            {
                yield return (x, y);
            }
        }

        for (var x = sx; x < sx + sw; x++)
        {
            yield return (x, PlotLayout.RailRow);
        }
    }

    /// <summary>Distance between plot centres along the grid, wrapping east-west.</summary>
    public static double DistanceKm(WorldMap map, int fromX, int fromY, int toX, int toY)
    {
        var dx = Math.Abs(fromX - toX);
        dx = Math.Min(dx, map.Width - dx);
        return Math.Sqrt((double)dx * dx + (double)(fromY - toY) * (fromY - toY)) * PlotRules.SideKm;
    }

    public static TimeSpan TripTime(double km) =>
        TimeSpan.FromSeconds(Math.Max(MinTrip.TotalSeconds, km / 100 * SecondsPer100Km));

    public static long FeeCents(double km, long quantity) =>
        BaseFeeCents + (long)Math.Ceiling(km / 100 * quantity * CentsPerItemPer100Km);

    /// <summary>What a train of a route takes from the station's stock: only the route's items (all when
    /// it has none), in the route's order, then by name, up to <see cref="RouteCapacity"/>.</summary>
    public static IReadOnlyList<(string Item, long Quantity)> Cargo(IReadOnlyDictionary<string, long> stock, IReadOnlyList<string> items)
    {
        var order = items.Count > 0
            ? items.Where(stock.ContainsKey)
            : stock.Keys.Order(StringComparer.Ordinal);
        var cargo = new List<(string, long)>();
        long room = RouteCapacity;
        foreach (var item in order)
        {
            var take = Math.Min(room, stock[item]);
            if (take > 0)
            {
                cargo.Add((item, take));
                room -= take;
            }
        }

        return cargo;
    }
}

/// <summary>A diesel locomotive: a train leaving a station that holds diesel burns some of it and gets there sooner.</summary>
public static class DieselRules
{
    /// <summary>A diesel train takes this part of a steam train's time.</summary>
    public const double TripFactor = 0.6;

    /// <summary>Diesel one train burns: a unit for every 250 km begun.</summary>
    public static long Fuel(double km) => Math.Max(1, (long)Math.Ceiling(km / 250));

    public static TimeSpan TripTime(TimeSpan steamTrip) => steamTrip * TripFactor;
}

/// <summary>Trucks from a truck depot drive goods from the plot's station to another station up to <see cref="MaxKm"/> away: no rail fee,
/// in <see cref="TripFactor"/> of a train's time, at most <see cref="Capacity"/> pieces at once, on diesel from the station.</summary>
public static class TruckRules
{
    public const double MaxKm = 60;

    public const double TripFactor = 0.5;

    public static readonly TimeSpan MinTrip = TimeSpan.FromSeconds(5);

    public const int Capacity = 1000;

    public const double KmPerDiesel = 25;

    /// <summary>Diesel a truck burns: a unit for every 25 km begun.</summary>
    public static long Fuel(double km) => Math.Max(1, (long)Math.Ceiling(km / KmPerDiesel));

    public static TimeSpan TripTime(double km) =>
        TimeSpan.FromSeconds(Math.Max(MinTrip.TotalSeconds, km / 100 * RailRules.SecondsPer100Km * TripFactor));

    /// <summary>Why a truck cannot go <paramref name="km"/>: <c>too_far</c>, or null when it can.</summary>
    public static string? Problem(double km) => km > MaxKm ? "too_far" : null;
}

/// <summary>Ships between ports: a ship needs no track, takes twice as long as a train and costs a quarter of the
/// fee; a lighthouse next to either end (on the plot or one around it) cuts a quarter off the trip.</summary>
public static class SeaRules
{
    public const double SlowerThanRail = 2;

    public const double LighthouseTrip = 0.75;

    public static long FeeCents(long railFeeCents) => (railFeeCents + 3) / 4;

    public static TimeSpan TripTime(TimeSpan railTrip, bool lighthouse) => railTrip * SlowerThanRail * (lighthouse ? LighthouseTrip : 1);
}

/// <summary>The glacier: a track on ice without a foundation takes trains <see cref="IceTrack"/> times as long, and from the glacier or the
/// tundra a dog sled or a snowmobile carries goods to a station up to <see cref="MaxKm"/> away with no track and no fee: the dogs slower than a
/// train, a snowmobile faster on petrol from the station.</summary>
public static class SledRules
{
    public const double IceTrack = 2;

    public const double MaxKm = 150;

    public const double DogTrip = 1.5;

    public const double SnowmobileTrip = 0.8;

    /// <summary>Petrol a snowmobile burns: a unit for every 50 km begun.</summary>
    public static long Fuel(double km) => Math.Max(1, (long)Math.Ceiling(km / 50));

    /// <summary>How much of a unit of petrol a unit of ethanol from the potato distillery stands for in a snowmobile or a diesel generator.</summary>
    public const double EthanolWorth = 0.8;

    /// <summary>Ethanol a snowmobile burns instead of petrol.</summary>
    public static long EthanolFuel(double km) => (long)Math.Ceiling(Fuel(km) / EthanolWorth);

    /// <summary>Off the glacier, a horse cart from a stable carries goods up to <see cref="HorseMaxKm"/> free, taking <see cref="HorseTrip"/> of a train's time.</summary>
    public const double HorseMaxKm = 50;

    public const double HorseTrip = 2;

    /// <summary>Concentrate the horses eat on the way: a unit for every 10 km begun, from the station.</summary>
    public static long HorseFeed(double km) => Math.Max(1, (long)Math.Ceiling(km / 10));
}
