using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Bots;

/// <summary>Why a bot asks players for help.</summary>
public static class HelpReasons
{
    /// <summary>Its line stood still: the bot took the jammed parts up, and a player puts the inserter by the machine back.</summary>
    public const string Stalled = "stalled";

    /// <summary>It put its line up without parts it could neither make nor buy: a player puts them in their places.</summary>
    public const string Parts = "parts";

    /// <summary>It put a belt or an inserter of its line up the wrong way round: a player turns it.</summary>
    public const string Turned = "turned";
}

/// <summary>
/// A bot's call for help, shown as an exclamation mark over its plot on the world map: what a player is to put up or turn
/// on the bot's plot (<see cref="Tasks"/>) for its line to work. Whoever does it gets <see cref="PayCents"/> from the bot's
/// wallet: what the parts they put up are worth at the trader and a little more, and the <see cref="BotRules.HelpFee"/>.
/// </summary>
public sealed record HelpRequest(
    string Bot, string Character, Guid Account, int X, int Y, string Reason,
    IReadOnlyList<HelpTask> Tasks, long PayCents, DateTimeOffset Since)
{
    /// <summary>What a helper brings from their own inventory: one of each part a task puts up.</summary>
    public IReadOnlyList<(string Item, long Quantity)> Needs =>
        Tasks.Where(t => t.Object is null).GroupBy(t => t.Kind).Select(g => (g.Key, (long)g.Count())).ToList();
}

/// <summary>
/// The bots' calls for help, in memory. A bot puts one up when its line does not work; a player takes it on
/// (<c>bots.help</c>), which gives them leave (<see cref="IPlotPermits"/>) to come onto the bot's plot and do what it
/// asks; once they did, the world pays them from the bot's wallet and tells the board (<see cref="IPlotHelpListener"/>),
/// and the bot sees the answer on its next step. After a restart the bots find out again what they need.
/// </summary>
public sealed class BotHelpBoard(IPlotPermits permits, TimeProvider time) : IPlotHelpListener
{
    private readonly object _lock = new();
    private readonly Dictionary<(int X, int Y), HelpRequest> _open = [];
    private readonly Dictionary<(int X, int Y), (Guid Helper, DateTimeOffset Until)> _taken = [];
    private readonly Dictionary<string, HelpRequest> _answered = new(StringComparer.Ordinal);

    /// <summary>The calls nobody is working on: not taken on yet, or their helper ran out of time.</summary>
    public IReadOnlyList<HelpRequest> Open()
    {
        lock (_lock)
        {
            return _open.Values.Where(r => !Taken(r)).OrderBy(r => r.Since).ToList();
        }
    }

    /// <summary>Puts the bot's call up, in place of any it had.</summary>
    public void Post(HelpRequest request)
    {
        lock (_lock)
        {
            Remove(request.Bot);
            _open[(request.X, request.Y)] = request;
        }
    }

    /// <summary>Takes the bot's call down: it means to manage without help. A call a player is working on stays up until
    /// their leave runs out, so nobody works for nothing; false then.</summary>
    public bool Withdraw(string bot)
    {
        lock (_lock)
        {
            if (_open.Values.Any(r => r.Bot == bot && Taken(r)))
            {
                return false;
            }

            Remove(bot);
            return true;
        }
    }

    /// <summary>
    /// A player takes the call on the plot on: for <see cref="BotRules.HelpLeave"/> nobody else can, and they have leave to
    /// do its tasks on the bot's plot. Taking on again a call one has already gives the same leave back.
    /// </summary>
    public (HelpRequest Request, PlotPermit Permit) Take(int x, int y, Guid player)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue((x, y), out var request))
            {
                throw new GameException("no_help_needed", "Nobody asks for help on this plot.");
            }

            if (request.Account == player)
            {
                throw new GameException("no_help_needed", "You cannot help yourself.");
            }

            if (Taken(request))
            {
                if (_taken[(x, y)].Helper != player)
                {
                    throw new GameException("help_taken", "Another player is helping here already.");
                }

                if (permits.Find(x, y) is { } given && given.Helper == player)
                {
                    return (request, given);
                }
            }

            var until = time.GetUtcNow() + BotRules.HelpLeave;
            var permit = new PlotPermit(x, y, request.Account, player, request.Tasks, request.PayCents, until);
            permits.Grant(permit);
            _taken[(x, y)] = (player, until);
            return (request, permit);
        }
    }

    /// <summary>A player did all a call asked and was paid: the call comes down and its bot hears of it.</summary>
    public void Helped(PlotPermit permit)
    {
        lock (_lock)
        {
            if (_open.TryGetValue((permit.X, permit.Y), out var request) && request.Account == permit.Owner)
            {
                _open.Remove((permit.X, permit.Y));
                _taken.Remove((permit.X, permit.Y));
                _answered[request.Bot] = request;
            }
        }
    }

    /// <summary>The bot's call that is up now, if any.</summary>
    public HelpRequest? Of(string bot)
    {
        lock (_lock)
        {
            return _open.Values.FirstOrDefault(r => r.Bot == bot);
        }
    }

    /// <summary>The help the bot got since it last looked, if any.</summary>
    public HelpRequest? TakeAnswer(string bot)
    {
        lock (_lock)
        {
            return _answered.Remove(bot, out var request) ? request : null;
        }
    }

    /// <summary>Whether a player is working on the call now.</summary>
    private bool Taken(HelpRequest request) =>
        _taken.TryGetValue((request.X, request.Y), out var taken) && taken.Until > time.GetUtcNow();

    private void Remove(string bot)
    {
        foreach (var key in _open.Where(p => p.Value.Bot == bot).Select(p => p.Key).ToList())
        {
            _open.Remove(key);
            _taken.Remove(key);
            permits.Revoke(key.X, key.Y);
        }
    }

    public static object Json(HelpRequest r) => new
    {
        x = r.X,
        y = r.Y,
        bot = r.Character,
        reason = r.Reason,
        needs = r.Needs.Select(n => new { id = n.Item, name = Items.Names.GetValueOrDefault(n.Item, n.Item), quantity = n.Quantity }),
        tasks = TasksJson(r),
        payCents = r.PayCents,
        sinceMs = r.Since.ToUnixTimeMilliseconds(),
    };

    /// <summary>The tasks of a call; <c>turn</c> marks those that turn what stands there rather than put something up.</summary>
    public static IEnumerable<object> TasksJson(HelpRequest r) => r.Tasks.Select(t => new
    {
        kind = t.Kind,
        name = Items.Names.GetValueOrDefault(t.Kind, t.Kind),
        tileX = t.TileX,
        tileY = t.TileY,
        rotation = t.Rotation,
        turn = t.Object is not null,
    });
}

/// <summary>Every bot that asks for help now, for the exclamation marks on the world map.</summary>
public sealed class HelpWantedHandler(BotHelpBoard board) : IMessageHandler
{
    public string Type => "bots.helpWanted";

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        client.RequireAccount();
        return Task.FromResult(new Reply("bots.helpWanted", new { requests = board.Open().Select(BotHelpBoard.Json) }));
    }
}

public sealed record HelpBotRequest(int X, int Y);

/// <summary>
/// A player takes on a bot's call for help: they get leave to come onto its plot and put up or turn what it asks
/// (<c>plot.place</c>, <c>plot.rotate</c>), and nobody else may for a while. The bot pays once everything stands as it
/// asked; the payment rides on the reply to the last place or turn. Money only moves from the bot to the player, so
/// helping makes none.
/// </summary>
public sealed class HelpBotHandler(BotHelpBoard board) : IMessageHandler
{
    public string Type => "bots.help";

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        var request = Payload.Read<HelpBotRequest>(payload);
        var (help, permit) = board.Take(request.X, request.Y, player);
        return Task.FromResult(new Reply("bots.helping", new
        {
            x = help.X,
            y = help.Y,
            bot = help.Character,
            reason = help.Reason,
            tasks = BotHelpBoard.TasksJson(help),
            payCents = help.PayCents,
            untilMs = permit.Until.ToUnixTimeMilliseconds(),
        }));
    }
}
