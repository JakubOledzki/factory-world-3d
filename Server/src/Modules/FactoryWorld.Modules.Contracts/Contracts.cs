using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryWorld.Modules.Contracts;

/// <summary>
/// An order to deliver a number of pieces of one item before a deadline, for a reward paid when the last piece
/// arrives. An NPC company gives each player orders of their own (<see cref="IssuerId"/> null); a player puts an
/// order on the board for others, and the reward waits here, out of their wallet, until someone delivers or the
/// order ends. One contractor takes it at a time and may deliver in parts: the pieces wait here too, and go back
/// to the contractor if they give up or miss the deadline.
/// </summary>
public sealed class Contract
{
    public long Id { get; set; }

    /// <summary>The player who gave the order, or null for an NPC company.</summary>
    public Guid? IssuerId { get; set; }

    /// <summary>The NPC company's name; null for players.</summary>
    public string? IssuerName { get; set; }

    /// <summary>Who is delivering: set when a player takes the order, and from the start for NPC orders.</summary>
    public Guid? ContractorId { get; set; }

    public required string ItemId { get; set; }

    public long Quantity { get; set; }

    public long Delivered { get; set; }

    public long RewardCents { get; set; }

    public long Xp { get; set; }

    /// <summary>When the order appears; a new NPC order may be waiting a few minutes in the future.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset Deadline { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>How it ended: <see cref="ContractOutcome"/>; null while open.</summary>
    public string? Outcome { get; set; }
}

public static class ContractOutcome
{
    public const string Done = "done";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Abandoned = "abandoned";
}

public sealed class ContractsDbContext(DbContextOptions<ContractsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "contracts";

    public DbSet<Contract> Contracts => Set<Contract>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Contract>(e =>
        {
            e.ToTable("contracts", t =>
            {
                t.HasCheckConstraint("ck_contracts_delivered", "delivered >= 0 AND delivered <= quantity");
                t.HasCheckConstraint("ck_contracts_reward", "reward_cents >= 0");
            });
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.IssuerId).HasColumnName("issuer_id");
            e.Property(c => c.IssuerName).HasColumnName("issuer_name").HasMaxLength(64);
            e.Property(c => c.ContractorId).HasColumnName("contractor_id");
            e.Property(c => c.ItemId).HasColumnName("item_id").HasMaxLength(64);
            e.Property(c => c.Quantity).HasColumnName("quantity");
            e.Property(c => c.Delivered).HasColumnName("delivered");
            e.Property(c => c.RewardCents).HasColumnName("reward_cents");
            e.Property(c => c.Xp).HasColumnName("xp");
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.Property(c => c.Deadline).HasColumnName("deadline");
            e.Property(c => c.AcceptedAt).HasColumnName("accepted_at");
            e.Property(c => c.ClosedAt).HasColumnName("closed_at");
            e.Property(c => c.Outcome).HasColumnName("outcome").HasMaxLength(16);
            e.HasIndex(c => c.Deadline).HasFilter("closed_at IS NULL");
            e.HasIndex(c => c.ContractorId).HasFilter("closed_at IS NULL");
            // Finished orders stay, and every player gets new NPC orders all the time: what a query looks for has an
            // index of its own, so no look walks through the old ones.
            e.HasIndex(c => new { c.IssuerId, c.Id }).HasFilter("closed_at IS NULL").HasDatabaseName("IX_contracts_open_issuer_id_id");
            e.HasIndex(c => new { c.IssuerId, c.ClosedAt }).HasFilter("closed_at IS NOT NULL").HasDatabaseName("IX_contracts_closed_issuer_id_closed_at");
            e.HasIndex(c => new { c.ContractorId, c.ClosedAt }).HasFilter("closed_at IS NOT NULL").HasDatabaseName("IX_contracts_closed_contractor_id_closed_at");
            e.HasIndex(c => c.Id).HasFilter("closed_at IS NULL AND contractor_id IS NULL AND issuer_id IS NOT NULL").HasDatabaseName("IX_contracts_board");
        });
    }
}

public static class ContractRules
{
    /// <summary>NPC orders every player has at a time.</summary>
    public const int NpcSlots = 3;

    /// <summary>After an NPC order is finished, the next one comes this much later.</summary>
    public static readonly TimeSpan NpcRefill = TimeSpan.FromMinutes(5);

    /// <summary>After an NPC order is given up, the next one comes this much later: giving up is no quick way to draw better orders.</summary>
    public static readonly TimeSpan NpcRefillAbandoned = TimeSpan.FromMinutes(30);

    /// <summary>Open orders one player may give at once.</summary>
    public const int MaxIssued = 10;

    /// <summary>Other players' orders one player may be delivering at once.</summary>
    public const int MaxTaken = 5;

    public const long MaxQuantity = 100_000;

    /// <summary>10 000 000 $.</summary>
    public const long MaxRewardCents = 1_000_000_000;

    public const int MinHours = 1;

    /// <summary>A week.</summary>
    public const int MaxHours = 168;

    /// <summary>XP for finishing another player's order; small, so that players cannot trade XP between them.</summary>
    public const long PlayerXp = 5;

    /// <summary>How many orders the board shows.</summary>
    public const int BoardSize = 50;

    /// <summary>Finished orders stay in the lists this long, so the players see how they ended.</summary>
    public static readonly TimeSpan ClosedShownFor = TimeSpan.FromDays(1);
}

public sealed record CreateRequest(string Item, long Quantity, long RewardCents, int Hours);

public sealed record ContractIdRequest(long Id);

public sealed record DeliverRequest(long Id, long Quantity);

/// <summary>What a change did: the player's balance, the XP gained (when an order was finished) and whether it was.</summary>
public sealed record ContractResult(long BalanceCents, XpGain? Xp, bool Completed, long RewardCents);

public sealed class ContractService(
    GameDb db, IInventory inventory, IWallet wallet, ICharacterProgress progress, ICharacterNames names, TimeProvider time)
{
    /// <summary>The player's NPC orders, the board of other players' orders, the orders they deliver and those they gave.</summary>
    public Task<object> ViewAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var now = time.GetUtcNow();
            await SettleExpiredAsync(now, cancellationToken);
            await FillNpcSlotsAsync(player, now, cancellationToken);

            var since = now - ContractRules.ClosedShownFor;
            var npc = await QueryAsync(
                "issuer_id IS NULL AND contractor_id = @player AND closed_at IS NULL ORDER BY created_at, id",
                player, since, cancellationToken);
            var board = await QueryAsync(
                $"issuer_id IS NOT NULL AND issuer_id <> @player AND contractor_id IS NULL AND closed_at IS NULL ORDER BY id DESC LIMIT {ContractRules.BoardSize}",
                player, since, cancellationToken);
            var taken = await QueryAsync(
                "issuer_id IS NOT NULL AND contractor_id = @player AND closed_at IS NULL ORDER BY deadline, id",
                player, since, cancellationToken);
            var issued = (await QueryAsync("issuer_id = @player AND closed_at IS NULL", player, since, cancellationToken))
                .Concat(await QueryAsync("issuer_id = @player AND closed_at > @since", player, since, cancellationToken))
                .OrderByDescending(c => c.Id).ToList();
            var finished = await QueryAsync(
                $"contractor_id = @player AND outcome = '{ContractOutcome.Done}' AND closed_at > @since ORDER BY closed_at DESC LIMIT 20",
                player, since, cancellationToken);

            var people = board.Concat(taken).Concat(issued).Concat(finished)
                .SelectMany(c => new[] { c.IssuerId, c.ContractorId })
                .OfType<Guid>().Distinct().ToList();
            var known = await names.NamesAsync(people, cancellationToken);
            object Show(Contract c) => Public(c, player, known);

            var waiting = npc.Where(c => c.CreatedAt > now).ToList();
            return (object)new
            {
                serverTime = now.ToUnixTimeMilliseconds(),
                npc = npc.Where(c => c.CreatedAt <= now).Select(Show),
                nextNpcAt = waiting.Count > 0 ? waiting.Min(c => c.CreatedAt).ToUnixTimeMilliseconds() : (long?)null,
                board = board.Select(Show),
                taken = taken.Select(Show),
                issued = issued.Select(Show),
                finished = finished.Select(Show),
                limits = new
                {
                    maxIssued = ContractRules.MaxIssued,
                    maxTaken = ContractRules.MaxTaken,
                    maxQuantity = ContractRules.MaxQuantity,
                    maxRewardCents = ContractRules.MaxRewardCents,
                    minHours = ContractRules.MinHours,
                    maxHours = ContractRules.MaxHours,
                },
            };
        }, cancellationToken);

    /// <summary>Puts an order on the board: the reward leaves the wallet now and waits on the order.</summary>
    public Task<ContractResult> CreateAsync(Guid issuer, CreateRequest request, CancellationToken cancellationToken)
    {
        if (!Items.Exists(request.Item ?? ""))
        {
            throw new GameException("unknown_item", $"Unknown item '{request.Item}'.");
        }

        CheckQuantity(request.Quantity);
        if (request.RewardCents is < 1 or > ContractRules.MaxRewardCents)
        {
            throw new GameException("bad_reward", $"The reward must be 0.01-{ContractRules.MaxRewardCents / 100} $.");
        }

        if (request.Hours is < ContractRules.MinHours or > ContractRules.MaxHours)
        {
            throw new GameException("bad_deadline", $"The deadline must be {ContractRules.MinHours}-{ContractRules.MaxHours} hours away.");
        }

        return db.InTransactionAsync(async () =>
        {
            await LockPlayerAsync(issuer, cancellationToken);
            if (await CountAsync("issuer_id = @player AND closed_at IS NULL", issuer, cancellationToken) >= ContractRules.MaxIssued)
            {
                throw new GameException("too_many_contracts", $"You can give at most {ContractRules.MaxIssued} open orders.");
            }

            var balance = await wallet.DebitAsync(issuer, request.RewardCents,
                $"contract {request.Quantity} {request.Item}", cancellationToken);
            var now = time.GetUtcNow();
            await InsertAsync(new Contract
            {
                IssuerId = issuer,
                ItemId = request.Item!,
                Quantity = request.Quantity,
                RewardCents = request.RewardCents,
                Xp = ContractRules.PlayerXp,
                CreatedAt = now,
                Deadline = now.AddHours(request.Hours),
            }, cancellationToken);
            return new ContractResult(balance, null, false, 0);
        }, cancellationToken);
    }

    /// <summary>Takes another player's order from the board: from now on only this player delivers it.</summary>
    public Task<ContractResult> AcceptAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await LockPlayerAsync(player, cancellationToken);
            var contract = await LockOpenAsync(id, cancellationToken);
            if (contract.IssuerId is null || contract.ContractorId is { } other && other != player)
            {
                throw new GameException("contract_taken", "Someone else is already delivering this order.");
            }

            if (contract.IssuerId == player)
            {
                throw new GameException("own_contract", "This is your own order.");
            }

            if (contract.ContractorId == player)
            {
                return new ContractResult(await wallet.BalanceAsync(player, cancellationToken), null, false, 0);
            }

            if (await CountAsync("issuer_id IS NOT NULL AND contractor_id = @player AND closed_at IS NULL", player, cancellationToken) >= ContractRules.MaxTaken)
            {
                throw new GameException("too_many_taken", $"You can deliver at most {ContractRules.MaxTaken} players' orders at once.");
            }

            await UpdateAsync("contractor_id = @player, accepted_at = @now", contract.Id, player, cancellationToken);
            return new ContractResult(await wallet.BalanceAsync(player, cancellationToken), null, false, 0);
        }, cancellationToken);

    /// <summary>Hands over pieces from the inventory; the last piece finishes the order and pays the reward.</summary>
    public Task<ContractResult> DeliverAsync(Guid player, DeliverRequest request, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckQuantity(request.Quantity);
            await LockPlayerAsync(player, cancellationToken);
            var contract = await LockOpenAsync(request.Id, cancellationToken);
            if (contract.ContractorId != player)
            {
                throw new GameException("not_contractor", "Take the order first.");
            }

            if (request.Quantity > contract.Quantity - contract.Delivered)
            {
                throw new GameException("bad_quantity", "The order needs fewer pieces.");
            }

            await inventory.RemoveAsync(player, contract.ItemId, request.Quantity, cancellationToken);
            contract.Delivered += request.Quantity;
            if (contract.Delivered < contract.Quantity)
            {
                await UpdateAsync($"delivered = {contract.Delivered}", contract.Id, player, cancellationToken);
                return new ContractResult(await wallet.BalanceAsync(player, cancellationToken), null, false, 0);
            }

            var now = time.GetUtcNow();
            await CloseAsync(contract, ContractOutcome.Done, now, cancellationToken);
            if (contract.IssuerId is { } issuer)
            {
                await inventory.AddAsync(issuer, contract.ItemId, contract.Quantity, cancellationToken);
            }
            else
            {
                await ReplaceNpcAsync(player, contract.ItemId, now + ContractRules.NpcRefill, cancellationToken);
            }

            var balance = await wallet.CreditAsync(player, contract.RewardCents,
                $"contract done {contract.Quantity} {contract.ItemId}", cancellationToken);
            var xp = contract.Xp > 0 ? await progress.AddXpAsync(player, contract.Xp, cancellationToken) : null;
            return new ContractResult(balance, xp, true, contract.RewardCents);
        }, cancellationToken);

    /// <summary>The contractor gives up: delivered pieces come back. A player's order goes back on the board,
    /// an NPC order is gone and the next one comes a few minutes later.</summary>
    public Task<ContractResult> AbandonAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await LockPlayerAsync(player, cancellationToken);
            var contract = await LockOpenAsync(id, cancellationToken);
            if (contract.ContractorId != player)
            {
                throw new GameException("not_contractor", "You are not delivering this order.");
            }

            if (contract.Delivered > 0)
            {
                await inventory.AddAsync(player, contract.ItemId, contract.Delivered, cancellationToken);
            }

            if (contract.IssuerId is null)
            {
                var now = time.GetUtcNow();
                await CloseAsync(contract, ContractOutcome.Abandoned, now, cancellationToken);
                await ReplaceNpcAsync(player, contract.ItemId, now + ContractRules.NpcRefillAbandoned, cancellationToken);
            }
            else
            {
                await UpdateAsync("contractor_id = NULL, accepted_at = NULL, delivered = 0", contract.Id, player, cancellationToken);
            }

            return new ContractResult(await wallet.BalanceAsync(player, cancellationToken), null, false, 0);
        }, cancellationToken);

    /// <summary>The issuer takes back an order nobody is delivering yet; the reward goes back to the wallet.</summary>
    public Task<ContractResult> CancelAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var contract = await LockOpenAsync(id, cancellationToken);
            if (contract.IssuerId != player)
            {
                throw new GameException("no_contract", "This order is no longer there.");
            }

            if (contract.ContractorId is not null)
            {
                throw new GameException("contract_taken", "Someone is already delivering this order.");
            }

            await CloseAsync(contract, ContractOutcome.Cancelled, time.GetUtcNow(), cancellationToken);
            var balance = await wallet.CreditAsync(player, contract.RewardCents,
                $"contract cancelled {contract.Quantity} {contract.ItemId}", cancellationToken);
            return new ContractResult(balance, null, false, 0);
        }, cancellationToken);

    /// <summary>Ends every order whose deadline has passed: delivered pieces go back to the contractor and a
    /// player's reward back to the issuer. Orders another request is working on are left for the next one.</summary>
    private async Task SettleExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = new List<Contract>();
        await using (var command = await db.CommandAsync(
            $"SELECT {Columns} FROM contracts.contracts WHERE closed_at IS NULL AND deadline <= @now ORDER BY deadline LIMIT 100 FOR UPDATE SKIP LOCKED",
            cancellationToken))
        {
            command.Parameters.AddWithValue("now", now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                expired.Add(Read(reader));
            }
        }

        foreach (var contract in expired)
        {
            // Ended at its deadline, so a player away for days finds a new NPC order at once.
            await CloseAsync(contract, ContractOutcome.Expired, contract.Deadline, cancellationToken);
            if (contract is { ContractorId: { } contractor, Delivered: > 0 })
            {
                await inventory.AddAsync(contractor, contract.ItemId, contract.Delivered, cancellationToken);
            }

            if (contract.IssuerId is { } issuer)
            {
                await wallet.CreditAsync(issuer, contract.RewardCents,
                    $"contract expired {contract.Quantity} {contract.ItemId}", cancellationToken);
            }
        }
    }

    /// <summary>Gives the player new NPC orders up to <see cref="ContractRules.NpcSlots"/>.</summary>
    private async Task FillNpcSlotsAsync(Guid player, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LockPlayerAsync(player, cancellationToken);
        var open = await QueryAsync("issuer_id IS NULL AND contractor_id = @player AND closed_at IS NULL", player, now, cancellationToken);
        var items = open.Select(c => c.ItemId).ToHashSet();
        for (var i = open.Count; i < ContractRules.NpcSlots; i++)
        {
            items.Add(await InsertNpcAsync(player, items, now, cancellationToken));
        }
    }

    /// <summary>The next NPC order after one for <paramref name="item"/> ended, for another item than the player's other orders.</summary>
    private async Task ReplaceNpcAsync(Guid player, string item, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var open = await QueryAsync("issuer_id IS NULL AND contractor_id = @player AND closed_at IS NULL", player, at, cancellationToken);
        await InsertNpcAsync(player, open.Select(c => c.ItemId).Append(item).ToHashSet(), at, cancellationToken);
    }

    /// <summary>A new NPC order for the player, appearing at <paramref name="at"/>, for an item not in <paramref name="avoid"/>.</summary>
    private async Task<string> InsertNpcAsync(Guid player, IReadOnlySet<string> avoid, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (template, quantity, issuer) = NpcContracts.Pick(avoid, Random.Shared);
        var reward = template.CentsPerPiece * quantity;
        await InsertAsync(new Contract
        {
            IssuerName = issuer,
            ContractorId = player,
            ItemId = template.Item,
            Quantity = quantity,
            RewardCents = reward,
            Xp = NpcContracts.Xp(reward),
            CreatedAt = at,
            Deadline = at.AddHours(template.Hours),
            AcceptedAt = at,
        }, cancellationToken);
        return template.Item;
    }

    /// <summary>One request at a time changes a player's contracts, so limits and NPC slots stay right.</summary>
    private async Task LockPlayerAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", cancellationToken);
        command.Parameters.AddWithValue("key", $"contracts:{player}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>An open order before its deadline, locked until the transaction ends.</summary>
    private async Task<Contract> LockOpenAsync(long id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync($"SELECT {Columns} FROM contracts.contracts WHERE id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        Contract? contract;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            contract = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }

        var now = time.GetUtcNow();
        if (contract is null || contract.ClosedAt is not null || contract.CreatedAt > now)
        {
            throw new GameException("no_contract", "This order is no longer there.");
        }

        if (contract.Deadline <= now)
        {
            throw new GameException("contract_expired", "The deadline of this order has passed.");
        }

        return contract;
    }

    private async Task CloseAsync(Contract contract, string outcome, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "UPDATE contracts.contracts SET closed_at = @at, outcome = @outcome, delivered = @delivered WHERE id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", contract.Id);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("delivered", contract.Delivered);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Sets columns of one contract; <c>@player</c> and <c>@now</c> may be used in <paramref name="set"/>.</summary>
    private async Task UpdateAsync(string set, long id, Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync($"UPDATE contracts.contracts SET {set} WHERE id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("player", player);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertAsync(Contract c, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO contracts.contracts
                (issuer_id, issuer_name, contractor_id, item_id, quantity, delivered, reward_cents, xp, created_at, deadline, accepted_at)
            VALUES (@issuer, @issuerName, @contractor, @item, @quantity, 0, @reward, @xp, @createdAt, @deadline, @acceptedAt)
            """, cancellationToken);
        command.Parameters.AddWithValue("issuer", (object?)c.IssuerId ?? DBNull.Value);
        command.Parameters.AddWithValue("issuerName", (object?)c.IssuerName ?? DBNull.Value);
        command.Parameters.AddWithValue("contractor", (object?)c.ContractorId ?? DBNull.Value);
        command.Parameters.AddWithValue("item", c.ItemId);
        command.Parameters.AddWithValue("quantity", c.Quantity);
        command.Parameters.AddWithValue("reward", c.RewardCents);
        command.Parameters.AddWithValue("xp", c.Xp);
        command.Parameters.AddWithValue("createdAt", c.CreatedAt);
        command.Parameters.AddWithValue("deadline", c.Deadline);
        command.Parameters.AddWithValue("acceptedAt", (object?)c.AcceptedAt ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> CountAsync(string where, Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync($"SELECT COUNT(*) FROM contracts.contracts WHERE {where}", cancellationToken);
        command.Parameters.AddWithValue("player", player);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private const string Columns =
        "id, issuer_id, issuer_name, contractor_id, item_id, quantity, delivered, reward_cents, xp, created_at, deadline, accepted_at, closed_at, outcome";

    private async Task<List<Contract>> QueryAsync(string where, Guid player, DateTimeOffset since, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync($"SELECT {Columns} FROM contracts.contracts WHERE {where}", cancellationToken);
        command.Parameters.AddWithValue("player", player);
        command.Parameters.AddWithValue("since", since);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var contracts = new List<Contract>();
        while (await reader.ReadAsync(cancellationToken))
        {
            contracts.Add(Read(reader));
        }

        return contracts;
    }

    private static Contract Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        IssuerId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
        IssuerName = reader.IsDBNull(2) ? null : reader.GetString(2),
        ContractorId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
        ItemId = reader.GetString(4),
        Quantity = reader.GetInt64(5),
        Delivered = reader.GetInt64(6),
        RewardCents = reader.GetInt64(7),
        Xp = reader.GetInt64(8),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        Deadline = reader.GetFieldValue<DateTimeOffset>(10),
        AcceptedAt = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
        ClosedAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        Outcome = reader.IsDBNull(13) ? null : reader.GetString(13),
    };

    private static object Public(Contract c, Guid viewer, IReadOnlyDictionary<Guid, string> known) => new
    {
        id = c.Id,
        item = c.ItemId,
        name = Items.Names.GetValueOrDefault(c.ItemId, c.ItemId),
        quantity = c.Quantity,
        delivered = c.Delivered,
        rewardCents = c.RewardCents,
        xp = c.Xp,
        deadline = c.Deadline.ToUnixTimeMilliseconds(),
        npc = c.IssuerId is null,
        issuer = c.IssuerId is { } issuer ? known.GetValueOrDefault(issuer, "?") : c.IssuerName,
        contractor = c.ContractorId is { } contractor && c.IssuerId is not null ? known.GetValueOrDefault(contractor, "?") : null,
        mine = c.IssuerId == viewer,
        delivering = c.ContractorId == viewer,
        state = c.Outcome ?? (c.ContractorId is null ? "open" : "taken"),
    };

    private static void CheckQuantity(long quantity)
    {
        if (quantity is < 1 or > ContractRules.MaxQuantity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{ContractRules.MaxQuantity}.");
        }
    }
}
