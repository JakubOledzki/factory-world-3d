using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Recycling;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class GarageUpgradeTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private async Task<GameClient> PlayerAsync(long cents = 100_000_000_00)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        Assert.Equal("garage", (await client.SendAsync("garage.get")).Type());
        if (cents > 0)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    /// <summary>Gives the materials of the next level of a track and buys it.</summary>
    private async Task<JsonElement> UpgradeAsync(GameClient client, string track)
    {
        var levels = await LevelsAsync(client);
        var step = GarageUpgrades.Find(track)!.Steps[levels[track]];
        foreach (var (item, quantity) in step.Materials)
        {
            await GiveAsync(client, item, quantity);
        }

        var reply = await client.SendAsync("garage.upgrade", new { track });
        Assert.Equal("garage.upgrades", reply.Type());
        return reply;
    }

    private static async Task<Dictionary<string, int>> LevelsAsync(GameClient client) =>
        (await client.SendAsync("garage.upgrades")).Payload().GetProperty("tracks").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("level").GetInt32());

    private static JsonElement Find(JsonElement garage, string kind) =>
        garage.Payload().GetProperty("objects").EnumerateArray().First(o => o.GetProperty("kind").GetString() == kind);

    [Fact]
    public void Every_track_names_real_materials_and_requires_only_reachable_levels()
    {
        foreach (var track in GarageUpgrades.Tracks)
        {
            Assert.InRange(track.MaxLevel, 5, 10);
            for (var i = 0; i < track.Steps.Count; i++)
            {
                var step = track.Steps[i];
                Assert.All(step.Materials, m => Assert.True(Items.Exists(m.Item), m.Item));
                Assert.All(step.Requires, r => Assert.InRange(r.Level, 1, GarageUpgrades.Find(r.Track)!.MaxLevel));
                Assert.All(step.Requires, r => Assert.NotEqual(track.Id, r.Track));
                if (i > 0)
                {
                    Assert.True(step.PriceCents > track.Steps[i - 1].PriceCents, $"{track.Id} {i + 1}");
                }
            }
        }

        Assert.Equal(10, GarageUpgrades.Find(GarageUpgrades.Floor)!.MaxLevel);
        Assert.Equal(10, GarageUpgrades.Find(GarageUpgrades.Walls)!.MaxLevel);
    }

    [Fact]
    public async Task Levels_are_bought_one_after_another_for_money_and_materials()
    {
        using var client = await PlayerAsync(cents: 100_00);

        // Not enough materials, then not enough money: nothing is taken either time.
        Assert.Equal("not_enough_items", (await client.SendAsync("garage.upgrade", new { track = GarageUpgrades.Floor })).ErrorCode());
        await GiveAsync(client, Items.Sand, 200);
        Assert.Equal("not_enough_money", (await client.SendAsync("garage.upgrade", new { track = GarageUpgrades.Floor })).ErrorCode());
        Assert.Equal(200, (await client.InventoryAsync())[Items.Sand]);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 100_000_000_00, "test", CancellationToken.None);
        }

        var before = await client.BalanceAsync();
        var bought = (await client.SendAsync("garage.upgrade", new { track = GarageUpgrades.Floor })).Payload();
        var floor = bought.GetProperty("tracks").EnumerateArray().First(t => t.GetProperty("id").GetString() == GarageUpgrades.Floor);
        Assert.Equal(1, floor.GetProperty("level").GetInt32());
        Assert.Equal(1, floor.GetProperty("bonus").GetInt32());
        Assert.Equal(before - 15_000_00, await client.BalanceAsync());
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Sand));

        // The garage reports the level, so the client draws the cleaner floor.
        var garage = (await client.SendAsync("garage.get")).Payload();
        Assert.Equal(1, garage.GetProperty("upgrades").GetProperty(GarageUpgrades.Floor).GetInt32());
        Assert.Equal("Garaż", garage.GetProperty("rank").GetString());

        Assert.Equal("no_upgrade", (await client.SendAsync("garage.upgrade", new { track = "roof" })).ErrorCode());
    }

    [Fact]
    public async Task Walls_need_the_light_and_the_floor_before_the_office()
    {
        using var client = await PlayerAsync();
        for (var i = 0; i < 5; i++)
        {
            await UpgradeAsync(client, GarageUpgrades.Walls);
        }

        // Oak panels need better lighting first; the materials stay in the inventory.
        foreach (var (item, quantity) in GarageUpgrades.Find(GarageUpgrades.Walls)!.Steps[5].Materials)
        {
            await GiveAsync(client, item, quantity);
        }

        Assert.Equal("upgrade_required", (await client.SendAsync("garage.upgrade", new { track = GarageUpgrades.Walls })).ErrorCode());
        await UpgradeAsync(client, GarageUpgrades.Lighting);
        await UpgradeAsync(client, GarageUpgrades.Lighting);
        Assert.Equal("garage.upgrades", (await client.SendAsync("garage.upgrade", new { track = GarageUpgrades.Walls })).Type());

        // All the way to the office with glass walls.
        while ((await LevelsAsync(client))[GarageUpgrades.Lighting] < 5)
        {
            await UpgradeAsync(client, GarageUpgrades.Lighting);
        }

        while ((await LevelsAsync(client))[GarageUpgrades.Floor] < 8)
        {
            await UpgradeAsync(client, GarageUpgrades.Floor);
        }

        JsonElement last = default;
        while ((await LevelsAsync(client))[GarageUpgrades.Walls] < 10)
        {
            last = await UpgradeAsync(client, GarageUpgrades.Walls);
        }

        Assert.Equal("Biuro", last.Payload().GetProperty("rank").GetString());
        Assert.Equal("max_level", (await client.SendAsync("garage.upgrade", new { track = GarageUpgrades.Walls })).ErrorCode());
    }

    [Fact]
    public async Task Upgrades_give_their_bonuses_to_trade_crafting_and_experience()
    {
        using var client = await PlayerAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var outfit = scope.ServiceProvider.GetRequiredService<IOutfit>();
        var progress = scope.ServiceProvider.GetRequiredService<ICharacterProgress>();

        var plain = await progress.AddXpAsync(client.AccountId, 100, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await UpgradeAsync(client, GarageUpgrades.Walls);
            await UpgradeAsync(client, GarageUpgrades.Floor);
        }

        await UpgradeAsync(client, GarageUpgrades.Lighting);
        await UpgradeAsync(client, GarageUpgrades.Lighting);

        Assert.Equal(3, await outfit.BonusAsync(client.AccountId, Industries.Trade, CancellationToken.None));
        Assert.Equal(3, await outfit.BonusAsync(client.AccountId, Industries.Crafting, CancellationToken.None));
        var lit = await progress.AddXpAsync(client.AccountId, 100, CancellationToken.None);
        Assert.Equal(104, lit.Xp - plain.Xp);

        // The trader pays 3% more for a stone.
        await GiveAsync(client, Items.Stone, 100);
        var before = await client.BalanceAsync();
        await client.SendAsync("market.sell", new { item = Items.Stone, quantity = 100 });
        var usual = scope.ServiceProvider.GetRequiredService<IMarketPrices>().BuysCents(Items.Stone)!.Value;
        Assert.Equal(usual * 103 / 100 * 100, await client.BalanceAsync() - before);
    }

    [Fact]
    public async Task Heating_makes_sorting_faster()
    {
        using var client = await PlayerAsync();
        var plain = (await client.SendAsync("garage.get")).Payload().GetProperty("sortSeconds").GetDouble();
        await UpgradeAsync(client, GarageUpgrades.Heating);
        await UpgradeAsync(client, GarageUpgrades.Heating);

        var warm = (await client.SendAsync("recycling.sort", new { count = 1 })).Payload();
        Assert.Equal(plain * 0.84, warm.GetProperty("sortSeconds").GetDouble(), 3);
        var finishes = warm.GetProperty("queue")[0].GetInt64() - warm.GetProperty("serverTime").GetInt64();
        Assert.Equal(plain * 840, finishes, 0);
    }

    [Fact]
    public async Task The_shelf_keeps_goods_and_grows_with_its_upgrade()
    {
        using var client = await PlayerAsync();
        var shelf = Find(await client.SendAsync("garage.get"), Items.Shelf).GetProperty("id").GetGuid();
        await GiveAsync(client, Items.Stone, 250);

        var state = (await client.SendAsync("garage.storage.put", new { id = shelf, item = Items.Stone, quantity = 250 })).Payload();
        Assert.Equal(GarageStorageRules.ShelfSlots, state.GetProperty("slots").GetInt32());
        Assert.Equal(3, state.GetProperty("usedSlots").GetInt32());
        Assert.True(state.GetProperty("canPut").GetBoolean());
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Stone));

        await client.SendAsync("garage.storage.take", new { id = shelf, item = Items.Stone, quantity = 50 });
        Assert.Equal(50, (await client.InventoryAsync())[Items.Stone]);
        Assert.Equal("not_enough_items", (await client.SendAsync("garage.storage.take", new { id = shelf, item = Items.Stone, quantity = 500 })).ErrorCode());

        await UpgradeAsync(client, GarageUpgrades.Shelves);
        var bigger = (await client.SendAsync("garage.storage", new { id = shelf })).Payload();
        Assert.Equal(GarageStorageRules.ShelfSlots + 8, bigger.GetProperty("slots").GetInt32());

        // Too much for the shelf.
        await GiveAsync(client, Items.Sand, 100_000);
        Assert.Equal("storage_full", (await client.SendAsync("garage.storage.put", new { id = shelf, item = Items.Sand, quantity = 100_000 })).ErrorCode());

        // Put away, the shelf gives back what is on it.
        await client.SendAsync("garage.pickup", new { id = shelf });
        Assert.Equal(250, (await client.InventoryAsync())[Items.Stone]);
    }

    [Fact]
    public async Task What_does_not_fit_in_the_inventory_waits_on_the_pallet()
    {
        using var client = await PlayerAsync();
        var garage = await client.SendAsync("garage.get");
        var pallet = Find(garage, Items.Pallet).GetProperty("id").GetGuid();
        // Fill every tile of the inventory.
        var fillers = new[]
        {
            Items.Stone, Items.Sand, Items.Coal, Items.IronOre, Items.CopperOre, Items.TinOre, Items.Wood, Items.Clay, Items.Wheat, Items.Corn,
            Items.Potato, Items.Carrot, Items.Cabbage, Items.Egg, Items.Wool, Items.Milk, Items.Salt, Items.Sulfur, Items.Quartz,
        };
        foreach (var item in fillers)
        {
            await GiveAsync(client, item, 1);
        }

        Assert.True((await client.InventoryAsync()).Count >= Items.InventorySlots);

        var bought = (await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 5 })).Payload();
        Assert.True(bought.GetProperty("toPallet").GetBoolean());
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Nails));
        var waiting = (await client.SendAsync("garage.storage", new { id = pallet })).Payload();
        Assert.False(waiting.GetProperty("canPut").GetBoolean());
        Assert.Equal(5, waiting.GetProperty("items")[0].GetProperty("quantity").GetInt64());
        Assert.Equal("not_stored_here", (await client.SendAsync("garage.storage.put", new { id = pallet, item = Items.Stone, quantity = 1 })).ErrorCode());

        // Taken off the pallet once there is room; moving the pallet keeps what waits on it.
        Assert.Equal("inventory_full", (await client.SendAsync("garage.storage.take", new { id = pallet, item = Items.Nails, quantity = 5 })).ErrorCode());
        await client.SendAsync("market.sell", new { item = Items.Stone, quantity = 1 });
        await client.SendAsync("garage.pickup", new { id = pallet });
        await client.SendAsync("garage.place", new { kind = Items.Pallet, x = 2, y = 1 });
        pallet = Find(await client.SendAsync("garage.get"), Items.Pallet).GetProperty("id").GetGuid();
        await client.SendAsync("garage.storage.take", new { id = pallet, item = Items.Nails, quantity = 0 });
        Assert.Equal(5, (await client.InventoryAsync())[Items.Nails]);

        // Without a pallet in the garage a full inventory stops the purchase.
        await client.SendAsync("garage.pickup", new { id = pallet });
        Assert.Equal("inventory_full", (await client.SendAsync("market.buy", new { item = Items.Axe, quantity = 1 })).ErrorCode());
    }

    [Fact]
    public async Task The_wardrobe_keeps_clothes_and_changes_whole_outfits()
    {
        using var client = await PlayerAsync();
        await client.SendAsync("market.buy", new { item = Items.ClothesWardrobe, quantity = 1 });
        var placed = await client.SendAsync("garage.place", new { kind = Items.ClothesWardrobe, x = 6, y = 3 });
        var wardrobe = Find(placed, Items.ClothesWardrobe).GetProperty("id").GetGuid();
        foreach (var item in new[] { Items.FlannelJacket, Items.WorkBoots, Items.SuitJacket, Items.DressShoes, Items.Beanie })
        {
            await GiveAsync(client, item, 1);
        }

        // A forest outfit worn and saved.
        await client.SendAsync("character.equip", new { item = Items.FlannelJacket });
        await client.SendAsync("character.equip", new { item = Items.WorkBoots });
        await client.SendAsync("character.equip", new { item = Items.Beanie });
        var saved = (await client.SendAsync("wardrobe.save", new { id = wardrobe, number = 1, name = "Las" })).Payload();
        Assert.Equal("Las", saved.GetProperty("outfits")[0].GetProperty("name").GetString());

        // A suit with a bare head.
        Assert.Equal("not_clothing", (await client.SendAsync("wardrobe.put", new { id = wardrobe, item = Items.Stone, quantity = 1 })).ErrorCode());
        await client.SendAsync("character.equip", new { item = Items.SuitJacket });
        await client.SendAsync("character.equip", new { item = Items.DressShoes });
        await client.SendAsync("character.unequip", new { clothing = "head" });
        await client.SendAsync("wardrobe.save", new { id = wardrobe, number = 2, name = "Handel" });

        // Back to the forest in one go: what comes off hangs in the wardrobe.
        var forest = (await client.SendAsync("wardrobe.wear", new { id = wardrobe, number = 1 })).Payload();
        Assert.Equal(Items.FlannelJacket, forest.GetProperty("worn").GetProperty("body").GetProperty("id").GetString());
        Assert.Equal(Items.Beanie, forest.GetProperty("worn").GetProperty("head").GetProperty("id").GetString());
        var hanging = forest.GetProperty("stored").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains(Items.SuitJacket, hanging);
        Assert.Contains(Items.DressShoes, hanging);

        var suit = (await client.SendAsync("wardrobe.wear", new { id = wardrobe, number = 2 })).Payload();
        Assert.Equal(Items.SuitJacket, suit.GetProperty("worn").GetProperty("body").GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, suit.GetProperty("worn").GetProperty("head").ValueKind);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var expected = new[] { Items.SuitJacket, Items.DressShoes }.Sum(c => Items.Clothes[c].Bonuses.GetValueOrDefault(Industries.Trade));
            Assert.True(expected > 0);
            Assert.Equal(expected, await scope.ServiceProvider.GetRequiredService<IOutfit>().BonusAsync(client.AccountId, Industries.Trade, CancellationToken.None));
        }

        // A piece that is nowhere to be found stops the change; nothing moves.
        await client.SendAsync("wardrobe.take", new { id = wardrobe, item = Items.FlannelJacket, quantity = 1 });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().RemoveAsync(client.AccountId, Items.FlannelJacket, 1, CancellationToken.None);
        }

        Assert.Equal("missing_clothes", (await client.SendAsync("wardrobe.wear", new { id = wardrobe, number = 1 })).ErrorCode());
        var still = (await client.SendAsync("wardrobe.get", new { id = wardrobe })).Payload();
        Assert.Equal(Items.SuitJacket, still.GetProperty("worn").GetProperty("body").GetProperty("id").GetString());

        // Put away, the wardrobe gives the clothes back.
        await client.SendAsync("garage.pickup", new { id = wardrobe });
        var inventory = await client.InventoryAsync();
        Assert.Equal(1, inventory[Items.Beanie]);
        Assert.Equal(1, inventory[Items.WorkBoots]);
    }
}
