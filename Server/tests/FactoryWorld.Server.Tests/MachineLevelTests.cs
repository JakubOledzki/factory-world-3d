using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Machines of three levels: each level researched after the one below it, built on it, bigger and better.</summary>
public sealed class MachineLevelTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    /// <summary>Each machine's levels, lowest first.</summary>
    public static TheoryData<string, string, string> Levels => new()
    {
        { Items.Furnace, Items.BrickFurnace, Items.BlastFurnace },
        { Items.Sawmill, Items.ElectricSawmill, Items.IndustrialSawmill },
        { Items.Mill, Items.RollerMill, Items.FlourWorks },
        { Items.Quarry, Items.MechanizedQuarry, Items.OpenPitMine },
        { Items.Crusher, Items.ConeCrusher, Items.OreProcessingPlant },
        { Items.Assembler, Items.FastAssembler, Items.ExpressAssembler },
    };

    private static bool Needs(Technology technology, string other) =>
        technology.Requires.Any(r => r == other || Needs(TechTree.ById[r], other));

    [Theory]
    [MemberData(nameof(Levels))]
    public void Each_higher_level_is_researched_after_the_one_below_and_built_on_it(string first, string second, string third)
    {
        string[] levels = [first, second, third];
        for (var i = 1; i < levels.Length; i++)
        {
            var technology = TechTree.ByItem[levels[i]];
            Assert.Contains(levels[i], Items.Names.Keys);
            if (TechTree.ByItem.TryGetValue(levels[i - 1], out var below))
            {
                Assert.True(Needs(technology, below.Id), $"{technology.Id} needs {below.Id}");
            }

            Assert.Contains((levels[i - 1], 1), Workshop.Find(levels[i])!.Ingredients);
            Assert.Equal(Footprints.Size(levels[i - 1]) + 1, Footprints.Size(levels[i]));
            Assert.Contains(levels[i], PlotObjects.Placeable);
            Assert.True(NpcPrices.Buys[levels[i]] > NpcPrices.Buys[levels[i - 1]], levels[i]);
        }
    }

    [Theory]
    [InlineData(Items.Mill, Items.RollerMill, Items.FlourWorks)]
    [InlineData(Items.Quarry, Items.MechanizedQuarry, Items.OpenPitMine)]
    [InlineData(Items.Crusher, Items.ConeCrusher, Items.OreProcessingPlant)]
    public void A_higher_level_makes_the_same_things_faster_and_never_gets_less_out_of_them(string first, string second, string third)
    {
        string[] levels = [first, second, third];
        for (var i = 1; i < levels.Length; i++)
        {
            var below = Processing.ByKind[levels[i - 1]];
            var machine = Processing.ByKind[levels[i]];
            Assert.True(machine.Powered, levels[i]);
            Assert.True(PowerRules.Demand[levels[i]] > PowerRules.Demand.GetValueOrDefault(levels[i - 1]), levels[i]);
            Assert.Equal(below.Recipes.Select(r => r.Id), machine.Recipes.Select(r => r.Id));
            foreach (var recipe in machine.Recipes)
            {
                var lower = below.Recipe(recipe.Id)!;
                Assert.True(recipe.Quantity / recipe.Time.TotalSeconds >= 1.8 * lower.Quantity / lower.Time.TotalSeconds, $"{levels[i]} {recipe.Id}");
                Assert.Equal(lower.Ingredients.Select(x => x.Item), recipe.Ingredients.Select(x => x.Item));
                foreach (var (item, quantity) in recipe.Ingredients)
                {
                    var lowerQuantity = lower.Ingredients.First(x => x.Item == item).Quantity;
                    Assert.True((double)recipe.Quantity / quantity >= (double)lower.Quantity / lowerQuantity, $"{levels[i]} {recipe.Id} {item}");
                }
            }
        }
    }

    [Theory]
    [InlineData(Items.Quarry)]
    [InlineData(Items.MechanizedQuarry)]
    [InlineData(Items.OpenPitMine)]
    public void Every_level_of_the_quarry_stands_in_the_mountains_only(string kind)
    {
        PlotObjects.RequireSite(kind, 0, 0, Biome.Mountains, 10, 10, []);
        Assert.Equal("no_quarry", Assert.Throws<GameException>(() => PlotObjects.RequireSite(kind, 0, 0, Biome.Forest, 10, 10, [])).Code);
    }

    [Fact]
    public void The_new_technologies_sit_in_the_branches_of_their_machines()
    {
        Assert.Equal("mining", TechTree.BranchOf[TechTree.BrickFurnaces].Id);
        Assert.Equal("mining", TechTree.BranchOf[TechTree.BlastFurnaces].Id);
        Assert.Equal("mining", TechTree.BranchOf[TechTree.ConeCrushing].Id);
        Assert.Equal("mining", TechTree.BranchOf[TechTree.OpenPitMining].Id);
        Assert.Equal("food", TechTree.BranchOf[TechTree.FlourIndustry].Id);
    }

    [Fact]
    public async Task The_brick_furnace_is_made_once_its_research_is_done()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var garage = await client.SendAsync("garage.get");
        var bench = garage.Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == Items.Workbench).GetProperty("id").GetString()!;

        async Task GiveAsync(string item, long quantity)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
        }

        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal("Piece ceglane", recipes[Items.BrickFurnace].GetProperty("locked").GetString());
        Assert.Equal("Piece hutnicze", recipes[Items.BlastFurnace].GetProperty("locked").GetString());

        foreach (var (item, quantity) in Workshop.Find(Items.BrickFurnace)!.Ingredients)
        {
            await GiveAsync(item, quantity);
        }

        Assert.Equal("locked", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.BrickFurnace })).ErrorCode());

        foreach (var (item, quantity) in TechTree.ById[TechTree.BrickFurnaces].Cost)
        {
            await GiveAsync(item, quantity);
        }

        Assert.Equal("research", (await client.SendAsync("research.start", new { technology = TechTree.BrickFurnaces })).Type());
        factory.Time.Advance(TechTree.ById[TechTree.BrickFurnaces].Time);
        await client.SendAsync("research.get");

        Assert.Null((await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.BrickFurnace })).ErrorCode());
    }
}
