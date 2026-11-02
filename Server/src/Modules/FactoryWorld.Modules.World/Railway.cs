using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

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
