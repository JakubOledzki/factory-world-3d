using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>What the server answered a bot: the reply's type and payload, or the error's code.</summary>
public sealed record BotReply(string Type, JsonElement Payload, string? Error)
{
    public bool Ok => Error is null;

    public bool Is(string type) => Ok && Type == type;
}

/// <summary>
/// A bot's connection to the game: it sends the same messages a client would and they go through the
/// same handlers and observers, so a bot earns XP, badges, tutorial rewards and a place in the rankings
/// exactly as a player does and no module needs to know about bots. The context lives as long as the
/// server, like a player who never logs out.
/// </summary>
public sealed class BotClient(
    Guid accountId, IReadOnlyDictionary<string, IMessageHandler> handlers, IReadOnlyList<IMessageObserver> observers, ServerLoad load, ILogger logger)
{
    public ClientContext Context { get; } = new() { ConnectionId = Guid.NewGuid(), AccountId = accountId };

    public async Task<BotReply> SendAsync(string type, object? payload, CancellationToken cancellationToken)
    {
        if (!handlers.TryGetValue(type, out var handler))
        {
            throw new InvalidOperationException($"No handler for '{type}'.");
        }

        var started = ServerLoad.Start();
        try
        {
            return await SendAsync(handler, type, payload, cancellationToken);
        }
        finally
        {
            // Counted apart from the players' own messages of the same kind.
            load.Record("bot " + type, started);
        }
    }

    private async Task<BotReply> SendAsync(IMessageHandler handler, string type, object? payload, CancellationToken cancellationToken)
    {
        var request = payload is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(payload, Payload.Json);
        Reply reply;
        try
        {
            reply = await handler.HandleAsync(request, Context, cancellationToken);
        }
        catch (GameException ex)
        {
            return new BotReply("error", default, ex.Code);
        }

        var envelope = new Envelope(reply.Type, "bot", JsonSerializer.SerializeToElement(reply.Payload, Payload.Json));
        foreach (var observer in observers)
        {
            try
            {
                await observer.ObserveAsync(new Envelope(type, "bot", request), envelope, Context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Observer {Observer} failed on a bot's {Type}", observer.GetType().Name, type);
            }
        }

        // Nobody reads a bot's notices (unlocked badges and the like).
        Context.Outbox.Clear();
        return new BotReply(envelope.Type, envelope.Payload!.Value, null);
    }
}

/// <summary>Reading replies: a missing property is null or zero, never an exception, as a reply may change shape.</summary>
internal static class Json
{
    public static JsonElement? Get(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    public static long Long(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var value) ? value : 0;

    public static long? LongOrNull(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var value) ? value : null;

    public static double Double(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : 0;

    public static string? Text(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    public static bool True(this JsonElement e, string name) => e.Get(name) is { ValueKind: JsonValueKind.True };

    public static Guid? Id(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.String } s && Guid.TryParse(s.GetString(), out var id) ? id : null;

    public static IEnumerable<JsonElement> Each(this JsonElement e, string name) =>
        e.Get(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    /// <summary>The total XP a reply shows after the action, if it shows one.</summary>
    public static long? Xp(this JsonElement e) =>
        e.Get("xp") is { ValueKind: JsonValueKind.Object } xp ? xp.LongOrNull("xp") : null;
}
