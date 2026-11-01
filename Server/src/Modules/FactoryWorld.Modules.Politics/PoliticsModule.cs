using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Politics;

/// <summary>A player's country: they vote and stand there.</summary>
public sealed class CitizenRow
{
    public Guid PlayerId { get; set; }

    public string Country { get; set; } = "";

    public DateTimeOffset Since { get; set; }
}

/// <summary>A citizen standing for president in a term's election, with their programme and the deposit (held until the votes are counted).</summary>
public sealed class CandidateRow
{
    public string Country { get; set; } = "";

    public int Term { get; set; }

    public Guid PlayerId { get; set; }

    public int PlotTaxPerMille { get; set; }

    public int ForeignerFeePercent { get; set; }

    public string Slogan { get; set; } = "";

    public long DepositCents { get; set; }

    public bool Settled { get; set; }
}

public sealed class VoteRow
{
    public string Country { get; set; } = "";

    public int Term { get; set; }

    public Guid VoterId { get; set; }

    public Guid CandidateId { get; set; }
}

/// <summary>A country's president for a term (elected the term before; null when nobody stood) and the laws in force.</summary>
public sealed class GovernmentRow
{
    public string Country { get; set; } = "";

    public int Term { get; set; }

    public Guid? PresidentId { get; set; }

    public int PlotTaxPerMille { get; set; }

    public int ForeignerFeePercent { get; set; }

    public string? Subsidy { get; set; }

    public int TariffPercent { get; set; }

    public bool Recalled { get; set; }
}

public sealed class MinisterRow
{
    public string Country { get; set; } = "";

    public int Term { get; set; }

    public string Office { get; set; } = "";

    public Guid PlayerId { get; set; }
}

/// <summary>A tariff a country charges on one item instead of its general one.</summary>
public sealed class ItemTariffRow
{
    public string Country { get; set; } = "";

    public string Item { get; set; } = "";

    public int Percent { get; set; }
}

/// <summary>What the government put into the country's treasury (an <see cref="Abstractions.Contracts.IWallet"/> account) or took out, for its citizens to see.</summary>
public sealed class TreasuryLedgerRow
{
    public long Id { get; set; }

    public string Country { get; set; } = "";

    public long AmountCents { get; set; }

    public long BalanceAfterCents { get; set; }

    public string Reason { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InvestmentRow
{
    public string Country { get; set; } = "";

    public string Kind { get; set; } = "";

    public int Level { get; set; }
}

public sealed class RecallRow
{
    public string Country { get; set; } = "";

    public int Term { get; set; }

    public Guid SignerId { get; set; }
}

public sealed class SponsorshipRow
{
    public long Id { get; set; }

    public string Country { get; set; } = "";

    public int Term { get; set; }

    public Guid CandidateId { get; set; }

    public string Company { get; set; } = "";

    public long Cents { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PartyRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public Guid LeaderId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PartyMemberRow
{
    public Guid PlayerId { get; set; }

    public Guid PartyId { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>A relation between two countries: an alliance (one row each way) or an embargo (<see cref="Country"/> sells no plots to <see cref="Other"/>'s citizens).</summary>
public sealed class RelationRow
{
    public string Country { get; set; } = "";

    public string Other { get; set; } = "";

    public string Kind { get; set; } = "";

    public DateTimeOffset Since { get; set; }
}

/// <summary>An alliance one country offers another, until that one offers it back.</summary>
public sealed class AllianceOfferRow
{
    public string Country { get; set; } = "";

    public string Other { get; set; } = "";

    public DateTimeOffset OfferedAt { get; set; }
}

public sealed class PoliticsDbContext(DbContextOptions<PoliticsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "politics";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<CitizenRow>(e =>
        {
            e.ToTable("citizens");
            e.HasKey(c => c.PlayerId);
            e.Property(c => c.PlayerId).HasColumnName("player_id");
            e.Property(c => c.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(c => c.Since).HasColumnName("since");
            e.HasIndex(c => c.Country);
        });
        model.Entity<CandidateRow>(e =>
        {
            e.ToTable("candidates");
            e.HasKey(c => new { c.Country, c.Term, c.PlayerId });
            e.Property(c => c.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(c => c.Term).HasColumnName("term");
            e.Property(c => c.PlayerId).HasColumnName("player_id");
            e.Property(c => c.PlotTaxPerMille).HasColumnName("plot_tax_per_mille");
            e.Property(c => c.ForeignerFeePercent).HasColumnName("foreigner_fee_percent");
            e.Property(c => c.Slogan).HasColumnName("slogan").HasMaxLength(NationRules.MaxSloganLength);
            e.Property(c => c.DepositCents).HasColumnName("deposit_cents");
            e.Property(c => c.Settled).HasColumnName("settled");
            e.HasIndex(c => c.PlayerId);
        });
        model.Entity<VoteRow>(e =>
        {
            e.ToTable("votes");
            e.HasKey(v => new { v.Country, v.Term, v.VoterId });
            e.Property(v => v.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(v => v.Term).HasColumnName("term");
            e.Property(v => v.VoterId).HasColumnName("voter_id");
            e.Property(v => v.CandidateId).HasColumnName("candidate_id");
        });
        model.Entity<GovernmentRow>(e =>
        {
            e.ToTable("governments");
            e.HasKey(g => new { g.Country, g.Term });
            e.Property(g => g.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(g => g.Term).HasColumnName("term");
            e.Property(g => g.PresidentId).HasColumnName("president_id");
            e.Property(g => g.PlotTaxPerMille).HasColumnName("plot_tax_per_mille");
            e.Property(g => g.ForeignerFeePercent).HasColumnName("foreigner_fee_percent");
            e.Property(g => g.Subsidy).HasColumnName("subsidy").HasMaxLength(16);
            e.Property(g => g.TariffPercent).HasColumnName("tariff_percent");
            e.Property(g => g.Recalled).HasColumnName("recalled");
            e.HasIndex(g => g.PresidentId);
        });
        model.Entity<MinisterRow>(e =>
        {
            e.ToTable("ministers");
            e.HasKey(m => new { m.Country, m.Term, m.Office });
            e.Property(m => m.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(m => m.Term).HasColumnName("term");
            e.Property(m => m.Office).HasColumnName("office").HasMaxLength(16);
            e.Property(m => m.PlayerId).HasColumnName("player_id");
        });
        model.Entity<ItemTariffRow>(e =>
        {
            e.ToTable("item_tariffs");
            e.HasKey(t => new { t.Country, t.Item });
            e.Property(t => t.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(t => t.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(t => t.Percent).HasColumnName("percent");
        });
        model.Entity<TreasuryLedgerRow>(e =>
        {
            e.ToTable("treasury_ledger");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(l => l.AmountCents).HasColumnName("amount_cents");
            e.Property(l => l.BalanceAfterCents).HasColumnName("balance_after_cents");
            e.Property(l => l.Reason).HasColumnName("reason").HasMaxLength(200);
            e.Property(l => l.CreatedAt).HasColumnName("created_at");
            e.HasIndex(l => new { l.Country, l.Id });
        });
        model.Entity<InvestmentRow>(e =>
        {
            e.ToTable("investments");
            e.HasKey(i => new { i.Country, i.Kind });
            e.Property(i => i.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(i => i.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(i => i.Level).HasColumnName("level");
        });
        model.Entity<RecallRow>(e =>
        {
            e.ToTable("recalls");
            e.HasKey(r => new { r.Country, r.Term, r.SignerId });
            e.Property(r => r.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(r => r.Term).HasColumnName("term");
            e.Property(r => r.SignerId).HasColumnName("signer_id");
        });
        model.Entity<SponsorshipRow>(e =>
        {
            e.ToTable("sponsorships");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(s => s.Term).HasColumnName("term");
            e.Property(s => s.CandidateId).HasColumnName("candidate_id");
            e.Property(s => s.Company).HasColumnName("company").HasMaxLength(64);
            e.Property(s => s.Cents).HasColumnName("cents");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.HasIndex(s => new { s.Country, s.Term });
        });
        model.Entity<PartyRow>(e =>
        {
            e.ToTable("parties");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.Name).HasColumnName("name").HasMaxLength(NationRules.MaxPartyName);
            e.Property(p => p.LeaderId).HasColumnName("leader_id");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.HasIndex(p => p.Name).IsUnique();
        });
        model.Entity<PartyMemberRow>(e =>
        {
            e.ToTable("party_members");
            e.HasKey(m => m.PlayerId);
            e.Property(m => m.PlayerId).HasColumnName("player_id");
            e.Property(m => m.PartyId).HasColumnName("party_id");
            e.Property(m => m.JoinedAt).HasColumnName("joined_at");
            e.HasIndex(m => m.PartyId);
        });
        model.Entity<RelationRow>(e =>
        {
            e.ToTable("relations");
            e.HasKey(r => new { r.Country, r.Other, r.Kind });
            e.Property(r => r.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(r => r.Other).HasColumnName("other").HasMaxLength(3);
            e.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(r => r.Since).HasColumnName("since");
            e.HasIndex(r => r.Other);
        });
        model.Entity<AllianceOfferRow>(e =>
        {
            e.ToTable("alliance_offers");
            e.HasKey(o => new { o.Country, o.Other });
            e.Property(o => o.Country).HasColumnName("country").HasMaxLength(3);
            e.Property(o => o.Other).HasColumnName("other").HasMaxLength(3);
            e.Property(o => o.OfferedAt).HasColumnName("offered_at");
            e.HasIndex(o => o.Other);
        });
    }
}

/// <summary>Countries the players govern: citizenship, elections of a president, ministers, laws, the treasury, national
/// investments, parties and diplomacy. Owns the <c>politics</c> schema.</summary>
public sealed class PoliticsModule : IGameModule
{
    public string Name => "Politics";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PoliticsDbContext>(PoliticsDbContext.Schema);
        services.AddScoped<NationService>();
        services.AddScoped<INationalPolicy>(provider => provider.GetRequiredService<NationService>());
        services.AddScoped<ICountries>(provider => provider.GetRequiredService<NationService>());
        foreach (var handler in PoliticsHandlers.All)
        {
            services.AddSingleton<IMessageHandler>(provider => new PoliticsHandler(provider.GetRequiredService<IServiceScopeFactory>(), handler.Type, handler.Run));
        }
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<PoliticsDbContext>(cancellationToken);
}
