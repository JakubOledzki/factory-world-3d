using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Upgraded machines: brick and blast furnaces, fast and express belts and inserters, mines built up.</summary>
public sealed class UpgradeTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private const int East = 1;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static PlotObject Obj(string kind, int x, int y, int rotation = East) =>
        new(Guid.NewGuid(), kind, x, y, rotation, Guid.Empty);

    private static PlotLogistics Build(IReadOnlyList<PlotObject> objects, Dictionary<Guid, Contents> contents)
    {
        var ports = new Dictionary<Guid, IMachinePort>();
        foreach (var o in objects)
        {
            if (Storages.Find(o.Kind) is { } kind)
            {
                if (!contents.TryGetValue(o.Id, out var c))
                {
                    contents[o.Id] = c = new Contents();
                }

                ports[o.Id] = new StoragePort(kind, c, () => { });
            }
        }

        return new PlotLogistics(objects, contents, ports);
    }

    private static string?[] Lanes(string?[] left) => [.. left, null, null, null, null];

    private static int Stored(Contents c, string item) => c.Items?.FirstOrDefault(s => s.Item == item)?.Quantity ?? 0;

    [Theory]
    [InlineData(Items.Belt, 4)]
    [InlineData(Items.FastBelt, 2)]
    [InlineData(Items.ExpressBelt, 2)]
    public void Faster_belts_move_an_item_more_places_a_tick(string kind, int ticksToTheNextBelt)
    {
        var a = Obj(kind, 0, 0);
        var b = Obj(kind, 1, 0);
        var contents = new Dictionary<Guid, Contents> { [a.Id] = new() { Belt = Lanes([Items.Wood, null, null, null]) } };
        var logistics = Build([a, b], contents);

        logistics.Run(Start, Start + LogisticsRules.Tick * (ticksToTheNextBelt - 1));
        Assert.Null(contents.GetValueOrDefault(b.Id)?.Belt?[0]);
        Assert.Contains(Items.Wood, contents[a.Id].Belt!);

        logistics.Run(Start, Start + LogisticsRules.Tick);
        Assert.DoesNotContain(Items.Wood, contents[a.Id].Belt!);
        Assert.Contains(Items.Wood, contents[b.Id].Belt!);
    }

    [Fact]
    public void A_splitter_keeps_up_with_the_fast_belt_that_feeds_it()
    {
        var feed = Obj(Items.ExpressBelt, 0, 0);
        var splitter = Obj(Items.Splitter, 1, 0);
        var up = Obj(Items.ExpressBelt, 1, -1, 2);
        var down = Obj(Items.ExpressBelt, 1, 1, 0);
        var contents = new Dictionary<Guid, Contents> { [feed.Id] = new() { Belt = Lanes([Items.Coal, Items.Coal, Items.Coal, Items.Coal]) } };
        var logistics = Build([feed, splitter, up, down], contents);

        logistics.Run(Start, Start + LogisticsRules.Tick * 4);
        var outside = contents[up.Id].Belt!.Count(i => i is not null) + contents[down.Id].Belt!.Count(i => i is not null);
        Assert.True(outside >= 3, $"Only {outside} coal got through the splitter.");
    }

    [Theory]
    [InlineData(Items.Inserter, 4)]
    [InlineData(Items.FastInserter, 2)]
    [InlineData(Items.ExpressInserter, 1)]
    public void Faster_inserters_swing_in_fewer_ticks(string kind, int swing)
    {
        Assert.Equal(swing, LogisticsRules.Swing(kind));
        var from = Obj(Items.WoodenChest, 0, 0);
        var to = Obj(Items.WoodenChest, 2, 0);
        var contents = new Dictionary<Guid, Contents> { [from.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 100 }] } };
        var logistics = Build([from, Obj(kind, 1, 0), to], contents);

        // A pick-up, a swing there, a drop, a swing back: one item every 2 × swing + 2 ticks.
        logistics.Run(Start, Start + LogisticsRules.Tick * 40);
        var expected = 40 / (2 * swing + 2);
        Assert.InRange(Stored(contents[to.Id], Items.Stone), expected - 1, expected + 1);
    }

    [Fact]
    public void Brick_and_blast_furnaces_smelt_faster_and_hold_more()
    {
        Assert.Equal([1, 2, 4], Smelting.Levels.Select(l => l.Speed));
        Assert.Equal([50, 100, 200], Smelting.Levels.Select(l => l.MaxInput));

        var s = Smelting.Start(FurnaceState.Empty with { Input = Items.IronOre, InputQuantity = 100, Fuel = 100 }, Start);
        var minute = Start + TimeSpan.FromSeconds(60);
        Assert.Equal(6, Smelting.Advance(s, Smelting.Stone, minute).OutputQuantity);
        Assert.Equal(12, Smelting.Advance(s, Smelting.Level(Items.BrickFurnace)!, minute).OutputQuantity);

        // The blast furnace also gets 8 smelts out of a coal instead of 5.
        var blast = Smelting.Advance(s, Smelting.Level(Items.BlastFurnace)!, minute);
        Assert.Equal(24, blast.OutputQuantity);
        Assert.Equal(97, blast.Fuel);
    }

    [Fact]
    public void A_mine_built_up_gives_more_loads_quicker_and_rests_shorter()
    {
        Assert.Equal([50, 100, 200, 400, 800], Mining.Levels.Select(l => l.Units));
        Assert.True(Mining.Levels.Zip(Mining.Levels.Skip(1)).All(p => p.Second.Time < p.First.Time && p.Second.Refill < p.First.Refill));
        Assert.Empty(Mining.Levels[0].Cost);
        Assert.All(Mining.Levels.Skip(1), l => Assert.NotEmpty(l.Cost));
    }

    // --- In the game ---------------------------------------------------------------------------

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 4000;
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

    private static async Task<(int X, int Y, JsonElement Plot, byte[] Tiles)> BuyAsync(GameClient client, Biome biome)
    {
        var (x, y) = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return (x, y, plot.Payload(), tiles.ToArray());
    }

    private static (int TileX, int TileY) FreeTile(byte[] tiles)
    {
        for (var i = 20 * PlotLayout.Size; i < tiles.Length; i++)
        {
            if (PlotTiles.Clear(tiles, i % PlotLayout.Size, i / PlotLayout.Size))
            {
                return (i % PlotLayout.Size, i / PlotLayout.Size);
            }
        }

        throw new InvalidOperationException("No free tile.");
    }

    [Fact]
    public async Task Upgraded_machines_are_made_at_the_workbench_from_the_ones_below_and_go_on_plots_only()
    {
        using var client = await PlayerAsync();
        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("ingredients").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList());
        Assert.Contains(Items.Furnace, recipes[Items.BrickFurnace]);
        Assert.Contains(Items.BrickFurnace, recipes[Items.BlastFurnace]);
        Assert.Contains(Items.Belt, recipes[Items.FastBelt]);
        Assert.Contains(Items.FastBelt, recipes[Items.ExpressBelt]);
        Assert.Contains(Items.Inserter, recipes[Items.FastInserter]);
        Assert.Contains(Items.FastInserter, recipes[Items.ExpressInserter]);

        var (x, y, plot, tiles) = await BuyAsync(client, Biome.Grassland);
        var placeable = plot.GetProperty("placeable").EnumerateArray().Select(p => p.GetString()).ToList();
        foreach (var kind in new[] { Items.BrickFurnace, Items.BlastFurnace, Items.FastBelt, Items.ExpressBelt, Items.FastInserter, Items.ExpressInserter })
        {
            Assert.Contains(kind, placeable);
        }

        await GiveAsync(client, Items.BrickFurnace, 1);
        Assert.Equal("not_placeable", (await client.SendAsync("garage.place", new { kind = Items.BrickFurnace, x = 7, y = 4 })).ErrorCode());

        // The trader buys them back at a quick sale.
        var sold = await client.SendAsync("market.sell", new { item = Items.BrickFurnace, quantity = 1 });
        Assert.Equal("market.traded", sold.Type());
    }

    [Fact]
    public async Task A_brick_furnace_on_a_plot_smelts_twice_as_fast_and_keeps_its_kind_when_picked_up()
    {
        using var client = await PlayerAsync();
        var (x, y, _, tiles) = await BuyAsync(client, Biome.Grassland);
        var (tileX, tileY) = FreeTile(tiles);
        await GiveAsync(client, Items.BrickFurnace, 1);
        await GiveAsync(client, Items.IronOre, 100);
        await GiveAsync(client, Items.Coal, 30);
        var furnace = (await client.SendAsync("plot.place", new { x, y, kind = Items.BrickFurnace, tileX, tileY }))
            .Payload().GetProperty("object").GetProperty("id").GetString();

        // It takes 100 ore, more than the stone furnace's 50.
        var loaded = (await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 100 })).Payload();
        Assert.Equal(Items.BrickFurnace, loaded.GetProperty("kind").GetString());
        Assert.Equal(2, loaded.GetProperty("level").GetInt32());
        Assert.Equal(100, loaded.GetProperty("maxInput").GetInt32());
        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 30 });
        // Charcoal goes on the same fire.
        await GiveAsync(client, Items.Charcoal, 10);
        var fired = (await client.SendAsync("furnace.load", new { furnace, item = Items.Charcoal, quantity = 10 })).Payload();
        Assert.Equal(40, fired.GetProperty("fuel").GetInt32());

        factory.Time.Advance(TimeSpan.FromSeconds(20));
        var state = (await client.SendAsync("furnace.get", new { furnace })).Payload();
        Assert.Equal(5, state.GetProperty("smeltSeconds").GetDouble());
        Assert.Equal(4, state.GetProperty("output").GetProperty("quantity").GetInt32());

        // Picked up some time later, it hands back what it smelted by then at its own speed.
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = furnace })).Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(10, inventory.GetValueOrDefault(Items.IronIngot));
        Assert.Equal(90, inventory.GetValueOrDefault(Items.IronOre));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.BrickFurnace));
    }

    [Fact]
    public async Task Fast_belts_and_inserters_tell_the_client_how_fast_they_go()
    {
        using var client = await PlayerAsync();
        var (x, y, _, tiles) = await BuyAsync(client, Biome.Grassland);
        var (tileX, tileY) = FreeTile(tiles);
        await GiveAsync(client, Items.ExpressBelt, 1);
        await GiveAsync(client, Items.FastInserter, 1);
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.ExpressBelt, tileX, tileY, rotation = East })).Type());
        var free = FreeTile([.. tiles.Select((t, i) => i == tileY * PlotLayout.Size + tileX ? (byte)TileObject.Rock : t)]);
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.FastInserter, tileX = free.TileX, tileY = free.TileY })).Type());

        var logistics = (await client.SendAsync("logistics.get", new { x, y })).Payload();
        Assert.Equal(3, logistics.GetProperty("belts")[0].GetProperty("speed").GetInt32());
        Assert.Equal(2, logistics.GetProperty("inserters")[0].GetProperty("swingTicks").GetInt32());
    }

    [Fact]
    public async Task The_owner_builds_a_mine_up_with_ingots_and_timber_for_more_quicker_loads()
    {
        using var client = await PlayerAsync();
        var (x, y, plot, _) = await BuyAsync(client, Biome.Mountains);
        var mine = plot.GetProperty("mines")[0];
        Assert.Equal(1, mine.GetProperty("level").GetInt32());
        Assert.Equal(Mining.Units, mine.GetProperty("capacity").GetInt32());
        var upgrade = mine.GetProperty("upgrade");
        Assert.Equal(2, upgrade.GetProperty("level").GetInt32());
        object At(int id) => new { x, y, mine = id };

        // One load first, so the built-up mine keeps what was dug out.
        factory.Time.Advance(Mining.Time);
        Assert.Equal("plot.mined", (await client.SendAsync("plot.mine", At(0))).Type());

        Assert.Equal("not_enough_items", (await client.SendAsync("plot.upgradeMine", At(0))).ErrorCode());
        foreach (var cost in upgrade.GetProperty("cost").EnumerateArray())
        {
            await GiveAsync(client, cost.GetProperty("id").GetString()!, cost.GetProperty("quantity").GetInt32());
        }

        using (var stranger = await PlayerAsync())
        {
            Assert.Equal("not_owner", (await stranger.SendAsync("plot.upgradeMine", At(0))).ErrorCode());
        }

        var upgraded = await client.SendAsync("plot.upgradeMine", At(0));
        Assert.Equal("plot.mineUpgraded", upgraded.Type());
        var built = upgraded.Payload().GetProperty("mine");
        Assert.Equal(2, built.GetProperty("level").GetInt32());
        Assert.Equal(100, built.GetProperty("capacity").GetInt32());
        Assert.Equal(99, built.GetProperty("units").GetInt32());
        Assert.Equal(4 / Mining.Speed(Biome.Mountains), built.GetProperty("seconds").GetDouble(), 6);
        Assert.Equal(0, (await client.InventoryAsync()).GetValueOrDefault(Items.IronIngot));

        // Its loads now take 4 s.
        factory.Time.Advance(TimeSpan.FromSeconds(4));
        var load = await client.SendAsync("plot.mine", At(0));
        Assert.Equal(98, load.Payload().GetProperty("mine").GetProperty("units").GetInt32());

        var level3 = built.GetProperty("upgrade");
        foreach (var cost in level3.GetProperty("cost").EnumerateArray())
        {
            await GiveAsync(client, cost.GetProperty("id").GetString()!, cost.GetProperty("quantity").GetInt32());
        }

        var top = (await client.SendAsync("plot.upgradeMine", At(0))).Payload().GetProperty("mine");
        Assert.Equal(3, top.GetProperty("level").GetInt32());

        // Deeper levels take mine supports and steel; the fifth is the last.
        for (var level = 4; level <= Mining.Levels.Count; level++)
        {
            foreach (var cost in top.GetProperty("upgrade").GetProperty("cost").EnumerateArray())
            {
                await GiveAsync(client, cost.GetProperty("id").GetString()!, cost.GetProperty("quantity").GetInt32());
            }

            top = (await client.SendAsync("plot.upgradeMine", At(0))).Payload().GetProperty("mine");
            Assert.Equal(level, top.GetProperty("level").GetInt32());
        }

        Assert.Equal(JsonValueKind.Null, top.GetProperty("upgrade").ValueKind);
        Assert.Equal("mine_max_level", (await client.SendAsync("plot.upgradeMine", At(0))).ErrorCode());

        // The level stays with the mine.
        var again = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("mines")[0];
        Assert.Equal(5, again.GetProperty("level").GetInt32());
        Assert.Equal(798, again.GetProperty("units").GetInt32());
    }
}
