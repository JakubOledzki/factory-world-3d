using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Characters;

/// <summary>What the character has on: tools in the tool bar and a backpack.</summary>
public sealed class Equipment(GameDb db, IInventory inventory, InventorySize size) : IEquipment
{
    /// <summary>The columns of the tool bar's slots, in order.</summary>
    private static readonly string[] Slots = ["Tool", "Tool2", "Tool3"];

    /// <summary>Slots of the tool bar with this backpack worn (or none).</summary>
    public static int ToolSlots(string? backpack) =>
        backpack is not null && Items.Backpacks.TryGetValue(backpack, out var b) ? b.ToolSlots : Items.ToolSlots;

    public async Task<IReadOnlyList<string>> ToolsAsync(Guid accountId, CancellationToken cancellationToken) =>
        await ReadAsync(accountId, false, cancellationToken) is { } worn
            ? worn.Tools.Take(ToolSlots(worn.Backpack)).OfType<string>().ToList()
            : [];

    public Task GiveToolAsync(Guid accountId, string tool, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (await ReadAsync(accountId, true, cancellationToken) is { } worn
                && Array.FindIndex(worn.Tools, 0, ToolSlots(worn.Backpack), t => t is null) is var free and >= 0)
            {
                await SetAsync(accountId, Slots[free], tool, cancellationToken);
            }
            else
            {
                await inventory.AddAsync(accountId, tool, 1, cancellationToken);
            }
        }, cancellationToken);

    /// <summary>Takes a tool from the inventory into a slot of the tool bar (the first free one when
    /// <paramref name="slot"/> is null, the first one when none is free), puts a backpack on or
    /// a piece of clothing in its slot. What was there goes back to the inventory.</summary>
    public Task EquipAsync(Guid accountId, string item, int? slot, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (Items.IsBackpack(item))
            {
                await WearAsync(accountId, item, cancellationToken);
                return;
            }

            if (Items.Clothes.TryGetValue(item, out var garment))
            {
                var on = await OutfitAsync(accountId, true, cancellationToken) ?? throw new GameException("no_character", "Create a character first.");
                await inventory.RemoveAsync(accountId, item, 1, cancellationToken);
                if (on.GetValueOrDefault(garment.Slot) is { } off)
                {
                    await inventory.AddAsync(accountId, off, 1, cancellationToken);
                }

                await SetAsync(accountId, ClothesColumn(garment.Slot), item, cancellationToken);
                return;
            }

            if (!Items.IsTool(item))
            {
                throw new GameException("not_a_tool", "Only tools fit in the tool bar.");
            }

            var worn = await RequireAsync(accountId, cancellationToken);
            var slots = ToolSlots(worn.Backpack);
            var at = slot ?? Array.FindIndex(worn.Tools, 0, slots, t => t is null);
            if (at < 0)
            {
                at = 0;
            }

            if (at >= slots)
            {
                throw new GameException("bad_slot", slots == 1 ? "Without a backpack the tool bar has one slot." : $"The tool bar has slots 0 to {slots - 1}.");
            }

            await inventory.RemoveAsync(accountId, item, 1, cancellationToken);
            if (worn.Tools[at] is { } old)
            {
                await inventory.AddAsync(accountId, old, 1, cancellationToken);
            }

            await SetAsync(accountId, Slots[at], item, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts the tool from a slot of the tool bar back into the inventory.</summary>
    public Task UnequipAsync(Guid accountId, int slot, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var worn = await RequireAsync(accountId, cancellationToken);
            if (slot < 0 || slot >= Slots.Length || worn.Tools[slot] is not { } tool)
            {
                return;
            }

            await inventory.AddAsync(accountId, tool, 1, cancellationToken);
            await SetAsync(accountId, Slots[slot], null, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes the clothes off a slot (one of <see cref="ClothingSlots.All"/>) into the inventory.</summary>
    public Task TakeOffAsync(Guid accountId, string slot, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!ClothingSlots.All.Contains(slot))
            {
                throw new GameException("bad_slot", "Clothes are worn on the head, body, legs or feet.");
            }

            var on = await OutfitAsync(accountId, true, cancellationToken) ?? throw new GameException("no_character", "Create a character first.");
            if (on.GetValueOrDefault(slot) is not { } worn)
            {
                return;
            }

            await inventory.AddAsync(accountId, worn, 1, cancellationToken);
            await SetAsync(accountId, ClothesColumn(slot), null, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts a piece of clothing (or nothing) on a slot without touching the inventory: the
    /// wardrobe keeps what comes off and gives what goes on.</summary>
    public async Task DressAsync(Guid accountId, string slot, string? item, CancellationToken cancellationToken)
    {
        if (!ClothingSlots.All.Contains(slot) || item is not null && (!Items.Clothes.TryGetValue(item, out var garment) || garment.Slot != slot))
        {
            throw new GameException("bad_slot", "This is not worn there.");
        }

        if (await OutfitAsync(accountId, true, cancellationToken) is null)
        {
            throw new GameException("no_character", "Create a character first.");
        }

        await SetAsync(accountId, ClothesColumn(slot), item, cancellationToken);
    }

    /// <summary>The column of a clothing slot.</summary>
    private static string ClothesColumn(string slot) => "Clothes" + char.ToUpperInvariant(slot[0]) + slot[1..];

    private static readonly string OutfitColumns = string.Join(", ", ClothingSlots.All.Select(s => $"\"{ClothesColumn(s)}\""));

    /// <summary>The clothes worn by slot, or null when the account has no character; locked with <paramref name="forUpdate"/>.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> OutfitAsync(Guid accountId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"""SELECT {OutfitColumns} FROM characters.characters WHERE "AccountId" = @account{(forUpdate ? " FOR UPDATE" : "")}""",
            cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var outfit = new Dictionary<string, string>();
        for (var i = 0; i < ClothingSlots.All.Count; i++)
        {
            if (!reader.IsDBNull(i))
            {
                outfit[ClothingSlots.All[i]] = reader.GetString(i);
            }
        }

        return outfit;
    }

    /// <summary>Eats a meal from the inventory (see <see cref="Items.Meals"/>): it helps until <paramref name="now"/> and its time, in place of the one before.</summary>
    public Task EatAsync(Guid accountId, string item, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Items.Meals.TryGetValue(item, out var meal))
            {
                throw new GameException("not_a_meal", "This is not something to eat on the go.");
            }

            _ = await RequireAsync(accountId, cancellationToken);
            await inventory.RemoveAsync(accountId, item, 1, cancellationToken);
            await using var update = await db.CommandAsync(
                """UPDATE characters.characters SET "Meal" = @meal, "MealUntil" = @until WHERE "AccountId" = @account""", cancellationToken);
            update.Parameters.AddWithValue("meal", item);
            update.Parameters.AddWithValue("until", now + meal.Time);
            update.Parameters.AddWithValue("account", accountId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>The meal that still helps the character at <paramref name="now"/>, or null.</summary>
    public async Task<string?> MealAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """SELECT "Meal" FROM characters.characters WHERE "AccountId" = @account AND "MealUntil" > @now""", cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("now", now);
        return await command.ExecuteScalarAsync(cancellationToken) is string meal && Items.Meals.ContainsKey(meal) ? meal : null;
    }

    /// <summary>Takes the backpack off into the inventory, with the tools in the slots only it gave.</summary>
    public Task TakeOffBackpackAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var worn = await RequireAsync(accountId, cancellationToken);
            if (worn.Backpack is not { } backpack)
            {
                return;
            }

            await inventory.AddAsync(accountId, backpack, 1, cancellationToken);
            await SetAsync(accountId, "Backpack", null, cancellationToken);
            await DropToolsAsync(accountId, worn, Items.ToolSlots, cancellationToken);
        }, cancellationToken);

    private async Task WearAsync(Guid accountId, string backpack, CancellationToken cancellationToken)
    {
        var worn = await RequireAsync(accountId, cancellationToken);
        await inventory.RemoveAsync(accountId, backpack, 1, cancellationToken);
        if (worn.Backpack is { } old)
        {
            await inventory.AddAsync(accountId, old, 1, cancellationToken);
        }

        await SetAsync(accountId, "Backpack", backpack, cancellationToken);
        await DropToolsAsync(accountId, worn, ToolSlots(backpack), cancellationToken);
    }

    /// <summary>Puts the tools from slot <paramref name="from"/> on back into the inventory: the tool bar got shorter.</summary>
    private async Task DropToolsAsync(Guid accountId, Worn worn, int from, CancellationToken cancellationToken)
    {
        for (var i = from; i < Slots.Length; i++)
        {
            if (worn.Tools[i] is { } tool)
            {
                await inventory.AddAsync(accountId, tool, 1, cancellationToken);
                await SetAsync(accountId, Slots[i], null, cancellationToken);
            }
        }
    }

    private async Task<Worn> RequireAsync(Guid accountId, CancellationToken cancellationToken) =>
        await ReadAsync(accountId, true, cancellationToken) ?? throw new GameException("no_character", "Create a character first.");

    /// <summary>What the character has on, or null when the account has no character; locked with <paramref name="forUpdate"/>.</summary>
    private async Task<Worn?> ReadAsync(Guid accountId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"""SELECT "Tool", "Tool2", "Tool3", "Backpack" FROM characters.characters WHERE "AccountId" = @account{(forUpdate ? " FOR UPDATE" : "")}""",
            cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new Worn([Text(0), Text(1), Text(2)], Text(3));
    }

    /// <param name="column">One of <see cref="Slots"/>, "Backpack" or a clothing slot's column.</param>
    private async Task SetAsync(Guid accountId, string column, string? item, CancellationToken cancellationToken)
    {
        await using var update = await db.CommandAsync(
            $"""UPDATE characters.characters SET "{column}" = @item WHERE "AccountId" = @account""", cancellationToken);
        update.Parameters.AddWithValue("item", (object?)item ?? DBNull.Value);
        update.Parameters.AddWithValue("account", accountId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        if (column == "Backpack")
        {
            size.Forget(accountId);
        }
    }

    private sealed record Worn(string?[] Tools, string? Backpack);
}

/// <summary>Tiles of the inventory: more with a backpack on. Apart from <see cref="Equipment"/>,
/// which needs the inventory itself. Read once per message (one scope): listing and moving items ask several
/// times, and only <see cref="Equipment"/> changes the backpack, which then <see cref="Forget"/>s it.</summary>
public sealed class InventorySize(GameDb db) : IInventorySize
{
    private readonly Dictionary<Guid, int> _slots = [];

    public async Task<int> SlotsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (_slots.TryGetValue(accountId, out var known))
        {
            return known;
        }

        await using var command = await db.CommandAsync(
            """SELECT "Backpack" FROM characters.characters WHERE "AccountId" = @account""", cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        var slots = await command.ExecuteScalarAsync(cancellationToken) is string backpack && Items.Backpacks.TryGetValue(backpack, out var b)
            ? Items.InventorySlots + b.ExtraSlots
            : Items.InventorySlots;
        _slots[accountId] = slots;
        return slots;
    }

    /// <summary>The backpack of <paramref name="accountId"/> changed: read it again next time.</summary>
    public void Forget(Guid accountId) => _slots.Remove(accountId);
}

/// <summary>What the worn clothes give, the bonuses of each piece added up per industry, with the level's on top
/// (<see cref="LevelBonuses"/>), what the last meal gives while it lasts and the player's other bonuses (the
/// garage's upgrades).</summary>
public sealed class Outfit(Equipment equipment, ICharacterXp xp, TimeProvider time, IEnumerable<IPlayerBonuses> others) : IOutfit
{
    public async Task<int> BonusAsync(Guid accountId, string industry, CancellationToken cancellationToken)
    {
        var percent = Bonuses(await equipment.OutfitAsync(accountId, false, cancellationToken) ?? new Dictionary<string, string>())
                .GetValueOrDefault(industry)
            + LevelBonuses.For(Leveling.LevelForXp(await xp.XpAsync(accountId, cancellationToken) ?? 0)).GetValueOrDefault(industry)
            + (await equipment.MealAsync(accountId, time.GetUtcNow(), cancellationToken) is { } meal ? Items.Meals[meal].Bonuses.GetValueOrDefault(industry) : 0);
        foreach (var bonus in others)
        {
            percent += await bonus.PercentAsync(accountId, industry, cancellationToken);
        }

        return percent;
    }

    public async Task<IReadOnlyDictionary<string, string>> WornAsync(Guid accountId, CancellationToken cancellationToken) =>
        await equipment.OutfitAsync(accountId, false, cancellationToken) ?? new Dictionary<string, string>();

    public Task SetWornAsync(Guid accountId, string slot, string? item, CancellationToken cancellationToken) =>
        equipment.DressAsync(accountId, slot, item, cancellationToken);

    /// <summary>Percent per industry the clothes give together; industries they do not help left out.</summary>
    public static IReadOnlyDictionary<string, int> Bonuses(IReadOnlyDictionary<string, string> outfit) =>
        outfit.Values.Where(Items.IsClothing)
            .SelectMany(c => Items.Clothes[c].Bonuses)
            .GroupBy(b => b.Key)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Value));
}
