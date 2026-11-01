using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Achievements;

/// <summary>Counts what each player does and tells them, on a connection that asked to be told, of every badge they unlock.</summary>
public sealed class AchievementObserver(IServiceScopeFactory scopes) : IMessageObserver
{
    /// <summary>The highest values (level, balance...) this connection has recorded, by stat.</summary>
    private const string Recorded = "achievements.recorded";

    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        if (client.AccountId is not { } owner || reply.Payload is not { } payload)
        {
            return;
        }

        var counts = Tally.Count(new HandledMessage(request.Type, request.Payload, reply.Type, payload));
        // Many replies show the balance or the level: one no higher than already recorded changes nothing.
        if (client.Items.GetValueOrDefault(Recorded) is not Highest { } highest || highest.Owner != owner)
        {
            client.Items[Recorded] = highest = new Highest(owner, []);
        }

        var recorded = highest.Values;

        foreach (var stat in counts.Keys.Where(Stats.IsHighest).ToList())
        {
            if (recorded.TryGetValue(stat, out var known) && counts[stat] <= known)
            {
                counts.Remove(stat);
            }
        }

        if (counts.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var unlocked = await scope.ServiceProvider.GetRequiredService<AchievementService>().RecordAsync(owner, counts, cancellationToken);
        foreach (var (stat, value) in counts.Where(c => Stats.IsHighest(c.Key)))
        {
            recorded[stat] = value;
        }
        if (!client.Watching)
        {
            return;
        }

        foreach (var badge in unlocked)
        {
            client.Outbox.Enqueue(new Envelope("achievement.unlocked", null, JsonSerializer.SerializeToElement(new
            {
                id = badge.Id,
                group = badge.Group,
                name = badge.Name,
                description = badge.Description,
            }, Payload.Json)));
        }
    }
}

/// <summary>The highest values recorded for the player logged in on a connection.</summary>
internal sealed record Highest(Guid Owner, Dictionary<string, long> Values);

public sealed class GetAchievementsHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "achievements.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var badges = await scope.ServiceProvider.GetRequiredService<AchievementService>().StateAsync(client.RequireAccount(), cancellationToken);
        return new Reply("achievements", new
        {
            unlocked = badges.Count(b => b.UnlockedAt is not null),
            total = badges.Count,
            groups = badges.GroupBy(b => b.Badge.Group).Select(g => new
            {
                name = g.Key,
                badges = g.Select(b => new
                {
                    id = b.Badge.Id,
                    name = b.Badge.Name,
                    description = b.Badge.Description,
                    goal = b.Badge.Goal,
                    progress = Math.Min(b.Value, b.Badge.Goal),
                    text = Badges.Progress(b.Badge, b.Value),
                    unlockedAt = b.UnlockedAt?.ToUnixTimeMilliseconds(),
                }),
            }),
        });
    }
}

/// <summary>From now on the connection gets "achievement.unlocked" after the reply that unlocked a badge.</summary>
public sealed class WatchAchievementsHandler : IMessageHandler
{
    public string Type => "achievements.watch";

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        client.RequireAccount();
        client.Watching = true;
        return Task.FromResult(new Reply("achievements.watching"));
    }
}

public sealed record RankingsRequest(string? Ranking);

/// <summary>The best players of one ranking, and where the asking player stands in it.</summary>
public sealed class GetRankingsHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public const int TopCount = 10;

    public string Type => "rankings.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        var request = payload is { ValueKind: JsonValueKind.Object } ? Payload.Read<RankingsRequest>(payload) : new RankingsRequest(null);
        await using var scope = scopes.CreateAsyncScope();
        var rankings = scope.ServiceProvider.GetServices<IPlayerRanking>().OrderBy(r => r.Order).ToList();
        var ranking = request.Ranking is null
            ? rankings[0]
            : rankings.FirstOrDefault(r => r.Id == request.Ranking)
              ?? throw new GameException("unknown_ranking", "There is no such ranking.");

        // A few more than shown, as players without a character are left out.
        var top = (await ranking.TopAsync(TopCount + 5, cancellationToken)).ToList();
        var names = await scope.ServiceProvider.GetRequiredService<ICharacterNames>().NamesAsync(
            top.Select(p => p.AccountId).Append(player).ToList(), cancellationToken);
        top = top.Where(p => names.ContainsKey(p.AccountId)).Take(TopCount).ToList();

        // Equal values share a place: 1, 2, 2, 4.
        var places = new long[top.Count];
        for (var i = 0; i < top.Count; i++)
        {
            places[i] = i > 0 && top[i].Value == top[i - 1].Value ? places[i - 1] : i + 1;
        }

        object? me = null;
        if (names.ContainsKey(player) && await ranking.ValueAsync(player, cancellationToken) is { } mine)
        {
            var index = top.FindIndex(p => p.AccountId == player);
            var place = index >= 0 ? places[index] : await ranking.CountAboveAsync(mine, cancellationToken) + 1;
            me = new { place, value = mine, text = ranking.Describe(mine) };
        }

        return new Reply("rankings", new
        {
            rankings = rankings.Select(r => new { id = r.Id, name = r.Name }),
            ranking = ranking.Id,
            top = top.Select((p, i) => new
            {
                place = places[i],
                name = names[p.AccountId],
                value = p.Value,
                text = ranking.Describe(p.Value),
                mine = p.AccountId == player,
            }),
            me,
        });
    }
}
