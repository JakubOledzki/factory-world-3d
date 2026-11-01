using System.Security.Cryptography;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Recycling;

/// <summary>
/// The automatic sorting machine, a machine of its own in the garage. The player loads trash into
/// it; the bags wait in its queue and are sorted one after another, as long as by hand, while the
/// player is away too. Everything sorted stays in the machine until taken out. When the machine
/// holds <see cref="MaxStored"/> items it stops, and goes on by itself once some are taken out.
/// </summary>
public static class SorterRules
{
    /// <summary>Most trash that can wait in the machine to be sorted.</summary>
    public const int MaxQueue = 50;

    /// <summary>Most sorted items the machine holds.</summary>
    public const int MaxStored = 50;

    /// <summary>What the machine holds after sorting until <paramref name="now"/>; <paramref name="made"/>
    /// gets each sorted item with the moment it was done.</summary>
    /// <param name="time">How long one bag takes (<see cref="GarageUpgrades.SortTime"/>).</param>
    public static SorterState Advance(SorterState s, DateTimeOffset now, TimeSpan time, Func<string> roll, List<(string Item, DateTimeOffset At)> made)
    {
        if (s.StartedAt is not { } started)
        {
            return s;
        }

        var possible = (int)Math.Min(int.MaxValue, Math.Max(0, (now - started).Ticks / time.Ticks));
        var n = Math.Min(possible, Math.Min(s.Queued, MaxStored - s.StoredCount));
        var stored = new Dictionary<string, int>(s.Stored);
        for (var i = 1; i <= n; i++)
        {
            var item = roll();
            stored[item] = stored.GetValueOrDefault(item) + 1;
            made.Add((item, started + time * i));
        }

        var after = new SorterState(s.Queued - n, stored, null);
        return after.Queued > 0 && after.StoredCount < MaxStored ? after with { StartedAt = started + time * n } : after;
    }

    /// <summary>Starts an idle machine that has trash to sort and room for what comes out.</summary>
    public static SorterState Start(SorterState s, DateTimeOffset now) =>
        s.StartedAt is null && s.Queued > 0 && s.StoredCount < MaxStored ? s with { StartedAt = now } : s;

    public static string Roll() => RecyclingRules.DropForRoll(RandomNumberGenerator.GetInt32(100));
}

/// <param name="Queued">Trash waiting, the bag being sorted now included.</param>
/// <param name="Stored">Sorted items in the machine, by item.</param>
/// <param name="StartedAt">When the bag being sorted now went in; null while the machine stands.</param>
public sealed record SorterState(int Queued, IReadOnlyDictionary<string, int> Stored, DateTimeOffset? StartedAt)
{
    public static readonly SorterState Empty = new(0, new Dictionary<string, int>(), null);

    public int StoredCount => Stored.Values.Sum();
}

/// <summary>A row of <c>recycling.sorters</c>; a machine gets one when it is first used.</summary>
public sealed class Sorter
{
    public Guid ObjectId { get; set; }

    public Guid OwnerId { get; set; }

    public int Queued { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
}

/// <summary>What one sorting machine holds of one item.</summary>
public sealed class SorterItem
{
    public Guid ObjectId { get; set; }

    public string Item { get; set; } = "";

    public int Quantity { get; set; }
}

internal static class SorterRows
{
    public static async Task<SorterState?> ReadAsync(GameDb db, Guid id, CancellationToken cancellationToken)
    {
        int queued;
        DateTimeOffset? started;
        await using (var command = await db.CommandAsync(
            "SELECT queued, started_at FROM recycling.sorters WHERE object_id = @id FOR UPDATE", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            queued = reader.GetInt32(0);
            started = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }

        var stored = new Dictionary<string, int>();
        await using (var command = await db.CommandAsync(
            "SELECT item, quantity FROM recycling.sorter_items WHERE object_id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stored[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return new SorterState(queued, stored, started);
    }

    public static async Task WriteAsync(GameDb db, Guid owner, Guid id, SorterState s, CancellationToken cancellationToken)
    {
        await using (var command = await db.CommandAsync(
            """
            INSERT INTO recycling.sorters (object_id, owner_id, queued, started_at) VALUES (@id, @owner, @queued, @started)
            ON CONFLICT (object_id) DO UPDATE SET queued = @queued, started_at = @started
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("queued", s.Queued);
            command.Parameters.Add(new NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)s.StartedAt ?? DBNull.Value });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = await db.CommandAsync("DELETE FROM recycling.sorter_items WHERE object_id = @id", cancellationToken))
        {
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var (item, quantity) in s.Stored.Where(i => i.Value > 0))
        {
            await using var insert = await db.CommandAsync(
                "INSERT INTO recycling.sorter_items (object_id, item, quantity) VALUES (@id, @item, @quantity)", cancellationToken);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("item", item);
            insert.Parameters.AddWithValue("quantity", quantity);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task DeleteAsync(GameDb db, Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "DELETE FROM recycling.sorter_items WHERE object_id = @id; DELETE FROM recycling.sorters WHERE object_id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The trash used and the items made, for the production statistics.</summary>
    public static List<ProductionEntry> Production(List<(string Item, DateTimeOffset At)> made) =>
        made.SelectMany(m => new[] { ProductionEntry.Used(Items.Trash, 1, m.At), ProductionEntry.Made(m.Item, 1, m.At) }).ToList();
}

public sealed record SorterReply(
    Guid Id, SorterState State, DateTimeOffset ServerTime, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp, TimeSpan SortTime);

public sealed class SorterService(GameDb db, IInventory inventory, ICharacterProgress progress, IProductionStats stats, TimeProvider time)
{
    public Task<SorterReply> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await OpenAsync(owner, id, cancellationToken);
            return new SorterReply(id, state, time.GetUtcNow(), [], null, await SortTimeAsync(owner, cancellationToken));
        }, cancellationToken);

    /// <summary>Puts trash from the inventory into the machine's queue.</summary>
    public Task<SorterReply> LoadAsync(Guid owner, Guid id, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var state = await OpenAsync(owner, id, cancellationToken);
            if (state.Queued + quantity > SorterRules.MaxQueue)
            {
                throw new GameException("sorter_full", $"At most {SorterRules.MaxQueue} trash can wait in the sorting machine.");
            }

            await inventory.RemoveAsync(owner, Items.Trash, quantity, cancellationToken);
            state = SorterRules.Start(state with { Queued = state.Queued + quantity }, time.GetUtcNow());
            await SorterRows.WriteAsync(db, owner, id, state, cancellationToken);
            return new SorterReply(id, state, time.GetUtcNow(), [], null, await SortTimeAsync(owner, cancellationToken));
        }, cancellationToken);

    /// <summary>Takes sorted items out: one kind (<paramref name="item"/>), all of them (null), or the
    /// trash still waiting (<c>trash</c>), each as far as it fits in the inventory; the rest stays in. Each sorted item
    /// taken out gives the XP of one sorting.</summary>
    public Task<SorterReply> TakeAsync(Guid owner, Guid id, string? item, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await OpenAsync(owner, id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            if (item == Items.Trash)
            {
                var back = 0;
                if (state.Queued > 0)
                {
                    back = (int)await inventory.FitAsync(owner, Items.Trash, state.Queued, cancellationToken);
                    await inventory.AddAsync(owner, Items.Trash, back, cancellationToken);
                    taken.Add((Items.Trash, back));
                }

                // Trash left waiting keeps being sorted.
                state = back < state.Queued ? state with { Queued = state.Queued - back } : state with { Queued = 0, StartedAt = null };
            }
            else
            {
                if (item is not null && !RecyclingRules.Drops.Any(d => d.Item == item))
                {
                    throw new GameException("bad_payload", "The sorting machine holds no such item.");
                }

                var stored = new Dictionary<string, int>(state.Stored);
                var wanted = state.Stored.Where(s => s.Value > 0 && (item is null || s.Key == item)).ToList();
                foreach (var (kind, quantity) in wanted)
                {
                    var fits = (int)await inventory.RoomForAsync(owner, kind, quantity, cancellationToken);
                    if (fits == 0)
                    {
                        continue;
                    }

                    await inventory.AddAsync(owner, kind, fits, cancellationToken);
                    taken.Add((kind, fits));
                    if (fits < quantity)
                    {
                        stored[kind] = quantity - fits;
                    }
                    else
                    {
                        stored.Remove(kind);
                    }
                }

                if (taken.Count == 0 && wanted.Count > 0)
                {
                    throw InventoryExtensions.InventoryFull();
                }

                var count = taken.Sum(t => t.Item2);
                if (count > 0)
                {
                    xp = await progress.AddXpAsync(owner, count * RecyclingRules.XpPerSort, cancellationToken);
                }

                state = SorterRules.Start(state with { Stored = stored }, time.GetUtcNow());
            }

            await SorterRows.WriteAsync(db, owner, id, state, cancellationToken);
            return new SorterReply(id, state, time.GetUtcNow(), taken, xp, await SortTimeAsync(owner, cancellationToken));
        }, cancellationToken);

    /// <summary>Sorts every machine in the garage up to now, e.g. before the heating changes how fast they go.</summary>
    public async Task CatchUpAllAsync(Guid owner, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using (var command = await db.CommandAsync(
            "SELECT id FROM recycling.garage_objects WHERE owner_id = @owner AND kind = @kind", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("kind", Items.SortingMachine);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        foreach (var id in ids)
        {
            await OpenAsync(owner, id, cancellationToken);
        }
    }

    private async Task<TimeSpan> SortTimeAsync(Guid owner, CancellationToken cancellationToken) =>
        GarageUpgrades.SortTime(await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken));

    /// <summary>The player's sorting machine, locked until the transaction ends and sorted up to now.</summary>
    private async Task<SorterState> OpenAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using (var command = await db.CommandAsync(
            "SELECT kind FROM recycling.garage_objects WHERE id = @id AND owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("owner", owner);
            if (await command.ExecuteScalarAsync(cancellationToken) as string != Items.SortingMachine)
            {
                throw new GameException("no_sorter", "There is no such sorting machine in your garage.");
            }
        }

        var stored = await SorterRows.ReadAsync(db, id, cancellationToken) ?? SorterState.Empty;
        var made = new List<(string, DateTimeOffset)>();
        var state = SorterRules.Advance(stored, time.GetUtcNow(), await SortTimeAsync(owner, cancellationToken), SorterRules.Roll, made);
        if (made.Count > 0)
        {
            await stats.RecordAsync(owner, SorterRows.Production(made), cancellationToken);
            await SorterRows.WriteAsync(db, owner, id, state, cancellationToken);
        }

        return state;
    }
}

/// <summary>Empties a sorting machine that is being picked up: what it sorted and the trash still
/// waiting go to the inventory.</summary>
public sealed class SorterContents(GameDb db, IInventory inventory, ICharacterProgress progress, IProductionStats stats, TimeProvider time) : IMachineContents
{
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        if (await SorterRows.ReadAsync(db, id, cancellationToken) is not { } row)
        {
            return;
        }

        var made = new List<(string, DateTimeOffset)>();
        var sortTime = GarageUpgrades.SortTime(await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken));
        var state = SorterRules.Advance(row, time.GetUtcNow(), sortTime, SorterRules.Roll, made);
        await stats.RecordAsync(owner, SorterRows.Production(made), cancellationToken);
        foreach (var (item, quantity) in state.Stored.Where(s => s.Value > 0))
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }

        if (state.StoredCount > 0)
        {
            await progress.AddXpAsync(owner, state.StoredCount * RecyclingRules.XpPerSort, cancellationToken);
        }

        if (state.Queued > 0)
        {
            await inventory.AddAsync(owner, Items.Trash, state.Queued, cancellationToken);
        }

        await SorterRows.DeleteAsync(db, id, cancellationToken);
    }
}

public sealed record SorterRequest(Guid Sorter);

public sealed record LoadSorterRequest(Guid Sorter, int Quantity);

public sealed record TakeSorterRequest(Guid Sorter, string? Item);

public abstract class SorterHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<SorterService>(), client.RequireAccount(), payload, cancellationToken);
        var s = reply.State;
        return new Reply("sorter", new
        {
            id = reply.Id,
            name = Items.Names[Items.SortingMachine],
            queued = s.Queued,
            maxQueue = SorterRules.MaxQueue,
            // In the order of the drop table, so the list does not jump around.
            stored = RecyclingRules.Drops
                .Where(d => s.Stored.GetValueOrDefault(d.Item) > 0)
                .Select(d => new { id = d.Item, name = Items.Names[d.Item], quantity = s.Stored[d.Item] }),
            storedCount = s.StoredCount,
            maxStored = SorterRules.MaxStored,
            // Unix milliseconds of when the bag being sorted went in; the client compares it with serverTime.
            startedAt = s.StartedAt?.ToUnixTimeMilliseconds(),
            sortSeconds = reply.SortTime.TotalSeconds,
            serverTime = reply.ServerTime.ToUnixTimeMilliseconds(),
            drops = RecyclingRules.Drops.Select(d => new { id = d.Item, name = Items.Names[d.Item], percent = d.Percent }),
            taken = reply.Taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = reply.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
        });
    }

    protected abstract Task<SorterReply> RunAsync(SorterService sorters, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What the sorting machine holds now; the client asks again when the next bag is due.</summary>
public sealed class GetSorterHandler(IServiceScopeFactory scopes) : SorterHandler(scopes)
{
    public override string Type => "sorter.get";

    protected override Task<SorterReply> RunAsync(SorterService sorters, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        sorters.StateAsync(owner, Payload.Read<SorterRequest>(payload).Sorter, cancellationToken);
}

public sealed class LoadSorterHandler(IServiceScopeFactory scopes) : SorterHandler(scopes)
{
    public override string Type => "sorter.load";

    protected override Task<SorterReply> RunAsync(SorterService sorters, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LoadSorterRequest>(payload);
        return sorters.LoadAsync(owner, request.Sorter, request.Quantity, cancellationToken);
    }
}

public sealed class TakeSorterHandler(IServiceScopeFactory scopes) : SorterHandler(scopes)
{
    public override string Type => "sorter.take";

    protected override Task<SorterReply> RunAsync(SorterService sorters, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TakeSorterRequest>(payload);
        return sorters.TakeAsync(owner, request.Sorter, string.IsNullOrEmpty(request.Item) ? null : request.Item, cancellationToken);
    }
}
