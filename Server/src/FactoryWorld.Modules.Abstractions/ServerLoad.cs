using System.Diagnostics;

namespace FactoryWorld.Modules.Abstractions;

/// <summary>
/// How long the server spends on each kind of message, from players and from bots, so the log can say once a
/// minute what keeps it busy and which messages were slow, and <c>/metrics</c> can show the same over time.
/// Kept in memory; nothing is saved.
/// </summary>
public sealed class ServerLoad
{
    /// <summary>A single message taking longer than this is logged on its own.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private Dictionary<string, Tally> _tallies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Overall> _totals = new(StringComparer.Ordinal);

    /// <summary>The upper bounds, in seconds, of the time buckets <see cref="Totals"/> counts messages in.</summary>
    public static readonly double[] Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    /// <summary>The time from now, to pass to <see cref="Record"/> when the work is done.</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>Counts one message of <paramref name="type"/> that started at <paramref name="started"/>; returns how long it took.</summary>
    public TimeSpan Record(string type, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        lock (_lock)
        {
            if (!_tallies.TryGetValue(type, out var tally))
            {
                _tallies[type] = tally = new Tally(type);
            }

            tally.Count++;
            tally.Total += elapsed;
            if (elapsed > tally.Max)
            {
                tally.Max = elapsed;
            }

            if (!_totals.TryGetValue(type, out var total))
            {
                _totals[type] = total = new Overall(type);
            }

            total.Add(elapsed.TotalSeconds);
        }

        return elapsed;
    }

    /// <summary>What was counted since the last call, the busiest kinds first; starts counting anew.</summary>
    public IReadOnlyList<Tally> Take()
    {
        Dictionary<string, Tally> taken;
        lock (_lock)
        {
            taken = _tallies;
            _tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        }

        return taken.Values.OrderByDescending(t => t.Total).ToList();
    }

    /// <summary>Everything counted since the server started, by kind of message; never reset.</summary>
    public IReadOnlyList<Overall> Totals()
    {
        lock (_lock)
        {
            return _totals.Values.Select(t => t.Copy()).OrderBy(t => t.Type, StringComparer.Ordinal).ToList();
        }
    }

    public sealed class Tally(string type)
    {
        public string Type { get; } = type;

        public long Count { get; set; }

        public TimeSpan Total { get; set; }

        public TimeSpan Max { get; set; }
    }

    public sealed class Overall(string type)
    {
        public string Type { get; } = type;

        public long Count { get; private set; }

        public double Seconds { get; private set; }

        /// <summary>How many messages took at most each of <see cref="Buckets"/>, counted cumulatively.</summary>
        public long[] InBucket { get; private init; } = new long[Buckets.Length];

        internal void Add(double seconds)
        {
            Count++;
            Seconds += seconds;
            for (var i = Buckets.Length - 1; i >= 0 && seconds <= Buckets[i]; i--)
            {
                InBucket[i]++;
            }
        }

        internal Overall Copy() => new(Type) { Count = Count, Seconds = Seconds, InBucket = (long[])InBucket.Clone() };
    }
}
