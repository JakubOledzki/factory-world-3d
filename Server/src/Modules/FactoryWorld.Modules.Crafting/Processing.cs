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
