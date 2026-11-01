using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Achievements;

/// <summary>The badges a player unlocked, as honours for the trophy case.</summary>
public sealed class BadgeHonours(AchievementService achievements) : IHonours
{
    public async Task<IReadOnlyList<Honour>> HonoursAsync(Guid player, CancellationToken cancellationToken) =>
        (await achievements.StateAsync(player, cancellationToken))
            .Where(b => b.UnlockedAt is not null)
            .OrderByDescending(b => b.UnlockedAt)
            .Select(b => new Honour("badge", b.Badge.Name, b.Badge.Description, b.UnlockedAt!.Value))
            .ToList();
}

public sealed record ShowcaseRequest(string? Name);

/// <summary>
/// What stands in a player's trophy case: their world firsts, weekly trophies and badges, newest first. Anybody may look,
/// like at the rankings; without a name it is the asking player's own case.
/// </summary>
public sealed class GetShowcaseHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    /// <summary>The most honours of one kind shown, so a case with hundreds of badges stays light.</summary>
    public const int ShownPerKind = 60;

    public string Type => "showcase.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        var request = payload is { ValueKind: JsonValueKind.Object } ? Payload.Read<ShowcaseRequest>(payload) : new ShowcaseRequest(null);
        await using var scope = scopes.CreateAsyncScope();
        var names = scope.ServiceProvider.GetRequiredService<ICharacterNames>();
        var owner = string.IsNullOrWhiteSpace(request.Name)
            ? player
            : await names.AccountAsync(request.Name.Trim(), cancellationToken) ?? throw new GameException("unknown_player", "There is no such player.");
        var name = (await names.NamesAsync([owner], cancellationToken)).GetValueOrDefault(owner)
            ?? throw new GameException("no_character", "The player has no character yet.");
        var honours = new List<Honour>();
        foreach (var source in scope.ServiceProvider.GetServices<IHonours>())
        {
            honours.AddRange(await source.HonoursAsync(owner, cancellationToken));
        }

        return new Reply("showcase", new
        {
            name,
            mine = owner == player,
            badges = honours.Count(h => h.Kind == "badge"),
            totalBadges = Badges.All.Count,
            honours = honours
                .GroupBy(h => h.Kind)
                .SelectMany(g => g.OrderByDescending(h => h.At).Take(ShownPerKind))
                .OrderBy(h => h.Kind switch { "first" => 0, "trophy" => 1, _ => 2 })
                .ThenByDescending(h => h.At)
                .Select(h => new { kind = h.Kind, name = h.Name, description = h.Description, at = h.At.ToUnixTimeMilliseconds() }),
        });
    }
}
