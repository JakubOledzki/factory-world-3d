using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryWorld.Modules.Law;

/// <summary>A player serving in a country's police.</summary>
public sealed class OfficerRow
{
    public Guid PlayerId { get; set; }

    public string Country { get; set; } = "";

    /// <summary>See <see cref="Ranks"/>.</summary>
    public short Rank { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>The salary is paid up to here.</summary>
    public DateTimeOffset PaidUntil { get; set; }
}

/// <summary>A player who asked to join a country's police; the chief takes them on or not.</summary>
public sealed class ApplicationRow
{
    public Guid PlayerId { get; set; }

    public string Country { get; set; } = "";

    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>An item a country bans, or lets only players with a licence have.</summary>
public sealed class CountryRuleRow
{
    public string Country { get; set; } = "";

    public string Item { get; set; } = "";

    /// <summary><c>banned</c> or <c>licensed</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>What a licence costs for a week, for a licensed item.</summary>
    public long FeeCents { get; set; }
}

/// <summary>A country's laws apart from items.</summary>
public sealed class CountryLawRow
{
    public string Country { get; set; } = "";

    /// <summary>Cutting trees without the forest certificate is forbidden.</summary>
    public bool ForestProtection { get; set; }
}

public sealed class LicenceRow
{
    public Guid PlayerId { get; set; }

    public string Country { get; set; } = "";

    public string Item { get; set; } = "";

    public DateTimeOffset Until { get; set; }
}

/// <summary>A train that went from one country into another, with the truth about it that only the server knows.</summary>
public sealed class CrossingRow
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public string FromCountry { get; set; } = "";

    public string ToCountry { get; set; } = "";

    public int FromX { get; set; }

    public int FromY { get; set; }

    public int ToX { get; set; }

    public int ToY { get; set; }

    public string[] Items { get; set; } = [];

    public long[] Quantities { get; set; } = [];

    /// <summary>The owner declared the goods and paid the duty; otherwise they smuggled them.</summary>
    public bool Declared { get; set; }

    /// <summary>The goods went in a hidden compartment.</summary>
    public bool Hidden { get; set; }

    public long DutyCents { get; set; }

    /// <summary>What a smuggling train got out of: the duty and the worth of the goods it should not have carried.</summary>
    public long EvadedCents { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>The officer who inspected the train (or the customs at the border: <see cref="Guid.Empty"/>).</summary>
    public Guid? InspectedBy { get; set; }
}

/// <summary>Something forbidden a player did, noted quietly; the police may find it.</summary>
public sealed class TraceRow
{
    public long Id { get; set; }

    public string Country { get; set; } = "";

    public Guid SuspectId { get; set; }

    /// <summary>Who did it with the suspect, e.g. who paid an officer a bribe.</summary>
    public Guid? AccompliceId { get; set; }

    /// <summary>See <see cref="Crimes"/>.</summary>
    public string Kind { get; set; } = "";

    public string Detail { get; set; } = "";

    public long ValueCents { get; set; }

    public DateTimeOffset At { get; set; }

    public bool Found { get; set; }
}

/// <summary>A player told the police about another one.</summary>
public sealed class ReportRow
{
    public long Id { get; set; }

    public string Country { get; set; } = "";

    public Guid ReporterId { get; set; }

    public Guid SuspectId { get; set; }

    public string Text { get; set; } = "";

    public DateTimeOffset At { get; set; }

    public bool Closed { get; set; }
}

/// <summary>A case against a player, from opening through the sentence to an appeal.</summary>
public sealed class CaseRow
{
    public long Id { get; set; }

    public string Country { get; set; } = "";

    public Guid SuspectId { get; set; }

    public string Kind { get; set; } = "";

    public string Detail { get; set; } = "";

    /// <summary>What the crime was worth: the duty evaded, the goods, the bribe.</summary>
    public long ValueCents { get; set; }

    /// <summary>Who opened it; <see cref="Guid.Empty"/> for the customs at the border.</summary>
    public Guid OpenedBy { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>See <see cref="CaseStatus"/>.</summary>
    public string Status { get; set; } = "";

    public long FineCents { get; set; }

    /// <summary>What the fine took from the suspect's wallet.</summary>
    public long CollectedCents { get; set; }

    public int TradeBanHours { get; set; }

    public int TravelBanHours { get; set; }

    public int WantedHours { get; set; }

    public Guid? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? AppealedAt { get; set; }

    public Guid? JudgedBy { get; set; }

    public bool BonusPaid { get; set; }

    /// <summary>For goods found in a search: the plot they lie on, to seize them there.</summary>
    public int? SeizeX { get; set; }

    public int? SeizeY { get; set; }

    public string[] SeizeItems { get; set; } = [];

    public long[] SeizeQuantities { get; set; } = [];
}

/// <summary>A punishment that lasts: no trade, no trains across borders, on the wanted list.</summary>
public sealed class SanctionRow
{
    public long Id { get; set; }

    public Guid PlayerId { get; set; }

    public string Kind { get; set; } = "";

    public string Country { get; set; } = "";

    public DateTimeOffset Until { get; set; }

    public long CaseId { get; set; }
}

/// <summary>A search warrant for a plot: a detective asks, a judge grants, the search uses it up.</summary>
public sealed class WarrantRow
{
    public long Id { get; set; }

    public string Country { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public Guid RequestedBy { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public Guid? GrantedBy { get; set; }

    public DateTimeOffset? GrantedAt { get; set; }

    public bool Used { get; set; }
}

/// <summary>How many times an officer did something on one day, against the daily limits.</summary>
public sealed class ActionCountRow
{
    public Guid PlayerId { get; set; }

    public int Day { get; set; }

    public string Kind { get; set; } = "";

    public int Count { get; set; }
}

/// <summary>A player whose trains have a hidden compartment, bought from the fence.</summary>
public sealed class CompartmentRow
{
    public Guid PlayerId { get; set; }

    public DateTimeOffset BoughtAt { get; set; }
}

public sealed class LawDbContext(DbContextOptions<LawDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "law";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<OfficerRow>(e =>
        {
            e.ToTable("officers", t => t.HasCheckConstraint("ck_officers_rank", "rank BETWEEN 0 AND 3"));
            e.HasKey(o => o.PlayerId);
            e.Property(o => o.PlayerId).HasColumnName("player_id");
            e.Property(o => o.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(o => o.Rank).HasColumnName("rank");
            e.Property(o => o.JoinedAt).HasColumnName("joined_at");
            e.Property(o => o.PaidUntil).HasColumnName("paid_until");
            e.HasIndex(o => o.Country);
        });
        model.Entity<ApplicationRow>(e =>
        {
            e.ToTable("applications");
            e.HasKey(a => a.PlayerId);
            e.Property(a => a.PlayerId).HasColumnName("player_id");
            e.Property(a => a.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(a => a.AppliedAt).HasColumnName("applied_at");
            e.HasIndex(a => a.Country);
        });
        model.Entity<CountryRuleRow>(e =>
        {
            e.ToTable("country_rules", t => t.HasCheckConstraint("ck_country_rules_fee", "fee_cents >= 0"));
            e.HasKey(r => new { r.Country, r.Item });
            e.Property(r => r.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(r => r.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(r => r.FeeCents).HasColumnName("fee_cents");
        });
        model.Entity<CountryLawRow>(e =>
        {
            e.ToTable("country_laws");
            e.HasKey(r => r.Country);
            e.Property(r => r.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(r => r.ForestProtection).HasColumnName("forest_protection");
        });
        model.Entity<LicenceRow>(e =>
        {
            e.ToTable("licences");
            e.HasKey(l => new { l.PlayerId, l.Country, l.Item });
            e.Property(l => l.PlayerId).HasColumnName("player_id");
            e.Property(l => l.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(l => l.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(l => l.Until).HasColumnName("until");
        });
        model.Entity<CrossingRow>(e =>
        {
            e.ToTable("crossings");
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.OwnerId).HasColumnName("owner_id");
            e.Property(c => c.FromCountry).HasColumnName("from_country").HasMaxLength(3);
            e.Property(c => c.ToCountry).HasColumnName("to_country").HasMaxLength(3);
            e.Property(c => c.FromX).HasColumnName("from_x");
            e.Property(c => c.FromY).HasColumnName("from_y");
            e.Property(c => c.ToX).HasColumnName("to_x");
            e.Property(c => c.ToY).HasColumnName("to_y");
            e.Property(c => c.Items).HasColumnName("items");
            e.Property(c => c.Quantities).HasColumnName("quantities");
            e.Property(c => c.Declared).HasColumnName("declared");
            e.Property(c => c.Hidden).HasColumnName("hidden");
            e.Property(c => c.DutyCents).HasColumnName("duty_cents");
            e.Property(c => c.EvadedCents).HasColumnName("evaded_cents");
            e.Property(c => c.At).HasColumnName("at");
            e.Property(c => c.InspectedBy).HasColumnName("inspected_by");
            e.HasIndex(c => new { c.ToCountry, c.At });
            e.HasIndex(c => new { c.OwnerId, c.At });
        });
        model.Entity<TraceRow>(e =>
        {
            e.ToTable("traces");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(t => t.SuspectId).HasColumnName("suspect_id");
            e.Property(t => t.AccompliceId).HasColumnName("accomplice_id");
            e.Property(t => t.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(t => t.Detail).HasColumnName("detail").HasMaxLength(200);
            e.Property(t => t.ValueCents).HasColumnName("value_cents");
            e.Property(t => t.At).HasColumnName("at");
            e.Property(t => t.Found).HasColumnName("found");
            e.HasIndex(t => new { t.Country, t.SuspectId, t.Found });
        });
        model.Entity<ReportRow>(e =>
        {
            e.ToTable("reports");
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(r => r.ReporterId).HasColumnName("reporter_id");
            e.Property(r => r.SuspectId).HasColumnName("suspect_id");
            e.Property(r => r.Text).HasColumnName("text").HasMaxLength(LawRules.ReportLength);
            e.Property(r => r.At).HasColumnName("at");
            e.Property(r => r.Closed).HasColumnName("closed");
            e.HasIndex(r => new { r.Country, r.Closed });
            e.HasIndex(r => new { r.ReporterId, r.At });
        });
        model.Entity<CaseRow>(e =>
        {
            e.ToTable("cases", t =>
            {
                t.HasCheckConstraint("ck_cases_fine", "fine_cents >= 0 AND collected_cents >= 0 AND collected_cents <= fine_cents");
                t.HasCheckConstraint("ck_cases_hours", "trade_ban_hours >= 0 AND travel_ban_hours >= 0 AND wanted_hours >= 0");
            });
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(c => c.SuspectId).HasColumnName("suspect_id");
            e.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(c => c.Detail).HasColumnName("detail").HasMaxLength(400);
            e.Property(c => c.ValueCents).HasColumnName("value_cents");
            e.Property(c => c.OpenedBy).HasColumnName("opened_by");
            e.Property(c => c.OpenedAt).HasColumnName("opened_at");
            e.Property(c => c.Status).HasColumnName("status").HasMaxLength(16);
            e.Property(c => c.FineCents).HasColumnName("fine_cents");
            e.Property(c => c.CollectedCents).HasColumnName("collected_cents");
            e.Property(c => c.TradeBanHours).HasColumnName("trade_ban_hours");
            e.Property(c => c.TravelBanHours).HasColumnName("travel_ban_hours");
            e.Property(c => c.WantedHours).HasColumnName("wanted_hours");
            e.Property(c => c.DecidedBy).HasColumnName("decided_by");
            e.Property(c => c.DecidedAt).HasColumnName("decided_at");
            e.Property(c => c.AppealedAt).HasColumnName("appealed_at");
            e.Property(c => c.JudgedBy).HasColumnName("judged_by");
            e.Property(c => c.BonusPaid).HasColumnName("bonus_paid");
            e.Property(c => c.SeizeX).HasColumnName("seize_x");
            e.Property(c => c.SeizeY).HasColumnName("seize_y");
            e.Property(c => c.SeizeItems).HasColumnName("seize_items");
            e.Property(c => c.SeizeQuantities).HasColumnName("seize_quantities");
            e.HasIndex(c => new { c.Country, c.Status });
            e.HasIndex(c => c.SuspectId);
            e.HasIndex(c => new { c.DecidedBy, c.BonusPaid });
        });
        model.Entity<SanctionRow>(e =>
        {
            e.ToTable("sanctions");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.PlayerId).HasColumnName("player_id");
            e.Property(s => s.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(s => s.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(s => s.Until).HasColumnName("until");
            e.Property(s => s.CaseId).HasColumnName("case_id");
            e.HasIndex(s => new { s.PlayerId, s.Kind, s.Until });
            e.HasIndex(s => s.CaseId);
        });
        model.Entity<WarrantRow>(e =>
        {
            e.ToTable("warrants");
            e.Property(w => w.Id).HasColumnName("id");
            e.Property(w => w.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(w => w.X).HasColumnName("x");
            e.Property(w => w.Y).HasColumnName("y");
            e.Property(w => w.RequestedBy).HasColumnName("requested_by");
            e.Property(w => w.RequestedAt).HasColumnName("requested_at");
            e.Property(w => w.GrantedBy).HasColumnName("granted_by");
            e.Property(w => w.GrantedAt).HasColumnName("granted_at");
            e.Property(w => w.Used).HasColumnName("used");
            e.HasIndex(w => new { w.Country, w.Used });
        });
        model.Entity<ActionCountRow>(e =>
        {
            e.ToTable("action_counts");
            e.HasKey(a => new { a.PlayerId, a.Day, a.Kind });
            e.Property(a => a.PlayerId).HasColumnName("player_id");
            e.Property(a => a.Day).HasColumnName("day");
            e.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(a => a.Count).HasColumnName("count");
        });
        model.Entity<CompartmentRow>(e =>
        {
            e.ToTable("compartments");
            e.HasKey(c => c.PlayerId);
            e.Property(c => c.PlayerId).HasColumnName("player_id");
            e.Property(c => c.BoughtAt).HasColumnName("bought_at");
        });
    }
}

/// <summary>
/// Police run by players in each country: officers and their ranks, customs at the border, cases and sentences, appeals,
/// search warrants, and the underworld they hunt (the fence, hidden compartments, bribes). Owns the <c>law</c> schema.
/// </summary>
public sealed class LawModule : IGameModule
{
    public string Name => "Law";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<LawDbContext>(LawDbContext.Schema);
        // A world without countries until a module that governs them registers its own.
        services.TryAddScoped<ICountries, NoCountries>();
        services.AddScoped<LawData>();
        services.AddScoped<PoliceService>();
        services.AddScoped<CaseService>();
        services.AddScoped<Underworld>();
        services.AddScoped<LawView>();
        services.AddScoped<BorderControl>();
        services.AddScoped<IBorderControl>(provider => provider.GetRequiredService<BorderControl>());
        services.AddScoped<ISanctions>(provider => provider.GetRequiredService<LawData>());
        services.AddScoped<ILawWatch>(provider => provider.GetRequiredService<Underworld>());
        foreach (var handler in LawHandlers.All)
        {
            services.AddSingleton(typeof(IMessageHandler), handler);
        }
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<LawDbContext>(cancellationToken);
}

/// <summary>Nobody governs any country: no borders, duties or police anywhere.</summary>
public sealed class NoCountries : ICountries
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public Task<string?> CountryOfPlotAsync(int x, int y, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> CitizenshipAsync(Guid player, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<IReadOnlySet<string>> OfficesAsync(Guid player, string country, CancellationToken cancellationToken) => Task.FromResult(None);

    public Task<Guid?> TreasuryAsync(string country, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<CountryTrade> TradeAsync(string from, string to, CancellationToken cancellationToken) => Task.FromResult(CountryTrade.Free);
}
