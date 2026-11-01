using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Market;

/// <summary>
/// The NPC trader: always sells trash, saplings, animals, nails, tools, backpacks, an anvil, a research table, a chainsaw, belts, inserters, wooden chests, machines (a forester's hut and a sawmill among them), the computer, generators, poles, flour, oil, bread and planks and always buys raw materials.
/// Everything else it buys it sells too, but dear: <see cref="Markup"/> times what it pays, so buying is a last resort and never pays to sell back.
/// These are its usual prices (cents); <see cref="TraderPrices"/> moves them with the day's drift and the market's events.
/// What it pays is the floor of the market: a quick sale gets it at once, players' listings usually pay more.
/// </summary>
public static class NpcPrices
{
    /// <summary>What the NPC charges for an item it has no price of its own for, in times what it pays for it.</summary>
    public const int Markup = 10;

    /// <summary>What the NPC charges the player: its own prices, then <see cref="Markup"/> times what it pays for the rest.</summary>
    public static readonly IReadOnlyDictionary<string, long> Sells;

    /// <summary>What the NPC charges for the things it stocks on purpose, at prices of their own.</summary>
    private static readonly Dictionary<string, long> OwnSells = new()
    {
        [Items.Trash] = 100,
        [Items.SortingMachine] = 100_000,
        [Items.Sapling] = 200,
        [Items.SaplingSpruce] = 200,
        [Items.SaplingBirch] = 200,
        [Items.SaplingOak] = 500,
        [Items.SaplingLinden] = 300,
        [Items.Nails] = 500,
        [Items.Workbench] = 50_000,
        [Items.Anvil] = 50_000,
        [Items.ClothesWardrobe] = 40_000,
        [Items.TrophyCase] = 60_000,
        [Items.ResearchTable] = 200_000,
        [Items.Axe] = 10_000,
        [Items.Shovel] = 10_000,
        [Items.Pickaxe] = 10_000,
        [Items.Hoe] = 10_000,
        [Items.Chainsaw] = 50_000,
        [Items.Shears] = 10_000,
        [Items.SchoolBackpack] = 50_000,
        [Items.HikingBackpack] = 500_000,
        [Items.TrekkingBackpack] = 2_500_000,
        [Items.ExpeditionBackpack] = 15_000_000,
        [Items.TycoonBackpack] = 100_000_000,
        [Items.Chicken] = 5000,
        [Items.Sheep] = 20_000,
        [Items.Cow] = 50_000,
        [Items.Camel] = 40_000,
        [Items.Pig] = 30_000,
        [Items.Goat] = 15_000,
        [Items.Belt] = 1000,
        [Items.Inserter] = 9000,
        [Items.Splitter] = 18_000,
        [Items.WoodenChest] = 7000,
        [Items.Mill] = 40_000,
        [Items.OilPress] = 40_000,
        [Items.PicklingBarrel] = 10_000,
        [Items.Bakery] = 60_000,
        [Items.Kitchen] = 60_000,
        [Items.Computer] = 80_000,
        [Items.Flour] = 400,
        [Items.SunflowerOil] = 1500,
        [Items.Bread] = 1500,
        [Items.Generator] = 60_000,
        [Items.Pole] = 2000,
        [Items.ForesterHut] = 60_000,
        [Items.Sawmill] = 40_000,
        [Items.Planks] = 300,
        [Items.FishingRod] = 10_000,
        // Concrete from the trader is a dear way out: making it in a mixer is far cheaper.
        [Items.Concrete] = 25_000,
        // A bank is bought only, never made, and the trader does not buy it back.
        [Items.Bank] = 10_000_000_000_00,
        [Items.BankBranch] = 5_000_000_000_00,
    };

    /// <summary>What the NPC pays the player.</summary>
    public static readonly IReadOnlyDictionary<string, long> Buys = new Dictionary<string, long>
    {
        [Items.WastePaper] = 100,
        [Items.WastePlastic] = 200,
        [Items.WasteGlass] = 200,
        [Items.WasteAluminium] = 600,
        [Items.WasteCopper] = 1500,
        [Items.Sand] = 50,
        [Items.Stone] = 100,
        [Items.Wood] = 200,
        [Items.Sapling] = 100,
        [Items.Coal] = 400,
        [Items.IronOre] = 600,
        [Items.CopperOre] = 1200,
        [Items.TinOre] = 2500,
        [Items.SilverOre] = 6000,
        [Items.GoldOre] = 50_000,
        [Items.IronIngot] = 1000,
        [Items.CopperIngot] = 1800,
        [Items.TinIngot] = 3500,
        [Items.SilverIngot] = 8000,
        [Items.GoldIngot] = 60_000,
        [Items.Nails] = 300,
        [Items.Wheat] = 60,
        [Items.Carrot] = 90,
        [Items.Potato] = 150,
        [Items.Corn] = 220,
        [Items.Cabbage] = 250,
        [Items.Sunflower] = 300,
        [Items.SortingMachine] = 50_000,
        [Items.Workbench] = 8000,
        [Items.ClothesWardrobe] = 10_000,
        [Items.TrophyCase] = 15_000,
        [Items.Furnace] = 3000,
        [Items.BrickFurnace] = 26_000,
        [Items.BlastFurnace] = 171_000,
        [Items.Axe] = 5000,
        [Items.Shovel] = 3500,
        [Items.Pickaxe] = 5000,
        [Items.Hoe] = 3500,
        [Items.Chainsaw] = 10_000,
        [Items.Shears] = 3200,
        [Items.SchoolBackpack] = 10_000,
        [Items.HikingBackpack] = 100_000,
        [Items.TrekkingBackpack] = 500_000,
        [Items.ExpeditionBackpack] = 3_000_000,
        [Items.TycoonBackpack] = 20_000_000,
        [Items.Chicken] = 1000,
        [Items.Sheep] = 5000,
        [Items.Cow] = 12_000,
        [Items.Egg] = 80,
        [Items.Wool] = 500,
        [Items.Milk] = 250,
        [Items.Anvil] = 23_000,
        [Items.ResearchTable] = 40_000,
        [Items.Belt] = 700,
        [Items.Inserter] = 6500,
        [Items.Splitter] = 13_000,
        [Items.FastBelt] = 4500,
        [Items.ExpressBelt] = 12_000,
        [Items.FastInserter] = 18_000,
        [Items.ExpressInserter] = 45_000,
        [Items.UndergroundBelt] = 6000,
        [Items.LongInserter] = 15_000,
        [Items.LogisticsDepot] = 110_000,
        [Items.DronePort] = 210_000,
        [Items.WoodenChest] = 5000,
        [Items.IronChest] = 11_000,
        [Items.WoodPile] = 1200,
        [Items.Heap] = 2500,
        [Items.GrainSilo] = 20_000,
        [Items.Mill] = 10_000,
        [Items.RollerMill] = 48_000,
        [Items.FlourWorks] = 175_000,
        [Items.OilPress] = 14_000,
        [Items.PicklingBarrel] = 5500,
        [Items.Bakery] = 11_000,
        [Items.Kitchen] = 15_000,
        [Items.Computer] = 20_000,
        [Items.Flour] = 200,
        [Items.CornFlour] = 600,
        [Items.SunflowerOil] = 900,
        [Items.Sauerkraut] = 350,
        [Items.Bread] = 700,
        [Items.CornBread] = 1600,
        [Items.CarrotCake] = 2400,
        [Items.Fries] = 900,
        [Items.VegetableSoup] = 1100,
        [Items.Bigos] = 750,
        [Items.ScrambledEggs] = 850,
        [Items.Generator] = 23_000,
        [Items.Pole] = 1500,
        [Items.ForesterHut] = 24_000,
        [Items.ForesterLodge] = 65_000,
        [Items.ForestryStation] = 242_000,
        [Items.Sawmill] = 22_000,
        [Items.ElectricSawmill] = 95_000,
        [Items.IndustrialSawmill] = 322_000,
        [Items.Assembler] = 35_000,
        [Items.FastAssembler] = 150_000,
        [Items.ExpressAssembler] = 420_000,
        [Items.Planks] = 150,
        [Items.Beams] = 600,
        [Items.WoodPellets] = 200,
        [Items.Plywood] = 600,
        [Items.Sulfur] = 1500,
        [Items.Salt] = 800,
        [Items.Marble] = 3000,
        [Items.Quartz] = 2000,
        [Items.CrushedIronOre] = 560,
        [Items.CrushedCopperOre] = 1100,
        [Items.CrushedTinOre] = 2300,
        [Items.StoneBlocks] = 450,
        [Items.MarbleSlabs] = 2200,
        [Items.Bricks] = 110,
        [Items.BronzeIngot] = 3100,
        [Items.SteelIngot] = 3400,
        [Items.Dynamite] = 1700,
        [Items.MineSupport] = 11_500,
        [Items.BronzePickaxe] = 14_000,
        [Items.SteelPickaxe] = 15_000,
        [Items.Drill] = 102_000,
        [Items.HeavyDrill] = 300_000,
        [Items.Quarry] = 30_000,
        [Items.Crusher] = 60_000,
        [Items.MechanizedQuarry] = 110_000,
        [Items.OpenPitMine] = 340_000,
        [Items.ConeCrusher] = 150_000,
        [Items.OreProcessingPlant] = 420_000,
        [Items.Stonemason] = 21_500,
        [Items.Foundry] = 61_000,
        [Items.WaterWheel] = 41_000,
        [Items.HydroPlant] = 245_000,
        [Items.CableStation] = 38_000,
        [Items.ExpressCableStation] = 80_000,
        [Items.FishingRod] = 3500,
        [Items.Herring] = 120,
        [Items.Cod] = 300,
        [Items.Carp] = 250,
        [Items.Pike] = 350,
        [Items.Salmon] = 900,
        [Items.Mussels] = 400,
        [Items.Seaweed] = 60,
        [Items.Amber] = 50_000,
        [Items.SmokedFish] = 900,
        [Items.SmokedSalmon] = 2200,
        [Items.SaltedHerring] = 450,
        [Items.CannedFish] = 2600,
        [Items.FishMeal] = 220,
        [Items.FishOil] = 650,
        [Items.FriedCarp] = 1300,
        [Items.FishSoup] = 450,
        [Items.FishingBoat] = 30_000,
        [Items.RowingBoat] = 12_000,
        [Items.Smokehouse] = 12_000,
        [Items.Cannery] = 60_000,
        [Items.SaltPan] = 14_000,
        [Items.FishFarm] = 25_000,
        [Items.Shipyard] = 35_000,
        [Items.Port] = 40_000,
        [Items.Breakwater] = 12_000,
        [Items.Lighthouse] = 60_000,
        [Items.TidalPlant] = 220_000,
        [Items.CrudeOil] = 150,
        [Items.NaturalGas] = 120,
        [Items.Gasoline] = 400,
        [Items.Diesel] = 380,
        [Items.Kerosene] = 280,
        [Items.Asphalt] = 220,
        [Items.Plastic] = 300,
        [Items.SyntheticRubber] = 2100,
        [Items.Fertilizer] = 150,
        [Items.Gasket] = 1600,
        [Items.Pumpjack] = 800_000,
        [Items.GasWell] = 550_000,
        [Items.OffshorePlatform] = 4_500_000,
        [Items.WaterInjector] = 700_000,
        [Items.OilSeparator] = 420_000,
        [Items.Distillery] = 45_000,
        [Items.Refinery] = 314_000,
        [Items.Cracker] = 270_000,
        [Items.ChemicalPlant] = 210_000,
        [Items.GasPlant] = 150_000,
        [Items.DieselGenerator] = 95_000,
        [Items.Pipe] = 900,
        [Items.Pump] = 9000,
        [Items.Tank] = 37_000,
        [Items.Road] = 200,
        [Items.TruckDepot] = 135_000,
        [Items.Scrubber] = 50_000,
        [Items.Glass] = 300,
        [Items.WindowPane] = 3100,
        [Items.GlassBottles] = 250,
        [Items.OpticalFiber] = 2200,
        [Items.Silicon] = 2500,
        [Items.SiliconWafer] = 1800,
        [Items.Water] = 20,
        [Items.Clay] = 100,
        [Items.AdobeBricks] = 120,
        [Items.Cement] = 400,
        [Items.Concrete] = 250,
        [Items.Dates] = 200,
        [Items.Mirror] = 12_000,
        [Items.Ammonite] = 200_000,
        [Items.AncientCoin] = 400_000,
        [Items.DinosaurBone] = 1_000_000,
        [Items.Camel] = 10_000,
        [Items.Glassworks] = 40_000,
        [Items.ArcFurnace] = 250_000,
        [Items.WaterWell] = 45_000,
        [Items.Desalinator] = 120_000,
        [Items.DryingYard] = 8000,
        [Items.CementKiln] = 50_000,
        [Items.ConcreteMixer] = 60_000,
        [Items.ReedFence] = 300,
        [Items.SolarPanel] = 12_000,
        [Items.SolarTower] = 600_000,
        [Items.Battery] = 15_000,
        [Items.Ice] = 30,
        [Items.MeteoricIron] = 15_000,
        [Items.Iridium] = 250_000,
        [Items.MeteoritePickaxe] = 60_000,
        [Items.SteelSaw] = 30_000,
        [Items.HarvesterSaw] = 50_000,
        [Items.MeteoriteSaw] = 400_000,
        [Items.FrozenFish] = 450,
        [Items.FrozenSalmon] = 1200,
        [Items.IceCream] = 400,
        [Items.ComputingPower] = 800,
        [Items.IceCore] = 1500,
        [Items.ResearchData] = 5000,
        [Items.WindTurbine] = 20_000,
        [Items.LargeWindTurbine] = 700_000,
        [Items.Heater] = 12_000,
        [Items.IceMelter] = 4000,
        [Items.ColdStore] = 18_000,
        [Items.ServerRoom] = 400_000,
        [Items.IceCoreDrill] = 60_000,
        [Items.PolarStation] = 50_000,
        [Items.IceHotel] = 60_000,
        [Items.Viewpoint] = 5000,
        [Items.DogKennel] = 12_000,
        [Items.SnowmobileGarage] = 70_000,
        [Items.TrackFoundation] = 30_000,
        [Items.CircuitBoard] = 2600,
        [Items.ElectricMotor] = 12_000,
        [Items.Tyres] = 16_500,
        [Items.Bicycle] = 19_000,
        [Items.Radio] = 7000,
        [Items.WashingMachine] = 40_000,
        [Items.Car] = 150_000,
        [Items.Blueprint] = 1500,
        [Items.AssemblyHall] = 384_000,
        [Items.MultistoreyFactory] = 1_052_000,
        [Items.Shop] = 15_000,
        [Items.HighBay] = 206_000,
        [Items.CitySorter] = 60_000,
        [Items.FreightTerminal] = 300_000,
        [Items.ExportChest] = 30_000,
        // Freight chests: each size is built from the one before.
        [Items.LoadingChest] = 21_000,
        [Items.LoadingChest2] = 52_000,
        [Items.LoadingChest3] = 160_000,
        [Items.LoadingChest4] = 440_000,
        [Items.ReceivingChest] = 21_000,
        [Items.ReceivingChest2] = 52_000,
        [Items.ReceivingChest3] = 160_000,
        [Items.ReceivingChest4] = 440_000,
        [Items.BuyMarket] = 150_000,
        [Items.RailPort] = 250_000,
        [Items.CompanyHq] = 600_000,
        [Items.DesignOffice] = 20_000,
        [Items.Laboratory] = 60_000,
        [Items.ServiceWorkshop] = 25_000,
        [Items.Tailor] = 30_000,
        [Items.MinerHelmet] = 16_000,
        [Items.SouWester] = 7500,
        [Items.StrawHat] = 2000,
        [Items.Beanie] = 4000,
        [Items.FlannelJacket] = 8000,
        [Items.SmithApron] = 14_000,
        [Items.Oilskin] = 20_000,
        [Items.SuitJacket] = 22_000,
        [Items.Overalls] = 9000,
        [Items.WorkTrousers] = 11_000,
        [Items.SuitTrousers] = 8500,
        [Items.RubberBoots] = 13_000,
        [Items.WorkBoots] = 14_000,
        [Items.DressShoes] = 7500,
        [Items.SaplingSpruce] = 100,
        [Items.SaplingBirch] = 100,
        [Items.SaplingOak] = 250,
        [Items.SaplingLinden] = 150,
        [Items.OakWood] = 600,
        [Items.Resin] = 300,
        [Items.Pulp] = 300,
        [Items.Paper] = 250,
        [Items.Cardboard] = 350,
        [Items.Chair] = 1500,
        [Items.Table] = 3000,
        [Items.Wardrobe] = 6000,
        [Items.OakDresser] = 12_000,
        // Charcoal is worth what coal is: a furnace hands what it burns back as coal.
        [Items.Charcoal] = 400,
        [Items.Mushrooms] = 150,
        [Items.Berries] = 80,
        [Items.Herbs] = 120,
        [Items.MushroomSoup] = 900,
        [Items.BerryPierogi] = 800,
        [Items.HerbalTea] = 400,
        [Items.Honey] = 500,
        [Items.Wax] = 300,
        [Items.Gingerbread] = 1500,
        [Items.Candles] = 600,
        [Items.Tar] = 500,
        [Items.Turpentine] = 900,
        [Items.Rosin] = 500,
        [Items.Insecticide] = 1500,
        [Items.PaperMill] = 60_000,
        [Items.Carpentry] = 20_000,
        [Items.CharcoalKiln] = 6000,
        [Items.TarKiln] = 15_000,
        [Items.Beehive] = 3000,
        [Items.Watchtower] = 8000,
        [Items.FireStation] = 40_000,
        // The crafting tree: each level pays 1.4 times what its ingredients would fetch.
        [Items.Lubricant] = 600,
        [Items.ElectricWire] = 750,
        [Items.GlassLaminate] = 850,
        [Items.Paint] = 900,
        [Items.CaseinGlue] = 700,
        [Items.InsulationBoard] = 2200,
        [Items.Firebrick] = 750,
        [Items.PrecastConcrete] = 6200,
        [Items.Coolant] = 250,
        [Items.IridiumAlloy] = 400_000,
        [Items.Provisions] = 3000,
        [Items.Bearing] = 2800,
        [Items.PaintedSheet] = 3000,
        [Items.Coil] = 7500,
        [Items.WiringHarness] = 12_500,
        [Items.RefractoryLining] = 17_000,
        [Items.CarbonComposite] = 2200,
        [Items.SuperconductingWire] = 140_000,
        [Items.RocketFuel] = 750,
        [Items.Gearbox] = 17_500,
        [Items.Rotor] = 38_500,
        [Items.MachineHousing] = 24_500,
        [Items.CombustionChamber] = 71_000,
        [Items.CompositePanel] = 13_500,
        [Items.SuperconductingMagnet] = 805_000,
        [Items.Alternator] = 105_000,
        [Items.ControlModule] = 63_500,
        [Items.HydraulicActuator] = 40_000,
        [Items.RocketNozzle] = 115_000,
        [Items.PressureVessel] = 63_500,
        [Items.ComputingCore] = 200_000,
        [Items.RobotArm] = 235_000,
        [Items.Turbopump] = 320_000,
        [Items.TurbineSet] = 245_000,
        [Items.CryogenicTank] = 190_000,
        [Items.IndustrialRobot] = 1_100_000,
        [Items.NavigationSystem] = 380_000,
        [Items.RocketEngine] = 1_250_000,
        [Items.PlasmaChamber] = 9_550_000,
        [Items.Avionics] = 1_150_000,
        [Items.PlasmaControl] = 6_800_000,
        [Items.RocketStage] = 9_300_000,
        [Items.Satellite] = 2_950_000,
        [Items.FusionReactor] = 37_500_000,
        [Items.LaunchRocket] = 28_000_000,
        [Items.MachineShop] = 87_500,
        [Items.RoboticHall] = 5_250_000,
        [Items.OxygenConverter] = 485_000,
        [Items.RollingMill] = 655_000,
        [Items.ControlCenter] = 1_550_000,
        [Items.ContinuousCaster] = 1_900_000,
        [Items.AutomatedWarehouse] = 4_250_000,
        [Items.SteelCombine] = 8_000_000,
        [Items.Megafactory] = 23_500_000,
        [Items.FusionPlant] = 56_500_000,
        // Bigger power: about 1.1 times what went into it; uranium ore is rare, from deep mines only.
        [Items.Transformer] = 20_000,
        [Items.Substation] = 50_000,
        [Items.Pylon] = 30_000,
        [Items.CoalPlant] = 240_000,
        [Items.EnergyStore] = 260_000,
        [Items.UraniumOre] = 8000,
        [Items.FuelRod] = 40_000,
        [Items.NuclearPlant] = 1_350_000,
        [Items.GridTie] = 120_000,
        // Husbandry and gastronomy: what the other industries need pays about 1.4 times what went into it.
        [Items.Meat] = 900,
        [Items.Hide] = 3000,
        [Items.Tallow] = 250,
        [Items.Bones] = 100,
        [Items.Manure] = 40,
        [Items.Gelatin] = 600,
        [Items.CultureMedium] = 1100,
        [Items.Cheese] = 1400,
        [Items.Butter] = 1050,
        [Items.Casein] = 700,
        [Items.Ethanol] = 380,
        [Items.Stillage] = 100,
        [Items.DriedMeat] = 2400,
        [Items.WorkersLunch] = 1200,
        [Items.CheeseSandwich] = 1500,
        [Items.MinersBreakfast] = 2700,
        [Items.LumberjackGoulash] = 3300,
        [Items.ConcentrateFeed] = 380,
        [Items.RoastBeef] = 2800,
        [Items.Cheesecake] = 2900,
        [Items.Horse] = 20_000,
        [Items.LeatherBoots] = 14_000,
        [Items.Canteen] = 43_000,
        [Items.Slaughterhouse] = 22_000,
        [Items.Dairy] = 28_000,
        [Items.PotatoDistillery] = 85_000,
        [Items.BiogasPlant] = 150_000,
        [Items.Stable] = 31_500,
        [Items.Feeder] = 3800,
        [Items.Bellows] = 12_000,
        [Items.Farmstead] = 28_500,
        [Items.ManorFarm] = 103_000,
        // The raw-material branches grown further: products about 1.2-1.4 times what went into them, machines about 1.1 times.
        [Items.Coke] = 600,
        [Items.CrushedSilverOre] = 5600,
        [Items.CrushedGoldOre] = 46_000,
        [Items.Jewelry] = 125_000,
        [Items.Silverware] = 19_000,
        [Items.Yarn] = 600,
        [Items.Cloth] = 2200,
        [Items.Linen] = 1100,
        [Items.Rope] = 650,
        [Items.SugarBeet] = 180,
        [Items.Flax] = 200,
        [Items.Hemp] = 160,
        [Items.Sugar] = 400,
        [Items.BeetPulp] = 80,
        [Items.Donuts] = 1100,
        [Items.Hydrogen] = 150,
        [Items.Pig] = 6000,
        [Items.Goat] = 3500,
        [Items.CokeOven] = 85_000,
        [Items.Jeweler] = 36_000,
        [Items.TreeNursery] = 11_500,
        [Items.SpinningMill] = 29_500,
        [Items.Loom] = 16_000,
        [Items.AutoBarn] = 47_000,
        [Items.SugarFactory] = 65_000,
        [Items.Sprinkler] = 12_300,
        [Items.MechanizedFarm] = 353_000,
        [Items.BiomassPlant] = 157_000,
        [Items.ChpPlant] = 350_000,
        [Items.GeothermalPlant] = 343_000,
        [Items.Electrolyzer] = 180_000,
        [Items.FuelCell] = 127_000,
    };

    static NpcPrices()
    {
        var sells = new Dictionary<string, long>(OwnSells);
        foreach (var (item, pays) in Buys)
        {
            sells.TryAdd(item, pays * Markup);
        }

        Sells = sells;
    }

    public const int MaxQuantity = 1000;
}

/// <summary>The trader's usual prices, for other modules (e.g. what towns pay for the goods they want).</summary>
public sealed class MarketPrices : IMarketPrices
{
    public long? BuysCents(string item) => NpcPrices.Buys.TryGetValue(item, out var cents) ? cents : null;

    public long? SellsCents(string item) => NpcPrices.Sells.TryGetValue(item, out var cents) ? cents : null;

    public long? CheapestSellsCents(string item) => NpcPrices.Sells.TryGetValue(item, out var cents)
        ? MarketEvents.Events.Select(e => e.Charges.GetValueOrDefault(item)).Append(0).Min(percent => cents * (100 + percent) / 100)
        : null;
}

public sealed record TradeRequest(string Item, int Quantity);

/// <param name="Balance">The player's money afterwards, in cents.</param>
/// <param name="ToPallet">How much of what was bought did not fit in the inventory and waits on the pallet in the garage.</param>
public sealed record Traded(long Balance, long ToPallet = 0);

public sealed class MarketService(
    GameDb db, IInventory inventory, IWallet wallet, TraderMarket trader, ITechnologies technologies, IEnumerable<IForestCertificate> certificates,
    IOutfit outfit, IEnumerable<IGarageDeliveries> deliveries, GameCounters counters)
{
    /// <summary>The player buys from the NPC: money out, items in, both or neither.</summary>
    public Task<Traded> BuyAsync(Guid owner, string item, int quantity, CancellationToken cancellationToken)
    {
        var price = Price(trader.Prices().Sells, item, quantity, "The trader doesn't sell this item.");
        return db.InTransactionAsync(async () =>
        {
            // Machines and logistics come from the trader only once researched.
            await technologies.RequireUnlockedAsync(owner, item, cancellationToken);
            var balance = await wallet.DebitAsync(owner, price * quantity, $"buy {quantity} {item}", cancellationToken);
            db.AfterCommit(() => counters.Trade(GameCounters.TraderBuy, item, quantity, price * quantity));
            // A backpack always fits: it is the way out of a full inventory.
            if (Items.IsBackpack(item))
            {
                await inventory.AddAsync(owner, item, quantity, cancellationToken);
                return new Traded(balance);
            }

            return new Traded(balance, await Delivery.OverflowAsync(inventory, deliveries, owner, item, quantity, cancellationToken));
        }, cancellationToken);
    }

    /// <summary>The player sells to the NPC: items out, money in, both or neither.</summary>
    public Task<Traded> SellAsync(Guid owner, string item, int quantity, CancellationToken cancellationToken)
    {
        var prices = trader.Prices();
        var price = Price(prices.Buys, item, quantity, "The trader doesn't buy this item.");
        return db.InTransactionAsync(async () =>
        {
            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            // Timber from a forest kept for good pays more.
            if (Items.IsTimber(item) && await CertifiedAsync(owner, cancellationToken))
            {
                price = price * (100 + ForestCertificate.BonusPercent) / 100;
            }

            // A suit makes the trader pay a few percent more.
            price = price * (100 + await outfit.BonusAsync(owner, Industries.Trade, cancellationToken)) / 100;
            // With every bonus the trader still pays less than he charges, or buying from him to sell back would pay.
            if (prices.Sells.TryGetValue(item, out var charge))
            {
                price = Math.Min(price, charge * MarketEvents.MaxPayOfCharge / 100);
            }

            db.AfterCommit(() => counters.Trade(GameCounters.TraderSell, item, quantity, price * quantity));
            return new Traded(await wallet.CreditAsync(owner, price * quantity, $"sell {quantity} {item}", cancellationToken));
        }, cancellationToken);
    }

    private async Task<bool> CertifiedAsync(Guid owner, CancellationToken cancellationToken)
    {
        foreach (var certificate in certificates)
        {
            if (await certificate.CertifiedAsync(owner, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static long Price(IReadOnlyDictionary<string, long> prices, string item, int quantity, string notTraded)
    {
        if (quantity is < 1 or > NpcPrices.MaxQuantity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{NpcPrices.MaxQuantity}.");
        }

        return prices.TryGetValue(item ?? "", out var price) ? price : throw new GameException("not_traded", notTraded);
    }
}

/// <summary>The trader's prices now, how far they are from the usual ones, and its current and next event.</summary>
public sealed class PricesHandler(TraderMarket trader, IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "market.prices";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var prices = trader.Prices();
        await using var scope = scopes.CreateAsyncScope();
        var locked = await scope.ServiceProvider.GetRequiredService<ITechnologies>().LockedAsync(client.RequireAccount(), cancellationToken);
        // What this player hasn't researched yet the trader doesn't offer at all; it still buys it back.
        bool Sold(string id) => prices.Sells.ContainsKey(id) && !locked.ContainsKey(id);
        return new Reply("market.prices", new
        {
            items = Items.Names.Keys
                .Where(id => Sold(id) || prices.Buys.ContainsKey(id))
                .Select(id => new
                {
                    id,
                    name = Items.Names[id],
                    buyCents = Sold(id) ? prices.Sells[id] : (long?)null,
                    buyChange = Sold(id) ? TraderPrices.Change(prices.Sells[id], NpcPrices.Sells[id]) : (int?)null,
                    sellCents = prices.Buys.TryGetValue(id, out var sell) ? sell : (long?)null,
                    sellChange = prices.Buys.ContainsKey(id) ? TraderPrices.Change(sell, NpcPrices.Buys[id]) : (int?)null,
                }),
            @event = Describe(prices.Event),
            next = Describe(trader.NextEvent()),
            serverTime = trader.Now.ToUnixTimeMilliseconds(),
        });
    }

    private static object? Describe(ScheduledEvent? scheduled) => scheduled is not { Event: var e } ? null : new
    {
        id = e.Id,
        name = e.Name,
        description = e.Description,
        startsAtMs = scheduled.StartsAt.ToUnixTimeMilliseconds(),
        endsAtMs = scheduled.EndsAt.ToUnixTimeMilliseconds(),
        effects = e.Pays.Keys.Union(e.Charges.Keys)
            .Where(Items.Names.ContainsKey)
            .Select(item => new
            {
                item,
                name = Items.Names[item],
                sellChange = e.Pays.TryGetValue(item, out var pays) ? pays : (int?)null,
                buyChange = e.Charges.TryGetValue(item, out var charges) ? charges : (int?)null,
            }),
    };
}

public abstract class TradeHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TradeRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        // A player the police banned from trading may not deal with the trader either.
        foreach (var sanctions in scope.ServiceProvider.GetServices<ISanctions>())
        {
            await sanctions.RequireAllowedAsync(client.RequireAccount(), SanctionKinds.Trade, cancellationToken);
        }

        var market = scope.ServiceProvider.GetRequiredService<MarketService>();
        var traded = await TradeAsync(market, client.RequireAccount(), request, cancellationToken);
        return new Reply("market.traded", new
        {
            item = request.Item,
            quantity = request.Quantity,
            balanceCents = traded.Balance,
            toPallet = traded.ToPallet > 0,
            palletQuantity = traded.ToPallet,
        });
    }

    protected abstract Task<Traded> TradeAsync(MarketService market, Guid owner, TradeRequest request, CancellationToken cancellationToken);
}

public sealed class BuyHandler(IServiceScopeFactory scopes) : TradeHandler(scopes)
{
    public override string Type => "market.buy";

    protected override Task<Traded> TradeAsync(MarketService market, Guid owner, TradeRequest request, CancellationToken cancellationToken) =>
        market.BuyAsync(owner, request.Item, request.Quantity, cancellationToken);
}

public sealed class SellHandler(IServiceScopeFactory scopes) : TradeHandler(scopes)
{
    public override string Type => "market.sell";

    protected override Task<Traded> TradeAsync(MarketService market, Guid owner, TradeRequest request, CancellationToken cancellationToken) =>
        market.SellAsync(owner, request.Item, request.Quantity, cancellationToken);
}

/// <summary>Buying from and selling to the NPC trader, and trading between players. Owns the <c>market</c> schema.</summary>
public sealed class MarketModule : IGameModule
{
    public string Name => "Market";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<MarketDbContext>(MarketDbContext.Schema);
        services.AddSingleton<TraderMarket>();
        services.AddSingleton<IMarketPrices, MarketPrices>();
        services.AddScoped<MarketService>();
        services.AddScoped<PlayerMarketService>();
        services.AddSingleton<MarketSummary>();
        services.AddScoped<IGameGauges, MarketGauges>();
        services.AddScoped<IExportMarket>(provider => provider.GetRequiredService<PlayerMarketService>());
        services.AddSingleton<IMessageHandler, PricesHandler>();
        services.AddSingleton<IMessageHandler, BuyHandler>();
        services.AddSingleton<IMessageHandler, SellHandler>();
        services.AddSingleton<IMessageHandler, OverviewHandler>();
        services.AddSingleton<IMessageHandler, ItemBoardHandler>();
        services.AddSingleton<IMessageHandler, MineHandler>();
        services.AddSingleton<IMessageHandler, ListHandler>();
        services.AddSingleton<IMessageHandler, UnlistHandler>();
        services.AddSingleton<IMessageHandler, PurchaseHandler>();
        services.AddSingleton<IMessageHandler, OrderHandler>();
        services.AddSingleton<IMessageHandler, CancelOrderHandler>();
        services.AddSingleton<IMessageHandler, FillHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<MarketDbContext>(cancellationToken);
}

/// <summary>Where bought goods go: into the inventory as far as its tiles hold them, the rest onto the pallet in the garage.</summary>
public static class Delivery
{
    /// <summary>Gives the player what they bought; returns how much of it went onto the pallet. Fails with
    /// <c>inventory_full</c> when not all of it fits in the inventory and the garage has no pallet.</summary>
    public static async Task<long> OverflowAsync(
        IInventory inventory, IEnumerable<IGarageDeliveries> deliveries, Guid player, string item, long quantity, CancellationToken cancellationToken)
    {
        var fits = await inventory.RoomForAsync(player, item, quantity, cancellationToken);
        var rest = quantity - fits;
        if (rest > 0 && !await PalletAsync(deliveries, player, item, rest, cancellationToken))
        {
            // The client says how many would fit, so the player can buy that many.
            throw InventoryExtensions.InventoryFull(fits);
        }

        if (fits > 0)
        {
            await inventory.AddAsync(player, item, fits, cancellationToken);
        }

        return rest;
    }

    private static async Task<bool> PalletAsync(
        IEnumerable<IGarageDeliveries> deliveries, Guid player, string item, long quantity, CancellationToken cancellationToken)
    {
        foreach (var pallet in deliveries)
        {
            if (await pallet.DeliverAsync(player, item, quantity, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
