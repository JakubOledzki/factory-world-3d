using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>What one step of a bot happens in: the time and the dice.</summary>
/// <remarks>
/// A bot does not hold back for the XP cap: it keeps working and trading, and the engine cuts whatever it earns
/// over the cap away after the step. Bots that stopped short of the cap (as they used to) did nothing at all
/// once they caught up with the best player, so nothing reached the market.
/// </remarks>
public sealed record BotWorld(DateTimeOffset Now, Random Random);

/// <summary>A plot the bot owns, with what it has seen on it this session.</summary>
internal sealed class BotPlot(int x, int y, int biome)
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public int Biome { get; } = biome;

    /// <summary>Tiles as plot.enter sent them; null until the bot has been on the plot this session.</summary>
    public byte[]? Tiles { get; set; }

    public int Size { get; set; }

    public (int X, int Y) Spawn { get; set; }

    /// <summary>Tiles with something to gather, nearest to the spawn first.</summary>
    public List<(int X, int Y)> Targets { get; } = [];

    /// <summary>Tiles found empty, nearest first, for fields and saplings.</summary>
    public List<(int X, int Y)> Free { get; } = [];

    public List<(int Id, int Units, DateTimeOffset? RefillAt, long UpgradeIron, long UpgradeWood, bool Upgradable)> Mines { get; } = [];

    public List<(int X, int Y, string? Crop, DateTimeOffset? ReadyAt)> Fields { get; } = [];

    public int Planted { get; set; }

    /// <summary>When the bot next looks what the town wants, for a plot in a town.</summary>
    public DateTimeOffset SupplyAt { get; set; }

    /// <summary>What stands on the plot (workbenches, furnaces, chests...), as plot.enter sent it.</summary>
    public List<(Guid Id, string Kind, int X, int Y)> Objects { get; } = [];

    /// <summary>Tiles taken by objects (all the tiles a machine covers), fields, trees, mines, the railway and the station.</summary>
    public HashSet<(int X, int Y)> Busy { get; } = [];

    /// <summary>Trees, rocks and dunes the bot clears to make room for its line.</summary>
    public List<(int X, int Y)> Clearing { get; } = [];
}

/// <summary>Where the bot's production line stands, found by its machine with the chests and the generator in their places.</summary>
internal sealed record BotLineSite(
    BotPlot Plot, Guid Input, Guid Machine, Guid Output, Guid Generator, IReadOnlyList<(Guid Id, string Kind, int X, int Y)> Parts);

/// <summary>What all the bots share: who is a bot, the market overview one of them fetched, and the board of calls for help.</summary>
public sealed class BotCommons(BotHelpBoard help, double mistakeChance = BotRules.MistakeChance)
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public BotHelpBoard Help { get; } = help;

    /// <summary>How likely a bot puts a part of its new line up the wrong way round (<see cref="BotRules.MistakeChance"/>).</summary>
    public double MistakeChance { get; set; } = mistakeChance;

    /// <summary>The market overview as some bot last fetched it, and when.</summary>
    internal (JsonElement Overview, DateTimeOffset At)? Overview { get; set; }

    public void AddBot(string character)
    {
        lock (_names)
        {
            _names.Add(character);
        }
    }

    /// <summary>The country a plot lies in, as the map the players' client has tells it.</summary>
    public Func<int, int, CancellationToken, Task<string?>> CountryOf { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

    private readonly HashSet<string> _people = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _protected = new(StringComparer.Ordinal);

    /// <summary>The law protects the player (below level 5) until then: bot detectives leave them be.</summary>
    public void Protect(string name, DateTimeOffset until)
    {
        lock (_protected)
        {
            _protected[name] = until;
        }
    }

    public bool IsProtected(string name, DateTimeOffset now)
    {
        lock (_protected)
        {
            return _protected.TryGetValue(name, out var until) && until > now;
        }
    }

    /// <summary>A player or bot some bot came across (in a town hall, at the border, in a report): bot detectives investigate them.</summary>
    public void Seen(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == "?")
        {
            return;
        }

        lock (_people)
        {
            _people.Add(name);
        }
    }

    /// <summary>Everyone the bots came across, the bots themselves included.</summary>
    public IReadOnlyList<string> People()
    {
        lock (_people)
        {
            lock (_names)
            {
                return _people.Concat(_names).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    private readonly Dictionary<Guid, string> _companies = [];
    private readonly HashSet<string> _unattached = new(StringComparer.Ordinal);

    /// <summary>A bot founded the company: other bots may join it. Bots join no player's company, where a player could take out
    /// what they pay in.</summary>
    public void Founded(Guid company, string bot)
    {
        lock (_companies)
        {
            _companies[company] = bot;
        }
    }

    public bool FoundedByBot(Guid company)
    {
        lock (_companies)
        {
            return _companies.ContainsKey(company);
        }
    }

    /// <summary>The bot is in no company: bots that founded one may invite it.</summary>
    public void Unattached(string character)
    {
        lock (_unattached)
        {
            _unattached.Add(character);
        }
    }

    /// <summary>The bot is in a company, or invited to one.</summary>
    public void Joined(string character)
    {
        lock (_unattached)
        {
            _unattached.Remove(character);
        }
    }

    /// <summary>Bots known to be in no company.</summary>
    public IReadOnlyList<string> Unattached()
    {
        lock (_unattached)
        {
            return _unattached.Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Whether a name on the market belongs to a bot: bots do not trade among themselves to sell on.</summary>
    public bool IsBot(string? character)
    {
        lock (_names)
        {
            return character is not null && _names.Contains(character);
        }
    }
}

/// <summary>
/// One bot: plays through <see cref="BotClient"/> like a player at the keyboard would, one action per step.
/// It remembers between steps what it saw (its inventory, its plots, the prices) and refreshes all of it
/// when a session starts. Every action that gives XP is skipped while it would carry the bot over its cap.
/// </summary>
/// <param name="grant">Credits the bot's wallet with this many cents (the server's allowance, see <see cref="BotRules.Allowance"/>).</param>
/// <param name="commons">What the bots share; a bot on its own has its own.</param>
public sealed partial class Bot(
    BotProfile profile, BotRow row, BotClient client, ILogger logger, BotCommons commons, Func<long, CancellationToken, Task>? grant = null)
{
    private readonly BotCommons _commons = commons;
    private readonly ILogger _logger = logger;
    private readonly Func<long, CancellationToken, Task>? _grant = grant;
    private readonly BotLine? _line = BotRules.Line(profile.Trade);
    private BotLineSite? _site;
    private DateTimeOffset _tendAt;
    private bool _recipeSet;
    private long _lastInputs = -1;
    private int _stalls;
    private int _placeFailures;
    private DateTimeOffset _gaveUpAt;
    private DateTimeOffset _wantingSince;
    private HelpRequest? _help;
    private DateTimeOffset _helpUntil;

    /// <summary>What is wrong with the bot's line that players may set right for it: parts to put up, a belt to turn.</summary>
    private readonly List<HelpTask> _fixes = [];
    private string _fixReason = HelpReasons.Parts;
    private DateTimeOffset _fixesSince;

    /// <summary>Whether the bot put a part of its line up the wrong way round this session already.</summary>
    private bool _mistaken;
    private long _traderIncome;
    private DateTimeOffset _contractsAt;
    private Dictionary<string, long> _needs = new(StringComparer.Ordinal);
    private Dictionary<string, long> _reserved = new(StringComparer.Ordinal);
    private Dictionary<string, long> _shortfalls = new(StringComparer.Ordinal);

    /// <summary>When the bot next looks after each machine of its workshop.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _workshopAt = [];

    /// <summary>What each machine of its workshop is short of for a few batches of what it is set to, to buy.</summary>
    private readonly Dictionary<Guid, Dictionary<string, long>> _workshopNeeds = [];

    /// <summary>What its workshop makes, to sell like its workbench products.</summary>
    private readonly HashSet<string> _workshopGoods = new(StringComparer.Ordinal);

    private readonly Dictionary<string, long> _inventory = new(StringComparer.Ordinal);
    private readonly List<BotPlot> _plots = [];
    private readonly Dictionary<string, long> _floor = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _traderSells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Xp, List<(string Item, long Quantity)> Ingredients, bool Locked, string Station, long Makes)> _recipes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Status, long Xp, List<(string Item, long Quantity)> Cost)> _techs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Width, int Depth)> _footprints = new(StringComparer.Ordinal);
    private readonly List<(string Kind, int X, int Y, int Rotation)> _garage = [];

    private bool _fresh;
    private bool _broken;
    private long _xp;
    private long _money;
    private List<string> _tools = [];
    private int _mapWidth;
    private int _mapHeight;
    private long _nextPlotBaseCents;
    private readonly Dictionary<int, double> _priceFactors = [];
    private Guid? _workbench;
    private Guid? _furnace;
    private bool _bin;
    private int _sortQueue;
    private DateTimeOffset _sortDoneAt;
    private int _craftQueue;

    /// <summary>The product the bot is making the parts of, to finish it before it starts on another.</summary>
    private string? _making;
    private int _openListings;
    private int _openOrders;
    private DateTimeOffset _lastListingAt;
    private readonly List<(long Id, string Item, bool Sold)> _listings = [];
    private int _searchRadius;
    private int _purchases;
    private int _steps;

    public BotProfile Profile { get; } = profile;

    /// <summary>What the bot is like (<see cref="BotTraits"/>).</summary>
    public BotTraits Traits { get; } = BotTraits.Of(profile.Name);

    /// <summary>What the bot has set its mind on; saved with its row at the end of each session.</summary>
    public BotGoals Goals { get; } = BotGoals.Load(row.Goals);

    /// <summary>Money the bot may spend on goods: what it has past what its goals hold back.</summary>
    private long Spare => _money - Goals.SavingCents;

    public BotRow Row { get; } = row;

    public BotClient Client { get; } = client;

    /// <summary>The bot's XP as it last saw it.</summary>
    public long Xp => _xp;

    /// <summary>Whether the bot has its production line standing (as far as it saw this session).</summary>
    public bool HasLine => _site is not null;

    /// <summary>The bot's call for help, while players may answer it.</summary>
    public HelpRequest? Help => _help;

    /// <summary>The session ended: whatever was seen is stale, and the next step looks around again first.</summary>
    public void EndSession()
    {
        Row.Goals = Goals.Save();
        _fresh = false;
        _purchases = 0;
        _traderIncome = 0;
        _help = null;
        _mistaken = false;
        _deposited = false;
        _politicsAt = default;
        _tieOpen = false;
        _lawAt = default;
        _townPlot = null;
        _countryLooked = false;
        _companyAt = default;
        _recipeSet = false;
        _lastInputs = -1;
        _placeFailures = 0;
        _gaveUpAt = default;
        foreach (var plot in _plots)
        {
            plot.Tiles = null;
        }
    }

    /// <summary>One action, like a player's click; nothing when there is nothing worth doing.</summary>
    public async Task<string?> StepAsync(BotWorld world, CancellationToken cancellationToken)
    {
        if (_broken)
        {
            return null;
        }

        if (!_fresh)
        {
            await RefreshAsync(cancellationToken);
            if (_broken)
            {
                return null;
            }
        }

        if (_commons.Help.TakeAnswer(Row.Name) is { } answered)
        {
            return await HelpedAsync(answered, world, cancellationToken);
        }

        if (_help is not null && _helpUntil <= world.Now)
        {
            // Nobody came: the bot sets about it itself, unless a player is at it right now.
            if (_commons.Help.Withdraw(Row.Name))
            {
                _help = null;
            }
            else
            {
                _helpUntil = world.Now + BotRules.HelpLeave;
            }
        }

        // Money also comes without a reply that shows it (tutorial rewards, sales to other players), so look now and then.
        // Listings sell while the bot is busy elsewhere, so it looks at its offers then too.
        if (++_steps % 10 == 0)
        {
            _money = (await SendAsync("wallet.get", null, cancellationToken)).Payload.Long("balanceCents");
            await MyOffersAsync(cancellationToken);
        }

        var candidates = Candidates(world);
        if (candidates.Count == 0)
        {
            return null;
        }

        var total = candidates.Sum(c => c.Weight);
        var roll = world.Random.Next(total);
        foreach (var (weight, name, run) in candidates)
        {
            roll -= weight;
            if (roll < 0)
            {
                var done = await run(cancellationToken);
                return done ? name : null;
            }
        }

        return null;
    }

    private long Have(string item) => _inventory.GetValueOrDefault(item);

    /// <summary>How many of <paramref name="quantity"/> pieces the bot sells to the trader, so as to stay within what it may take from NPCs in a session.</summary>
    private long TraderMay(string item, long quantity) =>
        Math.Clamp((BotRules.MaxTraderIncome - _traderIncome) / Math.Max(Floor(item), 1), 1, quantity);

    private long Floor(string item) => _floor.GetValueOrDefault(item);

    private bool Has(IEnumerable<(string Item, long Quantity)> cost) => cost.All(c => Have(c.Item) >= c.Quantity);

    /// <summary>Everything the bot could do now, with how likely it is to pick each.</summary>
    private List<(int Weight, string Name, Func<CancellationToken, Task<bool>> Run)> Candidates(BotWorld world)
    {
        var trade = Profile.Trade;
        var list = new List<(int, string, Func<CancellationToken, Task<bool>>)>();
        void Add(int weight, string name, Func<CancellationToken, Task<bool>> run) => list.Add((weight, name, run));
        _needs = Needs();
        _reserved = Reserved();
        _shortfalls = Shortfalls();
        var toSell = Surplus().Sum(s => s.Quantity);
        var traderPays = _traderIncome < BotRules.MaxTraderIncome;

        if (_sortQueue > 0 && _sortDoneAt <= world.Now)
        {
            Add(12, "garage", GarageAsync);
        }

        if (_sortQueue == 0 && _bin && Have(Items.Trash) > 0)
        {
            Add(_plots.Count == 0 ? 10 : 4, "sort", SortAsync);
        }

        if (Have(Items.Trash) < 5 && _sortQueue == 0 && _money >= 40_00 && _traderSells.ContainsKey(Items.Trash))
        {
            Add(3, "buy trash", ct => TradeAsync("market.buy", Items.Trash, 20, ct));
        }

        var waste = BotRules.Waste.FirstOrDefault(w => Have(w) >= 10);
        if (waste is not null && traderPays)
        {
            Add(8, "sell waste", ct => TradeAsync("market.sell", waste, TraderMay(waste, Have(waste)), ct));
        }

        if (_plots.Count < BotRules.MaxPlots(trade, Traits.Ambition) && _mapWidth > 0)
        {
            var biomes = BotRules.PlotBiomes(trade, _plots.Count, Traits.Ambition, _plots.Any(p => p.Biome == Biomes.City));
            var cheapest = biomes.Min(b => PlotPrice(b));
            if (_money >= cheapest + BotRules.Reserve)
            {
                // The first plot is what a new player saves up for.
                Add(_plots.Count == 0 ? 40 : 10, "buy plot", ct => BuyPlotAsync(biomes, ct));
            }

            SaveForPlot(cheapest, world);
        }
        else
        {
            Goals.Drop(BotGoals.Plot);
        }

        CompanyCandidates(world, Add);
        PoliticsCandidates(world, Add);
        BankCandidates(world, Add);
        PowerCandidates(world, Add);
        LawCandidates(world, Add);

        foreach (var plot in _plots.Where(p => p.Biome == Biomes.City && p.SupplyAt <= world.Now && traderPays))
        {
            Add(8, "supply town", ct => SupplyTownAsync(plot, world, ct));
        }

        var ore = BotRules.Ores.FirstOrDefault(o => Have(o) >= 5);
        foreach (var plot in _plots)
        {
            var mine = plot.Biome == Biomes.Mountains || plot.Biome == Biomes.Forest || plot.Biome == Biomes.Desert || plot.Biome == Biomes.Farmland;
            if (!mine)
            {
                continue;
            }

            var own = plot.Biome switch
            {
                Biomes.Forest => trade is BotTrade.Forester or BotTrade.Industrialist or BotTrade.Trader,
                Biomes.Mountains => trade is BotTrade.Miner or BotTrade.Industrialist,
                Biomes.Farmland => trade is BotTrade.Farmer,
                _ => false,
            };
            // With goods piling up unsold, the bot gathers less and sees to its line and the market instead.
            Add(toSell >= BotRules.Glut ? 2 : own ? 16 : 6, "work plot", ct => WorkPlotAsync(plot, world, ct));

            if (plot.Biome is Biomes.Forest or Biomes.Farmland && Have(Items.Sapling) > 0 && plot.Planted < BotRules.MaxPlanted)
            {
                Add(trade == BotTrade.Forester ? 5 : 2, "plant tree", ct => PlantTreeAsync(plot, world, ct));
            }

            foreach (var kind in plot.Tiles is null ? [] : StillToBuild(plot))
            {
                if (Have(kind) > 0 && plot.Free.Count > 0)
                {
                    Add(6, "build", ct => BuildAsync(plot, kind, ct));
                    break;
                }

                if (_workbench is not null && _craftQueue < 5 && !IsLocked(kind) && Has(Recipe(kind)))
                {
                    Add(4, "craft to build", ct => CraftAsync(kind, ct));
                    break;
                }
            }

            foreach (var (id, kind, _, _) in plot.Objects)
            {
                // The furnace of the line is the inserters' to work.
                if (kind == Items.Furnace && id != _site?.Machine)
                {
                    Add(ore is not null && Have(Items.Coal) >= 1 ? 6 : 2, "plot furnace", ct => FurnaceAsync(id, world, ct));
                }
                else if (BotRules.Workshops.Contains(kind) && id != _site?.Machine && _workshopAt.GetValueOrDefault(id) <= world.Now)
                {
                    Add(7, "workshop", ct => WorkshopAsync(id, world, ct));
                }
            }

            if (plot.Biome == Biomes.Mountains && plot.Mines.Any(m => m.Upgradable && Have(Items.IronIngot) >= m.UpgradeIron && Have(Items.Wood) >= m.UpgradeWood))
            {
                Add(4, "upgrade mine", ct => UpgradeMineAsync(plot, ct));
            }
        }

        if (_furnace is { } furnace)
        {
            Add(ore is not null && Have(Items.Coal) >= 1 ? 8 : 5, "furnace", ct => FurnaceAsync(furnace, world, ct));
        }
        else if (Have(Items.Furnace) > 0)
        {
            Add(10, "place furnace", ct => PlaceInGarageAsync(Items.Furnace, ct));
        }
        else if (ore is not null && _workbench is not null && _craftQueue < 5 && Has(Recipe(Items.Furnace)) && !IsLocked(Items.Furnace))
        {
            Add(8, "craft furnace", ct => CraftAsync(Items.Furnace, ct));
        }

        if (_workbench is not null && _craftQueue < 5 && ProductToMake(world, null) is not null)
        {
            Add(trade == BotTrade.Industrialist ? 8 : 5, "craft", ct => CraftForMarketAsync(world, ct));
        }

        if (_craftQueue > 0)
        {
            Add(2, "collect crafting", CraftingAsync);
        }

        foreach (var tech in ResearchNow())
        {
            if (_techs.TryGetValue(tech, out var t) && t.Status == "available" && Has(t.Cost))
            {
                Add(4, "research", ct => ResearchAsync(tech, ct));
                break;
            }
        }

        if (_techs.Values.Any(t => t.Status == "active"))
        {
            Add(1, "check research", ct => ParseResearchAsync(ct));
        }

        if (_line is not null && _help is null)
        {
            LineCandidates(_line, world, Add);
        }

        if (_openListings < BotRules.MaxListings && _lastListingAt + BotRules.ListEvery <= world.Now && Surplus().Any())
        {
            Add(trade == BotTrade.Trader ? 10 : 7, "list", ct => ListAsync(world, ct));
        }

        if (_openListings >= BotRules.MaxListings && _lastListingAt + BotRules.RelistAfter <= world.Now && _listings.Any(l => !l.Sold))
        {
            Add(3, "relist", ct => UnlistOldestAsync(world, ct));
        }

        var glut = Surplus().FirstOrDefault(s => s.Quantity >= 150 && Floor(s.Item) > 0).Item;
        if (glut is not null && traderPays)
        {
            Add(3, "quick sale", ct => TradeAsync("market.sell", glut, TraderMay(glut, 50), ct));
        }

        if (Spare > BotRules.Reserve + 5_00 && _purchases < BotRules.MaxPurchases)
        {
            Add(trade switch { BotTrade.Trader => 10, BotTrade.Industrialist => 8, _ => 6 }, "shop", ct => ShopAsync(world, null, ct));

            // Wood for chests on the market is quicker than planting and chopping it: the bot buys what it lacks, when it can.
            if (MaterialsOnSale() is { Count: > 0 } materials)
            {
                Add(12, "buy materials", ct => ShopAsync(world, materials, ct));
            }
        }

        Add(4, "fill order", ct => FillOrderAsync(world, ct));

        if (_openOrders < BotRules.MaxOrders && Spare > BotRules.Reserve + 50_00 && Wanted().Any())
        {
            Add(2, "place order", ct => PlaceOrderAsync(world, ct));
        }

        if (_contractsAt <= world.Now)
        {
            Add(6, "contracts", ct => ContractsAsync(world, ct));
        }

        return list;
    }
}
