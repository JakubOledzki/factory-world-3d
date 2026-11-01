using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Characters;

public sealed record CreateCharacterRequest(string Name, Appearance Appearance);

public sealed record EquipRequest(string Item, int? Slot);

/// <param name="Clothing">A clothing slot to take the clothes off (see <see cref="ClothingSlots.All"/>).</param>
public sealed record UnequipRequest(int? Slot, bool Backpack, string? Clothing = null);

public sealed record RestyleRequest(Appearance Appearance);

public sealed record EatRequest(string Item);

internal static class CharacterView
{
    public static DateTimeOffset Now(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

    public static object From(Character c, DateTimeOffset now)
    {
        var meal = c.MealAt(now);
        var level = Leveling.LevelForXp(c.Xp);
        return new
        {
            id = c.Id,
            name = c.Name,
            appearance = c.Appearance,
            level,
            xp = c.Xp,
            levelXp = Leveling.TotalXpForLevel(level),
            nextLevelXp = level < Leveling.MaxLevel ? Leveling.TotalXpForLevel(level + 1) : (long?)null,
            tools = c.Tools.Select(t => t is null ? null : new { id = t, name = Items.Names[t] }),
            backpack = c.Backpack is { } b ? new
            {
                id = b,
                name = Items.Names[b],
                extraSlots = Items.Backpacks[b].ExtraSlots,
                toolSlots = Items.Backpacks[b].ToolSlots,
            } : null,
            inventorySlots = Items.InventorySlots + (c.Backpack is { } worn ? Items.Backpacks[worn].ExtraSlots : 0),
            outfit = c.Outfit.ToDictionary(o => o.Key, o => new
            {
                id = o.Value,
                name = Items.Names[o.Value],
                bonuses = Items.Clothes[o.Value].Bonuses,
            }),
            // Percent per industry from all the clothes worn and the level.
            bonuses = LevelBonuses.With(Outfit.Bonuses(c.Outfit), level),
            levelBonuses = LevelBonuses.For(level),
            levelRewards = LevelBonuses.All.Select(b => new { level = b.Level, industry = b.Industry, percent = b.Percent }),
            // What the last meal gives, while it lasts (Unix milliseconds).
            meal = meal is null ? null : new
            {
                id = meal,
                name = Items.Names[meal],
                bonuses = Items.Meals[meal].Bonuses,
                until = c.MealUntil!.Value.ToUnixTimeMilliseconds(),
            },
            serverTime = now.ToUnixTimeMilliseconds(),
            restyleCents = CharacterRules.RestyleCents,
        };
    }
}

/// <summary>The editor's options; sent before login so the editor can be shown right after registering.</summary>
public sealed class OptionsHandler : IMessageHandler
{
    public string Type => "character.options";

    public bool RequiresAccount => false;

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken) =>
        Task.FromResult(new Reply("character.options", new
        {
            skinTones = AppearanceCatalog.SkinTones,
            hairStyles = AppearanceCatalog.HairStyles,
            hairColors = AppearanceCatalog.HairColors,
            shirtStyles = AppearanceCatalog.ShirtStyles,
            clothColors = AppearanceCatalog.ClothColors,
            faceStyles = AppearanceCatalog.FaceStyles,
            eyeColors = AppearanceCatalog.EyeColors,
            facialHairStyles = AppearanceCatalog.FacialHairStyles,
            clothingSlots = ClothingSlots.All,
            industries = Industries.All,
        }));
}

public sealed class GetCharacterHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "character.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var character = await scope.ServiceProvider.GetRequiredService<CharacterService>()
            .FindAsync(client.RequireAccount(), cancellationToken);
        return character is null ? new Reply("character.none") : new Reply("character", CharacterView.From(character, CharacterView.Now(scope)));
    }
}

public sealed class CreateCharacterHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "character.create";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<CreateCharacterRequest>(payload);
        if (request.Appearance is null)
        {
            throw new GameException("bad_payload", "Appearance is missing.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var character = await scope.ServiceProvider.GetRequiredService<CharacterService>()
            .CreateAsync(client.RequireAccount(), request.Name ?? "", request.Appearance, cancellationToken);
        return new Reply("character", CharacterView.From(character, CharacterView.Now(scope)));
    }
}

/// <summary>Puts a tool from the inventory into a slot of the tool bar, or a backpack on; what was there goes back to the inventory.</summary>
public sealed class EquipHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "character.equip";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<EquipRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var equipment = scope.ServiceProvider.GetRequiredService<Equipment>();
        await equipment.EquipAsync(client.RequireAccount(), request.Item ?? "", request.Slot, cancellationToken);
        var character = await scope.ServiceProvider.GetRequiredService<CharacterService>().FindAsync(client.RequireAccount(), cancellationToken);
        return new Reply("character", CharacterView.From(character!, CharacterView.Now(scope)));
    }
}

/// <summary>Puts the tool from a slot of the tool bar (the first one by default) back into the inventory, or takes the backpack or clothes off.</summary>
public sealed class UnequipHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "character.unequip";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = payload is { ValueKind: JsonValueKind.Object } ? Payload.Read<UnequipRequest>(payload) : new UnequipRequest(null, false);
        await using var scope = scopes.CreateAsyncScope();
        var equipment = scope.ServiceProvider.GetRequiredService<Equipment>();
        if (request.Backpack)
        {
            await equipment.TakeOffBackpackAsync(client.RequireAccount(), cancellationToken);
        }
        else if (request.Clothing is { } slot)
        {
            await equipment.TakeOffAsync(client.RequireAccount(), slot, cancellationToken);
        }
        else
        {
            await equipment.UnequipAsync(client.RequireAccount(), request.Slot ?? 0, cancellationToken);
        }

        var character = await scope.ServiceProvider.GetRequiredService<CharacterService>().FindAsync(client.RequireAccount(), cancellationToken);
        return new Reply("character", CharacterView.From(character!, CharacterView.Now(scope)));
    }
}

/// <summary>Changes the look of an existing character (face, hair, colours) for <see cref="CharacterRules.RestyleCents"/>.</summary>
public sealed class RestyleHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "character.restyle";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<RestyleRequest>(payload);
        if (request.Appearance is null)
        {
            throw new GameException("bad_payload", "Appearance is missing.");
        }

        await using var scope = scopes.CreateAsyncScope();
        var characters = scope.ServiceProvider.GetRequiredService<CharacterService>();
        await characters.RestyleAsync(client.RequireAccount(), request.Appearance, cancellationToken);
        var character = await characters.FindAsync(client.RequireAccount(), cancellationToken);
        return new Reply("character", CharacterView.From(character!, CharacterView.Now(scope)));
    }
}

/// <summary>Eats a meal from the inventory: hand work goes faster for a while (see <see cref="Items.Meals"/>).</summary>
public sealed class EatHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "character.eat";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<EatRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Equipment>().EatAsync(client.RequireAccount(), request.Item ?? "", CharacterView.Now(scope), cancellationToken);
        var character = await scope.ServiceProvider.GetRequiredService<CharacterService>().FindAsync(client.RequireAccount(), cancellationToken);
        return new Reply("character", CharacterView.From(character!, CharacterView.Now(scope)));
    }
}
