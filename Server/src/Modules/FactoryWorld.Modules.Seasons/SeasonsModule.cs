using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Seasons;

/// <summary>How many seasonal treats a player found on one day, against the daily limit.</summary>
public sealed class DropRow
{
    public Guid OwnerId { get; set; }

    public DateOnly Day { get; set; }

    public int Count { get; set; }
}

public sealed class SeasonsDbContext(DbContextOptions<SeasonsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "seasons";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<DropRow>(e =>
        {
            e.ToTable("drops", t => t.HasCheckConstraint("ck_drops_count", "count >= 0"));
            e.HasKey(d => new { d.OwnerId, d.Day });
            e.Property(d => d.OwnerId).HasColumnName("owner_id");
            e.Property(d => d.Day).HasColumnName("day");
            e.Property(d => d.Count).HasColumnName("count");
        });
    }
}

/// <summary>A decoration of a season, bought for its treats.</summary>
public sealed record SeasonOffer(string Item, int Price);

/// <summary>
/// A holiday of the year. <paramref name="From"/> and <paramref name="To"/> are (month, day), the end day left out;
/// a season that ends in the next year (Christmas) has <paramref name="To"/> before <paramref name="From"/>.
/// </summary>
public sealed record Season(string Id, string Name, string Theme, (int Month, int Day) From, (int Month, int Day) To, string Token, IReadOnlyList<SeasonOffer> Shop);

/// <summary>
/// Seasonal events: while one lasts, gathering on plots (cutting, mining, reaping) turns up its treats now and then, and the
/// treats buy its decorations. Nothing of it pays money: the trader neither buys nor sells treats or decorations.
/// The calendar follows from the date alone (UTC), so nothing has to be started or stopped.
/// </summary>
public static class SeasonCalendar
{
    /// <summary>One gathering in this many turns up a treat.</summary>
    public const int DropOneIn = 6;

    /// <summary>The most treats a player finds a day.</summary>
    public const int DailyLimit = 40;

    public static readonly IReadOnlySet<string> Gathering = new HashSet<string> { "plot.harvest", "plot.mine", "plot.reap" };

    public static readonly IReadOnlyList<Season> All =
    [
        new("halloween", "Halloween", "halloween", (10, 24), (11, 8), Items.Candy,
            [new(Items.PumpkinLantern, 30), new(Items.Scarecrow, 50)]),
        new("christmas", "Boże Narodzenie", "christmas", (12, 15), (1, 7), Items.Bauble,
            [new(Items.Snowman, 30), new(Items.ChristmasTree, 50)]),
    ];

    /// <summary>The season on at <paramref name="at"/>, and when it ends; null between seasons.</summary>
    public static (Season Season, DateTimeOffset Ends)? Current(DateTimeOffset at)
    {
        var day = at.UtcDateTime.Date;
        foreach (var season in All)
        {
            foreach (var year in new[] { day.Year - 1, day.Year })
            {
                var start = new DateTime(year, season.From.Month, season.From.Day, 0, 0, 0, DateTimeKind.Utc);
                var end = new DateTime(season.To.Month < season.From.Month ? year + 1 : year, season.To.Month, season.To.Day, 0, 0, 0, DateTimeKind.Utc);
                if (day >= start && day < end)
                {
                    return (season, new DateTimeOffset(end));
                }
            }
        }

        return null;
    }

    /// <summary>The next season to come after <paramref name="at"/> and when it starts.</summary>
    public static (Season Season, DateTimeOffset Starts) Next(DateTimeOffset at)
    {
        var day = at.UtcDateTime.Date;
        return All
            .SelectMany(s => new[] { day.Year, day.Year + 1 }.Select(y => (Season: s, Starts: new DateTimeOffset(new DateTime(y, s.From.Month, s.From.Day, 0, 0, 0, DateTimeKind.Utc)))))
            .Where(s => s.Starts > at)
            .MinBy(s => s.Starts);
    }

    /// <summary>Whether one gathering turns up a treat; <paramref name="random"/> is the server's.</summary>
    public static bool Drops(Random random) => random.Next(DropOneIn) == 0;
}

public sealed class SeasonService(GameDb db, IInventory inventory, IAlbumFinds album, TimeProvider time)
{
    /// <summary>Gives the player a treat of the season, unless they found the day's limit already; true if it did.</summary>
    public Task<bool> GiveTreatAsync(Guid owner, Season season, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(
                """
                INSERT INTO seasons.drops (owner_id, day, count) VALUES (@owner, @day, 1)
                ON CONFLICT (owner_id, day) DO UPDATE SET count = drops.count + 1 WHERE drops.count < @limit
                """, cancellationToken);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("day", DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
            command.Parameters.AddWithValue("limit", SeasonCalendar.DailyLimit);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return false;
            }

            await inventory.AddAsync(owner, season.Token, 1, cancellationToken);
            await album.RecordAsync(owner, [season.Token], cancellationToken);
            return true;
        }, cancellationToken);

    public async Task<int> FoundTodayAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT count FROM seasons.drops WHERE owner_id = @owner AND day = @day", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("day", DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        return await command.ExecuteScalarAsync(cancellationToken) is int count ? count : 0;
    }

    public async Task<object> StateAsync(Guid owner, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var current = SeasonCalendar.Current(now);
        var next = SeasonCalendar.Next(now);
        object? active = null;
        if (current is var (season, ends))
        {
            active = new
            {
                id = season.Id,
                name = season.Name,
                theme = season.Theme,
                ends = ends.ToUnixTimeMilliseconds(),
                token = new { id = season.Token, name = Items.Names[season.Token] },
                tokens = await inventory.CountAsync(owner, season.Token, cancellationToken),
                foundToday = await FoundTodayAsync(owner, cancellationToken),
                dailyLimit = SeasonCalendar.DailyLimit,
                dropOneIn = SeasonCalendar.DropOneIn,
                shop = season.Shop.Select(o => new { id = o.Item, name = Items.Names[o.Item], price = o.Price }),
            };
        }

        return new
        {
            active,
            next = new { id = next.Season.Id, name = next.Season.Name, starts = next.Starts.ToUnixTimeMilliseconds() },
        };
    }

    /// <summary>Buys a decoration of the season on now for its treats. Fails with <c>no_season</c>, <c>unknown_offer</c> or <c>not_enough_items</c>.</summary>
    public Task BuyAsync(Guid owner, string item, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (season, _) = SeasonCalendar.Current(time.GetUtcNow()) ?? throw new GameException("no_season", "No holiday is on now.");
            var offer = season.Shop.FirstOrDefault(o => o.Item == item) ?? throw new GameException("unknown_offer", "This season has no such decoration.");
            await inventory.RemoveAsync(owner, season.Token, offer.Price, cancellationToken);
            await inventory.ReceiveAsync(owner, offer.Item, 1, cancellationToken);
        }, cancellationToken);
}

/// <summary>Gives seasonal treats for gathering on plots while a season is on, and tells the player ("season.drop").</summary>
public sealed class SeasonObserver(IServiceScopeFactory scopes, TimeProvider time) : IMessageObserver
{
    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        if (client.AccountId is not { } owner || reply.Payload is null || reply.Type == "error" || !SeasonCalendar.Gathering.Contains(request.Type)
            || SeasonCalendar.Current(time.GetUtcNow()) is not var (season, _) || !SeasonCalendar.Drops(Random.Shared))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<SeasonService>().GiveTreatAsync(owner, season, cancellationToken) && client.Watching)
        {
            client.Outbox.Enqueue(new Envelope("season.drop", null, JsonSerializer.SerializeToElement(new
            {
                id = season.Token,
                name = Items.Names[season.Token],
                quantity = 1,
            }, Payload.Json)));
        }
    }
}

public sealed class GetSeasonHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "season.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("season", await scope.ServiceProvider.GetRequiredService<SeasonService>().StateAsync(client.RequireAccount(), cancellationToken));
    }
}

public sealed record BuyDecorationRequest(string Item);

public sealed class BuyDecorationHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "season.buy";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var owner = client.RequireAccount();
        var request = Payload.Read<BuyDecorationRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var seasons = scope.ServiceProvider.GetRequiredService<SeasonService>();
        await seasons.BuyAsync(owner, request.Item ?? "", cancellationToken);
        var state = await seasons.StateAsync(owner, cancellationToken);
        return new Reply("season", new
        {
            items = new[] { new { id = request.Item, name = Items.Names[request.Item!], quantity = 1 } },
            season = state,
        });
    }
}

/// <summary>Holidays: seasonal treats and decorations. Owns the <c>seasons</c> schema.</summary>
public sealed class SeasonsModule : IGameModule
{
    public string Name => "Seasons";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SeasonsDbContext>(SeasonsDbContext.Schema);
        services.AddScoped<SeasonService>();
        services.AddSingleton<IMessageObserver, SeasonObserver>();
        services.AddSingleton<IMessageHandler, GetSeasonHandler>();
        services.AddSingleton<IMessageHandler, BuyDecorationHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<SeasonsDbContext>(cancellationToken);
}
