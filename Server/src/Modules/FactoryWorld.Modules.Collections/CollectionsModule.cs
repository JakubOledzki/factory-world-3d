using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Collections;

/// <summary>An item of the album a player has found, and when they first did.</summary>
public sealed class FindRow
{
    public Guid OwnerId { get; set; }

    public required string Item { get; set; }

    public DateTimeOffset FoundAt { get; set; }
}

/// <summary>A full page of the album whose reward the player took.</summary>
public sealed class ClaimedPageRow
{
    public Guid OwnerId { get; set; }

    public required string Page { get; set; }

    public DateTimeOffset ClaimedAt { get; set; }
}

public sealed class CollectionsDbContext(DbContextOptions<CollectionsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "collections";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<FindRow>(e =>
        {
            e.ToTable("finds");
            e.HasKey(f => new { f.OwnerId, f.Item });
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(f => f.FoundAt).HasColumnName("found_at");
        });
        model.Entity<ClaimedPageRow>(e =>
        {
            e.ToTable("claimed_pages");
            e.HasKey(p => new { p.OwnerId, p.Page });
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Page).HasColumnName("page").HasMaxLength(32);
            e.Property(p => p.ClaimedAt).HasColumnName("claimed_at");
        });
    }
}

public sealed record AlbumPage(string Id, string Name, string Hint, IReadOnlyList<string> Items);

/// <summary>
/// The album of finds: rare things a player gathers, digs up or brings back, page by page. A page fills by itself as the
/// player finds its items (buying them does not count) and its reward, experience, is taken once. Page ids are stored.
/// </summary>
public static class Album
{
    /// <summary>Experience for filling a page.</summary>
    public const long PageXp = 1_000;

    public static readonly IReadOnlyList<AlbumPage> Pages =
    [
        new("fossils", "Skamieliny", "Wykopuje się je z wydm na pustyni.", [Items.Ammonite, Items.AncientCoin, Items.DinosaurBone]),
        new("sky_and_ice", "Z nieba i spod lodu", "Lodowiec: lód, meteoryty i rdzenie z wiertni.", [Items.Ice, Items.MeteoricIron, Items.IceCore]),
        new("treasures", "Skarby ziemi", "Kopalnie w górach i minerał działki.",
            [Items.SilverOre, Items.GoldOre, Items.UraniumOre, Items.Quartz, Items.Marble, Items.Sulfur, Items.Salt]),
        new("sea", "Morze", "Wędka, łodzie i plaża na wybrzeżu.", [Items.Herring, Items.Cod, Items.Salmon, Items.Mussels, Items.Seaweed, Items.Amber]),
        new("forest", "Las", "Ścinanie drzew, grzybobranie i ule.",
            [Items.OakWood, Items.Mushrooms, Items.Honey, Items.SaplingSpruce, Items.SaplingBirch, Items.SaplingOak, Items.SaplingLinden]),
        new("souvenirs", "Pamiątki z wypraw", "Przywożą je wyprawy, czasem.",
            [Items.Totem, Items.DesertRose, Items.Edelweiss, Items.MammothTusk, Items.Nautilus, Items.GoldenCompass]),
        new("holidays", "Święta", "Cukierki i ozdoby z Halloween i Bożego Narodzenia.",
            [Items.Candy, Items.PumpkinLantern, Items.Scarecrow, Items.Bauble, Items.ChristmasTree, Items.Snowman]),
    ];

    public static readonly IReadOnlySet<string> AllItems = Pages.SelectMany(p => p.Items).ToHashSet();

    /// <summary>Requests whose replies hand out what a player found ("items" or "taken"); buying and trading are not finds.</summary>
    public static readonly IReadOnlySet<string> Finding = new HashSet<string>
    {
        "plot.harvest", "plot.mine", "plot.reap", "plot.blast", "plot.collect", "meteor.dig", "hive.take", "drill.take",
        "forester.take", "farmstead.take", "machine.take", "furnace.take", "well.take", "expedition.claim", "season.buy",
    };

    /// <summary>Album items listed as [{ id, quantity }] under "items" or "taken" in a reply.</summary>
    public static IEnumerable<string> Found(JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in new[] { "items", "taken" })
        {
            if (!reply.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { } item && AllItems.Contains(item))
                {
                    yield return item;
                }
            }
        }
    }
}

public sealed class AlbumService(GameDb db, ICharacterProgress progress, TimeProvider time) : IAlbumFinds
{
    async Task IAlbumFinds.RecordAsync(Guid owner, IReadOnlyCollection<string> items, CancellationToken cancellationToken)
    {
        var album = items.Where(Album.AllItems.Contains).Distinct().ToList();
        if (album.Count > 0)
        {
            await RecordAsync(owner, album, cancellationToken);
        }
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> FindsAsync(Guid owner, CancellationToken cancellationToken)
    {
        var finds = new Dictionary<string, DateTimeOffset>();
        await using var command = await db.CommandAsync("SELECT item, found_at FROM collections.finds WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            finds[reader.GetString(0)] = reader.GetFieldValue<DateTimeOffset>(1);
        }

        return finds;
    }

    /// <summary>Writes down the items found; returns those found for the first time.</summary>
    public async Task<IReadOnlyList<string>> RecordAsync(Guid owner, IReadOnlyCollection<string> items, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO collections.finds (owner_id, item, found_at)
            SELECT @owner, item, @now FROM unnest(@items) AS item
            ON CONFLICT DO NOTHING
            RETURNING item
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        command.Parameters.AddWithValue("items", items.ToArray());
        var fresh = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            fresh.Add(reader.GetString(0));
        }

        return fresh;
    }

    public async Task<object> StateAsync(Guid owner, CancellationToken cancellationToken)
    {
        var finds = await FindsAsync(owner, cancellationToken);
        var claimed = new HashSet<string>();
        await using (var command = await db.CommandAsync("SELECT page FROM collections.claimed_pages WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(reader.GetString(0));
            }
        }

        return new
        {
            found = finds.Count,
            total = Album.AllItems.Count,
            pageXp = Album.PageXp,
            pages = Album.Pages.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                hint = p.Hint,
                complete = p.Items.All(finds.ContainsKey),
                claimed = claimed.Contains(p.Id),
                items = p.Items.Select(i => new
                {
                    id = i,
                    name = Items.Names.GetValueOrDefault(i, i),
                    found = finds.ContainsKey(i),
                    foundAt = finds.TryGetValue(i, out var at) ? at.ToUnixTimeMilliseconds() : (long?)null,
                }),
            }),
        };
    }

    /// <summary>Takes the reward of a full page once. Fails with <c>unknown_page</c>, <c>page_incomplete</c> or <c>already_claimed</c>.</summary>
    public Task<XpGain> ClaimAsync(Guid owner, string pageId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var page = Album.Pages.FirstOrDefault(p => p.Id == pageId) ?? throw new GameException("unknown_page", "There is no such page.");
            var finds = await FindsAsync(owner, cancellationToken);
            if (!page.Items.All(finds.ContainsKey))
            {
                throw new GameException("page_incomplete", "Find everything on the page first.");
            }

            await using var command = await db.CommandAsync(
                """
                INSERT INTO collections.claimed_pages (owner_id, page, claimed_at) VALUES (@owner, @page, @now)
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("page", page.Id);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("already_claimed", "The page's reward was taken already.");
            }

            return await progress.AddXpAsync(owner, Album.PageXp, cancellationToken);
        }, cancellationToken);
}

/// <summary>Watches what players gather and writes down the album items they find for the first time, telling them ("album.found").</summary>
public sealed class AlbumObserver(IServiceScopeFactory scopes) : IMessageObserver
{
    /// <summary>The album items the player on the connection has found, so finding them again costs nothing.</summary>
    private const string Known = "collections.found";

    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        if (client.AccountId is not { } owner || reply.Payload is not { } payload || !Album.Finding.Contains(request.Type))
        {
            return;
        }

        var found = Album.Found(payload).Distinct().ToList();
        if (found.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var album = scope.ServiceProvider.GetRequiredService<AlbumService>();
        if (client.Items.GetValueOrDefault(Known) is not (Guid knownOwner, HashSet<string> known) || knownOwner != owner)
        {
            known = (await album.FindsAsync(owner, cancellationToken)).Keys.ToHashSet();
            client.Items[Known] = (owner, known);
        }

        var unknown = found.Where(i => !known.Contains(i)).ToList();
        if (unknown.Count == 0)
        {
            return;
        }

        foreach (var item in await album.RecordAsync(owner, unknown, cancellationToken))
        {
            if (!client.Watching)
            {
                continue;
            }

            var page = Album.Pages.First(p => p.Items.Contains(item));
            client.Outbox.Enqueue(new Envelope("album.found", null, JsonSerializer.SerializeToElement(new
            {
                id = item,
                name = Items.Names.GetValueOrDefault(item, item),
                page = page.Name,
            }, Payload.Json)));
        }

        known.UnionWith(unknown);
    }
}

public sealed class GetAlbumHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "album.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("album", await scope.ServiceProvider.GetRequiredService<AlbumService>().StateAsync(client.RequireAccount(), cancellationToken));
    }
}

public sealed record ClaimPageRequest(string Page);

public sealed class ClaimPageHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "album.claim";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var owner = client.RequireAccount();
        var request = Payload.Read<ClaimPageRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var album = scope.ServiceProvider.GetRequiredService<AlbumService>();
        var xp = await album.ClaimAsync(owner, request.Page ?? "", cancellationToken);
        return new Reply("album", new
        {
            claimed = true,
            xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp },
            album = await album.StateAsync(owner, cancellationToken),
        });
    }
}

/// <summary>The album of finds. Owns the <c>collections</c> schema.</summary>
public sealed class CollectionsModule : IGameModule
{
    public string Name => "Collections";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CollectionsDbContext>(CollectionsDbContext.Schema);
        services.AddScoped<AlbumService>();
        services.AddScoped<IAlbumFinds>(p => p.GetRequiredService<AlbumService>());
        services.AddSingleton<IMessageObserver, AlbumObserver>();
        services.AddSingleton<IMessageHandler, GetAlbumHandler>();
        services.AddSingleton<IMessageHandler, ClaimPageHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<CollectionsDbContext>(cancellationToken);
}
