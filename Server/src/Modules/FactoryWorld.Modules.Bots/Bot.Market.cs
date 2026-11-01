using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>The bot on the players' market and at the trader.</summary>
public sealed partial class Bot
{
    private async Task<bool> TradeAsync(string type, string item, long quantity, CancellationToken cancellationToken)
    {
        var before = _money;
        var reply = await SendAsync(type, new { item, quantity = Math.Min(quantity, 1000) }, cancellationToken);
        if (!reply.Is("market.traded"))
        {
            return false;
        }

        if (type == "market.sell")
        {
            _traderIncome += Math.Max(0, _money - before);
        }

        await InventoryAsync(cancellationToken);
        return true;
    }

    /// <summary>What the bot has more of than it keeps: what it made itself first (a crafter sells its belts and chests rather than
    /// the iron it makes them of), then worth the most first.</summary>
    private IEnumerable<(string Item, long Quantity)> Surplus() =>
        _inventory
            .Where(i => (BotRules.Goods.Contains(i.Key) || _workshopGoods.Contains(i.Key) || Profile.Trade == BotTrade.Trader && Floor(i.Key) > 0)
                && i.Value > Keep(i.Key))
            .Select(i => (Item: i.Key, Quantity: i.Value - Keep(i.Key)))
            .OrderByDescending(s => BotRules.Products(Profile.Trade).Contains(s.Item) || _workshopGoods.Contains(s.Item))
            .ThenByDescending(s => s.Quantity * Math.Max(Floor(s.Item), 1));

    /// <summary>What the bot holds back from selling: what it keeps for its work, and what it buys for its trade up to as much as it buys.</summary>
    private long Keep(string item) =>
        Math.Max(KeepForWork(item), BotRules.Buys(Profile.Trade).Contains(item) ? BotRules.BuyUpTo : 0);

    /// <summary>What the bot holds back for its work: what its trade keeps, whatever it still means to build on its plots, and its line's parts and their makings.</summary>
    /// <summary>
    /// Frees a tile of a full inventory for <paramref name="incoming"/> the way a player would: sells the trader the
    /// whole of its cheapest stack it has no work for, if that is worth little.
    /// </summary>
    private async Task<bool> MakeRoomAsync(string incoming, CancellationToken cancellationToken)
    {
        var junk = _inventory
            .Where(i => i.Value > 0 && i.Key != incoming && Floor(i.Key) > 0 && KeepForWork(i.Key) == 0
                && !BotRules.Products(Profile.Trade).Contains(i.Key) && !_workshopGoods.Contains(i.Key)
                && !_workshopNeeds.Values.Any(n => n.ContainsKey(i.Key)) && Floor(i.Key) * i.Value <= BotRules.RoomFor)
            .OrderBy(i => Floor(i.Key) * i.Value)
            .Select(i => i.Key)
            .FirstOrDefault();
        if (junk is null || !await TradeAsync("market.sell", junk, Have(junk), cancellationToken))
        {
            return false;
        }

        await InventoryAsync(cancellationToken);
        return Have(junk) == 0;
    }

    private long KeepForWork(string item) =>
        BotRules.Keep(Profile.Trade, item) + _plots.Where(p => p.Tiles is not null).Sum(p => StillToBuild(p).Count(k => k == item))
        + _reserved.GetValueOrDefault(item);

    private async Task<bool> ListAsync(BotWorld world, CancellationToken cancellationToken)
    {
        var surplus = Surplus().Take(3).ToList();
        var (item, quantity) = surplus[world.Random.Next(surplus.Count)];
        quantity = Math.Min(quantity, BotRules.ListingSize);
        var floor = Math.Max(Floor(item), 1);
        var price = (long)Math.Round(floor * (1.4 + world.Random.NextDouble() * 0.8));

        // Undercut the cheapest listing a little, but never sell for near what the trader pays.
        var board = await SendAsync("market.item", new { item }, cancellationToken);
        if (board.Is("market.item"))
        {
            var cheapest = board.Payload.Each("listings").Where(l => !l.True("mine")).Select(l => l.Long("priceCents")).DefaultIfEmpty(0).First();
            if (cheapest > 0)
            {
                price = Math.Min(price, (long)Math.Round(cheapest * (0.93 + world.Random.NextDouble() * 0.07)));
            }
        }

        // No player buys from a bot what the trader sells for less.
        if (_traderSells.TryGetValue(item, out var shop))
        {
            price = Math.Min(price, shop * 97 / 100);
        }

        price = Math.Max(price, (long)Math.Ceiling(floor * BotRules.MinMarkup));
        var reply = await SendAsync("market.list", new { item, quantity, priceCents = price }, cancellationToken);
        if (!reply.Is("market.done"))
        {
            return false;
        }

        _openListings++;
        _lastListingAt = world.Now;
        _inventory[item] = Have(item) - quantity;
        return true;
    }

    /// <summary>Takes back the oldest listing nobody bought from, so it can go up again at the price players pay now.</summary>
    private async Task<bool> UnlistOldestAsync(BotWorld world, CancellationToken cancellationToken)
    {
        var oldest = _listings.Where(l => !l.Sold).MinBy(l => l.Id);
        var reply = await SendAsync("market.unlist", new { id = oldest.Id }, cancellationToken);
        _listings.Remove(oldest);
        if (!reply.Ok)
        {
            return false;
        }

        _openListings = _listings.Count;
        _lastListingAt = world.Now - BotRules.ListEvery;
        await InventoryAsync(cancellationToken);
        return true;
    }

    /// <summary>Buys from other players: what the bot needs for its work, or, as a trader, anything cheap enough to sell on.</summary>
    /// <param name="only">What to look for and how many of each, instead of anything worth buying.</param>
    private async Task<bool> ShopAsync(BotWorld world, IReadOnlyDictionary<string, long>? only, CancellationToken cancellationToken)
    {
        if (await OverviewAsync(world, cancellationToken) is not { } overview)
        {
            return false;
        }

        var wanted = Wanted().ToHashSet(StringComparer.Ordinal);
        var deals = new List<(string Item, long Price)>();
        foreach (var entry in overview.Each("items"))
        {
            var id = entry.Text("id")!;
            if (entry.LongOrNull("lowestCents") is not { } lowest || entry.Long("listed") == 0 || only is not null && !only.ContainsKey(id))
            {
                continue;
            }

            if (Limit(id, entry.LongOrNull("quickSaleCents") ?? Floor(id)) is { } limit && lowest <= limit)
            {
                deals.Add((id, lowest));
            }
        }

        if (deals.Count == 0)
        {
            return false;
        }

        var (item, _) = deals[world.Random.Next(deals.Count)];
        var board = await SendAsync("market.item", new { item }, cancellationToken);
        if (!board.Is("market.item"))
        {
            return false;
        }

        var most = Limit(item, Floor(item)) ?? 0;
        // From other bots only what it needs itself: bots buying from bots to sell on only pushed money round in circles.
        var fromBots = wanted.Contains(item);
        foreach (var listing in board.Payload.Each("listings")
                     .Where(l => !l.True("mine") && l.Long("priceCents") <= most && (fromBots || !_commons.IsBot(l.Text("owner")))))
        {
            var price = listing.Long("priceCents");
            var budget = (Spare - BotRules.Reserve) / Math.Max(price, 1);
            var quantity = Math.Min(Math.Min(listing.Long("quantity"), BotRules.PurchaseSize), budget);
            if (only is not null)
            {
                quantity = Math.Min(quantity, only[item]);
            }

            if (quantity <= 0)
            {
                return false;
            }

            var reply = await SendAsync("market.purchase", new { id = listing.Long("id"), quantity }, cancellationToken);
            if (reply.Is("market.done"))
            {
                _purchases++;
                _inventory[item] = Have(item) + quantity;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The most the bot pays a player for one piece, or null when it does not buy the item. What its trade needs it
    /// pays well for; anything else it takes when it is a bargain, and a trader buys to sell it on. Never more than
    /// the NPC trader charges, so buying from the trader to sell to a bot pays nobody.
    /// </summary>
    private double? Limit(string item, long floor)
    {
        if (floor <= 0)
        {
            return null;
        }

        var limit = (BotRules.Buys(Profile.Trade).Contains(item) || _needs.ContainsKey(item)) && Have(item) < BotRules.BuyUpTo ? floor * 2.0
            : _shortfalls.ContainsKey(item) && Have(item) < BotRules.BuyUpTo ? floor * BotRules.MaterialsMarkup
            : Profile.Trade == BotTrade.Trader ? floor * 1.5
            : BotRules.Goods.Contains(item) ? floor * 1.3
            : 0;
        if (_traderSells.TryGetValue(item, out var shop))
        {
            limit = Math.Min(limit, shop);
        }

        return limit > 0 ? limit : null;
    }

    /// <summary>Sells to a player's buy order that pays at least what the trader would.</summary>
    private async Task<bool> FillOrderAsync(BotWorld world, CancellationToken cancellationToken)
    {
        var surplus = Surplus().ToDictionary(s => s.Item, s => s.Quantity);
        if (surplus.Count == 0)
        {
            return false;
        }

        if (await OverviewAsync(world, cancellationToken) is not { } overview)
        {
            return false;
        }

        var options = overview.Each("items")
            .Where(e => surplus.ContainsKey(e.Text("id")!) && e.Long("wanted") > 0 && e.LongOrNull("highestCents") is { } best && best >= Floor(e.Text("id")!))
            .Select(e => e.Text("id")!)
            .ToList();
        if (options.Count == 0)
        {
            return false;
        }

        var item = options[world.Random.Next(options.Count)];
        var board = await SendAsync("market.item", new { item }, cancellationToken);
        if (!board.Is("market.item"))
        {
            return false;
        }

        foreach (var order in board.Payload.Each("orders").Where(o => !o.True("mine") && o.Long("priceCents") >= Floor(item)))
        {
            var quantity = Math.Min(order.Long("quantity"), Math.Min(surplus[item], BotRules.ListingSize));
            var reply = await SendAsync("market.fill", new { id = order.Long("id"), quantity }, cancellationToken);
            if (reply.Is("market.done"))
            {
                _inventory[item] = Have(item) - quantity;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> PlaceOrderAsync(BotWorld world, CancellationToken cancellationToken)
    {
        var wanted = Wanted().Where(i => Have(i) < 20 && Floor(i) > 0).ToList();
        if (wanted.Count == 0)
        {
            return false;
        }

        var item = wanted[world.Random.Next(wanted.Count)];
        var price = Math.Max(1, (long)Math.Round(Math.Max(Floor(item), 1) * (1.1 + world.Random.NextDouble() * 0.3)));
        var quantity = Math.Clamp(_needs.GetValueOrDefault(item, 20), 5, 40);
        if (price * quantity > (Spare - BotRules.Reserve) / 3)
        {
            return false;
        }

        var reply = await SendAsync("market.order", new { item, quantity, priceCents = price }, cancellationToken);
        if (!reply.Is("market.done"))
        {
            return false;
        }

        _openOrders++;
        return true;
    }
}
