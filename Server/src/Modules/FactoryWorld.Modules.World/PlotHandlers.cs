using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

public sealed record EnterRequest(int X, int Y);

public sealed record HarvestRequest(int X, int Y, int TileX, int TileY);

/// <summary>Enters a plot the player may visit: its tiles (gzip, base64) and where things stand. A visitor of a public plot
/// gets its rights without <c>work</c>, so the client lets them only walk about.</summary>
public sealed class EnterPlotHandler(IServiceScopeFactory scopes, WorldMap map, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.enter";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<EnterRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var rights = await scope.ServiceProvider.GetRequiredService<PlotService>()
            .RightsAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        // A fire since the last visit burns its trees before anyone sees them; only those who may enter start the watch.
        var fire = rights.Contains(PlotRight.Work)
            ? await scope.ServiceProvider.GetRequiredService<FireService>().CatchUpAsync(request.X, request.Y, cancellationToken)
            : null;
        var tiles = await scope.ServiceProvider.GetRequiredService<PlotService>()
            .TilesAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        var tools = await scope.ServiceProvider.GetRequiredService<IEquipment>().ToolsAsync(client.RequireAccount(), cancellationToken);
        var fields = await scope.ServiceProvider.GetRequiredService<FarmService>().FieldsAsync(request.X, request.Y, cancellationToken);
        var trees = await scope.ServiceProvider.GetRequiredService<ForestService>().TreesAsync(request.X, request.Y, cancellationToken);
        var animals = await scope.ServiceProvider.GetRequiredService<HusbandryService>().AnimalsAsync(request.X, request.Y, cancellationToken);
        var biome = map.BiomeAt(request.X, request.Y);
        var mines = await scope.ServiceProvider.GetRequiredService<MineService>().MinesAsync(request.X, request.Y, biome, cancellationToken);
        var objects = await scope.ServiceProvider.GetRequiredService<PlotObjectService>().ObjectsAsync(request.X, request.Y, cancellationToken);
        var permit = rights.Contains(PlotRight.Build)
            ? null
            : scope.ServiceProvider.GetRequiredService<PlotPermits>().For(client.RequireAccount(), request.X, request.Y);
        var pests = await scope.ServiceProvider.GetRequiredService<PestService>().StateAsync(request.X, request.Y, cancellationToken);
        var smog = await scope.ServiceProvider.GetRequiredService<SmogService>().LevelAsync(request.X, request.Y, cancellationToken);
        var certificate = await scope.ServiceProvider.GetRequiredService<ForestLedger>().JsonAsync(client.RequireAccount(), cancellationToken);
        var (likes, liked) = await scope.ServiceProvider.GetRequiredService<LikeService>()
            .LikesAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        var cities = scope.ServiceProvider.GetRequiredService<CityService>();
        object? city = null;
        if (cities.TownOf(request.X, request.Y) is { } town)
        {
            var level = CityRules.Level(await cities.DeliveredAsync(town, cancellationToken));
            city = new { name = town.Name, population = town.Population, level, levelName = CityRules.LevelNames[level] };
        }

        object? county = cities.CountyOf(request.X, request.Y) is { } seat ? new { name = seat.Name } : null;

        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal))
        {
            gzip.Write(tiles);
        }

        var (sx, sy, sw, sh) = PlotLayout.Station;
        return new Reply("plot", new
        {
            x = request.X,
            y = request.Y,
            biome = (int)biome,
            rights = rights.Order().Select(r => r.ToString().ToLowerInvariant()),
            // What the player came to set right on someone else's plot, with leave to (IPlotPermits); null otherwise.
            help = permit is null ? null : PlotPermits.Json(permit, objects),
            // How many players like the plot, and whether this one does.
            likes,
            liked,
            size = PlotLayout.Size,
            tileMeters = PlotLayout.TileMeters,
            tiles = Convert.ToBase64String(packed.ToArray()),
            railRow = PlotLayout.RailRow,
            station = new { x = sx, y = sy, width = sw, height = sh },
            spawn = new { x = PlotLayout.Spawn.X, y = PlotLayout.Spawn.Y },
            // The town a city plot belongs to, with its level; null elsewhere.
            city,
            // The town whose county the plot lies in (its mayor's laws hold here); null outside any.
            county,
            tools = tools.Select(t => new { id = t, name = Items.Names[t] }),
            harvestSeconds = HarvestRules.Time.TotalSeconds,
            // Rocks break and mines are worked this many percent faster here; better pickaxes are faster still.
            miningBonus = Mining.BonusPercent(biome),
            rockSeconds = HarvestRules.TimeFor(TileObject.Rock, Items.Pickaxe, biome).TotalSeconds,
            pickaxes = Items.Pickaxes,
            mineSeconds = (Mining.Time / Mining.Speed(biome)).TotalSeconds,
            mineUnits = Mining.Units,
            mines = mines.Select(MineHandler.MineJson),
            chopSeconds = HarvestRules.ChopTime.TotalSeconds,
            sawSeconds = HarvestRules.SawTime.TotalSeconds,
            // How long each saw takes over a tree, and how many trees next to it it fells at the same cut.
            saws = Items.Saws.ToDictionary(s => s.Key, s => new { seconds = (HarvestRules.ChopTime * s.Value.Time).TotalSeconds, fells = s.Value.Fells }),
            treeGrowSeconds = Forestry.GrowTime(biome)?.TotalSeconds,
            trees = trees.Select(PlantTreeHandler.TreeJson),
            // The kinds of tree with their saplings and growing times here, the bark beetle in a monoculture planted by hand,
            // the last forest fire (if recent) with what keeps fires small, and the player's forest certificate.
            treeKinds = PlantTreeHandler.KindsJson(biome),
            pests = PestService.Json(pests),
            fire = fire is null ? null : FireService.Json(fire),
            fires = FireService.RulesJson(biome),
            certificate,
            mushroomRegrowSeconds = Forestry.MushroomRegrow.TotalSeconds,
            objects = objects.Select(PlaceOnPlotHandler.ObjectJson),
            placeable = PlotObjects.Placeable,
            fertile = Farming.Fertile(biome),
            // The steppe and the desert can be tilled where a sprinkler waters, sprinklerReach tiles from it each way.
            irrigable = Farming.Irrigable(biome),
            sprinklerReach = Farming.SprinklerReach,
            sprinklerSpeed = Farming.SprinklerSpeed,
            // The smog over the plot: its own smoke, what it breathes from the plots around, and how fast crops grow in it.
            smog = SmogService.Json(smog),
            fields = fields.Select(FieldHandler.FieldJson),
            pasture = Husbandry.Pasture(biome),
            husbandryBonus = Husbandry.BonusPercent(biome),
            animals = animals.Select(AnimalHandler.AnimalJson),
            animalKinds = AnimalHandler.KindsJson(biome),
            feed = Husbandry.AnyFeed.Select(f => new { id = f, name = Items.Names[f] }),
            feedPerMeal = Husbandry.FeedPerMeal,
            crops = Farming.Crops.Select(c => new
            {
                id = c.Item,
                name = Items.Names[c.Item],
                growSeconds = c.Grow.TotalSeconds,
                yield = c.Yield,
                xp = c.Xp,
            }),
            // One cast of the rod here, faster on the coast and the lakeshore, and the storm blowing now or the next one.
            fishSeconds = Coast.FishTime(biome).TotalSeconds,
            fishingBonus = Coast.BonusPercent(biome),
            storm = StormJson(time.GetUtcNow()),
            storms = StormsJson(time.GetUtcNow(), 24),
            // The sun over every plot (a day of dayMs from epoch, the first daylightMs of it light), and on sand the sandstorms to come.
            sun = new
            {
                epoch = SeaWeather.Epoch.ToUnixTimeMilliseconds(),
                dayMs = (long)Sun.DayLength.TotalMilliseconds,
                daylightMs = (long)Sun.Daylight.TotalMilliseconds,
            },
            sandstorms = Oasis.Sandy((int)biome) ? SandstormsJson(time.GetUtcNow(), 12) : [],
            // Where crops grow: the rain (crops grow rainSpeed times as fast) and the droughts (droughtSpeed) to come.
            fieldWeather = Farming.Fertile(biome) ? FieldWeatherJson(time.GetUtcNow(), 12) : null,
            // On the glacier: the blizzards to come (turbines stop, ice freezes again, avalanches leave snow), the last one's window
            // for working out where its snow lies, and the frost that slows machines no heater warms.
            blizzards = biome == Biome.Ice ? BlizzardsJson(time.GetUtcNow(), 8) : [],
            frost = Frost.Freezes((int)biome) ? new { speed = Frost.Speed, heaterReach = Frost.HeaterReach } : null,
            bridgeBeams = PlotLayout.BridgeBeams,
            // On the glacier and the tundra: the nights the aurora shines, when tourists pay three times as much.
            auroras = Aurora.Seen((int)biome) ? AurorasJson(time.GetUtcNow(), 6) : [],
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    public static object StormJson(DateTimeOffset now)
    {
        var (start, end) = SeaWeather.Next(now);
        return new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() };
    }

    /// <summary>The storm blowing now (if any) and the next ones, <paramref name="count"/> in all, for the client's forecast.</summary>
    public static IReadOnlyList<object> StormsJson(DateTimeOffset now, int count)
    {
        var storms = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = SeaWeather.Next(at);
            storms.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return storms;
    }

    /// <summary>The rain or the drought now (if any) and the next ones, <paramref name="count"/> in all, with how fast crops grow in them.</summary>
    public static object FieldWeatherJson(DateTimeOffset now, int count)
    {
        var spells = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end, kind) = FieldWeather.Next(at);
            spells.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds(), kind = kind == FieldWeather.Kind.Rain ? "rain" : "drought" });
            at = end;
        }

        return new { rainSpeed = FieldWeather.RainSpeed, droughtSpeed = FieldWeather.DroughtSpeed, spells };
    }

    /// <summary>The blizzard blowing now (if any) and the next ones, <paramref name="count"/> in all.</summary>
    public static IReadOnlyList<object> BlizzardsJson(DateTimeOffset now, int count)
    {
        var storms = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = Blizzard.Next(at);
            storms.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return storms;
    }

    /// <summary>The aurora shining now (if it does) and the next ones, <paramref name="count"/> in all.</summary>
    public static IReadOnlyList<object> AurorasJson(DateTimeOffset now, int count)
    {
        var nights = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = Aurora.Next(at);
            nights.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return nights;
    }

    /// <summary>The sandstorm blowing now (if any) and the next ones, <paramref name="count"/> in all.</summary>
    public static IReadOnlyList<object> SandstormsJson(DateTimeOffset now, int count)
    {
        var storms = new List<object>();
        var at = now;
        for (var i = 0; i < count; i++)
        {
            var (start, end) = SandWeather.Next(at);
            storms.Add(new { start = start.ToUnixTimeMilliseconds(), end = end.ToUnixTimeMilliseconds() });
            at = end;
        }

        return storms;
    }
}

public sealed class HarvestHandler(IServiceScopeFactory scopes, TimeProvider time, OnlinePlayers online) : IMessageHandler
{
    internal const string LastHarvest = "world.lastHarvest";

    public string Type => "plot.harvest";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<HarvestRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var target = new StrikeTarget(request.X, request.Y, request.TileX, request.TileY);
        var harvest = await scope.ServiceProvider.GetRequiredService<PlotService>().HarvestAsync(
            client.RequireAccount(), request.X, request.Y, request.TileX, request.TileY,
            (work, struck) =>
            {
                // The server's own bots gather in one go, at the same pace.
                if (struck && !online.IsBot(client.RequireAccount()))
                {
                    Strikes.CheckLast(client, time, target, work);
                }
                else
                {
                    CheckPace(client, time, work);
                }
            },
            cancellationToken);
        Swung(client, time);
        return new Reply("plot.harvested", new
        {
            tileX = request.TileX,
            tileY = request.TileY,
            // What was gathered, e.g. "bridge" for a bridge laid over a crevasse.
            @object = harvest.Object.ToString().ToLowerInvariant(),
            items = harvest.Items.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
            // The trees next to it that a saw felled at the same cut.
            felled = (harvest.Felled ?? []).Select(t => new { tileX = t.X, tileY = t.Y }),
            xp = new { xp = harvest.Xp.Xp, level = harvest.Xp.Level, leveledUp = harvest.Xp.LeveledUp },
        });
    }

    /// <summary>Refuses work that ends before it could have been done since the last one ended.</summary>
    public static void CheckPace(ClientContext client, TimeProvider time, TimeSpan work)
    {
        // A small tolerance for network jitter between the client's swing and the request.
        if (client.Items.GetValueOrDefault(LastHarvest) is DateTimeOffset last && time.GetUtcNow() - last < work * 0.8)
        {
            throw new GameException("too_fast", "Wait until the last swing is done.");
        }
    }

    public static void Swung(ClientContext client, TimeProvider time) => client.Items[LastHarvest] = time.GetUtcNow();
}
