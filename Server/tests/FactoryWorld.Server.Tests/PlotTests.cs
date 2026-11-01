using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class PlotTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    /// <summary>Plots of a biome nobody in this test class has used yet.</summary>
    private static readonly Dictionary<Biome, int> Used = [];

    private static (int X, int Y) FreePlot(Biome biome)
    {
        lock (Used)
        {
            var skip = Used.GetValueOrDefault(biome) + 1000;
            Used[biome] = Used.GetValueOrDefault(biome) + 1;
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
    }

    private async Task<GameClient> PlayerAsync(long cents = 1_000_000_00)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        return client;
    }

    private static async Task<(int X, int Y)> BuyAsync(GameClient client, Biome biome)
    {
        var (x, y) = FreePlot(biome);
        var bought = await client.SendAsync("world.buy", new { x, y });
        Assert.Equal("world.bought", bought.Type());
        return (x, y);
    }

    private static async Task<(JsonElement Plot, byte[] Tiles)> EnterAsync(GameClient client, (int X, int Y) plot)
    {
        var reply = await client.SendAsync("plot.enter", new { x = plot.X, y = plot.Y });
        Assert.Equal("plot", reply.Type());
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(reply.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return (reply.Payload(), tiles.ToArray());
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private static (int X, int Y) Find(byte[] tiles, TileObject obj)
    {
        var i = Array.IndexOf(tiles, (byte)obj);
        Assert.True(i >= 0, $"No {obj} on the plot.");
        return (i % PlotLayout.Size, i / PlotLayout.Size);
    }

    /// <summary>The tool in the first slot of the tool bar.</summary>
    private static string? Tool(JsonElement character) =>
        character.GetProperty("tools")[0] is { ValueKind: JsonValueKind.Object } tool ? tool.GetProperty("id").GetString() : null;

    [Fact]
    public async Task A_forest_comes_with_an_axe_in_hand_and_the_next_tool_goes_to_the_inventory()
    {
        using var client = await PlayerAsync();
        await BuyAsync(client, Biome.Forest);
        Assert.Equal(Items.Axe, Tool((await client.SendAsync("character.get")).Payload()));

        var (x, y) = FreePlot(Biome.Desert);
        var desert = await client.SendAsync("world.buy", new { x, y });
        Assert.Equal(Items.Shovel, desert.Payload().GetProperty("tool").GetProperty("id").GetString());
        Assert.Equal(Items.Axe, Tool((await client.SendAsync("character.get")).Payload()));
        Assert.Equal(1, (await client.InventoryAsync())[Items.Shovel]);

        var (cx, cy) = FreePlot(Biome.City);
        var city = await client.SendAsync("world.buy", new { x = cx, y = cy });
        Assert.Equal(JsonValueKind.Null, city.Payload().GetProperty("tool").ValueKind);
    }

    [Fact]
    public async Task Equipping_swaps_the_tool_in_hand_with_one_from_the_inventory()
    {
        using var client = await PlayerAsync();
        await BuyAsync(client, Biome.Mountains);
        await BuyAsync(client, Biome.Desert);

        var swapped = await client.SendAsync("character.equip", new { item = Items.Shovel });
        Assert.Equal(Items.Shovel, Tool(swapped.Payload()));
        var inventory = await client.InventoryAsync();
        Assert.Equal(1, inventory[Items.Pickaxe]);
        Assert.False(inventory.ContainsKey(Items.Shovel));

        Assert.Equal("not_a_tool", (await client.SendAsync("character.equip", new { item = Items.Trash })).ErrorCode());
        Assert.Equal("not_enough_items", (await client.SendAsync("character.equip", new { item = Items.Axe })).ErrorCode());

        var empty = await client.SendAsync("character.unequip");
        Assert.Null(Tool(empty.Payload()));
        Assert.Equal(1, (await client.InventoryAsync())[Items.Shovel]);
    }

    [Fact]
    public async Task A_plot_has_its_biome_nature_a_railway_and_a_station_and_only_its_owner_may_enter()
    {
        using var owner = await PlayerAsync();
        var forest = await BuyAsync(owner, Biome.Forest);
        var (plot, tiles) = await EnterAsync(owner, forest);

        Assert.Equal(PlotLayout.Size * PlotLayout.Size, tiles.Length);
        Assert.Equal(20, plot.GetProperty("tileMeters").GetInt32());
        Assert.True(tiles.Count(Trees.IsTree) > tiles.Length / 5, "A forest is full of trees.");
        Assert.All(Enumerable.Range(0, PlotLayout.Size), x => Assert.Equal((byte)TileObject.Rail, tiles[PlotLayout.RailRow * PlotLayout.Size + x]));
        Assert.Contains((byte)TileObject.Station, tiles);
        var spawn = plot.GetProperty("spawn");
        Assert.Equal((byte)TileObject.None, tiles[spawn.GetProperty("y").GetInt32() * PlotLayout.Size + spawn.GetProperty("x").GetInt32()]);

        var (_, again) = await EnterAsync(owner, forest);
        Assert.Equal(tiles, again);

        using var stranger = await PlayerAsync();
        Assert.Equal("not_owner", (await stranger.SendAsync("plot.enter", new { x = forest.X, y = forest.Y })).ErrorCode());
    }

    [Fact]
    public async Task Chopping_a_tree_needs_an_axe_gives_wood_and_removes_the_tree()
    {
        using var client = await PlayerAsync();
        var forest = await BuyAsync(client, Biome.Forest);
        var (_, tiles) = await EnterAsync(client, forest);
        var (tx, ty) = Find(tiles, TileObject.Tree);

        var chopped = await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty });
        Assert.Equal("plot.harvested", chopped.Type());
        Assert.Equal(1, (await client.InventoryAsync())[Items.Wood]);

        factory.Time.Advance(HarvestRules.ChopTime);
        Assert.Equal("nothing_here", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty })).ErrorCode());
        var (_, after) = await EnterAsync(client, forest);
        Assert.Equal((byte)TileObject.None, after[ty * PlotLayout.Size + tx]);

        // Too quick after the last swing.
        var (nx, ny) = Find(after, TileObject.Tree);
        factory.Time.Advance(HarvestRules.ChopTime);
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = nx, tileY = ny })).Type());
        var (mx, my) = Find((await EnterAsync(client, forest)).Tiles, TileObject.Tree);
        // Cutting a tree takes longer than a swing at a rock.
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("too_fast", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = mx, tileY = my })).ErrorCode());

        await client.SendAsync("character.unequip");
        factory.Time.Advance(HarvestRules.ChopTime);
        Assert.Equal("wrong_tool", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = mx, tileY = my })).ErrorCode());
    }

    [Fact]
    public async Task Trees_cut_all_over_a_plot_stay_gone_and_are_kept_a_square_of_tiles_to_a_row()
    {
        using var client = await PlayerAsync();
        var forest = await BuyAsync(client, Biome.Forest);
        var (_, tiles) = await EnterAsync(client, forest);
        // Trees on either side of a square's edge, and the last trees of the plot.
        var trees = Enumerable.Range(0, tiles.Length).Where(i => Trees.IsTree(tiles[i])).Select(i => (X: i % PlotLayout.Size, Y: i / PlotLayout.Size)).ToList();
        var picked = new[]
        {
            trees.First(t => t.X % PlotHarvests.Side == PlotHarvests.Side - 1),
            trees.First(t => t.X % PlotHarvests.Side == 0 && t.X > 0),
            trees[^1],
        };

        foreach (var (tx, ty) in picked)
        {
            factory.Time.Advance(HarvestRules.ChopTime);
            Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty })).Type());
        }

        var (_, after) = await EnterAsync(client, forest);
        var cut = Enumerable.Range(0, tiles.Length).Where(i => tiles[i] != after[i]).Select(i => (X: i % PlotLayout.Size, Y: i / PlotLayout.Size)).ToList();
        Assert.All(picked, t => Assert.Equal((byte)TileObject.None, after[t.Y * PlotLayout.Size + t.X]));
        Assert.All(cut, t => Assert.True(Trees.IsTree(tiles[t.Y * PlotLayout.Size + t.X])));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var command = await db.CommandAsync(
            $"SELECT (SELECT count(*) FROM world.plot_harvests WHERE x = @x AND y = @y), (SELECT count(*) FROM {PlotHarvests.Tiles} WHERE x = @x AND y = @y)",
            CancellationToken.None);
        command.Parameters.AddWithValue("x", forest.X);
        command.Parameters.AddWithValue("y", forest.Y);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(cut.Select(t => PlotHarvests.Square(t.X, t.Y)).Distinct().Count(), reader.GetInt64(0));
        Assert.Equal(cut.Count, reader.GetInt64(1));
    }

    [Fact]
    public async Task A_chainsaw_from_the_trader_for_500_dollars_cuts_trees_70_percent_faster()
    {
        using var client = await PlayerAsync();
        var forest = await BuyAsync(client, Biome.Forest);
        var balance = await client.BalanceAsync();
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Chainsaw, quantity = 1 })).Type());
        Assert.Equal(balance - 50_000, await client.BalanceAsync());
        Assert.Equal(Items.Chainsaw, Tool((await client.SendAsync("character.equip", new { item = Items.Chainsaw })).Payload()));

        var (plot, tiles) = await EnterAsync(client, forest);
        Assert.Equal(5, plot.GetProperty("chopSeconds").GetDouble());
        Assert.Equal(1.5, plot.GetProperty("sawSeconds").GetDouble(), 6);
        Assert.Equal(TimeSpan.FromSeconds(1.5), HarvestRules.TimeFor(TileObject.Tree, Items.Chainsaw));

        var (tx, ty) = Find(tiles, TileObject.Tree);
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty })).Type());
        var (nx, ny) = Find((await EnterAsync(client, forest)).Tiles, TileObject.Tree);
        factory.Time.Advance(HarvestRules.SawTime);
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = nx, tileY = ny })).Type());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Wood]);

        // With the axe back in hand a tree takes the full time again.
        await client.SendAsync("character.equip", new { item = Items.Axe });
        var (mx, my) = Find((await EnterAsync(client, forest)).Tiles, TileObject.Tree);
        factory.Time.Advance(HarvestRules.SawTime);
        Assert.Equal("too_fast", (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = mx, tileY = my })).ErrorCode());

        // A chainsaw is no good on rocks.
        await client.SendAsync("character.equip", new { item = Items.Chainsaw });
        var mountains = await BuyAsync(client, Biome.Mountains);
        var (rx, ry) = Find((await EnterAsync(client, mountains)).Tiles, TileObject.Rock);
        factory.Time.Advance(HarvestRules.ChopTime);
        Assert.Equal("wrong_tool", (await client.SendAsync("plot.harvest", new { x = mountains.X, y = mountains.Y, tileX = rx, tileY = ry })).ErrorCode());
    }

    [Fact]
    public async Task Saws_better_than_the_chainsaw_are_researched_and_fell_more_trees_with_more_wood()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), HarvestRules.TimeFor(TileObject.Tree, Items.SteelSaw));
        Assert.Equal(TimeSpan.FromSeconds(0.5), HarvestRules.TimeFor(TileObject.Tree, Items.MeteoriteSaw));
        Assert.Equal(Items.MeteoriteSaw, Items.BestSaw([Items.Chainsaw, Items.MeteoriteSaw, Items.SteelSaw]));
        Assert.Equal(Items.HarvesterSaw, Items.BestSaw([Items.SteelSaw, Items.HarvesterSaw]));
        Assert.Equal([(Items.OakWood, 3)], PlotLayout.Yield(TileObject.OakTree, 99, saw: Items.Saws[Items.SteelSaw]));
        // A harvester's saw makes saplings fall twice as often; a meteorite one gets all the wood out of a tree the beetle is in.
        Assert.Equal([(Items.Wood, 2), (Items.Sapling, 1)], PlotLayout.Yield(TileObject.Tree, 50, saw: Items.Saws[Items.HarvesterSaw]));
        Assert.Equal([(Items.Wood, 1)], Trees.Yield(Trees.ById[Trees.Pine], 99, infested: true, saw: Items.Saws[Items.HarvesterSaw]));
        Assert.Equal([(Items.Wood, 3)], Trees.Yield(Trees.ById[Trees.Pine], 99, infested: true, saw: Items.Saws[Items.MeteoriteSaw]));
        Assert.Equal(TechTree.Sawing, TechTree.ByItem[Items.SteelSaw].Id);
        Assert.Equal(TechTree.Harvesters, TechTree.ByItem[Items.HarvesterSaw].Id);
        Assert.Equal(TechTree.MeteoriteSaws, TechTree.ByItem[Items.MeteoriteSaw].Id);
        Assert.All([TechTree.Sawing, TechTree.Harvesters, TechTree.MeteoriteSaws], t => Assert.Equal("forestry", TechTree.BranchOf[t].Id));

        using var client = await PlayerAsync();
        var forest = await BuyAsync(client, Biome.Forest);
        // Nobody gets a saw before researching it, not from the trader and not from other players.
        Assert.Equal("locked", (await client.SendAsync("market.buy", new { item = Items.HarvesterSaw, quantity = 1 })).ErrorCode());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            foreach (var technology in new[] { TechTree.Sawing, TechTree.Harvesters })
            {
                await using var command = await db.CommandAsync(
                    "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
                command.Parameters.AddWithValue("owner", client.AccountId);
                command.Parameters.AddWithValue("id", technology);
                command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        var balance = await client.BalanceAsync();
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.HarvesterSaw, quantity = 1 })).Type());
        Assert.Equal(balance - NpcPrices.Sells[Items.HarvesterSaw], await client.BalanceAsync());
        Assert.Equal(Items.HarvesterSaw, Tool((await client.SendAsync("character.equip", new { item = Items.HarvesterSaw })).Payload()));

        // A tree with at least two trees at its sides falls together with two of them.
        var (plot, tiles) = await EnterAsync(client, forest);
        Assert.Equal(1, plot.GetProperty("saws").GetProperty(Items.HarvesterSaw).GetProperty("seconds").GetDouble(), 6);
        bool TreeAt(int x, int y) => PlotLayout.Inside(x, y) && Trees.IsTree(tiles[y * PlotLayout.Size + x]);
        var (tx, ty) = Enumerable.Range(0, PlotLayout.Size * PlotLayout.Size).Select(i => (X: i % PlotLayout.Size, Y: i / PlotLayout.Size))
            .First(t => tiles[t.Y * PlotLayout.Size + t.X] == (byte)TileObject.Tree
                && new[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1) }.Count(d => TreeAt(t.X + d.Item1, t.Y + d.Item2)) >= 2);
        factory.Time.Advance(HarvestRules.ChopTime);
        var cut = (await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty })).Payload();
        var felled = cut.GetProperty("felled").EnumerateArray().Select(f => (f.GetProperty("tileX").GetInt32(), f.GetProperty("tileY").GetInt32())).ToList();
        Assert.Equal(2, felled.Count);
        var after = (await EnterAsync(client, forest)).Tiles;
        Assert.All(felled.Append((tx, ty)), t => Assert.Equal((byte)TileObject.None, after[t.Item2 * PlotLayout.Size + t.Item1]));
        // Every tree gives a log more than with an axe.
        Assert.True((await client.InventoryAsync()).Where(i => i.Key == Items.Wood || i.Key.EndsWith("_wood")).Sum(i => i.Value) >= 6);
    }

    [Fact]
    public async Task Breaking_a_rock_with_a_pickaxe_gives_stone()
    {
        using var client = await PlayerAsync();
        var mountains = await BuyAsync(client, Biome.Mountains);
        var (tx, ty) = Find((await EnterAsync(client, mountains)).Tiles, TileObject.Rock);

        factory.Time.Advance(HarvestRules.Time);
        var broken = await client.SendAsync("plot.harvest", new { x = mountains.X, y = mountains.Y, tileX = tx, tileY = ty });

        Assert.Equal("plot.harvested", broken.Type());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Stone]);
    }

    [Fact]
    public async Task A_bush_is_pulled_out_by_hand()
    {
        using var client = await PlayerAsync();
        var meadow = await BuyAsync(client, Biome.Grassland);
        var (tx, ty) = Find((await EnterAsync(client, meadow)).Tiles, TileObject.Bush);

        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x = meadow.X, y = meadow.Y, tileX = tx, tileY = ty })).Type());
        Assert.Equal((byte)TileObject.None, (await EnterAsync(client, meadow)).Tiles[ty * PlotLayout.Size + tx]);
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("nothing_here", (await client.SendAsync("plot.harvest", new { x = meadow.X, y = meadow.Y, tileX = tx, tileY = ty })).ErrorCode());
    }

    [Fact]
    public async Task Goods_go_by_train_to_another_players_station_for_a_fee()
    {
        using var sender = await PlayerAsync();
        var from = await BuyAsync(sender, Biome.Forest);
        var (_, tiles) = await EnterAsync(sender, from);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(sender.AccountId, Items.Wood, 3, CancellationToken.None);
        }

        using var receiver = await PlayerAsync();
        var to = await BuyAsync(receiver, Biome.Grassland);

        var loaded = await sender.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Wood, quantity = 3 });
        Assert.Equal(3, loaded.Payload().GetProperty("items")[0].GetProperty("quantity").GetInt64());
        Assert.False((await sender.InventoryAsync()).ContainsKey(Items.Wood));

        var balance = await sender.BalanceAsync();
        var sent = await sender.SendAsync("station.send", new { x = from.X, y = from.Y, toX = to.X, toY = to.Y, item = Items.Wood, quantity = 2 });
        Assert.Equal("station", sent.Type());
        Assert.Equal(1, sent.Payload().GetProperty("items")[0].GetProperty("quantity").GetInt64());
        Assert.Single(sent.Payload().GetProperty("outgoing").EnumerateArray());
        var km = RailRules.DistanceKm(Map, from.X, from.Y, to.X, to.Y);
        Assert.Equal(balance - RailRules.FeeCents(km, 2), await sender.BalanceAsync());

        var waiting = await receiver.SendAsync("station.get", new { x = to.X, y = to.Y });
        Assert.Equal(0, waiting.Payload().GetProperty("items").GetArrayLength());
        Assert.Single(waiting.Payload().GetProperty("incoming").EnumerateArray());

        factory.Time.Advance(RailRules.TripTime(km));
        var arrived = await receiver.SendAsync("station.get", new { x = to.X, y = to.Y });
        Assert.Equal(2, arrived.Payload().GetProperty("items")[0].GetProperty("quantity").GetInt64());
        Assert.Equal(0, arrived.Payload().GetProperty("incoming").GetArrayLength());

        await receiver.SendAsync("station.unload", new { x = to.X, y = to.Y, item = Items.Wood, quantity = 2 });
        Assert.Equal(2, (await receiver.InventoryAsync())[Items.Wood]);

        Assert.Equal("not_enough_items",
            (await sender.SendAsync("station.send", new { x = from.X, y = from.Y, toX = to.X, toY = to.Y, item = Items.Wood, quantity = 5 })).ErrorCode());
        Assert.Equal("not_owner", (await receiver.SendAsync("station.get", new { x = from.X, y = from.Y })).ErrorCode());
        var ocean = Map.PlotAt(0, -150);
        Assert.Equal("no_station",
            (await sender.SendAsync("station.send", new { x = from.X, y = from.Y, toX = ocean.X, toY = ocean.Y, item = Items.Wood, quantity = 1 })).ErrorCode());
    }
    [Fact]
    public async Task Fertile_soil_comes_with_a_hoe_to_till_fields_where_crops_grow_for_their_own_time()
    {
        using var client = await PlayerAsync();
        var farm = await BuyAsync(client, Biome.Farmland);
        Assert.Equal(Items.Hoe, Tool((await client.SendAsync("character.get")).Payload()));
        var (plot, tiles) = await EnterAsync(client, farm);
        Assert.True(plot.GetProperty("fertile").GetBoolean());
        Assert.Equal(Farming.Crops.Count, plot.GetProperty("crops").GetArrayLength());
        var (tx, ty) = (plot.GetProperty("spawn").GetProperty("x").GetInt32(), plot.GetProperty("spawn").GetProperty("y").GetInt32());
        Assert.Equal((byte)TileObject.None, tiles[ty * PlotLayout.Size + tx]);
        object At(string? crop = null) => new { x = farm.X, y = farm.Y, tileX = tx, tileY = ty, crop };

        Assert.Equal("no_field", (await client.SendAsync("plot.plant", At(Items.Wheat))).ErrorCode());
        factory.Time.Advance(HarvestRules.Time);
        var tilled = await client.SendAsync("plot.till", At());
        Assert.Equal("plot.field", tilled.Type());
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("blocked", (await client.SendAsync("plot.till", At())).ErrorCode());

        Assert.Equal("unknown_crop", (await client.SendAsync("plot.plant", At("gold"))).ErrorCode());
        var sown = await client.SendAsync("plot.plant", At(Items.Wheat));
        Assert.Equal(Items.Wheat, sown.Payload().GetProperty("field").GetProperty("crop").GetString());
        Assert.Equal("no_field", (await client.SendAsync("plot.plant", At(Items.Corn))).ErrorCode());
        Assert.Equal("not_ripe", (await client.SendAsync("plot.reap", At())).ErrorCode());

        var (after, _) = await EnterAsync(client, farm);
        Assert.Equal(Items.Wheat, after.GetProperty("fields")[0].GetProperty("crop").GetString());

        factory.Time.Advance(Farming.ById[Items.Wheat].Grow);
        var reaped = await client.SendAsync("plot.reap", At());
        Assert.Equal(4, reaped.Payload().GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(4, (await client.InventoryAsync())[Items.Wheat]);
        Assert.Equal("nothing_here", (await client.SendAsync("plot.reap", At())).ErrorCode());
        // The field stays tilled, ready for the next crop.
        Assert.Equal("plot.field", (await client.SendAsync("plot.plant", At(Items.Corn))).Type());

        var forest = await BuyAsync(client, Biome.Forest);
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("not_fertile", (await client.SendAsync("plot.till", new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty })).ErrorCode());
    }

    [Fact]
    public async Task A_field_tilled_by_hand_can_be_turned_back_into_bare_ground_once_nothing_ripe_grows_on_it()
    {
        using var client = await PlayerAsync();
        var farm = await BuyAsync(client, Biome.Farmland);
        var (plot, _) = await EnterAsync(client, farm);
        var (tx, ty) = (plot.GetProperty("spawn").GetProperty("x").GetInt32(), plot.GetProperty("spawn").GetProperty("y").GetInt32());
        object At(string? crop = null) => new { x = farm.X, y = farm.Y, tileX = tx, tileY = ty, crop };

        Assert.Equal("nothing_here", (await client.SendAsync("plot.untill", At())).ErrorCode());
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("plot.field", (await client.SendAsync("plot.till", At())).Type());
        Assert.Equal("plot.field", (await client.SendAsync("plot.plant", At(Items.Wheat))).Type());
        factory.Time.Advance(Farming.ById[Items.Wheat].Grow);
        Assert.Equal("reap_first", (await client.SendAsync("plot.untill", At())).ErrorCode());
        Assert.Equal("plot.field", (await client.SendAsync("plot.reap", At())).Type());

        // A crop still growing goes with the field.
        Assert.Equal("plot.field", (await client.SendAsync("plot.plant", At(Items.Wheat))).Type());
        var untilled = await client.SendAsync("plot.untill", At());
        Assert.Equal("plot.untilled", untilled.Type());
        Assert.Equal(tx, untilled.Payload().GetProperty("tileX").GetInt32());
        var (after, _) = await EnterAsync(client, farm);
        Assert.Equal(0, after.GetProperty("fields").GetArrayLength());

        // The ground is free again: the hoe tills it once more.
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("plot.field", (await client.SendAsync("plot.till", At())).Type());

        using var stranger = await PlayerAsync();
        Assert.NotEqual("plot.untilled", (await stranger.SendAsync("plot.untill", At())).Type());
    }

    [Fact]
    public void A_cut_tree_gives_one_wood_and_three_times_in_ten_a_sapling()
    {
        Assert.Equal([(Items.Wood, 1), (Items.Sapling, 1)], PlotLayout.Yield(TileObject.Tree, 0));
        Assert.Equal([(Items.Wood, 1), (Items.Sapling, 1)], PlotLayout.Yield(TileObject.Tree, 29));
        Assert.Equal([(Items.Wood, 1)], PlotLayout.Yield(TileObject.Tree, 30));
        Assert.Equal(TimeSpan.FromSeconds(5), HarvestRules.TimeFor(TileObject.Tree));
    }

    [Fact]
    public async Task A_sapling_from_the_trader_grows_into_a_tree_faster_in_a_forest_and_is_cut_with_an_axe()
    {
        // Enough for a fourth plot, the mountains.
        using var client = await PlayerAsync(2_000_000_00);
        var prices = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.Equal(200, prices[Items.Sapling].GetProperty("buyCents").GetInt64());
        Assert.Equal(200, prices[Items.Wood].GetProperty("sellCents").GetInt64());

        var balance = await client.BalanceAsync();
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Sapling, quantity = 3 })).Type());
        Assert.Equal(balance - 600, await client.BalanceAsync());

        var forest = await BuyAsync(client, Biome.Forest);
        var (plot, tiles) = await EnterAsync(client, forest);
        Assert.Equal(450, plot.GetProperty("treeGrowSeconds").GetDouble());
        var (tx, ty) = (plot.GetProperty("spawn").GetProperty("x").GetInt32(), plot.GetProperty("spawn").GetProperty("y").GetInt32());
        object At(int tileX, int tileY) => new { x = forest.X, y = forest.Y, tileX, tileY };

        var planted = await client.SendAsync("plot.plantTree", At(tx, ty));
        Assert.Equal("plot.tree", planted.Type());
        var readyAt = planted.Payload().GetProperty("tree").GetProperty("readyAt").GetInt64();
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromMinutes(7.5)).ToUnixTimeMilliseconds(), readyAt);
        Assert.Equal(2, (await client.InventoryAsync())[Items.Sapling]);

        Assert.Equal("blocked", (await client.SendAsync("plot.plantTree", At(tx, ty))).ErrorCode());
        var (nx, ny) = Find(tiles, TileObject.Tree);
        Assert.Equal("blocked", (await client.SendAsync("plot.plantTree", At(nx, ny))).ErrorCode());
        Assert.Equal("blocked", (await client.SendAsync("plot.plantTree", At(0, PlotLayout.RailRow))).ErrorCode());

        var (again, _) = await EnterAsync(client, forest);
        var tree = Assert.Single(again.GetProperty("trees").EnumerateArray());
        Assert.Equal(readyAt, tree.GetProperty("readyAt").GetInt64());

        factory.Time.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal("not_grown", (await client.SendAsync("plot.harvest", At(tx, ty))).ErrorCode());
        factory.Time.Advance(TimeSpan.FromMinutes(0.5));
        var cut = await client.SendAsync("plot.harvest", At(tx, ty));
        Assert.Equal("plot.harvested", cut.Type());
        Assert.Equal(1, (await client.InventoryAsync())[Items.Wood]);
        Assert.Equal(0, (await EnterAsync(client, forest)).Plot.GetProperty("trees").GetArrayLength());
        factory.Time.Advance(HarvestRules.ChopTime);
        Assert.Equal("nothing_here", (await client.SendAsync("plot.harvest", At(tx, ty))).ErrorCode());

        // Elsewhere a sapling grows the full ten minutes, and no trees grow on ice or in the mountains.
        var meadow = await BuyAsync(client, Biome.Grassland);
        var (meadowPlot, _) = await EnterAsync(client, meadow);
        Assert.Equal(600, meadowPlot.GetProperty("treeGrowSeconds").GetDouble());
        var ice = await BuyAsync(client, Biome.Ice);
        Assert.Equal("no_forestry", (await client.SendAsync("plot.plantTree", new { x = ice.X, y = ice.Y, tileX = tx, tileY = ty })).ErrorCode());
        var mountains = await BuyAsync(client, Biome.Mountains);
        Assert.Equal("no_forestry", (await client.SendAsync("plot.plantTree", new { x = mountains.X, y = mountains.Y, tileX = tx, tileY = ty })).ErrorCode());

        // The cut tree may have dropped a sapling; plant them all, then there is none left.
        var saplings = (int)(await client.InventoryAsync())[Items.Sapling];
        Assert.InRange(saplings, 2, 3);
        for (var i = 0; i < saplings; i++)
        {
            Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", At(tx + i, ty))).Type());
        }

        Assert.Equal("not_enough_items", (await client.SendAsync("plot.plantTree", At(tx + saplings, ty))).ErrorCode());
    }

    [Fact]
    public async Task Animals_from_the_trader_are_fed_on_a_pasture_and_give_eggs_wool_and_milk_faster_on_the_steppe()
    {
        using var client = await PlayerAsync();
        var prices = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.Equal(5000, prices[Items.Chicken].GetProperty("buyCents").GetInt64());
        Assert.Equal(20_000, prices[Items.Sheep].GetProperty("buyCents").GetInt64());
        Assert.Equal(500, prices[Items.Wool].GetProperty("sellCents").GetInt64());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Sheep, quantity = 1 })).Type());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Chicken, quantity = 1 })).Type());
        await GiveAsync(client, Items.Wheat, 10);

        // The steppe comes with shears and raises animals a quarter faster.
        var steppe = await BuyAsync(client, Biome.Grassland);
        Assert.Equal(Items.Shears, Tool((await client.SendAsync("character.get")).Payload()));
        var (plot, _) = await EnterAsync(client, steppe);
        Assert.True(plot.GetProperty("pasture").GetBoolean());
        Assert.Equal(25, plot.GetProperty("husbandryBonus").GetInt32());
        var kinds = plot.GetProperty("animalKinds").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetString()!);
        Assert.Equal(360, kinds[Items.Sheep].GetProperty("seconds").GetDouble());
        Assert.Equal(Items.Shears, kinds[Items.Sheep].GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.Null, kinds[Items.Cow].GetProperty("tool").ValueKind);
        var (tx, ty) = (plot.GetProperty("spawn").GetProperty("x").GetInt32() + 1, plot.GetProperty("spawn").GetProperty("y").GetInt32());
        object At(string? item = null) => new { x = steppe.X, y = steppe.Y, tileX = tx, tileY = ty, item };

        Assert.Equal("unknown_animal", (await client.SendAsync("plot.release", At(Items.Wheat))).ErrorCode());
        var released = await client.SendAsync("plot.release", At(Items.Sheep));
        Assert.Equal("plot.animal", released.Type());
        Assert.Equal(Items.Sheep, released.Payload().GetProperty("animal").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, released.Payload().GetProperty("animal").GetProperty("readyAt").ValueKind);
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Sheep));
        Assert.Equal("blocked", (await client.SendAsync("plot.release", At(Items.Chicken))).ErrorCode());
        Assert.Equal("blocked", (await client.SendAsync("plot.plantTree", At())).ErrorCode());
        Assert.Equal("blocked", (await client.SendAsync("plot.place", new { x = steppe.X, y = steppe.Y, tileX = tx, tileY = ty, kind = Items.WoodenChest })).ErrorCode());

        Assert.Equal("hungry", (await client.SendAsync("plot.collect", At())).ErrorCode());
        Assert.Equal("bad_feed", (await client.SendAsync("plot.feed", At(Items.Stone))).ErrorCode());
        var fed = await client.SendAsync("plot.feed", At(Items.Wheat));
        Assert.Equal("plot.animal", fed.Type());
        var readyAt = fed.Payload().GetProperty("animal").GetProperty("readyAt").GetInt64();
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromMinutes(6)).ToUnixTimeMilliseconds(), readyAt);
        Assert.Equal(8, (await client.InventoryAsync())[Items.Wheat]);
        Assert.Equal("already_fed", (await client.SendAsync("plot.feed", At(Items.Wheat))).ErrorCode());
        Assert.Equal("not_ready", (await client.SendAsync("plot.collect", At())).ErrorCode());

        var (again, _) = await EnterAsync(client, steppe);
        var animal = Assert.Single(again.GetProperty("animals").EnumerateArray());
        Assert.Equal(readyAt, animal.GetProperty("readyAt").GetInt64());

        factory.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("already_fed", (await client.SendAsync("plot.feed", At(Items.Wheat))).ErrorCode());
        // Wool is cut with the shears; eggs and milk are taken by hand.
        Assert.Null(Tool((await client.SendAsync("character.unequip")).Payload()));
        Assert.Equal("wrong_tool", (await client.SendAsync("plot.collect", At())).ErrorCode());
        await client.SendAsync("character.equip", new { item = Items.Shears });
        var collected = await client.SendAsync("plot.collect", At());
        Assert.Equal("plot.animal", collected.Type());
        // Every animal also leaves manure for the fields.
        var gathered = collected.Payload().GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, gathered.Count);
        var wool = gathered[0];
        Assert.Equal(Items.Wool, wool.GetProperty("id").GetString());
        Assert.Equal(2, wool.GetProperty("quantity").GetInt32());
        Assert.Equal(Items.Manure, gathered[1].GetProperty("id").GetString());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Wool]);
        Assert.Equal(1, (await client.InventoryAsync())[Items.Manure]);
        Assert.Equal(JsonValueKind.Null, collected.Payload().GetProperty("animal").GetProperty("readyAt").ValueKind);
        Assert.Equal("hungry", (await client.SendAsync("plot.collect", At())).ErrorCode());

        var taken = await client.SendAsync("plot.takeAnimal", At());
        Assert.Equal("plot.animal", taken.Type());
        Assert.Equal(JsonValueKind.Null, taken.Payload().GetProperty("animal").ValueKind);
        Assert.Equal(1, (await client.InventoryAsync())[Items.Sheep]);
        Assert.Equal("no_animal", (await client.SendAsync("plot.feed", At(Items.Wheat))).ErrorCode());
        Assert.Equal(0, (await EnterAsync(client, steppe)).Plot.GetProperty("animals").GetArrayLength());

        // A hen lays in four minutes elsewhere on grass, and no animal lives on a glacier.
        var forest = await BuyAsync(client, Biome.Forest);
        var (forestPlot, _) = await EnterAsync(client, forest);
        Assert.Equal(0, forestPlot.GetProperty("husbandryBonus").GetInt32());
        object InForest(string? item = null) => new { x = forest.X, y = forest.Y, tileX = tx, tileY = ty, item };
        Assert.Equal("plot.animal", (await client.SendAsync("plot.release", InForest(Items.Chicken))).Type());
        var hen = await client.SendAsync("plot.feed", InForest(Items.Corn));
        Assert.Equal("not_enough_items", hen.ErrorCode());
        hen = await client.SendAsync("plot.feed", InForest(Items.Wheat));
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromMinutes(4)).ToUnixTimeMilliseconds(), hen.Payload().GetProperty("animal").GetProperty("readyAt").GetInt64());
        factory.Time.Advance(TimeSpan.FromMinutes(4));
        var eggs = await client.SendAsync("plot.collect", InForest());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Egg]);
        Assert.Equal(Items.Egg, eggs.Payload().GetProperty("items")[0].GetProperty("id").GetString());

        var ice = await BuyAsync(client, Biome.Ice);
        Assert.False((await EnterAsync(client, ice)).Plot.GetProperty("pasture").GetBoolean());
        Assert.Equal("no_pasture", (await client.SendAsync("plot.release", new { x = ice.X, y = ice.Y, tileX = tx, tileY = ty, item = Items.Sheep })).ErrorCode());
        Assert.Equal(1, (await client.InventoryAsync())[Items.Sheep]);
    }

    [Fact]
    public void No_trees_grow_wild_in_the_mountains()
    {
        for (var i = 0; i < 20; i++)
        {
            var (x, y) = (i * 37 % 6900, i * 13 % 2900);
            var tiles = PlotLayout.Generate(x, y, Biome.Mountains);
            Assert.DoesNotContain((byte)TileObject.Tree, tiles);
            Assert.Contains((byte)TileObject.Rock, tiles);
            Assert.Contains((byte)TileObject.Bush, tiles);
        }

        Assert.Contains((byte)TileObject.Tree, PlotLayout.Generate(10, 10, Biome.Forest));
    }

    [Fact]
    public void A_mining_plot_has_one_to_five_mines_always_in_the_same_places_and_other_plots_none()
    {
        var counts = new HashSet<int>();
        for (var i = 0; i < 200; i++)
        {
            var (x, y) = (i * 37 % 6900, i * 13 % 2900);
            var mines = PlotLayout.Mines(x, y, Biome.Mountains);
            Assert.InRange(mines.Count, 1, 5);
            Assert.Equal(mines, PlotLayout.Mines(x, y, Biome.Mountains));
            counts.Add(mines.Count);
            if (i < 20)
            {
                var tiles = PlotLayout.Generate(x, y, Biome.Mountains);
                Assert.Equal(mines.Count * MineSite.Width * MineSite.Height, tiles.Count(t => t == (byte)TileObject.Mine));
                // The ground in front of each entrance is clear.
                Assert.All(mines, m => Assert.Equal((byte)TileObject.None, tiles[(m.Y + MineSite.Height) * PlotLayout.Size + m.X + 1]));
            }
        }

        Assert.Equal([1, 2, 3, 4, 5], counts.Order());
        Assert.Empty(PlotLayout.Mines(10, 10, Biome.Forest));
        Assert.DoesNotContain((byte)TileObject.Mine, PlotLayout.Generate(10, 10, Biome.Tundra));
    }

    [Fact]
    public void Each_ore_drops_on_its_own_roll_and_a_load_keeps_the_two_rarest()
    {
        int[] Rolls(params string[] dropped) => Mining.Drops.Select(d => dropped.Contains(d.Item) ? 0 : 999).ToArray();

        Assert.Equal([(Items.Stone, 1)], Mining.Load(Rolls(Items.Stone)));
        Assert.Equal([(Items.Coal, 1), (Items.Stone, 1)], Mining.Load(Rolls(Items.Coal, Items.Stone)));
        Assert.Equal([(Items.GoldOre, 1), (Items.SilverOre, 1)], Mining.Load(Rolls(Items.Stone, Items.Coal, Items.SilverOre, Items.GoldOre)));
        Assert.Equal([(Items.TinOre, 1), (Items.IronOre, 1)], Mining.Load(Rolls(Items.Stone, Items.IronOre, Items.TinOre)));
        Assert.Empty(Mining.Load(Rolls()));

        // The chances, in thousandths: stone 99%, coal 35%, iron and copper 20%, tin 10%, silver 5%, gold 0.1%.
        var chances = Mining.Drops.ToDictionary(d => d.Item, d => d.PerMille);
        Assert.Equal(990, chances[Items.Stone]);
        Assert.Equal(350, chances[Items.Coal]);
        Assert.Equal(200, chances[Items.IronOre]);
        Assert.Equal(200, chances[Items.CopperOre]);
        Assert.Equal(100, chances[Items.TinOre]);
        Assert.Equal(50, chances[Items.SilverOre]);
        Assert.Equal(1, chances[Items.GoldOre]);
        Assert.Equal(1, Mining.Load(Mining.Drops.Select(d => d.PerMille - 1).ToArray()).Count(i => i.Item == Items.GoldOre));
        Assert.Empty(Mining.Load(Mining.Drops.Select(d => d.PerMille).ToArray()));
    }

    [Fact]
    public async Task A_mine_is_worked_with_a_pickaxe_for_fifty_loads_then_rests_ten_minutes()
    {
        using var client = await PlayerAsync();
        var mountains = await BuyAsync(client, Biome.Mountains);
        var (plot, tiles) = await EnterAsync(client, mountains);
        // Mines in the mountains work 30% faster.
        Assert.Equal(5 / Mining.Speed(Biome.Mountains), plot.GetProperty("mineSeconds").GetDouble(), 6);
        var mines = plot.GetProperty("mines").EnumerateArray().ToList();
        Assert.Equal(PlotLayout.Mines(mountains.X, mountains.Y, Biome.Mountains).Count, mines.Count);
        var first = mines[0];
        Assert.Equal(Mining.Units, first.GetProperty("units").GetInt32());
        Assert.Equal((byte)TileObject.Mine, tiles[first.GetProperty("y").GetInt32() * PlotLayout.Size + first.GetProperty("x").GetInt32()]);
        object At(int mine) => new { x = mountains.X, y = mountains.Y, mine };

        factory.Time.Advance(Mining.Time);
        var mined = await client.SendAsync("plot.mine", At(0));
        Assert.Equal("plot.mined", mined.Type());
        Assert.Equal(Mining.Units - 1, mined.Payload().GetProperty("mine").GetProperty("units").GetInt32());
        var items = mined.Payload().GetProperty("items").EnumerateArray().ToList();
        Assert.InRange(items.Count, 0, Mining.MaxOres);
        var inventory = await client.InventoryAsync();
        Assert.All(items, i => Assert.Equal(1, inventory[i.GetProperty("id").GetString()!]));

        // Mining takes 5 s like chopping a tree, longer than a swing at a rock.
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("too_fast", (await client.SendAsync("plot.mine", At(0))).ErrorCode());
        Assert.Equal("no_mine", (await client.SendAsync("plot.mine", At(mines.Count))).ErrorCode());
        await client.SendAsync("character.unequip");
        factory.Time.Advance(Mining.Time);
        Assert.Equal("wrong_tool", (await client.SendAsync("plot.mine", At(0))).ErrorCode());
        await client.SendAsync("character.equip", new { item = Items.Pickaxe });

        using (var stranger = await PlayerAsync())
        {
            Assert.Equal("not_owner", (await stranger.SendAsync("plot.mine", At(0))).ErrorCode());
        }

        for (var left = Mining.Units - 2; left >= 0; left--)
        {
            factory.Time.Advance(Mining.Time);
            var load = await client.SendAsync("plot.mine", At(0));
            Assert.Equal(left, load.Payload().GetProperty("mine").GetProperty("units").GetInt32());
        }

        var emptyAt = factory.Time.GetUtcNow();
        factory.Time.Advance(Mining.Time);
        Assert.Equal("mine_empty", (await client.SendAsync("plot.mine", At(0))).ErrorCode());
        var dry = (await EnterAsync(client, mountains)).Plot.GetProperty("mines")[0];
        Assert.Equal(0, dry.GetProperty("units").GetInt32());
        Assert.Equal((emptyAt + Mining.Refill).ToUnixTimeMilliseconds(), dry.GetProperty("refillAt").GetInt64());

        factory.Time.Advance(Mining.Refill - Mining.Time - TimeSpan.FromSeconds(1));
        Assert.Equal("mine_empty", (await client.SendAsync("plot.mine", At(0))).ErrorCode());
        factory.Time.Advance(TimeSpan.FromSeconds(1));
        var full = await client.SendAsync("plot.mine", At(0));
        Assert.Equal(Mining.Units - 1, full.Payload().GetProperty("mine").GetProperty("units").GetInt32());
        Assert.Equal(JsonValueKind.Null, full.Payload().GetProperty("mine").GetProperty("refillAt").ValueKind);
    }

    [Fact]
    public async Task Every_ore_has_a_trader_price_and_can_be_listed_for_other_players()
    {
        using var client = await PlayerAsync();
        var prices = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.All(Mining.Drops, d => Assert.True(prices[d.Item].GetProperty("sellCents").GetInt64() > 0));
        Assert.Equal(50_000, prices[Items.GoldOre].GetProperty("sellCents").GetInt64());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, Items.SilverOre, 2, CancellationToken.None);
        }

        Assert.Equal("market.done", (await client.SendAsync("market.list", new { item = Items.SilverOre, quantity = 1, priceCents = 9000 })).Type());
        var balance = await client.BalanceAsync();
        Assert.Equal("market.traded", (await client.SendAsync("market.sell", new { item = Items.SilverOre, quantity = 1 })).Type());
        Assert.Equal(balance + 6000, await client.BalanceAsync());
    }

    [Fact]
    public async Task Oil_and_gas_stay_hidden_until_the_owner_pays_for_a_survey()
    {
        using var owner = await PlayerAsync();
        var tundra = await BuyAsync(owner, Biome.Tundra);
        using var stranger = await PlayerAsync();

        var before = (await owner.SendAsync("world.plot", new { x = tundra.X, y = tundra.Y })).Payload();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("survey").ValueKind);
        Assert.Equal(Deposits.SurveyPriceCents, before.GetProperty("surveyPriceCents").GetInt64());
        Assert.Equal("not_owner", (await stranger.SendAsync("world.survey", new { x = tundra.X, y = tundra.Y })).ErrorCode());

        var balance = await owner.BalanceAsync();
        var surveyed = await owner.SendAsync("world.survey", new { x = tundra.X, y = tundra.Y });
        Assert.Equal("world.surveyed", surveyed.Type());
        var expected = Deposits.Under(tundra.X, tundra.Y, Biome.Tundra);
        Assert.Equal((int)expected.Oil, surveyed.Payload().GetProperty("survey").GetProperty("oil").GetInt32());
        Assert.Equal((int)expected.Gas, surveyed.Payload().GetProperty("survey").GetProperty("gas").GetInt32());
        Assert.Equal(balance - Deposits.SurveyPriceCents, await owner.BalanceAsync());
        Assert.Equal("already_surveyed", (await owner.SendAsync("world.survey", new { x = tundra.X, y = tundra.Y })).ErrorCode());

        var mine = (await owner.SendAsync("world.plot", new { x = tundra.X, y = tundra.Y })).Payload();
        Assert.Equal((int)expected.Oil, mine.GetProperty("survey").GetProperty("oil").GetInt32());
        var theirs = (await stranger.SendAsync("world.plot", new { x = tundra.X, y = tundra.Y })).Payload();
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("survey").ValueKind);
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("surveyPriceCents").ValueKind);
    }

    [Fact]
    public void Deposits_are_rare_and_more_common_in_the_tundra_than_in_the_mountains()
    {
        int Count(Biome biome) => Enumerable.Range(0, 2000).Count(i => Deposits.Under(i, i * 7 % 2900, biome).Oil != DepositSize.None);
        Assert.InRange(Count(Biome.Tundra), 450, 750);
        Assert.InRange(Count(Biome.Mountains), 40, 130);
    }
}
