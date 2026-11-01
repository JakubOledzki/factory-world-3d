using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Law;

public sealed record FenceOffer(string Item, long PriceCents);

/// <summary>
/// What the police hunt beside smuggling: the fence, who pays more than the trader for explosives, uranium, fossils and whatever the
/// player's country bans, and sells hidden compartments for trains; bribes to officers; trees cut where the forest is protected.
/// None of it is stopped: each leaves a trace that a detective may find.
/// </summary>
public sealed class Underworld(
    LawData data, ICountries countries, IWallet wallet, IInventory inventory, IMarketPrices prices, ICharacterXp xp, ICharacterNames names,
    IForestCertificate certificate) : ILawWatch
{
    /// <summary>What the fence pays for one piece: more than the trader, but at most 90% of what the trader asks even in his
    /// cheapest sale, so goods bought from him never sell to the fence at a profit; null when he does not take it.</summary>
    public long? FencePrice(string item, IReadOnlySet<string> banned)
    {
        if (!LawRules.AlwaysContraband.Contains(item) && !banned.Contains(item) || prices.BuysCents(item) is not { } buys)
        {
            return null;
        }

        var price = buys * LawRules.FencePercent / 100;
        return prices.CheapestSellsCents(item) is { } cheapest ? Math.Min(price, cheapest * 90 / 100) : price;
    }

    /// <summary>The goods the player's own country bans, which the fence takes too.</summary>
    private async Task<IReadOnlySet<string>> BannedAtHomeAsync(Guid player, CancellationToken cancellationToken) =>
        await countries.CitizenshipAsync(player, cancellationToken) is { } home
            ? (await data.RulesAsync(home, cancellationToken)).Where(r => r.Kind == RuleKinds.Banned).Select(r => r.Item).ToHashSet()
            : new HashSet<string>();

    public async Task<IReadOnlyList<FenceOffer>> OffersAsync(Guid player, CancellationToken cancellationToken)
    {
        var banned = await BannedAtHomeAsync(player, cancellationToken);
        return LawRules.AlwaysContraband.Union(banned).Order()
            .Select(item => FencePrice(item, banned) is { } price ? new FenceOffer(item, price) : null)
            .OfType<FenceOffer>().ToList();
    }

    private async Task RequireFenceLevelAsync(Guid player, CancellationToken cancellationToken)
    {
        if (await xp.LevelAsync(player, cancellationToken) < LawRules.FenceLevel)
        {
            throw new GameException("fence_level_too_low", $"The fence deals with players from level {LawRules.FenceLevel}.");
        }
    }

    /// <summary>Sells goods to the fence; the sale leaves a trace in the player's country.</summary>
    public Task<long> SellAsync(Guid player, string item, long quantity, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            await RequireFenceLevelAsync(player, cancellationToken);
            if (quantity is < 1 or > 10_000)
            {
                throw new GameException("bad_quantity", "Sell 1-10000 pieces.");
            }

            var price = FencePrice(item ?? "", await BannedAtHomeAsync(player, cancellationToken))
                ?? throw new GameException("fence_not_traded", "The fence does not take this.");
            await data.CountActionAsync(player, "fence_dollars", LawRules.FenceDollarsPerDay, cancellationToken,
                (int)Math.Min(int.MaxValue, (price * quantity + 99) / 100));
            await inventory.RemoveAsync(player, item!, quantity, cancellationToken);
            var balance = await wallet.CreditAsync(player, price * quantity, $"fence sell {quantity} {item}", cancellationToken);
            await TraceAtHomeAsync(player, Crimes.BlackMarket, $"Sold {quantity} {item} to the fence", price * quantity, cancellationToken);
            return balance;
        }, cancellationToken);

    /// <summary>Buys a hidden compartment for all the player's trains: customs at the border notice them half as often, and only
    /// a detective finds the goods in it.</summary>
    public Task<long> BuyCompartmentAsync(Guid player, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            await RequireFenceLevelAsync(player, cancellationToken);
            if (await data.ExecuteAsync("INSERT INTO law.compartments (player_id, bought_at) VALUES (@p, @now) ON CONFLICT DO NOTHING",
                cancellationToken, ("p", player), ("now", data.Now)) == 0)
            {
                throw new GameException("already_bought", "Your trains have a hidden compartment already.");
            }

            var balance = await wallet.DebitAsync(player, LawRules.CompartmentCents, "fence hidden compartment", cancellationToken);
            await TraceAtHomeAsync(player, Crimes.BlackMarket, "Bought a hidden compartment from the fence", LawRules.CompartmentCents, cancellationToken);
            return balance;
        }, cancellationToken);

    public async Task<bool> HasCompartmentAsync(Guid player, CancellationToken cancellationToken) =>
        await data.ScalarAsync("SELECT 1 FROM law.compartments WHERE player_id = @p", cancellationToken, ("p", player)) is not null;

    /// <summary>Gives an officer money. It goes at once; the officer's country notes a bribe they took and the player gave.</summary>
    public Task<long> BribeAsync(Guid player, string officerName, long cents, CancellationToken cancellationToken) =>
        data.InTransactionAsync(async () =>
        {
            if (cents is < 1 or > LawRules.MaxBribeCents)
            {
                throw new GameException("bad_amount", $"Give 0.01-{LawRules.MaxBribeCents / 100} $.");
            }

            var target = await names.AccountAsync(officerName?.Trim() ?? "", cancellationToken);
            if (target is not { } officerId || officerId == player || await data.OfficerAsync(officerId, cancellationToken) is not { } officer)
            {
                throw new GameException("not_officer", "This player is not an officer.");
            }

            var balance = await wallet.DebitAsync(player, cents, "gift to an officer", cancellationToken);
            await wallet.CreditAsync(officerId, cents, "gift from a player", cancellationToken);
            await data.TraceAsync(officer.Country, officerId, player, Crimes.Bribe, $"Took {cents / 100} $ from a player", cents, cancellationToken);
            return balance;
        }, cancellationToken);

    public async Task TreesCutAsync(Guid player, int x, int y, int trees, CancellationToken cancellationToken)
    {
        if (trees < 1 || await countries.CountryOfPlotAsync(x, y, cancellationToken) is not { } country
            || !await data.ForestProtectedAsync(country, cancellationToken) || await certificate.CertifiedAsync(player, cancellationToken))
        {
            return;
        }

        // One trace a day: what was cut adds up.
        if (await data.ExecuteAsync(
            """
            UPDATE law.traces SET value_cents = value_cents + @v, detail = 'Cut ' || (value_cents + @v) / @per || ' trees without the forest certificate'
            WHERE id = (SELECT id FROM law.traces WHERE country = @c AND suspect_id = @p AND kind = @k AND NOT found AND at > @since ORDER BY id DESC LIMIT 1)
            """, cancellationToken, ("v", trees * LawRules.TreeWorthCents), ("per", LawRules.TreeWorthCents), ("c", country), ("p", player),
            ("k", Crimes.Logging), ("since", data.Now - TimeSpan.FromDays(1))) == 0)
        {
            await data.TraceAsync(country, player, null, Crimes.Logging, $"Cut {trees} trees without the forest certificate",
                trees * LawRules.TreeWorthCents, cancellationToken);
        }
    }

    private async Task TraceAtHomeAsync(Guid player, string kind, string detail, long valueCents, CancellationToken cancellationToken)
    {
        if (await countries.CitizenshipAsync(player, cancellationToken) is { } home)
        {
            await data.TraceAsync(home, player, null, kind, detail, valueCents, cancellationToken);
        }
    }
}
