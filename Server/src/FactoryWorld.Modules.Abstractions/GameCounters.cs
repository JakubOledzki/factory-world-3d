namespace FactoryWorld.Modules.Abstractions;

/// <summary>
/// What happened in the game since the server started, for <c>/metrics</c>: pieces of each item made and used up,
/// money paid in and out by kind of reason, players apart from bots, and goods traded on the markets. Counted only once the change is committed.
/// Kept in memory and never reset; Prometheus works out the rates and starts over across a restart.
/// </summary>
public sealed class GameCounters(OnlinePlayers online)
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Item, bool Bot), ItemCount> _items = [];
    private readonly Dictionary<(string Reason, bool Bot), MoneyCount> _money = [];
    private readonly Dictionary<(string Market, string Item), TradeCount> _trades = [];

    /// <summary>The <c>market</c> label of a trade: bought from the trader, sold to the trader, or between players.</summary>
    public const string TraderBuy = "trader_buy";
    public const string TraderSell = "trader_sell";
    public const string Players = "players";

    /// <summary>Who did it, as the <c>who</c> label says: <c>bot</c> or <c>player</c>.</summary>
    public static string Who(bool bot) => bot ? "bot" : "player";

    public void Production(Guid owner, string item, long produced, long consumed)
    {
        var key = (item, online.IsBot(owner));
        lock (_lock)
        {
            if (!_items.TryGetValue(key, out var count))
            {
                _items[key] = count = new ItemCount();
            }

            count.Produced += produced;
            count.Consumed += consumed;
        }
    }

    /// <summary>Money paid to (<paramref name="cents"/> &gt; 0) or taken from the owner; the ledger's reason.</summary>
    public void Money(Guid owner, long cents, string reason)
    {
        var key = (ReasonKind(reason), online.IsBot(owner));
        lock (_lock)
        {
            if (!_money.TryGetValue(key, out var count))
            {
                _money[key] = count = new MoneyCount();
            }

            if (cents > 0)
            {
                count.In += cents;
            }
            else
            {
                count.Out -= cents;
            }
        }
    }

    /// <summary>Pieces of an item changed hands on a market (<see cref="TraderBuy"/>, <see cref="TraderSell"/>,
    /// <see cref="Players"/>) for so many cents, before any fee.</summary>
    public void Trade(string market, string item, long quantity, long cents)
    {
        lock (_lock)
        {
            if (!_trades.TryGetValue((market, item), out var count))
            {
                _trades[(market, item)] = count = new TradeCount();
            }

            count.Quantity += quantity;
            count.Cents += cents;
        }
    }

    public IReadOnlyList<ItemTotal> Items()
    {
        lock (_lock)
        {
            return _items.Select(p => new ItemTotal(p.Key.Item, p.Key.Bot, p.Value.Produced, p.Value.Consumed))
                .OrderBy(t => t.Item, StringComparer.Ordinal).ThenBy(t => t.Bot).ToList();
        }
    }

    public IReadOnlyList<MoneyTotal> MoneyTotals()
    {
        lock (_lock)
        {
            return _money.Select(p => new MoneyTotal(p.Key.Reason, p.Key.Bot, p.Value.In, p.Value.Out))
                .OrderBy(t => t.Reason, StringComparer.Ordinal).ThenBy(t => t.Bot).ToList();
        }
    }

    public IReadOnlyList<TradeTotal> Trades()
    {
        lock (_lock)
        {
            return _trades.Select(p => new TradeTotal(p.Key.Market, p.Key.Item, p.Value.Quantity, p.Value.Cents))
                .OrderBy(t => t.Market, StringComparer.Ordinal).ThenBy(t => t.Item, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// The kind of a ledger reason, so a label has a few values and not one per plot or item: the first word
    /// ("sell 5 iron_ore" is "sell", "tax of Wola: ..." is "tax"), and the second one after "company" and "bank".
    /// </summary>
    public static string ReasonKind(string reason)
    {
        var words = reason.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "other";
        }

        var kind = Word(words[0]);
        if (words.Length > 1 && kind is "company" or "bank")
        {
            var second = Word(words[1]);
            kind = second is "research" or "deposit" ? $"{kind}_{second}" : kind;
        }

        return kind.Length == 0 ? "other" : kind;
    }

    private static string Word(string word) =>
        new(word.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or '_').Take(24).ToArray());

    private sealed class ItemCount
    {
        public long Produced;
        public long Consumed;
    }

    private sealed class MoneyCount
    {
        public long In;
        public long Out;
    }

    private sealed class TradeCount
    {
        public long Quantity;
        public long Cents;
    }

    public sealed record ItemTotal(string Item, bool Bot, long Produced, long Consumed);

    public sealed record MoneyTotal(string Reason, bool Bot, long InCents, long OutCents);

    public sealed record TradeTotal(string Market, string Item, long Quantity, long Cents);
}
