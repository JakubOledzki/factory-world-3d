using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Logistics;

/// <summary>A kind of storage put on a plot: how many slots it has, how much fits in a slot and,
/// for piles, heaps and silos, the only goods it takes.</summary>
public sealed record StorageKind(string Id, int Slots, int SlotSize, IReadOnlySet<string>? Only = null)
{
    public int Capacity => Slots * SlotSize;

    /// <summary>Chests take any goods but fluids, which are kept in tanks only.</summary>
    public bool Takes(string item) => Only?.Contains(item) ?? (Items.Exists(item) && !Items.IsFluid(item));
}

/// <summary>
/// Chests take any item, a slot of up to <see cref="StorageKind.SlotSize"/> pieces of one kind each.
/// A pile, a heap and a silo have one big slot for one kind of bulk goods at a time.
/// </summary>
public static class Storages
{
    public static readonly IReadOnlyList<StorageKind> Kinds =
    [
        new(Items.WoodenChest, 8, 100),
        new(Items.IronChest, 24, 100),
        // A logistics depot: steel racks of 48 slots, twice as deep as a chest's.
        new(Items.LogisticsDepot, 48, 200),
        // A port of drones keeps a little of what they fly in and out.
        new(Items.DronePort, 8, 100),
        // A high-bay store in a city: racks up to the roof on one tile.
        new(Items.HighBay, 60, 200),
        // An automated warehouse: robots stack 3 × 3 tiles of racks.
        new(Items.AutomatedWarehouse, 120, 500),
        // Freight chests for trains between plots: the bigger ones are researched one after another.
        new(Items.LoadingChest, 8, 100),
        new(Items.LoadingChest2, 16, 100),
        new(Items.LoadingChest3, 32, 200),
        new(Items.LoadingChest4, 64, 250),
        new(Items.ReceivingChest, 8, 100),
        new(Items.ReceivingChest2, 16, 100),
        new(Items.ReceivingChest3, 32, 200),
        new(Items.ReceivingChest4, 64, 250),
        new(Items.WoodPile, 1, 400, new HashSet<string> { Items.Wood }),
        // A heap of loose bulk goods outgrows an iron chest: twice its 2400 pieces, but of one kind only.
        new(Items.Heap, 1, 5000, new HashSet<string>
        {
            Items.Stone, Items.Sand, Items.Coal,
            Items.IronOre, Items.CopperOre, Items.TinOre, Items.SilverOre, Items.GoldOre,
            // What a crusher makes of ore, on its way to the furnace.
            Items.CrushedIronOre, Items.CrushedCopperOre, Items.CrushedTinOre,
        }),
        new(Items.GrainSilo, 1, 2000, new HashSet<string> { Items.Wheat, Items.Corn, Items.Sunflower }),
        // A tank holds one fluid at a time.
        new(Items.Tank, 1, 2000, new HashSet<string> { Items.CrudeOil, Items.NaturalGas, Items.Gasoline, Items.Diesel, Items.Kerosene }),
    ];

    public static StorageKind? Find(string kind) => Kinds.FirstOrDefault(k => k.Id == kind);

    /// <summary>Slots taken by what is stored: each kind of item fills whole slots.</summary>
    public static int UsedSlots(StorageKind kind, IEnumerable<ItemStack> stacks) =>
        stacks.Sum(s => (s.Quantity + kind.SlotSize - 1) / kind.SlotSize);

    /// <summary>How many more pieces of <paramref name="item"/> fit.</summary>
    public static int Room(StorageKind kind, IReadOnlyCollection<ItemStack> stacks, string item)
    {
        if (!kind.Takes(item))
        {
            return 0;
        }

        var mine = stacks.FirstOrDefault(s => s.Item == item)?.Quantity ?? 0;
        var others = UsedSlots(kind, stacks.Where(s => s.Item != item));
        return Math.Max(0, (kind.Slots - others) * kind.SlotSize - mine);
    }

    public static void Add(List<ItemStack> stacks, string item, int quantity)
    {
        if (stacks.FirstOrDefault(s => s.Item == item) is { } stack)
        {
            stack.Quantity += quantity;
        }
        else
        {
            stacks.Add(new ItemStack { Item = item, Quantity = quantity });
        }
    }

    /// <summary>Takes up to <paramref name="quantity"/> pieces out; returns how many there were.</summary>
    public static int Remove(List<ItemStack> stacks, string item, int quantity)
    {
        if (stacks.FirstOrDefault(s => s.Item == item) is not { } stack)
        {
            return 0;
        }

        var taken = Math.Min(quantity, stack.Quantity);
        stack.Quantity -= taken;
        if (stack.Quantity == 0)
        {
            stacks.Remove(stack);
        }

        return taken;
    }
}

public sealed class ItemStack
{
    public string Item { get; set; } = "";

    public int Quantity { get; set; }
}
