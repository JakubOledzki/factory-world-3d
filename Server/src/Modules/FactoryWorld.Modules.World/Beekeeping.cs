using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>
/// A beehive stands on a plot and its bees make honey by themselves, one jar every <see cref="HoneyTime"/>, and a piece of wax with
/// every <see cref="HoneyPerWax"/> jars. Lindens in flower within <see cref="Reach"/> tiles make them work faster: each one
/// <see cref="PerLinden"/> × more, up to twice as fast. A full hive (<see cref="MaxHoney"/> jars) waits until the honey is taken.
/// No bees fly on the glacier or in the mountains.
/// </summary>
public static class Bees
{
    public static readonly TimeSpan HoneyTime = TimeSpan.FromMinutes(10);

    public const int MaxHoney = 30;

    public const int HoneyPerWax = 3;

    public const int Reach = 6;

    public const double PerLinden = 0.1;

    public const double MaxBonus = 1;

    public const int XpPerJar = 1;

    public static double Speed(int lindens) => 1 + Math.Min(MaxBonus, PerLinden * lindens);

    public static bool Fly(Biome biome) => biome is not (Biome.Ice or Biome.Mountains);
}

/// <param name="Made">Jars made since the hive was put up, for the wax that comes with every third.</param>
/// <param name="Progress">How far the next jar is, 0-1.</param>
public sealed record Hive(Guid Id, int X, int Y, Guid Owner, int Honey, int Wax, int Made, double Progress, DateTimeOffset At)
{
    /// <summary>The hive as it is at <paramref name="now"/>, its bees working at <paramref name="speed"/> since <see cref="At"/>.</summary>
    public Hive Advance(DateTimeOffset now, double speed)
    {
        if (now <= At)
        {
            return this;
        }

        if (Honey >= Bees.MaxHoney)
        {
            return this with { Progress = 0, At = now };
        }

        var progress = Progress + (now - At) / Bees.HoneyTime * speed;
        var jars = Math.Min((int)progress, Bees.MaxHoney - Honey);
        var made = Made + jars;
        return this with
        {
            Honey = Honey + jars,
            Wax = Wax + made / Bees.HoneyPerWax - Made / Bees.HoneyPerWax,
            Made = made,
            Progress = Honey + jars >= Bees.MaxHoney ? 0 : progress - jars,
            At = now,
        };
    }
}

public sealed class BeeService(
    GameDb db, PlotService plots, WorldMap map, IInventory inventory, ICharacterProgress progress, IProductionStats stats, TimeProvider time)
    : IPlacedHook, IMachineContents
{
    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (placed.Kind != Items.Beehive)
        {
            return;
        }

        await using var insert = await db.CommandAsync(
            """
            INSERT INTO world.hives (object_id, x, y, owner_id, honey, wax, made, progress, at)
            VALUES (@id, @x, @y, @owner, 0, 0, 0, 0, @now) ON CONFLICT DO NOTHING
            """, cancellationToken);
        insert.Parameters.AddWithValue("id", placed.Id);
        insert.Parameters.AddWithValue("x", x);
        insert.Parameters.AddWithValue("y", y);
        insert.Parameters.AddWithValue("owner", owner);
        insert.Parameters.AddWithValue("now", time.GetUtcNow());
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The hive as it is now, with the lindens that feed its bees.</summary>
    public Task<(Hive Hive, int Lindens)> StateAsync(Guid player, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (hive, lindens) = await OpenAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, hive.X, hive.Y, cancellationToken, PlotRight.Work);
            await SaveAsync(hive, cancellationToken);
            return (hive, lindens);
        }, cancellationToken);

    /// <summary>Takes the honey and the wax out of the hive into the inventory, as far as they fit; the rest stays in.</summary>
    public Task<(Hive Hive, int Lindens, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp)> TakeAsync(
        Guid player, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (hive, lindens) = await OpenAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, hive.X, hive.Y, cancellationToken, PlotRight.Take);
            var taken = new List<(string Item, int Quantity)>();
            var honey = (int)await inventory.RoomForAsync(player, Items.Honey, hive.Honey, cancellationToken);
            if (honey > 0)
            {
                await inventory.AddAsync(player, Items.Honey, honey, cancellationToken);
                taken.Add((Items.Honey, honey));
            }

            var wax = (int)await inventory.RoomForAsync(player, Items.Wax, hive.Wax, cancellationToken);
            if (wax > 0)
            {
                await inventory.AddAsync(player, Items.Wax, wax, cancellationToken);
                taken.Add((Items.Wax, wax));
            }

            if (taken.Count == 0 && hive.Honey + hive.Wax > 0)
            {
                throw InventoryExtensions.InventoryFull();
            }

            XpGain? xp = null;
            if (taken.Count > 0)
            {
                await stats.RecordAsync(hive.Owner, taken.Select(t => ProductionEntry.Made(t.Item, t.Quantity, time.GetUtcNow())).ToList(), cancellationToken);
                xp = await progress.AddXpAsync(player, honey * Bees.XpPerJar, cancellationToken);
            }

            hive = hive with { Honey = hive.Honey - honey, Wax = hive.Wax - wax };
            await SaveAsync(hive, cancellationToken);
            return (hive, lindens, (IReadOnlyList<(string, int)>)taken, xp);
        }, cancellationToken);

    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM world.hives WHERE object_id = @id RETURNING honey, wax", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        int honey = 0, wax = 0;
        await using (var reader = await delete.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                (honey, wax) = (reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        if (honey > 0)
        {
            await inventory.AddAsync(owner, Items.Honey, honey, cancellationToken);
        }

        if (wax > 0)
        {
            await inventory.AddAsync(owner, Items.Wax, wax, cancellationToken);
        }
    }

    /// <summary>Reads the hive (locked until the transaction ends) and works it up to now.</summary>
    private async Task<(Hive, int)> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        Hive? hive = null;
        await using (var select = await db.CommandAsync(
            "SELECT x, y, owner_id, honey, wax, made, progress, at FROM world.hives WHERE object_id = @id FOR UPDATE", cancellationToken))
        {
            select.Parameters.AddWithValue("id", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                hive = new Hive(id, reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetDouble(6), reader.GetFieldValue<DateTimeOffset>(7));
            }
        }

        if (hive is null)
        {
            throw new GameException("no_object", "There is no such beehive.");
        }

        var tile = await TileAsync(id, cancellationToken);
        var lindens = await LindensAsync(hive.X, hive.Y, tile.X, tile.Y, cancellationToken);
        return (hive.Advance(time.GetUtcNow(), Bees.Speed(lindens)), lindens);
    }

    private async Task<(int X, int Y)> TileAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync("SELECT tile_x, tile_y FROM world.plot_objects WHERE id = @id", cancellationToken);
        select.Parameters.AddWithValue("id", id);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt32(0), reader.GetInt32(1)) : throw new GameException("no_object", "There is no such beehive.");
    }

    /// <summary>Lindens, wild or planted and grown, within the bees' reach of (tileX, tileY).</summary>
    private async Task<int> LindensAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        var tiles = await plots.TilesAsync(x, y, map.BiomeAt(x, y), cancellationToken);
        var lindens = 0;
        for (var ty = Math.Max(0, tileY - Bees.Reach); ty <= Math.Min(PlotLayout.Size - 1, tileY + Bees.Reach); ty++)
        {
            for (var tx = Math.Max(0, tileX - Bees.Reach); tx <= Math.Min(PlotLayout.Size - 1, tileX + Bees.Reach); tx++)
            {
                if (tiles[ty * PlotLayout.Size + tx] == (byte)TileObject.LindenTree)
                {
                    lindens++;
                }
            }
        }

        var now = time.GetUtcNow();
        lindens += (await ForestService.ReadAsync(db, x, y, cancellationToken))
            .Count(t => t.Species == Trees.Linden && t.ReadyAt <= now && Math.Abs(t.TileX - tileX) <= Bees.Reach && Math.Abs(t.TileY - tileY) <= Bees.Reach);
        return lindens;
    }

    private async Task SaveAsync(Hive hive, CancellationToken cancellationToken)
    {
        await using var update = await db.CommandAsync(
            "UPDATE world.hives SET honey = @honey, wax = @wax, made = @made, progress = @progress, at = @at WHERE object_id = @id", cancellationToken);
        update.Parameters.AddWithValue("id", hive.Id);
        update.Parameters.AddWithValue("honey", hive.Honey);
        update.Parameters.AddWithValue("wax", hive.Wax);
        update.Parameters.AddWithValue("made", hive.Made);
        update.Parameters.AddWithValue("progress", hive.Progress);
        update.Parameters.AddWithValue("at", hive.At);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    public static object Json(Hive hive, int lindens, DateTimeOffset now) => new
    {
        id = hive.Id,
        honey = hive.Honey,
        wax = hive.Wax,
        maxHoney = Bees.MaxHoney,
        honeyPerWax = Bees.HoneyPerWax,
        lindens,
        reach = Bees.Reach,
        speedPercent = (int)Math.Round(Bees.Speed(lindens) * 100),
        honeySeconds = (Bees.HoneyTime / Bees.Speed(lindens)).TotalSeconds,
        progress = hive.Progress,
        full = hive.Honey >= Bees.MaxHoney,
        serverTime = now.ToUnixTimeMilliseconds(),
    };
}

public sealed record HiveRequest(Guid Id);

public abstract class HiveHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<HiveRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (hive, lindens, taken, xp) = await RunAsync(scope.ServiceProvider.GetRequiredService<BeeService>(), client.RequireAccount(), request.Id, cancellationToken);
        return new Reply("hive", new
        {
            hive = BeeService.Json(hive, lindens, time.GetUtcNow()),
            taken = taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = xp is { } gain ? new { xp = gain.Xp, level = gain.Level, leveledUp = gain.LeveledUp } : null,
        });
    }

    protected abstract Task<(Hive, int, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        BeeService bees, Guid player, Guid id, CancellationToken cancellationToken);
}

public sealed class GetHiveHandler(IServiceScopeFactory scopes, TimeProvider time) : HiveHandler(scopes, time)
{
    public override string Type => "hive.get";

    protected override async Task<(Hive, int, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        BeeService bees, Guid player, Guid id, CancellationToken cancellationToken)
    {
        var (hive, lindens) = await bees.StateAsync(player, id, cancellationToken);
        return (hive, lindens, [], null);
    }
}

public sealed class TakeFromHiveHandler(IServiceScopeFactory scopes, TimeProvider time) : HiveHandler(scopes, time)
{
    public override string Type => "hive.take";

    protected override Task<(Hive, int, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        BeeService bees, Guid player, Guid id, CancellationToken cancellationToken) =>
        bees.TakeAsync(player, id, cancellationToken);
}
