using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class FreightTests(ServerFactory factory) : ExportTestBase(factory), IClassFixture<ServerFactory>
{
    private static readonly string[] Loading = [Items.LoadingChest, Items.LoadingChest2, Items.LoadingChest3, Items.LoadingChest4];
    private static readonly string[] Receiving = [Items.ReceivingChest, Items.ReceivingChest2, Items.ReceivingChest3, Items.ReceivingChest4];

    private static async Task<JsonElement> FreightAsync(GameClient client, string type = "freight.get", object? payload = null)
    {
        var reply = await client.SendAsync(type, payload ?? new { });
        Assert.True(reply.Type() == "freight", reply.ToString());
        return reply.Payload();
    }

    private static JsonElement Route(JsonElement freight) => Assert.Single(freight.GetProperty("routes").EnumerateArray());

    private static async Task<Dictionary<string, long>> StorageAsync(GameClient client, (int X, int Y) plot, string id) =>
        (await client.SendAsync("storage.get", new { x = plot.X, y = plot.Y, id })).Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());

    private async Task MoveTrainsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FreightService>().RunDueAsync(100, CancellationToken.None);
    }

    [Fact]
    public void Loading_and_receiving_chests_come_in_four_sizes_researched_one_after_another_in_the_logistics_branch()
    {
        string[] techs = [TechTree.RailFreight, TechTree.RailFreight2, TechTree.RailFreight3, TechTree.RailFreight4];
        var last = 0;
        for (var i = 0; i < 4; i++)
        {
            foreach (var chest in new[] { Loading[i], Receiving[i] })
            {
                Assert.Contains(chest, PlotObjects.Placeable);
                Assert.True(NpcPrices.Buys.ContainsKey(chest), chest);
                Assert.Equal(techs[i], TechTree.ByItem[chest].Id);
                Assert.Equal("logistics", TechTree.BranchOf[techs[i]].Id);
            }

            Assert.True(Items.IsLoadingChest(Loading[i]) && !Items.IsReceivingChest(Loading[i]));
            Assert.True(Items.IsReceivingChest(Receiving[i]) && !Items.IsLoadingChest(Receiving[i]));
            var capacity = Storages.Find(Loading[i])!.Capacity;
            Assert.Equal(capacity, Storages.Find(Receiving[i])!.Capacity);
            Assert.True(capacity > last);
            last = capacity;
            if (i > 0)
            {
                Assert.Contains(techs[i - 1], TechTree.ById[techs[i]].Requires);
            }
        }
    }

    [Fact]
    public async Task A_route_train_waits_for_its_whole_load_in_the_loading_chests_then_delivers_it_into_the_receiving_chests_by_their_filters()
    {
        using var client = await PlayerAsync();
        var krakow = TownNamed("Kraków");
        var (from, to) = (Near(krakow.X, krakow.Y, Biome.Farmland), Near(krakow.X, krakow.Y, Biome.Farmland, skip: 30));
        await BuyAsync(client, from);
        await BuyAsync(client, to);
        Assert.Equal("world.plot", (await client.SendAsync("world.rename", new { x = to.X, y = to.Y, name = "  Magazyn  " })).Type());

        // Two loading chests on one plot, a receiving chest set to stone and one that takes anything on the other.
        await GiveAsync(client, Items.LoadingChest, 2);
        await GiveAsync(client, Items.ReceivingChest, 2);
        var a = FreeTile(from, 30, 3);
        var loadA = await PlaceAsync(client, from, Items.LoadingChest, (a.X - 2, a.Y));
        var loadB = await PlaceAsync(client, from, Items.LoadingChest, (a.X + 2, a.Y));
        var b = FreeTile(to, 30, 3);
        var stoneChest = await PlaceAsync(client, to, Items.ReceivingChest, (b.X - 2, b.Y));
        var anyChest = await PlaceAsync(client, to, Items.ReceivingChest, (b.X + 2, b.Y));
        Assert.Equal("logistics", (await client.SendAsync("filter.set", new { x = to.X, y = to.Y, id = stoneChest, item = Items.Stone })).Type());

        await GiveAsync(client, Items.Wood, 500);
        await GiveAsync(client, Items.Stone, 50);
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x = from.X, y = from.Y, id = loadA, item = Items.Wood, quantity = 300 })).Type());
        Assert.Equal("storage", (await client.SendAsync("storage.put", new { x = from.X, y = from.Y, id = loadB, item = Items.Stone, quantity = 50 })).Type());

        var freight = await FreightAsync(client);
        var plots = freight.GetProperty("plots").EnumerateArray().ToList();
        Assert.Equal(2, plots.Single(p => p.GetProperty("x").GetInt32() == from.X && p.GetProperty("y").GetInt32() == from.Y).GetProperty("loading").GetInt32());
        var named = plots.Single(p => p.GetProperty("x").GetInt32() == to.X && p.GetProperty("y").GetInt32() == to.Y);
        Assert.Equal("Magazyn", named.GetProperty("name").GetString());
        Assert.Equal(2, named.GetProperty("receiving").GetInt32());

        var balance = await client.BalanceAsync();
        freight = await FreightAsync(client, "freight.add", new
        {
            fromX = from.X, fromY = from.Y, toX = to.X, toY = to.Y,
            cargo = new[] { new { item = Items.Wood, quantity = 500L }, new { item = Items.Stone, quantity = 50L } },
        });
        // 300 of 500 wood: the train waits and nothing is paid.
        var route = Route(freight);
        Assert.Equal(FreightRules.Loading, route.GetProperty("state").GetString());
        Assert.Equal("waiting", route.GetProperty("problem").GetString());
        Assert.Equal(300, route.GetProperty("cargo")[0].GetProperty("stock").GetInt64());
        Assert.Equal("Magazyn", route.GetProperty("to").GetProperty("name").GetString());
        var fee = route.GetProperty("feeCents").GetInt64();
        Assert.True(fee > RailRules.BaseFeeCents);
        Assert.Equal(balance, await client.BalanceAsync());

        // With the rest of the wood in, the train loads at its next look and pays the fee.
        await client.SendAsync("storage.put", new { x = from.X, y = from.Y, id = loadB, item = Items.Wood, quantity = 200 });
        Factory.Time.Advance(FreightRules.CheckEvery);
        route = Route(await FreightAsync(client));
        Assert.Equal(FreightRules.Travelling, route.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, route.GetProperty("problem").ValueKind);
        Assert.Equal(fee, route.GetProperty("lastFeeCents").GetInt64());
        Assert.Equal(balance - fee, await client.BalanceAsync());
        Assert.Empty(await StorageAsync(client, from, loadA));
        Assert.Empty(await StorageAsync(client, from, loadB));
        Assert.Equal(550, route.GetProperty("carried").EnumerateArray().Sum(c => c.GetProperty("quantity").GetInt64()));

        // It gets there after the trip and unloads: stone into the chest set to it, wood into the other.
        Factory.Time.Advance(TimeSpan.FromSeconds(route.GetProperty("tripSeconds").GetInt64() + 1));
        await MoveTrainsAsync();
        route = Route(await FreightAsync(client));
        Assert.Equal(FreightRules.Returning, route.GetProperty("state").GetString());
        Assert.Equal(1, route.GetProperty("trips").GetInt64());
        Assert.Equal(550, route.GetProperty("delivered").GetInt64());
        Assert.Equal(new Dictionary<string, long> { [Items.Stone] = 50 }, await StorageAsync(client, to, stoneChest));
        Assert.Equal(new Dictionary<string, long> { [Items.Wood] = 500 }, await StorageAsync(client, to, anyChest));

        // Back again, it waits for the next load.
        Factory.Time.Advance(TimeSpan.FromSeconds(route.GetProperty("tripSeconds").GetInt64() + 1));
        route = Route(await FreightAsync(client));
        Assert.Equal(FreightRules.Loading, route.GetProperty("state").GetString());
        Assert.Equal("waiting", route.GetProperty("problem").GetString());

        // Removed while empty, the route is gone at once.
        Assert.Empty((await FreightAsync(client, "freight.remove", new { id = route.GetProperty("id").GetInt64() })).GetProperty("routes").EnumerateArray());
    }

    [Fact]
    public async Task A_train_waits_with_its_goods_until_the_receiving_chests_have_room_and_a_removed_route_ends_once_they_are_delivered()
    {
        using var client = await PlayerAsync();
        var lille = TownNamed("Lille");
        var (from, to) = (Near(lille.X, lille.Y, Biome.Farmland), Near(lille.X, lille.Y, Biome.Farmland, skip: 30));
        await BuyAsync(client, from);
        await BuyAsync(client, to);
        await GiveAsync(client, Items.LoadingChest2, 1);
        await GiveAsync(client, Items.ReceivingChest, 1);
        var load = await PlaceAsync(client, from, Items.LoadingChest2, FreeTile(from, 30));
        var receive = await PlaceAsync(client, to, Items.ReceivingChest, FreeTile(to, 30));
        var room = Storages.Find(Items.ReceivingChest)!.Capacity;
        await GiveAsync(client, Items.Stone, room + 200);
        await client.SendAsync("storage.put", new { x = from.X, y = from.Y, id = load, item = Items.Stone, quantity = room + 200 });

        var route = Route(await FreightAsync(client, "freight.add", new
        {
            fromX = from.X, fromY = from.Y, toX = to.X, toY = to.Y, cargo = new[] { new { item = Items.Stone, quantity = (long)room + 200 } },
        }));
        Assert.Equal(FreightRules.Travelling, route.GetProperty("state").GetString());
        var id = route.GetProperty("id").GetInt64();
        // Removing it now lets the goods get there first.
        route = Route(await FreightAsync(client, "freight.remove", new { id }));
        Assert.True(route.GetProperty("stopping").GetBoolean());

        Factory.Time.Advance(TimeSpan.FromSeconds(route.GetProperty("tripSeconds").GetInt64() + 1));
        route = Route(await FreightAsync(client));
        Assert.Equal(FreightRules.Unloading, route.GetProperty("state").GetString());
        Assert.Equal("no_room", route.GetProperty("problem").GetString());
        Assert.Equal(200, Assert.Single(route.GetProperty("carried").EnumerateArray()).GetProperty("quantity").GetInt64());
        Assert.Equal(room, (await StorageAsync(client, to, receive))[Items.Stone]);

        // Room is made, the rest goes in and the route ends.
        await client.SendAsync("storage.take", new { x = to.X, y = to.Y, id = receive, item = Items.Stone, quantity = 300 });
        Factory.Time.Advance(FreightRules.CheckEvery);
        Assert.Empty((await FreightAsync(client)).GetProperty("routes").EnumerateArray());
        Assert.Equal(room - 100, (await StorageAsync(client, to, receive))[Items.Stone]);
    }

    [Fact]
    public async Task Routes_run_between_the_players_own_plots_with_real_goods_and_only_the_owner_names_a_plot()
    {
        using var client = await PlayerAsync();
        using var other = await PlayerAsync();
        var warsaw = TownNamed("Warszawa");
        var (mine, theirs) = (Near(warsaw.X, warsaw.Y, Biome.Farmland), Near(warsaw.X, warsaw.Y, Biome.Farmland, skip: 30));
        await BuyAsync(client, mine);
        await BuyAsync(other, theirs);
        object Add(int toX, int toY, string item, long quantity) =>
            new { fromX = mine.X, fromY = mine.Y, toX, toY, cargo = new[] { new { item, quantity } } };

        Assert.Equal("not_owner", (await client.SendAsync("freight.add", Add(theirs.X, theirs.Y, Items.Wood, 10))).ErrorCode());
        Assert.Equal("bad_destination", (await client.SendAsync("freight.add", Add(mine.X, mine.Y, Items.Wood, 10))).ErrorCode());
        Assert.Equal("bad_items", (await client.SendAsync("freight.add", Add(theirs.X, theirs.Y, Items.CrudeOil, 10))).ErrorCode());
        Assert.Equal("bad_quantity", (await client.SendAsync("freight.add", Add(theirs.X, theirs.Y, Items.Wood, FreightRules.MaxQuantity + 1))).ErrorCode());
        Assert.Equal("no_route", (await client.SendAsync("freight.remove", new { id = 999_999_999L })).ErrorCode());

        Assert.Equal("not_owner", (await other.SendAsync("world.rename", new { x = mine.X, y = mine.Y, name = "Moja" })).ErrorCode());
        Assert.Equal("bad_name", (await client.SendAsync("world.rename", new { x = mine.X, y = mine.Y, name = new string('a', PlotNames.MaxLength + 1) })).ErrorCode());
        var renamed = (await client.SendAsync("world.rename", new { x = mine.X, y = mine.Y, name = "Tartak" })).Payload();
        Assert.Equal("Tartak", renamed.GetProperty("name").GetString());
        var info = (await client.SendAsync("world.info")).Payload();
        Assert.Equal("Tartak", Assert.Single(info.GetProperty("plots").EnumerateArray()).GetProperty("name").GetString());
        // Others see the name too; an empty one takes it off.
        Assert.Equal("Tartak", (await other.SendAsync("world.plot", new { x = mine.X, y = mine.Y })).Payload().GetProperty("name").GetString());
        renamed = (await client.SendAsync("world.rename", new { x = mine.X, y = mine.Y, name = " " })).Payload();
        Assert.Equal(JsonValueKind.Null, renamed.GetProperty("name").ValueKind);
    }
}
