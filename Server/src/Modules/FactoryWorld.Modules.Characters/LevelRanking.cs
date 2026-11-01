using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Characters;

/// <summary>The most experienced characters: ranked by XP, shown as the level.</summary>
public sealed class LevelRanking(GameDb db) : IPlayerRanking
{
    public string Id => "level";

    public string Name => "Najwyższy poziom";

    public int Order => 20;

    public string Describe(long value) =>
        $"poziom {Leveling.LevelForXp(value)} ({value.ToString("#,0", new System.Globalization.NumberFormatInfo { NumberGroupSeparator = " " })} XP)";

    public async Task<IReadOnlyList<RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT "AccountId", "Xp" FROM characters.characters
            WHERE "Xp" > 0
            ORDER BY "Xp" DESC, "AccountId" LIMIT @count
            """, cancellationToken);
        command.Parameters.AddWithValue("count", count);
        var top = new List<RankedPlayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            top.Add(new(reader.GetGuid(0), reader.GetInt64(1)));
        }

        return top;
    }

    public async Task<long?> ValueAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """SELECT "Xp" FROM characters.characters WHERE "AccountId" = @account AND "Xp" > 0""", cancellationToken);
        command.Parameters.AddWithValue("account", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<long> CountAboveAsync(long value, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """SELECT count(*) FROM characters.characters WHERE "Xp" > @value""", cancellationToken);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
