using System.Text.Json;

namespace FactoryWorld.Modules.Abstractions;

/// <summary>
/// Every WebSocket frame is one JSON envelope: { "type": "...", "id": "...", "payload": { ... } }.
/// The client sets <c>id</c> on requests; the server echoes it on the reply.
/// </summary>
public sealed record Envelope(string Type, string? Id = null, JsonElement? Payload = null);

/// <summary>What the server knows about the connection a message arrived on.</summary>
public sealed class ClientContext
{
    public required Guid ConnectionId { get; init; }

    /// <summary>Set once the player has logged in on this connection.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>Per-connection state that a module keeps for itself, keyed by a name it owns.</summary>
    public Dictionary<string, object> Items { get; } = new();

    /// <summary>Messages for the player that answer no request (e.g. an unlocked achievement),
    /// sent right after the reply being handled.</summary>
    public Queue<Envelope> Outbox { get; } = new();

    /// <summary>Whether the player's game asked ("achievements.watch") to be told of badges, finds and treats as they come;
    /// bots and tests that did not ask get replies only.</summary>
    public bool Watching { get; set; }

    /// <summary>Set by the connection: sends the player this message and closes the connection, e.g. when the
    /// account logged in somewhere else (<see cref="OnlinePlayers"/>). Null where there is no socket (bots).</summary>
    public Action<Envelope>? Close { get; set; }

    public Guid RequireAccount() =>
        AccountId ?? throw new GameException("not_logged_in", "Log in first.");
}

/// <summary>A reply to a client request: a message type and an optional payload.</summary>
public sealed record Reply(string Type, object? Payload = null);

/// <summary>
/// Handles one client message type. Modules register handlers as
/// <c>services.AddSingleton&lt;IMessageHandler, MyHandler&gt;()</c>.
/// </summary>
public interface IMessageHandler
{
    string Type { get; }

    /// <summary>False for the few messages a player may send before logging in.</summary>
    bool RequiresAccount => true;

    Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken);
}

/// <summary>
/// Sees every request a handler answered without an error, after the reply is ready and before it is
/// sent, e.g. to follow a player's progress through the game. Modules register observers as
/// <c>services.AddSingleton&lt;IMessageObserver, MyObserver&gt;()</c>. A failing observer is logged and
/// never spoils the reply.
/// </summary>
public interface IMessageObserver
{
    Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken);
}

/// <summary>A failure the client may show to the player, e.g. "not enough money".</summary>
/// <param name="details">More for the client than the code says, e.g. how many pieces still fit; sent as the error's <c>details</c>.</param>
public sealed class GameException(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public object? Details { get; } = details;
}

public static class Payload
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads a request payload into <typeparamref name="T"/>, or fails with <c>bad_payload</c>.</summary>
    public static T Read<T>(JsonElement? payload) where T : class
    {
        try
        {
            if (payload is { ValueKind: JsonValueKind.Object } p && p.Deserialize<T>(Json) is { } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        throw new GameException("bad_payload", "Message payload is missing or malformed.");
    }
}
