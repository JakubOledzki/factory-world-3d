namespace FactoryWorld.Modules.Abstractions.Contracts;

// Public services that modules offer each other. Call them inside GameDb.InTransactionAsync
// when several must succeed together.

/// <summary>Items a player owns (owner = account id). Each tile of the inventory holds one stack of an item
/// (<see cref="Stacks.Of"/> pieces at most), so more of an item takes more tiles.</summary>
public interface IInventory
{
    /// <summary>Adds items whether or not they fit: for what comes back to the player (a machine picked up, an offer
    /// cancelled) or arrives without them doing anything. What does not fit lies past the tiles until room is made.</summary>
    Task AddAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken);

    /// <summary>Adds items the player takes (buys, gathers, takes out of something). Fails with
    /// <c>inventory_full</c> when they do not all fit (see <see cref="RoomAsync"/>).</summary>
    Task ReceiveAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken);

    /// <summary>Fails with <c>not_enough_items</c> when the player has fewer.</summary>
    Task RemoveAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken);

    Task<long> CountAsync(Guid owner, string item, CancellationToken cancellationToken);

    /// <summary>How many more pieces of the item fit: what its last stack still takes and whole stacks on the tiles no
    /// other item lies on; 0 when the inventory is full (or over full with things given back).</summary>
    Task<long> RoomAsync(Guid owner, string item, CancellationToken cancellationToken);

    /// <summary>Whether <see cref="ReceiveAsync"/> would take at least one piece of the item now.</summary>
    Task<bool> HasRoomAsync(Guid owner, string item, CancellationToken cancellationToken);
}

public static class InventoryExtensions
{
    /// <summary>How many of <paramref name="quantity"/> pieces the player can take now: all, or as many as fit. Fails with
    /// <c>inventory_full</c> when not one fits. For taking out of a machine or a store, where the rest stays inside.</summary>
    public static async Task<long> FitAsync(this IInventory inventory, Guid owner, string item, long quantity, CancellationToken cancellationToken)
    {
        var fits = await inventory.RoomForAsync(owner, item, quantity, cancellationToken);
        if (fits < 1 && quantity > 0)
        {
            throw InventoryFull();
        }

        return fits;
    }

    /// <summary>How many of <paramref name="quantity"/> pieces fit in the inventory now: all, some or none. For taking several
    /// things out of a machine at once, each as far as it fits.</summary>
    public static async Task<long> RoomForAsync(this IInventory inventory, Guid owner, string item, long quantity, CancellationToken cancellationToken) =>
        Math.Min(quantity, await inventory.RoomAsync(owner, item, cancellationToken));

    /// <summary>The error of an inventory with no room for what the player takes; <paramref name="room"/>, how many pieces would
    /// still fit, goes to the client in the error's details.</summary>
    public static GameException InventoryFull(long room = 0) =>
        new("inventory_full", "Your inventory is full: make room or put on a bigger backpack.", room > 0 ? new { room } : null);
}

/// <summary>A player's money, in cents, with every change recorded in a ledger.</summary>
public interface IWallet
{
    Task<long> CreditAsync(Guid owner, long cents, string reason, CancellationToken cancellationToken);

    /// <summary>Fails with <c>not_enough_money</c> when the balance is lower.</summary>
    Task<long> DebitAsync(Guid owner, long cents, string reason, CancellationToken cancellationToken);

    Task<long> BalanceAsync(Guid owner, CancellationToken cancellationToken);
}

public sealed record XpGain(long Xp, int Level, bool LeveledUp);

/// <summary>Character experience. Fails with <c>no_character</c> when the account has none yet.</summary>
public interface ICharacterProgress
{
    Task<XpGain> AddXpAsync(Guid accountId, long xp, CancellationToken cancellationToken);
}

/// <summary>Character names, e.g. to show who owns something.</summary>
public interface ICharacterNames
{
    /// <summary>Name by account id; accounts without a character are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>The account whose character has this name (any letter case), or null.</summary>
    Task<Guid?> AccountAsync(string name, CancellationToken cancellationToken);

    /// <summary>Characters whose name starts with <paramref name="prefix"/> (any case), at most <paramref name="limit"/>, by account id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(string prefix, int limit, CancellationToken cancellationToken);
}

/// <summary>Messages from the server itself in everybody's chat, e.g. that a player was the first in the world to do something.</summary>
public interface IAnnouncements
{
    /// <summary>Says <paramref name="text"/> (in Polish) to everybody; call it once the change it tells of is committed.</summary>
    void Announce(string text);
}

/// <summary>How a character looks, e.g. to draw other players walking on the same plot.</summary>
public interface ICharacterLooks
{
    /// <summary>Name and appearance (as the character editor saved it) by account id; accounts without a character are left out.</summary>
    Task<IReadOnlyDictionary<Guid, CharacterLook>> LooksAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}

/// <summary>A character's name and appearance; the appearance is sent to clients as it is.</summary>
public sealed record CharacterLook(string Name, object Appearance);

/// <summary>Characters' experience as a whole, e.g. to keep the server's bots behind the real players.</summary>
public interface ICharacterXp
{
    /// <summary>The most XP any character has, leaving out the accounts in <paramref name="excluding"/>; 0 when there is none.</summary>
    Task<long> HighestAsync(IReadOnlyCollection<Guid> excluding, CancellationToken cancellationToken);

    /// <summary>The character's XP, or null when the account has no character.</summary>
    Task<long?> XpAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The character's level; 1 when the account has no character.</summary>
    Task<int> LevelAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Takes experience away from a character that has more than <paramref name="maxXp"/>, so it has exactly that much.</summary>
    Task CapAsync(Guid accountId, long maxXp, CancellationToken cancellationToken);
}

/// <summary>The character's tool bar: tools (e.g. an axe) held in hand, outside the inventory.
/// One slot, more with a backpack on.</summary>
public interface IEquipment
{
    /// <summary>The tools in the tool bar, in slot order; empty slots left out.</summary>
    Task<IReadOnlyList<string>> ToolsAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Gives a new tool: into a free slot of the tool bar, otherwise into the inventory.</summary>
    Task GiveToolAsync(Guid accountId, string tool, CancellationToken cancellationToken);
}

/// <summary>The clothes a character wears and the level it has reached, and what they help with.</summary>
public interface IOutfit
{
    /// <summary>What the player gets in an industry (<see cref="Industries"/>), in percent: the worn clothes, the
    /// level, the last meal and every <see cref="IPlayerBonuses"/> (e.g. the garage's upgrades) together; 0 without any.</summary>
    Task<int> BonusAsync(Guid accountId, string industry, CancellationToken cancellationToken);

    /// <summary>The clothes worn by slot (<see cref="ClothingSlots.All"/>); empty when the account has no character.</summary>
    Task<IReadOnlyDictionary<string, string>> WornAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Puts <paramref name="item"/> (or nothing) on a slot as it is: the caller takes it from
    /// somewhere and keeps what was worn there. Fails with <c>no_character</c> without a character.</summary>
    Task SetWornAsync(Guid accountId, string slot, string? item, CancellationToken cancellationToken);
}

/// <summary>Bonuses a player earned apart from their clothes, e.g. by upgrading the garage.</summary>
public interface IPlayerBonuses
{
    /// <summary>Percent the player gets for <paramref name="bonus"/>: an industry (<see cref="Industries"/>)
    /// or <see cref="PlayerBonus.Xp"/>; 0 without any.</summary>
    Task<int> PercentAsync(Guid player, string bonus, CancellationToken cancellationToken);
}

public static class PlayerBonus
{
    /// <summary>More experience for everything the character does.</summary>
    public const string Xp = "xp";
}

/// <summary>The delivery pallet in the player's garage: what they buy waits there when the inventory is full.</summary>
public interface IGarageDeliveries
{
    /// <summary>Puts items on the pallet; false, with nothing done, when no pallet stands in the garage.</summary>
    Task<bool> DeliverAsync(Guid player, string item, long quantity, CancellationToken cancellationToken);
}

/// <summary>How many tiles a player's inventory has: <see cref="Items.InventorySlots"/>, more with a backpack on.</summary>
public interface IInventorySize
{
    Task<int> SlotsAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>Things a player has put somewhere: furniture in the garage, objects on a plot.
/// Each module that keeps such objects offers one of these.</summary>
public interface IPlacedObjects
{
    /// <summary>The kind (item id) of the player's object with this id, or null when it is not theirs or not here.</summary>
    Task<string?> KindAsync(Guid owner, Guid id, CancellationToken cancellationToken);
}

/// <summary>What a placed machine (e.g. a furnace) holds.</summary>
public interface IMachineContents
{
    /// <summary>The object is being picked up: everything in it goes back to the owner's inventory.</summary>
    Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken);
}

/// <summary>An object standing on a plot. <paramref name="Rotation"/> (0-3) counts quarter turns
/// anticlockwise seen from above, as in the garage: 0 faces south (the camera), 1 east, 2 north, 3 west.</summary>
public sealed record PlotObject(Guid Id, string Kind, int TileX, int TileY, int Rotation, Guid Owner);

/// <summary>What stands on plots, for modules that run machinery there.</summary>
public interface IPlotObjects
{
    Task<IReadOnlyList<PlotObject>> ObjectsAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>What every plot has built in and inserters can work with too (the railway station): one
    /// object per tile it covers, all with the same id, owned by the plot's owner.</summary>
    Task<IReadOnlyList<PlotObject>> FixturesAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>The plot an object stands on, or null when it stands on none.</summary>
    Task<(int X, int Y)?> PlotOfAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Fails with <c>not_owner</c> unless the player owns the plot, or may do <paramref name="right"/>
    /// there as a member of the company the plot belongs to.</summary>
    Task RequireOwnerAsync(Guid player, int x, int y, CancellationToken cancellationToken, PlotRight right = PlotRight.Build);
}

/// <summary>What a player does on a plot. Its owner may do everything; members of a company the
/// owner brought the plot into may do what their role allows.</summary>
public enum PlotRight
{
    /// <summary>Enter, gather, mine, farm, plant, work machines, put into storage and the station.</summary>
    Work,

    /// <summary>Take out of storage and the station, send trains.</summary>
    Take,

    /// <summary>Put objects on the plot, turn and pick them up, set splitters.</summary>
    Build,

    /// <summary>Only the owner, e.g. a geological survey.</summary>
    Owner,
    /// <summary>Set up and stop the station's train routes: the owner, and a company's partners and logistician.</summary>
    Routes,

    /// <summary>Come onto the plot and walk about: everyone who may do anything there, and on a public plot anyone at all.</summary>
    Visit,
}

/// <summary>A bought plot, for modules outside the world.</summary>
/// <param name="PriceCents">What its owner paid for it.</param>
public sealed record OwnedPlot(int X, int Y, Guid Owner, int Biome, long PriceCents = 0);

/// <summary>Who owns which plot.</summary>
public interface IPlotRegistry
{
    /// <summary>The plot if somebody bought it, otherwise null.</summary>
    Task<OwnedPlot?> FindAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>How many plots each owner has; owners without any are left out.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountsAsync(CancellationToken cancellationToken);
}

/// <summary>The company a plot belongs to, as one player sees it.</summary>
public sealed record PlotCompany(string Name, bool Member);

/// <summary>One thing a helper is to set right on someone else's plot: <paramref name="Kind"/> standing on the tile and facing
/// <paramref name="Rotation"/>. With <paramref name="Object"/> the object is there already and only needs turning; without, the
/// helper puts one from their own inventory there.</summary>
public sealed record HelpTask(string Kind, int TileX, int TileY, int Rotation, Guid? Object = null);

/// <summary>
/// Leave for one player to set things right on another's plot: to come onto it, to put there the objects
/// <paramref name="Tasks"/> name and to turn those it names, nothing else. Once every task is done the plot's
/// <paramref name="Owner"/> pays the helper <paramref name="PayCents"/>, and the objects put there are the owner's.
/// </summary>
public sealed record PlotPermit(
    int X, int Y, Guid Owner, Guid Helper, IReadOnlyList<HelpTask> Tasks, long PayCents, DateTimeOffset Until);

/// <summary>How far a helper got: tasks done of all, and once all are, what the owner paid them.</summary>
public sealed record HelpProgress(int Done, int Of, long PayCents, bool Finished, long? BalanceCents);

/// <summary>Leave to help on someone else's plot (bots ask players to set their lines going). Kept in memory: after a
/// restart whoever asked for help asks again.</summary>
public interface IPlotPermits
{
    /// <summary>Gives the leave, in place of any other on the same plot.</summary>
    void Grant(PlotPermit permit);

    /// <summary>Takes the leave on the plot back, if there is one.</summary>
    void Revoke(int x, int y);

    /// <summary>The leave on the plot, while it lasts.</summary>
    PlotPermit? Find(int x, int y);
}

/// <summary>Told when a helper has done every task of a leave and was paid.</summary>
public interface IPlotHelpListener
{
    void Helped(PlotPermit permit);
}

/// <summary>Plots that owners brought into a company, so its members work on them together.</summary>
public interface ICompanyPlots
{
    /// <summary>What the player may do on the plot as a member of its company; empty when it is in none of theirs.</summary>
    Task<IReadOnlySet<PlotRight>> RightsAsync(Guid player, int x, int y, CancellationToken cancellationToken);

    /// <summary>The company the plot is in, or null.</summary>
    Task<PlotCompany?> CompanyAsync(Guid viewer, int x, int y, CancellationToken cancellationToken);

    /// <summary>Plots of the player's company that other members own.</summary>
    Task<IReadOnlyList<(int X, int Y)>> SharedWithAsync(Guid player, CancellationToken cancellationToken);
}

/// <summary>Machinery that runs on a plot by itself (belts, inserters). It is brought up to now
/// before an object is put on the plot or taken off, so it never works with objects that were not there.</summary>
public interface IPlotAutomation
{
    Task CatchUpAsync(int x, int y, CancellationToken cancellationToken);
}

/// <summary>
/// A placed machine's inputs and outputs as an inserter sees them, during one run of a plot's
/// logistics. Every call gives the moment of the simulation, which only moves forward; the machine
/// does its own work (e.g. smelting) up to that moment first.
/// </summary>
public interface IMachinePort
{
    /// <summary>One item an inserter may take out now and that <paramref name="wanted"/> accepts, or null.</summary>
    string? Output(DateTimeOffset now, Func<string, bool> wanted);

    /// <summary>Takes out one piece of an item <see cref="Output"/> offered.</summary>
    void Take(string item, DateTimeOffset now);

    /// <summary>Whether one more piece of the item can go in now.</summary>
    bool Accepts(string item, DateTimeOffset now);

    /// <summary>Puts in one piece of an item the machine <see cref="Accepts"/>.</summary>
    void Put(string item, DateTimeOffset now);

    /// <summary>When the machine will next have something new to take out on its own (e.g. its next ingot), or null.</summary>
    DateTimeOffset? NextChange(DateTimeOffset now);

    /// <summary>True when what <see cref="Output"/> and <see cref="Accepts"/> answer changes only after a <see cref="Put"/>
    /// or a <see cref="Take"/>, or at <see cref="NextChange"/>; an inserter with nothing to do there can then wait for one
    /// of those instead of asking again every tick.</summary>
    bool Predictable => false;

    /// <summary>Writes the machine back, if anything changed.</summary>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Offered by each module that keeps machines with inputs and outputs (the furnace, the mill...),
/// so inserters work with any of them.</summary>
public interface IMachinePorts
{
    /// <summary>The machine, locked until the transaction ends; null when this module keeps no machine of this kind.</summary>
    Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken);

    /// <summary>About to open the machines among <paramref name="objects"/>: a module can read all of its own
    /// at once here, so that each <see cref="OpenAsync"/> after needs no query of its own.</summary>
    Task PrepareAsync(IReadOnlyList<PlotObject> objects, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Saves the ports this module opened; a module can write them all at once.</summary>
    async Task SaveAsync(IReadOnlyList<IMachinePort> ports, CancellationToken cancellationToken)
    {
        foreach (var port in ports)
        {
            await port.SaveAsync(cancellationToken);
        }
    }
}

/// <summary>
/// The time of a machine that runs on power: it goes with the clock while the machine has all the
/// power it needs, slower on too little and not at all without. A machine works out what it made
/// in its own time, so it needs to know nothing else about the grid.
/// </summary>
public interface IPowerClock
{
    /// <summary>The machine's time at <paramref name="now"/>; never ahead of it.</summary>
    DateTimeOffset At(DateTimeOffset now);

    /// <summary>When, from <paramref name="now"/> on, the machine's time reaches <paramref name="machineTime"/>; null if never on the power it has.</summary>
    DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now);

    /// <summary>How fast the machine works at <paramref name="now"/>: 1 with all its power, 0 without.</summary>
    double Speed(DateTimeOffset now);
}

/// <summary>One plot's power: generators, poles and the machines they reach.</summary>
public interface IPowerGrid
{
    /// <summary>The clock of a machine on the plot.</summary>
    IPowerClock Clock(Guid id);

    /// <summary>A generator as inserters see it (fuel goes in), or null when <paramref name="id"/> is none.</summary>
    IMachinePort? Port(Guid id);

    /// <summary>The grid as the client draws it (poles, wires, networks and how well each is powered), ready for JSON.</summary>
    object Summary(DateTimeOffset now);

    /// <summary>Writes the grid back, if fuel went into it.</summary>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Power on plots, for modules whose machines run on it. Within one request the same grid
/// is handed out until it is saved, so inserters feeding a generator power the machines at once.</summary>
public interface IPlotPower
{
    /// <summary>The plot's grid, locked until the transaction ends.</summary>
    Task<IPowerGrid> OpenAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>The clock of a machine on whatever plot it stands; one that has no power ever if it stands on none.</summary>
    Task<IPowerClock> ClockAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>The player's technology tree: research unlocks recipes, machines and what the trader sells.
/// An item no technology covers is open to everyone from the start.</summary>
public interface ITechnologies
{
    /// <summary>Every item the player cannot make or buy from the trader yet, with the name of the technology that unlocks it.</summary>
    Task<IReadOnlyDictionary<string, string>> LockedAsync(Guid player, CancellationToken cancellationToken);

    /// <summary>Fails with <c>locked</c> while the item is still to be researched.</summary>
    Task RequireUnlockedAsync(Guid player, string item, CancellationToken cancellationToken);

    /// <summary>Every technology the player has researched, by themselves or with their company.</summary>
    Task<IReadOnlySet<string>> ResearchedAsync(Guid player, CancellationToken cancellationToken);
}

/// <summary>What the trader usually pays for items, before its events move the prices.</summary>
public interface IMarketPrices
{
    /// <summary>The usual price the trader pays for one piece, in cents, or null when it buys none.</summary>
    long? BuysCents(string item);

    /// <summary>The usual price the trader asks for one piece, in cents, or null when it sells none.</summary>
    long? SellsCents(string item);

    /// <summary>The least the trader ever asks for one piece (in the market event that makes it cheapest), in cents, or null when it sells none.
    /// Whoever pays a player for goods from nothing pays less than this, or buying from the trader to sell on would pay.</summary>
    long? CheapestSellsCents(string item);
}

/// <summary>Lists goods on the players' market for a seller who is not at a computer: what an export train brings to a buying market.</summary>
public interface IExportMarket
{
    /// <summary>Puts the goods up for sale at the price, as many listings as the market's largest one takes; the seller's
    /// limit on open listings does not count them.</summary>
    Task ListAsync(Guid seller, string item, long quantity, long priceCents, CancellationToken cancellationToken);
}

/// <summary>The loading and receiving chests (<see cref="Items.IsLoadingChest"/>, <see cref="Items.IsReceivingChest"/>) on plots, for the
/// freight trains between a player's plots. Each call brings the plot's machinery up to now and locks it until the transaction ends.</summary>
public interface IFreightChests
{
    /// <summary>What the plot's loading chests hold together, by item.</summary>
    Task<IReadOnlyDictionary<string, long>> LoadingAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>Takes the goods out of the plot's loading chests, all or nothing; false, with nothing taken, when they hold fewer.</summary>
    Task<bool> TakeAsync(int x, int y, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken);

    /// <summary>Puts what fits of the goods into the plot's receiving chests, those whose filter is set to the item first; returns what
    /// did not fit (empty when everything did).</summary>
    Task<IReadOnlyDictionary<string, long>> UnloadAsync(int x, int y, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken);
}

/// <summary>The forest certificate: a player who planted at least as many trees as they cut over the last week, and
/// enough of them, sells timber dearer.</summary>
public interface IForestCertificate
{
    Task<bool> CertifiedAsync(Guid player, CancellationToken cancellationToken);
}

public static class ForestCertificate
{
    /// <summary>How much more the trader pays a certified player for timber, in percent.</summary>
    public const int BonusPercent = 15;
}

/// <summary>A country of the map; <paramref name="Plots"/> is how many plots of land it covers.</summary>
public sealed record CountryInfo(string Code, string Name, int Plots);

/// <summary>The countries of the map (Natural Earth's borders) and the plots players own in each.</summary>
public interface ICountryMap
{
    /// <summary>By name.</summary>
    IReadOnlyList<CountryInfo> All { get; }

    /// <summary>The country the plot lies in; null for the sea, lakes and Antarctica.</summary>
    CountryInfo? At(int x, int y);

    CountryInfo? Find(string? code);

    /// <summary>How many plots the player owns in each country.</summary>
    Task<IReadOnlyDictionary<string, int>> PlotsAsync(Guid owner, CancellationToken cancellationToken);

    /// <summary>How many plots are bought in each country.</summary>
    Task<IReadOnlyDictionary<string, int>> BoughtPlotsAsync(CancellationToken cancellationToken);
}

/// <summary>What the government a country's players elect changes in the world: the plot tax, what foreigners pay for
/// plots, rail fees, subsidies for goods delivered to towns. Money it takes goes into the country's treasury.</summary>
public interface INationalPolicy
{
    /// <summary>The plot tax in the country, in thousandths of a plot's worth a day.</summary>
    Task<int> PlotTaxPerMilleAsync(string country, CancellationToken cancellationToken);

    /// <summary>Of the plot tax paid on plots in the country, puts the treasury's share into it (the rest is gone from the game).</summary>
    Task CollectPlotTaxAsync(string country, long taxCents, CancellationToken cancellationToken);

    /// <summary>What a plot in the country costs the buyer, from its usual price: cheaper for citizens with the housing programme,
    /// dearer for foreigners by the fee the government set (that part goes into the treasury when <see cref="ChargePlotFeeAsync"/> takes it).
    /// Fails with <c>embargo</c> when the country does not sell plots to the buyer's citizens.</summary>
    Task<(long PriceCents, long FeeCents)> PlotPriceAsync(Guid buyer, string country, long priceCents, CancellationToken cancellationToken);

    /// <summary>Takes the foreigner's fee of a plot bought from the buyer's wallet into the treasury.</summary>
    Task ChargePlotFeeAsync(Guid buyer, string country, long feeCents, int x, int y, CancellationToken cancellationToken);

    /// <summary>What rail fees from a plot of the country are multiplied by: less with the national railway.</summary>
    Task<double> RailFactorAsync(string country, CancellationToken cancellationToken);

    /// <summary>Pays the player the country's subsidy on goods delivered to a town, from the treasury while it has money; returns what it paid.</summary>
    Task<long> SubsidizeAsync(Guid player, string country, string item, long cents, CancellationToken cancellationToken);

    /// <summary>The parties of the players who are in one, by account id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> PartiesAsync(IReadOnlyCollection<Guid> players, CancellationToken cancellationToken);
}

/// <summary>A company's cash, spent by its owner or treasurer from other modules (e.g. to back a candidate).</summary>
public interface ICompanyCash
{
    /// <summary>Takes money from the cash of the player's company; returns the company's name. Fails with <c>no_company</c>,
    /// <c>not_allowed</c> (not the owner or the treasurer) or <c>company_short</c>.</summary>
    Task<string> SpendAsync(Guid player, long cents, string reason, CancellationToken cancellationToken);
}

/// <summary>Something a player earned to show off: a badge, a world first, a place on a weekly podium.</summary>
/// <param name="Kind"><c>badge</c>, <c>first</c> or <c>trophy</c>.</param>
public sealed record Honour(string Kind, string Name, string Description, DateTimeOffset At);

/// <summary>Offered by each module that hands out honours, so a trophy case can show them all.</summary>
public interface IHonours
{
    /// <summary>The player's honours, newest first.</summary>
    Task<IReadOnlyList<Honour>> HonoursAsync(Guid player, CancellationToken cancellationToken);
}

/// <summary>Where a player's money came from and went, from the wallet's ledger.</summary>
public interface IMoneyHistory
{
    /// <summary>The changes between <paramref name="from"/> and <paramref name="to"/>, summed by reason, at most
    /// <paramref name="limit"/> reasons with the largest sums (in or out) first.</summary>
    Task<IReadOnlyList<(string Reason, long Cents)>> ByReasonAsync(Guid owner, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken);
}

/// <summary>What a player's factory made over a while, from the production statistics.</summary>
public interface IProductionHistory
{
    /// <summary>Pieces made of each item between <paramref name="from"/> and <paramref name="to"/>; only what the
    /// statistics still keep (a day), and only once the machines caught up with that time.</summary>
    Task<IReadOnlyDictionary<string, long>> MadeAsync(Guid owner, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <summary>The album of finds, for modules that hand out album items other than in a reply to the player (seasonal treats).</summary>
public interface IAlbumFinds
{
    /// <summary>Writes down that the player found these items; items not in the album, or found before, change nothing.</summary>
    Task RecordAsync(Guid owner, IReadOnlyCollection<string> items, CancellationToken cancellationToken);
}
