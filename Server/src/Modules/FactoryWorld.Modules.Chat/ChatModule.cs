using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Chat;

public static class ChatRules
{
    public const string World = "world";
    public const string Plot = "plot";

    public const int MaxLength = 200;

    /// <summary>Who the server's own announcements (<see cref="ChatRoom.Announce"/>) come from; they carry an empty sender id.</summary>
    public const string ServerName = "Serwer";

    /// <summary>Messages kept for everybody, and for each plot; older ones are forgotten (chat is not stored in the database).</summary>
    public const int WorldKept = 100;
    public const int PlotKept = 30;

    /// <summary>The most messages one answer carries, so a client that was away does not get the whole history at once.</summary>
    public const int MaxPerReply = 50;

    /// <summary>A player sends at most <see cref="Burst"/> messages within <see cref="Window"/>.</summary>
    public const int Burst = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>A plot's chat is forgotten this long after its last message.</summary>
    public static readonly TimeSpan PlotQuiet = TimeSpan.FromHours(1);

    /// <summary>A ping points at a tile of the plot (as the world's PlotLayout.Size).</summary>
    public const int PlotTiles = 250;

    /// <summary>The text as it is shown: control characters become spaces, the ends are trimmed.</summary>
    public static string Clean(string? text)
    {
        var clean = new StringBuilder();
        foreach (var c in text ?? "")
        {
            clean.Append(char.IsControl(c) ? ' ' : c);
        }

        return clean.ToString().Trim();
    }
}

/// <param name="PingX">The tile a ping points at on the plot, or null for a plain message.</param>
public sealed record ChatMessage(
    long Id, string Channel, int? PlotX, int? PlotY, Guid From, string Name, string Text, int? PingX, int? PingY, DateTimeOffset At);

/// <summary>
/// The chat: everybody's channel and one for each plot, kept in memory only. Every message gets the next number,
/// so a client asks for what came after the last one it saw.
/// </summary>
public sealed class ChatRoom(TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Queue<ChatMessage> _world = new();
    private readonly Dictionary<(int X, int Y), Queue<ChatMessage>> _plots = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _sent = new();
    private long _last;
    private DateTimeOffset _nextSweep;

    public long Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Adds a message; fails with <c>chat_too_fast</c> when the player sent too many just now.</summary>
    public ChatMessage Post(Guid from, string name, string channel, (int X, int Y)? plot, string text, (int X, int Y)? ping)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            Sweep(now);
            if (!_sent.TryGetValue(from, out var sent))
            {
                sent = new Queue<DateTimeOffset>();
                _sent[from] = sent;
            }

            while (sent.Count > 0 && now - sent.Peek() >= ChatRules.Window)
            {
                sent.Dequeue();
            }

            if (sent.Count >= ChatRules.Burst)
            {
                throw new GameException("chat_too_fast", $"At most {ChatRules.Burst} messages in {ChatRules.Window.TotalSeconds:0} seconds.");
            }

            sent.Enqueue(now);
            var message = new ChatMessage(++_last, channel, plot?.X, plot?.Y, from, name, text, ping?.X, ping?.Y, now);
            if (plot is { } at)
            {
                if (!_plots.TryGetValue(at, out var kept))
                {
                    kept = new Queue<ChatMessage>();
                    _plots[at] = kept;
                }

                Keep(kept, message, ChatRules.PlotKept);
            }
            else
            {
                Keep(_world, message, ChatRules.WorldKept);
            }

            return message;
        }
    }

    /// <summary>A message of the server's own in everybody's channel, from <see cref="ChatRules.ServerName"/>; no limit on how often.</summary>
    public ChatMessage Announce(string text)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var message = new ChatMessage(++_last, ChatRules.World, null, null, Guid.Empty, ChatRules.ServerName, ChatRules.Clean(text), null, null, now);
            Keep(_world, message, ChatRules.WorldKept);
            return message;
        }
    }

    /// <summary>Messages after <paramref name="after"/>: everybody's and, when given, those of one plot; the newest
    /// <see cref="ChatRules.MaxPerReply"/> of them, oldest first.</summary>
    public IReadOnlyList<ChatMessage> Since(long after, (int X, int Y)? plot)
    {
        lock (_gate)
        {
            IEnumerable<ChatMessage> found = _world.Where(m => m.Id > after);
            if (plot is { } at && _plots.TryGetValue(at, out var kept))
            {
                found = found.Concat(kept.Where(m => m.Id > after));
            }

            return found.OrderByDescending(m => m.Id).Take(ChatRules.MaxPerReply).Reverse().ToList();
        }
    }

    private static void Keep(Queue<ChatMessage> kept, ChatMessage message, int count)
    {
        kept.Enqueue(message);
        while (kept.Count > count)
        {
            kept.Dequeue();
        }
    }

    /// <summary>Now and then forgets plots nobody spoke on for a while, and players who went quiet.</summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now < _nextSweep)
        {
            return;
        }

        _nextSweep = now + TimeSpan.FromMinutes(1);
        foreach (var (at, kept) in _plots.ToList())
        {
            if (kept.Count == 0 || now - kept.Last().At > ChatRules.PlotQuiet)
            {
                _plots.Remove(at);
            }
        }

        foreach (var (player, sent) in _sent.ToList())
        {
            if (sent.Count == 0 || now - sent.Last() >= ChatRules.Window)
            {
                _sent.Remove(player);
            }
        }
    }
}

public sealed class ChatService(ChatRoom room, ICharacterNames names, IPlotObjects plots)
{
    public async Task<ChatMessage> SendAsync(Guid player, string? channel, int? x, int? y, string? text, int? pingX, int? pingY, CancellationToken cancellationToken)
    {
        var clean = ChatRules.Clean(text);
        if (clean.Length == 0)
        {
            throw new GameException("empty_message", "Write something first.");
        }

        if (clean.Length > ChatRules.MaxLength)
        {
            throw new GameException("message_too_long", $"A message has at most {ChatRules.MaxLength} characters.");
        }

        (int X, int Y)? plot = null;
        (int X, int Y)? ping = null;
        switch (channel)
        {
            case ChatRules.World:
                if (pingX is not null || pingY is not null)
                {
                    throw new GameException("bad_ping", "A ping points at a tile of a plot.");
                }

                break;
            case ChatRules.Plot:
                plot = await RequirePlotAsync(player, x, y, cancellationToken);
                if (pingX is not null || pingY is not null)
                {
                    if (pingX is not { } px || pingY is not { } py || px is < 0 or >= ChatRules.PlotTiles || py is < 0 or >= ChatRules.PlotTiles)
                    {
                        throw new GameException("bad_ping", "A ping points at a tile of the plot.");
                    }

                    ping = (px, py);
                }

                break;
            default:
                throw new GameException("bad_channel", "Write to the world or to the plot.");
        }

        var name = (await names.NamesAsync([player], cancellationToken)).GetValueOrDefault(player)
            ?? throw new GameException("no_character", "Create a character first.");
        return room.Post(player, name, channel, plot, clean, ping);
    }

    /// <summary>What came after <paramref name="after"/>, with the plot's own messages when the player may come onto it.</summary>
    public async Task<IReadOnlyList<ChatMessage>> SinceAsync(Guid player, long after, int? x, int? y, CancellationToken cancellationToken)
    {
        var plot = x is null && y is null ? ((int, int)?)null : await RequirePlotAsync(player, x, y, cancellationToken);
        return room.Since(after, plot);
    }

    private async Task<(int X, int Y)> RequirePlotAsync(Guid player, int? x, int? y, CancellationToken cancellationToken)
    {
        if (x is not { } px || y is not { } py)
        {
            throw new GameException("bad_plot", "Say which plot.");
        }

        // Anyone who may come onto the plot hears it: the owner, the company, and visitors of a public plot.
        await plots.RequireOwnerAsync(player, px, py, cancellationToken, PlotRight.Visit);
        return (px, py);
    }

    public static object Json(ChatMessage m) => new
    {
        id = m.Id,
        channel = m.Channel,
        from = m.From,
        name = m.Name,
        text = m.Text,
        ping = m.PingX is { } px && m.PingY is { } py ? new { x = px, y = py } : null,
        at = m.At.ToUnixTimeMilliseconds(),
    };
}

/// <summary>Announcements go into everybody's chat.</summary>
public sealed class ChatAnnouncements(ChatRoom room) : IAnnouncements
{
    public void Announce(string text) => room.Announce(text);
}

public sealed record ChatSendRequest(string? Channel, int? X, int? Y, string? Text, int? PingX, int? PingY);

public sealed record ChatGetRequest(long After, int? X, int? Y);

/// <summary>Says something to everybody or to the players on a plot, or points at a tile of the plot.</summary>
public sealed class ChatSendHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "chat.send";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var r = Payload.Read<ChatSendRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<ChatService>()
            .SendAsync(client.RequireAccount(), r.Channel, r.X, r.Y, r.Text, r.PingX, r.PingY, cancellationToken);
        return new Reply("chat.sent", new { message = ChatService.Json(message) });
    }
}

/// <summary>The messages after the last one the client saw; <c>after</c> 0 gives the latest ones.</summary>
public sealed class ChatGetHandler(IServiceScopeFactory scopes, ChatRoom room) : IMessageHandler
{
    public string Type => "chat.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var r = payload is { ValueKind: JsonValueKind.Object } ? Payload.Read<ChatGetRequest>(payload) : new ChatGetRequest(0, null, null);
        var last = room.Last;
        await using var scope = scopes.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<ChatService>()
            .SinceAsync(client.RequireAccount(), Math.Max(0, r.After), r.X, r.Y, cancellationToken);
        return new Reply("chat", new { messages = messages.Select(ChatService.Json), last = Math.Max(last, messages.Count > 0 ? messages[^1].Id : 0) });
    }
}

/// <summary>The chat: everybody's channel and one per plot, in memory, with a limit on how fast a player writes.</summary>
public sealed class ChatModule : IGameModule
{
    public string Name => "Chat";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ChatRoom>();
        services.AddScoped<ChatService>();
        services.AddSingleton<IAnnouncements, ChatAnnouncements>();
        services.AddSingleton<IMessageHandler, ChatSendHandler>();
        services.AddSingleton<IMessageHandler, ChatGetHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}
