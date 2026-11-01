using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Npgsql;

namespace FactoryWorld.Modules.Law;

public sealed record Officer(Guid PlayerId, string Country, short Rank, DateTimeOffset JoinedAt, DateTimeOffset PaidUntil);

public sealed record CountryRule(string Item, string Kind, long FeeCents);

public sealed record Sanction(string Kind, string Country, DateTimeOffset Until, long CaseId);

/// <summary>The law module's own tables, read and written by its services.</summary>
public sealed class LawData(GameDb db, TimeProvider time) : ISanctions
{
    public DateTimeOffset Now => time.GetUtcNow();

    public async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var command = await db.CommandAsync(sql, cancellationToken);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = await CommandAsync(sql, cancellationToken, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = await CommandAsync(sql, cancellationToken, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    public async Task<List<T>> QueryAsync<T>(
        string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = await CommandAsync(sql, cancellationToken, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken) => db.InTransactionAsync(work, cancellationToken);

    public Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken) => db.InTransactionAsync(work, cancellationToken);

    private static Officer ReadOfficer(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetInt16(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4));

    private const string OfficerColumns = "player_id, country, rank, joined_at, paid_until";

    public async Task<Officer?> OfficerAsync(Guid player, CancellationToken cancellationToken, bool forUpdate = false) =>
        (await QueryAsync($"SELECT {OfficerColumns} FROM law.officers WHERE player_id = @p{(forUpdate ? " FOR UPDATE" : "")}", ReadOfficer,
            cancellationToken, ("p", player))).FirstOrDefault();

    public Task<List<Officer>> OfficersAsync(string country, CancellationToken cancellationToken) =>
        QueryAsync($"SELECT {OfficerColumns} FROM law.officers WHERE country = @c ORDER BY rank DESC, joined_at", ReadOfficer,
            cancellationToken, ("c", country));

    public async Task<int> OfficerCountAsync(string country, CancellationToken cancellationToken) =>
        (int)(long)(await ScalarAsync("SELECT count(*) FROM law.officers WHERE country = @c", cancellationToken, ("c", country)))!;

    /// <summary>The officer, who must serve in a police at <paramref name="rank"/> or above.</summary>
    public async Task<Officer> RequireOfficerAsync(Guid player, short rank, CancellationToken cancellationToken)
    {
        var officer = await OfficerAsync(player, cancellationToken) ?? throw new GameException("not_officer", "You are not in the police.");
        if (officer.Rank < rank)
        {
            throw new GameException("rank_too_low", $"This takes the rank of {Ranks.Names[rank]} or higher.");
        }

        return officer;
    }

    public Task<List<CountryRule>> RulesAsync(string country, CancellationToken cancellationToken) =>
        QueryAsync("SELECT item, kind, fee_cents FROM law.country_rules WHERE country = @c ORDER BY item",
            r => new CountryRule(r.GetString(0), r.GetString(1), r.GetInt64(2)), cancellationToken, ("c", country));

    public async Task<bool> ForestProtectedAsync(string country, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT forest_protection FROM law.country_laws WHERE country = @c", cancellationToken, ("c", country)) is true;

    /// <summary>The items the player has a licence for in the country now.</summary>
    public async Task<IReadOnlySet<string>> LicencesAsync(Guid player, string country, CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT item FROM law.licences WHERE player_id = @p AND country = @c AND until > @now", r => r.GetString(0),
            cancellationToken, ("p", player), ("c", country), ("now", Now))).ToHashSet();

    /// <summary>Of <paramref name="goods"/>, what the country forbids the player to have: banned, or licensed without a licence.</summary>
    public async Task<Dictionary<string, long>> ForbiddenAsync(
        Guid player, string country, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken)
    {
        var rules = await RulesAsync(country, cancellationToken);
        if (rules.Count == 0)
        {
            return [];
        }

        var licences = await LicencesAsync(player, country, cancellationToken);
        return goods.Where(g => g.Value > 0 && rules.Any(r => r.Item == g.Key && (r.Kind == RuleKinds.Banned || !licences.Contains(g.Key))))
            .ToDictionary(g => g.Key, g => g.Value);
    }

    public Task<List<Sanction>> SanctionsAsync(Guid player, CancellationToken cancellationToken) =>
        QueryAsync("SELECT kind, country, until, case_id FROM law.sanctions WHERE player_id = @p AND until > @now ORDER BY until",
            r => new Sanction(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetInt64(3)),
            cancellationToken, ("p", player), ("now", Now));

    public async Task<bool> SanctionedAsync(Guid player, string kind, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT 1 FROM law.sanctions WHERE player_id = @p AND kind = @k AND until > @now LIMIT 1", cancellationToken,
            ("p", player), ("k", kind), ("now", Now)) is not null;

    public async Task RequireAllowedAsync(Guid player, string kind, CancellationToken cancellationToken)
    {
        if (await SanctionedAsync(player, kind, cancellationToken))
        {
            throw kind == SanctionKinds.Trade
                ? new GameException("trade_banned", "The police banned you from trading for now.")
                : new GameException("travel_banned", "The police banned your trains from crossing borders for now.");
        }
    }

    /// <summary>Counts <paramref name="amount"/> more of <paramref name="kind"/> for the player today; fails with <c>daily_limit</c>
    /// past <paramref name="limit"/>.</summary>
    public async Task CountActionAsync(Guid player, string kind, int limit, CancellationToken cancellationToken, int amount = 1)
    {
        var count = (int)(await ScalarAsync(
            """
            INSERT INTO law.action_counts (player_id, day, kind, count) VALUES (@p, @d, @k, @n)
            ON CONFLICT (player_id, day, kind) DO UPDATE SET count = action_counts.count + @n
            RETURNING count
            """, cancellationToken, ("p", player), ("d", LawRules.Day(Now)), ("k", kind), ("n", amount)))!;
        if (count > limit)
        {
            throw new GameException("daily_limit", $"You may do this {limit} times a day.");
        }
    }

    public async Task<int> ActionsTodayAsync(Guid player, string kind, CancellationToken cancellationToken) =>
        await ScalarAsync("SELECT count FROM law.action_counts WHERE player_id = @p AND day = @d AND kind = @k", cancellationToken,
            ("p", player), ("d", LawRules.Day(Now)), ("k", kind)) is int n ? n : 0;

    public Task TraceAsync(string country, Guid suspect, Guid? accomplice, string kind, string detail, long valueCents, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO law.traces (country, suspect_id, accomplice_id, kind, detail, value_cents, at, found)
            VALUES (@c, @s, @a, @k, @d, @v, @now, false)
            """, cancellationToken, ("c", country), ("s", suspect), ("a", accomplice), ("k", kind), ("d", Clip(detail, 200)), ("v", valueCents), ("now", Now));

    /// <summary>Opens a case; returns its id.</summary>
    public async Task<long> OpenCaseAsync(
        string country, Guid suspect, string kind, string detail, long valueCents, Guid openedBy, CancellationToken cancellationToken,
        (int X, int Y, IReadOnlyDictionary<string, long> Goods)? seize = null) =>
        (long)(await ScalarAsync(
            """
            INSERT INTO law.cases (country, suspect_id, kind, detail, value_cents, opened_by, opened_at, status, fine_cents, collected_cents,
                trade_ban_hours, travel_ban_hours, wanted_hours, bonus_paid, seize_x, seize_y, seize_items, seize_quantities)
            VALUES (@c, @s, @k, @d, @v, @by, @now, 'open', 0, 0, 0, 0, 0, false, @sx, @sy, @si, @sq)
            RETURNING id
            """, cancellationToken, ("c", country), ("s", suspect), ("k", kind), ("d", Clip(detail, 400)), ("v", valueCents), ("by", openedBy),
            ("now", Now), ("sx", seize?.X), ("sy", seize?.Y), ("si", seize?.Goods.Keys.ToArray() ?? []),
            ("sq", seize?.Goods.Values.ToArray() ?? [])))!;

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
