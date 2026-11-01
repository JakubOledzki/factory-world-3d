using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>The raw-material branches grown further: mining, forestry, husbandry, farming and energy.</summary>
public sealed class ResourceBranchTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private const int East = 1;

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    // --- The rules, without a database ----------------------------------------------------------

    public static TheoryData<string, string, string[]> NewTechnologies => new()
    {
        { TechTree.Geology, "mining", [] },
        { TechTree.Coking, "mining", [Items.CokeOven] },
        { TechTree.PreciousMetals, "mining", [Items.Jeweler] },
        { TechTree.TreeNursery, "forestry", [Items.TreeNursery] },
        { TechTree.Textiles, "husbandry", [Items.SpinningMill, Items.Loom] },
        { TechTree.AutoBarns, "husbandry", [Items.AutoBarn] },
        { TechTree.PigsAndGoats, "husbandry", [] },
        { TechTree.Irrigation, "farming", [Items.Sprinkler] },
        { TechTree.MechanizedFarming, "farming", [Items.MechanizedFarm] },
        { TechTree.SugarBeets, "farming", [Items.SugarFactory] },
        { TechTree.FiberCrops, "farming", [] },
        { TechTree.BiomassPower, "energy", [Items.BiomassPlant] },
        { TechTree.Cogeneration, "energy", [Items.ChpPlant] },
        { TechTree.GeothermalPower, "energy", [Items.GeothermalPlant] },
        { TechTree.HydrogenPower, "energy", [Items.Electrolyzer, Items.FuelCell] },
    };

    [Theory]
    [MemberData(nameof(NewTechnologies))]
    public void Each_new_technology_sits_in_its_branch_and_its_buildings_are_made_at_the_workbench_and_stand_on_plots(
        string technology, string branch, string[] buildings)
    {
        Assert.Equal(branch, TechTree.BranchOf[technology].Id);
        var tech = TechTree.ById[technology];
        Assert.All(tech.Requires, r => Assert.True(TechTree.ById.ContainsKey(r), r));
        Assert.All(tech.Cost, c => Assert.True(Items.Exists(c.Item), c.Item));
        foreach (var building in buildings)
        {
            Assert.Contains(building, tech.Unlocks);
            Assert.NotNull(Workshop.Find(building));
            Assert.Contains(building, PlotObjects.Placeable);
            Assert.True(NpcPrices.Sells[building] > NpcPrices.Buys[building], building);
        }

        Assert.All(tech.Unlocks, item => Assert.True(NpcPrices.Sells[item] > NpcPrices.Buys[item], item));
    }

    [Fact]
    public void Farming_has_a_branch_of_its_own_with_the_farmsteads_moved_out_of_husbandry()
    {
        Assert.Equal("farming", TechTree.BranchOf[TechTree.Farmsteads].Id);
        Assert.Equal("farming", TechTree.BranchOf[TechTree.ManorFarms].Id);
        Assert.Equal(3, Farmsteads.Level(Items.MechanizedFarm)!.Level);
        Assert.Equal(4, Footprints.Size(Items.MechanizedFarm));
        Assert.Contains((Items.ManorFarm, 1), Workshop.Find(Items.MechanizedFarm)!.Ingredients);
        foreach (var crop in new[] { Items.SugarBeet, Items.Flax, Items.Hemp })
        {
            Assert.True(Farming.ById.ContainsKey(crop), crop);
            Assert.NotNull(TechTree.ByItem[crop]);
        }
    }

    [Fact]
    public void Coke_smelts_in_a_furnace_as_much_as_two_coal_and_comes_out_of_four_coal_three_at_a_time_with_tar()
    {
        var level = Smelting.Level(Items.BlastFurnace)!;
        var heat = Smelting.CokeHeat(level);
        Assert.Equal(2 * level.SmeltsPerCoal, heat);
        var state = new FurnaceState(Items.IronOre, 100, 0, heat, null, 0, Start);
        var after = Smelting.Advance(state, level, Start + TimeSpan.FromHours(1));
        Assert.Equal(heat, after.OutputQuantity);
        Assert.Equal(Items.IronIngot, after.Output);
        Assert.Null(after.StartedAt);
        Assert.False(Smelting.CokeFits(state with { Heat = level.MaxFuel * level.SmeltsPerCoal }, level, 1));

        var oven = Processing.ByKind[Items.CokeOven].Recipe(Items.Coke)!;
        Assert.Equal(3, oven.Quantity);
        Assert.Contains((Items.Coal, 4), oven.Ingredients);
        Assert.Contains((Items.Tar, 1), oven.Extras);
        Assert.Equal(2 * Processing.Fuels[Items.Coal], Processing.Fuels[Items.Coke]);
        Assert.True(PowerRules.Burns(Items.CoalPlant, Items.Coke));
        Assert.True(PowerRules.Burns(Items.Heater, Items.Coke));
    }

    [Fact]
    public void Silver_and_gold_ore_are_crushed_and_smelted_and_worked_into_jewellery_and_tableware()
    {
        foreach (var crusher in new[] { Items.Crusher, Items.ConeCrusher, Items.OreProcessingPlant })
        {
            Assert.NotNull(Processing.ByKind[crusher].Recipe(Items.CrushedSilverOre));
            Assert.NotNull(Processing.ByKind[crusher].Recipe(Items.CrushedGoldOre));
        }

        Assert.Equal(Items.SilverIngot, Smelting.For(Items.CrushedSilverOre)!.Output);
        Assert.Equal(Items.GoldIngot, Smelting.For(Items.CrushedGoldOre)!.Output);
        var jeweler = Processing.ByKind[Items.Jeweler];
        Assert.Contains((Items.Amber, 1), jeweler.Recipe(Items.Jewelry)!.Ingredients);
        Assert.Contains((Items.SilverIngot, 2), jeweler.Recipe(Items.Silverware)!.Ingredients);
        // A crushed ore is worth less to the trader than the ingot it smelts into, and two ores less than the three crushed ones.
        Assert.True(NpcPrices.Buys[Items.CrushedGoldOre] < NpcPrices.Buys[Items.GoldIngot]);
        Assert.True(2 * NpcPrices.Buys[Items.GoldOre] < 3 * NpcPrices.Buys[Items.CrushedGoldOre]);
    }

    [Fact]
    public void Geology_makes_the_mineral_of_a_plot_come_up_twice_as_often()
    {
        var plain = new Mineral(MineralKind.Quartz, DepositSize.Medium);
        var level = Mining.Level(1);
        int Chance(Mineral m) => Mining.DropsFor(level, m).First(d => d.Item == Items.Quartz).PerMille;
        Assert.Equal(2 * Chance(plain), Chance(plain with { Rich = true }));
        Assert.Equal(GeologyTech.Id, TechTree.Geology);
        Assert.Empty(TechTree.ById[TechTree.Geology].Unlocks);
    }

    [Fact]
    public void A_tree_nursery_makes_the_forests_of_the_foresters_near_it_grow_faster()
    {
        PlotObject Nursery(int x, int y) => new(Guid.NewGuid(), Items.TreeNursery, x, y, 0, Guid.NewGuid());
        Assert.True(Foresters.Nursed([Nursery(20, 20)], 20 + Foresters.NurseryReach, 20 - Foresters.NurseryReach));
        Assert.False(Foresters.Nursed([Nursery(20, 20)], 20 + Foresters.NurseryReach + 1, 20));
        Assert.Equal(1.5, Foresters.NurserySpeed);
    }

    [Fact]
    public void An_automatic_barn_collects_what_the_animals_in_its_reach_give_and_feeds_them_again_while_its_feed_lasts()
    {
        var barn = new Trough(Guid.NewGuid(), Guid.NewGuid(), Items.AutoBarn, 10, 10, new Dictionary<string, int> { [Items.Wheat] = 6 });
        // Two cows side by side, both ready, the tenth collection of each due: each gives a calf.
        var first = new Beast(11, 10, Items.Cow, Start, 9);
        var second = new Beast(12, 10, Items.Cow, Start, 9);
        var far = new Beast(10 + Feeders.BarnReach + 1, 10, Items.Chicken, Start, 0);
        var yard = new Yard(Biome.Grassland, [barn], [first, second, far]);
        yard.Feed(Start + TimeSpan.FromMinutes(20));

        // On the steppe a cow gives every 9 minutes: the first is collected from at 0, 9 and 18 minutes and eats all three meals,
        // the second once, and then waits hungry. The hen stands out of reach.
        Assert.Equal(12, barn.Output[Items.Milk]);
        Assert.Equal(8, barn.Output[Items.Manure]);
        Assert.Equal(2, barn.Output[Items.Cow]);
        Assert.Equal(0, barn.Feed);
        Assert.Equal(Start + TimeSpan.FromMinutes(27), first.ReadyAt);
        Assert.Equal(12, first.Meals);
        Assert.Null(second.ReadyAt);
        Assert.Equal(Start, far.ReadyAt);
        Assert.Equal(Start + TimeSpan.FromMinutes(27), yard.NextReady(barn, Start + TimeSpan.FromMinutes(20)));

        // Full, it leaves the animals be.
        var full = new Trough(Guid.NewGuid(), Guid.NewGuid(), Items.AutoBarn, 10, 10, new Dictionary<string, int>(),
            new Dictionary<string, int> { [Items.Egg] = Feeders.BarnOutput });
        var hen = new Beast(11, 11, Items.Chicken, Start, 0);
        new Yard(Biome.Grassland, [full], [hen]).Feed(Start + TimeSpan.FromMinutes(1));
        Assert.Equal(Start, hen.ReadyAt);
    }

    [Fact]
    public void Pigs_eat_leftovers_and_give_meat_and_goats_give_milk_often()
    {
        var pig = Husbandry.ById[Items.Pig];
        Assert.Equal(Items.Meat, pig.Product);
        Assert.Contains(Items.Stillage, pig.Eats);
        Assert.Contains(Items.BeetPulp, pig.Eats);
        Assert.DoesNotContain(Items.Wheat, pig.Eats);
        Assert.Contains(Items.Stillage, Husbandry.AnyFeed);
        var goat = Husbandry.ById[Items.Goat];
        Assert.Equal(Items.Milk, goat.Product);
        Assert.True(goat.Time < Husbandry.ById[Items.Cow].Time);
        Assert.Equal(TechTree.PigsAndGoats, TechTree.ByItem[Items.Pig].Id);
    }

    [Fact]
    public void Biomass_cogeneration_geothermal_and_hydrogen_give_power_each_their_own_way()
    {
        Assert.Equal(500, PowerRules.KwOf(Items.BiomassPlant));
        Assert.True(PowerRules.Burns(Items.BiomassPlant, Items.WoodPellets));
        Assert.False(PowerRules.Burns(Items.BiomassPlant, Items.Coal));
        Assert.True(PowerRules.Steam(Items.BiomassPlant));
        Assert.True(PowerRules.Burns(Items.ChpPlant, Items.Coke));
        Assert.True(PowerRules.ChpReach > Frost.HeaterReach);
        Assert.True(PowerRules.Burns(Items.FuelCell, Items.Hydrogen));
        Assert.True(Items.IsGenerator(Items.FuelCell));

        // A geothermal plant gives without fuel in the mountains, more on the glacier, and nothing in a farmland.
        Assert.True(Items.IsRenewable(Items.GeothermalPlant));
        Assert.Equal(Geothermal.Kw, PowerRules.HydroKw(Items.GeothermalPlant, (int)Biome.Mountains));
        Assert.True(PowerRules.HydroKw(Items.GeothermalPlant, (int)Biome.Ice) > Geothermal.Kw);
        Assert.Equal(0, PowerRules.HydroKw(Items.GeothermalPlant, (int)Biome.Farmland));

        // The electrolyzer puts more power into a piece of hydrogen than a fuel cell gets back out of it.
        var electrolysis = Processing.ByKind[Items.Electrolyzer].Recipe(Items.Hydrogen)!;
        var kjIn = PowerRules.Demand[Items.Electrolyzer] * electrolysis.Time.TotalSeconds / electrolysis.Quantity;
        Assert.Equal(0.7, PowerRules.Fuels[Items.Hydrogen] / kjIn, 2);
    }

    // --- On a plot -------------------------------------------------------------------------------

    [Fact]
    public async Task A_sprinkler_lets_the_steppe_be_tilled_and_new_crops_are_sown_once_researched_and_grow_faster_there()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 12);
        await GiveAsync(client, Items.Hoe, 1);
        await client.SendAsync("character.equip", new { item = Items.Hoe });
        object At(int dx, string? crop = null) => new { x, y, tileX = tx + dx, tileY = ty, crop };

        Assert.Equal("not_fertile", (await client.SendAsync("plot.till", At(1))).ErrorCode());
        await GiveAsync(client, Items.Sprinkler, 1);
        await PlaceAsync(client, x, y, Items.Sprinkler, tx, ty + 1);
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("plot.field", (await client.SendAsync("plot.till", At(1))).Type());
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("not_fertile", (await client.SendAsync("plot.till", At(Farming.SprinklerReach + 1))).ErrorCode());

        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("locked", (await client.SendAsync("plot.plant", At(1, Items.SugarBeet))).ErrorCode());
        await ResearchedAsync(client, TechTree.SugarBeets);
        factory.Time.Advance(HarvestRules.Time);
        var sown = await client.SendAsync("plot.plant", At(1, Items.SugarBeet));
        Assert.Equal("plot.field", sown.Type());
        var readyAt = DateTimeOffset.FromUnixTimeMilliseconds(sown.Payload().GetProperty("field").GetProperty("readyAt").GetInt64());
        var grows = Farming.ById[Items.SugarBeet].Grow / Farming.SprinklerSpeed;
        Assert.InRange((readyAt - factory.Time.GetUtcNow() - grows).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task An_automatic_barn_feeds_hens_and_an_inserter_takes_the_eggs_it_collects_into_a_chest()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 8);
        await GiveAsync(client, Items.AutoBarn, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Chicken, 2);
        await GiveAsync(client, Items.Wheat, 20);

        // barn (2 × 2) → inserter → chest, two hens in its reach below them.
        var barn = await PlaceAsync(client, x, y, Items.AutoBarn, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 2, ty);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 3, ty);
        await client.SendAsync("plot.release", new { x, y, tileX = tx + 3, tileY = ty + 2, item = Items.Chicken });
        await client.SendAsync("plot.release", new { x, y, tileX = tx + 4, tileY = ty + 2, item = Items.Chicken });
        var filled = (await client.SendAsync("feeder.fill", new { x, y, id = barn, item = Items.Wheat, quantity = 20 })).Payload().GetProperty("feeder");
        Assert.True(filled.GetProperty("collects").GetBoolean());
        Assert.Equal(Feeders.BarnReach, filled.GetProperty("reach").GetInt32());
        Assert.Equal(16, filled.GetProperty("feed").GetInt32());
        await PowerAsync(client, x, y, tx, ty);

        // On the steppe a hen lays every 3 minutes: after 10, each laid three times (2 eggs and 1 manure each).
        factory.Time.Advance(TimeSpan.FromMinutes(10));
        var state = (await client.SendAsync("feeder.get", new { x, y, id = barn })).Payload().GetProperty("feeder");
        var chestState = (await client.SendAsync("storage.get", new { x, y, id = chest })).Payload();
        // The inserter took some of it to the chest; between the two, nothing was lost.
        Assert.True(CountIn(chestState, Items.Egg) + CountIn(chestState, Items.Manure) > 0, chestState.GetRawText());
        Assert.Equal(12, CountIn(state, Items.Egg) + CountIn(chestState, Items.Egg));
        Assert.Equal(6, CountIn(state, Items.Manure) + CountIn(chestState, Items.Manure));
        // Each hen ate four meals: when let out and after each of her three collections.
        Assert.Equal(20 - 2 * 2 * 4, state.GetProperty("feed").GetInt32());

        // Picked up, the barn gives back its feed and whatever it still holds.
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = barn })).Type());
        Assert.Equal(1, (await client.InventoryAsync()).GetValueOrDefault(Items.AutoBarn));
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static int CountIn(JsonElement storage, string item)
    {
        var total = 0;
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == item
                    && e.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number)
                {
                    total += q.GetInt32();
                    return;
                }

                foreach (var p in e.EnumerateObject())
                {
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in e.EnumerateArray())
                {
                    Walk(a);
                }
            }
        }

        Walk(storage);
        return total;
    }

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private async Task ResearchedAsync(GameClient client, string technology)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryWorld.Modules.Abstractions.GameDb>();
        await using var command = await db.CommandAsync(
            "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
        command.Parameters.AddWithValue("owner", client.AccountId);
        command.Parameters.AddWithValue("id", technology);
        command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 14000;
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

    /// <summary>A steppe plot of the player's with <paramref name="length"/> free tiles in a row from (tileX, tileY) east, and the three rows under it free too.</summary>
    private async Task<(int X, int Y, int TileX, int TileY)> PlotWithFreeRowAsync(GameClient client, int length)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        for (var ty = 20; ty + 4 <= PlotLayout.Size; ty++)
        {
            for (var tx = 0; tx + length <= PlotLayout.Size; tx++)
            {
                if (Enumerable.Range(tx, length).All(i => Enumerable.Range(ty, 4).All(row => bytes[row * PlotLayout.Size + i] == (byte)TileObject.None
                    && (i, row) != PlotLayout.Spawn)))
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free row.");
    }

    /// <summary>Powers the row from (tileX, tileY): a pole and a generator with coal two rows under it.</summary>
    private async Task PowerAsync(GameClient client, int x, int y, int tileX, int tileY)
    {
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 20);
        await PlaceAsync(client, x, y, Items.Pole, tileX + 2, tileY + 2);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tileX + 1, tileY + 3);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = East)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }
}
