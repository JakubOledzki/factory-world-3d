using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class CoastTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void The_coast_ends_in_the_sea_and_the_lakeshore_has_a_lake_while_other_plots_stay_dry()
    {
        var coast = PlotLayout.Generate(40, 40, Biome.Coast);
        var lake = PlotLayout.Generate(40, 40, Biome.Lakeshore);
        var forest = PlotLayout.Generate(40, 40, Biome.Forest);
        int Water(byte[] tiles) => tiles.Count(t => t == (byte)TileObject.Water);
        Assert.Equal(0, Water(forest));
        Assert.True(Water(lake) > 2000, $"lake of {Water(lake)} tiles");
        // The sea fills the last rows of every column, and never reaches the railway.
        for (var x = 0; x < PlotLayout.Size; x++)
        {
            Assert.Equal((byte)TileObject.Water, coast[(PlotLayout.Size - 1) * PlotLayout.Size + x]);
            var shore = Coast.ShoreRow(Coast.Seed(40, 40), x);
            Assert.Equal((byte)TileObject.Water, coast[shore * PlotLayout.Size + x]);
            Assert.Equal((byte)TileObject.None, coast[(shore - 1) * PlotLayout.Size + x]);
        }

        Assert.All(Enumerable.Range(0, 20 * PlotLayout.Size), i => Assert.NotEqual((byte)TileObject.Water, coast[i]));
    }

    [Fact]
    public void Coastal_plots_fish_thirty_percent_faster_and_start_with_a_rod()
    {
        Assert.Equal(Items.FishingRod, PlotLayout.StarterTool(Biome.Coast));
        Assert.Equal(Items.FishingRod, PlotLayout.StarterTool(Biome.Lakeshore));
        Assert.Equal(Items.FishingRod, PlotLayout.ToolFor(TileObject.Water));
        Assert.True(Items.IsTool(Items.FishingRod));
        Assert.Equal(Coast.CastTime.TotalSeconds / 1.3, Coast.FishTime(Biome.Coast).TotalSeconds, 6);
        Assert.Equal(Coast.FishTime(Biome.Coast), HarvestRules.TimeFor(TileObject.Water, Items.FishingRod, Biome.Coast));
        Assert.Contains(Coast.Catch(Biome.Coast, 0), c => c.Item == Items.Herring);
        Assert.Contains(Coast.Catch(Biome.Coast, 99), c => c.Item == Items.Cod);
        Assert.Contains(Coast.Catch(Biome.Lakeshore, 0), c => c.Item == Items.Carp);
        Assert.Contains(Coast.Catch(Biome.Lakeshore, 99), c => c.Item == Items.Pike);
    }

    [Fact]
    public void Storms_blow_now_and_then_and_boats_lose_exactly_that_time()
    {
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var storms = 0;
        var t = from;
        while (t < from + TimeSpan.FromDays(30))
        {
            var (start, end) = SeaWeather.Next(t);
            Assert.Equal(SeaWeather.Length, end - start);
            Assert.True(SeaWeather.IsStorm(start));
            Assert.False(SeaWeather.IsStorm(end));
            Assert.False(SeaWeather.IsStorm(start - TimeSpan.FromSeconds(1)));
            storms++;
            t = end;
        }

        // About one window in eight, twelve windows a day.
        Assert.InRange(storms, 20, 70);

        var (s, e) = SeaWeather.Next(from + TimeSpan.FromDays(3));
        Assert.Equal(SeaWeather.Length, SeaWeather.StormTime(s - TimeSpan.FromMinutes(5), e + TimeSpan.FromMinutes(5)));
        var clock = SeaClock.Instance;
        Assert.Equal(0, clock.Speed(s + TimeSpan.FromMinutes(1)));
        Assert.Equal(1, clock.Speed(s - TimeSpan.FromMinutes(1)));
        // A boat's time stands still through the storm.
        Assert.Equal(clock.At(s), clock.At(e));
        var before = s - TimeSpan.FromMinutes(10);
        var due = clock.When(clock.At(before) + TimeSpan.FromMinutes(20), before)!.Value;
        Assert.Equal(e + TimeSpan.FromMinutes(10), due);
    }

    [Fact]
    public void Shore_buildings_stand_by_the_water_of_their_biome()
    {
        var (x, y) = (40, 40);
        var tiles = PlotLayout.Generate(x, y, Biome.Coast);
        var shore = Coast.ShoreRow(Coast.Seed(x, y), 100) - 1;
        Coast.RequireSite(Items.FishingBoat, x, y, Biome.Coast, 100, shore);
        Coast.RequireSite(Items.Port, x, y, Biome.Coast, 100, shore);
        Assert.True(Coast.NextToWater(tiles, 100, shore));
        Assert.Equal("not_on_shore", Assert.Throws<Modules.Abstractions.GameException>(() =>
            Coast.RequireSite(Items.FishingBoat, x, y, Biome.Coast, 100, shore - 5)).Code);
        Assert.Equal("no_lake", Assert.Throws<Modules.Abstractions.GameException>(() =>
            Coast.RequireSite(Items.RowingBoat, x, y, Biome.Coast, 100, shore)).Code);
        Assert.Equal("no_coast", Assert.Throws<Modules.Abstractions.GameException>(() =>
            Coast.RequireSite(Items.Lighthouse, x, y, Biome.Lakeshore, 100, shore)).Code);
        Assert.Equal("no_shore", Assert.Throws<Modules.Abstractions.GameException>(() =>
            Coast.RequireSite(Items.FishFarm, x, y, Biome.Forest, 100, shore)).Code);
        // A lighthouse stands anywhere on the coast.
        Coast.RequireSite(Items.Lighthouse, x, y, Biome.Coast, 100, 40);
    }

    [Fact]
    public void The_beach_gets_a_dozen_finds_a_day_and_three_times_as_many_after_a_storm()
    {
        var tiles = PlotLayout.Generate(40, 40, Biome.Coast);
        var calm = Coast.Flotsam(40, 40, Biome.Coast, tiles, 20_000, false);
        var stormy = Coast.Flotsam(40, 40, Biome.Coast, tiles, 20_000, true);
        Assert.InRange(calm.Count, Coast.FlotsamPerDay - 3, Coast.FlotsamPerDay);
        Assert.True(stormy.Count > calm.Count * 2, $"{stormy.Count} after a storm");
        Assert.NotEqual(calm, Coast.Flotsam(40, 40, Biome.Coast, tiles, 20_001, false));
        Assert.Empty(Coast.Flotsam(40, 40, Biome.Lakeshore, PlotLayout.Generate(40, 40, Biome.Lakeshore), 20_000, true));
        Assert.All(calm, f => Assert.Equal((byte)TileObject.None, tiles[f.Y * PlotLayout.Size + f.X]));
        Assert.Contains(Coast.Find(99, false), f => f.Item == Items.Amber);
        Assert.DoesNotContain(Coast.Find(98, false), f => f.Item == Items.Amber);
        Assert.Contains(Coast.Find(98, true), f => f.Item == Items.Amber);
    }

    [Fact]
    public void The_coastal_branch_of_the_tree_and_the_trader_cover_everything_new()
    {
        Assert.Equal(TechTree.Fishing, TechTree.ByItem[Items.FishingBoat].Id);
        Assert.Equal(TechTree.FishProcessing, TechTree.ByItem[Items.Cannery].Id);
        Assert.Equal(TechTree.Seafaring, TechTree.ByItem[Items.Port].Id);
        Assert.Equal(TechTree.Aquaculture, TechTree.ByItem[Items.FishFarm].Id);
        Assert.Equal(TechTree.TidalPower, TechTree.ByItem[Items.TidalPlant].Id);
        Assert.Equal(Items.Shipyard, Workshop.Find(Items.FishingBoat)!.Station);
        Assert.True(Processing.ByKind[Items.FishingBoat].Sea);
        Assert.Contains(Items.FishMeal, Husbandry.Feed);
        Assert.Equal(100, PowerRules.HydroKw(Items.TidalPlant, (int)Biome.Coast));
        Assert.True(Items.IsHydro(Items.TidalPlant));
        foreach (var item in new[]
        {
            Items.FishingRod, Items.Herring, Items.Cod, Items.Carp, Items.Pike, Items.Salmon, Items.Mussels, Items.Seaweed, Items.Amber,
            Items.SmokedFish, Items.SmokedSalmon, Items.SaltedHerring, Items.CannedFish, Items.FishMeal, Items.FishOil, Items.FriedCarp,
            Items.FishSoup, Items.FishingBoat, Items.RowingBoat, Items.Smokehouse, Items.Cannery, Items.SaltPan, Items.FishFarm,
            Items.Shipyard, Items.Port, Items.Breakwater, Items.Lighthouse, Items.TidalPlant,
        })
        {
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
            if (item.StartsWith("furniture_") || item.StartsWith("power_"))
            {
                Assert.Contains(item, PlotObjects.Placeable);
                Assert.NotNull(Workshop.Find(item));
            }
        }

        Assert.Equal(1, SeaRules.FeeCents(4));
        Assert.Equal(TimeSpan.FromSeconds(30), SeaRules.TripTime(TimeSpan.FromSeconds(20), true));
    }

    // --- On a plot -------------------------------------------------------------------------------

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        return client;
    }

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    /// <summary>A free plot of the biome no other test here takes, three plots from any other one taken, so boats of one test never crowd another's.</summary>
    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) * 7 + 300;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 4)
        {
            for (var x = 0; x < Map.Width; x += 4)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private static async Task<(int X, int Y, JsonElement Plot)> BuyAsync(GameClient client, Biome biome)
    {
        var (x, y) = FreePlot(biome);
        var bought = await client.SendAsync("world.buy", new { x, y });
        Assert.True(bought.Type() == "world.bought", bought.ToString());
        return (x, y, (await client.SendAsync("plot.enter", new { x, y })).Payload());
    }

    private static byte[] Tiles(JsonElement plot)
    {
        using var packed = new MemoryStream(Convert.FromBase64String(plot.GetProperty("tiles").GetString()!));
        using var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return tiles.ToArray();
    }

    /// <summary>Free shore tiles of the plot as it is now, next to the water, each a few tiles from the last.</summary>
    private static List<(int X, int Y)> ShoreTiles(byte[] tiles)
    {
        var found = new List<(int, int)>();
        for (var x = 5; x < PlotLayout.Size - 5; x += 4)
        {
            for (var y = 20; y < PlotLayout.Size; y++)
            {
                if (tiles[y * PlotLayout.Size + x] == (byte)TileObject.None && Coast.NextToWater(tiles, x, y))
                {
                    found.Add((x, y));
                    break;
                }
            }
        }

        return found;
    }

    private static (int X, int Y) WaterBeside(byte[] tiles, (int X, int Y) shore)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (tiles[(shore.Y + dy) * PlotLayout.Size + shore.X + dx] == (byte)TileObject.Water)
                {
                    return (shore.X + dx, shore.Y + dy);
                }
            }
        }

        throw new InvalidOperationException("No water there.");
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation = 0 });
        Assert.True(reply.Type() == "plot.placed", reply.ToString());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static async Task<string?> PlaceErrorAsync(GameClient client, int x, int y, string kind, int tileX, int tileY) =>
        (await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation = 0 })).ErrorCode();

    /// <summary>Moves the clock to a calm moment with at least <paramref name="calm"/> before the next storm.</summary>
    private void Calm(TimeSpan calm)
    {
        while (SeaWeather.Next(factory.Time.GetUtcNow()) is var (start, end) && (SeaWeather.IsStorm(factory.Time.GetUtcNow()) || start - factory.Time.GetUtcNow() < calm))
        {
            factory.Time.Advance(end - factory.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task A_rod_catches_fish_from_the_sea_which_never_runs_dry_but_nobody_fishes_in_a_storm_without_a_breakwater()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Coast);
        Assert.Equal(30, plot.GetProperty("fishingBonus").GetInt32());
        Assert.True(plot.GetProperty("storm").GetProperty("end").GetInt64() > plot.GetProperty("storm").GetProperty("start").GetInt64());
        var tiles = Tiles(plot);
        var shore = ShoreTiles(tiles)[3];
        var (wx, wy) = WaterBeside(tiles, shore);
        Calm(TimeSpan.FromMinutes(5));

        for (var i = 0; i < 2; i++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(5));
            var caught = await client.SendAsync("plot.harvest", new { x, y, tileX = wx, tileY = wy });
            Assert.True(caught.Type() == "plot.harvested", caught.ToString());
            var fish = caught.Payload().GetProperty("items")[0].GetProperty("id").GetString();
            Assert.Contains(fish, new[] { Items.Herring, Items.Cod });
        }

        // The water is still there.
        Assert.Equal((byte)TileObject.Water, Tiles((await client.SendAsync("plot.enter", new { x, y })).Payload())[wy * PlotLayout.Size + wx]);

        var (start, _) = SeaWeather.Next(factory.Time.GetUtcNow());
        factory.Time.Advance(start - factory.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        Assert.Equal("storm", (await client.SendAsync("plot.harvest", new { x, y, tileX = wx, tileY = wy })).ErrorCode());

        await GiveAsync(client, Items.Breakwater, 1);
        var other = ShoreTiles(tiles)[6];
        await PlaceAsync(client, x, y, Items.Breakwater, other.X, other.Y);
        factory.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", new { x, y, tileX = wx, tileY = wy })).Type());
    }

    [Fact]
    public async Task A_boat_on_the_shore_fishes_by_itself_and_stays_in_port_in_a_storm()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Coast);
        var tiles = Tiles(plot);
        var shore = ShoreTiles(tiles);
        await GiveAsync(client, Items.FishingBoat, 6);
        await GiveAsync(client, Items.RowingBoat, 1);
        Assert.Equal("not_on_shore", await PlaceErrorAsync(client, x, y, Items.FishingBoat, PlotLayout.Spawn.X + 3, PlotLayout.Spawn.Y + 8));
        Assert.Equal("no_lake", await PlaceErrorAsync(client, x, y, Items.RowingBoat, shore[0].X, shore[0].Y));

        Calm(TimeSpan.FromMinutes(10));
        var boat = await PlaceAsync(client, x, y, Items.FishingBoat, shore[0].X, shore[0].Y);
        var set = await client.SendAsync("machine.recipe", new { machine = boat, recipe = Items.Herring });
        Assert.True(set.Type() == "machine", set.ToString());
        factory.Time.Advance(TimeSpan.FromSeconds(12 * 3 + 1));
        var state = (await client.SendAsync("machine.get", new { machine = boat })).Payload();
        Assert.Equal(9, state.GetProperty("output").GetProperty("quantity").GetInt32());

        // In a storm it catches nothing.
        var (start, end) = SeaWeather.Next(factory.Time.GetUtcNow());
        factory.Time.Advance(start - factory.Time.GetUtcNow() + TimeSpan.FromSeconds(1));
        var atStorm = (await client.SendAsync("machine.get", new { machine = boat })).Payload().GetProperty("output").GetProperty("quantity").GetInt32();
        factory.Time.Advance(TimeSpan.FromMinutes(20));
        var during = (await client.SendAsync("machine.get", new { machine = boat })).Payload();
        Assert.Equal(atStorm, during.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(0, during.GetProperty("speed").GetDouble());

        var taken = (await client.SendAsync("machine.take", new { machine = boat, what = "output" })).Payload();
        Assert.Equal(atStorm, (await client.InventoryAsync()).GetValueOrDefault(Items.Herring));
        Assert.NotEmpty(taken.GetProperty("taken").EnumerateArray());

        // A plot has room for four boats. The storm may have thrown finds up on the beach, so the shore is looked at again.
        var free = ShoreTiles(Tiles((await client.SendAsync("plot.enter", new { x, y })).Payload())).Where(t => t != shore[0]).ToList();
        for (var i = 1; i < Coast.PlotBoats; i++)
        {
            await PlaceAsync(client, x, y, Items.FishingBoat, free[i].X, free[i].Y);
        }

        Assert.Equal("too_many_boats", await PlaceErrorAsync(client, x, y, Items.FishingBoat, free[5].X, free[5].Y));
    }

    [Fact]
    public async Task The_sea_throws_finds_up_on_the_beach_which_are_picked_up_once_a_day()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Coast);
        var tiles = Tiles(plot);
        var finds = Enumerable.Range(0, tiles.Length).Where(i => tiles[i] == (byte)TileObject.Flotsam).ToList();
        Assert.NotEmpty(finds);
        var (tx, ty) = (finds[0] % PlotLayout.Size, finds[0] / PlotLayout.Size);
        factory.Time.Advance(TimeSpan.FromSeconds(2));
        var picked = await client.SendAsync("plot.harvest", new { x, y, tileX = tx, tileY = ty });
        Assert.True(picked.Type() == "plot.harvested", picked.ToString());
        Assert.NotEmpty(picked.Payload().GetProperty("items").EnumerateArray());
        factory.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("nothing_here", (await client.SendAsync("plot.harvest", new { x, y, tileX = tx, tileY = ty })).ErrorCode());
        var after = Tiles((await client.SendAsync("plot.enter", new { x, y })).Payload());
        Assert.Equal((byte)TileObject.None, after[ty * PlotLayout.Size + tx]);
    }

    [Fact]
    public async Task Ships_sail_between_ports_for_a_quarter_of_the_rail_fee_and_take_twice_as_long()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Coast);
        var (tx, ty, other) = await BuyAsync(client, Biome.Coast);
        await GiveAsync(client, Items.Port, 2);
        await GiveAsync(client, Items.Wood, 400);
        Assert.Equal("station", (await client.SendAsync("station.load", new { x, y, item = Items.Wood, quantity = 400 })).Type());
        Assert.Equal("no_port", (await client.SendAsync("station.send", new { x, y, toX = tx, toY = ty, item = Items.Wood, quantity = 100, sea = true })).ErrorCode());

        var here = ShoreTiles(Tiles(plot))[2];
        var there = ShoreTiles(Tiles(other))[2];
        await PlaceAsync(client, x, y, Items.Port, here.X, here.Y);
        await PlaceAsync(client, tx, ty, Items.Port, there.X, there.Y);

        var km = RailRules.DistanceKm(Map, x, y, tx, ty);
        var before = await client.BalanceAsync();
        var shipped = await client.SendAsync("station.send", new { x, y, toX = tx, toY = ty, item = Items.Wood, quantity = 100, sea = true });
        Assert.True(shipped.Type() == "station", shipped.ToString());
        Assert.Equal(SeaRules.FeeCents(RailRules.FeeCents(km, 100)), before - await client.BalanceAsync());
        var trip = shipped.Payload().GetProperty("outgoing")[0];
        var arrives = DateTimeOffset.FromUnixTimeMilliseconds(trip.GetProperty("arrivesAt").GetInt64());
        Assert.Equal(SeaRules.TripTime(RailRules.TripTime(km), false).TotalSeconds, (arrives - factory.Time.GetUtcNow()).TotalSeconds, 0);
    }

    [Fact]
    public async Task A_lakeshore_rowing_boat_catches_carp_and_pike()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Lakeshore);
        var shore = ShoreTiles(Tiles(plot));
        await GiveAsync(client, Items.RowingBoat, 1);
        await GiveAsync(client, Items.FishingBoat, 1);
        Assert.Equal("no_coast", await PlaceErrorAsync(client, x, y, Items.FishingBoat, shore[0].X, shore[0].Y));
        var boat = await PlaceAsync(client, x, y, Items.RowingBoat, shore[0].X, shore[0].Y);
        var set = (await client.SendAsync("machine.recipe", new { machine = boat, recipe = Items.Pike })).Payload();
        Assert.Contains(set.GetProperty("recipes").EnumerateArray(), r => r.GetProperty("id").GetString() == Items.Carp);
        Assert.Equal(Items.Pike, set.GetProperty("recipe").GetString());
    }
}
