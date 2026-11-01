using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>A player who liked a plot they visited. One row per player and plot, so a like counts once.</summary>
public sealed class PlotLikeRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public Guid PlayerId { get; set; }

    public DateTimeOffset LikedAt { get; set; }
}

/// <summary>A public plot in the gallery of the most liked factories.</summary>
public sealed record GalleryPlot(int X, int Y, Biome Biome, string? Name, string Owner, int Likes);

public static class GalleryRules
{
    /// <summary>How many plots the gallery shows.</summary>
    public const int Size = 20;

    /// <summary>The gallery is counted at most this often, not for every player who opens it.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromMinutes(2);
}

/// <summary>The most liked public plots, counted once every <see cref="GalleryRules.Refresh"/> for everybody.</summary>
public sealed class PlotGallery(TimeProvider time)
{
    private readonly SemaphoreSlim _counting = new(1, 1);
    private IReadOnlyList<GalleryPlot> _plots = [];
    private DateTimeOffset _countedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<GalleryPlot>> PlotsAsync(Func<Task<IReadOnlyList<GalleryPlot>>> count)
    {
        if (time.GetUtcNow() - _countedAt < GalleryRules.Refresh)
        {
            return _plots;
        }

        await _counting.WaitAsync();
        try
        {
            if (time.GetUtcNow() - _countedAt >= GalleryRules.Refresh)
            {
                _plots = await count();
                _countedAt = time.GetUtcNow();
            }

            return _plots;
        }
        finally
        {
            _counting.Release();
        }
    }

    /// <summary>Counts again at the next look, e.g. after a test changed the likes.</summary>
    public void Forget() => _countedAt = DateTimeOffset.MinValue;
}

public sealed class LikeService(GameDb db, PlotService plots, WorldMap map, ICharacterNames names, PlotGallery gallery, TimeProvider time)
{
    /// <summary>How many players like the plot, and whether <paramref name="player"/> is one of them.</summary>
    public async Task<(int Likes, bool Liked)> LikesAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT count(*), coalesce(bool_or(player_id = @player), false) FROM world.plot_likes WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("player", player);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ((int)reader.GetInt64(0), reader.GetBoolean(1));
    }

    /// <summary>Likes someone else's plot the player may visit, or takes the like back. The owner cannot like their own.</summary>
    public async Task<(int Likes, bool Liked)> LikeAsync(Guid player, int x, int y, bool like, CancellationToken cancellationToken)
    {
        var rights = await plots.RightsAsync(player, x, y, cancellationToken);
        if (rights.Contains(PlotRight.Owner))
        {
            throw new GameException("own_plot", "You cannot like your own plot.");
        }

        if (!rights.Contains(PlotRight.Visit))
        {
            throw new GameException("not_owner", "This plot is not open to visitors.");
        }

        await using (var command = await db.CommandAsync(
            like
                ? "INSERT INTO world.plot_likes (x, y, player_id, liked_at) VALUES (@x, @y, @player, @at) ON CONFLICT DO NOTHING"
                : "DELETE FROM world.plot_likes WHERE x = @x AND y = @y AND player_id = @player", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("player", player);
            command.Parameters.AddWithValue("at", time.GetUtcNow());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await LikesAsync(player, x, y, cancellationToken);
    }

    /// <summary>The most liked public plots, with the ones <paramref name="player"/> likes among them.</summary>
    public async Task<(IReadOnlyList<GalleryPlot> Plots, IReadOnlySet<(int X, int Y)> Liked)> GalleryAsync(Guid player, CancellationToken cancellationToken)
    {
        var top = await gallery.PlotsAsync(() => CountAsync(cancellationToken));
        var liked = new HashSet<(int, int)>();
        if (top.Count > 0)
        {
            await using var command = await db.CommandAsync(
                "SELECT x, y FROM world.plot_likes WHERE player_id = @player AND x = ANY(@xs) AND y = ANY(@ys)", cancellationToken);
            command.Parameters.AddWithValue("player", player);
            command.Parameters.AddWithValue("xs", top.Select(p => p.X).Distinct().ToArray());
            command.Parameters.AddWithValue("ys", top.Select(p => p.Y).Distinct().ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                liked.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        return (top, liked);
    }

    private async Task<IReadOnlyList<GalleryPlot>> CountAsync(CancellationToken cancellationToken)
    {
        var rows = new List<(int X, int Y, Guid Owner, string? Name, int Likes)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT p.x, p.y, p.owner_id, p.name, l.likes
            FROM (SELECT x, y, count(*) AS likes FROM world.plot_likes GROUP BY x, y) l
            JOIN world.plots p ON p.x = l.x AND p.y = l.y
            WHERE p.public
            ORDER BY l.likes DESC, p.bought_at
            LIMIT @size
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("size", GalleryRules.Size);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3), (int)reader.GetInt64(4)));
            }
        }

        var owners = await names.NamesAsync(rows.Select(r => r.Owner).Distinct().ToList(), cancellationToken);
        return rows.Select(r => new GalleryPlot(r.X, r.Y, map.BiomeAt(r.X, r.Y), r.Name, owners.GetValueOrDefault(r.Owner, "?"), r.Likes)).ToList();
    }
}

public sealed record LikePlotRequest(int X, int Y, bool Like = true);

/// <summary>A visitor likes a public plot (or takes the like back); answers with how many like it now.</summary>
public sealed class LikePlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.like";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LikePlotRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (likes, liked) = await scope.ServiceProvider.GetRequiredService<LikeService>()
            .LikeAsync(client.RequireAccount(), request.X, request.Y, request.Like, cancellationToken);
        return new Reply("plot.likes", new { x = request.X, y = request.Y, likes, liked });
    }
}

/// <summary>The most liked public factories, to go and see them.</summary>
public sealed class GalleryHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.gallery";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var (plots, liked) = await scope.ServiceProvider.GetRequiredService<LikeService>().GalleryAsync(client.RequireAccount(), cancellationToken);
        return new Reply("plot.gallery", new
        {
            plots = plots.Select((p, i) => new
            {
                place = i + 1,
                x = p.X,
                y = p.Y,
                biome = (int)p.Biome,
                name = p.Name,
                owner = p.Owner,
                likes = p.Likes,
                liked = liked.Contains((p.X, p.Y)),
            }),
            refreshSeconds = GalleryRules.Refresh.TotalSeconds,
        });
    }
}
