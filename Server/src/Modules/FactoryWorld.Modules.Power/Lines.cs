using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Power;

/// <summary>
/// Cables between plots. A player puts a grid tie on each plot and lays a cable from the tie on the plot that gets the power
/// to the tie on the plot that gives it, paying for every tile of its length. Between two plots of one owner the power is free;
/// from another player's plot it flows only while paid for, by the hour, at the price per kWh the supplier set on their tie, and
/// the money goes to the supplier at once. Cables never make a loop.
/// </summary>
public static class PowerLines
{
    /// <summary>What a cable costs to lay, for each tile of its length.</summary>
    public const long CentsPerTile = 10_000_00;

    /// <summary>How many tiles across a plot is (as <c>PlotLayout.Size</c> in the World module).</summary>
    public const int PlotTiles = 250;

    public const double MinKw = 10;

    public const double MaxKw = 5000;

    public const int MaxHours = 168;

    /// <summary>The advisory lock that lays cables one at a time.</summary>
    public const long BuildLock = 0x506F7765724C696E;

    /// <summary>The highest price per kWh a supplier may ask.</summary>
    public const long MaxPriceCents = 100_000;

    /// <summary>How far apart two ties are, in tiles, across the plots they stand on.</summary>
    public static double Distance(int x, int y, PlotObject a, int toX, int toY, PlotObject b)
    {
        double dx = (toX - x) * PlotTiles + b.TileX - a.TileX;
        double dy = (toY - y) * PlotTiles + b.TileY - a.TileY;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>What laying a cable of <paramref name="tiles"/> costs.</summary>
    public static long CableCents(double tiles) => (long)Math.Ceiling(tiles) * CentsPerTile;

    /// <summary>What <paramref name="hours"/> of a cable of <paramref name="kw"/> cost at <paramref name="priceCents"/> a kWh.</summary>
    public static long PowerCents(double kw, int hours, long priceCents) => (long)Math.Ceiling(kw * hours * priceCents);
}

/// <summary>A grid tie and its cables, as its window shows them.</summary>
public sealed record TieReply(int X, int Y, Guid Id, long PriceCents, bool Open, IReadOnlyList<LineView> Lines, DateTimeOffset Now, long? BalanceCents = null);

/// <param name="Supplying">Whether this tie gives the power along it.</param>
/// <param name="FlowKw">What it carries now.</param>
public sealed record LineView(Guid Id, bool Supplying, int OtherX, int OtherY, Guid OtherTie, string? OtherOwner, double Kw, DateTimeOffset PaidUntil,
    long PriceCents, bool Free, double FlowKw);

/// <summary>A tie that sells power to anyone, for players looking for a supplier.</summary>
public sealed record TieOffer(int X, int Y, Guid Id, string? Owner, long PriceCents, long CableCents, bool Mine);

public sealed class PowerLineService(
    GameDb db, PowerService power, IPlotObjects plots, IPlotRegistry registry, IWallet wallet, ICharacterNames names, TimeProvider time,
    IServiceProvider services)
{
    public Task<TieReply> GetAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await TieAsync(x, y, id, cancellationToken);
            // A tie starts closed and free, and once known it shows among its owner's other plots to lay a cable from.
            await using (var create = await db.CommandAsync(
                "INSERT INTO power.ties (id, x, y, price_cents, open) VALUES (@id, @x, @y, 0, false) ON CONFLICT (id) DO NOTHING", cancellationToken))
            {
                create.Parameters.AddWithValue("id", id);
                create.Parameters.AddWithValue("x", x);
                create.Parameters.AddWithValue("y", y);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await CatchUpAsync(x, y, cancellationToken);
            return await ReplyAsync(x, y, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Sets what the tie's owner asks for a kWh, and whether other players may lay cables to it.</summary>
    public Task<TieReply> PriceAsync(Guid player, int x, int y, Guid id, long priceCents, bool open, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (priceCents is < 0 or > PowerLines.MaxPriceCents)
            {
                throw new GameException("bad_power_price", $"A kWh costs 0 to {PowerLines.MaxPriceCents} cents.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Owner);
            await TieAsync(x, y, id, cancellationToken);
            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO power.ties (id, x, y, price_cents, open) VALUES (@id, @x, @y, @price, @open)
                ON CONFLICT (id) DO UPDATE SET price_cents = @price, open = @open
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("id", id);
                upsert.Parameters.AddWithValue("x", x);
                upsert.Parameters.AddWithValue("y", y);
                upsert.Parameters.AddWithValue("price", priceCents);
                upsert.Parameters.AddWithValue("open", open);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await CatchUpAsync(x, y, cancellationToken);
            return await ReplyAsync(x, y, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Lays a cable from the player's tie on (x, y), which gets the power, to the supplier's tie, and pays for it.</summary>
    public Task<TieReply> BuildAsync(Guid player, int x, int y, Guid id, int fromX, int fromY, Guid fromId, double kw, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!(kw >= PowerLines.MinKw && kw <= PowerLines.MaxKw))
            {
                throw new GameException("bad_kw", $"A cable carries {PowerLines.MinKw} to {PowerLines.MaxKw} kW.");
            }

            if (x == fromX && y == fromY)
            {
                throw new GameException("same_plot", "A cable joins two different plots; on one plot poles carry the power.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Build);
            var mine = await TieAsync(x, y, id, cancellationToken);
            var theirs = await TieAsync(fromX, fromY, fromId, cancellationToken);
            var receiverOwner = (await registry.FindAsync(x, y, cancellationToken))!.Owner;
            var supplierOwner = (await registry.FindAsync(fromX, fromY, cancellationToken))?.Owner
                ?? throw new GameException("no_object", "There is no such grid tie on that plot.");
            var free = receiverOwner == supplierOwner;
            if (!free && (await OfferAsync(fromId, cancellationToken)) is not { Open: true })
            {
                throw new GameException("tie_closed", "The owner of that grid tie does not sell power.");
            }

            // Cables are laid one at a time across the server, so two built at once between the same plots
            // cannot each miss the other and close a loop.
            await using (var serial = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                serial.Parameters.AddWithValue("key", PowerLines.BuildLock);
                await serial.ExecuteNonQueryAsync(cancellationToken);
            }

            var lines = await power.LinesAroundAsync(x, y, cancellationToken);
            if (lines.Any(l => l.FromTie == fromId && l.ToTie == id))
            {
                throw new GameException("line_exists", "A cable already joins these two grid ties.");
            }

            // Power may not come back round to where it started: follow where this plot already sends power.
            if (SendsTo(lines, x, y, fromX, fromY))
            {
                throw new GameException("line_loop", "That plot already gets power from this one; a cable back would make a loop.");
            }

            var cost = PowerLines.CableCents(PowerLines.Distance(x, y, mine, fromX, fromY, theirs));
            var balance = await wallet.DebitAsync(player, cost, $"power cable {fromX}:{fromY} to {x}:{y}", cancellationToken);

            var now = time.GetUtcNow();
            await CatchUpAsync(fromX, fromY, cancellationToken);
            await CatchUpAsync(x, y, cancellationToken);
            var supplier = await power.OpenAsync(fromX, fromY, cancellationToken);
            var receiver = await power.OpenAsync(x, y, cancellationToken);
            supplier.Settle(now);
            receiver.Settle(now);
            var row = new PowerLineRow
            {
                Id = Guid.NewGuid(), FromX = fromX, FromY = fromY, FromTie = fromId, ToX = x, ToY = y, ToTie = id, Kw = kw,
                PaidUntil = free ? PowerLink.Forever : now, BuiltAt = now,
            };
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO power.lines (id, from_x, from_y, from_tie, to_x, to_y, to_tie, kw, paid_until, built_at)
                VALUES (@id, @fromX, @fromY, @fromTie, @toX, @toY, @toTie, @kw, @paid, @built)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("id", row.Id);
                insert.Parameters.AddWithValue("fromX", fromX);
                insert.Parameters.AddWithValue("fromY", fromY);
                insert.Parameters.AddWithValue("fromTie", fromId);
                insert.Parameters.AddWithValue("toX", x);
                insert.Parameters.AddWithValue("toY", y);
                insert.Parameters.AddWithValue("toTie", id);
                insert.Parameters.AddWithValue("kw", kw);
                insert.Parameters.AddWithValue("paid", row.PaidUntil);
                insert.Parameters.AddWithValue("built", now);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            power.Link(row);
            supplier.Replan();
            receiver.Replan();
            await supplier.SaveAsync(cancellationToken);
            await receiver.SaveAsync(cancellationToken);
            return await ReplyAsync(x, y, id, cancellationToken) with { BalanceCents = balance };
        }, cancellationToken);

    /// <summary>Pays the supplier for <paramref name="hours"/> more of power along a cable to one of the player's plots.</summary>
    public Task<TieReply> BuyAsync(Guid player, Guid lineId, int hours, long? expectedPriceCents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (hours is < 1 or > PowerLines.MaxHours)
            {
                throw new GameException("bad_hours", $"Power is bought for 1 to {PowerLines.MaxHours} hours.");
            }

            var line = await LineAsync(lineId, cancellationToken);
            await plots.RequireOwnerAsync(player, line.ToX, line.ToY, cancellationToken, PlotRight.Build);
            var now = time.GetUtcNow();
            if (line.PaidUntil == PowerLink.Forever)
            {
                throw new GameException("line_free", "Power between your own plots is free.");
            }

            var supplierOwner = (await registry.FindAsync(line.FromX, line.FromY, cancellationToken))!.Owner;
            if (await OfferAsync(line.FromTie, cancellationToken) is not { Open: true } offer)
            {
                throw new GameException("tie_closed", "The owner of that grid tie does not sell power.");
            }

            // The buyer pays the price they saw, or nothing: the seller may have changed it since.
            var price = offer.PriceCents;
            if (expectedPriceCents is { } expected && expected != price)
            {
                throw new GameException("price_changed", "The seller changed the price of power; look at it again.");
            }

            var cents = PowerLines.PowerCents(line.Kw, hours, price);
            long? balance = null;
            if (cents > 0)
            {
                balance = await wallet.DebitAsync(player, cents, $"power {line.FromX}:{line.FromY} to {line.ToX}:{line.ToY}", cancellationToken);
                await wallet.CreditAsync(supplierOwner, cents, $"power sold to {line.ToX}:{line.ToY}", cancellationToken);
            }

            await CatchUpAsync(line.FromX, line.FromY, cancellationToken);
            await CatchUpAsync(line.ToX, line.ToY, cancellationToken);
            var supplier = await power.OpenAsync(line.FromX, line.FromY, cancellationToken);
            var receiver = await power.OpenAsync(line.ToX, line.ToY, cancellationToken);
            supplier.Settle(now);
            receiver.Settle(now);
            var link = power.Link(line);
            link.Until = (line.PaidUntil > now ? line.PaidUntil : now) + TimeSpan.FromHours(hours);
            await using (var update = await db.CommandAsync("UPDATE power.lines SET paid_until = @paid WHERE id = @id", cancellationToken))
            {
                update.Parameters.AddWithValue("id", lineId);
                update.Parameters.AddWithValue("paid", link.Until);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            supplier.Replan();
            receiver.Replan();
            await supplier.SaveAsync(cancellationToken);
            await receiver.SaveAsync(cancellationToken);
            return await ReplyAsync(line.ToX, line.ToY, line.ToTie, cancellationToken) with { BalanceCents = balance };
        }, cancellationToken);

    /// <summary>Takes a cable down, from either end; what was paid for it is lost.</summary>
    public Task<TieReply> RemoveAsync(Guid player, int x, int y, Guid id, Guid lineId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var line = await LineAsync(lineId, cancellationToken);
            if (!((line.FromX == x && line.FromY == y && line.FromTie == id) || (line.ToX == x && line.ToY == y && line.ToTie == id)))
            {
                throw new GameException("no_line", "There is no such cable at this grid tie.");
            }

            if (line.FromTie == id && line.PaidUntil != PowerLink.Forever && line.PaidUntil > time.GetUtcNow())
            {
                throw new GameException("line_paid", "The other player paid for power along this cable; it stays until that time runs out.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Build);
            await CatchUpAsync(line.FromX, line.FromY, cancellationToken);
            await CatchUpAsync(line.ToX, line.ToY, cancellationToken);
            await power.OpenAsync(x, y, cancellationToken);
            await power.UnlinkAsync(lineId, time.GetUtcNow(), cancellationToken);
            await using (var delete = await db.CommandAsync("DELETE FROM power.lines WHERE id = @id", cancellationToken))
            {
                delete.Parameters.AddWithValue("id", lineId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReplyAsync(x, y, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Grid ties that sell power and the player's own ties on other plots, nearest first, with what a cable to each would cost.</summary>
    public async Task<IReadOnlyList<TieOffer>> OffersAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken)
    {
        await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
        var mine = await TieAsync(x, y, id, cancellationToken);
        var found = new List<(Guid Id, int X, int Y, long Price, bool Open)>();
        await using (var command = await db.CommandAsync(
            "SELECT id, x, y, price_cents, open FROM power.ties WHERE NOT (x = @x AND y = @y) ORDER BY (x - @x) * (x - @x) + (y - @y) * (y - @y) LIMIT 200",
            cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add((reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetBoolean(4)));
            }
        }

        var offers = new List<(TieOffer Offer, Guid Owner)>();
        var me = (await registry.FindAsync(x, y, cancellationToken))!.Owner;
        foreach (var (tie, tx, ty, price, open) in found)
        {
            if (offers.Count >= 30)
            {
                break;
            }

            if (await registry.FindAsync(tx, ty, cancellationToken) is { } plot && (open || plot.Owner == me)
                && (await plots.ObjectsAsync(tx, ty, cancellationToken)).FirstOrDefault(o => o.Id == tie) is { } theirs)
            {
                var cable = PowerLines.CableCents(PowerLines.Distance(x, y, mine, tx, ty, theirs));
                offers.Add((new TieOffer(tx, ty, tie, null, price, cable, plot.Owner == me), plot.Owner));
            }
        }

        var owners = await names.NamesAsync(offers.Select(o => o.Owner).Distinct().ToList(), cancellationToken);
        return offers.Select(o => o.Offer with { Owner = owners.GetValueOrDefault(o.Owner) }).ToList();
    }

    /// <summary>Whether power already goes, cable by cable, from the plot (x, y) to (toX, toY).</summary>
    private static bool SendsTo(IReadOnlyList<PowerLineRow> lines, int x, int y, int toX, int toY)
    {
        var seen = new HashSet<(int, int)> { (x, y) };
        var queue = new Queue<(int X, int Y)>([(x, y)]);
        while (queue.TryDequeue(out var at))
        {
            if (at == (toX, toY))
            {
                return true;
            }

            foreach (var line in lines.Where(l => l.FromX == at.X && l.FromY == at.Y))
            {
                if (seen.Add((line.ToX, line.ToY)))
                {
                    queue.Enqueue((line.ToX, line.ToY));
                }
            }
        }

        return false;
    }

    /// <summary>Brings the plot's belts, inserters and power up to now, as before anything on it changes.</summary>
    private async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken)
    {
        foreach (var machinery in services.GetServices<IPlotAutomation>())
        {
            await machinery.CatchUpAsync(x, y, cancellationToken);
        }
    }

    private async Task<PlotObject> TieAsync(int x, int y, Guid id, CancellationToken cancellationToken) =>
        (await plots.ObjectsAsync(x, y, cancellationToken)).FirstOrDefault(o => o.Id == id && o.Kind == Items.GridTie)
        ?? throw new GameException("no_object", "There is no such grid tie on that plot.");

    private async Task<PowerTieRow?> OfferAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT x, y, price_cents, open FROM power.ties WHERE id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PowerTieRow { Id = id, X = reader.GetInt32(0), Y = reader.GetInt32(1), PriceCents = reader.GetInt64(2), Open = reader.GetBoolean(3) }
            : null;
    }

    private async Task<PowerLineRow> LineAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT from_x, from_y, from_tie, to_x, to_y, to_tie, kw, paid_until, built_at FROM power.lines WHERE id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new GameException("no_line", "There is no such cable.");
        }

        return new PowerLineRow
        {
            Id = id, FromX = reader.GetInt32(0), FromY = reader.GetInt32(1), FromTie = reader.GetGuid(2), ToX = reader.GetInt32(3), ToY = reader.GetInt32(4),
            ToTie = reader.GetGuid(5), Kw = reader.GetDouble(6), PaidUntil = reader.GetFieldValue<DateTimeOffset>(7), BuiltAt = reader.GetFieldValue<DateTimeOffset>(8),
        };
    }

    private async Task<TieReply> ReplyAsync(int x, int y, Guid id, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await power.OpenAsync(x, y, cancellationToken);
        var offer = await OfferAsync(id, cancellationToken);
        var rows = (await power.LinesOfAsync(x, y, cancellationToken)).Where(l => l.FromTie == id || l.ToTie == id).ToList();
        var others = new Dictionary<Guid, Guid>();
        var prices = new Dictionary<Guid, long>();
        foreach (var row in rows)
        {
            var supplying = row.FromTie == id;
            if (await registry.FindAsync(supplying ? row.ToX : row.FromX, supplying ? row.ToY : row.FromY, cancellationToken) is { } plot)
            {
                others[row.Id] = plot.Owner;
            }

            prices[row.Id] = supplying ? offer?.PriceCents ?? 0 : (await OfferAsync(row.FromTie, cancellationToken))?.PriceCents ?? 0;
        }

        var owners = await names.NamesAsync(others.Values.Distinct().ToList(), cancellationToken);
        var lines = rows.Select(row =>
        {
            var supplying = row.FromTie == id;
            var link = power.Link(row);
            return new LineView(row.Id, supplying, supplying ? row.ToX : row.FromX, supplying ? row.ToY : row.FromY, supplying ? row.ToTie : row.FromTie,
                others.TryGetValue(row.Id, out var owner) ? owners.GetValueOrDefault(owner) : null, row.Kw, link.Until, prices[row.Id],
                link.Until == PowerLink.Forever, PowerGrid.Flow(link, now));
        }).ToList();
        return new TieReply(x, y, id, offer?.PriceCents ?? 0, offer?.Open ?? false, lines, now);
    }
}

public sealed record TieRequest(int X, int Y, Guid Id);

public sealed record TiePriceRequest(int X, int Y, Guid Id, long PriceCents, bool Open);

public sealed record LineBuildRequest(int X, int Y, Guid Id, int FromX, int FromY, Guid FromId, double Kw);

public sealed record LineBuyRequest(Guid Line, int Hours, long? PriceCents = null);

public sealed record LineRemoveRequest(int X, int Y, Guid Id, Guid Line);

public static class TieJson
{
    public static object Tie(TieReply reply) => new
    {
        x = reply.X,
        y = reply.Y,
        id = reply.Id,
        // What the owner asks for a kWh and whether other players may lay cables to the tie.
        priceCents = reply.PriceCents,
        open = reply.Open,
        cableCentsPerTile = PowerLines.CentsPerTile,
        minKw = PowerLines.MinKw,
        maxKw = PowerLines.MaxKw,
        maxHours = PowerLines.MaxHours,
        serverTime = reply.Now.ToUnixTimeMilliseconds(),
        balanceCents = reply.BalanceCents,
        lines = reply.Lines.Select(l => new
        {
            id = l.Id,
            supplying = l.Supplying,
            x = l.OtherX,
            y = l.OtherY,
            tie = l.OtherTie,
            owner = l.OtherOwner,
            kw = l.Kw,
            // Unix milliseconds until when the power is paid for; null between one owner's plots, where it is free.
            paidUntil = l.Free ? (long?)null : l.PaidUntil.ToUnixTimeMilliseconds(),
            priceCents = l.PriceCents,
            flowKw = l.FlowKw,
        }),
    };
}

public abstract class TieHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<PowerLineService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("powertie", TieJson.Tie(reply));
    }

    protected abstract Task<TieReply> RunAsync(PowerLineService lines, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>A grid tie: its price and its cables.</summary>
public sealed class GetTieHandler(IServiceScopeFactory scopes) : TieHandler(scopes)
{
    public override string Type => "powertie.get";

    protected override Task<TieReply> RunAsync(PowerLineService lines, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TieRequest>(payload);
        return lines.GetAsync(player, request.X, request.Y, request.Id, cancellationToken);
    }
}

public sealed class PriceTieHandler(IServiceScopeFactory scopes) : TieHandler(scopes)
{
    public override string Type => "powertie.price";

    protected override Task<TieReply> RunAsync(PowerLineService lines, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TiePriceRequest>(payload);
        return lines.PriceAsync(player, request.X, request.Y, request.Id, request.PriceCents, request.Open, cancellationToken);
    }
}

public sealed class BuildLineHandler(IServiceScopeFactory scopes) : TieHandler(scopes)
{
    public override string Type => "powerline.build";

    protected override Task<TieReply> RunAsync(PowerLineService lines, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LineBuildRequest>(payload);
        return lines.BuildAsync(player, request.X, request.Y, request.Id, request.FromX, request.FromY, request.FromId, request.Kw, cancellationToken);
    }
}

public sealed class BuyLineHandler(IServiceScopeFactory scopes) : TieHandler(scopes)
{
    public override string Type => "powerline.buy";

    protected override Task<TieReply> RunAsync(PowerLineService lines, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LineBuyRequest>(payload);
        return lines.BuyAsync(player, request.Line, request.Hours, request.PriceCents, cancellationToken);
    }
}

public sealed class RemoveLineHandler(IServiceScopeFactory scopes) : TieHandler(scopes)
{
    public override string Type => "powerline.remove";

    protected override Task<TieReply> RunAsync(PowerLineService lines, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LineRemoveRequest>(payload);
        return lines.RemoveAsync(player, request.X, request.Y, request.Id, request.Line, cancellationToken);
    }
}

/// <summary>Grid ties that sell power, near the player's tie.</summary>
public sealed class TieOffersHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "powertie.offers";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TieRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var offers = await scope.ServiceProvider.GetRequiredService<PowerLineService>()
            .OffersAsync(client.RequireAccount(), request.X, request.Y, request.Id, cancellationToken);
        return new Reply("powertie.offers", new
        {
            id = request.Id,
            offers = offers.Select(o => new { x = o.X, y = o.Y, id = o.Id, owner = o.Owner, priceCents = o.PriceCents, cableCents = o.CableCents, mine = o.Mine }),
        });
    }
}
