namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// The wind over every plot. Time is cut into windows of <see cref="Window"/>, counted from <see cref="SeaWeather.Epoch"/>;
/// in each the wind blows with one strength, 0 to 1 of what a wind turbine needs for its full power. The calendar
/// follows from time alone, so the grid knows in advance what its turbines give.
/// </summary>
public static class Wind
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The strengths the wind blows with, one picked for each window: calm is rare, a good wind common.</summary>
    private static readonly double[] Strengths = [0.1, 0.3, 0.45, 0.6, 0.6, 0.75, 0.75, 0.9, 1, 1];

    /// <summary>Biomes (by the world's numbers) where the wind blows stronger, and by how much: on the steppe and the
    /// tundra +15%, on the glacier +20%, on the sea coast +10%.</summary>
    public static readonly IReadOnlyDictionary<int, double> Biomes = new Dictionary<int, double>
    {
        [4] = 1.15,
        [7] = 1.15,
        [8] = 1.2,
        [10] = 1.1,
    };

    public static double Bonus(int biome) => Biomes.GetValueOrDefault(biome, 1);

    private static long WindowOf(DateTimeOffset at) => (long)Math.Floor((at - SeaWeather.Epoch).Ticks / (double)Window.Ticks);

    /// <summary>How strongly the wind blows at <paramref name="at"/>, 0 to 1.</summary>
    public static double Strength(DateTimeOffset at)
    {
        var h = (ulong)WindowOf(at) * 0x9e3779b97f4a7c15UL ^ 0x77696e64UL;
        h ^= h >> 31;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 29;
        return Strengths[h % (ulong)Strengths.Length];
    }

    /// <summary>When the wind next changes after <paramref name="at"/>.</summary>
    public static DateTimeOffset NextChange(DateTimeOffset at) => SeaWeather.Epoch + TimeSpan.FromTicks((WindowOf(at) + 1) * Window.Ticks);
}

/// <summary>
/// Blizzards over the glacier, a calendar like <see cref="SandWeather"/>: windows of <see cref="Window"/>, in about one in
/// six a blizzard blows for <see cref="Length"/> from <see cref="Offset"/> into it. Wind turbines stop while it blows, so
/// the gale does not break them; after it the ice dug away has frozen again and avalanches leave snowdrifts about.
/// </summary>
public static class Blizzard
{
    /// <summary>The biome number of the glacier, as the map and the client know it.</summary>
    public const int Biome = 8;

    public static readonly TimeSpan Window = TimeSpan.FromHours(4);

    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(100);

    public static readonly TimeSpan Length = TimeSpan.FromMinutes(30);

    /// <summary>Chance of a blizzard in a window, in percent.</summary>
    public const int StormPercent = 18;

    /// <summary>Of the tiles of a glacier plot, this many in a thousand an avalanche covers with snow in each blizzard.</summary>
    public const int DriftPerMille = 15;

    private static long WindowOf(DateTimeOffset at) => (long)Math.Floor((at - SeaWeather.Epoch).Ticks / (double)Window.Ticks);

    private static DateTimeOffset StartOf(long window) => SeaWeather.Epoch + TimeSpan.FromTicks(window * Window.Ticks);

    public static bool Stormy(long window)
    {
        var h = (ulong)window * 0x9e3779b97f4a7c15UL ^ 0x1ce5707dUL;
        h ^= h >> 31;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 29;
        return h % 100 < StormPercent;
    }

    private static (DateTimeOffset Start, DateTimeOffset End)? Storm(long window) =>
        Stormy(window) ? (StartOf(window) + Offset, StartOf(window) + Offset + Length) : null;

    public static bool IsStorm(DateTimeOffset at) => Storm(WindowOf(at)) is { } s && at >= s.Start && at < s.End;

    /// <summary>The blizzard blowing at <paramref name="at"/>, or else the next one.</summary>
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

    /// <summary>The last blizzard over before <paramref name="at"/> (its window, to tell avalanches apart, and when it ended), or null if none
    /// was in the last <paramref name="windows"/> windows.</summary>
    public static (long Window, DateTimeOffset End)? LastEnded(DateTimeOffset at, int windows = 2000)
    {
        for (var w = WindowOf(at); w > WindowOf(at) - windows; w--)
        {
            if (Storm(w) is { } s && s.End <= at)
            {
                return (w, s.End);
            }
        }

        return null;
    }

    /// <summary>The next moment after <paramref name="at"/> a blizzard begins or ends.</summary>
    public static DateTimeOffset NextChange(DateTimeOffset at)
    {
        var (start, end) = Next(at);
        return at < start ? start : end;
    }

    /// <summary>Whether the avalanches of the blizzard of <paramref name="window"/> left snow on a tile of a plot. The client works it
    /// out the same way, so keep the arithmetic in 64 bits.</summary>
    public static bool Drift(long window, int plotX, int plotY, int tileX, int tileY)
    {
        var h = (window * 0x2545F491L + plotX * 374761393L + plotY * 668265263L + tileX * 2246822519L + tileY * 3266489917L) & 0xffffffffL;
        h = ((h ^ (h >> 15)) * 2246822519L) & 0xffffffffL;
        h = ((h ^ (h >> 13)) * 3266489917L) & 0xffffffffL;
        h ^= h >> 16;
        return h % 1000 < DriftPerMille;
    }
}

/// <summary>
/// The cold of the glacier: machines on power that no heater warms work at <see cref="Speed"/> of their pace. A heater
/// burns its fuel at <see cref="HeaterKw"/> all the time, and warms the tiles <see cref="HeaterReach"/> from it each way.
/// </summary>
public static class Frost
{
    public const double Speed = 0.5;

    public const double HeaterKw = 15;

    public const int HeaterReach = 3;

    public static bool Freezes(int biome) => biome == Blizzard.Biome;
}

/// <summary>
/// Northern lights over the glacier and the tundra: on some nights (the dark part of a game day of <see cref="Sun"/>)
/// the aurora shines all night long. Tourists come to see it: hotels and viewpoints earn <see cref="Rush"/> times as much while it shines.
/// </summary>
public static class Aurora
{
    /// <summary>Of nights, this many in a hundred have the aurora.</summary>
    public const int Percent = 40;

    public const double Rush = 3;

    /// <summary>Whether the aurora can be seen over a plot of <paramref name="biome"/>: the tundra and the glacier.</summary>
    public static bool Seen(int biome) => biome is 7 or 8;

    private static long DayOf(DateTimeOffset at) => (long)Math.Floor((at - SeaWeather.Epoch).Ticks / (double)Sun.DayLength.Ticks);

    private static DateTimeOffset StartOf(long day) => SeaWeather.Epoch + TimeSpan.FromTicks(day * Sun.DayLength.Ticks);

    public static bool Shines(long day)
    {
        var h = (ulong)day * 0x9e3779b97f4a7c15UL ^ 0xa0e0a0e0UL;
        h ^= h >> 31;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 29;
        return h % 100 < Percent;
    }

    /// <summary>The aurora of a day: the whole night, or null when the night is dark.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End)? Night(long day) =>
        Shines(day) ? (StartOf(day) + Sun.Daylight, StartOf(day + 1)) : null;

    public static bool IsShining(DateTimeOffset at) => Night(DayOf(at)) is { } n && at >= n.Start && at < n.End;

    /// <summary>The aurora shining at <paramref name="at"/>, or else the next one.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Next(DateTimeOffset at)
    {
        for (var d = DayOf(at); ; d++)
        {
            if (Night(d) is { } n && n.End > at)
            {
                return n;
            }
        }
    }

    /// <summary>How long the aurora shone between two moments.</summary>
    public static TimeSpan ShineTime(DateTimeOffset from, DateTimeOffset to)
    {
        var total = TimeSpan.Zero;
        for (var d = DayOf(from); d <= DayOf(to); d++)
        {
            if (Night(d) is { } n)
            {
                var start = n.Start > from ? n.Start : from;
                var end = n.End < to ? n.End : to;
                if (end > start)
                {
                    total += end - start;
                }
            }
        }

        return total;
    }
}

/// <summary>A tourist building's own time: it runs <see cref="Aurora.Rush"/> times as fast while the aurora shines, so it earns more then.</summary>
public sealed class AuroraClock : IPowerClock
{
    public static readonly AuroraClock Instance = new();

    /// <summary>Counted from here; earlier than any building can stand.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset At(DateTimeOffset now) => now + (Aurora.Rush - 1) * Aurora.ShineTime(Start, now);

    public double Speed(DateTimeOffset now) => Aurora.IsShining(now) ? Aurora.Rush : 1;

    public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now)
    {
        var at = now;
        var behind = machineTime - At(now);
        while (behind > TimeSpan.Zero)
        {
            var (start, end) = Aurora.Next(at);
            if (at < start)
            {
                if (behind <= start - at)
                {
                    return at + behind;
                }

                behind -= start - at;
                at = start;
                continue;
            }

            var bright = (end - at) * Aurora.Rush;
            if (behind <= bright)
            {
                return at + behind / Aurora.Rush;
            }

            behind -= bright;
            at = end;
        }

        return at;
    }
}
