using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class ResearchTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

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

    private async Task GiveCostAsync(GameClient client, string technology)
    {
        foreach (var (item, quantity) in TechTree.ById[technology].Cost)
        {
            await GiveAsync(client, item, quantity);
        }
    }

    /// <summary>Researches a technology from start to end, its cost given.</summary>
    private async Task ResearchAsync(GameClient client, string technology)
    {
        await GiveCostAsync(client, technology);
        Assert.Equal("research", (await client.SendAsync("research.start", new { technology })).Type());
        factory.Time.Advance(TechTree.ById[technology].Time);
        await client.SendAsync("research.get");
    }

    private static async Task<Dictionary<string, JsonElement>> TechnologiesAsync(GameClient client) =>
        (await client.SendAsync("research.get")).Payload().GetProperty("technologies").EnumerateArray()
            .ToDictionary(t => t.GetProperty("id").GetString()!);

    private static async Task<string> GarageObjectAsync(GameClient client, string kind)
    {
        var garage = await client.SendAsync("garage.get");
        return garage.Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == kind).GetProperty("id").GetString()!;
    }

    private static long Count(Dictionary<string, long> inventory, string item) => inventory.GetValueOrDefault(item);

    [Fact]
    public async Task A_new_player_can_make_the_basics_but_not_belts_or_machines()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);

        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal(JsonValueKind.Null, recipes[Items.Nails].GetProperty("locked").ValueKind);
        Assert.Equal(JsonValueKind.Null, recipes[Items.Furnace].GetProperty("locked").ValueKind);
        Assert.Equal("Logistyka", recipes[Items.Belt].GetProperty("locked").GetString());
        Assert.Equal("Młynarstwo", recipes[Items.Mill].GetProperty("locked").GetString());

        await GiveAsync(client, Items.IronIngot, 2);
        await GiveAsync(client, Items.Wood, 1);
        Assert.Equal("locked", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Belt })).ErrorCode());
        Assert.Equal(2, Count(await client.InventoryAsync(), Items.IronIngot));

        // The trader keeps them back as well: it doesn't offer them, but still buys them back.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 100_000, "test", CancellationToken.None);
        }

        Assert.Equal("locked", (await client.SendAsync("market.buy", new { item = Items.Inserter, quantity = 1 })).ErrorCode());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Nails, quantity = 1 })).Type());
        var prices = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(JsonValueKind.Null, prices[Items.Inserter].GetProperty("buyCents").ValueKind);
        Assert.Equal(NpcPrices.Buys[Items.Inserter], prices[Items.Inserter].GetProperty("sellCents").GetInt64());
        Assert.Equal(NpcPrices.Sells[Items.Nails], prices[Items.Nails].GetProperty("buyCents").GetInt64());
        Assert.False(prices[Items.Nails].TryGetProperty("locked", out _));

        var tree = await TechnologiesAsync(client);
        Assert.Equal("available", tree[TechTree.Logistics].GetProperty("status").GetString());
        Assert.Equal("locked", tree[TechTree.Automation].GetProperty("status").GetString());
        Assert.Equal(1, tree[TechTree.Automation].GetProperty("tier").GetInt32());
        Assert.Equal(2, TechTree.Tier(TechTree.ById[TechTree.Sorting]));
    }

    [Fact]
    public async Task Every_technology_belongs_to_one_branch_of_industry()
    {
        var listed = TechTree.Branches.SelectMany(b => b.Technologies).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(TechTree.Technologies.Select(t => t.Id).Order(), listed.Order());
        Assert.All(TechTree.Branches, b => Assert.True(Items.Names.ContainsKey(b.Icon), b.Icon));

        using var client = await PlayerAsync();
        var payload = (await client.SendAsync("research.get")).Payload();
        var branches = payload.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Equal(TechTree.Branches.Select(b => b.Id), branches);
        var tree = await TechnologiesAsync(client);
        Assert.Equal("forestry", tree[TechTree.Woodworking].GetProperty("branch").GetString());
        Assert.Equal("oil", tree[TechTree.Refining].GetProperty("branch").GetString());
        Assert.Equal("company", tree[TechTree.Megaprojects].GetProperty("branch").GetString());
    }

    [Fact]
    public async Task Other_players_offers_of_what_isnt_researched_yet_are_hidden_and_cannot_be_bought()
    {
        using var seller = await PlayerAsync();
        using var buyer = await PlayerAsync();
        await ResearchAsync(seller, TechTree.Logistics);
        await GiveAsync(seller, Items.Belt, 10);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(buyer.AccountId, 100_000, "test", CancellationToken.None);
        }

        Assert.Equal("market.done", (await seller.SendAsync("market.list", new { item = Items.Belt, quantity = 5, priceCents = 400 })).Type());
        var listing = (await seller.SendAsync("market.item", new { item = Items.Belt })).Payload().GetProperty("listings").EnumerateArray()
            .First(l => l.GetProperty("mine").GetBoolean()).GetProperty("id").GetInt64();

        // Not in the overview, so no category or search shows it; its board, buying and ordering say it's locked.
        static async Task<HashSet<string>> OverviewAsync(GameClient client) =>
            (await client.SendAsync("market.overview")).Payload().GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetString()!).ToHashSet();
        var overview = await OverviewAsync(buyer);
        Assert.DoesNotContain(Items.Belt, overview);
        Assert.DoesNotContain(Items.Mill, overview);
        Assert.Contains(Items.Wood, overview);
        Assert.Contains(Items.Belt, await OverviewAsync(seller));
        Assert.Equal("locked", (await buyer.SendAsync("market.item", new { item = Items.Belt })).ErrorCode());
        Assert.Equal("locked", (await buyer.SendAsync("market.purchase", new { id = listing, quantity = 1 })).ErrorCode());
        Assert.Equal("locked", (await buyer.SendAsync("market.order", new { item = Items.Belt, quantity = 1, priceCents = 400 })).ErrorCode());
        Assert.Equal(100_000, await buyer.BalanceAsync());
        Assert.Equal(0, Count(await buyer.InventoryAsync(), Items.Belt));

        // Once researched, it's there like anything else.
        await ResearchAsync(buyer, TechTree.Logistics);
        Assert.Contains(Items.Belt, await OverviewAsync(buyer));
        Assert.Equal("market.done", (await buyer.SendAsync("market.purchase", new { id = listing, quantity = 1 })).Type());
        Assert.Equal(1, Count(await buyer.InventoryAsync(), Items.Belt));
    }

    [Fact]
    public async Task Researching_logistics_uses_its_cost_takes_a_minute_and_unlocks_belts()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        await GiveAsync(client, Items.IronIngot, 12);
        await GiveAsync(client, Items.Wood, 21);

        var started = (await client.SendAsync("research.start", new { technology = TechTree.Logistics })).Payload();
        var logistics = started.GetProperty("technologies").EnumerateArray().First(t => t.GetProperty("id").GetString() == TechTree.Logistics);
        Assert.Equal("active", logistics.GetProperty("status").GetString());
        Assert.Equal(60_000, logistics.GetProperty("finishesAt").GetInt64() - logistics.GetProperty("startsAt").GetInt64());
        var inventory = await client.InventoryAsync();
        Assert.Equal(2, Count(inventory, Items.IronIngot));
        Assert.Equal(1, Count(inventory, Items.Wood));

        factory.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal("locked", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Belt })).ErrorCode());

        factory.Time.Advance(TimeSpan.FromSeconds(1));
        var done = (await client.SendAsync("research.get")).Payload();
        Assert.Equal(TechTree.Logistics, done.GetProperty("finished")[0].GetProperty("id").GetString());
        Assert.Equal(20, done.GetProperty("xp").GetProperty("xp").GetInt64());
        var tree = done.GetProperty("technologies").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!);
        Assert.Equal("done", tree[TechTree.Logistics].GetProperty("status").GetString());
        Assert.Equal("available", tree[TechTree.Automation].GetProperty("status").GetString());
        Assert.Equal("available", tree[TechTree.Storage].GetProperty("status").GetString());
        Assert.Equal("locked", tree[TechTree.Sorting].GetProperty("status").GetString());

        // The XP comes once.
        var again = (await client.SendAsync("research.get")).Payload();
        Assert.Equal(0, again.GetProperty("finished").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("xp").ValueKind);

        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.Belt })).Type());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 10_000, "test", CancellationToken.None);
        }

        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Belt, quantity = 1 })).Type());
        Assert.Equal("already_researched", (await client.SendAsync("research.start", new { technology = TechTree.Logistics })).ErrorCode());
    }

    [Fact]
    public async Task Research_needs_its_requirements_its_cost_and_goes_one_at_a_time()
    {
        using var client = await PlayerAsync();
        await GiveCostAsync(client, TechTree.Automation);
        Assert.Equal("research_locked", (await client.SendAsync("research.start", new { technology = TechTree.Automation })).ErrorCode());
        Assert.Equal("no_technology", (await client.SendAsync("research.start", new { technology = "time_travel" })).ErrorCode());

        await GiveAsync(client, Items.Wheat, 10);
        Assert.Equal("not_enough_items", (await client.SendAsync("research.start", new { technology = TechTree.Milling })).ErrorCode());
        Assert.Equal(10, Count(await client.InventoryAsync(), Items.Wheat));

        await GiveCostAsync(client, TechTree.Logistics);
        await GiveCostAsync(client, TechTree.Pickling);
        Assert.Equal("research", (await client.SendAsync("research.start", new { technology = TechTree.Logistics })).Type());
        Assert.Equal("research_busy", (await client.SendAsync("research.start", new { technology = TechTree.Pickling })).ErrorCode());
        Assert.Equal("research_busy", (await client.SendAsync("research.start", new { technology = TechTree.Logistics })).ErrorCode());

        factory.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("research", (await client.SendAsync("research.start", new { technology = TechTree.Pickling })).Type());
        Assert.Equal("research", (await client.SendAsync("research.cancel")).Type());
        Assert.Equal("research", (await client.SendAsync("research.start", new { technology = TechTree.Automation })).Type());
    }

    [Fact]
    public async Task Cancelled_research_gives_its_cost_back()
    {
        using var client = await PlayerAsync();
        Assert.Equal("no_research", (await client.SendAsync("research.cancel")).ErrorCode());
        await GiveCostAsync(client, TechTree.Milling);
        await client.SendAsync("research.start", new { technology = TechTree.Milling });
        factory.Time.Advance(TimeSpan.FromSeconds(30));

        var cancelled = await TechnologiesAsync(client);
        Assert.Equal("active", cancelled[TechTree.Milling].GetProperty("status").GetString());
        var reply = (await client.SendAsync("research.cancel")).Payload();
        Assert.Equal("available", reply.GetProperty("technologies").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == TechTree.Milling).GetProperty("status").GetString());
        var inventory = await client.InventoryAsync();
        Assert.Equal(40, Count(inventory, Items.Wheat));
        Assert.Equal(20, Count(inventory, Items.Stone));

        // Once done, it cannot be called off.
        await client.SendAsync("research.start", new { technology = TechTree.Milling });
        factory.Time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("no_research", (await client.SendAsync("research.cancel")).ErrorCode());
    }

    [Fact]
    public async Task Faster_belts_and_inserters_are_researched_in_the_logistics_branch()
    {
        using var client = await PlayerAsync();
        var bench = await GarageObjectAsync(client, Items.Workbench);
        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!);
        Assert.Equal("Szybka logistyka", recipes[Items.FastBelt].GetProperty("locked").GetString());
        Assert.Equal("Szybka logistyka", recipes[Items.FastInserter].GetProperty("locked").GetString());
        Assert.Equal("Ekspresowa logistyka", recipes[Items.ExpressBelt].GetProperty("locked").GetString());
        Assert.Equal("Ekspresowa logistyka", recipes[Items.ExpressInserter].GetProperty("locked").GetString());
        Assert.Equal("logistics", TechTree.BranchOf[TechTree.FastLogistics].Id);
        Assert.Equal("logistics", TechTree.BranchOf[TechTree.ExpressLogistics].Id);

        await ResearchAsync(client, TechTree.Logistics);
        await ResearchAsync(client, TechTree.Automation);
        await GiveAsync(client, Items.Belt, 2);
        await GiveAsync(client, Items.IronIngot, 3);
        await GiveAsync(client, Items.CopperIngot, 1);
        Assert.Equal("locked", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.FastBelt })).ErrorCode());

        await ResearchAsync(client, TechTree.FastLogistics);
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench = bench, recipe = Items.FastBelt })).Type());
        var tree = await TechnologiesAsync(client);
        Assert.Equal("locked", tree[TechTree.ExpressLogistics].GetProperty("status").GetString());

        await ResearchAsync(client, TechTree.Metallurgy);
        Assert.Equal("available", (await TechnologiesAsync(client))[TechTree.ExpressLogistics].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Carrot_cake_bakes_only_after_confectionery()
    {
        using var client = await PlayerAsync();
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
        var free = Enumerable.Range(PlotLayout.Size * 20, bytes.Length - PlotLayout.Size * 20).First(i => bytes[i] == (byte)TileObject.None);

        // A bakery from another player works for bread before its research, but not for cake.
        await GiveAsync(client, Items.Bakery, 1);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Bakery, tileX = free % PlotLayout.Size, tileY = free / PlotLayout.Size });
        var bakery = placed.Payload().GetProperty("object").GetProperty("id").GetString()!;
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = bakery, recipe = Items.Bread })).Type());
        var reply = await client.SendAsync("machine.recipe", new { machine = bakery, recipe = Items.CarrotCake });
        Assert.Equal("locked", reply.ErrorCode());
        var cake = (await client.SendAsync("machine.get", new { machine = bakery })).Payload().GetProperty("recipes").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == Items.CarrotCake);
        Assert.Equal("Cukiernictwo", cake.GetProperty("locked").GetString());

        await ResearchAsync(client, TechTree.Milling);
        await ResearchAsync(client, TechTree.OilPressing);
        await ResearchAsync(client, TechTree.Baking);
        await ResearchAsync(client, TechTree.Confectionery);
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = bakery, recipe = Items.CarrotCake })).Type());
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= 7000)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }
}
