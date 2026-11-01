using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Building plans put up and taken down in one go, and the marks over machines that stand still.</summary>
public sealed class PlotPlanTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

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

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 5000;
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

    /// <summary>A plot of the player's with 8 free tiles in a row from (tileX, tileY) east, and the three rows under it free too.</summary>
    private async Task<(int X, int Y, int TileX, int TileY)> PlotAsync(GameClient client)
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
        for (var ty = 20; ty + 4 < PlotLayout.Size; ty++)
        {
            for (var tx = 1; tx + 9 <= PlotLayout.Size; tx++)
            {
                if (Enumerable.Range(tx - 1, 9).All(i => Enumerable.Range(ty - 1, 5).All(row => bytes[row * PlotLayout.Size + i] == (byte)TileObject.None)))
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free row.");
    }

    private static object Belt(int tileX, int tileY) => new { kind = Items.Belt, tileX, tileY, rotation = 1 };

    private static IEnumerable<string> Kinds(JsonElement plot) =>
        plot.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("kind").GetString()!);

    [Fact]
    public async Task A_building_plan_puts_up_all_its_objects_at_once_and_takes_them_from_the_inventory()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);
        await GiveAsync(client, Items.Belt, 3);
        await GiveAsync(client, Items.Inserter, 1);

        var reply = await client.SendAsync("plot.placeMany", new
        {
            x, y,
            objects = new object[] { Belt(tx, ty), Belt(tx + 1, ty), Belt(tx + 2, ty), new { kind = Items.Inserter, tileX = tx + 3, tileY = ty, rotation = 1 } },
        });
        Assert.Equal("plot.placedMany", reply.Type());
        var placed = reply.Payload().GetProperty("objects").EnumerateArray().ToList();
        Assert.Equal(4, placed.Count);
        Assert.Equal([tx, tx + 1, tx + 2, tx + 3], placed.Select(o => o.GetProperty("tileX").GetInt32()));
        Assert.All(placed, o => Assert.Equal(1, o.GetProperty("rotation").GetInt32()));

        var inventory = await client.InventoryAsync();
        Assert.False(inventory.ContainsKey(Items.Belt));
        Assert.False(inventory.ContainsKey(Items.Inserter));
        var plot = await client.SendAsync("plot.enter", new { x, y });
        Assert.Equal(3, Kinds(plot.Payload()).Count(k => k == Items.Belt));
    }

    [Fact]
    public async Task A_plan_that_cannot_stand_whole_puts_up_nothing()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);

        // Two belts in the inventory for a plan of three: none goes up, both stay in the inventory.
        await GiveAsync(client, Items.Belt, 2);
        var short_ = await client.SendAsync("plot.placeMany", new { x, y, objects = new[] { Belt(tx, ty), Belt(tx + 1, ty), Belt(tx + 2, ty) } });
        Assert.Equal("error", short_.Type());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Belt]);

        // Two objects of the plan on the same tile: the second is blocked, so the first is not put up either.
        var overlap = await client.SendAsync("plot.placeMany", new { x, y, objects = new[] { Belt(tx, ty), Belt(tx, ty) } });
        Assert.Equal("blocked", overlap.ErrorCode());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Belt]);
        Assert.DoesNotContain(Items.Belt, Kinds((await client.SendAsync("plot.enter", new { x, y })).Payload()));
    }

    [Fact]
    public async Task A_plan_must_hold_one_to_a_hundred_objects_and_go_on_the_players_own_plot()
    {
        using var owner = await PlayerAsync();
        using var stranger = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(owner);
        await GiveAsync(stranger, Items.Belt, 1);

        Assert.Equal("bad_plan", (await owner.SendAsync("plot.placeMany", new { x, y, objects = Array.Empty<object>() })).ErrorCode());
        Assert.Equal("bad_plan", (await owner.SendAsync("plot.placeMany", new { x, y })).ErrorCode());
        var huge = Enumerable.Range(0, PlotObjects.MaxPlan + 1).Select(i => Belt(i % 8, ty)).ToArray();
        Assert.Equal("bad_plan", (await owner.SendAsync("plot.placeMany", new { x, y, objects = huge })).ErrorCode());
        Assert.Equal("not_owner", (await stranger.SendAsync("plot.placeMany", new { x, y, objects = new[] { Belt(tx, ty) } })).ErrorCode());
        Assert.Equal(1, (await stranger.InventoryAsync())[Items.Belt]);
        Assert.Equal("not_placeable", (await owner.SendAsync("plot.placeMany", new
        {
            x, y, objects = new[] { new { kind = Items.Wood, tileX = tx, tileY = ty, rotation = 0 } },
        })).ErrorCode());
    }

    [Fact]
    public async Task Undoing_a_plan_takes_all_its_objects_back_into_the_inventory_at_once()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);
        await GiveAsync(client, Items.Belt, 3);
        var placed = await client.SendAsync("plot.placeMany", new { x, y, objects = new[] { Belt(tx, ty), Belt(tx + 1, ty), Belt(tx + 2, ty) } });
        var ids = placed.Payload().GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("id").GetString()!).ToArray();

        // The same id twice, or one that is not there, takes nothing back.
        Assert.Equal("bad_plan", (await client.SendAsync("plot.pickupMany", new { x, y, ids = new[] { ids[0], ids[0] } })).ErrorCode());
        Assert.Equal("no_object", (await client.SendAsync("plot.pickupMany", new { x, y, ids = new[] { ids[0], Guid.NewGuid().ToString() } })).ErrorCode());
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Belt));

        var undone = await client.SendAsync("plot.pickupMany", new { x, y, ids });
        Assert.Equal("plot.pickedUpMany", undone.Type());
        Assert.Equal(3, undone.Payload().GetProperty("ids").GetArrayLength());
        Assert.Equal(3, (await client.InventoryAsync())[Items.Belt]);
        Assert.DoesNotContain(Items.Belt, Kinds((await client.SendAsync("plot.enter", new { x, y })).Payload()));
    }

    [Fact]
    public async Task Clearing_an_area_takes_everything_in_it_back_into_the_inventory_with_what_it_holds()
    {
        using var client = await PlayerAsync();
        using var stranger = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);
        await GiveAsync(client, Items.Belt, 4);
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 2);
        await client.SendAsync("plot.placeMany", new { x, y, objects = new[] { Belt(tx, ty), Belt(tx + 1, ty), Belt(tx + 2, ty), Belt(tx + 7, ty) } });
        var furnace = (await client.SendAsync("plot.place", new { x, y, kind = Items.Furnace, tileX = tx + 4, tileY = ty + 1, rotation = 0 }))
            .Payload().GetProperty("object").GetProperty("id").GetString()!;
        Assert.Equal("furnace", (await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 2 })).Type());

        // Only the owner clears their plot, and only inside it.
        Assert.Equal("not_owner", (await stranger.SendAsync("plot.pickupArea", new { x, y, fromX = tx, fromY = ty, toX = tx + 5, toY = ty + 2 })).ErrorCode());
        Assert.Equal("bad_area", (await client.SendAsync("plot.pickupArea", new { x, y, fromX = -1, fromY = ty, toX = tx, toY = ty })).ErrorCode());

        // Corners in any order; the belt east of the rectangle stays.
        var reply = await client.SendAsync("plot.pickupArea", new { x, y, fromX = tx + 5, fromY = ty + 2, toX = tx, toY = ty });
        Assert.Equal("plot.pickedUpArea", reply.Type());
        Assert.Equal(4, reply.Payload().GetProperty("objects").GetArrayLength());
        Assert.Equal(0, reply.Payload().GetProperty("left").GetInt32());
        var inventory = await client.InventoryAsync();
        Assert.Equal(3, inventory[Items.Belt]);
        Assert.Equal(1, inventory[Items.Furnace]);
        Assert.Equal(2, inventory[Items.IronOre]);
        var kinds = Kinds((await client.SendAsync("plot.enter", new { x, y })).Payload()).ToList();
        Assert.Equal([Items.Belt], kinds.Where(k => k is Items.Belt or Items.Furnace));

        // An empty area takes nothing and is no error.
        var empty = await client.SendAsync("plot.pickupArea", new { x, y, fromX = tx, fromY = ty, toX = tx + 5, toY = ty + 2 });
        Assert.Equal(0, empty.Payload().GetProperty("objects").GetArrayLength());
    }

    [Fact]
    public async Task The_plot_tells_which_machines_stand_still_and_what_they_wait_for()
    {
        using var client = await PlayerAsync();
        using var stranger = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 2);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Furnace, tileX = tx + 1, tileY = ty + 1, rotation = 0 });
        var furnace = placed.Payload().GetProperty("object").GetProperty("id").GetString()!;

        // Ore without coal: it waits for fuel, and says it wants coal.
        Assert.Equal("furnace", (await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 2 })).Type());
        var reply = await client.SendAsync("stats.machines", new { x, y });
        Assert.Equal("stats.machines", reply.Type());
        var status = reply.Payload().GetProperty("machines").EnumerateArray().Single();
        Assert.Equal(furnace, status.GetProperty("id").GetString());
        Assert.Equal("no_fuel", status.GetProperty("state").GetString());
        Assert.Equal([Items.Coal], status.GetProperty("missing").EnumerateArray().Select(m => m.GetProperty("id").GetString()));

        await GiveAsync(client, Items.Coal, 1);
        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 1 });
        status = (await client.SendAsync("stats.machines", new { x, y })).Payload().GetProperty("machines").EnumerateArray().Single();
        Assert.Equal("working", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("fired").GetBoolean());

        // Someone with no right to work there sees nothing of it.
        Assert.Equal("not_owner", (await stranger.SendAsync("stats.machines", new { x, y })).ErrorCode());
    }
}
