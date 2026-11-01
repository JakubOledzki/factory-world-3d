using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Things a player carries out of the inventory and puts on a free tile of their plot, e.g. a
/// workbench or an anvil to craft at far from the garage, a research table, a furnace, a machine that processes crops (mill, oil
/// press, pickling barrel, bakery, field kitchen), belts, inserters, chests, a generator and the
/// poles that carry its power, a computer to trade on the market from the plot, a forester's building
/// that grows and cuts a forest around itself, a sawmill, or the mountain industry: a drill at a mine,
/// a quarry, a crusher, a stonemason, a foundry, water power and cable car stations, or on the shore boats,
/// a smokehouse, a cannery, a salt pan, a fish farm, a shipyard, a port, a breakwater, a lighthouse and a tidal plant. Each covers a square of
/// tiles by its kind and level (see <see cref="Footprints"/>: a belt one tile, a first-level machine 2 × 2, a forester's building 3 × 3),
/// turned by quarter turns like furniture in the garage.
/// </summary>
public static class PlotObjects
{
    /// <summary>The most objects one building plan puts up (or takes down) at once.</summary>
    public const int MaxPlan = 100;

    /// <summary>The most objects taken off a marked area of the plot at once.</summary>
    public const int MaxArea = 400;

    /// <summary>Items that can be put on a plot.</summary>
    public static readonly IReadOnlySet<string> Placeable = new HashSet<string>
    {
        Items.Workbench, Items.Anvil, Items.ResearchTable, Items.Furnace, Items.BrickFurnace, Items.BlastFurnace,
        Items.Mill, Items.RollerMill, Items.FlourWorks, Items.OilPress, Items.PicklingBarrel, Items.Bakery, Items.Kitchen,
        Items.Belt, Items.FastBelt, Items.ExpressBelt, Items.Inserter, Items.FastInserter, Items.ExpressInserter, Items.Splitter,
        Items.UndergroundBelt, Items.LongInserter, Items.ExpressCableStation, Items.DronePort,
        Items.WoodenChest, Items.IronChest, Items.WoodPile, Items.Heap, Items.GrainSilo, Items.LogisticsDepot,
        Items.Generator, Items.Pole,
        Items.Computer,
        Items.ForesterHut, Items.ForesterLodge, Items.ForestryStation,
        Items.Sawmill, Items.ElectricSawmill, Items.IndustrialSawmill,
        Items.Assembler, Items.FastAssembler, Items.ExpressAssembler,
        Items.Drill, Items.HeavyDrill, Items.Quarry, Items.Crusher, Items.Stonemason, Items.Foundry,
        Items.MechanizedQuarry, Items.OpenPitMine, Items.ConeCrusher, Items.OreProcessingPlant,
        Items.WaterWheel, Items.HydroPlant, Items.CableStation,
        Items.FishingBoat, Items.RowingBoat, Items.Smokehouse, Items.Cannery, Items.SaltPan, Items.FishFarm,
        Items.Shipyard, Items.Port, Items.Breakwater, Items.Lighthouse, Items.TidalPlant,
        Items.Pumpjack, Items.GasWell, Items.OffshorePlatform, Items.WaterInjector, Items.OilSeparator,
        Items.Distillery, Items.Refinery, Items.Cracker, Items.ChemicalPlant, Items.GasPlant, Items.DieselGenerator,
        Items.Pipe, Items.Pump, Items.Tank, Items.Road, Items.TruckDepot, Items.Scrubber,
        Items.Glassworks, Items.ArcFurnace, Items.WaterWell, Items.Desalinator, Items.DryingYard, Items.CementKiln, Items.ConcreteMixer,
        Items.ReedFence, Items.SolarPanel, Items.SolarTower, Items.Battery,
        Items.WindTurbine, Items.LargeWindTurbine, Items.Heater, Items.IceMelter, Items.ColdStore, Items.ServerRoom,
        Items.IceCoreDrill, Items.PolarStation, Items.IceHotel, Items.Viewpoint, Items.DogKennel, Items.SnowmobileGarage, Items.TrackFoundation,
        Items.AssemblyHall, Items.MultistoreyFactory, Items.Shop, Items.HighBay, Items.CitySorter, Items.FreightTerminal, Items.CompanyHq,
        Items.DesignOffice, Items.Laboratory, Items.ServiceWorkshop, Items.Tailor,
        Items.PaperMill, Items.Carpentry, Items.CharcoalKiln, Items.TarKiln, Items.Beehive, Items.Watchtower, Items.FireStation,
        Items.MachineShop, Items.RoboticHall, Items.OxygenConverter, Items.RollingMill, Items.ControlCenter, Items.ContinuousCaster,
        Items.AutomatedWarehouse, Items.SteelCombine, Items.Megafactory, Items.FusionPlant,
        Items.Substation, Items.Pylon, Items.CoalPlant, Items.EnergyStore, Items.NuclearPlant, Items.GridTie,
        Items.Canteen, Items.Slaughterhouse, Items.Dairy, Items.PotatoDistillery, Items.BiogasPlant, Items.Stable, Items.Bellows,
        Items.Feeder, Items.Farmstead, Items.ManorFarm,
        Items.CokeOven, Items.Jeweler, Items.TreeNursery, Items.SpinningMill, Items.Loom, Items.AutoBarn, Items.SugarFactory, Items.Sprinkler,
        Items.MechanizedFarm, Items.BiomassPlant, Items.ChpPlant, Items.GeothermalPlant, Items.Electrolyzer, Items.FuelCell,
        Items.ExportChest, Items.BuyMarket, Items.RailPort, Items.Bank, Items.BankBranch,
        Items.LoadingChest, Items.LoadingChest2, Items.LoadingChest3, Items.LoadingChest4,
        Items.ReceivingChest, Items.ReceivingChest2, Items.ReceivingChest3, Items.ReceivingChest4,
        Items.PumpkinLantern, Items.Scarecrow, Items.ChristmasTree, Items.Snowman,
    };

    /// <summary>Ice hotels and viewpoints one plot takes, together.</summary>
    public const int MaxTouristSpots = 3;

    /// <summary>What stands on the glacier only, or on the glacier and the tundra.</summary>
    private static readonly IReadOnlyDictionary<string, Biome[]> Polar = new Dictionary<string, Biome[]>
    {
        [Items.IceCoreDrill] = [Biome.Ice],
        [Items.TrackFoundation] = [Biome.Ice],
        [Items.IceHotel] = [Biome.Ice, Biome.Tundra],
        [Items.Viewpoint] = [Biome.Ice, Biome.Tundra],
        [Items.DogKennel] = [Biome.Ice, Biome.Tundra],
        [Items.SnowmobileGarage] = [Biome.Ice, Biome.Tundra],
    };

    /// <summary>Fails unless an object of <paramref name="kind"/> may stand on (tileX, tileY) of a plot of
    /// <paramref name="biome"/>: a drill next to a mine that has none yet, a quarry in the mountains, water
    /// power by a river, a lake or the sea.</summary>
    public static void RequireSite(string kind, int x, int y, Biome biome, int tileX, int tileY, IEnumerable<PlacedOnPlot> others)
    {
        if (Items.IsDrill(kind))
        {
            var mines = PlotLayout.Mines(x, y, biome);
            var mine = Drills.MineAt(mines, tileX, tileY) ?? throw new GameException("no_mine_here", "A drill stands right next to a mine.");
            if (others.Any(o => Items.IsDrill(o.Kind) && Drills.MineAt(mines, o.TileX, o.TileY)?.Id == mine.Id))
            {
                throw new GameException("mine_has_drill", "This mine has a drill already.");
            }
        }

        if (Items.IsQuarry(kind) && biome != Biome.Mountains)
        {
            throw new GameException("no_quarry", "A quarry is cut into the mountains only.");
        }

        if (Items.IsHydro(kind) && kind != Items.TidalPlant && !WaterPower.Allowed((int)biome))
        {
            throw new GameException("no_water", "Water power needs a mountain stream, a lake or the sea.");
        }

        if (Items.IsGeothermal(kind) && !Geothermal.Allowed((int)biome))
        {
            throw new GameException("no_geothermal", "A geothermal plant stands in the mountains, on the glacier or on the tundra.");
        }

        if (kind == Items.SolarTower && !Oasis.Sandy((int)biome))
        {
            throw new GameException("no_sand", "A solar tower with mirrors stands on a desert or in an oasis.");
        }

        if (Polar.TryGetValue(kind, out var polar) && !polar.Contains(biome))
        {
            throw polar.Length == 1
                ? new GameException("no_glacier", "It stands on the glacier only.")
                : new GameException("no_polar", "It stands on the glacier or the tundra only.");
        }

        // Tourists come to see the place, not each viewpoint: a plot takes a few attractions, or rows of them would print money.
        if (kind is Items.IceHotel or Items.Viewpoint && others.Count(o => o.Kind is Items.IceHotel or Items.Viewpoint) >= MaxTouristSpots)
        {
            throw new GameException("tourists_full", $"A plot has room for at most {MaxTouristSpots} hotels and viewpoints.");
        }

        if (kind == Items.TrackFoundation && others.Any(o => o.Kind == Items.TrackFoundation))
        {
            throw new GameException("has_foundation", "The track of this plot has its foundation already.");
        }

        if (Items.IsCityOnly(kind) && biome != Biome.City)
        {
            throw new GameException("no_city", "It stands in a city only.");
        }

        if (biome == Biome.City && !Footprints.Tiles(kind, tileX, tileY).All(t => CityRules.InBuildArea(t.X, t.Y)))
        {
            throw new GameException("city_cramped", "In a city only the ground by the station can be built on.");
        }

        if (kind == Items.ExportChest && !ExportRules.NearStation(tileX, tileY))
        {
            throw new GameException("far_from_station", $"An export chest stands by the station, at most {ExportRules.StationReach} tiles from it.");
        }

        if (kind is Items.FreightTerminal or Items.CompanyHq or Items.ControlCenter or Items.Canteen or Items.Stable or Items.Bellows
            or Items.ExportChest or Items.RailPort && others.Any(o => o.Kind == kind)
            || Items.IsBank(kind) && others.Any(o => Items.IsBank(o.Kind)))
        {
            throw new GameException("has_one", "A plot has room for one of these.");
        }

        if (Items.IsFarmstead(kind) && !Farming.Fertile(biome))
        {
            throw new GameException("not_fertile", "A farmstead stands on fertile land: farmland or an oasis.");
        }

        if (kind == Items.Beehive && !Bees.Fly(biome))
        {
            throw new GameException("no_bees", "No bees fly on the glacier or in the mountains.");
        }

        Coast.RequireSite(kind, x, y, biome, tileX, tileY);
    }
}

public sealed record PlacedOnPlot(Guid Id, string Kind, int TileX, int TileY, int Rotation = 0);

/// <summary>One object of a building plan: what to put where, turned how.</summary>
public sealed record PlannedObject(string? Kind, int TileX, int TileY, int Rotation = 0);

/// <summary>Told about every object put on a plot, in the same transaction, e.g. so a forester starts its work then.</summary>
public interface IPlacedHook
{
    Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken);
}

public sealed class PlotObjectService(
    GameDb db, PlotService plots, IInventory inventory, IEnumerable<IMachineContents> machines, IEnumerable<IPlotAutomation> automation,
    IEnumerable<IPlacedHook> placing, BeachService beach, OilService oil, IEnumerable<ICompanyPlots> companies, PlotObjectsQuery query,
    WorldMap map, Towns towns, PlotPermits permits, IWallet wallet, IEnumerable<IPlotHelpListener> helped)
    : IPlacedObjects
{
    /// <summary>How far the player got with the help they give on the plot, after a place or a turn under a leave; null otherwise.</summary>
    public HelpProgress? Help { get; private set; }

    public async Task<IReadOnlyList<PlacedOnPlot>> ObjectsAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT id, kind, tile_x, tile_y, rotation FROM world.plot_objects WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<PlacedOnPlot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new PlacedOnPlot(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        }

        return objects;
    }

    /// <summary>Puts an object from the inventory on a free tile of the player's plot, turned by <paramref name="rotation"/> quarter turns.</summary>
    /// <summary>A helper (<see cref="IPlotPermits"/>) puts only what a task of their leave names, where it names, and the object is the plot owner's.</summary>
    public async Task<PlacedOnPlot> PlaceAsync(Guid player, int x, int y, string kind, int tileX, int tileY, int rotation, CancellationToken cancellationToken)
    {
        var permit = await LeaveAsync(player, x, y, cancellationToken);
        var placed = await db.InTransactionAsync(async () =>
        {
            var placed = await PlaceOneAsync(player, x, y, kind, tileX, tileY, rotation, true, cancellationToken, permit);
            if (permit is not null)
            {
                await HelpedAsync(permit, cancellationToken);
            }

            return placed;
        }, cancellationToken);
        Tell(permit);
        return placed;
    }

    /// <summary>The leave the player helps under on the plot: only when they may not build there themselves.</summary>
    private async Task<PlotPermit?> LeaveAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        (await plots.RightsAsync(player, x, y, cancellationToken)).Contains(PlotRight.Build) ? null : permits.For(player, x, y);

    /// <summary>
    /// Counts the leave's tasks done; with the last one the leave is used up and the plot's owner pays the helper, in the
    /// transaction that did it, so the help pays once and only when the owner can pay.
    /// </summary>
    private async Task HelpedAsync(PlotPermit permit, CancellationToken cancellationToken)
    {
        var done = PlotPermits.Done(permit, await ObjectsAsync(permit.X, permit.Y, cancellationToken));
        if (done < permit.Tasks.Count)
        {
            Help = new HelpProgress(done, permit.Tasks.Count, permit.PayCents, false, null);
            return;
        }

        if (!permits.Take(permit))
        {
            throw new GameException("help_expired", "The leave to help on this plot ran out.");
        }

        try
        {
            var reason = $"help on plot {permit.X}:{permit.Y}";
            try
            {
                await wallet.DebitAsync(permit.Owner, permit.PayCents, reason, cancellationToken);
            }
            catch (GameException ex) when (ex.Code == "not_enough_money")
            {
                throw new GameException("owner_cannot_pay", "The plot's owner has no money left to pay for the help.");
            }

            var balance = await wallet.CreditAsync(permit.Helper, permit.PayCents, reason, cancellationToken);
            Help = new HelpProgress(done, permit.Tasks.Count, permit.PayCents, true, balance);
        }
        catch
        {
            permits.Restore(permit);
            throw;
        }
    }

    /// <summary>Tells whoever asked for the help that it is done, once the payment is in.</summary>
    private void Tell(PlotPermit? permit)
    {
        if (permit is not null && Help is { Finished: true })
        {
            foreach (var listener in helped)
            {
                listener.Helped(permit);
            }
        }
    }

    /// <summary>Puts up a whole building plan at once: all of its objects or, when one of them cannot stand
    /// where planned, none. The plot's machinery is brought up to now once for the lot, not once per object.</summary>
    public Task<IReadOnlyList<PlacedOnPlot>> PlaceManyAsync(Guid player, int x, int y, IReadOnlyList<PlannedObject> objects, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (objects.Count is 0 || objects.Count > PlotObjects.MaxPlan)
            {
                throw new GameException("bad_plan", $"A building plan holds 1 to {PlotObjects.MaxPlan} objects.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            await CatchUpAsync(x, y, cancellationToken);
            var placed = new List<PlacedOnPlot>();
            foreach (var o in objects)
            {
                placed.Add(await PlaceOneAsync(player, x, y, o.Kind ?? "", o.TileX, o.TileY, o.Rotation, false, cancellationToken));
            }

            return (IReadOnlyList<PlacedOnPlot>)placed;
        }, cancellationToken);

    private async Task<PlacedOnPlot> PlaceOneAsync(
        Guid player, int x, int y, string kind, int tileX, int tileY, int rotation, bool catchUp, CancellationToken cancellationToken, PlotPermit? permit = null)
    {
        if (!PlotObjects.Placeable.Contains(kind))
        {
            throw new GameException("not_placeable", "This item cannot be put on a plot.");
        }

        if (rotation is < 0 or > 3)
        {
            throw new GameException("bad_rotation", "Rotation is 0, 1, 2 or 3 quarter turns.");
        }

        Biome biome;
        if (permit is null)
        {
            biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken);
        }
        else if (PlotPermits.PlaceTask(permit, kind, tileX, tileY) is null)
        {
            throw new GameException("not_help_task", "You may put here only what the owner asked for help with.");
        }
        else
        {
            biome = map.BiomeAt(x, y);
        }

        foreach (var (tx, ty) in Footprints.Tiles(kind, tileX, tileY))
        {
            if ((tx, ty) == PlotLayout.Spawn || !await plots.FreeAsync(player, x, y, tx, ty, cancellationToken))
            {
                throw new GameException("blocked", Footprints.Size(kind) > 1 ? $"It needs {Footprints.Size(kind)} × {Footprints.Size(kind)} free tiles." : "Something stands here.");
            }
        }

        if (Items.IsForester(kind) && Forestry.GrowTime(biome) is null)
        {
            throw new GameException("no_forestry", "No trees grow here.");
        }

        if (catchUp)
        {
            await CatchUpAsync(x, y, cancellationToken);
        }

        var standing = await ObjectsAsync(x, y, cancellationToken);
        PlotObjects.RequireSite(kind, x, y, biome, tileX, tileY, standing);
        if (Items.IsBoat(kind))
        {
            await beach.RequireRoomForBoatAsync(x, y, biome, cancellationToken);
        }

        await oil.RequireSiteAsync(kind, x, y, standing, cancellationToken);
        await ExportSites.RequireAsync(db, map, towns, kind, x, y, cancellationToken);
        if (kind == Items.CompanyHq)
        {
            // A company's headquarters stands on a plot brought into the company.
            PlotCompany? company = null;
            foreach (var c in companies)
            {
                company ??= await c.CompanyAsync(player, x, y, cancellationToken);
            }

            if (company is not { Member: true })
            {
                throw new GameException("hq_off_company", "Headquarters stand on a plot of your company.");
            }
        }

        await inventory.RemoveAsync(player, kind, 1, cancellationToken);
        var placed = new PlacedOnPlot(Guid.NewGuid(), kind, tileX, tileY, rotation);
        await using var insert = await db.CommandAsync(
            """
            INSERT INTO world.plot_objects (id, x, y, tile_x, tile_y, owner_id, kind, rotation)
            VALUES (@id, @x, @y, @tx, @ty, @owner, @kind, @rotation)
            ON CONFLICT DO NOTHING
            """, cancellationToken);
        insert.Parameters.AddWithValue("id", placed.Id);
        insert.Parameters.AddWithValue("x", x);
        insert.Parameters.AddWithValue("y", y);
        insert.Parameters.AddWithValue("tx", tileX);
        insert.Parameters.AddWithValue("ty", tileY);
        // What a helper puts up is the plot owner's, as if they had put it up themselves.
        var owner = permit?.Owner ?? player;
        insert.Parameters.AddWithValue("owner", owner);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.AddWithValue("rotation", rotation);
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        query.Forget();
        if (inserted == 0)
        {
            throw new GameException("blocked", "Something stands here.");
        }

        foreach (var hook in placing)
        {
            await hook.PlacedAsync(owner, x, y, placed, cancellationToken);
        }

        return placed;
    }

    /// <summary>Turns an object on the player's plot to face another way, e.g. a belt to carry elsewhere.</summary>
    /// <summary>A helper turns only what their leave names (<see cref="PlotPermits.MayTurn"/>).</summary>
    public async Task<PlacedOnPlot> RotateAsync(Guid player, int x, int y, Guid id, int rotation, CancellationToken cancellationToken)
    {
        var permit = await LeaveAsync(player, x, y, cancellationToken);
        var turned = await db.InTransactionAsync(async () =>
        {
            if (rotation is < 0 or > 3)
            {
                throw new GameException("bad_rotation", "Rotation is 0, 1, 2 or 3 quarter turns.");
            }

            if (permit is null)
            {
                await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            }
            else if ((await ObjectsAsync(x, y, cancellationToken)).FirstOrDefault(o => o.Id == id) is not { } standing || !PlotPermits.MayTurn(permit, standing))
            {
                throw new GameException("not_help_task", "You may turn only what the owner asked for help with.");
            }

            await CatchUpAsync(x, y, cancellationToken);
            await using var update = await db.CommandAsync(
                """
                UPDATE world.plot_objects SET rotation = @rotation WHERE id = @id AND x = @x AND y = @y
                RETURNING kind, tile_x, tile_y
                """, cancellationToken);
            update.Parameters.AddWithValue("rotation", rotation);
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("x", x);
            update.Parameters.AddWithValue("y", y);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            query.Forget();
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new GameException("no_object", "There is no such object on this plot.");
            }

            var turned = new PlacedOnPlot(id, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), rotation);
            await reader.DisposeAsync();
            if (permit is not null)
            {
                await HelpedAsync(permit, cancellationToken);
            }

            return turned;
        }, cancellationToken);
        Tell(permit);
        return turned;
    }

    /// <summary>Takes an object off the player's plot back into the inventory, with whatever it holds. On a
    /// company's plot a member who may build picks up what they put there; the plot's owner picks up anything.</summary>
    public Task PickUpAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(() => PickUpOneAsync(player, x, y, id, true, cancellationToken), cancellationToken);

    /// <summary>Takes several objects off the plot at once, e.g. to undo a building plan: all of them or none.</summary>
    public Task PickUpManyAsync(Guid player, int x, int y, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (ids.Count is 0 || ids.Count > PlotObjects.MaxPlan || ids.Distinct().Count() != ids.Count)
            {
                throw new GameException("bad_plan", $"Pick up 1 to {PlotObjects.MaxPlan} different objects.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            await CatchUpAsync(x, y, cancellationToken);
            foreach (var id in ids)
            {
                await PickUpOneAsync(player, x, y, id, false, cancellationToken);
            }
        }, cancellationToken);

    private async Task PickUpOneAsync(Guid player, int x, int y, Guid id, bool catchUp, CancellationToken cancellationToken)
    {
        var owner = (await plots.RightsAsync(player, x, y, cancellationToken)).Contains(PlotRight.Owner);
        await plots.RequireOwnerAsync(player, x, y, cancellationToken);
        (int X, int Y)? tile = null;
        await using (var find = await db.CommandAsync("SELECT tile_x, tile_y FROM world.plot_objects WHERE id = @id AND x = @x AND y = @y", cancellationToken))
        {
            find.Parameters.AddWithValue("id", id);
            find.Parameters.AddWithValue("x", x);
            find.Parameters.AddWithValue("y", y);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                tile = (reader.GetInt32(0), reader.GetInt32(1));
            }
        }

        if (tile is { } t)
        {
            await plots.RequireDugOutAsync(x, y, t.X, t.Y, cancellationToken);
        }

        if (catchUp)
        {
            await CatchUpAsync(x, y, cancellationToken);
        }

        var kind = await TakeOffAsync(player, x, y, id, owner, cancellationToken)
            ?? throw new GameException(owner ? "no_object" : "not_yours", "There is no such object of yours on this plot.");
        await inventory.AddAsync(player, kind, 1, cancellationToken);
    }

    /// <summary>Takes everything off a rectangle of the plot (both corners included) that the player may pick up: on a
    /// company's plot a member takes only what they put there, the owner everything. Objects snow buries stay. Each
    /// object goes back into the inventory like one picked up by hand, even when that fills it past its tiles, so
    /// nothing is lost; all of them or none. Returns what was taken and how many objects in the rectangle stayed.</summary>
    public Task<(IReadOnlyList<PlacedOnPlot> Taken, int Left)> PickUpAreaAsync(
        Guid player, int x, int y, int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!PlotLayout.Inside(fromX, fromY) || !PlotLayout.Inside(toX, toY))
            {
                throw new GameException("bad_area", "Mark a rectangle inside the plot.");
            }

            await plots.RequireOwnerAsync(player, x, y, cancellationToken);
            var owner = (await plots.RightsAsync(player, x, y, cancellationToken)).Contains(PlotRight.Owner);
            var inside = new List<(PlacedOnPlot Object, Guid PutBy)>();
            await using (var find = await db.CommandAsync(
                """
                SELECT id, kind, tile_x, tile_y, rotation, owner_id FROM world.plot_objects
                WHERE x = @x AND y = @y AND tile_x BETWEEN @x0 AND @x1 AND tile_y BETWEEN @y0 AND @y1
                ORDER BY tile_y, tile_x
                FOR UPDATE
                """, cancellationToken))
            {
                find.Parameters.AddWithValue("x", x);
                find.Parameters.AddWithValue("y", y);
                find.Parameters.AddWithValue("x0", Math.Min(fromX, toX));
                find.Parameters.AddWithValue("x1", Math.Max(fromX, toX));
                find.Parameters.AddWithValue("y0", Math.Min(fromY, toY));
                find.Parameters.AddWithValue("y1", Math.Max(fromY, toY));
                await using var reader = await find.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    inside.Add((new PlacedOnPlot(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)),
                        reader.GetGuid(5)));
                }
            }

            var mine = new List<PlacedOnPlot>();
            foreach (var (o, putBy) in inside)
            {
                if ((owner || putBy == player) && !await plots.BuriedAsync(x, y, o.TileX, o.TileY, cancellationToken))
                {
                    mine.Add(o);
                }
            }

            if (mine.Count > PlotObjects.MaxArea)
            {
                throw new GameException("area_too_big", $"Pick up at most {PlotObjects.MaxArea} objects at once: mark a smaller area.");
            }

            if (mine.Count > 0)
            {
                await CatchUpAsync(x, y, cancellationToken);
            }

            foreach (var o in mine)
            {
                var kind = await TakeOffAsync(player, x, y, o.Id, owner, cancellationToken)
                    ?? throw new GameException("no_object", "There is no such object of yours on this plot.");
                await inventory.AddAsync(player, kind, 1, cancellationToken);
            }

            return ((IReadOnlyList<PlacedOnPlot>)mine, inside.Count - mine.Count);
        }, cancellationToken);

    /// <summary>Deletes the object (the player's own, or any when <paramref name="owner"/>) and gives the player back
    /// what the machinery in it held; returns its kind, or null when there is no such object of theirs.</summary>
    private async Task<string?> TakeOffAsync(Guid player, int x, int y, Guid id, bool owner, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync(
            """
            DELETE FROM world.plot_objects WHERE id = @id AND x = @x AND y = @y AND (owner_id = @player OR @owner)
            RETURNING kind
            """, cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        delete.Parameters.AddWithValue("x", x);
        delete.Parameters.AddWithValue("y", y);
        delete.Parameters.AddWithValue("player", player);
        delete.Parameters.AddWithValue("owner", owner);
        var kind = await delete.ExecuteScalarAsync(cancellationToken) as string;
        query.Forget();
        if (kind is null)
        {
            return null;
        }

        foreach (var machine in machines)
        {
            await machine.ReleaseAsync(player, id, cancellationToken);
        }

        return kind;
    }

    /// <summary>Any object on a plot the player may work on: their own, or one of their company's.</summary>
    public async Task<string?> KindAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        int x, y, tileX, tileY;
        string kind;
        await using (var command = await db.CommandAsync("SELECT x, y, kind, tile_x, tile_y FROM world.plot_objects WHERE id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            (x, y, kind, tileX, tileY) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
        }

        if (!(await plots.RightsAsync(owner, x, y, cancellationToken)).Contains(PlotRight.Work))
        {
            return null;
        }

        // Snow from an avalanche buries a machine on the glacier until it is dug out.
        await plots.RequireDugOutAsync(x, y, tileX, tileY, cancellationToken);
        return kind;
    }

    private async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken)
    {
        foreach (var machinery in automation)
        {
            await machinery.CatchUpAsync(x, y, cancellationToken);
        }
    }
}

/// <summary>The objects on plots for other modules; apart from <see cref="PlotObjectService"/>, which
/// brings the plot's machinery up to now first and so needs those modules itself.</summary>
/// <remarks>Within one transaction a plot's objects are read once: bringing a plot's machinery up to now asks
/// for them for every machine on it. <see cref="PlotObjectService"/> forgets them when it changes them.</remarks>
public sealed class PlotObjectsQuery(GameDb db, PlotService plots) : IPlotObjects
{
    private int _readIn;
    private readonly Dictionary<(int X, int Y), IReadOnlyList<PlotObject>> _objects = [];
    private readonly Dictionary<Guid, (int X, int Y)> _plotOf = [];

    public async Task<IReadOnlyList<PlotObject>> ObjectsAsync(int x, int y, CancellationToken cancellationToken)
    {
        if (Remembered() && _objects.TryGetValue((x, y), out var known))
        {
            return known;
        }

        await using var command = await db.CommandAsync(
            "SELECT id, kind, tile_x, tile_y, rotation, owner_id FROM world.plot_objects WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var objects = new List<PlotObject>();
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new PlotObject(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetGuid(5)));
        }

        if (Remembered())
        {
            _objects[(x, y)] = objects;
            foreach (var o in objects)
            {
                _plotOf[o.Id] = (x, y);
            }
        }

        return objects;
    }

    public async Task<IReadOnlyList<PlotObject>> FixturesAsync(int x, int y, CancellationToken cancellationToken)
    {
        if (await StationStock.OwnerAsync(db, x, y, cancellationToken) is not { } owner)
        {
            return [];
        }

        var station = RailRules.StationId(x, y);
        return RailRules.StationTiles().Select(t => new PlotObject(station, RailRules.StationKind, t.TileX, t.TileY, 0, owner)).ToList();
    }

    public Task RequireOwnerAsync(Guid player, int x, int y, CancellationToken cancellationToken, PlotRight right = PlotRight.Build) =>
        plots.RequireOwnerAsync(player, x, y, cancellationToken, right);

    public async Task<(int X, int Y)?> PlotOfAsync(Guid id, CancellationToken cancellationToken)
    {
        if (Remembered() && _plotOf.TryGetValue(id, out var known))
        {
            return known;
        }

        await using var command = await db.CommandAsync("SELECT x, y FROM world.plot_objects WHERE id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt32(0), reader.GetInt32(1)) : null;
    }

    /// <summary>Objects were put on a plot, turned or taken off it: they are read again.</summary>
    internal void Forget()
    {
        _objects.Clear();
        _plotOf.Clear();
    }

    /// <summary>Whether what was read can be used again: only in the transaction it was read in.</summary>
    private bool Remembered()
    {
        if (db.Transaction is null)
        {
            return false;
        }

        if (_readIn != db.TransactionNumber)
        {
            Forget();
            _readIn = db.TransactionNumber;
        }

        return true;
    }
}

public sealed record PlaceOnPlotRequest(int X, int Y, string Kind, int TileX, int TileY, int Rotation = 0);

public sealed record RotateOnPlotRequest(int X, int Y, Guid Id, int Rotation);

public sealed record PickUpFromPlotRequest(int X, int Y, Guid Id);

public sealed record PlaceManyRequest(int X, int Y, PlannedObject[]? Objects);

public sealed record PickUpManyRequest(int X, int Y, Guid[]? Ids);

/// <summary>Puts up a copied building plan in one go.</summary>
public sealed class PlaceManyOnPlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.placeMany";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlaceManyRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var placed = await scope.ServiceProvider.GetRequiredService<PlotObjectService>().PlaceManyAsync(
            client.RequireAccount(), request.X, request.Y, request.Objects ?? [], cancellationToken);
        return new Reply("plot.placedMany", new { objects = placed.Select(PlaceOnPlotHandler.ObjectJson) });
    }
}

/// <summary>Takes several objects off the plot in one go, e.g. to undo a building plan.</summary>
public sealed class PickUpManyFromPlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.pickupMany";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PickUpManyRequest>(payload);
        var ids = request.Ids ?? [];
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlotObjectService>()
            .PickUpManyAsync(client.RequireAccount(), request.X, request.Y, ids, cancellationToken);
        return new Reply("plot.pickedUpMany", new { ids });
    }
}

public sealed record PickUpAreaRequest(int X, int Y, int FromX, int FromY, int ToX, int ToY);

/// <summary>Clears a marked rectangle of the plot: everything of the player's in it goes back into the inventory.</summary>
public sealed class PickUpAreaFromPlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.pickupArea";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PickUpAreaRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (taken, left) = await scope.ServiceProvider.GetRequiredService<PlotObjectService>().PickUpAreaAsync(
            client.RequireAccount(), request.X, request.Y, request.FromX, request.FromY, request.ToX, request.ToY, cancellationToken);
        return new Reply("plot.pickedUpArea", new { objects = taken.Select(PlaceOnPlotHandler.ObjectJson), left });
    }
}

public sealed class PlaceOnPlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.place";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlaceOnPlotRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PlotObjectService>();
        var placed = await service.PlaceAsync(
            client.RequireAccount(), request.X, request.Y, request.Kind ?? "", request.TileX, request.TileY, request.Rotation, cancellationToken);
        return new Reply("plot.placed", new { @object = ObjectJson(placed), help = HelpJson(service.Help) });
    }

    /// <summary>A helper's progress on someone else's plot (<see cref="IPlotPermits"/>), or null for the plot's own people.</summary>
    public static object? HelpJson(HelpProgress? help) => help is null ? null : new
    {
        done = help.Done,
        of = help.Of,
        payCents = help.PayCents,
        finished = help.Finished,
        balanceCents = help.BalanceCents,
    };

    public static object ObjectJson(PlacedOnPlot placed) => new
    {
        id = placed.Id,
        kind = placed.Kind,
        name = Items.Names[placed.Kind],
        tileX = placed.TileX,
        tileY = placed.TileY,
        rotation = placed.Rotation,
    };
}

public sealed class PickUpFromPlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.pickup";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PickUpFromPlotRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlotObjectService>()
            .PickUpAsync(client.RequireAccount(), request.X, request.Y, request.Id, cancellationToken);
        return new Reply("plot.pickedUp", new { id = request.Id });
    }
}

public sealed class RotateOnPlotHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.rotate";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<RotateOnPlotRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PlotObjectService>();
        var turned = await service.RotateAsync(client.RequireAccount(), request.X, request.Y, request.Id, request.Rotation, cancellationToken);
        return new Reply("plot.rotated", new { @object = PlaceOnPlotHandler.ObjectJson(turned), help = PlaceOnPlotHandler.HelpJson(service.Help) });
    }
}
