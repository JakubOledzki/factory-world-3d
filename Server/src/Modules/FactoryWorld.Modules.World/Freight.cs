using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Freight between a player's own plots. A route has one train: it waits at the plot it loads on until the loading chests there
/// hold everything one train carries (the route's items, so many of each), takes it for the usual rail fee by distance, goes to the
/// other plot and puts the goods into its receiving chests. When they are full it waits until there is room, then goes back empty
/// and waits for the next load. A plot may have any number of loading and receiving chests; a filter keeps a chest to one item.
/// </summary>
public static class FreightRules
{
    /// <summary>Most freight routes one player runs.</summary>
    public const int MaxRoutes = 20;

    /// <summary>Most kinds of goods one train carries.</summary>
    public const int MaxItems = 4;

    /// <summary>Most pieces one train carries, of all its goods together.</summary>
    public const long MaxQuantity = 10_000;

    /// <summary>How often a waiting train looks at the chests again: for its load, money for the fee, or room to unload.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);

    public const string Loading = "loading";
    public const string Travelling = "travelling";
    public const string Unloading = "unloading";
    public const string Returning = "returning";
}

/// <summary>Names players give their plots.</summary>
public static class PlotNames
{
    public const int MaxLength = 32;

    /// <summary>The name to keep: trimmed, null for none. Fails when it is too long or has control characters.</summary>
    public static string? Clean(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (name.Length > MaxLength || name.Any(char.IsControl))
        {
            throw new GameException("bad_name", $"A plot's name has at most {MaxLength} characters.");
        }

        return name;
    }
}

public sealed record FreightCargo(string Item, long Quantity, long Stock);

/// <param name="State">See <see cref="FreightRules"/>; <paramref name="NextAt"/> is when it gets there or back, or looks again.</param>
public sealed record FreightRoute(
    long Id, int FromX, int FromY, int ToX, int ToY, IReadOnlyList<FreightCargo> Cargo, string State, DateTimeOffset NextAt,
    IReadOnlyDictionary<string, long> Carried, long Trips, long Delivered, long LastFeeCents, string? Problem, bool Stopping,
    double Km, TimeSpan Trip, long FeeCents, bool Smuggle = false);

/// <summary>One of the player's plots as the route menu shows it.</summary>
public sealed record FreightPlot(int X, int Y, Biome Biome, string? Name, int Loading, int Receiving, double FeeFactor);

public sealed record FreightState(IReadOnlyList<FreightRoute> Routes, IReadOnlyList<FreightPlot> Plots, DateTimeOffset Now);

public sealed class FreightService(
    GameDb db, WorldMap map, IWallet wallet, IFreightChests chests, CityService cities, StationService stations, IBorderControl border, TimeProvider time)
{
    /// <summary>The player's routes and plots, after moving the trains that were due.</summary>
    public async Task<FreightState> StateAsync(Guid player, CancellationToken cancellationToken)
    {
        await RunDueAsync(player, 50, cancellationToken);
        return await ReadAsync(player, cancellationToken);
    }

    /// <summary>Sets up a route from one of the player's plots to another; its train starts waiting for the load at once.</summary>
    public async Task<FreightState> AddAsync(
        Guid player, int fromX, int fromY, int toX, int toY, IReadOnlyList<(string Item, long Quantity)> cargo, CancellationToken cancellationToken,
        bool smuggle = false)
    {
        if (fromX == toX && fromY == toY)
        {
            throw new GameException("bad_destination", "A route goes from one plot to another.");
        }

        if (cargo.Count is 0 or > FreightRules.MaxItems || cargo.Any(c => !Items.Exists(c.Item ?? "") || Items.IsFluid(c.Item!) || c.Quantity < 1)
            || cargo.Select(c => c.Item).Distinct().Count() != cargo.Count)
        {
            throw new GameException("bad_items", $"Choose 1-{FreightRules.MaxItems} kinds of goods (no fluids), at least one piece of each.");
        }

        if (cargo.Sum(c => c.Quantity) > FreightRules.MaxQuantity)
        {
            throw new GameException("bad_quantity", $"A train carries at most {FreightRules.MaxQuantity} pieces.");
        }

        await db.InTransactionAsync(async () =>
        {
            foreach (var (x, y) in new[] { (fromX, fromY), (toX, toY) })
            {
                if (await StationStock.OwnerAsync(db, x, y, cancellationToken) != player)
                {
                    throw new GameException("not_owner", "A route runs between your own plots.");
                }
            }

            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(player.ToByteArray()) ^ 0x6672_6569_6768_7421);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var count = await db.CommandAsync("SELECT count(*) FROM world.freight_routes WHERE owner_id = @owner", cancellationToken))
            {
                count.Parameters.AddWithValue("owner", player);
                if ((long)(await count.ExecuteScalarAsync(cancellationToken))! >= FreightRules.MaxRoutes)
                {
                    throw new GameException("too_many_routes", $"You run at most {FreightRules.MaxRoutes} routes.");
                }
            }

            var now = time.GetUtcNow();
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO world.freight_routes (owner_id, from_x, from_y, to_x, to_y, items, quantities, state, next_at,
                    carried_items, carried_quantities, stock, trips, delivered, last_fee_cents, stopping, created_at, smuggle)
                VALUES (@owner, @fx, @fy, @tx, @ty, @items, @quantities, @state, @now, '{}', '{}', '{}', 0, 0, 0, false, @now, @smuggle)
                """, cancellationToken);
            insert.Parameters.AddWithValue("owner", player);
            insert.Parameters.AddWithValue("fx", fromX);
            insert.Parameters.AddWithValue("fy", fromY);
            insert.Parameters.AddWithValue("tx", toX);
            insert.Parameters.AddWithValue("ty", toY);
            insert.Parameters.AddWithValue("items", cargo.Select(c => c.Item).ToArray());
            insert.Parameters.AddWithValue("quantities", cargo.Select(c => c.Quantity).ToArray());
            insert.Parameters.AddWithValue("state", FreightRules.Loading);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("smuggle", smuggle);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

        return await StateAsync(player, cancellationToken);
    }

    /// <summary>Ends a route: at once while its train is empty, else once the goods on it are delivered.</summary>
    public async Task<FreightState> RemoveAsync(Guid player, long id, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(async () =>
        {
            await using (var find = await db.CommandAsync(
                "SELECT cardinality(carried_items) FROM world.freight_routes WHERE id = @id AND owner_id = @owner FOR UPDATE", cancellationToken))
            {
                find.Parameters.AddWithValue("id", id);
                find.Parameters.AddWithValue("owner", player);
                if (await find.ExecuteScalarAsync(cancellationToken) is not int carried)
                {
                    throw new GameException("no_route", "There is no such route.");
                }

                if (carried > 0)
                {
                    await using var stop = await db.CommandAsync("UPDATE world.freight_routes SET stopping = true WHERE id = @id", cancellationToken);
                    stop.Parameters.AddWithValue("id", id);
                    await stop.ExecuteNonQueryAsync(cancellationToken);
                    return;
                }
            }

            await DeleteAsync(id, cancellationToken);
        }, cancellationToken);

        return await StateAsync(player, cancellationToken);
    }

    /// <summary>Moves every train that is due, each route in a transaction of its own; the background <see cref="RailwayDispatcher"/>
    /// calls this every moment. Returns how many were due.</summary>
    public Task<int> RunDueAsync(int limit, CancellationToken cancellationToken) => RunDueAsync(null, limit, cancellationToken);

    private async Task<int> RunDueAsync(Guid? owner, int limit, CancellationToken cancellationToken)
    {
        var due = new List<long>();
        await using (var command = await db.CommandAsync(
            "SELECT id FROM world.freight_routes WHERE next_at <= @now AND (@owner::uuid IS NULL OR owner_id = @owner) ORDER BY next_at LIMIT @limit",
            cancellationToken))
        {
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            command.Parameters.AddWithValue("owner", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)owner ?? DBNull.Value);
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(reader.GetInt64(0));
            }
        }

        foreach (var id in due)
        {
            try
            {
                await StepAsync(id, cancellationToken);
            }
            catch (GameException)
            {
                // Something on the way failed (e.g. the money went just then): the train looks again in a moment.
                await using var later = await db.CommandAsync("UPDATE world.freight_routes SET next_at = @next WHERE id = @id", cancellationToken);
                later.Parameters.AddWithValue("id", id);
                later.Parameters.AddWithValue("next", time.GetUtcNow() + FreightRules.CheckEvery);
                await later.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return due.Count;
    }

    /// <summary>
    /// One look at a route's train, in a transaction of its own: a waiting train loads when the chests hold its load and there is
    /// money for the fee; one that got there unloads what fits; one that came back waits for the next load. A transaction locks
    /// the machinery of one plot only, the one the train stands at.
    /// </summary>
    private Task StepAsync(long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var now = time.GetUtcNow();
            var route = await FindAsync(id, now, cancellationToken);
            if (route is null)
            {
                // Moved by someone else meanwhile.
                return;
            }

            if (await StationStock.OwnerAsync(db, route.FromX, route.FromY, cancellationToken) != route.OwnerId
                || await StationStock.OwnerAsync(db, route.ToX, route.ToY, cancellationToken) != route.OwnerId)
            {
                // One of the plots is no longer the route owner's.
                await DeleteAsync(id, cancellationToken);
                return;
            }

            if (route.State == FreightRules.Returning)
            {
                if (route.Stopping)
                {
                    await DeleteAsync(id, cancellationToken);
                    return;
                }

                route.State = FreightRules.Loading;
            }

            if (route.State == FreightRules.Loading)
            {
                await LoadAsync(route, now, cancellationToken);
            }
            else
            {
                await UnloadAsync(route, now, cancellationToken);
                if (route.Stopping && route.State == FreightRules.Returning)
                {
                    // The last goods are delivered: the route ends.
                    await DeleteAsync(id, cancellationToken);
                    return;
                }
            }

            await SaveAsync(route, cancellationToken);
        }, cancellationToken);

    private async Task LoadAsync(FreightRouteRow route, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stock = await chests.LoadingAsync(route.FromX, route.FromY, cancellationToken);
        route.Stock = route.Items.Select(i => stock.GetValueOrDefault(i)).ToArray();
        route.NextAt = now + FreightRules.CheckEvery;
        if (route.Items.Where((item, i) => stock.GetValueOrDefault(item) < route.Quantities[i]).Any())
        {
            route.LastProblem = "waiting";
            return;
        }

        var (km, trip, fee) = await TermsAsync(route.OwnerId, route.FromX, route.FromY, route.ToX, route.ToY, route.Quantities.Sum(), cancellationToken);
        if (await wallet.BalanceAsync(route.OwnerId, cancellationToken) < fee)
        {
            route.LastProblem = "no_money";
            return;
        }

        var load = route.Items.Zip(route.Quantities).ToDictionary(p => p.First, p => p.Second);
        // Into another country: the duty, or smuggling past customs.
        var crossing = await border.CrossAsync(route.OwnerId, (route.FromX, route.FromY), (route.ToX, route.ToY), load, route.Smuggle, cancellationToken, fee);
        if (crossing.Refused is { } refused)
        {
            route.LastProblem = refused;
            return;
        }

        if (!await chests.TakeAsync(route.FromX, route.FromY, load, cancellationToken))
        {
            // The duty is paid already: undo it with the rest.
            throw new GameException("waiting", "The load is not in the chests.");
        }

        await wallet.DebitAsync(route.OwnerId, fee,
            $"freight route {route.Id} {load.Values.Sum()} items {route.FromX}:{route.FromY}->{route.ToX}:{route.ToY} ({km:0} km)", cancellationToken);
        route.State = FreightRules.Travelling;
        route.NextAt = now + trip;
        // What customs seized at the border does not arrive.
        var carried = crossing.Left(load);
        route.CarriedItems = carried.Keys.ToArray();
        route.CarriedQuantities = carried.Values.ToArray();
        route.Stock = route.Items.Select(i => stock.GetValueOrDefault(i) - load[i]).ToArray();
        route.LastFeeCents = fee;
        route.LastProblem = null;
    }

    private async Task UnloadAsync(FreightRouteRow route, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var carried = route.CarriedItems.Zip(route.CarriedQuantities).ToDictionary(p => p.First, p => p.Second);
        var left = await chests.UnloadAsync(route.ToX, route.ToY, carried, cancellationToken);
        route.Delivered += carried.Values.Sum() - left.Values.Sum();
        route.CarriedItems = left.Keys.ToArray();
        route.CarriedQuantities = left.Values.ToArray();
        if (left.Count > 0)
        {
            route.State = FreightRules.Unloading;
            route.LastProblem = "no_room";
            route.NextAt = now + FreightRules.CheckEvery;
            return;
        }

        var (_, trip, _) = await TermsAsync(route.OwnerId, route.FromX, route.FromY, route.ToX, route.ToY, 0, cancellationToken);
        route.Trips++;
        route.State = FreightRules.Returning;
        route.LastProblem = null;
        route.NextAt = now + trip;
    }

    /// <summary>How far a train goes, how long it takes one way and what it costs with <paramref name="quantity"/> pieces: as a train
    /// sent from a station, cheaper from a city and with the company's big projects.</summary>
    private async Task<(double Km, TimeSpan Trip, long FeeCents)> TermsAsync(
        Guid owner, int fromX, int fromY, int toX, int toY, long quantity, CancellationToken cancellationToken)
    {
        var km = RailRules.DistanceKm(map, fromX, fromY, toX, toY);
        var fee = (long)Math.Ceiling(RailRules.FeeCents(km, quantity) * await cities.RailFactorAsync(fromX, fromY, cancellationToken)
            * await stations.PerksAsync(owner, false, cancellationToken));
        return (km, RailRules.TripTime(km), fee);
    }

    private async Task<FreightRouteRow?> FindAsync(long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"SELECT {Columns} FROM world.freight_routes WHERE id = @id AND next_at <= @now FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private const string Columns =
        "id, owner_id, from_x, from_y, to_x, to_y, items, quantities, state, next_at, carried_items, carried_quantities, stock, trips, delivered, " +
        "last_fee_cents, last_problem, stopping, smuggle";

    private static FreightRouteRow Read(Npgsql.NpgsqlDataReader r) => new()
    {
        Id = r.GetInt64(0),
        OwnerId = r.GetGuid(1),
        FromX = r.GetInt32(2),
        FromY = r.GetInt32(3),
        ToX = r.GetInt32(4),
        ToY = r.GetInt32(5),
        Items = r.GetFieldValue<string[]>(6),
        Quantities = r.GetFieldValue<long[]>(7),
        State = r.GetString(8),
        NextAt = r.GetFieldValue<DateTimeOffset>(9),
        CarriedItems = r.GetFieldValue<string[]>(10),
        CarriedQuantities = r.GetFieldValue<long[]>(11),
        Stock = r.GetFieldValue<long[]>(12),
        Trips = r.GetInt64(13),
        Delivered = r.GetInt64(14),
        LastFeeCents = r.GetInt64(15),
        LastProblem = r.IsDBNull(16) ? null : r.GetString(16),
        Stopping = r.GetBoolean(17),
        Smuggle = r.GetBoolean(18),
    };

    private async Task SaveAsync(FreightRouteRow route, CancellationToken cancellationToken)
    {
        await using var update = await db.CommandAsync(
            """
            UPDATE world.freight_routes SET state = @state, next_at = @next, carried_items = @carried, carried_quantities = @carried_quantities,
                stock = @stock, trips = @trips, delivered = @delivered, last_fee_cents = @fee, last_problem = @problem
            WHERE id = @id
            """, cancellationToken);
        update.Parameters.AddWithValue("id", route.Id);
        update.Parameters.AddWithValue("state", route.State);
        update.Parameters.AddWithValue("next", route.NextAt);
        update.Parameters.AddWithValue("carried", route.CarriedItems);
        update.Parameters.AddWithValue("carried_quantities", route.CarriedQuantities);
        update.Parameters.AddWithValue("stock", route.Stock);
        update.Parameters.AddWithValue("trips", route.Trips);
        update.Parameters.AddWithValue("delivered", route.Delivered);
        update.Parameters.AddWithValue("fee", route.LastFeeCents);
        update.Parameters.AddWithValue("problem", (object?)route.LastProblem ?? DBNull.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM world.freight_routes WHERE id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<FreightState> ReadAsync(Guid player, CancellationToken cancellationToken)
    {
        var rows = new List<FreightRouteRow>();
        await using (var command = await db.CommandAsync(
            $"SELECT {Columns} FROM world.freight_routes WHERE owner_id = @owner ORDER BY id", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(Read(reader));
            }
        }

        var routes = new List<FreightRoute>();
        foreach (var r in rows)
        {
            var (km, trip, fee) = await TermsAsync(player, r.FromX, r.FromY, r.ToX, r.ToY, r.Quantities.Sum(), cancellationToken);
            routes.Add(new FreightRoute(r.Id, r.FromX, r.FromY, r.ToX, r.ToY,
                r.Items.Select((item, i) => new FreightCargo(item, r.Quantities[i], i < r.Stock.Length ? r.Stock[i] : 0)).ToList(),
                r.State, r.NextAt, r.CarriedItems.Zip(r.CarriedQuantities).ToDictionary(p => p.First, p => p.Second),
                r.Trips, r.Delivered, r.LastFeeCents, r.LastProblem, r.Stopping, km, trip, fee, r.Smuggle));
        }

        // The player's plots with how many freight chests stand on each.
        var owned = new List<(int X, int Y, Biome Biome, string? Name)>();
        await using (var command = await db.CommandAsync(
            "SELECT x, y, biome, name FROM world.plots WHERE owner_id = @owner ORDER BY bought_at", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                owned.Add((reader.GetInt32(0), reader.GetInt32(1), (Biome)reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var counts = new Dictionary<(int, int), (int Loading, int Receiving)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT o.x, o.y, o.kind, count(*) FROM world.plot_objects o JOIN world.plots p ON p.x = o.x AND p.y = o.y
            WHERE p.owner_id = @owner AND o.kind = ANY(@kinds) GROUP BY o.x, o.y, o.kind
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            command.Parameters.AddWithValue("kinds", new[]
            {
                Items.LoadingChest, Items.LoadingChest2, Items.LoadingChest3, Items.LoadingChest4,
                Items.ReceivingChest, Items.ReceivingChest2, Items.ReceivingChest3, Items.ReceivingChest4,
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var at = (reader.GetInt32(0), reader.GetInt32(1));
                var (loading, receiving) = counts.GetValueOrDefault(at);
                var n = (int)reader.GetInt64(3);
                counts[at] = Items.IsLoadingChest(reader.GetString(2)) ? (loading + n, receiving) : (loading, receiving + n);
            }
        }

        var perks = await stations.PerksAsync(player, false, cancellationToken);
        var plotList = new List<FreightPlot>();
        foreach (var (x, y, biome, name) in owned)
        {
            var (loading, receiving) = counts.GetValueOrDefault((x, y));
            plotList.Add(new FreightPlot(x, y, biome, name, loading, receiving, await cities.RailFactorAsync(x, y, cancellationToken) * perks));
        }

        return new FreightState(routes, plotList, time.GetUtcNow());
    }
}

public sealed record FreightCargoRequest(string Item, long Quantity);

public sealed record FreightAddRequest(int FromX, int FromY, int ToX, int ToY, List<FreightCargoRequest>? Cargo, bool Smuggle = false);

public sealed record FreightRemoveRequest(long Id);

public abstract class FreightHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var s = await RunAsync(scope.ServiceProvider.GetRequiredService<FreightService>(), client.RequireAccount(), payload, cancellationToken);
        string Name(string item) => Items.Names.GetValueOrDefault(item, item);
        var nameOf = s.Plots.ToDictionary(p => (p.X, p.Y), p => p.Name);
        object Place(int x, int y) => new { x, y, name = nameOf.GetValueOrDefault((x, y)) };
        return new Reply("freight", new
        {
            routes = s.Routes.Select(r => new
            {
                id = r.Id,
                from = Place(r.FromX, r.FromY),
                to = Place(r.ToX, r.ToY),
                cargo = r.Cargo.Select(c => new { id = c.Item, name = Name(c.Item), quantity = c.Quantity, stock = c.Stock }),
                state = r.State,
                nextAt = r.NextAt.ToUnixTimeMilliseconds(),
                carried = r.Carried.Select(c => new { id = c.Key, name = Name(c.Key), quantity = c.Value }),
                trips = r.Trips,
                delivered = r.Delivered,
                lastFeeCents = r.LastFeeCents,
                problem = r.Problem,
                stopping = r.Stopping,
                smuggle = r.Smuggle,
                km = Math.Round(r.Km),
                tripSeconds = (long)r.Trip.TotalSeconds,
                // What one full train costs now.
                feeCents = r.FeeCents,
            }),
            plots = s.Plots.Select(p => new
            {
                x = p.X,
                y = p.Y,
                biome = (int)p.Biome,
                name = p.Name,
                loading = p.Loading,
                receiving = p.Receiving,
                // What rail fees from this plot are multiplied by: less from a city and with the company's big projects.
                feeFactor = p.FeeFactor,
            }),
            rates = new
            {
                baseFeeCents = RailRules.BaseFeeCents,
                centsPerItemPer100Km = RailRules.CentsPerItemPer100Km,
                secondsPer100Km = RailRules.SecondsPer100Km,
                minTripSeconds = RailRules.MinTrip.TotalSeconds,
                maxRoutes = FreightRules.MaxRoutes,
                maxItems = FreightRules.MaxItems,
                maxQuantity = FreightRules.MaxQuantity,
                checkSeconds = FreightRules.CheckEvery.TotalSeconds,
            },
            serverTime = s.Now.ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<FreightState> RunAsync(FreightService freight, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>The player's freight routes between their plots, and their plots with the freight chests on them.</summary>
public sealed class GetFreightHandler(IServiceScopeFactory scopes) : FreightHandler(scopes)
{
    public override string Type => "freight.get";

    protected override Task<FreightState> RunAsync(FreightService freight, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        freight.StateAsync(player, cancellationToken);
}

public sealed class AddFreightHandler(IServiceScopeFactory scopes) : FreightHandler(scopes)
{
    public override string Type => "freight.add";

    protected override Task<FreightState> RunAsync(FreightService freight, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<FreightAddRequest>(payload);
        return freight.AddAsync(player, r.FromX, r.FromY, r.ToX, r.ToY, (r.Cargo ?? []).Select(c => (c.Item, c.Quantity)).ToList(), cancellationToken, r.Smuggle);
    }
}

public sealed class RemoveFreightHandler(IServiceScopeFactory scopes) : FreightHandler(scopes)
{
    public override string Type => "freight.remove";

    protected override Task<FreightState> RunAsync(FreightService freight, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        freight.RemoveAsync(player, Payload.Read<FreightRemoveRequest>(payload).Id, cancellationToken);
}
