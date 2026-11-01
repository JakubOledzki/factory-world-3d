using System.IO.Compression;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>The crafting tree: parts of many branches that make the launch rocket, the fusion reactor and the buildings of steelworks and megafactories.</summary>
public sealed class CraftingTreeTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private static readonly string[] Tree =
    [
        Items.Lubricant, Items.ElectricWire, Items.GlassLaminate, Items.Paint, Items.CaseinGlue, Items.InsulationBoard, Items.Firebrick,
        Items.PrecastConcrete, Items.Coolant, Items.IridiumAlloy, Items.Provisions, Items.Bearing, Items.PaintedSheet, Items.Coil,
        Items.WiringHarness, Items.RefractoryLining, Items.CarbonComposite, Items.SuperconductingWire, Items.RocketFuel,
        Items.Gearbox, Items.Rotor, Items.MachineHousing, Items.CombustionChamber, Items.CompositePanel, Items.SuperconductingMagnet,
        Items.Alternator, Items.ControlModule, Items.HydraulicActuator, Items.RocketNozzle, Items.PressureVessel, Items.ComputingCore,
        Items.RobotArm, Items.Turbopump, Items.TurbineSet, Items.CryogenicTank, Items.IndustrialRobot, Items.NavigationSystem,
        Items.RocketEngine, Items.PlasmaChamber, Items.Avionics, Items.PlasmaControl, Items.RocketStage, Items.Satellite,
        Items.FusionReactor, Items.LaunchRocket, Items.MachineShop, Items.RoboticHall, Items.OxygenConverter, Items.RollingMill,
        Items.ControlCenter, Items.ContinuousCaster, Items.AutomatedWarehouse, Items.SteelCombine, Items.Megafactory, Items.FusionPlant,
    ];

    private static IEnumerable<(string Id, int Quantity, IReadOnlyList<(string Item, int Quantity)> Ingredients)> Recipes(string id) =>
        Workshop.Recipes.Where(r => r.Id == id).Select(r => (r.Id, r.Quantity, r.Ingredients))
            .Concat(Processing.Machines.SelectMany(m => m.Recipes).Where(r => r.Id == id).Select(r => (r.Id, r.Quantity, r.Ingredients)));

    /// <summary>How many steps of making lie under an item, by its shortest recipe: 0 for what is gathered.</summary>
    private static int Depth(string id, Dictionary<string, int> known, HashSet<string> open)
    {
        if (known.TryGetValue(id, out var depth))
        {
            return depth;
        }

        var recipes = Recipes(id).Where(r => r.Ingredients.Count > 0).ToList();
        if (recipes.Count == 0 || !open.Add(id))
        {
            return 0;
        }

        depth = recipes.Min(r => 1 + r.Ingredients.Max(i => Depth(i.Item, known, open)));
        open.Remove(id);
        return known[id] = depth;
    }

    [Fact]
    public void Every_part_of_the_tree_has_a_name_a_price_a_recipe_and_its_own_technology()
    {
        Assert.Equal(Tree.Length, Tree.Distinct().Count());
        foreach (var id in Tree)
        {
            Assert.True(Items.Names.ContainsKey(id), id);
            Assert.True(NpcPrices.Buys.ContainsKey(id), id);
            Assert.True(Recipes(id).Any(), id);
            Assert.True(TechTree.ByItem.TryGetValue(id, out var technology), id);
            Assert.Equal([id], technology.Unlocks);
            Assert.All(technology.Requires, r => Assert.True(TechTree.ById.ContainsKey(r), r));
        }
    }

    [Fact]
    public void The_launch_rocket_lies_more_than_ten_levels_deep()
    {
        var known = new Dictionary<string, int>();
        Assert.True(Depth(Items.LaunchRocket, known, []) >= 10, $"only {known[Items.LaunchRocket]} levels");
        Assert.True(Depth(Items.FusionReactor, known, []) >= 9);
        Assert.True(Depth(Items.Megafactory, known, []) >= 9);
    }

    [Fact]
    public void Each_level_fetches_more_than_what_went_into_it()
    {
        foreach (var id in Tree)
        {
            foreach (var (_, quantity, ingredients) in Recipes(id))
            {
                var cost = ingredients.Sum(i => NpcPrices.Buys[i.Item] * i.Quantity);
                Assert.True(NpcPrices.Buys[id] * quantity >= cost, $"{id}: {NpcPrices.Buys[id] * quantity} for {cost} of parts");
            }
        }
    }

    [Fact]
    public void The_big_buildings_are_powered_and_the_fusion_plant_burns_coolant()
    {
        Assert.Equal(5, Footprints.Size(Items.Megafactory));
        Assert.Equal(5, Footprints.Size(Items.SteelCombine));
        Assert.Equal(3, Footprints.Size(Items.RoboticHall));
        Assert.Equal(2, Footprints.Size(Items.MachineShop));
        Assert.True(Items.IsGenerator(Items.FusionPlant));
        Assert.Contains(Items.Coolant, PowerRules.Generators[Items.FusionPlant].Burns);
        foreach (var kind in new[] { Items.MachineShop, Items.RoboticHall, Items.OxygenConverter, Items.RollingMill, Items.ContinuousCaster, Items.SteelCombine, Items.Megafactory })
        {
            Assert.True(Processing.ByKind[kind].Powered, kind);
            Assert.True(PowerRules.Demand.ContainsKey(kind), kind);
        }

        // The megafactory makes what the machine shop, the assembly line and the robotic hall make, three times as fast.
        var shop = Processing.ByKind[Items.MachineShop].Recipe(Items.Bearing)!;
        Assert.Equal(shop.Time / Processing.CombinedSpeed, Processing.ByKind[Items.Megafactory].Recipe(Items.Bearing)!.Time);
        Assert.NotNull(Processing.ByKind[Items.Megafactory].Recipe(Items.IndustrialRobot));
        Assert.NotNull(Processing.ByKind[Items.SteelCombine].Recipe(Items.PaintedSheet));
    }

    [Fact]
    public void A_control_center_speeds_up_the_machines_on_power()
    {
        var plain = Processing.For(Items.MachineShop, 1)!.Recipe(Items.Bearing)!;
        var controlled = Processing.For(Items.MachineShop, 1, controlled: true)!.Recipe(Items.Bearing)!;
        Assert.Equal(plain.Time / Processing.ControlBonus, controlled.Time);

        // A machine off the grid gets nothing from it.
        Assert.Same(Processing.For(Items.Kitchen, 1), Processing.For(Items.Kitchen, 1, controlled: true));
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 12000;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    [Fact]
    public async Task A_megafactory_covers_five_by_five_tiles_and_a_plot_has_one_control_center()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            await inventory.AddAsync(client.AccountId, Items.Megafactory, 1, CancellationToken.None);
            await inventory.AddAsync(client.AccountId, Items.WoodenChest, 2, CancellationToken.None);
            await inventory.AddAsync(client.AccountId, Items.ControlCenter, 2, CancellationToken.None);
        }

        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        bool Free(int tx, int ty, int reach)
        {
            for (var dy = -reach; dy <= reach; dy++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    if (bytes[(ty + dy) * PlotLayout.Size + tx + dx] != (byte)TileObject.None)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        var (cx, cy) = Enumerable.Range(25, PlotLayout.Size - 40).SelectMany(ty => Enumerable.Range(10, PlotLayout.Size - 20).Select(tx => (tx, ty)))
            .First(t => Free(t.tx, t.ty, 5));
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.Megafactory, tileX = cx, tileY = cy })).Type());

        // Two tiles off its middle is still under it, three is not.
        Assert.Equal("blocked", (await client.SendAsync("plot.place", new { x, y, kind = Items.WoodenChest, tileX = cx + 2, tileY = cy - 2 })).ErrorCode());
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.WoodenChest, tileX = cx + 3, tileY = cy })).Type());

        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.ControlCenter, tileX = cx - 3, tileY = cy })).Type());
        Assert.NotEqual("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.ControlCenter, tileX = cx, tileY = cy + 4 })).Type());
    }
}
