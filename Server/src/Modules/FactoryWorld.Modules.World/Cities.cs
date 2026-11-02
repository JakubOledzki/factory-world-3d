using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>A town of the map, which the city plots around it belong to; its centre is the plot (<paramref name="X"/>, <paramref name="Y"/>).</summary>
public sealed record Town(int X, int Y, long Population, string Name);

/// <summary>The towns the city plots belong to, made by tools/build_cities.py from the map's towns, and their counties: the land
/// each town governs, its city plots and the countryside around them, made by tools/build_counties.py.</summary>
public sealed class Towns
{
    private readonly Dictionary<(int, int), Town> _ofPlot;
    private readonly ushort[] _county;
    private readonly int _width;
    private readonly Dictionary<Town, (int Left, int Top, int Right, int Bottom)> _bounds = [];

    /// <param name="county">For every plot, row by row and <paramref name="width"/> to a row, 0 for none or 1 + the index in
    /// <paramref name="all"/> of the town whose county it is in; empty for no counties.</param>
    public Towns(IReadOnlyList<Town> all, IReadOnlyDictionary<(int X, int Y), int> plots, int width = 0, ushort[]? county = null)
    {
        All = all;
        _ofPlot = plots.ToDictionary(p => p.Key, p => all[p.Value]);
        _width = width;
        _county = county ?? [];
        for (var i = 0; i < _county.Length; i++)
        {
            if (_county[i] == 0)
            {
                continue;
            }

            int x = i % width, y = i / width;
            var town = all[_county[i] - 1];
            _bounds[town] = _bounds.TryGetValue(town, out var b)
                ? (Math.Min(b.Left, x), Math.Min(b.Top, y), Math.Max(b.Right, x), Math.Max(b.Bottom, y))
                : (x, y, x, y);
        }
    }

    public IReadOnlyList<Town> All { get; }

    /// <summary>The town a city plot belongs to; a city plot no town is near is a town of its own.</summary>
    public Town Of(int x, int y) => _ofPlot.TryGetValue((x, y), out var town) ? town : new Town(x, y, CityRules.LoneTownPopulation, "Miasto");

    /// <summary>The town whose county the plot lies in, by the counties file; null for none (the sea, land no town reaches, and
    /// city plots no town is near, which are counties of their own: see <see cref="CityService.CountyOf"/>).</summary>
    public Town? CountyOf(int x, int y)
    {
        var i = (long)y * _width + x;
        return x < 0 || x >= _width || i < 0 || i >= _county.Length || _county[i] == 0 ? null : All[_county[i] - 1];
    }

    /// <summary>The columns and rows a town's county spans; the town's own plot when the counties file does not know it.
    /// A county across the edge of the map spans it all from one side to the other.</summary>
    public (int Left, int Top, int Right, int Bottom) CountyBounds(Town town) =>
        _bounds.TryGetValue(town, out var bounds) ? bounds : (town.X, town.Y, town.X, town.Y);

    /// <summary>Reads the file: the number of towns, the towns ("column, row, population, name"), then "column, row, town" for each city plot.</summary>
    public static Towns Read(Stream gzipped, Stream? counties = null)
    {
        List<Town> towns;
        var plots = new Dictionary<(int, int), int>();
        using (var reader = new StreamReader(new GZipStream(gzipped, CompressionMode.Decompress), Encoding.UTF8))
        {
            var count = int.Parse(reader.ReadLine()!);
            towns = new List<Town>(count);
            for (var i = 0; i < count; i++)
            {
                var parts = reader.ReadLine()!.Split('\t');
                towns.Add(new Town(int.Parse(parts[0]), int.Parse(parts[1]), long.Parse(parts[2]), parts[3]));
            }

            while (reader.ReadLine() is { Length: > 0 } line)
            {
                var parts = line.Split('\t');
                plots[(int.Parse(parts[0]), int.Parse(parts[1]))] = int.Parse(parts[2]);
            }
        }

        if (counties is null)
        {
            return new Towns(towns, plots);
        }

        var (width, county) = ReadCounties(counties);
        return new Towns(towns, plots, width, county);
    }

    /// <summary>The counties file: gzip of "FWK1", width and height as little-endian u16, then a u16 per plot, row by row
    /// (0 for none, i + 1 for the i-th town).</summary>
    private static (int Width, ushort[] County) ReadCounties(Stream gzipped)
    {
        using var stream = new GZipStream(gzipped, CompressionMode.Decompress);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "FWK1")
        {
            throw new InvalidDataException("Not a Factory World counties file.");
        }

        int width = reader.ReadUInt16(), height = reader.ReadUInt16();
        var bytes = reader.ReadBytes(width * height * 2);
        if (bytes.Length != width * height * 2)
        {
            throw new InvalidDataException("The counties file is cut short.");
        }

        var county = new ushort[width * height];
        Buffer.BlockCopy(bytes, 0, county, 0, bytes.Length);
        return (width, county);
    }

    public static Towns Load()
    {
        using var resource = typeof(Towns).Assembly.GetManifestResourceStream("cities.tsv.gz")
            ?? throw new InvalidOperationException("The towns resource is missing.");
        using var counties = typeof(Towns).Assembly.GetManifestResourceStream("counties.bin.gz")
            ?? throw new InvalidOperationException("The counties resource is missing.");
        return Read(resource, counties);
    }
}

/// <summary>
/// Cities. A city plot belongs to a town of the map (<see cref="Towns"/>), and only the ground by its station can be built on
/// (<see cref="InBuildArea"/>), so it builds up: high-bay stores and multi-storey factories. Every day a town wants a few goods
/// (<see cref="Needs"/>) and pays better than the trader for them; what the players deliver makes it grow from a village to a
/// metropolis (<see cref="LevelCents"/>), and a bigger town wants more and dearer goods. Players keep shops that sell to the
/// townspeople, take the town's rubbish away for a fee, sell the services of their offices to other players, and the players
/// who own plots in the town elect a mayor every week, who sets a tax on shops and services and one bonus for the town.
/// </summary>
public static class CityRules
{
    /// <summary>The ground of a city plot that can be built on: this many tiles either side of the station's middle, and the first rows.</summary>
    public const int BuildHalfWidth = 30;

    public const int BuildRows = 60;

    public static bool InBuildArea(int tileX, int tileY) => Math.Abs(tileX - PlotLayout.Spawn.X) <= BuildHalfWidth && tileY < BuildRows;

    /// <summary>The people of a city plot no town of the map is near.</summary>
    public const long LoneTownPopulation = 20_000;

    public static readonly IReadOnlyList<string> LevelNames = ["Wieś", "Miasteczko", "Miasto", "Duże miasto", "Metropolia"];

    /// <summary>The value of goods delivered (in cents) that makes a town grow to each level.</summary>
    public static readonly IReadOnlyList<long> LevelCents = [0, 20_000_00, 100_000_00, 500_000_00, 2_500_000_00];

    public static int Level(long deliveredCents)
    {
        var level = 0;
        while (level + 1 < LevelCents.Count && deliveredCents >= LevelCents[level + 1])
        {
            level++;
        }

        return level;
    }

    /// <summary>How much a town's size counts: 1 for 10 000 people, more for a bigger town (up to 3), less for a village (down to a half).</summary>
    public static double PopulationFactor(long population) => Math.Clamp(Math.Log10(Math.Max(population, 1) / 10_000.0) + 1, 0.5, 3);

    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public static int DayOf(DateTimeOffset at) => (int)Math.Floor((at - SeaWeather.Epoch) / Day);

    public static DateTimeOffset DayStart(int day) => SeaWeather.Epoch + Day * day;

    /// <summary>Weeks of the town halls: a mayor elected in one week rules the next.</summary>
    public static int WeekOf(DateTimeOffset at) => (int)Math.Floor(DayOf(at) / 7.0);

    public static DateTimeOffset WeekStart(int week) => DayStart(week * 7);

    /// <summary>The goods towns want, from which level on, and how many a town of 10 000 people wants a day.</summary>
    public static readonly IReadOnlyList<(string Item, int Level, int Quantity)> Goods =
    [
        (Items.Bread, 0, 40), (Items.Sauerkraut, 0, 20), (Items.VegetableSoup, 0, 20), (Items.ScrambledEggs, 0, 20), (Items.Fries, 0, 20),
        (Items.SmokedFish, 0, 15), (Items.CannedFish, 0, 15), (Items.Planks, 0, 60), (Items.Bricks, 0, 80), (Items.Coal, 0, 60),
        (Items.Milk, 0, 30), (Items.Egg, 0, 30), (Items.Wool, 0, 20), (Items.GlassBottles, 0, 30), (Items.WoodPellets, 0, 40),
        (Items.CarrotCake, 1, 15), (Items.Bigos, 1, 15), (Items.FishSoup, 1, 15), (Items.FriedCarp, 1, 10), (Items.WindowPane, 1, 20),
        (Items.Concrete, 1, 40), (Items.Cement, 1, 30), (Items.IceCream, 1, 20), (Items.FrozenFish, 1, 15), (Items.Plywood, 1, 20),
        (Items.Gasoline, 1, 30), (Items.Diesel, 1, 30), (Items.SaltedHerring, 1, 15), (Items.CornBread, 1, 20),
        (Items.SmokedSalmon, 2, 10), (Items.FrozenSalmon, 2, 8), (Items.Plastic, 2, 30),
        (Items.CircuitBoard, 2, 10), (Items.ElectricMotor, 2, 5), (Items.Tyres, 2, 3),
        (Items.OpticalFiber, 3, 10), (Items.Amber, 3, 5), (Items.Mirror, 3, 8),
        // Goods from the assembly halls: a town asks for bicycles and radios first, a metropolis for cars.
        (Items.Bicycle, 2, 5), (Items.Radio, 2, 8), (Items.WashingMachine, 3, 3), (Items.Car, 4, 1),
        // Forestry: paper, furniture, honey and what the forest floor gives.
        (Items.Paper, 0, 40), (Items.Honey, 0, 20), (Items.Charcoal, 0, 30), (Items.MushroomSoup, 0, 15), (Items.BerryPierogi, 0, 15),
        (Items.Cardboard, 1, 30), (Items.Chair, 1, 8), (Items.Table, 1, 5), (Items.Candles, 1, 15), (Items.Gingerbread, 1, 15), (Items.HerbalTea, 1, 20),
        (Items.Wardrobe, 2, 3), (Items.Turpentine, 2, 10),
        (Items.OakDresser, 3, 2),
        // Clothes from the tailors.
        (Items.Beanie, 1, 10), (Items.StrawHat, 1, 10), (Items.FlannelJacket, 2, 5), (Items.RubberBoots, 2, 5),
        (Items.SuitJacket, 3, 3), (Items.DressShoes, 3, 4),
        // Husbandry and gastronomy: cheese and butter from the dairy, meat and the fine dishes of banquets.
        (Items.Cheese, 0, 20), (Items.Butter, 0, 20), (Items.Meat, 1, 30), (Items.WorkersLunch, 1, 15), (Items.LeatherBoots, 2, 4),
        (Items.RoastBeef, 2, 10), (Items.Cheesecake, 2, 10),
        // The raw-material branches grown further: sugar and donuts, cloth, linen and rope, silver tableware and jewellery.
        (Items.Sugar, 0, 30), (Items.Donuts, 1, 15), (Items.Cloth, 1, 15), (Items.Linen, 1, 15), (Items.Rope, 1, 20),
        (Items.Silverware, 2, 4), (Items.Jewelry, 3, 2),
    ];

    /// <summary>A town pays this many times the trader's price for what it wants, and more at each level.</summary>
    public const double Premium = 1.5;

    public const double PremiumPerLevel = 0.1;

    /// <summary>How much more a town pays with the mayor's "demand" bonus.</summary>
    public const double DemandBonus = 1.15;

    /// <summary>A mayor's festival: fine dishes (roast beef or cheesecake) from the mayor's inventory make the town pay
    /// <see cref="FestivalBonus"/> times as much for what it wants, for <see cref="FestivalTime"/>.</summary>
    public const long FestivalDishes = 100;

    public const double FestivalBonus = 1.15;

    public static readonly TimeSpan FestivalTime = TimeSpan.FromHours(24);

    public static int NeedCount(int level) => 3 + level;

    /// <summary>What a town wants on a day: as many goods as its level allows, picked by the day, more of each the bigger and older the town.</summary>
    public static IReadOnlyList<(string Item, long Wanted)> Needs(Town town, int level, int day, Func<string, bool> priced)
    {
        var seed = (ulong)(uint)town.X * 0x9e3779b97f4a7c15UL ^ (ulong)(uint)town.Y * 0xbf58476d1ce4e5b9UL ^ (ulong)(uint)day * 0x94d049bb133111ebUL;
        ulong Hash(string item)
        {
            var h = seed;
            foreach (var c in item)
            {
                h = (h ^ c) * 0x100000001b3UL;
            }

            h ^= h >> 31;
            h *= 0xbf58476d1ce4e5b9UL;
            return h ^ (h >> 29);
        }

        // The newest goods of the town's level come first, so a town that grew asks for what it could not before.
        var open = Goods.Where(g => g.Level <= level && priced(g.Item)).ToList();
        // Past the last level with goods of its own, the newest goods there are are the fresh ones.
        var newest = open.Count > 0 ? open.Max(g => g.Level) : 0;
        var fresh = open.Where(g => g.Level == newest).OrderBy(g => Hash(g.Item)).Take(level > 0 ? 1 : 0);
        var picked = fresh.Concat(open.Except(fresh).OrderBy(g => Hash(g.Item))).Take(NeedCount(level));
        var factor = PopulationFactor(town.Population) * (1 + 0.5 * level);
        return picked.Select(g => (g.Item, Math.Max(1, (long)Math.Round(g.Quantity * factor)))).ToList();
    }

    public static long NeedPriceCents(long traderCents, int level, bool demandBonus, bool festival = false) =>
        (long)Math.Round(traderCents * (Premium + PremiumPerLevel * level) * (demandBonus ? DemandBonus : 1) * (festival ? FestivalBonus : 1));

    /// <summary>The most a town pays for goods the trader sells, in percent of what he asks for them at his cheapest.</summary>
    public const int MaxPayOfChargePercent = 90;

    /// <summary>What the town pays for each piece of rubbish taken away.</summary>
    public const long TrashFeeCents = 20;

    /// <summary>How much rubbish a town has to give away a day.</summary>
    public static long TrashPerDay(Town town, int level) => (long)Math.Round(300 * PopulationFactor(town.Population) * (1 + level));

    /// <summary>Customers who come into a shop in an hour, for a town of 10 000 people at its centre.</summary>
    public const double CustomersPerHour = 30;

    /// <summary>How many more customers the mayor's "shoppers" bonus brings.</summary>
    public const double ShoppersBonus = 1.3;

    /// <summary>Customers an hour of a shop <paramref name="distance"/> plots from its town's centre.</summary>
    public static double Customers(Town town, int level, double distance, bool shoppers) =>
        CustomersPerHour * PopulationFactor(town.Population) * (1 + 0.25 * level) / (1 + distance / 4) * (shoppers ? ShoppersBonus : 1);

    /// <summary>Chance that a customer buys a piece at <paramref name="priceCents"/>: always at the trader's price,
    /// half the time at twice that, never at three times, and never at what the trader himself asks for it at his cheapest
    /// (<paramref name="cheapestChargeCents"/>), or goods bought from him would sell here at a profit.</summary>
    public static double Accepts(long priceCents, long traderCents, long? cheapestChargeCents = null) =>
        priceCents >= cheapestChargeCents ? 0 : Math.Clamp(1.5 - priceCents / (2.0 * traderCents), 0, 1);

    /// <summary>A shop's sales are worked out for at most a day back.</summary>
    public static readonly TimeSpan ShopCatchUp = TimeSpan.FromDays(1);

    /// <summary>Kinds of goods a shop holds, and most of each.</summary>
    public const int ShopKinds = 8;

    public const long ShopMaxStock = 1000;

    /// <summary>What each office sells to the players who come by: what they get and its price. One office sells so many a day.</summary>
    public static readonly IReadOnlyDictionary<string, (string Item, int Quantity, long PriceCents)> Services = new Dictionary<string, (string, int, long)>
    {
        [Items.DesignOffice] = (Items.Blueprint, 1, 30_00),
        [Items.Laboratory] = (Items.ResearchData, 1, 80_00),
        [Items.ServiceWorkshop] = (Items.Gasket, 4, 90_00),
    };

    public const int ServiceSalesPerDay = 20;

    /// <summary>Part of a service's price its office's owner gets (before the mayor's tax); the rest pays for the stuff the office uses
    /// up. Less than what the trader pays for what the buyer gets keeps an office from turning a second account's money into goods for free.</summary>
    public const int ServiceOwnerPercent = 25;

    /// <summary>Rail fees from a city plot (−10%), with a freight terminal on it (another −25%), from any plot of the county with the
    /// mayor's "rail" bonus (another −20%).</summary>
    public const double CityRail = 0.9;

    public const double TerminalRail = 0.75;

    public const double MayorRail = 0.8;

    /// <summary>Routes a station with a freight terminal runs.</summary>
    public const int TerminalRoutes = 10;

    /// <summary>The highest tax a mayor sets on shops and services, in percent.</summary>
    public const int MaxTaxPercent = 10;

    /// <summary>The highest tax a mayor sets on the plots of the county, in thousandths of their worth a day, on top of the
    /// country's (3-7): see <see cref="PlotTaxService"/>.</summary>
    public const int MaxPlotTaxPerMille = 2;

    /// <summary>The bonuses a mayor picks one of, for the week.</summary>
    public static readonly IReadOnlyDictionary<string, string> Bonuses = new Dictionary<string, string>
    {
        ["demand"] = "Popyt: miasto płaci za dostawy 15% więcej",
        ["shoppers"] = "Zakupy: o 30% więcej klientów w sklepach",
        ["rail"] = "Kolej: opłaty kolejowe z działek powiatu o 20% niższe",
        ["plots"] = "Działki: działki w powiecie o 20% tańsze",
    };

    public const double CheapPlots = 0.8;

    /// <summary>The chief supplier: who delivered the most to a town in a week (at least <see cref="SupplierMinCents"/>) holds the
    /// title the next week, and the town pays them <see cref="SupplierBonusPercent"/> more for what it wants.</summary>
    public const int SupplierBonusPercent = 5;

    public const long SupplierMinCents = 500_00;

    /// <summary>How many of this week's best suppliers the town window lists.</summary>
    public const int SupplierRaceShown = 3;

    public static long SupplierPriceCents(long priceCents) => priceCents * (100 + SupplierBonusPercent) / 100;

    /// <summary>Plots between two, around the globe's seam too.</summary>
    public static double Distance(int width, int x, int y, int toX, int toY)
    {
        var dx = Math.Abs(x - toX);
        dx = Math.Min(dx, width - dx);
        return Math.Sqrt(dx * dx + (double)(y - toY) * (y - toY));
    }
}
