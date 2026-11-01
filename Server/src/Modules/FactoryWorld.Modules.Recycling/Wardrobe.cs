using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Recycling;

/// <summary>
/// The wardrobe in the garage keeps clothes and up to <see cref="Outfits"/> saved outfits (e.g. one
/// for the forest, one for the mine, a suit for trading). Putting an outfit on takes each piece from
/// the wardrobe, or from the inventory, and hangs up what was worn instead.
/// </summary>
public static class WardrobeRules
{
    public const int Outfits = 4;

    /// <summary>Most pieces of clothing the wardrobe holds.</summary>
    public const int Capacity = 60;

    public const int MaxNameLength = 20;
}

/// <summary>A row of <c>recycling.outfits</c>: a saved outfit, one piece (or none) per clothing slot.</summary>
public sealed class SavedOutfit
{
    public Guid OwnerId { get; set; }

    /// <summary>1 to <see cref="WardrobeRules.Outfits"/>.</summary>
    public int Number { get; set; }

    public string Name { get; set; } = "";

    public string? Head { get; set; }

    public string? Body { get; set; }

    public string? Legs { get; set; }

    public string? Feet { get; set; }
}

/// <summary>A saved outfit: its name and the piece for each slot (<see cref="ClothingSlots.All"/>), missing when the slot is left bare.</summary>
public sealed record WardrobeOutfit(int Number, string Name, IReadOnlyDictionary<string, string> Pieces);

public sealed record WardrobeState(
    Guid Id, IReadOnlyList<(string Item, long Quantity)> Stored, IReadOnlyList<WardrobeOutfit> Outfits, IReadOnlyDictionary<string, string> Worn);

public sealed class WardrobeService(GameDb db, IInventory inventory, IOutfit outfit)
{
    public Task<WardrobeState> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await OpenAsync(owner, id, cancellationToken);
            return await ReadAsync(owner, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Hangs clothes from the inventory in the wardrobe.</summary>
    public Task<WardrobeState> PutAsync(Guid owner, Guid id, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckClothing(item, quantity);
            await OpenAsync(owner, id, cancellationToken);
            var stored = await GarageItemRows.ReadAsync(db, id, cancellationToken);
            if (stored.Sum(s => s.Quantity) + quantity > WardrobeRules.Capacity)
            {
                throw new GameException("storage_full", $"The wardrobe holds {WardrobeRules.Capacity} pieces of clothing.");
            }

            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            await GarageItemRows.AddAsync(db, id, owner, item, quantity, cancellationToken);
            return await ReadAsync(owner, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes clothes out of the wardrobe into the inventory, as many as it has room for; the rest stays.</summary>
    public Task<WardrobeState> TakeAsync(Guid owner, Guid id, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckClothing(item, quantity);
            await OpenAsync(owner, id, cancellationToken);
            var taken = await inventory.FitAsync(owner, item, quantity, cancellationToken);
            await GarageItemRows.RemoveAsync(db, id, item, taken, cancellationToken);
            await inventory.AddAsync(owner, item, taken, cancellationToken);
            return await ReadAsync(owner, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Saves what the character wears now as outfit <paramref name="number"/>.</summary>
    public Task<WardrobeState> SaveAsync(Guid owner, Guid id, int number, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckNumber(number);
            await OpenAsync(owner, id, cancellationToken);
            var worn = await outfit.WornAsync(owner, cancellationToken);
            var title = string.IsNullOrWhiteSpace(name) ? $"Komplet {number}" : name.Trim();
            if (title.Length > WardrobeRules.MaxNameLength)
            {
                throw new GameException("bad_name", $"An outfit's name has at most {WardrobeRules.MaxNameLength} letters.");
            }

            await using var command = await db.CommandAsync(
                """
                INSERT INTO recycling.outfits (owner_id, number, name, head, body, legs, feet) VALUES (@owner, @number, @name, @head, @body, @legs, @feet)
                ON CONFLICT (owner_id, number) DO UPDATE SET name = @name, head = @head, body = @body, legs = @legs, feet = @feet
                """, cancellationToken);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("number", number);
            command.Parameters.AddWithValue("name", title);
            foreach (var slot in ClothingSlots.All)
            {
                command.Parameters.AddWithValue(slot, (object?)worn.GetValueOrDefault(slot) ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
            return await ReadAsync(owner, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Forgets a saved outfit; the clothes stay where they are.</summary>
    public Task<WardrobeState> ForgetAsync(Guid owner, Guid id, int number, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckNumber(number);
            await OpenAsync(owner, id, cancellationToken);
            await using var command = await db.CommandAsync(
                "DELETE FROM recycling.outfits WHERE owner_id = @owner AND number = @number", cancellationToken);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("number", number);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return await ReadAsync(owner, id, cancellationToken);
        }, cancellationToken);

    /// <summary>Changes into a saved outfit: each piece comes from the wardrobe or, if it is not
    /// there, from the inventory; what was worn is hung in the wardrobe. All or nothing.</summary>
    public Task<WardrobeState> WearAsync(Guid owner, Guid id, int number, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckNumber(number);
            await OpenAsync(owner, id, cancellationToken);
            var saved = (await OutfitsAsync(owner, cancellationToken)).FirstOrDefault(o => o.Number == number)
                ?? throw new GameException("no_outfit", "No outfit is saved under this number.");
            var worn = await outfit.WornAsync(owner, cancellationToken);
            foreach (var slot in ClothingSlots.All)
            {
                var wanted = saved.Pieces.GetValueOrDefault(slot);
                var current = worn.GetValueOrDefault(slot);
                if (wanted == current)
                {
                    continue;
                }

                if (wanted is not null)
                {
                    var hanging = (await GarageItemRows.ReadAsync(db, id, cancellationToken)).FirstOrDefault(s => s.Item == wanted).Quantity;
                    if (hanging > 0)
                    {
                        await GarageItemRows.RemoveAsync(db, id, wanted, 1, cancellationToken);
                    }
                    else if (await inventory.CountAsync(owner, wanted, cancellationToken) > 0)
                    {
                        await inventory.RemoveAsync(owner, wanted, 1, cancellationToken);
                    }
                    else
                    {
                        throw new GameException("missing_clothes", $"{Items.Names[wanted]} is neither in the wardrobe nor in the inventory.");
                    }
                }

                if (current is not null)
                {
                    await GarageItemRows.AddAsync(db, id, owner, current, 1, cancellationToken);
                }

                await outfit.SetWornAsync(owner, slot, wanted, cancellationToken);
            }

            return await ReadAsync(owner, id, cancellationToken);
        }, cancellationToken);

    private static void CheckClothing(string item, long quantity)
    {
        if (quantity is < 1 or > WardrobeRules.Capacity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{WardrobeRules.Capacity}.");
        }

        if (!Items.IsClothing(item ?? ""))
        {
            throw new GameException("not_clothing", "Only clothes go in the wardrobe.");
        }
    }

    private static void CheckNumber(int number)
    {
        if (number is < 1 or > WardrobeRules.Outfits)
        {
            throw new GameException("bad_outfit", $"Outfits are numbered 1 to {WardrobeRules.Outfits}.");
        }
    }

    private async Task OpenAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        if (await GarageItemRows.KindAsync(db, owner, id, cancellationToken) != Items.ClothesWardrobe)
        {
            throw new GameException("no_object", "There is no such wardrobe in your garage.");
        }
    }

    private async Task<List<WardrobeOutfit>> OutfitsAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT number, name, head, body, legs, feet FROM recycling.outfits WHERE owner_id = @owner ORDER BY number", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var outfits = new List<WardrobeOutfit>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var pieces = new Dictionary<string, string>();
            for (var i = 0; i < ClothingSlots.All.Count; i++)
            {
                if (!reader.IsDBNull(2 + i))
                {
                    pieces[ClothingSlots.All[i]] = reader.GetString(2 + i);
                }
            }

            outfits.Add(new WardrobeOutfit(reader.GetInt32(0), reader.GetString(1), pieces));
        }

        return outfits;
    }

    private async Task<WardrobeState> ReadAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        new(id, await GarageItemRows.ReadAsync(db, id, cancellationToken), await OutfitsAsync(owner, cancellationToken),
            await outfit.WornAsync(owner, cancellationToken));
}

public sealed record WardrobeRequest(Guid Id);

public sealed record WardrobeMoveRequest(Guid Id, string Item, long Quantity);

public sealed record WardrobeOutfitRequest(Guid Id, int Number, string? Name = null);

public abstract class WardrobeHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var s = await RunAsync(scope.ServiceProvider.GetRequiredService<WardrobeService>(), client.RequireAccount(), payload, cancellationToken);
        object? Piece(string? item) => item is null ? null : new { id = item, name = Items.Names[item] };
        return new Reply("wardrobe", new
        {
            id = s.Id,
            capacity = WardrobeRules.Capacity,
            outfits = Enumerable.Range(1, WardrobeRules.Outfits).Select(n => s.Outfits.FirstOrDefault(o => o.Number == n) is { } o
                ? new
                {
                    number = n,
                    saved = true,
                    name = o.Name,
                    pieces = ClothingSlots.All.ToDictionary(slot => slot, slot => Piece(o.Pieces.GetValueOrDefault(slot))),
                }
                : new { number = n, saved = false, name = $"Komplet {n}", pieces = ClothingSlots.All.ToDictionary(slot => slot, _ => (object?)null) }),
            stored = s.Stored.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity, slot = Items.Clothes[t.Item].Slot }),
            worn = ClothingSlots.All.ToDictionary(slot => slot, slot => Piece(s.Worn.GetValueOrDefault(slot))),
        });
    }

    protected abstract Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetWardrobeHandler(IServiceScopeFactory scopes) : WardrobeHandler(scopes)
{
    public override string Type => "wardrobe.get";

    protected override Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        wardrobe.StateAsync(owner, Payload.Read<WardrobeRequest>(payload).Id, cancellationToken);
}

public sealed class PutWardrobeHandler(IServiceScopeFactory scopes) : WardrobeHandler(scopes)
{
    public override string Type => "wardrobe.put";

    protected override Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WardrobeMoveRequest>(payload);
        return wardrobe.PutAsync(owner, request.Id, request.Item, request.Quantity, cancellationToken);
    }
}

public sealed class TakeWardrobeHandler(IServiceScopeFactory scopes) : WardrobeHandler(scopes)
{
    public override string Type => "wardrobe.take";

    protected override Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WardrobeMoveRequest>(payload);
        return wardrobe.TakeAsync(owner, request.Id, request.Item, request.Quantity, cancellationToken);
    }
}

public sealed class SaveOutfitHandler(IServiceScopeFactory scopes) : WardrobeHandler(scopes)
{
    public override string Type => "wardrobe.save";

    protected override Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WardrobeOutfitRequest>(payload);
        return wardrobe.SaveAsync(owner, request.Id, request.Number, request.Name, cancellationToken);
    }
}

public sealed class ForgetOutfitHandler(IServiceScopeFactory scopes) : WardrobeHandler(scopes)
{
    public override string Type => "wardrobe.forget";

    protected override Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WardrobeOutfitRequest>(payload);
        return wardrobe.ForgetAsync(owner, request.Id, request.Number, cancellationToken);
    }
}

public sealed class WearOutfitHandler(IServiceScopeFactory scopes) : WardrobeHandler(scopes)
{
    public override string Type => "wardrobe.wear";

    protected override Task<WardrobeState> RunAsync(WardrobeService wardrobe, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WardrobeOutfitRequest>(payload);
        return wardrobe.WearAsync(owner, request.Id, request.Number, cancellationToken);
    }
}
