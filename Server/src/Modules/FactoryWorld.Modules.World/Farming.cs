using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <param name="Item">The crop is harvested as this item, so its id is the item's.</param>
/// <param name="Yield">Items one ripe field gives.</param>
public sealed record Crop(string Item, TimeSpan Grow, int Yield, int Xp);

/// <summary>
/// Fertile plots can be farmed: the hoe tills a free tile into a field, a crop is sown on the field
/// and grows for its own time, then it is reaped by hand and the field can be sown again. A sprinkler waters the fields around it:
/// crops sown there grow faster, and on the steppe and the desert the ground it reaches can be tilled at all. Sugar beets, flax and
/// hemp are sown only once researched.
/// Rain makes crops grow faster, a drought and smog slower (<see cref="FieldWeather"/>, <see cref="Smog"/>).
/// </summary>
public static class Farming
{
    /// <summary>Every crop, in the order the client lists them.</summary>
    public static readonly IReadOnlyList<Crop> Crops =
    [
        new(Items.Wheat, TimeSpan.FromMinutes(5), 4, 3),
        new(Items.Carrot, TimeSpan.FromMinutes(7), 4, 4),
        new(Items.Cabbage, TimeSpan.FromMinutes(8), 2, 4),
        new(Items.Corn, TimeSpan.FromMinutes(10), 3, 5),
        new(Items.Potato, TimeSpan.FromMinutes(15), 5, 7),
        new(Items.Sunflower, TimeSpan.FromMinutes(20), 3, 9),
        // Crops of the farming branch, sown once researched.
        new(Items.SugarBeet, TimeSpan.FromMinutes(12), 4, 6),
        new(Items.Flax, TimeSpan.FromMinutes(10), 3, 5),
        new(Items.Hemp, TimeSpan.FromMinutes(14), 4, 6),
    ];

    public static readonly IReadOnlyDictionary<string, Crop> ById = Crops.ToDictionary(c => c.Item);

    /// <summary>Only fertile soil can be tilled.</summary>
    public static bool Fertile(Biome biome) => biome is Biome.Farmland or Biome.Oasis;

    public const int XpPerTill = 1;

    /// <summary>A crop sown with fertiliser grows this much faster: in half the time.</summary>
    public const double FertilizerSpeed = 2;

    /// <summary>A crop sown on manure grows a quarter faster: in 75% of its time.</summary>
    public const double ManureSpeed = 4.0 / 3;

    /// <summary>A sprinkler waters the fields this many tiles from it each way.</summary>
    public const int SprinklerReach = 4;

    /// <summary>A crop sown where a sprinkler waters grows this many times faster.</summary>
    public const double SprinklerSpeed = 1.3;

    /// <summary>Dry land that can be tilled where a sprinkler waters it: the steppe and the desert.</summary>
    public static bool Irrigable(Biome biome) => biome is Biome.Grassland or Biome.Desert;

    /// <summary>Whether a sprinkler among <paramref name="objects"/> waters the tile.</summary>
    public static bool Sprinkled(IEnumerable<PlotObject> objects, int tileX, int tileY) =>
        objects.Any(o => o.Kind == Items.Sprinkler && Math.Abs(o.TileX - tileX) <= SprinklerReach && Math.Abs(o.TileY - tileY) <= SprinklerReach);
}

/// <param name="Crop">Null on a tilled field nothing grows on yet.</param>
public sealed record Field(int TileX, int TileY, string? Crop, DateTimeOffset? ReadyAt);

/// <param name="Items">What reaping gave; empty when tilling or sowing.</param>
/// <param name="Xp">Null when the action gives no experience.</param>
public sealed record FieldChange(Field Field, IReadOnlyList<(string Item, int Quantity)> Items, XpGain? Xp);

public sealed class FarmService(
    GameDb db, PlotService plots, IInventory inventory, IEquipment equipment, ICharacterProgress progress, IProductionStats stats, TimeProvider time,
    OilService oil, IPlotObjects objects, ITechnologies technologies, SmogService smog)
{
    public async Task<IReadOnlyList<Field>> FieldsAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, crop, planted_at FROM world.plot_fields WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<Field>();
        while (await reader.ReadAsync(cancellationToken))
        {
            fields.Add(Read(reader.GetInt32(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return fields;
    }

    /// <summary>How many times faster the player's clothes make farm work and crops.</summary>
    public Task<double> SpeedAsync(Guid player, CancellationToken cancellationToken) =>
        plots.SpeedAsync(player, Industries.Farming, cancellationToken);

    /// <summary>Turns a free tile of a fertile plot into a field, with the hoe in hand.</summary>
    public Task<FieldChange> TillAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            if (!Farming.Fertile(biome)
                && !(Farming.Irrigable(biome) && Farming.Sprinkled(await objects.ObjectsAsync(x, y, cancellationToken), tileX, tileY)))
            {
                throw new GameException("not_fertile", "Only fertile soil can be tilled, or the steppe and the desert where a sprinkler waters it.");
            }

            if (!(await equipment.ToolsAsync(player, cancellationToken)).Contains(Items.Hoe))
            {
                throw new GameException("wrong_tool", "You need a hoe in your hand.");
            }

            if (!await plots.FreeAsync(player, x, y, tileX, tileY, cancellationToken))
            {
                throw new GameException("blocked", "Something stands here.");
            }

            await oil.RequireCleanAsync(x, y, tileX, tileY, cancellationToken);
            await using (var insert = await db.CommandAsync(
                "INSERT INTO world.plot_fields (x, y, tile_x, tile_y) VALUES (@x, @y, @tx, @ty) ON CONFLICT DO NOTHING", cancellationToken))
            {
                Bind(insert, x, y, tileX, tileY);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("blocked", "This is already a field.");
                }
            }

            var xp = await progress.AddXpAsync(player, Farming.XpPerTill, cancellationToken);
            return new FieldChange(new Field(tileX, tileY, null, null), [], xp);
        }, cancellationToken);

    /// <summary>Sows a crop on an empty field; <paramref name="fertilize"/> spends a fertiliser from the inventory, and the crop grows in half the time;
    /// <paramref name="manure"/> spends a manure, and it grows in 75% of the time.</summary>
    public Task<FieldChange> PlantAsync(
        Guid player, int x, int y, int tileX, int tileY, string crop, CancellationToken cancellationToken, bool fertilize = false, bool manure = false) =>
        db.InTransactionAsync(async () =>
        {
            if (!Farming.ById.ContainsKey(crop))
            {
                throw new GameException("unknown_crop", "There is no such crop.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await technologies.RequireUnlockedAsync(player, crop, cancellationToken);
            await oil.RequireCleanAsync(x, y, tileX, tileY, cancellationToken);
            var now = time.GetUtcNow();
            if (fertilize)
            {
                await inventory.RemoveAsync(player, Items.Fertilizer, 1, cancellationToken);
                await stats.RecordAsync(player, [ProductionEntry.Used(Items.Fertilizer, 1, now)], cancellationToken);
            }

            if (manure)
            {
                await inventory.RemoveAsync(player, Items.Manure, 1, cancellationToken);
                await stats.RecordAsync(player, [ProductionEntry.Used(Items.Manure, 1, now)], cancellationToken);
            }

            // A fertilised crop counts as sown so long ago that it is ripe in half its time; a farmer's clothes take off more.
            // Rain to come makes it ripe sooner, a drought later: it counts as sown as long before it is ripe as it grows in clear weather.
            // Smog over the plot slows it down.
            var speed = (fertilize ? Farming.FertilizerSpeed : 1) * (manure ? Farming.ManureSpeed : 1)
                * (Farming.Sprinkled(await objects.ObjectsAsync(x, y, cancellationToken), tileX, tileY) ? Farming.SprinklerSpeed : 1)
                * await plots.SpeedAsync(player, Industries.Farming, cancellationToken) * (await smog.LevelAsync(x, y, cancellationToken)).CropSpeed;
            var sown = FieldWeather.Ripe(now, Farming.ById[crop].Grow / speed) - Farming.ById[crop].Grow;
            await using var update = await db.CommandAsync(
                """
                UPDATE world.plot_fields SET crop = @crop, planted_at = @now
                WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty AND crop IS NULL
                """, cancellationToken);
            Bind(update, x, y, tileX, tileY);
            update.Parameters.AddWithValue("crop", crop);
            update.Parameters.AddWithValue("now", sown);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("no_field", "Till the ground first, or reap what grows here.");
            }

            return new FieldChange(Read(tileX, tileY, crop, sown), [], null);
        }, cancellationToken);

    /// <summary>Gathers a ripe crop by hand; the field stays tilled.</summary>
    public Task<FieldChange> ReapAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            string crop;
            DateTimeOffset planted;
            await using (var select = await db.CommandAsync(
                """
                SELECT crop, planted_at FROM world.plot_fields
                WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty AND crop IS NOT NULL
                FOR UPDATE
                """, cancellationToken))
            {
                Bind(select, x, y, tileX, tileY);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new GameException("nothing_here", "Nothing grows here.");
                }

                crop = reader.GetString(0);
                planted = reader.GetFieldValue<DateTimeOffset>(1);
            }

            var info = Farming.ById[crop];
            if (planted + info.Grow > time.GetUtcNow())
            {
                throw new GameException("not_ripe", "It is not ripe yet.");
            }

            await using (var clear = await db.CommandAsync(
                "UPDATE world.plot_fields SET crop = NULL, planted_at = NULL WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty", cancellationToken))
            {
                Bind(clear, x, y, tileX, tileY);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            await inventory.ReceiveAsync(player, crop, info.Yield, cancellationToken);
            await stats.RecordAsync(player, [ProductionEntry.Made(crop, info.Yield, time.GetUtcNow())], cancellationToken);
            var xp = await progress.AddXpAsync(player, info.Xp, cancellationToken);
            return new FieldChange(new Field(tileX, tileY, null, null), [(crop, info.Yield)], xp);
        }, cancellationToken);

    /// <summary>Turns a field tilled by hand back into bare ground; a crop still growing on it is lost, a ripe one has to be reaped first.
    /// Fields of a farmstead go only with the farmstead.</summary>
    public Task UntillAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            string? crop;
            DateTimeOffset? planted;
            await using (var select = await db.CommandAsync(
                """
                SELECT farmstead_id IS NOT NULL, crop, planted_at FROM world.plot_fields
                WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty
                FOR UPDATE
                """, cancellationToken))
            {
                Bind(select, x, y, tileX, tileY);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new GameException("nothing_here", "There is no field here.");
                }

                if (reader.GetBoolean(0))
                {
                    throw new GameException("farmstead_field", "This field belongs to a farmstead; it goes when the farmstead is picked up.");
                }

                crop = reader.IsDBNull(1) ? null : reader.GetString(1);
                planted = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
            }

            if (Read(tileX, tileY, crop, planted).ReadyAt is { } ready && ready <= time.GetUtcNow())
            {
                throw new GameException("reap_first", "Reap the ripe crop first.");
            }

            await using var delete = await db.CommandAsync(
                "DELETE FROM world.plot_fields WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty AND farmstead_id IS NULL", cancellationToken);
            Bind(delete, x, y, tileX, tileY);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private static Field Read(int tileX, int tileY, string? crop, DateTimeOffset? planted) =>
        new(tileX, tileY, crop, crop is not null && planted is { } at && Farming.ById.TryGetValue(crop, out var info) ? at + info.Grow : null);

    private static void Bind(Npgsql.NpgsqlCommand command, int x, int y, int tileX, int tileY)
    {
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("tx", tileX);
        command.Parameters.AddWithValue("ty", tileY);
    }
}

public sealed record FieldRequest(int X, int Y, int TileX, int TileY, string? Crop, bool Fertilize = false, bool Manure = false);

/// <summary>plot.till, plot.plant and plot.reap all answer with the changed field.</summary>
public abstract class FieldHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    protected TimeProvider Time => time;

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<FieldRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var change = await RunAsync(scope.ServiceProvider.GetRequiredService<FarmService>(), client, request, cancellationToken);
        return new Reply("plot.field", new
        {
            field = FieldJson(change.Field),
            items = change.Items.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
            xp = change.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<FieldChange> RunAsync(FarmService farm, ClientContext client, FieldRequest request, CancellationToken cancellationToken);

    public static object FieldJson(Field field) => new
    {
        tileX = field.TileX,
        tileY = field.TileY,
        crop = field.Crop,
        readyAt = field.ReadyAt?.ToUnixTimeMilliseconds(),
    };
}

public sealed class TillHandler(IServiceScopeFactory scopes, TimeProvider time) : FieldHandler(scopes, time)
{
    public override string Type => "plot.till";

    protected override async Task<FieldChange> RunAsync(FarmService farm, ClientContext client, FieldRequest r, CancellationToken cancellationToken)
    {
        // Tilling is a swing of a tool like chopping, with the same pace, quicker in a farmer's clothes.
        HarvestHandler.CheckPace(client, Time, HarvestRules.Time / await farm.SpeedAsync(client.RequireAccount(), cancellationToken));
        var change = await farm.TillAsync(client.RequireAccount(), r.X, r.Y, r.TileX, r.TileY, cancellationToken);
        HarvestHandler.Swung(client, Time);
        return change;
    }
}

public sealed class PlantHandler(IServiceScopeFactory scopes, TimeProvider time) : FieldHandler(scopes, time)
{
    public override string Type => "plot.plant";

    protected override Task<FieldChange> RunAsync(FarmService farm, ClientContext client, FieldRequest r, CancellationToken cancellationToken) =>
        farm.PlantAsync(client.RequireAccount(), r.X, r.Y, r.TileX, r.TileY, r.Crop ?? "", cancellationToken, r.Fertilize, r.Manure);
}

public sealed class ReapHandler(IServiceScopeFactory scopes, TimeProvider time) : FieldHandler(scopes, time)
{
    public override string Type => "plot.reap";

    protected override Task<FieldChange> RunAsync(FarmService farm, ClientContext client, FieldRequest r, CancellationToken cancellationToken) =>
        farm.ReapAsync(client.RequireAccount(), r.X, r.Y, r.TileX, r.TileY, cancellationToken);
}

public sealed class UntillHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public string Type => "plot.untill";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var r = Payload.Read<FieldRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FarmService>().UntillAsync(client.RequireAccount(), r.X, r.Y, r.TileX, r.TileY, cancellationToken);
        return new Reply("plot.untilled", new { tileX = r.TileX, tileY = r.TileY, serverTime = time.GetUtcNow().ToUnixTimeMilliseconds() });
    }
}
