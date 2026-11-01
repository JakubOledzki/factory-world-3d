namespace FactoryWorld.Modules.Bots;

/// <summary>What a bot is mostly up to; it decides the biome of its plots, what it makes and what it trades.</summary>
public enum BotTrade
{
    /// <summary>Buys forest, chops and plants trees, saws wood into planks on a sawmill line, sells wood, planks and chests.</summary>
    Forester,

    /// <summary>Buys mountains, works the mines, smelts ore on a furnace line, sells stone, coal, ore and ingots.</summary>
    Miner,

    /// <summary>Buys fertile soil, tills, sows and reaps, grinds wheat on a mill line, sells crops and flour.</summary>
    Farmer,

    /// <summary>The crafter: buys ingots, ore and wood on the market, smelts on a furnace line and crafts inserters, belts,
    /// poles and chests for the market.</summary>
    Industrialist,

    /// <summary>Lives off the market: buys cheap listings from players, sells them on, fills buy orders and gives out orders.</summary>
    Trader,

    /// <summary>Buys ore and coal from miners and players, smelts it on a furnace line on its plot, sells ingots and nails.</summary>
    Smelter,
}

/// <summary>One bot of the roster.</summary>
/// <param name="Name">Its character's name, as players see it in rankings and on the market.</param>
/// <param name="Latitude">Where on Earth it looks for its plots (a spiral out from here).</param>
/// <param name="Look">The character editor's choices: skin tone, hair style, hair colour, shirt style, shirt, pants and shoes colour.</param>
/// <param name="SessionMinutes">How long one of its play sessions lasts, give or take a half.</param>
/// <param name="RestHours">How long it stays away between sessions, give or take a half.</param>
public sealed record BotProfile(
    string Name, BotTrade Trade, double Latitude, double Longitude, int[] Look, int SessionMinutes, int RestHours);

/// <summary>
/// Every bot the server plays as: the <see cref="Founders"/> written out by hand, then as many made up by
/// <see cref="BotNames"/> as <see cref="BotsOptions.Count"/> asks for. Each one gets an account and a character
/// on the first start and keeps them, so its name, plots, money and badges last like a player's. A bot is known
/// by its name, so the roster must come out the same on every start: never reorder or edit the founders or the
/// word lists, only add to their ends. Names must be free character names (3-16 letters, digits or _); a bot
/// whose name a player took first gets a digit added.
/// </summary>
public static class BotRoster
{
    /// <summary>The first <paramref name="count"/> bots: the founders, then made-up ones.</summary>
    public static IReadOnlyList<BotProfile> First(int count)
    {
        var roster = Founders.Take(count).ToList();
        if (roster.Count < count)
        {
            roster.AddRange(BotNames.Profiles(Founders.Select(f => f.Name)).Take(count - roster.Count));
        }

        return roster;
    }

    /// <summary>The bots there were before the roster was made up: they keep their names, plots and money.</summary>
    public static readonly IReadOnlyList<BotProfile> Founders =
    [
        new("Kuba_Drwal", BotTrade.Forester, 52.70, 23.85, [1, 1, 1, 0, 5, 1, 1], 60, 2),
        new("Zosia_Las", BotTrade.Forester, 53.60, 17.90, [0, 3, 3, 2, 11, 8, 1], 45, 1),
        new("Bartosz99", BotTrade.Forester, 50.95, 22.75, [2, 2, 0, 0, 7, 1, 1], 75, 2),
        new("Wojtek_Kopalnia", BotTrade.Miner, 49.30, 19.95, [1, 1, 2, 1, 1, 8, 1], 75, 2),
        new("Majka_Huta", BotTrade.Miner, 50.90, 15.75, [0, 4, 4, 2, 2, 1, 0], 60, 2),
        new("Ola_Farma", BotTrade.Farmer, 52.25, 19.35, [0, 3, 6, 0, 5, 10, 1], 60, 2),
        new("Tomek_Rolnik", BotTrade.Farmer, 51.05, 20.95, [3, 0, 0, 1, 3, 1, 1], 45, 3),
        new("Piotrek_Stal", BotTrade.Industrialist, 50.25, 19.05, [1, 2, 1, 2, 8, 1, 1], 75, 2),
        new("Natalia_W", BotTrade.Industrialist, 51.10, 17.05, [0, 5, 2, 2, 9, 8, 0], 60, 2),
        new("Ewelina_Handel", BotTrade.Trader, 52.40, 16.95, [0, 4, 3, 0, 4, 1, 0], 60, 1),
        new("Marek_Tartak", BotTrade.Forester, 54.10, 22.90, [1, 1, 1, 2, 3, 1, 1], 60, 2),
        new("Kasia_Sosna", BotTrade.Forester, 53.15, 16.70, [0, 3, 4, 0, 6, 10, 0], 45, 1),
        new("Leszek_Buk", BotTrade.Forester, 49.60, 22.40, [2, 1, 5, 1, 1, 8, 1], 90, 3),
        new("Grzegorz_Ruda", BotTrade.Miner, 50.35, 18.90, [1, 2, 0, 0, 10, 1, 1], 60, 2),
        new("Iza_Wegiel", BotTrade.Miner, 50.10, 19.40, [0, 4, 2, 1, 7, 8, 0], 45, 2),
        new("Robert_Skala", BotTrade.Miner, 49.75, 20.40, [3, 1, 1, 2, 5, 1, 1], 75, 3),
        new("Dorota_Kamien", BotTrade.Miner, 50.80, 16.30, [1, 3, 7, 0, 11, 10, 0], 60, 1),
        new("Staszek_Pole", BotTrade.Farmer, 50.70, 23.25, [2, 0, 5, 0, 4, 1, 1], 75, 2),
        new("Hania_Zboze", BotTrade.Farmer, 53.00, 18.60, [0, 4, 4, 2, 2, 8, 0], 45, 1),
        new("Jurek_Sad", BotTrade.Farmer, 51.75, 21.20, [1, 2, 1, 1, 9, 1, 1], 60, 2),
        new("Basia_Ogrod", BotTrade.Farmer, 52.95, 21.80, [0, 3, 6, 0, 10, 10, 0], 60, 3),
        new("Adam_Kuznia", BotTrade.Industrialist, 51.25, 22.55, [1, 1, 2, 2, 1, 8, 1], 75, 2),
        new("Monika_Fabryka", BotTrade.Industrialist, 51.75, 19.45, [0, 5, 3, 1, 6, 1, 0], 60, 2),
        new("Pawel_Zaklad", BotTrade.Industrialist, 53.45, 14.55, [2, 2, 0, 2, 3, 8, 1], 45, 1),
        new("Krzysiek_Metal", BotTrade.Industrialist, 50.05, 21.00, [1, 0, 1, 0, 8, 10, 1], 90, 3),
        new("Michal_Kupiec", BotTrade.Trader, 50.05, 19.95, [1, 2, 1, 0, 2, 1, 1], 75, 1),
        new("Agata_Sklep", BotTrade.Trader, 51.10, 17.00, [0, 4, 7, 2, 5, 8, 0], 60, 2),
        new("Damian_Hurt", BotTrade.Trader, 54.35, 18.65, [2, 1, 0, 1, 11, 1, 1], 45, 1),
        new("Ania_Gielda", BotTrade.Trader, 52.20, 21.00, [0, 3, 3, 0, 7, 10, 0], 90, 2),
        new("Szymon_Targ", BotTrade.Trader, 53.15, 23.15, [3, 1, 2, 2, 9, 8, 1], 60, 2),
    ];
}
