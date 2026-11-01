using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Tutorial;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class TutorialTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private const int East = 1;

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

    private async Task GiveMoneyAsync(GameClient client, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots);
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

    private static async Task<byte[]> TilesAsync(GameClient client, (int X, int Y) plot)
    {
        var reply = await client.SendAsync("plot.enter", new { x = plot.X, y = plot.Y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(reply.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return tiles.ToArray();
    }

    /// <summary>The first of <paramref name="length"/> free tiles in a row, away from the station, with the row under them free too.</summary>
    private static (int X, int Y) FreeRow(byte[] tiles, int length)
    {
        for (var ty = 20; ty + 1 < PlotLayout.Size; ty++)
        {
            for (var tx = 0; tx + length <= PlotLayout.Size; tx++)
            {
                if (Enumerable.Range(tx, length).All(i => tiles[ty * PlotLayout.Size + i] == (byte)TileObject.None
                    && tiles[(ty + 1) * PlotLayout.Size + i] == (byte)TileObject.None))
                {
                    return (tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free row.");
    }

    private static async Task<JsonElement> TutorialAsync(GameClient client)
    {
        var reply = await client.SendAsync("tutorial.get");
        Assert.Equal("tutorial", reply.Type());
        return reply.Payload();
    }

    private static async Task<string> CurrentAsync(GameClient client)
    {
        var tutorial = await TutorialAsync(client);
        var current = tutorial.GetProperty("current").GetInt32();
        var steps = tutorial.GetProperty("steps");
        return current < steps.GetArrayLength() ? steps[current].GetProperty("id").GetString()! : "finished";
    }

    private static async Task<long> XpAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("xp").GetInt64();

    private async Task SortBagAsync(GameClient client)
    {
        await client.SendAsync("recycling.sort", new { count = 1 });
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (await client.SendAsync("garage.get")).Payload().GetProperty("results").GetArrayLength());
    }

    private static async Task<string> PlaceAsync(GameClient client, (int X, int Y) plot, string kind, int tileX, int tileY, int rotation = East)
    {
        var reply = await client.SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind, tileX, tileY, rotation });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_new_player_starts_at_sorting_trash_and_the_client_learns_what_to_watch()
    {
        using var client = await PlayerAsync();
        var tutorial = await TutorialAsync(client);

        Assert.Equal(0, tutorial.GetProperty("current").GetInt32());
        Assert.Equal(0, tutorial.GetProperty("count").GetInt64());
        Assert.False(tutorial.GetProperty("skipped").GetBoolean());
        Assert.Equal(TutorialRules.Steps.Count, tutorial.GetProperty("steps").GetArrayLength());
        var first = tutorial.GetProperty("steps")[0];
        Assert.Equal("sort_trash", first.GetProperty("id").GetString());
        Assert.Equal(5, first.GetProperty("goal").GetInt32());
        Assert.Equal(5000, first.GetProperty("rewardCents").GetInt64());
        Assert.Equal(["garage"], tutorial.GetProperty("watch").EnumerateArray().Select(w => w.GetString()));
    }

    [Fact]
    public async Task The_tasks_lead_from_sorting_trash_to_the_first_automatic_line_and_each_pays_its_reward()
    {
        using var client = await PlayerAsync();

        // Sorting: progress counts bag by bag, the reward comes with the fifth.
        for (var i = 0; i < 4; i++)
        {
            await SortBagAsync(client);
        }

        Assert.Equal(4, (await TutorialAsync(client)).GetProperty("count").GetInt64());
        Assert.Equal(0, await client.BalanceAsync());
        var xp = await XpAsync(client);
        await SortBagAsync(client);
        Assert.Equal("sell_waste", await CurrentAsync(client));
        Assert.Equal(50_00, await client.BalanceAsync());
        Assert.Equal(xp + 5 + 20, await XpAsync(client));

        // Selling to the trader.
        var waste = (await client.InventoryAsync()).Keys.First(k => k.StartsWith("waste_", StringComparison.Ordinal));
        var sold = await client.SendAsync("market.sell", new { item = waste, quantity = 1 });
        Assert.Equal("market.traded", sold.Type());
        Assert.Equal("buy_plot", await CurrentAsync(client));
        Assert.Equal(sold.Payload().GetProperty("balanceCents").GetInt64() + 100_00, await client.BalanceAsync());

        // A forest, which comes with an axe.
        await GiveMoneyAsync(client, 1_000_00);
        var forest = FreePlot(Biome.Forest);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = forest.X, y = forest.Y })).Type());
        Assert.Equal("chop_trees", await CurrentAsync(client));

        // Twenty trees.
        var tiles = await TilesAsync(client, forest);
        var trees = Enumerable.Range(0, tiles.Length).Where(i => tiles[i] == (byte)TileObject.Tree).Take(20).ToList();
        foreach (var tree in trees)
        {
            factory.Time.Advance(HarvestRules.ChopTime);
            var chopped = await client.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tree % PlotLayout.Size, tileY = tree / PlotLayout.Size });
            Assert.Equal("plot.harvested", chopped.Type());
        }

        Assert.Equal("plant_tree", await CurrentAsync(client));

        // A sapling on a free tile; the row next to it is kept for the line.
        tiles = await TilesAsync(client, forest);
        var (tx, ty) = FreeRow(tiles, 6);
        // Chopping may not drop one, but the reward for it always has a sapling.
        Assert.True((await client.InventoryAsync())[Items.Sapling] >= 1);
        Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", new { x = forest.X, y = forest.Y, tileX = tx + 5, tileY = ty })).Type());
        Assert.Equal("research_logistics", await CurrentAsync(client));

        // Research, paid for by the tutorial's iron and the wood just cut; its reward is the next research's cost.
        Assert.Equal(10, (await client.InventoryAsync())[Items.IronIngot]);
        Assert.Equal("research", (await client.SendAsync("research.start", new { technology = "logistics" })).Type());
        Assert.Equal("craft_wood_pile", await CurrentAsync(client));
        var inventory = await client.InventoryAsync();
        Assert.Equal(20, inventory[Items.IronIngot]);
        Assert.Equal(10, inventory[Items.CopperIngot]);
        Assert.Equal(20, inventory[Items.Nails]);
        factory.Time.Advance(TimeSpan.FromMinutes(1));

        // Ordering something else at the workbench does not count.
        var bench = (await client.SendAsync("garage.get")).Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == Items.Workbench).GetProperty("id").GetString();
        await GiveAsync(client, Items.Wood, 40);
        await GiveAsync(client, Items.Nails, 5);
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.WoodenChest })).Type());
        Assert.Equal("craft_wood_pile", await CurrentAsync(client));
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.WoodPile })).Type());
        Assert.Equal("place_wood_pile", await CurrentAsync(client));

        // The line: wood pile → inserter → three belts, going east.
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        await client.SendAsync("crafting.get");
        await PlaceAsync(client, forest, Items.WoodPile, tx, ty);
        Assert.Equal("fill_wood_pile", await CurrentAsync(client));

        var pile = (await client.SendAsync("logistics.get", new { x = forest.X, y = forest.Y })).Payload().GetProperty("storages")[0].GetProperty("id").GetString();
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x = forest.X, y = forest.Y, id = pile, item = Items.Wood, quantity = 10 })).Type());
        Assert.Equal("place_belts", await CurrentAsync(client));

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Belt, quantity = 3 })).Type());
        for (var i = 0; i < 3; i++)
        {
            await PlaceAsync(client, forest, Items.Belt, tx + 2 + i, ty);
        }

        Assert.Equal("research_automation", await CurrentAsync(client));
        Assert.Equal("research", (await client.SendAsync("research.start", new { technology = "automation" })).Type());
        Assert.Equal("place_inserter", await CurrentAsync(client));
        factory.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Inserter, quantity = 1 })).Type());
        await PlaceAsync(client, forest, Items.Inserter, tx + 1, ty);
        Assert.Equal("power_line", await CurrentAsync(client));

        // The generator and poles come with the reward; wood from chopping goes on its fire.
        await PlaceAsync(client, forest, Items.Pole, tx + 1, ty + 1);
        var generator = await PlaceAsync(client, forest, Items.Generator, tx + 2, ty + 1);
        var fuelled = await client.SendAsync("generator.load", new { x = forest.X, y = forest.Y, id = generator, item = Items.Wood, quantity = 1 });
        Assert.Equal("generator", fuelled.Type());
        Assert.Equal("first_line", await CurrentAsync(client));

        var before = await client.BalanceAsync();
        factory.Time.Advance(TimeSpan.FromSeconds(5));
        await client.SendAsync("logistics.get", new { x = forest.X, y = forest.Y });
        var done = await TutorialAsync(client);
        Assert.Equal(TutorialRules.Steps.Count, done.GetProperty("current").GetInt32());
        Assert.Equal(0, done.GetProperty("watch").GetArrayLength());
        Assert.Equal(before + 500_00, await client.BalanceAsync());
    }

    [Fact]
    public async Task A_skipped_tutorial_stands_still_and_a_plot_bought_meanwhile_counts_once_it_is_back()
    {
        using var client = await PlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var tutorial = scope.ServiceProvider.GetRequiredService<TutorialService>();
            await tutorial.AdvanceAsync(client.AccountId, "sort_trash", 5, CancellationToken.None);
            await tutorial.AdvanceAsync(client.AccountId, "sell_waste", 1, CancellationToken.None);
        }

        var skipped = await client.SendAsync("tutorial.skip");
        Assert.True(skipped.Payload().GetProperty("skipped").GetBoolean());

        await GiveMoneyAsync(client, 1_000_00);
        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        Assert.Equal("buy_plot", await CurrentAsync(client));

        var back = await client.SendAsync("tutorial.skip", new { skipped = false });
        Assert.False(back.Payload().GetProperty("skipped").GetBoolean());
        var balance = await client.BalanceAsync();
        Assert.Equal("world.info", (await client.SendAsync("world.info")).Type());
        Assert.Equal("chop_trees", await CurrentAsync(client));
        Assert.Equal(balance + 200_00, await client.BalanceAsync());
    }

    [Fact]
    public async Task Progress_for_another_task_than_the_one_at_hand_is_ignored()
    {
        using var client = await PlayerAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var tutorial = scope.ServiceProvider.GetRequiredService<TutorialService>();

        var state = await tutorial.AdvanceAsync(client.AccountId, "chop_trees", 10, CancellationToken.None);
        Assert.Equal(0, state.Index);
        Assert.Equal(0, state.Count);
        Assert.Equal(0, await client.BalanceAsync());
    }
}
