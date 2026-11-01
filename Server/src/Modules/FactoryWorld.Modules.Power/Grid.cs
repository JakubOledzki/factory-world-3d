using System.Text.Json.Serialization;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Power;

/// <summary>
/// The rules of power on plots. A generator burns wood or coal and gives up to
/// <see cref="GeneratorKw"/>; a pole powers the tiles around it (<see cref="SupplyRadius"/> each way)
/// and is wired to every pole up to <see cref="WireReach"/> tiles away. Poles wired together make one
/// network: its generators share the load of all the machines on it. Machines draw their power
/// whenever they stand on a network, busy or not; with too little power they all work slower, and
/// without any they stop. A water wheel, a hydro plant and a tidal plant on the coast give their power all the time without fuel, on
/// plots by a river, a lake or the sea; the generators of their network burn only for what they lack. Solar power gives by day,
/// wind turbines as much as the wind blows. On the glacier machines that no heater warms work at half their pace.
/// A substation powers a wider square than a pole, and high-voltage pylons are wired to each other from far apart, so one network
/// reaches across a whole plot; two poles of different kinds are wired as far as the shorter reach of the two. A grid tie on a
/// network joins it by cable to a tie on another plot (<see cref="PowerLink"/>): the supplier's network draws what the cable carries
/// like a machine, and the receiver's gets it as free power, as much of it as the supplier's network has power for.
/// </summary>
public static class PowerRules
{
    public const double GeneratorKw = 100;

    /// <summary>Generators of every kind: what each gives at most (kW) and what it burns. The steam generator burns
    /// wood and coal, the coal power plant coal, the gas power plant natural gas, the diesel generator diesel, petrol or ethanol,
    /// the nuclear plant fuel rods, the fusion plant coolant; the biomass plant wood, pellets and charcoal, the combined heat and power
    /// plant coal, coke and wood, and the fuel cell hydrogen.</summary>
    public static readonly IReadOnlyDictionary<string, (double Kw, IReadOnlySet<string> Burns)> Generators =
        new Dictionary<string, (double, IReadOnlySet<string>)>
        {
            [Items.Generator] = (GeneratorKw, new HashSet<string> { Items.Coal, Items.Charcoal, Items.Coke, Items.Wood, Items.WoodPellets }),
            [Items.CoalPlant] = (1000, new HashSet<string> { Items.Coal, Items.Charcoal, Items.Coke }),
            [Items.NuclearPlant] = (2500, new HashSet<string> { Items.FuelRod }),
            [Items.GasPlant] = (300, new HashSet<string> { Items.NaturalGas }),
            [Items.DieselGenerator] = (150, new HashSet<string> { Items.Diesel, Items.Gasoline, Items.Ethanol }),
            [Items.FusionPlant] = (5000, new HashSet<string> { Items.Coolant }),
            [Items.BiomassPlant] = (500, new HashSet<string> { Items.Wood, Items.WoodPellets, Items.Charcoal }),
            // Gives less than a coal power plant, but its waste heat warms the machines around it (see ChpReach).
            [Items.ChpPlant] = (700, new HashSet<string> { Items.Coal, Items.Charcoal, Items.Coke, Items.Wood, Items.WoodPellets }),
            [Items.FuelCell] = (200, new HashSet<string> { Items.Hydrogen }),
        };

    /// <summary>What a generator of <paramref name="kind"/> gives at most (kW).</summary>
    public static double KwOf(string kind) => Generators.TryGetValue(kind, out var g) ? g.Kw : 0;

    public static bool Burns(string kind, string fuel) =>
        Generators.TryGetValue(kind, out var g) ? g.Burns.Contains(fuel) : Items.IsHeater(kind) && HeaterFuels.Contains(fuel);

    /// <summary>What a heater burns: wood, coal, coke and pellets, like a steam generator.</summary>
    public static readonly IReadOnlySet<string> HeaterFuels = new HashSet<string> { Items.Coal, Items.Charcoal, Items.Coke, Items.Wood, Items.WoodPellets };

    /// <summary>A combined heat and power plant warms the machines this many tiles from it each way while it burns, as a heater does nearer.</summary>
    public const int ChpReach = 6;

    /// <summary>What each water power plant gives (kW), before the biome's bonus.</summary>
    public static readonly IReadOnlyDictionary<string, double> Hydro = new Dictionary<string, double>
    {
        [Items.WaterWheel] = 30,
        [Items.HydroPlant] = 150,
        // The tide comes and goes twice a day, so a tidal plant gives what it gives all the time.
        [Items.TidalPlant] = 100,
    };

    /// <summary>What each solar power plant gives in clear sun (kW), before the desert's bonus. It gives nothing at night.</summary>
    public static readonly IReadOnlyDictionary<string, double> Solar = new Dictionary<string, double>
    {
        [Items.SolarPanel] = 20,
        [Items.SolarTower] = 600,
    };

    /// <summary>What each wind turbine gives in a full wind (kW), before the biome's bonus.</summary>
    public static readonly IReadOnlyDictionary<string, double> Wind = new Dictionary<string, double>
    {
        [Items.WindTurbine] = 40,
        [Items.LargeWindTurbine] = 400,
    };

    /// <summary>The sun shines this much brighter on the sand of the desert and its oases.</summary>
    public const double SandySun = 1.25;

    /// <summary>A reed fence shelters solar power this many tiles from it each way from a sandstorm.</summary>
    public const int FenceReach = 3;

    /// <summary>What a steam generator, a coal or a nuclear power plant gives on sand, of its full power: there is no water to cool it.</summary>
    public const double DrySteam = 0.7;

    /// <summary>Whether a generator of <paramref name="kind"/> turns water into steam, and so gives less on sand.</summary>
    public static bool Steam(string kind) => kind is Items.Generator or Items.CoalPlant or Items.NuclearPlant or Items.BiomassPlant or Items.ChpPlant;

    /// <summary>The energy a battery stores at most (kJ): an hour of its full power, and more.</summary>
    public const double BatteryKj = 360_000;

    /// <summary>What a battery takes in or gives out at most (kW).</summary>
    public const double BatteryKw = 50;

    /// <summary>What each kind of store keeps at most (kJ) and takes in or gives out at most (kW): a battery, and an energy store
    /// that keeps ten batteries' worth and moves eight times their power.</summary>
    public static readonly IReadOnlyDictionary<string, (double Kj, double Kw)> Stores = new Dictionary<string, (double, double)>
    {
        [Items.Battery] = (BatteryKj, BatteryKw),
        [Items.EnergyStore] = (3_600_000, 400),
    };

    /// <summary>What a store of <paramref name="kind"/> keeps at most (kJ).</summary>
    public static double StoreKj(string kind) => Stores.TryGetValue(kind, out var s) ? s.Kj : 0;

    /// <summary>What a store of <paramref name="kind"/> takes in or gives out at most (kW).</summary>
    public static double StoreKw(string kind) => Stores.TryGetValue(kind, out var s) ? s.Kw : 0;

    /// <summary>Pieces of fuel a generator holds.</summary>
    public const int MaxFuel = 50;

    /// <summary>A pole powers a square of tiles this far from it each way: 5 × 5 tiles.</summary>
    public const int SupplyRadius = 2;

    /// <summary>Poles at most this many tiles apart are wired together.</summary>
    public const double WireReach = 7;

    /// <summary>What each kind of pole powers (tiles each way from it) and how far it is wired (tiles): a pole 5 × 5 and 7,
    /// a substation 9 × 9 and 10, a high-voltage pylon only 3 × 3 but 20.</summary>
    public static readonly IReadOnlyDictionary<string, (int Supply, double Reach)> Poles = new Dictionary<string, (int, double)>
    {
        [Items.Pole] = (SupplyRadius, WireReach),
        [Items.Substation] = (4, 10),
        [Items.Pylon] = (1, 20),
    };

    /// <summary>How many tiles each way a pole of <paramref name="kind"/> powers.</summary>
    public static int SupplyOf(string kind) => Poles.TryGetValue(kind, out var p) ? p.Supply : SupplyRadius;

    /// <summary>How far a pole of <paramref name="kind"/> is wired (tiles).</summary>
    public static double ReachOf(string kind) => Poles.TryGetValue(kind, out var p) ? p.Reach : WireReach;

    /// <summary>What generators burn, and the energy (kJ) in one piece: coal lasts 200 s at full power in a steam generator,
    /// wood 50 s, a pellet 40 s; a unit of gas 200 s in a gas power plant, of diesel 400 s and of petrol 300 s in a diesel generator;
    /// a fuel rod 10 minutes in a nuclear plant; a fusion plant uses up a unit of coolant a minute at full power. Coke burns a little longer
    /// than coal; hydrogen gives back in a fuel cell 70% of what the electrolyzer put into it.</summary>
    public static readonly IReadOnlyDictionary<string, double> Fuels = new Dictionary<string, double>
    {
        [Items.Coal] = 20_000,
        [Items.Charcoal] = 18_000,
        [Items.Wood] = 5_000,
        [Items.WoodPellets] = 4_000,
        [Items.NaturalGas] = 60_000,
        [Items.Diesel] = 60_000,
        [Items.Gasoline] = 45_000,
        // Ethanol from the potato distillery: 0.8 of petrol.
        [Items.Ethanol] = 36_000,
        [Items.Coolant] = 300_000,
        [Items.FuelRod] = 1_500_000,
        [Items.Coke] = 26_000,
        [Items.Hydrogen] = 840,
    };

    /// <summary>Machines that run on power, and what they draw (kW).</summary>
    public static readonly IReadOnlyDictionary<string, double> Demand = new Dictionary<string, double>
    {
        [Items.Inserter] = 5,
        [Items.FastInserter] = 8,
        [Items.ExpressInserter] = 12,
        [Items.LongInserter] = 7,
        [Items.DronePort] = 30,
        [Items.Mill] = 30,
        [Items.RollerMill] = 60,
        [Items.FlourWorks] = 120,
        [Items.OilPress] = 30,
        [Items.ElectricSawmill] = 40,
        [Items.IndustrialSawmill] = 80,
        [Items.FastAssembler] = 40,
        [Items.ExpressAssembler] = 100,
        [Items.Crusher] = 40,
        [Items.ConeCrusher] = 80,
        [Items.OreProcessingPlant] = 160,
        [Items.MechanizedQuarry] = 60,
        [Items.OpenPitMine] = 120,
        [Items.Drill] = 40,
        [Items.HeavyDrill] = 100,
        [Items.Cannery] = 30,
        [Items.Pumpjack] = 40,
        [Items.OffshorePlatform] = 150,
        [Items.WaterInjector] = 30,
        [Items.Refinery] = 80,
        [Items.Cracker] = 120,
        [Items.ChemicalPlant] = 60,
        [Items.Pump] = 5,
        [Items.ArcFurnace] = 150,
        [Items.WaterWell] = 20,
        [Items.Desalinator] = 60,
        [Items.ConcreteMixer] = 30,
        [Items.ServerRoom] = 200,
        [Items.IceCoreDrill] = 40,
        [Items.AssemblyHall] = 120,
        [Items.MultistoreyFactory] = 300,
        [Items.CitySorter] = 40,
        [Items.DesignOffice] = 30,
        [Items.Laboratory] = 60,
        [Items.ServiceWorkshop] = 40,
        [Items.PaperMill] = 60,
        [Items.MachineShop] = 40,
        [Items.RoboticHall] = 300,
        [Items.OxygenConverter] = 100,
        [Items.RollingMill] = 120,
        [Items.ControlCenter] = 60,
        [Items.ContinuousCaster] = 150,
        [Items.SteelCombine] = 600,
        [Items.Megafactory] = 1000,
        [Items.SpinningMill] = 25,
        [Items.Electrolyzer] = 150,
    };

    public static bool Covers(PlotObject pole, int tileX, int tileY) =>
        Math.Abs(pole.TileX - tileX) <= SupplyOf(pole.Kind) && Math.Abs(pole.TileY - tileY) <= SupplyOf(pole.Kind);

    /// <summary>Whether a pole powers an object: it reaches any tile the object covers.</summary>
    public static bool Covers(PlotObject pole, PlotObject o) =>
        Footprints.Tiles(o.Kind, o.TileX, o.TileY).Any(t => Covers(pole, t.X, t.Y));

    public static bool Wired(PlotObject a, PlotObject b)
    {
        var (dx, dy) = (a.TileX - b.TileX, a.TileY - b.TileY);
        var reach = Math.Min(ReachOf(a.Kind), ReachOf(b.Kind));
        return dx * dx + dy * dy <= reach * reach;
    }

    /// <summary>The networks of a plot: poles wired together, with the generators and machines they power.
    /// Something within reach of poles of two networks belongs to the one of the first pole (north to south, west to east).</summary>
    public static IReadOnlyList<Network> Networks(IEnumerable<PlotObject> objects)
    {
        var all = objects.ToList();
        var poles = all.Where(o => Items.IsPole(o.Kind)).OrderBy(o => o.TileY).ThenBy(o => o.TileX).ToList();
        var component = new int[poles.Count];
        Array.Fill(component, -1);
        var count = 0;
        for (var i = 0; i < poles.Count; i++)
        {
            if (component[i] >= 0)
            {
                continue;
            }

            component[i] = count;
            var queue = new Queue<int>([i]);
            while (queue.TryDequeue(out var at))
            {
                for (var j = 0; j < poles.Count; j++)
                {
                    if (component[j] < 0 && Wired(poles[at], poles[j]))
                    {
                        component[j] = count;
                        queue.Enqueue(j);
                    }
                }
            }

            count++;
        }

        var generators = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var hydro = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var solar = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var wind = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var batteries = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var consumers = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var ties = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList();
        var demand = new double[count];
        var bankKj = new double[count];
        var bankKw = new double[count];
        foreach (var o in all)
        {
            var isGenerator = Items.IsGenerator(o.Kind);
            if (!isGenerator && !Items.IsRenewable(o.Kind) && !Items.IsBattery(o.Kind) && !Demand.ContainsKey(o.Kind) && o.Kind != Items.GridTie)
            {
                continue;
            }

            var pole = poles.FindIndex(p => Covers(p, o));
            if (pole < 0)
            {
                continue;
            }

            if (isGenerator)
            {
                generators[component[pole]].Add(o.Id);
            }
            else if (Items.IsHydro(o.Kind) || Items.IsGeothermal(o.Kind))
            {
                hydro[component[pole]].Add(o.Id);
            }
            else if (Items.IsSolar(o.Kind))
            {
                solar[component[pole]].Add(o.Id);
            }
            else if (Items.IsWind(o.Kind))
            {
                wind[component[pole]].Add(o.Id);
            }
            else if (o.Kind == Items.GridTie)
            {
                ties[component[pole]].Add(o.Id);
            }
            else if (Items.IsBattery(o.Kind))
            {
                batteries[component[pole]].Add(o.Id);
                bankKj[component[pole]] += StoreKj(o.Kind);
                bankKw[component[pole]] += StoreKw(o.Kind);
            }
            else
            {
                consumers[component[pole]].Add(o.Id);
                demand[component[pole]] += Demand[o.Kind];
            }
        }

        return Enumerable.Range(0, count)
            .Select(n => new Network(n, poles.Where((_, i) => component[i] == n).Select(p => p.Id).ToList(), generators[n], consumers[n], demand[n], hydro[n])
            {
                Solar = solar[n],
                Wind = wind[n],
                Batteries = batteries[n],
                BankKj = bankKj[n],
                BankKw = bankKw[n],
                Ties = ties[n],
            })
            .ToList();
    }

    /// <summary>What a water power plant of <paramref name="kind"/> gives on a plot of <paramref name="biome"/> (kW); 0 where no water flows.
    /// A geothermal plant counts with them, as it too gives the same all the time.</summary>
    public static double HydroKw(string kind, int biome) =>
        Items.IsGeothermal(kind) ? Geothermal.KwIn(biome) : Hydro.GetValueOrDefault(kind) * WaterPower.Biomes.GetValueOrDefault(biome);

    /// <summary>What a solar power plant of <paramref name="kind"/> gives in clear sun on a plot of <paramref name="biome"/> (kW).</summary>
    public static double SolarKw(string kind, int biome) => Solar.GetValueOrDefault(kind) * (Oasis.Sandy(biome) ? SandySun : 1);

    /// <summary>What a wind turbine of <paramref name="kind"/> gives in a full wind on a plot of <paramref name="biome"/> (kW).</summary>
    public static double WindKw(string kind, int biome) => Wind.GetValueOrDefault(kind) * Abstractions.Contracts.Wind.Bonus(biome);

    /// <summary>What a generator of <paramref name="kind"/> gives at most on a plot of <paramref name="biome"/> (kW).</summary>
    public static double KwOf(string kind, int biome) => KwOf(kind) * (Steam(kind) && Oasis.Sandy(biome) ? DrySteam : 1);

    /// <summary>Whether a reed fence stands near enough to shelter solar power at the tile from a sandstorm.</summary>
    public static bool Sheltered(IEnumerable<PlotObject> objects, int tileX, int tileY) =>
        objects.Any(o => o.Kind == Items.ReedFence && Math.Abs(o.TileX - tileX) <= FenceReach && Math.Abs(o.TileY - tileY) <= FenceReach);
}

/// <param name="DemandKw">What all its machines draw together.</param>
/// <param name="Hydro">Water wheels and hydro plants on it, which give their power without fuel.</param>
public sealed record Network(
    int Id, IReadOnlyList<Guid> Poles, IReadOnlyList<Guid> Generators, IReadOnlyList<Guid> Consumers, double DemandKw, IReadOnlyList<Guid> Hydro)
{
    /// <summary>Solar panels and towers on it, which give their power by day without fuel.</summary>
    public IReadOnlyList<Guid> Solar { get; init; } = [];

    /// <summary>Wind turbines on it, which give as much as the wind blows, without fuel.</summary>
    public IReadOnlyList<Guid> Wind { get; init; } = [];

    /// <summary>Batteries and energy stores on it: they store what the free power has to spare and give it back when it falls short.</summary>
    public IReadOnlyList<Guid> Batteries { get; init; } = [];

    /// <summary>What its batteries store at most together (kJ).</summary>
    public double BankKj { get; init; }

    /// <summary>What its batteries take in or give out at most together (kW).</summary>
    public double BankKw { get; init; }

    /// <summary>Grid ties on it, which cables join to other plots.</summary>
    public IReadOnlyList<Guid> Ties { get; init; } = [];
}

/// <summary>A cable between a grid tie on one plot and one on another, carrying up to <see cref="Kw"/> while paid for (<see cref="Until"/>).</summary>
public sealed class PowerLink
{
    /// <summary>A cable between two plots of the same owner is never paid for.</summary>
    public static readonly DateTimeOffset Forever = DateTimeOffset.MaxValue;

    public required Guid Id { get; init; }

    public required Guid SupplierTie { get; init; }

    public required Guid ReceiverTie { get; init; }

    public required double Kw { get; init; }

    public DateTimeOffset Until { get; set; }

    public PowerGrid? Supplier { get; set; }

    public PowerGrid? Receiver { get; set; }
}

/// <param name="FuelItem">What lies by the fire, or null.</param>
/// <param name="Fuel">Pieces of it not burning yet.</param>
/// <param name="Energy">kJ left in the piece burning now.</param>
public sealed record GeneratorState(string? FuelItem, int Fuel, double Energy)
{
    public static readonly GeneratorState Empty = new(null, 0, 0);

    /// <summary>kJ left in it, the fuel not burning yet included.</summary>
    [JsonIgnore]
    public double Total => Energy + (FuelItem is { } fuel ? Fuel * PowerRules.Fuels.GetValueOrDefault(fuel) : 0);

    [JsonIgnore]
    public bool Running => Total > 0;

    /// <summary>After giving <paramref name="kj"/>: the burning piece first, then more pieces lit one by one.</summary>
    public GeneratorState Burn(double kj)
    {
        if (kj <= Energy)
        {
            return this with { Energy = Energy - kj };
        }

        var piece = FuelItem is { } fuel ? PowerRules.Fuels.GetValueOrDefault(fuel) : 0;
        var needed = kj - Energy;
        if (piece <= 0 || needed >= Fuel * piece)
        {
            return Empty;
        }

        var lit = (int)Math.Ceiling(needed / piece);
        var left = Fuel - lit;
        return new GeneratorState(left > 0 ? FuelItem : null, left, lit * piece - needed);
    }
}

/// <summary>What is saved of a plot's grid: the generators' fuel and how far each machine's own time
/// lags behind the clock (<see cref="IPowerClock"/>), both as they were at the moment it was settled.</summary>
public sealed class GridData
{
    public Dictionary<Guid, GeneratorState> Generators { get; set; } = [];

    /// <summary>Ticks of time each machine has gone without power.</summary>
    public Dictionary<Guid, long> Lags { get; set; } = [];

    /// <summary>kJ stored in each battery.</summary>
    public Dictionary<Guid, double> Batteries { get; set; } = [];
}

/// <summary>
/// One plot's power from the moment it was last settled on. Nothing changes the grid but fuel put
/// in or taken out, objects put on the plot or taken off, and the sun, which rises, sets and is
/// dimmed by sandstorms on a calendar known in advance. Each change of the first kind settles it
/// first: so how its generators burn down, how its batteries fill and empty and how fast its
/// machines work is known for any time after.
/// </summary>
public sealed class PowerGrid : IPowerGrid
{
    /// <summary>How far ahead a machine's clock looks for when it will have worked long enough.</summary>
    private static readonly TimeSpan WhenHorizon = TimeSpan.FromDays(30);

    /// <summary>How far ahead a generator's window looks for when it burns out.</summary>
    private static readonly TimeSpan RunsOutHorizon = TimeSpan.FromDays(3);

    /// <summary>How closely <see cref="FuelBelow"/> finds the moment a piece of fuel is lit.</summary>
    private static readonly TimeSpan FuelPrecision = TimeSpan.FromMilliseconds(1);

    /// <summary>A stretch of time in which a network runs the same way.</summary>
    /// <param name="Share">The part of its power each running generator gives, 0 to 1: they share the load by their size.</param>
    /// <param name="States">The network's generators as they were at <paramref name="Start"/>.</param>
    /// <param name="Free">What its water and solar power give in it (kW).</param>
    /// <param name="Rate">What goes into its batteries (kW), less than 0 while they give power out.</param>
    /// <param name="Bank">kJ in its batteries at <paramref name="Start"/>.</param>
    /// <param name="LagBefore">Ticks its machines went without power from the moment it was settled to <paramref name="Start"/>.</param>
    private sealed record Segment(
        DateTimeOffset Start, DateTimeOffset? End, double Speed, double Share, IReadOnlyDictionary<Guid, GeneratorState> States,
        double Free, double Rate, double Bank, long LagBefore);

    /// <summary>A network's segments, worked out as far as anyone asked, and where to go on from.</summary>
    private sealed class Timeline(DateTimeOffset start, Dictionary<Guid, GeneratorState> states, double bank)
    {
        public List<Segment> Segments { get; } = [];

        public DateTimeOffset At { get; set; } = start;

        public Dictionary<Guid, GeneratorState> States { get; set; } = states;

        public double Bank { get; set; } = bank;

        public long Lag { get; set; }

        /// <summary>Whether the last segment lasts for ever.</summary>
        public bool Done { get; set; }
    }

    private readonly IReadOnlyList<PlotObject> _objects;
    private readonly Dictionary<Guid, Network> _networkOf = [];
    private readonly HashSet<Guid> _sheltered = [];

    /// <summary>On the glacier, every machine on power and the moment from which no heater warms it any more.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _coldFrom = [];
    private readonly Func<GridData, DateTimeOffset, CancellationToken, Task> _save;
    private Timeline[] _timelines = [];

    public PowerGrid(
        int x, int y, IReadOnlyList<PlotObject> objects, GridData data, DateTimeOffset settled, Func<GridData, DateTimeOffset, CancellationToken, Task> save, int biome = 0)
    {
        X = x;
        Y = y;
        Biome = biome;
        _objects = objects;
        _save = save;
        Data = data;
        Settled = settled;
        Networks = PowerRules.Networks(objects);
        foreach (var network in Networks)
        {
            foreach (var id in network.Generators.Concat(network.Consumers).Concat(network.Hydro).Concat(network.Solar).Concat(network.Wind)
                .Concat(network.Batteries).Concat(network.Ties))
            {
                _networkOf[id] = network;
            }
        }

        foreach (var o in objects.Where(o => Items.IsSolar(o.Kind) && PowerRules.Sheltered(objects, o.TileX, o.TileY)))
        {
            _sheltered.Add(o.Id);
        }

        Plan();
    }

    public int X { get; }

    public int Y { get; }

    /// <summary>The plot's biome (as WorldMap numbers them), which decides what water and solar power give.</summary>
    public int Biome { get; }

    /// <summary>Whether sandstorms blow over the plot.</summary>
    public bool Sandy => Oasis.Sandy(Biome);

    /// <summary>Whether a sandstorm blows over the plot at <paramref name="at"/>.</summary>
    public bool Storm(DateTimeOffset at) => Sandy && SandWeather.IsStorm(at);

    /// <summary>Whether the plot lies on the glacier, where blizzards blow and machines freeze.</summary>
    public bool Icy => Frost.Freezes(Biome);

    /// <summary>Whether a blizzard blows over the plot at <paramref name="at"/>: its wind turbines stand still.</summary>
    public bool Blizzard(DateTimeOffset at) => Icy && Abstractions.Contracts.Blizzard.IsStorm(at);

    /// <summary>What a wind turbine on the plot gives in a full wind (kW).</summary>
    public double WindPeakKw(Guid id) => PowerRules.WindKw(KindOf(id), Biome);

    /// <summary>What a wind turbine gives at <paramref name="at"/> (kW): as much as the wind blows, nothing in a blizzard.</summary>
    public double WindKw(Guid id, DateTimeOffset at) => Blizzard(at) ? 0 : WindPeakKw(id) * Wind.Strength(at);

    /// <summary>What all the wind turbines of a network give together at <paramref name="at"/> (kW).</summary>
    public double WindKw(Network network, DateTimeOffset at) => network.Wind.Sum(id => WindKw(id, at));

    /// <summary>What a water power plant on the plot gives (kW), whether or not a pole reaches it.</summary>
    public double HydroKw(Guid id) => _objects.FirstOrDefault(o => o.Id == id) is { } o ? PowerRules.HydroKw(o.Kind, Biome) : 0;

    /// <summary>What all the water power of a network gives together (kW).</summary>
    public double HydroKw(Network network) => network.Hydro.Sum(HydroKw);

    /// <summary>What a solar power plant on the plot gives in clear sun (kW).</summary>
    public double SolarPeakKw(Guid id) => PowerRules.SolarKw(KindOf(id), Biome);

    /// <summary>Whether a reed fence shelters a solar power plant from sandstorms.</summary>
    public bool Sheltered(Guid id) => _sheltered.Contains(id);

    /// <summary>What a solar power plant gives at <paramref name="at"/> (kW): nothing at night, a quarter in a sandstorm without shelter.</summary>
    public double SolarKw(Guid id, DateTimeOffset at) =>
        !Sun.IsDay(at) ? 0 : SolarPeakKw(id) * (Storm(at) && !Sheltered(id) ? SandWeather.StormSun : 1);

    /// <summary>What all the solar power of a network gives together at <paramref name="at"/> (kW).</summary>
    public double SolarKw(Network network, DateTimeOffset at) => network.Solar.Sum(id => SolarKw(id, at));

    /// <summary>What a network's water and solar power give together at <paramref name="at"/> (kW).</summary>
    public double FreeKw(Network network, DateTimeOffset at) => HydroKw(network) + SolarKw(network, at) + WindKw(network, at) + ImportKw(network, at);

    /// <summary>Cables that take power from this plot to others.</summary>
    public List<PowerLink> Exports { get; } = [];

    /// <summary>Cables that bring power to this plot from others.</summary>
    public List<PowerLink> Imports { get; } = [];

    /// <summary>What a network sends along its cables at <paramref name="at"/> (kW): it draws that like a machine while the cable is paid for.</summary>
    public double ExportKw(Network network, DateTimeOffset at) =>
        Exports.Where(l => at < l.Until && network.Ties.Contains(l.SupplierTie)).Sum(l => l.Kw);

    /// <summary>What a network gets along its cables at <paramref name="at"/> (kW): as much of each as the supplier's network has power for.</summary>
    public double ImportKw(Network network, DateTimeOffset at) =>
        Imports.Where(l => network.Ties.Contains(l.ReceiverTie)).Sum(l => Flow(l, at));

    /// <summary>What a network's machines and the cables it sends power along draw at <paramref name="at"/> (kW).</summary>
    public double DemandKw(Network network, DateTimeOffset at) => network.DemandKw + ExportKw(network, at);

    /// <summary>How well the network of a tie is powered at <paramref name="at"/>: 0 when no pole reaches it.</summary>
    public double TieSpeed(Guid tie, DateTimeOffset at) => _networkOf.TryGetValue(tie, out var network) ? Speed(network, at) : 0;

    /// <summary>What a cable carries at <paramref name="at"/> (kW).</summary>
    public static double Flow(PowerLink link, DateTimeOffset at) => at < link.Until ? link.Kw * (link.Supplier?.TieSpeed(link.SupplierTie, at) ?? 0) : 0;

    /// <summary>Starts the networks' timelines again from the moment the grid was settled, after its cables changed.</summary>
    public void Replan() => Plan();

    /// <summary>What a network's power plants can give at <paramref name="at"/>: running generators, water and solar power and batteries with power in them (kW).</summary>
    public double SupplyKw(Network network, DateTimeOffset at) =>
        network.Generators.Where(g => Generator(g, at).Running).Sum(KwOf) + FreeKw(network, at) + (BankKj(network, at) > 0 ? network.BankKw : 0);

    /// <summary>What a generator on the plot gives at most (kW): a steam generator less on sand.</summary>
    public double KwOf(Guid id) => PowerRules.KwOf(KindOf(id), Biome);

    public IReadOnlyList<Network> Networks { get; }

    public GridData Data { get; private set; }

    /// <summary>The moment the grid is worked out from.</summary>
    public DateTimeOffset Settled { get; private set; }

    public IReadOnlyList<PlotObject> Objects => _objects;

    /// <summary>Whether something changed since it was read.</summary>
    public bool Changed { get; private set; }

    public Network? NetworkOf(Guid id) => _networkOf.GetValueOrDefault(id);

    /// <summary>Whether the grid knows of this object: it stands on the plot or did when the grid was last settled.</summary>
    public bool Knows(Guid id) =>
        _objects.Any(o => o.Id == id) || Data.Lags.ContainsKey(id) || Data.Generators.ContainsKey(id) || Data.Batteries.ContainsKey(id);

    public GeneratorState Generator(Guid id, DateTimeOffset at)
    {
        if (Items.IsHeater(KindOf(id)))
        {
            var heater = Data.Generators.GetValueOrDefault(id) ?? GeneratorState.Empty;
            return heater.Running ? heater.Burn(Frost.HeaterKw * Seconds(Settled, Max(at, Settled))) : heater;
        }

        if (_networkOf.TryGetValue(id, out var network) && SegmentAt(network, at) is { } segment)
        {
            var state = segment.States.GetValueOrDefault(id) ?? GeneratorState.Empty;
            return state.Running && segment.Share > 0 ? state.Burn(segment.Share * KwOf(id) * Seconds(segment.Start, Max(at, Settled))) : state;
        }

        return Data.Generators.GetValueOrDefault(id) ?? GeneratorState.Empty;
    }

    /// <summary>kJ in all the batteries of a network at <paramref name="at"/>.</summary>
    public double BankKj(Network network, DateTimeOffset at) =>
        SegmentAt(network, at) is { } s ? Math.Clamp(s.Bank + s.Rate * Seconds(s.Start, Max(at, Settled)), 0, network.BankKj) : 0;

    /// <summary>kJ in a battery at <paramref name="at"/>: a network's batteries fill and empty together, each by its size.</summary>
    public double Charge(Guid id, DateTimeOffset at) =>
        _networkOf.TryGetValue(id, out var network) && network.BankKj > 0
            ? BankKj(network, at) * PowerRules.StoreKj(KindOf(id)) / network.BankKj
            : Math.Min(PowerRules.StoreKj(KindOf(id)), Data.Batteries.GetValueOrDefault(id));

    /// <summary>What goes into a network's batteries at <paramref name="at"/> (kW), less than 0 while they give power out.</summary>
    public double BankRate(Network network, DateTimeOffset at) => SegmentAt(network, at)?.Rate ?? 0;

    /// <summary>The power a generator gives at <paramref name="at"/> (kW); water and solar power give their share of what their network uses,
    /// and a battery what it gives out (less than 0 while it charges).</summary>
    public double Output(Guid id, DateTimeOffset at)
    {
        if (!_networkOf.TryGetValue(id, out var network) || SegmentAt(network, at) is not { } segment)
        {
            return 0;
        }

        if (network.Hydro.Contains(id) || network.Solar.Contains(id) || network.Wind.Contains(id))
        {
            var used = segment.Free <= 0 ? 0 : Math.Min(1, (DemandKw(network, at) + Math.Max(0, segment.Rate)) / segment.Free);
            return (network.Hydro.Contains(id) ? HydroKw(id) : network.Solar.Contains(id) ? SolarKw(id, at) : WindKw(id, at)) * used;
        }

        if (network.Batteries.Contains(id))
        {
            return network.BankKj > 0 ? -segment.Rate * PowerRules.StoreKj(KindOf(id)) / network.BankKj : 0;
        }

        return segment.States.GetValueOrDefault(id)?.Running == true ? segment.Share * KwOf(id) : 0;
    }

    /// <summary>When a generator burns its last fuel, or null if it never does on what it powers now (looking a few days ahead).</summary>
    public DateTimeOffset? RunsOutAt(Guid id)
    {
        if (Items.IsHeater(KindOf(id)))
        {
            return HeaterOutAt(id);
        }

        if (!_networkOf.TryGetValue(id, out var network))
        {
            return null;
        }

        var timeline = _timelines[network.Id];
        for (var i = 0; Reach(network, i + 1) > i; i++)
        {
            var segment = timeline.Segments[i];
            if (segment.Start > Settled + RunsOutHorizon)
            {
                return null;
            }

            var running = segment.States.GetValueOrDefault(id)?.Running == true;
            var after = i + 1 < timeline.Segments.Count && timeline.Segments[i + 1].States.GetValueOrDefault(id)?.Running == true;
            if (running && !after && segment.End is { } end)
            {
                return end;
            }
        }

        return null;
    }

    /// <summary>The first moment after <paramref name="at"/> a generator has fewer than <paramref name="count"/> pieces of fuel
    /// not burning yet, or null when that does not come within a few days.</summary>
    public DateTimeOffset? FuelBelow(Guid id, int count, DateTimeOffset at)
    {
        bool Below(DateTimeOffset t) => Generator(id, t).Fuel < count;

        if (Below(at))
        {
            return at;
        }

        // Looks ahead twice as far each time, so the timeline is worked out not much past the moment, then halves the gap.
        var (before, step) = (at, TimeSpan.FromSeconds(1));
        while (!Below(at + step))
        {
            if (step >= RunsOutHorizon)
            {
                return null;
            }

            before = at + step;
            step *= 2;
        }

        var after = at + step;
        while (after - before > FuelPrecision)
        {
            var middle = before + (after - before) / 2;
            if (Below(middle))
            {
                after = middle;
            }
            else
            {
                before = middle;
            }
        }

        return after;
    }

    /// <summary>How fast a network's machines work at <paramref name="at"/>.</summary>
    public double Speed(Network network, DateTimeOffset at) => SegmentAt(network, at)?.Speed ?? 0;

    /// <summary>When the way a network runs next changes after <paramref name="at"/>, or null.</summary>
    public DateTimeOffset? NextChange(Network network, DateTimeOffset at) => SegmentAt(network, at)?.End;

    public double Speed(Guid id, DateTimeOffset at) =>
        _networkOf.TryGetValue(id, out var network) ? Speed(network, at) * (Cold(id, at) ? Frost.Speed : 1) : 0;

    /// <summary>When a heater burns its last fuel, or null when it is not burning.</summary>
    public DateTimeOffset? HeaterOutAt(Guid id) =>
        Data.Generators.GetValueOrDefault(id) is { Running: true } heater ? Settled + Ticks(heater.Total / Frost.HeaterKw) : null;

    /// <summary>Whether a machine on the glacier is cold at <paramref name="at"/>: no heater near it burns.</summary>
    public bool Cold(Guid id, DateTimeOffset at) => _coldFrom.TryGetValue(id, out var from) && at >= from;

    /// <summary>The machines on power that are cold at <paramref name="at"/>, for the client to show.</summary>
    public IEnumerable<Guid> ColdMachines(DateTimeOffset at) => _coldFrom.Where(c => at >= c.Value).Select(c => c.Key);

    /// <summary>Whether a heater, or a combined heat and power plant with its longer reach, warms the tile.</summary>
    public bool Warms(PlotObject heater, int tileX, int tileY)
    {
        var reach = heater.Kind == Items.ChpPlant ? PowerRules.ChpReach : Frost.HeaterReach;
        return Math.Abs(heater.TileX - tileX) <= reach && Math.Abs(heater.TileY - tileY) <= reach;
    }

    /// <summary>Until when a combined heat and power plant surely burns, or null when it is not burning: as long as its fuel lasts at full power.
    /// It may burn longer on a lighter load; the grid is worked out again whenever the plot is.</summary>
    public DateTimeOffset? ChpWarmsUntil(Guid id) =>
        Data.Generators.GetValueOrDefault(id) is { Running: true } plant && KwOf(id) > 0 ? Settled + Ticks(plant.Total / KwOf(id)) : null;

    /// <summary>Ticks a machine has gone without power by <paramref name="at"/>.</summary>
    public long Lag(Guid id, DateTimeOffset at)
    {
        var lag = Data.Lags.GetValueOrDefault(id);
        if (at <= Settled)
        {
            return lag;
        }

        if (!_networkOf.TryGetValue(id, out var network) || !PowerRules.Demand.ContainsKey(KindOf(id)))
        {
            return lag + (at - Settled).Ticks;
        }

        var segment = SegmentAt(network, at)!;
        return lag + segment.LagBefore + Unpowered(segment.Speed, (at - segment.Start).Ticks) + FrostLag(id, network, at);
    }

    /// <summary>Ticks a cold machine lost to the frost from the moment it was settled to <paramref name="at"/>, on top of what it lacked in power.</summary>
    private long FrostLag(Guid id, Network network, DateTimeOffset at)
    {
        if (!_coldFrom.TryGetValue(id, out var from) || at <= from)
        {
            return 0;
        }

        double lost = 0;
        foreach (var segment in _timelines[network.Id].Segments)
        {
            if (segment.Start >= at)
            {
                break;
            }

            var start = Max(segment.Start, from);
            var end = segment.End is { } e && e < at ? e : at;
            if (end > start)
            {
                lost += segment.Speed * (1 - Frost.Speed) * (end - start).Ticks;
            }
        }

        return (long)Math.Round(lost);
    }

    public IPowerClock Clock(Guid id) => new MachineClock(this, id);

    public object Summary(DateTimeOffset now) => PowerJson.Grid(this, now);

    public IMachinePort? Port(Guid id) => _objects.Any(o => o.Id == id && (Items.IsGenerator(o.Kind) || Items.IsHeater(o.Kind))) ? new GeneratorPort(this, id) : null;

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Changed)
        {
            await _save(Data, Settled, cancellationToken);
            Changed = false;
        }

        // Settling this grid settled the plots it sends power to.
        foreach (var link in Exports)
        {
            if (link.Receiver is { } receiver)
            {
                await receiver.SaveAsync(cancellationToken);
            }
        }
    }

    private bool _settlingReceivers;

    /// <summary>Brings everything up to <paramref name="at"/> and works on from there, so the grid can change.</summary>
    public void Settle(DateTimeOffset at)
    {
        at = Max(at, Settled);

        // The plots this one sends power to worked out what they got with the grid as it was: they are settled first.
        // Cables never make a loop, but should one appear, a plot already settling its receivers does not go round again.
        if (!_settlingReceivers)
        {
            _settlingReceivers = true;
            try
            {
                foreach (var link in Exports)
                {
                    link.Receiver?.Settle(at);
                }
            }
            finally
            {
                _settlingReceivers = false;
            }
        }

        var data = new GridData();
        foreach (var o in _objects)
        {
            if ((Items.IsGenerator(o.Kind) || Items.IsHeater(o.Kind)) && Generator(o.Id, at) is { Running: true } generator)
            {
                data.Generators[o.Id] = generator;
            }
            else if (Items.IsBattery(o.Kind) && Charge(o.Id, at) is > 0 and var charge)
            {
                data.Batteries[o.Id] = charge;
            }
            else if (PowerRules.Demand.ContainsKey(o.Kind))
            {
                data.Lags[o.Id] = Lag(o.Id, at);
            }
        }

        Data = data;
        Settled = at;
        Changed = true;
        Plan();
    }

    /// <summary>Puts fuel by a generator's fire, at <paramref name="at"/>; fails when it does not fit.</summary>
    public void Fuel(Guid id, string item, int quantity, DateTimeOffset at)
    {
        if (!PowerRules.Burns(KindOf(id), item))
        {
            throw new GameException("not_fuel", "This generator does not burn that.");
        }

        var state = Generator(id, at);
        if (state.FuelItem is { } fuel && fuel != item && state.Fuel > 0)
        {
            throw new GameException("generator_busy", $"{Items.Names[fuel]} lies by the fire; take it out first.");
        }

        if (state.Fuel + quantity > PowerRules.MaxFuel)
        {
            throw new GameException("generator_full", $"A generator holds at most {PowerRules.MaxFuel} pieces of fuel.");
        }

        Settle(at);
        Data.Generators[id] = state with { FuelItem = item, Fuel = state.Fuel + quantity };
        Plan();
    }

    /// <summary>Takes out the fuel not burning yet, at most <paramref name="limit"/> pieces; the piece in the fire burns on, and so
    /// does the fuel left.</summary>
    public (string Item, int Quantity)? Unfuel(Guid id, DateTimeOffset at, int limit = int.MaxValue)
    {
        var state = Generator(id, at);
        if (state.FuelItem is not { } fuel || state.Fuel == 0 || limit < 1)
        {
            return null;
        }

        var taken = Math.Min(limit, state.Fuel);
        Settle(at);
        Data.Generators[id] = taken < state.Fuel ? state with { Fuel = state.Fuel - taken } : state with { FuelItem = null, Fuel = 0 };
        Plan();
        return (fuel, taken);
    }

    /// <summary>A generator or a battery taken off the plot: the grid goes on without it.</summary>
    public void Remove(Guid id, DateTimeOffset at)
    {
        Settle(at);
        Data.Generators.Remove(id);
        Data.Lags.Remove(id);
        Data.Batteries.Remove(id);
        Plan();
    }

    /// <summary>Starts each network's timeline at <see cref="Settled"/>; it is worked out further only as far as someone asks.</summary>
    private void Plan()
    {
        _timelines = Networks
            .Select(n => new Timeline(
                Settled,
                n.Generators.ToDictionary(g => g, g => Data.Generators.GetValueOrDefault(g) ?? GeneratorState.Empty),
                n.Batteries.Sum(b => Math.Min(PowerRules.StoreKj(KindOf(b)), Data.Batteries.GetValueOrDefault(b)))))
            .ToArray();

        // On the glacier a machine stays warm while a heater near it burns: each burns at its own steady pace.
        _coldFrom.Clear();
        if (Icy)
        {
            var heaters = _objects.Where(o => Items.IsHeater(o.Kind) || o.Kind == Items.ChpPlant)
                .Select(h => (Heater: h, Out: Items.IsHeater(h.Kind) ? HeaterOutAt(h.Id) : ChpWarmsUntil(h.Id))).ToList();
            foreach (var o in _objects.Where(o => PowerRules.Demand.ContainsKey(o.Kind)))
            {
                var warm = heaters.Where(h => h.Out is not null && Warms(h.Heater, o.TileX, o.TileY)).Select(h => h.Out!.Value).DefaultIfEmpty(Settled).Max();
                _coldFrom[o.Id] = warm;
            }
        }
    }

    /// <summary>Works a network's timeline out past <paramref name="until"/>.</summary>
    private void Extend(Network network, DateTimeOffset until)
    {
        var timeline = _timelines[network.Id];
        while (!timeline.Done && timeline.At <= until)
        {
            Step(network, timeline);
        }
    }

    /// <summary>Works a network's timeline out to at least <paramref name="count"/> segments, if it has that many; returns how many it has.</summary>
    private int Reach(Network network, int count)
    {
        var timeline = _timelines[network.Id];
        while (!timeline.Done && timeline.Segments.Count < count)
        {
            Step(network, timeline);
        }

        return timeline.Segments.Count;
    }

    /// <summary>
    /// The next segment of a network: until the sun rises or sets or a sandstorm begins or ends (with solar power on it), the wind
    /// changes or a blizzard begins or ends (with wind turbines on it),
    /// its batteries fill up or run empty, or a generator burns out. Water and solar power cover what they can and what
    /// they have to spare goes into the batteries; what they lack comes from the batteries first, then from the generators.
    /// </summary>
    private void Step(Network network, Timeline timeline)
    {
        var (t, states, bank) = (timeline.At, timeline.States, timeline.Bank);
        var demand = DemandKw(network, t);
        var free = FreeKw(network, t);
        DateTimeOffset? end = network.Solar.Count == 0 ? null : Min(Sun.NextChange(t), Sandy ? SandWeather.NextChange(t) : (DateTimeOffset?)null);
        if (network.Wind.Count > 0)
        {
            end = Min(end, Min(Wind.NextChange(t), Icy ? Abstractions.Contracts.Blizzard.NextChange(t) : (DateTimeOffset?)null));
        }

        // Cables change what the network draws and gets when what was paid for runs out, and when the supplier's network changes.
        foreach (var link in Exports.Where(l => network.Ties.Contains(l.SupplierTie)).Concat(Imports.Where(l => network.Ties.Contains(l.ReceiverTie))))
        {
            if (link.Until > t && link.Until != PowerLink.Forever)
            {
                end = Min(end, link.Until);
            }
        }

        foreach (var link in Imports.Where(l => t < l.Until && network.Ties.Contains(l.ReceiverTie)))
        {
            if (link.Supplier is { } supplier && supplier.NetworkOf(link.SupplierTie) is { } far && supplier.NextChange(far, t) is { } change && change > t)
            {
                end = Min(end, change);
            }
        }

        double rate, share = 0, speed;
        Guid? burnsOut = null;
        if (free >= demand)
        {
            speed = 1;
            rate = bank < network.BankKj ? Math.Min(free - demand, network.BankKw) : 0;
            if (rate > 0)
            {
                end = Min(end, t + Ticks((network.BankKj - bank) / rate));
            }
        }
        else
        {
            var lacking = demand - free;
            var discharge = bank > 0 ? Math.Min(lacking, network.BankKw) : 0;
            rate = -discharge;
            lacking -= discharge;
            if (discharge > 0)
            {
                end = Min(end, t + Ticks(bank / discharge));
            }

            var running = states.Where(s => s.Value.Running).Select(s => s.Key).ToList();
            var supply = running.Sum(KwOf);
            if (lacking > 1e-9 && supply > 0)
            {
                share = Math.Min(lacking, supply) / supply;
                var first = running.MinBy(g => states[g].Total / KwOf(g));
                var burnout = t + Ticks(states[first].Total / (share * KwOf(first)));
                if (end is not { } e || burnout <= e)
                {
                    end = burnout;
                    burnsOut = first;
                }
            }

            speed = demand <= 0 ? 1 : Math.Min(1, (free + discharge + share * supply) / demand);
        }

        timeline.Segments.Add(new Segment(t, end, speed, share, states, free, rate, bank, timeline.Lag));
        if (end is not { } stop)
        {
            timeline.Done = true;
            return;
        }

        var seconds = Seconds(t, stop);
        timeline.States = states.ToDictionary(s => s.Key, s =>
            s.Key == burnsOut ? GeneratorState.Empty : s.Value.Running && share > 0 ? s.Value.Burn(share * KwOf(s.Key) * seconds) : s.Value);
        timeline.Bank = Math.Clamp(bank + rate * seconds, 0, network.BankKj);
        timeline.Lag += Unpowered(speed, (stop - t).Ticks);
        timeline.At = stop;
    }

    private Segment? SegmentAt(Network network, DateTimeOffset at)
    {
        Extend(network, at);
        var segments = _timelines[network.Id].Segments;
        var (low, high) = (0, segments.Count - 1);
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (segments[mid].Start <= at)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return segments.Count == 0 ? null : segments[low];
    }

    public string KindOf(Guid id) => _objects.FirstOrDefault(o => o.Id == id)?.Kind ?? "";

    /// <summary>A segment from <paramref name="start"/> on, cut where the machine grows cold, with the pace it works at in each piece.</summary>
    private IEnumerable<(DateTimeOffset From, DateTimeOffset? To, double Speed)> Pieces(Guid id, Segment segment, DateTimeOffset start)
    {
        if (!_coldFrom.TryGetValue(id, out var cold) || (segment.End is { } over && over <= cold))
        {
            yield return (start, segment.End, segment.Speed);
            yield break;
        }

        if (start < cold)
        {
            yield return (start, cold, segment.Speed);
            start = cold;
        }

        yield return (start, segment.End, segment.Speed * Frost.Speed);
    }

    private static long Unpowered(double speed, long ticks) => speed >= 1 ? 0 : speed <= 0 ? ticks : (long)Math.Round((1 - speed) * ticks);

    private static double Seconds(DateTimeOffset from, DateTimeOffset to) => Math.Max(0, (to - from).Ticks) / (double)TimeSpan.TicksPerSecond;

    /// <summary>A stretch of <paramref name="seconds"/>, at least a tick and at most a century.</summary>
    private static TimeSpan Ticks(double seconds) =>
        TimeSpan.FromTicks(Math.Max(1, (long)Math.Ceiling(Math.Min(seconds, 3.15e9) * TimeSpan.TicksPerSecond)));

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>A machine's own time: the clock less the time it went without power.</summary>
    private sealed class MachineClock(PowerGrid grid, Guid id) : IPowerClock
    {
        public DateTimeOffset At(DateTimeOffset now) => now - TimeSpan.FromTicks(grid.Lag(id, now));

        public double Speed(DateTimeOffset now) => grid.Speed(id, now);

        public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now)
        {
            if (At(now) >= machineTime)
            {
                return now;
            }

            if (grid._networkOf.GetValueOrDefault(id) is not { } network)
            {
                return null;
            }

            var from = Max(now, grid.Settled);
            double need = (machineTime - At(from)).Ticks;
            grid.Extend(network, from);
            var segments = grid._timelines[network.Id].Segments;
            for (var i = 0; grid.Reach(network, i + 1) > i; i++)
            {
                var segment = segments[i];
                if (segment.End is { } over && over <= from)
                {
                    continue;
                }

                var start = Max(segment.Start, from);
                if (start > from + WhenHorizon)
                {
                    return null;
                }

                // A machine on the glacier works slower from the moment no heater warms it.
                foreach (var (from2, to, speed) in grid.Pieces(id, segment, start))
                {
                    if (speed <= 0)
                    {
                        continue;
                    }

                    var due = from2 + TimeSpan.FromTicks((long)Math.Ceiling(need / speed));
                    if (to is not { } stop || due <= stop)
                    {
                        return due;
                    }

                    need -= (stop - from2).Ticks * speed;
                }
            }

            return null;
        }
    }

    /// <summary>A generator as inserters see it: wood or coal goes in, nothing comes out.</summary>
    private sealed class GeneratorPort(PowerGrid grid, Guid id) : IMachinePort
    {
        public string? Output(DateTimeOffset now, Func<string, bool> wanted) => null;

        public void Take(string item, DateTimeOffset now)
        {
        }

        public bool Accepts(string item, DateTimeOffset now)
        {
            if (!PowerRules.Burns(grid.KindOf(id), item))
            {
                return false;
            }

            var state = grid.Generator(id, now);
            return (state.FuelItem is null || state.FuelItem == item || state.Fuel == 0) && state.Fuel < PowerRules.MaxFuel;
        }

        public void Put(string item, DateTimeOffset now) => grid.Fuel(id, item, 1, now);

        /// <summary>A full fire makes room as it lights its next piece, and one with another fuel by it once it has lit the
        /// last of that: an inserter waiting with fuel goes on then, also while nobody looks at the plot.</summary>
        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            var state = grid.Generator(id, now);
            return state.Fuel == 0 ? null : grid.FuelBelow(id, state.Fuel >= PowerRules.MaxFuel ? PowerRules.MaxFuel : 1, now);
        }

        public Task SaveAsync(CancellationToken cancellationToken) => grid.SaveAsync(cancellationToken);
    }
}
