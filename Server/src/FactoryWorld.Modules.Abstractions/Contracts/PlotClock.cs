namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// A plot works only while someone it works for is in the game: its owner, or, for a plot brought into a company,
/// any member of the company. A player who drops out keeps it working for <see cref="Grace"/>, so a short
/// disconnection changes nothing. While nobody is there, the plot's time stands still.
/// </summary>
public static class PlotClockRules
{
    /// <summary>How long a plot keeps working after the last of its players left the game.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);

    /// <summary>How often who is in the game is checked against the plots.</summary>
    public static readonly TimeSpan Check = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The time of a plot: the clock less every stretch the plot stood still (<see cref="PlotClockRules"/>). Everything a
/// plot produces by itself (belts, inserters, machines, furnaces, power, mines, drills, wells, fields, animals,
/// forests, hives) runs on it, so standing still is a pause: nothing is made and nothing runs out, and the plot
/// goes on from there when its players come back.
/// </summary>
public interface IPlotClock
{
    /// <summary>The plot's time now; never ahead of the clock.</summary>
    Task<DateTimeOffset> NowAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>The plot's time now for the plot an object stands on; the clock for an object on no plot.</summary>
    Task<DateTimeOffset> NowOfAsync(Guid objectId, CancellationToken cancellationToken);
}
