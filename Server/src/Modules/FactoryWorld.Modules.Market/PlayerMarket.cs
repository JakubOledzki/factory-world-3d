using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Market;

/// <summary>
/// An offer between players: a sell listing (the items wait here, out of the seller's inventory) or a
/// buy order (the money waits here, out of the buyer's wallet). Other players take part of it or all of it.
/// Closed when everything is taken or the owner cancels; what is left goes back to the owner.
/// </summary>
public sealed class Offer
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public required string ItemId { get; set; }

    public long Quantity { get; set; }

    public long QuantityLeft { get; set; }

    public long PriceCents { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class MarketDbContext(DbContextOptions<MarketDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "market";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.SharedTypeEntity<Offer>("Listing", e => Map(e, "listings"));
        model.SharedTypeEntity<Offer>("Order", e => Map(e, "orders"));
    }

    private static void Map(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Offer> e, string table)
    {
        e.ToTable(table, t =>
        {
            t.HasCheckConstraint($"ck_{table}_quantity", "quantity_left >= 0 AND quantity_left <= quantity");
            t.HasCheckConstraint($"ck_{table}_price", "price_cents > 0");
        });
        e.HasKey(o => o.Id);
        e.Property(o => o.Id).HasColumnName("id");
        e.Property(o => o.OwnerId).HasColumnName("owner_id");
        e.Property(o => o.ItemId).HasColumnName("item_id").HasMaxLength(64);
        e.Property(o => o.Quantity).HasColumnName("quantity");
        e.Property(o => o.QuantityLeft).HasColumnName("quantity_left");
        e.Property(o => o.PriceCents).HasColumnName("price_cents");
        e.Property(o => o.CreatedAt).HasColumnName("created_at");
        e.Property(o => o.ClosedAt).HasColumnName("closed_at");
        e.HasIndex(o => new { o.ItemId, o.PriceCents }).HasFilter("closed_at IS NULL");
        // Closed offers stay, so a busy trader has thousands of them: the owner's open ones and the recently
        // closed ones each have an index of their own, and finding them never walks through the old ones.
        e.HasIndex(o => new { o.OwnerId, o.Id }).HasFilter("closed_at IS NULL").HasDatabaseName($"IX_{table}_open_owner_id_id");
        e.HasIndex(o => new { o.OwnerId, o.ClosedAt }).HasFilter("closed_at IS NOT NULL").HasDatabaseName($"IX_{table}_closed_owner_id_closed_at");
    }
}

public static class MarketRules
{
    /// <summary>Open listings, and separately open buy orders, one player may have at once.</summary>
    public const int MaxOpenOffers = 20;

    public const long MaxQuantity = 10_000;

    /// <summary>1 000 000 $ per piece.</summary>
    public const long MaxPriceCents = 100_000_000;

    /// <summary>How many offers of each kind the board of one item shows.</summary>
    public const int BoardSize = 50;

    /// <summary>Closed offers stay in "my offers" this long, so the owner sees what sold.</summary>
    public static readonly TimeSpan ClosedShownFor = TimeSpan.FromDays(1);

    /// <summary>The market keeps this share of every sale between players; the money leaves the game.</summary>
    public const int FeePercent = 3;

    /// <summary>What the seller gets for a sale of <paramref name="cents"/>: the fee is rounded up, so splitting a sale saves nothing.</summary>
    public static long AfterFee(long cents) => cents - (cents * FeePercent + 99) / 100;
}

public enum OfferKind
{
    Listing,
    Order,
}

public sealed record OfferRequest(string Item, long Quantity, long PriceCents);

public sealed record TakeRequest(long Id, long Quantity);

public sealed record IdRequest(long Id);

public sealed record ItemRequest(string Item);

public sealed record ItemSummary(string Item, long? LowestCents, long Listed, long? HighestCents, long Wanted);

/// <summary>
/// The best prices and amounts of every item (<see cref="PlayerMarketService.SummaryAsync"/>) as last worked out, shared
/// by everyone who opens the market. Adding up every open offer takes long once there are many of them, so a change to
/// an offer only marks its item, and the next look adds up that item alone. Everything is added up anew every
/// <see cref="KeptFor"/>, for offers changed while a look was adding them up.
/// </summary>
public sealed class MarketSummary
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private IReadOnlyDictionary<string, ItemSummary>? _items;
    private DateTimeOffset _at;
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>An offer of <paramref name="item"/> was made, taken or closed.</summary>
    public void Changed(string item)
    {
        lock (_lock)
        {
            _version++;
            _changed.Add(item);
        }
    }

    /// <summary>What is kept (null when everything must be added up), the items changed since, and the count of changes
    /// to hand back to <see cref="Keep"/>.</summary>
    public (IReadOnlyDictionary<string, ItemSummary>? Items, IReadOnlyList<string> Changed, long Version) Read(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _items is null || now - _at >= KeptFor ? (null, [], _version) : (_items, _changed.ToList(), _version);
        }
    }

    /// <summary>Keeps what a look added up, unless an offer changed while it did.</summary>
    public void Keep(long version, DateTimeOffset now, IReadOnlyDictionary<string, ItemSummary> items, bool everything)
    {
        lock (_lock)
        {
            if (version != _version)
            {
                return;
            }

            _items = items;
            _changed.Clear();
            if (everything)
            {
                _at = now;
            }
        }
    }
}

public sealed class PlayerMarketService(
    GameDb db, IInventory inventory, IWallet wallet, ICharacterNames names, TimeProvider time, ITechnologies technologies,
    IEnumerable<IGarageDeliveries> deliveries, MarketSummary summary, GameCounters counters)
    : IExportMarket
{
    /// <summary>Puts items up for sale: they leave the inventory now and wait on the listing.</summary>
    public Task<long> ListAsync(Guid seller, OfferRequest request, CancellationToken cancellationToken)
    {
        Check(request);
        return ChangeAsync(async () =>
        {
            await CheckOpenCountAsync(OfferKind.Listing, seller, cancellationToken);
            await inventory.RemoveAsync(seller, request.Item, request.Quantity, cancellationToken);
            await InsertAsync(OfferKind.Listing, seller, request, cancellationToken);
            return await wallet.BalanceAsync(seller, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Lists what an export train brought to a buying market: the goods are already out of the seller's hands.</summary>
    public async Task ListAsync(Guid seller, string item, long quantity, long priceCents, CancellationToken cancellationToken)
    {
        for (var left = quantity; left > 0; left -= MarketRules.MaxQuantity)
        {
            var request = new OfferRequest(item, Math.Min(left, MarketRules.MaxQuantity), priceCents);
            Check(request);
            await InsertAsync(OfferKind.Listing, seller, request, cancellationToken);
        }
    }

    /// <summary>Orders items from other players: the whole price leaves the wallet now and waits on the order.</summary>
    public Task<long> OrderAsync(Guid buyer, OfferRequest request, CancellationToken cancellationToken)
    {
        Check(request);
        return ChangeAsync(async () =>
        {
            // Nobody gets what they haven't researched yet, not even from other players.
            await technologies.RequireUnlockedAsync(buyer, request.Item, cancellationToken);
            await CheckOpenCountAsync(OfferKind.Order, buyer, cancellationToken);
            var balance = await wallet.DebitAsync(buyer, request.Quantity * request.PriceCents,
                $"buy order {request.Quantity} {request.Item}", cancellationToken);
            await InsertAsync(OfferKind.Order, buyer, request, cancellationToken);
            return balance;
        }, cancellationToken);
    }

    /// <summary>Buys from another player's listing: money to the seller, items to the buyer.</summary>
    public Task<Traded> PurchaseAsync(Guid buyer, TakeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var listing = await TakeAsync(OfferKind.Listing, buyer, request, cancellationToken);
            await technologies.RequireUnlockedAsync(buyer, listing.ItemId, cancellationToken);
            var cents = request.Quantity * listing.PriceCents;
            var balance = await wallet.DebitAsync(buyer, cents, $"market buy {request.Quantity} {listing.ItemId}", cancellationToken);
            await PayAsync(listing.OwnerId, cents, $"market sold {request.Quantity} {listing.ItemId}", cancellationToken);
            db.AfterCommit(() => counters.Trade(GameCounters.Players, listing.ItemId, request.Quantity, cents));
            return new Traded(balance, await Delivery.OverflowAsync(inventory, deliveries, buyer, listing.ItemId, request.Quantity, cancellationToken));
        }, cancellationToken);

    /// <summary>Fills another player's buy order: items to the buyer, the waiting money to the seller.</summary>
    public Task<long> FillAsync(Guid seller, TakeRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var order = await TakeAsync(OfferKind.Order, seller, request, cancellationToken);
            await inventory.RemoveAsync(seller, order.ItemId, request.Quantity, cancellationToken);
            await inventory.AddAsync(order.OwnerId, order.ItemId, request.Quantity, cancellationToken);
            db.AfterCommit(() => counters.Trade(GameCounters.Players, order.ItemId, request.Quantity, request.Quantity * order.PriceCents));
            return await PayAsync(seller, request.Quantity * order.PriceCents, $"filled order {request.Quantity} {order.ItemId}", cancellationToken);
        }, cancellationToken);

    /// <summary>Pays the seller for a sale, less the market's fee.</summary>
    private async Task<long> PayAsync(Guid seller, long cents, string reason, CancellationToken cancellationToken) =>
        MarketRules.AfterFee(cents) is var paid and > 0
            ? await wallet.CreditAsync(seller, paid, $"{reason}, fee {cents - paid}", cancellationToken)
            : await wallet.BalanceAsync(seller, cancellationToken);

    /// <summary>Closes the player's own offer; unsold items or unspent money go back.</summary>
    public Task<long> CancelAsync(OfferKind kind, Guid owner, long id, CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var offer = await LockAsync(kind, id, cancellationToken);
            if (offer is null || offer.OwnerId != owner || offer.ClosedAt is not null)
            {
                throw new GameException("no_offer", "This offer is no longer there.");
            }

            Touch(offer.ItemId);
            await using (var close = await db.CommandAsync($"UPDATE market.{Table(kind)} SET closed_at = @at WHERE id = @id", cancellationToken))
            {
                close.Parameters.AddWithValue("id", id);
                close.Parameters.AddWithValue("at", time.GetUtcNow());
                await close.ExecuteNonQueryAsync(cancellationToken);
            }

            if (offer.QuantityLeft == 0)
            {
                return await wallet.BalanceAsync(owner, cancellationToken);
            }

            if (kind == OfferKind.Listing)
            {
                await inventory.AddAsync(owner, offer.ItemId, offer.QuantityLeft, cancellationToken);
                return await wallet.BalanceAsync(owner, cancellationToken);
            }

            return await wallet.CreditAsync(owner, offer.QuantityLeft * offer.PriceCents,
                $"cancelled order {offer.QuantityLeft} {offer.ItemId}", cancellationToken);
        }, cancellationToken);

    /// <summary>Best open prices and amounts of every item that has offers.</summary>
    public async Task<IReadOnlyDictionary<string, ItemSummary>> SummaryAsync(CancellationToken cancellationToken)
    {
        var (kept, changed, version) = summary.Read(time.GetUtcNow());
        if (kept is not null && changed.Count == 0)
        {
            return kept;
        }

        var items = kept is null ? new Dictionary<string, ItemSummary>() : new Dictionary<string, ItemSummary>(kept);
        foreach (var item in changed)
        {
            items.Remove(item);
        }

        await using var command = await db.CommandAsync(
            """
            SELECT item_id, MIN(price_cents), SUM(quantity_left), NULL::bigint, 0::numeric
            FROM market.listings WHERE closed_at IS NULL AND (@all OR item_id = ANY(@items)) GROUP BY item_id
            UNION ALL
            SELECT item_id, NULL::bigint, 0::numeric, MAX(price_cents), SUM(quantity_left)
            FROM market.orders WHERE closed_at IS NULL AND (@all OR item_id = ANY(@items)) GROUP BY item_id
            """, cancellationToken);
        command.Parameters.AddWithValue("all", kept is null);
        command.Parameters.AddWithValue("items", changed.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = reader.GetString(0);
            var row = new ItemSummary(item,
                reader.IsDBNull(1) ? null : reader.GetInt64(1), (long)reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), (long)reader.GetDecimal(4));
            items[item] = items.TryGetValue(item, out var other)
                ? new ItemSummary(item, row.LowestCents ?? other.LowestCents, row.Listed + other.Listed,
                    row.HighestCents ?? other.HighestCents, row.Wanted + other.Wanted)
                : row;
        }

        summary.Keep(version, time.GetUtcNow(), items, everything: kept is null);
        return items;
    }

    /// <summary>Open offers of one item: cheapest listings and best-paying orders first.</summary>
    public async Task<object> BoardAsync(Guid viewer, string item, CancellationToken cancellationToken)
    {
        if (!Items.Exists(item ?? ""))
        {
            throw new GameException("unknown_item", $"Unknown item '{item}'.");
        }

        // The market doesn't show what the viewer hasn't researched yet.
        await technologies.RequireUnlockedAsync(viewer, item!, cancellationToken);
        var listings = await QueryAsync(OfferKind.Listing,
            "item_id = @item AND closed_at IS NULL ORDER BY price_cents, id", item!, null, cancellationToken);
        var orders = await QueryAsync(OfferKind.Order,
            "item_id = @item AND closed_at IS NULL ORDER BY price_cents DESC, id", item!, null, cancellationToken);
        var sellers = await names.NamesAsync(listings.Concat(orders).Select(o => o.OwnerId).Distinct().ToList(), cancellationToken);
        return new
        {
            item,
            name = Items.Names[item!],
            listings = listings.Select(o => Public(o, viewer, sellers)),
            orders = orders.Select(o => Public(o, viewer, sellers)),
        };
    }

    /// <summary>The player's open offers and those closed recently, newest first.</summary>
    public async Task<object> MineAsync(Guid owner, CancellationToken cancellationToken)
    {
        var listings = await MineAsync(OfferKind.Listing, owner, cancellationToken);
        var orders = await MineAsync(OfferKind.Order, owner, cancellationToken);
        return new { listings = listings.Select(Own), orders = orders.Select(Own) };
    }

    private async Task<IEnumerable<Offer>> MineAsync(OfferKind kind, Guid owner, CancellationToken cancellationToken)
    {
        var since = time.GetUtcNow() - MarketRules.ClosedShownFor;
        var open = await QueryAsync(kind, "owner_id = @owner AND closed_at IS NULL ORDER BY id DESC", owner, null, cancellationToken);
        // In the order of the index: sorted by id, a plan made once for every player (see Npgsql's auto-prepare) walks the whole table.
        var closed = await QueryAsync(kind, "owner_id = @owner AND closed_at > @since ORDER BY closed_at DESC", owner, since, cancellationToken);
        return open.Concat(closed).OrderByDescending(o => o.Id).Take(MarketRules.BoardSize);
    }

    private static object Public(Offer o, Guid viewer, IReadOnlyDictionary<Guid, string> owners) => new
    {
        id = o.Id,
        quantity = o.QuantityLeft,
        priceCents = o.PriceCents,
        owner = owners.GetValueOrDefault(o.OwnerId, "?"),
        mine = o.OwnerId == viewer,
    };

    private static object Own(Offer o) => new
    {
        id = o.Id,
        item = o.ItemId,
        name = Items.Names.GetValueOrDefault(o.ItemId, o.ItemId),
        quantity = o.Quantity,
        done = o.Quantity - o.QuantityLeft,
        priceCents = o.PriceCents,
        open = o.ClosedAt is null,
    };

    private static void Check(OfferRequest request)
    {
        if (!Items.Exists(request.Item ?? ""))
        {
            throw new GameException("unknown_item", $"Unknown item '{request.Item}'.");
        }

        CheckQuantity(request.Quantity);
        if (request.PriceCents is < 1 or > MarketRules.MaxPriceCents)
        {
            throw new GameException("bad_price", $"Price must be 0.01-{MarketRules.MaxPriceCents / 100} $.");
        }
    }

    private static void CheckQuantity(long quantity)
    {
        if (quantity is < 1 or > MarketRules.MaxQuantity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{MarketRules.MaxQuantity}.");
        }
    }

    private static string Table(OfferKind kind) => kind == OfferKind.Listing ? "listings" : "orders";

    private async Task CheckOpenCountAsync(OfferKind kind, Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"SELECT COUNT(*) FROM market.{Table(kind)} WHERE owner_id = @owner AND closed_at IS NULL", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        var open = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (open >= MarketRules.MaxOpenOffers)
        {
            throw new GameException("too_many_offers", $"You can have at most {MarketRules.MaxOpenOffers} open offers of this kind.");
        }
    }

    /// <summary>The items whose offers this request changed.</summary>
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);

    /// <summary>Marks the item changed in the summary at once, and once more when the change is committed (see <see cref="ChangeAsync"/>),
    /// so a look in between, which still saw the offers as they were, keeps nothing.</summary>
    private void Touch(string item)
    {
        _touched.Add(item);
        summary.Changed(item);
    }

    /// <summary>Runs a change to offers in one transaction.</summary>
    private async Task<T> ChangeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await db.InTransactionAsync(work, cancellationToken);
        }
        finally
        {
            foreach (var item in _touched)
            {
                summary.Changed(item);
            }

            _touched.Clear();
        }
    }

    private async Task InsertAsync(OfferKind kind, Guid owner, OfferRequest request, CancellationToken cancellationToken)
    {
        Touch(request.Item);
        await using var command = await db.CommandAsync(
            $"""
            INSERT INTO market.{Table(kind)} (owner_id, item_id, quantity, quantity_left, price_cents, created_at)
            VALUES (@owner, @item, @quantity, @quantity, @price, @at)
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", request.Item);
        command.Parameters.AddWithValue("quantity", request.Quantity);
        command.Parameters.AddWithValue("price", request.PriceCents);
        command.Parameters.AddWithValue("at", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Takes part of someone else's open offer; the row stays locked until the transaction ends.</summary>
    private async Task<Offer> TakeAsync(OfferKind kind, Guid taker, TakeRequest request, CancellationToken cancellationToken)
    {
        CheckQuantity(request.Quantity);
        var offer = await LockAsync(kind, request.Id, cancellationToken);
        if (offer is null || offer.ClosedAt is not null)
        {
            throw new GameException("no_offer", "This offer is no longer there.");
        }

        if (offer.OwnerId == taker)
        {
            throw new GameException("own_offer", "This is your own offer.");
        }

        if (request.Quantity > offer.QuantityLeft)
        {
            throw new GameException("not_enough_offered", "The offer has fewer items left.");
        }

        Touch(offer.ItemId);
        await using var command = await db.CommandAsync(
            $"""
            UPDATE market.{Table(kind)}
            SET quantity_left = quantity_left - @quantity, closed_at = CASE WHEN quantity_left = @quantity THEN @at END
            WHERE id = @id
            """, cancellationToken);
        command.Parameters.AddWithValue("id", request.Id);
        command.Parameters.AddWithValue("quantity", request.Quantity);
        command.Parameters.AddWithValue("at", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return offer;
    }

    private async Task<Offer?> LockAsync(OfferKind kind, long id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"SELECT {Columns} FROM market.{Table(kind)} WHERE id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private const string Columns = "id, owner_id, item_id, quantity, quantity_left, price_cents, created_at, closed_at";

    private async Task<List<Offer>> QueryAsync(OfferKind kind, string where, object key, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"SELECT {Columns} FROM market.{Table(kind)} WHERE {where} LIMIT {MarketRules.BoardSize}", cancellationToken);
        command.Parameters.AddWithValue(key is Guid ? "owner" : "item", key);
        if (since is { } at)
        {
            command.Parameters.AddWithValue("since", at);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var offers = new List<Offer>();
        while (await reader.ReadAsync(cancellationToken))
        {
            offers.Add(Read(reader));
        }

        return offers;
    }

    private static Offer Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OwnerId = reader.GetGuid(1),
        ItemId = reader.GetString(2),
        Quantity = reader.GetInt64(3),
        QuantityLeft = reader.GetInt64(4),
        PriceCents = reader.GetInt64(5),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        ClosedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
    };
}

/// <summary>Every item the player has researched, with the trader's quick sale price and the best offers of players.</summary>
public sealed class OverviewHandler(IServiceScopeFactory scopes, TraderMarket trader) : IMessageHandler
{
    public string Type => "market.overview";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var account = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        var summary = await scope.ServiceProvider.GetRequiredService<PlayerMarketService>().SummaryAsync(cancellationToken);
        // What the player hasn't researched yet isn't on the market for them.
        var locked = await scope.ServiceProvider.GetRequiredService<ITechnologies>().LockedAsync(account, cancellationToken);
        var prices = trader.Prices();
        return new Reply("market.overview", new
        {
            items = Items.Names.Where(item => !locked.ContainsKey(item.Key)).Select(item =>
            {
                summary.TryGetValue(item.Key, out var s);
                return new
                {
                    id = item.Key,
                    name = item.Value,
                    quickSaleCents = prices.Buys.TryGetValue(item.Key, out var floor) ? floor : (long?)null,
                    lowestCents = s?.LowestCents,
                    listed = s?.Listed ?? 0,
                    highestCents = s?.HighestCents,
                    wanted = s?.Wanted ?? 0,
                };
            }),
        });
    }
}

public abstract class PlayerMarketHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    /// <summary>Whether the message trades (lists, buys, sells), which a player the police banned from trading may not.</summary>
    protected virtual bool Trades => false;

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (Trades)
        {
            foreach (var sanctions in scope.ServiceProvider.GetServices<ISanctions>())
            {
                await sanctions.RequireAllowedAsync(client.RequireAccount(), SanctionKinds.Trade, cancellationToken);
            }
        }

        return await HandleAsync(scope.ServiceProvider.GetRequiredService<PlayerMarketService>(), payload, client.RequireAccount(), cancellationToken);
    }

    protected abstract Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken);

    /// <summary>The reply to every change: the player's new balance.</summary>
    protected static Reply Done(long balance) => new("market.done", new { balanceCents = balance });
}

public sealed class ItemBoardHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.item";

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        new("market.item", await market.BoardAsync(player, Payload.Read<ItemRequest>(payload).Item, cancellationToken));
}

public sealed class MineHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.mine";

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        new("market.mine", await market.MineAsync(player, cancellationToken));
}

public sealed class ListHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.list";

    protected override bool Trades => true;

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        Done(await market.ListAsync(player, Payload.Read<OfferRequest>(payload), cancellationToken));
}

public sealed class UnlistHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.unlist";

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        Done(await market.CancelAsync(OfferKind.Listing, player, Payload.Read<IdRequest>(payload).Id, cancellationToken));
}

public sealed class PurchaseHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.purchase";

    protected override bool Trades => true;

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken)
    {
        var traded = await market.PurchaseAsync(player, Payload.Read<TakeRequest>(payload), cancellationToken);
        return new Reply("market.done", new { balanceCents = traded.Balance, toPallet = traded.ToPallet > 0, palletQuantity = traded.ToPallet });
    }
}

public sealed class OrderHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.order";

    protected override bool Trades => true;

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        Done(await market.OrderAsync(player, Payload.Read<OfferRequest>(payload), cancellationToken));
}

public sealed class CancelOrderHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.cancelOrder";

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        Done(await market.CancelAsync(OfferKind.Order, player, Payload.Read<IdRequest>(payload).Id, cancellationToken));
}

public sealed class FillHandler(IServiceScopeFactory scopes) : PlayerMarketHandler(scopes)
{
    public override string Type => "market.fill";

    protected override bool Trades => true;

    protected override async Task<Reply> HandleAsync(PlayerMarketService market, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        Done(await market.FillAsync(player, Payload.Read<TakeRequest>(payload), cancellationToken));
}
