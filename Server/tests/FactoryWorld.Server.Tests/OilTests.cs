using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class OilTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static int _plots;

    private static PlotObject Obj(string kind, int x, int y, int rotation = 1) => new(Guid.NewGuid(), kind, x, y, rotation, Guid.Empty);

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void Bigger_deposits_hold_more_and_pump_faster_and_the_seabed_holds_twice_as_much()
    {
        Assert.Equal(4_000, OilRules.Reserve(OilField.Oil, DepositSize.Small, false));
        Assert.Equal(25_000, OilRules.Reserve(OilField.Oil, DepositSize.Large, false));
        Assert.Equal(15_000, OilRules.Reserve(OilField.Oil, DepositSize.Medium, true));
        // Water forces out oil, not gas.
        Assert.Equal(10_000, OilRules.Reserve(OilField.Gas, DepositSize.Medium, true));
        Assert.Equal(20_000, OilRules.Reserve(OilField.Seabed, DepositSize.Medium, false));
        Assert.True(OilRules.UnitTime(Items.Pumpjack, DepositSize.Large, false) < OilRules.UnitTime(Items.Pumpjack, DepositSize.Small, false));
        Assert.Equal(OilRules.UnitTime(Items.GasWell, DepositSize.Small, false) * 2, OilRules.UnitTime(Items.GasWell, DepositSize.Small, true));
        Assert.Equal(3, OilRules.MaxWells(DepositSize.Large));
        Assert.Equal(Items.NaturalGas, OilRules.Product(OilRules.FieldOf(Items.GasWell)));
        Assert.Equal(Items.CrudeOil, OilRules.Product(OilRules.FieldOf(Items.OffshorePlatform)));

        // Only the coast has oil under the seabed.
        Assert.All(Enumerable.Range(0, 50), i => Assert.Equal(DepositSize.None, Deposits.SeabedIn(i, i, Biome.Tundra)));
        Assert.Contains(Enumerable.Range(0, 50), i => Deposits.SeabedIn(i, 7, Biome.Coast) != DepositSize.None);
    }

    [Fact]
    public void Fluids_flow_only_through_pipes_and_pumps_and_never_onto_belts()
    {
        var pipe = Obj(Items.Pipe, 0, 0);
        var next = Obj(Items.Pipe, 1, 0);
        var belt = Obj(Items.Belt, 2, 0);
        string?[] Full(string item) => [null, null, null, item, null, null, null, null];
        var contents = new Dictionary<Guid, Contents> { [pipe.Id] = new() { Belt = Full(Items.CrudeOil) } };
        var logistics = new PlotLogistics([pipe, next, belt], contents, new Dictionary<Guid, IMachinePort>());
        logistics.Run(Start, Start + TimeSpan.FromSeconds(5));
        // Through the next pipe to its end, and no further onto the belt.
        Assert.Equal(Items.CrudeOil, contents[next.Id].Belt![3]);
        Assert.All(contents[belt.Id].Belt!, Assert.Null);

        // A pump takes oil out of a tank onto a pipe; an inserter leaves it alone.
        var tank = Obj(Items.Tank, 5, 5);
        var pump = Obj(Items.Pump, 6, 5);
        var out1 = Obj(Items.Pipe, 7, 5);
        var chest = Obj(Items.IronChest, 5, 8);
        var inserter = Obj(Items.Inserter, 6, 8);
        var out2 = Obj(Items.Belt, 7, 8);
        var tankContents = new Contents { Items = [new ItemStack { Item = Items.CrudeOil, Quantity = 5 }] };
        var chestContents = new Contents { Items = [new ItemStack { Item = Items.Wood, Quantity = 5 }] };
        contents = new Dictionary<Guid, Contents> { [tank.Id] = tankContents, [chest.Id] = chestContents };
        var ports = new Dictionary<Guid, IMachinePort>
        {
            [tank.Id] = new StoragePort(Storages.Find(Items.Tank)!, tankContents, () => { }),
            [chest.Id] = new StoragePort(Storages.Find(Items.IronChest)!, chestContents, () => { }),
        };
        new PlotLogistics([tank, pump, out1, chest, inserter, out2], contents, ports).Run(Start, Start + TimeSpan.FromSeconds(3));
        Assert.Contains(Items.CrudeOil, contents[out1.Id].Belt!);
        Assert.Contains(Items.Wood, contents[out2.Id].Belt!);
        Assert.DoesNotContain(Items.Wood, contents[out1.Id].Belt!);

        // A tank holds only fluids, and a chest none.
        Assert.False(Storages.Find(Items.Tank)!.Takes(Items.Wood));
        Assert.False(Storages.Find(Items.IronChest)!.Takes(Items.Diesel));
    }

    [Fact]
    public void A_gas_power_plant_burns_gas_and_shares_the_load_with_a_steam_generator_by_its_size()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var gas = Obj(Items.GasPlant, 1, 0);
        var steam = Obj(Items.Generator, -1, 0);
        var refineries = new[] { Obj(Items.Refinery, 0, 1), Obj(Items.Refinery, 1, 1), Obj(Items.Cracker, -1, 1) };
        var data = new GridData
        {
            Generators = { [gas.Id] = new GeneratorState(Items.NaturalGas, 10, 0), [steam.Id] = new GeneratorState(Items.Coal, 10, 0) },
        };
        var grid = new PowerGrid(0, 0, [pole, gas, steam, .. refineries], data, Start, (_, _, _) => Task.CompletedTask);
        // 280 kW drawn from 400 kW: each gives 70% of what it can.
        Assert.Equal(1, grid.Clock(refineries[0].Id).Speed(Start));
        Assert.Equal(210, grid.Output(gas.Id, Start), 6);
        Assert.Equal(70, grid.Output(steam.Id, Start), 6);
        Assert.Equal("not_fuel", Assert.Throws<GameException>(() => grid.Fuel(gas.Id, Items.Coal, 1, Start)).Code);
        grid.Fuel(steam.Id, Items.Coal, 1, Start);
        Assert.True(PowerRules.Burns(Items.DieselGenerator, Items.Gasoline));
        Assert.Equal(300, PowerRules.KwOf(Items.GasPlant));
    }

    [Fact]
    public void The_oil_market_has_its_crisis_and_its_glut_and_swings_more_than_the_rest()
    {
        Assert.Contains(MarketEvents.Events, e => e.Id == "oil_crisis" && e.Pays[Items.CrudeOil] > 0);
        Assert.Contains(MarketEvents.Events, e => e.Id == "oil_glut" && e.Pays[Items.CrudeOil] < 0);
        Assert.Equal(MarketEvents.OilDrift, MarketEvents.DriftOf(Items.Gasoline));
        var swings = Enumerable.Range(0, 400).Select(h => MarketEvents.DriftAt(Items.CrudeOil, Start + TimeSpan.FromHours(h))).ToList();
        Assert.Contains(swings, d => Math.Abs(d) > MarketEvents.Drift);
        Assert.All(swings, d => Assert.InRange(d, -MarketEvents.OilDrift, MarketEvents.OilDrift));
        Assert.True(NpcPrices.Buys[Items.Gasoline] * 2 > NpcPrices.Buys[Items.CrudeOil] * 3, "refining pays");
    }

    [Fact]
    public void Every_oil_machine_and_product_has_a_recipe_a_technology_and_a_price()
    {
        foreach (var item in new[]
                 {
                     Items.Pumpjack, Items.GasWell, Items.OffshorePlatform, Items.WaterInjector, Items.OilSeparator, Items.Distillery, Items.Refinery,
                     Items.Cracker, Items.ChemicalPlant, Items.GasPlant, Items.DieselGenerator, Items.Pipe, Items.Pump, Items.Tank, Items.Road, Items.Gasket,
                 })
        {
            Assert.NotNull(Workshop.Find(item));
            Assert.Contains(TechTree.Technologies, t => t.Unlocks.Contains(item));
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
            Assert.Contains(item, PlotObjects.Placeable.Append(Items.Gasket));
        }

        foreach (var item in new[] { Items.Gasoline, Items.Diesel, Items.Kerosene, Items.Asphalt, Items.Plastic, Items.SyntheticRubber, Items.Fertilizer })
        {
            Assert.Contains(Processing.Machines, m => m.Recipe(item) is not null);
            Assert.True(NpcPrices.Buys.ContainsKey(item), item);
        }
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
        var db = scope.ServiceProvider.GetRequiredService<FactoryWorld.Modules.Abstractions.GameDb>();
        await using var command = await db.CommandAsync(sql, CancellationToken.None);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>A free plot of the biome whose survey finds what <paramref name="fits"/> wants.</summary>
    private static (int X, int Y) FreePlot(Biome biome, Func<Survey, bool> fits)
    {
        var skip = Interlocked.Increment(ref _plots) * 5 + 500;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 3)
        {
            for (var x = 0; x < Map.Width; x += 3)
            {
                if (Map.BiomeAt(x, y) == biome && fits(Deposits.Under(x, y, biome)) && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private static async Task<(int X, int Y, JsonElement Plot)> BuyAsync(GameClient client, Biome biome, Func<Survey, bool> fits)
    {
        var (x, y) = FreePlot(biome, fits);
        var bought = await client.SendAsync("world.buy", new { x, y });
        Assert.True(bought.Type() == "world.bought", bought.ToString());
        return (x, y, (await client.SendAsync("plot.enter", new { x, y })).Payload());
    }

    /// <summary>Tiles of an entered plot with nothing on them, those whose whole 3 × 3 square is clear first.</summary>
    private static List<(int X, int Y)> FreeTiles(JsonElement plot)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        bool Clear(int x, int y) => PlotLayout.Inside(x, y) && bytes[y * PlotLayout.Size + x] == (byte)TileObject.None && (x, y) != PlotLayout.Spawn;
        var free = new List<(int X, int Y)>();
        for (var y = 2; y < PlotLayout.Size - 2; y++)
        {
            for (var x = 2; x < PlotLayout.Size - 2; x++)
            {
                if (Enumerable.Range(-1, 3).All(dy => Enumerable.Range(-1, 3).All(dx => Clear(x + dx, y + dy))))
                {
                    free.Add((x, y));
                }
            }
        }

        return free;
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation = 0 });
        Assert.True(reply.Type() == "plot.placed", reply.ToString());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static (int X, int Y) Spot(int n) => (PlotLayout.Spawn.X + 3 + n * 2, PlotLayout.Spawn.Y + 6);

    [Fact]
    public async Task A_gas_well_stands_only_where_the_survey_found_gas_and_pumps_it_until_the_deposit_runs_dry()
    {
        using var client = await PlayerAsync();
        var (x, y, _) = await BuyAsync(client, Biome.Tundra, s => s.Gas == DepositSize.Small && s.Oil == DepositSize.None);
        await GiveAsync(client, Items.GasWell, 2);
        await GiveAsync(client, Items.Pumpjack, 1);
        var (wx, wy) = Spot(0);
        Assert.Equal("not_surveyed", (await client.SendAsync("plot.place", new { x, y, kind = Items.GasWell, tileX = wx, tileY = wy, rotation = 0 })).ErrorCode());
        Assert.Equal("world.surveyed", (await client.SendAsync("world.survey", new { x, y })).Type());
        Assert.Equal("no_oil", (await client.SendAsync("plot.place", new { x, y, kind = Items.Pumpjack, tileX = wx, tileY = wy, rotation = 0 })).ErrorCode());

        var well = await PlaceAsync(client, x, y, Items.GasWell, wx, wy);
        // A small deposit takes one well.
        var (ox, oy) = Spot(1);
        Assert.Equal("too_many_wells", (await client.SendAsync("plot.place", new { x, y, kind = Items.GasWell, tileX = ox, tileY = oy, rotation = 0 })).ErrorCode());

        factory.Time.Advance(TimeSpan.FromSeconds(6 * 5 + 1));
        var state = (await client.SendAsync("well.get", new { x, y, id = well })).Payload();
        Assert.Equal(5, state.GetProperty("stored").GetInt32());
        Assert.Equal(Items.NaturalGas, state.GetProperty("product").GetString());
        Assert.Equal(4_000 - 5, state.GetProperty("left").GetInt64());

        var taken = (await client.SendAsync("well.take", new { x, y, id = well })).Payload();
        Assert.Equal(5, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(5, (await client.InventoryAsync())[Items.NaturalGas]);

        // Two units left in the deposit: the well pumps those and stops.
        await SqlAsync("UPDATE world.oil_fields SET pumped = 3998 WHERE x = @x AND y = @y", ("x", x), ("y", y));
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        var dry = (await client.SendAsync("well.get", new { x, y, id = well })).Payload();
        Assert.Equal(2, dry.GetProperty("stored").GetInt32());
        Assert.Equal(0, dry.GetProperty("left").GetInt64());

        // Picked up, it gives back what it held.
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = well })).Type());
        Assert.Equal(7, (await client.InventoryAsync())[Items.NaturalGas]);
    }

    [Fact]
    public async Task A_well_without_gaskets_leaks_and_fouls_the_ground_until_a_separator_or_a_gasket_puts_it_right()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Forest, s => s.Gas != DepositSize.None);
        await client.SendAsync("world.survey", new { x, y });
        var free = FreeTiles(plot);
        await GiveAsync(client, Items.GasWell, 1);
        await GiveAsync(client, Items.Sapling, 3);
        await GiveAsync(client, Items.OilSeparator, 1);
        await GiveAsync(client, Items.Gasket, 2);
        var (wx, wy) = free[0];
        var well = await PlaceAsync(client, x, y, Items.GasWell, wx, wy);
        await SqlAsync("UPDATE world.plot_wells SET wear = @wear WHERE object_id = @id", ("wear", OilRules.WearPerGasket - 1), ("id", Guid.Parse(well)));
        factory.Time.Advance(TimeSpan.FromSeconds(7));
        var leaking = (await client.SendAsync("well.get", new { x, y, id = well })).Payload();
        Assert.True(leaking.GetProperty("leaking").GetBoolean());
        Assert.True(leaking.GetProperty("polluting").GetBoolean());

        var (tx, ty) = free.First(t => OilRules.Near(wx, wy, t.X, t.Y) && Math.Abs(t.X - wx) > 1);
        Assert.Equal("polluted", (await client.SendAsync("plot.plantTree", new { x, y, tileX = tx, tileY = ty })).ErrorCode());

        // A separator next to the well keeps the ground clean.
        var (sx, sy) = free.First(t => OilRules.Near(wx, wy, t.X, t.Y) && Math.Abs(t.Y - wy) > 1 && Math.Abs(t.X - tx) > 1);
        await PlaceAsync(client, x, y, Items.OilSeparator, sx, sy);
        Assert.False((await client.SendAsync("well.get", new { x, y, id = well })).Payload().GetProperty("polluting").GetBoolean());
        Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", new { x, y, tileX = tx, tileY = ty })).Type());

        // A gasket seals it at once, and the other is kept for later.
        var sealedWell = (await client.SendAsync("well.seal", new { x, y, id = well })).Payload();
        Assert.False(sealedWell.GetProperty("leaking").GetBoolean());
        Assert.Equal(1, sealedWell.GetProperty("gaskets").GetInt32());
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Gasket));
    }

    [Fact]
    public async Task A_station_with_diesel_sends_a_diesel_train_that_gets_there_sooner()
    {
        using var client = await PlayerAsync();
        var from = await BuyAsync(client, Biome.Grassland, _ => true);
        var to = await BuyAsync(client, Biome.Forest, _ => true);
        await GiveAsync(client, Items.Wood, 4);
        await GiveAsync(client, Items.Diesel, 50);
        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Wood, quantity = 4 });
        var km = RailRules.DistanceKm(Map, from.X, from.Y, to.X, to.Y);

        var steam = (await client.SendAsync("station.send", new { x = from.X, y = from.Y, toX = to.X, toY = to.Y, item = Items.Wood, quantity = 2 })).Payload();
        var steamArrives = steam.GetProperty("outgoing")[0].GetProperty("arrivesAt").GetInt64();
        Assert.Equal(RailRules.TripTime(km).TotalMilliseconds, steamArrives - steam.GetProperty("serverTime").GetInt64(), 1.0);

        await client.SendAsync("station.load", new { x = from.X, y = from.Y, item = Items.Diesel, quantity = 50 });
        var diesel = (await client.SendAsync("station.send", new { x = from.X, y = from.Y, toX = to.X, toY = to.Y, item = Items.Wood, quantity = 2 })).Payload();
        var trips = diesel.GetProperty("outgoing").EnumerateArray().Select(t => t.GetProperty("arrivesAt").GetInt64()).ToList();
        Assert.Equal(DieselRules.TripTime(RailRules.TripTime(km)).TotalMilliseconds, trips.Min() - diesel.GetProperty("serverTime").GetInt64(), 1.0);
        var left = diesel.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetString() == Items.Diesel).GetProperty("quantity").GetInt64();
        Assert.Equal(50 - DieselRules.Fuel(km), left);
    }

    [Fact]
    public async Task Fertiliser_makes_a_crop_ripe_in_half_the_time()
    {
        using var client = await PlayerAsync();
        var (x, y, plot) = await BuyAsync(client, Biome.Farmland, _ => true);
        await GiveAsync(client, Items.Fertilizer, 1);
        var (tx, ty) = (plot.GetProperty("spawn").GetProperty("x").GetInt32(), plot.GetProperty("spawn").GetProperty("y").GetInt32());
        factory.Time.Advance(HarvestRules.Time);
        Assert.Equal("plot.field", (await client.SendAsync("plot.till", new { x, y, tileX = tx, tileY = ty })).Type());
        var sown = (await client.SendAsync("plot.plant", new { x, y, tileX = tx, tileY = ty, crop = Items.Wheat, fertilize = true })).Payload();
        var ready = sown.GetProperty("field").GetProperty("readyAt").GetInt64() - sown.GetProperty("serverTime").GetInt64();
        Assert.Equal(Farming.ById[Items.Wheat].Grow.TotalMilliseconds / 2, ready, 1);
        Assert.False((await client.InventoryAsync()).ContainsKey(Items.Fertilizer));
    }
}
