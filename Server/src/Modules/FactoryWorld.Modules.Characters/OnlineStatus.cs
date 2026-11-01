using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Characters;

public sealed record OnlineRequest(string[]? Names);

/// <summary>Which of these characters' players are in the game now, for the dot next to a name
/// (green online, red offline). Names come back as asked; an unknown name is offline.</summary>
public sealed class OnlineHandler(IServiceScopeFactory scopes, OnlinePlayers online) : IMessageHandler
{
    /// <summary>The most names one request asks about.</summary>
    public const int MaxNames = 200;

    public string Type => "players.online";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var names = (Payload.Read<OnlineRequest>(payload).Names ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(MaxNames).ToList();
        var accounts = new Dictionary<string, Guid>();
        if (names.Count > 0)
        {
            await using var scope = scopes.CreateAsyncScope();
            await using var command = await scope.ServiceProvider.GetRequiredService<GameDb>().CommandAsync(
                """SELECT "NormalizedName", "AccountId" FROM characters.characters WHERE "NormalizedName" = ANY(@names)""", cancellationToken);
            command.Parameters.AddWithValue("names", names.Select(Normalized).Distinct().ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts[reader.GetString(0)] = reader.GetGuid(1);
            }
        }

        return new Reply("players.online", new
        {
            players = names.Select(n => new
            {
                name = n,
                online = accounts.TryGetValue(Normalized(n), out var account) && online.IsOnline(account),
            }),
        });
    }

    private static string Normalized(string name) => name.Trim().ToLowerInvariant();
}
