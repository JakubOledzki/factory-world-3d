using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

public sealed class ExportService(
    GameDb db, LandMasses land, Towns towns, PlotService plots, IInventory inventory, IWallet wallet, ICharacterNames names,
    IEnumerable<IPlotAutomation> automation, IExportMarket market, TimeProvider time) : IPlacedHook, IMachineContents
{
    // --- The chest ----------------------------------------------------------------------------------

    /// <summary>The export chest of one of the player's plots, after sending its train if it is due and listing what has arrived.</summary>
    public Task<ExportChestState> StateAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Work, _ => Task.CompletedTask, cancellationToken);

    /// <summary>Moves items from the inventory into the chest.</summary>
    public Task<ExportChestState> LoadAsync(Guid player, int x, int y, string item, long quantity, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Work, async _ =>
        {
            CheckQuantity(item, quantity);
            var stock = await ReadStockAsync(x, y, cancellationToken);
            if (Room(stock, item) < quantity)
            {
                throw new GameException("chest_full", $"The export chest holds {ExportRules.Capacity} pieces of {ExportRules.MaxKinds} kinds at most.");
            }

            await inventory.RemoveAsync(player, item, quantity, cancellationToken);
            await ChangeStockAsync(x, y, item, quantity, cancellationToken);
        }, cancellationToken);

    /// <summary>Moves items from the chest into the inventory, as many as it has room for; the rest stays.</summary>
    public Task<ExportChestState> UnloadAsync(Guid player, int x, int y, string item, long quantity, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Take, async _ =>
        {
            CheckQuantity(item, quantity);
            var taken = await inventory.FitAsync(player, item, quantity, cancellationToken);
            await ChangeStockAsync(x, y, item, -taken, cancellationToken);
            await inventory.AddAsync(player, item, taken, cancellationToken);
        }, cancellationToken);

    /// <summary>Sets what a piece of <paramref name="item"/> is listed for; 0 takes the price off, and the train leaves the item behind.</summary>
    public Task<ExportChestState> PriceAsync(Guid player, int x, int y, string item, long priceCents, CancellationToken cancellationToken) =>
        ChangeAsync(player, x, y, PlotRight.Routes, async _ =>
        {
            if (!Items.Exists(item ?? ""))
            {
                throw new GameException("unknown_item", "Unknown item.");
            }

            if (priceCents is < 0 or > 1_000_000_00)
            {
                throw new GameException("bad_price", "The price must be 0.01-1000000 $.");
            }

            await using var command = await db.CommandAsync(
                priceCents == 0
                    ? "DELETE FROM world.export_prices WHERE x = @x AND y = @y AND item = @item"
                    : """
                      INSERT INTO world.export_prices (x, y, item, price_cents) VALUES (@x, @y, @item, @price)
                      ON CONFLICT (x, y, item) DO UPDATE SET price_cents = excluded.price_cents
                      """, cancellationToken);
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("item", item!);
            command.Parameters.AddWithValue("price", priceCents);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private async Task<ExportChestState> ChangeAsync(
        Guid player, int x, int y, PlotRight right, Func<Guid, Task> change, CancellationToken cancellationToken)
    {
        await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
        var chest = await ChestAsync(x, y, cancellationToken) ?? throw new GameException("no_export_chest", "There is no export chest on this plot.");
        await DepartAsync(chest, cancellationToken);
        await ArriveAsync(cancellationToken);
        return await db.InTransactionAsync(async () =>
        {
            await CatchUpPlotAsync(x, y, cancellationToken);
            await LockAsync(x, y, cancellationToken);
            await change(chest);
            return await ReadAsync(chest, x, y, cancellationToken);
        }, cancellationToken);
    }

    private async Task<ExportChestState> ReadAsync(Guid chest, int x, int y, CancellationToken cancellationToken)
    {
        var stock = await ReadStockAsync(x, y, cancellationToken);
        var prices = await ReadPricesAsync(x, y, cancellationToken);
        DateTimeOffset next;
        DateTimeOffset? last;
        long quantity;
        string? problem;
        await using (var command = await db.CommandAsync(
            "SELECT next_pickup_at, last_pickup_at, last_quantity, last_problem FROM world.export_chests WHERE object_id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", chest);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (next, last, quantity, problem) = (reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        var shipments = new List<ExportShipment>();
        await using (var command = await db.CommandAsync(
            """
            SELECT id, item, quantity, price_cents, market_x, market_y, arrives_at FROM world.export_shipments
            WHERE from_x = @x AND from_y = @y ORDER BY arrives_at, id LIMIT 50
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                shipments.Add(new ExportShipment(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        var seller = await StationStock.OwnerAsync(db, x, y, cancellationToken) ?? Guid.Empty;
        var posts = await PostsAsync(cancellationToken);
        var route = Route(posts, x, y);
        var nameOf = route is null ? new Dictionary<Guid, string>()
            : await names.NamesAsync(route.Ports.Select(p => p.Owner).Append(route.Market.Owner).Distinct().ToList(), cancellationToken);
        return new ExportChestState(chest, x, y, stock, prices, next, last, quantity, problem, route,
            route is null ? 0 : PortFees(route, seller), shipments, nameOf, time.GetUtcNow(), posts.Count(p => p.Kind == Items.BuyMarket));
    }

    /// <summary>The way a train from the plot takes now, or null when no buying market can be reached.</summary>
    private ExportRoute? Route(IReadOnlyList<ExportPost> posts, int x, int y) =>
        ExportRouter.Find(land, x, y, posts.Where(p => p.Kind == Items.BuyMarket).ToList(), posts.Where(p => p.Kind == Items.RailPort).ToList());

    /// <summary>What the ports of the route charge a train of <paramref name="seller"/>'s: nothing at their own.</summary>
    private static long PortFees(ExportRoute route, Guid seller) => route.Ports.Where(p => p.Owner != seller).Sum(p => p.Value);

    // --- Trains -------------------------------------------------------------------------------------

    /// <summary>Sends the trains of every chest that is due and lists the goods of every train that has arrived; the background
    /// <see cref="ExportDispatcher"/> calls this every moment. Returns how many chests and trains it handled.</summary>
    public async Task<int> RunDueAsync(int limit, CancellationToken cancellationToken)
    {
        var due = new List<Guid>();
        await using (var command = await db.CommandAsync(
            "SELECT object_id FROM world.export_chests WHERE next_pickup_at <= @now ORDER BY next_pickup_at LIMIT @limit", cancellationToken))
        {
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            command.Parameters.AddWithValue("limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add(reader.GetGuid(0));
            }
        }

        foreach (var chest in due)
        {
            await DepartAsync(chest, cancellationToken);
        }

        return due.Count + await ArriveAsync(cancellationToken);
    }

    /// <summary>
    /// The train at one chest, at the latest full hour it was due: it takes every priced item to the nearest buying market, for
    /// the fees of the ports on the way and the market's commission, paid at once. Without goods, prices, a market or money it
    /// takes nothing. Runs in a transaction of its own.
    /// </summary>
    private Task DepartAsync(Guid chest, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            int x, y;
            await using (var find = await db.CommandAsync("SELECT x, y FROM world.export_chests WHERE object_id = @id", cancellationToken))
            {
                find.Parameters.AddWithValue("id", chest);
                await using var reader = await find.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return;
                }

                (x, y) = (reader.GetInt32(0), reader.GetInt32(1));
            }

            // Inserters may have filled the chest up to now.
            await CatchUpPlotAsync(x, y, cancellationToken);
            await LockAsync(x, y, cancellationToken);
            var now = time.GetUtcNow();
            DateTimeOffset next;
            await using (var command = await db.CommandAsync(
                "SELECT next_pickup_at FROM world.export_chests WHERE object_id = @id AND next_pickup_at <= @now FOR UPDATE", cancellationToken))
            {
                command.Parameters.AddWithValue("id", chest);
                command.Parameters.AddWithValue("now", now);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return;
                }

                next = reader.GetFieldValue<DateTimeOffset>(0);
            }

            // Trains missed while nobody was around come as one, at the last full hour.
            var departure = next + ExportRules.Every * Math.Floor((now - next) / ExportRules.Every);
            var stock = await ReadStockAsync(x, y, cancellationToken);
            var prices = await ReadPricesAsync(x, y, cancellationToken);
            var cargo = stock.Where(s => prices.ContainsKey(s.Key)).Select(s => (Item: s.Key, Quantity: s.Value, Price: prices[s.Key])).ToList();
            var seller = await StationStock.OwnerAsync(db, x, y, cancellationToken);
            string? problem = null;
            ExportRoute? route = null;
            if (seller is null)
            {
                problem = "no_owner";
            }
            else if (stock.Count == 0)
            {
                problem = "empty";
            }
            else if (cargo.Count == 0)
            {
                problem = "no_price";
            }
            else if ((route = Route(await PostsAsync(cancellationToken), x, y)) is null)
            {
                problem = (await PostsAsync(cancellationToken)).Any(p => p.Kind == Items.BuyMarket) ? "no_route" : "no_market";
            }

            long sent = 0;
            if (problem is null)
            {
                var owner = seller!.Value;
                var commission = route!.Market.Owner == owner ? 0
                    : ExportRules.Commission(cargo.Sum(c => c.Quantity * c.Price), (int)route.Market.Value);
                var fees = route.Ports.Where(p => p.Owner != owner).ToList();
                var total = commission + fees.Sum(p => p.Value);
                if (await wallet.BalanceAsync(owner, cancellationToken) < total)
                {
                    problem = "no_money";
                }
                else
                {
                    if (total > 0)
                    {
                        await wallet.DebitAsync(owner, total, $"export {cargo.Sum(c => c.Quantity)} items {x}:{y}->{route.Market.X}:{route.Market.Y}", cancellationToken);
                    }

                    foreach (var port in fees.Where(p => p.Value > 0))
                    {
                        await EarnAsync(port, port.Value, $"rail port fee {x}:{y}", cancellationToken);
                    }

                    if (commission > 0)
                    {
                        await EarnAsync(route.Market, commission, $"buying market commission {x}:{y}", cancellationToken);
                    }

                    foreach (var (item, quantity, price) in cargo)
                    {
                        await ChangeStockAsync(x, y, item, -quantity, cancellationToken);
                        await using var insert = await db.CommandAsync(
                            """
                            INSERT INTO world.export_shipments (seller_id, from_x, from_y, market_x, market_y, item, quantity, price_cents, arrives_at)
                            VALUES (@seller, @fx, @fy, @mx, @my, @item, @quantity, @price, @at)
                            """, cancellationToken);
                        insert.Parameters.AddWithValue("seller", owner);
                        insert.Parameters.AddWithValue("fx", x);
                        insert.Parameters.AddWithValue("fy", y);
                        insert.Parameters.AddWithValue("mx", route.Market.X);
                        insert.Parameters.AddWithValue("my", route.Market.Y);
                        insert.Parameters.AddWithValue("item", item);
                        insert.Parameters.AddWithValue("quantity", quantity);
                        insert.Parameters.AddWithValue("price", price);
                        insert.Parameters.AddWithValue("at", departure + route.Trip);
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                        sent += quantity;
                    }
                }
            }

            await using var update = await db.CommandAsync(
                """
                UPDATE world.export_chests SET next_pickup_at = @next, last_pickup_at = @at, last_quantity = @sent, last_problem = @problem
                WHERE object_id = @id
                """, cancellationToken);
            update.Parameters.AddWithValue("id", chest);
            update.Parameters.AddWithValue("next", departure + ExportRules.Every);
            update.Parameters.AddWithValue("at", departure);
            update.Parameters.AddWithValue("sent", sent);
            update.Parameters.AddWithValue("problem", (object?)problem ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Lists the goods of every train that has got to its market; returns how many there were.</summary>
    private Task<int> ArriveAsync(CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            // DELETE ... RETURNING hands each arrived train to exactly one request.
            await using var command = await db.CommandAsync(
                "DELETE FROM world.export_shipments WHERE arrives_at <= @now RETURNING seller_id, item, quantity, price_cents", cancellationToken);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            var arrived = new List<(Guid Seller, string Item, long Quantity, long Price)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    arrived.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
                }
            }

            foreach (var (seller, item, quantity, price) in arrived)
            {
                await market.ListAsync(seller, item, quantity, price, cancellationToken);
            }

            return arrived.Count;
        }, cancellationToken);

    private async Task EarnAsync(ExportPost post, long cents, string reason, CancellationToken cancellationToken)
    {
        await wallet.CreditAsync(post.Owner, cents, reason, cancellationToken);
        await using var update = await db.CommandAsync(
            "UPDATE world.export_posts SET earned_cents = earned_cents + @cents, uses = uses + 1 WHERE object_id = @id", cancellationToken);
        update.Parameters.AddWithValue("id", post.Id);
        update.Parameters.AddWithValue("cents", cents);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Markets and ports --------------------------------------------------------------------------

    /// <summary>Every buying market and rail port in the world, with the owners of their plots.</summary>
    private async Task<IReadOnlyList<ExportPost>> PostsAsync(CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT p.object_id, p.kind, p.x, p.y, l.owner_id, p.value FROM world.export_posts p
            JOIN world.plots l ON l.x = p.x AND l.y = p.y WHERE l.owner_id IS NOT NULL ORDER BY p.object_id
            """, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var posts = new List<ExportPost>();
        while (await reader.ReadAsync(cancellationToken))
        {
            posts.Add(new ExportPost(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetGuid(4), reader.GetInt64(5)));
        }

        return posts;
    }

    /// <summary>A buying market or a rail port: anyone may look, its plot's owner sees what it earned and sets its commission or fee.</summary>
    public async Task<object> PostAsync(Guid player, Guid id, CancellationToken cancellationToken)
    {
        var (post, earned, uses) = await FindPostAsync(id, cancellationToken);
        var mine = post.Owner == player;
        var town = post.Kind == Items.BuyMarket ? towns.Of(post.X, post.Y).Name : null;
        return new
        {
            id = post.Id,
            kind = post.Kind,
            name = Items.Names[post.Kind],
            x = post.X,
            y = post.Y,
            town,
            owner = (await names.NamesAsync([post.Owner], cancellationToken)).GetValueOrDefault(post.Owner, "?"),
            mine,
            // A market's commission in percent, a port's fee in cents.
            value = post.Value,
            maxValue = post.Kind == Items.BuyMarket ? ExportRules.MaxCommissionPercent : ExportRules.MaxPortFeeCents,
            earnedCents = mine ? earned : (long?)null,
            uses = mine ? uses : (long?)null,
            markets = (await PostsAsync(cancellationToken)).Count(p => p.Kind == Items.BuyMarket),
        };
    }

    public async Task<object> SetPostAsync(Guid player, Guid id, long value, CancellationToken cancellationToken)
    {
        var (post, _, _) = await FindPostAsync(id, cancellationToken);
        await plots.RequireOwnerAsync(player, post.X, post.Y, cancellationToken, PlotRight.Owner);
        var most = post.Kind == Items.BuyMarket ? ExportRules.MaxCommissionPercent : ExportRules.MaxPortFeeCents;
        if (value < 0 || value > most)
        {
            throw post.Kind == Items.BuyMarket
                ? new GameException("bad_commission", $"The commission is 0-{ExportRules.MaxCommissionPercent}%.")
                : new GameException("bad_fee", $"The fee is 0-{ExportRules.MaxPortFeeCents / 100} $.");
        }

        await using (var update = await db.CommandAsync("UPDATE world.export_posts SET value = @value WHERE object_id = @id", cancellationToken))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("value", value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return await PostAsync(player, id, cancellationToken);
    }

    private async Task<(ExportPost Post, long Earned, long Uses)> FindPostAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT p.kind, p.x, p.y, l.owner_id, p.value, p.earned_cents, p.uses FROM world.export_posts p
            JOIN world.plots l ON l.x = p.x AND l.y = p.y WHERE p.object_id = @id AND l.owner_id IS NOT NULL
            """, cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new GameException("no_post", "There is no such buying market or rail port.");
        }

        return (new ExportPost(id, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetGuid(3), reader.GetInt64(4)),
            reader.GetInt64(5), reader.GetInt64(6));
    }

    // --- Placing and picking up ---------------------------------------------------------------------

    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (placed.Kind == Items.ExportChest)
        {
            await using var insert = await db.CommandAsync(
                "INSERT INTO world.export_chests (object_id, x, y, next_pickup_at, last_quantity) VALUES (@id, @x, @y, @next, 0)", cancellationToken);
            insert.Parameters.AddWithValue("id", placed.Id);
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("next", ExportRules.NextPickup(time.GetUtcNow()));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (placed.Kind is Items.BuyMarket or Items.RailPort)
        {
            await using var insert = await db.CommandAsync(
                "INSERT INTO world.export_posts (object_id, kind, x, y, value, earned_cents, uses) VALUES (@id, @kind, @x, @y, @value, 0, 0)",
                cancellationToken);
            insert.Parameters.AddWithValue("id", placed.Id);
            insert.Parameters.AddWithValue("kind", placed.Kind);
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("value", placed.Kind == Items.BuyMarket ? ExportRules.DefaultCommissionPercent : ExportRules.DefaultPortFeeCents);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>A chest picked up gives back what it holds; trains on their way still get there.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using (var delete = await db.CommandAsync("DELETE FROM world.export_posts WHERE object_id = @id", cancellationToken))
        {
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        int x, y;
        await using (var delete = await db.CommandAsync("DELETE FROM world.export_chests WHERE object_id = @id RETURNING x, y", cancellationToken))
        {
            delete.Parameters.AddWithValue("id", id);
            await using var reader = await delete.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            (x, y) = (reader.GetInt32(0), reader.GetInt32(1));
        }

        await LockAsync(x, y, cancellationToken);
        foreach (var (item, quantity) in await ReadStockAsync(x, y, cancellationToken))
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }

        await using var empty = await db.CommandAsync("DELETE FROM world.export_items WHERE x = @x AND y = @y", cancellationToken);
        empty.Parameters.AddWithValue("x", x);
        empty.Parameters.AddWithValue("y", y);
        await empty.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- Stock --------------------------------------------------------------------------------------

    private async Task<Guid?> ChestAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT object_id FROM world.export_chests WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private async Task CatchUpPlotAsync(int x, int y, CancellationToken cancellationToken)
    {
        foreach (var machinery in automation)
        {
            await machinery.CatchUpAsync(x, y, cancellationToken);
        }
    }

    /// <summary>Locks one plot's export chest until the transaction ends. Take the plot's logistics first, then this.</summary>
    internal static async Task LockAsync(GameDb db, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken);
        command.Parameters.AddWithValue("key", ((long)x << 32 | (uint)y) ^ 0x6578_706f_7274_2121);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task LockAsync(int x, int y, CancellationToken cancellationToken) => LockAsync(db, x, y, cancellationToken);

    internal static async Task<Dictionary<string, long>> ReadStockAsync(GameDb db, int x, int y, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, long>();
        await using var command = await db.CommandAsync(
            "SELECT item, quantity FROM world.export_items WHERE x = @x AND y = @y ORDER BY item", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items[reader.GetString(0)] = reader.GetInt64(1);
        }

        return items;
    }

    private Task<Dictionary<string, long>> ReadStockAsync(int x, int y, CancellationToken cancellationToken) => ReadStockAsync(db, x, y, cancellationToken);

    private async Task<Dictionary<string, long>> ReadPricesAsync(int x, int y, CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, long>();
        await using var command = await db.CommandAsync(
            "SELECT item, price_cents FROM world.export_prices WHERE x = @x AND y = @y ORDER BY item", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices[reader.GetString(0)] = reader.GetInt64(1);
        }

        return prices;
    }

    internal static async Task ChangeStockAsync(GameDb db, int x, int y, string item, long change, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.export_items (x, y, item, quantity) VALUES (@x, @y, @item, @change)
            ON CONFLICT (x, y, item) DO UPDATE SET quantity = export_items.quantity + excluded.quantity
            RETURNING quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("change", change);
        var left = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (left < 0)
        {
            throw new GameException("not_enough_items", "The export chest doesn't have that many.");
        }

        if (left == 0)
        {
            await using var delete = await db.CommandAsync(
                "DELETE FROM world.export_items WHERE x = @x AND y = @y AND item = @item", cancellationToken);
            delete.Parameters.AddWithValue("x", x);
            delete.Parameters.AddWithValue("y", y);
            delete.Parameters.AddWithValue("item", item);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private Task ChangeStockAsync(int x, int y, string item, long change, CancellationToken cancellationToken) =>
        ChangeStockAsync(db, x, y, item, change, cancellationToken);

    /// <summary>How many more pieces of <paramref name="item"/> fit in a chest holding <paramref name="stock"/>.</summary>
    public static long Room(IReadOnlyDictionary<string, long> stock, string item)
    {
        if (Items.IsFluid(item) || (!stock.ContainsKey(item) && stock.Count >= ExportRules.MaxKinds))
        {
            return 0;
        }

        return Math.Max(0, ExportRules.Capacity - stock.Values.Sum());
    }

    private static void CheckQuantity(string item, long quantity)
    {
        if (!Items.Exists(item ?? ""))
        {
            throw new GameException("unknown_item", "Unknown item.");
        }

        if (quantity is < 1 or > ExportRules.Capacity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{ExportRules.Capacity}.");
        }
    }
}
