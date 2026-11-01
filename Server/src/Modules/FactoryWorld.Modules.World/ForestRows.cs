namespace FactoryWorld.Modules.World;

/// <summary>A plot's watch for forest fires: fires up to <see cref="CheckedAt"/> have burnt, and the last one.</summary>
public sealed class PlotFireRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public DateTimeOffset? LastAt { get; set; }

    public int LastX { get; set; }

    public int LastY { get; set; }

    public int LastBurnt { get; set; }

    public bool LastStopped { get; set; }
}

/// <summary>Until when the trees planted on a plot are sprayed against the bark beetle.</summary>
public sealed class PlotSprayRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public DateTimeOffset Until { get; set; }
}

/// <summary>A beehive on a plot and the honey and wax waiting in it.</summary>
public sealed class HiveRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    public int Honey { get; set; }

    public int Wax { get; set; }

    public int Made { get; set; }

    public double Progress { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>Trees a player planted and cut by hand on one day (days from 1970).</summary>
public sealed class ForestLedgerRow
{
    public Guid PlayerId { get; set; }

    public int Day { get; set; }

    public int Planted { get; set; }

    public int Cut { get; set; }
}
