using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Statistics;

/// <summary>
/// How well a factory works for its size: the value its production added in the last hour (what it made less what it
/// used up, both at the trader's usual buying prices), split over the plots the player owns. A small, well-built factory
/// beats a sprawling one that wastes its land.
/// </summary>
public static class Efficiency
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>The scores are worked out for everyone at once and kept this long.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(1);

    /// <summary>Cents of value added an hour per plot; a player without plots counts as having one (the garage).</summary>
    public static long PerPlot(long addedCents, int plots) => addedCents / Math.Max(1, plots);
}

/// <summary>The last scores of <see cref="EfficiencyRanking"/>, for every player at once.</summary>
public sealed class EfficiencyScores
{
    public readonly Lock Gate = new();

    public DateTimeOffset At { get; set; } = DateTimeOffset.MinValue;

    public IReadOnlyList<RankedPlayer> Sorted { get; set; } = [];
}

/// <summary>The ranking "Najwydajniejsze fabryki": value added an hour per plot.</summary>
public sealed class EfficiencyRanking(GameDb db, IPlotRegistry plots, IMarketPrices prices, EfficiencyScores scores, TimeProvider time) : IPlayerRanking
{
    public string Id => "efficiency";

    public string Name => "Najwydajniejsze fabryki";

    public int Order => 35;

    public string Describe(long value) => $"{Polish.Money(value)}/h na działkę";

    public async Task<IReadOnlyList<RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken) =>
        (await SortedAsync(cancellationToken)).Take(count).ToList();

    public async Task<long?> ValueAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await SortedAsync(cancellationToken)).FirstOrDefault(p => p.AccountId == accountId)?.Value;

    public async Task<long> CountAboveAsync(long value, CancellationToken cancellationToken) =>
        (await SortedAsync(cancellationToken)).Count(p => p.Value > value);

    private async Task<IReadOnlyList<RankedPlayer>> SortedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        lock (scores.Gate)
        {
            if (now - scores.At < Efficiency.Kept && now >= scores.At)
            {
                return scores.Sorted;
            }
        }

        var added = new Dictionary<Guid, long>();
        await using (var command = await db.CommandAsync(
            """
            SELECT owner_id, item, sum(produced)::bigint, sum(consumed)::bigint FROM stats.production
            WHERE minute >= @start GROUP BY owner_id, item
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("start", now - Efficiency.Window);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (prices.BuysCents(reader.GetString(1)) is { } price)
                {
                    var owner = reader.GetGuid(0);
                    added[owner] = added.GetValueOrDefault(owner) + (reader.GetInt64(2) - reader.GetInt64(3)) * price;
                }
            }
        }

        var owned = await plots.CountsAsync(cancellationToken);
        var sorted = added
            .Select(a => new RankedPlayer(a.Key, Efficiency.PerPlot(a.Value, owned.GetValueOrDefault(a.Key))))
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value).ThenBy(p => p.AccountId)
            .ToList();
        lock (scores.Gate)
        {
            scores.At = now;
            scores.Sorted = sorted;
        }

        return sorted;
    }
}

internal static class Polish
{
    /// <summary>Cents as "1 234,50 $", like the client shows money.</summary>
    public static string Money(long cents)
    {
        var digits = Math.Abs(cents / 100).ToString();
        var grouped = new System.Text.StringBuilder(cents < 0 ? "-" : "");
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                grouped.Append(' ');
            }

            grouped.Append(digits[i]);
        }

        return $"{grouped},{Math.Abs(cents % 100):00} $";
    }
}
