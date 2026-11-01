using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Recycling;

/// <summary>One level of an upgrade: what it is called, what it costs and what must be done first
/// (besides the level before it on the same track).</summary>
public sealed record UpgradeStep(
    string Name, long PriceCents, IReadOnlyList<(string Item, int Quantity)> Materials, IReadOnlyList<(string Track, int Level)> Requires);

/// <summary>A way to upgrade the garage, bought level by level: each level costs money and materials
/// and gives <see cref="PerLevel"/> more of <see cref="Bonus"/>.</summary>
/// <param name="Start">What the garage has before the first level.</param>
/// <param name="Bonus">An industry (<see cref="Industries"/>), <see cref="PlayerBonus.Xp"/>,
/// <see cref="GarageUpgrades.ShelfSlots"/> or <see cref="GarageUpgrades.Sorting"/>.</param>
/// <param name="BonusName">How the client names the bonus, followed by the amount and <paramref name="Unit"/>.</param>
public sealed record UpgradeTrack(
    string Id, string Name, string Start, string Bonus, int PerLevel, string BonusName, string Unit, IReadOnlyList<UpgradeStep> Steps)
{
    public int MaxLevel => Steps.Count;
}

/// <summary>
/// The garage is upgraded along five tracks, from a dirty garage up to an office with glass walls
/// over the city. A level is bought only after the one before it, sometimes only once another
/// track is far enough too. Every level gives its track's bonus: faster crafting (floor), better
/// prices at the trader (walls), more experience (lighting), more room on the shelf (shelves) and
/// faster sorting of trash (heating).
/// </summary>
public static class GarageUpgrades
{
    public const string Floor = "floor";
    public const string Walls = "walls";
    public const string Lighting = "lighting";
    public const string Shelves = "shelves";
    public const string Heating = "heating";

    /// <summary>Extra slots of the shelf in the garage.</summary>
    public const string ShelfSlots = "shelf_slots";

    /// <summary>Percent less time to sort a bag of trash, by hand and in the sorting machine.</summary>
    public const string Sorting = "sorting";

    private static UpgradeStep Step(string name, long dollars, (string, int)[] materials, params (string, int)[] requires) =>
        new(name, dollars * 100, materials, requires);

    public static readonly IReadOnlyList<UpgradeTrack> Tracks =
    [
        new(Floor, "Podłoga", "Surowy beton w plamach", Industries.Crafting, 1, "Wytwarzanie szybsze o", "%",
        [
            Step("Oczyszczony beton", 15_000, [(Items.Sand, 200)]),
            Step("Beton szlifowany", 30_000, [(Items.Stone, 200), (Items.Sand, 100)]),
            Step("Posadzka żywiczna", 50_000, [(Items.Cement, 100), (Items.Paint, 50)]),
            Step("Płytki warsztatowe", 80_000, [(Items.Clay, 300), (Items.Cement, 150)]),
            Step("Gres", 130_000, [(Items.StoneBlocks, 200), (Items.Cement, 200)]),
            Step("Panele", 200_000, [(Items.Plywood, 400)]),
            Step("Deski sosnowe", 300_000, [(Items.Planks, 600), (Items.Nails, 300)]),
            Step("Parkiet dębowy", 450_000, [(Items.OakWood, 400), (Items.CaseinGlue, 100)]),
            Step("Lastryko", 700_000, [(Items.MarbleSlabs, 200), (Items.Concrete, 300)]),
            Step("Marmur", 1_200_000, [(Items.MarbleSlabs, 400), (Items.Cement, 200)]),
        ]),
        new(Walls, "Ściany", "Gołe pustaki", Industries.Trade, 1, "Handlarz płaci więcej o", "%",
        [
            Step("Tynk", 20_000, [(Items.Cement, 200), (Items.Sand, 200)]),
            Step("Białe ściany", 35_000, [(Items.Paint, 50)]),
            Step("Boazeria", 60_000, [(Items.Planks, 300), (Items.Nails, 200)]),
            Step("Płytki do połowy", 90_000, [(Items.Clay, 300), (Items.Cement, 150)]),
            Step("Cegła klinkierowa", 140_000, [(Items.Bricks, 1200), (Items.Cement, 200)]),
            Step("Dębowe panele", 220_000, [(Items.OakWood, 300), (Items.Paint, 100)], (Lighting, 2)),
            Step("Duże okna", 330_000, [(Items.WindowPane, 40), (Items.SteelIngot, 100)], (Lighting, 3)),
            Step("Beton architektoniczny", 500_000, [(Items.InsulationBoard, 100), (Items.PrecastConcrete, 100)], (Floor, 6)),
            Step("Przeszklenie", 800_000, [(Items.WindowPane, 100), (Items.GlassLaminate, 100), (Items.SteelIngot, 100)], (Floor, 7)),
            Step("Biuro z widokiem na miasto", 1_500_000, [(Items.WindowPane, 200), (Items.GlassLaminate, 200), (Items.SteelIngot, 200)],
                (Floor, 8), (Lighting, 5)),
        ]),
        new(Lighting, "Oświetlenie", "Goła żarówka", PlayerBonus.Xp, 2, "Doświadczenie większe o", "%",
        [
            Step("Świetlówki", 20_000, [(Items.Glass, 50), (Items.CopperIngot, 50)]),
            Step("Lampy warsztatowe", 60_000, [(Items.Glass, 100), (Items.ElectricWire, 100)]),
            Step("Panele LED", 150_000, [(Items.CircuitBoard, 50), (Items.ElectricWire, 100)]),
            Step("Oświetlenie biurowe", 400_000, [(Items.CircuitBoard, 100), (Items.GlassLaminate, 100)]),
            Step("Inteligentne oświetlenie", 900_000, [(Items.CircuitBoard, 200), (Items.Battery, 20)]),
        ]),
        new(Shelves, "Regały", "Drewniany regał", ShelfSlots, 8, "Regał mieści więcej o", " miejsc",
        [
            Step("Dodatkowe półki", 30_000, [(Items.Planks, 200), (Items.Nails, 100)]),
            Step("Regał metalowy", 80_000, [(Items.IronIngot, 100), (Items.Nails, 200)]),
            Step("Szuflady na drobiazgi", 160_000, [(Items.Plywood, 200), (Items.SteelIngot, 50)]),
            Step("Regał wysokiego składowania", 350_000, [(Items.SteelIngot, 150)]),
            Step("System magazynowy", 700_000, [(Items.SteelIngot, 200), (Items.CircuitBoard, 100)]),
        ]),
        new(Heating, "Ogrzewanie", "Zimny garaż", Sorting, 8, "Segregacja szybsza o", "%",
        [
            Step("Koza na drewno", 25_000, [(Items.Bricks, 200), (Items.IronIngot, 100)]),
            Step("Kaloryfery", 70_000, [(Items.IronIngot, 150), (Items.CopperIngot, 50)]),
            Step("Ocieplenie", 150_000, [(Items.InsulationBoard, 100)]),
            Step("Grzejnik elektryczny", 300_000, [(Items.Heater, 10), (Items.ElectricWire, 100)]),
            Step("Pompa ciepła", 700_000, [(Items.Heater, 20), (Items.Coil, 50)]),
        ]),
    ];

    public static UpgradeTrack? Find(string id) => Tracks.FirstOrDefault(t => t.Id == id);

    /// <summary>What the garage is called, by how far it is upgraded: an office once the walls are glass.</summary>
    public static string Rank(IReadOnlyDictionary<string, int> levels)
    {
        if (levels.GetValueOrDefault(Walls) >= Find(Walls)!.MaxLevel)
        {
            return "Biuro";
        }

        return levels.Values.Sum() switch
        {
            >= 24 => "Studio",
            >= 16 => "Pracownia",
            >= 8 => "Warsztat",
            _ => "Garaż",
        };
    }

    /// <summary>The bonus the levels give, by bonus key; keys no track gives are left out.</summary>
    public static int Bonus(IReadOnlyDictionary<string, int> levels, string bonus) =>
        Tracks.Where(t => t.Bonus == bonus).Sum(t => levels.GetValueOrDefault(t.Id) * t.PerLevel);

    /// <summary>How long sorting one bag takes with this much heating.</summary>
    public static TimeSpan SortTime(IReadOnlyDictionary<string, int> levels) =>
        RecyclingRules.SortTime * (100 - Bonus(levels, Sorting)) / 100;

    /// <summary>Slots of the shelf in the garage.</summary>
    public static int ShelfSize(IReadOnlyDictionary<string, int> levels) => GarageStorageRules.ShelfSlots + Bonus(levels, ShelfSlots);
}

/// <summary>A row of <c>recycling.garage_upgrades</c>: the level a player bought on one track.</summary>
public sealed class GarageUpgrade
{
    public Guid OwnerId { get; set; }

    public string Track { get; set; } = "";

    public int Level { get; set; }
}

internal static class GarageUpgradeRows
{
    /// <summary>Level by track; tracks never upgraded are left out.</summary>
    public static async Task<IReadOnlyDictionary<string, int>> LevelsAsync(GameDb db, Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT track, level FROM recycling.garage_upgrades WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var levels = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            levels[reader.GetString(0)] = reader.GetInt32(1);
        }

        return levels;
    }
}

public sealed record UpgradesState(IReadOnlyDictionary<string, int> Levels, long BalanceCents);

public sealed class GarageUpgradeService(GameDb db, IInventory inventory, IWallet wallet, SorterService sorters)
{
    public Task<UpgradesState> StateAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () => new UpgradesState(
            await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken),
            await wallet.BalanceAsync(owner, cancellationToken)), cancellationToken);

    /// <summary>Buys the next level of a track: money and materials out, the level in, all or nothing.</summary>
    public Task<UpgradesState> BuyAsync(Guid owner, string trackId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var track = GarageUpgrades.Find(trackId ?? "") ?? throw new GameException("no_upgrade", "There is no such upgrade.");

            // Lock the garage so two purchases at once cannot both buy the same level.
            await using (var command = await db.CommandAsync(
                "SELECT 1 FROM recycling.garages WHERE owner_id = @owner FOR UPDATE", cancellationToken))
            {
                command.Parameters.AddWithValue("owner", owner);
                if (await command.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new GameException("no_garage", "Open your garage first.");
                }
            }

            var levels = await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken);
            var level = levels.GetValueOrDefault(track.Id);
            if (level >= track.MaxLevel)
            {
                throw new GameException("max_level", "This is upgraded as far as it goes.");
            }

            var step = track.Steps[level];
            foreach (var (other, needed) in step.Requires)
            {
                if (levels.GetValueOrDefault(other) < needed)
                {
                    throw new GameException(
                        "upgrade_required", $"First upgrade {GarageUpgrades.Find(other)!.Name.ToLowerInvariant()} to level {needed}.");
                }
            }

            foreach (var (item, quantity) in step.Materials)
            {
                await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            }

            var balance = await wallet.DebitAsync(owner, step.PriceCents, $"garage {track.Id} {level + 1}", cancellationToken);

            // Sorting machines work out what they sorted at the old speed before the heating changes it.
            if (track.Bonus == GarageUpgrades.Sorting)
            {
                await sorters.CatchUpAllAsync(owner, cancellationToken);
            }

            await using (var command = await db.CommandAsync(
                """
                INSERT INTO recycling.garage_upgrades (owner_id, track, level) VALUES (@owner, @track, @level)
                ON CONFLICT (owner_id, track) DO UPDATE SET level = @level
                """, cancellationToken))
            {
                command.Parameters.AddWithValue("owner", owner);
                command.Parameters.AddWithValue("track", track.Id);
                command.Parameters.AddWithValue("level", level + 1);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return new UpgradesState(await GarageUpgradeRows.LevelsAsync(db, owner, cancellationToken), balance);
        }, cancellationToken);
}

/// <summary>What the garage's upgrades give the player outside the garage: crafting, trade and experience.</summary>
public sealed class GarageBonuses(GameDb db) : IPlayerBonuses
{
    public async Task<int> PercentAsync(Guid player, string bonus, CancellationToken cancellationToken) =>
        GarageUpgrades.Tracks.Any(t => t.Bonus == bonus)
            ? GarageUpgrades.Bonus(await GarageUpgradeRows.LevelsAsync(db, player, cancellationToken), bonus)
            : 0;
}

public sealed record UpgradeRequest(string Track);

public abstract class UpgradesHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<GarageUpgradeService>(), client.RequireAccount(), payload, cancellationToken);
        var levels = state.Levels;
        return new Reply("garage.upgrades", new
        {
            rank = GarageUpgrades.Rank(levels),
            balanceCents = state.BalanceCents,
            tracks = GarageUpgrades.Tracks.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                start = t.Start,
                level = levels.GetValueOrDefault(t.Id),
                maxLevel = t.MaxLevel,
                bonusName = t.BonusName,
                unit = t.Unit,
                perLevel = t.PerLevel,
                bonus = levels.GetValueOrDefault(t.Id) * t.PerLevel,
                steps = t.Steps.Select((s, i) => new
                {
                    level = i + 1,
                    name = s.Name,
                    priceCents = s.PriceCents,
                    materials = s.Materials.Select(m => new { id = m.Item, name = Items.Names[m.Item], quantity = m.Quantity }),
                    requires = s.Requires.Select(r => new { track = r.Track, name = GarageUpgrades.Find(r.Track)!.Name, level = r.Level }),
                }),
            }),
        });
    }

    protected abstract Task<UpgradesState> RunAsync(GarageUpgradeService upgrades, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>Every track with its levels, what each costs, and how far the player's garage is.</summary>
public sealed class GetUpgradesHandler(IServiceScopeFactory scopes) : UpgradesHandler(scopes)
{
    public override string Type => "garage.upgrades";

    protected override Task<UpgradesState> RunAsync(GarageUpgradeService upgrades, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        upgrades.StateAsync(owner, cancellationToken);
}

public sealed class BuyUpgradeHandler(IServiceScopeFactory scopes) : UpgradesHandler(scopes)
{
    public override string Type => "garage.upgrade";

    protected override Task<UpgradesState> RunAsync(GarageUpgradeService upgrades, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        upgrades.BuyAsync(owner, Payload.Read<UpgradeRequest>(payload).Track, cancellationToken);
}
