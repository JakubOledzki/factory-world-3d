namespace FactoryWorld.Modules.Achievements;

/// <summary>What the module counts for each player. Ids are stored in the database, so never rename one.</summary>
public static class Stats
{
    public const string Wood = "wood";
    public const string TreesPlanted = "trees_planted";
    public const string Mined = "mined";
    public const string GoldOre = "gold_ore";
    public const string Ingots = "ingots";
    public const string Crafted = "crafted";
    public const string Processed = "processed";
    public const string Fields = "fields";
    public const string Crops = "crops";
    public const string Placed = "placed";
    public const string Belts = "belts";
    public const string Trains = "trains";
    public const string Plots = "plots";
    public const string Surveys = "surveys";
    public const string Sold = "sold";
    public const string Trades = "trades";
    public const string Contracts = "contracts";
    public const string Fossils = "fossils";
    public const string DinosaurBones = "dinosaur_bones";
    public const string Glass = "glass";
    public const string Ice = "ice";
    public const string Meteorites = "meteorites";
    public const string Tourists = "tourists";
    public const string Bridges = "bridges";
    public const string CityDeliveries = "city_deliveries";
    public const string CityTrash = "city_trash";
    public const string OakWood = "oak_wood";
    public const string Mushrooms = "mushrooms";
    public const string Honey = "honey";
    public const string Furniture = "furniture";
    public const string AlbumPages = "album_pages";
    public const string Expeditions = "expeditions";
    public const string Puzzles = "puzzles";
    public const string PuzzleGold = "puzzle_gold";
    public const string Mentored = "mentored";

    /// <summary>Everything the player made by hand or with machines: gathered, mined, smelted, crafted, grown, processed.</summary>
    public const string Produced = "produced";

    public const string Badges = "badges";

    // Kept as the highest value seen, not added up.
    public const string Level = "level";
    public const string Money = "money";
    public const string ShopSold = "shop_sold";
    public const string Mayor = "mayor";

    public static bool IsHighest(string stat) => stat is Level or Money or ShopSold or Mayor;
}

/// <param name="Goal">The badge is unlocked when <paramref name="Stat"/> reaches this.</param>
public sealed record Badge(string Id, string Group, string Name, string Description, string Stat, long Goal);

/// <summary>Every achievement badge, in the order the player sees them. Ids are stored, so never rename one.</summary>
public static class Badges
{
    /// <summary>The pages of the album of finds (<c>Album.Pages</c> in the collections module).</summary>
    public const int AlbumSize = 7;

    public static readonly IReadOnlyList<Badge> All =
    [
        new("first_tree", "Las", "Pierwsze drzewo", "Zetnij pierwsze drzewo.", Stats.Wood, 1),
        new("lumberjack", "Las", "Drwal", "Zbierz 100 drewna.", Stats.Wood, 100),
        new("forest_king", "Las", "Król lasu", "Zbierz 1000 drewna.", Stats.Wood, 1000),
        new("gardener", "Las", "Ogrodnik", "Posadź 10 sadzonek.", Stats.TreesPlanted, 10),
        new("forester", "Las", "Leśnik", "Posadź 100 sadzonek.", Stats.TreesPlanted, 100),
        new("old_oak", "Las", "Dąb Bartek", "Zetnij pierwszy dąb.", Stats.OakWood, 1),
        new("mushroom_picker", "Las", "Grzybiarz", "Zbierz 100 grzybów.", Stats.Mushrooms, 100),
        new("beekeeper", "Las", "Pszczelarz", "Wybierz z uli 100 słoików miodu.", Stats.Honey, 100),
        new("cabinetmaker", "Las", "Meblarz", "Zrób w stolarni 20 mebli.", Stats.Furniture, 20),

        new("first_dig", "Górnictwo", "Pierwszy urobek", "Wydobądź coś w kopalni.", Stats.Mined, 1),
        new("miner", "Górnictwo", "Górnik", "Wydobądź 100 jednostek w kopalniach.", Stats.Mined, 100),
        new("master_miner", "Górnictwo", "Mistrz kilofa", "Wydobądź 1000 jednostek w kopalniach.", Stats.Mined, 1000),
        new("gold_rush", "Górnictwo", "Gorączka złota", "Znajdź rudę złota.", Stats.GoldOre, 1),

        new("first_ingot", "Hutnictwo", "Pierwszy wytop", "Wyjmij z pieca pierwszą sztabkę.", Stats.Ingots, 1),
        new("smelter", "Hutnictwo", "Hutnik", "Wyjmij z pieców 100 sztabek.", Stats.Ingots, 100),
        new("steelworks", "Hutnictwo", "Huta", "Wyjmij z pieców 1000 sztabek.", Stats.Ingots, 1000),

        new("tinkerer", "Rzemiosło", "Majsterkowicz", "Wytwórz 10 przedmiotów przy stole warsztatowym.", Stats.Crafted, 10),
        new("craftsman", "Rzemiosło", "Rzemieślnik", "Wytwórz 100 przedmiotów przy stole warsztatowym.", Stats.Crafted, 100),
        new("processor", "Rzemiosło", "Przetwórca", "Wyjmij z maszyn przetwórczych 50 produktów.", Stats.Processed, 50),
        new("chef", "Rzemiosło", "Szef kuchni", "Wyjmij z maszyn przetwórczych 500 produktów.", Stats.Processed, 500),

        new("plowman", "Rolnictwo", "Oracz", "Zaorz 10 pól.", Stats.Fields, 10),
        new("first_harvest", "Rolnictwo", "Pierwszy plon", "Zbierz pierwszą dojrzałą roślinę.", Stats.Crops, 1),
        new("farmer", "Rolnictwo", "Rolnik", "Zbierz 100 plonów.", Stats.Crops, 100),
        new("granary", "Rolnictwo", "Pełny spichlerz", "Zbierz 1000 plonów.", Stats.Crops, 1000),

        new("builder", "Fabryka", "Budowniczy", "Postaw 10 obiektów na działkach.", Stats.Placed, 10),
        new("engineer", "Fabryka", "Inżynier", "Postaw 100 obiektów na działkach.", Stats.Placed, 100),
        new("conveyor", "Fabryka", "Taśmowa produkcja", "Postaw 50 taśmociągów.", Stats.Belts, 50),
        new("railwayman", "Fabryka", "Kolejarz", "Wyślij 10 pociągów.", Stats.Trains, 10),
        new("industrialist", "Fabryka", "Przemysłowiec", "Wyprodukuj łącznie 10 000 sztuk czegokolwiek.", Stats.Produced, 10_000),

        new("landowner", "Ziemia", "Ziemianin", "Kup pierwszą działkę.", Stats.Plots, 1),
        new("land_baron", "Ziemia", "Magnat ziemski", "Kup 3 działki.", Stats.Plots, 3),
        new("geologist", "Ziemia", "Geolog", "Zleć badanie geologiczne działki.", Stats.Surveys, 1),

        new("paleontologist", "Pustynia", "Paleontolog", "Wykop z wydmy pierwszą skamielinę.", Stats.Fossils, 1),
        new("fossil_collection", "Pustynia", "Gabinet osobliwości", "Wykop z wydm 10 skamielin.", Stats.Fossils, 10),
        new("dinosaur", "Pustynia", "Park jurajski", "Wykop kość dinozaura.", Stats.DinosaurBones, 1),
        new("glassblower", "Pustynia", "Hutnik szkła", "Wyjmij z huty 100 sztuk szkła.", Stats.Glass, 100),

        new("ice_cutter", "Lodowiec", "Lodziarz", "Zbierz z lodowca 100 lodu.", Stats.Ice, 100),
        new("stargazer", "Lodowiec", "Łowca meteorytów", "Rozbij pierwszy meteoryt.", Stats.Meteorites, 1),
        new("meteorite_collection", "Lodowiec", "Kamienie z nieba", "Rozbij 10 meteorytów.", Stats.Meteorites, 10),
        new("hotelier", "Lodowiec", "Hotelarz", "Odbierz od turystów 100 utargów.", Stats.Tourists, 100),
        new("bridge_builder", "Lodowiec", "Budowniczy mostów", "Połóż most nad szczeliną.", Stats.Bridges, 1),

        new("townsfolk", "Miasto", "Dobry sąsiad", "Dostarcz miastu pierwszy towar, którego potrzebuje.", Stats.CityDeliveries, 1),
        new("city_supplier", "Miasto", "Zaopatrzeniowiec", "Dostarcz miastom 1000 potrzebnych towarów.", Stats.CityDeliveries, 1000),
        new("garbage_collector", "Miasto", "Śmieciarz", "Wywieź z miejskiego wysypiska 1000 śmieci.", Stats.CityTrash, 1000),
        new("shopkeeper", "Miasto", "Sklepikarz", "Sprzedaj w jednym sklepie 100 towarów.", Stats.ShopSold, 100),
        new("mayor", "Miasto", "Burmistrz", "Zostań wybrany na burmistrza.", Stats.Mayor, 1),

        new("merchant", "Handel", "Kupiec", "Sprzedaj handlarzowi 100 przedmiotów.", Stats.Sold, 100),
        new("trader", "Handel", "Handlowiec", "Kup albo sprzedaj coś innym graczom 10 razy.", Stats.Trades, 10),
        new("contractor", "Handel", "Solidny wykonawca", "Wykonaj 5 zleceń.", Stats.Contracts, 5),
        new("supplier", "Handel", "Dostawca roku", "Wykonaj 50 zleceń.", Stats.Contracts, 50),
        new("wealthy", "Handel", "Dorobkiewicz", "Miej w portfelu 10 000 $.", Stats.Money, 10_000_00),
        new("millionaire", "Handel", "Milioner", "Miej w portfelu 1 000 000 $.", Stats.Money, 1_000_000_00),

        new("level_5", "Postać", "Czeladnik", "Osiągnij 5. poziom.", Stats.Level, 5),
        new("level_10", "Postać", "Fachowiec", "Osiągnij 10. poziom.", Stats.Level, 10),
        new("level_25", "Postać", "Mistrz", "Osiągnij 25. poziom.", Stats.Level, 25),
        new("level_50", "Postać", "Legenda", "Osiągnij 50. poziom.", Stats.Level, 50),
        new("collector", "Postać", "Kolekcjoner", "Zdobądź 20 innych odznak.", Stats.Badges, 20),

        new("album_page", "Przygody", "Zbieracz", "Uzupełnij pierwszą stronę albumu znalezisk.", Stats.AlbumPages, 1),
        new("curator", "Przygody", "Kustosz", "Uzupełnij wszystkie strony albumu znalezisk.", Stats.AlbumPages, AlbumSize),
        new("explorer", "Przygody", "Odkrywca", "Wróć z pierwszej wyprawy.", Stats.Expeditions, 1),
        new("traveller", "Przygody", "Podróżnik", "Wróć z 25 wypraw.", Stats.Expeditions, 25),
        new("puzzle_solver", "Przygody", "Łamigłówka", "Rozwiąż pierwszą zagadkę inżynierską.", Stats.Puzzles, 1),
        new("puzzle_master", "Przygody", "Złota rączka", "Zdobądź 5 złotych medali w zagadkach inżynierskich.", Stats.PuzzleGold, 5),
        new("mentor", "Przygody", "Mentor", "Doprowadź podopiecznego do 10. poziomu.", Stats.Mentored, 1),
    ];

    public static readonly ILookup<string, Badge> ByStat = All.ToLookup(b => b.Stat);

    public static readonly IReadOnlyDictionary<string, Badge> ById = All.ToDictionary(b => b.Id);

    /// <summary>How far the player is, e.g. "37 / 100" or "2 500,00 / 10 000,00 $".</summary>
    public static string Progress(Badge badge, long value)
    {
        var shown = Math.Min(value, badge.Goal);
        return badge.Stat == Stats.Money
            ? $"{Polish.Money(shown)[..^2]} / {Polish.Money(badge.Goal)}"
            : $"{Polish.Number(shown)} / {Polish.Number(badge.Goal)}";
    }
}

public static class Polish
{
    /// <summary>12345 as "12 345".</summary>
    public static string Number(long n)
    {
        var digits = Math.Abs(n).ToString();
        var grouped = new System.Text.StringBuilder(n < 0 ? "-" : "");
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                grouped.Append(' ');
            }

            grouped.Append(digits[i]);
        }

        return grouped.ToString();
    }

    /// <summary>Cents as "1 234,50 $", like the client shows money.</summary>
    public static string Money(long cents) => $"{Number(cents / 100)},{Math.Abs(cents % 100):00} $";

    /// <summary>The noun form after a number: 1 odznaka, 2 odznaki, 5 odznak.</summary>
    public static string Count(long n, string one, string few, string many)
    {
        var form = n == 1 ? one
            : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few
            : many;
        return $"{Number(n)} {form}";
    }
}
