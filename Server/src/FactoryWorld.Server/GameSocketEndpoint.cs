using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Server;

/// <summary>One WebSocket per player. Requests are handled in order, one at a time; each reply may be
/// followed by messages from <see cref="ClientContext.Outbox"/>, which have no id.</summary>
public static class GameSocketEndpoint
{
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>How long a closed connection waits for the client to answer the close before it is dropped.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    public static async Task HandleAsync(HttpContext context, MessageDispatcher dispatcher, OnlinePlayers online, ILogger<MessageDispatcher> logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Compressed when the client offers it (browsers do, so the web and phone game gets its plots and logistics several
        // times smaller); each message on its own, without a dictionary shared across messages, so what one message holds
        // (a session token) never shapes how the next one compresses.
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            DangerousEnableCompression = true,
            DisableServerContextTakeover = true,
        });
        var client = new ClientContext { ConnectionId = Guid.NewGuid() };
        var cancellationToken = context.RequestAborted;
        logger.LogInformation("Client {ConnectionId} connected", client.ConnectionId);

        // Replies and a close from elsewhere (the account logged in on another connection) take turns sending.
        var sending = new SemaphoreSlim(1, 1);
        var closed = false;
        client.Close = notice => _ = Task.Run(async () =>
        {
            await sending.WaitAsync(CancellationToken.None);
            try
            {
                if (closed)
                {
                    return;
                }

                closed = true;
                logger.LogInformation("Client {ConnectionId} closed: {Type}", client.ConnectionId, notice.Type);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(notice, MessageDispatcher.Json),
                    WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, notice.Type, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
            {
                // The connection is gone already.
                return;
            }
            finally
            {
                sending.Release();
            }

            // A client that never answers the close is dropped, so the loop below ends.
            await Task.Delay(CloseTimeout, CancellationToken.None);
            if (socket.State != WebSocketState.Closed)
            {
                socket.Abort();
            }
        });

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var frame = await ReceiveTextAsync(socket, cancellationToken);
                if (frame is null || closed)
                {
                    break;
                }

                var reply = await dispatcher.DispatchAsync(frame, client, cancellationToken);
                await sending.WaitAsync(cancellationToken);
                try
                {
                    if (closed)
                    {
                        break;
                    }

                    var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, MessageDispatcher.Json);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                    while (client.Outbox.TryDequeue(out var notice))
                    {
                        bytes = JsonSerializer.SerializeToUtf8Bytes(notice, MessageDispatcher.Json);
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                    }
                }
                finally
                {
                    sending.Release();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The client went away; nothing to clean up yet.
        }
        finally
        {
            online.Leave(client);
        }

        await sending.WaitAsync(CancellationToken.None);
        try
        {
            if (!closed && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                closed = true;
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // The client went away while closing.
        }
        finally
        {
            sending.Release();
        }

        logger.LogInformation("Client {ConnectionId} disconnected", client.ConnectionId);
    }

    /// <summary>Reads one whole text message, or returns null when the client closes.</summary>
    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big", cancellationToken);
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }
}
