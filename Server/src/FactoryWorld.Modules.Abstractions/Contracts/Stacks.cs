namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// How many pieces of one item a tile of the player's inventory holds: its stack. More pieces than a stack take more tiles,
/// so the 20 tiles of an inventory without a backpack hold 4000 stone at most. Loose raw goods stack highest; machines,
/// tools and the largest buildings lowest. Storages (chests, the shelf, heaps and silos) count their room their own way.
/// </summary>
public static class Stacks
{
    /// <summary>Raw goods: stone, ore, coal, wood, crops, saplings, fluids and waste.</summary>
    public const int Bulk = 200;

    /// <summary>Materials and half-products (ingots, bricks, glass, flour...), and belts, pipes and roads; what has no other stack.</summary>
    public const int Material = 100;

    /// <summary>Meals, parts, inserters and splitters, poles, chests and the other small things put on plots by the dozen, and finds.</summary>
    public const int Part = 50;

    /// <summary>Machines and buildings, furniture and goods for towns, animals, decorations and the parts of rockets and reactors.</summary>
    public const int Machine = 10;

    /// <summary>Tools, backpacks, clothes, the largest buildings and the rocket, the satellite and the fusion reactor.</summary>
    public const int Single = 1;

    /// <summary>The stack of <paramref name="item"/>.</summary>
    public static int Of(string item) => Sizes.TryGetValue(item, out var size) ? size : Material;

    /// <summary>Tiles <paramref name="quantity"/> pieces of <paramref name="item"/> take: full stacks and one more for the rest.</summary>
    public static long Tiles(string item, long quantity) => quantity <= 0 ? 0 : (quantity + Of(item) - 1) / Of(item);

    private static readonly HashSet<string> RawGoods =
    [
        Items.Stone, Items.Sand, Items.Coal, Items.Charcoal, Items.Coke, Items.Clay, Items.Ice, Items.Wood, Items.OakWood, Items.Manure,
        Items.IronOre, Items.CopperOre, Items.TinOre, Items.SilverOre, Items.GoldOre, Items.UraniumOre,
        Items.CrushedIronOre, Items.CrushedCopperOre, Items.CrushedTinOre, Items.CrushedSilverOre, Items.CrushedGoldOre,
        Items.Sulfur, Items.Salt, Items.Marble, Items.Quartz,
        Items.Wheat, Items.Carrot, Items.Cabbage, Items.Corn, Items.Potato, Items.Sunflower, Items.SugarBeet, Items.Flax, Items.Hemp,
        Items.Dates, Items.Mushrooms, Items.Berries, Items.Herbs, Items.Seaweed, Items.Hydrogen, Items.Trash,
    ];

    /// <summary>Belts, pipes and roads are laid by the hundred.</summary>
    private static readonly HashSet<string> Lines = [Items.Belt, Items.FastBelt, Items.ExpressBelt, Items.Pipe, Items.Road];

    private static readonly HashSet<string> Parts =
    [
        // Meals and dishes.
        Items.Bread, Items.CornBread, Items.CarrotCake, Items.Fries, Items.VegetableSoup, Items.Bigos, Items.ScrambledEggs, Items.Sauerkraut,
        Items.SmokedFish, Items.SmokedSalmon, Items.SaltedHerring, Items.CannedFish, Items.FriedCarp, Items.FishSoup, Items.DriedMeat,
        Items.WorkersLunch, Items.CheeseSandwich, Items.MinersBreakfast, Items.LumberjackGoulash, Items.RoastBeef, Items.Cheesecake,
        Items.Donuts, Items.MushroomSoup, Items.BerryPierogi, Items.HerbalTea, Items.Gingerbread, Items.IceCream, Items.Provisions,
        // Parts of the crafting tree and electronics.
        Items.Bearing, Items.Coil, Items.WiringHarness, Items.Gearbox, Items.Rotor, Items.MachineHousing, Items.CombustionChamber,
        Items.CompositePanel, Items.SuperconductingMagnet, Items.Alternator, Items.ControlModule, Items.HydraulicActuator,
        Items.RocketNozzle, Items.PressureVessel, Items.ComputingCore, Items.RobotArm, Items.Turbopump, Items.TurbineSet,
        Items.CryogenicTank, Items.IndustrialRobot, Items.NavigationSystem, Items.CircuitBoard, Items.ElectricMotor, Items.Transformer,
        Items.FuelRod, Items.Jewelry, Items.Silverware, Items.Tyres,
        // Small things put on plots by the dozen.
        Items.Inserter, Items.FastInserter, Items.ExpressInserter, Items.LongInserter, Items.Pump, Items.Splitter, Items.UndergroundBelt,
        Items.Pole, Items.Substation, Items.Pylon, Items.WoodenChest, Items.IronChest, Items.SolarPanel, Items.Battery, Items.ReedFence,
        Items.LoadingChest, Items.LoadingChest2, Items.LoadingChest3, Items.LoadingChest4,
        Items.ReceivingChest, Items.ReceivingChest2, Items.ReceivingChest3, Items.ReceivingChest4,
        // Finds and holiday treats.
        Items.Amber, Items.Ammonite, Items.AncientCoin, Items.DinosaurBone, Items.Candy, Items.Bauble,
    ];

    private static readonly HashSet<string> Goods =
    [
        Items.Chair, Items.Table, Items.Wardrobe, Items.OakDresser, Items.Bicycle, Items.Radio, Items.WashingMachine, Items.Car,
        Items.RocketEngine, Items.PlasmaChamber, Items.Avionics, Items.PlasmaControl, Items.RocketStage,
    ];

    /// <summary>What crowns the crafting tree: one to a tile.</summary>
    private static readonly HashSet<string> Crowns = [Items.LaunchRocket, Items.Satellite, Items.FusionReactor];

    /// <summary>The stack of every item of the game. Declared after the sets it is made of: static fields start in order.</summary>
    public static readonly IReadOnlyDictionary<string, int> Sizes = Items.Names.Keys.ToDictionary(id => id, Size);

    private static int Size(string id)
    {
        if (Items.IsTool(id) || Items.IsBackpack(id) || Items.IsClothing(id) || Crowns.Contains(id) || Footprints.Size(id) >= 5)
        {
            return Single;
        }

        if (RawGoods.Contains(id) || Items.IsSapling(id) || Items.IsFluid(id) || id.StartsWith("waste_", StringComparison.Ordinal))
        {
            return Bulk;
        }

        if (Lines.Contains(id))
        {
            return Material;
        }

        if (Parts.Contains(id))
        {
            return Part;
        }

        if (Goods.Contains(id) || Items.IsDecoration(id) || Items.IsSouvenir(id)
            || id.StartsWith("animal_", StringComparison.Ordinal) || id.StartsWith("furniture_", StringComparison.Ordinal)
            || id.StartsWith("power_", StringComparison.Ordinal) || id.StartsWith("storage_", StringComparison.Ordinal)
            || id.StartsWith("logistics_", StringComparison.Ordinal))
        {
            return Machine;
        }

        return Material;
    }
}
