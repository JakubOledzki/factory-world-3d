using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>What the export tests share: the map, players, plots and the buildings of export by rail.</summary>
public abstract class ExportTestBase(ServerFactory factory)
{
    protected static readonly WorldMap Map = WorldMap.Load();
    protected static readonly Towns AllTowns = Towns.Load();
    protected static readonly LandMasses Land = new(Map);

    protected ServerFactory Factory => factory;

    protected static Town TownNamed(string name) => AllTowns.All.First(t => t.Name == name);

    /// <summary>A chest's tile by the station's platform, and one too far from it.</summary>
    protected static readonly (int X, int Y) ByStation = (PlotLayout.Station.X + 4, PlotLayout.RailRow + 2);

    protected static readonly (int X, int Y) FarFromStation = (PlotLayout.Spawn.X, PlotLayout.RailRow + 20);

    protected async Task<GameClient> PlayerAsync(long cents = 1_000_000_00)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        return client;
    }

    protected async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    protected static async Task BuyAsync(GameClient client, (int X, int Y) plot)
    {
        var bought = await client.SendAsync("world.buy", new { x = plot.X, y = plot.Y });
        Assert.True(bought.Type() == "world.bought", bought.ToString());
    }

    /// <summary>A plot of <paramref name="biome"/> near (x, y) on the same landmass as it, or on another one.</summary>
    protected static (int X, int Y) Near(int x, int y, Biome biome, bool sameLand = true, int skip = 0)
    {
        for (var r = 0; r < 20; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    var (px, py) = (x + dx, y + dy);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Map.BiomeAt(px, py) == biome && Land.Joined(x, y, px, py) == sameLand && skip-- <= 0)
                    {
                        return (px, py);
                    }
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    /// <summary>Where a rail port's 9 × 9 ground fits on a coast plot, all of it free and its edge by the sea.</summary>
    protected (int X, int Y) Shore((int X, int Y) plot) => FindShore(plot) ?? throw new InvalidOperationException("No shore.");

    /// <summary>The coast plot nearest (x, y) on the same landmass or on another one with room for a rail port by the sea.</summary>
    protected (int X, int Y) PortPlot(int x, int y, bool sameLand = true)
    {
        for (var skip = 0; ; skip++)
        {
            var plot = Near(x, y, Biome.Coast, sameLand, skip);
            if (FindShore(plot) is not null)
            {
                return plot;
            }
        }
    }

    private (int X, int Y)? FindShore((int X, int Y) plot)
    {
        var tiles = PlotLayout.Generate(plot.X, plot.Y, Biome.Coast);
        // What the sea threw up on the beach today lies on free sand by the water and is in the way too.
        var day = SeaWeather.Day(factory.Time.GetUtcNow());
        foreach (var (fx, fy) in Coast.Flotsam(plot.X, plot.Y, Biome.Coast, tiles, day, true))
        {
            tiles[fy * PlotLayout.Size + fx] = (byte)TileObject.Flotsam;
        }

        for (var y = PlotLayout.Size - 1; y >= 20; y--)
        {
            for (var x = 40; x < PlotLayout.Size - 40; x++)
            {
                var ground = Footprints.Tiles(Items.RailPort, x, y).ToList();
                if (ground.All(t => PlotLayout.Inside(t.X, t.Y) && tiles[t.Y * PlotLayout.Size + t.X] == (byte)TileObject.None && t != PlotLayout.Spawn)
                    && ground.Any(t => Coast.NextToWater(tiles, t.X, t.Y)))
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    /// <summary>A free tile of a new plot from row <paramref name="row"/> on, with <paramref name="reach"/> free tiles all round it, away from water.</summary>
    protected static (int X, int Y) FreeTile((int X, int Y) plot, int row, int reach = 1)
    {
        var tiles = PlotLayout.Generate(plot.X, plot.Y, Map.BiomeAt(plot.X, plot.Y));
        bool Free(int x, int y) => tiles[y * PlotLayout.Size + x] == (byte)TileObject.None;
        for (var y = row; y < PlotLayout.Size - 1 - reach; y++)
        {
            for (var x = 20; x < PlotLayout.Size - 20; x++)
            {
                var around = Enumerable.Range(-reach, 2 * reach + 1).SelectMany(dy => Enumerable.Range(-reach, 2 * reach + 1).Select(dx => (X: x + dx, Y: y + dy))).ToList();
                if (around.All(t => Free(t.X, t.Y) && t != PlotLayout.Spawn && !Coast.NextToWater(tiles, t.X, t.Y)))
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No free tile.");
    }

    protected static async Task<string> PlaceAsync(GameClient client, (int X, int Y) plot, string kind, (int X, int Y) tile)
    {
        var reply = await client.SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind, tileX = tile.X, tileY = tile.Y, rotation = 0 });
        Assert.True(reply.Type() == "plot.placed", reply.ToString());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    protected static async Task<string?> PlaceErrorAsync(GameClient client, (int X, int Y) plot, string kind, (int X, int Y) tile) =>
        (await client.SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind, tileX = tile.X, tileY = tile.Y, rotation = 0 })).ErrorCode();

    protected static async Task<JsonElement> ExportAsync(GameClient client, (int X, int Y) plot, string type = "export.get", object? extra = null)
    {
        var payload = new Dictionary<string, object> { ["x"] = plot.X, ["y"] = plot.Y };
        foreach (var p in extra?.GetType().GetProperties() ?? [])
        {
            payload[p.Name] = p.GetValue(extra)!;
        }

        var reply = await client.SendAsync(type, payload);
        Assert.True(reply.Type() == "export", reply.ToString());
        return reply.Payload();
    }

    /// <summary>Moves the clock to the next full hour, when the trains call at the chests.</summary>
    protected void ToNextPickup() => factory.Time.Advance(ExportRules.NextPickup(factory.Time.GetUtcNow()) - factory.Time.GetUtcNow() + TimeSpan.FromSeconds(1));

    protected async Task RunDueAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ExportService>().RunDueAsync(100, CancellationToken.None);
    }

    protected static async Task<List<JsonElement>> ListingsAsync(GameClient client) =>
        (await client.SendAsync("market.mine")).Payload().GetProperty("listings").EnumerateArray().Where(l => l.GetProperty("open").GetBoolean()).ToList();
}

public sealed class ExportTests(ServerFactory factory) : ExportTestBase(factory), IClassFixture<ServerFactory>
{
    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void Great_Britain_is_an_island_but_Paris_and_Warsaw_share_a_continent()
    {
        var (london, paris, warsaw) = (TownNamed("Londyn"), TownNamed("Paryż"), TownNamed("Warszawa"));
        Assert.True(Land.Joined(paris.X, paris.Y, warsaw.X, warsaw.Y));
        Assert.False(Land.Joined(london.X, london.Y, paris.X, paris.Y));
        Assert.NotNull(Land.Of(london.X, london.Y));
        // The open sea belongs to no landmass.
        var (ox, oy) = Map.PlotAt(45, -30);
        Assert.True(Map.IsWater(ox, oy));
        Assert.Null(Land.Of(ox, oy));
        // Around the seam of the map, land at both ends of a row is one (the far east of Russia and Chukotka).
        var (ex, ey) = Map.PlotAt(66, 179.5);
        var (wx, wy) = Map.PlotAt(66, -179.5);
        Assert.True(Land.Joined(ex, ey, wx, wy));
    }

    [Fact]
    public void Trains_take_a_tenth_of_a_minute_a_plot_over_land_three_times_as_long_by_sea_and_call_every_full_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ExportRules.TripTime(10, 0));
        Assert.Equal(TimeSpan.FromMinutes(10), ExportRules.TripTime(100, 0));
        Assert.Equal(TimeSpan.FromMinutes(100), ExportRules.TripTime(1000, 0));
        Assert.Equal(TimeSpan.FromMinutes(3), ExportRules.TripTime(0, 10));
        Assert.Equal(ExportRules.MinTrip, ExportRules.TripTime(1, 0));
        var at = new DateTimeOffset(2026, 10, 25, 14, 47, 13, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 15, 0, 0, TimeSpan.Zero), ExportRules.NextPickup(at));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.Zero), ExportRules.NextPickup(at.AddMinutes(13).AddSeconds(-13)));
        Assert.Equal(5, ExportRules.Commission(100, 5));
        Assert.Equal(6, ExportRules.Commission(101, 5));
        Assert.Equal(1, ExportRules.Commission(1, 5));
        Assert.True(ExportRules.NearStation(ByStation.X, ByStation.Y));
        Assert.False(ExportRules.NearStation(FarFromStation.X, FarFromStation.Y));

        // Researched in the city branch and the logistics branch.
        Assert.Equal(TechTree.ExportMarket, TechTree.ByItem[Items.BuyMarket].Id);
        Assert.Equal(TechTree.ExportMarket, TechTree.ByItem[Items.ExportChest].Id);
        Assert.Equal(TechTree.RailFerry, TechTree.ByItem[Items.RailPort].Id);
        Assert.Equal("city", TechTree.BranchOf[TechTree.ExportMarket].Id);
        Assert.Equal("logistics", TechTree.BranchOf[TechTree.RailFerry].Id);
        Assert.Contains(TechTree.Seafaring, TechTree.ById[TechTree.RailFerry].Requires);
        Assert.All([Items.ExportChest, Items.BuyMarket, Items.RailPort], i =>
        {
            Assert.Contains(i, PlotObjects.Placeable);
            Assert.True(NpcPrices.Buys.ContainsKey(i), i);
        });
        Assert.True(Items.IsCityOnly(Items.BuyMarket));
        Assert.Equal(Biome.Coast, Coast.OnShore[Items.RailPort]);
    }

    [Fact]
    public void The_router_takes_the_quickest_way_through_ports_and_counts_the_sea_three_times()
    {
        var (london, paris, warsaw) = (TownNamed("Londyn"), TownNamed("Paryż"), TownNamed("Warszawa"));
        var owner = Guid.NewGuid();
        var marketParis = new ExportPost(Guid.NewGuid(), Items.BuyMarket, paris.X, paris.Y, owner, 5);
        var marketWarsaw = new ExportPost(Guid.NewGuid(), Items.BuyMarket, warsaw.X, warsaw.Y, owner, 5);
        // From Kraków the nearer market is Warsaw's.
        var krakow = TownNamed("Kraków");
        var route = ExportRouter.Find(Land, krakow.X, krakow.Y, [marketParis, marketWarsaw], [])!;
        Assert.Equal(marketWarsaw, route.Market);
        Assert.Empty(route.Ports);
        var plots = CityRules.Distance(Map.Width, krakow.X, krakow.Y, warsaw.X, warsaw.Y);
        Assert.Equal(plots, route.LandPlots, 6);
        Assert.Equal(TimeSpan.FromMinutes(plots * ExportRules.MinutesPerPlot), route.Trip);

        // From London there is no way to the continent without ports.
        Assert.Null(ExportRouter.Find(Land, london.X, london.Y, [marketParis], []));
        var dover = Near(TownNamed("Dover").X, TownNamed("Dover").Y, Biome.Coast);
        var calais = Near(dover.X, dover.Y, Biome.Coast, sameLand: false);
        var portDover = new ExportPost(Guid.NewGuid(), Items.RailPort, dover.X, dover.Y, owner, 1000);
        var portCalais = new ExportPost(Guid.NewGuid(), Items.RailPort, calais.X, calais.Y, owner, 2000);
        // A port far away on the continent is not taken: the quickest way crosses the Channel.
        var far = new ExportPost(Guid.NewGuid(), Items.RailPort, krakow.X, krakow.Y, owner, 3000);
        route = ExportRouter.Find(Land, london.X, london.Y, [marketParis], [far, portDover, portCalais])!;
        Assert.Equal(new[] { portDover, portCalais }, route.Ports);
        Assert.Equal(3, route.Legs.Count);
        Assert.Equal(new[] { false, true, false }, route.Legs.Select(l => l.BySea));
        var sea = CityRules.Distance(Map.Width, dover.X, dover.Y, calais.X, calais.Y);
        Assert.Equal(sea, route.SeaPlots, 6);
        var land = CityRules.Distance(Map.Width, london.X, london.Y, dover.X, dover.Y) + CityRules.Distance(Map.Width, calais.X, calais.Y, paris.X, paris.Y);
        Assert.Equal(TimeSpan.FromMinutes((land + 3 * sea) * ExportRules.MinutesPerPlot), route.Trip);
    }

    // --- In the world -----------------------------------------------------------------------------

    [Fact]
    public async Task Inserters_fill_the_export_chest_from_the_factory_while_nobody_watches()
    {
        using var client = await PlayerAsync();
        var plot = Near(TownNamed("Kraków").X, TownNamed("Kraków").Y, Biome.Farmland);
        await BuyAsync(client, plot);
        await GiveAsync(client, Items.ExportChest, 1);
        await GiveAsync(client, Items.WoodenChest, 1);
        await GiveAsync(client, Items.Inserter, 1);
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Generator, 1);
        await GiveAsync(client, Items.Coal, 10);
        await GiveAsync(client, Items.Stone, 40);
        await PlaceAsync(client, plot, Items.ExportChest, ByStation);
        // Chest → inserter turned to the north → the export chest.
        var store = await PlaceAsync(client, plot, Items.WoodenChest, (ByStation.X, ByStation.Y + 2));
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place",
            new { x = plot.X, y = plot.Y, kind = Items.Inserter, tileX = ByStation.X, tileY = ByStation.Y + 1, rotation = 2 })).Type());
        await client.SendAsync("storage.put", new { x = plot.X, y = plot.Y, id = store, item = Items.Stone, quantity = 40 });
        var (sx, _, _, _) = PlotLayout.Station;
        await PlaceAsync(client, plot, Items.Pole, (sx + 2, PlotLayout.RailRow + 3));
        var generator = await PlaceAsync(client, plot, Items.Generator, (sx + 2, PlotLayout.RailRow + 4));
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x = plot.X, y = plot.Y, id = generator, item = Items.Coal, quantity = 10 })).Type());

        Factory.Time.Advance(TimeSpan.FromMinutes(2));
        var chest = await ExportAsync(client, plot);
        Assert.Equal(40, Assert.Single(chest.GetProperty("items").EnumerateArray()).GetProperty("quantity").GetInt64());
    }

    [Fact]
    public async Task A_train_takes_the_priced_goods_of_the_export_chest_every_hour_to_the_nearest_buying_market_which_lists_them()
    {
        using var seller = await PlayerAsync();
        using var merchant = await PlayerAsync();
        using var buyer = await PlayerAsync();
        var paris = TownNamed("Paryż");
        var farm = Near(TownNamed("Lille").X, TownNamed("Lille").Y, Biome.Farmland);
        await BuyAsync(seller, farm);
        await GiveAsync(seller, Items.ExportChest, 2);

        // The chest stands by the station, one a plot.
        Assert.Equal("far_from_station", await PlaceErrorAsync(seller, farm, Items.ExportChest, FarFromStation));
        await PlaceAsync(seller, farm, Items.ExportChest, ByStation);
        Assert.Equal("has_one", await PlaceErrorAsync(seller, farm, Items.ExportChest, (ByStation.X + 1, ByStation.Y)));

        await GiveAsync(seller, Items.Wood, 500);
        await GiveAsync(seller, Items.Stone, 50);
        var chest = await ExportAsync(seller, farm, "export.load", new { item = Items.Wood, quantity = 500 });
        await ExportAsync(seller, farm, "export.load", new { item = Items.Stone, quantity = 50 });
        Assert.Equal("chest_full", (await seller.SendAsync("export.load", new { x = farm.X, y = farm.Y, item = Items.Wood, quantity = ExportRules.Capacity })).ErrorCode());
        chest = await ExportAsync(seller, farm, "export.price", new { item = Items.Wood, priceCents = 250L });
        Assert.Equal(250, chest.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == Items.Wood).GetProperty("priceCents").GetInt64());
        Assert.Equal(0, chest.GetProperty("markets").GetInt32());
        Assert.Equal(JsonValueKind.Null, chest.GetProperty("route").ValueKind);

        // With no buying market in the world, the train takes nothing.
        ToNextPickup();
        chest = await ExportAsync(seller, farm);
        Assert.Equal("no_market", chest.GetProperty("problem").GetString());
        Assert.Equal(500, chest.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == Items.Wood).GetProperty("quantity").GetInt64());

        // A buying market stands in a city only, one a town.
        var centre = (paris.X, paris.Y);
        await BuyAsync(merchant, centre);
        await GiveAsync(merchant, Items.BuyMarket, 2);
        var marketTile = (PlotLayout.Spawn.X - 10, 30);
        Assert.Equal("no_city", await PlaceErrorAsync(seller, farm, Items.BuyMarket, FreeTile(farm, 30)));
        var market = await PlaceAsync(merchant, centre, Items.BuyMarket, marketTile);

        // The merchant sets the commission; others may look but not change it.
        var post = (await merchant.SendAsync("export.post", new { id = market })).Payload();
        Assert.Equal(ExportRules.DefaultCommissionPercent, post.GetProperty("value").GetInt64());
        Assert.Equal("Paryż", post.GetProperty("town").GetString());
        Assert.Equal("bad_commission", (await merchant.SendAsync("export.setPost", new { id = market, value = 21 })).ErrorCode());
        Assert.Equal("not_owner", (await seller.SendAsync("export.setPost", new { id = market, value = 1 })).ErrorCode());
        Assert.Equal(10, (await merchant.SendAsync("export.setPost", new { id = market, value = 10 })).Payload().GetProperty("value").GetInt64());

        chest = await ExportAsync(seller, farm);
        var route = chest.GetProperty("route");
        Assert.Equal("Paryż", route.GetProperty("town").GetString());
        Assert.Equal(10, route.GetProperty("commissionPercent").GetInt64());
        var plots = CityRules.Distance(Map.Width, farm.X, farm.Y, paris.X, paris.Y);
        var trip = ExportRules.TripTime(plots, 0);
        Assert.Equal((long)trip.TotalSeconds, route.GetProperty("tripSeconds").GetInt64());

        // At the next full hour the train takes the priced wood and leaves the stone; the commission is paid at once.
        var (sellerBefore, merchantBefore) = (await seller.BalanceAsync(), await merchant.BalanceAsync());
        ToNextPickup();
        var departure = ExportRules.NextPickup(Factory.Time.GetUtcNow()) - ExportRules.Every;
        chest = await ExportAsync(seller, farm);
        Assert.Equal(JsonValueKind.Null, chest.GetProperty("problem").ValueKind);
        Assert.Equal(500, chest.GetProperty("lastQuantity").GetInt64());
        Assert.Equal(Items.Stone, Assert.Single(chest.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        var shipment = Assert.Single(chest.GetProperty("shipments").EnumerateArray());
        Assert.Equal(500, shipment.GetProperty("quantity").GetInt64());
        Assert.Equal("Paryż", shipment.GetProperty("town").GetString());
        Assert.Equal((departure + trip).ToUnixTimeMilliseconds(), shipment.GetProperty("arrivesAt").GetInt64());
        var commission = ExportRules.Commission(500 * 250, 10);
        Assert.Equal(sellerBefore - commission, await seller.BalanceAsync());
        Assert.Equal(merchantBefore + commission, await merchant.BalanceAsync());
        Assert.Empty(await ListingsAsync(seller));

        // When it gets there, the wood are on the market at the seller's price.
        Factory.Time.Advance(trip);
        await RunDueAsync();
        Assert.Empty((await ExportAsync(seller, farm)).GetProperty("shipments").EnumerateArray());
        var listing = Assert.Single(await ListingsAsync(seller));
        Assert.Equal(Items.Wood, listing.GetProperty("item").GetString());
        Assert.Equal(500, listing.GetProperty("quantity").GetInt64());
        Assert.Equal(250, listing.GetProperty("priceCents").GetInt64());
        var earned = (await merchant.SendAsync("export.post", new { id = market })).Payload();
        Assert.Equal(commission, earned.GetProperty("earnedCents").GetInt64());

        // Anyone buys them like any other listing; the money goes to the seller.
        var balance = await seller.BalanceAsync();
        var bought = await buyer.SendAsync("market.purchase", new { id = listing.GetProperty("id").GetInt64(), quantity = 100 });
        Assert.True(bought.Type() == "market.done", bought.ToString());
        Assert.Equal(balance + MarketRules.AfterFee(100 * 250), await seller.BalanceAsync());

        // A train finds nothing priced in the chest, only the stone: it takes nothing.
        ToNextPickup();
        chest = await ExportAsync(seller, farm);
        Assert.Equal("no_price", chest.GetProperty("problem").GetString());

        // A chest picked up gives its goods back.
        Assert.Equal("plot.pickedUp", (await seller.SendAsync("plot.pickup", new { x = farm.X, y = farm.Y, id = chest.GetProperty("id").GetString() })).Type());
        Assert.Equal(50, (await seller.InventoryAsync())[Items.Stone]);
        Assert.Equal("no_export_chest", (await seller.SendAsync("export.get", new { x = farm.X, y = farm.Y })).ErrorCode());

        // A town has room for one buying market, whichever of its plots it stands on.
        var (first, second) = AllTowns.All.Where(t => t.Population > 500_000).Select(t =>
        {
            var plots = Enumerable.Range(-3, 7).SelectMany(dy => Enumerable.Range(-3, 7).Select(dx => (X: t.X + dx, Y: t.Y + dy)))
                .Where(p => Map.BiomeAt(p.X, p.Y) == Biome.City && AllTowns.Of(p.X, p.Y) == t).Take(2).ToList();
            return plots.Count == 2 ? (plots[0], plots[1]) : default;
        }).First(p => p != default);
        using var rival = await PlayerAsync();
        await BuyAsync(merchant, first);
        await BuyAsync(rival, second);
        await GiveAsync(rival, Items.BuyMarket, 1);
        await PlaceAsync(merchant, first, Items.BuyMarket, marketTile);
        Assert.Equal("town_has_market", await PlaceErrorAsync(rival, second, Items.BuyMarket, marketTile));
    }
}

/// <summary>Over the sea, with a database of its own: the market of these tests is the only one in its world.</summary>
public sealed class ExportSeaTests(ServerFactory factory) : ExportTestBase(factory), IClassFixture<ServerFactory>
{
    [Fact]
    public async Task From_an_island_the_train_crosses_the_sea_on_ferries_from_rail_ports_whose_owners_take_their_fee()
    {
        using var seller = await PlayerAsync();
        using var merchant = await PlayerAsync();
        using var ferryman = await PlayerAsync();
        var (london, paris) = (TownNamed("Londyn"), TownNamed("Paryż"));
        var field = Near(london.X, london.Y, Biome.Farmland);
        await BuyAsync(seller, field);
        await GiveAsync(seller, Items.ExportChest, 1);
        await PlaceAsync(seller, field, Items.ExportChest, ByStation);
        await GiveAsync(seller, Items.Bread, 200);
        await ExportAsync(seller, field, "export.load", new { item = Items.Bread, quantity = 200 });
        await ExportAsync(seller, field, "export.price", new { item = Items.Bread, priceCents = 2000L });

        await BuyAsync(merchant, (paris.X, paris.Y));
        await GiveAsync(merchant, Items.BuyMarket, 1);
        await PlaceAsync(merchant, (paris.X, paris.Y), Items.BuyMarket, (PlotLayout.Spawn.X - 10, 30));

        // There is a market, but no way over the Channel without ports.
        var chest = await ExportAsync(seller, field);
        Assert.Equal(1, chest.GetProperty("markets").GetInt32());
        Assert.Equal(JsonValueKind.Null, chest.GetProperty("route").ValueKind);
        ToNextPickup();
        Assert.Equal("no_route", (await ExportAsync(seller, field)).GetProperty("problem").GetString());

        // A rail port stands on the sea shore only.
        var dover = PortPlot(TownNamed("Dover").X, TownNamed("Dover").Y);
        var calais = PortPlot(dover.X, dover.Y, sameLand: false);
        await GiveAsync(ferryman, Items.RailPort, 3);
        var inland = Near(dover.X, dover.Y, Biome.Farmland);
        await BuyAsync(ferryman, inland);
        Assert.Equal("no_coast", await PlaceErrorAsync(ferryman, inland, Items.RailPort, FreeTile(inland, 30, Footprints.Reach(Items.RailPort))));
        await BuyAsync(ferryman, dover);
        await BuyAsync(ferryman, calais);
        Assert.Equal("not_on_shore", await PlaceErrorAsync(ferryman, dover, Items.RailPort, FreeTile(dover, 30, Footprints.Reach(Items.RailPort))));
        var portDover = await PlaceAsync(ferryman, dover, Items.RailPort, Shore(dover));
        var portCalais = await PlaceAsync(ferryman, calais, Items.RailPort, Shore(calais));
        // The port with its tracks and ferry ramp covers 9 × 9 tiles.
        await GiveAsync(ferryman, Items.WoodenChest, 1);
        Assert.Equal("blocked", await PlaceErrorAsync(ferryman, dover, Items.WoodenChest, (Shore(dover).X + 4, Shore(dover).Y - 4)));
        Assert.Equal(ExportRules.DefaultPortFeeCents, (await ferryman.SendAsync("export.post", new { id = portDover })).Payload().GetProperty("value").GetInt64());
        Assert.Equal("bad_fee", (await ferryman.SendAsync("export.setPost", new { id = portCalais, value = ExportRules.MaxPortFeeCents + 1 })).ErrorCode());
        Assert.Equal("export.post", (await ferryman.SendAsync("export.setPost", new { id = portCalais, value = 25_00 })).Type());

        chest = await ExportAsync(seller, field);
        var route = chest.GetProperty("route");
        Assert.Equal(2, route.GetProperty("ports").GetArrayLength());
        Assert.Equal(10_00 + 25_00, route.GetProperty("portFeesCents").GetInt64());
        Assert.Equal(new[] { false, true, false }, route.GetProperty("legs").EnumerateArray().Select(l => l.GetProperty("sea").GetBoolean()));
        var land = CityRules.Distance(Map.Width, field.X, field.Y, dover.X, dover.Y) + CityRules.Distance(Map.Width, calais.X, calais.Y, paris.X, paris.Y);
        var sea = CityRules.Distance(Map.Width, dover.X, dover.Y, calais.X, calais.Y);
        var trip = ExportRules.TripTime(land, sea);
        Assert.Equal((long)trip.TotalSeconds, route.GetProperty("tripSeconds").GetInt64());

        // The ports' fees and the commission are paid when the train leaves, into their owners' wallets.
        var (sellerBefore, ferrymanBefore, merchantBefore) = (await seller.BalanceAsync(), await ferryman.BalanceAsync(), await merchant.BalanceAsync());
        ToNextPickup();
        chest = await ExportAsync(seller, field);
        Assert.Equal(200, chest.GetProperty("lastQuantity").GetInt64());
        var commission = ExportRules.Commission(200 * 2000, ExportRules.DefaultCommissionPercent);
        Assert.Equal(sellerBefore - 35_00 - commission, await seller.BalanceAsync());
        Assert.Equal(ferrymanBefore + 35_00, await ferryman.BalanceAsync());
        Assert.Equal(merchantBefore + commission, await merchant.BalanceAsync());
        var port = (await ferryman.SendAsync("export.post", new { id = portCalais })).Payload();
        Assert.Equal(25_00, port.GetProperty("earnedCents").GetInt64());
        Assert.Equal(1, port.GetProperty("uses").GetInt64());

        // The bread is on the market once the train has crossed and got to Paris.
        Factory.Time.Advance(trip - TimeSpan.FromSeconds(30));
        await RunDueAsync();
        Assert.Empty(await ListingsAsync(seller));
        Factory.Time.Advance(TimeSpan.FromSeconds(30));
        await RunDueAsync();
        Assert.Equal(200, Assert.Single(await ListingsAsync(seller)).GetProperty("quantity").GetInt64());

        // Without the money for the fees the train leaves the goods.
        await GiveAsync(seller, Items.Bread, 10);
        await ExportAsync(seller, field, "export.load", new { item = Items.Bread, quantity = 10 });
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var wallet = scope.ServiceProvider.GetRequiredService<IWallet>();
            await wallet.DebitAsync(seller.AccountId, await seller.BalanceAsync(), "test", CancellationToken.None);
        }

        ToNextPickup();
        Assert.Equal("no_money", (await ExportAsync(seller, field)).GetProperty("problem").GetString());
    }
}
