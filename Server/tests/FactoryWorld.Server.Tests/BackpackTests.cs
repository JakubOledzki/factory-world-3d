using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class BackpackTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private async Task<GameClient> RichPlayerAsync(params string[] items)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 200_000_000, "test", CancellationToken.None);
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        foreach (var item in items)
        {
            await inventory.AddAsync(client.AccountId, item, 1, CancellationToken.None);
        }

        return client;
    }

    /// <summary>The tool in each slot of the tool bar, "" for an empty one.</summary>
    private static string[] Tools(JsonElement character) =>
        character.GetProperty("tools").EnumerateArray()
            .Select(t => t.ValueKind == JsonValueKind.Object ? t.GetProperty("id").GetString()! : "").ToArray();

    private static int Slots(JsonElement inventory) => inventory.Payload().GetProperty("slots").GetInt32();

    [Fact]
    public async Task The_trader_sells_backpacks_from_500_dollars_to_a_million()
    {
        using var client = await RichPlayerAsync();
        var prices = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString()!.StartsWith("backpack_"))
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("buyCents").GetInt64());

        Assert.Equal(Items.Backpacks.Count, prices.Count);
        Assert.InRange(prices.Values.Min(), 50_000 * 0.5, 50_000 * 1.5);
        Assert.InRange(prices.Values.Max(), 100_000_000 * 0.5, 100_000_000 * 1.5);
        Assert.Equal(20, Items.Backpacks.Values.Min(b => b.ExtraSlots));
        Assert.Equal(100, Items.Backpacks.Values.Max(b => b.ExtraSlots));
    }

    [Fact]
    public async Task A_backpack_adds_tiles_and_a_second_tool_slot_until_it_is_taken_off()
    {
        using var client = await RichPlayerAsync(Items.Axe, Items.Pickaxe);
        Assert.Equal(Items.InventorySlots, Slots(await client.SendAsync("inventory.get")));
        Assert.Single(Tools((await client.SendAsync("character.get")).Payload()));

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.SchoolBackpack, quantity = 1 })).Type());
        var worn = (await client.SendAsync("character.equip", new { item = Items.SchoolBackpack })).Payload();
        Assert.Equal(Items.SchoolBackpack, worn.GetProperty("backpack").GetProperty("id").GetString());
        Assert.Equal(Items.InventorySlots + 20, worn.GetProperty("inventorySlots").GetInt32());
        Assert.Equal(Items.InventorySlots + 20, Slots(await client.SendAsync("inventory.get")));

        await client.SendAsync("character.equip", new { item = Items.Axe });
        var both = (await client.SendAsync("character.equip", new { item = Items.Pickaxe })).Payload();
        Assert.Equal([Items.Axe, Items.Pickaxe], Tools(both));
        Assert.DoesNotContain(Items.Pickaxe, await client.InventoryAsync());

        var off = (await client.SendAsync("character.unequip", new { backpack = true })).Payload();
        Assert.Equal(JsonValueKind.Null, off.GetProperty("backpack").ValueKind);
        Assert.Equal([Items.Axe], Tools(off));
        var inventory = await client.InventoryAsync();
        Assert.Equal(1, inventory[Items.Pickaxe]);
        Assert.Equal(1, inventory[Items.SchoolBackpack]);
        Assert.Equal(Items.InventorySlots, Slots(await client.SendAsync("inventory.get")));
    }

    [Fact]
    public async Task Only_the_million_dollar_backpack_gives_three_tool_slots()
    {
        using var client = await RichPlayerAsync(Items.Axe, Items.Pickaxe, Items.Hoe, Items.TycoonBackpack, Items.ExpeditionBackpack);
        await client.SendAsync("character.equip", new { item = Items.ExpeditionBackpack });
        await client.SendAsync("character.equip", new { item = Items.Axe });
        await client.SendAsync("character.equip", new { item = Items.Pickaxe });
        Assert.Equal("bad_slot", (await client.SendAsync("character.equip", new { item = Items.Hoe, slot = 2 })).ErrorCode());

        var tycoon = (await client.SendAsync("character.equip", new { item = Items.TycoonBackpack })).Payload();
        Assert.Equal(Items.InventorySlots + 100, tycoon.GetProperty("inventorySlots").GetInt32());
        var three = (await client.SendAsync("character.equip", new { item = Items.Hoe })).Payload();
        Assert.Equal([Items.Axe, Items.Pickaxe, Items.Hoe], Tools(three));
        Assert.Equal(1, (await client.InventoryAsync())[Items.ExpeditionBackpack]);

        // Back to a smaller backpack: the third tool goes back to the inventory.
        var smaller = (await client.SendAsync("character.equip", new { item = Items.ExpeditionBackpack })).Payload();
        Assert.Equal([Items.Axe, Items.Pickaxe], Tools(smaller));
        Assert.Equal(1, (await client.InventoryAsync())[Items.Hoe]);
    }

    [Fact]
    public async Task A_full_inventory_takes_no_new_kind_of_item_but_a_backpack_makes_room()
    {
        using var client = await RichPlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            await inventory.AddAsync(client.AccountId, Items.Nails, 1, CancellationToken.None);
            var held = (await client.InventoryAsync()).Keys.ToHashSet();
            foreach (var item in Items.Names.Keys.Where(i => !held.Contains(i) && !Items.IsBackpack(i) && i != Items.Sapling)
                         .Take(Items.InventorySlots - held.Count))
            {
                await inventory.AddAsync(client.AccountId, item, 1, CancellationToken.None);
            }
        }

        var have = await client.InventoryAsync();
        Assert.Equal(Items.InventorySlots, have.Count);
        Assert.Equal("inventory_full", (await client.SendAsync("market.buy", new { item = Items.Sapling, quantity = 1 })).ErrorCode());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 1 })).Type());

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.SchoolBackpack, quantity = 1 })).Type());
        Assert.Equal("character", (await client.SendAsync("character.equip", new { item = Items.SchoolBackpack })).Type());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Sapling, quantity = 1 })).Type());
    }
}
