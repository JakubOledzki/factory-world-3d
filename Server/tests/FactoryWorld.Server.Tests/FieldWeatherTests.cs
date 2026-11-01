using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Server.Tests;

public sealed class FieldWeatherTests
{
    private static readonly DateTimeOffset Start = SeaWeather.Epoch + TimeSpan.FromDays(300);

    private static DateTimeOffset First(FieldWeather.Kind kind)
    {
        for (var at = Start; ; at += FieldWeather.Window)
        {
            var (start, _, found) = FieldWeather.Next(at);
            if (found == kind)
            {
                return start;
            }
        }
    }

    [Fact]
    public void It_rains_in_about_one_window_in_five_and_a_drought_comes_in_about_one_in_ten()
    {
        var kinds = Enumerable.Range(0, 2000).Select(w => FieldWeather.Of(w)).ToList();
        Assert.InRange(kinds.Count(k => k == FieldWeather.Kind.Rain), 300, 500);
        Assert.InRange(kinds.Count(k => k == FieldWeather.Kind.Drought), 120, 280);

        var rain = First(FieldWeather.Kind.Rain);
        Assert.Equal(FieldWeather.Kind.Rain, FieldWeather.At(rain));
        Assert.Equal(FieldWeather.RainSpeed, FieldWeather.Speed(rain + TimeSpan.FromMinutes(1)));
        Assert.Equal(FieldWeather.Kind.Clear, FieldWeather.At(rain + FieldWeather.RainLength));
        var drought = First(FieldWeather.Kind.Drought);
        Assert.Equal(FieldWeather.DroughtSpeed, FieldWeather.Speed(drought + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Crops_sown_in_the_rain_are_ripe_sooner_and_in_a_drought_later()
    {
        var grow = TimeSpan.FromMinutes(30);
        var rain = First(FieldWeather.Kind.Rain);
        Assert.Equal(rain + grow / FieldWeather.RainSpeed, FieldWeather.Ripe(rain, grow));
        var drought = First(FieldWeather.Kind.Drought);
        Assert.Equal(drought + grow / FieldWeather.DroughtSpeed, FieldWeather.Ripe(drought, grow));

        // Sown just before the rain: clear weather first, then faster.
        var before = rain - TimeSpan.FromMinutes(10);
        Assert.Equal(rain + TimeSpan.FromMinutes(20) / FieldWeather.RainSpeed, FieldWeather.Ripe(before, grow));

        // A crop that grows longer than the rain lasts gains only the rain's part.
        var longer = TimeSpan.FromHours(2);
        var ripe = FieldWeather.Ripe(rain, longer);
        Assert.InRange(ripe, rain + longer / FieldWeather.RainSpeed + TimeSpan.FromSeconds(1), rain + longer / FieldWeather.DroughtSpeed);
    }
}
