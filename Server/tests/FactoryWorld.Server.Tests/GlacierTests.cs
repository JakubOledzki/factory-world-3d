using System.IO.Compression;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class GlacierTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly DateTimeOffset Dawn = SeaWeather.Epoch;
    private const int Ice = (int)Biome.Ice;
    private static int _plots;

    private static PlotObject Obj(string kind, int x, int y) => new(Guid.NewGuid(), kind, x, y, 0, Guid.Empty);

    private static PowerGrid Grid(IReadOnlyList<PlotObject> objects, int biome, GridData? data = null, DateTimeOffset? at = null) =>
        new(0, 0, objects, data ?? new GridData(), at ?? Dawn, (_, _, _) => Task.CompletedTask, biome);

    /// <summary>A calm moment on the glacier with the wind blowing as strong as <paramref name="strength"/>.</summary>
    private static DateTimeOffset Blowing(double strength)
    {
        for (var at = Dawn; ; at += Wind.Window)
        {
            if (Wind.Strength(at) == strength && !Blizzard.IsStorm(at) && !Blizzard.IsStorm(at + Wind.Window))
            {
                return at;
            }
        }
    }

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void The_wind_changes_every_ten_minutes_and_blizzards_blow_over_the_glacier_now_and_then()
    {
        var strengths = Enumerable.Range(0, 1000).Select(i => Wind.Strength(Dawn + Wind.Window * i)).ToList();
        Assert.All(strengths, s => Assert.InRange(s, 0.1, 1));
        Assert.InRange(strengths.Distinct().Count(), 5, 7);
        Assert.InRange(strengths.Average(), 0.55, 0.8);
        Assert.Equal(Wind.Strength(Dawn), Wind.Strength(Dawn + TimeSpan.FromMinutes(9)));
        Assert.Equal(Dawn + Wind.Window, Wind.NextChange(Dawn + TimeSpan.FromMinutes(3)));
        Assert.Equal(1.2, Wind.Bonus(Ice));
        Assert.Equal(1.15, Wind.Bonus((int)Biome.Tundra));
        Assert.Equal(1, Wind.Bonus((int)Biome.Forest));

        var stormy = Enumerable.Range(0, 2000).Count(w => Blizzard.Stormy(w));
        Assert.InRange(stormy, 260, 460);
        var (start, end) = Blizzard.Next(Dawn);
        Assert.Equal(Blizzard.Length, end - start);
        Assert.True(Blizzard.IsStorm(start));
        Assert.Equal(end, Blizzard.LastEnded(end + TimeSpan.FromMinutes(1))!.Value.End);
        Assert.Equal(start, Blizzard.NextChange(start - TimeSpan.FromMinutes(1)));

        // Avalanches leave snow on about one tile in seventy, different ones each blizzard.
        var drifts = Enumerable.Range(0, 100).SelectMany(ty => Enumerable.Range(0, 100).Select(tx => Blizzard.Drift(7, 10, 20, tx, ty))).Count(d => d);
        Assert.InRange(drifts, 100, 200);
        Assert.NotEqual(
            Enumerable.Range(0, 400).Select(t => Blizzard.Drift(7, 10, 20, t, 0)),
            Enumerable.Range(0, 400).Select(t => Blizzard.Drift(8, 10, 20, t, 0)));
    }

    [Fact]
    public void Wind_turbines_give_as_much_as_the_wind_blows_more_on_the_glacier_and_nothing_in_a_blizzard()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var turbine = Obj(Items.WindTurbine, 1, 0);
        var mill = Obj(Items.Mill, -1, 0);
        var full = Blowing(1);
        var grid = Grid([pole, turbine, mill], Ice, at: full);
        Assert.Equal(48, grid.WindKw(turbine.Id, full), 6);
        Assert.Equal(1, grid.Speed(grid.Networks[0], full));
        // Powered well, but with no heater near it the mill works at half pace in the cold.
        Assert.Equal(Frost.Speed, grid.Speed(mill.Id, full));
        Assert.Equal(30, grid.Output(turbine.Id, full), 6);
        // The network changes when the wind does.
        Assert.Equal(Wind.NextChange(full), grid.NextChange(grid.Networks[0], full));

        var weak = Blowing(0.3);
        var calm = Grid([pole, turbine, mill], (int)Biome.Forest, at: weak);
        Assert.Equal(12, calm.WindKw(turbine.Id, weak), 6);
        Assert.Equal(0.4, calm.Speed(mill.Id, weak), 6);

        var (storm, _) = Blizzard.Next(Dawn);
        var blizzard = Grid([pole, turbine], Ice, at: storm);
        Assert.True(blizzard.Blizzard(storm));
        Assert.Equal(0, blizzard.WindKw(turbine.Id, storm));
        // Off the glacier no blizzard blows.
        Assert.False(Grid([pole, turbine], (int)Biome.Tundra, at: storm).Blizzard(storm));

        Assert.Equal(400 * 1.2, PowerRules.WindKw(Items.LargeWindTurbine, Ice), 6);
        Assert.Equal(1, Footprints.Reach(Items.LargeWindTurbine));
        Assert.True(Items.IsRenewable(Items.WindTurbine));
    }

    [Fact]
    public void On_the_glacier_machines_work_at_half_pace_unless_a_burning_heater_warms_them()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var generator = Obj(Items.Generator, 1, 0);
        var mill = Obj(Items.Mill, -1, 0);
        var far = Obj(Items.Crusher, 2, 2);
        var heater = Obj(Items.Heater, -3, 0);
        var data = new GridData
        {
            Generators =
            {
                [generator.Id] = new GeneratorState(Items.Coal, 50, 0),
                // One piece of wood: 5000 kJ at 15 kW lasts 333 s.
                [heater.Id] = new GeneratorState(Items.Wood, 1, 0),
            },
        };
        var grid = Grid([pole, generator, mill, far, heater], Ice, data);
        var warmFor = TimeSpan.FromSeconds(5000 / Frost.HeaterKw);

        Assert.Equal(1, grid.Speed(mill.Id, Dawn + TimeSpan.FromSeconds(10)));
        Assert.True(grid.Cold(far.Id, Dawn));
        Assert.Equal(Frost.Speed, grid.Speed(far.Id, Dawn + TimeSpan.FromSeconds(10)));
        Assert.Equal(Dawn + warmFor, grid.RunsOutAt(heater.Id)!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Frost.Speed, grid.Speed(mill.Id, Dawn + TimeSpan.FromSeconds(400)));

        // After 1000 s the mill lost half of the 667 s it was cold, the crusher half of all of it.
        Assert.Equal((1000 - warmFor.TotalSeconds) * (1 - Frost.Speed), TimeSpan.FromTicks(grid.Lag(mill.Id, Dawn + TimeSpan.FromSeconds(1000))).TotalSeconds, 2);
        Assert.Equal(500, TimeSpan.FromTicks(grid.Lag(far.Id, Dawn + TimeSpan.FromSeconds(1000))).TotalSeconds, 2);

        // 600 s of the mill's own time: 333 s warm, then 267 s more at half pace.
        var due = grid.Clock(mill.Id).When(Dawn + TimeSpan.FromSeconds(600), Dawn)!.Value;
        Assert.Equal(Dawn + warmFor + 2 * (TimeSpan.FromSeconds(600) - warmFor), due, TimeSpan.FromSeconds(0.01));
        Assert.Contains(far.Id, grid.ColdMachines(Dawn));
        Assert.DoesNotContain(mill.Id, grid.ColdMachines(Dawn));

        // Settled, the heater keeps what it has not burnt and the machines what they lost.
        grid.Settle(Dawn + TimeSpan.FromSeconds(100));
        Assert.Equal(5000 - 1500, grid.Data.Generators[heater.Id].Total, 3);
        Assert.Equal(50, TimeSpan.FromTicks(grid.Data.Lags[far.Id]).TotalSeconds, 2);

        // Off the glacier nothing freezes.
        var forest = Grid([pole, generator, far], (int)Biome.Forest, new GridData { Generators = { [generator.Id] = new GeneratorState(Items.Coal, 50, 0) } });
        Assert.Equal(1, forest.Speed(far.Id, Dawn + TimeSpan.FromSeconds(10)));
        Assert.True(PowerRules.Burns(Items.Heater, Items.WoodPellets));
        Assert.False(PowerRules.Burns(Items.Heater, Items.NaturalGas));
    }

    [Fact]
    public void The_glacier_has_ice_meteorites_and_crevasses_and_ice_cools_machines()
    {
        var (x, y) = FreePlot(Biome.Ice);
        var tiles = PlotLayout.Generate(x, y, Biome.Ice);
        Assert.True(tiles.Count(t => t == (byte)TileObject.IceBlock) > tiles.Count(t => t == (byte)TileObject.Rock));
        Assert.Contains((byte)TileObject.Crevasse, tiles);
        Assert.Contains((byte)TileObject.Meteorite, tiles);
        Assert.Equal(Items.Pickaxe, PlotLayout.StarterTool(Biome.Ice));

        Assert.Equal([(Items.Ice, 2)], PlotLayout.Yield(TileObject.IceBlock, 50, Biome.Ice));
        Assert.Equal([(Items.Ice, 1)], PlotLayout.Yield(TileObject.Snowdrift, 50, Biome.Ice));
        Assert.Equal([(Items.MeteoricIron, 1), (Items.Iridium, 1)], PlotLayout.Yield(TileObject.Meteorite, 2, Biome.Ice));
        Assert.Equal([(Items.MeteoricIron, 1)], PlotLayout.Yield(TileObject.Meteorite, 10, Biome.Ice));
        Assert.Equal(2 * HarvestRules.TimeFor(TileObject.Rock, Items.Pickaxe, Biome.Ice), HarvestRules.TimeFor(TileObject.Meteorite, Items.Pickaxe, Biome.Ice));
        Assert.Equal(2, Items.Pickaxes[Items.MeteoritePickaxe]);

        // A cold store and a server room cool themselves with a block of ice per batch off the glacier.
        Assert.Same(Processing.ByKind[Items.ColdStore], Processing.For(Items.ColdStore, Ice));
        Assert.Equal([(Items.Cod, 2), (Items.Ice, 1)], Processing.For(Items.ColdStore, (int)Biome.Coast)!.Recipe(Items.FrozenFish)!.Ingredients);
        Assert.Equal([(Items.Ice, 1)], Processing.For(Items.ServerRoom, (int)Biome.City)!.Recipe(Items.ComputingPower)!.Ingredients);
        var iced = new MachineState(Items.FrozenFish, [4, 2], null, 0, 0, 0, null);
        Assert.Equal(2, Processing.ForState(Items.ColdStore, iced)!.Recipe(Items.FrozenFish)!.Ingredients.Count);
        Assert.Single(Processing.ForState(Items.ColdStore, iced with { Inputs = [4] })!.Recipe(Items.FrozenFish)!.Ingredients);
        // The glacier's cold air cools a refinery.
        Assert.Equal(TimeSpan.FromSeconds(8) / 1.2, Processing.For(Items.Refinery, Ice)!.Recipe(Items.Gasoline)!.Time);
        Assert.True(PowerRules.Demand.ContainsKey(Items.ServerRoom));
    }

    [Fact]
    public void Tourist_buildings_run_three_times_as_fast_while_the_aurora_shines()
    {
        var nights = Enumerable.Range(0, 1000).Count(d => Aurora.Shines(d));
        Assert.InRange(nights, 300, 500);
        var (start, end) = Aurora.Next(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(Sun.DayLength - Sun.Daylight, end - start);
        Assert.False(Sun.IsDay(start));
        Assert.True(Aurora.IsShining(start + TimeSpan.FromMinutes(1)));

        var clock = AuroraClock.Instance;
        Assert.Equal(TimeSpan.FromMinutes(30), clock.At(start + TimeSpan.FromMinutes(10)) - clock.At(start));
        Assert.Equal(TimeSpan.FromMinutes(1), clock.At(start) - clock.At(start - TimeSpan.FromMinutes(1)));
        Assert.Equal(Aurora.Rush, clock.Speed(start));
        var from = start - TimeSpan.FromMinutes(5);
        var due = clock.When(clock.At(from) + TimeSpan.FromMinutes(35), from)!.Value;
        Assert.Equal(start + TimeSpan.FromMinutes(10), due, TimeSpan.FromMilliseconds(1));
        Assert.True(Aurora.Seen((int)Biome.Tundra));
        Assert.False(Aurora.Seen((int)Biome.Desert));
    }

    [Fact]
    public void Every_glacier_machine_and_product_has_a_recipe_a_technology_and_a_price()
    {
        foreach (var item in new[]
                 {
                     Items.WindTurbine, Items.LargeWindTurbine, Items.Heater, Items.IceMelter, Items.ColdStore, Items.ServerRoom, Items.IceCoreDrill,
                     Items.PolarStation, Items.IceHotel, Items.Viewpoint, Items.DogKennel, Items.SnowmobileGarage, Items.TrackFoundation,
                 })
        {
            Assert.NotNull(Workshop.Find(item));
            Assert.Contains(TechTree.Technologies, t => t.Unlocks.Contains(item));
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
            Assert.Contains(item, PlotObjects.Placeable);
        }

        foreach (var item in new[]
                 {
                     Items.FrozenFish, Items.FrozenSalmon, Items.IceCream, Items.ComputingPower, Items.IceCore, Items.ResearchData, Items.Water,
                 })
        {
            Assert.Contains(Processing.Machines, m => m.Recipe(item) is not null);
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
        }

        Assert.NotNull(Workshop.Find(Items.MeteoritePickaxe));
        Assert.All([Items.Ice, Items.MeteoricIron, Items.Iridium], i => Assert.True(NpcPrices.Buys.ContainsKey(i), i));
        Assert.False(NpcPrices.Buys.ContainsKey(Items.Takings));
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

    private async Task SqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using var command = await db.CommandAsync(sql, CancellationToken.None);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) * 3 + 300;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 2)
        {
            for (var x = 0; x < Map.Width; x += 2)
            {
                if (Map.BiomeAt(x, y) == biome && Map.BiomeAt(x + 1, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private static async Task<(int X, int Y, byte[] Tiles)> BuyAsync(GameClient client, Biome biome, (int X, int Y)? at = null)
    {
        var (x, y) = at ?? FreePlot(biome);
        var bought = await client.SendAsync("world.buy", new { x, y });
        Assert.True(bought.Type() == "world.bought", bought.ToString());
        return (x, y, await TilesAsync(client, x, y));
    }

    private static async Task<byte[]> TilesAsync(GameClient client, int x, int y)
    {
        var plot = (await client.SendAsync("plot.enter", new { x, y })).Payload();
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return tiles.ToArray();
    }

    private static int First(byte[] tiles, TileObject obj) =>
        Enumerable.Range(PlotLayout.Size * 20, tiles.Length - PlotLayout.Size * 22).First(i => tiles[i] == (byte)obj);

    private static object At(int x, int y, int i) => new { x, y, tileX = i % PlotLayout.Size, tileY = i / PlotLayout.Size };

    [Fact]
    public async Task Ice_is_cut_with_a_pickaxe_and_freezes_again_after_a_blizzard_and_a_crevasse_takes_a_bridge_of_beams()
    {
        using var client = await PlayerAsync();
        var (x, y, tiles) = await BuyAsync(client, Biome.Ice);

        var ice = First(tiles, TileObject.IceBlock);
        factory.Time.Advance(HarvestRules.Time);
        var cut = await client.SendAsync("plot.harvest", At(x, y, ice));
        Assert.True(cut.Type() == "plot.harvested", cut.ToString());
        Assert.Equal(2, (await client.InventoryAsync())[Items.Ice]);

        // Cut long before the last blizzard, it froze again.
        await SqlAsync("UPDATE world.plot_harvests SET harvested_at = ARRAY(SELECT CASE WHEN a IS NOT NULL THEN @long END FROM unnest(harvested_at) WITH ORDINALITY AS u(a, i) ORDER BY i) WHERE x = @x AND y = @y",
            ("long", factory.Time.GetUtcNow() - TimeSpan.FromDays(3)), ("x", x), ("y", y));
        Assert.Equal((byte)TileObject.IceBlock, (await TilesAsync(client, x, y))[ice]);

        // Nothing stands on a crevasse until three beams bridge it.
        var crevasse = First(tiles, TileObject.Crevasse);
        await GiveAsync(client, Items.Pole, 1);
        Assert.Equal("blocked", (await client.SendAsync("plot.place",
            new { x, y, kind = Items.Pole, tileX = crevasse % PlotLayout.Size, tileY = crevasse / PlotLayout.Size, rotation = 0 })).ErrorCode());
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("not_enough_items", (await client.SendAsync("plot.harvest", At(x, y, crevasse))).ErrorCode());
        await GiveAsync(client, Items.Beams, PlotLayout.BridgeBeams);
        factory.Time.Advance(HarvestRules.Time);
        var bridged = (await client.SendAsync("plot.harvest", At(x, y, crevasse))).Payload();
        Assert.Equal("bridge", bridged.GetProperty("object").GetString());
        Assert.Equal((byte)TileObject.Bridge, (await TilesAsync(client, x, y))[crevasse]);
        var placed = await client.SendAsync("plot.place",
            new { x, y, kind = Items.Pole, tileX = crevasse % PlotLayout.Size, tileY = crevasse / PlotLayout.Size, rotation = 0 });
        Assert.True(placed.Type() == "plot.placed", placed.ToString());
    }

    [Fact]
    public async Task An_avalanche_buries_what_stands_on_the_ice_until_it_is_dug_out_with_a_shovel()
    {
        using var client = await PlayerAsync();
        var (x, y, tiles) = await BuyAsync(client, Biome.Ice);
        // A free tile the next blizzard's avalanches will cover.
        var (start, end) = Blizzard.Next(factory.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        var window = (long)Math.Floor((start - SeaWeather.Epoch).Ticks / (double)Blizzard.Window.Ticks);
        var spot = Enumerable.Range(PlotLayout.Size * 20, tiles.Length - PlotLayout.Size * 22)
            .First(i => tiles[i] == (byte)TileObject.None && Blizzard.Drift(window, x, y, i % PlotLayout.Size, i / PlotLayout.Size));
        await GiveAsync(client, Items.WoodenChest, 1);
        var chest = (await client.SendAsync("plot.place",
            new { x, y, kind = Items.WoodenChest, tileX = spot % PlotLayout.Size, tileY = spot / PlotLayout.Size, rotation = 0 })).Payload()
            .GetProperty("object").GetProperty("id").GetString();

        factory.Time.Advance(end - factory.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        Assert.Equal((byte)TileObject.Snowdrift, (await TilesAsync(client, x, y))[spot]);
        Assert.Equal("buried", (await client.SendAsync("plot.pickup", new { x, y, id = chest })).ErrorCode());

        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("wrong_tool", (await client.SendAsync("plot.harvest", At(x, y, spot))).ErrorCode());
        await GiveAsync(client, Items.Shovel, 1);
        await client.SendAsync("character.equip", new { item = Items.Shovel });
        factory.Time.Advance(HarvestRules.Time);
        var dug = await client.SendAsync("plot.harvest", At(x, y, spot));
        Assert.True(dug.Type() == "plot.harvested", dug.ToString());
        Assert.Equal((byte)TileObject.None, (await TilesAsync(client, x, y))[spot]);
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = chest })).Type());
    }

    [Fact]
    public async Task A_hotel_on_the_ice_pays_its_takings_into_the_wallet_and_a_heater_burns_without_a_pole()
    {
        using var client = await PlayerAsync();
        var (x, y, tiles) = await BuyAsync(client, Biome.Ice);
        await GiveAsync(client, Items.IceHotel, 1);
        await GiveAsync(client, Items.Heater, 1);
        await GiveAsync(client, Items.Wood, 10);
        var clear = Enumerable.Range(PlotLayout.Size * 20, tiles.Length - PlotLayout.Size * 22)
            .First(i => i % PlotLayout.Size is > 2 and < PlotLayout.Size - 6
                && Enumerable.Range(-1, 3).All(dy => Enumerable.Range(-1, 6).All(dx => tiles[i + dy * PlotLayout.Size + dx] == 0)));
        var (tx, ty) = (clear % PlotLayout.Size, clear / PlotLayout.Size);
        var hotel = (await client.SendAsync("plot.place", new { x, y, kind = Items.IceHotel, tileX = tx, tileY = ty, rotation = 0 })).Payload()
            .GetProperty("object").GetProperty("id").GetString();
        var heater = (await client.SendAsync("plot.place", new { x, y, kind = Items.Heater, tileX = tx + 3, tileY = ty, rotation = 0 })).Payload()
            .GetProperty("object").GetProperty("id").GetString();

        var warm = (await client.SendAsync("generator.load", new { x, y, id = heater, item = Items.Wood, quantity = 5 })).Payload();
        Assert.True(warm.GetProperty("heater").GetBoolean());
        Assert.Equal(25_000, warm.GetProperty("total").GetDouble(), 3);
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, warm.GetProperty("runsOutAt").ValueKind);

        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine = hotel, recipe = Items.Takings })).Type());
        Assert.Equal("machine", (await client.SendAsync("machine.load", new { machine = hotel, item = Items.Wood, quantity = 5 })).Type());
        factory.Time.Advance(TimeSpan.FromMinutes(6));
        var before = await client.BalanceAsync();
        var taken = (await client.SendAsync("machine.take", new { machine = hotel, what = "output" })).Payload();
        Assert.Equal(20, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(before + 20 * Processing.TakingsCents, await client.BalanceAsync());
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Takings));

        // A plot takes three attractions at most, hotels and viewpoints together.
        await GiveAsync(client, Items.Viewpoint, 3);
        var spots = Enumerable.Range(PlotLayout.Size * 30, tiles.Length - PlotLayout.Size * 32)
            .Where(i => i % 4 == 0 && i % PlotLayout.Size is > 2 and < PlotLayout.Size - 4
                && Enumerable.Range(0, 3).All(dy => Enumerable.Range(0, 3).All(dx => tiles[i + dy * PlotLayout.Size + dx] == 0)))
            .Where((_, n) => n % 8 == 0);
        var placed = 0;
        string? refused = null;
        foreach (var spot in spots)
        {
            var reply = await client.SendAsync("plot.place",
                new { x, y, kind = Items.Viewpoint, tileX = spot % PlotLayout.Size, tileY = spot / PlotLayout.Size, rotation = 0 });
            if (reply.Type() == "plot.placed")
            {
                placed++;
            }
            else if (reply.ErrorCode() != "blocked")
            {
                refused = reply.ErrorCode();
                break;
            }
        }

        Assert.Equal(PlotObjects.MaxTouristSpots - 1, placed);
        Assert.Equal("tourists_full", refused);

        // Tourists come to the glacier and the tundra only.
        var (fx, fy, forest) = await BuyAsync(client, Biome.Forest);
        await GiveAsync(client, Items.Viewpoint, 1);
        var free = First(forest, TileObject.None);
        Assert.Equal("no_polar", (await client.SendAsync("plot.place",
            new { x = fx, y = fy, kind = Items.Viewpoint, tileX = free % PlotLayout.Size, tileY = free / PlotLayout.Size, rotation = 0 })).ErrorCode());
    }

    [Fact]
    public async Task Research_data_and_computing_power_take_time_off_the_research_being_done()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.IronIngot, 15);
        await GiveAsync(client, Items.Stone, 30);
        await GiveAsync(client, Items.Coal, 20);
        await GiveAsync(client, Items.ResearchData, 6);
        Assert.Equal("no_research", (await client.SendAsync("research.boost", new { item = Items.ResearchData, quantity = 1 })).ErrorCode());
        var started = (await client.SendAsync("research.start", new { technology = TechTree.Heating })).Payload();
        long Finishes(System.Text.Json.JsonElement r) =>
            r.GetProperty("technologies").EnumerateArray().First(t => t.GetProperty("id").GetString() == TechTree.Heating).GetProperty("finishesAt").GetInt64();

        var boosted = (await client.SendAsync("research.boost", new { item = Items.ResearchData, quantity = 1 })).Payload();
        Assert.Equal(Finishes(started) - 60_000, Finishes(boosted));
        Assert.Equal("not_boost", (await client.SendAsync("research.boost", new { item = Items.Coal, quantity = 1 })).ErrorCode());

        // 30 s were left: one piece does it, the rest stays in the inventory.
        var done = (await client.SendAsync("research.boost", new { item = Items.ResearchData, quantity = 5 })).Payload();
        Assert.Equal("done", done.GetProperty("technologies").EnumerateArray().First(t => t.GetProperty("id").GetString() == TechTree.Heating)
            .GetProperty("status").GetString());
        Assert.Equal(4, (await client.InventoryAsync())[Items.ResearchData]);
    }

    [Fact]
    public async Task A_dog_sled_carries_goods_off_the_glacier_free_and_trains_crawl_on_ice_without_a_foundation()
    {
        using var client = await PlayerAsync();
        var (x, y, tiles) = await BuyAsync(client, Biome.Ice);
        var (nx, ny) = (x + 1, y);
        await BuyAsync(client, Biome.Ice, (nx, ny));
        await GiveAsync(client, Items.Ice, 30);
        Assert.Equal("station", (await client.SendAsync("station.load", new { x, y, item = Items.Ice, quantity = 30 })).Type());
        object Send(bool sled) => new { x, y, toX = nx, toY = ny, item = Items.Ice, quantity = 10, sled };
        Assert.Equal("no_sled", (await client.SendAsync("station.send", Send(true))).ErrorCode());

        var km = RailRules.DistanceKm(Map, x, y, nx, ny);
        var balance = await client.BalanceAsync();
        var train = (await client.SendAsync("station.send", Send(false))).Payload();
        var trip = RailRules.TripTime(km);
        long Arrives(System.Text.Json.JsonElement s) => s.GetProperty("outgoing").EnumerateArray().Max(o => o.GetProperty("arrivesAt").GetInt64());
        Assert.InRange(Arrives(train) - (factory.Time.GetUtcNow() + trip * SledRules.IceTrack).ToUnixTimeMilliseconds(), -2, 2);
        Assert.True(await client.BalanceAsync() < balance);

        await GiveAsync(client, Items.DogKennel, 1);
        var free = First(tiles, TileObject.None);
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place",
            new { x, y, kind = Items.DogKennel, tileX = free % PlotLayout.Size, tileY = free / PlotLayout.Size, rotation = 0 })).Type());
        balance = await client.BalanceAsync();
        var sled = (await client.SendAsync("station.send", Send(true))).Payload();
        Assert.Equal(balance, await client.BalanceAsync());
        Assert.Contains(sled.GetProperty("outgoing").EnumerateArray(),
            o => Math.Abs(o.GetProperty("arrivesAt").GetInt64() - (factory.Time.GetUtcNow() + trip * SledRules.DogTrip).ToUnixTimeMilliseconds()) <= 2);
    }

    [Fact]
    public async Task The_trader_sells_the_meteorite_pickaxe_only_once_meteorite_tools_are_researched()
    {
        Assert.Equal(TechTree.MeteoriteTools, TechTree.ByItem[Items.MeteoritePickaxe].Id);
        Assert.Equal("mining", TechTree.BranchOf[TechTree.MeteoriteTools].Id);
        Assert.Contains(TechTree.DeepMining, TechTree.ById[TechTree.MeteoriteTools].Requires);

        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 10_000_000, "test", CancellationToken.None);
        async Task<bool> OfferedAsync() => (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .Any(i => i.GetProperty("id").GetString() == Items.MeteoritePickaxe && i.GetProperty("buyCents").ValueKind == System.Text.Json.JsonValueKind.Number);

        Assert.False(await OfferedAsync());
        Assert.Equal("locked", (await client.SendAsync("market.buy", new { item = Items.MeteoritePickaxe, quantity = 1 })).ErrorCode());

        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await using (var command = await db.CommandAsync(
            "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None))
        {
            command.Parameters.AddWithValue("owner", client.AccountId);
            command.Parameters.AddWithValue("id", TechTree.MeteoriteTools);
            command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.True(await OfferedAsync());
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.MeteoritePickaxe, quantity = 1 })).Type());
    }
}
