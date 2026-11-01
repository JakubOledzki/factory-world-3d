using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>What the logistics branch researches after the first belts: underground belts, long-armed inserters,
/// bigger grippers, the logistics depot, the express cable car and drones.</summary>
public sealed class LogisticsBranchTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // Rotations: 0 faces south, 1 east, 2 north, 3 west.
    private const int South = 0;
    private const int East = 1;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static PlotObject Obj(string kind, int x, int y, int rotation = East) =>
        new(Guid.NewGuid(), kind, x, y, rotation, Guid.Empty);

    private static PlotLogistics Build(
        IReadOnlyList<PlotObject> objects, Dictionary<Guid, Contents> contents, IReadOnlyDictionary<Guid, int>? grips = null)
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

        return new PlotLogistics(objects, contents, ports, grips: grips);
    }

    private static int Stored(Contents c, string item) => c.Items?.FirstOrDefault(s => s.Item == item)?.Quantity ?? 0;

    private static string?[] Lanes(string?[] left) => [.. left, null, null, null, null];

    private static int Count(Contents? c, string item) => c?.Belt?.Count(i => i == item) ?? 0;

    [Fact]
    public void An_underground_belt_carries_items_under_whatever_stands_between_to_the_next_one_ahead()
    {
        var feed = Obj(Items.Belt, 0, 0);
        var down = Obj(Items.UndergroundBelt, 1, 0);
        // A belt crossing the line above the tunnel, running south.
        var crossA = Obj(Items.Belt, 2, 0, South);
        var crossB = Obj(Items.Belt, 3, 0, South);
        var up = Obj(Items.UndergroundBelt, 4, 0);
        var after = Obj(Items.Belt, 5, 0);
        var contents = new Dictionary<Guid, Contents>
        {
            [feed.Id] = new() { Belt = Lanes([Items.Wood, Items.Wood, null, null]) },
            [crossA.Id] = new() { Belt = Lanes([Items.Stone, null, null, null]) },
        };
        var logistics = Build([feed, down, crossA, crossB, up, after], contents);

        logistics.Run(Start, Start + LogisticsRules.Tick * 40);
        Assert.Equal(2, Count(contents[after.Id], Items.Wood));
        Assert.Equal(0, Count(contents.GetValueOrDefault(crossA.Id), Items.Wood) + Count(contents.GetValueOrDefault(crossB.Id), Items.Wood));
        // The crossing belt goes on its way and never onto the tunnel.
        Assert.Equal(0, Count(contents.GetValueOrDefault(down.Id), Items.Stone) + Count(contents.GetValueOrDefault(up.Id), Items.Stone));
    }

    [Fact]
    public void Underground_belts_pair_up_from_the_back_up_to_four_tiles_apart()
    {
        var line = Enumerable.Range(0, 4).Select(i => Obj(Items.UndergroundBelt, i, 0)).ToList();
        var links = LogisticsRules.TunnelLinks(line);
        Assert.Equal(new Dictionary<Guid, Guid> { [line[0].Id] = line[1].Id, [line[2].Id] = line[3].Id }, links);

        var near = Obj(Items.UndergroundBelt, 10, 0);
        var five = Obj(Items.UndergroundBelt, 15, 0);
        Assert.Equal(five.Id, LogisticsRules.TunnelLinks([near, five])[near.Id]);
        var six = Obj(Items.UndergroundBelt, 16, 0);
        Assert.Empty(LogisticsRules.TunnelLinks([near, six]));

        // Facing another way they never pair.
        Assert.Empty(LogisticsRules.TunnelLinks([near, Obj(Items.UndergroundBelt, 12, 0, South)]));
    }

    [Theory]
    [InlineData(Items.Belt, 8)]
    [InlineData(Items.ExpressBelt, 3)]
    public void An_underground_belt_keeps_up_with_the_belt_that_feeds_it(string belt, int ticks)
    {
        var feed = Obj(belt, 0, 0);
        var down = Obj(Items.UndergroundBelt, 1, 0);
        var up = Obj(Items.UndergroundBelt, 3, 0);
        var after = Obj(belt, 4, 0);
        var contents = new Dictionary<Guid, Contents> { [down.Id] = new() { Belt = Lanes([Items.Coal, null, null, null]) } };
        var logistics = Build([feed, down, up, after], contents);

        // Through both ends, four places each, at the speed of the feeding belt.
        logistics.Run(Start, Start + LogisticsRules.Tick * (ticks - 1));
        Assert.Equal(0, Count(contents.GetValueOrDefault(after.Id), Items.Coal));
        logistics.Run(Start, Start + LogisticsRules.Tick);
        Assert.Equal(1, Count(contents[after.Id], Items.Coal));
    }

    [Fact]
    public void A_long_armed_inserter_reaches_two_tiles_each_way()
    {
        var from = Obj(Items.WoodenChest, 0, 0);
        var nearFrom = Obj(Items.WoodenChest, 1, 0);
        var arm = Obj(Items.LongInserter, 2, 0);
        var nearTo = Obj(Items.WoodenChest, 3, 0);
        var to = Obj(Items.WoodenChest, 4, 0);
        var contents = new Dictionary<Guid, Contents>
        {
            [from.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 10 }] },
            [nearFrom.Id] = new() { Items = [new() { Item = Items.Coal, Quantity = 10 }] },
        };
        var logistics = Build([from, nearFrom, arm, nearTo, to], contents);
        Assert.Contains((0, 0), PlotLogistics.Reach([arm]));
        Assert.Contains((4, 0), PlotLogistics.Reach([arm]));

        // A pick-up, a swing of 3 ticks there, a drop, 3 back: one stone every 8 ticks.
        logistics.Run(Start, Start + LogisticsRules.Tick * 40);
        Assert.InRange(Stored(contents[to.Id], Items.Stone), 4, 6);
        Assert.Equal(10, Stored(contents[nearFrom.Id], Items.Coal));
        Assert.Equal(0, Stored(contents[nearTo.Id], Items.Stone) + Stored(contents[nearTo.Id], Items.Coal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Bigger_grippers_move_more_pieces_a_swing(int grip)
    {
        var from = Obj(Items.WoodenChest, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var to = Obj(Items.WoodenChest, 2, 0);
        var contents = new Dictionary<Guid, Contents> { [from.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 100 }] } };
        var logistics = Build([from, inserter, to], contents, new Dictionary<Guid, int> { [inserter.Id] = grip });

        // One swing there and back every 2 × 4 + 2 ticks, whatever it carries.
        logistics.Run(Start, Start + LogisticsRules.Tick * 40);
        Assert.InRange(Stored(contents[to.Id], Items.Stone), 3 * grip, 4 * grip);
        Assert.Equal(100, Stored(contents[from.Id], Items.Stone) + Stored(contents[to.Id], Items.Stone) + contents[inserter.Id].Held());
    }

    [Fact]
    public void An_inserter_with_a_full_hand_drops_on_a_belt_what_fits_and_keeps_the_rest()
    {
        var from = Obj(Items.WoodenChest, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var belt = Obj(Items.Belt, 2, 0, South);
        var contents = new Dictionary<Guid, Contents>
        {
            [inserter.Id] = new() { Hand = Items.Stone, Count = 3, Swing = LogisticsRules.SwingTicks },
            // Only one place free on the belt, which leads nowhere.
            [belt.Id] = new() { Belt = [Items.Coal, Items.Coal, Items.Coal, Items.Coal, Items.Coal, Items.Coal, Items.Coal, null] },
        };
        var logistics = Build([from, inserter, belt], contents, new Dictionary<Guid, int> { [inserter.Id] = 3 });

        logistics.Run(Start, Start + LogisticsRules.Tick * 5);
        Assert.Equal(Items.Stone, contents[inserter.Id].Hand);
        Assert.Equal(2, contents[inserter.Id].Held());
        Assert.Equal(1, Count(contents[belt.Id], Items.Stone));
        Assert.Contains((Items.Stone, 2), contents[inserter.Id].All());
    }

    [Fact]
    public void Drones_fly_items_from_ports_without_a_filter_to_the_ports_that_want_them()
    {
        var sender = Obj(Items.DronePort, 0, 0);
        var stone = Obj(Items.DronePort, 20, 5);
        var coal = Obj(Items.DronePort, 3, 30);
        var other = Obj(Items.DronePort, 10, 10);
        var contents = new Dictionary<Guid, Contents>
        {
            [sender.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 10 }, new() { Item = Items.Wood, Quantity = 5 }] },
            [stone.Id] = new() { Only = Items.Stone },
            [coal.Id] = new() { Only = Items.Coal },
        };
        var logistics = Build([sender, stone, coal, other], contents);

        logistics.Run(Start, Start + LogisticsRules.Tick * 4);
        Assert.Equal(4 * LogisticsRules.DroneLoad, Stored(contents[stone.Id], Items.Stone));

        logistics.Run(Start, Start + LogisticsRules.Tick * 40);
        Assert.Equal(10, Stored(contents[stone.Id], Items.Stone));
        Assert.Equal(0, Stored(contents[sender.Id], Items.Stone));
        // Wood has no port that wants it, and a port without a filter takes nothing from the others.
        Assert.Equal(5, Stored(contents[sender.Id], Items.Wood));
        Assert.Empty(contents[coal.Id].Items ?? []);
        Assert.Empty(contents.GetValueOrDefault(other.Id)?.Items ?? []);
    }

    [Fact]
    public void An_express_cable_station_sends_its_items_twice_as_far()
    {
        var plain = Obj(Items.CableStation, 0, 0);
        var express = Obj(Items.ExpressCableStation, 0, 2);
        var farFromPlain = Obj(Items.CableStation, 40, 0);
        var farFromExpress = Obj(Items.CableStation, 40, 2);
        var links = LogisticsRules.CableLinks([plain, express, farFromPlain, farFromExpress]);
        Assert.False(links.ContainsKey(plain.Id));
        Assert.Equal(farFromExpress.Id, links[express.Id]);
        Assert.Equal(3, LogisticsRules.BeltSpeed(Items.ExpressCableStation));
        Assert.Equal(LogisticsRules.ExpressCableSpan, LogisticsRules.Span(Items.ExpressCableStation));
    }

    // --- On a plot ------------------------------------------------------------------------------

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

    /// <summary>Researches a technology from start to end, its cost given.</summary>
    private async Task ResearchAsync(GameClient client, string technology)
    {
        foreach (var (item, quantity) in TechTree.ById[technology].Cost)
        {
            await GiveAsync(client, item, quantity);
        }

        Assert.Equal("research", (await client.SendAsync("research.start", new { technology })).Type());
        factory.Time.Advance(TechTree.ById[technology].Time);
        await client.SendAsync("research.get");
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 3000;
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

    /// <summary>A plot of the player's with <paramref name="length"/> free tiles in a row from (tileX, tileY) east, and the row under it free too.</summary>
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
        for (var ty = 20; ty < PlotLayout.Size - 1; ty++)
        {
            for (var tx = 0; tx + Math.Max(length, 4) <= PlotLayout.Size; tx++)
            {
                if (Enumerable.Range(tx, length).All(i => bytes[ty * PlotLayout.Size + i] == (byte)TileObject.None && bytes[(ty + 1) * PlotLayout.Size + i] == (byte)TileObject.None))
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free row.");
    }

    /// <summary>Powers a row of up to five tiles from (tileX, tileY): a pole and a generator with coal in the row under it.</summary>
    private async Task PowerAsync(GameClient client, int x, int y, int tileX, int tileY, int coal = 20)
    {
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, coal);
        await PlaceAsync(client, x, y, Items.Pole, tileX + 2, tileY + 1);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tileX + 3, tileY + 1);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = coal })).Type());
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = East)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static long StoredIn(JsonElement storage, string item) =>
        storage.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("id").GetString() == item)
            .Sum(i => i.GetProperty("quantity").GetInt64());

    [Fact]
    public async Task The_new_machines_are_researched_in_the_logistics_branch_and_go_on_plots()
    {
        using var client = await PlayerAsync();
        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal("Taśmociągi podziemne", recipes[Items.UndergroundBelt].GetProperty("locked").GetString());
        Assert.Equal("Długoramienne podajniki", recipes[Items.LongInserter].GetProperty("locked").GetString());
        Assert.Equal("Magazyn logistyczny", recipes[Items.LogisticsDepot].GetProperty("locked").GetString());
        Assert.Equal("Ekspresowa kolejka linowa", recipes[Items.ExpressCableStation].GetProperty("locked").GetString());
        Assert.Equal("Drony transportowe", recipes[Items.DronePort].GetProperty("locked").GetString());

        foreach (var id in new[]
        {
            TechTree.UndergroundBelts, TechTree.LongInserters, TechTree.BiggerGrippers, TechTree.IndustrialGrippers,
            TechTree.LogisticsDepot, TechTree.ExpressCable, TechTree.Drones,
        })
        {
            Assert.Equal("logistics", TechTree.BranchOf[id].Id);
        }

        var tree = (await client.SendAsync("research.get")).Payload().GetProperty("technologies").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!);
        Assert.Equal(0, tree[TechTree.BiggerGrippers].GetProperty("unlocks").GetArrayLength());
        Assert.Equal(Items.FastInserter, tree[TechTree.BiggerGrippers].GetProperty("icon").GetString());

        var (x, y, _, _) = await PlotWithFreeRowAsync(client, 1);
        var placeable = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("placeable").EnumerateArray()
            .Select(p => p.GetString()).ToList();
        foreach (var kind in new[] { Items.UndergroundBelt, Items.LongInserter, Items.LogisticsDepot, Items.ExpressCableStation, Items.DronePort })
        {
            Assert.Contains(kind, placeable);
        }
    }

    [Fact]
    public async Task Researched_grippers_make_the_owners_inserters_carry_two_pieces_a_swing()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 5);
        await ResearchAsync(client, TechTree.Logistics);
        await ResearchAsync(client, TechTree.Automation);
        await ResearchAsync(client, TechTree.FastLogistics);
        await ResearchAsync(client, TechTree.BiggerGrippers);
        await GiveAsync(client, Items.WoodenChest, 2);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.Stone, 100);

        var source = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var target = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 2, ty);
        await client.SendAsync("storage.put", new { x, y, id = source, item = Items.Stone, quantity = 100 });
        await PowerAsync(client, x, y, tx, ty);

        // 50 ticks: five swings there and back, two stones each.
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        var logistics = (await client.SendAsync("logistics.get", new { x, y })).Payload();
        var storages = logistics.GetProperty("storages").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.InRange(StoredIn(storages[target], Items.Stone), 8, 10);
        var inserter = logistics.GetProperty("inserters")[0];
        Assert.Equal(1, inserter.GetProperty("reach").GetInt32());
        Assert.Equal(100, StoredIn(storages[source], Items.Stone) + StoredIn(storages[target], Items.Stone) + inserter.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Drone_ports_on_a_plot_fly_stone_to_the_port_set_to_take_it()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 5);
        await GiveAsync(client, Items.DronePort, 2);
        await GiveAsync(client, Items.LogisticsDepot, 1);
        await GiveAsync(client, Items.Stone, 30);

        var sender = await PlaceAsync(client, x, y, Items.DronePort, tx, ty);
        var receiver = await PlaceAsync(client, x, y, Items.DronePort, tx + 4, ty);
        var depot = await PlaceAsync(client, x, y, Items.LogisticsDepot, tx + 2, ty);
        Assert.Equal("logistics", (await client.SendAsync("filter.set", new { x, y, id = receiver, item = Items.Stone })).Type());
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x, y, id = sender, item = Items.Stone, quantity = 20 })).Type());
        var stored = (await client.SendAsync("storage.put", new { x, y, id = depot, item = Items.Stone, quantity = 10 })).Payload();
        Assert.Equal(48, stored.GetProperty("slots").GetInt32());
        Assert.Equal(200, stored.GetProperty("slotSize").GetInt32());

        // Without power the drones stay put.
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        var idle = (await client.SendAsync("storage.get", new { x, y, id = receiver })).Payload();
        Assert.Equal(0, StoredIn(idle, Items.Stone));

        await PowerAsync(client, x, y, tx, ty);
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        var flown = (await client.SendAsync("storage.get", new { x, y, id = receiver })).Payload();
        Assert.Equal(20, StoredIn(flown, Items.Stone));
        // A depot is not a port of drones: they leave it alone.
        Assert.Equal(10, StoredIn((await client.SendAsync("storage.get", new { x, y, id = depot })).Payload(), Items.Stone));
    }
}
