namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// The border between countries as a train sees it. Every train between plots in two countries goes through it: a declared
/// train pays the duty, a smuggling one pays nothing but may be caught. Call it inside the transaction that sends the train.
/// </summary>
public interface IBorderControl
{
    /// <summary>
    /// Lets the goods of <paramref name="owner"/> cross from one plot to the other. Charges the duty of a declared train to the
    /// owner's wallet; customs may seize goods of a smuggling one, which then do not travel. Within one country (or where there
    /// is none) nothing happens. Never throws for the law: a train that may not go gets <see cref="BorderCrossing.Refused"/>,
    /// also when the owner lacks the money for the duty and the <paramref name="feeCents"/> they are about to pay for the train.
    /// </summary>
    Task<BorderCrossing> CrossAsync(
        Guid owner, (int X, int Y) from, (int X, int Y) to, IReadOnlyDictionary<string, long> goods, bool smuggle, CancellationToken cancellationToken,
        long feeCents = 0);
}

/// <param name="Refused">Why the train may not go (<c>embargo</c>, <c>banned_goods</c>, <c>travel_banned</c>,
/// <c>no_money_duty</c>, <c>smuggling_protected</c>), or null.</param>
/// <param name="DutyCents">The duty charged.</param>
/// <param name="Seized">What customs took off a smuggling train; the train carries the rest.</param>
public sealed record BorderCrossing(string? Refused, long DutyCents, IReadOnlyDictionary<string, long> Seized)
{
    public static readonly BorderCrossing Open = new(null, 0, new Dictionary<string, long>());

    /// <summary>The goods left on the train after customs.</summary>
    public IReadOnlyDictionary<string, long> Left(IReadOnlyDictionary<string, long> goods) =>
        goods.Select(g => (g.Key, Quantity: g.Value - Seized.GetValueOrDefault(g.Key))).Where(g => g.Quantity > 0)
            .ToDictionary(g => g.Key, g => g.Quantity);
}

/// <summary>Punishments that stop a player doing things for a while, given by the police.</summary>
public interface ISanctions
{
    /// <summary>Fails with <c>trade_banned</c> or <c>travel_banned</c> while the player is banned from <paramref name="kind"/>
    /// (<see cref="SanctionKinds"/>).</summary>
    Task RequireAllowedAsync(Guid player, string kind, CancellationToken cancellationToken);
}

public static class SanctionKinds
{
    /// <summary>No trading with the trader or on the players' market.</summary>
    public const string Trade = "trade";

    /// <summary>No trains across a border.</summary>
    public const string Travel = "travel";

    /// <summary>The wanted list: customs look at the player's trains more often.</summary>
    public const string Wanted = "wanted";
}

/// <summary>What players do that some countries forbid; the law module notes it and the police may find it.</summary>
public interface ILawWatch
{
    /// <summary>The player cut <paramref name="trees"/> trees on the plot.</summary>
    Task TreesCutAsync(Guid player, int x, int y, int trees, CancellationToken cancellationToken);
}

/// <summary>Everything stored on a plot (chests, warehouses, silos), for a police search.</summary>
public interface IPlotStorage
{
    /// <summary>What the plot's storages hold together, by item.</summary>
    Task<IReadOnlyDictionary<string, long>> ContentsAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>Takes up to the given goods out of the plot's storages; returns what was taken.</summary>
    Task<IReadOnlyDictionary<string, long>> SeizeAsync(int x, int y, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken);
}
