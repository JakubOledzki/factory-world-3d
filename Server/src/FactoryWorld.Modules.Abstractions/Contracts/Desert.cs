namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// The sun over every plot. A game day lasts <see cref="DayLength"/>, counted from <see cref="SeaWeather.Epoch"/>:
/// the sun is up for its first <see cref="Daylight"/> and down for the rest. Solar power works by day only.
/// </summary>
public static class Sun
{
    public static readonly TimeSpan DayLength = TimeSpan.FromHours(1);

    public static readonly TimeSpan Daylight = TimeSpan.FromMinutes(36);

    private static TimeSpan Into(DateTimeOffset at) =>
        TimeSpan.FromTicks((long)(((at - SeaWeather.Epoch).Ticks % DayLength.Ticks + DayLength.Ticks) % DayLength.Ticks));

    public static bool IsDay(DateTimeOffset at) => Into(at) < Daylight;

    /// <summary>The next sunrise or sunset after <paramref name="at"/>.</summary>
    public static DateTimeOffset NextChange(DateTimeOffset at)
    {
        var into = Into(at);
        return at - into + (into < Daylight ? Daylight : DayLength);
    }
}

/// <summary>
/// Sandstorms over the desert, a calendar like <see cref="SeaWeather"/>: windows of <see cref="Window"/>, in about one
/// in seven a storm blows for <see cref="Length"/> from <see cref="Offset"/> into it. Solar power gives a quarter of
/// itself while it blows, unless a reed fence shelters it, and the dunes dug away drift back.
/// </summary>
public static class SandWeather
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(70);

    public static readonly TimeSpan Length = TimeSpan.FromMinutes(30);

    /// <summary>Chance of a sandstorm in a window, in percent.</summary>
    public const int StormPercent = 15;

    /// <summary>What solar power gives in a sandstorm without shelter, of what it gives in clear sun.</summary>
    public const double StormSun = 0.25;

    private static long WindowOf(DateTimeOffset at) => (long)Math.Floor((at - SeaWeather.Epoch).Ticks / (double)Window.Ticks);

    private static DateTimeOffset StartOf(long window) => SeaWeather.Epoch + TimeSpan.FromTicks(window * Window.Ticks);

    public static bool Stormy(long window)
    {
        var h = (ulong)window * 0x9e3779b97f4a7c15UL ^ 0x5a4d5703UL;
        h ^= h >> 31;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 29;
        return h % 100 < StormPercent;
    }

    private static (DateTimeOffset Start, DateTimeOffset End)? Storm(long window) =>
        Stormy(window) ? (StartOf(window) + Offset, StartOf(window) + Offset + Length) : null;

    public static bool IsStorm(DateTimeOffset at) => Storm(WindowOf(at)) is { } s && at >= s.Start && at < s.End;

    /// <summary>The sandstorm blowing at <paramref name="at"/>, or else the next one.</summary>
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

    /// <summary>When the last sandstorm before <paramref name="at"/> ended, or null if none did in the last <paramref name="windows"/> windows.</summary>
    public static DateTimeOffset? LastEnded(DateTimeOffset at, int windows = 2000)
    {
        for (var w = WindowOf(at); w > WindowOf(at) - windows; w--)
        {
            if (Storm(w) is { } s && s.End <= at)
            {
                return s.End;
            }
        }

        return null;
    }

    /// <summary>The next moment after <paramref name="at"/> a sandstorm begins or ends.</summary>
    public static DateTimeOffset NextChange(DateTimeOffset at)
    {
        var (start, end) = Next(at);
        return at < start ? start : end;
    }
}

/// <summary>Oases: a few desert plots have a pond in the middle, date palms around it and a little fertile soil.</summary>
public static class Oasis
{
    /// <summary>The biome number of an oasis, as the map and the client know it.</summary>
    public const int Biome = 12;

    public const int Desert = 5;

    /// <summary>Of desert plots, this many in a hundred are oases.</summary>
    public const int Percent = 4;

    /// <summary>Whether the desert plot at (x, y) is an oasis. The client works it out the same way, so keep the arithmetic in 64 bits.</summary>
    public static bool Is(int x, int y)
    {
        var h = ((long)x * 374761393 + (long)y * 668265263) & 0xffffffffL;
        h = ((h ^ (h >> 13)) * 1274126177) & 0xffffffffL;
        h ^= h >> 16;
        return h % 100 < Percent;
    }

    /// <summary>The sand of the desert and of its oases: sandstorms blow and the sun shines brighter on both.</summary>
    public static bool Sandy(int biome) => biome is Desert or Biome;
}
