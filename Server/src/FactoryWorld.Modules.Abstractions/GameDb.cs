using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Abstractions;

/// <summary>
/// One database connection per request, shared by every module's DbContext, so that an operation
/// spanning modules (e.g. selling: remove items, add money) commits or fails as a whole.
/// </summary>
public sealed class GameDb(NpgsqlDataSource dataSource) : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = dataSource.CreateConnection();

    /// <summary>The transaction of <see cref="InTransactionAsync{T}"/>, if one is running.</summary>
    public NpgsqlTransaction? Transaction { get; private set; }

    /// <summary>Counts the transactions begun on this connection, so that what was read in one is known apart from the next.</summary>
    public int TransactionNumber { get; private set; }

    /// <summary>Runs <paramref name="work"/> in one transaction; a nested call joins the outer one.</summary>
    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (Transaction is not null)
        {
            return await work();
        }

        await OpenAsync(cancellationToken);
        await using var transaction = await Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        Transaction = transaction;
        TransactionNumber++;
        try
        {
            var result = await work();
            await transaction.CommitAsync(cancellationToken);
            var committed = _afterCommit;
            _afterCommit = null;
            committed?.ForEach(action => action());
            return result;
        }
        finally
        {
            Transaction = null;
            _afterCommit = null;
        }
    }

    private List<Action>? _afterCommit;

    /// <summary>Runs <paramref name="action"/> once the running transaction commits, and never when it rolls back;
    /// right away outside a transaction, where every command commits on its own.</summary>
    public void AfterCommit(Action action)
    {
        if (Transaction is null)
        {
            action();
        }
        else
        {
            (_afterCommit ??= []).Add(action);
        }
    }

    public Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken) =>
        InTransactionAsync(async () => { await work(); return true; }, cancellationToken);

    private readonly Dictionary<object, object?> _readAhead = [];
    private int _readAheadIn;

    /// <summary>
    /// Keeps a row read ahead of its use (many rows in one query instead of one query each) until
    /// <see cref="TakeReadAhead{T}"/> takes it; only inside a transaction, which the rows stay locked in.
    /// </summary>
    public void ReadAhead(object key, object? row)
    {
        if (Transaction is null)
        {
            return;
        }

        if (_readAheadIn != TransactionNumber)
        {
            _readAhead.Clear();
            _readAheadIn = TransactionNumber;
        }

        _readAhead[key] = row;
    }

    /// <summary>The row <see cref="ReadAhead"/> kept for <paramref name="key"/> in this transaction, once:
    /// a second read goes to the database again, and so does one after <see cref="ForgetReadAhead"/>.</summary>
    public bool TakeReadAhead<T>(object key, out T? row)
    {
        if (Transaction is not null && _readAheadIn == TransactionNumber && _readAhead.Remove(key, out var kept))
        {
            row = (T?)kept;
            return true;
        }

        row = default;
        return false;
    }

    /// <summary>The row was written: what was read ahead of it is out of date.</summary>
    public void ForgetReadAhead(object key) => _readAhead.Remove(key);

    /// <summary>A raw SQL command on the shared connection, inside the current transaction if any.</summary>
    public async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken);
        return new NpgsqlCommand(sql, Connection, Transaction);
    }

    /// <summary>Several commands sent to the database together, in one round trip, inside the current transaction if any.</summary>
    public async Task<NpgsqlBatch> BatchAsync(CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken);
        return new NpgsqlBatch(Connection, Transaction);
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (Connection.State != ConnectionState.Open)
        {
            await Connection.OpenAsync(cancellationToken);
        }
    }
}

/// <summary>Base for module DbContexts: saves inside the shared <see cref="GameDb"/> transaction when one is running.</summary>
public abstract class ModuleDbContext(DbContextOptions options, GameDb gameDb) : DbContext(options)
{
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var current = Database.CurrentTransaction?.GetDbTransaction();
        if (current != gameDb.Transaction)
        {
            await Database.UseTransactionAsync(gameDb.Transaction, cancellationToken);
        }

        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

public static class ModuleDbContextRegistration
{
    /// <summary>Registers a module's DbContext on the shared connection, with migrations kept in the module's own schema.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext =>
        services.AddDbContext<TContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<GameDb>().Connection,
            npgsql => npgsql.MigrationsHistoryTable("__migrations", schema)));

    /// <summary>Applies the module's pending migrations; call from <see cref="IGameModule.InitializeAsync"/>.</summary>
    public static async Task MigrateModuleAsync<TContext>(this IServiceProvider services, CancellationToken cancellationToken)
        where TContext : ModuleDbContext
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync(cancellationToken);
    }
}
