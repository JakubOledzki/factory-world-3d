using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class InventoryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static Dictionary<string, int> Slots(JsonElement reply) =>
        reply.Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("slot").GetInt32());

    private async Task<GameClient> PlayerWithAsync(params string[] items)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        foreach (var item in items)
        {
            await inventory.AddAsync(client.AccountId, item, 3, CancellationToken.None);
        }

        return client;
    }

    [Fact]
    public async Task Items_get_the_first_free_tiles_and_keep_them()
    {
        using var client = await PlayerWithAsync(Items.Wood, Items.Stone, Items.Coal);

        var first = Slots(await client.SendAsync("inventory.get"));
        Assert.Equal([0, 1, 2], first.Values.Order());
        Assert.Equal(Items.InventorySlots, (await client.SendAsync("inventory.get")).Payload().GetProperty("slots").GetInt32());
        Assert.Equal(first, Slots(await client.SendAsync("inventory.get")));
    }

    [Fact]
    public async Task Moving_to_an_empty_tile_keeps_it_there()
    {
        using var client = await PlayerWithAsync(Items.Wood, Items.Stone);

        var moved = await client.SendAsync("inventory.move", new { item = Items.Wood, slot = 7 });

        Assert.Equal("inventory", moved.Type());
        Assert.Equal(7, Slots(moved)[Items.Wood]);
        Assert.Equal(7, Slots(await client.SendAsync("inventory.get"))[Items.Wood]);
    }

    [Fact]
    public async Task Moving_onto_another_item_swaps_them()
    {
        using var client = await PlayerWithAsync(Items.Wood, Items.Stone);
        var before = Slots(await client.SendAsync("inventory.get"));

        var after = Slots(await client.SendAsync("inventory.move", new { item = Items.Wood, slot = before[Items.Stone] }));

        Assert.Equal(before[Items.Stone], after[Items.Wood]);
        Assert.Equal(before[Items.Wood], after[Items.Stone]);
    }

    [Fact]
    public async Task A_used_up_item_frees_its_tile_for_new_ones()
    {
        using var client = await PlayerWithAsync(Items.Wood, Items.Stone);
        await client.SendAsync("inventory.move", new { item = Items.Wood, slot = 0 });
        await client.SendAsync("inventory.move", new { item = Items.Stone, slot = 5 });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            await inventory.RemoveAsync(client.AccountId, Items.Wood, 3, CancellationToken.None);
            await inventory.AddAsync(client.AccountId, Items.Coal, 1, CancellationToken.None);
        }

        var slots = Slots(await client.SendAsync("inventory.get"));

        Assert.False(slots.ContainsKey(Items.Wood));
        Assert.Equal(0, slots[Items.Coal]);
        Assert.Equal(5, slots[Items.Stone]);
    }

    [Fact]
    public async Task Bad_moves_are_refused()
    {
        using var client = await PlayerWithAsync(Items.Wood);

        Assert.Equal("bad_slot", (await client.SendAsync("inventory.move", new { item = Items.Wood, slot = 100 })).ErrorCode());
        Assert.Equal("bad_slot", (await client.SendAsync("inventory.move", new { item = Items.Wood, slot = -1 })).ErrorCode());
        Assert.Equal("not_enough_items", (await client.SendAsync("inventory.move", new { item = Items.Stone, slot = 3 })).ErrorCode());
        Assert.Equal("unknown_item", (await client.SendAsync("inventory.move", new { item = "gold", slot = 3 })).ErrorCode());
    }
}
