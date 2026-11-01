using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Characters;

public sealed class CharacterNames(GameDb db) : ICharacterNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        if (accountIds.Count == 0)
        {
            return names;
        }

        await using var command = await db.CommandAsync(
            """SELECT "AccountId", "Name" FROM characters.characters WHERE "AccountId" = ANY(@accounts)""", cancellationToken);
        command.Parameters.AddWithValue("accounts", accountIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names[reader.GetGuid(0)] = reader.GetString(1);
        }

        return names;
    }

    public async Task<Guid?> AccountAsync(string name, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """SELECT "AccountId" FROM characters.characters WHERE "NormalizedName" = @name""", cancellationToken);
        command.Parameters.AddWithValue("name", name.Trim().ToLowerInvariant());
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(string prefix, int limit, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        if (string.IsNullOrWhiteSpace(prefix) || limit < 1)
        {
            return names;
        }

        // LIKE wildcards typed by the player are taken literally.
        var pattern = prefix.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        await using var command = await db.CommandAsync(
            """SELECT "AccountId", "Name" FROM characters.characters WHERE "Name" ILIKE @pattern ORDER BY lower("Name") LIMIT @limit""",
            cancellationToken);
        command.Parameters.AddWithValue("pattern", pattern);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names[reader.GetGuid(0)] = reader.GetString(1);
        }

        return names;
    }
}
