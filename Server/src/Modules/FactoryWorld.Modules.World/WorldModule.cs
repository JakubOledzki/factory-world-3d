using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>A bought plot. Plots nobody owns are not stored: their biome comes from the map.</summary>
public sealed class Plot
{
    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    public Biome Biome { get; set; }

    public long PriceCents { get; set; }

    public DateTimeOffset BoughtAt { get; set; }

    /// <summary>What its owner called it, or null.</summary>
    public string? Name { get; set; }

    /// <summary>Whether anyone may come onto it to look around; otherwise only the owner and their company.</summary>
    public bool Public { get; set; }
}

/// <summary>A square of <see cref="PlotHarvests.Side"/> × <see cref="PlotHarvests.Side"/> tiles of a plot where trees, rocks or dunes have been gathered.</summary>
public sealed class PlotHarvest
{
    public int X { get; set; }

    public int Y { get; set; }

    /// <summary>Which square of the plot, see <see cref="PlotHarvests.Square"/>.</summary>
    public short Square { get; set; }

    /// <summary>
    /// When each tile of the square was gathered, by <see cref="PlotHarvests.Cell"/>, NULL where it was not: on sand a dune dug
    /// before the last sandstorm has drifted back.
    /// </summary>
    public DateTimeOffset?[] HarvestedAt { get; set; } = [];
}

/// <summary>A tilled tile of a fertile plot, with what grows on it.</summary>
public sealed class PlotField
{
    public int X { get; set; }

    public int Y { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public string? Crop { get; set; }

    public DateTimeOffset? PlantedAt { get; set; }

    /// <summary>The farmstead that tends it, or null for a field a player tends.</summary>
    public Guid? FarmsteadId { get; set; }
}

/// <summary>A tree a player planted on a plot, growing until <see cref="ReadyAt"/>.</summary>
public sealed class PlotTree
{
    public int X { get; set; }

    public int Y { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public DateTimeOffset ReadyAt { get; set; }

    /// <summary>The forester's building that planted it, or null for a tree a player planted.</summary>
    public Guid? ForesterId { get; set; }

    /// <summary>The kind of tree, see <see cref="Trees"/>.</summary>
    public string Species { get; set; } = Trees.Pine;
}

/// <summary>An animal a player let out on a plot; fed, it gives its product at <see cref="ReadyAt"/>.</summary>
public sealed class PlotAnimalRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public string Kind { get; set; } = "";

    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>Collections so far, which count towards a young one.</summary>
    public int Meals { get; set; }
}

/// <summary>A mine of a plot that has been worked: loads taken since it was last full, and when it ran dry.</summary>
public sealed class PlotMine
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Mine { get; set; }

    public int Used { get; set; }

    public DateTimeOffset? EmptyAt { get; set; }

    /// <summary>How far the owner has built the mine up: 1 to <see cref="Mining.Levels"/>.</summary>
    public int Level { get; set; } = 1;
}

/// <summary>Something a player put on a tile of their plot, e.g. a workbench or a furnace.</summary>
public sealed class PlotObjectRow
{
    public Guid Id { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public Guid OwnerId { get; set; }

    public string Kind { get; set; } = "";

    /// <summary>Quarter turns anticlockwise seen from above; 0 faces south.</summary>
    public int Rotation { get; set; }
}

/// <summary>A forester's building on a plot: the wood it keeps, when it can start cutting the next tree and what it plants.</summary>
public sealed class PlotForesterRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>Plain wood: of pines, spruces, birches and lindens.</summary>
    public int Wood { get; set; }

    public int OakWood { get; set; }

    public DateTimeOffset BusyUntil { get; set; }

    /// <summary>The kind of tree it plants.</summary>
    public string Species { get; set; } = Trees.Pine;
}

/// <summary>A farmstead on a plot: the crops it keeps, its manure, when it can reap again and what it sows.</summary>
public sealed class PlotFarmsteadRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>The crop it sows.</summary>
    public string Crop { get; set; } = "";

    /// <summary>What it harvested, as JSON: crop → count.</summary>
    public string Store { get; set; } = "{}";

    public int Manure { get; set; }

    public DateTimeOffset BusyUntil { get; set; }
}

/// <summary>A feeder on a plot: the feed in it, which it gives the hungry animals around it.</summary>
public sealed class PlotFeederRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>The feed in it, as JSON: item → count.</summary>
    public string Store { get; set; } = "{}";

    /// <summary>What an automatic barn collected from the animals and keeps until taken out, as JSON: item → count; a plain feeder keeps none.</summary>
    public string Output { get; set; } = "{}";
}

/// <summary>A well pumping oil or gas: what it holds, how far it is with its unit and the state of its seals.</summary>
public sealed class PlotWellRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    public string Kind { get; set; } = "";

    public int Stored { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public int Wear { get; set; }

    public int Gaskets { get; set; }

    public bool Leaking { get; set; }
}

/// <summary>How much the wells of a plot have pumped from one of its deposits.</summary>
public sealed class OilFieldRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public short Field { get; set; }

    public long Pumped { get; set; }
}

/// <summary>A drill standing at a mine: the mine it works, the ore it holds and how far it is with its load.</summary>
public sealed class PlotDrillRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public Guid OwnerId { get; set; }

    public int Mine { get; set; }

    /// <summary>Ore by item, as JSON.</summary>
    public string Ores { get; set; } = "{}";

    /// <summary>When, in the drill's own time on the power grid, its load started; null while it stands idle.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Whether it waits for its dry mine to fill up again.</summary>
    public bool Waiting { get; set; }
}

/// <summary>What a geological survey found under a plot.</summary>
public sealed class SurveyRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public short Oil { get; set; }

    public short Gas { get; set; }

    public DateTimeOffset SurveyedAt { get; set; }
}

/// <summary>Goods waiting in a plot's railway station.</summary>
public sealed class StationItem
{
    public int X { get; set; }

    public int Y { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }
}

/// <summary>A train on its way from one plot's station to another's.</summary>
public sealed class ShipmentRow
{
    public long Id { get; set; }

    public int FromX { get; set; }

    public int FromY { get; set; }

    public int ToX { get; set; }

    public int ToY { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }

    public Guid SenderId { get; set; }

    public DateTimeOffset ArrivesAt { get; set; }
}

/// <summary>A train that leaves one plot's station for another's by itself, every few minutes.</summary>
public sealed class TrainRouteRow
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public int FromX { get; set; }

    public int FromY { get; set; }

    public int ToX { get; set; }

    public int ToY { get; set; }

    /// <summary>The items it takes; empty for everything in the station.</summary>
    public string[] Items { get; set; } = [];

    public int EveryMinutes { get; set; }

    /// <summary>Trucks from the plot's truck depot drive it instead of a train.</summary>
    public bool ByTruck { get; set; }

    public DateTimeOffset NextDepartureAt { get; set; }

    public DateTimeOffset? LastDepartedAt { get; set; }

    public long LastQuantity { get; set; }

    public string? LastProblem { get; set; }

    /// <summary>Across a border its goods are not declared: no duty, but customs may catch it.</summary>
    public bool Smuggle { get; set; }
}

/// <summary>A freight route between two of a player's plots, with its one train (see <see cref="FreightRules"/>).</summary>
public sealed class FreightRouteRow
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public int FromX { get; set; }

    public int FromY { get; set; }

    public int ToX { get; set; }

    public int ToY { get; set; }

    /// <summary>What one train carries: <see cref="Quantities"/>[i] pieces of <see cref="Items"/>[i].</summary>
    public string[] Items { get; set; } = [];

    public long[] Quantities { get; set; } = [];

    /// <summary>Where the train is: <c>loading</c>, <c>travelling</c>, <c>unloading</c> or <c>returning</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>When the train is looked at next: it gets there, comes back, or checks the chests again.</summary>
    public DateTimeOffset NextAt { get; set; }

    /// <summary>The goods on the train, while it carries any.</summary>
    public string[] CarriedItems { get; set; } = [];

    public long[] CarriedQuantities { get; set; } = [];

    /// <summary>What the loading chests held of each of <see cref="Items"/> when the waiting train last looked.</summary>
    public long[] Stock { get; set; } = [];

    public long Trips { get; set; }

    public long Delivered { get; set; }

    public long LastFeeCents { get; set; }

    /// <summary>Why the train waits: <c>waiting</c>, <c>no_money</c> or <c>no_room</c>.</summary>
    public string? LastProblem { get; set; }

    /// <summary>The route was removed while the train carried goods: it ends once they are delivered.</summary>
    public bool Stopping { get; set; }

    /// <summary>Across a border its goods are not declared: no duty, but customs may catch it.</summary>
    public bool Smuggle { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An export chest by a plot's station and when its train comes next.</summary>
public sealed class ExportChestRow
{
    public Guid ObjectId { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public DateTimeOffset NextPickupAt { get; set; }

    public DateTimeOffset? LastPickupAt { get; set; }

    public long LastQuantity { get; set; }

    public string? LastProblem { get; set; }
}

/// <summary>What a plot's export chest holds of one item.</summary>
public sealed class ExportItemRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }
}

/// <summary>What a plot's export chest lists a piece of one item for.</summary>
public sealed class ExportPriceRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public required string Item { get; set; }

    public long PriceCents { get; set; }
}

/// <summary>A buying market (its commission in percent) or a rail port (its fee in cents), and what it earned its owner.</summary>
public sealed class ExportPostRow
{
    public Guid ObjectId { get; set; }

    public required string Kind { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public long Value { get; set; }

    public long EarnedCents { get; set; }

    public long Uses { get; set; }
}

/// <summary>Goods of an export train on their way to a buying market.</summary>
public sealed class ExportShipmentRow
{
    public long Id { get; set; }

    public Guid SellerId { get; set; }

    public int FromX { get; set; }

    public int FromY { get; set; }

    public int MarketX { get; set; }

    public int MarketY { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }

    public long PriceCents { get; set; }

    public DateTimeOffset ArrivesAt { get; set; }
}

/// <summary>A company's bank, by the company's id, with the company's name when it put up its bank.</summary>
public sealed class BankRow
{
    public Guid CompanyId { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A bank or a branch of it standing on a city plot.</summary>
public sealed class BankBuildingRow
{
    public Guid ObjectId { get; set; }

    public Guid CompanyId { get; set; }

    public required string Kind { get; set; }

    public int X { get; set; }

    public int Y { get; set; }
}

/// <summary>A player's account in a bank and the money on it.</summary>
public sealed class BankAccountRow
{
    public Guid CompanyId { get; set; }

    public Guid PlayerId { get; set; }

    public long BalanceCents { get; set; }

    public DateTimeOffset OpenedAt { get; set; }
}

/// <summary>Money on its way into an account, out of one into a wallet, or from one account to another.</summary>
public sealed class BankTransferRow
{
    public long Id { get; set; }

    public required string Kind { get; set; }

    public Guid PlayerId { get; set; }

    public Guid ToPlayerId { get; set; }

    public Guid? FromCompanyId { get; set; }

    public Guid? ToCompanyId { get; set; }

    public long Cents { get; set; }

    public DateTimeOffset SentAt { get; set; }

    public DateTimeOffset ArrivesAt { get; set; }
}

public sealed class WorldDbContext(DbContextOptions<WorldDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "world";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Plot>(e =>
        {
            e.ToTable("plots");
            // One row per plot, so two players can never both buy the same one.
            e.HasKey(p => new { p.X, p.Y });
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Biome).HasColumnName("biome");
            e.Property(p => p.PriceCents).HasColumnName("price_cents");
            e.Property(p => p.BoughtAt).HasColumnName("bought_at");
            e.Property(p => p.Name).HasColumnName("name").HasMaxLength(PlotNames.MaxLength);
            e.Property(p => p.Public).HasColumnName("public").HasDefaultValue(false);
            e.HasIndex(p => p.OwnerId);
        });
        model.Entity<PlotHarvest>(e =>
        {
            e.ToTable("plot_harvests");
            e.HasKey(h => new { h.X, h.Y, h.Square });
            e.Property(h => h.X).HasColumnName("x");
            e.Property(h => h.Y).HasColumnName("y");
            e.Property(h => h.Square).HasColumnName("square");
            e.Property(h => h.HarvestedAt).HasColumnName("harvested_at");
        });
        model.Entity<PlotField>(e =>
        {
            e.ToTable("plot_fields");
            e.HasKey(f => new { f.X, f.Y, f.TileX, f.TileY });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.TileX).HasColumnName("tile_x");
            e.Property(f => f.TileY).HasColumnName("tile_y");
            e.Property(f => f.Crop).HasColumnName("crop").HasMaxLength(32);
            e.Property(f => f.PlantedAt).HasColumnName("planted_at");
            e.Property(f => f.FarmsteadId).HasColumnName("farmstead_id");
            e.HasIndex(f => f.FarmsteadId);
        });
        model.Entity<PlotTree>(e =>
        {
            e.ToTable("plot_trees");
            e.HasKey(t => new { t.X, t.Y, t.TileX, t.TileY });
            e.Property(t => t.X).HasColumnName("x");
            e.Property(t => t.Y).HasColumnName("y");
            e.Property(t => t.TileX).HasColumnName("tile_x");
            e.Property(t => t.TileY).HasColumnName("tile_y");
            e.Property(t => t.ReadyAt).HasColumnName("ready_at");
            e.Property(t => t.ForesterId).HasColumnName("forester_id");
            e.Property(t => t.Species).HasColumnName("species").HasMaxLength(16).HasDefaultValue(Trees.Pine);
            e.HasIndex(t => t.ForesterId);
        });
        model.Entity<PlotAnimalRow>(e =>
        {
            e.ToTable("plot_animals");
            e.HasKey(a => new { a.X, a.Y, a.TileX, a.TileY });
            e.Property(a => a.X).HasColumnName("x");
            e.Property(a => a.Y).HasColumnName("y");
            e.Property(a => a.TileX).HasColumnName("tile_x");
            e.Property(a => a.TileY).HasColumnName("tile_y");
            e.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(a => a.ReadyAt).HasColumnName("ready_at");
            e.Property(a => a.Meals).HasColumnName("meals").HasDefaultValue(0);
        });
        model.Entity<PlotMine>(e =>
        {
            e.ToTable("plot_mines");
            e.HasKey(m => new { m.X, m.Y, m.Mine });
            e.Property(m => m.X).HasColumnName("x");
            e.Property(m => m.Y).HasColumnName("y");
            e.Property(m => m.Mine).HasColumnName("mine");
            e.Property(m => m.Used).HasColumnName("used");
            e.Property(m => m.EmptyAt).HasColumnName("empty_at");
            e.Property(m => m.Level).HasColumnName("level").HasDefaultValue(1);
        });
        model.Entity<PlotObjectRow>(e =>
        {
            e.ToTable("plot_objects");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.X).HasColumnName("x");
            e.Property(o => o.Y).HasColumnName("y");
            e.Property(o => o.TileX).HasColumnName("tile_x");
            e.Property(o => o.TileY).HasColumnName("tile_y");
            e.Property(o => o.OwnerId).HasColumnName("owner_id");
            e.Property(o => o.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(o => o.Rotation).HasColumnName("rotation");
            // One object per tile.
            e.HasIndex(o => new { o.X, o.Y, o.TileX, o.TileY }).IsUnique();
        });
        model.Entity<PlotForesterRow>(e =>
        {
            e.ToTable("plot_foresters");
            e.HasKey(f => f.ObjectId);
            e.Property(f => f.ObjectId).HasColumnName("object_id");
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Wood).HasColumnName("wood");
            e.Property(f => f.OakWood).HasColumnName("oak_wood").HasDefaultValue(0);
            e.Property(f => f.BusyUntil).HasColumnName("busy_until");
            e.Property(f => f.Species).HasColumnName("species").HasMaxLength(16).HasDefaultValue(Trees.Pine);
            e.HasIndex(f => new { f.X, f.Y });
            e.HasIndex(f => f.OwnerId);
        });

        model.Entity<PlotFarmsteadRow>(e =>
        {
            e.ToTable("plot_farmsteads");
            e.HasKey(f => f.ObjectId);
            e.Property(f => f.ObjectId).HasColumnName("object_id");
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Crop).HasColumnName("crop").HasMaxLength(32);
            e.Property(f => f.Store).HasColumnName("store").HasDefaultValue("{}");
            e.Property(f => f.Manure).HasColumnName("manure").HasDefaultValue(0);
            e.Property(f => f.BusyUntil).HasColumnName("busy_until");
            e.HasIndex(f => new { f.X, f.Y });
            e.HasIndex(f => f.OwnerId);
        });

        model.Entity<PlotFeederRow>(e =>
        {
            e.ToTable("plot_feeders");
            e.HasKey(f => f.ObjectId);
            e.Property(f => f.ObjectId).HasColumnName("object_id");
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Store).HasColumnName("store").HasDefaultValue("{}");
            e.Property(f => f.Output).HasColumnName("output").HasDefaultValue("{}");
            e.HasIndex(f => new { f.X, f.Y });
        });

        model.Entity<PlotDrillRow>(e =>
        {
            e.ToTable("plot_drills");
            e.HasKey(d => d.ObjectId);
            e.Property(d => d.ObjectId).HasColumnName("object_id");
            e.Property(d => d.X).HasColumnName("x");
            e.Property(d => d.Y).HasColumnName("y");
            e.Property(d => d.OwnerId).HasColumnName("owner_id");
            e.Property(d => d.Mine).HasColumnName("mine");
            e.Property(d => d.Ores).HasColumnName("ores").HasColumnType("jsonb");
            e.Property(d => d.StartedAt).HasColumnName("started_at");
            e.Property(d => d.Waiting).HasColumnName("waiting");
            e.HasIndex(d => new { d.X, d.Y });
            e.HasIndex(d => d.OwnerId);
        });

        model.Entity<PlotWellRow>(e =>
        {
            e.ToTable("plot_wells");
            e.HasKey(w => w.ObjectId);
            e.Property(w => w.ObjectId).HasColumnName("object_id");
            e.Property(w => w.X).HasColumnName("x");
            e.Property(w => w.Y).HasColumnName("y");
            e.Property(w => w.OwnerId).HasColumnName("owner_id");
            e.Property(w => w.Kind).HasColumnName("kind");
            e.Property(w => w.Stored).HasColumnName("stored");
            e.Property(w => w.StartedAt).HasColumnName("started_at");
            e.Property(w => w.Wear).HasColumnName("wear");
            e.Property(w => w.Gaskets).HasColumnName("gaskets");
            e.Property(w => w.Leaking).HasColumnName("leaking");
            e.HasIndex(w => new { w.X, w.Y });
            e.HasIndex(w => w.OwnerId);
        });

        model.Entity<OilFieldRow>(e =>
        {
            e.ToTable("oil_fields");
            e.HasKey(f => new { f.X, f.Y, f.Field });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.Field).HasColumnName("field");
            e.Property(f => f.Pumped).HasColumnName("pumped");
        });

        model.Entity<BeachFind>(e =>
        {
            e.ToTable("beach_finds");
            e.HasKey(f => new { f.X, f.Y, f.Day, f.TileX, f.TileY });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.Day).HasColumnName("day");
            e.Property(f => f.TileX).HasColumnName("tile_x");
            e.Property(f => f.TileY).HasColumnName("tile_y");
        });

        model.Entity<SurveyRow>(e =>
        {
            e.ToTable("surveys");
            e.HasKey(s => new { s.X, s.Y });
            e.Property(s => s.X).HasColumnName("x");
            e.Property(s => s.Y).HasColumnName("y");
            e.Property(s => s.Oil).HasColumnName("oil");
            e.Property(s => s.Gas).HasColumnName("gas");
            e.Property(s => s.SurveyedAt).HasColumnName("surveyed_at");
        });
        model.Entity<StationItem>(e =>
        {
            e.ToTable("station_items");
            e.HasKey(i => new { i.X, i.Y, i.Item });
            e.Property(i => i.X).HasColumnName("x");
            e.Property(i => i.Y).HasColumnName("y");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(i => i.Quantity).HasColumnName("quantity");
        });
        model.Entity<ShipmentRow>(e =>
        {
            e.ToTable("shipments");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.FromX).HasColumnName("from_x");
            e.Property(s => s.FromY).HasColumnName("from_y");
            e.Property(s => s.ToX).HasColumnName("to_x");
            e.Property(s => s.ToY).HasColumnName("to_y");
            e.Property(s => s.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.SenderId).HasColumnName("sender_id");
            e.Property(s => s.ArrivesAt).HasColumnName("arrives_at");
            e.HasIndex(s => new { s.ToX, s.ToY, s.ArrivesAt });
            e.HasIndex(s => new { s.FromX, s.FromY });
        });
        model.Entity<TrainRouteRow>(e =>
        {
            e.ToTable("train_routes");
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.FromX).HasColumnName("from_x");
            e.Property(r => r.FromY).HasColumnName("from_y");
            e.Property(r => r.ToX).HasColumnName("to_x");
            e.Property(r => r.ToY).HasColumnName("to_y");
            e.Property(r => r.Items).HasColumnName("items");
            e.Property(r => r.EveryMinutes).HasColumnName("every_minutes");
            e.Property(r => r.ByTruck).HasColumnName("by_truck");
            e.Property(r => r.NextDepartureAt).HasColumnName("next_departure_at");
            e.Property(r => r.LastDepartedAt).HasColumnName("last_departed_at");
            e.Property(r => r.LastQuantity).HasColumnName("last_quantity");
            e.Property(r => r.LastProblem).HasColumnName("last_problem").HasMaxLength(32);
            e.Property(r => r.Smuggle).HasColumnName("smuggle").HasDefaultValue(false);
            e.HasIndex(r => r.NextDepartureAt);
            e.HasIndex(r => new { r.FromX, r.FromY });
            e.HasIndex(r => new { r.ToX, r.ToY });
        });
        model.Entity<FreightRouteRow>(e =>
        {
            e.ToTable("freight_routes");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.FromX).HasColumnName("from_x");
            e.Property(r => r.FromY).HasColumnName("from_y");
            e.Property(r => r.ToX).HasColumnName("to_x");
            e.Property(r => r.ToY).HasColumnName("to_y");
            e.Property(r => r.Items).HasColumnName("items");
            e.Property(r => r.Quantities).HasColumnName("quantities");
            e.Property(r => r.State).HasColumnName("state").HasMaxLength(16);
            e.Property(r => r.NextAt).HasColumnName("next_at");
            e.Property(r => r.CarriedItems).HasColumnName("carried_items");
            e.Property(r => r.CarriedQuantities).HasColumnName("carried_quantities");
            e.Property(r => r.Stock).HasColumnName("stock");
            e.Property(r => r.Trips).HasColumnName("trips");
            e.Property(r => r.Delivered).HasColumnName("delivered");
            e.Property(r => r.LastFeeCents).HasColumnName("last_fee_cents");
            e.Property(r => r.LastProblem).HasColumnName("last_problem").HasMaxLength(32);
            e.Property(r => r.Stopping).HasColumnName("stopping");
            e.Property(r => r.Smuggle).HasColumnName("smuggle").HasDefaultValue(false);
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.HasIndex(r => r.NextAt);
            e.HasIndex(r => r.OwnerId);
        });
        model.Entity<ExportChestRow>(e =>
        {
            e.ToTable("export_chests");
            e.HasKey(c => c.ObjectId);
            e.Property(c => c.ObjectId).HasColumnName("object_id");
            e.Property(c => c.X).HasColumnName("x");
            e.Property(c => c.Y).HasColumnName("y");
            e.Property(c => c.NextPickupAt).HasColumnName("next_pickup_at");
            e.Property(c => c.LastPickupAt).HasColumnName("last_pickup_at");
            e.Property(c => c.LastQuantity).HasColumnName("last_quantity");
            e.Property(c => c.LastProblem).HasColumnName("last_problem").HasMaxLength(32);
            e.HasIndex(c => c.NextPickupAt);
            e.HasIndex(c => new { c.X, c.Y }).IsUnique();
        });
        model.Entity<ExportItemRow>(e =>
        {
            e.ToTable("export_items");
            e.HasKey(i => new { i.X, i.Y, i.Item });
            e.Property(i => i.X).HasColumnName("x");
            e.Property(i => i.Y).HasColumnName("y");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(i => i.Quantity).HasColumnName("quantity");
        });
        model.Entity<ExportPriceRow>(e =>
        {
            e.ToTable("export_prices");
            e.HasKey(i => new { i.X, i.Y, i.Item });
            e.Property(i => i.X).HasColumnName("x");
            e.Property(i => i.Y).HasColumnName("y");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(i => i.PriceCents).HasColumnName("price_cents");
        });
        model.Entity<ExportPostRow>(e =>
        {
            e.ToTable("export_posts");
            e.HasKey(p => p.ObjectId);
            e.Property(p => p.ObjectId).HasColumnName("object_id");
            e.Property(p => p.Kind).HasColumnName("kind").HasMaxLength(64);
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.Value).HasColumnName("value");
            e.Property(p => p.EarnedCents).HasColumnName("earned_cents");
            e.Property(p => p.Uses).HasColumnName("uses");
        });
        model.Entity<ExportShipmentRow>(e =>
        {
            e.ToTable("export_shipments");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.SellerId).HasColumnName("seller_id");
            e.Property(s => s.FromX).HasColumnName("from_x");
            e.Property(s => s.FromY).HasColumnName("from_y");
            e.Property(s => s.MarketX).HasColumnName("market_x");
            e.Property(s => s.MarketY).HasColumnName("market_y");
            e.Property(s => s.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.PriceCents).HasColumnName("price_cents");
            e.Property(s => s.ArrivesAt).HasColumnName("arrives_at");
            e.HasIndex(s => s.ArrivesAt);
            e.HasIndex(s => new { s.FromX, s.FromY });
        });
        model.Entity<BankRow>(e =>
        {
            e.ToTable("banks");
            e.HasKey(b => b.CompanyId);
            e.Property(b => b.CompanyId).HasColumnName("company_id");
            e.Property(b => b.Name).HasColumnName("name").HasMaxLength(64);
            e.Property(b => b.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<BankBuildingRow>(e =>
        {
            e.ToTable("bank_buildings");
            e.HasKey(b => b.ObjectId);
            e.Property(b => b.ObjectId).HasColumnName("object_id");
            e.Property(b => b.CompanyId).HasColumnName("company_id");
            e.Property(b => b.Kind).HasColumnName("kind").HasMaxLength(64);
            e.Property(b => b.X).HasColumnName("x");
            e.Property(b => b.Y).HasColumnName("y");
            e.HasIndex(b => b.CompanyId);
        });
        model.Entity<BankAccountRow>(e =>
        {
            e.ToTable("bank_accounts");
            e.HasKey(a => new { a.CompanyId, a.PlayerId });
            e.Property(a => a.CompanyId).HasColumnName("company_id");
            e.Property(a => a.PlayerId).HasColumnName("player_id");
            e.Property(a => a.BalanceCents).HasColumnName("balance_cents");
            e.Property(a => a.OpenedAt).HasColumnName("opened_at");
            e.HasIndex(a => a.PlayerId);
        });
        model.Entity<BankTransferRow>(e =>
        {
            e.ToTable("bank_transfers");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(t => t.PlayerId).HasColumnName("player_id");
            e.Property(t => t.ToPlayerId).HasColumnName("to_player_id");
            e.Property(t => t.FromCompanyId).HasColumnName("from_company_id");
            e.Property(t => t.ToCompanyId).HasColumnName("to_company_id");
            e.Property(t => t.Cents).HasColumnName("cents");
            e.Property(t => t.SentAt).HasColumnName("sent_at");
            e.Property(t => t.ArrivesAt).HasColumnName("arrives_at");
            e.HasIndex(t => t.ArrivesAt);
            e.HasIndex(t => t.PlayerId);
            e.HasIndex(t => new { t.ToPlayerId, t.ToCompanyId });
        });
        model.Entity<TownProgressRow>(e =>
        {
            e.ToTable("town_progress");
            e.HasKey(t => new { t.TownX, t.TownY });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.DeliveredCents).HasColumnName("delivered_cents");
        });
        model.Entity<TownContributionRow>(e =>
        {
            e.ToTable("town_contributions");
            e.HasKey(t => new { t.TownX, t.TownY, t.PlayerId });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.PlayerId).HasColumnName("player_id");
            e.Property(t => t.DeliveredCents).HasColumnName("delivered_cents");
        });
        model.Entity<TownDeliveryRow>(e =>
        {
            e.ToTable("town_deliveries");
            e.HasKey(t => new { t.TownX, t.TownY, t.Day, t.Item });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.Day).HasColumnName("day");
            e.Property(t => t.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(t => t.Quantity).HasColumnName("quantity");
        });
        model.Entity<ShopRow>(e =>
        {
            e.ToTable("shops");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.SettledAt).HasColumnName("settled_at");
            e.Property(s => s.Carry).HasColumnName("carry");
            e.Property(s => s.SoldCents).HasColumnName("sold_cents");
            e.Property(s => s.SoldItems).HasColumnName("sold_items");
        });
        model.Entity<ShopItemRow>(e =>
        {
            e.ToTable("shop_items");
            e.HasKey(s => new { s.ShopId, s.Item });
            e.Property(s => s.ShopId).HasColumnName("shop_id");
            e.Property(s => s.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.PriceCents).HasColumnName("price_cents");
        });
        model.Entity<ServiceSaleRow>(e =>
        {
            e.ToTable("service_sales");
            e.HasKey(s => new { s.Id, s.Day });
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Day).HasColumnName("day");
            e.Property(s => s.Count).HasColumnName("count");
        });
        model.Entity<PlotTaxRow>(e =>
        {
            e.ToTable("plot_taxes");
            e.HasKey(t => t.OwnerId);
            e.Property(t => t.OwnerId).HasColumnName("owner_id");
            e.Property(t => t.PaidUntil).HasColumnName("paid_until");
        });
        model.Entity<SavedPlanRow>(e =>
        {
            e.ToTable("plans");
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Name).HasColumnName("name").HasMaxLength(PlanRules.MaxName);
            e.Property(p => p.Objects).HasColumnName("objects").HasColumnType("jsonb");
            e.Property(p => p.SavedAt).HasColumnName("saved_at");
            // A player's plans have names of their own.
            e.HasIndex(p => new { p.OwnerId, p.Name }).IsUnique();
        });
        model.Entity<PlotLikeRow>(e =>
        {
            e.ToTable("plot_likes");
            // A player likes a plot once.
            e.HasKey(l => new { l.X, l.Y, l.PlayerId });
            e.Property(l => l.X).HasColumnName("x");
            e.Property(l => l.Y).HasColumnName("y");
            e.Property(l => l.PlayerId).HasColumnName("player_id");
            e.Property(l => l.LikedAt).HasColumnName("liked_at");
            e.HasIndex(l => l.PlayerId);
        });
        model.Entity<TownVoteRow>(e =>
        {
            e.ToTable("town_votes");
            e.HasKey(v => new { v.TownX, v.TownY, v.Week, v.VoterId });
            e.Property(v => v.TownX).HasColumnName("town_x");
            e.Property(v => v.TownY).HasColumnName("town_y");
            e.Property(v => v.Week).HasColumnName("week");
            e.Property(v => v.VoterId).HasColumnName("voter_id");
            e.Property(v => v.CandidateId).HasColumnName("candidate_id");
        });
        model.Entity<TownPolicyRow>(e =>
        {
            e.ToTable("town_policies");
            e.HasKey(p => new { p.TownX, p.TownY, p.Week });
            e.Property(p => p.TownX).HasColumnName("town_x");
            e.Property(p => p.TownY).HasColumnName("town_y");
            e.Property(p => p.Week).HasColumnName("week");
            e.Property(p => p.MayorId).HasColumnName("mayor_id");
            e.Property(p => p.TaxPercent).HasColumnName("tax_percent");
            e.Property(p => p.Bonus).HasColumnName("bonus").HasMaxLength(16);
            e.Property(p => p.Recalled).HasColumnName("recalled");
            e.Property(p => p.PlotTaxPerMille).HasColumnName("plot_tax_per_mille").HasDefaultValue(0);
        });
        model.Entity<TownTreasuryRow>(e =>
        {
            e.ToTable("town_treasuries", t => t.HasCheckConstraint("ck_town_treasuries_cents", "cents >= 0"));
            e.HasKey(t => new { t.TownX, t.TownY });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.Cents).HasColumnName("cents");
        });
        model.Entity<TownLedgerRow>(e =>
        {
            e.ToTable("town_ledger");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.TownX).HasColumnName("town_x");
            e.Property(l => l.TownY).HasColumnName("town_y");
            e.Property(l => l.AmountCents).HasColumnName("amount_cents");
            e.Property(l => l.BalanceAfterCents).HasColumnName("balance_after_cents");
            e.Property(l => l.Reason).HasColumnName("reason").HasMaxLength(200);
            e.Property(l => l.CreatedAt).HasColumnName("created_at");
            e.HasIndex(l => new { l.TownX, l.TownY, l.Id });
        });
        model.Entity<TownInvestmentRow>(e =>
        {
            e.ToTable("town_investments");
            e.HasKey(i => new { i.TownX, i.TownY, i.Kind });
            e.Property(i => i.TownX).HasColumnName("town_x");
            e.Property(i => i.TownY).HasColumnName("town_y");
            e.Property(i => i.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(i => i.Level).HasColumnName("level");
        });
        model.Entity<TownRecallRow>(e =>
        {
            e.ToTable("town_recalls");
            e.HasKey(r => new { r.TownX, r.TownY, r.Week, r.SignerId });
            e.Property(r => r.TownX).HasColumnName("town_x");
            e.Property(r => r.TownY).HasColumnName("town_y");
            e.Property(r => r.Week).HasColumnName("week");
            e.Property(r => r.SignerId).HasColumnName("signer_id");
        });
        model.Entity<TownCandidateRow>(e =>
        {
            e.ToTable("town_candidates");
            e.HasKey(c => new { c.TownX, c.TownY, c.Week, c.PlayerId });
            e.Property(c => c.TownX).HasColumnName("town_x");
            e.Property(c => c.TownY).HasColumnName("town_y");
            e.Property(c => c.Week).HasColumnName("week");
            e.Property(c => c.PlayerId).HasColumnName("player_id");
            e.Property(c => c.TaxPercent).HasColumnName("tax_percent");
            e.Property(c => c.Bonus).HasColumnName("bonus").HasMaxLength(16);
            e.Property(c => c.Slogan).HasColumnName("slogan").HasMaxLength(TownHallRules.MaxSloganLength);
            e.Property(c => c.DepositCents).HasColumnName("deposit_cents");
            e.Property(c => c.Settled).HasColumnName("settled");
            e.HasIndex(c => c.PlayerId);
        });
        model.Entity<TownSponsorshipRow>(e =>
        {
            e.ToTable("town_sponsorships");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.TownX).HasColumnName("town_x");
            e.Property(s => s.TownY).HasColumnName("town_y");
            e.Property(s => s.Week).HasColumnName("week");
            e.Property(s => s.CandidateId).HasColumnName("candidate_id");
            e.Property(s => s.Company).HasColumnName("company").HasMaxLength(64);
            e.Property(s => s.Cents).HasColumnName("cents");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.HasIndex(s => new { s.TownX, s.TownY, s.Week });
        });
        model.Entity<TownOrdinanceRow>(e =>
        {
            e.ToTable("town_ordinances");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.TownX).HasColumnName("town_x");
            e.Property(o => o.TownY).HasColumnName("town_y");
            e.Property(o => o.Week).HasColumnName("week");
            e.Property(o => o.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(o => o.ProposedBy).HasColumnName("proposed_by");
            e.Property(o => o.VotingEnds).HasColumnName("voting_ends");
            e.HasIndex(o => new { o.TownX, o.TownY, o.Week, o.Kind }).IsUnique();
        });
        model.Entity<TownOrdinanceVoteRow>(e =>
        {
            e.ToTable("town_ordinance_votes");
            e.HasKey(v => new { v.OrdinanceId, v.VoterId });
            e.Property(v => v.OrdinanceId).HasColumnName("ordinance_id");
            e.Property(v => v.VoterId).HasColumnName("voter_id");
            e.Property(v => v.Yes).HasColumnName("yes");
        });
        model.Entity<TownFestivalRow>(e =>
        {
            e.ToTable("town_festivals");
            e.HasKey(f => new { f.TownX, f.TownY });
            e.Property(f => f.TownX).HasColumnName("town_x");
            e.Property(f => f.TownY).HasColumnName("town_y");
            e.Property(f => f.Until).HasColumnName("until");
        });
        model.Entity<MeteorRow>(e =>
        {
            e.ToTable("meteors", t => t.HasCheckConstraint("ck_meteors_left", "iron_left >= 0 AND iridium_left >= 0"));
            e.HasKey(m => m.Slot);
            e.Property(m => m.Slot).HasColumnName("slot").ValueGeneratedNever();
            e.Property(m => m.X).HasColumnName("x");
            e.Property(m => m.Y).HasColumnName("y");
            e.Property(m => m.IronLeft).HasColumnName("iron_left");
            e.Property(m => m.IridiumLeft).HasColumnName("iridium_left");
            e.Property(m => m.FellAt).HasColumnName("fell_at");
        });
        model.Entity<MeteorDigRow>(e =>
        {
            e.ToTable("meteor_digs");
            e.HasKey(d => new { d.Slot, d.PlayerId });
            e.Property(d => d.Slot).HasColumnName("slot");
            e.Property(d => d.PlayerId).HasColumnName("player_id");
            e.Property(d => d.Pieces).HasColumnName("pieces");
            e.Property(d => d.LastAt).HasColumnName("last_at");
        });
        model.Entity<TownWeekDeliveryRow>(e =>
        {
            e.ToTable("town_week_deliveries");
            e.HasKey(d => new { d.TownX, d.TownY, d.Week, d.PlayerId });
            e.Property(d => d.TownX).HasColumnName("town_x");
            e.Property(d => d.TownY).HasColumnName("town_y");
            e.Property(d => d.Week).HasColumnName("week");
            e.Property(d => d.PlayerId).HasColumnName("player_id");
            e.Property(d => d.DeliveredCents).HasColumnName("delivered_cents");
            // The chief supplier is the best of a week.
            e.HasIndex(d => new { d.TownX, d.TownY, d.Week, d.DeliveredCents });
        });
        model.Entity<TownTenderRow>(e =>
        {
            e.ToTable("town_tenders", t =>
            {
                t.HasCheckConstraint("ck_town_tenders_delivered", "delivered >= 0 AND delivered <= quantity");
                t.HasCheckConstraint("ck_town_tenders_money", "price_cents >= 0 AND deposit_cents >= 0");
            });
            e.HasKey(t => new { t.TownX, t.TownY, t.Week });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.Week).HasColumnName("week");
            e.Property(t => t.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(t => t.Quantity).HasColumnName("quantity");
            e.Property(t => t.CapCents).HasColumnName("cap_cents");
            e.Property(t => t.ClosesAt).HasColumnName("closes_at");
            e.Property(t => t.EndsAt).HasColumnName("ends_at");
            e.Property(t => t.State).HasColumnName("state").HasMaxLength(16);
            e.Property(t => t.WinnerId).HasColumnName("winner_id");
            e.Property(t => t.PriceCents).HasColumnName("price_cents");
            e.Property(t => t.DepositCents).HasColumnName("deposit_cents");
            e.Property(t => t.Delivered).HasColumnName("delivered");
            // The tender clock looks for tenders that are due.
            e.HasIndex(t => t.State);
        });
        model.Entity<TenderBidRow>(e =>
        {
            e.ToTable("tender_bids", t => t.HasCheckConstraint("ck_tender_bids_money", "price_cents > 0 AND deposit_cents > 0"));
            e.HasKey(b => new { b.TownX, b.TownY, b.Week, b.BidderId });
            e.Property(b => b.TownX).HasColumnName("town_x");
            e.Property(b => b.TownY).HasColumnName("town_y");
            e.Property(b => b.Week).HasColumnName("week");
            e.Property(b => b.BidderId).HasColumnName("bidder_id");
            e.Property(b => b.PriceCents).HasColumnName("price_cents");
            e.Property(b => b.DepositCents).HasColumnName("deposit_cents");
            e.Property(b => b.At).HasColumnName("at");
        });
        model.Entity<PlotFireRow>(e =>
        {
            e.ToTable("plot_fires");
            e.HasKey(f => new { f.X, f.Y });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.CheckedAt).HasColumnName("checked_at");
            e.Property(f => f.LastAt).HasColumnName("last_at");
            e.Property(f => f.LastX).HasColumnName("last_x");
            e.Property(f => f.LastY).HasColumnName("last_y");
            e.Property(f => f.LastBurnt).HasColumnName("last_burnt");
            e.Property(f => f.LastStopped).HasColumnName("last_stopped");
        });
        model.Entity<PlotSprayRow>(e =>
        {
            e.ToTable("plot_sprays");
            e.HasKey(s => new { s.X, s.Y });
            e.Property(s => s.X).HasColumnName("x");
            e.Property(s => s.Y).HasColumnName("y");
            e.Property(s => s.Until).HasColumnName("until");
        });
        model.Entity<HiveRow>(e =>
        {
            e.ToTable("hives");
            e.HasKey(h => h.ObjectId);
            e.Property(h => h.ObjectId).HasColumnName("object_id");
            e.Property(h => h.X).HasColumnName("x");
            e.Property(h => h.Y).HasColumnName("y");
            e.Property(h => h.OwnerId).HasColumnName("owner_id");
            e.Property(h => h.Honey).HasColumnName("honey");
            e.Property(h => h.Wax).HasColumnName("wax");
            e.Property(h => h.Made).HasColumnName("made");
            e.Property(h => h.Progress).HasColumnName("progress");
            e.Property(h => h.At).HasColumnName("at");
        });
        model.Entity<ForestLedgerRow>(e =>
        {
            e.ToTable("forest_ledger");
            e.HasKey(l => new { l.PlayerId, l.Day });
            e.Property(l => l.PlayerId).HasColumnName("player_id");
            e.Property(l => l.Day).HasColumnName("day");
            e.Property(l => l.Planted).HasColumnName("planted");
            e.Property(l => l.Cut).HasColumnName("cut");
        });
        model.Entity<FootprintVersionRow>(e =>
        {
            e.ToTable("footprint_versions");
            e.HasKey(v => v.Version);
            e.Property(v => v.Version).HasColumnName("version").ValueGeneratedNever();
            e.Property(v => v.At).HasColumnName("at");
        });
    }
}

public sealed record PlotOwner(Guid AccountId, string? Name);

/// <param name="PriceCents">What the asking player would pay for it now; null when it cannot be bought.</param>
/// <param name="Survey">What lies under the plot, shown only to its owner once they paid for a survey.</param>
/// <param name="Company">The company its owner brought it into, if any.</param>
/// <param name="CanEnter">Whether the asking player may go onto it: its owner, a member of its company, or anyone when it is public.</param>
/// <param name="Name">What its owner called it, or null.</param>
/// <param name="Public">Whether its owner lets anyone come onto it to look around.</param>
public sealed record PlotState(
    int X, int Y, Biome Biome, PlotOwner? Owner, long? PriceCents, Survey? Survey = null, PlotCompany? Company = null, bool CanEnter = false,
    string? Name = null, bool Public = false);

/// <param name="Tool">The tool that came with the plot, or null.</param>
public sealed record Purchase(PlotState Plot, long BalanceCents, XpGain Xp, string? Tool);

public sealed class WorldService(
    GameDb db, WorldMap map, IWallet wallet, ICharacterProgress progress, ICharacterNames names, IEquipment equipment, SurveyService surveys,
    IEnumerable<ICompanyPlots> companies, CityService cities, PlotTaxService taxes, Countries countries, INationalPolicy nation, TimeProvider time)
{
    /// <summary>What a geological survey costs the player.</summary>
    public Task<long> SurveyPriceAsync(Guid player, CancellationToken cancellationToken) => surveys.PriceAsync(player, cancellationToken);

    /// <summary>Most owned plots one area request returns.</summary>
    public const int MaxAreaPlots = 5000;

    /// <summary>The player's plot tax, with the days since it was last paid paid now.</summary>
    public Task<PlotTax> TaxAsync(Guid owner, CancellationToken cancellationToken) => taxes.SettleAsync(owner, cancellationToken);

    public async Task<long> CountOwnedAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT count(*) FROM world.plots WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<(int X, int Y, Biome Biome, string? Name)>> OwnedAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT x, y, biome, name FROM world.plots WHERE owner_id = @owner ORDER BY bought_at", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plots = new List<(int, int, Biome, string?)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            plots.Add((reader.GetInt32(0), reader.GetInt32(1), (Biome)reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return plots;
    }

    /// <summary>Gives one of the player's plots a name, or takes it off with an empty one.</summary>
    public async Task<PlotState> RenameAsync(Guid player, int x, int y, string? name, CancellationToken cancellationToken)
    {
        name = PlotNames.Clean(name);
        await using (var update = await db.CommandAsync(
            "UPDATE world.plots SET name = @name WHERE x = @x AND y = @y AND owner_id = @owner", cancellationToken))
        {
            update.Parameters.AddWithValue("x", x);
            update.Parameters.AddWithValue("y", y);
            update.Parameters.AddWithValue("owner", player);
            update.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("not_owner", "Only its owner names a plot.");
            }
        }

        return await PlotAsync(player, x, y, cancellationToken);
    }

    /// <summary>Owned plots inside a rectangle of the grid, with whose they are.</summary>
    /// <summary>Plots other members brought into the player's company.</summary>
    public async Task<IReadOnlyList<(int X, int Y, Biome Biome)>> SharedAsync(Guid player, CancellationToken cancellationToken)
    {
        var plots = new List<(int, int, Biome)>();
        foreach (var registry in companies)
        {
            plots.AddRange((await registry.SharedWithAsync(player, cancellationToken)).Select(p => (p.X, p.Y, map.BiomeAt(p.X, p.Y))));
        }

        return plots;
    }

    public async Task<IReadOnlyList<(int X, int Y, Guid Owner)>> AreaAsync(int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT x, y, owner_id FROM world.plots
            WHERE x >= @x AND x < @x + @width AND y >= @y AND y < @y + @height
            LIMIT @limit
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("width", width);
        command.Parameters.AddWithValue("height", height);
        command.Parameters.AddWithValue("limit", MaxAreaPlots);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plots = new List<(int, int, Guid)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            plots.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2)));
        }

        return plots;
    }

    public async Task<PlotState> PlotAsync(Guid viewer, int x, int y, CancellationToken cancellationToken)
    {
        var biome = BiomeAt(x, y);
        Guid? found = null;
        string? plotName = null;
        var open = false;
        await using (var command = await db.CommandAsync("SELECT owner_id, name, public FROM world.plots WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                (found, plotName) = (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                open = reader.GetBoolean(2);
            }
        }

        if (found is { } owner)
        {
            var name = (await names.NamesAsync([owner], cancellationToken)).GetValueOrDefault(owner);
            // Deposits stay secret from everyone but the owner who paid for the survey.
            var survey = owner == viewer ? await surveys.FindAsync(x, y, cancellationToken) : null;
            PlotCompany? company = null;
            foreach (var registry in companies)
            {
                company ??= await registry.CompanyAsync(viewer, x, y, cancellationToken);
            }

            return new PlotState(x, y, biome, new PlotOwner(owner, name), null, survey, company, owner == viewer || company?.Member == true || open, plotName, open);
        }

        long? price = null;
        if (Biomes.All[biome].Buyable)
        {
            try
            {
                price = (await PriceAsync(viewer, x, y, biome, cancellationToken)).Total;
            }
            catch (GameException e) when (e.Code == "embargo")
            {
                // The country sells no plots to the viewer's country.
            }
        }

        return new PlotState(x, y, biome, null, price);
    }

    /// <summary>What the player pays for a free plot: the price of their next plot in the biome, less in a town that makes plots
    /// cheaper; the country's government makes it cheaper for its citizens or adds a fee for foreigners (which goes to its treasury).</summary>
    private async Task<(long Total, long Fee)> PriceAsync(Guid buyer, int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        var price = (long)Math.Round(PlotPrices.For(await CountOwnedAsync(buyer, cancellationToken), biome) * await cities.PlotPriceFactorAsync(x, y, cancellationToken));
        return countries.At(x, y) is { } country ? await nation.PlotPriceAsync(buyer, country.Code, price, cancellationToken) : (price, 0);
    }

    /// <summary>Buys a free plot: money out, plot in, both or neither. The price depends on how many plots the player has.</summary>
    public Task<Purchase> BuyAsync(Guid owner, int x, int y, CancellationToken cancellationToken)
    {
        var biome = BiomeAt(x, y);
        if (!Biomes.All[biome].Buyable)
        {
            throw new GameException("not_buyable", "Water cannot be bought.");
        }

        return db.InTransactionAsync(async () =>
        {
            // One purchase at a time per player, so two at once cannot both pay the lower price.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()));
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            // The days before are taxed by the plots the player had then; who owes the tax buys nothing more.
            if (await taxes.SettleAsync(owner, cancellationToken) is { Due: true } tax)
            {
                throw PlotTaxService.Due(tax);
            }

            var (total, fee) = await PriceAsync(owner, x, y, biome, cancellationToken);
            var price = total - fee;
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.plots (x, y, owner_id, biome, price_cents, bought_at)
                VALUES (@x, @y, @owner, @biome, @price, @at)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("x", x);
                insert.Parameters.AddWithValue("y", y);
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("biome", (short)biome);
                insert.Parameters.AddWithValue("price", price);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("plot_taken", "Someone already owns this plot.");
                }
            }

            var balance = await wallet.DebitAsync(owner, price, $"plot {x}:{y}", cancellationToken);
            if (fee > 0)
            {
                await nation.ChargePlotFeeAsync(owner, countries.At(x, y)!.Code, fee, x, y, cancellationToken);
                balance = await wallet.BalanceAsync(owner, cancellationToken);
            }

            var xp = await progress.AddXpAsync(owner, PlotRules.XpPerPlot, cancellationToken);
            // A forest comes with an axe, a desert with a shovel, mountains with a pickaxe, the steppe with shears.
            var tool = PlotLayout.StarterTool(biome);
            if (tool is not null)
            {
                await equipment.GiveToolAsync(owner, tool, cancellationToken);
            }

            var name = (await names.NamesAsync([owner], cancellationToken)).GetValueOrDefault(owner);
            return new Purchase(new PlotState(x, y, biome, new PlotOwner(owner, name), null, CanEnter: true), balance, xp, tool);
        }, cancellationToken);
    }

    private Biome BiomeAt(int x, int y) =>
        map.Contains(x, y) ? map.BiomeAt(x, y) : throw new GameException("bad_plot", "There is no such plot.");
}

/// <summary>Who owns a plot, for other modules (e.g. companies).</summary>
public sealed class PlotRegistry(GameDb db) : IPlotRegistry
{
    public async Task<OwnedPlot?> FindAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT owner_id, biome, price_cents FROM world.plots WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new OwnedPlot(x, y, reader.GetGuid(0), reader.GetInt16(1), reader.GetInt64(2)) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountsAsync(CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT owner_id, count(*)::int FROM world.plots GROUP BY owner_id", cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<Guid, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetGuid(0)] = reader.GetInt32(1);
        }

        return counts;
    }
}

public sealed record PlotRequest(int X, int Y);

public sealed record AreaRequest(int X, int Y, int Width, int Height);

public abstract class WorldHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await RunAsync(scope.ServiceProvider.GetRequiredService<WorldService>(), client.RequireAccount(), payload, cancellationToken);
    }

    protected abstract Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken);

    /// <param name="surveyPriceCents">What a survey costs this player (less with geology researched); the plain price when not given.</param>
    public static object PlotJson(PlotState plot, Guid player, long? surveyPriceCents = null) => new
    {
        x = plot.X,
        y = plot.Y,
        biome = (int)plot.Biome,
        owner = plot.Owner is { } owner ? new { name = owner.Name, mine = owner.AccountId == player } : null,
        priceCents = plot.PriceCents,
        survey = plot.Survey is { } survey ? SurveyHandler.SurveyJson(survey) : null,
        surveyPriceCents = plot.Owner?.AccountId == player ? surveyPriceCents ?? Deposits.SurveyPriceCents : (long?)null,
        company = plot.Company is { } company ? new { name = company.Name, member = company.Member } : null,
        canEnter = plot.CanEnter,
        name = plot.Name,
        // Public: anyone may come to look around; private: only the owner and the members of its company.
        @public = plot.Public,
    };
}

/// <summary>The map's size, the biomes, the price list and the player's own plots.</summary>
public sealed class InfoHandler(IServiceScopeFactory scopes, WorldMap map) : WorldHandler(scopes)
{
    public override string Type => "world.info";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var mine = await world.OwnedAsync(player, cancellationToken);
        var shared = await world.SharedAsync(player, cancellationToken);
        var tax = await world.TaxAsync(player, cancellationToken);
        return new Reply("world.info", new
        {
            width = map.Width,
            height = map.Height,
            plotKm = PlotRules.SideKm,
            biomes = Biomes.All.Values.Select(b => new
            {
                id = (int)b.Id,
                name = b.Name,
                buyable = b.Buyable,
                priceFactor = b.PriceFactor,
                bonuses = b.Bonuses.Select(bonus => new { text = bonus.Text, good = bonus.Good }),
            }),
            priceTiersCents = PlotPrices.TierCents,
            nextBasePriceCents = PlotPrices.Base(mine.Count),
            plots = mine.Select(p => new { x = p.X, y = p.Y, biome = (int)p.Biome, name = p.Name }),
            sharedPlots = shared.Select(p => new { x = p.X, y = p.Y, biome = (int)p.Biome }),
            // Every plot but the first costs this many thousandths of its price a day.
            taxPerMille = PlotTaxRules.PerMillePerDay,
            tax = new { dailyCents = tax.DailyCents, paidUntil = tax.PaidUntil.ToUnixTimeMilliseconds(), owedCents = tax.OwedCents },
        });
    }
}

/// <summary>Owned plots in a rectangle of the map, to draw them.</summary>
public sealed class AreaHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.area";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<AreaRequest>(payload);
        if (request.Width is < 1 or > 10_000 || request.Height is < 1 or > 10_000)
        {
            throw new GameException("bad_area", "Ask for at most 10000 × 10000 plots.");
        }

        var plots = await world.AreaAsync(request.X, request.Y, request.Width, request.Height, cancellationToken);
        return new Reply("world.area", new
        {
            request.X,
            request.Y,
            request.Width,
            request.Height,
            plots = plots.Select(p => new { x = p.X, y = p.Y, mine = p.Owner == player }),
        });
    }
}

public sealed class GetPlotHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.plot";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotRequest>(payload);
        return new Reply("world.plot", PlotJson(
            await world.PlotAsync(player, request.X, request.Y, cancellationToken), player, await world.SurveyPriceAsync(player, cancellationToken)));
    }
}

public sealed record PublicRequest(int X, int Y, bool Public);

/// <summary>The owner opens their plot to everyone (only to look around) or keeps it to themselves and their company;
/// answers with the plot like "world.plot".</summary>
public sealed class SetPlotPublicHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.setPublic";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PublicRequest>(payload);
        var player = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlotService>().SetPublicAsync(player, request.X, request.Y, request.Public, cancellationToken);
        var plot = await scope.ServiceProvider.GetRequiredService<WorldService>().PlotAsync(player, request.X, request.Y, cancellationToken);
        return new Reply("world.plot", WorldHandler.PlotJson(plot, player));
    }
}

public sealed record RenamePlotRequest(int X, int Y, string? Name);

/// <summary>The owner names a plot (an empty name takes it off); answers like "world.plot".</summary>
public sealed class RenamePlotHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.rename";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<RenamePlotRequest>(payload);
        return new Reply("world.plot", PlotJson(await world.RenameAsync(player, request.X, request.Y, request.Name, cancellationToken), player));
    }
}

public sealed class BuyPlotHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.buy";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotRequest>(payload);
        var purchase = await world.BuyAsync(player, request.X, request.Y, cancellationToken);
        return new Reply("world.bought", new
        {
            plot = PlotJson(purchase.Plot, player),
            balanceCents = purchase.BalanceCents,
            xp = new { xp = purchase.Xp.Xp, level = purchase.Xp.Level, leveledUp = purchase.Xp.LeveledUp },
            tool = purchase.Tool is { } tool ? new { id = tool, name = Items.Names[tool] } : null,
        });
    }
}

/// <summary>The map of the Earth in 5 × 5 km plots and who owns which. Owns the <c>world</c> schema.</summary>
public sealed class WorldModule : IGameModule
{
    public string Name => "World";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<WorldDbContext>(WorldDbContext.Schema);
        services.AddSingleton(_ => WorldMap.Load());
        services.AddSingleton(_ => Countries.Load());
        services.AddScoped<ICountryMap, CountryMap>();
        services.AddScoped<WorldService>();
        services.AddScoped<PlotService>();
        services.AddScoped<PlotTaxService>();
        services.AddScoped<BeachService>();
        services.AddScoped<StationService>();
        services.AddScoped<FarmService>();
        services.AddScoped<SmogService>();
        services.AddScoped<ForestService>();
        services.AddScoped<ForestLedger>();
        services.AddScoped<IForestCertificate>(provider => provider.GetRequiredService<ForestLedger>());
        services.AddScoped<IPlayerRanking, GreenestRanking>();
        services.AddScoped<PestService>();
        services.AddScoped<FireService>();
        services.AddScoped<BeeService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<BeeService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<BeeService>());
        services.AddSingleton<IMessageHandler, GetHiveHandler>();
        services.AddSingleton<IMessageHandler, TakeFromHiveHandler>();
        services.AddSingleton<IMessageHandler, SprayHandler>();
        services.AddScoped<HusbandryService>();
        services.AddScoped<FeederService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<FeederService>());
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<FeederService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<FeederService>());
        services.AddScoped<IMachinePorts>(provider => provider.GetRequiredService<FeederService>());
        services.AddScoped<MineService>();
        services.AddScoped<SurveyService>();
        services.AddSingleton<PlotPermits>();
        services.AddSingleton<IPlotPermits>(provider => provider.GetRequiredService<PlotPermits>());
        services.AddScoped<PlotObjectService>();
        services.AddScoped<IPlacedObjects>(provider => provider.GetRequiredService<PlotObjectService>());
        services.AddScoped<PlotObjectsQuery>();
        services.AddScoped<IPlotObjects>(sp => sp.GetRequiredService<PlotObjectsQuery>());
        services.AddScoped<IPlotRegistry, PlotRegistry>();
        services.AddScoped<IGameGauges, PlotGauges>();
        services.AddSingleton<IMessageHandler, InfoHandler>();
        services.AddSingleton<IMessageHandler, AreaHandler>();
        services.AddSingleton<IMessageHandler, GetPlotHandler>();
        services.AddSingleton<IMessageHandler, BuyPlotHandler>();
        services.AddSingleton<IMessageHandler, SetPlotPublicHandler>();
        services.AddSingleton<PlotPresence>();
        services.AddSingleton<IMessageHandler, EnterPlotHandler>();
        services.AddSingleton<IMessageHandler, PresenceHandler>();
        services.AddSingleton<IMessageHandler, LeavePlotHandler>();
        services.AddSingleton<IMessageHandler, HarvestHandler>();
        services.AddSingleton<IMessageHandler, GetStationHandler>();
        services.AddSingleton<IMessageHandler, LoadStationHandler>();
        services.AddSingleton<IMessageHandler, UnloadStationHandler>();
        services.AddSingleton<IMessageHandler, SendHandler>();
        services.AddSingleton<IMessageHandler, AddRouteHandler>();
        services.AddSingleton<IMessageHandler, RemoveRouteHandler>();
        services.AddSingleton<IMessageHandler, FindPartnersHandler>();
        services.AddScoped<FreightService>();
        services.AddSingleton<IMessageHandler, GetFreightHandler>();
        services.AddSingleton<IMessageHandler, AddFreightHandler>();
        services.AddSingleton<IMessageHandler, RemoveFreightHandler>();
        services.AddSingleton<IMessageHandler, RenamePlotHandler>();
        services.AddScoped<IMachinePorts, StationPorts>();
        services.AddSingleton(provider => new LandMasses(provider.GetRequiredService<WorldMap>()));
        services.AddScoped<ExportService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<ExportService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<ExportService>());
        services.AddScoped<IMachinePorts, ExportChestPorts>();
        services.AddHostedService<ExportDispatcher>();
        services.AddSingleton<IMessageHandler, GetExportHandler>();
        services.AddSingleton<IMessageHandler, LoadExportHandler>();
        services.AddSingleton<IMessageHandler, UnloadExportHandler>();
        services.AddSingleton<IMessageHandler, PriceExportHandler>();
        services.AddSingleton<IMessageHandler, GetExportPostHandler>();
        services.AddSingleton<IMessageHandler, SetExportPostHandler>();
        services.AddScoped<BankService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<BankService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<BankService>());
        services.AddHostedService<BankDispatcher>();
        services.AddSingleton<IMessageHandler, GetBankHandler>();
        services.AddSingleton<IMessageHandler, OpenBankAccountHandler>();
        services.AddSingleton<IMessageHandler, CloseBankAccountHandler>();
        services.AddSingleton<IMessageHandler, DepositHandler>();
        services.AddSingleton<IMessageHandler, WithdrawHandler>();
        services.AddSingleton<IMessageHandler, BankTransferHandler>();
        services.AddSingleton<IMessageHandler, BankOfBuildingHandler>();
        services.AddScoped<ForesterService>();
        services.AddScoped<FarmsteadService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<FarmsteadService>());
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<FarmsteadService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<FarmsteadService>());
        services.AddScoped<IMachinePorts>(provider => provider.GetRequiredService<FarmsteadService>());
        services.AddScoped<IMachineStatuses>(provider => provider.GetRequiredService<FarmsteadService>());
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<ForesterService>());
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<ForesterService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<ForesterService>());
        services.AddScoped<IMachinePorts>(provider => provider.GetRequiredService<ForesterService>());
        services.AddScoped<IMachineStatuses>(provider => provider.GetRequiredService<ForesterService>());
        services.AddScoped<OilService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<OilService>());
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<OilService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<OilService>());
        services.AddScoped<IMachinePorts>(provider => provider.GetRequiredService<OilService>());
        services.AddScoped<IMachineStatuses>(provider => provider.GetRequiredService<OilService>());
        services.AddSingleton<IMessageHandler, GetWellHandler>();
        services.AddSingleton<IMessageHandler, TakeFromWellHandler>();
        services.AddSingleton<IMessageHandler, SealWellHandler>();
        services.AddScoped<DrillService>();
        services.AddScoped<IPlacedHook>(provider => provider.GetRequiredService<DrillService>());
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<DrillService>());
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<DrillService>());
        services.AddScoped<IMachinePorts>(provider => provider.GetRequiredService<DrillService>());
        services.AddScoped<IMachineStatuses>(provider => provider.GetRequiredService<DrillService>());
        services.AddSingleton<IMessageHandler, GetDrillHandler>();
        services.AddSingleton<IMessageHandler, TakeFromDrillHandler>();
        services.AddSingleton<IMessageHandler, GetForestryHandler>();
        services.AddSingleton<IMessageHandler, TakeFromForesterHandler>();
        services.AddSingleton<IMessageHandler, PlantForesterHandler>();
        services.AddSingleton<IMessageHandler, GetFarmsteadsHandler>();
        services.AddSingleton<IMessageHandler, TakeFromFarmsteadHandler>();
        services.AddSingleton<IMessageHandler, SowFarmsteadHandler>();
        services.AddHostedService<RailwayDispatcher>();
        services.AddSingleton<IMessageHandler, TillHandler>();
        services.AddSingleton<IMessageHandler, PlantHandler>();
        services.AddSingleton<IMessageHandler, ReapHandler>();
        services.AddSingleton<IMessageHandler, UntillHandler>();
        services.AddSingleton<IMessageHandler, PlantTreeHandler>();
        services.AddSingleton<IMessageHandler, ReleaseAnimalHandler>();
        services.AddSingleton<IMessageHandler, FeedAnimalHandler>();
        services.AddSingleton<IMessageHandler, CollectAnimalHandler>();
        services.AddSingleton<IMessageHandler, TakeAnimalHandler>();
        services.AddSingleton<IMessageHandler, GetAnimalsHandler>();
        services.AddSingleton<IMessageHandler, GetFeederHandler>();
        services.AddSingleton<IMessageHandler, FillFeederHandler>();
        services.AddSingleton<IMessageHandler, TakeFromFeederHandler>();
        services.AddSingleton<IMessageHandler, MineHandler>();
        services.AddSingleton<IMessageHandler, UpgradeMineHandler>();
        services.AddSingleton<IMessageHandler, BlastMineHandler>();
        services.AddSingleton<IMessageHandler, SurveyHandler>();
        services.AddSingleton<IMessageHandler, PlaceOnPlotHandler>();
        services.AddSingleton<IMessageHandler, PickUpFromPlotHandler>();
        services.AddSingleton<IMessageHandler, PlaceManyOnPlotHandler>();
        services.AddSingleton<PlotGallery>();
        services.AddScoped<LikeService>();
        services.AddScoped<PlanService>();
        services.AddSingleton<IMessageHandler, GetPlansHandler>();
        services.AddSingleton<IMessageHandler, SavePlanHandler>();
        services.AddSingleton<IMessageHandler, DeletePlanHandler>();
        services.AddSingleton<IMessageHandler, LikePlotHandler>();
        services.AddSingleton<IMessageHandler, GalleryHandler>();
        services.AddSingleton<IMessageHandler, PickUpManyFromPlotHandler>();
        services.AddSingleton<IMessageHandler, PickUpAreaFromPlotHandler>();
        services.AddSingleton<IMessageHandler, RotateOnPlotHandler>();
        services.AddSingleton(_ => Towns.Load());
        services.AddScoped<CityService>();
        services.AddScoped<IMachineContents>(provider => provider.GetRequiredService<CityService>());
        services.AddSingleton<IMessageHandler, GetCityHandler>();
        services.AddSingleton<IMessageHandler, DeliverToCityHandler>();
        services.AddSingleton<IMessageHandler, CityTrashHandler>();
        services.AddSingleton<IMessageHandler, CityServiceHandler>();
        services.AddSingleton<IMessageHandler, CityVoteHandler>();
        services.AddSingleton<IMessageHandler, CityPolicyHandler>();
        services.AddSingleton<IMessageHandler, CityInvestHandler>();
        services.AddSingleton<IMessageHandler, CityRecallHandler>();
        services.AddSingleton<IMessageHandler, CityStandHandler>();
        services.AddSingleton<IMessageHandler, CityWithdrawHandler>();
        services.AddSingleton<IMessageHandler, CitySponsorHandler>();
        services.AddSingleton<IMessageHandler, CityProposeHandler>();
        services.AddSingleton<IMessageHandler, CityOrdinanceVoteHandler>();
        services.AddSingleton<IMessageHandler, CityFestivalHandler>();
        services.AddSingleton<IMessageHandler, CityTenderBidHandler>();
        services.AddSingleton<IMessageHandler, CityTenderWithdrawHandler>();
        services.AddSingleton<IMessageHandler, CityTenderDeliverHandler>();
        services.AddHostedService<TenderClock>();
        services.AddScoped<MeteorService>();
        services.AddSingleton<IMessageHandler, GetMeteorHandler>();
        services.AddSingleton<IMessageHandler, DigMeteorHandler>();
        services.AddHostedService<MeteorClock>();
        services.AddSingleton<IMessageHandler, GetShopHandler>();
        services.AddSingleton<IMessageHandler, StockShopHandler>();
        services.AddSingleton<IMessageHandler, TakeFromShopHandler>();
        services.AddScoped<FootprintMigration>();
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.MigrateModuleAsync<WorldDbContext>(cancellationToken);
        // Load the map before the first player asks for it.
        services.GetRequiredService<WorldMap>();
    }

    /// <summary>Once every module's tables are there: objects built before machines grew are moved or given back.</summary>
    public async Task StartedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FootprintMigration>().RunAsync(cancellationToken);
    }
}
