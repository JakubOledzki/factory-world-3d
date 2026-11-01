using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Puzzles;

/// <summary>A player's best solution of one puzzle.</summary>
public sealed class ResultRow
{
    public Guid OwnerId { get; set; }

    public required string PuzzleId { get; set; }

    /// <summary>3 gold, 2 silver, 1 bronze.</summary>
    public int Medal { get; set; }

    public int Objects { get; set; }

    public double Rate { get; set; }

    /// <summary>The pieces of the best solution, as JSON, so the player can open it again.</summary>
    public string Layout { get; set; } = "[]";

    public DateTimeOffset SolvedAt { get; set; }
}

public sealed class PuzzlesDbContext(DbContextOptions<PuzzlesDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "puzzles";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<ResultRow>(e =>
        {
            e.ToTable("results");
            e.HasKey(r => new { r.OwnerId, r.PuzzleId });
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.PuzzleId).HasColumnName("puzzle_id").HasMaxLength(32);
            e.Property(r => r.Medal).HasColumnName("medal");
            e.Property(r => r.Objects).HasColumnName("objects");
            e.Property(r => r.Rate).HasColumnName("rate");
            e.Property(r => r.Layout).HasColumnName("layout").HasColumnType("jsonb");
            e.Property(r => r.SolvedAt).HasColumnName("solved_at");
        });
    }
}

/// <summary>
/// Engineering puzzles: small yards to lay out a line in, judged by <see cref="Solver"/>. They teach how belts, inserters and
/// machines work together and pay nothing but experience the first time (and once more for the first gold), so they touch
/// no money and no items. Rates are those of the real objects (<c>LogisticsRules</c>, <c>Processing</c>).
/// </summary>
public static class PuzzleBook
{
    public const long SolveXp = 300;
    public const long GoldXp = 300;

    /// <summary>The most pieces a submitted layout may have.</summary>
    public const int MostPieces = 80;

    private static readonly PuzzleMachine Sawmill = new(Items.Sawmill, Items.Wood, Items.Planks, 7.5, 2);
    private static readonly PuzzleMachine Stonemason = new(Items.Stonemason, Items.Stone, Items.StoneBlocks, 22.5, 1.0 / 3);

    public static readonly IReadOnlyList<Puzzle> All =
    [
        new("first_belt", "Pierwsza taśma",
            "Przenieś drewno ze skrzyni po lewej do skrzyni po prawej: co najmniej 30 sztuk na minutę. Podajnik bierze zza siebie i kładzie przed siebie.",
            7, 3, (0, 1), (6, 1), Items.Wood, Items.Wood, 30,
            new Dictionary<string, int> { [Items.Belt] = 10, [Items.Inserter] = 4 }, null, 5, 7),
        new("sawmill", "Tartak",
            "Zrób z drewna deski: 15 desek na minutę. Tartak bierze 7,5 drewna na minutę i robi z każdego 2 deski.",
            8, 5, (0, 2), (7, 2), Items.Wood, Items.Planks, 15,
            new Dictionary<string, int> { [Items.Belt] = 10, [Items.Inserter] = 4, [Items.Sawmill] = 1 }, Sawmill, 5, 7),
        new("two_sawmills", "Dwa tartaki",
            "30 desek na minutę: jeden tartak nie nadąży. Dwa tartaki, a ich deski muszą trafić do jednej skrzyni.",
            7, 7, (0, 3), (6, 3), Items.Wood, Items.Planks, 30,
            new Dictionary<string, int> { [Items.Belt] = 20, [Items.Inserter] = 8, [Items.Sawmill] = 2 }, Sawmill, 11, 15),
        new("stonemason", "Kamieniarz",
            "15 bloków kamiennych na minutę. Kamieniarz bierze 22,5 kamienia na minutę i robi z trzech jeden blok. Skrzynia z kamieniem stoi w rogu.",
            8, 5, (0, 0), (7, 4), Items.Stone, Items.StoneBlocks, 15,
            new Dictionary<string, int> { [Items.Belt] = 20, [Items.Inserter] = 8, [Items.Stonemason] = 2 }, Stonemason, 11, 15),
        new("fast_line", "Szybka linia",
            "45 desek na minutę z trzech tartaków. Zwykły podajnik przenosi 37,5 sztuki na minutę, szybki dwa razy tyle.",
            10, 7, (0, 0), (9, 5), Items.Wood, Items.Planks, 45,
            new Dictionary<string, int> { [Items.Belt] = 30, [Items.Inserter] = 10, [Items.FastInserter] = 2, [Items.Sawmill] = 3 }, Sawmill, 21, 25),
    ];

    public static Puzzle? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

public sealed record Best(int Medal, int Objects, double Rate, JsonElement Layout);

public sealed class PuzzleService(GameDb db, ICharacterProgress progress, TimeProvider time)
{
    public async Task<IReadOnlyDictionary<string, Best>> BestAsync(Guid owner, CancellationToken cancellationToken)
    {
        var best = new Dictionary<string, Best>();
        await using var command = await db.CommandAsync("SELECT puzzle_id, medal, objects, rate, layout FROM puzzles.results WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            best[reader.GetString(0)] = new Best(reader.GetInt32(1), reader.GetInt32(2), reader.GetDouble(3), JsonDocument.Parse(reader.GetString(4)).RootElement.Clone());
        }

        return best;
    }

    public async Task<object> StateAsync(Guid owner, CancellationToken cancellationToken)
    {
        var best = await BestAsync(owner, cancellationToken);
        return new
        {
            solveXp = PuzzleBook.SolveXp,
            goldXp = PuzzleBook.GoldXp,
            beltPerMinute = Solver.BeltPerMinute,
            puzzles = PuzzleBook.All.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                story = p.Story,
                width = p.Width,
                height = p.Height,
                source = new { x = p.Source.X, y = p.Source.Y },
                sink = new { x = p.Sink.X, y = p.Sink.Y },
                input = new { id = p.Input, name = Items.Names[p.Input] },
                output = new { id = p.Output, name = Items.Names[p.Output] },
                target = p.Target,
                gold = p.Gold,
                silver = p.Silver,
                allowed = p.Allowed.Select(a => new
                {
                    id = a.Key,
                    name = Items.Names[a.Key],
                    most = a.Value,
                    size = Footprints.Size(a.Key),
                    perMinute = Solver.IsInserter(a.Key) ? Solver.InserterPerMinute(a.Key) : Solver.IsBelt(a.Key) ? Solver.BeltPerMinute : (double?)null,
                }),
                machine = p.Machine is { } m ? new { id = m.Kind, inPerMinute = m.InPerMinute, outPerIn = m.OutPerIn } : null,
                best = best.TryGetValue(p.Id, out var b) ? new { medal = b.Medal, objects = b.Objects, rate = b.Rate, layout = b.Layout } : null,
            }),
        };
    }

    /// <summary>Judges a layout and keeps it when it is the player's best. Fails with <c>unknown_puzzle</c> or <c>too_many_pieces</c>.</summary>
    public Task<(Evaluation Evaluation, int Medal, bool FirstSolve, bool FirstGold, XpGain? Xp)> SubmitAsync(
        Guid owner, string puzzleId, IReadOnlyList<Piece> pieces, CancellationToken cancellationToken) =>
        db.InTransactionAsync<(Evaluation, int, bool, bool, XpGain?)>(async () =>
        {
            var puzzle = PuzzleBook.Find(puzzleId) ?? throw new GameException("unknown_puzzle", "There is no such puzzle.");
            if (pieces.Count > PuzzleBook.MostPieces)
            {
                throw new GameException("too_many_pieces", $"A layout has at most {PuzzleBook.MostPieces} pieces.");
            }

            var evaluation = Solver.Evaluate(puzzle, pieces);
            var solved = evaluation.Problems.Count == 0 && evaluation.Rate >= puzzle.Target - 1e-6;
            var medal = Solver.Medal(puzzle, solved, pieces.Count);
            if (!solved)
            {
                return (evaluation, 0, false, false, null);
            }

            // One solution of a player is judged at a time, so two sent at once cannot both be the first, even before the row exists.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()) ^ 0x70757a7a_6c650000);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            var before = 0;
            var objectsBefore = int.MaxValue;
            await using (var read = await db.CommandAsync(
                "SELECT medal, objects FROM puzzles.results WHERE owner_id = @owner AND puzzle_id = @puzzle FOR UPDATE", cancellationToken))
            {
                read.Parameters.AddWithValue("owner", owner);
                read.Parameters.AddWithValue("puzzle", puzzle.Id);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    before = reader.GetInt32(0);
                    objectsBefore = reader.GetInt32(1);
                }
            }

            if (before == 0 || pieces.Count < objectsBefore)
            {
                await using var save = await db.CommandAsync(
                    """
                    INSERT INTO puzzles.results (owner_id, puzzle_id, medal, objects, rate, layout, solved_at)
                    VALUES (@owner, @puzzle, @medal, @objects, @rate, @layout::jsonb, @now)
                    ON CONFLICT (owner_id, puzzle_id) DO UPDATE SET medal = GREATEST(results.medal, excluded.medal), objects = excluded.objects,
                        rate = excluded.rate, layout = excluded.layout, solved_at = excluded.solved_at
                    """, cancellationToken);
                save.Parameters.AddWithValue("owner", owner);
                save.Parameters.AddWithValue("puzzle", puzzle.Id);
                save.Parameters.AddWithValue("medal", medal);
                save.Parameters.AddWithValue("objects", pieces.Count);
                save.Parameters.AddWithValue("rate", evaluation.Rate);
                save.Parameters.AddWithValue("layout", JsonSerializer.Serialize(pieces.Select(p => new { kind = p.Kind, x = p.X, y = p.Y, rotation = p.Rotation })));
                save.Parameters.AddWithValue("now", time.GetUtcNow());
                await save.ExecuteNonQueryAsync(cancellationToken);
            }

            var firstSolve = before == 0;
            var firstGold = medal == 3 && before < 3;
            var xp = (firstSolve ? PuzzleBook.SolveXp : 0) + (firstGold ? PuzzleBook.GoldXp : 0);
            return (evaluation, medal, firstSolve, firstGold, xp > 0 ? await progress.AddXpAsync(owner, xp, cancellationToken) : null);
        }, cancellationToken);
}

public sealed class GetPuzzlesHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "puzzle.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("puzzles", await scope.ServiceProvider.GetRequiredService<PuzzleService>().StateAsync(client.RequireAccount(), cancellationToken));
    }
}

public sealed record PieceRequest(string Kind, int X, int Y, int Rotation);

public sealed record SubmitPuzzleRequest(string Puzzle, IReadOnlyList<PieceRequest>? Pieces);

/// <summary>Judges a layout: how much it delivers, what is wrong, and the medal; the best one is kept.</summary>
public sealed class SubmitPuzzleHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "puzzle.submit";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var owner = client.RequireAccount();
        var request = Payload.Read<SubmitPuzzleRequest>(payload);
        var pieces = (request.Pieces ?? []).Select(p => new Piece(p.Kind ?? "", p.X, p.Y, p.Rotation)).ToList();
        await using var scope = scopes.CreateAsyncScope();
        var puzzles = scope.ServiceProvider.GetRequiredService<PuzzleService>();
        var (evaluation, medal, firstSolve, firstGold, xp) = await puzzles.SubmitAsync(owner, request.Puzzle ?? "", pieces, cancellationToken);
        return new Reply("puzzle.result", new
        {
            puzzle = request.Puzzle,
            rate = evaluation.Rate,
            solved = medal > 0,
            medal,
            objects = pieces.Count,
            problems = evaluation.Problems,
            flow = pieces.Select((_, i) => evaluation.Flow.GetValueOrDefault(i)),
            firstSolve,
            firstGold,
            xp = xp is { } gain ? new { xp = gain.Xp, level = gain.Level, leveledUp = gain.LeveledUp } : null,
        });
    }
}

/// <summary>Engineering puzzles. Owns the <c>puzzles</c> schema.</summary>
public sealed class PuzzlesModule : IGameModule
{
    public string Name => "Puzzles";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PuzzlesDbContext>(PuzzlesDbContext.Schema);
        services.AddScoped<PuzzleService>();
        services.AddSingleton<IMessageHandler, GetPuzzlesHandler>();
        services.AddSingleton<IMessageHandler, SubmitPuzzleHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<PuzzlesDbContext>(cancellationToken);
}
