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
