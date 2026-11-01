using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Law;

/// <summary>The ranks of the police, from the newest officer up. Each one may do what the ones below it may, and more.</summary>
public static class Ranks
{
    /// <summary>Looks at the trains on the border, the reports and the cases; inspects a few trains a day.</summary>
    public const short Cadet = 0;

    /// <summary>Also sentences and dismisses cases.</summary>
    public const short Constable = 1;

    /// <summary>Also finds hidden compartments, investigates suspects and officers, asks for warrants and searches plots.</summary>
    public const short Detective = 2;

    /// <summary>Also takes officers on, promotes and fires them. Appointed by the president or the minister of the interior.</summary>
    public const short Chief = 3;

    public static readonly IReadOnlyList<string> Names = ["cadet", "constable", "detective", "chief"];

    public static readonly IReadOnlyList<string> PolishNames = ["Kadet", "Posterunkowy", "Detektyw", "Komendant"];

    /// <summary>The salary a day, from the country's treasury.</summary>
    public static readonly IReadOnlyList<long> SalaryCents = [50_00, 100_00, 150_00, 200_00];

    /// <summary>Trains an officer may inspect a day.</summary>
    public static readonly IReadOnlyList<int> Inspections = [3, 6, 10, 10];
}

/// <summary>What the police go after.</summary>
public static class Crimes
{
    /// <summary>Goods taken across a border without paying the duty, or that the country bans.</summary>
    public const string Smuggling = "smuggling";

    /// <summary>Goods the country bans, or lets only licence holders have, found in a search.</summary>
    public const string Contraband = "contraband";

    /// <summary>Trees cut without the forest certificate where the forest is protected.</summary>
    public const string Logging = "logging";

    /// <summary>Goods sold to the fence, or a hidden compartment bought from him.</summary>
    public const string BlackMarket = "black_market";

    /// <summary>Money given to an officer, or taken by one.</summary>
    public const string Bribe = "bribe";
}

public static class CaseStatus
{
    /// <summary>Waiting for an officer to sentence or dismiss it.</summary>
    public const string Open = "open";

    public const string Sentenced = "sentenced";

    public const string Dismissed = "dismissed";

    /// <summary>The suspect appealed the sentence; a judge decides.</summary>
    public const string Appealed = "appealed";

    public const string Upheld = "upheld";

    /// <summary>The judge overturned the sentence: the fine came back and the bans ended.</summary>
    public const string Overturned = "overturned";
}

public static class RuleKinds
{
    public const string Banned = "banned";

    public const string Licensed = "licensed";
}

public static class LawRules
{
    /// <summary>Players below this level cannot be prosecuted, nor join the police.</summary>
    public const int ProtectedBelowLevel = 5;

    /// <summary>The fence deals only with players of at least this level.</summary>
    public const int FenceLevel = 5;

    /// <summary>How long the police can still look at a train that crossed a border.</summary>
    public static readonly TimeSpan CrossingsKept = TimeSpan.FromHours(48);

    /// <summary>How long a sentenced player has to appeal; the officer's share of the fine is paid only after.</summary>
    public static readonly TimeSpan AppealWindow = TimeSpan.FromHours(24);

    /// <summary>An appeal no judge looked at in this time stands as sentenced.</summary>
    public static readonly TimeSpan AppealExpires = TimeSpan.FromHours(72);

    /// <summary>A granted warrant lasts this long.</summary>
    public static readonly TimeSpan WarrantLasts = TimeSpan.FromHours(24);

    public static readonly TimeSpan LicenceLasts = TimeSpan.FromDays(7);

    /// <summary>Most days of salary paid at once, when an officer comes back after a while.</summary>
    public const int MaxPaidDays = 7;

    /// <summary>The officer who sentenced gets this share of what the fine took, from the treasury.</summary>
    public const int BonusPercent = 10;

    /// <summary>A fine may be up to this many times what the crime was worth, and at least <see cref="MinFineCapCents"/>.</summary>
    public const int FineTimesValue = 3;

    public const long MinFineCapCents = 500_00;

    public const int MaxBanHours = 48;

    public const int MaxWantedHours = 72;

    public const int Investigations = 3;

    /// <summary>The chance each hidden trace of a suspect comes to light in an investigation, in percent.</summary>
    public const int InvestigationPercent = 50;

    /// <summary>Most reports one player makes a day.</summary>
    public const int ReportsPerDay = 5;

    public const int ReportLength = 200;

    /// <summary>Most sentences against one player in one country a day.</summary>
    public const int SentencesPerSuspectPerDay = 2;

    /// <summary>Customs at the border look at a smuggling train with this chance, in percent, more for every officer of the country.</summary>
    public const int CustomsBasePercent = 10;

    public const int CustomsPerOfficerPercent = 5;

    public const int CustomsMaxPercent = 50;

    /// <summary>More for a player on the wanted list.</summary>
    public const int CustomsWantedPercent = 25;

    /// <summary>The damage of one tree cut where the forest is protected, for the fine.</summary>
    public const long TreeWorthCents = 5_00;

    public const long MaxBribeCents = 1_000_000_00;

    /// <summary>Most items a country bans or licenses.</summary>
    public const int MaxRules = 40;

    public const long MaxLicenceFeeCents = 100_000_00;

    /// <summary>Goods the fence always takes: explosives, uranium and fossils dug out of the ground.</summary>
    public static readonly IReadOnlySet<string> AlwaysContraband = new HashSet<string>
    {
        Items.Dynamite, Items.UraniumOre, Items.Ammonite, Items.AncientCoin, Items.DinosaurBone,
    };

    /// <summary>What the fence pays more than the trader, in percent; still less than the trader asks for the same.</summary>
    public const int FencePercent = 140;

    /// <summary>The most the fence pays one player a day, in dollars: what a country bans is up to its government, so this keeps
    /// a ban on everyday goods from turning the fence into a mint.</summary>
    public const int FenceDollarsPerDay = 10_000;

    /// <summary>A hidden compartment for the player's trains.</summary>
    public const long CompartmentCents = 2_500_00;

    /// <summary>The worth of goods for duties and fines: what the trader pays for them.</summary>
    public static long Worth(IMarketPrices prices, string item, long quantity) => (prices.BuysCents(item) ?? 0) * quantity;

    /// <summary>The duty on goods at a rate, rounded up.</summary>
    public static long Duty(IMarketPrices prices, CountryTrade trade, IReadOnlyDictionary<string, long> goods) =>
        goods.Sum(g => (Worth(prices, g.Key, g.Value) * trade.PercentOn(g.Key) + 99) / 100);

    /// <summary>The most a fine for a crime worth <paramref name="valueCents"/> may be.</summary>
    public static long FineCap(long valueCents) => Math.Max(MinFineCapCents, valueCents * FineTimesValue);

    public static int Day(DateTimeOffset at) => (int)(at.ToUnixTimeSeconds() / 86_400);
}
