using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Characters;
using FactoryWorld.Modules.Companies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace FactoryWorld.Server.Tests;

/// <summary>
/// The real server on a fresh PostgreSQL database that is dropped afterwards.
/// Point FW_TEST_DB at a server where the user may create databases (default: local docker-compose db).
/// </summary>
public class ServerFactory : WebApplicationFactory<Program>
{
    private readonly string _admin =
        Environment.GetEnvironmentVariable("FW_TEST_DB")
        ?? "Host=localhost;Port=5433;Database=postgres;Username=factory;Password=factory";

    private readonly string _database = $"fw_test_{Guid.NewGuid():N}";

    /// <summary>The server's clock; tests move it forward instead of waiting. It starts at <see cref="Start"/>, not at the real time,
    /// so the weather over the fields and the holidays are the same on every run.</summary>
    public FakeTimeProvider Time { get; } = new(Start);

    /// <summary>A summer noon (no holiday) with at least <see cref="ClearFields"/> of clear weather over the fields ahead, so
    /// crops grow for their own time unless a test moves the clock into the rain or a drought.</summary>
    public static readonly DateTimeOffset Start = ClearFrom(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public static readonly TimeSpan ClearFields = TimeSpan.FromHours(6);

    private static DateTimeOffset ClearFrom(DateTimeOffset at)
    {
        while (FieldWeather.Next(at) is var (start, end, _) && start - at < ClearFields)
        {
            at = end;
        }

        return at;
    }

    /// <summary>Whether the trader's prices drift and its events run; off, tests can count on the usual prices.</summary>
    protected virtual bool PriceChanges => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Market:PriceChanges", PriceChanges.ToString());
        // Bots would play next to the test players; their own tests drive them by hand.
        builder.UseSetting("Bots:Enabled", "false");
        builder.UseSetting("Bots:Count", BotTests.TestBots.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A bot's line goes up right; the tests that need it put up wrong set the chance themselves.
        builder.UseSetting("Bots:MistakeChance", "0");
        // Plots stand still while their players are away; the tests of that check who is in the game themselves, so the
        // others' plots run as the clock moves however long a test takes.
        builder.UseSetting("PlotClock:Auto", "false");
        builder.UseSetting("ConnectionStrings:Game",
            new NpgsqlConnectionStringBuilder(_admin) { Database = _database }.ConnectionString);
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
        // Every test starts on an empty database, where EF Core logs a failed query for each module's missing migrations
        // table; those lines buried the names of failing tests in the CI log.
        builder.ConfigureLogging(logging => logging
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None)
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Connection", LogLevel.None)
            .AddFilter("FactoryWorld.Server.MessageDispatcher", LogLevel.Warning));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    public async Task<GameClient> ConnectAsync()
    {
        var socket = await Server.CreateWebSocketClient().ConnectAsync(new Uri(Server.BaseAddress, "ws"), CancellationToken.None);
        return new GameClient(socket);
    }

    /// <summary>Gives the account's character enough XP to bring plots into a company.</summary>
    public async Task CompanyLevelAsync(Guid account)
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterProgress>()
            .AddXpAsync(account, Leveling.TotalXpForLevel(CompanyRules.PlotMinLevel), CancellationToken.None);
    }
}

/// <summary>A test player: sends one request and reads its reply.</summary>
public sealed class GameClient(WebSocket socket) : IDisposable
{
    private int _nextId;

    /// <summary>The account made by <see cref="RegisterAsync"/>.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Messages the server sent without being asked (no id), e.g. "achievement.unlocked", oldest first.</summary>
    public List<JsonElement> Notices { get; } = [];

    public async Task<JsonElement> SendRawAsync(string frame)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);
        return await ReceiveAsync();
    }

    public async Task<JsonElement> SendAsync(string type, object? payload = null)
    {
        var id = (++_nextId).ToString();
        var reply = await SendRawAsync(JsonSerializer.Serialize(new { type, id, payload }));
        // Notices from the previous request come before this reply.
        while (reply.TryGetProperty("id", out var replyId) && replyId.ValueKind == JsonValueKind.Null)
        {
            Notices.Add(reply);
            reply = await ReceiveAsync();
        }

        return reply;
    }

    /// <summary>The next message the server sends, e.g. a notice that answers no request.</summary>
    public Task<JsonElement> NextAsync() => ReceiveAsync();

    /// <summary>Waits for the server to close the connection; returns how it said it closed.</summary>
    public async Task<string?> ClosedAsync()
    {
        var buffer = new byte[64 * 1024];
        while ((await socket.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close)
        {
        }

        return socket.CloseStatusDescription;
    }

    private async Task<JsonElement> ReceiveAsync()
    {
        // Large replies (a plot's tiles) can come in several frames.
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(message.ToArray()).RootElement.Clone();
    }

    /// <summary>Registers a new account with a unique e-mail and returns the session token.</summary>
    public async Task<string> RegisterAsync(string? email = null)
    {
        var reply = await SendAsync("auth.register", new { email = email ?? $"{Guid.NewGuid():N}@example.com", password = "secret-password" });
        Assert.Equal("auth.ok", reply.Type());
        AccountId = reply.Payload().GetProperty("accountId").GetGuid();
        return reply.Payload().GetProperty("token").GetString()!;
    }

    /// <summary>Registers and creates a character, so the player can earn XP.</summary>
    public async Task RegisterWithCharacterAsync()
    {
        await RegisterAsync();
        var look = new { skinTone = 0, hairStyle = 0, hairColor = 0, shirtStyle = 0, shirtColor = 0, pantsColor = 0, shoesColor = 0 };
        var reply = await SendAsync("character.create", new { name = "P" + Guid.NewGuid().ToString("N")[..10], appearance = look });
        Assert.Equal("character", reply.Type());
    }

    /// <summary>Item id → quantity from "inventory.get".</summary>
    public async Task<Dictionary<string, long>> InventoryAsync()
    {
        var reply = await SendAsync("inventory.get");
        return reply.Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());
    }

    public async Task<long> BalanceAsync() =>
        (await SendAsync("wallet.get")).Payload().GetProperty("balanceCents").GetInt64();

    public void Dispose() => socket.Dispose();
}

public static class ReplyExtensions
{
    public static string? Type(this JsonElement reply) => reply.GetProperty("type").GetString();

    public static JsonElement Payload(this JsonElement reply) => reply.GetProperty("payload");

    public static string? ErrorCode(this JsonElement reply) =>
        reply.Type() == "error" ? reply.Payload().GetProperty("code").GetString() : null;
}

/// <summary>Free ground in a plot's tiles, for tests that put machines down: machines cover up to 4 × 4 tiles.</summary>
public static class PlotTiles
{
    /// <summary>Whether the square of tiles <paramref name="reach"/> each way around (x, y) is open ground, off the spawn:
    /// with the default reach anything up to 5 × 5 fits on (x, y).</summary>
    public static bool Clear(byte[] tiles, int x, int y, int reach = 2)
    {
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                var (tx, ty) = (x + dx, y + dy);
                if (!FactoryWorld.Modules.World.PlotLayout.Inside(tx, ty) || (tx, ty) == FactoryWorld.Modules.World.PlotLayout.Spawn
                    || tiles[ty * FactoryWorld.Modules.World.PlotLayout.Size + tx] != (byte)FactoryWorld.Modules.World.TileObject.None)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Of <paramref name="tiles"/> in their order, those at least <paramref name="apart"/> tiles from every one taken before.</summary>
    public static IEnumerable<(int X, int Y)> Apart(IEnumerable<(int X, int Y)> tiles, int apart = 5)
    {
        var taken = new List<(int X, int Y)>();
        foreach (var t in tiles)
        {
            if (taken.All(o => Math.Max(Math.Abs(o.X - t.X), Math.Abs(o.Y - t.Y)) >= apart))
            {
                taken.Add(t);
                yield return t;
            }
        }
    }
}
