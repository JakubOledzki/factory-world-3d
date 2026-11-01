using FactoryWorld.Modules.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FactoryWorld.Modules.Bots;

/// <summary>A bot's account and when it plays; one row per name of the roster.</summary>
public sealed class BotRow
{
    /// <summary>The name in <see cref="BotRoster"/>; the character may be called slightly differently.</summary>
    public required string Name { get; set; }

    public required string CharacterName { get; set; }

    public Guid AccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When its next session starts.</summary>
    public DateTimeOffset NextSessionAt { get; set; }

    /// <summary>When the session it is in ends; null between sessions.</summary>
    public DateTimeOffset? SessionUntil { get; set; }

    /// <summary>Its goals (<see cref="BotGoals"/>) as JSON; null before it set any.</summary>
    public string? Goals { get; set; }
}

public sealed class BotsDbContext(DbContextOptions<BotsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "bots";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<BotRow>(e =>
        {
            e.ToTable("bots");
            e.HasKey(b => b.Name);
            e.Property(b => b.Name).HasColumnName("name").HasMaxLength(32);
            e.Property(b => b.CharacterName).HasColumnName("character_name").HasMaxLength(32);
            e.Property(b => b.AccountId).HasColumnName("account_id");
            e.Property(b => b.CreatedAt).HasColumnName("created_at");
            e.Property(b => b.NextSessionAt).HasColumnName("next_session_at");
            e.Property(b => b.SessionUntil).HasColumnName("session_until");
            e.Property(b => b.Goals).HasColumnName("goals").HasColumnType("jsonb");
            e.HasIndex(b => b.AccountId).IsUnique();
        });
    }
}
