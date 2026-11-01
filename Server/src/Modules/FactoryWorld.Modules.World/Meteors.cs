using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>A meteorite that fell in one period (<see cref="MeteorRules.SlotOf"/>): where, and what is left of it.</summary>
public sealed class MeteorRow
{
    public long Slot { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public long IronLeft { get; set; }

    public long IridiumLeft { get; set; }

    public DateTimeOffset FellAt { get; set; }
}

/// <summary>What one player dug out of one meteorite, and when they last swung the pickaxe at it.</summary>
public sealed class MeteorDigRow
{
    public long Slot { get; set; }

    public Guid PlayerId { get; set; }

    public long Pieces { get; set; }

    public DateTimeOffset LastAt { get; set; }
}

/// <summary>
/// Falling meteorites: every <see cref="Every"/> one falls near a player's plot somewhere in the world, with
/// <see cref="Iron"/> pieces of meteoric iron and a little iridium in it. Anyone with a pickaxe who owns a plot within
/// <see cref="Reach"/> plots of it digs, one piece every <see cref="DigEvery"/>, until it is dug out or the next one falls. Who digs the most
/// tops its list.
/// </summary>
public static class MeteorRules
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(8);

    public const long Iron = 30;

    public const long Iridium = 2;

    /// <summary>One dig in this many also brings out iridium, while there is some.</summary>
    public const int IridiumChance = 8;

    /// <summary>How far (in plots) a player's nearest plot may be to dig.</summary>
    public const double Reach = 15;

    /// <summary>How far from the plot it is aimed at a meteorite may land, in plots either way.</summary>
    public const int Scatter = 8;

    public static readonly TimeSpan DigEvery = TimeSpan.FromSeconds(20);

    public const long DigXp = 5;

    /// <summary>How many of the best diggers the list shows.</summary>
    public const int Shown = 5;

    public static long SlotOf(DateTimeOffset at) => (long)Math.Floor((at - SeaWeather.Epoch) / Every);

    public static DateTimeOffset SlotStart(long slot) => SeaWeather.Epoch + Every * slot;

    /// <summary>Where a meteorite aimed at plot (<paramref name="x"/>, <paramref name="y"/>) lands: somewhere on land within
    /// <see cref="Scatter"/> plots, or right there when no land is near.</summary>
    public static (int X, int Y) Landing(WorldMap map, int x, int y, Random random)
    {
        for (var tries = 0; tries < 50; tries++)
        {
            var (lx, ly) = (((x + random.Next(-Scatter, Scatter + 1)) % map.Width + map.Width) % map.Width, y + random.Next(-Scatter, Scatter + 1));
            if (map.Contains(lx, ly) && map.BiomeAt(lx, ly) is not (Biome.Ocean or Biome.Lake))
            {
                return (lx, ly);
            }
        }

        return (x, y);
    }
}

public sealed record MeteorDig(long Iron, long Iridium, long IronLeft, XpGain Xp);

public sealed class MeteorService(
    GameDb db, WorldMap map, Towns towns, IInventory inventory, IEquipment equipment, ICharacterProgress progress, ICharacterNames names,
    IAnnouncements announcements, OnlinePlayers online, TimeProvider time)
{
    /// <summary>The meteorite of the current period: it falls the first time anybody (or the clock) asks.</summary>
    public Task<MeteorRow> CurrentAsync(CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var slot = MeteorRules.SlotOf(time.GetUtcNow());
            if (await ReadAsync(slot, false, cancellationToken) is { } fallen)
            {
                return fallen;
            }

            var random = Random.Shared;
            var (x, y) = await AimAsync(random, cancellationToken);
            var (lx, ly) = MeteorRules.Landing(map, x, y, random);
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.meteors (slot, x, y, iron_left, iridium_left, fell_at) VALUES (@slot, @x, @y, @iron, @iridium, @at)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("slot", slot);
                insert.Parameters.AddWithValue("x", lx);
                insert.Parameters.AddWithValue("y", ly);
                insert.Parameters.AddWithValue("iron", MeteorRules.Iron);
                insert.Parameters.AddWithValue("iridium", MeteorRules.Iridium);
                insert.Parameters.AddWithValue("at", MeteorRules.SlotStart(slot));
                if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    var near = NearestTown(lx, ly);
                    db.AfterCommit(() => announcements.Announce(
                        $"Meteoryt spadł niedaleko miasta {near} (działka {lx}, {ly})! Gracze z działką w promieniu {MeteorRules.Reach:0} działek mogą go wydobywać."));
                }
            }

            return (await ReadAsync(slot, false, cancellationToken))!;
        }, cancellationToken);

    /// <summary>A plot a real player owns, picked at random, so meteorites fall where people play; any spot on land without one.</summary>
    private async Task<(int X, int Y)> AimAsync(Random random, CancellationToken cancellationToken)
    {
        var owned = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync("SELECT x, y, owner_id FROM world.plots", cancellationToken))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!online.IsBot(reader.GetGuid(2)))
                {
                    owned.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }
        }

        if (owned.Count > 0)
        {
            return owned[random.Next(owned.Count)];
        }

        var town = towns.All[random.Next(towns.All.Count)];
        return (town.X, town.Y);
    }

    private string NearestTown(int x, int y) =>
        towns.All.MinBy(t => CityRules.Distance(map.Width, x, y, t.X, t.Y) - Math.Log10(Math.Max(t.Population, 1)))?.Name ?? "?";

    private async Task<MeteorRow?> ReadAsync(long slot, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"SELECT x, y, iron_left, iridium_left, fell_at FROM world.meteors WHERE slot = @slot {(forUpdate ? "FOR UPDATE" : "")}", cancellationToken);
        command.Parameters.AddWithValue("slot", slot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MeteorRow
            {
                Slot = slot,
                X = reader.GetInt32(0),
                Y = reader.GetInt32(1),
                IronLeft = reader.GetInt64(2),
                IridiumLeft = reader.GetInt64(3),
                FellAt = reader.GetFieldValue<DateTimeOffset>(4),
            }
            : null;
    }

    /// <summary>How many plots the player's nearest plot is from (<paramref name="x"/>, <paramref name="y"/>); null without plots.</summary>
    private async Task<double?> DistanceAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        double? nearest = null;
        await using var command = await db.CommandAsync("SELECT x, y FROM world.plots WHERE owner_id = @player", cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var distance = CityRules.Distance(map.Width, reader.GetInt32(0), reader.GetInt32(1), x, y);
            nearest = nearest is { } n ? Math.Min(n, distance) : distance;
        }

        return nearest;
    }

    /// <summary>One swing of the pickaxe at this period's meteorite: a piece of meteoric iron, now and then iridium as well.</summary>
    public Task<MeteorDig> DigAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var current = await CurrentAsync(cancellationToken);
            var meteor = (await ReadAsync(current.Slot, true, cancellationToken))!;
            if (await DistanceAsync(player, meteor.X, meteor.Y, cancellationToken) is not { } distance || distance > MeteorRules.Reach)
            {
                throw new GameException("meteor_too_far", $"Dig with a plot of your own within {MeteorRules.Reach:0} plots of the meteorite.");
            }

            var held = await equipment.ToolsAsync(player, cancellationToken);
            if (!Items.Pickaxes.Keys.Any(held.Contains))
            {
                throw new GameException("wrong_tool", $"You need {Items.Names[Items.Pickaxe]} in your hand.");
            }

            if (meteor.IronLeft <= 0)
            {
                throw new GameException("meteor_empty", "This meteorite is dug out; wait for the next one.");
            }

            var now = time.GetUtcNow();
            await using (var swing = await db.CommandAsync(
                """
                INSERT INTO world.meteor_digs (slot, player_id, pieces, last_at) VALUES (@slot, @player, 1, @now)
                ON CONFLICT (slot, player_id) DO UPDATE SET pieces = meteor_digs.pieces + 1, last_at = excluded.last_at
                WHERE meteor_digs.last_at <= @ready
                """, cancellationToken))
            {
                swing.Parameters.AddWithValue("slot", meteor.Slot);
                swing.Parameters.AddWithValue("player", player);
                swing.Parameters.AddWithValue("now", now);
                swing.Parameters.AddWithValue("ready", now - MeteorRules.DigEvery);
                if (await swing.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("meteor_cooldown", $"One piece every {MeteorRules.DigEvery.TotalSeconds:0} seconds.");
                }
            }

            var iridium = meteor.IridiumLeft > 0 && Random.Shared.Next(MeteorRules.IridiumChance) == 0 ? 1L : 0L;
            meteor.IronLeft--;
            meteor.IridiumLeft -= iridium;
            await using (var save = await db.CommandAsync(
                "UPDATE world.meteors SET iron_left = @iron, iridium_left = @iridium WHERE slot = @slot", cancellationToken))
            {
                save.Parameters.AddWithValue("slot", meteor.Slot);
                save.Parameters.AddWithValue("iron", meteor.IronLeft);
                save.Parameters.AddWithValue("iridium", meteor.IridiumLeft);
                await save.ExecuteNonQueryAsync(cancellationToken);
            }

            await inventory.ReceiveAsync(player, Items.MeteoricIron, 1, cancellationToken);
            if (iridium > 0)
            {
                await inventory.ReceiveAsync(player, Items.Iridium, iridium, cancellationToken);
            }

            if (meteor.IronLeft == 0 && (await DiggersAsync(meteor.Slot, 1, cancellationToken)) is [var best])
            {
                var name = (await names.NamesAsync([best.Player], cancellationToken)).GetValueOrDefault(best.Player, "?");
                db.AfterCommit(() => announcements.Announce($"Meteoryt wydobyty do końca. Najwięcej wykopał {name}: {best.Pieces} szt."));
            }

            var xp = await progress.AddXpAsync(player, MeteorRules.DigXp, cancellationToken);
            return new MeteorDig(1, iridium, meteor.IronLeft, xp);
        }, cancellationToken);

    private async Task<IReadOnlyList<(Guid Player, long Pieces)>> DiggersAsync(long slot, int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT player_id, pieces FROM world.meteor_digs WHERE slot = @slot ORDER BY pieces DESC, last_at, player_id LIMIT @count", cancellationToken);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("count", count);
        var found = new List<(Guid, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add((reader.GetGuid(0), reader.GetInt64(1)));
        }

        return found;
    }

    /// <summary>The meteorite as the player sees it: where it is, what is left, whether they reach it, and who dug the most.</summary>
    public async Task<object> ViewAsync(Guid player, CancellationToken cancellationToken)
    {
        var meteor = await CurrentAsync(cancellationToken);
        var now = time.GetUtcNow();
        var distance = await DistanceAsync(player, meteor.X, meteor.Y, cancellationToken);
        var diggers = await DiggersAsync(meteor.Slot, MeteorRules.Shown, cancellationToken);
        long mine = 0;
        DateTimeOffset? last = null;
        await using (var command = await db.CommandAsync(
            "SELECT pieces, last_at FROM world.meteor_digs WHERE slot = @slot AND player_id = @player", cancellationToken))
        {
            command.Parameters.AddWithValue("slot", meteor.Slot);
            command.Parameters.AddWithValue("player", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                mine = reader.GetInt64(0);
                last = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        var named = await names.NamesAsync(diggers.Select(d => d.Player).ToList(), cancellationToken);
        return new
        {
            x = meteor.X,
            y = meteor.Y,
            town = NearestTown(meteor.X, meteor.Y),
            // Unix milliseconds.
            fellAt = meteor.FellAt.ToUnixTimeMilliseconds(),
            nextAt = MeteorRules.SlotStart(meteor.Slot + 1).ToUnixTimeMilliseconds(),
            ironLeft = meteor.IronLeft,
            iron = MeteorRules.Iron,
            iridiumLeft = meteor.IridiumLeft,
            reach = MeteorRules.Reach,
            distance = distance is { } d ? Math.Round(d, 1) : (double?)null,
            inReach = distance <= MeteorRules.Reach,
            digSeconds = MeteorRules.DigEvery.TotalSeconds,
            readyAt = (last is { } l ? l + MeteorRules.DigEvery : now).ToUnixTimeMilliseconds(),
            mine,
            diggers = diggers.Select((g, i) => new { place = i + 1, name = named.GetValueOrDefault(g.Player, "?"), pieces = g.Pieces, mine = g.Player == player }),
            serverTime = now.ToUnixTimeMilliseconds(),
        };
    }
}

/// <summary>Lets each period's meteorite fall when the period begins, so the chat tells everybody even when nobody looks.</summary>
public sealed class MeteorClock(IServiceScopeFactory scopes, ILogger<MeteorClock> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MeteorService>().CurrentAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Letting the meteorite fall failed");
            }
        }
    }
}

public sealed class GetMeteorHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "meteor.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("meteor", new { state = await scope.ServiceProvider.GetRequiredService<MeteorService>().ViewAsync(client.RequireAccount(), cancellationToken) });
    }
}

public sealed class DigMeteorHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "meteor.dig";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        var meteors = scope.ServiceProvider.GetRequiredService<MeteorService>();
        var dig = await meteors.DigAsync(player, cancellationToken);
        var items = new List<object> { new { id = Items.MeteoricIron, name = Items.Names[Items.MeteoricIron], quantity = dig.Iron } };
        if (dig.Iridium > 0)
        {
            items.Add(new { id = Items.Iridium, name = Items.Names[Items.Iridium], quantity = dig.Iridium });
        }

        return new Reply("meteor", new
        {
            items,
            xp = new { xp = dig.Xp.Xp, level = dig.Xp.Level, leveledUp = dig.Xp.LeveledUp },
            state = await meteors.ViewAsync(player, cancellationToken),
        });
    }
}
