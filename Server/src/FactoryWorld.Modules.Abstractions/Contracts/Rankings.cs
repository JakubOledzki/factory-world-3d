namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>One player's score in a ranking.</summary>
public sealed record RankedPlayer(Guid AccountId, long Value);

/// <summary>
/// A list of the best players by one measure (money, level...). Each module that keeps such a
/// measure may offer one: <c>services.AddScoped&lt;IPlayerRanking, MyRanking&gt;()</c>.
/// </summary>
public interface IPlayerRanking
{
    /// <summary>Stable id the client asks for, e.g. "money".</summary>
    string Id { get; }

    /// <summary>Title in Polish, e.g. "Najbogatsi".</summary>
    string Name { get; }

    /// <summary>Order in the list of rankings, smallest first.</summary>
    int Order { get; }

    /// <summary>The score as the player reads it, in Polish, e.g. "1 234,50 $".</summary>
    string Describe(long value);

    /// <summary>The best players, highest value first; players with nothing to count are left out.</summary>
    Task<IReadOnlyList<RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken);

    /// <summary>The player's value, or null when they have nothing to count yet.</summary>
    Task<long?> ValueAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>How many players have a higher value; the player's place is this plus one.</summary>
    Task<long> CountAboveAsync(long value, CancellationToken cancellationToken);
}
