using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Modules.Achievements;

public sealed record BadgeState(Badge Badge, long Value, DateTimeOffset? UnlockedAt);

public sealed class AchievementService(GameDb db, TimeProvider time)
{
    /// <summary>Adds to the player's stats and unlocks the badges they reach; returns the newly unlocked ones.</summary>
    public async Task<IReadOnlyList<Badge>> RecordAsync(Guid owner, IReadOnlyDictionary<string, long> counts, CancellationToken cancellationToken)
    {
        return await db.InTransactionAsync(async () =>
        {
            var unlocked = new List<Badge>();
            var reached = await AddAsync(owner, counts, cancellationToken);
            // Unlocking adds to the badge count, which itself unlocks badges.
            while (reached.Count > 0)
            {
                var due = reached.SelectMany(s => Badges.ByStat[s.Key].Where(b => b.Goal <= s.Value)).ToList();
                var fresh = await UnlockAsync(owner, due, cancellationToken);
                unlocked.AddRange(fresh);
                reached = fresh.Count == 0
                    ? []
                    : await AddAsync(owner, new Dictionary<string, long> { [Stats.Badges] = fresh.Count }, cancellationToken);
            }

            return unlocked;
        }, cancellationToken);
    }

    /// <summary>Every badge with the player's progress, in the order of <see cref="Badges.All"/>.</summary>
    public async Task<IReadOnlyList<BadgeState>> StateAsync(Guid owner, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, long>();
        await using (var command = await db.CommandAsync(
            "SELECT stat, value FROM achievements.stats WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        var unlocked = new Dictionary<string, DateTimeOffset>();
        await using (var command = await db.CommandAsync(
            "SELECT badge_id, unlocked_at FROM achievements.badges WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                unlocked[reader.GetString(0)] = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        return Badges.All
            .Select(b => new BadgeState(b, values.GetValueOrDefault(b.Stat), unlocked.TryGetValue(b.Id, out var at) ? at : null))
            .ToList();
    }

    /// <summary>Stat → its value after the change, for the stats that changed.</summary>
    private async Task<Dictionary<string, long>> AddAsync(Guid owner, IReadOnlyDictionary<string, long> counts, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO achievements.stats (owner_id, stat, value)
            SELECT @owner, s, v FROM unnest(@stats, @values) AS t(s, v)
            ON CONFLICT (owner_id, stat) DO UPDATE SET value = CASE
                WHEN stats.stat = ANY(@highest) THEN GREATEST(stats.value, excluded.value)
                ELSE stats.value + excluded.value END
            RETURNING stat, value
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("stats", counts.Keys.ToArray());
        command.Parameters.AddWithValue("values", counts.Values.ToArray());
        command.Parameters.AddWithValue("highest", counts.Keys.Where(Stats.IsHighest).ToArray());
        var values = new Dictionary<string, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetInt64(1);
        }

        return values;
    }

    private async Task<List<Badge>> UnlockAsync(Guid owner, List<Badge> due, CancellationToken cancellationToken)
    {
        if (due.Count == 0)
        {
            return [];
        }

        await using var command = await db.CommandAsync(
            """
            INSERT INTO achievements.badges (owner_id, badge_id, unlocked_at)
            SELECT @owner, b, @at FROM unnest(@badges) AS t(b)
            ON CONFLICT DO NOTHING
            RETURNING badge_id
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("badges", due.Select(b => b.Id).ToArray());
        command.Parameters.AddWithValue("at", time.GetUtcNow());
        var fresh = new List<Badge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            fresh.Add(Badges.ById[reader.GetString(0)]);
        }

        return fresh;
    }
}

/// <summary>A ranking by one of the module's own stats.</summary>
public sealed class StatRanking(GameDb db, string stat, string name, int order, Func<long, string> describe)
    : Abstractions.Contracts.IPlayerRanking
{
    public string Id => stat;

    public string Name => name;

    public int Order => order;

    public string Describe(long value) => describe(value);

    public async Task<IReadOnlyList<Abstractions.Contracts.RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT owner_id, value FROM achievements.stats
            WHERE stat = @stat AND value > 0
            ORDER BY value DESC, owner_id LIMIT @count
            """, cancellationToken);
        command.Parameters.AddWithValue("stat", stat);
        command.Parameters.AddWithValue("count", count);
        var top = new List<Abstractions.Contracts.RankedPlayer>();
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
            "SELECT value FROM achievements.stats WHERE stat = @stat AND owner_id = @owner AND value > 0", cancellationToken);
        command.Parameters.AddWithValue("stat", stat);
        command.Parameters.AddWithValue("owner", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<long> CountAboveAsync(long value, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT count(*) FROM achievements.stats WHERE stat = @stat AND value > @value", cancellationToken);
        command.Parameters.AddWithValue("stat", stat);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
