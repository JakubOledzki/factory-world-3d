using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Timelapse;

/// <summary>What stood on a plot on one day, as somebody last saw it walking in: gzipped JSON [[kind, x, y], ...].</summary>
public sealed class FrameRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public DateOnly Day { get; set; }

    public int Size { get; set; }

    public int Count { get; set; }

    public byte[] Objects { get; set; } = [];
}

public sealed class TimelapseDbContext(DbContextOptions<TimelapseDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "timelapse";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<FrameRow>(e =>
        {
            e.ToTable("frames");
            e.HasKey(f => new { f.X, f.Y, f.Day });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.Day).HasColumnName("day");
            e.Property(f => f.Size).HasColumnName("size");
            e.Property(f => f.Count).HasColumnName("count");
            e.Property(f => f.Objects).HasColumnName("objects");
        });
    }
}

/// <summary>
/// The timelapse of a factory: every day somebody walks onto a plot, what stands on it is kept as that day's frame (the
/// day's last look wins), so its owner and visitors can watch it grow from the first workbench. Kept on demand, from the
/// plot as "plot.enter" sends it, without a loop in the background.
/// </summary>
public static class TimelapseRules
{
    /// <summary>The most frames sent at once; a longer history is thinned out evenly, keeping the first and the last.</summary>
    public const int FramesSent = 60;

    /// <summary>Frames older than this are dropped.</summary>
    public const int DaysKept = 730;

    /// <summary>Picks at most <paramref name="count"/> of <paramref name="total"/> frames, evenly, with the first and the last.</summary>
    public static IReadOnlyList<int> Pick(int total, int count)
    {
        if (total <= count)
        {
            return Enumerable.Range(0, total).ToList();
        }

        return Enumerable.Range(0, count).Select(i => (int)Math.Round(i * (total - 1) / (double)(count - 1))).Distinct().ToList();
    }

    public static byte[] Pack(IReadOnlyList<(string Kind, int X, int Y)> objects)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(objects.Select(o => new object[] { o.Kind, o.X, o.Y }));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(json);
        }

        return output.ToArray();
    }

    public static JsonElement Unpack(byte[] packed)
    {
        using var input = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<JsonElement>(input);
    }

    /// <summary>The objects of a "plot.enter" reply, as [{ kind, tileX, tileY }].</summary>
    public static IReadOnlyList<(string Kind, int X, int Y)> Objects(JsonElement plot)
    {
        var objects = new List<(string, int, int)>();
        if (plot.ValueKind == JsonValueKind.Object && plot.TryGetProperty("objects", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in list.EnumerateArray())
            {
                if (o.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    && o.TryGetProperty("tileX", out var x) && x.TryGetInt32(out var tx)
                    && o.TryGetProperty("tileY", out var y) && y.TryGetInt32(out var ty))
                {
                    objects.Add((kind.GetString()!, tx, ty));
                }
            }
        }

        return objects;
    }
}

public sealed class TimelapseService(GameDb db, TimeProvider time)
{
    /// <summary>Keeps what stands on the plot now as today's frame.</summary>
    public async Task RecordAsync(int x, int y, int size, IReadOnlyList<(string Kind, int X, int Y)> objects, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        await using var command = await db.CommandAsync(
            """
            INSERT INTO timelapse.frames (x, y, day, size, count, objects) VALUES (@x, @y, @day, @size, @count, @objects)
            ON CONFLICT (x, y, day) DO UPDATE SET size = excluded.size, count = excluded.count, objects = excluded.objects;
            DELETE FROM timelapse.frames WHERE x = @x AND y = @y AND day < @oldest;
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("day", today);
        command.Parameters.AddWithValue("size", size);
        command.Parameters.AddWithValue("count", objects.Count);
        command.Parameters.AddWithValue("objects", TimelapseRules.Pack(objects));
        command.Parameters.AddWithValue("oldest", today.AddDays(-TimelapseRules.DaysKept));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<object> FramesAsync(int x, int y, CancellationToken cancellationToken)
    {
        var days = new List<(DateOnly Day, int Count)>();
        await using (var list = await db.CommandAsync("SELECT day, count FROM timelapse.frames WHERE x = @x AND y = @y ORDER BY day", cancellationToken))
        {
            list.Parameters.AddWithValue("x", x);
            list.Parameters.AddWithValue("y", y);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                days.Add((reader.GetFieldValue<DateOnly>(0), reader.GetInt32(1)));
            }
        }

        var picked = TimelapseRules.Pick(days.Count, TimelapseRules.FramesSent).Select(i => days[i].Day).ToArray();
        var frames = new List<object>();
        var size = 0;
        await using (var read = await db.CommandAsync(
            "SELECT day, size, objects FROM timelapse.frames WHERE x = @x AND y = @y AND day = ANY(@days) ORDER BY day", cancellationToken))
        {
            read.Parameters.AddWithValue("x", x);
            read.Parameters.AddWithValue("y", y);
            read.Parameters.AddWithValue("days", picked);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                size = Math.Max(size, reader.GetInt32(1));
                frames.Add(new
                {
                    day = reader.GetFieldValue<DateOnly>(0).ToString("yyyy-MM-dd"),
                    objects = TimelapseRules.Unpack(reader.GetFieldValue<byte[]>(2)),
                });
            }
        }

        return new { x, y, size, days = days.Count, frames };
    }
}

/// <summary>Keeps a frame of the plot whenever somebody walks onto it.</summary>
public sealed class TimelapseObserver(IServiceScopeFactory scopes) : IMessageObserver
{
    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        if (request.Type != "plot.enter" || reply.Type != "plot" || reply.Payload is not { ValueKind: JsonValueKind.Object } plot
            || !plot.TryGetProperty("x", out var px) || !px.TryGetInt32(out var x)
            || !plot.TryGetProperty("y", out var py) || !py.TryGetInt32(out var y))
        {
            return;
        }

        var size = plot.TryGetProperty("size", out var s) && s.TryGetInt32(out var tiles) ? tiles : 0;
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TimelapseService>().RecordAsync(x, y, size, TimelapseRules.Objects(plot), cancellationToken);
    }
}

public sealed record TimelapseRequest(int X, int Y);

/// <summary>The frames of a plot's timelapse, for whoever may walk onto it (its people, and anyone when it is public).</summary>
public sealed class GetTimelapseHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "timelapse.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        var request = Payload.Read<TimelapseRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPlotObjects>().RequireOwnerAsync(player, request.X, request.Y, cancellationToken, PlotRight.Visit);
        return new Reply("timelapse", await scope.ServiceProvider.GetRequiredService<TimelapseService>().FramesAsync(request.X, request.Y, cancellationToken));
    }
}

/// <summary>Timelapses of factories. Owns the <c>timelapse</c> schema.</summary>
public sealed class TimelapseModule : IGameModule
{
    public string Name => "Timelapse";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TimelapseDbContext>(TimelapseDbContext.Schema);
        services.AddScoped<TimelapseService>();
        services.AddSingleton<IMessageObserver, TimelapseObserver>();
        services.AddSingleton<IMessageHandler, GetTimelapseHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<TimelapseDbContext>(cancellationToken);
}
