namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// The tiles an object covers on a plot: a square of <see cref="Size"/> × <see cref="Size"/> tiles. Belts, inserters, chests,
/// poles and the like stand on one tile. Machines grow with their level: the first level of a machine (a stone furnace, a crusher,
/// a mill, a sawmill, an assembling machine) covers 2 × 2, so two inserters fit along each side; the next (a brick furnace, an electric
/// sawmill, a roller mill, a cone crusher, a foundry, a refinery) 3 × 3 and the best (a blast furnace, an industrial sawmill, flour works,
/// an open-pit mine, an ore processing plant) 4 × 4. A forester's building, a solar tower,
/// the halls of the crafting tree and the like cover 3 × 3, the steel combine, the megafactory and a bank 5 × 5, a rail port 9 × 9.
/// </summary>
/// <remarks>An odd square stands centred on the object's tile (its <see cref="PlotObject.TileX"/>, <see cref="PlotObject.TileY"/>); an even
/// one has that tile as the top left of its middle, so a 2 × 2 covers its own tile and those to the right and below it.</remarks>
public static class Footprints
{
    /// <summary>How many tiles wide (and deep) an object of <paramref name="kind"/> is.</summary>
    public static int Size(string kind) => Sizes.GetValueOrDefault(kind, 1);

    /// <summary>How many tiles the object reaches out from its own tile towards the top and the left.</summary>
    public static int Low(string kind) => (Size(kind) - 1) / 2;

    /// <summary>How many tiles the object reaches out from its own tile towards the bottom and the right.</summary>
    public static int High(string kind) => Size(kind) / 2;

    /// <summary>How many tiles an object centred on its tile reaches out each way: 0 for one tile, 1 for 3 × 3, 2 for 5 × 5, 4 for 9 × 9.</summary>
    public static int Reach(string kind) => Low(kind);

    /// <summary>Every tile an object of <paramref name="kind"/> standing on (x, y) covers.</summary>
    public static IEnumerable<(int X, int Y)> Tiles(string kind, int x, int y)
    {
        var (low, high) = (Low(kind), High(kind));
        for (var dy = -low; dy <= high; dy++)
        {
            for (var dx = -low; dx <= high; dx++)
            {
                yield return (x + dx, y + dy);
            }
        }
    }

    public static bool Covers(string kind, int x, int y, int tileX, int tileY) =>
        tileX >= x - Low(kind) && tileX <= x + High(kind) && tileY >= y - Low(kind) && tileY <= y + High(kind);

    /// <summary>The object once on every tile it covers, all with its id: for whatever looks objects up by tile.</summary>
    public static IEnumerable<PlotObject> Spread(PlotObject o) =>
        Size(o.Kind) == 1 ? [o] : Tiles(o.Kind, o.TileX, o.TileY).Select(t => o with { TileX = t.X, TileY = t.Y });

    /// <summary>Kinds on more than one tile and their sizes, side by side for queries:
    /// <c>unnest(@kinds, @sizes) AS f(kind, size)</c>, with an object covering <c>tile_x - (size - 1) / 2</c> to <c>tile_x + size / 2</c>.</summary>
    public static readonly string[] SizedKinds;

    /// <inheritdoc cref="SizedKinds"/>
    public static readonly int[] SizedSizes;

    /// <summary>The SQL that tells whether an object row <paramref name="o"/> covers the tile (<paramref name="tx"/>, <paramref name="ty"/>),
    /// with <see cref="SizedKinds"/> and <see cref="SizedSizes"/> passed as <c>@sizedKinds</c> and <c>@sizedSizes</c>.</summary>
    public static string CoversSql(string o, string tx, string ty) =>
        $"""
        (({o}.tile_x = {tx} AND {o}.tile_y = {ty}) OR EXISTS (SELECT 1 FROM unnest(@sizedKinds::text[], @sizedSizes::int[]) AS f(kind, size)
            WHERE f.kind = {o}.kind AND {tx} BETWEEN {o}.tile_x - (f.size - 1) / 2 AND {o}.tile_x + f.size / 2
            AND {ty} BETWEEN {o}.tile_y - (f.size - 1) / 2 AND {o}.tile_y + f.size / 2))
        """;

    /// <summary>Goes up by one whenever <see cref="Sizes"/> grows or a sized kind may stand on fewer plots (<see cref="Items.IsCityOnly"/>):
    /// on the next start the world's footprint migration moves, or gives back, whatever no longer fits on plots built before.</summary>
    public const int Version = 2;

    /// <summary>What stands on more than one tile, and on how many each way.</summary>
    public static readonly IReadOnlyDictionary<string, int> Sizes = new Dictionary<string, int>
    {
        // The first level of a machine: 2 × 2.
        [Items.Furnace] = 2,
        [Items.Mill] = 2,
        [Items.EnergyStore] = 2,
        [Items.OilPress] = 2,
        [Items.PicklingBarrel] = 2,
        [Items.Bakery] = 2,
        [Items.Kitchen] = 2,
        [Items.Sawmill] = 2,
        [Items.Quarry] = 2,
        [Items.Crusher] = 2,
        [Items.Stonemason] = 2,
        [Items.Smokehouse] = 2,
        [Items.Cannery] = 2,
        [Items.Distillery] = 2,
        [Items.Glassworks] = 2,
        [Items.CementKiln] = 2,
        [Items.ConcreteMixer] = 2,
        [Items.IceMelter] = 2,
        [Items.ColdStore] = 2,
        [Items.PaperMill] = 2,
        [Items.Carpentry] = 2,
        [Items.CharcoalKiln] = 2,
        [Items.TarKiln] = 2,
        [Items.Dairy] = 2,
        [Items.Slaughterhouse] = 2,
        [Items.PotatoDistillery] = 2,
        [Items.MachineShop] = 2,
        [Items.MultistoreyFactory] = 2,
        [Items.Assembler] = 2,
        [Items.SpinningMill] = 2,
        [Items.Loom] = 2,
        [Items.SugarFactory] = 2,
        [Items.Jeweler] = 2,
        [Items.Electrolyzer] = 2,
        [Items.TreeNursery] = 2,
        [Items.AutoBarn] = 2,
        [Items.FuelCell] = 2,
        // The second level: 3 × 3.
        [Items.BrickFurnace] = 3,
        [Items.ElectricSawmill] = 3,
        [Items.Foundry] = 3,
        [Items.Refinery] = 3,
        [Items.Cracker] = 3,
        [Items.ChemicalPlant] = 3,
        [Items.ArcFurnace] = 3,
        [Items.FastAssembler] = 3,
        [Items.RollerMill] = 3,
        [Items.MechanizedQuarry] = 3,
        [Items.ConeCrusher] = 3,
        [Items.ForesterHut] = 3,
        [Items.ForesterLodge] = 3,
        [Items.ForestryStation] = 3,
        [Items.SolarTower] = 3,
        [Items.LargeWindTurbine] = 3,
        [Items.IceHotel] = 3,
        [Items.AssemblyHall] = 3,
        [Items.RoboticHall] = 3,
        [Items.OxygenConverter] = 3,
        [Items.RollingMill] = 3,
        [Items.ContinuousCaster] = 3,
        [Items.AutomatedWarehouse] = 3,
        [Items.FusionPlant] = 3,
        [Items.CoalPlant] = 3,
        [Items.Canteen] = 3,
        [Items.BiogasPlant] = 3,
        [Items.Farmstead] = 3,
        [Items.ManorFarm] = 3,
        [Items.BuyMarket] = 3,
        [Items.BankBranch] = 3,
        [Items.CokeOven] = 3,
        [Items.BiomassPlant] = 3,
        [Items.ChpPlant] = 3,
        [Items.GeothermalPlant] = 3,
        // The best level: 4 × 4.
        [Items.BlastFurnace] = 4,
        [Items.IndustrialSawmill] = 4,
        [Items.ExpressAssembler] = 4,
        [Items.FlourWorks] = 4,
        [Items.OpenPitMine] = 4,
        [Items.OreProcessingPlant] = 4,
        [Items.NuclearPlant] = 4,
        [Items.MechanizedFarm] = 4,
        // The largest buildings.
        [Items.SteelCombine] = 5,
        [Items.Megafactory] = 5,
        [Items.Bank] = 5,
        [Items.RailPort] = 9,
    };

    static Footprints()
    {
        SizedKinds = Sizes.Keys.ToArray();
        SizedSizes = SizedKinds.Select(k => Sizes[k]).ToArray();
    }
}
