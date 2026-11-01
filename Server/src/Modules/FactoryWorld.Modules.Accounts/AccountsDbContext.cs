using FactoryWorld.Modules.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FactoryWorld.Modules.Accounts;

public sealed class Account
{
    public Guid Id { get; set; }

    /// <summary>Lower-cased, trimmed; unique.</summary>
    public required string Email { get; set; }

    public required string PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A login on one device. Only the SHA-256 of the token is stored.</summary>
public sealed class Session
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "accounts";

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

        model.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.Property(a => a.Email).HasMaxLength(AccountRules.MaxEmailLength);
            e.HasIndex(a => a.Email).IsUnique();
        });

        model.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => s.AccountId);
            e.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
