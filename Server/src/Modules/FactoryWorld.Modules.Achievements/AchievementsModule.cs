using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Achievements;

/// <summary>One counter of one player, e.g. how much wood they have gathered.</summary>
public sealed class PlayerStat
{
    public Guid OwnerId { get; set; }

    public required string Stat { get; set; }

    public long Value { get; set; }
}

public sealed class UnlockedBadge
{
    public Guid OwnerId { get; set; }

    public required string BadgeId { get; set; }

    public DateTimeOffset UnlockedAt { get; set; }
}

public sealed class AchievementsDbContext(DbContextOptions<AchievementsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "achievements";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<PlayerStat>(e =>
        {
            e.ToTable("stats");
            e.HasKey(s => new { s.OwnerId, s.Stat });
            e.Property(s => s.OwnerId).HasColumnName("owner_id");
            e.Property(s => s.Stat).HasColumnName("stat").HasMaxLength(32);
            e.Property(s => s.Value).HasColumnName("value");
            // Rankings read the best players of one stat.
            e.HasIndex(s => new { s.Stat, s.Value });
        });
        model.Entity<UnlockedBadge>(e =>
        {
            e.ToTable("badges");
            e.HasKey(b => new { b.OwnerId, b.BadgeId });
            e.Property(b => b.OwnerId).HasColumnName("owner_id");
            e.Property(b => b.BadgeId).HasColumnName("badge_id").HasMaxLength(64);
            e.Property(b => b.UnlockedAt).HasColumnName("unlocked_at");
        });
    }
}

/// <summary>Achievement badges for milestones and rankings of the best players. Owns the <c>achievements</c> schema.</summary>
public sealed class AchievementsModule : IGameModule
{
    public string Name => "Achievements";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AchievementsDbContext>(AchievementsDbContext.Schema);
        services.AddScoped<AchievementService>();
        services.AddScoped<IPlayerRanking>(p => new StatRanking(p.GetRequiredService<GameDb>(), Stats.Produced, "Największa produkcja", 30,
            n => Polish.Count(n, "sztuka", "sztuki", "sztuk")));
        services.AddScoped<IPlayerRanking>(p => new StatRanking(p.GetRequiredService<GameDb>(), Stats.Badges, "Najwięcej odznak", 40,
            n => Polish.Count(n, "odznaka", "odznaki", "odznak")));
        services.AddSingleton<IMessageObserver, AchievementObserver>();
        services.AddSingleton<IMessageHandler, GetAchievementsHandler>();
        services.AddSingleton<IMessageHandler, WatchAchievementsHandler>();
        services.AddSingleton<IMessageHandler, GetRankingsHandler>();
        services.AddScoped<IHonours, BadgeHonours>();
        services.AddSingleton<IMessageHandler, GetShowcaseHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<AchievementsDbContext>(cancellationToken);
}
