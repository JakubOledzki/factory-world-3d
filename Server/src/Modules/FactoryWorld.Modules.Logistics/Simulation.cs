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

/// <summary>
/// One plot's belts and inserters, with the machines and storage they reach, run step by step.
/// Belts move their items forward along two lanes, onto the belt they face (not head on). A belt fed
/// from behind, or from one side only, carries both lanes on as they are, so belts can turn corners;
/// a belt that joins another from the side as well puts all it carries on the near lane of that one.
/// An inserter picks one item from the tile behind it (a belt, a machine's output or a storage),
/// swings round and drops it on the tile in front, when that takes it: on a belt, on the far lane
/// first. Faster belts move their items more places a tick, in turns within the tick, and faster
/// inserters swing in fewer ticks; a splitter keeps up with the belt that feeds it from behind.
/// Inserters run on power: on a grid short of it they swing slower, without any they stand still.
/// </summary>
public sealed class PlotLogistics
{
    /// <summary>A belt or a splitter: lanes of places that items move along.</summary>
    private sealed class Belt(Guid id, int x, int y, int rotation, Contents contents, bool splitter, int speed, bool fluid = false, bool tunnel = false)
    {
        /// <summary>An underground belt: it runs straight, never turning a corner.</summary>
        public bool Tunnel { get; } = tunnel;

        /// <summary>A pipe: it carries fluids only, and joins only other pipes.</summary>
        public bool Fluid { get; } = fluid;

        public Guid Id { get; } = id;
        public int X { get; } = x;
        public int Y { get; } = y;
        public int Rotation { get; } = rotation;
        public Contents Contents { get; } = contents;
        public string?[] Slots => Contents.Belt!;
        public bool IsSplitter { get; } = splitter;

        /// <summary>Counts the changes to its places, so an inserter can tell nothing changed on it.</summary>
        public int Version { get; set; }

        /// <summary>Already among the changed objects.</summary>
        public bool Marked { get; set; }

        /// <summary>Places items move along it in a tick.</summary>
        public int Speed { get; set; } = speed;

        /// <summary>Where items go at the end of the lanes: a belt has one way on, a splitter two
        /// (its left and its right), either of which may lead nowhere.</summary>
        public Link?[] Out { get; } = splitter ? new Link?[2] : new Link?[1];
    }

    /// <summary>A machine or a storage that counts what goes in and comes out, so an inserter can tell nothing has.</summary>
    /// <param name="made">Called when something is taken out of a machine (not a storage): what it made goes on.</param>
    private sealed class Tracked(IMachinePort port, Action? made) : IMachinePort
    {
        public int Version { get; private set; }

        public string? Output(DateTimeOffset now, Func<string, bool> wanted) => port.Output(now, wanted);

        public void Take(string item, DateTimeOffset now)
        {
            port.Take(item, now);
            Version++;
            made?.Invoke();
        }

        public bool Accepts(string item, DateTimeOffset now) => port.Accepts(item, now);

        public void Put(string item, DateTimeOffset now)
        {
            port.Put(item, now);
            Version++;
        }

        public DateTimeOffset? NextChange(DateTimeOffset now) => port.NextChange(now);

        public bool Predictable => port.Predictable;

        public Task SaveAsync(CancellationToken cancellationToken) => port.SaveAsync(cancellationToken);
    }

    /// <summary>The belt items go on to, the lane each lane leads to and the place on it they come on at.</summary>
    private sealed record Link(Belt To, int[] Into, int Entry);

    private sealed class Inserter(Guid id, Contents contents, int swingTicks, bool fluid = false, int grip = 1)
    {
        /// <summary>Pieces of one item it picks up at once.</summary>
        public int Grip { get; } = grip;

        /// <summary>A pump: it moves fluids only, from and onto pipes and machines; an inserter never touches them.</summary>
        public bool Fluid { get; } = fluid;

        public Guid Id { get; } = id;
        public Contents Contents { get; } = contents;
        public int SwingTicks { get; } = swingTicks;

        /// <summary>Its time on the plot's power grid; null runs it as if it had all the power it needs.</summary>
        public IPowerClock? Clock { get; set; }
        public Belt? FromBelt { get; set; }
        public IMachinePort? FromPort { get; set; }
        public Belt? ToBelt { get; set; }
        public IMachinePort? ToPort { get; set; }

        /// <summary>The machines it takes from and drops into, as counted.</summary>
        public Tracked? FromTracked { get; set; }
        public Tracked? ToTracked { get; set; }

        /// <summary>The lanes of the belt in front to drop on, the far one first.</summary>
        public int[] Lanes { get; set; } = [1, 0];

        /// <summary>It found nothing to do, and will not until one of what it reaches changes: it waits for that.</summary>
        public bool Waiting { get; set; }
        public int WaitFrom { get; set; }
        public int WaitTo { get; set; }
        public int WaitFlights { get; set; }
        public DateTimeOffset? WaitUntil { get; set; }
    }

    // An inserter drops on a lane in the middle first, where the item has room to move on.
    private static readonly int[] DropOrder = [1, 2, 0, 3];

    private readonly List<Belt> _belts = [];
    private readonly List<Inserter> _inserters = [];
    private readonly List<IMachinePort> _ports = [];

    /// <summary>A port of drones: what it holds, and its time on the plot's power grid.</summary>
    private sealed record DronePort(Guid Id, Contents Contents, IPowerClock? Clock);

    private readonly List<DronePort> _drones = [];

    /// <summary>Ticks drones flew in: they change storages without going through their ports.</summary>
    private int _flights;

    /// <summary>Pieces taken out of machines so far: machinery they keep busy is doing real work, not going round in circles.</summary>
    private int _made;
    private static readonly StorageKind DroneStorage = Storages.Find(Items.DronePort)!;

    /// <summary>Turns belts take in a tick: as many as the fastest one moves places.</summary>
    private readonly int _phases;

    /// <summary>Ticks worked out one by one; the others were skipped as nothing moved.</summary>
    public int Steps { get; private set; }

    /// <summary>Objects whose contents changed and must be written back.</summary>
    public HashSet<Guid> Changed { get; } = [];

    /// <param name="objects">Every object on the plot.</param>
    /// <param name="contents">Contents of belts and inserters by object id; missing ones start empty and are added.</param>
    /// <param name="ports">Machines and storage inserters can reach, by object id.</param>
    /// <param name="power">The plot's power grid; without one inserters work as if they had all the power they need.</param>
    /// <param name="grips">Pieces each inserter picks up at once, by object id, when more than one.</param>
    public PlotLogistics(
        IReadOnlyList<PlotObject> objects, Dictionary<Guid, Contents> contents, IReadOnlyDictionary<Guid, IMachinePort> ports, IPowerGrid? power = null,
        IReadOnlyDictionary<Guid, int>? grips = null)
    {
        var at = objects.ToDictionary(o => (o.TileX, o.TileY));
        var belts = new Dictionary<(int, int), Belt>();
        foreach (var o in objects.Where(o => Items.IsConveyor(o.Kind)).OrderBy(o => o.TileY).ThenBy(o => o.TileX))
        {
            var c = Contents(contents, o.Id);
            if (c.Belt is not { Length: LogisticsRules.BeltSlots * LogisticsRules.BeltLanes })
            {
                // A belt from before there were two lanes keeps what it had on the left one.
                var slots = new string?[LogisticsRules.BeltSlots * LogisticsRules.BeltLanes];
                c.Belt?.Take(slots.Length).ToArray().CopyTo(slots, 0);
                c.Belt = slots;
            }

            belts[(o.TileX, o.TileY)] = new Belt(o.Id, o.TileX, o.TileY, o.Rotation, c, o.Kind == Items.Splitter, LogisticsRules.BeltSpeed(o.Kind),
                Items.CarriesFluids(o.Kind), o.Kind == Items.UndergroundBelt);
        }

        var byId = belts.Values.ToDictionary(b => b.Id);
        var tunnels = LogisticsRules.TunnelLinks(objects);

        // A splitter or an underground belt going down keeps up with the belt that feeds it from behind; the one
        // coming up again as fast as the one that took its items down.
        // Over and over, as one may feed the next (a line of tunnels, a splitter after one), until nothing changes.
        for (var pass = 0; pass <= belts.Count; pass++)
        {
            var changed = false;
            foreach (var belt in belts.Values.Where(b => b.IsSplitter || (b.Tunnel && !tunnels.ContainsValue(b.Id))))
            {
                var (dx, dy) = LogisticsRules.Facing(belt.Rotation);
                if (belts.GetValueOrDefault((belt.X - dx, belt.Y - dy)) is { IsSplitter: false } feeder && feeder.Rotation == belt.Rotation
                    && feeder.Speed != belt.Speed)
                {
                    belt.Speed = feeder.Speed;
                    changed = true;
                }
            }

            foreach (var (entry, exit) in tunnels)
            {
                changed |= byId[exit].Speed != byId[entry].Speed;
                byId[exit].Speed = byId[entry].Speed;
            }

            if (!changed)
            {
                break;
            }
        }

        _phases = belts.Count == 0 ? 0 : belts.Values.Max(b => b.Speed);

        // Only belts turn corners; a splitter always runs straight.
        // Pipes and belts never join, so each bends only by its own kind.
        // An underground belt going down feeds no belt in front of it, only the one it comes up at.
        var beltRotations = belts.Where(b => !b.Value.IsSplitter && !b.Value.Fluid && !tunnels.ContainsKey(b.Value.Id)).ToDictionary(b => b.Key, b => b.Value.Rotation);
        var pipeRotations = belts.Where(b => b.Value.Fluid).ToDictionary(b => b.Key, b => b.Value.Rotation);
        var cables = LogisticsRules.CableLinks(objects);
        foreach (var belt in belts.Values)
        {
            var rotations = belt.Fluid ? pipeRotations : beltRotations;
            if (cables.TryGetValue(belt.Id, out var station))
            {
                // Over the cable onto the start of the station ahead.
                belt.Out[0] = new Link(byId[station], [0, 1], 0);
            }
            else if (tunnels.TryGetValue(belt.Id, out var exit))
            {
                // Under the ground onto the start of the one that comes up again.
                belt.Out[0] = new Link(byId[exit], [0, 1], 0);
            }
            else if (belt.IsSplitter)
            {
                var left = LogisticsRules.Left(belt.Rotation);
                belt.Out[0] = Connect(belts, rotations, belt, left);
                belt.Out[1] = Connect(belts, rotations, belt, (-left.Dx, -left.Dy));
            }
            else
            {
                belt.Out[0] = Connect(belts, rotations, belt, LogisticsRules.Facing(belt.Rotation));
            }
        }

        // Downstream belts move first, so a full line of items moves all together.
        var seen = new HashSet<Belt>();
        foreach (var start in belts.Values)
        {
            Downstream(start, seen);
        }

        var tracked = ports.Values.Distinct().ToDictionary(p => p, p => new Tracked(p, p is StoragePort ? null : () => _made++));
        foreach (var o in objects.Where(o => Items.IsInserter(o.Kind)).OrderBy(o => o.TileY).ThenBy(o => o.TileX))
        {
            var (dx, dy) = LogisticsRules.Facing(o.Rotation);
            var reach = LogisticsRules.Reach(o.Kind);
            var fluid = Items.CarriesFluids(o.Kind);
            var inserter = new Inserter(o.Id, Contents(contents, o.Id), LogisticsRules.Swing(o.Kind), fluid, grips?.GetValueOrDefault(o.Id, 1) ?? 1)
            {
                Clock = power?.Clock(o.Id),
            };
            var from = (o.TileX - dx * reach, o.TileY - dy * reach);
            var to = (o.TileX + dx * reach, o.TileY + dy * reach);
            // A pump reaches pipes only, an inserter belts only.
            inserter.FromBelt = belts.GetValueOrDefault(from) is { } fromBelt && fromBelt.Fluid == fluid ? fromBelt : null;
            inserter.ToBelt = belts.GetValueOrDefault(to) is { } toBelt && toBelt.Fluid == fluid ? toBelt : null;
            if (inserter.ToBelt is { } onto && LogisticsRules.Left(onto.Rotation) == (dx, dy))
            {
                // Standing on the belt's right, it reaches over to the left lane first.
                inserter.Lanes = [0, 1];
            }
            var condition = inserter.Contents;
            if (at.TryGetValue(from, out var source) && !belts.ContainsKey(from))
            {
                inserter.FromPort = inserter.FromTracked = ports.TryGetValue(source.Id, out var port) ? tracked[port] : null;
                if (condition.Keep > 0 && inserter.FromTracked is not null && Storages.Find(source.Kind) is not null)
                {
                    // It keeps a reserve in the storage behind it.
                    var stored = Contents(contents, source.Id);
                    inserter.FromPort = new ConditionPort(inserter.FromTracked, () => stored.Items, 0, condition.Keep);
                }
            }

            if (at.TryGetValue(to, out var target) && !belts.ContainsKey(to) && ports.TryGetValue(target.Id, out var into))
            {
                // A storage or a machine with a filter lets in only its item.
                inserter.ToTracked = tracked[into];
                inserter.ToPort = contents.GetValueOrDefault(target.Id)?.Only is { } only ? new FilteredPort(inserter.ToTracked, only) : inserter.ToTracked;
                if (condition.Below > 0 && Storages.Find(target.Kind) is not null)
                {
                    // It fills the storage in front up to so many.
                    var stored = Contents(contents, target.Id);
                    inserter.ToPort = new ConditionPort(inserter.ToPort, () => stored.Items, condition.Below, 0);
                }
            }

            _inserters.Add(inserter);
        }

        _ports.AddRange(tracked.Values);
        foreach (var o in objects.Where(o => o.Kind == Items.DronePort).OrderBy(o => o.TileY).ThenBy(o => o.TileX))
        {
            _drones.Add(new DronePort(o.Id, Contents(contents, o.Id), power?.Clock(o.Id)));
        }
    }

    /// <summary>How items leave <paramref name="belt"/> towards <paramref name="way"/>: onto the belt
    /// there, unless that one runs head on into it.</summary>
    private static Link? Connect(Dictionary<(int, int), Belt> belts, Dictionary<(int, int), int> rotations, Belt belt, (int Dx, int Dy) way)
    {
        if (!belts.TryGetValue((belt.X + way.Dx, belt.Y + way.Dy), out var next) || LogisticsRules.Facing(next.Rotation) == (-way.Dx, -way.Dy)
            || next.Fluid != belt.Fluid)
        {
            return null;
        }

        if (LogisticsRules.Facing(next.Rotation) == way)
        {
            // From behind onto the start of it.
            return new Link(next, [0, 1], 0);
        }

        if (!next.IsSplitter && !next.Tunnel && Bend(rotations, next.X, next.Y, next.Rotation) is not 0)
        {
            // From the side of a belt fed from that side only: it is a corner, the lanes go round it.
            return new Link(next, [0, 1], 0);
        }

        // From the side into the middle of the near lane.
        var near = LogisticsRules.Left(next.Rotation) == (-way.Dx, -way.Dy) ? 0 : 1;
        return new Link(next, [near, near], 1);
    }

    private void Downstream(Belt belt, HashSet<Belt> seen)
    {
        if (!seen.Add(belt))
        {
            return;
        }

        foreach (var link in belt.Out)
        {
            if (link is not null)
            {
                Downstream(link.To, seen);
            }
        }

        _belts.Add(belt);
    }

    /// <summary>Which side a belt turns in from: -1 when the only belt feeding it comes from its left,
    /// 1 from its right, 0 when it runs straight (fed from behind, from both sides or not at all).</summary>
    private static int Bend(Dictionary<(int, int), int> belts, int x, int y, int rotation)
    {
        bool Feeds((int Dx, int Dy) from)
        {
            if (!belts.TryGetValue((x + from.Dx, y + from.Dy), out var r))
            {
                return false;
            }

            var (dx, dy) = LogisticsRules.Facing(r);
            return (dx, dy) == (-from.Dx, -from.Dy);
        }

        var (fx, fy) = LogisticsRules.Facing(rotation);
        var left = LogisticsRules.Left(rotation);
        var (fromLeft, fromRight) = (Feeds(left), Feeds((-left.Dx, -left.Dy)));
        if (Feeds((-fx, -fy)) || fromLeft == fromRight)
        {
            return 0;
        }

        return fromLeft ? -1 : 1;
    }

    /// <summary>The tiles that inserters pick up from and drop on, which the machines there must be opened for.</summary>
    public static IEnumerable<(int TileX, int TileY)> Reach(IEnumerable<PlotObject> objects)
    {
        foreach (var o in objects.Where(o => Items.IsInserter(o.Kind)))
        {
            var (dx, dy) = LogisticsRules.Facing(o.Rotation);
            var reach = LogisticsRules.Reach(o.Kind);
            yield return (o.TileX - dx * reach, o.TileY - dy * reach);
            yield return (o.TileX + dx * reach, o.TileY + dy * reach);
        }
    }

    /// <summary>Runs from <paramref name="from"/> (on the grid of ticks) up to <paramref name="now"/>;
    /// returns the last tick worked out, the start of the next run.</summary>
    public DateTimeOffset Run(DateTimeOffset from, DateTimeOffset now)
    {
        var tick = LogisticsRules.Tick;
        var last = from + tick * Math.Max(0, (now - from).Ticks / tick.Ticks);
        if (_belts.Count == 0 && _inserters.Count == 0 && _drones.Count == 0)
        {
            return last;
        }

        var t = from;
        var worked = 0;
        while (t < last)
        {
            t += tick;
            var made = _made;
            if (Step(t))
            {
                // A machine that keeps making things (a forester, a drill) keeps its line busy for as long as it works.
                if (_made != made)
                {
                    worked = 0;
                }
                else if (++worked >= LogisticsRules.MaxTicks)
                {
                    return last;
                }

                continue;
            }

            // Nothing moved, so nothing will until a machine makes something new by itself.
            var next = _ports.Select(p => p.NextChange(t)).Where(n => n > t).Min();
            if (next is not { } due || due > last)
            {
                return last;
            }

            // Next step on the first tick at or after it, after a pause that starts the count of work anew.
            worked = 0;
            var ticks = ((due - from).Ticks + tick.Ticks - 1) / tick.Ticks;
            t = from + tick * (ticks - 1);
        }

        return last;
    }

    /// <summary>One tick; true when anything moved.</summary>
    private bool Step(DateTimeOffset now)
    {
        Steps++;
        var moved = false;
        for (var phase = 0; phase < _phases; phase++)
        {
            moved |= MoveBelts(phase);
        }

        foreach (var inserter in _inserters)
        {
            var c = inserter.Contents;
            var speed = inserter.Clock?.Speed(now) ?? 1;
            if (speed <= 0)
            {
                continue;
            }

            if (speed < 1)
            {
                // Short of power it gathers what one move takes over the ticks; one waiting for
                // something to do keeps no more than that.
                var charge = Math.Min(c.Charge, 1) + speed;
                if (charge != c.Charge)
                {
                    c.Charge = charge;
                    Changed.Add(inserter.Id);
                }

                if (charge < 1)
                {
                    moved = true;
                    continue;
                }
            }

            if (inserter.Waiting && Unchanged(inserter, now))
            {
                continue;
            }

            inserter.Waiting = false;

            if (c.Hand is { } hand)
            {
                if (c.Swing < inserter.SwingTicks)
                {
                    c.Swing++;
                }
                else if (Drop(inserter, hand, c.Held(), now) is var left && left < c.Held())
                {
                    // What does not fit yet stays in its hand for the next tick.
                    c.Hand = left == 0 ? null : hand;
                    c.Count = left > 1 ? left : 0;
                }
                else
                {
                    Wait(inserter, now);
                    continue;
                }
            }
            else if (c.Swing > 0)
            {
                c.Swing--;
            }
            else if (Pick(inserter, now) is ({ } picked, var count))
            {
                c.Hand = picked;
                c.Count = count > 1 ? count : 0;
            }
            else
            {
                Wait(inserter, now);
                continue;
            }

            if (speed < 1)
            {
                c.Charge -= 1;
            }

            Changed.Add(inserter.Id);
            moved = true;
        }

        if (Fly(now))
        {
            _flights++;
            return true;
        }

        return moved;
    }

    /// <summary>An inserter found nothing to do: when all it reaches changes only as it is used or at a time it can
    /// tell, it waits for that rather than looking again every tick. A belt or a machine that may change by itself
    /// (on power, say) keeps it looking.</summary>
    private void Wait(Inserter inserter, DateTimeOffset now)
    {
        if (inserter.FromTracked is { Predictable: false } || inserter.ToTracked is { Predictable: false })
        {
            return;
        }

        DateTimeOffset? until = null;
        foreach (var port in (ReadOnlySpan<Tracked?>)[inserter.FromTracked, inserter.ToTracked])
        {
            if (port?.NextChange(now) is { } next)
            {
                if (next <= now)
                {
                    return;
                }

                until = until is { } u && u < next ? u : next;
            }
        }

        inserter.Waiting = true;
        inserter.WaitFrom = Version(inserter.FromBelt, inserter.FromTracked);
        inserter.WaitTo = Version(inserter.ToBelt, inserter.ToTracked);
        inserter.WaitFlights = _flights;
        inserter.WaitUntil = until;
    }

    /// <summary>Nothing an inserter waiting since <see cref="Wait"/> reaches has changed yet.</summary>
    private bool Unchanged(Inserter inserter, DateTimeOffset now) =>
        inserter.WaitFlights == _flights && (inserter.WaitUntil is not { } until || now < until)
        && inserter.WaitFrom == Version(inserter.FromBelt, inserter.FromTracked) && inserter.WaitTo == Version(inserter.ToBelt, inserter.ToTracked);

    private static int Version(Belt? belt, Tracked? port) => belt?.Version ?? port?.Version ?? 0;

    /// <summary>A place on <paramref name="belt"/> changed.</summary>
    private void Touch(Belt belt)
    {
        belt.Version++;
        if (!belt.Marked)
        {
            belt.Marked = true;
            Changed.Add(belt.Id);
        }
    }

    /// <summary>Drones fly from each port without a filter to the ports set to take what it holds, a few pieces a
    /// tick, while both ports have power; true when any flew.</summary>
    private bool Fly(DateTimeOffset now)
    {
        var flew = false;
        foreach (var from in _drones)
        {
            if (from.Contents.Only is not null || from.Contents.Items is not { Count: > 0 } stacks || (from.Clock?.Speed(now) ?? 1) <= 0)
            {
                continue;
            }

            var load = LogisticsRules.DroneLoad;
            foreach (var item in stacks.Select(st => st.Item).ToList())
            {
                foreach (var to in _drones)
                {
                    if (load == 0)
                    {
                        break;
                    }

                    if (to == from || to.Contents.Only != item || (to.Clock?.Speed(now) ?? 1) <= 0)
                    {
                        continue;
                    }

                    var have = stacks.FirstOrDefault(st => st.Item == item)?.Quantity ?? 0;
                    var flown = Math.Min(Math.Min(load, have), Storages.Room(DroneStorage, to.Contents.Items ??= [], item));
                    if (flown <= 0)
                    {
                        continue;
                    }

                    Storages.Remove(stacks, item, flown);
                    Storages.Add(to.Contents.Items, item, flown);
                    load -= flown;
                    Changed.Add(to.Id);
                    Changed.Add(from.Id);
                    flew = true;
                }
            }
        }

        return flew;
    }

    /// <summary>One turn of the belts within a tick: those fast enough move their items one place on.</summary>
    private bool MoveBelts(int phase)
    {
        var moved = false;
        const int size = LogisticsRules.BeltSlots;
        foreach (var belt in _belts)
        {
            if (belt.Speed <= phase)
            {
                continue;
            }

            var slots = belt.Slots;
            for (var lane = 0; lane < LogisticsRules.BeltLanes; lane++)
            {
                var start = lane * size;
                var end = start + size - 1;
                if (slots[end] is { } leaving && Pass(belt, lane, leaving))
                {
                    slots[end] = null;
                    Touch(belt);
                    moved = true;
                }

                for (var i = end - 1; i >= start; i--)
                {
                    if (slots[i] is not null && slots[i + 1] is null)
                    {
                        slots[i + 1] = slots[i];
                        slots[i] = null;
                        Touch(belt);
                        moved = true;
                    }
                }
            }
        }

        return moved;
    }

    /// <summary>Puts the item at the end of a lane on the belt it goes on to, when there is room there.
    /// A splitter sends items left and right in turn, or filtered items one way and the rest the other.</summary>
    private bool Pass(Belt belt, int lane, string item)
    {
        var c = belt.Contents;
        int[] ways = !belt.IsSplitter ? [0]
            : c.Filter is { Count: > 0 } filter ? [filter.Contains(item) ? c.FilterSide : 1 - c.FilterSide]
            : [c.Turn, 1 - c.Turn];
        foreach (var way in ways)
        {
            if (belt.Out[way] is not { } link)
            {
                continue;
            }

            var at = link.Into[lane] * LogisticsRules.BeltSlots + link.Entry;
            if (link.To.Slots[at] is null)
            {
                link.To.Slots[at] = item;
                Touch(link.To);
                if (belt.IsSplitter)
                {
                    c.Turn = 1 - way;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>What an inserter picks up: one item, and as many more pieces of it as its grip holds when they are there.</summary>
    private (string? Item, int Count) Pick(Inserter inserter, DateTimeOffset now)
    {
        // An inserter with a filter picks up its item only.
        var only = inserter.Contents.Only;
        Func<string, bool> wanted;
        if (inserter.ToBelt is not null)
        {
            wanted = item => Items.IsFluid(item) == inserter.Fluid && (only is null || item == only);
        }
        else if (inserter.ToPort is { } port)
        {
            wanted = item => Items.IsFluid(item) == inserter.Fluid && (only is null || item == only) && port.Accepts(item, now);
        }
        else
        {
            return (null, 0);
        }

        if (inserter.FromBelt is { } belt)
        {
            // The item furthest along goes first, from either lane, then more of the same.
            string? picked = null;
            var count = 0;
            for (var i = LogisticsRules.BeltSlots - 1; i >= 0 && count < inserter.Grip; i--)
            {
                for (var lane = 0; lane < LogisticsRules.BeltLanes && count < inserter.Grip; lane++)
                {
                    var at = lane * LogisticsRules.BeltSlots + i;
                    if (belt.Slots[at] is { } item && (picked is null ? wanted(item) : item == picked))
                    {
                        belt.Slots[at] = null;
                        Touch(belt);
                        picked = item;
                        count++;
                    }
                }
            }

            return (picked, count);
        }

        if (inserter.FromPort is { } from && from.Output(now, wanted) is { } output)
        {
            from.Take(output, now);
            var count = 1;
            while (count < inserter.Grip && from.Output(now, item => item == output) == output)
            {
                from.Take(output, now);
                count++;
            }

            return (output, count);
        }

        return (null, 0);
    }

    /// <summary>Drops what an inserter holds where it takes it; returns how many pieces are left in its hand.</summary>
    private int Drop(Inserter inserter, string item, int count, DateTimeOffset now)
    {
        if (inserter.ToBelt is { } belt)
        {
            foreach (var lane in inserter.Lanes)
            {
                foreach (var i in DropOrder)
                {
                    var at = lane * LogisticsRules.BeltSlots + i;
                    if (count > 0 && belt.Slots[at] is null)
                    {
                        belt.Slots[at] = item;
                        Touch(belt);
                        count--;
                    }
                }
            }

            return count;
        }

        while (count > 0 && inserter.ToPort is { } port && port.Accepts(item, now))
        {
            port.Put(item, now);
            count--;
        }

        return count;
    }

    private static Contents Contents(Dictionary<Guid, Contents> contents, Guid id)
    {
        if (!contents.TryGetValue(id, out var c))
        {
            contents[id] = c = new Contents();
        }

        return c;
    }
}
