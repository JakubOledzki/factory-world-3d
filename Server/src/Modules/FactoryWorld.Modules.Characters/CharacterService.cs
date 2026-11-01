using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryWorld.Modules.Characters;

public sealed class CharacterService(CharactersDbContext db, GameDb gameDb, IWallet wallet, TimeProvider time)
{
    public Task<Character?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.Characters.AsNoTracking().SingleOrDefaultAsync(c => c.AccountId == accountId, cancellationToken);

    public async Task<Character> CreateAsync(Guid accountId, string name, Appearance appearance, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (!CharacterRules.IsValidName(name))
        {
            throw new GameException("invalid_name",
                $"Name must be {CharacterRules.MinNameLength}-{CharacterRules.MaxNameLength} letters, digits or _.");
        }

        if (!AppearanceCatalog.IsValid(appearance))
        {
            throw new GameException("invalid_appearance", "Unknown appearance option.");
        }

        var character = new Character
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Appearance = appearance,
            CreatedAt = time.GetUtcNow(),
        };
        db.Characters.Add(character);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw pg.ConstraintName?.Contains("account", StringComparison.OrdinalIgnoreCase) == true
                ? new GameException("character_exists", "This account already has a character.")
                : new GameException("name_taken", "This name is already taken.");
        }

        return character;
    }

    /// <summary>A new look for the character: the player pays <see cref="CharacterRules.RestyleCents"/>, or nothing changes.</summary>
    public Task RestyleAsync(Guid accountId, Appearance appearance, CancellationToken cancellationToken) =>
        gameDb.InTransactionAsync(async () =>
        {
            if (!AppearanceCatalog.IsValid(appearance))
            {
                throw new GameException("invalid_appearance", "Unknown appearance option.");
            }

            var character = await FindAsync(accountId, cancellationToken) ?? throw new GameException("no_character", "Create a character first.");
            if (character.Appearance == appearance)
            {
                throw new GameException("same_look", "That is how the character looks already.");
            }

            await wallet.DebitAsync(accountId, CharacterRules.RestyleCents, "new look", cancellationToken);
            await using var update = await gameDb.CommandAsync(
                """UPDATE characters.characters SET "Appearance" = @look::jsonb WHERE "AccountId" = @account""", cancellationToken);
            update.Parameters.AddWithValue("look", JsonSerializer.Serialize(appearance));
            update.Parameters.AddWithValue("account", accountId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
}
