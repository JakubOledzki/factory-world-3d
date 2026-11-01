using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Law;

/// <summary>
/// The border between two countries with a government. A declared train pays the duty of the country it goes to (into its
/// treasury) and may not carry what that country bans, nor anything at all under an embargo. A smuggling train pays nothing and
/// carries anything, but customs look at it with a chance that grows with the country's police; caught, its forbidden goods are
/// seized and put up for sale by the treasury, and a case is opened. Every crossing is kept for the police to inspect later.
/// </summary>
public sealed class BorderControl(
    LawData data, ICountries countries, IWallet wallet, IMarketPrices prices, ICharacterXp xp, IExportMarket market) : IBorderControl
{
    public async Task<BorderCrossing> CrossAsync(
        Guid owner, (int X, int Y) from, (int X, int Y) to, IReadOnlyDictionary<string, long> goods, bool smuggle, CancellationToken cancellationToken,
        long feeCents = 0)
    {
        var fromCountry = await countries.CountryOfPlotAsync(from.X, from.Y, cancellationToken);
        var toCountry = await countries.CountryOfPlotAsync(to.X, to.Y, cancellationToken);
        if (fromCountry is null || toCountry is null || fromCountry == toCountry
            || await countries.TreasuryAsync(toCountry, cancellationToken) is not { } treasury)
        {
            return BorderCrossing.Open;
        }

        if (await data.SanctionedAsync(owner, SanctionKinds.Travel, cancellationToken))
        {
            return new BorderCrossing("travel_banned", 0, new Dictionary<string, long>());
        }

        var trade = await countries.TradeAsync(fromCountry, toCountry, cancellationToken);
        var duty = LawRules.Duty(prices, trade, goods);
        var forbidden = await data.ForbiddenAsync(owner, toCountry, goods, cancellationToken);
        if (!smuggle)
        {
            if (trade.Embargo)
            {
                return new BorderCrossing("embargo", 0, new Dictionary<string, long>());
            }

            if (forbidden.Count > 0)
            {
                return new BorderCrossing("banned_goods", 0, new Dictionary<string, long>());
            }

            if (duty > 0)
            {
                if (await wallet.BalanceAsync(owner, cancellationToken) < duty + feeCents)
                {
                    return new BorderCrossing("no_money_duty", 0, new Dictionary<string, long>());
                }

                await wallet.DebitAsync(owner, duty, $"duty {fromCountry}->{toCountry}", cancellationToken);
                await wallet.CreditAsync(treasury, duty, $"duty {fromCountry}->{toCountry}", cancellationToken);
            }

            await RecordAsync(owner, fromCountry, toCountry, from, to, goods, true, false, duty, 0, null, cancellationToken);
            return new BorderCrossing(null, duty, new Dictionary<string, long>());
        }

        // What smuggling saves: the duty, the worth of what may not come in, everything under an embargo.
        var illegal = trade.Embargo
            ? goods.Where(g => g.Value > 0).ToDictionary(g => g.Key, g => g.Value)
            : goods.Where(g => g.Value > 0 && (forbidden.ContainsKey(g.Key) || trade.PercentOn(g.Key) > 0)).ToDictionary(g => g.Key, g => g.Value);
        var evaded = duty + forbidden.Sum(g => LawRules.Worth(prices, g.Key, g.Value))
            + (trade.Embargo ? goods.Sum(g => LawRules.Worth(prices, g.Key, g.Value)) : 0);
        // The police leave new players alone, so new players may not smuggle: a low-level account would carry anything for nothing.
        if (illegal.Count > 0 && await xp.LevelAsync(owner, cancellationToken) < LawRules.ProtectedBelowLevel)
        {
            return new BorderCrossing("smuggling_protected", 0, new Dictionary<string, long>());
        }

        var hidden = await data.ScalarAsync("SELECT 1 FROM law.compartments WHERE player_id = @p", cancellationToken, ("p", owner)) is not null;
        Guid? inspected = null;
        var seized = new Dictionary<string, long>();
        if (illegal.Count > 0 && Random.Shared.Next(100) < await CustomsPercentAsync(owner, toCountry, hidden, cancellationToken))
        {
            // Caught: the forbidden goods stay at the border and are sold for the treasury.
            inspected = Guid.Empty;
            seized = illegal;
            await Seized.SellAsync(market, prices, treasury, seized, cancellationToken);

            await data.OpenCaseAsync(toCountry, owner, Crimes.Smuggling,
                $"Customs at the border from {fromCountry}: {Describe(seized)}", evaded, Guid.Empty, cancellationToken);
        }

        await RecordAsync(owner, fromCountry, toCountry, from, to, goods, false, hidden, 0, illegal.Count > 0 ? evaded : 0, inspected, cancellationToken);
        return new BorderCrossing(null, 0, seized);
    }

    /// <summary>The chance customs look at a smuggling train, in percent.</summary>
    private async Task<int> CustomsPercentAsync(Guid owner, string country, bool hidden, CancellationToken cancellationToken)
    {
        var percent = Math.Min(LawRules.CustomsMaxPercent,
            LawRules.CustomsBasePercent + LawRules.CustomsPerOfficerPercent * await data.OfficerCountAsync(country, cancellationToken));
        if (hidden)
        {
            percent /= 2;
        }

        if (await data.SanctionedAsync(owner, SanctionKinds.Wanted, cancellationToken))
        {
            percent += LawRules.CustomsWantedPercent;
        }

        return percent;
    }

    private Task RecordAsync(
        Guid owner, string fromCountry, string toCountry, (int X, int Y) from, (int X, int Y) to, IReadOnlyDictionary<string, long> goods,
        bool declared, bool hidden, long duty, long evaded, Guid? inspectedBy, CancellationToken cancellationToken) =>
        data.ExecuteAsync(
            """
            INSERT INTO law.crossings (owner_id, from_country, to_country, from_x, from_y, to_x, to_y, items, quantities, declared, hidden,
                duty_cents, evaded_cents, at, inspected_by)
            VALUES (@o, @fc, @tc, @fx, @fy, @tx, @ty, @items, @q, @declared, @hidden, @duty, @evaded, @now, @by)
            """, cancellationToken, ("o", owner), ("fc", fromCountry), ("tc", toCountry), ("fx", from.X), ("fy", from.Y), ("tx", to.X), ("ty", to.Y),
            ("items", goods.Keys.ToArray()), ("q", goods.Values.ToArray()), ("declared", declared), ("hidden", hidden), ("duty", duty),
            ("evaded", evaded), ("now", data.Now), ("by", inspectedBy));

    public static string Describe(IReadOnlyDictionary<string, long> goods) =>
        string.Join(", ", goods.Select(g => $"{g.Value} {g.Key}"));
}

/// <summary>Goods the police took: the treasury puts them up on the players' market.</summary>
public static class Seized
{
    /// <summary>Lists the goods for the treasury at what the trader asks for them (twice what it pays where it sells none),
    /// so nobody buys them cheaper than from the trader or makes money selling them on to the fence.</summary>
    public static async Task SellAsync(
        IExportMarket market, IMarketPrices prices, Guid treasury, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken)
    {
        foreach (var (item, quantity) in goods)
        {
            if (quantity > 0 && (prices.SellsCents(item) ?? prices.BuysCents(item) * 2) is { } price and > 0)
            {
                await market.ListAsync(treasury, item, quantity, price, cancellationToken);
            }
        }
    }
}
