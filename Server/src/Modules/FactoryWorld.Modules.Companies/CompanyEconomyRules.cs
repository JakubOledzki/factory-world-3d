using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Companies;

/// <summary>
/// The company's cash and shares. Members pay into the cash for new shares at the price a share has then; selling from
/// the warehouse, orders and deals earn money for it, and once a day a part of what it earned since goes out to the
/// shareholders as a dividend. A share is worth the company's value split evenly: its cash, what lies in its warehouse
/// at the trader's prices, and ten times what it earned in the last week.
/// </summary>
public static class CompanyMoney
{
    /// <summary>What the founder holds from the start.</summary>
    public const long FounderShares = 1000;

    /// <summary>A share never costs less, so the first deposits into an empty company buy a sensible number.</summary>
    public const long MinSharePriceCents = 100;

    public const int DefaultDividendPercent = 20;

    /// <summary>Fine dishes (roast beef or cheesecake) a company's debut banquet takes from the warehouse when it first goes public.</summary>
    public const long ListingBanquet = 200;

    public static readonly TimeSpan DividendEvery = TimeSpan.FromDays(1);

    /// <summary>How many past days of earnings count towards the company's value.</summary>
    public const int ProfitDays = 7;

    public const int ProfitMultiple = 10;

    /// <summary>What the company pays back for a share sold to it, in percent of its price.</summary>
    public const int BuybackPercent = 95;

    /// <summary>The most one payment into or out of the cash may move.</summary>
    public const long MaxCents = 100_000_000_00;

    public static long Value(long cashCents, long warehouseCents, long profitCents) =>
        cashCents + warehouseCents + ProfitMultiple * Math.Max(0, profitCents);

    public static long SharePrice(long valueCents, long shares) =>
        shares <= 0 ? MinSharePriceCents : Math.Max(MinSharePriceCents, valueCents / shares);

    /// <summary>A payout split by shares, rounded down; what is left over stays in the cash.</summary>
    public static IReadOnlyDictionary<Guid, long> Split(long cents, IReadOnlyDictionary<Guid, long> shares)
    {
        var total = shares.Values.Sum();
        return total <= 0
            ? new Dictionary<Guid, long>()
            : shares.ToDictionary(s => s.Key, s => (long)(cents * (decimal)s.Value / total));
    }
}

/// <summary>A construction too big for one player: members bring thousands of parts, then it takes days to build.</summary>
/// <param name="Reward">What lands in the company's warehouse when it is built.</param>
/// <param name="Perk">What it gives every member from then on (<see cref="CompanyPerks"/>), if anything.</param>
/// <param name="Requires">The projects the company must have built before taking this one on.</param>
/// <param name="Wonder">A wonder of the world: only one company on the server builds it, the first to bring every part.</param>
public sealed record BigProject(
    string Id, string Name, string Description, IReadOnlyList<(string Item, long Quantity)> Parts, TimeSpan Build,
    IReadOnlyList<(string Item, int Quantity)> Reward, string? Perk, IReadOnlyList<string>? Requires = null, bool Wonder = false);

public static class BigProjects
{
    public static readonly IReadOnlyList<BigProject> All =
    [
        new("offshore_rig", "Platforma wiertnicza", "Dwie gotowe platformy wiertnicze trafiają do magazynu spółki.",
            [(Items.SteelIngot, 3000), (Items.Concrete, 2000), (Items.Pipe, 500), (Items.Gasket, 100), (Items.Provisions, 500)], TimeSpan.FromDays(2),
            [(Items.OffshorePlatform, 2)], null),
        new(CompanyPerks.Harbour, "Port spółki", "Statki członków spółki płacą za rejs między portami połowę opłaty.",
            [(Items.Concrete, 3000), (Items.Beams, 2000), (Items.SteelIngot, 1500), (Items.Tar, 300), (Items.Provisions, 1000)], TimeSpan.FromDays(2),
            [], CompanyPerks.Harbour),
        new("mirror_field", "Pole lustrzane", "Trzy elektrownie słoneczne z lustrami trafiają do magazynu spółki.",
            [(Items.Mirror, 1500), (Items.Glass, 3000), (Items.SteelIngot, 2000), (Items.Concrete, 1000), (Items.Provisions, 500)], TimeSpan.FromDays(3),
            [(Items.SolarTower, 3)], null),
        new(CompanyPerks.Railway, "Kolej międzykontynentalna", "Pociągi członków spółki płacą o 30% mniej, także na trasach.",
            [(Items.SteelIngot, 5000), (Items.Beams, 4000), (Items.Concrete, 3000), (Items.Diesel, 2000), (Items.Provisions, 1000)], TimeSpan.FromDays(4),
            [], CompanyPerks.Railway),

        // The space goals: the top of the crafting tree, built one after another.
        new(CompanyPerks.Spaceport, "Kosmodrom", "Wyrzutnia, z której spółka wyśle misję orbitalną.",
            [(Items.PrecastConcrete, 2000), (Items.RefractoryLining, 300), (Items.IndustrialRobot, 10), (Items.ControlModule, 20), (Items.Provisions, 2000)],
            TimeSpan.FromDays(5), [], CompanyPerks.Spaceport),
        new(CompanyPerks.Orbit, "Misja orbitalna", "Satelita prowadzi pociągi i statki członków spółki: płacą o 20% mniej na każdej trasie.",
            [(Items.LaunchRocket, 1), (Items.Satellite, 1), (Items.RocketFuel, 200), (Items.Provisions, 500)],
            TimeSpan.FromDays(3), [], CompanyPerks.Orbit, [CompanyPerks.Spaceport]),
        new(CompanyPerks.Station, "Stacja orbitalna", "Laboratorium na orbicie: badania członków spółki trwają o 20% krócej.",
            [(Items.LaunchRocket, 2), (Items.FusionReactor, 1), (Items.IndustrialRobot, 4), (Items.CryogenicTank, 4), (Items.Provisions, 1000)],
            TimeSpan.FromDays(7), [], CompanyPerks.Station, [CompanyPerks.Orbit]),

        // Wonders of the world: a race between companies, as only the first to bring every part builds one. The others get their parts back.
        new(CompanyPerks.Library, "Wielka Biblioteka", "Cud świata, jeden na serwerze: badania członków spółki trwają o 15% krócej.",
            [(Items.Paper, 20_000), (Items.ResearchData, 500), (Items.Concrete, 5000), (Items.Beams, 3000), (Items.Provisions, 2000)],
            TimeSpan.FromDays(3), [], CompanyPerks.Library, Wonder: true),
        new(CompanyPerks.Hub, "Światowy Węzeł Kolejowy", "Cud świata, jeden na serwerze: członkowie spółki płacą o 15% mniej na każdej trasie.",
            [(Items.SteelIngot, 10_000), (Items.Concrete, 8000), (Items.Beams, 5000), (Items.ElectricMotor, 500), (Items.Provisions, 3000)],
            TimeSpan.FromDays(4), [], CompanyPerks.Hub, Wonder: true),
        new(CompanyPerks.Colossus, "Kolos Przemysłu", "Cud świata, jeden na serwerze: członkowie spółki dostają o 10% więcej doświadczenia za wszystko.",
            [(Items.SteelIngot, 15_000), (Items.PrecastConcrete, 5000), (Items.IndustrialRobot, 30), (Items.ControlModule, 50), (Items.Provisions, 5000)],
            TimeSpan.FromDays(7), [], CompanyPerks.Colossus, Wonder: true),
    ];

    public static readonly IReadOnlyList<BigProject> Wonders = All.Where(p => p.Wonder).ToList();

    public static readonly IReadOnlyDictionary<string, BigProject> ById = All.ToDictionary(p => p.Id);
}

/// <summary>A large order from the trader that only a company takes: a few each week, delivered from its warehouse by the week's end.</summary>
/// <param name="RewardCents">Paid into the cash once the whole quantity is in.</param>
public sealed record CompanyOrder(int Slot, string Item, long Quantity, long RewardCents, DateTimeOffset EndsAt);

public static class CompanyOrders
{
    public const int PerWeek = 3;

    /// <summary>How much more than the trader's price an order pays.</summary>
    public const double RewardFactor = 1.6;

    private static readonly (string Item, long Quantity)[] Pool =
    [
        (Items.SteelIngot, 20_000), (Items.IronIngot, 30_000), (Items.CopperIngot, 20_000), (Items.Concrete, 10_000),
        (Items.Planks, 25_000), (Items.Bricks, 15_000), (Items.Glass, 10_000), (Items.Plastic, 5000), (Items.Bread, 8000),
        (Items.Paper, 8000), (Items.CannedFish, 4000), (Items.CircuitBoard, 1000), (Items.Chair, 1500), (Items.Gasoline, 10_000),
    ];

    /// <summary>Weeks start on Monday, 00:00 UTC; this is the first Monday after the Unix epoch.</summary>
    private const long FirstMonday = 4 * 86_400;

    private const long WeekSeconds = 7 * 86_400;

    public static int Week(DateTimeOffset at) => (int)Math.Floor((at.ToUnixTimeSeconds() - FirstMonday) / (double)WeekSeconds);

    public static DateTimeOffset WeekEnd(int week) => DateTimeOffset.FromUnixTimeSeconds(FirstMonday + (week + 1L) * WeekSeconds);

    /// <summary>The company's orders in a week: the same for everyone who asks, different for each company and week.</summary>
    public static IReadOnlyList<CompanyOrder> For(Guid company, int week, IMarketPrices prices)
    {
        var bytes = company.ToByteArray();
        var seed = BitConverter.ToUInt64(bytes, 0) ^ BitConverter.ToUInt64(bytes, 8) ^ (ulong)week * 0x9E3779B97F4A7C15UL;
        return Pool.Select((p, i) => (p.Item, p.Quantity, Order: Mix(seed + (ulong)i)))
            .Where(p => prices.BuysCents(p.Item) is not null)
            .OrderBy(p => p.Order)
            .Take(PerWeek)
            .Select((p, slot) => new CompanyOrder(slot, p.Item, p.Quantity,
                (long)Math.Ceiling(p.Quantity * prices.BuysCents(p.Item)!.Value * RewardFactor), WeekEnd(week)))
            .ToList();
    }

    public static ulong Mix(ulong x)
    {
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33;
        x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return x;
    }
}

/// <summary>Tasks for members: bring so many of an item into the warehouse for a wage, which is set aside from the cash and paid piece by piece.</summary>
public static class CompanyTasks
{
    public const int MaxOpen = 20;
}

/// <summary>
/// A standing deal between two companies: so many of an item a day for so much a day, for some days. Each day the
/// goods go from the supplier's warehouse to the buyer's, and the money the other way. A supplier that is short pays
/// the buyer half the day's price instead; a buyer that cannot pay ends the deal.
/// </summary>
public static class Deals
{
    public const int MaxDays = 30;

    public const long MaxPerDay = 10_000;

    public const int MissPenaltyPercent = 50;

    /// <summary>Most offers and running deals a company has at once.</summary>
    public const int MaxOpen = 10;

    public static readonly TimeSpan Day = TimeSpan.FromDays(1);
}

/// <summary>Monthly seasons: the companies that earned the most in a month win money for their cash and a trophy.</summary>
public static class Seasons
{
    public static readonly long[] PrizesCents = [50_000_00, 25_000_00, 10_000_00];

    /// <summary>The season of a moment, as yyyymm.</summary>
    public static int Of(DateTimeOffset at) => at.UtcDateTime.Year * 100 + at.UtcDateTime.Month;

    public static DateOnly Start(int season) => new(season / 100, season % 100, 1);

    public static DateOnly End(int season) => Start(season).AddMonths(1);

    public static int Previous(int season) => Of(new DateTimeOffset(Start(season).AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    /// <summary>How many companies each ranking lists.</summary>
    public const int Top = 10;
}
