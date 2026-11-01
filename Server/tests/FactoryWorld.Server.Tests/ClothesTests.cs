using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Characters;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class ClothesTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private static readonly Towns AllTowns = Towns.Load();

    private static int _towns;

    private async Task<GameClient> PlayerAsync(long cents = 1_000_000_00, params string[] items)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        if (cents > 0)
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        foreach (var item in items)
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, 1, CancellationToken.None);
        }

        return client;
    }

    private static Dictionary<string, int> Bonuses(JsonElement character) =>
        character.GetProperty("bonuses").EnumerateObject().ToDictionary(b => b.Name, b => b.Value.GetInt32());

    private static Dictionary<string, string> Outfit(JsonElement character) =>
        character.GetProperty("outfit").EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetProperty("id").GetString()!);

    [Fact]
    public async Task Every_fifth_level_adds_a_bonus_on_top_of_the_clothes()
    {
        using var client = await PlayerAsync(0, Items.FlannelJacket);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(client.AccountId, Leveling.TotalXpForLevel(10), CancellationToken.None);
        }

        var dressed = (await client.SendAsync("character.equip", new { item = Items.FlannelJacket })).Payload();

        // Levels 5 and 10: forestry and mining 5% each; the jacket adds 20% to forestry.
        Assert.Equal(new Dictionary<string, int> { ["forestry"] = 25, ["mining"] = 5 }, Bonuses(dressed));
        Assert.Equal(5, dressed.GetProperty("levelBonuses").GetProperty("forestry").GetInt32());
        Assert.Equal(LevelBonuses.All.Count, dressed.GetProperty("levelRewards").GetArrayLength());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var outfit = scope.ServiceProvider.GetRequiredService<IOutfit>();
            Assert.Equal(25, await outfit.BonusAsync(client.AccountId, Industries.Forestry, CancellationToken.None));
            Assert.Equal(0, await outfit.BonusAsync(client.AccountId, Industries.Farming, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Clothes_go_on_their_slot_add_up_their_bonuses_and_come_off_into_the_inventory()
    {
        using var client = await PlayerAsync(0, Items.FlannelJacket, Items.WorkBoots, Items.Oilskin);

        await client.SendAsync("character.equip", new { item = Items.FlannelJacket });
        var dressed = (await client.SendAsync("character.equip", new { item = Items.WorkBoots })).Payload();
        Assert.Equal(new Dictionary<string, string> { ["body"] = Items.FlannelJacket, ["feet"] = Items.WorkBoots }, Outfit(dressed));
        Assert.Equal(new Dictionary<string, int> { ["forestry"] = 30, ["mining"] = 10 }, Bonuses(dressed));
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.FlannelJacket));

        // Another jacket swaps with the one worn.
        var swapped = (await client.SendAsync("character.equip", new { item = Items.Oilskin })).Payload();
        Assert.Equal(Items.Oilskin, Outfit(swapped)["body"]);
        Assert.Equal(1, (await client.InventoryAsync())[Items.FlannelJacket]);

        var off = (await client.SendAsync("character.unequip", new { clothing = "feet" })).Payload();
        Assert.False(Outfit(off).ContainsKey("feet"));
        Assert.Equal(new Dictionary<string, int> { ["fishing"] = 20 }, Bonuses(off));
        Assert.Equal(1, (await client.InventoryAsync())[Items.WorkBoots]);
        Assert.Equal("bad_slot", (await client.SendAsync("character.unequip", new { clothing = "hands" })).ErrorCode());
    }

    [Fact]
    public async Task Others_on_the_plot_see_the_clothes_with_the_look()
    {
        using var client = await PlayerAsync(0, Items.MinerHelmet);
        await client.SendAsync("character.equip", new { item = Items.MinerHelmet });

        await using var scope = factory.Services.CreateAsyncScope();
        var look = (await scope.ServiceProvider.GetRequiredService<ICharacterLooks>().LooksAsync([client.AccountId], CancellationToken.None))[client.AccountId];
        var json = JsonSerializer.SerializeToElement(look.Appearance, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(Items.MinerHelmet, json.GetProperty("outfit").GetProperty("head").GetString());
        Assert.Equal(0, json.GetProperty("faceStyle").GetInt32());
    }

    [Fact]
    public async Task A_new_look_costs_1000_dollars_and_old_clients_get_the_first_face()
    {
        using var client = await PlayerAsync(1500_00);
        var before = (await client.SendAsync("character.get")).Payload().GetProperty("appearance");
        Assert.Equal(0, before.GetProperty("faceStyle").GetInt32());
        Assert.Equal(0, before.GetProperty("facialHair").GetInt32());

        var options = (await client.SendAsync("character.options")).Payload();
        Assert.Equal(AppearanceCatalog.FaceStyles, options.GetProperty("faceStyles").GetInt32());
        Assert.Equal(AppearanceCatalog.EyeColors.Length, options.GetProperty("eyeColors").GetArrayLength());

        var look = new
        {
            skinTone = 3, hairStyle = 5, hairColor = 2, shirtStyle = 2, shirtColor = 4, pantsColor = 3, shoesColor = 1,
            faceStyle = 3, eyeColor = 2, facialHair = 3,
        };
        var restyled = await client.SendAsync("character.restyle", new { appearance = look });
        Assert.Equal("character", restyled.Type());
        var after = restyled.Payload().GetProperty("appearance");
        Assert.Equal(3, after.GetProperty("faceStyle").GetInt32());
        Assert.Equal(3, after.GetProperty("facialHair").GetInt32());
        Assert.Equal(5, after.GetProperty("hairStyle").GetInt32());
        Assert.Equal(500_00, await client.BalanceAsync());

        Assert.Equal("same_look", (await client.SendAsync("character.restyle", new { appearance = look })).ErrorCode());
        Assert.Equal("not_enough_money", (await client.SendAsync("character.restyle", new
        {
            appearance = new
            {
                skinTone = 1, hairStyle = 5, hairColor = 2, shirtStyle = 2, shirtColor = 4, pantsColor = 3, shoesColor = 1,
                faceStyle = 3, eyeColor = 2, facialHair = 3,
            },
        })).ErrorCode());
        Assert.Equal("invalid_appearance", (await client.SendAsync("character.restyle", new
        {
            appearance = new { skinTone = 1, hairStyle = 1, hairColor = 1, shirtStyle = 1, shirtColor = 1, pantsColor = 1, shoesColor = 1, faceStyle = 9 },
        })).ErrorCode());
        Assert.Equal(500_00, await client.BalanceAsync());
    }

    [Fact]
    public async Task A_smiths_apron_makes_crafting_faster_and_a_suit_makes_the_trader_pay_more()
    {
        using var client = await PlayerAsync(0, Items.SmithApron, Items.SuitTrousers, Items.DressShoes);
        double Seconds(JsonElement crafting, string recipe) =>
            crafting.Payload().GetProperty("recipes").EnumerateArray().First(r => r.GetProperty("id").GetString() == recipe).GetProperty("seconds").GetDouble();

        var plain = Seconds(await client.SendAsync("crafting.get"), Items.Furnace);
        await client.SendAsync("character.equip", new { item = Items.SmithApron });
        Assert.Equal(plain / 1.2, Seconds(await client.SendAsync("crafting.get"), Items.Furnace), 6);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, Items.IronIngot, 20, CancellationToken.None);
        }

        await client.SendAsync("market.sell", new { item = Items.IronIngot, quantity = 10 });
        var undressed = await client.BalanceAsync();
        await client.SendAsync("character.equip", new { item = Items.SuitTrousers });
        await client.SendAsync("character.equip", new { item = Items.DressShoes });
        await client.SendAsync("market.sell", new { item = Items.IronIngot, quantity = 10 });
        Assert.Equal(undressed * 106 / 100, await client.BalanceAsync() - undressed);
    }

    [Fact]
    public async Task Clothes_speed_up_their_industry_by_hand()
    {
        Assert.Equal(Industries.Forestry, HarvestRules.Industry(TileObject.OakTree));
        Assert.Equal(Industries.Mining, HarvestRules.Industry(TileObject.Rock));
        Assert.Equal(Industries.Fishing, HarvestRules.Industry(TileObject.Water));
        Assert.Null(HarvestRules.Industry(TileObject.Dune));

        using var client = await PlayerAsync(0, Items.Beanie, Items.FlannelJacket, Items.WorkBoots);
        foreach (var item in new[] { Items.Beanie, Items.FlannelJacket, Items.WorkBoots })
        {
            await client.SendAsync("character.equip", new { item });
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var plots = scope.ServiceProvider.GetRequiredService<PlotService>();
        Assert.Equal(1.4, await plots.SpeedAsync(client.AccountId, Industries.Forestry, CancellationToken.None), 6);
        Assert.Equal(1.1, await plots.SpeedAsync(client.AccountId, Industries.Mining, CancellationToken.None), 6);
        Assert.Equal(1.0, await plots.SpeedAsync(client.AccountId, Industries.Farming, CancellationToken.None), 6);
    }

    [Fact]
    public void Every_piece_of_clothing_is_sewn_at_the_tailors_researched_in_tailoring_and_bought_by_the_trader()
    {
        Assert.Equal(15, Items.Clothes.Count);
        Assert.All(ClothingSlots.All, slot => Assert.Contains(Items.Clothes.Values, g => g.Slot == slot));
        var tailoring = TechTree.ById[TechTree.Tailoring];
        Assert.Contains(Items.Tailor, tailoring.Unlocks);
        foreach (var item in Items.Clothes.Keys)
        {
            Assert.Equal(Items.Tailor, Workshop.Find(item)!.Station);
            Assert.Contains(item, tailoring.Unlocks);
            Assert.True(Items.Names.ContainsKey(item));
        }

        Assert.True(Items.IsCityOnly(Items.Tailor));
        Assert.Equal(Items.Workbench, Workshop.Find(Items.Tailor)!.Station);
    }

    [Fact]
    public async Task A_tailors_stands_in_a_city_only_and_sews_once_tailoring_is_researched()
    {
        using var client = await PlayerAsync(1_000_000_00, Items.Tailor, Items.Tailor);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, Items.Wool, 20, CancellationToken.None);
        }

        var forest = await FreePlotAsync(client, Biome.Forest);
        var refused = await client.SendAsync("plot.place", new { x = forest.X, y = forest.Y, kind = Items.Tailor, tileX = 10, tileY = 10, rotation = 0 });
        Assert.Equal("no_city", refused.ErrorCode());

        var city = CityPlot();
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = city.X, y = city.Y })).Type());
        var placed = await client.SendAsync("plot.place", new { x = city.X, y = city.Y, kind = Items.Tailor, tileX = PlotLayout.Spawn.X + 5, tileY = 30, rotation = 0 });
        Assert.True(placed.Type() == "plot.placed", placed.ToString());
        var tailor = placed.Payload().GetProperty("object").GetProperty("id").GetString();

        Assert.Equal("locked", (await client.SendAsync("crafting.craft", new { workbench = tailor, recipe = Items.Beanie })).ErrorCode());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded)
                VALUES (@owner, @tech, @starts, @finishes, true)
                """, CancellationToken.None);
            insert.Parameters.AddWithValue("owner", client.AccountId);
            insert.Parameters.AddWithValue("tech", TechTree.Tailoring);
            insert.Parameters.AddWithValue("starts", factory.Time.GetUtcNow() - TimeSpan.FromHours(2));
            insert.Parameters.AddWithValue("finishes", factory.Time.GetUtcNow() - TimeSpan.FromHours(1));
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var ordered = await client.SendAsync("crafting.craft", new { workbench = tailor, recipe = Items.Beanie });
        Assert.True(ordered.Type() == "crafting", ordered.ToString());
        Assert.Equal(14, (await client.InventoryAsync())[Items.Wool]);
    }

    private static async Task<(int X, int Y)> FreePlotAsync(GameClient client, Biome biome)
    {
        for (var y = 220; ; y++)
        {
            for (var x = 3; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && (await client.SendAsync("world.buy", new { x, y })).Type() == "world.bought")
                {
                    return (x, y);
                }
            }
        }
    }

    /// <summary>A city plot of a big town, a different one each time.</summary>
    private static (int X, int Y) CityPlot()
    {
        var skip = Interlocked.Increment(ref _towns) * 5 + 400;
        foreach (var town in AllTowns.All.Where(t => t.Population > 100_000).Skip(skip))
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var (x, y) = (town.X + dx, town.Y + dy);
                    if (Map.Contains(x, y) && Map.BiomeAt(x, y) == Biome.City && AllTowns.Of(x, y) == town)
                    {
                        return (x, y);
                    }
                }
            }
        }

        throw new InvalidOperationException("No town.");
    }
}
