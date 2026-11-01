using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class FeederTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private const int East = 1;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void A_feeder_feeds_the_hungry_animals_in_its_reach_with_what_each_eats_until_it_runs_out()
    {
        var feeder = new Trough(Guid.NewGuid(), Guid.NewGuid(), Items.Feeder, 10, 10, new Dictionary<string, int> { [Items.Wheat] = 5, [Items.ConcentrateFeed] = 2 });
        var yard = new Yard(Biome.Grassland, [feeder],
        [
            new Beast(10 + Feeders.Reach, 10, Items.Chicken, null, 0),
            new Beast(10, 10 - Feeders.Reach, Items.Horse, null, 0),
            new Beast(11, 11, Items.Cow, null, 0),
            new Beast(10 + Feeders.Reach + 1, 10, Items.Sheep, null, 0),
        ]);
        yard.Feed(Start);

        // The horse gets the concentrate, the hen and the cow wheat; the sheep stands too far, and one piece of wheat is left.
        Assert.Equal(3, yard.Fed.Count);
        Assert.Equal(Start + TimeSpan.FromMinutes(3), yard.Fed[(10 + Feeders.Reach, 10)]);
        Assert.Equal(Start + TimeSpan.FromMinutes(9), yard.Fed[(11, 11)]);
        Assert.Contains((10, 10 - Feeders.Reach), yard.Fed.Keys);
        Assert.DoesNotContain((10 + Feeders.Reach + 1, 10), yard.Fed.Keys);
        Assert.Equal(1, feeder.Feed);
        Assert.Equal(6, yard.Eaten.Sum(e => e.Entry.Consumed));

        // One piece is not a meal; a second makes one, for the next hungry animal in reach.
        var later = new Yard(Biome.Grassland, [feeder], [new Beast(12, 12, Items.Chicken, null, 0)]);
        later.Feed(Start);
        Assert.Empty(later.Fed);
        Assert.Equal(Feeders.Capacity - 1, feeder.Add(Items.Corn, 1000));
        later.Feed(Start);
        Assert.Single(later.Fed);
        Assert.Equal(Feeders.Capacity - 2, feeder.Feed);
    }

    [Fact]
    public void The_feeder_is_researched_made_at_the_workbench_and_bought_from_the_trader_for_more_than_it_pays()
    {
        Assert.Equal(TechTree.Feeders, TechTree.ByItem[Items.Feeder].Id);
        Assert.NotNull(Workshop.Find(Items.Feeder));
        Assert.True(NpcPrices.Sells[Items.Feeder] > NpcPrices.Buys[Items.Feeder]);
    }

    // --- On a plot -------------------------------------------------------------------------------

    [Fact]
    public async Task An_inserter_fills_a_feeder_and_it_feeds_the_hens_around_it_when_they_come_out_and_after_each_collection()
    {
        using var client = await PlayerAsync();
        using var other = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 6);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.Feeder, 1);
        await GiveAsync(client, Items.Chicken, 2);
        await GiveAsync(client, Items.Wheat, 20);

        // chest → inserter → feeder, a hen beside it that waits hungry while the feeder is empty.
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var feeder = await PlaceAsync(client, x, y, Items.Feeder, tx + 2, ty);
        var first = await client.SendAsync("plot.release", new { x, y, tileX = tx + 3, tileY = ty + 1, item = Items.Chicken });
        Assert.Equal(JsonValueKind.Null, first.Payload().GetProperty("animal").GetProperty("readyAt").ValueKind);
        await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Wheat, quantity = 10 });
        await PowerAsync(client, x, y, tx, ty);

        // The inserter fills it and the hen eats at once; on the steppe she lays in 3 minutes.
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var animals = (await client.SendAsync("plot.animals", new { x, y })).Payload().GetProperty("animals");
        var hen = Assert.Single(animals.EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, hen.GetProperty("readyAt").ValueKind);
        var state = (await client.SendAsync("feeder.get", new { x, y, id = feeder })).Payload().GetProperty("feeder");
        Assert.Equal(8, state.GetProperty("feed").GetInt32());
        Assert.Equal(1, state.GetProperty("animals").GetInt32());
        Assert.Equal(0, state.GetProperty("hungry").GetInt32());

        // A second hen let out in its reach is fed the moment she comes out.
        var second = await client.SendAsync("plot.release", new { x, y, tileX = tx + 4, tileY = ty + 1, item = Items.Chicken });
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromMinutes(3)).ToUnixTimeMilliseconds(),
            second.Payload().GetProperty("animal").GetProperty("readyAt").GetInt64());

        // Eggs are still collected by hand, and the hen is fed again right after.
        factory.Time.Advance(TimeSpan.FromMinutes(3));
        var collected = await client.SendAsync("plot.collect", new { x, y, tileX = tx + 3, tileY = ty + 1 });
        Assert.Equal("plot.animal", collected.Type());
        Assert.Equal(Items.Egg, collected.Payload().GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromMinutes(3)).ToUnixTimeMilliseconds(),
            collected.Payload().GetProperty("animal").GetProperty("readyAt").GetInt64());
        state = (await client.SendAsync("feeder.get", new { x, y, id = feeder })).Payload().GetProperty("feeder");
        Assert.Equal(4, state.GetProperty("feed").GetInt32());

        // Feed goes in by hand too, as much as there is room for; the trader's stone does not.
        Assert.Equal("bad_feed", (await client.SendAsync("feeder.fill", new { x, y, id = feeder, item = Items.Stone, quantity = 1 })).ErrorCode());
        Assert.Equal("bad_quantity", (await client.SendAsync("feeder.fill", new { x, y, id = feeder, item = Items.Wheat, quantity = 0 })).ErrorCode());
        Assert.Equal("not_enough_items", (await client.SendAsync("feeder.fill", new { x, y, id = feeder, item = Items.Corn, quantity = 5 })).ErrorCode());
        var filled = (await client.SendAsync("feeder.fill", new { x, y, id = feeder, item = Items.Wheat, quantity = 6 })).Payload().GetProperty("feeder");
        Assert.Equal(10, filled.GetProperty("feed").GetInt32());
        Assert.Equal(4, (await client.InventoryAsync()).GetValueOrDefault(Items.Wheat));
        Assert.Equal("not_owner", (await other.SendAsync("feeder.get", new { x, y, id = feeder })).ErrorCode());
        Assert.Equal("not_owner", (await other.SendAsync("feeder.take", new { x, y, id = feeder })).ErrorCode());
        Assert.Equal("not_owner", (await other.SendAsync("plot.animals", new { x, y })).ErrorCode());

        // Taken out, the feed goes back to the inventory; picked up, so does the feeder with what was put in since.
        var taken = (await client.SendAsync("feeder.take", new { x, y, id = feeder })).Payload();
        Assert.Equal(10, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0, taken.GetProperty("feeder").GetProperty("feed").GetInt32());
        Assert.Equal(14, (await client.InventoryAsync()).GetValueOrDefault(Items.Wheat));
        await client.SendAsync("feeder.fill", new { x, y, id = feeder, item = Items.Wheat, quantity = 3 });
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = feeder })).Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(14, inventory.GetValueOrDefault(Items.Wheat));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.Feeder));
    }

    // --- Helpers ---------------------------------------------------------------------------------

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
        var skip = Interlocked.Increment(ref _plots) + 13000;
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
        await PlaceAsync(client, x, y, Items.Pole, tileX + 1, tileY + 2);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tileX + 2, tileY + 2);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = East)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }
}
