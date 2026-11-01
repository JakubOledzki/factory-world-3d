using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Rivalry;

/// <summary>Who was the first in the world to reach a milestone, and when.</summary>
public sealed class FirstRow
{
    public required string Id { get; set; }

    public Guid OwnerId { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>A player's score in one week's challenge.</summary>
public sealed class ChallengeScoreRow
{
    public int Week { get; set; }

    public Guid OwnerId { get; set; }

    public long Value { get; set; }
}

/// <summary>A place on a finished week's podium and the prize paid for it.</summary>
public sealed class ChallengeWinnerRow
{
    public int Week { get; set; }

    public int Place { get; set; }

    public Guid OwnerId { get; set; }

    public long Value { get; set; }

    public long PrizeCents { get; set; }
}

/// <summary>A week whose prizes were paid out.</summary>
public sealed class SettledWeekRow
{
    public int Week { get; set; }

    public DateTimeOffset SettledAt { get; set; }
}

public sealed class RivalryDbContext(DbContextOptions<RivalryDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "rivalry";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<FirstRow>(e =>
        {
            e.ToTable("firsts");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id").HasMaxLength(32);
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.At).HasColumnName("at");
        });
        model.Entity<ChallengeScoreRow>(e =>
        {
            e.ToTable("challenge_scores", t => t.HasCheckConstraint("ck_challenge_scores_value", "value >= 0"));
            e.HasKey(s => new { s.Week, s.OwnerId });
            e.Property(s => s.Week).HasColumnName("week");
            e.Property(s => s.OwnerId).HasColumnName("owner_id");
            e.Property(s => s.Value).HasColumnName("value");
            // The week's ranking reads the best scores.
            e.HasIndex(s => new { s.Week, s.Value });
        });
        model.Entity<ChallengeWinnerRow>(e =>
        {
            e.ToTable("challenge_winners");
            e.HasKey(w => new { w.Week, w.Place });
            e.Property(w => w.Week).HasColumnName("week");
            e.Property(w => w.Place).HasColumnName("place");
            e.Property(w => w.OwnerId).HasColumnName("owner_id");
            e.Property(w => w.Value).HasColumnName("value");
            e.Property(w => w.PrizeCents).HasColumnName("prize_cents");
            e.HasIndex(w => w.OwnerId);
        });
        model.Entity<SettledWeekRow>(e =>
        {
            e.ToTable("settled_weeks");
            e.HasKey(w => w.Week);
            e.Property(w => w.Week).HasColumnName("week").ValueGeneratedNever();
            e.Property(w => w.SettledAt).HasColumnName("settled_at");
        });
    }
}

/// <summary>
/// Rivalry between players: the firsts in the world (<see cref="Firsts"/>) and the challenge of the week
/// (<see cref="Challenges"/>). Like the achievements, it reads what players did from the requests the server
/// answered, so the modules that run the game need not know about it. Owns the <c>rivalry</c> schema.
/// </summary>
public sealed class RivalryModule : IGameModule
{
    public string Name => "Rivalry";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<RivalryDbContext>(RivalryDbContext.Schema);
        services.AddSingleton<RivalryCache>();
        services.AddScoped<RivalryService>();
        services.AddScoped<IPlayerRanking, ChallengeRanking>();
        services.AddScoped<IHonours, RivalryHonours>();
        services.AddSingleton<IMessageObserver, RivalryObserver>();
        services.AddSingleton<IMessageHandler, GetRivalryHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<RivalryDbContext>(cancellationToken);
}
