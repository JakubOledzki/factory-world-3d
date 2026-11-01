namespace FactoryWorld.Modules.Bots;

/// <summary>
/// Makes up the bots after the founders: nicknames like players pick (SzalonyKret, KapitanKilof, dziki_bobr77),
/// a trade, a home somewhere on Earth where that trade has land, a look and a rhythm of play. Everything comes
/// from a fixed seed, so the same bots come out on every start. Changing a list or the seed renames every made-up
/// bot (new accounts for all of them), so leave them as they are.
/// </summary>
public static class BotNames
{
    private const int Seed = 20261026;

    // Masculine, so that every adjective goes with every noun.
    private static readonly string[] Adjectives =
    [
        "Cichy", "Rdzawy", "Stalowy", "Złoty", "Szalony", "Leniwy", "Mglisty", "Dziki", "Wesoły", "Sprytny",
        "Żelazny", "Miedziany", "Ognisty", "Nocny", "Mroźny", "Senny", "Dzielny", "Chytry", "Wielki", "Mały",
        "Krzywy", "Brodaty", "Gruby", "Turbo", "Mega", "Tajny", "Głodny", "Zakręcony", "Pancerny", "Szybki",
        "Kosmiczny", "Atomowy", "Parowy", "Dymiący", "Węglowy", "Dębowy", "Sosnowy", "Granitowy", "Pogodny", "Uparty",
    ];

    private static readonly string[] Titles =
    [
        "Pan", "Kapitan", "Doktor", "Baron", "Hrabia", "Profesor", "Generał", "Mistrz", "Książę", "Sir", "Lord", "Wujek", "Dziadek",
    ];

    private static readonly string[] Nouns =
    [
        "Bóbr", "Kret", "Borsuk", "Jeż", "Lis", "Wilk", "Kruk", "Smok", "Kilof", "Młot",
        "Dzik", "Żubr", "Sokół", "Puchacz", "Szerszeń", "Pancernik", "Kombajn", "Traktor", "Dźwig", "Wulkan",
        "Meteor", "Cyklon", "Golem", "Robot", "Tytan", "Kowal", "Hutnik", "Górnik", "Drwal", "Rolnik",
        "Kupiec", "Magnat", "Ziemniak", "Pierożek", "Kapeć", "Trzmiel", "Wiewiór", "Kotlet", "Pączek", "Ogórek",
        "Grzyb", "Kaktus", "Bałwan", "Pirat", "Rycerz", "Szeryf", "Kosmita", "Dynamit", "Bigos", "Śrubokręt",
    ];

    /// <summary>Where each trade finds its land (latitude, longitude): forests, mountains, farmland, forests near industry,
    /// cities, steel towns. Every list has 16 places.</summary>
    private static readonly IReadOnlyDictionary<BotTrade, (double Lat, double Lon)[]> Homes = new Dictionary<BotTrade, (double, double)[]>
    {
        [BotTrade.Forester] =
        [
            (61.0, 15.0), (62.5, 26.0), (60.5, 10.5), (53.6, 17.9), (50.6, 10.8), (48.2, 8.2), (56.8, 25.5), (47.0, 25.2),
            (58.5, 31.5), (47.5, -71.5), (45.5, -84.5), (60.5, 100.0), (46.5, 14.5), (55.0, 9.5), (52.0, 33.0), (49.5, -122.5),
        ],
        [BotTrade.Miner] =
        [
            (46.6, 10.2), (47.2, 13.2), (49.3, 20.0), (45.6, 25.2), (42.7, 0.8), (63.0, 12.8), (42.6, 20.2), (60.0, 59.5),
            (43.0, 43.0), (39.5, -106.0), (-33.0, -70.0), (50.8, 15.7), (40.8, 22.8), (51.0, -116.0), (37.2, -3.4), (45.8, 6.9),
        ],
        [BotTrade.Farmer] =
        [
            (49.5, 32.0), (47.0, 20.0), (48.2, 1.8), (52.3, 19.3), (44.3, 26.5), (41.8, -93.5), (38.5, -98.0), (-34.0, -61.0),
            (52.3, 5.6), (45.1, 10.8), (51.5, 40.0), (50.5, 4.6), (53.5, 10.5), (46.8, 4.5), (44.5, -0.5), (49.8, 18.0),
        ],
        [BotTrade.Industrialist] =
        [
            (50.3, 19.0), (51.5, 7.2), (50.1, 14.4), (49.8, 18.3), (45.5, 9.2), (52.5, 13.4), (51.0, 13.7), (48.2, 16.4),
            (47.5, 19.1), (59.3, 18.1), (41.9, -87.6), (35.7, 139.7), (55.8, 37.6), (40.4, -3.7), (53.5, -2.2), (44.4, 26.1),
        ],
        [BotTrade.Trader] =
        [
            (52.2, 21.0), (48.9, 2.4), (51.5, -0.1), (53.6, 10.0), (52.4, 4.9), (50.9, 4.4), (41.0, 29.0), (55.7, 12.6),
            (60.2, 24.9), (40.7, -74.0), (45.5, -73.6), (37.5, 127.0), (31.2, 121.5), (-23.5, -46.6), (54.4, 18.6), (46.2, 6.1),
        ],
        // Steel towns between forests and mountains: wood for the fire, ore from the miners nearby.
        [BotTrade.Smelter] =
        [
            (50.3, 18.7), (51.4, 7.0), (49.6, 18.6), (48.7, 21.3), (46.6, 15.6), (47.1, 15.4), (60.6, 15.6), (59.4, 16.5),
            (53.4, 59.0), (56.8, 60.6), (40.4, -80.0), (46.5, -84.3), (49.4, 6.2), (45.7, 13.8), (43.3, -2.9), (-20.3, -43.9),
        ],
    };

    /// <summary>
    /// The trades made-up bots get in turn. Every list of homes is as long as the others, so a bot's trade does not change
    /// what the seed gives next and adding a trade here keeps every name; a bot that gets a new trade keeps its plots.
    /// </summary>
    private static readonly BotTrade[] Trades =
        [BotTrade.Forester, BotTrade.Miner, BotTrade.Farmer, BotTrade.Industrialist, BotTrade.Trader, BotTrade.Smelter];

    /// <summary>Made-up bots one after another, none named like <paramref name="taken"/>; endless, take as many as needed.</summary>
    public static IEnumerable<BotProfile> Profiles(IEnumerable<string> taken)
    {
        var used = taken.Select(Plain).ToHashSet(StringComparer.Ordinal);
        var random = new Random(Seed);
        var index = 0;
        foreach (var name in Names(random))
        {
            if (!used.Add(Plain(name)))
            {
                continue;
            }

            var trade = Trades[index++ % Trades.Length];
            var homes = Homes[trade];
            var (lat, lon) = homes[random.Next(homes.Length)];
            int[] look =
            [
                random.Next(6), random.Next(6), random.Next(8), random.Next(3), random.Next(12), random.Next(12), random.Next(12),
            ];
            yield return new BotProfile(
                name, trade,
                Math.Round(Math.Clamp(lat + (random.NextDouble() - 0.5) * 3, -60, 70), 2),
                Math.Round(lon + (random.NextDouble() - 0.5) * 3, 2),
                look,
                SessionMinutes: 45 + 15 * random.Next(4),
                RestHours: 1 + random.Next(3));
        }
    }

    /// <summary>A name in small letters without Polish ones, as in a bot's e-mail address: no two bots may share it.</summary>
    public static string Plain(string name) =>
        string.Concat(name.ToLowerInvariant().Select(c => c switch
        {
            'ą' => 'a', 'ć' => 'c', 'ę' => 'e', 'ł' => 'l', 'ń' => 'n', 'ó' => 'o', 'ś' => 's', 'ź' or 'ż' => 'z',
            _ => c,
        }));

    /// <summary>Every adjective and title with every noun in a shuffled order, some written the way players like.</summary>
    private static IEnumerable<string> Names(Random random)
    {
        var bases = Adjectives.SelectMany(a => Nouns.Select(n => (First: a, Noun: n)))
            .Concat(Titles.SelectMany(t => Nouns.Select(n => (First: t, Noun: n))))
            .ToArray();
        random.Shuffle(bases);
        // Once every pair is used, the pairs come round again with a number.
        for (var round = 0; ; round++)
        {
            foreach (var (first, noun) in bases)
            {
                var style = random.Next(100);
                var name = style switch
                {
                    < 12 => $"{first}{noun}{random.Next(2, 100)}",
                    < 22 => $"{first.ToLowerInvariant()}_{noun.ToLowerInvariant()}",
                    < 25 => $"xX{noun}Xx",
                    _ => $"{first}{noun}",
                };
                if (round > 0)
                {
                    name += round + 1;
                }

                if (name.Length is >= 3 and <= 16)
                {
                    yield return name;
                }
            }
        }
    }
}
