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

/// <param name="Sender">Who sent the train, when it was someone else.</param>
/// <param name="Receiver">Who owns the destination, when it is someone else.</param>
public sealed record Shipment(
    long Id, int FromX, int FromY, int ToX, int ToY, string Item, long Quantity, DateTimeOffset ArrivesAt, string? Sender = null, string? Receiver = null);

/// <summary>A train that leaves a station by itself every <see cref="EveryMinutes"/>.</summary>
/// <param name="Items">Only these leave with it; none means everything in the station.</param>
/// <param name="Problem">Why the last train did not leave: <c>empty</c>, <c>no_money</c> or <c>no_station</c>.</param>
public sealed record TrainRoute(
    long Id, int ToX, int ToY, IReadOnlyList<string> Items, int EveryMinutes, DateTimeOffset NextAt,
    DateTimeOffset? LastAt, long LastQuantity, string? Problem, string? Receiver, bool Smuggle = false, bool ByTruck = false);

public sealed record StationState(
    int X, int Y, IReadOnlyDictionary<string, long> Items, IReadOnlyList<Shipment> Incoming, IReadOnlyList<Shipment> Outgoing,
    IReadOnlyList<TrainRoute> Routes, DateTimeOffset Now, double FeeFactor = 1, int MaxRoutes = RailRules.MaxRoutes);

/// <summary>A player found by name, with the plots their station stands on.</summary>
public sealed record RailPartner(string Name, IReadOnlyList<(int X, int Y, Biome Biome)> Plots);

/// <summary>The goods in stations, shared by the station's window, its routes and the inserters that work it.</summary>
internal static class StationStock
{
    /// <summary>Locks one station until the transaction ends. Take the plot's logistics first, then this.</summary>
    public static async Task LockAsync(GameDb db, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken);
        command.Parameters.AddWithValue("key", ((long)x << 32 | (uint)y) ^ 0x73746174_696f6e21);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<Dictionary<string, long>> ReadAsync(GameDb db, int x, int y, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, long>();
        await using var command = await db.CommandAsync(
            "SELECT item, quantity FROM world.station_items WHERE x = @x AND y = @y ORDER BY item", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items[reader.GetString(0)] = reader.GetInt64(1);
        }

        return items;
    }

    public static async Task ChangeAsync(GameDb db, int x, int y, string item, long change, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.station_items (x, y, item, quantity) VALUES (@x, @y, @item, @change)
            ON CONFLICT (x, y, item) DO UPDATE SET quantity = station_items.quantity + excluded.quantity
            RETURNING quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("change", change);
        var left = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (left < 0)
        {
            throw new GameException("not_enough_items", "The station doesn't have that many.");
        }

        if (left == 0)
        {
            await using var delete = await db.CommandAsync(
                "DELETE FROM world.station_items WHERE x = @x AND y = @y AND item = @item", cancellationToken);
            delete.Parameters.AddWithValue("x", x);
            delete.Parameters.AddWithValue("y", y);
            delete.Parameters.AddWithValue("item", item);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Starts a train: the goods are already out of the station, they arrive after the trip.</summary>
    public static async Task DispatchAsync(
        GameDb db, int x, int y, int toX, int toY, string item, long quantity, Guid sender, DateTimeOffset arrivesAt, CancellationToken cancellationToken)
    {
        await using var insert = await db.CommandAsync(
            """
            INSERT INTO world.shipments (from_x, from_y, to_x, to_y, item, quantity, sender_id, arrives_at)
            VALUES (@fx, @fy, @tx, @ty, @item, @quantity, @sender, @at)
            """, cancellationToken);
        insert.Parameters.AddWithValue("fx", x);
        insert.Parameters.AddWithValue("fy", y);
        insert.Parameters.AddWithValue("tx", toX);
        insert.Parameters.AddWithValue("ty", toY);
        insert.Parameters.AddWithValue("item", item);
        insert.Parameters.AddWithValue("quantity", quantity);
        insert.Parameters.AddWithValue("sender", sender);
        insert.Parameters.AddWithValue("at", arrivesAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Takes the diesel a train of <paramref name="km"/> burns from the station, when it holds that much; true when it did.</summary>
    public static Task<bool> FuelAsync(GameDb db, int x, int y, double km, CancellationToken cancellationToken) =>
        BurnAsync(db, x, y, Items.Diesel, DieselRules.Fuel(km), cancellationToken);

    /// <summary>Takes <paramref name="need"/> of a fuel from the station, when it holds that much; true when it did.</summary>
    public static async Task<bool> BurnAsync(GameDb db, int x, int y, string fuel, long need, CancellationToken cancellationToken)
    {
        if ((await ReadAsync(db, x, y, cancellationToken)).GetValueOrDefault(fuel) < need)
        {
            return false;
        }

        await ChangeAsync(db, x, y, fuel, -need, cancellationToken);
        return true;
    }

    /// <summary>The owner of a plot, or null when nobody bought it.</summary>
    public static async Task<Guid?> OwnerAsync(GameDb db, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT owner_id FROM world.plots WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}

public sealed class StationService(
    GameDb db, WorldMap map, PlotService plots, IInventory inventory, IWallet wallet, ICharacterNames names,
    IEnumerable<IPlotAutomation> automation, BeachService beach, CityService cities, IEnumerable<ICompanies> companies, IBorderControl border,
    TimeProvider time)
{
    /// <summary>What the player's company's big projects take off their rail or sea fees.</summary>
    internal async Task<double> PerksAsync(Guid player, bool bySea, CancellationToken cancellationToken)
    {
        var projects = new HashSet<string>();
        foreach (var company in companies)
        {
            projects.UnionWith(await company.ProjectsAsync(player, cancellationToken));
        }

        return CompanyPerks.FeeFactor(projects, bySea);
    }

    /// <summary>The station of one of the player's plots, after unloading every train that has arrived.</summary>
    public Task<StationState> StateAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Work, () => Task.CompletedTask, cancellationToken);

    /// <summary>Moves items from the inventory into the station.</summary>
    public Task<StationState> LoadAsync(Guid player, int x, int y, string item, long quantity, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Work, async () =>
        {
            CheckQuantity(item, quantity);
            await inventory.RemoveAsync(player, item, quantity, cancellationToken);
            await StationStock.ChangeAsync(db, x, y, item, quantity, cancellationToken);
        }, cancellationToken);

    /// <summary>Moves items from the station into the inventory, as many as it has room for; the rest stays.</summary>
    public Task<StationState> UnloadAsync(Guid player, int x, int y, string item, long quantity, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Take, async () =>
        {
            CheckQuantity(item, quantity);
            var taken = await inventory.FitAsync(player, item, quantity, cancellationToken);
            await StationStock.ChangeAsync(db, x, y, item, -taken, cancellationToken);
            await inventory.AddAsync(player, item, taken, cancellationToken);
        }, cancellationToken);

    /// <summary>Sends items from this station by train to the station of another owned plot; <paramref name="bySea"/>, by
    /// ship from this plot's port to the other's: slower, but for a quarter of the fee (<see cref="SeaRules"/>). Into another
    /// country the goods pay its duty, or are smuggled (<paramref name="smuggle"/>) past customs that may seize them.</summary>
    public Task<StationState> SendAsync(
        Guid player, int x, int y, int toX, int toY, string item, long quantity, CancellationToken cancellationToken, bool bySea = false, bool bySled = false,
        bool smuggle = false, bool byTruck = false) =>
        ChangeAsync(player, x, y, PlotRight.Take, async () =>
        {
            CheckQuantity(item, quantity);
            await CheckDestinationAsync(x, y, toX, toY, cancellationToken);
            var km = RailRules.DistanceKm(map, x, y, toX, toY);
            if (byTruck)
            {
                await CheckTruckAsync(x, y, km, cancellationToken);
                if (quantity > TruckRules.Capacity)
                {
                    throw new GameException("too_much", $"A truck carries at most {TruckRules.Capacity} pieces.");
                }

                await StationStock.ChangeAsync(db, x, y, item, -quantity, cancellationToken);
                if (!await StationStock.BurnAsync(db, x, y, Items.Diesel, TruckRules.Fuel(km), cancellationToken))
                {
                    throw new GameException("no_diesel", $"The truck needs {TruckRules.Fuel(km)} diesel in the station.");
                }

                // A truck pays no rail fee, but crosses the border like a train.
                await CrossAndDispatchAsync(player, x, y, toX, toY, item, quantity, smuggle, TruckRules.TripTime(km), cancellationToken);
                return;
            }

            // Rail in a city is cheaper, and cheaper still with a freight terminal or the mayor's rail bonus.
            var (fee, trip) = ((long)Math.Ceiling(RailRules.FeeCents(km, quantity) * await cities.RailFactorAsync(x, y, cancellationToken)), RailRules.TripTime(km));
            if (bySea)
            {
                if (!await beach.HasAsync(x, y, Items.Port, 0, cancellationToken) || !await beach.HasAsync(toX, toY, Items.Port, 0, cancellationToken))
                {
                    throw new GameException("no_port", "A ship sails from a port to a port: both plots need one.");
                }

                var lighthouse = await beach.HasAsync(x, y, Items.Lighthouse, 1, cancellationToken) || await beach.HasAsync(toX, toY, Items.Lighthouse, 1, cancellationToken);
                (fee, trip) = (SeaRules.FeeCents(fee), SeaRules.TripTime(trip, lighthouse));
            }

            fee = (long)Math.Ceiling(fee * await PerksAsync(player, bySea, cancellationToken));
            if (bySled)
            {
                (fee, trip) = (0, await SledTripAsync(x, y, km, trip, cancellationToken));
            }

            await StationStock.ChangeAsync(db, x, y, item, -quantity, cancellationToken);
            if (!bySea && !bySled)
            {
                if (await StationStock.FuelAsync(db, x, y, km, cancellationToken))
                {
                    trip = DieselRules.TripTime(trip);
                }

                trip *= await IceTrackAsync(x, y, toX, toY, cancellationToken);
            }

            if (fee > 0)
            {
                await wallet.DebitAsync(player, fee, $"{(bySea ? "ship" : "rail")} {quantity} {item} {x}:{y}->{toX}:{toY}", cancellationToken);
            }

            await CrossAndDispatchAsync(player, x, y, toX, toY, item, quantity, smuggle, trip, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes goods already off the station across the border and sends what customs leave; fails when the border refuses them.</summary>
    private async Task CrossAndDispatchAsync(
        Guid player, int x, int y, int toX, int toY, string item, long quantity, bool smuggle, TimeSpan trip, CancellationToken cancellationToken)
    {
        var goods = new Dictionary<string, long> { [item] = quantity };
        var crossing = await border.CrossAsync(player, (x, y), (toX, toY), goods, smuggle, cancellationToken);
        if (crossing.Refused is { } refused)
        {
            throw Border.Refusal(refused);
        }

        // What customs seized at the border does not arrive.
        if (crossing.Left(goods).GetValueOrDefault(item) is var left and > 0)
        {
            await StationStock.DispatchAsync(db, x, y, toX, toY, item, left, player, time.GetUtcNow() + trip, cancellationToken);
        }
    }

    /// <summary>Fails unless trucks can leave the plot for <paramref name="km"/> away: a truck depot on it, and not too far.</summary>
    private async Task CheckTruckAsync(int x, int y, double km, CancellationToken cancellationToken)
    {
        if (!await beach.HasAsync(x, y, Items.TruckDepot, 0, cancellationToken))
        {
            throw new GameException("no_truck_depot", "Trucks leave from a truck depot on the plot.");
        }

        if (TruckRules.Problem(km) is not null)
        {
            throw new GameException("too_far", $"A truck goes at most {TruckRules.MaxKm:0} km.");
        }
    }

    /// <summary>How long a sled takes for <paramref name="km"/>, given a train's <paramref name="railTrip"/>: a snowmobile when there is petrol in the
    /// station for it (which it burns), else the dogs. Fails when the plot has neither, is not on the glacier or the tundra, or it is too far.</summary>
    private async Task<TimeSpan> SledTripAsync(int x, int y, double km, TimeSpan railTrip, CancellationToken cancellationToken)
    {
        if (map.BiomeAt(x, y) is not (Biome.Ice or Biome.Tundra))
        {
            // Elsewhere a horse cart from a stable goes a short way, on concentrate from the station.
            if (!await beach.HasAsync(x, y, Items.Stable, 0, cancellationToken))
            {
                throw new GameException("no_polar", "Sleds go from the glacier or the tundra; elsewhere a horse cart leaves from a stable.");
            }

            if (km > SledRules.HorseMaxKm)
            {
                throw new GameException("too_far", $"A horse cart goes at most {SledRules.HorseMaxKm:0} km.");
            }

            if (!await StationStock.BurnAsync(db, x, y, Items.ConcentrateFeed, SledRules.HorseFeed(km), cancellationToken))
            {
                throw new GameException("no_feed", $"The horses need {SledRules.HorseFeed(km)} concentrate feed in the station.");
            }

            return railTrip * SledRules.HorseTrip;
        }

        if (km > SledRules.MaxKm)
        {
            throw new GameException("too_far", $"A sled goes at most {SledRules.MaxKm:0} km.");
        }

        var snowmobile = await beach.HasAsync(x, y, Items.SnowmobileGarage, 0, cancellationToken);
        if (snowmobile && (await StationStock.BurnAsync(db, x, y, Items.Gasoline, SledRules.Fuel(km), cancellationToken)
            || await StationStock.BurnAsync(db, x, y, Items.Ethanol, SledRules.EthanolFuel(km), cancellationToken)))
        {
            return railTrip * SledRules.SnowmobileTrip;
        }

        if (await beach.HasAsync(x, y, Items.DogKennel, 0, cancellationToken))
        {
            return railTrip * SledRules.DogTrip;
        }

        throw snowmobile
            ? new GameException("no_petrol", "A snowmobile needs petrol in the station.")
            : new GameException("no_sled", "Sleds leave from a dog kennel or a snowmobile garage on the plot.");
    }

    /// <summary>How many times as long a train takes on the track at either end: on the glacier, without a foundation, twice.</summary>
    private async Task<double> IceTrackAsync(int x, int y, int toX, int toY, CancellationToken cancellationToken)
    {
        foreach (var (px, py) in new[] { (x, y), (toX, toY) })
        {
            if (map.BiomeAt(px, py) == Biome.Ice && !await beach.HasAsync(px, py, Items.TrackFoundation, 0, cancellationToken))
            {
                return SledRules.IceTrack;
            }
        }

        return 1;
    }

    /// <summary>Sets up a train that leaves this station by itself every <paramref name="everyMinutes"/>,
    /// the first one now, with the chosen items (or everything) for another owned plot.</summary>
    public async Task<StationState> AddRouteAsync(
        Guid player, int x, int y, int toX, int toY, IReadOnlyList<string> items, int everyMinutes, CancellationToken cancellationToken, bool smuggle = false,
        bool byTruck = false)
    {
        await ChangeAsync(player, x, y, PlotRight.Routes, async () =>
        {
            if (!RailRules.RouteMinutes.Contains(everyMinutes))
            {
                throw new GameException("bad_interval", $"A train can leave every {string.Join(", ", RailRules.RouteMinutes)} minutes.");
            }

            if (items.Count > RailRules.MaxRouteItems || items.Any(i => !Items.Exists(i ?? "")))
            {
                throw new GameException("bad_items", $"Choose at most {RailRules.MaxRouteItems} items for a route.");
            }

            await CheckDestinationAsync(x, y, toX, toY, cancellationToken);
            if (byTruck)
            {
                await CheckTruckAsync(x, y, RailRules.DistanceKm(map, x, y, toX, toY), cancellationToken);
            }

            await using (var count = await db.CommandAsync("SELECT count(*) FROM world.train_routes WHERE from_x = @x AND from_y = @y", cancellationToken))
            {
                count.Parameters.AddWithValue("x", x);
                count.Parameters.AddWithValue("y", y);
                var most = await cities.MaxRoutesAsync(x, y, cancellationToken);
                if ((long)(await count.ExecuteScalarAsync(cancellationToken))! >= most)
                {
                    throw new GameException("too_many_routes", $"A station runs at most {most} routes.");
                }
            }

            await using var insert = await db.CommandAsync(
                """
                INSERT INTO world.train_routes (owner_id, from_x, from_y, to_x, to_y, items, every_minutes, next_departure_at, last_quantity, smuggle, by_truck)
                VALUES (@owner, @fx, @fy, @tx, @ty, @items, @every, @next, 0, @smuggle, @truck)
                """, cancellationToken);
            insert.Parameters.AddWithValue("owner", player);
            insert.Parameters.AddWithValue("fx", x);
            insert.Parameters.AddWithValue("fy", y);
            insert.Parameters.AddWithValue("tx", toX);
            insert.Parameters.AddWithValue("ty", toY);
            insert.Parameters.AddWithValue("items", items.Distinct().ToArray());
            insert.Parameters.AddWithValue("every", everyMinutes);
            insert.Parameters.AddWithValue("truck", byTruck);
            insert.Parameters.AddWithValue("next", time.GetUtcNow());
            insert.Parameters.AddWithValue("smuggle", smuggle);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

        // The first train leaves at once.
        return await StateAsync(player, x, y, cancellationToken);
    }

    /// <summary>Stops a route; trains already on their way still arrive.</summary>
    public Task<StationState> RemoveRouteAsync(Guid player, int x, int y, long id, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Routes, async () =>
        {
            await using var delete = await db.CommandAsync(
                "DELETE FROM world.train_routes WHERE id = @id AND from_x = @x AND from_y = @y", cancellationToken);
            delete.Parameters.AddWithValue("id", id);
            delete.Parameters.AddWithValue("x", x);
            delete.Parameters.AddWithValue("y", y);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("no_route", "There is no such route at this station.");
            }
        }, cancellationToken);

    /// <summary>Other players by the start of their name, with their plots, to send trains to.</summary>
    public async Task<IReadOnlyList<RailPartner>> FindAsync(Guid player, string query, CancellationToken cancellationToken)
    {
        var found = await names.FindAsync(query ?? "", 9, cancellationToken);
        var owners = found.Keys.Where(id => id != player).ToArray();
        var plotsOf = owners.ToDictionary(id => id, _ => new List<(int, int, Biome)>());
        if (owners.Length > 0)
        {
            await using var command = await db.CommandAsync(
                "SELECT owner_id, x, y, biome FROM world.plots WHERE owner_id = ANY(@owners) ORDER BY bought_at", cancellationToken);
            command.Parameters.AddWithValue("owners", owners);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                plotsOf[reader.GetGuid(0)].Add((reader.GetInt32(1), reader.GetInt32(2), (Biome)reader.GetInt16(3)));
            }
        }

        // Only players with a station to send to.
        return owners.Where(id => plotsOf[id].Count > 0)
            .Select(id => new RailPartner(found[id], plotsOf[id]))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// Sends the trains of every route that is due, each in its own transaction; the background
    /// <see cref="RailwayDispatcher"/> calls this every moment. Returns how many routes were due.
    /// </summary>
    public async Task<int> RunDueRoutesAsync(int limit, CancellationToken cancellationToken)
    {
        var due = new List<long>();
        await using (var command = await db.CommandAsync(
            "SELECT id FROM world.train_routes WHERE next_departure_at <= @now ORDER BY next_departure_at LIMIT @limit", cancellationToken))
        {
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in due)
        {
            await DepartAsync(id, cancellationToken);
        }

        return due.Count;
    }

    /// <summary>Sends the due trains of routes that leave from or go to this station, so a station
    /// shows what it should even when the background dispatcher has not come round yet.</summary>
    private async Task CatchUpRoutesAsync(int x, int y, CancellationToken cancellationToken)
    {
        var due = new List<long>();
        await using (var command = await db.CommandAsync(
            """
            SELECT id FROM world.train_routes
            WHERE ((from_x = @x AND from_y = @y) OR (to_x = @x AND to_y = @y)) AND next_departure_at <= @now
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in due)
        {
            await DepartAsync(id, cancellationToken);
        }
    }

    /// <summary>
    /// One train of a route, at the latest time it was due: it takes what the station holds of the
    /// route's items, up to its capacity, and costs the usual fee. Without goods or money it stays.
    /// Runs in a transaction of its own unless one is open already.
    /// </summary>
    private Task DepartAsync(long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            int x, y;
            await using (var find = await db.CommandAsync("SELECT from_x, from_y FROM world.train_routes WHERE id = @id", cancellationToken))
            {
                find.Parameters.AddWithValue("id", id);
                await using var reader = await find.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return;
                }

                (x, y) = (reader.GetInt32(0), reader.GetInt32(1));
            }

            // Inserters may have loaded the station up to now; then the station itself, then the route.
            await CatchUpPlotAsync(x, y, cancellationToken);
            await StationStock.LockAsync(db, x, y, cancellationToken);
            await DeliverAsync(x, y, cancellationToken);

            var now = time.GetUtcNow();
            Guid owner;
            int toX, toY, every;
            string[] items;
            DateTimeOffset next;
            bool smuggle, byTruck;
            await using (var route = await db.CommandAsync(
                """
                SELECT owner_id, to_x, to_y, items, every_minutes, next_departure_at, smuggle, by_truck FROM world.train_routes
                WHERE id = @id AND next_departure_at <= @now FOR UPDATE
                """, cancellationToken))
            {
                route.Parameters.AddWithValue("id", id);
                route.Parameters.AddWithValue("now", now);
                await using var reader = await route.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    // Sent by someone else meanwhile.
                    return;
                }

                (owner, toX, toY, items, every, next, smuggle) = (reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetFieldValue<string[]>(3), reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetBoolean(6));
                byTruck = reader.GetBoolean(7);
            }

            if (await StationStock.OwnerAsync(db, x, y, cancellationToken) != owner)
            {
                // The plot is no longer the route owner's.
                await using var delete = await db.CommandAsync("DELETE FROM world.train_routes WHERE id = @id", cancellationToken);
                delete.Parameters.AddWithValue("id", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
                return;
            }

            // Trains missed while nobody was around leave as one, at the last time one was due.
            var interval = TimeSpan.FromMinutes(every);
            var departure = next + interval * Math.Floor((now - next) / interval);
            string? problem = null;
            long sent = 0;
            if (await StationStock.OwnerAsync(db, toX, toY, cancellationToken) is null)
            {
                problem = "no_station";
            }
            else if (RailRules.Cargo(await StationStock.ReadAsync(db, x, y, cancellationToken), items) is not { Count: > 0 } cargo)
            {
                problem = "empty";
            }
            else if (byTruck)
            {
                (problem, sent) = await TruckDepartAsync(owner, x, y, toX, toY, items, smuggle, departure, cancellationToken);
            }
            else
            {
                var km = RailRules.DistanceKm(map, x, y, toX, toY);
                var quantity = cargo.Sum(c => c.Quantity);
                var fee = (long)Math.Ceiling(RailRules.FeeCents(km, quantity) * await cities.RailFactorAsync(x, y, cancellationToken)
                    * await PerksAsync(owner, false, cancellationToken));
                var goods = cargo.GroupBy(c => c.Item).ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity));
                if (await wallet.BalanceAsync(owner, cancellationToken) < fee)
                {
                    problem = "no_money";
                }
                else if (await border.CrossAsync(owner, (x, y), (toX, toY), goods, smuggle, cancellationToken, fee) is var crossing
                    && crossing.Refused is { } refused)
                {
                    problem = refused;
                }
                else
                {
                    await wallet.DebitAsync(owner, fee, $"rail route {id} {quantity} items {x}:{y}->{toX}:{toY}", cancellationToken);
                    foreach (var (item, count) in cargo)
                    {
                        await StationStock.ChangeAsync(db, x, y, item, -count, cancellationToken);
                    }

                    var trip = await StationStock.FuelAsync(db, x, y, km, cancellationToken) ? DieselRules.TripTime(RailRules.TripTime(km)) : RailRules.TripTime(km);
                    trip *= await IceTrackAsync(x, y, toX, toY, cancellationToken);
                    sent = quantity;
                    // What customs seized at the border does not arrive.
                    foreach (var (item, count) in crossing.Left(goods))
                    {
                        await StationStock.DispatchAsync(db, x, y, toX, toY, item, count, owner, departure + trip, cancellationToken);
                    }
                }
            }

            await using var update = await db.CommandAsync(
                problem is null
                    ? """
                      UPDATE world.train_routes SET next_departure_at = @next, last_departed_at = @at, last_quantity = @sent, last_problem = NULL
                      WHERE id = @id
                      """
                    : "UPDATE world.train_routes SET next_departure_at = @next, last_problem = @problem WHERE id = @id",
                cancellationToken);
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("next", departure + interval);
            update.Parameters.AddWithValue("at", departure);
            update.Parameters.AddWithValue("sent", sent);
            update.Parameters.AddWithValue("problem", (object?)problem ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>A truck route's load: the train's, cut to what one truck carries.</summary>
    private static List<(string Item, long Quantity)> TruckCargo(IReadOnlyList<(string Item, long Quantity)> cargo)
    {
        var load = new List<(string, long)>();
        long room = TruckRules.Capacity;
        foreach (var (item, quantity) in cargo)
        {
            var take = Math.Min(room, quantity);
            if (take > 0)
            {
                load.Add((item, take));
                room -= take;
            }
        }

        return load;
    }

    /// <summary>Sends a route's truck with up to <see cref="TruckRules.Capacity"/> of the route's <paramref name="items"/>, burning
    /// diesel from the station first (a route taking everything leaves the truck's own diesel behind); returns why it stayed
    /// (<c>no_truck_depot</c>, <c>too_far</c>, <c>no_diesel</c>, <c>empty</c>, or the border's refusal) or null, and the pieces it took.</summary>
    private async Task<(string? Problem, long Sent)> TruckDepartAsync(
        Guid owner, int x, int y, int toX, int toY, IReadOnlyList<string> items, bool smuggle, DateTimeOffset departure, CancellationToken cancellationToken)
    {
        if (!await beach.HasAsync(x, y, Items.TruckDepot, 0, cancellationToken))
        {
            return ("no_truck_depot", 0);
        }

        var km = RailRules.DistanceKm(map, x, y, toX, toY);
        if (TruckRules.Problem(km) is { } far)
        {
            return (far, 0);
        }

        if (!await StationStock.BurnAsync(db, x, y, Items.Diesel, TruckRules.Fuel(km), cancellationToken))
        {
            return ("no_diesel", 0);
        }

        var load = TruckCargo(RailRules.Cargo(await StationStock.ReadAsync(db, x, y, cancellationToken), items));
        if (load.Count == 0)
        {
            // Only the diesel was there: it goes back in the tank.
            await StationStock.ChangeAsync(db, x, y, Items.Diesel, TruckRules.Fuel(km), cancellationToken);
            return ("empty", 0);
        }

        var goods = load.GroupBy(l => l.Item).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var crossing = await border.CrossAsync(owner, (x, y), (toX, toY), goods, smuggle, cancellationToken);
        if (crossing.Refused is { } refused)
        {
            await StationStock.ChangeAsync(db, x, y, Items.Diesel, TruckRules.Fuel(km), cancellationToken);
            return (refused, 0);
        }

        foreach (var (item, count) in load)
        {
            await StationStock.ChangeAsync(db, x, y, item, -count, cancellationToken);
        }

        // What customs seized at the border does not arrive.
        foreach (var (item, count) in crossing.Left(goods))
        {
            await StationStock.DispatchAsync(db, x, y, toX, toY, item, count, owner, departure + TruckRules.TripTime(km), cancellationToken);
        }

        return (null, load.Sum(l => l.Quantity));
    }

    /// <summary>
    /// Every station request: due trains leave first, then, in one transaction, the plot's inserters
    /// are brought up to now, the station is locked, arrived trains unload, <paramref name="change"/>
    /// runs and the station is read back.
    /// </summary>
    private async Task<StationState> ChangeAsync(
        Guid player, int x, int y, PlotRight right, Func<Task> change, CancellationToken cancellationToken)
    {
        await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
        await CatchUpRoutesAsync(x, y, cancellationToken);
        return await db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
            await CatchUpPlotAsync(x, y, cancellationToken);
            await StationStock.LockAsync(db, x, y, cancellationToken);
            await DeliverAsync(x, y, cancellationToken);
            await change();
            return await ReadAsync(player, x, y, cancellationToken);
        }, cancellationToken);
    }

    private async Task CatchUpPlotAsync(int x, int y, CancellationToken cancellationToken)
    {
        foreach (var machinery in automation)
        {
            await machinery.CatchUpAsync(x, y, cancellationToken);
        }
    }

    private async Task CheckDestinationAsync(int x, int y, int toX, int toY, CancellationToken cancellationToken)
    {
        if (!map.Contains(toX, toY) || (toX == x && toY == y))
        {
            throw new GameException("bad_destination", "Choose another plot to send to.");
        }

        if (await StationStock.OwnerAsync(db, toX, toY, cancellationToken) is null)
        {
            throw new GameException("no_station", "Nobody owns that plot, so it has no station yet.");
        }
    }

    private static void CheckQuantity(string item, long quantity)
    {
        if (!Items.Exists(item ?? ""))
        {
            throw new GameException("unknown_item", "Unknown item.");
        }

        if (quantity is < 1 or > RailRules.MaxQuantity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{RailRules.MaxQuantity}.");
        }
    }

    /// <summary>Puts the goods of every train that has arrived into the station.</summary>
    private async Task DeliverAsync(int x, int y, CancellationToken cancellationToken)
    {
        // DELETE ... RETURNING hands each arrived train to exactly one request.
        await using var command = await db.CommandAsync(
            """
            DELETE FROM world.shipments WHERE to_x = @x AND to_y = @y AND arrives_at <= @now
            RETURNING item, quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var arrived = new List<(string, long)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                arrived.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var (item, quantity) in arrived)
        {
            await StationStock.ChangeAsync(db, x, y, item, quantity, cancellationToken);
        }
    }

    private async Task<StationState> ReadAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        var items = await StationStock.ReadAsync(db, x, y, cancellationToken);
        var shipments = new List<(Shipment Trip, Guid Sender)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT id, from_x, from_y, to_x, to_y, item, quantity, arrives_at, sender_id FROM world.shipments
            WHERE (to_x = @x AND to_y = @y) OR (from_x = @x AND from_y = @y)
            ORDER BY arrives_at LIMIT 100
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                shipments.Add((new Shipment(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.GetString(5), reader.GetInt64(6), reader.GetFieldValue<DateTimeOffset>(7)), reader.GetGuid(8)));
            }
        }

        var routes = new List<TrainRoute>();
        await using (var command = await db.CommandAsync(
            """
            SELECT id, to_x, to_y, items, every_minutes, next_departure_at, last_departed_at, last_quantity, last_problem, smuggle, by_truck
            FROM world.train_routes WHERE from_x = @x AND from_y = @y ORDER BY id
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                routes.Add(new TrainRoute(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetFieldValue<string[]>(3),
                    reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5),
                    reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), null, reader.GetBoolean(9), reader.GetBoolean(10)));
            }
        }

        // Who is on the other end, when it is not the player.
        var destinations = shipments.Where(s => s.Trip.FromX == x && s.Trip.FromY == y).Select(s => (s.Trip.ToX, s.Trip.ToY))
            .Concat(routes.Select(r => (r.ToX, r.ToY))).Distinct().ToList();
        var receivers = new Dictionary<(int, int), Guid>();
        foreach (var (tx, ty) in destinations)
        {
            if (await StationStock.OwnerAsync(db, tx, ty, cancellationToken) is { } owner && owner != player)
            {
                receivers[(tx, ty)] = owner;
            }
        }

        var others = shipments.Select(s => s.Sender).Concat(receivers.Values).Where(id => id != player).Distinct().ToList();
        var nameOf = await names.NamesAsync(others, cancellationToken);
        string? Receiver(int tx, int ty) => receivers.TryGetValue((tx, ty), out var owner) ? nameOf.GetValueOrDefault(owner, "?") : null;

        var incoming = shipments.Where(s => s.Trip.ToX == x && s.Trip.ToY == y)
            .Select(s => s.Trip with { Sender = s.Sender == player ? null : nameOf.GetValueOrDefault(s.Sender, "?") }).ToList();
        var outgoing = shipments.Where(s => s.Trip.FromX == x && s.Trip.FromY == y)
            .Select(s => s.Trip with { Receiver = Receiver(s.Trip.ToX, s.Trip.ToY) }).ToList();
        return new StationState(x, y, items, incoming, outgoing,
            routes.Select(r => r with { Receiver = Receiver(r.ToX, r.ToY) }).ToList(), time.GetUtcNow(),
            await cities.RailFactorAsync(x, y, cancellationToken) * await PerksAsync(player, false, cancellationToken),
            await cities.MaxRoutesAsync(x, y, cancellationToken));
    }
}

/// <summary>
/// The station as inserters see it: anything can be put in (up to <see cref="RailRules.InserterStockLimit"/>
/// of one item) and anything taken out, every kind in turn. Trains that arrive while the plot's
/// machinery runs unload at the moment they arrive.
/// </summary>
public sealed class StationPorts(GameDb db, TimeProvider time) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (kind != RailRules.StationKind || RailRules.StationAt(id) is not var (x, y))
        {
            return null;
        }

        await StationStock.LockAsync(db, x, y, cancellationToken);
        var stock = await StationStock.ReadAsync(db, x, y, cancellationToken);
        var arriving = new List<(long Id, string Item, long Quantity, DateTimeOffset At)>();
        await using (var command = await db.CommandAsync(
            "SELECT id, item, quantity, arrives_at FROM world.shipments WHERE to_x = @x AND to_y = @y AND arrives_at <= @now ORDER BY arrives_at",
            cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                arriving.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        return new Port(db, x, y, stock, arriving);
    }

    private sealed class Port(
        GameDb db, int x, int y, Dictionary<string, long> stock, List<(long Id, string Item, long Quantity, DateTimeOffset At)> arriving)
        : IMachinePort
    {
        private readonly Dictionary<string, long> _changes = [];
        private readonly List<long> _arrived = [];
        private int _next;

        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Arrive(now);
            var kinds = stock.Where(s => s.Value > 0).Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
            for (var i = 0; i < kinds.Count; i++)
            {
                var item = kinds[(_next + i) % kinds.Count];
                if (wanted(item))
                {
                    return item;
                }
            }

            return null;
        }

        public void Take(string item, DateTimeOffset now)
        {
            if (stock.GetValueOrDefault(item) > 0)
            {
                Change(item, -1);
                _next++;
            }
        }

        public bool Accepts(string item, DateTimeOffset now)
        {
            Arrive(now);
            return stock.GetValueOrDefault(item) < RailRules.InserterStockLimit;
        }

        public void Put(string item, DateTimeOffset now) => Change(item, 1);

        public DateTimeOffset? NextChange(DateTimeOffset now) =>
            arriving.Where(a => a.At > now).Select(a => (DateTimeOffset?)a.At).FirstOrDefault();

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_arrived.Count > 0)
            {
                await using var delete = await db.CommandAsync("DELETE FROM world.shipments WHERE id = ANY(@ids)", cancellationToken);
                delete.Parameters.AddWithValue("ids", _arrived.ToArray());
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var (item, change) in _changes.Where(c => c.Value != 0))
            {
                await StationStock.ChangeAsync(db, x, y, item, change, cancellationToken);
            }
        }

        private void Arrive(DateTimeOffset now)
        {
            while (arriving.Count > 0 && arriving[0].At <= now)
            {
                Change(arriving[0].Item, arriving[0].Quantity);
                _arrived.Add(arriving[0].Id);
                arriving.RemoveAt(0);
            }
        }

        private void Change(string item, long change)
        {
            stock[item] = stock.GetValueOrDefault(item) + change;
            _changes[item] = _changes.GetValueOrDefault(item) + change;
        }
    }
}

/// <summary>Sends the trains of routes, and moves freight trains between plots, when they are due, whether or not anyone looks at the stations.</summary>
public sealed class RailwayDispatcher(IServiceScopeFactory scopes, ILogger<RailwayDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Many due at once (e.g. after a restart) go out a batch per moment.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<StationService>().RunDueRoutesAsync(200, stoppingToken);
                // Freight trains between a player's plots too.
                await scope.ServiceProvider.GetRequiredService<FreightService>().RunDueAsync(200, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Sending route trains failed");
            }
        }
    }
}

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
