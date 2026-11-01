using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Bots;

/// <summary>Biome numbers as the protocol sends them (world.info, world.plot); fixed, so safe to name here.</summary>
internal static class Biomes
{
    public const int Forest = 2;
    public const int Farmland = 3;
    public const int Desert = 5;
    public const int Mountains = 6;
    public const int City = 9;
}

/// <summary>Tile codes of plot.enter: what stands on a tile.</summary>
internal static class Tiles
{
    public const byte None = 0;
    public const byte Tree = 1;
    public const byte Rock = 2;
    public const byte Dune = 3;
}

/// <summary>How bots pace themselves, so that they feel like players and never swamp the market.</summary>
public static class BotRules
{
    /// <summary>Open sell listings one bot keeps at most (players may have 20).</summary>
    public const int MaxListings = 6;

    /// <summary>Open buy orders one bot keeps at most.</summary>
    public const int MaxOrders = 3;

    /// <summary>Open orders one bot gives on the contracts board at most.</summary>
    public const int MaxIssued = 1;

    /// <summary>Most pieces in one listing.</summary>
    public const long ListingSize = 40;

    /// <summary>A bot lists something at most this often.</summary>
    public static readonly TimeSpan ListEvery = TimeSpan.FromMinutes(3);

    /// <summary>With every listing slot taken and nothing new listed for this long, the bot takes its oldest unsold listing back to put it up cheaper.</summary>
    public static readonly TimeSpan RelistAfter = TimeSpan.FromMinutes(30);

    /// <summary>Purchases from players one bot makes in a session at most.</summary>
    public const int MaxPurchases = 12;

    /// <summary>Most pieces a bot buys from one listing at once.</summary>
    public const long PurchaseSize = 40;

    /// <summary>
    /// Money a bot has at least when a session starts: what it buys from players with. Sorting trash pays too
    /// little for a bot to ever buy much, so the server tops it up; a bot never pays a player more than the
    /// trader would charge, so this buys players' goods without printing an easy profit.
    /// </summary>
    public const long Allowance = 100_00;

    /// <summary>What a bot with no plot gets on top of its first plot's price, for tools and trash.</summary>
    public const long StartingExtra = 300_00;

    /// <summary>A bot's listing asks at least this much more than the trader pays.</summary>
    public const double MinMarkup = 1.15;

    /// <summary>Cents a bot does not spend on goods; a plot is the exception, saved up for on purpose.</summary>
    public const long Reserve = 10_00;

    /// <summary>Fields a farming bot works at once.</summary>
    public const int MaxFields = 24;

    /// <summary>Saplings a forestry bot has in the ground at once.</summary>
    public const int MaxPlanted = 40;

    /// <summary>
    /// Cents a bot takes from the NPC trader and NPC orders in one session at most. Selling to the trader makes new money
    /// (a sale between players only moves it), and two thousand bots selling all they gather swamped the economy, so past
    /// this a bot sells only to players and lets the rest wait in its inventory and on its listings.
    /// </summary>
    public const long MaxTraderIncome = 100_00;

    /// <summary>The most a bot gets from the trader for a stack it sells off to free a tile of a full inventory.</summary>
    public const long RoomFor = 5_00;

    /// <summary>With this many pieces to sell piling up, a bot gathers less and works its line and the market more.</summary>
    public const long Glut = 600;

    /// <summary>How often a bot looks after its production line: empties the output, refills the input and the generator.</summary>
    public static readonly TimeSpan TendEvery = TimeSpan.FromMinutes(2);

    /// <summary>How often a bot opens the contracts board.</summary>
    public static readonly TimeSpan ContractsEvery = TimeSpan.FromMinutes(3);

    /// <summary>How long the market overview one bot fetched serves every bot (as long as the server keeps its own summary).</summary>
    public static readonly TimeSpan OverviewFor = TimeSpan.FromSeconds(10);

    /// <summary>What a bot pays a player who helps it on top of what the parts are worth (or alone, for setting a stalled line going).</summary>
    public const long HelpFee = 10_00;

    /// <summary>A bot pays for parts a player brings this many times what the trader pays for them, and the fee.</summary>
    public const double HelpMarkup = 1.25;

    /// <summary>A bot pays for parts at most this share of what the NPC trader charges for them, the fee included.</summary>
    public const double HelpBelowTrader = 0.95;

    /// <summary>How long a player who took on a bot's call for help has to do it before others may.</summary>
    public static readonly TimeSpan HelpLeave = TimeSpan.FromMinutes(15);

    /// <summary>How likely a bot puts one belt or inserter of its new line up the wrong way round, for players to turn
    /// (at most once a session).</summary>
    public const double MistakeChance = 0.2;

    /// <summary>Parts a bot puts its line up without and asks players to put in place, when it can neither make nor buy them.</summary>
    public static readonly IReadOnlySet<string> HelpParts = new HashSet<string> { Items.Inserter, Items.Belt, Items.Pole };

    /// <summary>Most things one call for help asks a player to put up or turn.</summary>
    public const int MaxHelpTasks = 3;

    /// <summary>A bot waits this long for help before it gives up and sets its line going itself.</summary>
    public static readonly TimeSpan HelpWait = TimeSpan.FromMinutes(30);

    /// <summary>A bot that could not put its line up three times tries again after this long.</summary>
    public static readonly TimeSpan RetryBuildAfter = TimeSpan.FromMinutes(30);

    /// <summary>A bot asks for parts once it has wanted them this long without getting them.</summary>
    public static readonly TimeSpan PartsWait = TimeSpan.FromMinutes(20);

    /// <summary>Looks after its line this many times in a row without anything coming out of it, and the bot calls the line stalled.</summary>
    public const int StalledAfter = 2;

    /// <summary>A bot buys a part of its line from the NPC trader, when it cannot make it, only for at most this many times what the trader pays.</summary>
    public const double MaxPartMarkup = 3;

    /// <summary>
    /// The production line the bot puts up on its plot once it has researched what it takes: miners, smelters and
    /// crafters smelt ore, farmers grind wheat, foresters saw planks. Traders live off the market alone.
    /// </summary>
    public static BotLine? Line(BotTrade trade) => trade switch
    {
        BotTrade.Miner or BotTrade.Smelter or BotTrade.Industrialist => new BotLine(Items.Furnace, null,
            Ores, [Items.Wood, Items.Coal],
            [Items.IronIngot, Items.CopperIngot, Items.TinIngot, Items.SilverIngot, Items.GoldIngot]),
        BotTrade.Farmer => new BotLine(Items.Mill, Items.Flour, [Items.Wheat], [Items.Wood, Items.Coal], [Items.Flour]),
        BotTrade.Forester => new BotLine(Items.Sawmill, Items.Planks, [Items.Wood], [Items.Wood], [Items.Planks]),
        _ => null,
    };

    /// <summary>
    /// Plots a bot buys over its life, as far as its money goes: three to five by how ambitious it is (a trader one or two).
    /// The third costs 100 000 $, so few get that far.
    /// </summary>
    public static int MaxPlots(BotTrade trade, double ambition) => trade switch
    {
        BotTrade.Trader => ambition >= AmbitiousFrom ? 2 : 1,
        _ => 3 + (int)Math.Round(Math.Clamp(ambition, 0, 1) * 2),
    };

    /// <summary>A bot this ambitious (<see cref="BotTraits.Ambition"/>) buys a plot in a town besides the ones of its trade.</summary>
    public const double AmbitiousFrom = 0.5;

    /// <summary>A bot starts saving up for its next plot once it has this share of the price.</summary>
    public const double SaveForPlotFrom = 0.25;

    /// <summary>
    /// Biomes the bot's next plot may have, given how many it owns: those of its trade, and for an ambitious bot that owns
    /// a plot already and none in a town, a town's too, to supply the town from.
    /// </summary>
    public static int[] PlotBiomes(BotTrade trade, int owned, double ambition = 0, bool hasCity = true)
    {
        int[] biomes = trade switch
        {
            BotTrade.Forester => [Biomes.Forest],
            BotTrade.Miner => owned == 0 ? [Biomes.Mountains] : [Biomes.Mountains, Biomes.Forest],
            BotTrade.Farmer => owned == 0 ? [Biomes.Farmland] : [Biomes.Farmland, Biomes.Forest],
            BotTrade.Industrialist => owned == 0 ? [Biomes.Forest] : [Biomes.Mountains],
            BotTrade.Smelter => owned == 0 ? [Biomes.Forest] : [Biomes.Mountains, Biomes.Forest],
            _ => [Biomes.Forest],
        };
        return owned > 0 && ambition >= AmbitiousFrom && !hasCity ? [.. biomes, Biomes.City] : biomes;
    }

    /// <summary>Bots this greedy sell the power of their line's generator through a grid tie, once they researched power distribution.</summary>
    public const double SellsPowerFrom = 0.5;

    /// <summary>What a bot asks for a kWh where nobody nearby sells power to compare with.</summary>
    public const long PowerPriceCents = 10;

    /// <summary>How far from a pole a bot puts its grid tie, within the pole's reach of 5 × 5 tiles.</summary>
    public const int PoleReach = 2;

    /// <summary>How often a bot looks at the police window: its work as an officer, or an application.</summary>
    public static readonly TimeSpan LawEvery = TimeSpan.FromMinutes(20);

    /// <summary>Bots this civic apply to their country's police.</summary>
    public const double PoliceFrom = 0.6;

    /// <summary>Bots that dare this much (<see cref="BotTraits.Risk"/>) sell the fence what it buys.</summary>
    public const double FenceFrom = 0.75;

    /// <summary>Police ranks as the law module numbers them: cadet 0, constable 1, detective 2, chief 3.</summary>
    public const int ConstableRank = 1;

    public const int DetectiveRank = 2;

    public const int ChiefRank = 3;

    /// <summary>People a bot detective tries in one look, until one may be investigated.</summary>
    public const int SuspectsPerLook = 8;

    /// <summary>Cases a bot officer sentences in one look at most.</summary>
    public const int SentencesPerLook = 2;

    /// <summary>The smallest fine a bot officer gives.</summary>
    public const long MinFine = 50_00;

    /// <summary>How often a bot looks at the banks and its account.</summary>
    public static readonly TimeSpan BankEvery = TimeSpan.FromHours(1);

    /// <summary>Bots this greedy (<see cref="BotTraits.Greed"/>) keep savings in a bank.</summary>
    public const double BanksFrom = 0.4;

    /// <summary>A bot opens an account and pays in once it has this much to spare.</summary>
    public const long SavesInBankFrom = 2_000_00;

    /// <summary>With less than this in its wallet (over its reserve) a bot takes money out of its account.</summary>
    public const long WithdrawBelow = 200_00;

    /// <summary>The most a bot pays in or takes out at once.</summary>
    public const long BankBatch = 1_000_00;

    /// <summary>How often a bot looks at its town hall and its country's government.</summary>
    public static readonly TimeSpan PoliticsEvery = TimeSpan.FromMinutes(30);

    /// <summary>Bots this civic (<see cref="BotTraits.Civic"/>) vote, in towns and countries; most do.</summary>
    public const double VotesFrom = 0.3;

    /// <summary>Bots this civic, and ambitious, stand for mayor where no player does; one bot per town at a time.</summary>
    public const double StandsFrom = 0.8;

    /// <summary>Bots this civic, and ambitious, run for president where nobody else does yet.</summary>
    public const double RunsForPresidentFrom = 0.97;

    /// <summary>What a bot keeps besides the deposit when it stands for an office.</summary>
    public const long StandSpare = 1_000_00;

    /// <summary>The mayor's bonuses a bot promises, one of them by its nature.</summary>
    public static readonly string[] MayorBonuses = ["demand", "shoppers", "rail", "plots"];

    /// <summary>The tax on shops and services a bot promises as mayor: 2 to 10%, more for a greedy one.</summary>
    public static int TaxPromise(BotTraits traits) => 2 + (int)Math.Round(traits.Greed * 8);

    /// <summary>How often a bot looks at its company and its invitations.</summary>
    public static readonly TimeSpan CompanyEvery = TimeSpan.FromMinutes(10);

    /// <summary>Bots this ambitious found a company of bots; about one in seven.</summary>
    public const double FoundsFrom = 0.85;

    /// <summary>A bot founds a company once it has this much: it costs nothing, but a pauper does not.</summary>
    public const long FoundCompanyFrom = 1_000_00;

    /// <summary>Bots this loyal accept an invitation from a bot's company; most do.</summary>
    public const double JoinsFrom = 0.2;

    /// <summary>A bot pays into its company's cash only with this much to spare, and only if it is not too greedy.</summary>
    public const long DepositFrom = 500_00;

    public const double DepositsBelowGreed = 0.7;

    /// <summary>The most a bot pays into its company's cash in a session.</summary>
    public const long MaxDeposit = 200_00;

    /// <summary>The most pieces a bot brings to its company, or takes from it, at once.</summary>
    public const long CompanyBatch = 200;

    /// <summary>Members a bot's company grows to, the founder and those invited included: 3 to 6, more for an ambitious founder.</summary>
    public static int CompanySize(double ambition) => 3 + (int)Math.Round(Math.Clamp(ambition, 0, 1) * 3);

    private static readonly string[] CompanyWords = ["Zakłady", "Grupa", "Spółdzielnia", "Kombinat", "Bractwo", "Holding", "Manufaktura", "Konsorcjum"];

    /// <summary>Names a bot tries for its company, its own name in them, until one is free (3 to 24 characters).</summary>
    public static IEnumerable<string> CompanyNames(string character, BotTraits traits)
    {
        var start = (int)(traits.Risk * CompanyWords.Length) % CompanyWords.Length;
        for (var i = 0; i < CompanyWords.Length; i++)
        {
            var word = CompanyWords[(start + i) % CompanyWords.Length];
            var name = $"{word} {character}";
            yield return name.Length <= 24 ? name : name[..24].TrimEnd(' ', '_', '.', '-', '&');
        }

        yield return $"{character[..Math.Min(character.Length, 15)]} i Spółka";
    }

    /// <summary>How often a bot with a plot in a town looks what the town wants.</summary>
    public static readonly TimeSpan SupplyEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What the bot puts up on a plot of a biome, in the order it gets to them: it crafts each piece
    /// (once the technology allows) and places it near where it appears. Furnaces on a plot it smelts in
    /// like in the garage; the rest is what a player's yard looks like to visitors.
    /// </summary>
    public static string[] Builds(BotTrade trade, int biome) => (trade, biome) switch
    {
        (BotTrade.Forester, Biomes.Forest) =>
            [Items.Workbench, Items.WoodPile, Items.WoodPile, Items.WoodenChest, Items.CharcoalKiln, Items.Carpentry, Items.TarKiln],
        (BotTrade.Miner, Biomes.Mountains) => [Items.Furnace, Items.Furnace, Items.Heap, Items.WoodenChest, Items.Stonemason],
        (BotTrade.Industrialist, Biomes.Mountains) =>
            [Items.Furnace, Items.Furnace, Items.Anvil, Items.IronChest, Items.Foundry, Items.Glassworks, Items.Stonemason],
        (BotTrade.Farmer, Biomes.Farmland) =>
            [Items.Workbench, Items.Mill, Items.WoodenChest, Items.GrainSilo, Items.PicklingBarrel, Items.Bakery, Items.Kitchen, Items.Smokehouse],
        (BotTrade.Smelter, Biomes.Mountains or Biomes.Forest) => [Items.WoodenChest, Items.Foundry, Items.Glassworks],
        (_, Biomes.Forest) => [Items.WoodPile, Items.WoodenChest],
        _ => [Items.WoodenChest],
    };

    /// <summary>
    /// What the bot makes at the workbench for the market, once the technology allows: belts, inserters, chests and the
    /// like, the things players build their factories of. It picks whichever pays best over what the ingredients fetch
    /// and is not already plentiful on the market, and makes the parts first (nails, belts, an inserter) when it has to.
    /// </summary>
    public static string[] Products(BotTrade trade) => trade switch
    {
        BotTrade.Forester => [Items.WoodenChest, Items.WoodPile, Items.Heap],
        BotTrade.Miner => [Items.IronChest, Items.Heap, Items.Belt, Items.Nails],
        // Copper a smelter has goes into its line's inserters and poles, so it makes nothing of copper for sale.
        BotTrade.Smelter => [Items.IronChest, Items.Belt, Items.Nails],
        BotTrade.Industrialist =>
        [
            Items.FastInserter, Items.LongInserter, Items.Splitter, Items.FastBelt, Items.UndergroundBelt,
            Items.LoadingChest, Items.ReceivingChest, Items.Inserter, Items.IronChest, Items.WoodenChest, Items.Pole, Items.Belt, Items.Nails,
        ],
        _ => [],
    };

    /// <summary>How many of a product the bot makes ahead before it waits for them to sell.</summary>
    public static long Stock(string product) => product switch
    {
        Items.FastInserter or Items.LongInserter or Items.Splitter or Items.LoadingChest or Items.ReceivingChest => 5,
        Items.Inserter or Items.IronChest or Items.FastBelt or Items.UndergroundBelt => 10,
        _ => 20,
    };

    /// <summary>
    /// What all the pieces of one item on the players' market (every seller's together) may be worth, at what the trader
    /// pays, before bots stop making more of it for sale: a few thousand bots each making a little still swamp a market.
    /// </summary>
    public const long MarketValueCap = 4000_00;

    /// <summary>Pieces of an item on the players' market past which bots make no more of it for sale.</summary>
    public static long MarketCap(long floorCents) => Math.Clamp(MarketValueCap / Math.Max(floorCents, 1), 10, 300);

    /// <summary>
    /// Machines on its plot the bot keeps working, like a player's workshop: it sets each to whatever pays best of what it has
    /// the ingredients for, fills it, keeps the fire going and takes the products out for the market (bread, sauerkraut and
    /// meals, smoked fish, charcoal, furniture and tar, stone blocks, glass, steel, bronze and bricks). Machines on power it
    /// leaves alone: the bot's generator powers only its line.
    /// </summary>
    public static readonly IReadOnlySet<string> Workshops = new HashSet<string>
    {
        Items.PicklingBarrel, Items.Bakery, Items.Kitchen, Items.Smokehouse, Items.CharcoalKiln, Items.Carpentry, Items.TarKiln,
        Items.Stonemason, Items.Foundry, Items.Glassworks,
    };

    /// <summary>How often a bot looks after one machine of its workshop.</summary>
    public static readonly TimeSpan WorkshopEvery = TimeSpan.FromMinutes(3);

    /// <summary>With this many of a machine's product unsold, the bot stops filling the machine until they sell.</summary>
    public const long WorkshopStock = 80;

    /// <summary>Batches of a machine's recipe the bot loads, and buys the missing ingredients for, at once.</summary>
    public const long WorkshopBatches = 5;

    /// <summary>Crafts of a product the bot buys the missing materials for at once (wood for five chests, say).</summary>
    public const long ShortfallCrafts = 5;

    /// <summary>
    /// What a bot pays at most for materials it lacks for its products, times what the trader pays for them (and never more
    /// than the trader charges): dear enough to buy what players have up, cheap enough that the product still pays.
    /// </summary>
    public const double MaterialsMarkup = 1.5;

    /// <summary>How far down a product the bot works out which parts to make first (fast inserter, inserter, nails).</summary>
    public const int PartsDepth = 3;

    /// <summary>Technologies the bot researches, in order, when it can pay for them; the line needs the first ones.</summary>
    public static string[] Research(BotTrade trade) => trade switch
    {
        BotTrade.Forester => ["logistics", "automation", "forestry_industry", "storage", "charring", "furniture"],
        BotTrade.Miner => ["logistics", "automation", "storage", "stonemasonry"],
        BotTrade.Smelter => ["logistics", "automation", "storage", "metallurgy", "glassmaking"],
        BotTrade.Industrialist =>
        [
            "logistics", "automation", "storage", "sorting", "fast_logistics", "underground_belts", "rail_freight", "long_inserters",
            "metallurgy", "glassmaking", "stonemasonry", "hydropower", "power_distribution",
        ],
        BotTrade.Farmer => ["milling", "logistics", "automation", "pickling", "baking", "oil_pressing", "cooking", "fishing"],
        _ => [],
    };

    /// <summary>What the bot looks for on the players' market: the inputs of its trade. What it still lacks for its line and its
    /// research it looks for too (<see cref="Bot"/> adds that).</summary>
    public static string[] Buys(BotTrade trade) => trade switch
    {
        BotTrade.Forester => [Items.Sapling, Items.Nails],
        BotTrade.Miner => [Items.Wood, Items.Coal],
        BotTrade.Farmer => [Items.Wheat, Items.Wood, Items.Coal],
        BotTrade.Industrialist => [Items.IronIngot, Items.CopperIngot, Items.IronOre, Items.Coal, Items.Wood],
        BotTrade.Smelter => [Items.IronOre, Items.CopperOre, Items.TinOre, Items.Coal, Items.Wood],
        _ => [],
    };

    /// <summary>
    /// A bot buys what its trade works with while it has fewer than this, and does not sell it below this either:
    /// a crafter that bought iron to make belts of only to put the iron itself up for sale got nowhere.
    /// </summary>
    public const long BuyUpTo = 200;

    /// <summary>How many of an item the bot holds back from selling, for its own work.</summary>
    public static long Keep(BotTrade trade, string item) => (trade, item) switch
    {
        (BotTrade.Forester, Items.Wood) => 60,
        (BotTrade.Forester, Items.Sapling) => 10,
        (BotTrade.Forester, Items.Nails) => 30,
        (BotTrade.Miner, Items.IronOre or Items.CopperOre or Items.TinOre or Items.SilverOre or Items.GoldOre) => 1000,
        (BotTrade.Miner, Items.Coal) => 30,
        (BotTrade.Miner, Items.Stone) => 40,
        (BotTrade.Miner, Items.IronIngot) => 40,
        (BotTrade.Miner, Items.Wood) => 50,
        (BotTrade.Farmer, Items.Wheat) => 60,
        (BotTrade.Farmer, Items.Stone) => 30,
        (BotTrade.Farmer, Items.Wood or Items.Coal) => 20,
        (BotTrade.Smelter, Items.IronOre or Items.CopperOre or Items.TinOre or Items.SilverOre or Items.GoldOre) => 1000,
        (BotTrade.Smelter, Items.Coal or Items.Wood) => 30,
        (BotTrade.Smelter, Items.IronIngot) => 20,
        (BotTrade.Industrialist, Items.IronOre or Items.CopperOre or Items.TinOre or Items.SilverOre or Items.GoldOre) => 1000,
        (BotTrade.Industrialist, Items.Coal) => 30,
        (BotTrade.Industrialist, Items.IronIngot or Items.CopperIngot) => 30,
        (BotTrade.Industrialist, Items.Wood or Items.Nails) => 30,
        // Parts of its dearer products, so that not every belt and inserter goes on the market.
        (BotTrade.Industrialist, Items.Belt) => 8,
        (BotTrade.Industrialist, Items.Inserter) => 2,
        _ => 0,
    };

    /// <summary>Items bots sell on the market when they have more than they keep.</summary>
    public static readonly IReadOnlySet<string> Goods = new HashSet<string>
    {
        Items.Wood, Items.Stone, Items.Sand, Items.Coal, Items.Sapling,
        Items.IronOre, Items.CopperOre, Items.TinOre, Items.SilverOre, Items.GoldOre,
        Items.IronIngot, Items.CopperIngot, Items.TinIngot, Items.SilverIngot, Items.GoldIngot,
        Items.Nails, Items.Belt, Items.WoodenChest, Items.IronChest, Items.WoodPile, Items.Inserter, Items.Pole,
        Items.Heap, Items.Splitter, Items.FastBelt, Items.UndergroundBelt, Items.FastInserter, Items.LongInserter,
        Items.LoadingChest, Items.ReceivingChest,
        Items.Wheat, Items.Carrot, Items.Cabbage, Items.Corn, Items.Potato, Items.Sunflower,
        Items.Flour, Items.Planks,
    };

    /// <summary>What sorting trash gives; bots sell it to the trader like new players do.</summary>
    public static readonly string[] Waste =
        [Items.WastePaper, Items.WastePlastic, Items.WasteAluminium, Items.WasteGlass, Items.WasteCopper];

    public static readonly string[] Ores = [Items.IronOre, Items.CopperOre, Items.TinOre, Items.SilverOre, Items.GoldOre];

    /// <summary>Crops a farming bot sows, by weight: mostly wheat, now and then something slower.</summary>
    public static readonly (string Crop, int Weight)[] Crops =
        [(Items.Wheat, 5), (Items.Carrot, 3), (Items.Cabbage, 2), (Items.Corn, 2), (Items.Potato, 2), (Items.Sunflower, 1)];
}
