using System.Security.Cryptography;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// Runs the roster: makes an account and a character for every bot that has none, and on each tick lets
/// every bot in a session take one step. A bot plays in sessions of some minutes with hours of rest between
/// them, like a player who drops in a few times a day. Before a tick the engine looks up the most XP any
/// real player has; a bot plays on regardless, and after each step whatever it has over one XP below that
/// is cut away, so no bot ever shows a higher level than the best player.
/// </summary>
public sealed class BotEngine(
    IServiceScopeFactory scopes,
    IEnumerable<IMessageHandler> handlers,
    IEnumerable<IMessageObserver> observers,
    TimeProvider time,
    OnlinePlayers online,
    ServerLoad load,
    BotsOptions options,
    BotHelpBoard help,
    ILogger<BotEngine> logger)
{
    private readonly IReadOnlyDictionary<string, IMessageHandler> _handlers = handlers.ToDictionary(h => h.Type, StringComparer.Ordinal);
    private readonly IReadOnlyList<IMessageObserver> _observers = observers.ToList();
    private readonly Dictionary<string, Bot> _bots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ticking = new(1, 1);
    private readonly BotCommons _commons = new(help, options.MistakeChance)
    {
        // A player's client finds the country of a plot in the map it ships with; a bot asks the countries the same.
        CountryOf = async (x, y, cancellationToken) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICountries>().CountryOfPlotAsync(x, y, cancellationToken);
        },
    };

    /// <summary>What all the bots share, e.g. the board of their calls for help.</summary>
    public BotCommons Commons => _commons;

    public Random Random { get; init; } = Random.Shared;

    /// <summary>A new bot's first session starts within this time, so the roster shows up soon after the server starts.</summary>
    public static readonly TimeSpan FirstSessionWithin = TimeSpan.FromMinutes(15);

    /// <summary>Every bot the server plays as: the first <see cref="BotsOptions.Count"/> of the roster.</summary>
    public IReadOnlyList<BotProfile> Roster { get; } = BotRoster.First(Math.Max(0, options.Count));

    /// <summary>The bots as they are in memory, by roster name; empty before <see cref="EnsureBotsAsync"/>.</summary>
    public IReadOnlyCollection<Bot> Bots => _bots.Values;

    /// <summary>Gives every bot of the roster an account, a character and a garage, if it has none yet.</summary>
    public async Task EnsureBotsAsync(CancellationToken cancellationToken)
    {
        await _ticking.WaitAsync(cancellationToken);
        try
        {
            var rows = await RowsAsync(cancellationToken);
            foreach (var profile in Roster)
            {
                if (!rows.TryGetValue(profile.Name, out var row))
                {
                    row = await CreateAsync(profile, cancellationToken);
                    if (row is null)
                    {
                        continue;
                    }
                }
                else if (row.SessionUntil is null && row.NextSessionAt > time.GetUtcNow() + FirstSessionWithin && await XpAsync(row.AccountId, cancellationToken) == 0)
                {
                    // A bot that has not played yet (made when first sessions waited hours) comes in soon too.
                    row.NextSessionAt = FirstSession();
                    await SaveScheduleAsync(row, cancellationToken);
                }

                online.AddBotAccount(row.AccountId);
                _commons.AddBot(row.CharacterName);
                if (!_bots.ContainsKey(profile.Name))
                {
                    var account = row.AccountId;
                    _bots[profile.Name] = new Bot(profile, row, new BotClient(account, _handlers, _observers, load, logger), logger, _commons,
                        (cents, ct) => GrantAsync(account, cents, ct));
                }
            }
        }
        finally
        {
            _ticking.Release();
        }
    }

    /// <summary>One step for every bot whose session is on; starts and ends sessions as their time comes.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!await _ticking.WaitAsync(0, cancellationToken))
        {
            return;
        }

        var started = ServerLoad.Start();
        try
        {
            var now = time.GetUtcNow();
            var cap = await XpCapAsync(cancellationToken);
            foreach (var bot in _bots.Values)
            {
                var row = bot.Row;
                if (row.SessionUntil is { } until && until <= now)
                {
                    row.SessionUntil = null;
                    row.NextSessionAt = now + Jitter(TimeSpan.FromHours(bot.Profile.RestHours));
                    bot.EndSession();
                    // Nobody can help a bot that has gone; it asks again next time if it still needs to.
                    help.Withdraw(row.Name);
                    online.SetBot(row.AccountId, false);
                    await SaveScheduleAsync(row, cancellationToken);
                    logger.LogInformation("Bot {Bot} logged out until {Next}", row.CharacterName, row.NextSessionAt);
                    continue;
                }

                if (row.SessionUntil is null)
                {
                    if (row.NextSessionAt > now)
                    {
                        continue;
                    }

                    row.SessionUntil = now + Jitter(TimeSpan.FromMinutes(bot.Profile.SessionMinutes));
                    await SaveScheduleAsync(row, cancellationToken);
                    logger.LogInformation("Bot {Bot} logged in until {Until}", row.CharacterName, row.SessionUntil);
                }

                // In a session the bot shows online like a player (also after a restart in the middle of one).
                online.SetBot(row.AccountId, true);
                await StepAsync(bot, now, cap, cancellationToken);
            }
        }
        finally
        {
            load.Record("bots.tick", started);
            _ticking.Release();
        }
    }

    /// <summary>One step of one bot, whatever its schedule says; for tests and for trying a bot out.</summary>
    public async Task<string?> StepAsync(string name, CancellationToken cancellationToken)
    {
        var bot = _bots[name];
        await _ticking.WaitAsync(cancellationToken);
        try
        {
            return await StepAsync(bot, time.GetUtcNow(), await XpCapAsync(cancellationToken), cancellationToken);
        }
        finally
        {
            _ticking.Release();
        }
    }

    /// <summary>The most XP a bot may have now: one below the best real player, so it never shows a higher level or outranks them.</summary>
    public async Task<long> XpCapAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var xp = scope.ServiceProvider.GetRequiredService<ICharacterXp>();
        var highest = await xp.HighestAsync(_bots.Values.Select(b => b.Row.AccountId).ToList(), cancellationToken);
        return Math.Max(0, highest - 1);
    }

    private async Task<string?> StepAsync(Bot bot, DateTimeOffset now, long cap, CancellationToken cancellationToken)
    {
        string? action = null;
        try
        {
            action = await bot.StepAsync(new BotWorld(now, Random), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bot {Bot} failed a step", bot.Row.CharacterName);
        }

        // Whatever the step gave (a tutorial reward, say), the bot ends it no higher than the best player.
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterXp>().CapAsync(bot.Row.AccountId, cap, cancellationToken);
        if (action is not null)
        {
            logger.LogDebug("Bot {Bot}: {Action}", bot.Row.CharacterName, action);
        }

        return action;
    }

    private async Task GrantAsync(Guid account, long cents, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(account, cents, "bot allowance", cancellationToken);
    }

    private TimeSpan Jitter(TimeSpan span) => span * (0.5 + Random.NextDouble());

    /// <summary>Spread the first sessions out, so the bots do not all show up at once.</summary>
    private DateTimeOffset FirstSession() => time.GetUtcNow() + FirstSessionWithin * Random.NextDouble();

    private async Task<long> XpAsync(Guid account, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterXp>().XpAsync(account, cancellationToken) ?? 0;
    }

    /// <summary>Registers the bot like a new player would: an account with a password nobody keeps, then a character.</summary>
    private async Task<BotRow?> CreateAsync(BotProfile profile, CancellationToken cancellationToken)
    {
        var client = new BotClient(Guid.Empty, _handlers, _observers, load, logger);
        client.Context.AccountId = null;
        var email = $"{BotNames.Plain(profile.Name)}@boty.factoryworld.local";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var registered = await client.SendAsync("auth.register", new { email, password }, cancellationToken);
        // Registering logs the account in on this client like a player's connection; the bot shows online only in its sessions.
        online.Leave(client.Context);
        if (!registered.Is("auth.ok"))
        {
            logger.LogError("Could not register bot {Bot}: {Error}", profile.Name, registered.Error);
            return null;
        }

        var look = profile.Look;
        var appearance = new
        {
            skinTone = look[0], hairStyle = look[1], hairColor = look[2], shirtStyle = look[3],
            shirtColor = look[4], pantsColor = look[5], shoesColor = look[6],
        };
        string? characterName = null;
        foreach (var candidate in new[] { profile.Name }.Concat(Enumerable.Range(2, 8).Select(n => $"{profile.Name}{n}")))
        {
            var created = await client.SendAsync("character.create", new { name = candidate, appearance }, cancellationToken);
            if (created.Is("character"))
            {
                characterName = candidate;
                break;
            }

            if (created.Error != "name_taken")
            {
                logger.LogError("Could not create the character of bot {Bot}: {Error}", profile.Name, created.Error);
                return null;
            }
        }

        if (characterName is null)
        {
            logger.LogError("Every name for bot {Bot} is taken", profile.Name);
            return null;
        }

        // The garage with its starting trash, as the client opens it right after the editor.
        await client.SendAsync("garage.get", null, cancellationToken);

        var now = time.GetUtcNow();
        var row = new BotRow
        {
            Name = profile.Name,
            CharacterName = characterName,
            AccountId = client.Context.AccountId!.Value,
            CreatedAt = now,
            NextSessionAt = FirstSession(),
        };
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var insert = await db.CommandAsync(
            """
            INSERT INTO bots.bots (name, character_name, account_id, created_at, next_session_at, session_until)
            VALUES (@name, @character, @account, @at, @next, NULL)
            """, cancellationToken);
        insert.Parameters.AddWithValue("name", row.Name);
        insert.Parameters.AddWithValue("character", row.CharacterName);
        insert.Parameters.AddWithValue("account", row.AccountId);
        insert.Parameters.AddWithValue("at", row.CreatedAt);
        insert.Parameters.AddWithValue("next", row.NextSessionAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Bot {Bot} joined the game as {Character}", profile.Name, characterName);
        return row;
    }

    private async Task<Dictionary<string, BotRow>> RowsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var command = await db.CommandAsync(
            "SELECT name, character_name, account_id, created_at, next_session_at, session_until FROM bots.bots", cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new Dictionary<string, BotRow>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = new BotRow
            {
                Name = reader.GetString(0),
                CharacterName = reader.GetString(1),
                AccountId = reader.GetGuid(2),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                NextSessionAt = reader.GetFieldValue<DateTimeOffset>(4),
                SessionUntil = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            };
        }

        return rows;
    }

    private async Task SaveScheduleAsync(BotRow row, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var command = await db.CommandAsync(
            "UPDATE bots.bots SET next_session_at = @next, session_until = @until, goals = @goals::jsonb WHERE name = @name", cancellationToken);
        command.Parameters.AddWithValue("name", row.Name);
        command.Parameters.AddWithValue("next", row.NextSessionAt);
        command.Parameters.AddWithValue("until", (object?)row.SessionUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("goals", (object?)row.Goals ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
