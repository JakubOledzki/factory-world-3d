using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Wallet;

public sealed class Wallet
{
    public Guid OwnerId { get; set; }

    public long BalanceCents { get; set; }
}

/// <summary>One change of a balance. The ledger explains every cent: sum of amounts = balance.</summary>
public sealed class LedgerEntry
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public long AmountCents { get; set; }

    public long BalanceAfterCents { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WalletDbContext(DbContextOptions<WalletDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "wallet";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Wallet>(e =>
        {
            e.ToTable("wallets", t => t.HasCheckConstraint("ck_wallets_balance", "balance_cents >= 0"));
            e.HasKey(w => w.OwnerId);
            e.Property(w => w.OwnerId).HasColumnName("owner_id");
            e.Property(w => w.BalanceCents).HasColumnName("balance_cents");
        });
        model.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger");
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.OwnerId).HasColumnName("owner_id");
            e.Property(l => l.AmountCents).HasColumnName("amount_cents");
            e.Property(l => l.BalanceAfterCents).HasColumnName("balance_after_cents");
            e.Property(l => l.Reason).HasColumnName("reason").HasMaxLength(200);
            e.Property(l => l.CreatedAt).HasColumnName("created_at");
            e.HasIndex(l => new { l.OwnerId, l.Id });
        });
    }
}

public sealed class WalletService(GameDb db, TimeProvider time, GameCounters counters) : IWallet
{
    public async Task<long> CreditAsync(Guid owner, long cents, string reason, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cents);
        return await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                """
                INSERT INTO wallet.wallets (owner_id, balance_cents) VALUES (@owner, @cents)
                ON CONFLICT (owner_id) DO UPDATE SET balance_cents = wallets.balance_cents + excluded.balance_cents
                RETURNING balance_cents
                """, cancellationToken);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("cents", cents);
            var balance = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            await RecordAsync(owner, cents, balance, reason, cancellationToken);
            return balance;
        }, cancellationToken);
    }

    public async Task<long> DebitAsync(Guid owner, long cents, string reason, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cents);
        return await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                """
                UPDATE wallet.wallets SET balance_cents = balance_cents - @cents
                WHERE owner_id = @owner AND balance_cents >= @cents
                RETURNING balance_cents
                """, cancellationToken);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("cents", cents);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long balance)
            {
                throw new GameException("not_enough_money", "You don't have enough money.");
            }

            await RecordAsync(owner, -cents, balance, reason, cancellationToken);
            return balance;
        }, cancellationToken);
    }

    public async Task<long> BalanceAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT balance_cents FROM wallet.wallets WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        return await command.ExecuteScalarAsync(cancellationToken) is long n ? n : 0;
    }

    private async Task RecordAsync(Guid owner, long amount, long balance, string reason, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO wallet.ledger (owner_id, amount_cents, balance_after_cents, reason, created_at)
            VALUES (@owner, @amount, @balance, @reason, @at)
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("balance", balance);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("at", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
        db.AfterCommit(() => counters.Money(owner, amount, reason));
    }
}

public sealed class MoneyHistory(GameDb db) : IMoneyHistory
{
    public async Task<IReadOnlyList<(string Reason, long Cents)>> ByReasonAsync(
        Guid owner, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT reason, sum(amount_cents)::bigint AS cents FROM wallet.ledger
            WHERE owner_id = @owner AND created_at >= @from AND created_at < @to
            GROUP BY reason
            ORDER BY abs(sum(amount_cents)) DESC
            LIMIT @limit
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("limit", limit);
        var rows = new List<(string, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }
}

public sealed class GetWalletHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "wallet.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var balance = await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(client.RequireAccount(), cancellationToken);
        return new Reply("wallet", new { balanceCents = balance });
    }
}

/// <summary>Players' money. Owns the <c>wallet</c> schema.</summary>
public sealed class WalletModule : IGameModule
{
    public string Name => "Wallet";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WalletDbContext>(WalletDbContext.Schema);
        services.AddScoped<IWallet, WalletService>();
        services.AddScoped<IMoneyHistory, MoneyHistory>();
        services.AddScoped<IPlayerRanking, WalletRanking>();
        services.AddScoped<IGameGauges, WalletGauges>();
        services.AddSingleton<IMessageHandler, GetWalletHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<WalletDbContext>(cancellationToken);
}
