using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Recycling;

/// <summary>
/// Things kept in the garage outside the inventory. The shelf holds any goods, <see cref="SlotSize"/>
/// pieces of one kind to a slot; the shelves upgrade adds slots. The delivery pallet takes what the
/// player buys when the inventory is full, with no limit, and only gives it out. Its goods belong
/// to the player rather than to the pallet, so they wait while the pallet is moved or put away.
/// </summary>
public static class GarageStorageRules
{
    public const int ShelfSlots = 16;

    public const int SlotSize = 100;

    /// <summary>Slots taken by what is stored: each kind of item fills whole slots.</summary>
    public static long UsedSlots(IEnumerable<(string Item, long Quantity)> stacks) =>
        stacks.Sum(s => (s.Quantity + SlotSize - 1) / SlotSize);

    /// <summary>How many more pieces of <paramref name="item"/> fit in <paramref name="slots"/> slots.</summary>
    public static long Room(int slots, IReadOnlyCollection<(string Item, long Quantity)> stacks, string item)
    {
        var mine = stacks.FirstOrDefault(s => s.Item == item).Quantity;
        var others = UsedSlots(stacks.Where(s => s.Item != item));
        return Math.Max(0, (slots - others) * SlotSize - mine);
    }
}

/// <summary>A row of <c>recycling.garage_items</c>: what a shelf, a wardrobe or (by the owner's id) the pallet holds of one item.</summary>
public sealed class GarageItem
{
    /// <summary>The shelf's or wardrobe's object id; the owner's account id for the pallet.</summary>
    public Guid HolderId { get; set; }

    public Guid OwnerId { get; set; }

    public string Item { get; set; } = "";

    public long Quantity { get; set; }
}

internal static class GarageItemRows
{
    public static async Task<List<(string Item, long Quantity)>> ReadAsync(GameDb db, Guid holder, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT item, quantity FROM recycling.garage_items WHERE holder_id = @holder AND quantity > 0 ORDER BY item", cancellationToken);
        command.Parameters.AddWithValue("holder", holder);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var stacks = new List<(string, long)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            stacks.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return stacks;
    }

    public static async Task AddAsync(GameDb db, Guid holder, Guid owner, string item, long quantity, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO recycling.garage_items (holder_id, owner_id, item, quantity) VALUES (@holder, @owner, @item, @quantity)
            ON CONFLICT (holder_id, item) DO UPDATE SET quantity = garage_items.quantity + excluded.quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("holder", holder);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("quantity", quantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Fails with <c>not_enough_items</c> when the holder has fewer.</summary>
    public static async Task RemoveAsync(GameDb db, Guid holder, string item, long quantity, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "UPDATE recycling.garage_items SET quantity = quantity - @quantity WHERE holder_id = @holder AND item = @item AND quantity >= @quantity",
            cancellationToken);
        command.Parameters.AddWithValue("holder", holder);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("quantity", quantity);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new GameException("not_enough_items", "There are not that many here.");
        }

        await using var clean = await db.CommandAsync(
            "DELETE FROM recycling.garage_items WHERE holder_id = @holder AND item = @item AND quantity = 0", cancellationToken);
        clean.Parameters.AddWithValue("holder", holder);
        clean.Parameters.AddWithValue("item", item);
        await clean.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Everything the holder has goes back to the owner's inventory.</summary>
    public static async Task EmptyAsync(GameDb db, IInventory inventory, Guid owner, Guid holder, CancellationToken cancellationToken)
    {
        foreach (var (item, quantity) in await ReadAsync(db, holder, cancellationToken))
        {
            await inventory.AddAsync(owner, item, quantity, cancellationToken);
        }

        await using var command = await db.CommandAsync("DELETE FROM recycling.garage_items WHERE holder_id = @holder", cancellationToken);
        command.Parameters.AddWithValue("holder", holder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The kind of the player's object in the garage, or null; locks the garage row so changes to the same storage wait for each other.</summary>
    public static async Task<string?> KindAsync(GameDb db, Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using (var lockGarage = await db.CommandAsync(
            "SELECT 1 FROM recycling.garages WHERE owner_id = @owner FOR UPDATE", cancellationToken))
        {
            lockGarage.Parameters.AddWithValue("owner", owner);
            await lockGarage.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = await db.CommandAsync(
            "SELECT kind FROM recycling.garage_objects WHERE id = @id AND owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}

/// <param name="Slots">Slots of the shelf; null for the pallet, which has no limit.</param>
public sealed record GarageStorageState(Guid Id, string Kind, int? Slots, IReadOnlyList<(string Item, long Quantity)> Stacks);

public sealed class GarageStorageService(GameDb db, IInventory inventory)
{
    public Task<GarageStorageState> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () => await ReadAsync(owner, id, await OpenAsync(owner, id, cancellationToken), cancellationToken), cancellationToken);

    /// <summary>Moves items from the inventory onto the shelf; the pallet takes only deliveries.</summary>
    public Task<GarageStorageState> PutAsync(Guid owner, Guid id, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            Check(item, quantity);
            var kind = await OpenAsync(owner, id, cancellationToken);
            if (kind != Items.Shelf)
            {
                throw new GameException("not_stored_here", "Only deliveries go onto the pallet.");
            }

            var state = await ReadAsync(owner, id, kind, cancellationToken);
            if (GarageStorageRules.Room(state.Slots!.Value, state.Stacks, item) < quantity)
            {
                throw new GameException("storage_full", "There is no room for that much on the shelf.");
            }

            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            await GarageItemRows.AddAsync(db, id, owner, item, quantity, cancellationToken);
            return await ReadAsync(owner, id, kind, cancellationToken);
        }, cancellationToken);

    /// <summary>Moves items from the shelf or the pallet into the inventory: <paramref name="quantity"/> of them, or all when 0.
    /// Only as many as the inventory has room for come out; the rest stays.</summary>
    public Task<GarageStorageState> TakeAsync(Guid owner, Guid id, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var kind = await OpenAsync(owner, id, cancellationToken);
            var holder = Holder(owner, id, kind);
            if (quantity == 0)
            {
                quantity = (await GarageItemRows.ReadAsync(db, holder, cancellationToken)).FirstOrDefault(s => s.Item == item).Quantity;
                if (quantity == 0)
                {
                    throw new GameException("not_enough_items", "There is none of it here.");
                }
            }

            Check(item, quantity);
            var taken = await inventory.FitAsync(owner, item, quantity, cancellationToken);
            await GarageItemRows.RemoveAsync(db, holder, item, taken, cancellationToken);
            await inventory.AddAsync(owner, item, taken, cancellationToken);
            return await ReadAsync(owner, id, kind, cancellationToken);
        }, cancellationToken);

    private static void Check(string item, long quantity)
    {
        if (quantity is < 1 or > 1_000_000)
        {
            throw new GameException("bad_quantity", "Quantity must be 1-1000000.");
        }

        if (!Items.Exists(item ?? ""))
        {
            throw new GameException("unknown_item", "There is no such item.");
        }
    }

    private async Task<string> OpenAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        await GarageItemRows.KindAsync(db, owner, id, cancellationToken) is { } kind && kind is Items.Shelf or Items.Pallet
            ? kind
            : throw new GameException("no_object", "There is no such shelf or pallet in your garage.");

    /// <summary>The pallet's goods are the owner's, wherever the pallet stands.</summary>
    private static Guid Holder(Guid owner, Guid id, string kind) => kind == Items.Pallet ? owner : id;

    private async Task<GarageStorageState> ReadAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        int? slots = kind == Items.Shelf
            ? GarageUpgrades.ShelfSize(await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken))
            : null;
        return new GarageStorageState(id, kind, slots, await GarageItemRows.ReadAsync(db, Holder(owner, id, kind), cancellationToken));
    }
}

/// <summary>What the player buys waits on the pallet in the garage when their inventory is full.</summary>
public sealed class PalletDeliveries(GameDb db) : IGarageDeliveries
{
    public async Task<bool> DeliverAsync(Guid player, string item, long quantity, CancellationToken cancellationToken)
    {
        await using (var command = await db.CommandAsync(
            "SELECT 1 FROM recycling.garage_objects WHERE owner_id = @owner AND kind = @kind LIMIT 1", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            command.Parameters.AddWithValue("kind", Items.Pallet);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                return false;
            }
        }

        await GarageItemRows.AddAsync(db, player, player, item, quantity, cancellationToken);
        return true;
    }
}

/// <summary>A shelf or wardrobe being picked up gives back what is in it. The pallet keeps its
/// deliveries: they are held under the owner, not the pallet.</summary>
public sealed class GarageStorageContents(GameDb db, IInventory inventory) : IMachineContents
{
    public Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        GarageItemRows.EmptyAsync(db, inventory, owner, id, cancellationToken);
}

public sealed record GarageStorageRequest(Guid Id);

public sealed record GarageStorageMoveRequest(Guid Id, string Item, long Quantity);

public abstract class GarageStorageHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var s = await RunAsync(scope.ServiceProvider.GetRequiredService<GarageStorageService>(), client.RequireAccount(), payload, cancellationToken);
        var stored = s.Stacks.Sum(t => t.Quantity);
        // The same shape as a chest on a plot ("storage"), so the client shows both in one window.
        return new Reply("storage", new
        {
            id = s.Id,
            kind = s.Kind,
            name = Items.Names[s.Kind],
            garage = true,
            canPut = s.Kind == Items.Shelf,
            slots = s.Slots ?? 0,
            slotSize = GarageStorageRules.SlotSize,
            usedSlots = GarageStorageRules.UsedSlots(s.Stacks),
            capacity = (long)(s.Slots ?? 0) * GarageStorageRules.SlotSize,
            stored,
            items = s.Stacks.Select(t => new { id = t.Item, name = Items.Names.GetValueOrDefault(t.Item, t.Item), quantity = t.Quantity }),
        });
    }

    protected abstract Task<GarageStorageState> RunAsync(GarageStorageService storage, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What the shelf or the pallet in the garage holds.</summary>
public sealed class GetGarageStorageHandler(IServiceScopeFactory scopes) : GarageStorageHandler(scopes)
{
    public override string Type => "garage.storage";

    protected override Task<GarageStorageState> RunAsync(GarageStorageService storage, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        storage.StateAsync(owner, Payload.Read<GarageStorageRequest>(payload).Id, cancellationToken);
}

public sealed class PutGarageStorageHandler(IServiceScopeFactory scopes) : GarageStorageHandler(scopes)
{
    public override string Type => "garage.storage.put";

    protected override Task<GarageStorageState> RunAsync(GarageStorageService storage, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<GarageStorageMoveRequest>(payload);
        return storage.PutAsync(owner, request.Id, request.Item, request.Quantity, cancellationToken);
    }
}

public sealed class TakeGarageStorageHandler(IServiceScopeFactory scopes) : GarageStorageHandler(scopes)
{
    public override string Type => "garage.storage.take";

    protected override Task<GarageStorageState> RunAsync(GarageStorageService storage, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<GarageStorageMoveRequest>(payload);
        return storage.TakeAsync(owner, request.Id, request.Item, request.Quantity, cancellationToken);
    }
}
