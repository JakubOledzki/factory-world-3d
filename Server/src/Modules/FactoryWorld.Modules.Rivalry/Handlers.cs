using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Rivalry;

/// <summary>Reads firsts and challenge scores from every request a player's handler answered, and tells the player of a first they took.</summary>
public sealed class RivalryObserver(IServiceScopeFactory scopes, OnlinePlayers online, RivalryCache claimed, TimeProvider time) : IMessageObserver
{
    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        // The server's bots play along, but compete for nothing.
        if (client.AccountId is not { } owner || reply.Payload is not { } payload || online.IsBot(owner))
        {
            return;
        }

        var reached = Firsts.Reached(request.Type, request.Payload, reply.Type, payload)
            .Where(id => !claimed.Loaded || !claimed.Contains(id))
            .ToList();
        var score = Challenges.Score(Challenges.For(Challenges.Week(time.GetUtcNow())), request.Type, request.Payload, payload);
        if (reached.Count == 0 && score <= 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var taken = await scope.ServiceProvider.GetRequiredService<RivalryService>().RecordAsync(owner, reached, score, cancellationToken);
        foreach (var first in taken)
        {
            // Shown like a new badge.
            client.Outbox.Enqueue(new Envelope("achievement.unlocked", null, JsonSerializer.SerializeToElement(new
            {
                id = $"first_{first.Id}",
                group = "Pierwsi na świecie",
                title = "Pierwszy na świecie!",
                name = first.Name,
                description = first.Description,
            }, Payload.Json)));
        }
    }
}

/// <summary>This week's challenge, last week's podium, the player's trophies and the firsts in the world.</summary>
public sealed class GetRivalryHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "rivalry.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("rivalry", await scope.ServiceProvider.GetRequiredService<RivalryService>().ViewAsync(client.RequireAccount(), cancellationToken));
    }
}
