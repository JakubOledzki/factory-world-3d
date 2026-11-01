using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Market;

/// <summary>
/// The markets now: open offers between players, the best prices and amounts of each item offered, the trader's
/// prices of the items anyone offers or traded since the server started (not all of them: each item is a series),
/// and the trader's event.
/// </summary>
public sealed class MarketGauges(GameDb db, PlayerMarketService market, TraderMarket trader, GameCounters counters, OnlinePlayers online)
    : IGameGauges
{
    public async Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        var offers = new List<GaugeSample>();
        foreach (var (kind, table) in new[] { ("listing", "listings"), ("order", "orders") })
        {
            var open = new Dictionary<bool, (long Offers, long Owners)> { [false] = (0, 0), [true] = (0, 0) };
            await using var command = await db.CommandAsync(
                $"SELECT owner_id = ANY(@bots), COUNT(*), COUNT(DISTINCT owner_id) FROM market.{table} WHERE closed_at IS NULL GROUP BY 1",
                cancellationToken);
            command.Parameters.AddWithValue("bots", online.BotAccounts);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    open[reader.GetBoolean(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                }
            }

            offers.AddRange(open.Select(o => new GaugeSample(o.Value.Offers, ("kind", kind), ("who", GameCounters.Who(o.Key)))));
        }

        var summary = (await market.SummaryAsync(cancellationToken)).Values.OrderBy(s => s.Item, StringComparer.Ordinal).ToList();
        var prices = trader.Prices();
        var watched = summary.Select(s => s.Item).Concat(counters.Trades().Select(t => t.Item)).ToHashSet(StringComparer.Ordinal);
        var traderPrices = new List<GaugeSample>();
        foreach (var item in watched.Order(StringComparer.Ordinal))
        {
            if (prices.Buys.TryGetValue(item, out var buys))
            {
                traderPrices.Add(new(buys, ("item", item), ("side", "buys")));
            }

            if (prices.Sells.TryGetValue(item, out var sells))
            {
                traderPrices.Add(new(sells, ("item", item), ("side", "sells")));
            }
        }

        return
        [
            new("factoryworld_market_offers", "Open offers between players: sell listings and buy orders.", offers),
            new("factoryworld_market_lowest_listing_cents", "Cheapest open sell listing of an item, in cents per piece.",
                summary.Where(s => s.LowestCents is not null).Select(s => new GaugeSample(s.LowestCents!.Value, ("item", s.Item))).ToList()),
            new("factoryworld_market_highest_order_cents", "Best-paying open buy order of an item, in cents per piece.",
                summary.Where(s => s.HighestCents is not null).Select(s => new GaugeSample(s.HighestCents!.Value, ("item", s.Item))).ToList()),
            new("factoryworld_market_listed_pieces", "Pieces of an item waiting on open sell listings.",
                summary.Where(s => s.Listed > 0).Select(s => new GaugeSample(s.Listed, ("item", s.Item))).ToList()),
            new("factoryworld_market_wanted_pieces", "Pieces of an item wanted by open buy orders.",
                summary.Where(s => s.Wanted > 0).Select(s => new GaugeSample(s.Wanted, ("item", s.Item))).ToList()),
            new("factoryworld_trader_price_cents",
                "What the trader pays (side buys) and charges (side sells) now for an item offered or traded since the start, in cents.",
                traderPrices),
            new("factoryworld_market_event", "The trader's market event now (1), or calm.",
                [new(1, ("event", prices.Event?.Event.Id ?? "calm"))]),
        ];
    }
}
