using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;

namespace FactoryWorld.Modules.Market;

/// <summary>
/// Something that happens to the trader's market for a while: a drought makes grain dear, a steel
/// boom pays more for iron. Changes are in percent of the usual price.
/// </summary>
/// <param name="Pays">How much more (or less) the trader pays for these items.</param>
/// <param name="Charges">How much more (or less) the trader charges for these items.</param>
public sealed record MarketEvent(
    string Id,
    string Name,
    string Description,
    IReadOnlyDictionary<string, int> Pays,
    IReadOnlyDictionary<string, int> Charges);

/// <summary>An event in the trader's calendar, from when to when.</summary>
public sealed record ScheduledEvent(MarketEvent Event, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// The trader's calendar and prices, the same on every server: time is cut into slots of
/// <see cref="SlotLength"/>, and a hash of the slot's number picks its event, or a calm one.
/// On top of that, what the trader pays for each item drifts by up to <see cref="Drift"/> percent,
/// a new target every day, changing hour by hour. What it charges only changes with events.
/// Nothing is stored: any time has its prices, so players can see the next event coming.
/// </summary>
public static class MarketEvents
{
    public static readonly TimeSpan SlotLength = TimeSpan.FromHours(6);

    /// <summary>The most the trader's buying prices drift, in percent, either way.</summary>
    public const int Drift = 10;

    /// <summary>Oil, gas and their fuels drift further: up to this many percent either way.</summary>
    public const int OilDrift = 25;

    /// <summary>Goods of the oil market, whose prices swing more than the rest.</summary>
    public static readonly IReadOnlySet<string> OilGoods = new HashSet<string>
    {
        Items.CrudeOil, Items.NaturalGas, Items.Gasoline, Items.Diesel, Items.Kerosene, Items.Asphalt,
    };

    /// <summary>The most what the trader pays for <paramref name="item"/> drifts, in percent, either way.</summary>
    public static int DriftOf(string item) => OilGoods.Contains(item) ? OilDrift : Drift;

    /// <summary>The trader always pays less than it charges for the same item, at most this many percent of it.</summary>
    public const int MaxPayOfCharge = 90;

    /// <summary>Of every <c>Events.Count + CalmWeight</c> slots, about this many have no event.</summary>
    private const int CalmWeight = 7;

    private static readonly string[] Crops = [Items.Wheat, Items.Corn, Items.Carrot, Items.Potato, Items.Cabbage, Items.Sunflower];

    private static readonly string[] Waste = [Items.WastePaper, Items.WastePlastic, Items.WasteGlass, Items.WasteAluminium, Items.WasteCopper];

    private static readonly string[] Machines =
    [
        Items.SortingMachine, Items.Workbench, Items.Anvil, Items.ResearchTable, Items.Mill, Items.OilPress, Items.PicklingBarrel, Items.Bakery, Items.Kitchen,
        Items.Belt, Items.Inserter, Items.Splitter, Items.WoodenChest, Items.Computer, Items.ForesterHut, Items.Sawmill,
    ];

    private static readonly IReadOnlyDictionary<string, int> Nothing = new Dictionary<string, int>();

    private static Dictionary<string, int> All(IEnumerable<string> items, int percent) => items.ToDictionary(i => i, _ => percent);

    private static Dictionary<string, int> With(this Dictionary<string, int> changes, string item, int percent)
    {
        changes[item] = percent;
        return changes;
    }

    public static readonly IReadOnlyList<MarketEvent> Events =
    [
        new("drought", "Susza",
            "Susza zniszczyła zbiory. Handlarz płaci dużo więcej za zboże, warzywa i mąkę.",
            All(Crops, 50).With(Items.Wheat, 60).With(Items.Corn, 60).With(Items.Flour, 30).With(Items.CornFlour, 30), Nothing),
        new("harvest", "Urodzaj",
            "Rekordowe zbiory w całym kraju. Płody rolne tanieją.",
            All(Crops, -30), Nothing),
        new("steel_boom", "Boom na stal",
            "Huty nie nadążają z zamówieniami. Ruda żelaza, sztabki i gwoździe są w cenie.",
            new Dictionary<string, int> { [Items.IronOre] = 50, [Items.IronIngot] = 60, [Items.Nails] = 40, [Items.IronChest] = 30 }, Nothing),
        new("building_boom", "Boom budowlany",
            "Wszyscy budują. Drewno, deski, belki, kamień i piasek schodzą na pniu.",
            new Dictionary<string, int>
            {
                [Items.Wood] = 50, [Items.Planks] = 50, [Items.Beams] = 50, [Items.Plywood] = 30, [Items.Stone] = 50, [Items.Sand] = 80,
                [Items.WoodPile] = 30, [Items.Heap] = 30,
            }, Nothing),
        new("winter", "Sroga zima",
            "Mróz trzyma od tygodnia. Węgiel, drewno i pellet na opał drożeją.",
            new Dictionary<string, int> { [Items.Coal] = 80, [Items.Wood] = 30, [Items.WoodPellets] = 50 }, Nothing),
        new("copper_rush", "Gorączka miedzi",
            "Elektrownie kupują kable. Handlarz płaci więcej za miedź, także ze złomu.",
            new Dictionary<string, int> { [Items.CopperOre] = 50, [Items.CopperIngot] = 50, [Items.WasteCopper] = 40 }, Nothing),
        new("gold_rush", "Gorączka złota",
            "Niepewne czasy, ludzie uciekają w kruszce. Złoto i srebro drożeją.",
            new Dictionary<string, int> { [Items.GoldOre] = 40, [Items.GoldIngot] = 40, [Items.SilverOre] = 25, [Items.SilverIngot] = 25 }, Nothing),
        new("recycling", "Akcja recyklingu",
            "Miasto dopłaca do surowców wtórnych. Posegregowane odpady są warte więcej, ale worki ze śmieciami drożeją.",
            All(Waste, 50), new Dictionary<string, int> { [Items.Trash] = 25 }),
        new("fair", "Jarmark",
            "Na rynku jarmark i tłumy głodnych gości. Gotowe jedzenie sprzedaje się drożej.",
            All([Items.Sauerkraut, Items.CornBread, Items.CarrotCake, Items.Fries, Items.VegetableSoup, Items.Bigos], 40), Nothing),
        new("oil_crisis", "Kryzys naftowy",
            "Tankowce stoją w porcie, a stacje benzynowe mają kolejki. Ropa i paliwa są o wiele droższe.",
            new Dictionary<string, int>
            {
                [Items.CrudeOil] = 80, [Items.Gasoline] = 60, [Items.Diesel] = 60, [Items.Kerosene] = 50, [Items.NaturalGas] = 40,
            }, new Dictionary<string, int> { [Items.Gasoline] = 30, [Items.Diesel] = 30 }),
        new("oil_glut", "Nadpodaż ropy",
            "Magazyny ropy pękają w szwach. Handlarz płaci za ropę, gaz i paliwa dużo mniej.",
            new Dictionary<string, int>
            {
                [Items.CrudeOil] = -40, [Items.NaturalGas] = -40, [Items.Gasoline] = -25, [Items.Diesel] = -25, [Items.Kerosene] = -25,
            }, Nothing),
        new("machine_sale", "Wyprzedaż maszyn",
            "Handlarz czyści magazyn. Maszyny, taśmy i skrzynie są o ćwierć tańsze.",
            Nothing, All(Machines, -25)),
    ];

    /// <summary>The event of the slot <paramref name="at"/> falls in, or null when the market is calm.</summary>
    public static ScheduledEvent? At(DateTimeOffset at)
    {
        var slot = Slot(at);
        if (Pick(slot) is not { } e)
        {
            return null;
        }

        // An event that already ran in the slot before is over: the market calms down.
        if (Pick(slot - 1) == e)
        {
            return null;
        }

        var start = DateTimeOffset.UnixEpoch + slot * SlotLength;
        return new ScheduledEvent(e, start, start + SlotLength);
    }

    /// <summary>The next event that starts after <paramref name="at"/>, looking a few days ahead.</summary>
    public static ScheduledEvent? Next(DateTimeOffset at)
    {
        var start = DateTimeOffset.UnixEpoch + (Slot(at) + 1) * SlotLength;
        for (var i = 0; i < 16; i++, start += SlotLength)
        {
            if (At(start) is { } e)
            {
                return e;
            }
        }

        return null;
    }

    private static long Slot(DateTimeOffset at) => Math.DivRem((at - DateTimeOffset.UnixEpoch).Ticks, SlotLength.Ticks) is var (q, r) && r < 0 ? q - 1 : q;

    private static MarketEvent? Pick(long slot)
    {
        var n = (int)(Hash((ulong)slot ^ 0x5EED_E7E7) % (ulong)(Events.Count + CalmWeight));
        return n < Events.Count ? Events[n] : null;
    }

    /// <summary>The drift of what the trader pays for <paramref name="item"/>, in percent: a straight line, hour by hour, between one day's target and the next.</summary>
    public static double DriftAt(string item, DateTimeOffset at)
    {
        var hours = Math.DivRem((at - DateTimeOffset.UnixEpoch).Ticks, TimeSpan.TicksPerHour) is var (q, r) && r < 0 ? q - 1 : q;
        var day = Math.DivRem(hours, 24, out var hour);
        if (hour < 0)
        {
            day--;
            hour += 24;
        }

        var seed = Fnv(item);
        var drift = DriftOf(item);
        return double.Lerp(Target(seed, day, drift), Target(seed, day + 1, drift), hour / 24.0);
    }

    private static double Target(ulong seed, long day, int drift) =>
        (Hash(seed ^ (ulong)day) % (ulong)(2 * drift * 100 + 1) / 100.0) - drift;

    /// <summary>SplitMix64: a well-mixed number for any input, the same on every machine.</summary>
    private static ulong Hash(ulong x)
    {
        x += 0x9E37_79B9_7F4A_7C15;
        x = (x ^ (x >> 30)) * 0xBF58_476D_1CE4_E5B9;
        x = (x ^ (x >> 27)) * 0x94D0_49BB_1331_11EB;
        return x ^ (x >> 31);
    }

    private static ulong Fnv(string s)
    {
        var h = 0xCBF2_9CE4_8422_2325;
        foreach (var c in s)
        {
            h = (h ^ c) * 0x0000_0100_0000_01B3;
        }

        return h;
    }
}

/// <summary>The trader's prices at one moment, with the usual ones to compare.</summary>
public sealed class TraderPrices
{
    /// <summary>What the trader charges the player, in cents.</summary>
    public required IReadOnlyDictionary<string, long> Sells { get; init; }

    /// <summary>What the trader pays the player, in cents.</summary>
    public required IReadOnlyDictionary<string, long> Buys { get; init; }

    public ScheduledEvent? Event { get; init; }

    /// <summary>The usual prices, unchanged.</summary>
    public static readonly TraderPrices Usual = new() { Sells = NpcPrices.Sells, Buys = NpcPrices.Buys };

    public static TraderPrices At(DateTimeOffset at)
    {
        var e = MarketEvents.At(at)?.Event;
        var sells = NpcPrices.Sells.ToDictionary(p => p.Key, p => Apply(p.Value, e?.Charges.GetValueOrDefault(p.Key) ?? 0));
        var buys = NpcPrices.Buys.ToDictionary(p => p.Key, p =>
        {
            var drift = 1 + MarketEvents.DriftAt(p.Key, at) / 100;
            var price = Apply((long)Math.Round(p.Value * drift), e?.Pays.GetValueOrDefault(p.Key) ?? 0);
            return sells.TryGetValue(p.Key, out var charge) ? Math.Min(price, charge * MarketEvents.MaxPayOfCharge / 100) : price;
        });
        return new TraderPrices { Sells = sells, Buys = buys, Event = MarketEvents.At(at) };
    }

    private static long Apply(long cents, int percent) => Math.Max(1, (long)Math.Round(cents * (100 + percent) / 100.0));

    /// <summary>How far a price is from the usual one, in whole percent.</summary>
    public static int Change(long cents, long usual) => (int)Math.Round((cents - usual) * 100.0 / usual);
}

/// <summary>
/// The trader's prices now. Setting <c>Market:PriceChanges</c> to false keeps the usual prices and
/// no events, e.g. for tests that count on them.
/// </summary>
public sealed class TraderMarket(TimeProvider time, IConfiguration configuration)
{
    public bool Changing { get; } = configuration.GetValue("Market:PriceChanges", true);

    public DateTimeOffset Now => time.GetUtcNow();

    public TraderPrices Prices() => Changing ? TraderPrices.At(time.GetUtcNow()) : TraderPrices.Usual;

    public ScheduledEvent? NextEvent() => Changing ? MarketEvents.Next(time.GetUtcNow()) : null;
}
