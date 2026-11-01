using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class DesertTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly DateTimeOffset Dawn = SeaWeather.Epoch;
    private static int _plots;

    private static PlotObject Obj(string kind, int x, int y) => new(Guid.NewGuid(), kind, x, y, 0, Guid.Empty);

    private static PowerGrid Grid(IReadOnlyList<PlotObject> objects, int biome, GridData? data = null, DateTimeOffset? at = null) =>
        new(0, 0, objects, data ?? new GridData(), at ?? Dawn, (_, _, _) => Task.CompletedTask, biome);

    /// <summary>A moment in the daylight of a sandstorm.</summary>
    private static DateTimeOffset StormyDay()
    {
        for (var at = Dawn; ; at += SandWeather.Window)
        {
            var (start, _) = SandWeather.Next(at);
            if (Sun.IsDay(start + TimeSpan.FromMinutes(5)))
            {
                return start + TimeSpan.FromMinutes(5);
            }
        }
    }

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void The_sun_shines_for_the_first_36_minutes_of_each_hour_and_sandstorms_blow_now_and_then()
    {
        Assert.True(Sun.IsDay(Dawn));
        Assert.False(Sun.IsDay(Dawn + TimeSpan.FromMinutes(40)));
        Assert.Equal(Dawn + Sun.Daylight, Sun.NextChange(Dawn));
        Assert.Equal(Dawn + Sun.DayLength, Sun.NextChange(Dawn + TimeSpan.FromMinutes(40)));

        var stormy = Enumerable.Range(0, 2000).Count(w => SandWeather.Stormy(w));
        Assert.InRange(stormy, 200, 400);
        var (start, end) = SandWeather.Next(Dawn);
        Assert.Equal(SandWeather.Length, end - start);
        Assert.True(SandWeather.IsStorm(start + TimeSpan.FromMinutes(1)));
        Assert.False(SandWeather.IsStorm(end));
        Assert.Equal(end, SandWeather.LastEnded(end + TimeSpan.FromMinutes(1)));
        Assert.Equal(start, SandWeather.NextChange(start - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_few_desert_plots_are_oases_dearer_and_fertile_where_camels_live()
    {
        var desert = Enumerable.Range(0, Map.Height).SelectMany(y => Enumerable.Range(0, Map.Width).Select(x => (x, y)))
            .Where(p => Map.BiomeAt(p.x, p.y) is Biome.Desert or Biome.Oasis).ToList();
        var oases = desert.Count(p => Map.BiomeAt(p.x, p.y) == Biome.Oasis);
        Assert.InRange(oases * 100.0 / desert.Count, 3, 5);
        Assert.Equal(1.5, Biomes.All[Biome.Oasis].PriceFactor);
        Assert.True(Farming.Fertile(Biome.Oasis));
        Assert.False(Farming.Fertile(Biome.Desert));

        // A pond in the middle, date palms around it.
        var oasis = desert.First(p => Map.BiomeAt(p.x, p.y) == Biome.Oasis);
        var tiles = PlotLayout.Generate(oasis.x, oasis.y, Biome.Oasis);
        Assert.Contains((byte)TileObject.Water, tiles);
        Assert.Contains((byte)TileObject.Tree, tiles);
        Assert.Equal([(Items.Wood, 1), (Items.Dates, 2)], PlotLayout.Yield(TileObject.Tree, 0, Biome.Oasis));

        var camel = Husbandry.ById[Items.Camel];
        Assert.True(Husbandry.Lives(camel, Biome.Desert));
        Assert.True(Husbandry.Lives(camel, Biome.Oasis));
        Assert.False(Husbandry.Lives(camel, Biome.Grassland));
        Assert.False(Husbandry.Lives(Husbandry.ById[Items.Cow], Biome.Desert));
        Assert.True(Husbandry.Pasture(Biome.Desert));
        Assert.Equal([(Items.Milk, 2), (Items.Wool, 1)], camel.Gives);
    }

    [Fact]
    public void Dunes_on_sand_give_more_sand_and_sometimes_clay_or_a_fossil()
    {
        Assert.Equal([(Items.Sand, 3)], PlotLayout.Yield(TileObject.Dune, 50, Biome.Coast));
        Assert.Equal([(Items.Sand, 4)], PlotLayout.Yield(TileObject.Dune, 50, Biome.Desert));
        Assert.Equal([(Items.Sand, 4), (Items.Clay, 1)], PlotLayout.Yield(TileObject.Dune, 5, Biome.Oasis));
        Assert.Equal([(Items.Sand, 4), (Items.Ammonite, 1)], PlotLayout.Yield(TileObject.Dune, 50, Biome.Desert, 1));
        Assert.Equal([(Items.Sand, 4), (Items.AncientCoin, 1)], PlotLayout.Yield(TileObject.Dune, 50, Biome.Desert, 2));
        Assert.Equal([(Items.Sand, 4), (Items.DinosaurBone, 1)], PlotLayout.Yield(TileObject.Dune, 50, Biome.Desert, 3));
        Assert.All([Items.Ammonite, Items.AncientCoin, Items.DinosaurBone], f => Assert.True(NpcPrices.Buys[f] >= 20_000));
    }

    [Fact]
    public void Glass_goes_faster_on_sand_and_a_deep_well_draws_slower_in_the_desert_and_faster_in_an_oasis()
    {
        Assert.Equal(TimeSpan.FromSeconds(10) / 1.3, Processing.For(Items.Glassworks, Oasis.Desert)!.Recipe(Items.Glass)!.Time);
        Assert.Equal(TimeSpan.FromSeconds(10), Processing.For(Items.Glassworks, 2)!.Recipe(Items.Glass)!.Time);
        Assert.Equal(TimeSpan.FromSeconds(6), Processing.For(Items.WaterWell, Oasis.Desert)!.Recipe(Items.Water)!.Time);
        Assert.Equal(TimeSpan.FromSeconds(1.5), Processing.For(Items.WaterWell, Oasis.Biome)!.Recipe(Items.Water)!.Time);
        Assert.Equal(TimeSpan.FromSeconds(20) / 1.15, Processing.For(Items.Smokehouse, (int)Biome.Coast)!.Recipe(Items.SmokedFish)!.Time);
        Assert.Same(Processing.ByKind[Items.Mill], Processing.For(Items.Mill, Oasis.Desert));
        Assert.Same(Processing.For(Items.Glassworks, Oasis.Biome), Processing.For(Items.Glassworks, Oasis.Desert));
        Assert.True(Items.IsFluid(Items.Water));
    }

    [Fact]
    public void Solar_panels_power_machines_by_day_and_charge_a_battery_that_carries_them_into_the_night()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var panels = new[] { Obj(Items.SolarPanel, 1, 0), Obj(Items.SolarPanel, -1, 0) };
        var battery = Obj(Items.Battery, 0, 1);
        var mill = Obj(Items.Mill, 1, 1);
        var grid = Grid([pole, .. panels, battery, mill], (int)Biome.Grassland);
        var dusk = Dawn + Sun.Daylight;

        // 40 kW of sun for a 30 kW mill: the 10 kW over goes into the battery.
        Assert.Equal(1, grid.Speed(mill.Id, Dawn + TimeSpan.FromMinutes(10)));
        Assert.Equal(-10, grid.Output(battery.Id, Dawn + TimeSpan.FromMinutes(10)), 6);
        Assert.Equal(20, grid.Output(panels[0].Id, Dawn + TimeSpan.FromMinutes(10)), 6);
        Assert.Equal(10 * Sun.Daylight.TotalSeconds, grid.Charge(battery.Id, dusk), 3);

        // At night the battery gives 30 kW for 720 s, then the mill stops until dawn.
        Assert.Equal(1, grid.Speed(mill.Id, dusk + TimeSpan.FromMinutes(10)));
        Assert.Equal(30, grid.Output(battery.Id, dusk + TimeSpan.FromMinutes(10)), 6);
        Assert.Equal(0, grid.Speed(mill.Id, dusk + TimeSpan.FromMinutes(13)));
        Assert.Equal(0, grid.Output(panels[0].Id, dusk + TimeSpan.FromMinutes(13)));
        Assert.Equal(720, TimeSpan.FromTicks(grid.Lag(mill.Id, Dawn + Sun.DayLength)).TotalSeconds, 2);

        // 3000 s of the mill's own time: 2160 by day, 720 on the battery and 120 more the next morning.
        var due = grid.Clock(mill.Id).When(Dawn + TimeSpan.FromSeconds(3000), Dawn)!.Value;
        Assert.Equal(Dawn + Sun.DayLength + TimeSpan.FromSeconds(120), due, TimeSpan.FromSeconds(0.01));

        // Settled at night, the battery keeps what it had left.
        grid.Settle(dusk + TimeSpan.FromMinutes(5));
        Assert.Equal(10 * Sun.Daylight.TotalSeconds - 30 * 300, grid.Data.Batteries[battery.Id], 3);
    }

    [Fact]
    public void The_desert_sun_is_brighter_but_a_sandstorm_dims_it_unless_a_reed_fence_shelters_the_panel()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var open = Obj(Items.SolarPanel, -2, 0);
        var sheltered = Obj(Items.SolarPanel, 2, 0);
        var fence = Obj(Items.ReedFence, 5, 0);
        var steam = Obj(Items.Generator, 0, 1);
        var grid = Grid([pole, open, sheltered, fence, steam], Oasis.Desert);
        var calm = Dawn + TimeSpan.FromMinutes(5);
        Assert.False(SandWeather.IsStorm(calm));
        Assert.Equal(25, grid.SolarKw(open.Id, calm), 6);
        var storm = StormyDay();
        Assert.True(grid.Storm(storm));
        Assert.Equal(25 * SandWeather.StormSun, grid.SolarKw(open.Id, storm), 6);
        Assert.Equal(25, grid.SolarKw(sheltered.Id, storm), 6);
        // A steam generator has no water to cool it on sand.
        Assert.Equal(70, grid.KwOf(steam.Id));
        Assert.Equal(100, Grid([pole, steam], 2).KwOf(steam.Id));
        // A solar tower covers 3 × 3 tiles.
        Assert.Equal(1, Footprints.Reach(Items.SolarTower));
    }

    [Fact]
    public void Every_desert_machine_and_product_has_a_recipe_a_technology_and_a_price()
    {
        foreach (var item in new[]
                 {
                     Items.Glassworks, Items.ArcFurnace, Items.WaterWell, Items.Desalinator, Items.DryingYard, Items.CementKiln, Items.ConcreteMixer,
                     Items.ReedFence, Items.SolarPanel, Items.SolarTower, Items.Battery,
                 })
        {
            Assert.NotNull(Workshop.Find(item));
            Assert.Contains(TechTree.Technologies, t => t.Unlocks.Contains(item));
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
            Assert.Contains(item, PlotObjects.Placeable);
        }

        foreach (var item in new[]
                 {
                     Items.Glass, Items.WindowPane, Items.GlassBottles, Items.OpticalFiber, Items.Silicon, Items.SiliconWafer, Items.Water,
                     Items.AdobeBricks, Items.Cement, Items.Concrete, Items.Mirror,
                 })
        {
            Assert.Contains(Processing.Machines, m => m.Recipe(item) is not null);
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
        }

        Assert.True(NpcPrices.Sells.ContainsKey(Items.Camel));
    }

    [Fact]
    public async Task A_player_buys_concrete_from_the_trader_before_researching_building_materials()
    {
        using var client = await PlayerAsync();
        // Concrete goes into track foundations, assembly halls and city orders long before a player builds a mixer.
        foreach (var item in new[] { Items.AdobeBricks, Items.Cement, Items.Concrete })
        {
            Assert.False(TechTree.ByItem.ContainsKey(item), item);
            Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item, quantity = 10 })).Type());
        }

        var prices = (await client.SendAsync("market.prices")).Payload().GetProperty("items").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(250_00, NpcPrices.Sells[Items.Concrete]);
        Assert.Equal(NpcPrices.Sells[Items.Concrete], prices[Items.Concrete].GetProperty("buyCents").GetInt64());
        // The machines still need the research.
        Assert.Equal("locked", (await client.SendAsync("market.buy", new { item = Items.ConcreteMixer, quantity = 1 })).ErrorCode());
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
        var skip = Interlocked.Increment(ref _plots) * 3 + 200;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 2)
        {
            for (var x = 0; x < Map.Width; x += 2)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private static async Task<(int X, int Y, byte[] Tiles)> BuyAsync(GameClient client, Biome biome)
    {
        var (x, y) = FreePlot(biome);
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

    [Fact]
    public async Task A_dug_dune_drifts_back_after_a_sandstorm_unless_a_reed_fence_stands_near()
    {
        using var client = await PlayerAsync();
        var (x, y, tiles) = await BuyAsync(client, Biome.Desert);
        var dunes = Enumerable.Range(PlotLayout.Size * 20, tiles.Length - PlotLayout.Size * 20).Where(i => tiles[i] == (byte)TileObject.Dune).ToList();
        var far = dunes[0];
        // Another dune well away from the first, with a free tile three rows north of it for a fence.
        var near = dunes.First(i => (Math.Abs(i % PlotLayout.Size - far % PlotLayout.Size) > 12 || Math.Abs(i / PlotLayout.Size - far / PlotLayout.Size) > 12)
            && tiles[i - 3 * PlotLayout.Size] == (byte)TileObject.None);
        foreach (var i in new[] { far, near })
        {
            factory.Time.Advance(HarvestRules.Time);
            var dug = (await client.SendAsync("plot.harvest", new { x, y, tileX = i % PlotLayout.Size, tileY = i / PlotLayout.Size })).Payload();
            Assert.Equal(4, dug.GetProperty("items").EnumerateArray().First(s => s.GetProperty("id").GetString() == Items.Sand).GetProperty("quantity").GetInt32());
        }

        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("nothing_here", (await client.SendAsync("plot.harvest", new { x, y, tileX = far % PlotLayout.Size, tileY = far / PlotLayout.Size })).ErrorCode());

        // A reed fence three tiles from the second dune keeps the sand off it.
        await GiveAsync(client, Items.ReedFence, 1);
        var fence = (near % PlotLayout.Size, near / PlotLayout.Size - 3);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.ReedFence, tileX = fence.Item1, tileY = fence.Item2, rotation = 0 });
        Assert.True(placed.Type() == "plot.placed", placed.ToString());

        // Dug long before the last sandstorm.
        await SqlAsync("UPDATE world.plot_harvests SET harvested_at = ARRAY(SELECT CASE WHEN a IS NOT NULL THEN @long END FROM unnest(harvested_at) WITH ORDINALITY AS u(a, i) ORDER BY i) WHERE x = @x AND y = @y",
            ("long", factory.Time.GetUtcNow() - TimeSpan.FromDays(3)), ("x", x), ("y", y));
        var after = await TilesAsync(client, x, y);
        Assert.Equal((byte)TileObject.Dune, after[far]);
        Assert.Equal((byte)TileObject.None, after[near]);

        factory.Time.Advance(HarvestRules.Time);
        var again = await client.SendAsync("plot.harvest", new { x, y, tileX = far % PlotLayout.Size, tileY = far / PlotLayout.Size });
        Assert.True(again.Type() == "plot.harvested", again.ToString());
        Assert.Equal((byte)TileObject.None, (await TilesAsync(client, x, y))[far]);
    }

    [Fact]
    public async Task Only_camels_live_on_the_desert_and_give_milk_and_wool_for_dates()
    {
        using var client = await PlayerAsync();
        var (x, y, tiles) = await BuyAsync(client, Biome.Desert);
        var free = Enumerable.Range(PlotLayout.Size * 20, tiles.Length - PlotLayout.Size * 20).First(i => tiles[i] == (byte)TileObject.None);
        object At(string? item = null) => new { x, y, tileX = free % PlotLayout.Size, tileY = free / PlotLayout.Size, item };
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Camel, quantity = 1 })).Type());
        await GiveAsync(client, Items.Cow, 1);
        await GiveAsync(client, Items.Dates, 2);

        Assert.Equal("no_pasture", (await client.SendAsync("plot.release", At(Items.Cow))).ErrorCode());
        Assert.Equal("plot.animal", (await client.SendAsync("plot.release", At(Items.Camel))).Type());
        Assert.Equal("plot.animal", (await client.SendAsync("plot.feed", At(Items.Dates))).Type());
        factory.Time.Advance(TimeSpan.FromMinutes(10));
        var collected = (await client.SendAsync("plot.collect", At())).Payload();
        var inventory = await client.InventoryAsync();
        Assert.Equal(2, inventory[Items.Milk]);
        Assert.Equal(1, inventory[Items.Wool]);
        Assert.Equal(2, collected.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Solar_power_burns_nothing_and_a_solar_tower_stands_only_on_sand()
    {
        using var client = await PlayerAsync();
        var (x, y, _) = await BuyAsync(client, Biome.Desert);
        await GiveAsync(client, Items.SolarPanel, 1);
        await GiveAsync(client, Items.Battery, 1);
        await GiveAsync(client, Items.Pole, 1);
        await GiveAsync(client, Items.Coal, 5);
        var tiles = await TilesAsync(client, x, y);
        var spots = Enumerable.Range(PlotLayout.Size * 20, tiles.Length - PlotLayout.Size * 22)
            .First(i => i % PlotLayout.Size < PlotLayout.Size - 3 && Enumerable.Range(0, 3).All(d => tiles[i + d] == 0));
        var (tx, ty) = (spots % PlotLayout.Size, spots / PlotLayout.Size);
        async Task<string> Place(string kind, int dx) =>
            (await client.SendAsync("plot.place", new { x, y, kind, tileX = tx + dx, tileY = ty, rotation = 0 })).Payload().GetProperty("object").GetProperty("id").GetString()!;
        var panel = await Place(Items.SolarPanel, 0);
        await Place(Items.Pole, 1);
        var battery = await Place(Items.Battery, 2);

        var state = (await client.SendAsync("generator.get", new { x, y, id = panel })).Payload();
        Assert.True(state.GetProperty("solar").GetBoolean());
        Assert.Equal(25, state.GetProperty("capacityKw").GetDouble(), 6);
        Assert.Equal(1, state.GetProperty("network").GetProperty("solar").GetInt32());
        var bank = (await client.SendAsync("generator.get", new { x, y, id = battery })).Payload();
        Assert.True(bank.GetProperty("battery").GetBoolean());
        Assert.Equal(PowerRules.BatteryKj, bank.GetProperty("capacityKj").GetDouble());
        Assert.Equal("no_fuel_needed", (await client.SendAsync("generator.load", new { x, y, id = panel, item = Items.Coal, quantity = 1 })).ErrorCode());

        var (fx, fy, forest) = await BuyAsync(client, Biome.Forest);
        await GiveAsync(client, Items.SolarTower, 1);
        var clear = Enumerable.Range(PlotLayout.Size * 20, forest.Length - PlotLayout.Size * 22)
            .First(i => i % PlotLayout.Size is > 2 and < PlotLayout.Size - 3
                && Enumerable.Range(-1, 3).All(dy => Enumerable.Range(-1, 3).All(dx => forest[i + dy * PlotLayout.Size + dx] == 0)));
        Assert.Equal("no_sand", (await client.SendAsync("plot.place",
            new { x = fx, y = fy, kind = Items.SolarTower, tileX = clear % PlotLayout.Size, tileY = clear / PlotLayout.Size, rotation = 0 })).ErrorCode());
    }
}
