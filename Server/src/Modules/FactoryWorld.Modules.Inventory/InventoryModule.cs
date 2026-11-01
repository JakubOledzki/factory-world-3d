using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackSizes = FactoryWorld.Modules.Abstractions.Contracts.Stacks;

namespace FactoryWorld.Modules.Inventory;

/// <summary>How many of one item a player owns.</summary>
public sealed class Stack
{
    public Guid OwnerId { get; set; }

    public required string ItemId { get; set; }

    public long Quantity { get; set; }

    /// <summary>The tiles of the inventory grid the item's stacks lie on, in order: full stacks first, the rest on the last.
    /// Given when the inventory is listed; empty until then, and short of what the item needs when the grid is full.</summary>
    public int[] Tiles { get; set; } = [];
}

/// <summary>An item the player put on a numbered slot of the quick bar (keys 1 to 5).</summary>
public sealed class HotbarSlot
{
    public Guid OwnerId { get; set; }

    public int Slot { get; set; }

    public required string ItemId { get; set; }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "inventory";

    public DbSet<Stack> Stacks => Set<Stack>();

    public DbSet<HotbarSlot> HotbarSlots => Set<HotbarSlot>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Stack>(e =>
        {
            e.ToTable("stacks", t => t.HasCheckConstraint("ck_stacks_quantity", "quantity >= 0"));
            e.HasKey(s => new { s.OwnerId, s.ItemId });
            e.Property(s => s.OwnerId).HasColumnName("owner_id");
            e.Property(s => s.ItemId).HasColumnName("item_id").HasMaxLength(64);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.Tiles).HasColumnName("tiles").HasDefaultValueSql("'{}'");
        });
        model.Entity<HotbarSlot>(e =>
        {
            e.ToTable("hotbar", t => t.HasCheckConstraint("ck_hotbar_slot", $"slot >= 0 AND slot < {Hotbar.Slots}"));
            e.HasKey(s => new { s.OwnerId, s.Slot });
            e.Property(s => s.OwnerId).HasColumnName("owner_id");
            e.Property(s => s.Slot).HasColumnName("slot");
            e.Property(s => s.ItemId).HasColumnName("item_id").HasMaxLength(64);
        });
    }
}

/// <summary>How the stacks of an inventory lie on its tiles; pure, for the service and the tests.</summary>
public static class InventoryRules
{
    /// <summary>How many more pieces of <paramref name="item"/> fit in <paramref name="slots"/> tiles holding <paramref name="stacks"/>:
    /// what its last stack still takes and whole stacks on the tiles the other items leave free.</summary>
    public static long Room(int slots, IEnumerable<(string Item, long Quantity)> stacks, string item)
    {
        long mine = 0, others = 0;
        foreach (var (id, quantity) in stacks)
        {
            if (id == item)
            {
                mine += quantity;
            }
            else
            {
                others += StackSizes.Tiles(id, quantity);
            }
        }

        return Math.Max(0, (slots - others) * StackSizes.Of(item) - mine);
    }

    /// <summary>
    /// Gives every item the tiles its stacks need. An item keeps the tiles it had, in their order, as far as it still needs them
    /// (and they are in the grid and not taken twice, which two requests at once can do); then the items short of tiles get the
    /// first free ones, in the order of their first tile. When the grid is full, what is left lies past it, on no tile.
    /// </summary>
    public static void Lay(int slots, IReadOnlyList<Stack> stacks)
    {
        var order = stacks.OrderBy(s => s.Tiles.Length > 0 ? s.Tiles[0] : int.MaxValue).ThenBy(s => s.ItemId, StringComparer.Ordinal).ToList();
        var taken = new HashSet<int>();
        var kept = new List<List<int>>();
        foreach (var stack in order)
        {
            var need = StackSizes.Tiles(stack.ItemId, stack.Quantity);
            var keep = new List<int>();
            foreach (var tile in stack.Tiles)
            {
                if (keep.Count < need && tile >= 0 && tile < slots && taken.Add(tile))
                {
                    keep.Add(tile);
                }
            }

            kept.Add(keep);
        }

        var free = new Queue<int>(Enumerable.Range(0, slots).Where(t => !taken.Contains(t)));
        for (var i = 0; i < order.Count; i++)
        {
            var need = StackSizes.Tiles(order[i].ItemId, order[i].Quantity);
            while (kept[i].Count < need && free.TryDequeue(out var tile))
            {
                kept[i].Add(tile);
            }

            order[i].Tiles = kept[i].ToArray();
        }
    }

    /// <summary>What lies past the grid of an item laid by <see cref="Lay"/>: the pieces its tiles do not hold.</summary>
    public static long Over(Stack stack) => Math.Max(0, stack.Quantity - (long)stack.Tiles.Length * StackSizes.Of(stack.ItemId));
}

/// <summary>The player's items. Each tile of the grid (<see cref="IInventorySize"/> of them) holds one stack: up to
/// <see cref="StackSizes.Of"/> pieces of one item. Things given back can fill more than the tiles; nothing new comes in
/// through <see cref="ReceiveAsync"/> until there is room for it.</summary>
public sealed class InventoryService(GameDb db, IInventorySize size) : IInventory
{
    public async Task AddAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken)
    {
        Check(item, quantity);
        await using var command = await db.CommandAsync(
            """
            INSERT INTO inventory.stacks (owner_id, item_id, quantity) VALUES (@owner, @item, @quantity)
            ON CONFLICT (owner_id, item_id) DO UPDATE SET quantity = stacks.quantity + excluded.quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("quantity", quantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReceiveAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken)
    {
        Check(item, quantity);
        // Two requests at once may both see the same room and fill a little past it, as things given back do: nothing is
        // made or lost by it, and the player takes nothing new until there is room again.
        if (await RoomAsync(owner, item, cancellationToken) is var room && quantity > room)
        {
            throw InventoryExtensions.InventoryFull(room);
        }

        await AddAsync(owner, item, quantity, cancellationToken);
    }

    public async Task<long> RoomAsync(Guid owner, string item, CancellationToken cancellationToken)
    {
        if (!Items.Exists(item))
        {
            throw new GameException("unknown_item", $"Unknown item '{item}'.");
        }

        var held = new List<(string, long)>();
        await using (var command = await db.CommandAsync(
            "SELECT item_id, quantity FROM inventory.stacks WHERE owner_id = @owner AND quantity > 0", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                held.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        return InventoryRules.Room(await size.SlotsAsync(owner, cancellationToken), held, item);
    }

    public async Task<bool> HasRoomAsync(Guid owner, string item, CancellationToken cancellationToken) =>
        await RoomAsync(owner, item, cancellationToken) > 0;

    /// <summary>Tiles of the player's inventory grid; items can be moved to any of them.</summary>
    public Task<int> SlotsAsync(Guid owner, CancellationToken cancellationToken) => size.SlotsAsync(owner, cancellationToken);

    public async Task RemoveAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken)
    {
        Check(item, quantity);
        // One conditional update: two requests at once can never take more than the player has.
        // An emptied item frees its tiles; it gets new ones when the player gets it again.
        await using var command = await db.CommandAsync(
            """
            UPDATE inventory.stacks SET quantity = quantity - @quantity,
                tiles = CASE WHEN quantity = @quantity THEN '{}' ELSE tiles END
            WHERE owner_id = @owner AND item_id = @item AND quantity >= @quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("quantity", quantity);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new GameException("not_enough_items", "You don't have enough of this item.");
        }
    }

    public async Task<long> CountAsync(Guid owner, string item, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT quantity FROM inventory.stacks WHERE owner_id = @owner AND item_id = @item", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", item);
        return await command.ExecuteScalarAsync(cancellationToken) is long n ? n : 0;
    }

    /// <summary>The tiles of the grid and the player's items in tile order, items past the grid last. Items get the tiles
    /// their stacks need (<see cref="InventoryRules.Lay"/>), which are saved.</summary>
    public async Task<(int Slots, IReadOnlyList<Stack> Stacks)> ListAsync(Guid owner, CancellationToken cancellationToken)
    {
        var stacks = new List<Stack>();
        await using (var command = await db.CommandAsync(
            "SELECT item_id, quantity, tiles FROM inventory.stacks WHERE owner_id = @owner AND quantity > 0", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stacks.Add(new Stack
                {
                    OwnerId = owner,
                    ItemId = reader.GetString(0),
                    Quantity = reader.GetInt64(1),
                    Tiles = reader.GetFieldValue<int[]>(2),
                });
            }
        }

        var slots = await size.SlotsAsync(owner, cancellationToken);
        var before = stacks.ToDictionary(s => s.ItemId, s => s.Tiles);
        InventoryRules.Lay(slots, stacks);
        foreach (var stack in stacks.Where(s => !s.Tiles.SequenceEqual(before[s.ItemId])))
        {
            await SaveTilesAsync(owner, stack, cancellationToken);
        }

        return (slots, stacks.OrderBy(s => s.Tiles.Length > 0 ? s.Tiles[0] : int.MaxValue).ThenBy(s => s.ItemId, StringComparer.Ordinal).ToList());
    }

    /// <summary>Moves what lies on tile <paramref name="from"/> to tile <paramref name="to"/>; what lay there takes its place.
    /// Two stacks of one item swap too: the one that was not full moves.</summary>
    public async Task MoveAsync(Guid owner, int from, int to, CancellationToken cancellationToken)
    {
        var slots = await size.SlotsAsync(owner, cancellationToken);
        if (from < 0 || from >= slots || to < 0 || to >= slots)
        {
            throw new GameException("bad_slot", $"The inventory has tiles 0 to {slots - 1}.");
        }

        await db.InTransactionAsync(async () =>
        {
            // Tiles are given when the inventory is listed; this makes sure both tiles are known as they are now.
            var (_, stacks) = await ListAsync(owner, cancellationToken);
            var moved = stacks.FirstOrDefault(s => s.Tiles.Contains(from))
                ?? throw new GameException("not_enough_items", "Nothing lies on that tile.");
            if (from == to)
            {
                return;
            }

            // Both places are found before either changes: two tiles of one item are in the same array.
            var there = stacks.FirstOrDefault(s => s.Tiles.Contains(to));
            var i = Array.IndexOf(moved.Tiles, from);
            if (there is not null)
            {
                there.Tiles[Array.IndexOf(there.Tiles, to)] = from;
            }

            moved.Tiles[i] = to;

            await SaveTilesAsync(owner, moved, cancellationToken);
            if (there is not null && there != moved)
            {
                await SaveTilesAsync(owner, there, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>Moves the first stack of an item to tile <paramref name="slot"/> (the older form of <see cref="MoveAsync(Guid, int, int, CancellationToken)"/>).</summary>
    public async Task MoveAsync(Guid owner, string item, int slot, CancellationToken cancellationToken)
    {
        if (!Items.Exists(item))
        {
            throw new GameException("unknown_item", $"Unknown item '{item}'.");
        }

        var slots = await size.SlotsAsync(owner, cancellationToken);
        if (slot < 0 || slot >= slots)
        {
            throw new GameException("bad_slot", $"The inventory has tiles 0 to {slots - 1}.");
        }

        var (_, stacks) = await ListAsync(owner, cancellationToken);
        var stack = stacks.FirstOrDefault(s => s.ItemId == item) ?? throw new GameException("not_enough_items", "You don't have this item.");
        if (stack.Tiles.Length == 0)
        {
            throw new GameException("inventory_full", "This item lies past the tiles: make room first.");
        }

        await MoveAsync(owner, stack.Tiles[0], slot, cancellationToken);
    }

    private async Task SaveTilesAsync(Guid owner, Stack stack, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "UPDATE inventory.stacks SET tiles = @tiles WHERE owner_id = @owner AND item_id = @item AND quantity > 0", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", stack.ItemId);
        command.Parameters.AddWithValue("tiles", stack.Tiles);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Check(string item, long quantity)
    {
        if (!Items.Exists(item))
        {
            throw new GameException("unknown_item", $"Unknown item '{item}'.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
    }
}

public sealed class GetInventoryHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "inventory.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await InventoryReply.CreateAsync(scope.ServiceProvider.GetRequiredService<InventoryService>(), client.RequireAccount(), cancellationToken);
    }
}

public sealed class MoveItemHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "inventory.move";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var account = client.RequireAccount();
        var request = Payload.Read<MoveItemRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
        if (request is { From: { } from, To: { } to })
        {
            await inventory.MoveAsync(account, from, to, cancellationToken);
        }
        else
        {
            await inventory.MoveAsync(account, request.Item ?? "", request.Slot ?? -1, cancellationToken);
        }

        return await InventoryReply.CreateAsync(inventory, account, cancellationToken);
    }

    /// <summary>A tile to another (<c>from</c>, <c>to</c>), or, as older clients ask, an item's first stack to a tile (<c>item</c>, <c>slot</c>).</summary>
    private sealed record MoveItemRequest(string? Item, int? Slot, int? From, int? To);
}

/// <summary>How many pieces of every item fill a tile of the inventory (<see cref="StackSizes"/>); the client asks once, for
/// the item cards and to count tiles before taking things.</summary>
public sealed class GetStacksHandler : IMessageHandler
{
    public string Type => "inventory.stacks";

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken) =>
        Task.FromResult(new Reply("inventory.stacks", new { stacks = StackSizes.Sizes }));
}

/// <summary>The quick bar: five numbered slots, each holding the id of an item the player picks up with one key.
/// It only points at items; how many are left is the inventory's count, so a slot stays when the item runs out.</summary>
public sealed class Hotbar(GameDb db, InventoryService inventory)
{
    public const int Slots = 5;

    /// <summary>The item on each slot, null for an empty one.</summary>
    public async Task<string?[]> GetAsync(Guid owner, CancellationToken cancellationToken)
    {
        var slots = new string?[Slots];
        await using var command = await db.CommandAsync(
            "SELECT slot, item_id FROM inventory.hotbar WHERE owner_id = @owner ORDER BY slot", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var slot = reader.GetInt32(0);
            var item = reader.GetString(1);
            // An item removed from the game would have no name or icon: it shows as an empty slot. So does
            // an item on two slots at once (two requests at the same moment): it stays on the first.
            if (slot is >= 0 and < Slots && Items.Exists(item) && !slots.Contains(item))
            {
                slots[slot] = item;
            }
        }

        return slots;
    }

    /// <summary>Puts an item the player owns on a slot, or empties the slot (null). The item leaves any other slot it was on.</summary>
    public async Task SetAsync(Guid owner, int slot, string? item, CancellationToken cancellationToken)
    {
        if (slot is < 0 or >= Slots)
        {
            throw new GameException("bad_hotbar_slot", $"The quick bar has slots 0 to {Slots - 1}.");
        }

        if (item is not null)
        {
            if (!Items.Exists(item))
            {
                throw new GameException("unknown_item", $"Unknown item '{item}'.");
            }

            if (await inventory.CountAsync(owner, item, cancellationToken) <= 0)
            {
                throw new GameException("not_enough_items", "You don't have this item.");
            }
        }

        await db.InTransactionAsync(async () =>
        {
            await using (var clear = await db.CommandAsync(
                "DELETE FROM inventory.hotbar WHERE owner_id = @owner AND (slot = @slot OR item_id = @item)", cancellationToken))
            {
                clear.Parameters.AddWithValue("owner", owner);
                clear.Parameters.AddWithValue("slot", slot);
                clear.Parameters.AddWithValue("item", (object?)item ?? DBNull.Value);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            if (item is null)
            {
                return;
            }

            // Two requests at once may both have cleared before inserting: the later one wins the slot.
            await using var put = await db.CommandAsync(
                """
                INSERT INTO inventory.hotbar (owner_id, slot, item_id) VALUES (@owner, @slot, @item)
                ON CONFLICT (owner_id, slot) DO UPDATE SET item_id = excluded.item_id
                """, cancellationToken);
            put.Parameters.AddWithValue("owner", owner);
            put.Parameters.AddWithValue("slot", slot);
            put.Parameters.AddWithValue("item", item);
            await put.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }
}

public sealed class GetHotbarHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "hotbar.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var account = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        return HotbarReply(await scope.ServiceProvider.GetRequiredService<Hotbar>().GetAsync(account, cancellationToken));
    }

    internal static Reply HotbarReply(string?[] slots) => new("hotbar", new { slots });
}

public sealed class SetHotbarHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "hotbar.set";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var account = client.RequireAccount();
        var request = Payload.Read<SetHotbarRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var hotbar = scope.ServiceProvider.GetRequiredService<Hotbar>();
        await hotbar.SetAsync(account, request.Slot, string.IsNullOrEmpty(request.Item) ? null : request.Item, cancellationToken);
        return GetHotbarHandler.HotbarReply(await hotbar.GetAsync(account, cancellationToken));
    }

    private sealed record SetHotbarRequest(int Slot, string? Item);
}

internal static class InventoryReply
{
    public static async Task<Reply> CreateAsync(InventoryService inventory, Guid account, CancellationToken cancellationToken)
    {
        var (slots, stacks) = await inventory.ListAsync(account, cancellationToken);
        // An item with no tile at all (the grid is full) shows past the grid, as one tile each.
        var past = slots;
        var items = new List<object>();
        foreach (var s in stacks)
        {
            items.Add(new
            {
                id = s.ItemId,
                name = Items.Names[s.ItemId],
                quantity = s.Quantity,
                slot = s.Tiles.Length > 0 ? s.Tiles[0] : past++,
                tiles = s.Tiles,
                over = InventoryRules.Over(s),
                stack = StackSizes.Of(s.ItemId),
            });
        }

        return new Reply("inventory", new { slots, items });
    }
}

/// <summary>Items players own. Owns the <c>inventory</c> schema.</summary>
public sealed class InventoryModule : IGameModule
{
    public string Name => "Inventory";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<InventoryDbContext>(InventoryDbContext.Schema);
        services.AddScoped<InventoryService>();
        services.AddScoped<IInventory>(sp => sp.GetRequiredService<InventoryService>());
        services.AddSingleton<IMessageHandler, GetInventoryHandler>();
        services.AddSingleton<IMessageHandler, MoveItemHandler>();
        services.AddSingleton<IMessageHandler, GetStacksHandler>();
        services.AddScoped<Hotbar>();
        services.AddSingleton<IMessageHandler, GetHotbarHandler>();
        services.AddSingleton<IMessageHandler, SetHotbarHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<InventoryDbContext>(cancellationToken);
}
