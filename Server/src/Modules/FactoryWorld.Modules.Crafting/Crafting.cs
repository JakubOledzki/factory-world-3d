using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Crafting;

/// <summary>Something made at a workbench or an anvil: what it takes, how long, and what comes out.</summary>
/// <param name="Id">The recipe's id, the same as the item it makes.</param>
/// <param name="Station">Where it is made: <see cref="Items.Workbench"/>, <see cref="Items.Anvil"/>, <see cref="Items.Shipyard"/> or <see cref="Items.Tailor"/>.</param>
public sealed record Recipe(
    string Id, int Quantity, TimeSpan Time, IReadOnlyList<(string Item, int Quantity)> Ingredients, int Xp, string Station = Items.Workbench);

/// <summary>
/// At a workbench (the one in the garage or one put on a plot) or an anvil, which forges the tools,
/// the player picks a recipe and orders it. Both share one queue. The ingredients are taken at once; the item is made after the recipe's time, one
/// order after another, and lands in the inventory even when the player has walked away.
/// </summary>
public static class Workshop
{
    /// <summary>Orders that can wait in the queue, the one being made included.</summary>
    public const int MaxQueue = 5;

    public static readonly IReadOnlyList<Recipe> Recipes =
    [
        new(Items.Workbench, 1, TimeSpan.FromSeconds(30), [(Items.Wood, 20), (Items.Nails, 5)], 10),
        new(Items.Furnace, 1, TimeSpan.FromSeconds(45), [(Items.Stone, 20)], 15),
        new(Items.BrickFurnace, 1, TimeSpan.FromSeconds(60), [(Items.Furnace, 1), (Items.Stone, 40), (Items.Sand, 20), (Items.IronIngot, 10)], 25),
        new(Items.BlastFurnace, 1, TimeSpan.FromSeconds(90), [(Items.BrickFurnace, 1), (Items.Stone, 60), (Items.IronIngot, 40), (Items.CopperIngot, 20), (Items.Provisions, 5)], 50),
        new(Items.Nails, 5, TimeSpan.FromSeconds(10), [(Items.IronIngot, 1)], 2),
        // Assembling machines, each built on the one below it.
        new(Items.Assembler, 1, TimeSpan.FromSeconds(60), [(Items.Workbench, 1), (Items.IronIngot, 10), (Items.CopperIngot, 5), (Items.Nails, 10)], 20),
        new(Items.FastAssembler, 1, TimeSpan.FromSeconds(90), [(Items.Assembler, 1), (Items.SteelIngot, 10), (Items.CopperIngot, 15), (Items.FastInserter, 2)], 40),
        new(Items.ExpressAssembler, 1, TimeSpan.FromSeconds(120),
            [(Items.FastAssembler, 1), (Items.SteelIngot, 20), (Items.ElectricMotor, 4), (Items.CircuitBoard, 4), (Items.ExpressInserter, 2)], 80),
        new(Items.Belt, 4, TimeSpan.FromSeconds(10), [(Items.IronIngot, 2), (Items.Wood, 1)], 3),
        new(Items.Inserter, 1, TimeSpan.FromSeconds(15), [(Items.IronIngot, 2), (Items.CopperIngot, 1), (Items.Nails, 5)], 5),
        new(Items.FastBelt, 2, TimeSpan.FromSeconds(15), [(Items.Belt, 2), (Items.IronIngot, 3), (Items.CopperIngot, 1)], 5),
        new(Items.ExpressBelt, 2, TimeSpan.FromSeconds(20), [(Items.FastBelt, 2), (Items.IronIngot, 4), (Items.TinIngot, 1)], 8),
        new(Items.FastInserter, 1, TimeSpan.FromSeconds(20), [(Items.Inserter, 1), (Items.IronIngot, 2), (Items.CopperIngot, 2)], 8),
        new(Items.ExpressInserter, 1, TimeSpan.FromSeconds(25), [(Items.FastInserter, 1), (Items.CopperIngot, 3), (Items.SilverIngot, 1)], 12),
        new(Items.Splitter, 1, TimeSpan.FromSeconds(20), [(Items.Belt, 2), (Items.IronIngot, 4), (Items.CopperIngot, 2)], 6),
        new(Items.UndergroundBelt, 2, TimeSpan.FromSeconds(20), [(Items.Belt, 4), (Items.IronIngot, 8), (Items.Stone, 10)], 8),
        new(Items.LongInserter, 1, TimeSpan.FromSeconds(20), [(Items.Inserter, 1), (Items.IronIngot, 3), (Items.CopperIngot, 2)], 8),
        new(Items.LogisticsDepot, 1, TimeSpan.FromSeconds(40), [(Items.SteelIngot, 20), (Items.IronIngot, 20), (Items.Beams, 10)], 15),
        new(Items.DronePort, 1, TimeSpan.FromSeconds(60), [(Items.SteelIngot, 20), (Items.CircuitBoard, 10), (Items.CopperIngot, 20), (Items.ElectricMotor, 4)], 25),
        new(Items.WoodenChest, 1, TimeSpan.FromSeconds(10), [(Items.Wood, 10), (Items.Nails, 5)], 3),
        new(Items.IronChest, 1, TimeSpan.FromSeconds(20), [(Items.IronIngot, 8)], 6),
        new(Items.WoodPile, 1, TimeSpan.FromSeconds(5), [(Items.Wood, 4)], 1),
        new(Items.Heap, 1, TimeSpan.FromSeconds(10), [(Items.Stone, 10), (Items.Wood, 4)], 2),
        new(Items.GrainSilo, 1, TimeSpan.FromSeconds(40), [(Items.IronIngot, 10), (Items.Stone, 20), (Items.Nails, 10)], 10),
        new(Items.Mill, 1, TimeSpan.FromSeconds(60), [(Items.Stone, 30), (Items.Wood, 10), (Items.IronIngot, 2)], 20),
        new(Items.RollerMill, 1, TimeSpan.FromSeconds(90), [(Items.Mill, 1), (Items.Beams, 10), (Items.IronIngot, 20), (Items.CopperIngot, 10)], 40),
        new(Items.FlourWorks, 1, TimeSpan.FromSeconds(120), [(Items.RollerMill, 1), (Items.SteelIngot, 20), (Items.Bricks, 40), (Items.CopperIngot, 20)], 80),
        new(Items.ForesterHut, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 40), (Items.Stone, 20), (Items.Nails, 20), (Items.Sapling, 10)], 20),
        new(Items.ForesterLodge, 1, TimeSpan.FromSeconds(90), [(Items.ForesterHut, 1), (Items.Planks, 40), (Items.Beams, 10), (Items.IronIngot, 10)], 40),
        new(Items.ForestryStation, 1, TimeSpan.FromSeconds(120),
            [(Items.ForesterLodge, 1), (Items.Beams, 30), (Items.Plywood, 20), (Items.IronIngot, 30), (Items.CopperIngot, 10), (Items.Provisions, 10)], 80),
        new(Items.Sawmill, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 30), (Items.IronIngot, 6), (Items.Nails, 10)], 20),
        new(Items.ElectricSawmill, 1, TimeSpan.FromSeconds(90), [(Items.Sawmill, 1), (Items.Beams, 10), (Items.IronIngot, 20), (Items.CopperIngot, 10)], 40),
        new(Items.IndustrialSawmill, 1, TimeSpan.FromSeconds(120),
            [(Items.ElectricSawmill, 1), (Items.Plywood, 10), (Items.IronIngot, 40), (Items.CopperIngot, 20), (Items.TinIngot, 5), (Items.Provisions, 10)], 80),
        new(Items.OilPress, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 15), (Items.IronIngot, 4), (Items.Nails, 10)], 20),
        new(Items.PicklingBarrel, 1, TimeSpan.FromSeconds(30), [(Items.Wood, 12), (Items.Nails, 5)], 10),
        new(Items.Bakery, 1, TimeSpan.FromSeconds(90), [(Items.Stone, 40), (Items.IronIngot, 3), (Items.Sand, 10)], 30),
        new(Items.Kitchen, 1, TimeSpan.FromSeconds(90), [(Items.Stone, 20), (Items.IronIngot, 5), (Items.CopperIngot, 2)], 30),
        new(Items.Generator, 1, TimeSpan.FromSeconds(60), [(Items.Stone, 10), (Items.IronIngot, 8), (Items.CopperIngot, 4)], 20),
        new(Items.Pole, 2, TimeSpan.FromSeconds(5), [(Items.Wood, 2), (Items.CopperIngot, 1)], 1),
        new(Items.Anvil, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 15), (Items.Stone, 10)], 20),
        new(Items.ClothesWardrobe, 1, TimeSpan.FromSeconds(40), [(Items.Wood, 20), (Items.Nails, 10)], 10),
        new(Items.TrophyCase, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 25), (Items.Nails, 12), (Items.Glass, 6)], 15),
        new(Items.Axe, 1, TimeSpan.FromSeconds(20), [(Items.IronIngot, 3), (Items.Wood, 2)], 5, Items.Anvil),
        new(Items.Shovel, 1, TimeSpan.FromSeconds(20), [(Items.IronIngot, 2), (Items.Wood, 2)], 5, Items.Anvil),
        new(Items.Pickaxe, 1, TimeSpan.FromSeconds(20), [(Items.IronIngot, 3), (Items.Wood, 2)], 5, Items.Anvil),
        new(Items.Hoe, 1, TimeSpan.FromSeconds(20), [(Items.IronIngot, 2), (Items.Wood, 2)], 5, Items.Anvil),
        new(Items.Shears, 1, TimeSpan.FromSeconds(20), [(Items.IronIngot, 2), (Items.Wood, 1)], 5, Items.Anvil),
        new(Items.BronzePickaxe, 1, TimeSpan.FromSeconds(25), [(Items.BronzeIngot, 3), (Items.Wood, 2)], 8, Items.Anvil),
        new(Items.SteelPickaxe, 1, TimeSpan.FromSeconds(30), [(Items.SteelIngot, 3), (Items.Wood, 2)], 12, Items.Anvil),
        // The mountain industry.
        new(Items.Drill, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 30), (Items.CopperIngot, 15), (Items.Inserter, 2), (Items.Stone, 20)], 30),
        new(Items.HeavyDrill, 1, TimeSpan.FromSeconds(120), [(Items.Drill, 1), (Items.SteelIngot, 20), (Items.BronzeIngot, 10), (Items.Quartz, 5)], 80),
        new(Items.Quarry, 1, TimeSpan.FromSeconds(60), [(Items.Stone, 40), (Items.Wood, 20), (Items.IronIngot, 10), (Items.Nails, 10)], 20),
        new(Items.Crusher, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 25), (Items.Stone, 30), (Items.CopperIngot, 8)], 25),
        // The higher levels of the quarry and the crusher, each built on the one below it.
        new(Items.MechanizedQuarry, 1, TimeSpan.FromSeconds(90), [(Items.Quarry, 1), (Items.SteelIngot, 10), (Items.IronIngot, 30), (Items.CopperIngot, 10)], 40),
        new(Items.OpenPitMine, 1, TimeSpan.FromSeconds(120),
            [(Items.MechanizedQuarry, 1), (Items.SteelIngot, 30), (Items.Dynamite, 10), (Items.Beams, 20), (Items.CopperIngot, 20)], 80),
        new(Items.ConeCrusher, 1, TimeSpan.FromSeconds(90), [(Items.Crusher, 1), (Items.SteelIngot, 15), (Items.IronIngot, 20), (Items.CopperIngot, 10)], 40),
        new(Items.OreProcessingPlant, 1, TimeSpan.FromSeconds(120),
            [(Items.ConeCrusher, 1), (Items.SteelIngot, 30), (Items.BronzeIngot, 20), (Items.CopperIngot, 20), (Items.Provisions, 5)], 80),
        new(Items.Stonemason, 1, TimeSpan.FromSeconds(45), [(Items.Stone, 30), (Items.IronIngot, 10), (Items.Wood, 10)], 15),
        new(Items.Foundry, 1, TimeSpan.FromSeconds(90), [(Items.Stone, 40), (Items.Sand, 20), (Items.IronIngot, 20), (Items.CopperIngot, 10)], 35),
        new(Items.WaterWheel, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 30), (Items.IronIngot, 20), (Items.Nails, 10)], 25),
        new(Items.HydroPlant, 1, TimeSpan.FromSeconds(180), [(Items.SteelIngot, 30), (Items.Bricks, 60), (Items.CopperIngot, 30), (Items.Quartz, 5)], 100),
        new(Items.CableStation, 1, TimeSpan.FromSeconds(20), [(Items.SteelIngot, 4), (Items.CopperIngot, 4), (Items.IronIngot, 6)], 8),
        new(Items.ExpressCableStation, 1, TimeSpan.FromSeconds(30), [(Items.CableStation, 1), (Items.SteelIngot, 6), (Items.Bearing, 4)], 12),
        new(Items.Dynamite, 3, TimeSpan.FromSeconds(15), [(Items.Sulfur, 2), (Items.Coal, 1), (Items.Sand, 1)], 4),
        new(Items.MineSupport, 1, TimeSpan.FromSeconds(20), [(Items.SteelIngot, 2), (Items.Beams, 2)], 6),
        // The coast: the rod is forged, boats and fish farms are built at the shipyard on the shore.
        new(Items.FishingRod, 1, TimeSpan.FromSeconds(20), [(Items.Wood, 3), (Items.IronIngot, 1)], 5, Items.Anvil),
        new(Items.Shipyard, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 40), (Items.Planks, 20), (Items.IronIngot, 15), (Items.Nails, 20)], 25),
        new(Items.FishingBoat, 1, TimeSpan.FromSeconds(90), [(Items.Planks, 30), (Items.Beams, 10), (Items.Nails, 20), (Items.IronIngot, 10)], 30, Items.Shipyard),
        new(Items.RowingBoat, 1, TimeSpan.FromSeconds(45), [(Items.Planks, 15), (Items.Beams, 4), (Items.Nails, 10)], 15, Items.Shipyard),
        new(Items.FishFarm, 1, TimeSpan.FromSeconds(60), [(Items.Beams, 10), (Items.Planks, 20), (Items.IronIngot, 10), (Items.Wool, 5)], 25, Items.Shipyard),
        new(Items.Smokehouse, 1, TimeSpan.FromSeconds(45), [(Items.Stone, 30), (Items.Wood, 10), (Items.IronIngot, 4)], 15),
        new(Items.Cannery, 1, TimeSpan.FromSeconds(90), [(Items.IronIngot, 30), (Items.TinIngot, 5), (Items.CopperIngot, 10), (Items.Stone, 20)], 35),
        new(Items.SaltPan, 1, TimeSpan.FromSeconds(45), [(Items.Stone, 30), (Items.Sand, 20), (Items.Wood, 10)], 15),
        new(Items.Port, 1, TimeSpan.FromSeconds(90), [(Items.Beams, 20), (Items.Planks, 40), (Items.Stone, 40), (Items.IronIngot, 10)], 40),
        new(Items.Breakwater, 1, TimeSpan.FromSeconds(45), [(Items.Stone, 100)], 15),
        new(Items.Lighthouse, 1, TimeSpan.FromSeconds(90), [(Items.Stone, 60), (Items.Sand, 20), (Items.IronIngot, 20), (Items.CopperIngot, 10)], 40),
        new(Items.TidalPlant, 1, TimeSpan.FromSeconds(180), [(Items.SteelIngot, 30), (Items.CopperIngot, 40), (Items.Bricks, 40), (Items.StoneBlocks, 20)], 100),
        // Oil and gas: wells, pipes and tanks, the refineries and chemistry, the engines that burn their fuels and asphalt roads.
        new(Items.Gasket, 2, TimeSpan.FromSeconds(10), [(Items.CopperIngot, 1), (Items.IronIngot, 1)], 2, Items.Anvil),
        new(Items.Pumpjack, 1, TimeSpan.FromSeconds(90), [(Items.SteelIngot, 100), (Items.IronIngot, 200), (Items.CopperIngot, 50)], 40),
        new(Items.GasWell, 1, TimeSpan.FromSeconds(60), [(Items.SteelIngot, 80), (Items.IronIngot, 100), (Items.CopperIngot, 40)], 30),
        new(Items.OffshorePlatform, 1, TimeSpan.FromSeconds(300),
            [(Items.SteelIngot, 800), (Items.Beams, 400), (Items.CopperIngot, 300), (Items.StoneBlocks, 400)], 200),
        new(Items.WaterInjector, 1, TimeSpan.FromSeconds(90), [(Items.SteelIngot, 100), (Items.IronIngot, 100), (Items.CopperIngot, 60)], 35),
        new(Items.OilSeparator, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 150), (Items.SteelIngot, 50), (Items.Sand, 200)], 25),
        new(Items.Pipe, 4, TimeSpan.FromSeconds(10), [(Items.IronIngot, 3)], 3),
        new(Items.Pump, 1, TimeSpan.FromSeconds(15), [(Items.IronIngot, 4), (Items.CopperIngot, 2)], 5),
        new(Items.Tank, 1, TimeSpan.FromSeconds(40), [(Items.SteelIngot, 6), (Items.IronIngot, 10)], 12),
        new(Items.Distillery, 1, TimeSpan.FromSeconds(90), [(Items.Bricks, 30), (Items.IronIngot, 15), (Items.CopperIngot, 10)], 40),
        new(Items.Refinery, 1, TimeSpan.FromSeconds(180), [(Items.Distillery, 1), (Items.SteelIngot, 30), (Items.Bricks, 40), (Items.CopperIngot, 20), (Items.Provisions, 20)], 100),
        new(Items.Cracker, 1, TimeSpan.FromSeconds(180), [(Items.SteelIngot, 40), (Items.CopperIngot, 30), (Items.BronzeIngot, 10)], 100),
        new(Items.ChemicalPlant, 1, TimeSpan.FromSeconds(150), [(Items.SteelIngot, 30), (Items.CopperIngot, 20), (Items.TinIngot, 10)], 80),
        new(Items.GasPlant, 1, TimeSpan.FromSeconds(150), [(Items.SteelIngot, 25), (Items.CopperIngot, 20), (Items.Bricks, 20)], 70),
        new(Items.DieselGenerator, 1, TimeSpan.FromSeconds(90), [(Items.SteelIngot, 15), (Items.IronIngot, 10), (Items.CopperIngot, 10)], 40),
        new(Items.Road, 4, TimeSpan.FromSeconds(10), [(Items.Asphalt, 2), (Items.Stone, 2)], 2),
        new(Items.TruckDepot, 1, TimeSpan.FromSeconds(120), [(Items.SteelIngot, 20), (Items.Tyres, 2), (Items.Concrete, 10), (Items.ElectricMotor, 1)], 50),
        new(Items.Scrubber, 1, TimeSpan.FromSeconds(60), [(Items.SteelIngot, 10), (Items.Charcoal, 20), (Items.CopperIngot, 5)], 20),
        // The desert: glass and silicon, water, building materials and the sun's power.
        new(Items.Glassworks, 1, TimeSpan.FromSeconds(60), [(Items.Bricks, 30), (Items.Stone, 20), (Items.IronIngot, 10)], 25),
        new(Items.ArcFurnace, 1, TimeSpan.FromSeconds(150), [(Items.SteelIngot, 20), (Items.CopperIngot, 30), (Items.Bricks, 20)], 80),
        new(Items.WaterWell, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 15), (Items.Stone, 20), (Items.CopperIngot, 5)], 25),
        new(Items.Desalinator, 1, TimeSpan.FromSeconds(90), [(Items.SteelIngot, 15), (Items.CopperIngot, 15), (Items.Glass, 10)], 40),
        new(Items.DryingYard, 1, TimeSpan.FromSeconds(30), [(Items.Wood, 20), (Items.Planks, 10), (Items.Stone, 10)], 10),
        new(Items.CementKiln, 1, TimeSpan.FromSeconds(60), [(Items.Stone, 40), (Items.Bricks, 20), (Items.IronIngot, 10)], 30),
        new(Items.ConcreteMixer, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 20), (Items.SteelIngot, 5), (Items.CopperIngot, 5)], 30),
        new(Items.ReedFence, 4, TimeSpan.FromSeconds(10), [(Items.Planks, 2), (Items.Wood, 2)], 2),
        new(Items.SolarPanel, 1, TimeSpan.FromSeconds(30), [(Items.Glass, 4), (Items.SiliconWafer, 2), (Items.CopperIngot, 2)], 15),
        new(Items.SolarTower, 1, TimeSpan.FromSeconds(300),
            [(Items.Mirror, 40), (Items.SteelIngot, 40), (Items.StoneBlocks, 30), (Items.CopperIngot, 20)], 200),
        new(Items.Battery, 1, TimeSpan.FromSeconds(45), [(Items.IronIngot, 8), (Items.CopperIngot, 8), (Items.Sulfur, 4)], 20),
        // The glacier: wind power, heaters, the cold store and the server room, polar research, tourists, sleds and the best pickaxe.
        new(Items.WindTurbine, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 20), (Items.CopperIngot, 15), (Items.Planks, 10)], 25),
        new(Items.LargeWindTurbine, 1, TimeSpan.FromSeconds(300),
            [(Items.SteelIngot, 60), (Items.CopperIngot, 40), (Items.Concrete, 30), (Items.Iridium, 4)], 200),
        new(Items.Heater, 1, TimeSpan.FromSeconds(45), [(Items.IronIngot, 12), (Items.Bricks, 10), (Items.CopperIngot, 4)], 20),
        new(Items.IceMelter, 1, TimeSpan.FromSeconds(30), [(Items.IronIngot, 10), (Items.Stone, 10)], 10),
        new(Items.ColdStore, 1, TimeSpan.FromSeconds(60), [(Items.IronIngot, 20), (Items.Planks, 20), (Items.CopperIngot, 8)], 25),
        new(Items.ServerRoom, 1, TimeSpan.FromSeconds(240),
            [(Items.SiliconWafer, 20), (Items.CopperIngot, 40), (Items.OpticalFiber, 10), (Items.SteelIngot, 20), (Items.Iridium, 2)], 150),
        new(Items.IceCoreDrill, 1, TimeSpan.FromSeconds(90), [(Items.SteelIngot, 15), (Items.IronIngot, 20), (Items.CopperIngot, 10)], 40),
        new(Items.PolarStation, 1, TimeSpan.FromSeconds(120), [(Items.Planks, 40), (Items.IronIngot, 20), (Items.Glass, 10), (Items.CopperIngot, 10)], 50),
        new(Items.IceHotel, 1, TimeSpan.FromSeconds(180), [(Items.Ice, 80), (Items.Planks, 30), (Items.Wool, 20), (Items.IronIngot, 10)], 80),
        new(Items.Viewpoint, 1, TimeSpan.FromSeconds(45), [(Items.Planks, 20), (Items.Beams, 6), (Items.Nails, 10)], 15),
        new(Items.DogKennel, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 30), (Items.Wool, 10), (Items.Nails, 10)], 25),
        new(Items.SnowmobileGarage, 1, TimeSpan.FromSeconds(120), [(Items.SteelIngot, 20), (Items.SyntheticRubber, 10), (Items.IronIngot, 20)], 60),
        new(Items.TrackFoundation, 1, TimeSpan.FromSeconds(60), [(Items.Concrete, 20), (Items.Beams, 10), (Items.SteelIngot, 10)], 30),
        new(Items.AssemblyHall, 1, TimeSpan.FromSeconds(180), [(Items.SteelIngot, 40), (Items.Concrete, 30), (Items.CopperIngot, 20), (Items.Glass, 10), (Items.Provisions, 20)], 80),
        new(Items.MultistoreyFactory, 1, TimeSpan.FromSeconds(300),
            [(Items.AssemblyHall, 1), (Items.Concrete, 60), (Items.SteelIngot, 60), (Items.Blueprint, 2), (Items.Provisions, 60)], 150),
        new(Items.Shop, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 20), (Items.Glass, 10), (Items.Bricks, 20)], 25),
        new(Items.HighBay, 1, TimeSpan.FromSeconds(90), [(Items.SteelIngot, 40), (Items.IronChest, 1), (Items.Concrete, 10), (Items.Provisions, 30)], 40),
        new(Items.CitySorter, 1, TimeSpan.FromSeconds(90), [(Items.IronIngot, 30), (Items.SteelIngot, 10), (Items.CopperIngot, 10)], 40),
        new(Items.FreightTerminal, 1, TimeSpan.FromSeconds(180), [(Items.Concrete, 40), (Items.SteelIngot, 30), (Items.Bricks, 40), (Items.Blueprint, 1)], 80),
        new(Items.ExportChest, 1, TimeSpan.FromSeconds(30), [(Items.IronChest, 1), (Items.Planks, 20), (Items.IronIngot, 10)], 15),
        new(Items.LoadingChest, 1, TimeSpan.FromSeconds(15), [(Items.WoodenChest, 1), (Items.IronIngot, 10), (Items.Nails, 5)], 6),
        new(Items.LoadingChest2, 1, TimeSpan.FromSeconds(30), [(Items.LoadingChest, 1), (Items.IronChest, 1), (Items.IronIngot, 10)], 12),
        new(Items.LoadingChest3, 1, TimeSpan.FromSeconds(60), [(Items.LoadingChest2, 1), (Items.SteelIngot, 20), (Items.Beams, 10)], 25),
        new(Items.LoadingChest4, 1, TimeSpan.FromSeconds(120), [(Items.LoadingChest3, 1), (Items.SteelIngot, 40), (Items.ElectricMotor, 4)], 50),
        new(Items.ReceivingChest, 1, TimeSpan.FromSeconds(15), [(Items.WoodenChest, 1), (Items.IronIngot, 10), (Items.Nails, 5)], 6),
        new(Items.ReceivingChest2, 1, TimeSpan.FromSeconds(30), [(Items.ReceivingChest, 1), (Items.IronChest, 1), (Items.IronIngot, 10)], 12),
        new(Items.ReceivingChest3, 1, TimeSpan.FromSeconds(60), [(Items.ReceivingChest2, 1), (Items.SteelIngot, 20), (Items.Beams, 10)], 25),
        new(Items.ReceivingChest4, 1, TimeSpan.FromSeconds(120), [(Items.ReceivingChest3, 1), (Items.SteelIngot, 40), (Items.ElectricMotor, 4)], 50),
        new(Items.BuyMarket, 1, TimeSpan.FromSeconds(240),
            [(Items.Concrete, 40), (Items.Bricks, 60), (Items.Planks, 40), (Items.Glass, 20), (Items.SteelIngot, 20), (Items.Blueprint, 2)], 120),
        new(Items.RailPort, 1, TimeSpan.FromSeconds(240), [(Items.Port, 1), (Items.SteelIngot, 40), (Items.Concrete, 40), (Items.Beams, 20)], 120),
        new(Items.CompanyHq, 1, TimeSpan.FromSeconds(240),
            [(Items.Concrete, 50), (Items.Glass, 30), (Items.SteelIngot, 30), (Items.MarbleSlabs, 20), (Items.Blueprint, 2)], 120),
        new(Items.DesignOffice, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 30), (Items.Glass, 10), (Items.Bricks, 20)], 30),
        new(Items.Laboratory, 1, TimeSpan.FromSeconds(120), [(Items.Glass, 20), (Items.GlassBottles, 20), (Items.SteelIngot, 15), (Items.CopperIngot, 10)], 50),
        new(Items.ServiceWorkshop, 1, TimeSpan.FromSeconds(60), [(Items.Bricks, 30), (Items.IronIngot, 20), (Items.SteelIngot, 10)], 30),
        // Forestry.
        new(Items.PaperMill, 1, TimeSpan.FromSeconds(120), [(Items.Beams, 20), (Items.IronIngot, 30), (Items.CopperIngot, 10), (Items.Bricks, 20)], 50),
        new(Items.Carpentry, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 30), (Items.IronIngot, 10), (Items.Nails, 20)], 25),
        new(Items.CharcoalKiln, 1, TimeSpan.FromSeconds(40), [(Items.Stone, 40), (Items.Wood, 10)], 15),
        new(Items.TarKiln, 1, TimeSpan.FromSeconds(60), [(Items.Bricks, 20), (Items.IronIngot, 10), (Items.Stone, 20)], 25),
        new(Items.Beehive, 1, TimeSpan.FromSeconds(20), [(Items.Planks, 10), (Items.Nails, 5)], 8),
        new(Items.Watchtower, 1, TimeSpan.FromSeconds(45), [(Items.Beams, 12), (Items.Planks, 10), (Items.Nails, 10)], 15),
        new(Items.FireStation, 1, TimeSpan.FromSeconds(90), [(Items.Bricks, 40), (Items.IronIngot, 20), (Items.Planks, 20), (Items.CopperIngot, 5)], 40),
        new(Items.Candles, 2, TimeSpan.FromSeconds(10), [(Items.Wax, 1)], 2),
        new(Items.MeteoritePickaxe, 1, TimeSpan.FromSeconds(40), [(Items.MeteoricIron, 4), (Items.Iridium, 1), (Items.Wood, 2)], 20, Items.Anvil),
        new(Items.SteelSaw, 1, TimeSpan.FromSeconds(35), [(Items.SteelIngot, 4), (Items.BronzeIngot, 2), (Items.Wood, 2)], 14, Items.Anvil),
        new(Items.HarvesterSaw, 1, TimeSpan.FromSeconds(45), [(Items.SteelIngot, 6), (Items.CopperIngot, 4), (Items.Plywood, 10)], 20, Items.Anvil),
        new(Items.MeteoriteSaw, 1, TimeSpan.FromSeconds(50), [(Items.MeteoricIron, 4), (Items.Iridium, 1), (Items.SteelIngot, 4)], 25, Items.Anvil),
        // Clothes, sewn at a tailor's in town.
        new(Items.Tailor, 1, TimeSpan.FromSeconds(90), [(Items.Planks, 20), (Items.IronIngot, 10), (Items.Wool, 20), (Items.Glass, 5)], 35),
        new(Items.MinerHelmet, 1, TimeSpan.FromSeconds(60), [(Items.SteelIngot, 3), (Items.Plastic, 2), (Items.Glass, 1), (Items.CopperIngot, 1)], 20, Items.Tailor),
        new(Items.SouWester, 1, TimeSpan.FromSeconds(40), [(Items.Wool, 3), (Items.SyntheticRubber, 2)], 12, Items.Tailor),
        new(Items.StrawHat, 1, TimeSpan.FromSeconds(30), [(Items.Wheat, 20)], 8, Items.Tailor),
        new(Items.Beanie, 1, TimeSpan.FromSeconds(30), [(Items.Wool, 6)], 8, Items.Tailor),
        new(Items.FlannelJacket, 1, TimeSpan.FromSeconds(60), [(Items.Wool, 12), (Items.Wood, 2)], 18, Items.Tailor),
        new(Items.SmithApron, 1, TimeSpan.FromSeconds(60), [(Items.Wool, 6), (Items.SyntheticRubber, 3), (Items.IronIngot, 2)], 20, Items.Tailor),
        new(Items.Oilskin, 1, TimeSpan.FromSeconds(75), [(Items.Wool, 6), (Items.SyntheticRubber, 6)], 25, Items.Tailor),
        new(Items.SuitJacket, 1, TimeSpan.FromSeconds(90), [(Items.Wool, 16), (Items.SilverIngot, 1)], 30, Items.Tailor),
        new(Items.Overalls, 1, TimeSpan.FromSeconds(50), [(Items.Wool, 10), (Items.IronIngot, 2)], 15, Items.Tailor),
        new(Items.WorkTrousers, 1, TimeSpan.FromSeconds(50), [(Items.Wool, 10), (Items.Plastic, 4), (Items.SyntheticRubber, 1)], 18, Items.Tailor),
        new(Items.SuitTrousers, 1, TimeSpan.FromSeconds(60), [(Items.Wool, 12)], 20, Items.Tailor),
        new(Items.RubberBoots, 1, TimeSpan.FromSeconds(40), [(Items.SyntheticRubber, 5)], 15, Items.Tailor),
        new(Items.WorkBoots, 1, TimeSpan.FromSeconds(50), [(Items.Wool, 3), (Items.SyntheticRubber, 3), (Items.SteelIngot, 1)], 18, Items.Tailor),
        new(Items.DressShoes, 1, TimeSpan.FromSeconds(50), [(Items.SyntheticRubber, 2), (Items.Wool, 2), (Items.Wax, 2)], 15, Items.Tailor),
        // The crafting tree's buildings: the machine shop and the robotic hall, the steelworks, the megafactory and the fusion plant.
        new(Items.MachineShop, 1, TimeSpan.FromSeconds(60), [(Items.SteelIngot, 6), (Items.ElectricMotor, 2), (Items.CopperIngot, 10)], 6),
        new(Items.RoboticHall, 1, TimeSpan.FromSeconds(300), [(Items.IndustrialRobot, 3), (Items.ExpressBelt, 10), (Items.ComputingCore, 1), (Items.PrecastConcrete, 20)], 90),
        new(Items.OxygenConverter, 1, TimeSpan.FromSeconds(120), [(Items.RefractoryLining, 8), (Items.HydraulicActuator, 2), (Items.ControlModule, 1), (Items.SteelIngot, 20)], 36),
        new(Items.RollingMill, 1, TimeSpan.FromSeconds(120), [(Items.Gearbox, 4), (Items.Alternator, 2), (Items.ControlModule, 1), (Items.PrecastConcrete, 20)], 36),
        new(Items.ControlCenter, 1, TimeSpan.FromSeconds(120), [(Items.ComputingCore, 4), (Items.ControlModule, 4), (Items.OpticalFiber, 20), (Items.MachineHousing, 1)], 60),
        new(Items.ContinuousCaster, 1, TimeSpan.FromSeconds(300), [(Items.IndustrialRobot, 1), (Items.RefractoryLining, 10), (Items.Coolant, 20), (Items.SteelIngot, 20)], 90),
        new(Items.AutomatedWarehouse, 1, TimeSpan.FromSeconds(300), [(Items.IndustrialRobot, 2), (Items.ComputingCore, 2), (Items.ExpressBelt, 20), (Items.PrecastConcrete, 30)], 90),
        new(Items.SteelCombine, 1, TimeSpan.FromSeconds(300), [(Items.OxygenConverter, 2), (Items.RollingMill, 2), (Items.ContinuousCaster, 1), (Items.ControlCenter, 1)], 150),
        new(Items.Megafactory, 1, TimeSpan.FromSeconds(300), [(Items.RoboticHall, 2), (Items.ControlCenter, 1), (Items.AutomatedWarehouse, 1), (Items.PrecastConcrete, 100)], 150),
        new(Items.FusionPlant, 1, TimeSpan.FromSeconds(300), [(Items.FusionReactor, 1), (Items.PrecastConcrete, 50), (Items.Alternator, 4), (Items.IndustrialRobot, 2)], 240),
        // Bigger power: transformers for the substation, the pylon and the plants, the coal and nuclear power plants and the energy store.
        new(Items.Transformer, 1, TimeSpan.FromSeconds(30), [(Items.CopperIngot, 6), (Items.IronIngot, 4), (Items.SteelIngot, 1)], 8),
        new(Items.Substation, 1, TimeSpan.FromSeconds(40), [(Items.Transformer, 1), (Items.SteelIngot, 4), (Items.CopperIngot, 6), (Items.Bricks, 5)], 15),
        new(Items.Pylon, 1, TimeSpan.FromSeconds(30), [(Items.SteelIngot, 6), (Items.CopperIngot, 4), (Items.Stone, 10)], 10),
        new(Items.GridTie, 1, TimeSpan.FromSeconds(60), [(Items.Transformer, 2), (Items.SteelIngot, 10), (Items.CopperIngot, 20), (Items.Concrete, 10)], 30),
        new(Items.CoalPlant, 1, TimeSpan.FromSeconds(180),
            [(Items.SteelIngot, 40), (Items.Bricks, 60), (Items.Concrete, 20), (Items.Transformer, 2), (Items.ElectricMotor, 2)], 120),
        new(Items.EnergyStore, 1, TimeSpan.FromSeconds(120), [(Items.Battery, 8), (Items.SteelIngot, 20), (Items.Transformer, 2), (Items.Concrete, 10)], 80),
        new(Items.NuclearPlant, 1, TimeSpan.FromSeconds(300),
            [(Items.PrecastConcrete, 60), (Items.SteelIngot, 80), (Items.PressureVessel, 2), (Items.Alternator, 2), (Items.ControlModule, 2), (Items.Transformer, 4)], 200),
        // Husbandry and gastronomy: the canteen, the slaughterhouse, the dairy, the potato distillery, the biogas plant, the stable, the feeder,
        // the farmer's farmstead and manor farm, and what is made of hides.
        new(Items.Canteen, 1, TimeSpan.FromSeconds(120), [(Items.Kitchen, 1), (Items.Planks, 20), (Items.IronIngot, 10), (Items.Glass, 10)], 40),
        new(Items.Slaughterhouse, 1, TimeSpan.FromSeconds(60), [(Items.Bricks, 20), (Items.IronIngot, 10), (Items.Stone, 20), (Items.Planks, 10)], 25),
        new(Items.Dairy, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 10), (Items.IronIngot, 10), (Items.Glass, 5), (Items.TinIngot, 2)], 25),
        new(Items.PotatoDistillery, 1, TimeSpan.FromSeconds(90), [(Items.Bricks, 20), (Items.CopperIngot, 10), (Items.Tank, 1), (Items.Pipe, 4)], 40),
        new(Items.BiogasPlant, 1, TimeSpan.FromSeconds(180), [(Items.Bricks, 40), (Items.IronIngot, 20), (Items.Tank, 2), (Items.Pipe, 10)], 80),
        new(Items.Stable, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 30), (Items.Beams, 10), (Items.Nails, 20), (Items.Hide, 2)], 25),
        new(Items.Feeder, 1, TimeSpan.FromSeconds(25), [(Items.Planks, 12), (Items.Nails, 6)], 10),
        new(Items.Farmstead, 1, TimeSpan.FromSeconds(60), [(Items.Wood, 40), (Items.Planks, 20), (Items.Nails, 20), (Items.Hoe, 1)], 20),
        new(Items.ManorFarm, 1, TimeSpan.FromSeconds(120),
            [(Items.Farmstead, 1), (Items.Bricks, 40), (Items.Beams, 10), (Items.IronIngot, 20), (Items.Provisions, 5)], 60),
        new(Items.Bellows, 1, TimeSpan.FromSeconds(30), [(Items.Hide, 2), (Items.Planks, 4), (Items.IronIngot, 2)], 10),
        new(Items.LeatherBoots, 1, TimeSpan.FromSeconds(50), [(Items.Hide, 2), (Items.Wool, 2), (Items.SteelIngot, 1)], 20, Items.Tailor),
        // The raw-material branches grown further: mining, forestry, husbandry, farming and energy.
        new(Items.CokeOven, 1, TimeSpan.FromSeconds(120), [(Items.Bricks, 60), (Items.SteelIngot, 15), (Items.IronIngot, 20)], 50),
        new(Items.Jeweler, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 10), (Items.Glass, 6), (Items.SteelIngot, 4), (Items.SilverIngot, 2)], 25),
        new(Items.TreeNursery, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 20), (Items.Beams, 6), (Items.Sapling, 10), (Items.Nails, 10)], 20),
        new(Items.SpinningMill, 1, TimeSpan.FromSeconds(60), [(Items.Planks, 20), (Items.IronIngot, 10), (Items.CopperIngot, 6), (Items.Nails, 10)], 25),
        new(Items.Loom, 1, TimeSpan.FromSeconds(60), [(Items.Beams, 10), (Items.Planks, 10), (Items.IronIngot, 4), (Items.Nails, 10)], 20),
        new(Items.AutoBarn, 1, TimeSpan.FromSeconds(90), [(Items.Feeder, 2), (Items.Bricks, 20), (Items.IronIngot, 20), (Items.Inserter, 2)], 30),
        new(Items.SugarFactory, 1, TimeSpan.FromSeconds(90), [(Items.Bricks, 30), (Items.IronIngot, 15), (Items.Tank, 1), (Items.Pipe, 4)], 35),
        new(Items.Sprinkler, 1, TimeSpan.FromSeconds(30), [(Items.Pipe, 4), (Items.IronIngot, 4), (Items.CopperIngot, 2)], 8),
        new(Items.MechanizedFarm, 1, TimeSpan.FromSeconds(180),
            [(Items.ManorFarm, 1), (Items.SteelIngot, 40), (Items.ElectricMotor, 4), (Items.Bricks, 40), (Items.Provisions, 10)], 120),
        new(Items.BiomassPlant, 1, TimeSpan.FromSeconds(150),
            [(Items.Bricks, 60), (Items.SteelIngot, 20), (Items.Beams, 20), (Items.CopperIngot, 20), (Items.Transformer, 1)], 70),
        new(Items.ChpPlant, 1, TimeSpan.FromSeconds(150), [(Items.CoalPlant, 1), (Items.Heater, 2), (Items.Pipe, 20), (Items.CopperIngot, 20)], 100),
        new(Items.GeothermalPlant, 1, TimeSpan.FromSeconds(180),
            [(Items.SteelIngot, 40), (Items.MineSupport, 10), (Items.Pipe, 20), (Items.Bricks, 30), (Items.Transformer, 2)], 100),
        new(Items.Electrolyzer, 1, TimeSpan.FromSeconds(90), [(Items.Tank, 2), (Items.CopperIngot, 30), (Items.SilverIngot, 2), (Items.Transformer, 1)], 40),
        new(Items.FuelCell, 1, TimeSpan.FromSeconds(90), [(Items.Tank, 1), (Items.CopperIngot, 20), (Items.SilverIngot, 4), (Items.CircuitBoard, 4)], 40),
    ];

    public static Recipe? Find(string id) => Recipes.FirstOrDefault(r => r.Id == id);

    /// <summary>Fails unless the player has an object of <paramref name="kind"/> with this id, in the garage or on a plot.</summary>
    public static Task RequireAsync(IEnumerable<IPlacedObjects> places, Guid owner, Guid id, string kind, CancellationToken cancellationToken) =>
        RequireAsync(places, owner, id, k => k == kind, kind, cancellationToken);

    /// <summary>Fails unless the player has an object of a kind <paramref name="fits"/> with this id, in
    /// the garage or on a plot; returns its kind. <paramref name="name"/> names what is missing.</summary>
    public static async Task<string> RequireAsync(
        IEnumerable<IPlacedObjects> places, Guid owner, Guid id, Func<string, bool> fits, string name, CancellationToken cancellationToken)
    {
        foreach (var place in places)
        {
            if (await place.KindAsync(owner, id, cancellationToken) is { } found)
            {
                if (fits(found))
                {
                    return found;
                }

                break;
            }
        }

        throw new GameException("no_object", $"There is no {Items.Names[name]} of yours here.");
    }
}

public sealed record CraftingJob(long Id, string Recipe, DateTimeOffset StartsAt, DateTimeOffset FinishesAt);

public sealed record CraftingState(
    IReadOnlyList<CraftingJob> Queue,
    DateTimeOffset ServerTime,
    IReadOnlyList<(string Item, int Quantity)> Done,
    XpGain? Xp);

public sealed class CraftingService(
    GameDb db, IEnumerable<IPlacedObjects> places, IInventory inventory, ICharacterProgress progress, IProductionStats stats, ITechnologies technologies,
    IOutfit outfit, TimeProvider time)
{
    /// <summary>The queue as it is now, after handing out every order that is done.</summary>
    public Task<CraftingState> StateAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (done, xp) = await CompleteDueAsync(owner, cancellationToken);
            return new CraftingState(await QueueAsync(owner, cancellationToken), time.GetUtcNow(), done, xp);
        }, cancellationToken);

    /// <summary>Orders a recipe at one of the player's workbenches or anvils: ingredients out now, the item later.</summary>
    public Task<CraftingState> CraftAsync(Guid owner, Guid workbench, string recipeId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var recipe = Workshop.Find(recipeId) ?? throw new GameException("no_recipe", "There is no such recipe.");
            await Workshop.RequireAsync(places, owner, workbench, recipe.Station, cancellationToken);
            await technologies.RequireUnlockedAsync(owner, recipe.Id, cancellationToken);
            var (done, xp) = await CompleteDueAsync(owner, cancellationToken);

            // One order at a time per player, so two at once cannot both take the last queue slot.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()) ^ 0x6372616674);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            var queue = await QueueAsync(owner, cancellationToken);
            if (queue.Count >= Workshop.MaxQueue)
            {
                throw new GameException("crafting_queue_full", $"At most {Workshop.MaxQueue} orders can wait at the workbench.");
            }

            foreach (var (item, quantity) in recipe.Ingredients)
            {
                await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            }

            var now = time.GetUtcNow();
            var start = queue.Count > 0 && queue[^1].FinishesAt > now ? queue[^1].FinishesAt : now;
            await using (var insert = await db.CommandAsync(
                "INSERT INTO crafting.jobs (owner_id, recipe, starts_at, finishes_at) VALUES (@owner, @recipe, @start, @end)", cancellationToken))
            {
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("recipe", recipe.Id);
                insert.Parameters.AddWithValue("start", start);
                // Clothes for the trade (an apron, work trousers) make it go faster.
                insert.Parameters.AddWithValue("end", start + recipe.Time / Industries.Speed(await outfit.BonusAsync(owner, Industries.Crafting, cancellationToken)));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return new CraftingState(await QueueAsync(owner, cancellationToken), now, done, xp);
        }, cancellationToken);

    /// <summary>Calls off an order that is not done yet: its ingredients come back and the later orders move up.</summary>
    public Task<CraftingState> CancelAsync(Guid owner, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (done, xp) = await CompleteDueAsync(owner, cancellationToken);
            await using (var delete = await db.CommandAsync(
                "DELETE FROM crafting.jobs WHERE id = @id AND owner_id = @owner RETURNING recipe, starts_at, finishes_at", cancellationToken))
            {
                delete.Parameters.AddWithValue("id", id);
                delete.Parameters.AddWithValue("owner", owner);
                string recipeId;
                DateTimeOffset startsAt, finishesAt;
                await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
                {
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        throw new GameException("no_order", "There is no such order.");
                    }

                    recipeId = reader.GetString(0);
                    startsAt = reader.GetFieldValue<DateTimeOffset>(1);
                    finishesAt = reader.GetFieldValue<DateTimeOffset>(2);
                }

                var now = time.GetUtcNow();
                var shift = finishesAt - (startsAt > now ? startsAt : now);
                await using var move = await db.CommandAsync(
                    """
                    UPDATE crafting.jobs SET starts_at = starts_at - @shift, finishes_at = finishes_at - @shift
                    WHERE owner_id = @owner AND starts_at >= @end
                    """, cancellationToken);
                move.Parameters.AddWithValue("owner", owner);
                move.Parameters.AddWithValue("shift", shift);
                move.Parameters.AddWithValue("end", finishesAt);
                await move.ExecuteNonQueryAsync(cancellationToken);

                if (Workshop.Find(recipeId) is { } recipe)
                {
                    foreach (var (item, quantity) in recipe.Ingredients)
                    {
                        await inventory.AddAsync(owner, item, quantity, cancellationToken);
                    }
                }
            }

            return new CraftingState(await QueueAsync(owner, cancellationToken), time.GetUtcNow(), done, xp);
        }, cancellationToken);

    private async Task<(IReadOnlyList<(string Item, int Quantity)> Done, XpGain? Xp)> CompleteDueAsync(Guid owner, CancellationToken cancellationToken)
    {
        // DELETE ... RETURNING hands each finished order to exactly one request, so an item is never given twice.
        await using var command = await db.CommandAsync(
            "DELETE FROM crafting.jobs WHERE owner_id = @owner AND finishes_at <= @now RETURNING recipe, finishes_at", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var recipes = new List<Recipe>();
        var made = new List<ProductionEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Workshop.Find(reader.GetString(0)) is { } recipe)
                {
                    recipes.Add(recipe);
                    var at = reader.GetFieldValue<DateTimeOffset>(1);
                    made.Add(ProductionEntry.Made(recipe.Id, recipe.Quantity, at));
                    made.AddRange(recipe.Ingredients.Select(i => ProductionEntry.Used(i.Item, i.Quantity, at)));
                }
            }
        }

        if (recipes.Count == 0)
        {
            return ([], null);
        }

        await stats.RecordAsync(owner, made, cancellationToken);
        var done = recipes.GroupBy(r => r.Id).Select(g => (g.Key, g.Sum(r => r.Quantity))).ToList();
        foreach (var (item, quantity) in done)
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }

        var xp = await progress.AddXpAsync(owner, recipes.Sum(r => r.Xp), cancellationToken);
        return (done, xp);
    }

    private async Task<List<CraftingJob>> QueueAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT id, recipe, starts_at, finishes_at FROM crafting.jobs WHERE owner_id = @owner ORDER BY finishes_at", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var queue = new List<CraftingJob>();
        while (await reader.ReadAsync(cancellationToken))
        {
            queue.Add(new CraftingJob(reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return queue;
    }
}

public sealed record CraftRequest(Guid Workbench, string Recipe);

public sealed record CancelCraftRequest(long Id);

public abstract class CraftingHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var owner = client.RequireAccount();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<CraftingService>(), owner, payload, cancellationToken);
        var locked = await scope.ServiceProvider.GetRequiredService<ITechnologies>().LockedAsync(owner, cancellationToken);
        var speed = Industries.Speed(await scope.ServiceProvider.GetRequiredService<IOutfit>().BonusAsync(owner, Industries.Crafting, cancellationToken));
        return new Reply("crafting", new
        {
            recipes = Workshop.Recipes.Select(r => new
            {
                id = r.Id,
                name = Items.Names[r.Id],
                quantity = r.Quantity,
                seconds = r.Time.TotalSeconds / speed,
                ingredients = r.Ingredients.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
                xp = r.Xp,
                // The technology still to research before the recipe can be ordered, or null.
                locked = locked.GetValueOrDefault(r.Id),
                station = r.Station,
            }),
            // Unix milliseconds: the client compares them with serverTime, not its own clock.
            queue = state.Queue.Select(j => new
            {
                id = j.Id,
                recipe = j.Recipe,
                name = Items.Names.GetValueOrDefault(j.Recipe, j.Recipe),
                startsAt = j.StartsAt.ToUnixTimeMilliseconds(),
                finishesAt = j.FinishesAt.ToUnixTimeMilliseconds(),
            }),
            maxQueue = Workshop.MaxQueue,
            serverTime = state.ServerTime.ToUnixTimeMilliseconds(),
            done = state.Done.Select(d => new { id = d.Item, name = Items.Names[d.Item], quantity = d.Quantity }),
            xp = state.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
        });
    }

    protected abstract Task<CraftingState> RunAsync(CraftingService crafting, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>The recipes and the player's orders; also hands out the finished ones, so the client calls it when a timer runs out.</summary>
public sealed class GetCraftingHandler(IServiceScopeFactory scopes) : CraftingHandler(scopes)
{
    public override string Type => "crafting.get";

    protected override Task<CraftingState> RunAsync(CraftingService crafting, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        crafting.StateAsync(owner, cancellationToken);
}

public sealed class CraftHandler(IServiceScopeFactory scopes) : CraftingHandler(scopes)
{
    public override string Type => "crafting.craft";

    protected override Task<CraftingState> RunAsync(CraftingService crafting, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<CraftRequest>(payload);
        return crafting.CraftAsync(owner, request.Workbench, request.Recipe ?? "", cancellationToken);
    }
}

public sealed class CancelCraftHandler(IServiceScopeFactory scopes) : CraftingHandler(scopes)
{
    public override string Type => "crafting.cancel";

    protected override Task<CraftingState> RunAsync(CraftingService crafting, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        crafting.CancelAsync(owner, Payload.Read<CancelCraftRequest>(payload).Id, cancellationToken);
}
