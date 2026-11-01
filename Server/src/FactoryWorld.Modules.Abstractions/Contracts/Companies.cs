namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>The company a player is in.</summary>
/// <param name="Leads">Whether their role decides for the company: research, big projects (the owner and partners).</param>
public sealed record CompanyMembership(Guid Company, string Name, string Role, bool Leads);

/// <summary>Companies as other modules see them: who is in which, and what the company built together.</summary>
public interface ICompanies
{
    /// <summary>The player's company, or null when they are in none.</summary>
    Task<CompanyMembership?> MembershipAsync(Guid player, CancellationToken cancellationToken);

    /// <summary>Every member of the company.</summary>
    Task<IReadOnlyList<Guid>> MembersAsync(Guid company, CancellationToken cancellationToken);

    /// <summary>The big projects (<see cref="CompanyPerks"/>) the player's company finished; empty without one.</summary>
    Task<IReadOnlySet<string>> ProjectsAsync(Guid player, CancellationToken cancellationToken);
}

/// <summary>The technologies a company researched together, for every member.</summary>
public interface ICompanyTechnologies
{
    Task<bool> ResearchedAsync(Guid company, string technology, CancellationToken cancellationToken);
}

/// <summary>What finished big projects give every member of the company.</summary>
public static class CompanyPerks
{
    /// <summary>The company's port: ships between ports cost members half the fee.</summary>
    public const string Harbour = "harbour";

    public const double HarbourShipFactor = 0.5;

    /// <summary>The intercontinental railway: trains cost members 30% less.</summary>
    public const string Railway = "railway";

    public const double RailwayFactor = 0.7;

    /// <summary>The company's spaceport: the launch site the orbital mission needs.</summary>
    public const string Spaceport = "spaceport";

    /// <summary>The orbital mission: satellites guide trains and ships, members pay 20% less on every route.</summary>
    public const string Orbit = "orbital_mission";

    public const double OrbitFactor = 0.8;

    /// <summary>The orbital station: research of members takes 20% less time.</summary>
    public const string Station = "orbital_station";

    public const double StationResearchFactor = 0.8;

    /// <summary>A wonder of the world, which only one company on the server builds: the great library, research of members takes 15% less time.</summary>
    public const string Library = "great_library";

    public const double LibraryResearchFactor = 0.85;

    /// <summary>A wonder of the world: the world railway hub, members pay 15% less on every route.</summary>
    public const string Hub = "world_rail_hub";

    public const double HubFactor = 0.85;

    /// <summary>A wonder of the world: the colossus of industry, members get 10% more experience for everything.</summary>
    public const string Colossus = "colossus";

    public const int ColossusXpPercent = 10;

    /// <summary>The fee factor for a member sending goods, by rail or sea.</summary>
    public static double FeeFactor(IReadOnlySet<string> projects, bool bySea) =>
        (projects.Contains(Railway) ? RailwayFactor : 1) * (bySea && projects.Contains(Harbour) ? HarbourShipFactor : 1)
        * (projects.Contains(Orbit) ? OrbitFactor : 1) * (projects.Contains(Hub) ? HubFactor : 1);

    /// <summary>What a member's research time is multiplied by.</summary>
    public static double ResearchFactor(IReadOnlySet<string> projects) =>
        (projects.Contains(Station) ? StationResearchFactor : 1) * (projects.Contains(Library) ? LibraryResearchFactor : 1);
}

/// <summary>Technologies only a company can research, together; they open company features.</summary>
public static class CompanyTechs
{
    /// <summary>Big projects.</summary>
    public const string Megaprojects = "megaprojects";

    /// <summary>Going public on the stock exchange.</summary>
    public const string CorporateFinance = "corporate_finance";

    /// <summary>A bank in town, with branches in others, where players keep money on accounts.</summary>
    public const string Banking = "banking";
}
