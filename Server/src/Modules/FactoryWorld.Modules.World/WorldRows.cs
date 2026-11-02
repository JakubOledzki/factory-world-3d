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
