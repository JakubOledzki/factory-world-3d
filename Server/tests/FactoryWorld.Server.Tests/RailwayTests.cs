using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class RailwayTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private const int North = 2;
    private const int South = 0;

    private static int _plots;

    private static (int X, int Y) FreePlot()
    {
        var skip = Interlocked.Increment(ref _plots) + 6000;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == Biome.Grassland && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private async Task<GameClient> PlayerAsync(long cents = 1_000_000_00)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        if (cents > 0)
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private static async Task<(int X, int Y)> BuyAsync(GameClient client)
    {
        var (x, y) = FreePlot();
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        return (x, y);
    }

    private static long Stock(JsonElement station, string item) =>
        station.Payload().GetProperty("items").EnumerateArray().Where(i => i.GetProperty("id").GetString() == item)
            .Sum(i => i.GetProperty("quantity").GetInt64());

    private static JsonElement Route(JsonElement station) => Assert.Single(station.Payload().GetProperty("routes").EnumerateArray());

    private static async Task<string> PlaceAsync(GameClient client, (int X, int Y) plot, string kind, int tileX, int tileY, int rotation)
    {
        var reply = await client.SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind, tileX, tileY, rotation });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    /// <summary>A pole and a generator full of coal near the station, so inserters at the track have power.</summary>
    private async Task PowerAsync(GameClient client, (int X, int Y) plot)
    {
        var (sx, _, _, _) = PlotLayout.Station;
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 10);
        await PlaceAsync(client, plot, Items.Pole, sx + 2, PlotLayout.RailRow + 3, South);
        var generator = await PlaceAsync(client, plot, Items.Generator, sx + 2, PlotLayout.RailRow + 4, South);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x = plot.X, y = plot.Y, id = generator, item = Items.Coal, quantity = 10 })).Type());
    }

    [Fact]
    public async Task A_route_sends_a_train_at_once_and_again_every_few_minutes_with_what_the_station_holds()
    {
        using var client = await PlayerAsync();
        var from = await BuyAsync(client);
        var to = await BuyAsync(client);
        await GiveAsync(client, Items.Wood, 2500);
        await GiveAsync(client, Items.Stone, 10);
        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Wood, quantity = 2500 });
        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Stone, quantity = 10 });

        var balance = await client.BalanceAsync();
        var added = await client.SendAsync("station.route", new
        {
            x = from.X, y = from.Y, toX = to.X, toY = to.Y, items = new[] { Items.Wood }, everyMinutes = 5,
        });
        Assert.Equal("station", added.Type());

        // The first train took as much wood as it carries, and no stone.
        var route = Route(added);
        Assert.Equal(RailRules.RouteCapacity, route.GetProperty("lastQuantity").GetInt64());
        Assert.Equal(JsonValueKind.Null, route.GetProperty("problem").ValueKind);
        Assert.Equal(2500 - RailRules.RouteCapacity, Stock(added, Items.Wood));
        Assert.Equal(10, Stock(added, Items.Stone));
        Assert.Equal(RailRules.RouteCapacity, added.Payload().GetProperty("outgoing").EnumerateArray().Sum(t => t.GetProperty("quantity").GetInt64()));
        var km = RailRules.DistanceKm(Map, from.X, from.Y, to.X, to.Y);
        Assert.Equal(balance - RailRules.FeeCents(km, RailRules.RouteCapacity), await client.BalanceAsync());

        factory.Time.Advance(RailRules.TripTime(km));
        Assert.Equal(RailRules.RouteCapacity, Stock(await client.SendAsync("station.get", new { x = to.X, y = to.Y }), Items.Wood));

        // Five minutes after the first, the next train takes the rest; looking at either end sends it.
        factory.Time.Advance(TimeSpan.FromMinutes(5) - RailRules.TripTime(km));
        var second = await client.SendAsync("station.get", new { x = to.X, y = to.Y });
        Assert.Equal(500, second.Payload().GetProperty("incoming").EnumerateArray().Sum(t => t.GetProperty("quantity").GetInt64()));
        factory.Time.Advance(RailRules.TripTime(km));
        Assert.Equal(2500, Stock(await client.SendAsync("station.get", new { x = to.X, y = to.Y }), Items.Wood));

        // With no wood left the next train stays, and says why.
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var empty = await client.SendAsync("station.get", new { x = from.X, y = from.Y });
        Assert.Equal("empty", Route(empty).GetProperty("problem").GetString());
        Assert.Equal(10, Stock(empty, Items.Stone));

        var removed = await client.SendAsync("station.unroute", new { x = from.X, y = from.Y, id = Route(empty).GetProperty("id").GetInt64() });
        Assert.Equal(0, removed.Payload().GetProperty("routes").GetArrayLength());
    }

    [Fact]
    public async Task A_truck_from_the_depot_drives_goods_to_a_station_nearby_on_diesel_without_the_rail_fee()
    {
        using var client = await PlayerAsync();
        var from = await BuyAsync(client);
        var near = await BuyAsync(client);
        Assert.InRange(RailRules.DistanceKm(Map, from.X, from.Y, near.X, near.Y), 1, TruckRules.MaxKm);
        // Another player's plot further away than a truck goes.
        using var other = await PlayerAsync();
        var far = FreePlot();
        while (RailRules.DistanceKm(Map, from.X, from.Y, far.X, far.Y) <= TruckRules.MaxKm)
        {
            far = FreePlot();
        }

        Assert.Equal("world.bought", (await other.SendAsync("world.buy", new { x = far.X, y = far.Y })).Type());
        await GiveAsync(client, Items.Wood, 1500);
        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Wood, quantity = 1500 });
        object Send(int toX, int toY, long quantity) => new { x = from.X, y = from.Y, toX, toY, item = Items.Wood, quantity, truck = true };

        Assert.Equal("no_truck_depot", (await client.SendAsync("station.send", Send(near.X, near.Y, 10))).ErrorCode());
        await GiveAsync(client, Items.TruckDepot, 1);
        var (sx, _, _, _) = PlotLayout.Station;
        await PlaceAsync(client, from, Items.TruckDepot, sx + 4, PlotLayout.RailRow + 3, South);
        Assert.Equal("no_diesel", (await client.SendAsync("station.send", Send(near.X, near.Y, 10))).ErrorCode());
        Assert.Equal("too_far", (await client.SendAsync("station.send", Send(far.X, far.Y, 10))).ErrorCode());
        Assert.Equal("too_much", (await client.SendAsync("station.send", Send(near.X, near.Y, TruckRules.Capacity + 1))).ErrorCode());

        await GiveAsync(client, Items.Diesel, 10);
        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Diesel, quantity = 10 });
        var balance = await client.BalanceAsync();
        var km = RailRules.DistanceKm(Map, from.X, from.Y, near.X, near.Y);
        var sent = await client.SendAsync("station.send", Send(near.X, near.Y, 400));
        Assert.Equal("station", sent.Type());
        Assert.Equal(balance, await client.BalanceAsync());
        Assert.Equal(10 - TruckRules.Fuel(km), Stock(sent, Items.Diesel));
        var arrives = sent.Payload().GetProperty("outgoing").EnumerateArray().Single().GetProperty("arrivesAt").GetInt64();
        Assert.InRange(arrives - (factory.Time.GetUtcNow() + TruckRules.TripTime(km)).ToUnixTimeMilliseconds(), -2, 2);

        // A truck route takes what one truck carries, every time it is due.
        var routed = await client.SendAsync("station.route", new { x = from.X, y = from.Y, toX = near.X, toY = near.Y, items = new[] { Items.Wood }, everyMinutes = 1, truck = true });
        var route = Route(routed);
        Assert.True(route.GetProperty("truck").GetBoolean());
        Assert.Equal(JsonValueKind.Null, route.GetProperty("problem").ValueKind);
        Assert.Equal(TruckRules.Capacity, route.GetProperty("lastQuantity").GetInt64());
        Assert.Equal(1500 - 400 - TruckRules.Capacity, Stock(routed, Items.Wood));
        Assert.Equal(balance, await client.BalanceAsync());

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        await client.SendAsync("station.get", new { x = from.X, y = from.Y });
        factory.Time.Advance(TruckRules.TripTime(km));
        Assert.Equal(1500, Stock(await client.SendAsync("station.get", new { x = near.X, y = near.Y }), Items.Wood));
        Assert.Equal("too_far", (await client.SendAsync("station.route",
            new { x = from.X, y = from.Y, toX = far.X, toY = far.Y, everyMinutes = 1, truck = true })).ErrorCode());
    }

    [Fact]
    public async Task Routes_are_checked_and_a_train_without_money_for_the_fee_stays()
    {
        using var client = await PlayerAsync(cents: 0);
        using var other = await PlayerAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // Just enough for two plots.
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 11_000_00, "test", CancellationToken.None);
        }

        var from = await BuyAsync(client);
        var to = await BuyAsync(client);
        object Route(int every, string[]? items = null, int? toX = null) =>
            new { x = from.X, y = from.Y, toX = toX ?? to.X, toY = to.Y, items, everyMinutes = every };

        Assert.Equal("bad_interval", (await client.SendAsync("station.route", Route(7))).ErrorCode());
        Assert.Equal("bad_items", (await client.SendAsync("station.route", Route(5, ["nothing"]))).ErrorCode());
        Assert.Equal("bad_destination", (await client.SendAsync("station.route", Route(5, toX: from.X))).ErrorCode());
        Assert.Equal("not_owner", (await other.SendAsync("station.route", Route(5))).ErrorCode());

        Assert.Equal(0, await client.BalanceAsync());
        await GiveAsync(client, Items.Wood, 5);
        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Wood, quantity = 5 });
        var poor = await client.SendAsync("station.route", Route(1));
        Assert.Equal("no_money", RailwayTests.Route(poor).GetProperty("problem").GetString());
        Assert.Equal(5, Stock(poor, Items.Wood));

        for (var i = 1; i < RailRules.MaxRoutes; i++)
        {
            Assert.Equal("station", (await client.SendAsync("station.route", Route(60))).Type());
        }

        Assert.Equal("too_many_routes", (await client.SendAsync("station.route", Route(60))).ErrorCode());
    }

    [Fact]
    public async Task Players_are_found_by_name_and_their_station_sees_who_sent_the_train()
    {
        using var sender = await PlayerAsync();
        using var receiver = await PlayerAsync();
        var from = await BuyAsync(sender);
        var to = await BuyAsync(receiver);
        var name = (await receiver.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;
        var senderName = (await sender.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

        var found = await sender.SendAsync("station.find", new { query = name[..6].ToLowerInvariant() });
        Assert.Equal("station.found", found.Type());
        var player = Assert.Single(found.Payload().GetProperty("players").EnumerateArray(), p => p.GetProperty("name").GetString() == name);
        var plot = Assert.Single(player.GetProperty("plots").EnumerateArray());
        Assert.Equal((to.X, to.Y), (plot.GetProperty("x").GetInt32(), plot.GetProperty("y").GetInt32()));
        // Nobody finds themselves.
        Assert.DoesNotContain((await sender.SendAsync("station.find", new { query = senderName })).Payload().GetProperty("players").EnumerateArray(),
            p => p.GetProperty("name").GetString() == senderName);

        await GiveAsync(sender, Items.IronIngot, 4);
        await sender.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.IronIngot, quantity = 4 });
        var routed = await sender.SendAsync("station.route", new { x = from.X, y = from.Y, toX = to.X, toY = to.Y, everyMinutes = 15 });
        Assert.Equal(name, Route(routed).GetProperty("receiver").GetString());
        Assert.Equal(name, routed.Payload().GetProperty("outgoing")[0].GetProperty("receiver").GetString());

        var incoming = (await receiver.SendAsync("station.get", new { x = to.X, y = to.Y })).Payload().GetProperty("incoming");
        Assert.Equal(senderName, incoming[0].GetProperty("sender").GetString());
        Assert.Equal(4, incoming[0].GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task Inserters_load_trains_at_one_plot_and_unload_them_at_another_while_nobody_watches()
    {
        using var client = await PlayerAsync();
        var mine = await BuyAsync(client);
        var factoryPlot = await BuyAsync(client);
        await GiveAsync(client, Items.WoodenChest, 2);
        await GiveAsync(client, Items.Inserter, 2);
        await GiveAsync(client, Items.IronOre, 30);

        // Chest → inserter facing the track in front of the station: it loads the trains.
        var (sx, _, _, _) = PlotLayout.Station;
        var loading = await PlaceAsync(client, mine, Items.WoodenChest, sx + 1, PlotLayout.RailRow + 2, South);
        await PlaceAsync(client, mine, Items.Inserter, sx + 1, PlotLayout.RailRow + 1, North);
        await client.SendAsync("storage.put", new { x = mine.X, y = mine.Y, id = loading, item = Items.IronOre, quantity = 30 });

        // At the other plot an inserter turned away from the track unloads them into a chest.
        await PlaceAsync(client, factoryPlot, Items.Inserter, sx + 3, PlotLayout.RailRow + 1, South);
        var unloading = await PlaceAsync(client, factoryPlot, Items.WoodenChest, sx + 3, PlotLayout.RailRow + 2, South);
        await PowerAsync(client, mine);
        await PowerAsync(client, factoryPlot);

        var routed = await client.SendAsync("station.route", new
        {
            x = mine.X, y = mine.Y, toX = factoryPlot.X, toY = factoryPlot.Y, items = new[] { Items.IronOre }, everyMinutes = 1,
        });
        Assert.Equal("empty", Route(routed).GetProperty("problem").GetString());

        // The inserter fills the station within the minute and the next train takes all of it.
        factory.Time.Advance(TimeSpan.FromMinutes(1));
        var sent = await client.SendAsync("station.get", new { x = mine.X, y = mine.Y });
        Assert.Equal(30, Route(sent).GetProperty("lastQuantity").GetInt64());
        Assert.Equal(0, Stock(sent, Items.IronOre));

        var km = RailRules.DistanceKm(Map, mine.X, mine.Y, factoryPlot.X, factoryPlot.Y);
        factory.Time.Advance(RailRules.TripTime(km) + TimeSpan.FromMinutes(2));
        var logistics = await client.SendAsync("logistics.get", new { x = factoryPlot.X, y = factoryPlot.Y });
        var chest = logistics.Payload().GetProperty("storages").EnumerateArray().Single(s => s.GetProperty("id").GetString() == unloading);
        Assert.Equal(30, chest.GetProperty("stored").GetInt64());
        Assert.Equal(0, Stock(await client.SendAsync("station.get", new { x = factoryPlot.X, y = factoryPlot.Y }), Items.IronOre));

        Assert.Equal("blocked", (await client.SendAsync("plot.place", new
        {
            x = mine.X, y = mine.Y, kind = Items.Inserter, tileX = sx + 5, tileY = PlotLayout.RailRow, rotation = North,
        })).ErrorCode());
    }
}
