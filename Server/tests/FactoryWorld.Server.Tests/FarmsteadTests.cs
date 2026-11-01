using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class FarmsteadTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static FarmLevel Farmstead => Farmsteads.Level(Items.Farmstead)!;

    private static Crop Wheat => Farming.ById[Items.Wheat];

    [Fact]
    public void A_farmstead_farms_around_its_clearing_but_not_on_the_path_from_its_door()
    {
        var farmed = Enumerable.Range(-6, 13).SelectMany(dy => Enumerable.Range(-6, 13).Select(dx => (dx, dy)))
            .Where(t => Farmsteads.Farms(Farmstead, 0, t.dx, t.dy)).ToList();
        Assert.DoesNotContain((0, 0), farmed);
        Assert.DoesNotContain((2, 2), farmed);
        Assert.DoesNotContain((5, 0), farmed);
        Assert.Contains((-4, -4), farmed);
        Assert.Contains((3, 0), farmed);
        // Facing south, its door looks down: the three columns below it stay a path.
        Assert.DoesNotContain((0, 3), farmed);
        Assert.DoesNotContain((1, 4), farmed);
        Assert.Contains((2, 4), farmed);
        Assert.Equal(81 - 25 - 6, farmed.Count);

        // Two that stand close share the land between them and keep off each other's clearings.
        var a = new PlotObject(Guid.NewGuid(), Items.Farmstead, 50, 50, 0, Guid.Empty);
        var b = new PlotObject(Guid.NewGuid(), Items.Farmstead, 56, 50, 0, Guid.Empty);
        var areas = Farmsteads.Areas([b, a]);
        Assert.Empty(areas[a.Id].Intersect(areas[b.Id]));
        Assert.DoesNotContain((54, 50), areas[a.Id]);
        Assert.DoesNotContain((52, 50), areas[b.Id]);
    }

    [Fact]
    public void A_farmstead_reaps_ripe_fields_one_after_another_sows_again_and_stops_when_full()
    {
        var patch = new FarmPatch(Farmstead, Wheat, new Dictionary<string, int>(), 0, Start);
        for (var i = 0; i < 20; i++)
        {
            patch.Open.Add((i, 0));
        }

        patch.Advance(Start);
        Assert.Equal(20, patch.Fields.Count);
        Assert.All(patch.Fields.Values, f => Assert.Equal(Start + Wheat.Grow, f.ReadyAt));
        Assert.Equal(Start + Wheat.Grow + Farmstead.ReapTime, patch.NextReap());

        // Ripe after 5 minutes, then a field every 4 s, each sown again at once.
        patch.Advance(Start + Wheat.Grow + TimeSpan.FromSeconds(40));
        Assert.Equal(10 * Wheat.Yield, patch.Store[Items.Wheat]);
        Assert.Equal(20, patch.Fields.Count);
        Assert.Equal(10, patch.Made.Count(m => m.Item == Items.Wheat));

        // Manure goes under the seed, one a field, and the crop grows a quarter faster.
        var manured = new FarmPatch(Farmstead, Wheat, new Dictionary<string, int>(), 0, Start);
        Assert.Equal(Farmsteads.MaxManure, manured.AddManure(1000));
        manured.Open.Add((0, 0));
        manured.Open.Add((1, 0));
        manured.Advance(Start);
        Assert.Equal(Farmsteads.MaxManure - 2, manured.Manure);
        Assert.Equal(Start + Wheat.Grow * 0.75, manured.Fields[(0, 0)].ReadyAt);

        // Full, it stands until crops are taken out, and goes on from that moment.
        var full = new FarmPatch(Farmstead, Wheat, new Dictionary<string, int> { [Items.Wheat] = Farmstead.Capacity - 2 }, 0, Start);
        full.Open.Add((0, 0));
        full.Open.Add((1, 0));
        full.Advance(Start);
        full.Advance(Start + TimeSpan.FromHours(1));
        Assert.True(full.Full);
        Assert.Equal(Farmstead.Capacity, full.Crops);
        Assert.Null(full.Full ? null : full.NextReap());
        Assert.Equal(Farmstead.Capacity, full.TakeAll(Start + TimeSpan.FromHours(2)).Sum(t => t.Quantity));
        Assert.Equal(Start + TimeSpan.FromHours(2), full.BusyUntil);
    }

    [Fact]
    public void The_manor_farm_is_the_better_farmstead_and_both_come_from_research_and_the_workshop()
    {
        var manor = Farmsteads.Level(Items.ManorFarm)!;
        Assert.True(manor.Radius > Farmstead.Radius);
        Assert.True(manor.Capacity > Farmstead.Capacity);
        Assert.True(manor.ReapTime < Farmstead.ReapTime);
        Assert.True(Farmsteads.GrowTime(manor, Wheat, false) < Farmsteads.GrowTime(Farmstead, Wheat, false));
        Assert.Equal(TechTree.Farmsteads, TechTree.ByItem[Items.Farmstead].Id);
        Assert.Equal(TechTree.ManorFarms, TechTree.ByItem[Items.ManorFarm].Id);
        Assert.Contains(Items.Farmstead, Workshop.Find(Items.ManorFarm)!.Ingredients.Select(i => i.Item));
        Assert.Equal(3, Footprints.Size(Items.Farmstead));
    }

    // --- On a plot -------------------------------------------------------------------------------

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
        var skip = Interlocked.Increment(ref _plots) + 11000;
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

    /// <summary>Buys a plot of <paramref name="biome"/> and finds a tile with a free square around it, 4 tiles each way.</summary>
    private async Task<(int X, int Y, int TileX, int TileY)> PlotAsync(GameClient client, Biome biome)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var (x, y) = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        const int reach = 4;
        for (var ty = 25; ty < PlotLayout.Size - 10; ty++)
        {
            for (var tx = 10; tx < PlotLayout.Size - 10; tx++)
            {
                var free = true;
                for (var dy = -reach; dy <= reach && free; dy++)
                {
                    for (var dx = -reach; dx <= reach && free; dx++)
                    {
                        free = bytes[(ty + dy) * PlotLayout.Size + tx + dx] == (byte)TileObject.None && (tx + dx, ty + dy) != PlotLayout.Spawn;
                    }
                }

                if (free)
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free square.");
    }

    private static JsonElement Farm(JsonElement reply, string id) =>
        reply.GetProperty("farmsteads").EnumerateArray().First(f => f.GetProperty("id").GetString() == id);

    [Fact]
    public async Task A_farmstead_on_farmland_sows_its_fields_reaps_them_and_keeps_the_crops_until_taken_out()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client, Biome.Farmland);
        await GiveAsync(client, Items.Farmstead, 1);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Farmstead, tileX = tx, tileY = ty, rotation = 0 });
        Assert.Equal("plot.placed", placed.Type());
        var id = placed.Payload().GetProperty("object").GetProperty("id").GetString()!;

        // It tills and sows wheat on its whole square at once.
        var reply = (await client.SendAsync("farmstead.get", new { x, y })).Payload();
        Assert.Equal(50, reply.GetProperty("fields").GetArrayLength());
        var sown = Farm(reply, id);
        Assert.Equal(1, sown.GetProperty("level").GetInt32());
        Assert.Equal(Items.Wheat, sown.GetProperty("crop").GetString());
        Assert.Equal(50, sown.GetProperty("fields").GetInt32());
        Assert.Equal(0, sown.GetProperty("crops").GetInt32());
        var fields = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("fields").EnumerateArray()
            .Count(f => f.GetProperty("crop").GetString() == Items.Wheat);
        Assert.Equal(50, fields);

        // Wheat is ripe after 5 minutes; it reaps a field every 4 s.
        factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(40));
        var taken = (await client.SendAsync("farmstead.take", new { x, y, id })).Payload();
        Assert.Equal(40, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0, Farm(taken, id).GetProperty("crops").GetInt32());
        Assert.Equal(40, (await client.InventoryAsync()).GetValueOrDefault(Items.Wheat));

        // From now on it sows potatoes, on manure.
        await GiveAsync(client, Items.Manure, 10);
        var potatoes = Farm((await client.SendAsync("farmstead.sow", new { x, y, id, crop = Items.Potato, manure = 10 })).Payload(), id);
        Assert.Equal(Items.Potato, potatoes.GetProperty("crop").GetString());
        Assert.Equal(10, potatoes.GetProperty("manure").GetInt32());
        Assert.Equal(0, (await client.InventoryAsync()).GetValueOrDefault(Items.Manure));
        Assert.Equal("unknown_crop", (await client.SendAsync("farmstead.sow", new { x, y, id, crop = Items.Coal })).ErrorCode());

        // Two more fields of wheat are reaped and sown with potatoes on manure; picked up, it gives back what it keeps and its fields go with it.
        factory.Time.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id })).Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(48, inventory.GetValueOrDefault(Items.Wheat));
        Assert.Equal(8, inventory.GetValueOrDefault(Items.Manure));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.Farmstead));
        Assert.Empty((await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("fields").EnumerateArray());
    }

    [Fact]
    public async Task A_farmstead_needs_fertile_land()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client, Biome.Grassland);
        await GiveAsync(client, Items.Farmstead, 1);
        Assert.Equal("not_fertile", (await client.SendAsync("plot.place", new { x, y, kind = Items.Farmstead, tileX = tx, tileY = ty, rotation = 0 })).ErrorCode());
    }
}
