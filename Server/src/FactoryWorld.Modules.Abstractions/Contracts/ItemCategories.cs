namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// The groups the market shows items in, so that a player finds what they sell or want quickly. The ids are what the
/// server sends; the client names them in Polish. Every item is in exactly one group.
/// </summary>
public static class ItemCategories
{
    /// <summary>What is dug, cut, pumped or drawn: ores, wood, stone, minerals, crude oil and gas, water and ice.</summary>
    public const string Raw = "raw";
    /// <summary>Food and farming: crops, animals and what they give, fish, saplings, food and meals.</summary>
    public const string Food = "food";
    /// <summary>Fuels for furnaces, generators and rockets.</summary>
    public const string Fuel = "fuel";
    /// <summary>What is made of raw materials: ingots, timber, bricks, chemicals, parts and finished goods.</summary>
    public const string Materials = "materials";
    /// <summary>Machines and buildings put on plots, in the garage or in towns.</summary>
    public const string Machines = "machines";
    /// <summary>Belts, inserters, pipes and other transport, and storage.</summary>
    public const string Logistics = "logistics";
    /// <summary>What makes, carries or stores power.</summary>
    public const string Power = "power";
    /// <summary>Tools, backpacks and clothes.</summary>
    public const string Equipment = "equipment";
    /// <summary>Waste, decorations, souvenirs, fossils and the rest.</summary>
    public const string Other = "other";

    /// <summary>The groups in the order the market lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Raw, Materials, Food, Fuel, Machines, Logistics, Power, Equipment, Other];

    private static readonly HashSet<string> RawItems =
    [
        Items.Wood, Items.OakWood, Items.Stone, Items.Sand, Items.Clay, Items.Coal, Items.IronOre, Items.CopperOre, Items.TinOre,
        Items.SilverOre, Items.GoldOre, Items.UraniumOre, Items.Sulfur, Items.Salt, Items.Marble, Items.Quartz, Items.CrudeOil,
        Items.NaturalGas, Items.Water, Items.Ice, Items.MeteoricIron, Items.Iridium, Items.Amber, Items.Resin, Items.IceCore,
    ];

    private static readonly HashSet<string> FuelItems =
    [
        Items.Charcoal, Items.Coke, Items.WoodPellets, Items.Gasoline, Items.Diesel, Items.Kerosene, Items.Hydrogen, Items.FuelRod,
        Items.RocketFuel,
    ];

    private static readonly HashSet<string> FoodItems =
    [
        Items.Egg, Items.Wool, Items.Milk, Items.Wheat, Items.Carrot, Items.Cabbage, Items.Corn, Items.Potato, Items.Sunflower,
        Items.Flour, Items.CornFlour, Items.SunflowerOil, Items.Sauerkraut, Items.Bread, Items.CornBread, Items.CarrotCake, Items.Fries,
        Items.VegetableSoup, Items.Bigos, Items.ScrambledEggs, Items.Herring, Items.Cod, Items.Carp, Items.Pike, Items.Salmon,
        Items.Mussels, Items.Seaweed, Items.SmokedFish, Items.SmokedSalmon, Items.SaltedHerring, Items.CannedFish, Items.FishMeal,
        Items.FishOil, Items.FriedCarp, Items.FishSoup, Items.Dates, Items.FrozenFish, Items.FrozenSalmon, Items.IceCream,
        Items.Mushrooms, Items.Berries, Items.Herbs, Items.MushroomSoup, Items.BerryPierogi, Items.HerbalTea, Items.Honey,
        Items.Gingerbread, Items.Meat, Items.Hide, Items.Tallow, Items.Bones, Items.Manure, Items.Cheese, Items.Butter,
        Items.DriedMeat, Items.WorkersLunch, Items.CheeseSandwich, Items.MinersBreakfast, Items.LumberjackGoulash,
        Items.ConcentrateFeed, Items.RoastBeef, Items.Cheesecake, Items.SugarBeet, Items.Flax, Items.Hemp, Items.Sugar,
        Items.BeetPulp, Items.Donuts, Items.Provisions,
    ];

    private static readonly HashSet<string> OtherItems =
    [
        Items.Trash, Items.Candy, Items.Bauble, Items.ComputingPower, Items.ResearchData, Items.Takings, Items.DiningTakings,
    ];

    /// <summary>The group <paramref name="item"/> is listed in.</summary>
    public static string Of(string item)
    {
        if (RawItems.Contains(item))
        {
            return Raw;
        }
        if (FuelItems.Contains(item))
        {
            return Fuel;
        }
        if (FoodItems.Contains(item) || Items.IsSapling(item) || item.StartsWith("animal_", StringComparison.Ordinal))
        {
            return Food;
        }
        if (OtherItems.Contains(item) || item.StartsWith("waste_", StringComparison.Ordinal) || Items.IsSouvenir(item)
            || Items.IsDecoration(item) || Items.IsFossil(item))
        {
            return Other;
        }
        if (item.StartsWith("tool_", StringComparison.Ordinal) || item.StartsWith("backpack_", StringComparison.Ordinal) || Items.IsClothing(item))
        {
            return Equipment;
        }
        if (item.StartsWith("power_", StringComparison.Ordinal))
        {
            return Power;
        }
        if (item.StartsWith("logistics_", StringComparison.Ordinal) || item.StartsWith("storage_", StringComparison.Ordinal))
        {
            return Logistics;
        }
        if (item.StartsWith("furniture_", StringComparison.Ordinal))
        {
            return Machines;
        }
        return Materials;
    }
}
