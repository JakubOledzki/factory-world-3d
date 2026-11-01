namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// Storms over the coasts and lakes. Time is cut into windows of <see cref="Window"/>; in about one window
/// in eight a storm blows for <see cref="Length"/> from <see cref="Offset"/> into it. The calendar follows
/// from time alone, the same on every server, so nothing is stored: boats stay in port while it blows
/// (<see cref="SeaClock"/>), nobody fishes with a rod without a breakwater, and the sea throws up three
/// times as much on the beach the day after.
/// </summary>
public static class SeaWeather
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);

    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan Length = TimeSpan.FromMinutes(40);

    /// <summary>Chance of a storm in a window, in percent.</summary>
    public const int StormPercent = 12;

    /// <summary>Boats count their time from here; any fixed moment before the game began would do.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static long WindowOf(DateTimeOffset at) => (long)Math.Floor((at - Epoch).Ticks / (double)Window.Ticks);

    private static DateTimeOffset StartOf(long window) => Epoch + TimeSpan.FromTicks(window * Window.Ticks);

    /// <summary>Whether a storm blows in a window.</summary>
    public static bool Stormy(long window)
    {
        var h = (ulong)window * 0x9e3779b97f4a7c15UL ^ 0x5eaf00d5UL;
        h ^= h >> 31;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 29;
        return h % 100 < StormPercent;
    }

    /// <summary>The storm of a window, or null when it is calm.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End)? Storm(long window) =>
        Stormy(window) ? (StartOf(window) + Offset, StartOf(window) + Offset + Length) : null;

    public static bool IsStorm(DateTimeOffset at) => Storm(WindowOf(at)) is { } s && at >= s.Start && at < s.End;

    /// <summary>The storm blowing at <paramref name="at"/>, or else the next one.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Next(DateTimeOffset at)
    {
        for (var w = WindowOf(at); ; w++)
        {
            if (Storm(w) is { } s && s.End > at)
            {
                return s;
            }
        }
    }

    /// <summary>How long storms blew between two moments.</summary>
    public static TimeSpan StormTime(DateTimeOffset from, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;
        for (var w = WindowOf(from); w <= WindowOf(to); w++)
        {
            if (Storm(w) is { } s)
            {
                var start = s.Start > from ? s.Start : from;
                var end = s.End < to ? s.End : to;
                if (end > start)
                {
                    total += end - start;
                }
            }
        }

        return total;
    }

    /// <summary>Whether a storm blew on the day before <paramref name="day"/> (days counted from the Unix epoch, UTC).</summary>
    public static bool StormBefore(int day)
    {
        var start = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(day - 1);
        return StormTime(start, start + TimeSpan.FromDays(1)) > TimeSpan.Zero;
    }

    /// <summary>The day of a moment, counted from the Unix epoch in UTC.</summary>
    public static int Day(DateTimeOffset at) => (int)((at - DateTimeOffset.UnixEpoch).Ticks / TimeSpan.TicksPerDay);
}

/// <summary>A boat's own time: the clock less every storm since <see cref="SeaWeather.Epoch"/>, so it stands still while one blows.</summary>
public sealed class SeaClock : IPowerClock
{
    public static readonly SeaClock Instance = new();

    public DateTimeOffset At(DateTimeOffset now) => now - SeaWeather.StormTime(SeaWeather.Epoch, now);

    public double Speed(DateTimeOffset now) => SeaWeather.IsStorm(now) ? 0 : 1;

    public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now)
    {
        var at = now;
        var behind = machineTime - At(now);
        while (behind > TimeSpan.Zero)
        {
            var (start, end) = SeaWeather.Next(at);
            if (at >= start)
            {
                at = end;
                continue;
            }

            var calm = start - at;
            if (behind <= calm)
            {
                return at + behind;
            }

            behind -= calm;
            at = end;
        }

        return at;
    }
}
