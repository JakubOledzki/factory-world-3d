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

/// <summary>How much a town has got delivered in all, for its level.</summary>
public sealed class TownProgressRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public long DeliveredCents { get; set; }
}

/// <summary>How much one player delivered to a town in all.</summary>
public sealed class TownContributionRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public Guid PlayerId { get; set; }

    public long DeliveredCents { get; set; }
}

/// <summary>How many pieces of an item a town got on a day; "trash" counts the rubbish taken away.</summary>
public sealed class TownDeliveryRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Day { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }
}

/// <summary>A shop's sales: until when they are worked out, the part of a customer carried over, and what it sold in all.</summary>
public sealed class ShopRow
{
    public Guid Id { get; set; }

    public DateTimeOffset SettledAt { get; set; }

    public double Carry { get; set; }

    public long SoldCents { get; set; }

    public long SoldItems { get; set; }
}

public sealed class ShopItemRow
{
    public Guid ShopId { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }

    public long PriceCents { get; set; }
}

/// <summary>Services an office sold on a day.</summary>
public sealed class ServiceSaleRow
{
    public Guid Id { get; set; }

    public int Day { get; set; }

    public int Count { get; set; }
}

public sealed class TownVoteRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid VoterId { get; set; }

    public Guid CandidateId { get; set; }
}

/// <summary>A town's mayor for a week (elected the week before; null when nobody voted) and what they set.</summary>
public sealed class TownPolicyRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid? MayorId { get; set; }

    public int TaxPercent { get; set; }

    public string? Bonus { get; set; }

    /// <summary>Two thirds of the townsfolk recalled the mayor before the week was out.</summary>
    public bool Recalled { get; set; }

    /// <summary>The mayor's tax on the plots of the county, in thousandths of their worth a day.</summary>
    public int PlotTaxPerMille { get; set; }
}

public sealed record TownPolicy(Guid? Mayor, int TaxPercent, string? Bonus, bool Recalled = false, int PlotTaxPerMille = 0);

/// <summary>A town's festival, which the mayor holds with fine dishes: it wants goods dearer until <see cref="Until"/>.</summary>
public sealed class TownFestivalRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public DateTimeOffset Until { get; set; }
}

public sealed partial class CityService(
    GameDb db, WorldMap map, Towns towns, PlotService plots, IInventory inventory, IWallet wallet, ICharacterProgress progress,
    ICharacterNames names, IMarketPrices prices, BeachService beach, IAnnouncements announcements, Countries countries,
    INationalPolicy nation, IServiceProvider services, TimeProvider time) : IMachineContents
{
    /// <summary>The town of a city plot; fails for any other plot.</summary>
    public Town RequireTown(int x, int y)
    {
        if (!map.Contains(x, y) || map.BiomeAt(x, y) != Biome.City)
        {
            throw new GameException("not_city", "This plot is not in a city.");
        }

        return towns.Of(x, y);
    }

    public Town? TownOf(int x, int y) => map.Contains(x, y) && map.BiomeAt(x, y) == Biome.City ? towns.Of(x, y) : null;

    /// <summary>The town whose county the plot lies in, whose mayor's laws hold there: a city plot's own town, or the town that
    /// governs the countryside around (<see cref="Towns.CountyOf"/>); null for the sea and land no town reaches.</summary>
    public Town? CountyOf(int x, int y) => TownOf(x, y) ?? (map.Contains(x, y) ? towns.CountyOf(x, y) : null);

    /// <summary>The town whose county the plot lies in; fails for a plot in none.</summary>
    public Town RequireCounty(int x, int y) => CountyOf(x, y) ?? throw new GameException("not_county", "This plot is in no town's county.");

    public async Task<long> DeliveredAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT delivered_cents FROM world.town_progress WHERE town_x = @x AND town_y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        return await command.ExecuteScalarAsync(cancellationToken) is long cents ? cents : 0;
    }

    /// <summary>The mayor and their policy this week: the winner of last week's vote, worked out once and kept.</summary>
    public async Task<TownPolicy> PolicyAsync(Town town, CancellationToken cancellationToken)
    {
        var week = CityRules.WeekOf(time.GetUtcNow());
        if (await ReadPolicyAsync(town, week, cancellationToken) is { } policy)
        {
            return policy;
        }

        Guid? mayor = null;
        await using (var count = await db.CommandAsync(
            """
            SELECT candidate_id FROM world.town_votes WHERE town_x = @x AND town_y = @y AND week = @week
            GROUP BY candidate_id ORDER BY count(*) DESC, candidate_id LIMIT 1
            """, cancellationToken))
        {
            count.Parameters.AddWithValue("x", town.X);
            count.Parameters.AddWithValue("y", town.Y);
            count.Parameters.AddWithValue("week", week - 1);
            mayor = await count.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        // The first look in a new week also counts the votes of the weeks before for those who stood, so their deposits are settled.
        return await db.InTransactionAsync(async () =>
        {
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.town_policies (town_x, town_y, week, mayor_id, tax_percent, bonus, recalled) VALUES (@x, @y, @week, @mayor, 0, NULL, FALSE)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("x", town.X);
                insert.Parameters.AddWithValue("y", town.Y);
                insert.Parameters.AddWithValue("week", week);
                insert.Parameters.AddWithValue("mayor", (object?)mayor ?? DBNull.Value);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    await SettleCandidatesAsync(town, week, cancellationToken);
                }
            }

            return (await ReadPolicyAsync(town, week, cancellationToken))!;
        }, cancellationToken);
    }

    private async Task<TownPolicy?> ReadPolicyAsync(Town town, int week, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT mayor_id, tax_percent, bonus, recalled, plot_tax_per_mille FROM world.town_policies WHERE town_x = @x AND town_y = @y AND week = @week",
            cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TownPolicy(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3),
                reader.GetInt32(4))
            : null;
    }

    /// <summary>What the town wants today, how much of it came already and what it pays a piece.</summary>
    public async Task<IReadOnlyList<(string Item, long Wanted, long Delivered, long PriceCents)>> NeedsAsync(
        Town town, int level, TownPolicy policy, CancellationToken cancellationToken)
    {
        var day = CityRules.DayOf(time.GetUtcNow());
        var delivered = await DayDeliveriesAsync(town, day, cancellationToken);
        var festival = await FestivalUntilAsync(town, cancellationToken) > time.GetUtcNow();
        var hall = 1 + TownHallRules.MarketHallPremium * await InvestmentAsync(town, "market_hall", cancellationToken);
        return CityRules.Needs(town, level, day, item => prices.BuysCents(item) is not null)
            .Select(n => (n.Item, n.Wanted, delivered.GetValueOrDefault(n.Item),
                BelowTrader(n.Item, (long)Math.Round(CityRules.NeedPriceCents(prices.BuysCents(n.Item)!.Value, level, policy.Bonus == "demand", festival) * hall))))
            .ToList();
    }

    /// <summary>What the town pays a piece, less than the trader asks for it even in a sale: the town pays from nothing, and
    /// goods bought from the trader to deliver here must not pay.</summary>
    private long BelowTrader(string item, long priceCents) =>
        prices.CheapestSellsCents(item) is { } cheapest ? Math.Min(priceCents, cheapest * CityRules.MaxPayOfChargePercent / 100) : priceCents;

    /// <summary>When the town's festival ends, or null when it never held one.</summary>
    public async Task<DateTimeOffset?> FestivalUntilAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT until FROM world.town_festivals WHERE town_x = @x AND town_y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        return await command.ExecuteScalarAsync(cancellationToken) is DateTimeOffset until ? until : null;
    }

    /// <summary>The mayor holds a festival: <see cref="CityRules.FestivalDishes"/> fine dishes from their inventory, roasts first, and the town
    /// pays more for what it wants for <see cref="CityRules.FestivalTime"/> (from when the one going on ends).</summary>
    public Task<DateTimeOffset> FestivalAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            if ((await PolicyAsync(town, cancellationToken)).Mayor != player)
            {
                throw new GameException("not_mayor", "Only this week's mayor holds a festival.");
            }

            var roasts = await inventory.CountAsync(player, Items.RoastBeef, cancellationToken);
            var cakes = await inventory.CountAsync(player, Items.Cheesecake, cancellationToken);
            if (roasts + cakes < CityRules.FestivalDishes)
            {
                throw new GameException("banquet_needed", $"A festival takes {CityRules.FestivalDishes} fine dishes: roast beef or cheesecake.");
            }

            var fromRoasts = Math.Min(roasts, CityRules.FestivalDishes);
            if (fromRoasts > 0)
            {
                await inventory.RemoveAsync(player, Items.RoastBeef, fromRoasts, cancellationToken);
            }

            if (CityRules.FestivalDishes - fromRoasts > 0)
            {
                await inventory.RemoveAsync(player, Items.Cheesecake, CityRules.FestivalDishes - fromRoasts, cancellationToken);
            }

            var now = time.GetUtcNow();
            var until = (await FestivalUntilAsync(town, cancellationToken) is { } going && going > now ? going : now) + CityRules.FestivalTime;
            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO world.town_festivals (town_x, town_y, until) VALUES (@x, @y, @until)
                ON CONFLICT (town_x, town_y) DO UPDATE SET until = @until
                """, cancellationToken);
            upsert.Parameters.AddWithValue("x", town.X);
            upsert.Parameters.AddWithValue("y", town.Y);
            upsert.Parameters.AddWithValue("until", until);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
            await progress.AddXpAsync(player, (int)CityRules.FestivalDishes, cancellationToken);
            return until;
        }, cancellationToken);

    private async Task<Dictionary<string, long>> DayDeliveriesAsync(Town town, int day, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT item, quantity FROM world.town_deliveries WHERE town_x = @x AND town_y = @y AND day = @day", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("day", day);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var delivered = new Dictionary<string, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            delivered[reader.GetString(0)] = reader.GetInt64(1);
        }

        return delivered;
    }

    private async Task AddDayDeliveryAsync(Town town, int day, string item, long quantity, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.town_deliveries (town_x, town_y, day, item, quantity) VALUES (@x, @y, @day, @item, @quantity)
            ON CONFLICT (town_x, town_y, day, item) DO UPDATE SET quantity = world.town_deliveries.quantity + excluded.quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("quantity", quantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One town at a time changes, so two deliveries cannot both fill the same need.</summary>
    private async Task LockTownAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken);
        command.Parameters.AddWithValue("key", 0x746f776eL << 32 | (long)(town.X & 0xffff) << 16 | (long)(town.Y & 0xffff));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Delivers goods the town wants, from the inventory or from the station of the city plot: the town pays for as many as it still
    /// wants today, grows on them, and the player gets experience.</summary>
    public Task<(long Quantity, long Cents, long Subsidy, XpGain Xp, int Level)> DeliverAsync(
        Guid player, int x, int y, string item, long quantity, bool fromStation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireTown(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, fromStation ? PlotRight.Take : PlotRight.Work);
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Deliver at least one.");
            }

            await LockTownAsync(town, cancellationToken);
            var before = CityRules.Level(await DeliveredAsync(town, cancellationToken));
            var policy = await PolicyAsync(town, cancellationToken);
            var need = (await NeedsAsync(town, before, policy, cancellationToken)).FirstOrDefault(n => n.Item == item);
            if (need.Item is null)
            {
                throw new GameException("not_wanted", "The town does not want this today.");
            }

            var taken = Math.Min(quantity, need.Wanted - need.Delivered);
            if (taken <= 0)
            {
                throw new GameException("need_met", "The town has got all it wants of this today.");
            }

            if (fromStation)
            {
                await StationStock.ChangeAsync(db, x, y, item, -taken, cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(player, item, taken, cancellationToken);
            }

            // The town's chief supplier of last week gets a little more for everything it wants.
            var price = await SupplierAsync(town, CityRules.WeekOf(time.GetUtcNow()), cancellationToken) == player
                ? BelowTrader(item, CityRules.SupplierPriceCents(need.PriceCents))
                : need.PriceCents;
            var cents = taken * price;
            await AddDayDeliveryAsync(town, CityRules.DayOf(time.GetUtcNow()), item, taken, cancellationToken);
            await GrowAsync(town, player, cents, cancellationToken);
            await wallet.CreditAsync(player, cents, $"town {town.Name}: {taken} {item}", cancellationToken);
            var xp = await progress.AddXpAsync(player, Math.Max(1, cents / 1000), cancellationToken);
            // The country's subsidy for the goods, if its government pays one, from its treasury.
            var subsidy = countries.At(x, y) is { } country ? await nation.SubsidizeAsync(player, country.Code, item, cents, cancellationToken) : 0;
            return (taken, cents, subsidy, xp, CityRules.Level(await DeliveredAsync(town, cancellationToken)));
        }, cancellationToken);

    /// <summary>Takes rubbish away from the town into the city plot's station, for a fee the town pays; the town has only so much a day.</summary>
    public Task<(long Quantity, long Cents)> TakeTrashAsync(Guid player, int x, int y, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireTown(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Take at least one.");
            }

            await LockTownAsync(town, cancellationToken);
            var day = CityRules.DayOf(time.GetUtcNow());
            var left = CityRules.TrashPerDay(town, CityRules.Level(await DeliveredAsync(town, cancellationToken)))
                - (await DayDeliveriesAsync(town, day, cancellationToken)).GetValueOrDefault(Items.Trash);
            var taken = Math.Min(quantity, left);
            if (taken <= 0)
            {
                throw new GameException("no_trash", "The town has no more rubbish today.");
            }

            await AddDayDeliveryAsync(town, day, Items.Trash, taken, cancellationToken);
            await StationStock.ChangeAsync(db, x, y, Items.Trash, taken, cancellationToken);
            var cents = taken * CityRules.TrashFeeCents;
            // With the recycling ordinance the treasury adds half again, as far as it has money.
            if (await InForceAsync(town, "recycling", cancellationToken))
            {
                var extra = Math.Min(cents * TownHallRules.RecyclingPercent / 100, await TreasuryAsync(town, cancellationToken));
                await ChangeTreasuryAsync(town, -extra, "program recyklingu", cancellationToken);
                cents += extra;
            }

            await wallet.CreditAsync(player, cents, $"town {town.Name}: {taken} rubbish taken away", cancellationToken);
            return (taken, cents);
        }, cancellationToken);

    /// <summary>The offices in the plots of a town that sell services.</summary>
    private async Task<IReadOnlyList<(Guid Id, int X, int Y, string Kind, Guid Owner)>> ServicesOfAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT id, x, y, kind, owner_id FROM world.plot_objects
            WHERE kind = ANY(@kinds) AND x BETWEEN @x - 40 AND @x + 40 AND y BETWEEN @y - 40 AND @y + 40
            ORDER BY kind, id
            """, cancellationToken);
        command.Parameters.AddWithValue("kinds", CityRules.Services.Keys.ToArray());
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<(Guid, int, int, string, Guid)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add((reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetGuid(4)));
        }

        return found.Where(s => TownOf(s.Item2, s.Item3) == town).ToList();
    }

    private async Task<int> SalesTodayAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT count FROM world.service_sales WHERE id = @id AND day = @day", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("day", CityRules.DayOf(time.GetUtcNow()));
        return await command.ExecuteScalarAsync(cancellationToken) is int count ? count : 0;
    }

    /// <summary>Pays an owner what came in, less the mayor's tax (unless the mayor is the one who paid or got paid), which goes into
    /// the treasury, all but the mayor's pay.</summary>
    private async Task PayAsync(Town town, Guid owner, Guid? payer, long cents, string reason, CancellationToken cancellationToken)
    {
        if (cents <= 0)
        {
            return;
        }

        var policy = await PolicyAsync(town, cancellationToken);
        var tax = policy.Mayor is { } mayor && mayor != owner && mayor != payer ? cents * policy.TaxPercent / 100 : 0;
        await wallet.CreditAsync(owner, cents - tax, reason, cancellationToken);
        var pay = tax * TownHallRules.MayorSharePercent / 100;
        if (pay > 0)
        {
            await wallet.CreditAsync(policy.Mayor!.Value, pay, $"mayor's pay in {town.Name}: {reason}", cancellationToken);
        }

        await ChangeTreasuryAsync(town, tax - pay, $"podatek: {reason}", cancellationToken);
    }

    /// <summary>Buys an office's service: the buyer pays, its owner gets a part (less the mayor's tax), and the buyer gets what it makes.</summary>
    public Task<(string Item, int Quantity, long Cents)> BuyServiceAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireTown(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var office = (await ServicesOfAsync(town, cancellationToken)).FirstOrDefault(s => s.Id == id);
            if (office.Kind is null)
            {
                throw new GameException("no_service", "There is no such office in this town.");
            }

            if (office.Owner == player)
            {
                throw new GameException("own_service", "This office is yours: make it yourself.");
            }

            await using (var lockRow = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockRow.Parameters.AddWithValue("key", BitConverter.ToInt64(id.ToByteArray()));
                await lockRow.ExecuteNonQueryAsync(cancellationToken);
            }

            var limit = await ServiceSalesPerDayAsync(town, cancellationToken);
            if (await SalesTodayAsync(id, cancellationToken) >= limit)
            {
                throw new GameException("service_busy", $"An office serves {limit} customers a day; come back tomorrow.");
            }

            var (item, quantity, price) = CityRules.Services[office.Kind];
            await wallet.DebitAsync(player, price, $"service {office.Kind} in {town.Name}", cancellationToken);
            await inventory.ReceiveAsync(player, item, quantity, cancellationToken);
            await PayAsync(town, office.Owner, player, price * CityRules.ServiceOwnerPercent / 100, $"service {office.Kind} sold", cancellationToken);
            await using (var count = await db.CommandAsync(
                """
                INSERT INTO world.service_sales (id, day, count) VALUES (@id, @day, 1)
                ON CONFLICT (id, day) DO UPDATE SET count = world.service_sales.count + 1
                """, cancellationToken))
            {
                count.Parameters.AddWithValue("id", id);
                count.Parameters.AddWithValue("day", CityRules.DayOf(time.GetUtcNow()));
                await count.ExecuteNonQueryAsync(cancellationToken);
            }

            return (item, quantity, price);
        }, cancellationToken);

    private async Task<int> ServiceSalesPerDayAsync(Town town, CancellationToken cancellationToken) =>
        await InForceAsync(town, "busy_offices", cancellationToken) ? TownHallRules.BusyOfficeSales : CityRules.ServiceSalesPerDay;

    /// <summary>The players who own a plot in the town's county: they elect its mayor and vote on its ordinances.</summary>
    private async Task<IReadOnlyList<Guid>> TownsfolkAsync(Town town, CancellationToken cancellationToken)
    {
        var (left, top, right, bottom) = towns.CountyBounds(town);
        await using var command = await db.CommandAsync(
            "SELECT x, y, owner_id FROM world.plots WHERE x BETWEEN @left AND @right AND y BETWEEN @top AND @bottom", cancellationToken);
        command.Parameters.AddWithValue("left", Math.Min(left, town.X));
        command.Parameters.AddWithValue("right", Math.Max(right, town.X));
        command.Parameters.AddWithValue("top", Math.Min(top, town.Y));
        command.Parameters.AddWithValue("bottom", Math.Max(bottom, town.Y));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var owners = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (CountyOf(reader.GetInt32(0), reader.GetInt32(1)) == town)
            {
                owners.Add(reader.GetGuid(2));
            }
        }

        return owners.Distinct().ToList();
    }

    /// <summary>Votes for next week's mayor: a player who owns a plot in the town votes for one who does too, once a week (a new vote replaces the old).</summary>
    public Task VoteAsync(Guid player, int x, int y, string candidate, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            var folk = await TownsfolkAsync(town, cancellationToken);
            if (!folk.Contains(player))
            {
                throw new GameException("not_townsman", "Only players who own a plot in the town's county vote here.");
            }

            var chosen = await names.AccountAsync(candidate ?? "", cancellationToken);
            if (chosen is not { } id || !folk.Contains(id))
            {
                throw new GameException("not_candidate", "Vote for a player who owns a plot in the town.");
            }

            await using var vote = await db.CommandAsync(
                """
                INSERT INTO world.town_votes (town_x, town_y, week, voter_id, candidate_id) VALUES (@x, @y, @week, @voter, @candidate)
                ON CONFLICT (town_x, town_y, week, voter_id) DO UPDATE SET candidate_id = excluded.candidate_id
                """, cancellationToken);
            vote.Parameters.AddWithValue("x", town.X);
            vote.Parameters.AddWithValue("y", town.Y);
            vote.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
            vote.Parameters.AddWithValue("voter", player);
            vote.Parameters.AddWithValue("candidate", id);
            await vote.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>The mayor sets this week's tax on shops and services, the town's bonus and, unless null, the tax on the county's plots.</summary>
    public Task SetPolicyAsync(Guid player, int x, int y, int taxPercent, string? bonus, int? plotTaxPerMille, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            if ((await PolicyAsync(town, cancellationToken)).Mayor != player)
            {
                throw new GameException("not_mayor", "Only this week's mayor sets the town's policy.");
            }

            if (taxPercent < 0 || taxPercent > CityRules.MaxTaxPercent)
            {
                throw new GameException("bad_tax", $"The tax is 0-{CityRules.MaxTaxPercent}%.");
            }

            if (plotTaxPerMille is < 0 or > CityRules.MaxPlotTaxPerMille)
            {
                throw new GameException("bad_plot_tax", $"The plot tax is 0-{CityRules.MaxPlotTaxPerMille} per mille a day.");
            }

            if (bonus is not null && !CityRules.Bonuses.ContainsKey(bonus))
            {
                throw new GameException("bad_bonus", "There is no such bonus.");
            }

            await using var update = await db.CommandAsync(
                """
                UPDATE world.town_policies SET tax_percent = @tax, bonus = @bonus, plot_tax_per_mille = COALESCE(@plot_tax, plot_tax_per_mille)
                WHERE town_x = @x AND town_y = @y AND week = @week
                """, cancellationToken);
            update.Parameters.AddWithValue("plot_tax", NpgsqlDbType.Integer, (object?)plotTaxPerMille ?? DBNull.Value);
            update.Parameters.AddWithValue("x", town.X);
            update.Parameters.AddWithValue("y", town.Y);
            update.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
            update.Parameters.AddWithValue("tax", taxPercent);
            update.Parameters.AddWithValue("bonus", (object?)bonus ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Everything the town window shows, from any plot of the town's county.</summary>
    public async Task<object> ViewAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        var town = RequireCounty(x, y);
        await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
        var now = time.GetUtcNow();
        var delivered = await DeliveredAsync(town, cancellationToken);
        var level = CityRules.Level(delivered);
        var policy = await PolicyAsync(town, cancellationToken);
        var needs = await NeedsAsync(town, level, policy, cancellationToken);
        var day = CityRules.DayOf(now);
        var trashTaken = (await DayDeliveriesAsync(town, day, cancellationToken)).GetValueOrDefault(Items.Trash);

        var contributors = new List<(Guid Id, long Cents)>();
        long mine = 0;
        await using (var command = await db.CommandAsync(
            """
            SELECT player_id, delivered_cents FROM world.town_contributions WHERE town_x = @x AND town_y = @y
            ORDER BY delivered_cents DESC, player_id LIMIT 5;
            SELECT delivered_cents FROM world.town_contributions WHERE town_x = @x AND town_y = @y AND player_id = @player
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("player", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                contributors.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                mine = reader.GetInt64(0);
            }
        }

        var offices = await ServicesOfAsync(town, cancellationToken);
        var salesLimit = await ServiceSalesPerDayAsync(town, cancellationToken);
        var sales = new Dictionary<Guid, int>();
        foreach (var office in offices)
        {
            sales[office.Id] = await SalesTodayAsync(office.Id, cancellationToken);
        }

        var folk = await TownsfolkAsync(town, cancellationToken);
        var votes = new Dictionary<Guid, int>();
        Guid? myVote = null;
        await using (var command = await db.CommandAsync(
            "SELECT voter_id, candidate_id FROM world.town_votes WHERE town_x = @x AND town_y = @y AND week = @week", cancellationToken))
        {
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("week", CityRules.WeekOf(now));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var candidate = reader.GetGuid(1);
                votes[candidate] = votes.GetValueOrDefault(candidate) + 1;
                if (reader.GetGuid(0) == player)
                {
                    myVote = candidate;
                }
            }
        }

        var week = CityRules.WeekOf(now);
        var supplier = await SupplierAsync(town, week, cancellationToken);
        var race = await WeekSuppliersAsync(town, week, CityRules.SupplierRaceShown, cancellationToken);
        var myWeek = await WeekDeliveredAsync(town, week, player, cancellationToken);
        var tender = await TenderViewAsync(town, level, player, folk.Contains(player), cancellationToken);
        var named = await names.NamesAsync(
            contributors.Select(c => c.Id).Concat(offices.Select(o => o.Owner)).Concat(folk).Concat(policy.Mayor is { } m ? [m] : [])
                .Concat(supplier is { } s ? [s] : []).Concat(race.Select(r => r.Player)).Concat(tender.People).Distinct().ToList(),
            cancellationToken);
        string Name(Guid id) => named.GetValueOrDefault(id, "?");
        return new
        {
            town = new
            {
                name = town.Name,
                x = town.X,
                y = town.Y,
                population = town.Population,
                level,
                levelName = CityRules.LevelNames[level],
                levelNames = CityRules.LevelNames,
                deliveredCents = delivered,
                levelCents = CityRules.LevelCents,
                distance = Math.Round(CityRules.Distance(map.Width, x, y, town.X, town.Y), 1),
                // False on a plot of the county outside the city: the town hall works from there, deliveries, rubbish and services do not.
                inCity = TownOf(x, y) is not null,
            },
            dayEndsAt = CityRules.DayStart(day + 1).ToUnixTimeMilliseconds(),
            needs = needs.Select(n => new
            {
                id = n.Item,
                name = Items.Names.GetValueOrDefault(n.Item, n.Item),
                wanted = n.Wanted,
                delivered = n.Delivered,
                priceCents = n.PriceCents,
            }),
            contributors = contributors.Select(c => new { name = Name(c.Id), cents = c.Cents, mine = c.Id == player }),
            myCents = mine,
            // The chief supplier: who delivered the most last week holds the title this week and gets supplierPercent more for deliveries.
            supplier = new
            {
                name = supplier is { } chief ? Name(chief) : null,
                isMe = supplier == player,
                percent = CityRules.SupplierBonusPercent,
                minCents = CityRules.SupplierMinCents,
                weekEndsAt = CityRules.WeekStart(week + 1).ToUnixTimeMilliseconds(),
                race = race.Select((r, i) => new { place = i + 1, name = Name(r.Player), cents = r.Cents, mine = r.Player == player }),
                myCents = myWeek,
            },
            tender = tender.Json(Name),
            trash = new
            {
                perDay = CityRules.TrashPerDay(town, level),
                taken = trashTaken,
                feeCents = CityRules.TrashFeeCents,
            },
            services = offices.Select(o => new
            {
                id = o.Id,
                kind = o.Kind,
                name = Items.Names[o.Kind],
                owner = Name(o.Owner),
                own = o.Owner == player,
                item = CityRules.Services[o.Kind].Item,
                itemName = Items.Names[CityRules.Services[o.Kind].Item],
                quantity = CityRules.Services[o.Kind].Quantity,
                priceCents = CityRules.Services[o.Kind].PriceCents,
                left = salesLimit - sales[o.Id],
            }),
            hall = new
            {
                week,
                weekEndsAt = CityRules.WeekStart(week + 1).ToUnixTimeMilliseconds(),
                mayor = policy.Mayor is { } mayor ? Name(mayor) : null,
                isMayor = policy.Mayor == player,
                taxPercent = policy.TaxPercent,
                maxTaxPercent = CityRules.MaxTaxPercent,
                plotTaxPerMille = policy.PlotTaxPerMille,
                maxPlotTaxPerMille = CityRules.MaxPlotTaxPerMille,
                bonus = policy.Bonus,
                bonuses = CityRules.Bonuses.Select(b => new { id = b.Key, name = b.Value }),
                // A festival the mayor holds with fine dishes: the town pays festivalPercent more while it lasts (Unix milliseconds).
                festivalUntil = await FestivalUntilAsync(town, cancellationToken) is { } festival && festival > now ? festival.ToUnixTimeMilliseconds() : (long?)null,
                festivalDishes = CityRules.FestivalDishes,
                festivalPercent = (int)Math.Round((CityRules.FestivalBonus - 1) * 100),
                festivalHours = CityRules.FestivalTime.TotalHours,
                canVote = folk.Contains(player),
                myVote = myVote is { } v ? Name(v) : null,
                candidates = folk.Select(f => new { name = Name(f), votes = votes.GetValueOrDefault(f), me = f == player })
                    .OrderByDescending(c => c.votes).ThenBy(c => c.name),
                politics = await HallPoliticsAsync(player, town, policy, folk, votes, cancellationToken),
            },
        };
    }

    // --- Shops ---------------------------------------------------------------------------------------

    /// <summary>A shop's plot, owner and kind, or a failure when the id is no shop.</summary>
    private async Task<(int X, int Y, Guid Owner)> ShopAtAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT x, y, owner_id, kind FROM world.plot_objects WHERE id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(3) != Items.Shop)
        {
            throw new GameException("no_shop", "There is no such shop.");
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2));
    }

    private async Task<List<ShopItemRow>> ShopItemsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT item, quantity, price_cents FROM world.shop_items WHERE shop_id = @id ORDER BY item", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ShopItemRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShopItemRow { ShopId = id, Item = reader.GetString(0), Quantity = reader.GetInt64(1), PriceCents = reader.GetInt64(2) });
        }

        return items;
    }

    private async Task SaveShopItemAsync(ShopItemRow row, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            row.Quantity > 0
                ? """
                  INSERT INTO world.shop_items (shop_id, item, quantity, price_cents) VALUES (@id, @item, @quantity, @price)
                  ON CONFLICT (shop_id, item) DO UPDATE SET quantity = excluded.quantity, price_cents = excluded.price_cents
                  """
                : "DELETE FROM world.shop_items WHERE shop_id = @id AND item = @item",
            cancellationToken);
        command.Parameters.AddWithValue("id", row.ShopId);
        command.Parameters.AddWithValue("item", row.Item);
        command.Parameters.AddWithValue("quantity", row.Quantity);
        command.Parameters.AddWithValue("price", row.PriceCents);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<double> CustomersAsync(int x, int y, CancellationToken cancellationToken)
    {
        var town = towns.Of(x, y);
        var level = CityRules.Level(await DeliveredAsync(town, cancellationToken));
        var shoppers = (await PolicyAsync(town, cancellationToken)).Bonus == "shoppers";
        var hall = (1 + TownHallRules.ParkCustomers * await InvestmentAsync(town, "park", cancellationToken))
            * (await InForceAsync(town, "late_shops", cancellationToken) ? TownHallRules.LateShops : 1);
        return CityRules.Customers(town, level, CityRules.Distance(map.Width, x, y, town.X, town.Y), shoppers) * hall;
    }

    /// <summary>Works out what the townspeople bought in the shop since it was last looked at: each customer picks one of its
    /// goods and buys a piece if the price is right. The money goes to the owner, less the mayor's tax.</summary>
    private async Task<(Guid Owner, int X, int Y)> SettleAsync(Guid id, CancellationToken cancellationToken)
    {
        var (x, y, owner) = await ShopAtAsync(id, cancellationToken);
        var now = time.GetUtcNow();
        await using (var create = await db.CommandAsync(
            "INSERT INTO world.shops (id, settled_at, carry, sold_cents, sold_items) VALUES (@id, @now, 0, 0, 0) ON CONFLICT DO NOTHING", cancellationToken))
        {
            create.Parameters.AddWithValue("id", id);
            create.Parameters.AddWithValue("now", now);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTimeOffset settled;
        double carry;
        await using (var read = await db.CommandAsync("SELECT settled_at, carry FROM world.shops WHERE id = @id FOR UPDATE", cancellationToken))
        {
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (settled, carry) = (reader.GetFieldValue<DateTimeOffset>(0), reader.GetDouble(1));
        }

        if (now <= settled)
        {
            return (owner, x, y);
        }

        var stock = await ShopItemsAsync(id, cancellationToken);
        var hours = (now - settled < CityRules.ShopCatchUp ? now - settled : CityRules.ShopCatchUp).TotalHours;
        var expected = carry + (stock.Count > 0 ? (await CustomersAsync(x, y, cancellationToken)) * hours : 0);
        var customers = (long)Math.Floor(expected);
        carry = expected - customers;
        var random = new Random(HashCode.Combine(id, settled.UtcTicks));
        long revenue = 0, sold = 0;
        var changed = new HashSet<ShopItemRow>();
        for (var i = 0; i < customers; i++)
        {
            var shelf = stock.Where(s => s.Quantity > 0).ToList();
            if (shelf.Count == 0)
            {
                carry = 0;
                break;
            }

            var pick = shelf[random.Next(shelf.Count)];
            var trader = prices.BuysCents(pick.Item) ?? 0;
            if (trader > 0 && random.NextDouble() < CityRules.Accepts(pick.PriceCents, trader, prices.CheapestSellsCents(pick.Item)))
            {
                pick.Quantity--;
                revenue += pick.PriceCents;
                sold++;
                changed.Add(pick);
            }
        }

        foreach (var row in changed)
        {
            await SaveShopItemAsync(row, cancellationToken);
        }

        await using (var update = await db.CommandAsync(
            """
            UPDATE world.shops SET settled_at = @now, carry = @carry, sold_cents = sold_cents + @revenue, sold_items = sold_items + @sold
            WHERE id = @id
            """, cancellationToken))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("carry", carry);
            update.Parameters.AddWithValue("revenue", revenue);
            update.Parameters.AddWithValue("sold", sold);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await PayAsync(towns.Of(x, y), owner, null, revenue, $"shop sales: {sold} items", cancellationToken);
        return (owner, x, y);
    }

    private async Task<object> ShopStateAsync(Guid id, int x, int y, CancellationToken cancellationToken)
    {
        var stock = await ShopItemsAsync(id, cancellationToken);
        long soldCents = 0, soldItems = 0;
        await using (var command = await db.CommandAsync("SELECT sold_cents, sold_items FROM world.shops WHERE id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                (soldCents, soldItems) = (reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        var town = towns.Of(x, y);
        return new
        {
            id,
            town = town.Name,
            customersPerHour = Math.Round(await CustomersAsync(x, y, cancellationToken), 1),
            soldCents,
            soldItems,
            maxKinds = CityRules.ShopKinds,
            maxStock = CityRules.ShopMaxStock,
            taxPercent = (await PolicyAsync(town, cancellationToken)).TaxPercent,
            items = stock.Select(s => new
            {
                id = s.Item,
                name = Items.Names.GetValueOrDefault(s.Item, s.Item),
                quantity = s.Quantity,
                priceCents = s.PriceCents,
                traderCents = prices.BuysCents(s.Item),
                buyPercent = prices.BuysCents(s.Item) is { } trader ? (int)Math.Round(100 * CityRules.Accepts(s.PriceCents, trader, prices.CheapestSellsCents(s.Item))) : 0,
            }),
        };
    }

    public Task<object> ShopAsync(Guid player, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (_, x, y) = await SettleAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            return await ShopStateAsync(id, x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts goods from the inventory on the shop's shelves at a price (which then holds for all of that item there).</summary>
    public Task<object> StockShopAsync(Guid player, Guid id, string item, long quantity, long priceCents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (_, x, y) = await SettleAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            if (prices.BuysCents(item ?? "") is null)
            {
                throw new GameException("not_sellable", "Nobody in town buys this.");
            }

            if (priceCents < 1)
            {
                throw new GameException("bad_price", "Set a price.");
            }

            var stock = await ShopItemsAsync(id, cancellationToken);
            var row = stock.FirstOrDefault(s => s.Item == item);
            if (row is null && stock.Count >= CityRules.ShopKinds)
            {
                throw new GameException("shop_full", $"A shop holds {CityRules.ShopKinds} kinds of goods.");
            }

            row ??= new ShopItemRow { ShopId = id, Item = item! };
            if (quantity < 0 || row.Quantity + quantity > CityRules.ShopMaxStock)
            {
                throw new GameException("bad_quantity", $"A shop holds at most {CityRules.ShopMaxStock} of each.");
            }

            if (quantity > 0)
            {
                await inventory.RemoveAsync(player, item!, quantity, cancellationToken);
            }

            row.Quantity += quantity;
            row.PriceCents = priceCents;
            if (row.Quantity == 0)
            {
                throw new GameException("bad_quantity", "Put at least one on the shelf.");
            }

            await SaveShopItemAsync(row, cancellationToken);
            return await ShopStateAsync(id, x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes goods off the shop's shelves back into the inventory, as many as it has room for; the rest stays.</summary>
    public Task<object> TakeFromShopAsync(Guid player, Guid id, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (_, x, y) = await SettleAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            var row = (await ShopItemsAsync(id, cancellationToken)).FirstOrDefault(s => s.Item == item);
            if (row is null || quantity < 1 || quantity > row.Quantity)
            {
                throw new GameException("bad_quantity", "The shop has not so many.");
            }

            var taken = await inventory.FitAsync(player, item, quantity, cancellationToken);
            row.Quantity -= taken;
            await SaveShopItemAsync(row, cancellationToken);
            await inventory.AddAsync(player, item, taken, cancellationToken);
            return await ShopStateAsync(id, x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>A shop picked up (it is gone from the plot by now): what is left on its shelves goes back to the owner.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        foreach (var row in await ShopItemsAsync(id, cancellationToken))
        {
            await inventory.AddAsync(owner, row.Item, row.Quantity, cancellationToken);
        }

        await using var delete = await db.CommandAsync(
            "DELETE FROM world.shop_items WHERE shop_id = @id; DELETE FROM world.shops WHERE id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    // --- What cities change elsewhere -----------------------------------------------------------------

    /// <summary>What rail fees from a plot are multiplied by: less with the country's railway, in a city, with a freight terminal,
    /// and anywhere in a town's county with the town's station and the mayor's bonus.</summary>
    public async Task<double> RailFactorAsync(int x, int y, CancellationToken cancellationToken)
    {
        var national = countries.At(x, y) is { } country ? await nation.RailFactorAsync(country.Code, cancellationToken) : 1;
        if (CountyOf(x, y) is not { } town)
        {
            return national;
        }

        var factor = national * (1 - TownHallRules.StationRail * await InvestmentAsync(town, "station", cancellationToken));
        if (TownOf(x, y) is not null)
        {
            factor *= CityRules.CityRail;
            if (await beach.HasAsync(x, y, Items.FreightTerminal, 0, cancellationToken))
            {
                factor *= CityRules.TerminalRail;
            }
        }

        if ((await PolicyAsync(town, cancellationToken)).Bonus == "rail")
        {
            factor *= CityRules.MayorRail;
        }

        return factor;
    }

    public async Task<int> MaxRoutesAsync(int x, int y, CancellationToken cancellationToken) =>
        TownOf(x, y) is not null && await beach.HasAsync(x, y, Items.FreightTerminal, 0, cancellationToken) ? CityRules.TerminalRoutes : RailRules.MaxRoutes;

    /// <summary>What the price of a plot in a town's county is multiplied by: less with the mayor's "plots" bonus and with the
    /// settlers' ordinance.</summary>
    public async Task<double> PlotPriceFactorAsync(int x, int y, CancellationToken cancellationToken) =>
        CountyOf(x, y) is { } town
            ? ((await PolicyAsync(town, cancellationToken)).Bonus == "plots" ? CityRules.CheapPlots : 1)
                * (await InForceAsync(town, "settlers", cancellationToken) ? TownHallRules.SettlersPlots : 1)
            : 1;
}

public sealed record CityRequest(int X, int Y);

public sealed record DeliverRequest(int X, int Y, string Item, long Quantity, bool FromStation = false);

public sealed record TrashRequest(int X, int Y, long Quantity);

public sealed record ServiceRequest(int X, int Y, Guid Id);

public sealed record VoteRequest(int X, int Y, string Candidate);

public sealed record PolicyRequest(int X, int Y, int TaxPercent, string? Bonus, int? PlotTaxPerMille = null);

public sealed record ShopRequest(Guid Id, string? Item = null, long Quantity = 0, long PriceCents = 0);

public abstract class CityHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var cities = scope.ServiceProvider.GetRequiredService<CityService>();
        var player = client.RequireAccount();
        var done = await RunAsync(cities, player, payload, cancellationToken);
        var at = Payload.Read<CityRequest>(payload);
        return new Reply("city", new { result = done, state = await cities.ViewAsync(player, at.X, at.Y, cancellationToken) });
    }

    protected abstract Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetCityHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.get";

    protected override Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(null);
}

public sealed class DeliverToCityHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.deliver";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<DeliverRequest>(payload);
        var (quantity, cents, subsidy, xp, level) = await cities.DeliverAsync(player, r.X, r.Y, r.Item ?? "", r.Quantity, r.FromStation, cancellationToken);
        return new { item = r.Item, quantity, cents, subsidyCents = subsidy, level, xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } };
    }
}

public sealed class CityTrashHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.trash";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<TrashRequest>(payload);
        var (quantity, cents) = await cities.TakeTrashAsync(player, r.X, r.Y, r.Quantity, cancellationToken);
        return new { quantity, cents };
    }
}

public sealed class CityServiceHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.service";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ServiceRequest>(payload);
        var (item, quantity, cents) = await cities.BuyServiceAsync(player, r.X, r.Y, r.Id, cancellationToken);
        return new { item, name = Items.Names[item], quantity, cents };
    }
}

public sealed class CityVoteHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.vote";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<VoteRequest>(payload);
        await cities.VoteAsync(player, r.X, r.Y, r.Candidate, cancellationToken);
        return null;
    }
}

public sealed class CityPolicyHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.policy";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<PolicyRequest>(payload);
        await cities.SetPolicyAsync(player, r.X, r.Y, r.TaxPercent, string.IsNullOrEmpty(r.Bonus) ? null : r.Bonus, r.PlotTaxPerMille, cancellationToken);
        return null;
    }
}

public abstract class ShopHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var cities = scope.ServiceProvider.GetRequiredService<CityService>();
        var request = Payload.Read<ShopRequest>(payload);
        return new Reply("shop", await RunAsync(cities, client.RequireAccount(), request, cancellationToken));
    }

    protected abstract Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken);
}

public sealed class GetShopHandler(IServiceScopeFactory scopes) : ShopHandler(scopes)
{
    public override string Type => "shop.get";

    protected override Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken) =>
        cities.ShopAsync(player, request.Id, cancellationToken);
}

public sealed class StockShopHandler(IServiceScopeFactory scopes) : ShopHandler(scopes)
{
    public override string Type => "shop.stock";

    protected override Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken) =>
        cities.StockShopAsync(player, request.Id, request.Item ?? "", request.Quantity, request.PriceCents, cancellationToken);
}

public sealed class TakeFromShopHandler(IServiceScopeFactory scopes) : ShopHandler(scopes)
{
    public override string Type => "shop.take";

    protected override Task<object> RunAsync(CityService cities, Guid player, ShopRequest request, CancellationToken cancellationToken) =>
        cities.TakeFromShopAsync(player, request.Id, request.Item ?? "", request.Quantity, cancellationToken);
}

public sealed class CityFestivalHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.festival";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<CityRequest>(payload);
        var until = await cities.FestivalAsync(player, r.X, r.Y, cancellationToken);
        return new { festivalUntil = until.ToUnixTimeMilliseconds() };
    }
}
