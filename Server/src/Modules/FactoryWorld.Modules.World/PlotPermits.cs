using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Leave to help on someone else's plot, in memory: one per plot, for one helper, until it runs out or the last task is
/// done. Who may do what with it is <see cref="PlotObjectService"/>'s to check.
/// </summary>
public sealed class PlotPermits(TimeProvider time) : IPlotPermits
{
    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), PlotPermit> _permits = [];

    public void Grant(PlotPermit permit)
    {
        lock (_lock)
        {
            _permits[(permit.X, permit.Y)] = permit;
        }
    }

    public void Revoke(int x, int y)
    {
        lock (_lock)
        {
            _permits.Remove((x, y));
        }
    }

    public PlotPermit? Find(int x, int y)
    {
        lock (_lock)
        {
            if (!_permits.TryGetValue((x, y), out var permit))
            {
                return null;
            }

            if (permit.Until > time.GetUtcNow())
            {
                return permit;
            }

            _permits.Remove((x, y));
            return null;
        }
    }

    /// <summary>The player's leave on the plot, if they have one.</summary>
    public PlotPermit? For(Guid player, int x, int y) => Find(x, y) is { } permit && permit.Helper == player ? permit : null;

    /// <summary>Takes this very leave off the plot for the helper who finished it, so that it pays once; false when it is gone.</summary>
    public bool Take(PlotPermit permit)
    {
        lock (_lock)
        {
            return _permits.TryGetValue((permit.X, permit.Y), out var standing) && ReferenceEquals(standing, permit) && _permits.Remove((permit.X, permit.Y));
        }
    }

    /// <summary>Puts back a leave whose payment failed, unless another took its place meanwhile.</summary>
    public void Restore(PlotPermit permit)
    {
        lock (_lock)
        {
            _permits.TryAdd((permit.X, permit.Y), permit);
        }
    }

    /// <summary>Whether <paramref name="kind"/> may stand on the tile under the leave: some task puts one there.</summary>
    public static HelpTask? PlaceTask(PlotPermit permit, string kind, int tileX, int tileY) =>
        permit.Tasks.FirstOrDefault(t => t.Object is null && t.Kind == kind && t.TileX == tileX && t.TileY == tileY);

    /// <summary>Whether the object may be turned under the leave: a task names it, or it stands where a task puts one of its kind.</summary>
    public static bool MayTurn(PlotPermit permit, PlacedOnPlot placed) =>
        permit.Tasks.Any(t => t.Object == placed.Id || t.Object is null && t.Kind == placed.Kind && t.TileX == placed.TileX && t.TileY == placed.TileY);

    /// <summary>How many tasks are done with the plot's objects as they stand.</summary>
    public static int Done(PlotPermit permit, IReadOnlyList<PlacedOnPlot> objects) =>
        permit.Tasks.Count(t => objects.Any(o =>
            (t.Object is null ? o.Kind == t.Kind && o.TileX == t.TileX && o.TileY == t.TileY : o.Id == t.Object) && o.Rotation == t.Rotation));

    public static object Json(PlotPermit permit, IReadOnlyList<PlacedOnPlot> objects) => new
    {
        tasks = permit.Tasks.Select(t => new
        {
            kind = t.Kind,
            name = Items.Names.GetValueOrDefault(t.Kind, t.Kind),
            tileX = t.TileX,
            tileY = t.TileY,
            rotation = t.Rotation,
            id = t.Object,
            done = objects.Any(o =>
                (t.Object is null ? o.Kind == t.Kind && o.TileX == t.TileX && o.TileY == t.TileY : o.Id == t.Object) && o.Rotation == t.Rotation),
        }),
        payCents = permit.PayCents,
        untilMs = permit.Until.ToUnixTimeMilliseconds(),
    };
}
