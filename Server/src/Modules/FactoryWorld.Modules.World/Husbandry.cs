using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <param name="Item">The animal as it lies in the inventory, so its id is the item's.</param>
/// <param name="Product">What a fed animal gives once <see cref="Time"/> has passed.</param>
/// <param name="Yield">Products one collection gives.</param>
/// <param name="Time">How long a fed animal takes to give its product on a plot without a husbandry bonus.</param>
/// <param name="Tool">The tool in hand needed to collect the product, or null for bare hands.</param>
public sealed record Animal(string Item, string Product, int Yield, TimeSpan Time, string? Tool, int Xp)
{
    /// <summary>A second product one collection gives too, or null: a camel gives wool besides its milk.</summary>
    public (string Item, int Quantity)? Extra { get; init; }

    /// <summary>Whether it lives only on sand: a camel lives on a desert and in an oasis, and nowhere else.</summary>
    public bool Sandy { get; init; }

    /// <summary>Manure one collection gives besides, for the fields and the biogas plant.</summary>
    public int Manure { get; init; }

    /// <summary>Every how many collections two of a kind on neighbouring tiles give a young one; 0 for never.</summary>
    public int BreedEvery { get; init; }

    /// <summary>What it eats, when not <see cref="Husbandry.Feed"/>: a work horse wants concentrate.</summary>
    public IReadOnlyList<string>? Diet { get; init; }

    /// <summary>Whether it works while fed: a horse hauls logs and ore (see <see cref="Husbandry.HorseBonus"/>).</summary>
    public bool Works { get; init; }

    /// <summary>Everything one collection gives.</summary>
    public IReadOnlyList<(string Item, int Quantity)> Gives =>
        [(Product, Yield), .. Extra is { } extra ? [extra] : Array.Empty<(string, int)>(), .. Manure > 0 && Product != Items.Manure ? [(Items.Manure, Manure)] : Array.Empty<(string, int)>()];

    /// <summary>What it eats.</summary>
    public IReadOnlyList<string> Eats => Diet ?? Husbandry.Feed;
}

/// <summary>
/// Animals bought on the market are let out on a free tile of a pasture (a plot of a biome with
/// grass), fed a few crops and, after their time, give their product: a hen eggs, a sheep wool
/// (cut with shears), a cow milk. A fed animal gives once, then wants feeding again. The steppe
/// and fertile soils raise animals faster; only camels live on a desert and nothing on a glacier. An animal is
/// taken back into the inventory with what it was going to give, if anything, lost.
/// </summary>
public static class Husbandry
{
    /// <summary>Every animal, in the order the client lists them.</summary>
    public static readonly IReadOnlyList<Animal> Animals =
    [
        new(Items.Chicken, Items.Egg, 2, TimeSpan.FromMinutes(4), null, 2) { Manure = 1, BreedEvery = 5 },
        new(Items.Sheep, Items.Wool, 2, TimeSpan.FromMinutes(8), Items.Shears, 5) { Manure = 1, BreedEvery = 8 },
        new(Items.Cow, Items.Milk, 3, TimeSpan.FromMinutes(12), null, 7) { Manure = 2, BreedEvery = 10 },
        new(Items.Camel, Items.Milk, 2, TimeSpan.FromMinutes(10), null, 8) { Extra = (Items.Wool, 1), Sandy = true, BreedEvery = 10 },
        // A work horse lives by a stable, eats concentrate only and, while fed, helps with the work around it.
        new(Items.Horse, Items.Manure, 2, TimeSpan.FromMinutes(10), null, 6) { BreedEvery = 20, Diet = [Items.ConcentrateFeed], Works = true },
        // A pig eats what is left of the kitchen and the distillery and gives meat without the slaughterhouse; a goat gives milk often, a little at a time.
        new(Items.Pig, Items.Meat, 1, TimeSpan.FromMinutes(15), null, 6)
        {
            Manure = 2, BreedEvery = 6, Diet = [Items.Potato, Items.Corn, Items.Stillage, Items.BeetPulp, Items.ConcentrateFeed],
        },
        new(Items.Goat, Items.Milk, 2, TimeSpan.FromMinutes(7), null, 5) { Manure = 1, BreedEvery = 8 },
    ];

    public static readonly IReadOnlyDictionary<string, Animal> ById = Animals.ToDictionary(a => a.Item);

    /// <summary>What any animal eats, <see cref="FeedPerMeal"/> of one kind per feeding: crops, fish meal from the cannery and dates from an oasis.</summary>
    public static readonly IReadOnlyList<string> Feed =
        [Items.Wheat, Items.Corn, Items.Carrot, Items.Cabbage, Items.FishMeal, Items.Dates, Items.ConcentrateFeed, Items.BeetPulp];

    /// <summary>Whatever some animal eats: <see cref="Feed"/> and the diets of the animals that want their own.</summary>
    public static readonly IReadOnlyList<string> AnyFeed = Animals.SelectMany(a => a.Eats).Distinct().ToList();

    public const int FeedPerMeal = 2;

    public const int XpPerRelease = 1;

    /// <summary>How much faster cutting trees and bushes, breaking rocks and working mines goes with a fed work horse at hand.</summary>
    public const double HorseBonus = 1.25;

    /// <summary>How many tiles away from the work a horse still helps.</summary>
    public const int HorseReach = 6;

    /// <summary>Whether a fed (working) horse stands on the plot at <paramref name="now"/>, within <see cref="HorseReach"/> of a tile when one is given.</summary>
    public static async Task<bool> HorseAtWorkAsync(GameDb db, int x, int y, (int X, int Y)? near, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT EXISTS (SELECT 1 FROM world.plot_animals WHERE x = @x AND y = @y AND kind = @kind AND ready_at > @now
                AND (@near = false OR (abs(tile_x - @tx) <= @r AND abs(tile_y - @ty) <= @r)))
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("kind", Items.Horse);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("near", near is not null);
        command.Parameters.AddWithValue("tx", near?.X ?? 0);
        command.Parameters.AddWithValue("ty", near?.Y ?? 0);
        command.Parameters.AddWithValue("r", HorseReach);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The biome's husbandry bonus in percent, as the map lists it: the steppe raises animals 25%
    /// faster, fertile soils 20% faster, and no animal lives on a desert or a glacier (null).
    /// </summary>
    public static int? BonusPercent(Biome biome) => biome switch
    {
        Biome.Grassland => 25,
        Biome.Farmland => 20,
        Biome.Desert or Biome.Ice => null,
        _ => 0,
    };

    /// <summary>Whether animals can be kept on a plot of a biome: camels on a desert too.</summary>
    public static bool Pasture(Biome biome) => Animals.Any(a => Lives(a, biome));

    /// <summary>Whether an animal lives on a plot of a biome: a camel on sand only, the others where <see cref="BonusPercent"/> lets them.</summary>
    public static bool Lives(Animal animal, Biome biome) => animal.Sandy ? Oasis.Sandy((int)biome) : BonusPercent(biome) is not null;

    /// <summary>How long a fed animal takes to give its product on a plot of a biome; null where it does not live.</summary>
    public static TimeSpan? Time(Animal animal, Biome biome) =>
        !Lives(animal, biome) ? null : animal.Sandy ? animal.Time : animal.Time * (100 - BonusPercent(biome)!.Value) / 100.0;
}

/// <param name="ReadyAt">When the animal gives its product; null while it is hungry.</param>
/// <param name="Meals">Collections so far, which count towards a young one.</param>
public sealed record PlotAnimal(int TileX, int TileY, string Kind, DateTimeOffset? ReadyAt, int Meals = 0);

/// <param name="Animal">The animal after the action; null when it was taken off the plot.</param>
/// <param name="Items">What collecting gave; empty otherwise.</param>
/// <param name="Xp">Null when the action gives no experience.</param>
public sealed record AnimalChange(PlotAnimal? Animal, IReadOnlyList<(string Item, int Quantity)> Items, XpGain? Xp);

public sealed class HusbandryService(
    GameDb db, PlotService plots, FeederService feeders, IInventory inventory, ICharacterProgress progress, IProductionStats stats,
    TimeProvider time)
{
    public async Task<IReadOnlyList<PlotAnimal>> AnimalsAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT tile_x, tile_y, kind, ready_at, meals FROM world.plot_animals WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var animals = new List<PlotAnimal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            animals.Add(new PlotAnimal(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4)));
        }

        return animals;
    }

    /// <summary>Lets an animal from the inventory out on a free tile of a pasture.</summary>
    public Task<AnimalChange> ReleaseAsync(Guid player, int x, int y, int tileX, int tileY, string kind, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Husbandry.ById.ContainsKey(kind))
            {
                throw new GameException("unknown_animal", "There is no such animal.");
            }

            var biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            if (!Husbandry.Pasture(biome))
            {
                throw new GameException("no_pasture", "No animal lives on a glacier.");
            }

            if (!Husbandry.Lives(Husbandry.ById[kind], biome))
            {
                throw new GameException(Husbandry.ById[kind].Sandy ? "camel_sand" : "no_pasture",
                    Husbandry.ById[kind].Sandy ? "A camel lives on a desert or in an oasis." : "Only camels live on a desert.");
            }

            if ((tileX, tileY) == PlotLayout.Spawn || !await plots.FreeAsync(player, x, y, tileX, tileY, cancellationToken))
            {
                throw new GameException("blocked", "Something stands here.");
            }

            if (Husbandry.ById[kind].Works && !await HasAsync(x, y, Items.Stable, cancellationToken))
            {
                throw new GameException("no_stable", "A work horse lives on a plot with a stable.");
            }

            await feeders.LockAsync(x, y, cancellationToken);
            await inventory.RemoveAsync(player, kind, 1, cancellationToken);
            await stats.RecordAsync(player, [ProductionEntry.Used(kind, 1, time.GetUtcNow())], cancellationToken);
            await using (var insert = await db.CommandAsync(
                "INSERT INTO world.plot_animals (x, y, tile_x, tile_y, kind) VALUES (@x, @y, @tx, @ty, @kind) ON CONFLICT DO NOTHING", cancellationToken))
            {
                Bind(insert, x, y, tileX, tileY);
                insert.Parameters.AddWithValue("kind", kind);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("blocked", "An animal already stands here.");
                }
            }

            // A feeder in reach feeds it at once.
            var fed = await feeders.FeedAsync(x, y, cancellationToken);
            var xp = await progress.AddXpAsync(player, Husbandry.XpPerRelease, cancellationToken);
            return new AnimalChange(new PlotAnimal(tileX, tileY, kind, fed.TryGetValue((tileX, tileY), out var ready) ? ready : null), [], xp);
        }, cancellationToken);

    /// <summary>Feeds a hungry animal crops from the inventory; it then gives its product after its time.</summary>
    public Task<AnimalChange> FeedAsync(Guid player, int x, int y, int tileX, int tileY, string feed, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Husbandry.AnyFeed.Contains(feed))
            {
                throw new GameException("bad_feed", "Animals eat wheat, corn, carrots, cabbage, fish meal, dates, beet pulp and concentrate.");
            }

            var biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var (kind, readyAt, _) = await LockAsync(x, y, tileX, tileY, cancellationToken);
            if (!Husbandry.ById[kind].Eats.Contains(feed))
            {
                throw new GameException("bad_feed", kind == Items.Horse ? "A work horse eats concentrate feed only." : "It does not eat that.");
            }

            if (readyAt is not null)
            {
                throw new GameException("already_fed", readyAt <= time.GetUtcNow() ? "Collect what it gives first." : "It has eaten already.");
            }

            var animal = Husbandry.ById[kind];
            var ready = time.GetUtcNow() + (Husbandry.Time(animal, biome) ?? animal.Time);
            await inventory.RemoveAsync(player, feed, Husbandry.FeedPerMeal, cancellationToken);
            await stats.RecordAsync(player, [ProductionEntry.Used(feed, Husbandry.FeedPerMeal, time.GetUtcNow())], cancellationToken);
            await using (var update = await db.CommandAsync(
                "UPDATE world.plot_animals SET ready_at = @ready WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty", cancellationToken))
            {
                Bind(update, x, y, tileX, tileY);
                update.Parameters.AddWithValue("ready", ready);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            return new AnimalChange(new PlotAnimal(tileX, tileY, kind, ready), [], null);
        }, cancellationToken);

    /// <summary>Takes what a fed animal gives, with the tool it needs in hand; the animal is hungry again.</summary>
    public Task<AnimalChange> CollectAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            await feeders.LockAsync(x, y, cancellationToken);
            var (kind, readyAt, meals) = await LockAsync(x, y, tileX, tileY, cancellationToken);
            if (readyAt is null)
            {
                throw new GameException("hungry", "Feed it first.");
            }

            if (readyAt > time.GetUtcNow())
            {
                throw new GameException("not_ready", "It has nothing to give yet.");
            }

            var animal = Husbandry.ById[kind];
            if (animal.Tool is { } tool)
            {
                await plots.RequireToolAsync(player, tool, cancellationToken);
            }

            meals++;
            await using (var clear = await db.CommandAsync(
                "UPDATE world.plot_animals SET ready_at = NULL, meals = @meals WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty", cancellationToken))
            {
                Bind(clear, x, y, tileX, tileY);
                clear.Parameters.AddWithValue("meals", meals);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            // Two of a kind side by side have a young one now and then; it goes to the inventory.
            var gives = animal.Gives.ToList();
            if (animal.BreedEvery > 0 && meals % animal.BreedEvery == 0 && await MateAsync(x, y, tileX, tileY, kind, cancellationToken))
            {
                gives.Add((kind, 1));
            }

            foreach (var (item, quantity) in gives)
            {
                await inventory.ReceiveAsync(player, item, quantity, cancellationToken);
            }

            await stats.RecordAsync(player, gives.Select(g => ProductionEntry.Made(g.Item, g.Quantity, time.GetUtcNow())).ToList(), cancellationToken);
            var fed = await feeders.FeedAsync(x, y, cancellationToken);
            var xp = await progress.AddXpAsync(player, animal.Xp, cancellationToken);
            return new AnimalChange(new PlotAnimal(tileX, tileY, kind, fed.TryGetValue((tileX, tileY), out var ready) ? ready : null, meals), gives, xp);
        }, cancellationToken);

    /// <summary>Takes an animal back into the inventory; what it was going to give is lost.</summary>
    public Task<AnimalChange> TakeAsync(Guid player, int x, int y, int tileX, int tileY, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Build);
            var (kind, _, _) = await LockAsync(x, y, tileX, tileY, cancellationToken);
            await using (var delete = await db.CommandAsync(
                "DELETE FROM world.plot_animals WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty", cancellationToken))
            {
                Bind(delete, x, y, tileX, tileY);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await inventory.AddAsync(player, kind, 1, cancellationToken);
            return new AnimalChange(null, [(kind, 1)], null);
        }, cancellationToken);

    /// <summary>The animal on a tile, locked for the transaction; fails with <c>no_animal</c> when there is none.</summary>
    private async Task<(string Kind, DateTimeOffset? ReadyAt, int Meals)> LockAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            "SELECT kind, ready_at, meals FROM world.plot_animals WHERE x = @x AND y = @y AND tile_x = @tx AND tile_y = @ty FOR UPDATE", cancellationToken);
        Bind(select, x, y, tileX, tileY);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new GameException("no_animal", "No animal stands here.");
        }

        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2));
    }

    /// <summary>Whether an animal of the same kind stands on a tile next to (tileX, tileY).</summary>
    private async Task<bool> MateAsync(int x, int y, int tileX, int tileY, string kind, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT EXISTS (SELECT 1 FROM world.plot_animals WHERE x = @x AND y = @y AND kind = @kind
                AND abs(tile_x - @tx) <= 1 AND abs(tile_y - @ty) <= 1 AND (tile_x, tile_y) <> (@tx, @ty))
            """, cancellationToken);
        Bind(command, x, y, tileX, tileY);
        command.Parameters.AddWithValue("kind", kind);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<bool> HasAsync(int x, int y, string kind, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT EXISTS (SELECT 1 FROM world.plot_objects WHERE x = @x AND y = @y AND kind = @kind)", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("kind", kind);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static void Bind(Npgsql.NpgsqlCommand command, int x, int y, int tileX, int tileY)
    {
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("tx", tileX);
        command.Parameters.AddWithValue("ty", tileY);
    }
}

/// <param name="Item">The animal to let out (plot.release) or the crop to feed (plot.feed); unused otherwise.</param>
public sealed record AnimalRequest(int X, int Y, int TileX, int TileY, string? Item);

/// <summary>plot.release, plot.feed, plot.collect and plot.takeAnimal all answer with the changed animal.</summary>
public abstract class AnimalHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<AnimalRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var change = await RunAsync(scope.ServiceProvider.GetRequiredService<HusbandryService>(), client.RequireAccount(), request, cancellationToken);
        return new Reply("plot.animal", new
        {
            tileX = request.TileX,
            tileY = request.TileY,
            animal = change.Animal is { } animal ? AnimalJson(animal) : null,
            items = change.Items.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
            xp = change.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
            serverTime = time.GetUtcNow().ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<AnimalChange> RunAsync(HusbandryService husbandry, Guid player, AnimalRequest request, CancellationToken cancellationToken);

    public static object AnimalJson(PlotAnimal animal) => new
    {
        tileX = animal.TileX,
        tileY = animal.TileY,
        kind = animal.Kind,
        name = Items.Names[animal.Kind],
        readyAt = animal.ReadyAt?.ToUnixTimeMilliseconds(),
        meals = animal.Meals,
    };

    /// <summary>The animals a plot's biome can keep and how long each takes there, for the plot's window.</summary>
    public static IEnumerable<object> KindsJson(Biome biome) => Husbandry.Animals.Select(a => new
    {
        id = a.Item,
        name = Items.Names[a.Item],
        product = a.Product,
        productName = Items.Names[a.Product],
        yield = a.Yield,
        extra = a.Extra is { } extra ? new { id = extra.Item, name = Items.Names[extra.Item], quantity = extra.Quantity } : null,
        manure = a.Product == Items.Manure ? 0 : a.Manure,
        breedEvery = a.BreedEvery,
        eats = a.Eats,
        works = a.Works,
        lives = Husbandry.Lives(a, biome),
        seconds = Husbandry.Time(a, biome)?.TotalSeconds,
        tool = a.Tool,
        toolName = a.Tool is { } tool ? Items.Names[tool] : null,
        xp = a.Xp,
    });
}

public sealed class ReleaseAnimalHandler(IServiceScopeFactory scopes, TimeProvider time) : AnimalHandler(scopes, time)
{
    public override string Type => "plot.release";

    protected override Task<AnimalChange> RunAsync(HusbandryService husbandry, Guid player, AnimalRequest r, CancellationToken cancellationToken) =>
        husbandry.ReleaseAsync(player, r.X, r.Y, r.TileX, r.TileY, r.Item ?? "", cancellationToken);
}

public sealed class FeedAnimalHandler(IServiceScopeFactory scopes, TimeProvider time) : AnimalHandler(scopes, time)
{
    public override string Type => "plot.feed";

    protected override Task<AnimalChange> RunAsync(HusbandryService husbandry, Guid player, AnimalRequest r, CancellationToken cancellationToken) =>
        husbandry.FeedAsync(player, r.X, r.Y, r.TileX, r.TileY, r.Item ?? "", cancellationToken);
}

public sealed class CollectAnimalHandler(IServiceScopeFactory scopes, TimeProvider time) : AnimalHandler(scopes, time)
{
    public override string Type => "plot.collect";

    protected override Task<AnimalChange> RunAsync(HusbandryService husbandry, Guid player, AnimalRequest r, CancellationToken cancellationToken) =>
        husbandry.CollectAsync(player, r.X, r.Y, r.TileX, r.TileY, cancellationToken);
}

public sealed class TakeAnimalHandler(IServiceScopeFactory scopes, TimeProvider time) : AnimalHandler(scopes, time)
{
    public override string Type => "plot.takeAnimal";

    protected override Task<AnimalChange> RunAsync(HusbandryService husbandry, Guid player, AnimalRequest r, CancellationToken cancellationToken) =>
        husbandry.TakeAsync(player, r.X, r.Y, r.TileX, r.TileY, cancellationToken);
}
