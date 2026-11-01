using System.Text.Json;
using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Modules.Session;

public static class Protocol
{
    /// <summary>Bumped on every breaking change to the message format.</summary>
    public const int Version = 1;
}

/// <summary>First message after connecting. Checks the client speaks our protocol version.</summary>
public sealed class HelloHandler : IMessageHandler
{
    public string Type => "hello";

    public bool RequiresAccount => false;

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var clientVersion = payload is { ValueKind: JsonValueKind.Object } p
            && p.TryGetProperty("protocol", out var v) && v.TryGetInt32(out var n) ? n : 0;

        if (clientVersion != Protocol.Version)
        {
            throw new GameException("protocol_mismatch",
                $"Client protocol {clientVersion}, server protocol {Protocol.Version}. Update the game.");
        }

        return Task.FromResult(new Reply("welcome", new
        {
            protocol = Protocol.Version,
            connectionId = client.ConnectionId,
        }));
    }
}

public sealed class PingHandler(TimeProvider time) : IMessageHandler
{
    public string Type => "ping";

    public bool RequiresAccount => false;

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken) =>
        Task.FromResult(new Reply("pong", new { serverTime = time.GetUtcNow() }));
}
