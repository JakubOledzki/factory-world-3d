using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Characters;

public sealed class CharacterXp(GameDb db) : ICharacterXp
{
    public async Task<long> HighestAsync(IReadOnlyCollection<Guid> excluding, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """SELECT COALESCE(MAX("Xp"), 0) FROM characters.characters WHERE NOT ("AccountId" = ANY(@excluding))""", cancellationToken);
        command.Parameters.AddWithValue("excluding", excluding.ToArray());
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<long?> XpAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """SELECT "Xp" FROM characters.characters WHERE "AccountId" = @account""", cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<int> LevelAsync(Guid accountId, CancellationToken cancellationToken) =>
        Leveling.LevelForXp(await XpAsync(accountId, cancellationToken) ?? 0);

    public async Task CapAsync(Guid accountId, long maxXp, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """UPDATE characters.characters SET "Xp" = @max WHERE "AccountId" = @account AND "Xp" > @max""", cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("max", Math.Max(maxXp, 0));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
