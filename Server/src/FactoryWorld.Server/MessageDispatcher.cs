using System.Text.Json;
using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Server;

/// <summary>Routes each incoming envelope to the module handler registered for its type.</summary>
public sealed class MessageDispatcher(
    IEnumerable<IMessageHandler> handlers,
    IEnumerable<IMessageObserver> observers,
    ServerLoad load,
    ILogger<MessageDispatcher> logger)
{
    public static JsonSerializerOptions Json => Payload.Json;

    private readonly Dictionary<string, IMessageHandler> _handlers =
        handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);

    private readonly IMessageObserver[] _observers = observers.ToArray();

    public async Task<Envelope> DispatchAsync(string frame, ClientContext client, CancellationToken cancellationToken)
    {
        Envelope? request;
        try
        {
            request = JsonSerializer.Deserialize<Envelope>(frame, Json);
        }
        catch (JsonException)
        {
            return Error(null, "bad_json", "Message is not valid JSON.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Type))
        {
            return Error(request?.Id, "bad_envelope", "Message has no type.");
        }

        if (!_handlers.TryGetValue(request.Type, out var handler))
        {
            return Error(request.Id, "unknown_type", $"Unknown message type '{request.Type}'.");
        }

        if (handler.RequiresAccount && client.AccountId is null)
        {
            return Error(request.Id, "not_logged_in", "Log in first.");
        }

        var started = ServerLoad.Start();
        try
        {
            var reply = await handler.HandleAsync(request.Payload, client, cancellationToken);
            var envelope = new Envelope(reply.Type, request.Id, JsonSerializer.SerializeToElement(reply.Payload, Json));
            await ObserveAsync(request, envelope, client, cancellationToken);
            return envelope;
        }
        catch (GameException ex)
        {
            return Error(request.Id, ex.Code, ex.Message, ex.Details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Handler for {Type} failed", request.Type);
            return Error(request.Id, "internal", "Server error.");
        }
        finally
        {
            if (load.Record(request.Type, started) is var took && took > ServerLoad.Slow)
            {
                logger.LogWarning("Slow {Type}: {Milliseconds:0} ms", request.Type, took.TotalMilliseconds);
            }
        }
    }

    private async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        foreach (var observer in _observers)
        {
            try
            {
                await observer.ObserveAsync(request, reply, client, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Observer {Observer} failed on {Type}", observer.GetType().Name, request.Type);
            }
        }
    }

    private static Envelope Error(string? id, string code, string message, object? details = null) =>
        new("error", id, details is null
            ? JsonSerializer.SerializeToElement(new { code, message }, Json)
            : JsonSerializer.SerializeToElement(new { code, message, details }, Json));
}
