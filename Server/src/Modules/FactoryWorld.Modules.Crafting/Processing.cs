using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Crafting;

/// <summary>One batch of a processing machine: the ingredients it takes and what comes out.</summary>
/// <param name="Id">The recipe's id, the same as the item it makes.</param>
/// <param name="Xp">Experience for each item taken out of the machine.</param>
public sealed record ProcessingRecipe(string Id, int Quantity, TimeSpan Time, IReadOnlyList<(string Item, int Quantity)> Ingredients, int Xp)
{
    /// <summary>What else each batch gives besides the product, e.g. the hide, tallow and bones of a slaughtered cow.</summary>
    public IReadOnlyList<(string Item, int Quantity)> Extras { get; init; } = [];
}

/// <param name="Kind">The machine's item id.</param>
/// <param name="Fired">Whether it burns fuel: one batch takes heat from the fire.</param>
/// <param name="Powered">Whether it runs on power: it works in its own time (<see cref="IPowerClock"/>), slower on too little power.</param>
/// <param name="Sea">Whether it works out on the water: a boat stays in port while a storm blows (<see cref="SeaClock"/>).</param>
/// <param name="Aurora">Whether tourists keep it busy: it works faster while the aurora shines (<see cref="AuroraClock"/>).</param>
public sealed record ProcessingMachine(
    string Kind, bool Fired, IReadOnlyList<ProcessingRecipe> Recipes, bool Powered = false, bool Sea = false, bool Aurora = false)
{
    public ProcessingRecipe? Recipe(string? id) => Recipes.FirstOrDefault(r => r.Id == id);
}

/// <summary>
/// Machines that turn crops into food, put on a plot like the furnace: a mill grinds grain into
/// flour, an oil press squeezes sunflowers, a barrel pickles cabbage, and the fired bakery and
/// field kitchen bake and cook from those; a sawmill saws wood into planks, beams and pellets. In the
/// mountains a quarry cuts stone, a crusher grinds stone and ore (the mill, the quarry and the crusher come in three levels, like the sawmill), a stonemason dresses stone and marble and a
/// fired foundry casts bronze and steel and fires bricks. On the shore boats fish, a smokehouse smokes fish, a
/// cannery cans it, a salt pan boils salt and a fish farm raises salmon and mussels. The mill, the crusher, the cannery,
/// oil press and the electric and industrial sawmills run on power from a
/// generator, through poles; they keep their times (<see cref="MachineState.StartedAt"/>) in their
/// own time, which stands still while they have no power. The player sets a recipe, then loads its ingredients
/// (and fuel, where the machine burns it); the machine makes one batch after another while the
/// player is away too, and the products wait in it until taken out.
/// </summary>
public static class Processing
{
    /// <summary>Of each ingredient, and of fuel.</summary>
    public const int MaxInput = 50;

    public const int MaxFuel = 50;

    /// <summary>A full machine stops until its products are taken out.</summary>
    public const int MaxOutput = 100;

    /// <summary>What a fired machine burns, and how many batches one piece lasts.</summary>
    public static readonly IReadOnlyDictionary<string, int> Fuels = new Dictionary<string, int>
    {
        [Items.Coal] = 4,
        [Items.Charcoal] = 4,
        [Items.Wood] = 1,
        [Items.WoodPellets] = 2,
        // Coke of the coke oven burns as hot as two coal.
        [Items.Coke] = 8,
    };

    public static readonly IReadOnlyList<ProcessingMachine> Machines =
    [
        new(Items.Mill, false,
        [
            new(Items.Flour, 1, TimeSpan.FromSeconds(6), [(Items.Wheat, 2)], 1),
            new(Items.CornFlour, 1, TimeSpan.FromSeconds(6), [(Items.Corn, 2)], 1),
            // Concentrate for work horses, of corn and the distillery's stillage.
            new(Items.ConcentrateFeed, 2, TimeSpan.FromSeconds(8), [(Items.Corn, 2), (Items.Stillage, 1)], 1),
        ], Powered: true),
        // The mill's higher levels, like the sawmills': the roller mill about three times the pace with a quarter more flour out of
        // the same grain, the flour works about six times with half as much more.
        new(Items.RollerMill, false,
        [
            new(Items.Flour, 5, TimeSpan.FromSeconds(10), [(Items.Wheat, 8)], 1),
            new(Items.CornFlour, 5, TimeSpan.FromSeconds(10), [(Items.Corn, 8)], 1),
            new(Items.ConcentrateFeed, 5, TimeSpan.FromSeconds(8), [(Items.Corn, 4), (Items.Stillage, 2)], 1),
        ], Powered: true),
        new(Items.FlourWorks, false,
        [
            new(Items.Flour, 3, TimeSpan.FromSeconds(3), [(Items.Wheat, 4)], 1),
            new(Items.CornFlour, 3, TimeSpan.FromSeconds(3), [(Items.Corn, 4)], 1),
            new(Items.ConcentrateFeed, 3, TimeSpan.FromSeconds(2), [(Items.Corn, 2), (Items.Stillage, 1)], 1),
        ], Powered: true),
        new(Items.OilPress, false,
        [
            new(Items.SunflowerOil, 1, TimeSpan.FromSeconds(10), [(Items.Sunflower, 2)], 2),
        ], Powered: true),
        new(Items.PicklingBarrel, false,
        [
            new(Items.Sauerkraut, 2, TimeSpan.FromSeconds(60), [(Items.Cabbage, 2)], 1),
            new(Items.SaltedHerring, 3, TimeSpan.FromSeconds(60), [(Items.Herring, 3), (Items.Salt, 1)], 2),
        ]),
        new(Items.Bakery, true,
        [
            new(Items.Bread, 1, TimeSpan.FromSeconds(20), [(Items.Flour, 2)], 3),
            new(Items.CornBread, 1, TimeSpan.FromSeconds(20), [(Items.CornFlour, 2)], 3),
            new(Items.CarrotCake, 1, TimeSpan.FromSeconds(30), [(Items.Flour, 2), (Items.Carrot, 3), (Items.SunflowerOil, 1)], 6),
            new(Items.Gingerbread, 2, TimeSpan.FromSeconds(30), [(Items.Flour, 2), (Items.Honey, 1)], 5),
            new(Items.Donuts, 2, TimeSpan.FromSeconds(25), [(Items.Flour, 2), (Items.Sugar, 1), (Items.Egg, 1), (Items.SunflowerOil, 1)], 5),
            // Of the dairy's cheese and butter: sandwiches for the road, cheesecake for banquets and provisions that keep.
            new(Items.CheeseSandwich, 2, TimeSpan.FromSeconds(10), [(Items.Bread, 1), (Items.Cheese, 1)], 3),
            new(Items.Cheesecake, 2, TimeSpan.FromSeconds(30), [(Items.Cheese, 2), (Items.Egg, 2), (Items.Flour, 1), (Items.Butter, 1)], 6),
            new(Items.Provisions, 2, TimeSpan.FromSeconds(15), [(Items.Cheese, 2), (Items.Bread, 2), (Items.Cardboard, 1)], 2),
        ]),
        new(Items.Kitchen, true,
        [
            new(Items.Fries, 2, TimeSpan.FromSeconds(15), [(Items.Potato, 3), (Items.SunflowerOil, 1)], 2),
            new(Items.VegetableSoup, 1, TimeSpan.FromSeconds(20), [(Items.Carrot, 2), (Items.Potato, 2), (Items.Cabbage, 1)], 4),
            new(Items.Bigos, 2, TimeSpan.FromSeconds(25), [(Items.Sauerkraut, 2), (Items.Potato, 2)], 3),
            new(Items.ScrambledEggs, 2, TimeSpan.FromSeconds(15), [(Items.Egg, 3), (Items.SunflowerOil, 1)], 3),
            new(Items.FriedCarp, 1, TimeSpan.FromSeconds(15), [(Items.Carp, 1), (Items.SunflowerOil, 1)], 3),
            new(Items.FishSoup, 2, TimeSpan.FromSeconds(20), [(Items.Pike, 1), (Items.Potato, 2), (Items.Carrot, 1)], 4),
            new(Items.MushroomSoup, 2, TimeSpan.FromSeconds(20), [(Items.Mushrooms, 3), (Items.Potato, 1)], 4),
            new(Items.BerryPierogi, 2, TimeSpan.FromSeconds(25), [(Items.Flour, 2), (Items.Berries, 3)], 4),
            new(Items.HerbalTea, 2, TimeSpan.FromSeconds(10), [(Items.Herbs, 2)], 2),
            // Provisions for the crew of an orbital station.
            new(Items.Provisions, 2, TimeSpan.FromSeconds(10), [(Items.CannedFish, 1), (Items.Bread, 2), (Items.Cardboard, 1)], 2),
            // Meals that feed the works canteen and the character, and a roast for banquets.
            new(Items.WorkersLunch, 2, TimeSpan.FromSeconds(20), [(Items.Bigos, 1), (Items.Bread, 1), (Items.Milk, 1)], 4),
            new(Items.LumberjackGoulash, 1, TimeSpan.FromSeconds(25), [(Items.Meat, 2), (Items.Potato, 2), (Items.Cabbage, 1)], 5),
            new(Items.MinersBreakfast, 1, TimeSpan.FromSeconds(15), [(Items.CheeseSandwich, 1), (Items.Egg, 1), (Items.HerbalTea, 1)], 4),
            new(Items.RoastBeef, 2, TimeSpan.FromSeconds(30), [(Items.Meat, 3), (Items.Potato, 2), (Items.Butter, 1)], 6),
            // Bones boiled into gelatin, and of it a culture medium that speeds research up.
            new(Items.Gelatin, 1, TimeSpan.FromSeconds(15), [(Items.Bones, 4)], 1),
            new(Items.CultureMedium, 1, TimeSpan.FromSeconds(15), [(Items.Gelatin, 1), (Items.Egg, 2), (Items.Water, 1)], 3),
        ]),
        // Sawmills of three levels: the plain one saws by hand, the electric and the industrial one run on
        // power and glue plywood from planks. Every level gets more out of a log as well as working faster, so a better
        // sawmill pays for itself on the same wood: the electric one about three times the pace of the plain one with
        // a quarter more out of each log, the industrial one about six times with half as much more again.
        new(Items.Sawmill, false,
        [
            new(Items.Planks, 2, TimeSpan.FromSeconds(8), [(Items.Wood, 1)], 1),
            new(Items.Beams, 1, TimeSpan.FromSeconds(10), [(Items.Wood, 2)], 2),
            new(Items.WoodPellets, 3, TimeSpan.FromSeconds(8), [(Items.Wood, 2)], 1),
        ]),
        new(Items.ElectricSawmill, false,
        [
            new(Items.Planks, 5, TimeSpan.FromSeconds(5), [(Items.Wood, 2)], 1),
            new(Items.Beams, 3, TimeSpan.FromSeconds(8), [(Items.Wood, 5)], 2),
            new(Items.WoodPellets, 5, TimeSpan.FromSeconds(4), [(Items.Wood, 3)], 1),
            new(Items.Plywood, 1, TimeSpan.FromSeconds(8), [(Items.Planks, 3)], 3),
        ], Powered: true),
        new(Items.IndustrialSawmill, false,
        [
            new(Items.Planks, 9, TimeSpan.FromSeconds(4), [(Items.Wood, 3)], 1),
            new(Items.Beams, 2, TimeSpan.FromSeconds(2.5), [(Items.Wood, 3)], 2),
            new(Items.WoodPellets, 4, TimeSpan.FromSeconds(1.5), [(Items.Wood, 2)], 1),
            new(Items.Plywood, 2, TimeSpan.FromSeconds(4), [(Items.Planks, 6)], 3),
        ], Powered: true),
        // The mountain industry. A quarry cuts stone out of the mountain with nothing put in; it stands on mountain plots only.
        new(Items.Quarry, false,
        [
            new(Items.Stone, 2, TimeSpan.FromSeconds(6), [], 1),
        ]),
        // The quarry's higher levels, on power: the mechanised one cuts three times as much, the open-pit mine six times.
        new(Items.MechanizedQuarry, false,
        [
            new(Items.Stone, 6, TimeSpan.FromSeconds(6), [], 1),
        ], Powered: true),
        new(Items.OpenPitMine, false,
        [
            new(Items.Stone, 6, TimeSpan.FromSeconds(3), [], 1),
        ], Powered: true),
        // A crusher grinds stone into sand and ore into crushed ore, three from two, which smelts into as many ingots.
        new(Items.Crusher, false,
        [
            new(Items.Sand, 3, TimeSpan.FromSeconds(3), [(Items.Stone, 1)], 1),
            new(Items.CrushedIronOre, 3, TimeSpan.FromSeconds(5), [(Items.IronOre, 2)], 2),
            new(Items.CrushedCopperOre, 3, TimeSpan.FromSeconds(5), [(Items.CopperOre, 2)], 2),
            new(Items.CrushedTinOre, 3, TimeSpan.FromSeconds(6), [(Items.TinOre, 2)], 3),
            new(Items.CrushedSilverOre, 3, TimeSpan.FromSeconds(8), [(Items.SilverOre, 2)], 4),
            new(Items.CrushedGoldOre, 3, TimeSpan.FromSeconds(10), [(Items.GoldOre, 2)], 6),
        ], Powered: true),
        // The crusher's higher levels: the cone crusher about three times the pace, with seven of crushed ore from four, and the
        // ore processing plant about six times, with two from one.
        new(Items.ConeCrusher, false,
        [
            new(Items.Sand, 7, TimeSpan.FromSeconds(2.5), [(Items.Stone, 2)], 1),
            new(Items.CrushedIronOre, 7, TimeSpan.FromSeconds(4), [(Items.IronOre, 4)], 2),
            new(Items.CrushedCopperOre, 7, TimeSpan.FromSeconds(4), [(Items.CopperOre, 4)], 2),
            new(Items.CrushedTinOre, 7, TimeSpan.FromSeconds(4.5), [(Items.TinOre, 4)], 3),
            new(Items.CrushedSilverOre, 7, TimeSpan.FromSeconds(6), [(Items.SilverOre, 4)], 4),
            new(Items.CrushedGoldOre, 7, TimeSpan.FromSeconds(7.5), [(Items.GoldOre, 4)], 6),
        ], Powered: true),
        new(Items.OreProcessingPlant, false,
        [
            new(Items.Sand, 4, TimeSpan.FromSeconds(0.7), [(Items.Stone, 1)], 1),
            new(Items.CrushedIronOre, 4, TimeSpan.FromSeconds(1.2), [(Items.IronOre, 2)], 2),
            new(Items.CrushedCopperOre, 4, TimeSpan.FromSeconds(1.2), [(Items.CopperOre, 2)], 2),
            new(Items.CrushedTinOre, 4, TimeSpan.FromSeconds(1.4), [(Items.TinOre, 2)], 3),
            new(Items.CrushedSilverOre, 4, TimeSpan.FromSeconds(1.8), [(Items.SilverOre, 2)], 4),
            new(Items.CrushedGoldOre, 4, TimeSpan.FromSeconds(2.2), [(Items.GoldOre, 2)], 6),
        ], Powered: true),
        new(Items.Stonemason, false,
        [
            new(Items.StoneBlocks, 1, TimeSpan.FromSeconds(8), [(Items.Stone, 3)], 2),
            new(Items.MarbleSlabs, 2, TimeSpan.FromSeconds(10), [(Items.Marble, 1)], 3),
            // Refractory lining of firebricks.
            new(Items.RefractoryLining, 1, TimeSpan.FromSeconds(15), [(Items.Firebrick, 8), (Items.PrecastConcrete, 1)], 3),
        ]),
        // A foundry casts bronze and steel and fires bricks, on coal or wood.
        new(Items.Foundry, true,
        [
            new(Items.BronzeIngot, 4, TimeSpan.FromSeconds(12), [(Items.CopperIngot, 3), (Items.TinIngot, 1)], 4),
            new(Items.SteelIngot, 1, TimeSpan.FromSeconds(15), [(Items.IronIngot, 2), (Items.Coal, 1)], 3),
            new(Items.Bricks, 4, TimeSpan.FromSeconds(10), [(Items.Sand, 2), (Items.Stone, 2)], 2),
            // Firebricks of clay and quartz.
            new(Items.Firebrick, 4, TimeSpan.FromSeconds(10), [(Items.Clay, 2), (Items.Quartz, 1)], 2),
        ]),
        // Boats on the shore fish by themselves, 30% faster than a rod does on the same water; they stay in port in a storm.
        new(Items.FishingBoat, false,
        [
            new(Items.Herring, 3, TimeSpan.FromSeconds(12), [], 1),
            new(Items.Cod, 2, TimeSpan.FromSeconds(16), [], 2),
        ], Sea: true),
        new(Items.RowingBoat, false,
        [
            new(Items.Carp, 2, TimeSpan.FromSeconds(12), [], 1),
            new(Items.Pike, 1, TimeSpan.FromSeconds(12), [], 2),
        ], Sea: true),
        // A smokehouse smokes over a wood fire; a cannery on power cans herring in oil and grinds what is left into meal.
        new(Items.Smokehouse, true,
        [
            new(Items.SmokedFish, 1, TimeSpan.FromSeconds(20), [(Items.Cod, 2)], 3),
            new(Items.SmokedSalmon, 1, TimeSpan.FromSeconds(25), [(Items.Salmon, 1)], 4),
            // Meat dried in the smoke keeps; provisions packed of it go into big builds.
            new(Items.DriedMeat, 2, TimeSpan.FromSeconds(30), [(Items.Meat, 3), (Items.Salt, 1)], 4),
            new(Items.Provisions, 2, TimeSpan.FromSeconds(15), [(Items.DriedMeat, 1), (Items.Bread, 2), (Items.Cardboard, 1)], 2),
        ]),
        new(Items.Cannery, false,
        [
            new(Items.CannedFish, 2, TimeSpan.FromSeconds(15), [(Items.Herring, 2), (Items.SunflowerOil, 1), (Items.TinIngot, 1)], 4),
            new(Items.FishMeal, 2, TimeSpan.FromSeconds(8), [(Items.Herring, 3)], 1),
            new(Items.FishOil, 1, TimeSpan.FromSeconds(10), [(Items.Herring, 4)], 2),
        ], Powered: true),
        // A salt pan boils salt out of the sea in the sun; it stands on the coast, by the water.
        new(Items.SaltPan, false,
        [
            new(Items.Salt, 1, TimeSpan.FromSeconds(20), [], 1),
        ]),
        // A fish farm keeps salmon in cages fed on fish meal; mussels grow on its ropes by themselves.
        new(Items.FishFarm, false,
        [
            new(Items.Salmon, 2, TimeSpan.FromSeconds(40), [(Items.FishMeal, 1)], 3),
            new(Items.Mussels, 2, TimeSpan.FromSeconds(60), [], 2),
        ]),
        // Oil: a fired distillery boils crude oil into kerosene or leaves asphalt; a refinery on power splits it into
        // any fuel, faster and more of it; a cracker breaks heavy fuels into petrol; a chemical plant makes plastic,
        // rubber, fertiliser and tyres.
        new(Items.Distillery, true,
        [
            new(Items.Kerosene, 1, TimeSpan.FromSeconds(12), [(Items.CrudeOil, 2)], 2),
            new(Items.Asphalt, 1, TimeSpan.FromSeconds(10), [(Items.CrudeOil, 2)], 1),
            // Lubricant for bearings and gearboxes, of kerosene and wax.
            new(Items.Lubricant, 2, TimeSpan.FromSeconds(10), [(Items.Kerosene, 2), (Items.Wax, 1)], 2),
        ]),
        new(Items.Refinery, false,
        [
            new(Items.Gasoline, 2, TimeSpan.FromSeconds(8), [(Items.CrudeOil, 3)], 3),
            new(Items.Diesel, 2, TimeSpan.FromSeconds(8), [(Items.CrudeOil, 3)], 3),
            new(Items.Kerosene, 2, TimeSpan.FromSeconds(6), [(Items.CrudeOil, 2)], 2),
            new(Items.Asphalt, 2, TimeSpan.FromSeconds(6), [(Items.CrudeOil, 2)], 1),
            // Rocket fuel: kerosene chilled with coolant.
            new(Items.RocketFuel, 2, TimeSpan.FromSeconds(15), [(Items.Kerosene, 3), (Items.Coolant, 1)], 3),
        ], Powered: true),
        new(Items.Cracker, false,
        [
            new(Items.Gasoline, 4, TimeSpan.FromSeconds(10), [(Items.Diesel, 2), (Items.Kerosene, 2)], 4),
            new(Items.Diesel, 3, TimeSpan.FromSeconds(10), [(Items.Asphalt, 2), (Items.NaturalGas, 1)], 3),
        ], Powered: true),
        new(Items.ChemicalPlant, false,
        [
            new(Items.Plastic, 2, TimeSpan.FromSeconds(8), [(Items.NaturalGas, 2)], 3),
            new(Items.SyntheticRubber, 1, TimeSpan.FromSeconds(10), [(Items.Kerosene, 1), (Items.Sulfur, 1)], 3),
            new(Items.Fertilizer, 3, TimeSpan.FromSeconds(10), [(Items.NaturalGas, 2)], 2),
            new(Items.Tyres, 1, TimeSpan.FromSeconds(20), [(Items.SyntheticRubber, 4), (Items.SteelIngot, 1)], 8),
            new(Items.Insecticide, 2, TimeSpan.FromSeconds(10), [(Items.Turpentine, 1), (Items.NaturalGas, 1)], 3),
            // Glass laminate, coolant and carbon composite for the crafting tree.
            new(Items.GlassLaminate, 2, TimeSpan.FromSeconds(10), [(Items.Glass, 2), (Items.Resin, 1), (Items.Plastic, 1)], 2),
            new(Items.Coolant, 2, TimeSpan.FromSeconds(10), [(Items.NaturalGas, 2), (Items.Water, 2), (Items.Ice, 2)], 2),
            new(Items.CarbonComposite, 2, TimeSpan.FromSeconds(15), [(Items.GlassLaminate, 2), (Items.Charcoal, 2), (Items.CaseinGlue, 1)], 3),
            // Fuel rods for the nuclear plant: uranium ore from the deep mines sealed in steel.
            new(Items.FuelRod, 1, TimeSpan.FromSeconds(60), [(Items.UraniumOre, 4), (Items.SteelIngot, 2)], 6),
        ], Powered: true),
        // The desert: a fired glassworks melts sand into glass (30% faster on sand) and blows bottles from waste glass;
        // an arc furnace on power makes silicon from quartz and cuts it into wafers for solar panels.
        new(Items.Glassworks, true,
        [
            new(Items.Glass, 2, TimeSpan.FromSeconds(10), [(Items.Sand, 3)], 2),
            new(Items.GlassBottles, 3, TimeSpan.FromSeconds(8), [(Items.WasteGlass, 2)], 2),
            new(Items.WindowPane, 2, TimeSpan.FromSeconds(12), [(Items.Glass, 3), (Items.TinIngot, 1)], 4),
            new(Items.Mirror, 1, TimeSpan.FromSeconds(12), [(Items.Glass, 2), (Items.SilverIngot, 1)], 5),
            new(Items.OpticalFiber, 1, TimeSpan.FromSeconds(15), [(Items.Glass, 2), (Items.Plastic, 1)], 6),
        ]),
        new(Items.ArcFurnace, false,
        [
            new(Items.Silicon, 1, TimeSpan.FromSeconds(10), [(Items.Quartz, 2), (Items.Coal, 1)], 4),
            new(Items.SiliconWafer, 2, TimeSpan.FromSeconds(8), [(Items.Silicon, 1)], 4),
            // The iridium alloy, of the glacier's iridium and meteoric iron.
            new(Items.IridiumAlloy, 1, TimeSpan.FromSeconds(10), [(Items.Iridium, 1), (Items.MeteoricIron, 2), (Items.SteelIngot, 2)], 2),
        ], Powered: true),
        // Water: a deep well on power draws it out of the ground (half as fast in the desert, twice in an oasis);
        // a desalinator on the sea coast makes it from the sea and leaves salt.
        new(Items.WaterWell, false,
        [
            new(Items.Water, 1, TimeSpan.FromSeconds(3), [], 1),
        ], Powered: true),
        new(Items.Desalinator, false,
        [
            new(Items.Water, 2, TimeSpan.FromSeconds(4), [], 1),
            new(Items.Salt, 1, TimeSpan.FromSeconds(8), [], 1),
        ], Powered: true),
        // Building: bricks dried in the sun from sand, clay and water; a fired kiln burns cement, a mixer on power makes concrete.
        new(Items.DryingYard, false,
        [
            new(Items.AdobeBricks, 2, TimeSpan.FromSeconds(30), [(Items.Sand, 2), (Items.Clay, 1), (Items.Water, 1)], 2),
        ]),
        new(Items.CementKiln, true,
        [
            new(Items.Cement, 2, TimeSpan.FromSeconds(12), [(Items.Stone, 2), (Items.Clay, 1)], 3),
        ]),
        new(Items.ConcreteMixer, false,
        [
            new(Items.Concrete, 3, TimeSpan.FromSeconds(8), [(Items.Cement, 1), (Items.Sand, 2), (Items.Water, 1)], 3),
            // Precast concrete, reinforced with steel.
            new(Items.PrecastConcrete, 1, TimeSpan.FromSeconds(10), [(Items.Concrete, 4), (Items.SteelIngot, 1)], 2),
        ], Powered: true),
        // The glacier: a fired melter turns ice into water; a cold store freezes fish and makes ice cream, and a server room
        // on power computes for research: both cool themselves for free on the glacier and on a block of ice per batch elsewhere.
        new(Items.IceMelter, true,
        [
            new(Items.Water, 2, TimeSpan.FromSeconds(4), [(Items.Ice, 1)], 1),
        ]),
        new(Items.ColdStore, false,
        [
            new(Items.FrozenFish, 2, TimeSpan.FromSeconds(20), [(Items.Cod, 2)], 2),
            new(Items.FrozenSalmon, 2, TimeSpan.FromSeconds(20), [(Items.Salmon, 2)], 3),
            new(Items.IceCream, 2, TimeSpan.FromSeconds(25), [(Items.Milk, 2), (Items.Egg, 1)], 3),
        ]),
        new(Items.ServerRoom, false,
        [
            new(Items.ComputingPower, 1, TimeSpan.FromSeconds(10), [], 2),
        ], Powered: true),
        // An ice core drill on power brings up cores of the glacier's ice, which a fired polar station studies.
        new(Items.IceCoreDrill, false,
        [
            new(Items.IceCore, 1, TimeSpan.FromSeconds(30), [], 3),
        ], Powered: true),
        new(Items.PolarStation, true,
        [
            new(Items.ResearchData, 1, TimeSpan.FromSeconds(20), [(Items.IceCore, 1)], 5),
        ]),
        // Tourists: a hotel of ice, heated by its fire, and a viewpoint take money from guests, three times as much under the aurora.
        new(Items.IceHotel, true,
        [
            new(Items.Takings, 4, TimeSpan.FromSeconds(60), [], 2),
            // A restaurant: guests pay far more for a roast.
            new(Items.DiningTakings, 10, TimeSpan.FromSeconds(60), [(Items.RoastBeef, 1)], 3),
        ], Aurora: true),
        new(Items.Viewpoint, false,
        [
            new(Items.Takings, 1, TimeSpan.FromSeconds(60), [], 1),
            // A café with cheesecake.
            new(Items.DiningTakings, 8, TimeSpan.FromSeconds(120), [(Items.Cheesecake, 1)], 2),
        ], Aurora: true),
        // Cities: an assembly hall puts together what the other biomes make; a multi-storey factory does the same twice as fast on less ground (2 × 2).
        new(Items.AssemblyHall, false, Assembly, Powered: true),
        new(Items.MultistoreyFactory, false, Assembly.Select(r => r with { Time = r.Time / 2 }).ToList(), Powered: true),
        // The crafting tree: a machine shop makes parts, a robotic hall the top of the tree; steelworks make steel, sheet and alloys
        // better than the foundry and the machine shop, and the steel combine and the megafactory do everything their parts do, three times as fast.
        new(Items.MachineShop, false, MachineShop, Powered: true),
        new(Items.RoboticHall, false, RoboticHall, Powered: true),
        new(Items.OxygenConverter, false, Converter, Powered: true),
        new(Items.RollingMill, false, Rolling, Powered: true),
        new(Items.ContinuousCaster, false, Casting, Powered: true),
        new(Items.SteelCombine, false, Combined(CombinedSpeed, Converter, Rolling, Casting), Powered: true),
        new(Items.Megafactory, false, Combined(CombinedSpeed, MachineShop, Assembly, RoboticHall), Powered: true),
        // Assembling machines make the workbench's recipes by themselves: the first one without power, as fast as the workbench, the
        // fast and the express ones on power, twice and four times as fast.
        new(Items.Assembler, false, Assembling(1)),
        new(Items.FastAssembler, false, Assembling(2), Powered: true),
        new(Items.ExpressAssembler, false, Assembling(4), Powered: true),
        // A municipal sorting plant sorts the town's rubbish.
        new(Items.CitySorter, false,
        [
            new(Items.WastePaper, 2, TimeSpan.FromSeconds(8), [(Items.Trash, 4)], 1),
            new(Items.WastePlastic, 2, TimeSpan.FromSeconds(8), [(Items.Trash, 6)], 1),
            new(Items.WasteGlass, 2, TimeSpan.FromSeconds(8), [(Items.Trash, 6)], 1),
            new(Items.WasteAluminium, 1, TimeSpan.FromSeconds(10), [(Items.Trash, 10)], 1),
            new(Items.WasteCopper, 1, TimeSpan.FromSeconds(10), [(Items.Trash, 25)], 1),
        ], Powered: true),
        // Offices that also sell their services to other players (see the world's cities).
        new(Items.DesignOffice, false,
        [
            new(Items.Blueprint, 1, TimeSpan.FromSeconds(40), [(Items.WastePaper, 2)], 3),
        ], Powered: true),
        new(Items.Laboratory, false,
        [
            new(Items.ResearchData, 1, TimeSpan.FromSeconds(60), [(Items.SiliconWafer, 2), (Items.GlassBottles, 4), (Items.Water, 2)], 5),
        ], Powered: true),
        new(Items.ServiceWorkshop, false,
        [
            new(Items.Gasket, 4, TimeSpan.FromSeconds(20), [(Items.SteelIngot, 1), (Items.SyntheticRubber, 1)], 3),
        ], Powered: true),
        // Forestry: a paper mill on power pulps pellets into paper and presses waste paper into cardboard; a carpenter's shop makes
        // furniture, the oak dresser of oak wood; a charcoal kiln chars wood slowly in its own fire; a fired tar kiln boils resin.
        new(Items.PaperMill, false,
        [
            new(Items.Pulp, 2, TimeSpan.FromSeconds(12), [(Items.WoodPellets, 3), (Items.Water, 1)], 2),
            new(Items.Paper, 3, TimeSpan.FromSeconds(8), [(Items.Pulp, 2)], 2),
            // Cardboard is pressed from waste paper.
            new(Items.Cardboard, 2, TimeSpan.FromSeconds(8), [(Items.WastePaper, 3)], 2),
        ], Powered: true),
        new(Items.Carpentry, false,
        [
            new(Items.Chair, 1, TimeSpan.FromSeconds(20), [(Items.Planks, 4), (Items.Nails, 2)], 3),
            new(Items.Table, 1, TimeSpan.FromSeconds(30), [(Items.Planks, 6), (Items.Beams, 2), (Items.Nails, 4)], 5),
            new(Items.Wardrobe, 1, TimeSpan.FromSeconds(45), [(Items.Plywood, 4), (Items.Planks, 4), (Items.Nails, 6)], 8),
            new(Items.OakDresser, 1, TimeSpan.FromSeconds(60), [(Items.OakWood, 6), (Items.Nails, 4), (Items.Rosin, 1)], 12),
            // Insulation boards of wool between plywood.
            new(Items.InsulationBoard, 1, TimeSpan.FromSeconds(10), [(Items.Wool, 2), (Items.Plywood, 1)], 2),
        ]),
        new(Items.CharcoalKiln, false,
        [
            new(Items.Charcoal, 2, TimeSpan.FromSeconds(60), [(Items.Wood, 4)], 2),
        ]),
        new(Items.TarKiln, true,
        [
            new(Items.Tar, 2, TimeSpan.FromSeconds(20), [(Items.Resin, 3)], 2),
            new(Items.Turpentine, 1, TimeSpan.FromSeconds(15), [(Items.Resin, 2)], 3),
            new(Items.Rosin, 2, TimeSpan.FromSeconds(15), [(Items.Resin, 2)], 2),
            // Paint and casein glue.
            new(Items.Paint, 3, TimeSpan.FromSeconds(10), [(Items.Turpentine, 1), (Items.SunflowerOil, 1), (Items.Clay, 1)], 2),
            new(Items.CaseinGlue, 2, TimeSpan.FromSeconds(10), [(Items.Milk, 2), (Items.Rosin, 1)], 2),
            // Lubricant rendered of tallow and wax, without kerosene.
            new(Items.Lubricant, 2, TimeSpan.FromSeconds(10), [(Items.Tallow, 2), (Items.Wax, 1)], 2),
        ]),
        // Husbandry for the rest of the game. A slaughterhouse cuts a cow into meat and gives its hide, tallow and bones too.
        new(Items.Slaughterhouse, false,
        [
            new(Items.Meat, 12, TimeSpan.FromSeconds(60), [(Items.Cow, 1)], 10) { Extras = [(Items.Hide, 1), (Items.Tallow, 4), (Items.Bones, 6)] },
        ]),
        // A dairy makes cheese, butter and casein of milk, and of casein a plastic (galalith) and casein paint.
        new(Items.Dairy, false,
        [
            new(Items.Cheese, 1, TimeSpan.FromSeconds(20), [(Items.Milk, 4)], 3),
            new(Items.Butter, 1, TimeSpan.FromSeconds(15), [(Items.Milk, 3)], 2),
            new(Items.Casein, 2, TimeSpan.FromSeconds(15), [(Items.Milk, 4)], 2),
            new(Items.Plastic, 3, TimeSpan.FromSeconds(10), [(Items.Casein, 1), (Items.Water, 1)], 2),
            new(Items.Paint, 3, TimeSpan.FromSeconds(10), [(Items.Casein, 1), (Items.Clay, 1), (Items.SunflowerOil, 1)], 2),
        ]),
        // A fired potato distillery: ethanol burns in diesel generators and snowmobiles, the stillage left goes into horse feed.
        new(Items.PotatoDistillery, true,
        [
            new(Items.Ethanol, 2, TimeSpan.FromSeconds(15), [(Items.Potato, 4)], 2) { Extras = [(Items.Stillage, 1)] },
        ]),
        // A biogas plant ferments manure into gas and leaves fertiliser.
        new(Items.BiogasPlant, false,
        [
            new(Items.NaturalGas, 2, TimeSpan.FromSeconds(30), [(Items.Manure, 4), (Items.Water, 1)], 2) { Extras = [(Items.Fertilizer, 1)] },
        ]),
        // The raw-material branches grown further. A coke oven bakes coal into coke, which burns as hot as two coal, and gives tar besides.
        new(Items.CokeOven, false,
        [
            new(Items.Coke, 3, TimeSpan.FromSeconds(20), [(Items.Coal, 4)], 3) { Extras = [(Items.Tar, 1)] },
        ]),
        // A jeweller works the precious metals: gold and amber into jewellery, silver into tableware, both for the towns.
        new(Items.Jeweler, false,
        [
            new(Items.Jewelry, 1, TimeSpan.FromSeconds(40), [(Items.GoldIngot, 1), (Items.Amber, 1)], 15),
            new(Items.Silverware, 1, TimeSpan.FromSeconds(30), [(Items.SilverIngot, 2)], 6),
        ]),
        // A spinning mill on power spins wool into yarn and twists hemp into rope; a loom weaves yarn into cloth and flax into linen.
        new(Items.SpinningMill, false,
        [
            new(Items.Yarn, 2, TimeSpan.FromSeconds(10), [(Items.Wool, 2)], 1),
            new(Items.Rope, 1, TimeSpan.FromSeconds(10), [(Items.Hemp, 3)], 2),
        ], Powered: true),
        new(Items.Loom, false,
        [
            new(Items.Cloth, 1, TimeSpan.FromSeconds(15), [(Items.Yarn, 3)], 3),
            new(Items.Linen, 1, TimeSpan.FromSeconds(15), [(Items.Flax, 4)], 3),
        ]),
        // A fired sugar factory boils sugar out of beets; the pulp left goes to the animals.
        new(Items.SugarFactory, true,
        [
            new(Items.Sugar, 2, TimeSpan.FromSeconds(20), [(Items.SugarBeet, 4)], 2) { Extras = [(Items.BeetPulp, 2)] },
        ]),
        // An electrolyzer on power splits water into hydrogen, which a fuel cell turns back into power.
        new(Items.Electrolyzer, false,
        [
            new(Items.Hydrogen, 1, TimeSpan.FromSeconds(8), [(Items.Water, 1)], 1),
        ], Powered: true),
    ];

    /// <summary>What an assembly hall puts together.</summary>
    private static List<ProcessingRecipe> Assembly =>
    [
        new(Items.CircuitBoard, 2, TimeSpan.FromSeconds(15), [(Items.CopperIngot, 1), (Items.Plastic, 1), (Items.SiliconWafer, 1)], 2),
        new(Items.ElectricMotor, 1, TimeSpan.FromSeconds(20), [(Items.CopperIngot, 4), (Items.IronIngot, 2)], 2),
        // Goods the towns want: bicycles and radios, then washing machines and cars for the big towns.
        new(Items.Bicycle, 1, TimeSpan.FromSeconds(30), [(Items.SteelIngot, 3), (Items.SyntheticRubber, 2)], 4),
        new(Items.Radio, 1, TimeSpan.FromSeconds(25), [(Items.CircuitBoard, 1), (Items.Plastic, 2), (Items.CopperIngot, 1)], 3),
        new(Items.WashingMachine, 1, TimeSpan.FromSeconds(60), [(Items.SteelIngot, 4), (Items.ElectricMotor, 1), (Items.CircuitBoard, 1), (Items.Glass, 1)], 8),
        new(Items.Car, 1, TimeSpan.FromSeconds(180),
            [(Items.SteelIngot, 10), (Items.Tyres, 1), (Items.Glass, 4), (Items.ElectricMotor, 2), (Items.Radio, 1), (Items.Gearbox, 1), (Items.PaintedSheet, 4)], 20),
        // The crafting tree from level 3 up: parts, robots, rocket engines and the plasma chamber.
        new(Items.CompositePanel, 1, TimeSpan.FromSeconds(30), [(Items.CarbonComposite, 3), (Items.InsulationBoard, 1), (Items.CaseinGlue, 1)], 5),
        new(Items.Alternator, 1, TimeSpan.FromSeconds(45), [(Items.Rotor, 1), (Items.MachineHousing, 1), (Items.WiringHarness, 1)], 8),
        new(Items.ControlModule, 1, TimeSpan.FromSeconds(45), [(Items.CircuitBoard, 2), (Items.WiringHarness, 1), (Items.WindowPane, 1), (Items.MachineHousing, 1)], 8),
        new(Items.PressureVessel, 1, TimeSpan.FromSeconds(45), [(Items.CompositePanel, 2), (Items.PaintedSheet, 4), (Items.Gasket, 4)], 8),
        new(Items.ComputingCore, 1, TimeSpan.FromSeconds(90), [(Items.ControlModule, 2), (Items.OpticalFiber, 4), (Items.SiliconWafer, 4), (Items.Coolant, 2)], 12),
        new(Items.RobotArm, 1, TimeSpan.FromSeconds(90), [(Items.HydraulicActuator, 2), (Items.ControlModule, 1), (Items.WiringHarness, 2)], 12),
        new(Items.Turbopump, 1, TimeSpan.FromSeconds(90), [(Items.Rotor, 2), (Items.Gearbox, 1), (Items.CombustionChamber, 1), (Items.ControlModule, 1)], 12),
        new(Items.TurbineSet, 1, TimeSpan.FromSeconds(90), [(Items.CompositePanel, 4), (Items.Alternator, 1), (Items.Gearbox, 1)], 12),
        new(Items.CryogenicTank, 1, TimeSpan.FromSeconds(90), [(Items.PressureVessel, 1), (Items.InsulationBoard, 4), (Items.Coolant, 4), (Items.ControlModule, 1)], 12),
        new(Items.IndustrialRobot, 1, TimeSpan.FromSeconds(180), [(Items.RobotArm, 2), (Items.ComputingCore, 1), (Items.Alternator, 1)], 20),
        new(Items.NavigationSystem, 1, TimeSpan.FromSeconds(180), [(Items.ComputingCore, 1), (Items.Mirror, 4), (Items.Coil, 2), (Items.OpticalFiber, 4)], 20),
        new(Items.RocketEngine, 1, TimeSpan.FromSeconds(180), [(Items.Turbopump, 2), (Items.RocketNozzle, 1), (Items.CombustionChamber, 1), (Items.ControlModule, 1)], 20),
        new(Items.PlasmaChamber, 1, TimeSpan.FromSeconds(180), [(Items.SuperconductingMagnet, 8), (Items.RefractoryLining, 4), (Items.PressureVessel, 2), (Items.ComputingCore, 1)], 20),
    ];

    /// <summary>What a machine shop makes: wire and the mechanical parts of the crafting tree.</summary>
    private static List<ProcessingRecipe> MachineShop =>
    [
        new(Items.ElectricWire, 4, TimeSpan.FromSeconds(10), [(Items.CopperIngot, 1), (Items.Plastic, 1)], 2),
        new(Items.Bearing, 2, TimeSpan.FromSeconds(15), [(Items.SteelIngot, 1), (Items.Lubricant, 1)], 3),
        new(Items.PaintedSheet, 2, TimeSpan.FromSeconds(15), [(Items.SteelIngot, 1), (Items.Paint, 1)], 3),
        new(Items.Coil, 1, TimeSpan.FromSeconds(15), [(Items.ElectricWire, 6), (Items.GlassLaminate, 1)], 3),
        new(Items.WiringHarness, 1, TimeSpan.FromSeconds(15), [(Items.ElectricWire, 8), (Items.SyntheticRubber, 1), (Items.CaseinGlue, 1)], 3),
        new(Items.SuperconductingWire, 4, TimeSpan.FromSeconds(15), [(Items.IridiumAlloy, 1), (Items.ElectricWire, 4), (Items.Coolant, 2)], 3),
        new(Items.Gearbox, 1, TimeSpan.FromSeconds(30), [(Items.BronzeIngot, 2), (Items.Bearing, 2), (Items.Lubricant, 1)], 5),
        new(Items.Rotor, 1, TimeSpan.FromSeconds(30), [(Items.Coil, 2), (Items.Bearing, 2), (Items.SteelIngot, 2)], 5),
        new(Items.MachineHousing, 1, TimeSpan.FromSeconds(30), [(Items.PaintedSheet, 4), (Items.InsulationBoard, 1), (Items.Gasket, 2)], 5),
        new(Items.CombustionChamber, 1, TimeSpan.FromSeconds(30), [(Items.RefractoryLining, 2), (Items.SteelIngot, 4), (Items.Gasket, 2)], 5),
        new(Items.SuperconductingMagnet, 1, TimeSpan.FromSeconds(30), [(Items.SuperconductingWire, 4), (Items.Coil, 2), (Items.Coolant, 2)], 5),
        new(Items.HydraulicActuator, 1, TimeSpan.FromSeconds(45), [(Items.Gearbox, 1), (Items.Gasket, 2), (Items.Lubricant, 2), (Items.SteelIngot, 2)], 8),
        new(Items.RocketNozzle, 1, TimeSpan.FromSeconds(45), [(Items.CombustionChamber, 1), (Items.SteelIngot, 2), (Items.Firebrick, 4)], 8),
    ];

    /// <summary>What a robotic hall builds: the top of the crafting tree, rockets, satellites and fusion reactors.</summary>
    private static List<ProcessingRecipe> RoboticHall =>
    [
        new(Items.Avionics, 1, TimeSpan.FromSeconds(300), [(Items.NavigationSystem, 1), (Items.ComputingCore, 2), (Items.WiringHarness, 4)], 30),
        new(Items.PlasmaControl, 1, TimeSpan.FromSeconds(300), [(Items.ComputingCore, 2), (Items.SuperconductingMagnet, 4), (Items.ControlModule, 2), (Items.IndustrialRobot, 1)], 30),
        new(Items.RocketStage, 1, TimeSpan.FromSeconds(600), [(Items.RocketEngine, 4), (Items.CryogenicTank, 2), (Items.Avionics, 1), (Items.CompositePanel, 8)], 50),
        new(Items.Satellite, 1, TimeSpan.FromSeconds(600), [(Items.SolarPanel, 8), (Items.Avionics, 1), (Items.CompositePanel, 4), (Items.SuperconductingMagnet, 1)], 50),
        new(Items.FusionReactor, 1, TimeSpan.FromSeconds(600), [(Items.PlasmaChamber, 2), (Items.PlasmaControl, 1), (Items.TurbineSet, 2), (Items.CryogenicTank, 2)], 50),
        new(Items.LaunchRocket, 1, TimeSpan.FromSeconds(1200), [(Items.RocketStage, 2), (Items.Avionics, 1), (Items.CompositePanel, 6)], 80),
    ];

    /// <summary>What an oxygen converter, a rolling mill and a continuous caster make, each better than the machine it outdoes.</summary>
    private static List<ProcessingRecipe> Converter =>
    [
        new(Items.SteelIngot, 2, TimeSpan.FromSeconds(12), [(Items.IronIngot, 3), (Items.Coal, 1)], 3),
    ];

    private static List<ProcessingRecipe> Rolling =>
    [
        new(Items.PaintedSheet, 3, TimeSpan.FromSeconds(10), [(Items.SteelIngot, 1), (Items.Paint, 1)], 3),
        new(Items.ElectricWire, 6, TimeSpan.FromSeconds(6), [(Items.CopperIngot, 1), (Items.Plastic, 1)], 2),
    ];

    private static List<ProcessingRecipe> Casting =>
    [
        new(Items.BronzeIngot, 5, TimeSpan.FromSeconds(10), [(Items.CopperIngot, 3), (Items.TinIngot, 1)], 4),
        new(Items.IridiumAlloy, 2, TimeSpan.FromSeconds(30), [(Items.Iridium, 1), (Items.MeteoricIron, 2), (Items.SteelIngot, 2)], 20),
    ];

    /// <summary>What an assembling machine makes, <paramref name="faster"/> times as fast as the workbench: every workbench recipe whose
    /// ingredients fit into it (<see cref="MaxInput"/> of each).</summary>
    private static List<ProcessingRecipe> Assembling(double faster) =>
        Workshop.Recipes.Where(r => r.Station == Items.Workbench && r.Ingredients.All(i => i.Quantity <= MaxInput))
            .Select(r => new ProcessingRecipe(r.Id, r.Quantity, r.Time / faster, r.Ingredients, r.Xp)).ToList();

    /// <summary>Recipes of several machines in one, <paramref name="faster"/> times as fast.</summary>
    private static List<ProcessingRecipe> Combined(double faster, params List<ProcessingRecipe>[] lists) =>
        lists.SelectMany(l => l).GroupBy(r => r.Id).Select(g => g.First() with { Time = g.First().Time / faster }).ToList();

    /// <summary>How much faster the steel combine and the megafactory work than the machines they combine.</summary>
    public const double CombinedSpeed = 3;

    /// <summary>How much faster every machine works in a city: industry and assembly +25%.</summary>
    public const double CityBonus = 1.25;

    /// <summary>What one piece of takings a tourist pays, in cents.</summary>
    public const long TakingsCents = 500;

    /// <summary>The biome number of a city.</summary>
    public const int City = 9;

    /// <summary>Machines that cool themselves with a block of ice per batch off the glacier.</summary>
    private static bool Iced(string kind, int biome) => kind is Items.ColdStore or Items.ServerRoom && biome != Blizzard.Biome;

    public static readonly IReadOnlyDictionary<string, ProcessingMachine> ByKind = Machines.ToDictionary(m => m.Kind);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, double, bool), ProcessingMachine> Variants = new();

    /// <summary>How much faster a machine of <paramref name="kind"/> works on a plot of <paramref name="biome"/>: a glassworks on sand,
    /// a smokehouse and a cannery by the water, a deep well slower in the desert and faster in an oasis.</summary>
    public static double Bonus(string kind, int biome) => kind switch
    {
        Items.Glassworks when Oasis.Sandy(biome) => 1.3,
        Items.Smokehouse or Items.Cannery when biome is 10 or 11 => 1.15,
        Items.WaterWell when biome == Oasis.Desert => 0.5,
        Items.WaterWell when biome == Oasis.Biome => 2,
        // The cold air of the glacier cools what runs hot.
        Items.Refinery or Items.Cracker or Items.ArcFurnace or Items.ChemicalPlant when biome == Blizzard.Biome => 1.2,
        _ when biome == City => CityBonus,
        _ => 1,
    };

    /// <summary>How much faster a machine on power works on a plot with a control center.</summary>
    public const double ControlBonus = 1.2;

    /// <summary>How much faster a foundry works on a plot with a leather bellows.</summary>
    public const double BellowsBonus = 1.15;

    /// <summary>The machine of <paramref name="kind"/> as it works on a plot of <paramref name="biome"/>: its recipes shorter by its <see cref="Bonus"/>
    /// (by <see cref="ControlBonus"/> on power when the plot has a control center, by <see cref="BellowsBonus"/> for a foundry with bellows and by
    /// <see cref="Canteens.Bonus"/> for a crew the plot's canteen <paramref name="fed"/>), and off the glacier with a block of ice more for a machine that has to be cooled.</summary>
    public static ProcessingMachine? For(string kind, int biome, bool controlled = false, bool bellows = false, bool fed = false)
    {
        if (!ByKind.TryGetValue(kind, out var machine))
        {
            return null;
        }

        var bonus = Bonus(kind, biome) * (controlled && machine.Powered ? ControlBonus : 1) * (bellows && kind == Items.Foundry ? BellowsBonus : 1)
            * (fed && Canteens.Eats(kind) ? Canteens.Bonus : 1);
        var iced = Iced(kind, biome);
        return bonus == 1 && !iced ? machine : Variants.GetOrAdd((kind, bonus, iced), _ => machine with
        {
            Recipes = machine.Recipes.Select(r => r with
            {
                Time = r.Time / bonus,
                Ingredients = iced ? [.. r.Ingredients, (Items.Ice, 1)] : r.Ingredients,
            }).ToList(),
        });
    }

    /// <summary>The machine of <paramref name="kind"/> as it was when it loaded <paramref name="state"/>: cooled with ice when it holds a slot more
    /// than its plain recipe has. For a machine taken off its plot, which no longer tells its biome.</summary>
    public static ProcessingMachine? ForState(string kind, MachineState state) =>
        ByKind.TryGetValue(kind, out var machine) && machine.Recipe(state.Recipe) is { } recipe && state.Inputs.Count > recipe.Ingredients.Count
            ? For(kind, 0)
            : machine;

    /// <summary>The biome of the plot a machine stands on, or 0 when it stands on none.</summary>
    public static async Task<int> BiomeOfAsync(IPlotObjects plots, IPlotRegistry registry, Guid id, CancellationToken cancellationToken) =>
        await plots.PlotOfAsync(id, cancellationToken) is { } at ? (await registry.FindAsync(at.X, at.Y, cancellationToken))?.Biome ?? 0 : 0;

    /// <summary>The machine of <paramref name="kind"/> as it works where the object <paramref name="id"/> stands at <paramref name="now"/>: by the plot's biome,
    /// its control center, its bellows and whether its canteen has food.</summary>
    public static async Task<ProcessingMachine?> OnPlotAsync(
        string kind, GameDb db, IPlotObjects plots, IPlotRegistry registry, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await plots.PlotOfAsync(id, cancellationToken) is not { } at)
        {
            return For(kind, 0);
        }

        var biome = (await registry.FindAsync(at.X, at.Y, cancellationToken))?.Biome ?? 0;
        var objects = await plots.ObjectsAsync(at.X, at.Y, cancellationToken);
        var fed = Canteens.Eats(kind) && await Canteens.FedAsync(db, objects, now, cancellationToken);
        return For(kind, biome, objects.Any(o => o.Kind == Items.ControlCenter), objects.Any(o => o.Kind == Items.Bellows), fed);
    }

    /// <summary>The clock a machine works by: its own time on the power grid, the boats' time that stands still in a storm, or plain time.</summary>
    public static async Task<IPowerClock> ClockAsync(ProcessingMachine machine, IPlotPower power, Guid id, CancellationToken cancellationToken) =>
        machine.Powered ? await power.ClockAsync(id, cancellationToken) : machine.Sea ? SeaClock.Instance : machine.Aurora ? AuroraClock.Instance : PlainTime.Instance;

    /// <summary>Batches the machine can still make from what it holds, ignoring time.</summary>
    public static int Batches(ProcessingMachine machine, MachineState s)
    {
        if (machine.Recipe(s.Recipe) is not { } recipe)
        {
            return 0;
        }

        var n = (MaxOutput - s.Output) / recipe.Quantity;
        for (var i = 0; i < recipe.Ingredients.Count; i++)
        {
            n = Math.Min(n, s.Input(i) / recipe.Ingredients[i].Quantity);
        }

        for (var i = 0; i < recipe.Extras.Count; i++)
        {
            n = Math.Min(n, (MaxOutput - s.Extra(i)) / recipe.Extras[i].Quantity);
        }

        if (machine.Fired)
        {
            n = Math.Min(n, s.Heat + s.Fuel * FuelBatches(s.FuelItem));
        }

        return Math.Max(0, n);
    }

    private static int FuelBatches(string? fuel) => fuel is not null && Fuels.TryGetValue(fuel, out var batches) ? batches : 0;

    /// <summary>Whether <paramref name="item"/> can go by the fire now.</summary>
    public static bool FireTakes(ProcessingMachine machine, MachineState s, string item) =>
        machine.Fired && Fuels.ContainsKey(item) && (s.FuelItem is null || s.FuelItem == item || s.Fuel == 0) && s.Fuel < MaxFuel;

    /// <summary>
    /// Where an inserter puts <paramref name="item"/> that is both fuel and an ingredient of the recipe (coal for steel, in
    /// <paramref name="slot"/>): by the fire while the fire lasts for fewer batches than the ingredient, so neither runs out first.
    /// </summary>
    public static bool ToFire(ProcessingMachine machine, MachineState s, string item, int slot)
    {
        if (!FireTakes(machine, s, item))
        {
            return false;
        }

        if (slot < 0 || machine.Recipe(s.Recipe) is not { } recipe || s.Input(slot) >= MaxInput)
        {
            return true;
        }

        var fire = s.Heat + s.Fuel * FuelBatches(s.Fuel > 0 ? s.FuelItem : item);
        return fire < s.Input(slot) / recipe.Ingredients[slot].Quantity;
    }

    /// <summary>What the machine holds after working until <paramref name="now"/>: batches started after its service was due take longer.</summary>
    public static MachineState Advance(ProcessingMachine machine, MachineState s, DateTimeOffset now)
    {
        if (s.StartedAt is not { } started || machine.Recipe(s.Recipe) is not { } recipe)
        {
            return s with { StartedAt = null };
        }

        var elapsed = Math.Max(0, (now - started).Ticks);
        var fresh = MachineServicing.FreshBatches(recipe, s, started);
        var plain = elapsed / recipe.Time.Ticks;
        var possible = plain <= fresh ? plain : fresh + (elapsed - fresh * recipe.Time.Ticks) / MachineServicing.WornTime(recipe).Ticks;
        var n = (int)Math.Min(possible, Batches(machine, s));
        var inputs = s.Inputs.ToArray();
        for (var i = 0; i < recipe.Ingredients.Count; i++)
        {
            inputs[i] -= n * recipe.Ingredients[i].Quantity;
        }

        var extras = new int[recipe.Extras.Count];
        for (var i = 0; i < extras.Length; i++)
        {
            extras[i] = s.Extra(i) + n * recipe.Extras[i].Quantity;
        }

        var after = s with { Inputs = inputs, Output = s.Output + n * recipe.Quantity, Extras = extras };
        if (machine.Fired)
        {
            var perFuel = FuelBatches(s.FuelItem);
            var burnt = n > s.Heat ? (n - s.Heat + perFuel - 1) / perFuel : 0;
            after = after with { Fuel = s.Fuel - burnt, Heat = s.Heat + burnt * perFuel - n };
            if (after.Fuel == 0)
            {
                after = after with { FuelItem = null };
            }
        }

        var freshMade = Math.Min(n, fresh);
        return Batches(machine, after) > 0
            ? after with { StartedAt = started + recipe.Time * freshMade + MachineServicing.WornTime(recipe) * (n - freshMade) }
            : after with { StartedAt = null };
    }

    /// <summary>Starts an idle machine that has everything for a batch; its first service falls due <see cref="MachineServicing.Every"/> after.</summary>
    public static MachineState Start(ProcessingMachine machine, MachineState s, DateTimeOffset now) =>
        s.StartedAt is null && Batches(machine, s) > 0 ? s with { StartedAt = now, ServiceDue = s.ServiceDue ?? now + MachineServicing.Every } : s;
}

/// <summary>
/// Machines wear: <see cref="Every"/> of their own time after they first work, or after their last service, they work
/// <see cref="SlowerPercent"/>% slower until the owner pays for a service, a share of what the trader asks for the machine.
/// </summary>
public static class MachineServicing
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(3);

    public const int SlowerPercent = 20;

    /// <summary>A service costs this share of the machine's price at the trader.</summary>
    public const int PricePercent = 10;

    /// <summary>For machines the trader does not sell.</summary>
    public const long MinCents = 100;

    public static long Cents(string kind, IMarketPrices prices) =>
        Math.Max(MinCents, (prices.SellsCents(kind) ?? 0) * PricePercent / 100);

    /// <summary>A batch at <see cref="SlowerPercent"/>% less speed.</summary>
    public static TimeSpan WornTime(ProcessingRecipe recipe) => recipe.Time * 100 / (100 - SlowerPercent);

    /// <summary>Whether a batch started at <paramref name="start"/> (machine time) is made by a machine that waits for its service.</summary>
    public static bool Worn(MachineState s, DateTimeOffset start) => s.ServiceDue is { } due && start >= due;

    public static TimeSpan BatchTime(ProcessingRecipe recipe, MachineState s, DateTimeOffset start) => Worn(s, start) ? WornTime(recipe) : recipe.Time;

    /// <summary>How many batches from <paramref name="started"/> on start before the service is due: <see cref="int.MaxValue"/> without a due date.</summary>
    public static int FreshBatches(ProcessingRecipe recipe, MachineState s, DateTimeOffset started) =>
        s.ServiceDue is not { } due ? int.MaxValue
        : due <= started ? 0
        : (int)Math.Min(int.MaxValue, ((due - started).Ticks + recipe.Time.Ticks - 1) / recipe.Time.Ticks);
}

/// <param name="Recipe">The recipe the player set, or null.</param>
/// <param name="Inputs">How much of each of the recipe's ingredients is loaded, in the recipe's order.</param>
/// <param name="FuelItem">What lies by the fire (coal or wood), or null.</param>
/// <param name="Heat">Batches left from fuel already burning.</param>
/// <param name="Output">Products made from the recipe, waiting to be taken out.</param>
/// <param name="StartedAt">When the batch being made now started; null while the machine stands idle.</param>
/// <param name="Extras">How much of each of the recipe's <see cref="ProcessingRecipe.Extras"/> waits to be taken out, in the recipe's order.</param>
/// <param name="ServiceDue">In the machine's own time, when it needs a service; batches started from then on take longer. Null until it first works.</param>
public sealed record MachineState(
    string? Recipe, IReadOnlyList<int> Inputs, string? FuelItem, int Fuel, int Heat, int Output, DateTimeOffset? StartedAt,
    IReadOnlyList<int>? Extras = null, DateTimeOffset? ServiceDue = null)
{
    public static readonly MachineState Empty = new(null, [], null, 0, 0, 0, null);

    public int Input(int i) => i < Inputs.Count ? Inputs[i] : 0;

    public int Extra(int i) => Extras is { } extras && i < extras.Count ? extras[i] : 0;

    /// <summary>Whether anything made waits in the machine: the product or what came with it.</summary>
    public bool HasOutput => Output > 0 || (Extras?.Any(e => e > 0) ?? false);
}

/// <param name="MachineTime">The machine's own time at <paramref name="ServerTime"/>, which <see cref="MachineState.StartedAt"/> is in.</param>
/// <param name="Speed">How fast it works on the power it has: 1 for machines that need none.</param>
/// <param name="ServiceCents">What a service of the machine costs.</param>
public sealed record MachineReply(
    Guid Id, ProcessingMachine Machine, MachineState State, DateTimeOffset ServerTime, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp,
    DateTimeOffset MachineTime = default, double Speed = 1, long ServiceCents = 0, long? BalanceCents = null);

/// <summary>Rows of <c>crafting.machines</c>; a machine gets one when it is first used.</summary>
internal static class MachineRows
{
    private const string Columns = "kind, recipe, inputs, fuel_item, fuel, heat, output, started_at, extras, service_due";

    private const string Write =
        """
        INSERT INTO crafting.machines (object_id, owner_id, kind, recipe, inputs, fuel_item, fuel, heat, output, started_at, extras, service_due)
        VALUES (@id, @owner, @kind, @recipe, @inputs, @fuelItem, @fuel, @heat, @output, @started, @extras, @serviceDue)
        ON CONFLICT (object_id) DO UPDATE SET
            recipe = @recipe, inputs = @inputs, fuel_item = @fuelItem, fuel = @fuel, heat = @heat,
            output = @output, started_at = @started, extras = @extras, service_due = @serviceDue
        """;

    internal static (Type, Guid) Key(Guid id) => (typeof(MachineRows), id);

    /// <summary>The machine's kind and what it holds, locked until the transaction ends.</summary>
    public static async Task<(string Kind, MachineState State)?> ReadAsync(GameDb db, Guid id, CancellationToken cancellationToken)
    {
        if (db.TakeReadAhead<(string, MachineState)?>(Key(id), out var known))
        {
            return known;
        }

        await using var command = await db.CommandAsync($"SELECT {Columns} FROM crafting.machines WHERE object_id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Row(reader) : null;
    }

    /// <summary>What the machine holds, locked; a machine used for the first time is empty, and takes over the wear of the
    /// owner's machine of its kind that was picked up nearest its service.</summary>
    public static async Task<MachineState> ReadOrNewAsync(GameDb db, Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (await ReadAsync(db, id, cancellationToken) is { } row)
        {
            return row.State;
        }

        DateTimeOffset? due;
        await using (var take = await db.CommandAsync(
            """
            DELETE FROM crafting.machine_wear WHERE id = (
                SELECT id FROM crafting.machine_wear WHERE owner_id = @owner AND kind = @kind ORDER BY service_due LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING service_due
            """, cancellationToken))
        {
            take.Parameters.AddWithValue("owner", owner);
            take.Parameters.AddWithValue("kind", kind);
            await using var reader = await take.ExecuteReaderAsync(cancellationToken);
            due = await reader.ReadAsync(cancellationToken) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
        }

        if (due is null)
        {
            return MachineState.Empty;
        }

        var state = MachineState.Empty with { ServiceDue = due };
        await WriteAsync(db, owner, id, kind, state, cancellationToken);
        return state;
    }

    /// <summary>Reads (and locks) the rows of these machines in one query, for <see cref="ReadAsync"/> to take one by one.</summary>
    public static async Task ReadAheadAsync(GameDb db, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count < 2 || db.Transaction is null)
        {
            return;
        }

        // Locked in the order of their ids, like any other transaction locking several of them.
        await using var command = await db.CommandAsync(
            $"SELECT object_id, {Columns} FROM crafting.machines WHERE object_id = ANY(@ids) ORDER BY object_id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        var found = new Dictionary<Guid, (string, MachineState)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                found[reader.GetGuid(0)] = Row(reader, 1);
            }
        }

        foreach (var id in ids)
        {
            db.ReadAhead(Key(id), found.TryGetValue(id, out var row) ? row : null);
        }
    }

    private static (string Kind, MachineState State) Row(NpgsqlDataReader reader, int at = 0) =>
        (reader.GetString(at), new MachineState(
            reader.IsDBNull(at + 1) ? null : reader.GetString(at + 1), reader.GetFieldValue<int[]>(at + 2),
            reader.IsDBNull(at + 3) ? null : reader.GetString(at + 3), reader.GetInt32(at + 4), reader.GetInt32(at + 5), reader.GetInt32(at + 6),
            reader.IsDBNull(at + 7) ? null : reader.GetFieldValue<DateTimeOffset>(at + 7), reader.GetFieldValue<int[]>(at + 8),
            reader.IsDBNull(at + 9) ? null : reader.GetFieldValue<DateTimeOffset>(at + 9)));

    public static async Task WriteAsync(GameDb db, Guid owner, Guid id, string kind, MachineState s, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(Write, cancellationToken);
        Fill(command.Parameters, owner, id, kind, s);
        db.ForgetReadAhead(Key(id));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes several machines in one round trip.</summary>
    public static async Task WriteAsync(GameDb db, IReadOnlyList<(Guid Owner, Guid Id, string Kind, MachineState State)> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using var batch = await db.BatchAsync(cancellationToken);
        foreach (var (owner, id, kind, state) in rows)
        {
            var command = new NpgsqlBatchCommand(Write);
            Fill(command.Parameters, owner, id, kind, state);
            batch.BatchCommands.Add(command);
            db.ForgetReadAhead(Key(id));
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Fill(NpgsqlParameterCollection parameters, Guid owner, Guid id, string kind, MachineState s)
    {
        parameters.AddWithValue("id", id);
        parameters.AddWithValue("owner", owner);
        parameters.AddWithValue("kind", kind);
        parameters.AddWithValue("recipe", (object?)s.Recipe ?? DBNull.Value);
        parameters.AddWithValue("inputs", s.Inputs.ToArray());
        parameters.AddWithValue("fuelItem", (object?)s.FuelItem ?? DBNull.Value);
        parameters.AddWithValue("fuel", s.Fuel);
        parameters.AddWithValue("heat", s.Heat);
        parameters.AddWithValue("output", s.Output);
        parameters.AddWithValue("extras", (s.Extras ?? []).ToArray());
        parameters.Add(new NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)s.StartedAt ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter("serviceDue", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)s.ServiceDue ?? DBNull.Value });
    }

    /// <summary>Everything the machine holds, as item stacks.</summary>
    public static IEnumerable<(string Item, int Quantity)> Contents(ProcessingMachine machine, MachineState s)
    {
        if (machine.Recipe(s.Recipe) is { } recipe)
        {
            if (s.Output > 0)
            {
                yield return (recipe.Id, s.Output);
            }

            for (var i = 0; i < recipe.Extras.Count; i++)
            {
                if (s.Extra(i) > 0)
                {
                    yield return (recipe.Extras[i].Item, s.Extra(i));
                }
            }

            for (var i = 0; i < recipe.Ingredients.Count; i++)
            {
                if (s.Input(i) > 0)
                {
                    yield return (recipe.Ingredients[i].Item, s.Input(i));
                }
            }
        }

        if (s.FuelItem is { } fuel && s.Fuel > 0)
        {
            yield return (fuel, s.Fuel);
        }
    }

    /// <summary>The machine is being picked up: what it holds goes to the owner's inventory.</summary>
    public static async Task ReleaseAsync(
        GameDb db, IInventory inventory, IWallet wallet, IProductionStats stats, IPlotPower power, Guid owner, Guid id, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await ReadAsync(db, id, cancellationToken) is not { } row || Processing.ForState(row.Kind, row.State) is not { } machine)
        {
            return;
        }

        var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
        var state = Processing.Advance(machine, row.State, clock.At(now));
        await stats.RecordAsync(owner, MachineProduction.Processed(machine, row.State, state, clock, now), cancellationToken);
        foreach (var (item, quantity) in Contents(machine, state))
        {
            if (Items.IsMoney(item))
            {
                await wallet.CreditAsync(owner, quantity * Processing.TakingsCents, $"takings {id}", cancellationToken);
            }
            else
            {
                await inventory.AddAsync(owner, item, quantity, cancellationToken);
            }
        }

        // Picking a machine up and putting it down again does not service it.
        if (state.ServiceDue is { } due)
        {
            await using var wear = await db.CommandAsync(
                "INSERT INTO crafting.machine_wear (owner_id, kind, service_due) VALUES (@owner, @kind, @due)", cancellationToken);
            wear.Parameters.AddWithValue("owner", owner);
            wear.Parameters.AddWithValue("kind", machine.Kind);
            wear.Parameters.AddWithValue("due", due);
            await wear.ExecuteNonQueryAsync(cancellationToken);
        }

        db.ForgetReadAhead(MachineRows.Key(id));
        await using var delete = await db.CommandAsync("DELETE FROM crafting.machines WHERE object_id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class MachineService(
    GameDb db, IEnumerable<IPlacedObjects> places, IInventory inventory, ICharacterProgress progress, IProductionStats stats, ITechnologies technologies,
    IPlotPower power, IPlotObjects plots, IPlotRegistry registry, IWallet wallet, IMarketPrices prices, TimeProvider time)
{
    public Task<MachineReply> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock) = await OpenAsync(owner, id, cancellationToken);
            return Reply(id, machine, state, clock, [], null);
        }, cancellationToken);

    /// <summary>Sets what the machine makes. Ingredients and products of the old recipe go back to the inventory; the fire keeps burning.</summary>
    public Task<MachineReply> SetRecipeAsync(Guid owner, Guid id, string recipeId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock) = await OpenAsync(owner, id, cancellationToken);
            var recipe = machine.Recipe(recipeId) ?? throw new GameException("no_recipe", "This machine has no such recipe.");
            await technologies.RequireUnlockedAsync(owner, recipe.Id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            if (state.Recipe != recipe.Id)
            {
                // The products must all fit; the ingredients come back whether or not they do.
                (state, xp) = await TakeOutputAsync(owner, machine, state, taken, partial: false, cancellationToken);
                state = await TakeInputsAsync(owner, machine, state, taken, partial: false, cancellationToken);
                state = Processing.Start(machine, state with { Recipe = recipe.Id, Inputs = new int[recipe.Ingredients.Count] }, clock.At(time.GetUtcNow()));
                await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            }

            return Reply(id, machine, state, clock, taken, xp);
        }, cancellationToken);

    /// <summary>
    /// Puts items from the inventory into the machine: fuel by the fire, an ingredient of the recipe in its place. Something
    /// that is both (coal in a foundry making steel) goes in for the recipe, unless <paramref name="fuel"/> asks for the fire.
    /// </summary>
    public Task<MachineReply> LoadAsync(Guid owner, Guid id, string item, int quantity, bool fuel, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var (machine, state, clock) = await OpenAsync(owner, id, cancellationToken);
            var recipe = machine.Recipe(state.Recipe);
            var slot = recipe is null || fuel ? -1 : recipe.Ingredients.ToList().FindIndex(i => Items.Fills(i.Item, item));
            if (slot >= 0)
            {
                if (state.Input(slot) + quantity > Processing.MaxInput)
                {
                    throw new GameException("machine_full", $"The machine holds at most {Processing.MaxInput} of each ingredient.");
                }

                var inputs = state.Inputs.ToArray();
                inputs[slot] += quantity;
                state = state with { Inputs = inputs };
            }
            else if (machine.Fired && Processing.Fuels.ContainsKey(item))
            {
                if (state.FuelItem is { } burning && burning != item && state.Fuel > 0)
                {
                    throw new GameException("machine_busy", $"{Items.Names[burning]} lies by the fire; take it out first.");
                }

                if (state.Fuel + quantity > Processing.MaxFuel)
                {
                    throw new GameException("machine_full", $"The machine holds at most {Processing.MaxFuel} fuel.");
                }

                state = state with { FuelItem = item, Fuel = state.Fuel + quantity };
            }
            else
            {
                throw fuel
                    ? new GameException("not_fuel", "This machine does not burn that.")
                    : recipe is null
                    ? new GameException("choose_recipe", "Choose what the machine makes first.")
                    : new GameException("not_processable", "This does not go into the machine.");
            }

            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            state = Processing.Start(machine, state, clock.At(time.GetUtcNow()));
            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            return Reply(id, machine, state, clock, [], null);
        }, cancellationToken);

    /// <summary>Takes the products (<c>output</c>), the ingredients not used yet (<c>input</c>) or the fuel (<c>fuel</c>) out, as
    /// much as fits in the inventory; the rest stays in.</summary>
    public Task<MachineReply> TakeAsync(Guid owner, Guid id, string what, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock) = await OpenAsync(owner, id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            switch (what)
            {
                case "output":
                    (state, xp) = await TakeOutputAsync(owner, machine, state, taken, partial: true, cancellationToken);
                    // Room again for what was waiting on a full machine.
                    state = Processing.Start(machine, state, clock.At(time.GetUtcNow()));
                    break;
                case "input":
                    state = await TakeInputsAsync(owner, machine, state, taken, partial: true, cancellationToken);
                    break;
                case "fuel":
                    var back = 0;
                    if (state.FuelItem is { } fuel && state.Fuel > 0)
                    {
                        back = (int)await inventory.FitAsync(owner, fuel, state.Fuel, cancellationToken);
                        await inventory.AddAsync(owner, fuel, back, cancellationToken);
                        taken.Add((fuel, back));
                    }

                    if (back < state.Fuel)
                    {
                        // Fuel left by the fire keeps the batches going.
                        state = state with { Fuel = state.Fuel - back };
                        break;
                    }

                    // The batch in the fire keeps going on the fuel already lit, if there is any.
                    state = state with { FuelItem = null, Fuel = 0 };
                    if (state.Heat == 0)
                    {
                        state = state with { StartedAt = null };
                    }

                    break;
                default:
                    throw new GameException("bad_payload", "Take out output, input or fuel.");
            }

            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            return Reply(id, machine, state, clock, taken, xp);
        }, cancellationToken);

    /// <summary>Gives the player <paramref name="quantity"/> pieces; returns how many. With <paramref name="partial"/> only as many
    /// as fit, else all of them or, when they do not fit, none with <c>inventory_full</c>.</summary>
    private async Task<int> GiveAsync(Guid owner, string item, int quantity, bool partial, CancellationToken cancellationToken)
    {
        if (!partial)
        {
            await inventory.ReceiveAsync(owner, item, quantity, cancellationToken);
            return quantity;
        }

        var fits = (int)await inventory.RoomForAsync(owner, item, quantity, cancellationToken);
        if (fits > 0)
        {
            await inventory.AddAsync(owner, item, fits, cancellationToken);
        }

        return fits;
    }

    /// <summary>Hands the products (and what the recipe gives besides) to the player: as many as fit (<paramref name="partial"/>),
    /// the rest staying in, or all of them, failing with <c>inventory_full</c> when they do not fit (before a new recipe).</summary>
    private async Task<(MachineState, XpGain?)> TakeOutputAsync(
        Guid owner, ProcessingMachine machine, MachineState state, List<(string, int)> taken, bool partial, CancellationToken cancellationToken)
    {
        if (machine.Recipe(state.Recipe) is not { } recipe || !state.HasOutput)
        {
            return (state, null);
        }

        var given = 0;
        var extras = new int[recipe.Extras.Count];
        for (var i = 0; i < extras.Length; i++)
        {
            extras[i] = state.Extra(i);
            if (extras[i] > 0 && await GiveAsync(owner, recipe.Extras[i].Item, extras[i], partial, cancellationToken) is var some and > 0)
            {
                taken.Add((recipe.Extras[i].Item, some));
                extras[i] -= some;
                given += some;
            }
        }

        state = state with { Extras = extras };
        XpGain? xp = null;
        if (state.Output > 0)
        {
            int made;
            // What tourists paid goes into the wallet.
            if (Items.IsMoney(recipe.Id))
            {
                made = state.Output;
                await wallet.CreditAsync(owner, made * Processing.TakingsCents, $"takings {machine.Kind}", cancellationToken);
            }
            else
            {
                made = await GiveAsync(owner, recipe.Id, state.Output, partial, cancellationToken);
            }

            if (made > 0)
            {
                taken.Add((recipe.Id, made));
                xp = await progress.AddXpAsync(owner, (long)made * recipe.Xp, cancellationToken);
                state = state with { Output = state.Output - made };
                given += made;
            }
        }

        if (given == 0)
        {
            throw InventoryExtensions.InventoryFull();
        }

        return (state, xp);
    }

    /// <summary>Gives back the ingredients not used yet: as many as fit (<paramref name="partial"/>), the rest staying in, or all
    /// of them, fitting or not (a new recipe). The batch going on stops when what is left makes no batch.</summary>
    private async Task<MachineState> TakeInputsAsync(
        Guid owner, ProcessingMachine machine, MachineState state, List<(string, int)> taken, bool partial, CancellationToken cancellationToken)
    {
        if (machine.Recipe(state.Recipe) is not { } recipe)
        {
            return state;
        }

        var inputs = new int[recipe.Ingredients.Count];
        var given = 0;
        for (var i = 0; i < inputs.Length; i++)
        {
            inputs[i] = state.Input(i);
            if (inputs[i] == 0)
            {
                continue;
            }

            var item = recipe.Ingredients[i].Item;
            var back = partial ? (int)await inventory.RoomForAsync(owner, item, inputs[i], cancellationToken) : inputs[i];
            if (back > 0)
            {
                await inventory.AddAsync(owner, item, back, cancellationToken);
                taken.Add((item, back));
                inputs[i] -= back;
                given += back;
            }
        }

        if (given == 0 && inputs.Any(q => q > 0))
        {
            throw InventoryExtensions.InventoryFull();
        }

        state = state with { Inputs = inputs };
        return Processing.Batches(machine, state) > 0 ? state : state with { StartedAt = null };
    }

    /// <summary>Services the machine for <see cref="MachineServicing.Cents"/>: it works at full speed again for <see cref="MachineServicing.Every"/>
    /// of its own time from now. What it made until now was made at the speed it had.</summary>
    public Task<MachineReply> ServiceAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock) = await OpenAsync(owner, id, cancellationToken);
            var cents = MachineServicing.Cents(machine.Kind, prices);
            var balance = await wallet.DebitAsync(owner, cents, $"service {machine.Kind} {id}", cancellationToken);
            state = state with { ServiceDue = clock.At(time.GetUtcNow()) + MachineServicing.Every };
            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            return Reply(id, machine, state, clock, [], null) with { BalanceCents = balance };
        }, cancellationToken);

    private MachineReply Reply(Guid id, ProcessingMachine machine, MachineState state, IPowerClock clock, IReadOnlyList<(string, int)> taken, XpGain? xp)
    {
        var now = time.GetUtcNow();
        return new MachineReply(id, machine, state, now, taken, xp, clock.At(now), clock.Speed(now), MachineServicing.Cents(machine.Kind, prices));
    }

    /// <summary>The player's machine, locked until the transaction ends and worked up to now, with the
    /// clock it works by: its own time on the power grid if it runs on power.</summary>
    private async Task<(ProcessingMachine, MachineState, IPowerClock)> OpenAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        ProcessingMachine? machine = null;
        foreach (var place in places)
        {
            if (await place.KindAsync(owner, id, cancellationToken) is { } kind)
            {
                machine = await Processing.OnPlotAsync(kind, db, plots, registry, id, time.GetUtcNow(), cancellationToken);
                break;
            }
        }

        if (machine is null)
        {
            throw new GameException("no_object", "There is no machine of yours here.");
        }

        // The grid is locked before the machine, as when inserters work it.
        var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
        var stored = await MachineRows.ReadOrNewAsync(db, owner, id, machine.Kind, cancellationToken);
        var now = time.GetUtcNow();
        var state = Processing.Advance(machine, stored, clock.At(now));
        var made = MachineProduction.Processed(machine, stored, state, clock, now);
        if (made.Count > 0)
        {
            await stats.RecordAsync(owner, made, cancellationToken);
            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
        }

        return (machine, state, clock);
    }
}

/// <summary>Processing machines as inserters see them: ingredients of the chosen recipe and fuel go in, products come out.</summary>
public sealed class ProcessingPorts(GameDb db, IProductionStats stats, IPlotPower power, IPlotObjects plots, IPlotRegistry registry, TimeProvider time) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Processing.ByKind.ContainsKey(kind) || (await Processing.OnPlotAsync(kind, db, plots, registry, id, time.GetUtcNow(), cancellationToken)) is not { } machine)
        {
            return null;
        }

        var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
        var state = await MachineRows.ReadOrNewAsync(db, owner, id, machine.Kind, cancellationToken);
        return new Port(db, stats, owner, id, machine, state, clock);
    }

    public Task PrepareAsync(IReadOnlyList<PlotObject> objects, CancellationToken cancellationToken) =>
        MachineRows.ReadAheadAsync(db, objects.Where(o => Processing.ByKind.ContainsKey(o.Kind)).Select(o => o.Id).Distinct().ToList(), cancellationToken);

    public async Task SaveAsync(IReadOnlyList<IMachinePort> ports, CancellationToken cancellationToken)
    {
        var changed = ports.OfType<Port>().Where(p => p.Changed).ToList();
        foreach (var owner in changed.GroupBy(p => p.Owner))
        {
            await stats.RecordAsync(owner.Key, owner.SelectMany(p => p.Made).ToList(), cancellationToken);
        }

        await MachineRows.WriteAsync(db, changed.Select(p => (p.Owner, p.Id, p.Kind, p.State)).ToList(), cancellationToken);
    }

    /// <summary>Every call is given the time on the plot; the machine works in its own (<paramref name="clock"/>).</summary>
    private sealed class Port(GameDb db, IProductionStats stats, Guid owner, Guid id, ProcessingMachine machine, MachineState state, IPowerClock clock) : IMachinePort
    {
        private readonly List<ProductionEntry> _made = [];
        private bool _changed;

        public Guid Owner => owner;
        public Guid Id => id;
        public string Kind => machine.Kind;
        public MachineState State => state;
        public bool Changed => _changed;
        public IReadOnlyList<ProductionEntry> Made => _made;

        private ProcessingRecipe? Recipe => machine.Recipe(state.Recipe);

        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Advance(now);
            if (Recipe is not { } recipe)
            {
                return null;
            }

            // Money is no good on a belt: tourists' takings are taken out by hand.
            if (state.Output > 0 && !Items.IsMoney(recipe.Id) && wanted(recipe.Id))
            {
                return recipe.Id;
            }

            for (var i = 0; i < recipe.Extras.Count; i++)
            {
                if (state.Extra(i) > 0 && wanted(recipe.Extras[i].Item))
                {
                    return recipe.Extras[i].Item;
                }
            }

            return null;
        }

        public void Take(string item, DateTimeOffset now)
        {
            Advance(now);
            if (Recipe?.Id == item && state.Output > 0)
            {
                // Room again for what was waiting on a full machine.
                state = Processing.Start(machine, state with { Output = state.Output - 1 }, clock.At(now));
                _changed = true;
            }
            else if (Recipe is { } recipe && recipe.Extras.ToList().FindIndex(e => e.Item == item) is var extra and >= 0 && state.Extra(extra) > 0)
            {
                var extras = new int[recipe.Extras.Count];
                for (var i = 0; i < extras.Length; i++)
                {
                    extras[i] = state.Extra(i);
                }

                extras[extra]--;
                state = Processing.Start(machine, state with { Extras = extras }, clock.At(now));
                _changed = true;
            }
        }

        public bool Accepts(string item, DateTimeOffset now)
        {
            Advance(now);
            var slot = Slot(item);
            return (slot >= 0 && state.Input(slot) < Processing.MaxInput) || Processing.FireTakes(machine, state, item);
        }

        public void Put(string item, DateTimeOffset now)
        {
            // Coal for steel goes by the fire too, enough of it to burn all the coal in the recipe's place.
            if (Slot(item) is var slot and >= 0 && !Processing.ToFire(machine, state, item, slot))
            {
                var inputs = new int[Recipe!.Ingredients.Count];
                for (var i = 0; i < inputs.Length; i++)
                {
                    inputs[i] = state.Input(i);
                }

                inputs[slot]++;
                state = state with { Inputs = inputs };
            }
            else
            {
                state = state with { FuelItem = item, Fuel = state.Fuel + 1 };
            }

            state = Processing.Start(machine, state, clock.At(now));
            _changed = true;
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            Advance(now);
            return state.StartedAt is { } started && Recipe is { } recipe ? clock.When(started + MachineServicing.BatchTime(recipe, state, started), now) : null;
        }

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_changed)
            {
                await stats.RecordAsync(owner, _made, cancellationToken);
                await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            }
        }

        private void Advance(DateTimeOffset now)
        {
            var before = state;
            state = Processing.Advance(machine, state, clock.At(now));
            var made = MachineProduction.Processed(machine, before, state, clock, now);
            if (made.Count > 0)
            {
                _made.AddRange(made);
                _changed = true;
            }
        }

        private int Slot(string item) => Recipe is { } recipe ? recipe.Ingredients.ToList().FindIndex(i => Items.Fills(i.Item, item)) : -1;
    }
}

/// <summary>The clock of machines that need no power.</summary>
internal sealed class PlainTime : IPowerClock
{
    public static readonly PlainTime Instance = new();

    public DateTimeOffset At(DateTimeOffset now) => now;

    public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now) => machineTime > now ? machineTime : now;

    public double Speed(DateTimeOffset now) => 1;
}

public sealed record MachineRequest(Guid Machine);

public sealed record MachineRecipeRequest(Guid Machine, string Recipe);

/// <param name="Fuel">Puts it by the fire even where the recipe wants it too (coal in a foundry making steel).</param>
public sealed record LoadMachineRequest(Guid Machine, string Item, int Quantity, bool Fuel = false);

public sealed record TakeMachineRequest(Guid Machine, string What);

public abstract class MachineHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var owner = client.RequireAccount();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<MachineService>(), owner, payload, cancellationToken);
        var locked = await scope.ServiceProvider.GetRequiredService<ITechnologies>().LockedAsync(owner, cancellationToken);
        var machine = reply.Machine;
        var s = reply.State;
        var recipe = machine.Recipe(s.Recipe);
        return new Reply("machine", new
        {
            id = reply.Id,
            kind = machine.Kind,
            name = Items.Names[machine.Kind],
            recipe = recipe?.Id,
            recipes = machine.Recipes.Select(r => new
            {
                id = r.Id,
                name = Items.Names[r.Id],
                quantity = r.Quantity,
                seconds = r.Time.TotalSeconds,
                ingredients = r.Ingredients.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
                extras = r.Extras.Select(e => new { id = e.Item, name = Items.Names[e.Item], quantity = e.Quantity }),
                xp = r.Xp,
                locked = locked.GetValueOrDefault(r.Id),
            }),
            inputs = recipe?.Ingredients.Select((ingredient, i) => new
            {
                id = ingredient.Item,
                name = Items.Names[ingredient.Item],
                quantity = s.Input(i),
                need = ingredient.Quantity,
                // What else goes in for it, e.g. charcoal for coal.
                also = Items.StandIns(ingredient.Item).Select(a => new { id = a, name = Items.Names[a] }),
            }) ?? [],
            output = recipe is null || s.Output == 0 ? null : new { id = recipe.Id, name = Items.Names[recipe.Id], quantity = s.Output },
            // What came with the product, e.g. hide and bones out of a slaughterhouse.
            extras = recipe?.Extras.Select((e, i) => new { id = e.Item, name = Items.Names[e.Item], quantity = s.Extra(i) }).Where(e => e.quantity > 0).ToList() ?? [],
            fired = machine.Fired,
            fuel = s.FuelItem is { } fuel && s.Fuel > 0 ? new { id = fuel, name = Items.Names[fuel], quantity = s.Fuel } : null,
            heat = s.Heat,
            fuels = machine.Fired
                ? Processing.Fuels.Select(f => new { id = f.Key, name = Items.Names[f.Key], batches = f.Value })
                : [],
            batches = Processing.Batches(machine, s),
            // Unix milliseconds of when the batch being made would have started at full power (a machine
            // short of power keeps its time on its own clock), and how long one takes.
            startedAt = s.StartedAt is { } started ? (reply.ServerTime - (reply.MachineTime - started)).ToUnixTimeMilliseconds() : (long?)null,
            powered = machine.Powered,
            // How fast it works on the power it has, 0 to 1.
            speed = reply.Speed,
            // The batch being made: longer while the machine waits for its service.
            seconds = recipe is null ? (double?)null : (s.StartedAt is { } at ? MachineServicing.BatchTime(recipe, s, at) : MachineServicing.BatchTime(recipe, s, reply.MachineTime)).TotalSeconds,
            worn = MachineServicing.Worn(s, s.StartedAt ?? reply.MachineTime),
            serviceDue = s.ServiceDue is { } due ? (reply.ServerTime - (reply.MachineTime - due)).ToUnixTimeMilliseconds() : (long?)null,
            serviceCents = reply.ServiceCents,
            slowerPercent = MachineServicing.SlowerPercent,
            serverTime = reply.ServerTime.ToUnixTimeMilliseconds(),
            maxInput = Processing.MaxInput,
            maxFuel = Processing.MaxFuel,
            maxOutput = Processing.MaxOutput,
            taken = reply.Taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = reply.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
            balanceCents = reply.BalanceCents,
        });
    }

    protected abstract Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What the machine holds now; the client asks again when the next batch is due.</summary>
public sealed class GetMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.get";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        machines.StateAsync(owner, Payload.Read<MachineRequest>(payload).Machine, cancellationToken);
}

public sealed class SetMachineRecipeHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.recipe";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<MachineRecipeRequest>(payload);
        return machines.SetRecipeAsync(owner, request.Machine, request.Recipe ?? "", cancellationToken);
    }
}

public sealed class LoadMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.load";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LoadMachineRequest>(payload);
        return machines.LoadAsync(owner, request.Machine, request.Item ?? "", request.Quantity, request.Fuel, cancellationToken);
    }
}

/// <summary>Pays for a service of the machine, which then works at full speed again.</summary>
public sealed class ServiceMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.service";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        machines.ServiceAsync(owner, Payload.Read<MachineRequest>(payload).Machine, cancellationToken);
}

public sealed class TakeMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.take";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TakeMachineRequest>(payload);
        return machines.TakeAsync(owner, request.Machine, request.What ?? "", cancellationToken);
    }
}
