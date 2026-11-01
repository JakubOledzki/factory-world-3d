using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Expeditions;

/// <summary>An expedition a player sent: out until <see cref="ReturnsAt"/>, then waiting until they take what it brought.</summary>
public sealed class TripRow
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public required string Region { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset ReturnsAt { get; set; }

    public DateTimeOffset? ClaimedAt { get; set; }
}

public sealed class ExpeditionsDbContext(DbContextOptions<ExpeditionsDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "expeditions";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<TripRow>(e =>
        {
            e.ToTable("trips");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.OwnerId).HasColumnName("owner_id");
            e.Property(t => t.Region).HasColumnName("region").HasMaxLength(32);
            e.Property(t => t.StartedAt).HasColumnName("started_at");
            e.Property(t => t.ReturnsAt).HasColumnName("returns_at");
            e.Property(t => t.ClaimedAt).HasColumnName("claimed_at");
            e.HasIndex(t => new { t.OwnerId, t.ClaimedAt });
        });
    }
}

/// <summary>Something an expedition may bring: between <paramref name="Min"/> and <paramref name="Max"/> pieces, in <paramref name="Percent"/> of trips.</summary>
public sealed record Find(string Item, int Min, int Max, int Percent = 100);

/// <summary>A part of the world an expedition goes to. Ids are stored, so never rename one.</summary>
public sealed record Region(string Id, string Name, string Description, int Level, TimeSpan Duration, IReadOnlyList<(string Item, int Quantity)> Supplies,
    long Xp, IReadOnlyList<Find> Finds, string Souvenir);

/// <summary>
/// Expeditions: a player packs provisions (and, by sea, diesel) and sends a party to a far region; hours later it comes back
/// with goods of that land, now and then a fossil or amber, and sometimes a souvenir for the album. What comes back is drawn
/// by the server when it is taken. At the trader's prices a trip brings back about as much as its supplies are worth, a little
/// less on average, so expeditions are a gamble and a hunt for souvenirs, not a source of money.
/// </summary>
public static class ExpeditionRules
{
    /// <summary>Expeditions out (or back and not yet emptied) at once; more from <see cref="SecondPartyLevel"/>.</summary>
    public const int Parties = 1;

    public const int SecondPartyLevel = 25;

    /// <summary>Percent of trips that bring the region's souvenir; the golden compass turns up anywhere, rarely.</summary>
    public const int SouvenirPercent = 15;

    public const int CompassPercent = 2;

    public static readonly IReadOnlyList<Region> Regions =
    [
        new("forest", "Puszcza", "Dzikie lasy daleko od dróg: drewno, dęby, grzyby i miód.", 1, TimeSpan.FromHours(2),
            [(Items.Provisions, 3)], 40,
            [new(Items.Wood, 15, 30), new(Items.OakWood, 1, 5), new(Items.Mushrooms, 5, 15), new(Items.Honey, 1, 3, 50),
             new(Items.SaplingOak, 1, 2, 25), new(Items.SaplingLinden, 1, 2, 25), new(Items.SaplingBirch, 1, 2, 25)],
            Items.Totem),
        new("desert", "Pustynia", "Karawana przez wydmy: piasek, glina i daktyle, a czasem skamielina.", 10, TimeSpan.FromHours(4),
            [(Items.Provisions, 6)], 100,
            [new(Items.Sand, 40, 80), new(Items.Dates, 10, 30), new(Items.Clay, 20, 40), new(Items.Ammonite, 1, 1, 2), new(Items.AncientCoin, 1, 1, 1)],
            Items.DesertRose),
        new("mountains", "Góry", "Wspinaczka do starych sztolni: kamień, rudy i minerały.", 10, TimeSpan.FromHours(4),
            [(Items.Provisions, 6)], 100,
            [new(Items.Stone, 20, 40), new(Items.IronOre, 5, 12), new(Items.CopperOre, 2, 5), new(Items.Quartz, 1, 1, 50), new(Items.Marble, 1, 2, 30),
             new(Items.SilverOre, 1, 3, 10), new(Items.GoldOre, 1, 1, 2)],
            Items.Edelweiss),
        new("sea", "Morze", "Rejs kutrem na dalekie łowiska: ryby, małże, algi i bursztyn.", 15, TimeSpan.FromHours(6),
            [(Items.Provisions, 8), (Items.Diesel, 10)], 150,
            [new(Items.Herring, 20, 40), new(Items.Cod, 10, 20), new(Items.Salmon, 3, 8), new(Items.Mussels, 5, 15), new(Items.Seaweed, 20, 40),
             new(Items.Amber, 1, 1, 15)],
            Items.Nautilus),
        new("tundra", "Tundra i lodowiec", "Wyprawa za koło polarne: lód, rdzenie lodowe, węgiel i meteoryty.", 20, TimeSpan.FromHours(8),
            [(Items.Provisions, 10)], 200,
            [new(Items.Ice, 100, 200), new(Items.IceCore, 2, 6), new(Items.Coal, 10, 20), new(Items.MeteoricIron, 1, 2, 50)],
            Items.MammothTusk),
    ];

    public static Region? Find(string id) => Regions.FirstOrDefault(r => r.Id == id);

    public static int PartiesAt(int level) => level >= SecondPartyLevel ? Parties + 1 : Parties;

    /// <summary>What one trip to <paramref name="region"/> brings back; <paramref name="random"/> is the server's.</summary>
    public static IReadOnlyList<(string Item, long Quantity)> Roll(Region region, Random random)
    {
        var loot = new List<(string, long)>();
        foreach (var find in region.Finds)
        {
            if (random.Next(100) < find.Percent)
            {
                loot.Add((find.Item, random.Next(find.Min, find.Max + 1)));
            }
        }

        if (random.Next(100) < SouvenirPercent)
        {
            loot.Add((region.Souvenir, 1));
        }

        if (random.Next(100) < CompassPercent)
        {
            loot.Add((Items.GoldenCompass, 1));
        }

        return loot;
    }
}

public sealed record TripState(long Id, string Region, DateTimeOffset StartedAt, DateTimeOffset ReturnsAt);

public sealed class ExpeditionService(GameDb db, IInventory inventory, ICharacterXp levels, ICharacterProgress progress, TimeProvider time)
{
    public async Task<IReadOnlyList<TripState>> TripsAsync(Guid owner, CancellationToken cancellationToken)
    {
        var trips = new List<TripState>();
        await using var command = await db.CommandAsync(
            "SELECT id, region, started_at, returns_at FROM expeditions.trips WHERE owner_id = @owner AND claimed_at IS NULL ORDER BY id", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            trips.Add(new TripState(reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return trips;
    }

    public async Task<object> StateAsync(Guid owner, CancellationToken cancellationToken)
    {
        var level = await levels.LevelAsync(owner, cancellationToken);
        var trips = await TripsAsync(owner, cancellationToken);
        var now = time.GetUtcNow();
        return new
        {
            now = now.ToUnixTimeMilliseconds(),
            level,
            parties = ExpeditionRules.PartiesAt(level),
            secondPartyLevel = ExpeditionRules.SecondPartyLevel,
            souvenirPercent = ExpeditionRules.SouvenirPercent,
            regions = ExpeditionRules.Regions.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                description = r.Description,
                level = r.Level,
                minutes = (int)r.Duration.TotalMinutes,
                xp = r.Xp,
                supplies = r.Supplies.Select(s => new { id = s.Item, name = Items.Names[s.Item], quantity = s.Quantity }),
                finds = r.Finds.Select(f => new { id = f.Item, name = Items.Names[f.Item], min = f.Min, max = f.Max, percent = f.Percent }),
                souvenir = new { id = r.Souvenir, name = Items.Names[r.Souvenir] },
            }),
            trips = trips.Select(t => new
            {
                id = t.Id,
                region = t.Region,
                name = ExpeditionRules.Find(t.Region)?.Name ?? t.Region,
                startedAt = t.StartedAt.ToUnixTimeMilliseconds(),
                returnsAt = t.ReturnsAt.ToUnixTimeMilliseconds(),
                back = t.ReturnsAt <= now,
            }),
        };
    }

    /// <summary>Packs the supplies and sends a party. Fails with <c>unknown_region</c>, <c>expedition_level</c>,
    /// <c>parties_out</c> (all parties are out) or <c>not_enough_items</c>.</summary>
    public Task SendAsync(Guid owner, string regionId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var region = ExpeditionRules.Find(regionId) ?? throw new GameException("unknown_region", "There is no such region.");
            var level = await levels.LevelAsync(owner, cancellationToken);
            if (level < region.Level)
            {
                throw new GameException("expedition_level", $"Expeditions there need level {region.Level}.");
            }

            // One send at a time per player, so two at once cannot both slip under the limit of parties.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()) ^ 0x65787065_64000000);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            if ((await TripsAsync(owner, cancellationToken)).Count >= ExpeditionRules.PartiesAt(level))
            {
                throw new GameException("parties_out", "All your parties are out; take what one brought first.");
            }

            foreach (var (item, quantity) in region.Supplies)
            {
                await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            }

            var now = time.GetUtcNow();
            await using var insert = await db.CommandAsync(
                "INSERT INTO expeditions.trips (owner_id, region, started_at, returns_at) VALUES (@owner, @region, @now, @back)", cancellationToken);
            insert.Parameters.AddWithValue("owner", owner);
            insert.Parameters.AddWithValue("region", region.Id);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("back", now + region.Duration);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Takes what a party that came back brought, once. Fails with <c>unknown_trip</c> or <c>not_back</c>.</summary>
    public Task<(Region Region, IReadOnlyList<(string Item, long Quantity)> Loot, XpGain Xp)> ClaimAsync(Guid owner, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var now = time.GetUtcNow();
            string? regionId = null;
            DateTimeOffset returnsAt = default;
            await using (var find = await db.CommandAsync(
                "SELECT region, returns_at FROM expeditions.trips WHERE id = @id AND owner_id = @owner AND claimed_at IS NULL FOR UPDATE", cancellationToken))
            {
                find.Parameters.AddWithValue("id", id);
                find.Parameters.AddWithValue("owner", owner);
                await using var reader = await find.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    regionId = reader.GetString(0);
                    returnsAt = reader.GetFieldValue<DateTimeOffset>(1);
                }
            }

            if (regionId is null || ExpeditionRules.Find(regionId) is not { } region)
            {
                throw new GameException("unknown_trip", "There is no such expedition of yours waiting.");
            }

            if (returnsAt > now)
            {
                throw new GameException("not_back", "The expedition is not back yet.");
            }

            await using (var claim = await db.CommandAsync(
                "UPDATE expeditions.trips SET claimed_at = @now WHERE id = @id AND claimed_at IS NULL", cancellationToken))
            {
                claim.Parameters.AddWithValue("id", id);
                claim.Parameters.AddWithValue("now", now);
                if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("unknown_trip", "There is no such expedition of yours waiting.");
                }
            }

            var loot = ExpeditionRules.Roll(region, Random.Shared);
            foreach (var (item, quantity) in loot)
            {
                await inventory.AddAsync(owner, item, quantity, cancellationToken);
            }

            var xp = await progress.AddXpAsync(owner, region.Xp, cancellationToken);
            return (region, loot, xp);
        }, cancellationToken);
}

public sealed class GetExpeditionsHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "expedition.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("expeditions", await scope.ServiceProvider.GetRequiredService<ExpeditionService>().StateAsync(client.RequireAccount(), cancellationToken));
    }
}

public sealed record SendExpeditionRequest(string Region);

public sealed class SendExpeditionHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "expedition.send";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var owner = client.RequireAccount();
        var request = Payload.Read<SendExpeditionRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var expeditions = scope.ServiceProvider.GetRequiredService<ExpeditionService>();
        await expeditions.SendAsync(owner, request.Region ?? "", cancellationToken);
        return new Reply("expeditions", await expeditions.StateAsync(owner, cancellationToken));
    }
}

public sealed record ClaimExpeditionRequest(long Id);

public sealed class ClaimExpeditionHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "expedition.claim";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var owner = client.RequireAccount();
        var request = Payload.Read<ClaimExpeditionRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var expeditions = scope.ServiceProvider.GetRequiredService<ExpeditionService>();
        var (region, loot, xp) = await expeditions.ClaimAsync(owner, request.Id, cancellationToken);
        return new Reply("expedition.claimed", new
        {
            region = region.Name,
            items = loot.Select(l => new { id = l.Item, name = Items.Names[l.Item], quantity = l.Quantity }),
            xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp },
            expeditions = await expeditions.StateAsync(owner, cancellationToken),
        });
    }
}

/// <summary>Expeditions to far regions. Owns the <c>expeditions</c> schema.</summary>
public sealed class ExpeditionsModule : IGameModule
{
    public string Name => "Expeditions";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ExpeditionsDbContext>(ExpeditionsDbContext.Schema);
        services.AddScoped<ExpeditionService>();
        services.AddSingleton<IMessageHandler, GetExpeditionsHandler>();
        services.AddSingleton<IMessageHandler, SendExpeditionHandler>();
        services.AddSingleton<IMessageHandler, ClaimExpeditionHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<ExpeditionsDbContext>(cancellationToken);
}
