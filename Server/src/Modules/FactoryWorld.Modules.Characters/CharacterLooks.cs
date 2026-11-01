using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryWorld.Modules.Characters;

public sealed class CharacterLooks(CharactersDbContext db) : ICharacterLooks
{
    public async Task<IReadOnlyDictionary<Guid, CharacterLook>> LooksAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return new Dictionary<Guid, CharacterLook>();
        }

        var ids = accountIds.ToArray();
        var characters = await db.Characters.AsNoTracking()
            .Where(c => ids.Contains(c.AccountId))
            .Select(c => new { c.AccountId, c.Name, c.Appearance, c.ClothesHead, c.ClothesBody, c.ClothesLegs, c.ClothesFeet })
            .ToListAsync(cancellationToken);
        // The clothes go with the look, by slot, so the others see what the character wears.
        return characters.ToDictionary(c => c.AccountId, c => new CharacterLook(c.Name, new
        {
            c.Appearance.SkinTone,
            c.Appearance.HairStyle,
            c.Appearance.HairColor,
            c.Appearance.ShirtStyle,
            c.Appearance.ShirtColor,
            c.Appearance.PantsColor,
            c.Appearance.ShoesColor,
            c.Appearance.FaceStyle,
            c.Appearance.EyeColor,
            c.Appearance.FacialHair,
            Outfit = new Dictionary<string, string?>
            {
                [ClothingSlots.Head] = c.ClothesHead,
                [ClothingSlots.Body] = c.ClothesBody,
                [ClothingSlots.Legs] = c.ClothesLegs,
                [ClothingSlots.Feet] = c.ClothesFeet,
            }.Where(o => o.Value is not null).ToDictionary(o => o.Key, o => o.Value),
        }));
    }
}
