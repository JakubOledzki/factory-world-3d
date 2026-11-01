using System.Security.Cryptography;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Recycling;

public static class RecyclingRules
{
    /// <summary>Trash in the bin of a brand new garage.</summary>
    public const int StartingTrash = 50;

    public static readonly TimeSpan SortTime = TimeSpan.FromSeconds(10);

    /// <summary>At the bin the player sorts by hand, one bag at a time: the bag's pieces go one by one into
    /// their bins (<see cref="WasteSorting"/>), and walking away gives the bag back. The automatic
    /// sorting machine is a machine of its own (<see cref="SorterRules"/>) and does not sort at the bin.</summary>
    public const int MaxQueueByHand = 1;

    /// <summary>The pieces of a new bag: random kinds, never all for the same bin.</summary>
    public static IReadOnlyList<string> RollPieces()
    {
        while (true)
        {
            var pieces = Enumerable.Range(0, WasteSorting.Pieces)
                .Select(_ => WasteSorting.Kinds[RandomNumberGenerator.GetInt32(WasteSorting.Kinds.Count)].Id)
                .ToList();
            if (pieces.Select(p => WasteSorting.ById[p].Bin).Distinct().Count() > 1)
            {
                return pieces;
            }
        }
    }

    public const int XpPerSort = 5;

    /// <summary>What one sorting gives, by chance in percent (sums to 100).</summary>
    public static readonly (string Item, int Percent)[] Drops =
    [
        (Items.WastePaper, 30),
        (Items.WastePlastic, 25),
        (Items.WasteAluminium, 20),
        (Items.WasteGlass, 15),
        (Items.WasteCopper, 10),
    ];

    /// <summary>The item for a roll of 0-99.</summary>
    public static string DropForRoll(int roll)
    {
        foreach (var (item, percent) in Drops)
        {
            if (roll < percent)
            {
                return item;
            }

            roll -= percent;
        }

        throw new ArgumentOutOfRangeException(nameof(roll));
    }
}

/// <summary>The garage floor, a grid of square cells, and the furniture that can stand on it.
/// Cells are counted from the front-left corner of the floor: x to the right, y towards the back
/// wall. A piece of furniture stands on whole cells; its position is its front-left cell. Its
/// rotation (0-3) counts quarter turns anticlockwise seen from above; 0 faces the camera.</summary>
public static class GarageLayout
{
    /// <summary>Side of one cell, in metres.</summary>
    public const double CellSize = 0.5;

    public const int Width = 16;
    public const int Depth = 12;

    /// <summary>Cells taken (width along x, depth along y) by everything that can be placed.</summary>
    public static readonly IReadOnlyDictionary<string, (int Width, int Depth)> Footprints =
        new Dictionary<string, (int, int)>
        {
            [Items.TrashBin] = (2, 2),
            [Items.Workbench] = (5, 2),
            [Items.Shelf] = (4, 1),
            [Items.BarrelBlue] = (1, 1),
            [Items.BarrelRed] = (1, 1),
            [Items.Tyres] = (2, 2),
            [Items.Pallet] = (2, 2),
            [Items.GasCylinder] = (1, 1),
            [Items.SortingMachine] = (3, 2),
            [Items.Furnace] = (2, 2),
            [Items.Anvil] = (2, 1),
            [Items.ClothesWardrobe] = (3, 1),
            [Items.TrophyCase] = (3, 1),
            [Items.ResearchTable] = (3, 2),
        };

    /// <summary>How a new garage is furnished.</summary>
    public static readonly (string Kind, int X, int Y)[] Starter =
    [
        (Items.TrashBin, 5, 8),
        (Items.Workbench, 10, 10),
        (Items.GasCylinder, 9, 11),
        (Items.Shelf, 1, 5),
        (Items.BarrelBlue, 15, 5),
        (Items.BarrelRed, 15, 3),
        (Items.Tyres, 12, 1),
        (Items.Pallet, 2, 1),
        (Items.ResearchTable, 1, 10),
    ];

    /// <summary>Cells taken by a piece of furniture turned by `rotation` quarter turns.</summary>
    public static (int Width, int Depth) Size(string kind, int rotation)
    {
        var (w, d) = Footprints[kind];
        return rotation % 2 == 0 ? (w, d) : (d, w);
    }

    /// <summary>Whether a piece of furniture on cell (x, y) fits inside the walls.</summary>
    public static bool InsideRoom(string kind, int x, int y, int rotation = 0)
    {
        var (w, d) = Size(kind, rotation);
        return x >= 0 && y >= 0 && x + w <= Width && y + d <= Depth;
    }

    /// <summary>Where a piece of furniture fits among the others: its spot in the starter layout
    /// if that is free, else the first free cell from the back wall, turned or not; null when none is.</summary>
    public static (int X, int Y, int Rotation)? FreeSpot(string kind, IReadOnlyList<PlacedObject> objects)
    {
        bool Fits(int x, int y, int rotation) =>
            InsideRoom(kind, x, y, rotation) && !objects.Any(o => Overlaps(new PlacedObject(Guid.Empty, kind, x, y, rotation), o));

        foreach (var (starter, sx, sy) in Starter)
        {
            if (starter == kind && Fits(sx, sy, 0))
            {
                return (sx, sy, 0);
            }
        }

        for (var y = Depth - 1; y >= 0; y--)
        {
            for (var x = 0; x < Width; x++)
            {
                for (var rotation = 0; rotation < 2; rotation++)
                {
                    if (Fits(x, y, rotation))
                    {
                        return (x, y, rotation);
                    }
                }
            }
        }

        return null;
    }

    public static bool Overlaps(PlacedObject a, PlacedObject b)
    {
        var (aw, ad) = Size(a.Kind, a.Rotation);
        var (bw, bd) = Size(b.Kind, b.Rotation);
        return a.X < b.X + bw && b.X < a.X + aw && a.Y < b.Y + bd && b.Y < a.Y + ad;
    }
}

public sealed class Garage
{
    public Guid OwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set once the starter furniture has been put in, so it is never put in twice.</summary>
    public bool Furnished { get; set; }

    /// <summary>Set once the garage has its research table: garages furnished before research
    /// moved to its own table get one too, once.</summary>
    public bool HasResearchTable { get; set; }
}

public sealed class GarageObject
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string Kind { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public int Rotation { get; set; }
}

/// <summary>One queued sorting. The trash is taken when queued; the result is rolled when it finishes.</summary>
public sealed class SortingJob
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public DateTimeOffset FinishesAt { get; set; }
}

/// <summary>The bag a player is sorting by hand at the bin: its pieces (kinds, comma-separated, in the order the
/// client shows them) and which of them are in their bins already (bit i for piece i).</summary>
public sealed class SortingRound
{
    public Guid OwnerId { get; set; }

    public string Pieces { get; set; } = "";

    public int Sorted { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

public sealed class RecyclingDbContext(DbContextOptions<RecyclingDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "recycling";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Garage>(e =>
        {
            e.ToTable("garages");
            e.HasKey(g => g.OwnerId);
            e.Property(g => g.OwnerId).HasColumnName("owner_id");
            e.Property(g => g.CreatedAt).HasColumnName("created_at");
            e.Property(g => g.Furnished).HasColumnName("furnished");
            e.Property(g => g.HasResearchTable).HasColumnName("research_table");
        });
        model.Entity<GarageObject>(e =>
        {
            e.ToTable("garage_objects");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.OwnerId).HasColumnName("owner_id");
            e.Property(o => o.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(o => o.X).HasColumnName("x");
            e.Property(o => o.Y).HasColumnName("y");
            e.Property(o => o.Rotation).HasColumnName("rotation");
            e.HasIndex(o => o.OwnerId);
        });
        model.Entity<Sorter>(e =>
        {
            e.ToTable("sorters");
            e.HasKey(s => s.ObjectId);
            e.Property(s => s.ObjectId).HasColumnName("object_id");
            e.Property(s => s.OwnerId).HasColumnName("owner_id");
            e.Property(s => s.Queued).HasColumnName("queued");
            e.Property(s => s.StartedAt).HasColumnName("started_at");
        });
        model.Entity<SorterItem>(e =>
        {
            e.ToTable("sorter_items");
            e.HasKey(i => new { i.ObjectId, i.Item });
            e.Property(i => i.ObjectId).HasColumnName("object_id");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(i => i.Quantity).HasColumnName("quantity");
        });
        model.Entity<GarageUpgrade>(e =>
        {
            e.ToTable("garage_upgrades");
            e.HasKey(u => new { u.OwnerId, u.Track });
            e.Property(u => u.OwnerId).HasColumnName("owner_id");
            e.Property(u => u.Track).HasColumnName("track").HasMaxLength(16);
            e.Property(u => u.Level).HasColumnName("level");
        });
        model.Entity<GarageItem>(e =>
        {
            e.ToTable("garage_items");
            e.HasKey(i => new { i.HolderId, i.Item });
            e.Property(i => i.HolderId).HasColumnName("holder_id");
            e.Property(i => i.OwnerId).HasColumnName("owner_id");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(i => i.Quantity).HasColumnName("quantity");
        });
        model.Entity<SavedOutfit>(e =>
        {
            e.ToTable("outfits");
            e.HasKey(o => new { o.OwnerId, o.Number });
            e.Property(o => o.OwnerId).HasColumnName("owner_id");
            e.Property(o => o.Number).HasColumnName("number");
            e.Property(o => o.Name).HasColumnName("name").HasMaxLength(WardrobeRules.MaxNameLength);
            e.Property(o => o.Head).HasColumnName("head").HasMaxLength(32);
            e.Property(o => o.Body).HasColumnName("body").HasMaxLength(32);
            e.Property(o => o.Legs).HasColumnName("legs").HasMaxLength(32);
            e.Property(o => o.Feet).HasColumnName("feet").HasMaxLength(32);
        });
        model.Entity<SortingJob>(e =>
        {
            e.ToTable("sorting_jobs");
            e.Property(j => j.Id).HasColumnName("id");
            e.Property(j => j.OwnerId).HasColumnName("owner_id");
            e.Property(j => j.FinishesAt).HasColumnName("finishes_at");
            e.HasIndex(j => new { j.OwnerId, j.FinishesAt });
        });
        model.Entity<SortingRound>(e =>
        {
            e.ToTable("sorting_rounds");
            e.HasKey(r => r.OwnerId);
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.Pieces).HasColumnName("pieces").HasMaxLength(256);
            e.Property(r => r.Sorted).HasColumnName("sorted");
            e.Property(r => r.StartedAt).HasColumnName("started_at");
        });
    }
}

public sealed record PlacedObject(Guid Id, string Kind, int X, int Y, int Rotation = 0);

public sealed record GarageState(
    long Trash,
    IReadOnlyList<DateTimeOffset> Queue,
    DateTimeOffset ServerTime,
    IReadOnlyList<string> Results,
    XpGain? Xp,
    IReadOnlyList<PlacedObject> Objects,
    IReadOnlyDictionary<string, int> Upgrades,
    Round? Round = null,
    PutResult? Put = null);

/// <summary>The bag being sorted by hand: its pieces' kinds and which are in their bins.</summary>
public sealed record Round(IReadOnlyList<string> Pieces, int Sorted)
{
    public bool IsSorted(int piece) => (Sorted & (1 << piece)) != 0;

    public bool Done => Enumerable.Range(0, Pieces.Count).All(IsSorted);
}

/// <summary>Where the last piece went: into its bin, or back out of the wrong one.</summary>
public sealed record PutResult(int Piece, string Bin, bool Correct);

public sealed class RecyclingService(
    GameDb db, IInventory inventory, ICharacterProgress progress, IEnumerable<IMachineContents> machines, IProductionStats stats, TimeProvider time)
{
    /// <summary>The garage as it is now, after finishing every sorting whose time has come.</summary>
    public Task<GarageState> StateAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await EnsureGarageAsync(owner, cancellationToken);
            var (results, xp) = await CompleteDueAsync(owner, cancellationToken);
            return await ReadStateAsync(owner, results, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Opens a bag of trash at the bin (<paramref name="count"/> must be 1): its pieces wait to be put into their bins.</summary>
    public Task<GarageState> SortAsync(Guid owner, int count, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (count is < 1 or > RecyclingRules.MaxQueueByHand)
            {
                throw new GameException("bad_quantity", $"By hand you sort {RecyclingRules.MaxQueueByHand} bag at a time.");
            }

            await EnsureGarageAsync(owner, cancellationToken);
            var (results, xp) = await CompleteDueAsync(owner, cancellationToken);

            // Lock the garage row so two requests cannot both fill the last queue slots.
            await LockGarageAsync(owner, cancellationToken);

            var objects = await ObjectsAsync(owner, cancellationToken);
            if (!objects.Any(o => o.Kind == Items.TrashBin))
            {
                throw new GameException("no_bin", "Put the trash bin in the garage first.");
            }

            if ((await QueueAsync(owner, cancellationToken)).Count > 0 || await RoundAsync(owner, cancellationToken) is not null)
            {
                throw new GameException("queue_full", "A bag is being sorted already.");
            }

            await inventory.RemoveAsync(owner, Items.Trash, count, cancellationToken);
            await using (var insert = await db.CommandAsync(
                "INSERT INTO recycling.sorting_rounds (owner_id, pieces, sorted, started_at) VALUES (@owner, @pieces, 0, @now)", cancellationToken))
            {
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("pieces", string.Join(',', RecyclingRules.RollPieces()));
                insert.Parameters.AddWithValue("now", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadStateAsync(owner, results, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts piece <paramref name="piece"/> of the open bag into <paramref name="bin"/>. Into the wrong bin it
    /// comes back; once every piece is in its bin, the bag is sorted and gives what a bag gives.</summary>
    public Task<GarageState> PutAsync(Guid owner, int piece, string bin, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!WasteSorting.Bins.Any(b => b.Id == bin))
            {
                throw new GameException("bad_bin", "There is no such bin.");
            }

            await EnsureGarageAsync(owner, cancellationToken);
            var (results, xp) = await CompleteDueAsync(owner, cancellationToken);
            await LockGarageAsync(owner, cancellationToken);
            var round = await RoundAsync(owner, cancellationToken)
                ?? throw new GameException("no_bag", "No bag is open at the bin.");
            if (piece < 0 || piece >= round.Pieces.Count || round.IsSorted(piece))
            {
                throw new GameException("bad_piece", "There is no such piece left in the bag.");
            }

            var correct = WasteSorting.ById[round.Pieces[piece]].Bin == bin;
            if (correct)
            {
                round = round with { Sorted = round.Sorted | (1 << piece) };
                if (round.Done)
                {
                    await using (var delete = await db.CommandAsync("DELETE FROM recycling.sorting_rounds WHERE owner_id = @owner", cancellationToken))
                    {
                        delete.Parameters.AddWithValue("owner", owner);
                        await delete.ExecuteNonQueryAsync(cancellationToken);
                    }

                    (results, xp) = await GiveSortedAsync(owner, [time.GetUtcNow()], cancellationToken);
                }
                else
                {
                    await using var update = await db.CommandAsync(
                        "UPDATE recycling.sorting_rounds SET sorted = @sorted WHERE owner_id = @owner", cancellationToken);
                    update.Parameters.AddWithValue("owner", owner);
                    update.Parameters.AddWithValue("sorted", round.Sorted);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            return await ReadStateAsync(owner, results, xp, cancellationToken, new PutResult(piece, bin, correct));
        }, cancellationToken);

    /// <summary>The player walked away from the bin: the bag being sorted by hand is not finished
    /// and goes back into the inventory, the pieces sorted already with it.</summary>
    public Task<GarageState> StopAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await EnsureGarageAsync(owner, cancellationToken);
            var (results, xp) = await CompleteDueAsync(owner, cancellationToken);
            await LockGarageAsync(owner, cancellationToken);
            await using (var command = await db.CommandAsync(
                "DELETE FROM recycling.sorting_jobs WHERE owner_id = @owner AND finishes_at > @now", cancellationToken))
            {
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("now", time.GetUtcNow());
                var stopped = await command.ExecuteNonQueryAsync(cancellationToken);
                await using var open = await db.CommandAsync("DELETE FROM recycling.sorting_rounds WHERE owner_id = @owner", cancellationToken);
                open.Parameters.AddWithValue("owner", owner);
                stopped += await open.ExecuteNonQueryAsync(cancellationToken);
                if (stopped > 0)
                {
                    await inventory.AddAsync(owner, Items.Trash, stopped, cancellationToken);
                }
            }

            return await ReadStateAsync(owner, results, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts a piece of furniture from the inventory on the floor, its front-left corner on cell (x, y).</summary>
    public Task<GarageState> PlaceAsync(Guid owner, string kind, int x, int y, int rotation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!GarageLayout.Footprints.ContainsKey(kind))
            {
                throw new GameException("not_placeable", "This item cannot be placed.");
            }

            if (rotation is < 0 or > 3 || !GarageLayout.InsideRoom(kind, x, y, rotation))
            {
                throw new GameException("bad_position", "It does not fit inside the garage there.");
            }

            await EnsureGarageAsync(owner, cancellationToken);
            var (results, xp) = await CompleteDueAsync(owner, cancellationToken);
            await LockGarageAsync(owner, cancellationToken);
            var placed = new PlacedObject(Guid.Empty, kind, x, y, rotation);
            if ((await ObjectsAsync(owner, cancellationToken)).Any(o => GarageLayout.Overlaps(placed, o)))
            {
                throw new GameException("blocked", "Something else already stands there.");
            }

            await inventory.RemoveAsync(owner, kind, 1, cancellationToken);
            await InsertObjectAsync(owner, kind, x, y, rotation, cancellationToken);
            return await ReadStateAsync(owner, results, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes a piece of furniture off the floor into the inventory.</summary>
    public Task<GarageState> PickUpAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await EnsureGarageAsync(owner, cancellationToken);
            var (results, xp) = await CompleteDueAsync(owner, cancellationToken);
            await using var command = await db.CommandAsync(
                "DELETE FROM recycling.garage_objects WHERE id = @id AND owner_id = @owner RETURNING kind", cancellationToken);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("owner", owner);
            var kind = await command.ExecuteScalarAsync(cancellationToken) as string
                ?? throw new GameException("no_object", "There is no such object in your garage.");
            foreach (var machine in machines)
            {
                await machine.ReleaseAsync(owner, id, cancellationToken);
            }

            await inventory.AddAsync(owner, kind, 1, cancellationToken);
            return await ReadStateAsync(owner, results, xp, cancellationToken);
        }, cancellationToken);

    private async Task EnsureGarageAsync(Guid owner, CancellationToken cancellationToken)
    {
        // Nearly every garage has long had all of it: one read instead of three writes that change nothing.
        await using (var ready = await db.CommandAsync(
            "SELECT furnished AND research_table FROM recycling.garages WHERE owner_id = @owner", cancellationToken))
        {
            ready.Parameters.AddWithValue("owner", owner);
            if (await ready.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }
        }

        await using (var command = await db.CommandAsync(
            "INSERT INTO recycling.garages (owner_id, created_at) VALUES (@owner, @at) ON CONFLICT DO NOTHING", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("at", time.GetUtcNow());
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await inventory.AddAsync(owner, Items.Trash, RecyclingRules.StartingTrash, cancellationToken);
            }
        }

        // Garages made before furniture existed get theirs here too, exactly once. The starter
        // furniture has the research table in it already.
        await using (var furnish = await db.CommandAsync(
            "UPDATE recycling.garages SET furnished = TRUE, research_table = TRUE WHERE owner_id = @owner AND NOT furnished", cancellationToken))
        {
            furnish.Parameters.AddWithValue("owner", owner);
            if (await furnish.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                foreach (var (kind, x, y) in GarageLayout.Starter)
                {
                    await InsertObjectAsync(owner, kind, x, y, 0, cancellationToken);
                }
            }
        }

        // Garages furnished before research moved off the workbench get a research table, once:
        // on its starter spot or the first free one, or into the inventory when the floor is full.
        await using var give = await db.CommandAsync(
            "UPDATE recycling.garages SET research_table = TRUE WHERE owner_id = @owner AND NOT research_table", cancellationToken);
        give.Parameters.AddWithValue("owner", owner);
        if (await give.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            var objects = await ObjectsAsync(owner, cancellationToken);
            if (GarageLayout.FreeSpot(Items.ResearchTable, objects) is var (x, y, rotation))
            {
                await InsertObjectAsync(owner, Items.ResearchTable, x, y, rotation, cancellationToken);
            }
            else
            {
                await inventory.AddAsync(owner, Items.ResearchTable, 1, cancellationToken);
            }
        }
    }

    private async Task LockGarageAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT 1 FROM recycling.garages WHERE owner_id = @owner FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertObjectAsync(Guid owner, string kind, int x, int y, int rotation, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "INSERT INTO recycling.garage_objects (id, owner_id, kind, x, y, rotation) VALUES (@id, @owner, @kind, @x, @y, @rotation)", cancellationToken);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("rotation", rotation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<PlacedObject>> ObjectsAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT id, kind, x, y, rotation FROM recycling.garage_objects WHERE owner_id = @owner ORDER BY y DESC, x", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<PlacedObject>();
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new PlacedObject(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        }

        return objects;
    }

    private async Task<(IReadOnlyList<string> Results, XpGain? Xp)> CompleteDueAsync(Guid owner, CancellationToken cancellationToken)
    {
        // DELETE ... RETURNING hands each finished job to exactly one request, so a result is never given twice.
        await using var command = await db.CommandAsync(
            "DELETE FROM recycling.sorting_jobs WHERE owner_id = @owner AND finishes_at <= @now RETURNING finishes_at", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var finishedAt = new List<DateTimeOffset>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                finishedAt.Add(reader.GetFieldValue<DateTimeOffset>(0));
            }
        }

        return finishedAt.Count == 0 ? ([], null) : await GiveSortedAsync(owner, finishedAt, cancellationToken);
    }

    /// <summary>What sorted bags give, one roll each, sorted at <paramref name="finishedAt"/>: the waste, and XP.</summary>
    private async Task<(IReadOnlyList<string> Results, XpGain? Xp)> GiveSortedAsync(
        Guid owner, IReadOnlyList<DateTimeOffset> finishedAt, CancellationToken cancellationToken)
    {
        var finished = finishedAt.Count;
        var results = Enumerable.Range(0, finished)
            .Select(_ => RecyclingRules.DropForRoll(RandomNumberGenerator.GetInt32(100)))
            .ToList();
        foreach (var group in results.GroupBy(r => r))
        {
            await inventory.AddAsync(owner, group.Key, group.Count(), cancellationToken);
        }

        await stats.RecordAsync(owner, finishedAt.SelectMany((at, i) => new[]
        {
            ProductionEntry.Used(Items.Trash, 1, at),
            ProductionEntry.Made(results[i], 1, at),
        }).ToList(), cancellationToken);

        var xp = await progress.AddXpAsync(owner, finished * RecyclingRules.XpPerSort, cancellationToken);
        return (results, xp);
    }

    private async Task<List<DateTimeOffset>> QueueAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT finishes_at FROM recycling.sorting_jobs WHERE owner_id = @owner ORDER BY finishes_at", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var queue = new List<DateTimeOffset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            queue.Add(reader.GetFieldValue<DateTimeOffset>(0));
        }

        return queue;
    }

    private async Task<Round?> RoundAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT pieces, sorted FROM recycling.sorting_rounds WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new Round(reader.GetString(0).Split(','), reader.GetInt32(1)) : null;
    }

    private async Task<GarageState> ReadStateAsync(
        Guid owner, IReadOnlyList<string> results, XpGain? xp, CancellationToken cancellationToken, PutResult? put = null) =>
        new(await inventory.CountAsync(owner, Items.Trash, cancellationToken),
            await QueueAsync(owner, cancellationToken),
            time.GetUtcNow(),
            results,
            xp,
            await ObjectsAsync(owner, cancellationToken),
            await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken),
            await RoundAsync(owner, cancellationToken),
            put);
}

/// <summary>Furniture standing in the player's garage, for other modules (e.g. the workbench to craft at).</summary>
public sealed class GarageObjects(GameDb db) : IPlacedObjects
{
    public async Task<string?> KindAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT kind FROM recycling.garage_objects WHERE id = @id AND owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}

public sealed record SortRequest(int Count);

public sealed record PlaceRequest(string Kind, int X, int Y, int Rotation = 0);

public sealed record PickUpRequest(Guid Id);

public abstract class GarageHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<RecyclingService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("garage", new
        {
            trash = state.Trash,
            // Unix milliseconds: the client compares them with serverTime, not its own clock.
            queue = state.Queue.Select(t => t.ToUnixTimeMilliseconds()),
            serverTime = state.ServerTime.ToUnixTimeMilliseconds(),
            sortSeconds = GarageUpgrades.SortTime(state.Upgrades).TotalSeconds,
            maxQueue = RecyclingRules.MaxQueueByHand,
            results = state.Results.Select(id => new { id, name = Items.Names[id] }),
            xp = state.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
            room = new { width = GarageLayout.Width, depth = GarageLayout.Depth, cellSize = GarageLayout.CellSize },
            objects = state.Objects.Select(o => new { id = o.Id, kind = o.Kind, x = o.X, y = o.Y, rotation = o.Rotation }),
            furniture = GarageLayout.Footprints.ToDictionary(
                f => f.Key, f => new { name = Items.Names[f.Key], width = f.Value.Width, depth = f.Value.Depth }),
            // Level of each upgrade track, which the client draws: the floor, the walls and the light.
            upgrades = GarageUpgrades.Tracks.ToDictionary(t => t.Id, t => state.Upgrades.GetValueOrDefault(t.Id)),
            rank = GarageUpgrades.Rank(state.Upgrades),
            // The bag open at the bin: its pieces in order, each until it is in its bin; null when none is open.
            round = state.Round is { } round
                ? new
                {
                    pieces = round.Pieces.Select((kind, i) => new { index = i, kind, name = WasteSorting.ById[kind].Name, sorted = round.IsSorted(i) }),
                    left = round.Pieces.Count - round.Pieces.Where((_, i) => round.IsSorted(i)).Count(),
                }
                : null,
            bins = WasteSorting.Bins.Select(b => new { id = b.Id, name = b.Name }),
            // The piece just put into a bin, and whether that was its bin (if not, it is back in the bag).
            put = state.Put is { } put ? new { piece = put.Piece, bin = put.Bin, correct = put.Correct } : null,
        });
    }

    protected abstract Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>The garage's state; also collects finished sortings, so the client calls it when a timer runs out.</summary>
public sealed class GetGarageHandler(IServiceScopeFactory scopes) : GarageHandler(scopes)
{
    public override string Type => "garage.get";

    protected override Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        recycling.StateAsync(owner, cancellationToken);
}

public sealed class SortHandler(IServiceScopeFactory scopes) : GarageHandler(scopes)
{
    public override string Type => "recycling.sort";

    protected override Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        recycling.SortAsync(owner, Payload.Read<SortRequest>(payload).Count, cancellationToken);
}

public sealed record PutRequest(int Piece, string? Bin);

/// <summary>A piece of the bag open at the bin goes into a bin; the server says whether it was the right one.</summary>
public sealed class PutHandler(IServiceScopeFactory scopes) : GarageHandler(scopes)
{
    public override string Type => "recycling.put";

    protected override Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PutRequest>(payload);
        return recycling.PutAsync(owner, request.Piece, request.Bin ?? "", cancellationToken);
    }
}

public sealed class StopHandler(IServiceScopeFactory scopes) : GarageHandler(scopes)
{
    public override string Type => "recycling.stop";

    protected override Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        recycling.StopAsync(owner, cancellationToken);
}

public sealed class PlaceHandler(IServiceScopeFactory scopes) : GarageHandler(scopes)
{
    public override string Type => "garage.place";

    protected override Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlaceRequest>(payload);
        return recycling.PlaceAsync(owner, request.Kind ?? "", request.X, request.Y, request.Rotation, cancellationToken);
    }
}

public sealed class PickUpHandler(IServiceScopeFactory scopes) : GarageHandler(scopes)
{
    public override string Type => "garage.pickup";

    protected override Task<GarageState> RunAsync(RecyclingService recycling, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        recycling.PickUpAsync(owner, Payload.Read<PickUpRequest>(payload).Id, cancellationToken);
}

/// <summary>The garage with its furniture, trash bin, sorting by hand and the automatic sorting machine,
/// the shelf, the delivery pallet, the wardrobe and the garage's upgrades. Owns the <c>recycling</c> schema.</summary>
public sealed class RecyclingModule : IGameModule
{
    public string Name => "Recycling";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<RecyclingDbContext>(RecyclingDbContext.Schema);
        services.AddScoped<RecyclingService>();
        services.AddScoped<IPlacedObjects, GarageObjects>();
        services.AddScoped<SorterService>();
        services.AddScoped<IMachineContents, SorterContents>();
        services.AddScoped<GarageUpgradeService>();
        services.AddScoped<IPlayerBonuses, GarageBonuses>();
        services.AddScoped<GarageStorageService>();
        services.AddScoped<IMachineContents, GarageStorageContents>();
        services.AddScoped<IGarageDeliveries, PalletDeliveries>();
        services.AddScoped<WardrobeService>();
        services.AddSingleton<IMessageHandler, GetGarageHandler>();
        services.AddSingleton<IMessageHandler, SortHandler>();
        services.AddSingleton<IMessageHandler, PutHandler>();
        services.AddSingleton<IMessageHandler, StopHandler>();
        services.AddSingleton<IMessageHandler, PlaceHandler>();
        services.AddSingleton<IMessageHandler, PickUpHandler>();
        services.AddSingleton<IMessageHandler, GetSorterHandler>();
        services.AddSingleton<IMessageHandler, LoadSorterHandler>();
        services.AddSingleton<IMessageHandler, TakeSorterHandler>();
        services.AddSingleton<IMessageHandler, GetUpgradesHandler>();
        services.AddSingleton<IMessageHandler, BuyUpgradeHandler>();
        services.AddSingleton<IMessageHandler, GetGarageStorageHandler>();
        services.AddSingleton<IMessageHandler, PutGarageStorageHandler>();
        services.AddSingleton<IMessageHandler, TakeGarageStorageHandler>();
        services.AddSingleton<IMessageHandler, GetWardrobeHandler>();
        services.AddSingleton<IMessageHandler, PutWardrobeHandler>();
        services.AddSingleton<IMessageHandler, TakeWardrobeHandler>();
        services.AddSingleton<IMessageHandler, SaveOutfitHandler>();
        services.AddSingleton<IMessageHandler, ForgetOutfitHandler>();
        services.AddSingleton<IMessageHandler, WearOutfitHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<RecyclingDbContext>(cancellationToken);
}
