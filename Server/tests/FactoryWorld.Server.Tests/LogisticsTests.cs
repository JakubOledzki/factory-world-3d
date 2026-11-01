using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class LogisticsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // Rotations: 0 faces south, 1 east, 2 north, 3 west.
    private const int East = 1;
    private const int West = 3;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    private static PlotObject Obj(string kind, int x, int y, int rotation = East) =>
        new(Guid.NewGuid(), kind, x, y, rotation, Guid.Empty);

    private static (PlotLogistics Logistics, Dictionary<Guid, Contents> Contents) Build(
        IReadOnlyList<PlotObject> objects, Dictionary<Guid, Contents>? contents = null)
    {
        contents ??= [];
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

        return (new PlotLogistics(objects, contents, ports), contents);
    }

    private static int Stored(Contents c, string item) => c.Items?.FirstOrDefault(s => s.Item == item)?.Quantity ?? 0;

    // A belt's places: the left lane, then the right one.
    private static string?[] Lanes(string?[] left, string?[]? right = null) => [.. left, .. right ?? [null, null, null, null]];

    private static string?[] Lane(Contents c, int lane) => c.Belt![(lane * LogisticsRules.BeltSlots)..((lane + 1) * LogisticsRules.BeltSlots)];

    [Fact]
    public void A_belt_moves_an_item_one_place_a_tick_onto_the_next_belt_and_stops_at_the_end()
    {
        var a = Obj(Items.Belt, 0, 0);
        var b = Obj(Items.Belt, 1, 0);
        var contents = new Dictionary<Guid, Contents> { [a.Id] = new() { Belt = Lanes([Items.Wood, null, null, null], [Items.Stone, null, null, null]) } };
        var (logistics, _) = Build([a, b], contents);

        logistics.Run(Start, Start + LogisticsRules.Tick * 3);
        Assert.Equal(Items.Wood, contents[a.Id].Belt![3]);

        logistics.Run(Start, Start + LogisticsRules.Tick * 1);
        Assert.Equal(Items.Wood, contents[b.Id].Belt![0]);

        // Each lane keeps to itself.
        logistics.Run(Start, Start + LogisticsRules.Tick * 10);
        Assert.Equal(Lanes([null, null, null, Items.Wood], [null, null, null, Items.Stone]), contents[b.Id].Belt!);
    }

    [Fact]
    public void A_belt_from_before_the_lanes_keeps_its_items_on_the_left_lane()
    {
        var a = Obj(Items.Belt, 0, 0);
        var contents = new Dictionary<Guid, Contents> { [a.Id] = new() { Belt = [Items.Wood, null, null, null] } };
        Build([a], contents);
        Assert.Equal(Lanes([Items.Wood, null, null, null]), contents[a.Id].Belt!);
    }

    [Fact]
    public void Belts_facing_each_other_pass_nothing_and_a_belt_fed_from_one_side_turns_the_corner()
    {
        var a = Obj(Items.Belt, 0, 0, East);
        var b = Obj(Items.Belt, 1, 0, West);
        var contents = new Dictionary<Guid, Contents> { [a.Id] = new() { Belt = Lanes([null, null, null, Items.Stone]) } };
        Build([a, b], contents).Logistics.Run(Start, Start + TimeSpan.FromSeconds(5));
        Assert.Equal(Items.Stone, contents[a.Id].Belt![3]);

        // Going south, then east: the left lane stays the left one round the corner.
        var down = Obj(Items.Belt, 5, 4, 0);
        var corner = Obj(Items.Belt, 5, 5, East);
        contents = new Dictionary<Guid, Contents> { [down.Id] = new() { Belt = Lanes([null, null, null, Items.Stone], [null, null, null, Items.Coal]) } };
        Build([down, corner], contents).Logistics.Run(Start, Start + LogisticsRules.Tick);
        Assert.Equal(Lanes([Items.Stone, null, null, null], [Items.Coal, null, null, null]), contents[corner.Id].Belt!);
    }

    [Fact]
    public void A_belt_joining_a_running_line_from_the_side_puts_everything_on_the_near_lane()
    {
        // A line going east, fed from behind, and a belt coming down into it from the north: its left side.
        var behind = Obj(Items.Belt, 4, 5, East);
        var side = Obj(Items.Belt, 5, 4, 0);
        var line = Obj(Items.Belt, 5, 5, East);
        var contents = new Dictionary<Guid, Contents> { [side.Id] = new() { Belt = Lanes([null, null, null, Items.Stone], [null, null, null, Items.Coal]) } };
        var (logistics, _) = Build([behind, side, line], contents);

        logistics.Run(Start, Start + LogisticsRules.Tick);
        Assert.Equal(Lanes([null, Items.Stone, null, null]), contents[line.Id].Belt!);
        logistics.Run(Start, Start + LogisticsRules.Tick * 3);
        Assert.Equal(Lanes([null, null, Items.Coal, Items.Stone]), contents[line.Id].Belt!);
    }

    [Fact]
    public void A_full_line_of_belts_moves_together_and_waits_at_its_end()
    {
        var belts = Enumerable.Range(0, 3).Select(x => Obj(Items.Belt, x, 0)).ToList();
        var contents = belts.ToDictionary(b => b.Id, _ => new Contents { Belt = Lanes([Items.Coal, Items.Coal, Items.Coal, null], [Items.IronOre, null, null, null]) });
        var (logistics, _) = Build(belts, contents);

        logistics.Run(Start, Start + LogisticsRules.Tick);
        Assert.All(belts, b => Assert.Equal(Lanes([null, Items.Coal, Items.Coal, Items.Coal], [null, Items.IronOre, null, null]), contents[b.Id].Belt!));

        // Two lanes carry twice as much.
        logistics.Run(Start, Start + TimeSpan.FromMinutes(1));
        Assert.Equal(12, belts.Sum(b => contents[b.Id].Belt!.Count(i => i is not null)));
        Assert.All(Lane(contents[belts[2].Id], 0), i => Assert.Equal(Items.Coal, i));
        Assert.All(Lane(contents[belts[2].Id], 1)[1..], i => Assert.Equal(Items.IronOre, i));
    }

    [Fact]
    public void Inserters_carry_from_chest_over_belts_to_another_chest_until_it_is_empty()
    {
        // chest → inserter → belt → belt → belt → inserter → chest, along a row.
        var from = Obj(Items.WoodenChest, 0, 0);
        var objects = new List<PlotObject> { from, Obj(Items.Inserter, 1, 0) };
        objects.AddRange(Enumerable.Range(2, 3).Select(x => Obj(Items.Belt, x, 0)));
        var to = Obj(Items.IronChest, 6, 0);
        objects.AddRange([Obj(Items.Inserter, 5, 0), to]);
        var contents = new Dictionary<Guid, Contents> { [from.Id] = new() { Items = [new() { Item = Items.Wood, Quantity = 20 }] } };
        var (logistics, _) = Build(objects, contents);

        var end = logistics.Run(Start, Start + TimeSpan.FromSeconds(5));
        Assert.Equal(Start + TimeSpan.FromSeconds(5), end);
        Assert.InRange(Stored(contents[from.Id], Items.Wood), 15, 18);
        Assert.InRange(Stored(contents[to.Id], Items.Wood), 0, 2);

        logistics.Run(end, end + TimeSpan.FromHours(5));
        Assert.Equal(0, Stored(contents[from.Id], Items.Wood));
        Assert.Equal(20, Stored(contents[to.Id], Items.Wood));
    }

    [Fact]
    public void An_inserter_with_a_condition_fills_a_chest_up_to_its_limit_and_keeps_a_reserve_in_the_one_behind()
    {
        // chest (20 wood) → inserter (fill up to 8) → chest, and below it chest (20 wood) → inserter (keep 5) → chest
        var from = Obj(Items.WoodenChest, 0, 0);
        var filling = Obj(Items.Inserter, 1, 0);
        var filled = Obj(Items.WoodenChest, 2, 0);
        var reserve = Obj(Items.WoodenChest, 0, 2);
        var keeping = Obj(Items.Inserter, 1, 2);
        var to = Obj(Items.WoodenChest, 2, 2);
        var contents = new Dictionary<Guid, Contents>
        {
            [from.Id] = new() { Items = [new() { Item = Items.Wood, Quantity = 20 }] },
            [reserve.Id] = new() { Items = [new() { Item = Items.Wood, Quantity = 20 }] },
            [filling.Id] = new() { Below = 8 },
            [keeping.Id] = new() { Keep = 5 },
        };
        var (logistics, _) = Build([from, filling, filled, reserve, keeping, to], contents);

        logistics.Run(Start, Start + TimeSpan.FromHours(1));
        Assert.Equal(8, Stored(contents[filled.Id], Items.Wood));
        Assert.Equal(12, Stored(contents[from.Id], Items.Wood));
        Assert.Equal(5, Stored(contents[reserve.Id], Items.Wood));
        Assert.Equal(15, Stored(contents[to.Id], Items.Wood));
    }

    [Fact]
    public void An_inserter_with_nothing_to_take_from_a_chest_takes_what_another_puts_in_on_the_same_tick()
    {
        // chest → inserter → chest → inserter → chest: the second one has nothing to do until the first drops.
        var from = Obj(Items.WoodenChest, 0, 0);
        var middle = Obj(Items.WoodenChest, 2, 0);
        var second = Obj(Items.Inserter, 3, 0);
        var to = Obj(Items.WoodenChest, 4, 0);
        var contents = new Dictionary<Guid, Contents> { [from.Id] = new() { Items = [new() { Item = Items.Wood, Quantity = 3 }] } };
        var (logistics, _) = Build([from, Obj(Items.Inserter, 1, 0), middle, second, to], contents);

        var t = Start;
        for (var i = 0; i < 200; i++)
        {
            t = logistics.Run(t, t + LogisticsRules.Tick);
            Assert.Equal(0, Stored(contents[middle.Id], Items.Wood));
        }

        Assert.Equal(3, Stored(contents[to.Id], Items.Wood));
        Assert.Null(contents[second.Id].Hand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_inserter_takes_what_a_machine_makes_on_the_first_tick_it_is_there(bool predictable)
    {
        var machine = Obj(Items.Furnace, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var chest = Obj(Items.WoodenChest, 2, 0);
        var contents = new Dictionary<Guid, Contents> { [chest.Id] = new() };
        var ready = Start + LogisticsRules.Tick * 5 + TimeSpan.FromMilliseconds(1);
        var port = new OneIngot(ready, predictable);
        var ports = new Dictionary<Guid, IMachinePort>
        {
            [machine.Id] = port,
            [chest.Id] = new StoragePort(Storages.Find(Items.WoodenChest)!, contents[chest.Id], () => { }),
        };
        var logistics = new PlotLogistics([machine, inserter, chest], contents, ports);

        var t = logistics.Run(Start, Start + LogisticsRules.Tick * 5);
        Assert.False(port.Taken);
        Assert.Null(contents[inserter.Id].Hand);

        logistics.Run(t, t + LogisticsRules.Tick);
        Assert.True(port.Taken);
        Assert.Equal(Items.IronIngot, contents[inserter.Id].Hand);
    }

    /// <summary>A machine that makes one ingot at <paramref name="ready"/>; one that is not predictable does not say when.</summary>
    private sealed class OneIngot(DateTimeOffset ready, bool predictable) : IMachinePort
    {
        public bool Taken { get; private set; }

        public bool Predictable => predictable;

        public string? Output(DateTimeOffset now, Func<string, bool> wanted) => !Taken && now >= ready && wanted(Items.IronIngot) ? Items.IronIngot : null;

        public void Take(string item, DateTimeOffset now) => Taken = true;

        public bool Accepts(string item, DateTimeOffset now) => false;

        public void Put(string item, DateTimeOffset now)
        {
        }

        public DateTimeOffset? NextChange(DateTimeOffset now) => predictable && !Taken ? ready : null;

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task An_inserter_of_a_player_without_bigger_grippers_takes_items_off_a_belt()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 5);
        await GiveAsync(client, Items.WoodenChest, 2);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.Belt, 1);
        await GiveAsync(client, Items.Wood, 5);

        // chest → inserter → belt → inserter → chest: the second one has to pick up from the belt.
        var source = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        await PlaceAsync(client, x, y, Items.Belt, tx + 2, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 3, ty);
        var target = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 4, ty);
        await client.SendAsync("storage.put", new { x, y, id = source, item = Items.Wood, quantity = 5 });
        await PowerAsync(client, x, y, tx, ty);

        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var logistics = await client.SendAsync("logistics.get", new { x, y });
        var storages = logistics.Payload().GetProperty("storages").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(5, StoredIn(storages[target], Items.Wood));
    }

    [Fact]
    public async Task The_short_form_of_the_logistics_lists_only_belts_that_carry_something_and_what_changes()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 4);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.Belt, 2);
        await GiveAsync(client, Items.Stone, 1);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        var inserter = await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var loaded = await PlaceAsync(client, x, y, Items.Belt, tx + 2, ty);
        // Turned back towards the first one, so the stone stays on that one.
        await PlaceAsync(client, x, y, Items.Belt, tx + 3, ty, West);
        await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Stone, quantity = 1 });
        await PowerAsync(client, x, y, tx, ty);

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var compact = (await client.SendAsync("logistics.get", new { x, y, compact = true })).Payload();
        Assert.True(compact.GetProperty("compact").GetBoolean());
        var belt = Assert.Single(compact.GetProperty("belts").EnumerateArray());
        Assert.Equal(loaded, belt.GetProperty("id").GetString());
        Assert.Equal(1, belt.GetProperty("items").EnumerateArray().Count(i => i.ValueKind == JsonValueKind.String));
        Assert.False(belt.TryGetProperty("speed", out _));
        var arm = Assert.Single(compact.GetProperty("inserters").EnumerateArray());
        Assert.Equal(inserter, arm.GetProperty("id").GetString());
        Assert.False(arm.TryGetProperty("hand", out _));
        Assert.Equal(JsonValueKind.Null, compact.GetProperty("names").ValueKind);
        Assert.False(compact.GetProperty("power").TryGetProperty("wires", out _));
        Assert.Equal(1, compact.GetProperty("storages").GetArrayLength());

        // The full form, as before: every belt.
        var full = (await client.SendAsync("logistics.get", new { x, y })).Payload();
        Assert.Equal(2, full.GetProperty("belts").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, (await client.SendAsync("logistics.get", new { x, y, compact = true, names = true })).Payload().GetProperty("names").ValueKind);
    }

    [Fact]
    public void An_inserter_beside_a_belt_drops_on_the_far_lane_first()
    {
        // A belt going east; the inserter stands north of it (its left) and drops south onto it.
        var chest = Obj(Items.WoodenChest, 2, -1);
        var belt = Obj(Items.Belt, 2, 1);
        var contents = new Dictionary<Guid, Contents> { [chest.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 1 }] } };
        Build([chest, Obj(Items.Inserter, 2, 0, 0), belt], contents).Logistics.Run(Start, Start + TimeSpan.FromSeconds(5));

        Assert.Equal(Lanes([null, null, null, null], [null, null, null, Items.Stone]), contents[belt.Id].Belt!);
    }

    private static (PlotObject Input, PlotObject Splitter, PlotObject Left, PlotObject Right) SplitterLine()
    {
        // In from the west; out to the north (the splitter's left) and the south (its right).
        return (Obj(Items.Belt, 0, 0, East), Obj(Items.Splitter, 1, 0, East), Obj(Items.Belt, 1, -1, 2), Obj(Items.Belt, 1, 1, 0));
    }

    private static int Count(Contents c, string item) => c.Belt!.Count(i => i == item);

    [Fact]
    public void A_splitter_sends_items_left_and_right_in_turn()
    {
        var (input, splitter, left, right) = SplitterLine();
        var contents = new Dictionary<Guid, Contents> { [input.Id] = new() { Belt = Lanes([Items.Wood, Items.Wood, Items.Wood, Items.Wood]) } };
        Build([input, splitter, left, right], contents).Logistics.Run(Start, Start + TimeSpan.FromSeconds(10));

        Assert.Equal(2, Count(contents[left.Id], Items.Wood));
        Assert.Equal(2, Count(contents[right.Id], Items.Wood));
    }

    [Fact]
    public void A_splitter_with_a_filter_sends_those_items_one_way_and_the_rest_the_other()
    {
        var (input, splitter, left, right) = SplitterLine();
        var contents = new Dictionary<Guid, Contents>
        {
            [input.Id] = new() { Belt = Lanes([Items.Wood, Items.Stone, Items.Wood, Items.Stone], [Items.Stone, Items.Wood, null, null]) },
            [splitter.Id] = new() { Filter = [Items.Stone], FilterSide = 1 },
        };
        Build([input, splitter, left, right], contents).Logistics.Run(Start, Start + TimeSpan.FromSeconds(10));

        Assert.Equal(3, Count(contents[left.Id], Items.Wood));
        Assert.Equal(3, Count(contents[right.Id], Items.Stone));
        Assert.Equal(0, Count(contents[left.Id], Items.Stone) + Count(contents[right.Id], Items.Wood));
    }

    [Fact]
    public void A_filtered_item_waits_when_its_way_out_is_missing()
    {
        var (input, splitter, left, _) = SplitterLine();
        var contents = new Dictionary<Guid, Contents>
        {
            [input.Id] = new() { Belt = Lanes([null, null, null, Items.Stone]) },
            [splitter.Id] = new() { Filter = [Items.Stone], FilterSide = 1 },
        };
        Build([input, splitter, left], contents).Logistics.Run(Start, Start + TimeSpan.FromSeconds(10));

        Assert.Equal(1, Count(contents[splitter.Id], Items.Stone));
        Assert.Equal(0, Count(contents[left.Id], Items.Stone));
    }

    [Fact]
    public void An_inserter_picks_only_what_the_storage_in_front_takes()
    {
        var chest = Obj(Items.WoodenChest, 0, 0);
        var pile = Obj(Items.WoodPile, 2, 0);
        var contents = new Dictionary<Guid, Contents>
        {
            [chest.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 5 }, new() { Item = Items.Wood, Quantity = 3 }] },
        };
        Build([chest, Obj(Items.Inserter, 1, 0), pile], contents).Logistics.Run(Start, Start + TimeSpan.FromMinutes(5));

        Assert.Equal(5, Stored(contents[chest.Id], Items.Stone));
        Assert.Equal(3, Stored(contents[pile.Id], Items.Wood));
    }

    [Fact]
    public void An_inserter_with_a_filter_picks_up_only_its_item()
    {
        var chest = Obj(Items.WoodenChest, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var target = Obj(Items.WoodenChest, 2, 0);
        var contents = new Dictionary<Guid, Contents>
        {
            [chest.Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 5 }, new() { Item = Items.Wood, Quantity = 3 }] },
            [inserter.Id] = new() { Only = Items.Wood },
        };
        Build([chest, inserter, target], contents).Logistics.Run(Start, Start + TimeSpan.FromMinutes(5));

        Assert.Equal(5, Stored(contents[chest.Id], Items.Stone));
        Assert.Equal(0, Stored(contents[target.Id], Items.Stone));
        Assert.Equal(3, Stored(contents[target.Id], Items.Wood));
    }

    [Fact]
    public void A_chest_with_a_filter_lets_in_only_its_item()
    {
        // Off a belt carrying stone and wood, into a chest that takes stone only.
        var belt = Obj(Items.Belt, 0, 0);
        var target = Obj(Items.WoodenChest, 2, 0);
        var contents = new Dictionary<Guid, Contents>
        {
            [belt.Id] = new() { Belt = Lanes([Items.Wood, null, Items.Stone, null], [Items.Stone, null, null, null]) },
            [target.Id] = new() { Only = Items.Stone },
        };
        Build([belt, Obj(Items.Inserter, 1, 0), target], contents).Logistics.Run(Start, Start + TimeSpan.FromMinutes(5));

        Assert.Equal(2, Stored(contents[target.Id], Items.Stone));
        Assert.Equal(0, Stored(contents[target.Id], Items.Wood));
        Assert.Contains(Items.Wood, contents[belt.Id].Belt!);
    }

    [Fact]
    public void An_inserter_takes_mixed_goods_out_of_a_chest_in_turn()
    {
        var chest = Obj(Items.WoodenChest, 0, 0);
        var belt = Obj(Items.Belt, 2, 0);
        var contents = new Dictionary<Guid, Contents>
        {
            [chest.Id] = new() { Items = [new() { Item = Items.IronOre, Quantity = 10 }, new() { Item = Items.Coal, Quantity = 10 }] },
        };
        Build([chest, Obj(Items.Inserter, 1, 0), belt], contents).Logistics.Run(Start, Start + TimeSpan.FromMinutes(5));

        // Eight items fill both lanes of the belt and a ninth waits in the inserter's hand:
        // ore, coal, ore, coal, ore, coal, ore, coal, ore.
        Assert.Equal(5, Stored(contents[chest.Id], Items.IronOre));
        Assert.Equal(6, Stored(contents[chest.Id], Items.Coal));
    }

    [Fact]
    public void Storages_take_their_goods_up_to_their_size()
    {
        var heap = Storages.Find(Items.Heap)!;
        Assert.Equal(5000, Storages.Room(heap, [], Items.IronOre));
        Assert.Equal(0, Storages.Room(heap, [], Items.Wood));
        Assert.Equal(0, Storages.Room(heap, [new() { Item = Items.Coal, Quantity = 1 }], Items.Stone));

        var chest = Storages.Find(Items.WoodenChest)!;
        List<ItemStack> stacks = [new() { Item = Items.Wood, Quantity = 650 }];
        Assert.Equal(150, Storages.Room(chest, stacks, Items.Wood));
        Assert.Equal(100, Storages.Room(chest, stacks, Items.Stone));
    }

    // --- In the game ---------------------------------------------------------------------------

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

    /// <summary>A plot of the player's with <paramref name="length"/> free tiles in a row from (tileX, tileY) east, and the two rows under it free too.</summary>
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
        for (var ty = 20; ty < PlotLayout.Size; ty++)
        {
            for (var tx = 0; tx + Math.Max(length, 4) <= PlotLayout.Size; tx++)
            {
                if (Enumerable.Range(tx, length).All(i => Enumerable.Range(ty, 3).All(row => bytes[row * PlotLayout.Size + i] == (byte)TileObject.None)))
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free row.");
    }

    /// <summary>Powers a row of up to five tiles from (tileX, tileY): a pole and a generator with coal two rows under it, past a 2 × 2 machine.</summary>
    private async Task PowerAsync(GameClient client, int x, int y, int tileX, int tileY, int coal = 20)
    {
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, coal);
        await PlaceAsync(client, x, y, Items.Pole, tileX + 2, tileY + 2);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tileX + 3, tileY + 2);
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
    public async Task Belts_inserters_and_storage_are_made_at_the_workbench()
    {
        using var client = await PlayerAsync();
        var crafting = await client.SendAsync("crafting.get");
        var recipes = crafting.Payload().GetProperty("recipes").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains(Items.Belt, recipes);
        Assert.Contains(Items.Inserter, recipes);
        Assert.Contains(Items.WoodenChest, recipes);
        Assert.Contains(Items.IronChest, recipes);
        Assert.Contains(Items.WoodPile, recipes);
        Assert.Contains(Items.Heap, recipes);
        Assert.Contains(Items.GrainSilo, recipes);
        Assert.Contains(Items.Splitter, recipes);
    }

    [Fact]
    public async Task A_splitter_on_a_plot_keeps_its_filter()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 1);
        await GiveAsync(client, Items.Splitter, 1);
        var splitter = await PlaceAsync(client, x, y, Items.Splitter, tx, ty);

        var set = await client.SendAsync("splitter.set", new { x, y, id = splitter, side = 1, items = new[] { Items.Stone, Items.Coal } });
        Assert.Equal("logistics", set.Type());
        var state = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("belts")[0];
        Assert.Equal([Items.Stone, Items.Coal], state.GetProperty("filter").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(1, state.GetProperty("filterSide").GetInt32());

        Assert.Equal("bad_filter", (await client.SendAsync("splitter.set", new { x, y, id = splitter, side = 0, items = new[] { "nothing" } })).ErrorCode());
        Assert.Equal("bad_side", (await client.SendAsync("splitter.set", new { x, y, id = splitter, side = 2, items = Array.Empty<string>() })).ErrorCode());
        Assert.Equal("logistics", (await client.SendAsync("splitter.set", new { x, y, id = splitter, side = 0, items = Array.Empty<string>() })).Type());
        state = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("belts")[0];
        Assert.Empty(state.GetProperty("filter").EnumerateArray());
    }

    [Fact]
    public async Task An_inserter_keeps_its_condition_and_only_an_inserter_takes_one()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 2);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.WoodenChest, 1);
        var inserter = await PlaceAsync(client, x, y, Items.Inserter, tx, ty);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 1, ty);

        var set = await client.SendAsync("inserter.condition", new { x, y, id = inserter, below = 100, keep = 10 });
        Assert.Equal("logistics", set.Type());
        var condition = (await client.SendAsync("logistics.get", new { x, y, compact = true })).Payload().GetProperty("conditions")[0];
        Assert.Equal(inserter, condition.GetProperty("id").GetString());
        Assert.Equal(100, condition.GetProperty("below").GetInt32());
        Assert.Equal(10, condition.GetProperty("keep").GetInt32());

        Assert.Equal("bad_condition", (await client.SendAsync("inserter.condition", new { x, y, id = inserter, below = -1, keep = 0 })).ErrorCode());
        Assert.Equal("no_object", (await client.SendAsync("inserter.condition", new { x, y, id = chest, below = 5, keep = 0 })).ErrorCode());
        using var other = await PlayerAsync();
        Assert.Equal("not_owner", (await other.SendAsync("inserter.condition", new { x, y, id = inserter, below = 5, keep = 0 })).ErrorCode());

        Assert.Equal("logistics", (await client.SendAsync("inserter.condition", new { x, y, id = inserter, below = 0, keep = 0 })).Type());
        Assert.Empty((await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("conditions").EnumerateArray());
    }

    [Fact]
    public async Task A_filter_on_a_chest_or_an_inserter_is_kept_and_shown_on_the_plot()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 3);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.WoodPile, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.Belt, 1);
        await GiveAsync(client, Items.Wood, 5);
        await GiveAsync(client, Items.Stone, 5);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        var inserter = await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var belt = await PlaceAsync(client, x, y, Items.Belt, tx + 2, ty);
        var pile = await PlaceAsync(client, x, y, Items.WoodPile, tx, ty + 1);
        await GiveAsync(client, Items.Furnace, 1);
        var furnace = await PlaceAsync(client, x, y, Items.Furnace, tx + 1, ty + 1);
        Assert.Equal("logistics", (await client.SendAsync("filter.set", new { x, y, id = furnace, item = Items.IronOre })).Type());

        var set = await client.SendAsync("filter.set", new { x, y, id = chest, item = Items.Wood });
        Assert.Equal("logistics", set.Type());
        Assert.True(set.Payload().GetProperty("names").TryGetProperty(Items.Wood, out _));
        Assert.Equal("logistics", (await client.SendAsync("filter.set", new { x, y, id = inserter, item = Items.Stone })).Type());
        var filters = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("filters").EnumerateArray()
            .ToDictionary(f => f.GetProperty("id").GetString()!, f => f.GetProperty("item").GetString());
        Assert.Equal(Items.Wood, filters[chest]);
        Assert.Equal(Items.Stone, filters[inserter]);
        Assert.Equal(Items.IronOre, filters[furnace]);

        // A chest with a filter takes nothing else, from the inventory either.
        Assert.Equal("not_stored_here", (await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Stone, quantity = 1 })).ErrorCode());
        var put = await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Wood, quantity = 5 });
        Assert.Equal(Items.Wood, put.Payload().GetProperty("filter").GetString());

        Assert.Equal("not_stored_here", (await client.SendAsync("filter.set", new { x, y, id = pile, item = Items.Stone })).ErrorCode());
        Assert.Equal("not_filterable", (await client.SendAsync("filter.set", new { x, y, id = belt, item = Items.Stone })).ErrorCode());
        Assert.Equal("bad_filter", (await client.SendAsync("filter.set", new { x, y, id = chest, item = "nothing" })).ErrorCode());

        Assert.Equal("logistics", (await client.SendAsync("filter.set", new { x, y, id = chest, item = (string?)null })).Type());
        filters = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("filters").EnumerateArray()
            .ToDictionary(f => f.GetProperty("id").GetString()!, f => f.GetProperty("item").GetString());
        Assert.False(filters.ContainsKey(chest));
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Stone, quantity = 1 })).Type());
    }

    [Fact]
    public async Task Objects_on_a_plot_keep_their_rotation_and_can_be_turned()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 1);
        await GiveAsync(client, Items.Belt, 1);

        var belt = await PlaceAsync(client, x, y, Items.Belt, tx, ty, 2);
        var objects = (await client.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("objects");
        Assert.Equal(2, objects[0].GetProperty("rotation").GetInt32());

        var turned = await client.SendAsync("plot.rotate", new { x, y, id = belt, rotation = 3 });
        Assert.Equal(3, turned.Payload().GetProperty("object").GetProperty("rotation").GetInt32());
        Assert.Equal("bad_rotation", (await client.SendAsync("plot.rotate", new { x, y, id = belt, rotation = 4 })).ErrorCode());
    }

    [Fact]
    public async Task A_chest_takes_items_in_and_gives_them_back_and_a_pile_takes_only_wood()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 2);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.WoodPile, 1);
        await GiveAsync(client, Items.Wood, 30);
        await GiveAsync(client, Items.Stone, 10);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        var pile = await PlaceAsync(client, x, y, Items.WoodPile, tx + 1, ty);

        var put = await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Stone, quantity = 10 });
        Assert.Equal("storage", put.Type());
        Assert.Equal(10, StoredIn(put.Payload(), Items.Stone));
        Assert.Equal("not_stored_here", (await client.SendAsync("storage.put", new { x, y, id = pile, item = Items.Stone, quantity = 1 })).ErrorCode());
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x, y, id = pile, item = Items.Wood, quantity = 30 })).Type());

        var taken = await client.SendAsync("storage.take", new { x, y, id = chest, item = Items.Stone, quantity = 4 });
        Assert.Equal(6, StoredIn(taken.Payload(), Items.Stone));
        var inventory = await client.InventoryAsync();
        Assert.Equal(4, inventory.GetValueOrDefault(Items.Stone));
        Assert.Equal(0, inventory.GetValueOrDefault(Items.Wood));

        // Picking a storage up hands back what is in it.
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = pile })).Type());
        inventory = await client.InventoryAsync();
        Assert.Equal(30, inventory.GetValueOrDefault(Items.Wood));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.WoodPile));
    }

    [Fact]
    public async Task Inserters_feed_a_furnace_from_a_chest_and_take_its_ingots_to_another_chest_while_the_player_is_away()
    {
        using var client = await PlayerAsync();
        using var other = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 6);
        await GiveAsync(client, Items.WoodenChest, 2);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 10);
        await GiveAsync(client, Items.Coal, 2);

        // chest → inserter → furnace (2 × 2) → inserter → chest
        var source = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var furnace = await PlaceAsync(client, x, y, Items.Furnace, tx + 2, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 4, ty);
        var target = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 5, ty);
        await client.SendAsync("storage.put", new { x, y, id = source, item = Items.IronOre, quantity = 10 });
        await client.SendAsync("storage.put", new { x, y, id = source, item = Items.Coal, quantity = 2 });
        await PowerAsync(client, x, y, tx, ty);

        factory.Time.Advance(TimeSpan.FromHours(3));
        var logistics = await client.SendAsync("logistics.get", new { x, y });
        Assert.Equal("logistics", logistics.Type());
        var storages = logistics.Payload().GetProperty("storages").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(10, StoredIn(storages[target], Items.IronIngot));
        Assert.Equal(0, StoredIn(storages[source], Items.IronOre));
        Assert.Equal(0, StoredIn(storages[source], Items.Coal));
        var left = await client.SendAsync("furnace.get", new { furnace });
        Assert.Equal(0, left.Payload().GetProperty("fuel").GetInt32());
        Assert.Equal(JsonValueKind.Null, left.Payload().GetProperty("output").ValueKind);

        Assert.Equal("not_owner", (await other.SendAsync("logistics.get", new { x, y })).ErrorCode());
        Assert.Equal("not_owner", (await other.SendAsync("storage.take", new { x, y, id = target, item = Items.IronIngot, quantity = 0 })).ErrorCode());

        var taken = await client.SendAsync("storage.take", new { x, y, id = target, item = Items.IronIngot, quantity = 0 });
        Assert.Equal(0, StoredIn(taken.Payload(), Items.IronIngot));
        Assert.Equal(10, (await client.InventoryAsync()).GetValueOrDefault(Items.IronIngot));
    }

    [Fact]
    public async Task Inserters_feed_a_foundry_making_steel_coal_for_the_recipe_and_for_the_fire()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 7);
        await GiveAsync(client, Items.WoodenChest, 2);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.Foundry, 1);
        await GiveAsync(client, Items.IronIngot, 8);
        await GiveAsync(client, Items.Coal, 10);

        // chest → inserter → foundry (3 × 3, placed by its middle) → inserter → chest
        var source = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var foundry = await PlaceAsync(client, x, y, Items.Foundry, tx + 3, ty + 1);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 5, ty);
        var target = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 6, ty);
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = foundry, recipe = Items.SteelIngot })).Type());
        await client.SendAsync("storage.put", new { x, y, id = source, item = Items.IronIngot, quantity = 8 });
        await client.SendAsync("storage.put", new { x, y, id = source, item = Items.Coal, quantity = 10 });

        // Poles under both inserters, the generator beside the first.
        await GiveAsync(client, Items.Pole, 2);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 20);
        await PlaceAsync(client, x, y, Items.Pole, tx + 1, ty + 1);
        await PlaceAsync(client, x, y, Items.Pole, tx + 5, ty + 1);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx, ty + 1);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 })).Type());

        factory.Time.Advance(TimeSpan.FromHours(1));
        var storages = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("storages")
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(4, StoredIn(storages[target], Items.SteelIngot));
        Assert.Equal(0, StoredIn(storages[source], Items.IronIngot));
        Assert.Equal(0, StoredIn(storages[source], Items.Coal));
        var left = (await client.SendAsync("machine.get", new { machine = foundry })).Payload();
        Assert.Equal(4, left.GetProperty("inputs")[1].GetProperty("quantity").GetInt32());
        Assert.Equal(Items.Coal, left.GetProperty("fuel").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Inserters_work_a_mill_too_feeding_it_wheat_and_taking_its_flour()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 6);
        await GiveAsync(client, Items.GrainSilo, 1);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.Mill, 1);
        await GiveAsync(client, Items.Wheat, 20);

        var silo = await PlaceAsync(client, x, y, Items.GrainSilo, tx, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var mill = await PlaceAsync(client, x, y, Items.Mill, tx + 2, ty);
        await PlaceAsync(client, x, y, Items.Inserter, tx + 4, ty);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx + 5, ty);
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = mill, recipe = Items.Flour })).Type());
        await client.SendAsync("storage.put", new { x, y, id = silo, item = Items.Wheat, quantity = 20 });
        await PowerAsync(client, x, y, tx, ty);

        factory.Time.Advance(TimeSpan.FromHours(1));
        var storages = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("storages")
            .EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(0, StoredIn(storages[silo], Items.Wheat));
        Assert.Equal(10, StoredIn(storages[chest], Items.Flour));

        // The production statistics count what the mill made while the inserters worked it, and that it now waits for wheat.
        var stats = (await client.SendAsync("stats.production", new { minutes = 1440 })).Payload();
        var items = stats.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.Equal(10, items[Items.Flour].GetProperty("produced").GetInt64());
        Assert.Equal(20, items[Items.Wheat].GetProperty("consumed").GetInt64());
        var stall = Assert.Single(stats.GetProperty("stalls").EnumerateArray());
        Assert.Equal("no_input", stall.GetProperty("reason").GetString());
        Assert.Equal(Items.Wheat, Assert.Single(stall.GetProperty("missing").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Picking_up_a_belt_or_inserter_gives_back_what_it_carries()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotWithFreeRowAsync(client, 3);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.Belt, 1);
        await GiveAsync(client, Items.Stone, 3);
        var chest = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
        var inserter = await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
        var belt = await PlaceAsync(client, x, y, Items.Belt, tx + 2, ty);
        await client.SendAsync("storage.put", new { x, y, id = chest, item = Items.Stone, quantity = 3 });
        await PowerAsync(client, x, y, tx, ty);

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var belts = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("belts");
        Assert.Equal(3, belts[0].GetProperty("items").EnumerateArray().Count(i => i.ValueKind == JsonValueKind.String));

        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = belt })).Type());
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = inserter })).Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(3, inventory.GetValueOrDefault(Items.Stone));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.Belt));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.Inserter));
    }
}
