using System.Collections.Concurrent;

namespace FactoryWorld.Modules.Abstractions;

/// <summary>
/// Who is in the game right now: the one connection each logged-in account plays on, and the bots in a play
/// session. An account plays on one connection at a time; logging in on another one closes the first.
/// </summary>
public sealed class OnlinePlayers
{
    /// <summary>The notice the replaced connection gets right before it is closed.</summary>
    public const string KickedType = "auth.kicked";

    private readonly ConcurrentDictionary<Guid, ClientContext> _connections = new();
    private readonly ConcurrentDictionary<Guid, byte> _bots = new();
    private readonly ConcurrentDictionary<Guid, byte> _botAccounts = new();

    /// <summary>The account now plays on this connection. The one it played on before, if any, is told
    /// (<see cref="KickedType"/>) and closed.</summary>
    public void Enter(ClientContext client, Guid accountId)
    {
        ClientContext? previous = null;
        _connections.AddOrUpdate(accountId, client, (_, old) =>
        {
            previous = old;
            return client;
        });

        if (previous is not null && previous != client)
        {
            previous.Close?.Invoke(new Envelope(KickedType, null, System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                code = "logged_in_elsewhere",
                message = "You logged in on another device.",
            }, Payload.Json)));
        }
    }

    /// <summary>The connection logged out or went away; the account stays online only if it plays somewhere else.</summary>
    public void Leave(ClientContext client)
    {
        if (client.AccountId is { } accountId)
        {
            _connections.TryRemove(KeyValuePair.Create(accountId, client));
        }
    }

    /// <summary>A bot started (true) or ended (false) a play session.</summary>
    public void SetBot(Guid accountId, bool online)
    {
        if (online)
        {
            _bots[accountId] = 0;
        }
        else
        {
            _bots.TryRemove(accountId, out _);
        }
    }

    /// <summary>How many accounts play on a connection right now.</summary>
    public int Players => _connections.Count;

    /// <summary>How many bots are in a play session right now.</summary>
    public int Bots => _bots.Count;

    /// <summary>The account belongs to one of the server's bots, online or not.</summary>
    public void AddBotAccount(Guid accountId) => _botAccounts[accountId] = 0;

    /// <summary>Whether the account is one of the server's bots (<see cref="AddBotAccount"/>).</summary>
    public bool IsBot(Guid accountId) => _botAccounts.ContainsKey(accountId);

    /// <summary>Every account of the server's bots (<see cref="AddBotAccount"/>), online or not.</summary>
    public Guid[] BotAccounts => _botAccounts.Keys.ToArray();

    public bool IsOnline(Guid accountId) => _connections.ContainsKey(accountId) || _bots.ContainsKey(accountId);
}
