using System.Collections.Concurrent;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Rivalry;

/// <summary>What the module remembers between requests. The firsts somebody has already taken, so a reply that shows one again (a rich player's balance) costs no query.
/// Filled from the database the first time it is asked.</summary>
public sealed class RivalryCache
{
    private readonly ConcurrentDictionary<string, byte> _claimed = new();
    private volatile bool _loaded;

    public bool Loaded => _loaded;

    public bool Contains(string id) => _claimed.ContainsKey(id);

    public void Add(string id) => _claimed[id] = 0;

    public void Load(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            Add(id);
        }

        _loaded = true;
    }

    /// <summary>The last week whose prizes are known to be paid; weeks up to it need no look.</summary>
    public int SettledUpTo { get; set; } = int.MinValue;
}

public sealed record FirstState(WorldFirst First, Guid? Owner, DateTimeOffset? At);

public sealed record ScoredPlayer(Guid Owner, long Value);

public sealed class RivalryService(GameDb db, IWallet wallet, ICharacterNames names, IAnnouncements announcements, RivalryCache claimed, TimeProvider time)
{
    /// <summary>Takes the firsts nobody has yet and adds to the player's score in this week's challenge; returns the firsts they took.</summary>
    public Task<IReadOnlyList<WorldFirst>> RecordAsync(Guid owner, IReadOnlyCollection<string> reached, long score, CancellationToken cancellationToken) =>
        db.InTransactionAsync<IReadOnlyList<WorldFirst>>(async () =>
        {
            var now = time.GetUtcNow();
            await SettleAsync(cancellationToken);
            var taken = new List<WorldFirst>();
            await LoadClaimedAsync(cancellationToken);
            var open = reached.Distinct().Where(id => Firsts.ById.ContainsKey(id) && !claimed.Contains(id)).ToArray();
            if (open.Length > 0)
            {
                await using var insert = await db.CommandAsync(
                    """
                    INSERT INTO rivalry.firsts (id, owner_id, at) SELECT f, @owner, @at FROM unnest(@ids) AS t(f)
                    ON CONFLICT DO NOTHING RETURNING id
                    """, cancellationToken);
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("at", now);
                insert.Parameters.AddWithValue("ids", open);
                await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    taken.Add(Firsts.ById[reader.GetString(0)]);
                }
            }

            if (score > 0)
            {
                await using var add = await db.CommandAsync(
                    """
                    INSERT INTO rivalry.challenge_scores (week, owner_id, value) VALUES (@week, @owner, @score)
                    ON CONFLICT (week, owner_id) DO UPDATE SET value = challenge_scores.value + excluded.value
                    """, cancellationToken);
                add.Parameters.AddWithValue("week", Challenges.Week(now));
                add.Parameters.AddWithValue("owner", owner);
                add.Parameters.AddWithValue("score", score);
                await add.ExecuteNonQueryAsync(cancellationToken);
            }

            if (taken.Count > 0)
            {
                var name = (await names.NamesAsync([owner], cancellationToken)).GetValueOrDefault(owner, "Ktoś");
                db.AfterCommit(() =>
                {
                    foreach (var first in taken)
                    {
                        announcements.Announce($"Pierwszy na świecie: {name} zdobywa tytuł „{first.Name}”. {first.Description}");
                    }
                });
            }

            // Somebody else's claim (a conflict) is just as final.
            db.AfterCommit(() =>
            {
                foreach (var id in open)
                {
                    claimed.Add(id);
                }
            });
            return taken;
        }, cancellationToken);

    private async Task LoadClaimedAsync(CancellationToken cancellationToken)
    {
        if (claimed.Loaded)
        {
            return;
        }

        var ids = new List<string>();
        await using (var command = await db.CommandAsync("SELECT id FROM rivalry.firsts", cancellationToken))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        claimed.Load(ids);
    }

    /// <summary>Pays the prizes of every finished week that has not had them yet, once: the week is marked paid in the same transaction.</summary>
    public Task SettleAsync(CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var current = Challenges.Week(time.GetUtcNow());
            if (claimed.SettledUpTo >= current - 1)
            {
                return 0;
            }

            var due = new List<int>();
            await using (var command = await db.CommandAsync(
                """
                SELECT DISTINCT s.week FROM rivalry.challenge_scores s
                WHERE s.week < @week AND NOT EXISTS (SELECT 1 FROM rivalry.settled_weeks w WHERE w.week = s.week)
                ORDER BY s.week
                """, cancellationToken))
            {
                command.Parameters.AddWithValue("week", current);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    due.Add(reader.GetInt32(0));
                }
            }

            var announced = new List<string>();
            foreach (var week in due)
            {
                await using (var mark = await db.CommandAsync(
                    "INSERT INTO rivalry.settled_weeks (week, settled_at) VALUES (@week, @at) ON CONFLICT DO NOTHING", cancellationToken))
                {
                    mark.Parameters.AddWithValue("week", week);
                    mark.Parameters.AddWithValue("at", time.GetUtcNow());
                    if (await mark.ExecuteNonQueryAsync(cancellationToken) == 0)
                    {
                        // Another request paid it just now.
                        continue;
                    }
                }

                var challenge = Challenges.For(week);
                var podium = await TopAsync(week, Challenges.PrizesCents.Count, cancellationToken);
                for (var i = 0; i < podium.Count; i++)
                {
                    var prize = Challenges.PrizesCents[i];
                    await using (var winner = await db.CommandAsync(
                        """
                        INSERT INTO rivalry.challenge_winners (week, place, owner_id, value, prize_cents) VALUES (@week, @place, @owner, @value, @prize)
                        """, cancellationToken))
                    {
                        winner.Parameters.AddWithValue("week", week);
                        winner.Parameters.AddWithValue("place", i + 1);
                        winner.Parameters.AddWithValue("owner", podium[i].Owner);
                        winner.Parameters.AddWithValue("value", podium[i].Value);
                        winner.Parameters.AddWithValue("prize", prize);
                        await winner.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await wallet.CreditAsync(podium[i].Owner, prize, $"challenge {challenge.Id} week {week}: place {i + 1}", cancellationToken);
                }

                if (podium.Count > 0)
                {
                    var named = await names.NamesAsync(podium.Select(p => p.Owner).ToList(), cancellationToken);
                    announced.Add($"Wyzwanie „{challenge.Name}” rozstrzygnięte: wygrywa {named.GetValueOrDefault(podium[0].Owner, "?")} "
                        + $"({Rules.Count(podium[0].Value, challenge.One, challenge.Few, challenge.Many)}).");
                }
            }

            db.AfterCommit(() =>
            {
                claimed.SettledUpTo = Math.Max(claimed.SettledUpTo, current - 1);
                announced.ForEach(announcements.Announce);
            });
            return 0;
        }, cancellationToken);

    /// <summary>The best scores of a week, highest first; equal scores in the order of the owners' ids.</summary>
    public async Task<IReadOnlyList<ScoredPlayer>> TopAsync(int week, int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT owner_id, value FROM rivalry.challenge_scores
            WHERE week = @week AND value > 0
            ORDER BY value DESC, owner_id LIMIT @count
            """, cancellationToken);
        command.Parameters.AddWithValue("week", week);
        command.Parameters.AddWithValue("count", count);
        var top = new List<ScoredPlayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            top.Add(new(reader.GetGuid(0), reader.GetInt64(1)));
        }

        return top;
    }

    public async Task<long?> ScoreAsync(int week, Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT value FROM rivalry.challenge_scores WHERE week = @week AND owner_id = @owner AND value > 0", cancellationToken);
        command.Parameters.AddWithValue("week", week);
        command.Parameters.AddWithValue("owner", owner);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<long> CountAboveAsync(int week, long value, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT count(*) FROM rivalry.challenge_scores WHERE week = @week AND value > @value", cancellationToken);
        command.Parameters.AddWithValue("week", week);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<FirstState>> FirstsAsync(CancellationToken cancellationToken)
    {
        var taken = new Dictionary<string, (Guid Owner, DateTimeOffset At)>();
        await using var command = await db.CommandAsync("SELECT id, owner_id, at FROM rivalry.firsts", cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            taken[reader.GetString(0)] = (reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2));
        }

        return Firsts.All.Select(f => taken.TryGetValue(f.Id, out var t) ? new FirstState(f, t.Owner, t.At) : new FirstState(f, null, null)).ToList();
    }

    /// <summary>Everything the "Rywalizacja" tab shows: this week's challenge, last week's podium, the player's trophies and the firsts.</summary>
    public async Task<object> ViewAsync(Guid player, CancellationToken cancellationToken)
    {
        await SettleAsync(cancellationToken);
        var now = time.GetUtcNow();
        var week = Challenges.Week(now);
        var challenge = Challenges.For(week);
        var top = await TopAsync(week, 10, cancellationToken);
        var mine = await ScoreAsync(week, player, cancellationToken);
        var place = mine is { } value ? await CountAboveAsync(week, value, cancellationToken) + 1 : (long?)null;

        var last = new List<ChallengeWinnerRow>();
        var trophies = new List<ChallengeWinnerRow>();
        await using (var command = await db.CommandAsync(
            """
            SELECT week, place, owner_id, value, prize_cents FROM rivalry.challenge_winners WHERE week = @last ORDER BY place;
            SELECT week, place, owner_id, value, prize_cents FROM rivalry.challenge_winners WHERE owner_id = @player ORDER BY week DESC, place
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("last", week - 1);
            command.Parameters.AddWithValue("player", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                last.Add(Winner(reader));
            }

            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                trophies.Add(Winner(reader));
            }
        }

        var firsts = await FirstsAsync(cancellationToken);
        var named = await names.NamesAsync(
            top.Select(t => t.Owner).Concat(last.Select(l => l.OwnerId)).Concat(firsts.Where(f => f.Owner is not null).Select(f => f.Owner!.Value))
                .Append(player).Distinct().ToList(),
            cancellationToken);
        string Name(Guid id) => named.GetValueOrDefault(id, "?");
        string Score(Challenge c, long v) => Rules.Count(v, c.One, c.Few, c.Many);

        var places = new long[top.Count];
        for (var i = 0; i < top.Count; i++)
        {
            places[i] = i > 0 && top[i].Value == top[i - 1].Value ? places[i - 1] : i + 1;
        }

        var previous = Challenges.For(week - 1);
        var next = Challenges.For(week + 1);
        return new
        {
            challenge = new
            {
                id = challenge.Id,
                name = challenge.Name,
                description = challenge.Description,
                // Unix milliseconds.
                endsAt = Challenges.WeekStart(week + 1).ToUnixTimeMilliseconds(),
                prizes = Challenges.PrizesCents,
                top = top.Select((t, i) => new { place = places[i], name = Name(t.Owner), value = t.Value, text = Score(challenge, t.Value), mine = t.Owner == player }),
                me = mine is { } m ? new { place, value = m, text = Score(challenge, m) } : null,
                next = new { id = next.Id, name = next.Name, description = next.Description },
            },
            lastWeek = new
            {
                name = previous.Name,
                winners = last.Select(w => new { place = w.Place, name = Name(w.OwnerId), text = Score(previous, w.Value), prizeCents = w.PrizeCents, mine = w.OwnerId == player }),
            },
            trophies = trophies.Select(t => new { place = t.Place, name = Challenges.For(t.Week).Name, weekStart = Challenges.WeekStart(t.Week).ToUnixTimeMilliseconds(), prizeCents = t.PrizeCents }),
            firsts = firsts.Select(f => new
            {
                id = f.First.Id,
                name = f.First.Name,
                description = f.First.Description,
                owner = f.Owner is { } o ? Name(o) : null,
                mine = f.Owner == player,
                at = f.At?.ToUnixTimeMilliseconds(),
            }),
            serverTime = now.ToUnixTimeMilliseconds(),
        };
    }

    private static ChallengeWinnerRow Winner(Npgsql.NpgsqlDataReader reader) => new()
    {
        Week = reader.GetInt32(0),
        Place = reader.GetInt32(1),
        OwnerId = reader.GetGuid(2),
        Value = reader.GetInt64(3),
        PrizeCents = reader.GetInt64(4),
    };
}

/// <summary>This week's challenge as one of the player rankings, so it shows beside the richest and the rest.</summary>
public sealed class ChallengeRanking(RivalryService rivalry, TimeProvider time) : IPlayerRanking
{
    private Challenge Challenge => Challenges.For(Challenges.Week(time.GetUtcNow()));

    public string Id => "challenge";

    public string Name => $"Wyzwanie: {Challenge.Name}";

    public int Order => 60;

    public string Describe(long value) => Rules.Count(value, Challenge.One, Challenge.Few, Challenge.Many);

    public async Task<IReadOnlyList<RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken) =>
        (await rivalry.TopAsync(Challenges.Week(time.GetUtcNow()), count, cancellationToken)).Select(p => new RankedPlayer(p.Owner, p.Value)).ToList();

    public Task<long?> ValueAsync(Guid accountId, CancellationToken cancellationToken) =>
        rivalry.ScoreAsync(Challenges.Week(time.GetUtcNow()), accountId, cancellationToken);

    public Task<long> CountAboveAsync(long value, CancellationToken cancellationToken) =>
        rivalry.CountAboveAsync(Challenges.Week(time.GetUtcNow()), value, cancellationToken);
}
