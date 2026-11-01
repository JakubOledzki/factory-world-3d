using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Server.Tests;

public sealed class CompendiumTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task The_compendium_lists_every_item_and_where_each_recipe_is_made()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var reply = await client.SendAsync("compendium.get");
        Assert.Equal("compendium", reply.Type());
        var payload = reply.Payload();
        var items = payload.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToHashSet();
        Assert.Equal(Items.Names.Count, items.Count);

        var recipes = payload.GetProperty("recipes").EnumerateArray().ToList();
        string[] Stations(string item) => recipes.Where(r => r.GetProperty("id").GetString() == item)
            .Select(r => r.GetProperty("station").GetString()!).ToArray();
        Assert.Contains(Items.Workbench, Stations(Items.Workbench));
        Assert.Contains(Items.Anvil, Stations(Items.Pickaxe));
        Assert.Superset(new HashSet<string> { Items.Furnace, Items.BrickFurnace, Items.BlastFurnace }, Stations(Items.IronIngot).ToHashSet());
        Assert.Contains(Items.Mill, Stations(Items.Flour));
        // A blast furnace smelts four times as fast as the stone one.
        double Seconds(string station) => recipes.First(r => r.GetProperty("id").GetString() == Items.IronIngot
            && r.GetProperty("station").GetString() == station
            && r.GetProperty("ingredients")[0].GetProperty("id").GetString() == Items.IronOre).GetProperty("seconds").GetDouble();
        Assert.Equal(10, Seconds(Items.Furnace));
        Assert.Equal(2.5, Seconds(Items.BlastFurnace));
        Assert.True(recipes.All(r => items.Contains(r.GetProperty("id").GetString()!)));

        Assert.Equal(4, payload.GetProperty("fuels").GetProperty(Items.Coal).GetInt32());
        Assert.True(payload.GetProperty("canteen").TryGetProperty(Items.Bread, out _));
        // A new player has the brick furnace still to research.
        Assert.Equal(JsonValueKind.String, payload.GetProperty("locked").GetProperty(Items.BrickFurnace).ValueKind);
    }
}
