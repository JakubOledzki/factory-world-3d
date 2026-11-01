using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class MountainIndustryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1);

    private static PlotObject Obj(string kind, int x, int y, int rotation = 1) => new(Guid.NewGuid(), kind, x, y, rotation, Guid.Empty);

    [Fact]
    public void Mountains_mine_thirty_percent_faster_and_better_pickaxes_faster_still()
    {
        Assert.Equal(30, Mining.BonusPercent(Biome.Mountains));
        Assert.Equal(0, Mining.BonusPercent(Biome.Grassland));
        Assert.Equal(1.3, Mining.Speed(Biome.Mountains), 6);
        Assert.True(Items.Pickaxes[Items.SteelPickaxe] > Items.Pickaxes[Items.BronzePickaxe]);
        Assert.True(Items.Pickaxes[Items.BronzePickaxe] > Items.Pickaxes[Items.Pickaxe]);
        Assert.All(Items.Pickaxes.Keys, p => Assert.True(Items.IsTool(p), p));
    }

    [Fact]
    public void Deep_mine_levels_need_supports_and_bring_up_silver_and_gold_more_often()
    {
        Assert.Equal(5, Mining.Levels.Count);
        Assert.Contains(Mining.Levels[3].Cost, c => c.Item == Items.MineSupport);
        Assert.Contains(Mining.Levels[4].Cost, c => c.Item == Items.MineSupport);
        int PerMille(MineLevel level, string item) => Mining.DropsFor(level, Mineral.None).First(d => d.Item == item).PerMille;
        Assert.Equal(PerMille(Mining.Levels[0], Items.SilverOre) * 3, PerMille(Mining.Levels[4], Items.SilverOre));
        Assert.Equal(PerMille(Mining.Levels[0], Items.GoldOre) * 3, PerMille(Mining.Levels[4], Items.GoldOre));
        Assert.Equal(PerMille(Mining.Levels[0], Items.IronOre), PerMille(Mining.Levels[4], Items.IronOre));
    }

    [Fact]
    public void Some_mountains_hold_a_mineral_that_their_mines_bring_up_too()
    {
        var found = new Dictionary<MineralKind, int>();
        for (var y = 0; y < Map.Height; y += 3)
        {
            for (var x = 0; x < Map.Width; x += 3)
            {
                var biome = Map.BiomeAt(x, y);
                var mineral = Deposits.MineralIn(x, y, biome);
                if (biome != Biome.Mountains)
                {
                    Assert.Equal(MineralKind.None, mineral.Kind);
                }
                else if (mineral.Kind != MineralKind.None)
                {
                    found[mineral.Kind] = found.GetValueOrDefault(mineral.Kind) + 1;
                }
            }
        }

        Assert.Equal(4, found.Count);
        var marble = new Mineral(MineralKind.Marble, DepositSize.Large);
        Assert.Equal(Items.Marble, marble.Item);
        Assert.Equal(Mining.MineralPerMille(DepositSize.Large), Mining.DropsFor(Mining.Levels[0], marble).First(d => d.Item == Items.Marble).PerMille);
    }

    [Fact]
    public void Water_turns_wheels_without_fuel_and_generators_burn_only_for_what_is_missing()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var wheel = Obj(Items.WaterWheel, 1, 0);
        var mill = Obj(Items.Mill, -1, 0);
        var grid = new PowerGrid(0, 0, [pole, wheel, mill], new GridData(), Start, (_, _, _) => Task.CompletedTask, (int)Biome.Lakeshore);

        // A wheel gives 30 kW, just what a mill draws, for ever.
        Assert.Equal(1, grid.Clock(mill.Id).Speed(Start + TimeSpan.FromDays(3)));
        Assert.Equal(30, grid.Output(wheel.Id, Start));

        // In the mountains the water runs 15% faster; two crushers (80 kW) are short of one wheel.
        var crushers = new[] { Obj(Items.Crusher, -1, 1), Obj(Items.Crusher, 1, 1) };
        var mountains = new PowerGrid(0, 0, [pole, wheel, .. crushers], new GridData(), Start, (_, _, _) => Task.CompletedTask, (int)Biome.Mountains);
        Assert.Equal(34.5 / 80, mountains.Clock(crushers[0].Id).Speed(Start), 6);

        // With a generator as well, the generator makes up the rest and burns slower for it.
        var generator = Obj(Items.Generator, 0, 1);
        var data = new GridData { Generators = { [generator.Id] = new GeneratorState(Items.Coal, 1, 0) } };
        var mixed = new PowerGrid(0, 0, [pole, wheel, generator, .. crushers], data, Start, (_, _, _) => Task.CompletedTask, (int)Biome.Mountains);
        Assert.Equal(1, mixed.Clock(crushers[0].Id).Speed(Start));
        Assert.InRange((mixed.RunsOutAt(generator.Id)!.Value - Start).TotalSeconds, 20_000_000.0 / 1000 / 45.5 - 1, 20_000_000.0 / 1000 / 45.5 + 1);
    }

    [Fact]
    public void Cable_stations_carry_items_over_the_cable_to_the_next_station_ahead()
    {
        // Rotation 1 faces east.
        var feed = Obj(Items.Belt, 0, 0);
        var a = Obj(Items.CableStation, 1, 0);
        var wall = Obj(Items.WoodenChest, 2, 0, 0);
        var b = Obj(Items.CableStation, 12, 0);
        var turned = Obj(Items.CableStation, 30, 0, 0);
        var after = Obj(Items.Belt, 13, 0);
        var links = LogisticsRules.CableLinks([feed, a, wall, b, turned, after]);
        Assert.Equal(b.Id, links[a.Id]);
        Assert.False(links.ContainsKey(b.Id));
        Assert.False(links.ContainsKey(turned.Id));

        var contents = new Dictionary<Guid, Contents> { [feed.Id] = new() { Belt = [Items.IronOre, null, null, null, null, null, null, null] } };
        var logistics = new PlotLogistics([feed, a, wall, b, after], contents, new Dictionary<Guid, IMachinePort>());
        logistics.Run(Start, Start + LogisticsRules.Tick * 20);
        Assert.Equal(Items.IronOre, contents[after.Id].Belt![3]);
    }

    [Fact]
    public void Processing_turns_stone_and_ore_into_more_and_bronze_and_steel()
    {
        Assert.Empty(Processing.ByKind[Items.Quarry].Recipe(Items.Stone)!.Ingredients);
        Assert.True(Processing.ByKind[Items.Crusher].Powered);
        Assert.True(Processing.ByKind[Items.Foundry].Fired);
        var crushed = Processing.ByKind[Items.Crusher].Recipe(Items.CrushedIronOre)!;
        Assert.Equal(3, crushed.Quantity);
        Assert.Equal([(Items.IronOre, 2)], crushed.Ingredients);
        Assert.Equal(Items.IronIngot, Smelting.Recipes.First(r => r.Input == Items.CrushedIronOre).Output);
        Assert.NotNull(Processing.ByKind[Items.Foundry].Recipe(Items.BronzeIngot));
        Assert.NotNull(Processing.ByKind[Items.Foundry].Recipe(Items.SteelIngot));
        Assert.NotNull(Processing.ByKind[Items.Stonemason].Recipe(Items.MarbleSlabs));
    }

    [Fact]
    public void The_mining_branch_of_the_tree_unlocks_the_mountain_industry()
    {
        Assert.Equal(TechTree.MiningAutomation, TechTree.ByItem[Items.Drill].Id);
        Assert.Equal(TechTree.MiningAutomation, TechTree.ByItem[Items.Quarry].Id);
        Assert.Equal(TechTree.Crushing, TechTree.ByItem[Items.Crusher].Id);
        Assert.Equal(TechTree.Metallurgy, TechTree.ByItem[Items.Foundry].Id);
        Assert.Equal(TechTree.DeepMining, TechTree.ByItem[Items.MineSupport].Id);
        Assert.Equal(TechTree.HydroPlant, TechTree.ByItem[Items.HydroPlant].Id);
        Assert.Equal(TechTree.CableTransport, TechTree.ByItem[Items.CableStation].Id);
        Assert.Contains(TechTree.MiningAutomation, TechTree.ById[TechTree.DeepMining].Requires);
        Assert.Contains(Items.Drill, Workshop.Find(Items.HeavyDrill)!.Ingredients.Select(i => i.Item));

        // Every new thing has a price at the trader.
        Assert.All(new[] { Items.Sulfur, Items.Salt, Items.Marble, Items.Quartz, Items.BronzeIngot, Items.SteelIngot, Items.Drill, Items.CableStation },
            item => Assert.True(NpcPrices.Buys.ContainsKey(item), item));
    }

    // --- On a plot -------------------------------------------------------------------------------

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
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

    private static async Task<(int X, int Y, JsonElement Plot)> BuyAsync(GameClient client, Biome biome)
    {
        var (x, y) = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        return (x, y, (await client.SendAsync("plot.enter", new { x, y })).Payload());
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = 0)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.True(reply.Type() == "plot.placed", reply.ToString());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static async Task<string?> PlaceErrorAsync(GameClient client, int x, int y, string kind, int tileX, int tileY) =>
        (await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation = 0 })).ErrorCode();

    [Fact]
    public async Task A_drill_next_to_a_mine_works_it_on_power_and_keeps_the_ore_until_taken_out()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Mountains);
        var mine = plot.GetProperty("mines")[0];
        var (mx, my) = (mine.GetProperty("x").GetInt32(), mine.GetProperty("y").GetInt32());
        await GiveAsync(client, Items.Drill, 2);
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 20);

        // It has to touch a mine, one drill to a mine.
        Assert.Equal("no_mine_here", await PlaceErrorAsync(client, x, y, Items.Drill, mx + 1, my + 5));
        var drill = await PlaceAsync(client, x, y, Items.Drill, mx + 1, my + MineSite.Height);
        Assert.Equal("mine_has_drill", await PlaceErrorAsync(client, x, y, Items.Drill, mx + 2, my + MineSite.Height));

        // Without power it stands still.
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var idle = (await client.SendAsync("drill.get", new { x, y, id = drill })).Payload();
        Assert.Equal(0, idle.GetProperty("stored").GetInt32());
        Assert.Equal(0, idle.GetProperty("speed").GetDouble());

        await PlaceAsync(client, x, y, Items.Pole, mx + 2, my + MineSite.Height + 1);
        var generator = await PlaceAsync(client, x, y, Items.Generator, mx + 3, my + MineSite.Height + 1);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());

        // A load takes 6 s, 30% less in the mountains.
        var seconds = 6 / Mining.Speed(Biome.Mountains);
        factory.Time.Advance(TimeSpan.FromSeconds(seconds * 10 + 0.1));
        var working = (await client.SendAsync("drill.get", new { x, y, id = drill })).Payload();
        Assert.Equal(seconds, working.GetProperty("seconds").GetDouble(), 6);
        Assert.Equal(Mining.Units - 10, working.GetProperty("unitsLeft").GetInt32());
        var stored = working.GetProperty("stored").GetInt32();
        Assert.True(stored > 0, "nothing mined");

        // The player's mine shows the loads it took.
        var mines = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("mines");
        Assert.Equal(Mining.Units - 10, mines[0].GetProperty("units").GetInt32());

        var taken = (await client.SendAsync("drill.take", new { x, y, id = drill })).Payload();
        Assert.Equal(stored, taken.GetProperty("taken").EnumerateArray().Sum(t => t.GetProperty("quantity").GetInt32()));
        Assert.Equal(0, taken.GetProperty("stored").GetInt32());
        var inventory = await client.InventoryAsync();
        Assert.All(taken.GetProperty("taken").EnumerateArray(), t =>
            Assert.Equal(t.GetProperty("quantity").GetInt32(), inventory.GetValueOrDefault(t.GetProperty("id").GetString()!)));
    }

    [Fact]
    public async Task Dynamite_blasts_five_loads_out_of_a_mine_at_once()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Mountains);
        var mine = plot.GetProperty("mines")[0].GetProperty("id").GetInt32();
        Assert.Equal("not_enough_items", (await client.SendAsync("plot.blast", new { x, y, mine })).ErrorCode());

        await GiveAsync(client, Items.Dynamite, 1);
        var blasted = (await client.SendAsync("plot.blast", new { x, y, mine })).Payload();
        Assert.Equal(Mining.Units - Mining.BlastLoads, blasted.GetProperty("mine").GetProperty("units").GetInt32());
        Assert.Equal(Mining.BlastLoads, blasted.GetProperty("blasted").GetInt32());
        Assert.Equal(0, (await client.InventoryAsync()).GetValueOrDefault(Items.Dynamite));
    }

    [Fact]
    public async Task Quarries_stand_only_in_the_mountains_and_water_wheels_only_by_water()
    {
        using var client = await PlayerAsync();
        var (x, y, _) = await BuyAsync(client, Biome.Grassland);
        await GiveAsync(client, Items.Quarry, 1);
        await GiveAsync(client, Items.WaterWheel, 1);
        var (tx, ty) = (PlotLayout.Spawn.X + 6, PlotLayout.Spawn.Y + 6);
        Assert.Equal("no_quarry", await PlaceErrorAsync(client, x, y, Items.Quarry, tx, ty));
        Assert.Equal("no_water", await PlaceErrorAsync(client, x, y, Items.WaterWheel, tx, ty));
    }

    [Fact]
    public void A_heap_takes_crushed_ore_too()
    {
        var heap = Storages.Find(Items.Heap)!;
        Assert.All([Items.CrushedIronOre, Items.CrushedCopperOre, Items.CrushedTinOre], ore => Assert.True(heap.Takes(ore), ore));
    }

    [Fact]
    public async Task Inserters_carry_ore_through_a_crusher_onto_a_heap()
    {
        using var client = await PlayerAsync();
        var (x, y, _) = await BuyAsync(client, Biome.Grassland);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryWorld.Modules.Abstractions.GameDb>();
            await using var command = await db.CommandAsync(
                "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
            command.Parameters.AddWithValue("owner", client.AccountId);
            command.Parameters.AddWithValue("id", TechTree.Crushing);
            command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await GiveAsync(client, Items.Crusher, 1);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Heap, 1);
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 20);
        await GiveAsync(client, Items.IronOre, 4);

        // A chest of ore, an inserter, the crusher (2 × 2), an inserter and the heap, in a line facing south; powered from the side.
        var (tx, ty) = (PlotLayout.Spawn.X + 4, PlotLayout.Spawn.Y + 4);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty - 2);
        await PlaceAsync(client, x, y, Items.Inserter, tx, ty - 1, 0);
        var crusher = await PlaceAsync(client, x, y, Items.Crusher, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx, ty + 2, 0);
        var heap = await PlaceAsync(client, x, y, Items.Heap, tx, ty + 3);
        await PlaceAsync(client, x, y, Items.Pole, tx + 2, ty + 1);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx + 3, ty + 1);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = crusher, recipe = Items.CrushedIronOre })).Type());
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.IronOre, quantity = 4 })).Type());

        // Two batches of three from four ores, all of it out of the crusher.
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        var storages = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("storages")
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        var stored = storages[heap].GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() == Items.CrushedIronOre).Sum(i => i.GetProperty("quantity").GetInt32());
        Assert.Equal(6, stored);
        Assert.Equal(JsonValueKind.Null, (await client.SendAsync("machine.get", new { machine = crusher })).Payload().GetProperty("output").ValueKind);
    }
}
