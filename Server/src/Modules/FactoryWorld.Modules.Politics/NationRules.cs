using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Politics;

/// <summary>
/// The countries of the map, governed by their players. A player who owns a plot in a country may take its citizenship, and its
/// citizens elect a president for a term of two weeks: candidates stand with a programme (the plot tax and the fee foreigners pay
/// for plots) and a deposit. The president names ministers, each with one area, and sets the laws within fixed bounds: the plot
/// tax, the foreigners' fee, a tariff on goods coming in by rail (which the police collects), a subsidy on one group of goods
/// the country's towns buy. Half of the plot tax goes into the country's treasury (the rest is gone, as before), with the fees;
/// the president keeps a tenth of what comes in as pay, and the government spends the rest on the national railway, a housing
/// programme and the subsidies. Two thirds of the citizens recall a president. Countries ally (no fee and no tariff between them)
/// and put embargoes on each other (no plots for the other's citizens, no goods). Players join parties, whose names show in elections.
/// </summary>
public static class NationRules
{
    public static readonly TimeSpan Term = TimeSpan.FromDays(14);

    public static int TermOf(DateTimeOffset at) => (int)Math.Floor((at - SeaWeather.Epoch) / Term);

    public static DateTimeOffset TermStart(int term) => SeaWeather.Epoch + Term * term;

    /// <summary>A player changes citizenship at most this often.</summary>
    public static readonly TimeSpan CitizenshipChange = TimeSpan.FromDays(7);

    /// <summary>The plot tax a government sets, in thousandths of a plot's worth a day.</summary>
    public const int DefaultPlotTaxPerMille = 5;

    public const int MinPlotTaxPerMille = 3;

    public const int MaxPlotTaxPerMille = 7;

    /// <summary>The share of the plot tax that goes into the treasury of the country the plot lies in.</summary>
    public const int TreasurySharePercent = 50;

    /// <summary>The most a country charges foreigners on top of a plot's price.</summary>
    public const int MaxForeignerFeePercent = 20;

    /// <summary>The highest tariff on goods coming in from another country.</summary>
    public const int MaxTariffPercent = 30;

    /// <summary>A country sets its own tariff for this many items at most.</summary>
    public const int MaxItemTariffs = 20;

    /// <summary>The part of everything the treasury takes in that goes to the president as pay.</summary>
    public const int PresidentSharePercent = 10;

    public const long DepositCents = 10_000_00;

    public const int DepositBackPercent = 10;

    public const int MaxSloganLength = 80;

    public const long MaxSponsorCents = 1_000_000_00;

    /// <summary>Groups of goods a government subsidises: the country's towns pay <see cref="SubsidyPercent"/>% more for them, from the treasury.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, IReadOnlySet<string> Items)> Subsidies =
        new Dictionary<string, (string, IReadOnlySet<string>)>
        {
            ["food"] = ("Żywność", new HashSet<string>
            {
                Items.Bread, Items.Sauerkraut, Items.VegetableSoup, Items.ScrambledEggs, Items.Fries, Items.SmokedFish, Items.CannedFish, Items.Milk,
                Items.Egg, Items.CarrotCake, Items.Bigos, Items.FishSoup, Items.FriedCarp, Items.IceCream, Items.FrozenFish, Items.SaltedHerring,
                Items.CornBread, Items.SmokedSalmon, Items.FrozenSalmon, Items.Honey, Items.MushroomSoup, Items.BerryPierogi, Items.Gingerbread,
                Items.HerbalTea, Items.Cheese, Items.Butter, Items.Meat, Items.WorkersLunch, Items.RoastBeef, Items.Cheesecake,
            }),
            ["materials"] = ("Materiały budowlane i opał", new HashSet<string>
            {
                Items.Planks, Items.Bricks, Items.Coal, Items.GlassBottles, Items.WoodPellets, Items.WindowPane, Items.Concrete, Items.Cement,
                Items.Plywood, Items.Paper, Items.Charcoal, Items.Cardboard, Items.Turpentine,
            }),
            ["industry"] = ("Przemysł", new HashSet<string>
            {
                Items.Gasoline, Items.Diesel, Items.Plastic, Items.CircuitBoard, Items.ElectricMotor, Items.Tyres, Items.OpticalFiber, Items.Mirror,
            }),
            ["crafts"] = ("Rzemiosło: meble i ubrania", new HashSet<string>
            {
                Items.Wool, Items.Amber, Items.Chair, Items.Table, Items.Wardrobe, Items.OakDresser, Items.Candles, Items.Beanie, Items.StrawHat,
                Items.FlannelJacket, Items.RubberBoots, Items.SuitJacket, Items.DressShoes, Items.LeatherBoots,
            }),
        };

    public const int SubsidyPercent = 10;

    /// <summary>What the government builds with the treasury: name, what each level does, most levels, and the office that orders it.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Effect, int MaxLevel, string Office)> Investments =
        new Dictionary<string, (string, string, int, string)>
        {
            ["railway"] = ("Kolej państwowa", "opłaty kolejowe w całym kraju o 5% niższe na poziom", 3, CountryOffices.Transport),
            ["housing"] = ("Program mieszkaniowy", "działki w kraju o 5% tańsze dla obywateli na poziom", 3, CountryOffices.Finance),
        };

    public static readonly IReadOnlyList<long> InvestmentCents = [200_000_00, 600_000_00, 1_800_000_00];

    public const double RailwayPerLevel = 0.05;

    public const double HousingPerLevel = 0.05;

    public static readonly IReadOnlyDictionary<string, string> OfficeNames = new Dictionary<string, string>
    {
        [CountryOffices.President] = "Prezydent",
        [CountryOffices.Finance] = "Minister finansów",
        [CountryOffices.Industry] = "Minister przemysłu",
        [CountryOffices.Transport] = "Minister transportu",
        [CountryOffices.Foreign] = "Minister spraw zagranicznych",
        [CountryOffices.Interior] = "Minister spraw wewnętrznych",
        [CountryOffices.Justice] = "Minister sprawiedliwości",
    };

    /// <summary>The laws and which office sets each.</summary>
    public static readonly IReadOnlyDictionary<string, string> Laws = new Dictionary<string, string>
    {
        ["plotTax"] = CountryOffices.Finance,
        ["foreignerFee"] = CountryOffices.Finance,
        ["tariff"] = CountryOffices.Finance,
        ["itemTariff"] = CountryOffices.Finance,
        ["subsidy"] = CountryOffices.Industry,
    };

    /// <summary>Signatures that recall a president: two thirds of the citizens, and at least two.</summary>
    public static bool Recalls(int signatures, int citizens) => signatures >= 2 && signatures * 3 >= citizens * 2;

    public const long PartyCents = 500_00;

    public const int MinPartyName = 3;

    public const int MaxPartyName = 32;
}
