using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Logistics;

/// <summary>
/// The rules of belts and inserters. The logistics of a plot run in steps of <see cref="Tick"/>,
/// worked out when someone looks at the plot or changes it, for all the time since the last look.
/// </summary>
public static class LogisticsRules
{
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    /// <summary>Places for items along one lane of a belt tile; an item moves one place per tick.</summary>
    public const int BeltSlots = 4;

    /// <summary>Lanes side by side on a belt, the left one (as the belt runs) first.</summary>
    public const int BeltLanes = 2;

    /// <summary>Ticks the arm of a plain inserter takes to swing from where it picks up to where it drops, and as many back.</summary>
    public const int SwingTicks = 4;

    /// <summary>Belts of every level and how many places an item moves along them in a tick.</summary>
    public static readonly IReadOnlyDictionary<string, int> BeltSpeeds = new Dictionary<string, int>
    {
        [Items.Belt] = 1,
        [Items.FastBelt] = 2,
        [Items.ExpressBelt] = 3,
        [Items.CableStation] = 2,
        [Items.ExpressCableStation] = 3,
        // Fluids run fast through pipes.
        [Items.Pipe] = 3,
    };

    /// <summary>Tiles a cable runs at most from one station to the next one ahead.</summary>
    public const int CableSpan = 24;

    /// <summary>Tiles the cable of an express station runs at most.</summary>
    public const int ExpressCableSpan = 48;

    public static int Span(string station) => station == Items.ExpressCableStation ? ExpressCableSpan : CableSpan;

    /// <summary>Which station each cable station sends its items to over the cable: the nearest one (of
    /// either level) ahead of it, facing the same way, at least two tiles off and no further than its <see cref="Span"/>.</summary>
    public static Dictionary<Guid, Guid> CableLinks(IEnumerable<PlotObject> objects)
    {
        var stations = objects.Where(o => Items.IsCableStation(o.Kind)).ToDictionary(o => (o.TileX, o.TileY));
        var links = new Dictionary<Guid, Guid>();
        foreach (var station in stations.Values)
        {
            var (dx, dy) = Facing(station.Rotation);
            for (var d = 2; d <= Span(station.Kind); d++)
            {
                if (stations.TryGetValue((station.TileX + dx * d, station.TileY + dy * d), out var other) && other.Rotation == station.Rotation)
                {
                    links[station.Id] = other.Id;
                    break;
                }
            }
        }

        return links;
    }

    /// <summary>Tiles an underground belt reaches at most to the one that brings its items up again: up to 4 tiles between them.</summary>
    public const int TunnelSpan = 5;

    /// <summary>Where each underground belt that takes items down sends them: to the nearest underground belt
    /// ahead of it, facing the same way, no further than <see cref="TunnelSpan"/>, which brings them up. Along a
    /// line of them they pair up from the back, one going down, the next coming up.</summary>
    public static Dictionary<Guid, Guid> TunnelLinks(IEnumerable<PlotObject> objects)
    {
        var tunnels = objects.Where(o => o.Kind == Items.UndergroundBelt).ToDictionary(o => (o.TileX, o.TileY));
        var links = new Dictionary<Guid, Guid>();
        var exits = new HashSet<Guid>();
        // The furthest back along the way they run first.
        foreach (var entry in tunnels.Values.OrderBy(o => Facing(o.Rotation).Dx * o.TileX + Facing(o.Rotation).Dy * o.TileY).ThenBy(o => o.Id))
        {
            if (exits.Contains(entry.Id))
            {
                continue;
            }

            var (dx, dy) = Facing(entry.Rotation);
            for (var d = 1; d <= TunnelSpan; d++)
            {
                if (tunnels.TryGetValue((entry.TileX + dx * d, entry.TileY + dy * d), out var exit) && exit.Rotation == entry.Rotation)
                {
                    links[entry.Id] = exit.Id;
                    exits.Add(exit.Id);
                    break;
                }
            }
        }

        return links;
    }

    /// <summary>Inserters of every level and the ticks their arm takes to swing one way.</summary>
    public static readonly IReadOnlyDictionary<string, int> InserterSwings = new Dictionary<string, int>
    {
        [Items.Inserter] = SwingTicks,
        [Items.FastInserter] = 2,
        [Items.ExpressInserter] = 1,
        [Items.LongInserter] = 3,
        [Items.Pump] = 1,
    };

    /// <summary>Tiles away an inserter picks up behind it and drops in front: two for the long-armed one.</summary>
    public static int Reach(string kind) => kind == Items.LongInserter ? 2 : 1;

    /// <summary>Pieces of one item an inserter carries at once: more once its owner researched bigger grippers. Pumps move one unit.</summary>
    public static int Grip(string kind, bool bigger, bool industrial) =>
        kind == Items.Pump ? 1 : industrial ? 3 : bigger ? 2 : 1;

    /// <summary>Pieces a port of drones flies to the ports that want them each tick, while it has power.</summary>
    public const int DroneLoad = 1;

    /// <summary>Places a tick an item moves along a belt of this kind; a splitter's own speed comes from the belt feeding it.</summary>
    public static int BeltSpeed(string kind) => BeltSpeeds.GetValueOrDefault(kind, 1);

    public static int Swing(string kind) => InserterSwings.GetValueOrDefault(kind, SwingTicks);

    /// <summary>Ticks of work done in one catch-up without a pause: 30 minutes. Machinery that keeps busy for longer
    /// while nobody looks (e.g. items going round a loop of belts) rests for the rest of the time; machinery that
    /// pauses now and then (e.g. an inserter waiting for room by a generator's fire), or that something is taken out
    /// of a machine for now and then (e.g. wood out of a forester), works on for as long as it takes.</summary>
    public const int MaxTicks = 9000;

    /// <summary>The tile a rotation faces, as (dx, dy): 0 south, 1 east, 2 north, 3 west.</summary>
    public static (int Dx, int Dy) Facing(int rotation) => (rotation & 3) switch
    {
        0 => (0, 1),
        1 => (1, 0),
        2 => (0, -1),
        _ => (-1, 0),
    };

    /// <summary>The tile on the left of something going the way a rotation faces.</summary>
    public static (int Dx, int Dy) Left(int rotation)
    {
        var (dx, dy) = Facing(rotation);
        return (dy, -dx);
    }
}

/// <summary>What a belt, an inserter or a storage holds; kept as JSON, one row per object.</summary>
public sealed class Contents
{
    /// <summary>A belt's places, lane after lane (left, then right), each from where items come on
    /// to where they leave; null is empty.</summary>
    public string?[]? Belt { get; set; }

    /// <summary>The item in an inserter's hand.</summary>
    public string? Hand { get; set; }

    /// <summary>How many pieces of <see cref="Hand"/> it holds when more than one (bigger grippers); 0 is one.</summary>
    public int Count { get; set; }

    /// <summary>Pieces in the inserter's hand.</summary>
    public int Held() => Hand is null ? 0 : Math.Max(1, Count);

    /// <summary>How far the inserter's arm is, 0 (at the pick-up side) to its swing ticks (<see cref="LogisticsRules.Swing"/>).</summary>
    public int Swing { get; set; }

    /// <summary>How much of its next move an inserter on too little power has the power for, 0 to 1.</summary>
    public double Charge { get; set; }

    /// <summary>What a storage holds.</summary>
    public List<ItemStack>? Items { get; set; }

    /// <summary>A splitter's filter: these items go out on <see cref="FilterSide"/> only, the rest on
    /// the other side. Without one it sends items left and right in turn.</summary>
    public List<string>? Filter { get; set; }

    /// <summary>The one item an inserter, a storage or a machine is set to take (its filter): an inserter
    /// picks up nothing else, a storage or a machine lets nothing else in. Null takes anything.</summary>
    public string? Only { get; set; }

    /// <summary>An inserter's or a pump's condition: it drops nothing more into the storage in front while that holds this
    /// many of the item (fills it up to this many). 0 for none.</summary>
    public int Below { get; set; }

    /// <summary>An inserter's or a pump's condition: it leaves this many of each item in the storage behind it (keeps a
    /// reserve there). 0 for none.</summary>
    public int Keep { get; set; }

    /// <summary>The side a splitter sends filtered items to: 0 left, 1 right (as the splitter runs).</summary>
    public int FilterSide { get; set; }

    /// <summary>The side a splitter without a filter sends the next item to.</summary>
    public int Turn { get; set; }

    /// <summary>The stack of a storage inserters take from next: they go round all of them in turn,
    /// so ore and coal stored together both reach a furnace.</summary>
    public int Next { get; set; }

    /// <summary>Everything in it, for handing back to the player.</summary>
    public IEnumerable<(string Item, int Quantity)> All()
    {
        foreach (var item in Belt ?? [])
        {
            if (item is not null)
            {
                yield return (item, 1);
            }
        }

        if (Hand is not null)
        {
            yield return (Hand, Held());
        }

        foreach (var stack in Items ?? [])
        {
            yield return (stack.Item, stack.Quantity);
        }
    }
}

/// <summary>A storage as inserters see it: anything stored can come out, anything it takes can go in.</summary>
public sealed class StoragePort(StorageKind kind, Contents contents, Action changed) : IMachinePort
{
    private List<ItemStack> Stacks => contents.Items ??= [];

    public string? Output(DateTimeOffset now, Func<string, bool> wanted)
    {
        var stacks = Stacks;
        for (var i = 0; i < stacks.Count; i++)
        {
            var stack = stacks[(contents.Next + i) % stacks.Count];
            if (stack.Quantity > 0 && wanted(stack.Item))
            {
                return stack.Item;
            }
        }

        return null;
    }

    public void Take(string item, DateTimeOffset now)
    {
        var at = Stacks.FindIndex(s => s.Item == item);
        if (Storages.Remove(Stacks, item, 1) > 0)
        {
            // The next stack comes next; one that ran out has made room for it already.
            contents.Next = Stacks.Count == 0 ? 0 : (Stacks.Count > at && Stacks[at].Item == item ? at + 1 : at) % Stacks.Count;
            changed();
        }
    }

    public bool Accepts(string item, DateTimeOffset now) => Storages.Room(kind, Stacks, item) > 0;

    public void Put(string item, DateTimeOffset now)
    {
        Storages.Add(Stacks, item, 1);
        changed();
    }

    public DateTimeOffset? NextChange(DateTimeOffset now) => null;

    public bool Predictable => true;

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A storage as an inserter with a condition sees it: in front, it takes an item only while it holds fewer than
/// <paramref name="below"/> of it; behind, it gives an item up only while it holds more than <paramref name="keep"/> of it.</summary>
public sealed class ConditionPort(IMachinePort port, Func<List<ItemStack>?> stacks, int below, int keep) : IMachinePort
{
    private int Count(string item) => stacks()?.Where(s => s.Item == item).Sum(s => s.Quantity) ?? 0;

    public string? Output(DateTimeOffset now, Func<string, bool> wanted) =>
        port.Output(now, keep > 0 ? item => wanted(item) && Count(item) > keep : wanted);

    public void Take(string item, DateTimeOffset now) => port.Take(item, now);

    public bool Accepts(string item, DateTimeOffset now) => (below <= 0 || Count(item) < below) && port.Accepts(item, now);

    public void Put(string item, DateTimeOffset now) => port.Put(item, now);

    public DateTimeOffset? NextChange(DateTimeOffset now) => port.NextChange(now);

    public bool Predictable => port.Predictable;

    public Task SaveAsync(CancellationToken cancellationToken) => port.SaveAsync(cancellationToken);
}

/// <summary>A machine or a storage as inserters dropping into it see it while it has a filter: it takes its one item only.</summary>
public sealed class FilteredPort(IMachinePort port, string only) : IMachinePort
{
    public string? Output(DateTimeOffset now, Func<string, bool> wanted) => port.Output(now, wanted);

    public void Take(string item, DateTimeOffset now) => port.Take(item, now);

    public bool Accepts(string item, DateTimeOffset now) => item == only && port.Accepts(item, now);

    public void Put(string item, DateTimeOffset now) => port.Put(item, now);

    public DateTimeOffset? NextChange(DateTimeOffset now) => port.NextChange(now);

    public bool Predictable => port.Predictable;

    public Task SaveAsync(CancellationToken cancellationToken) => port.SaveAsync(cancellationToken);
}
