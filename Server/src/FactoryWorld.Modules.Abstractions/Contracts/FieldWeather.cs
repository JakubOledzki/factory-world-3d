namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// Rain and drought over the fields, a calendar like <see cref="SeaWeather"/>: windows of <see cref="Window"/>, counted from
/// <see cref="SeaWeather.Epoch"/>. In about one window in five it rains for <see cref="RainLength"/> from <see cref="RainOffset"/>
/// into it, and crops grow <see cref="RainSpeed"/> times as fast; in about one in ten a drought lasts the whole window, and they
/// grow at <see cref="DroughtSpeed"/>. The calendar follows from time alone, so a crop's ripening is worked out when it is sown.
/// </summary>
public static class FieldWeather
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(2);

    public static readonly TimeSpan RainOffset = TimeSpan.FromMinutes(20);

    public static readonly TimeSpan RainLength = TimeSpan.FromMinutes(60);

    /// <summary>Chance of rain in a window, in percent, and of a drought.</summary>
    public const int RainPercent = 20;

    public const int DroughtPercent = 10;

    public const double RainSpeed = 1.5;

    public const double DroughtSpeed = 0.5;

    public enum Kind
    {
        Clear,
        Rain,
        Drought,
    }

    private static long WindowOf(DateTimeOffset at) => (long)Math.Floor((at - SeaWeather.Epoch).Ticks / (double)Window.Ticks);

    private static DateTimeOffset StartOf(long window) => SeaWeather.Epoch + TimeSpan.FromTicks(window * Window.Ticks);

    /// <summary>The weather of a window: rain, a drought or neither.</summary>
    public static Kind Of(long window)
    {
        var h = (ulong)window * 0x9e3779b97f4a7c15UL ^ 0x7261696eUL;
        h ^= h >> 31;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 29;
        var roll = h % 100;
        return roll < RainPercent ? Kind.Rain : roll < RainPercent + DroughtPercent ? Kind.Drought : Kind.Clear;
    }

    /// <summary>The rain or the drought of a window, or null when it has neither.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End, Kind Kind)? Spell(long window) => Of(window) switch
    {
        Kind.Rain => (StartOf(window) + RainOffset, StartOf(window) + RainOffset + RainLength, Kind.Rain),
        Kind.Drought => (StartOf(window), StartOf(window + 1), Kind.Drought),
        _ => null,
    };

    /// <summary>The weather over the fields at <paramref name="at"/>.</summary>
    public static Kind At(DateTimeOffset at) => Spell(WindowOf(at)) is { } s && at >= s.Start && at < s.End ? s.Kind : Kind.Clear;

    /// <summary>How many times as fast crops grow at <paramref name="at"/>.</summary>
    public static double Speed(DateTimeOffset at) => At(at) switch
    {
        Kind.Rain => RainSpeed,
        Kind.Drought => DroughtSpeed,
        _ => 1,
    };

    /// <summary>The spell of rain or drought going on at <paramref name="at"/>, or else the next one.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End, Kind Kind) Next(DateTimeOffset at)
    {
        for (var w = WindowOf(at); ; w++)
        {
            if (Spell(w) is { } s && s.End > at)
            {
                return s;
            }
        }
    }

    /// <summary>When a crop sown at <paramref name="from"/> that needs <paramref name="grow"/> in clear weather is ripe: faster
    /// through the rain, slower through a drought.</summary>
    public static DateTimeOffset Ripe(DateTimeOffset from, TimeSpan grow)
    {
        var at = from;
        var left = grow;
        while (left > TimeSpan.Zero)
        {
            var (start, end, kind) = Next(at);
            if (at < start)
            {
                // Clear weather up to the next spell.
                if (left <= start - at)
                {
                    return at + left;
                }

                left -= start - at;
                at = start;
            }

            var speed = kind == Kind.Rain ? RainSpeed : DroughtSpeed;
            var work = (end - at) * speed;
            if (left <= work)
            {
                return at + left / speed;
            }

            left -= work;
            at = end;
        }

        return at;
    }
}
