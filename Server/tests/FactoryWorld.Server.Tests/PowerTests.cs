using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class PowerTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1);

    private static PlotObject Obj(string kind, int x, int y, int rotation = 1) => new(Guid.NewGuid(), kind, x, y, rotation, Guid.Empty);

    private static PowerGrid Grid(IReadOnlyList<PlotObject> objects, GridData? data = null) =>
        new(0, 0, objects, data ?? new GridData(), Start, (_, _, _) => Task.CompletedTask);

    private static GridData Fuelled(PlotObject generator, string item, int quantity) =>
        new() { Generators = { [generator.Id] = new GeneratorState(item, quantity, 0) } };

    [Fact]
    public void Poles_in_reach_of_each_other_make_one_network_with_what_they_cover()
    {
        var a = Obj(Items.Pole, 0, 0);
        var b = Obj(Items.Pole, 7, 0);
        var far = Obj(Items.Pole, 30, 0);
        var mill = Obj(Items.Mill, 9, 2);
        var generator = Obj(Items.Generator, -2, -2);
        var alone = Obj(Items.Mill, 20, 0);
        var networks = PowerRules.Networks([a, b, far, mill, generator, alone]);

        Assert.Equal(2, networks.Count);
        Assert.Equal([a.Id, b.Id], networks[0].Poles);
        Assert.Equal([mill.Id], networks[0].Consumers);
        Assert.Equal([generator.Id], networks[0].Generators);
        Assert.Equal(30, networks[0].DemandKw);
        Assert.Empty(networks[1].Consumers);
    }

    [Fact]
    public void A_machine_works_at_full_speed_until_the_generator_burns_its_fuel_then_stops()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var mill = Obj(Items.Mill, 1, 0);
        var generator = Obj(Items.Generator, -1, 0);
        var grid = Grid([pole, mill, generator], Fuelled(generator, Items.Coal, 1));
        var clock = grid.Clock(mill.Id);

        // One coal is 20 MJ; the mill draws 30 kW, so it lasts 666.7 s.
        Assert.Equal(1, clock.Speed(Start + TimeSpan.FromSeconds(600)));
        Assert.Equal(30, grid.Output(generator.Id, Start));
        Assert.Equal(Start + TimeSpan.FromSeconds(600), clock.At(Start + TimeSpan.FromSeconds(600)));
        var out_ = grid.RunsOutAt(generator.Id)!.Value;
        Assert.InRange((out_ - Start).TotalSeconds, 666.6, 666.7);
        Assert.Equal(0, clock.Speed(out_ + TimeSpan.FromSeconds(1)));

        // Its own time stands still from then on.
        Assert.Equal(clock.At(out_), clock.At(out_ + TimeSpan.FromHours(1)));
        Assert.Null(clock.When(clock.At(out_) + TimeSpan.FromSeconds(1), out_));
        Assert.False(grid.Generator(generator.Id, out_ + TimeSpan.FromSeconds(1)).Running);
    }

    [Fact]
    public void Too_little_power_slows_every_machine_on_the_network_down()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var generator = Obj(Items.Generator, 0, 1);
        var mills = Enumerable.Range(-2, 5).Select(x => Obj(Items.Mill, x, -1)).ToList();
        var grid = Grid([pole, generator, .. mills], Fuelled(generator, Items.Coal, 10));

        // Five mills draw 150 kW from a 100 kW generator.
        var clock = grid.Clock(mills[0].Id);
        Assert.Equal(100.0 / 150, clock.Speed(Start), 6);
        Assert.Equal(Start + TimeSpan.FromSeconds(20), clock.At(Start + TimeSpan.FromSeconds(30)));
        Assert.Equal(Start + TimeSpan.FromSeconds(30), clock.When(Start + TimeSpan.FromSeconds(20), Start));
    }

    [Fact]
    public void A_machine_without_a_pole_near_has_no_power_and_settling_keeps_its_lag()
    {
        var generator = Obj(Items.Generator, 0, 0);
        var mill = Obj(Items.Mill, 1, 0);
        var grid = Grid([generator, mill], Fuelled(generator, Items.Wood, 5));
        Assert.Equal(0, grid.Clock(mill.Id).Speed(Start));
        Assert.Equal(Start, grid.Clock(mill.Id).At(Start + TimeSpan.FromMinutes(5)));

        // The generator gives nothing to nobody, so it keeps its wood.
        grid.Settle(Start + TimeSpan.FromMinutes(5));
        Assert.Equal(5, grid.Data.Generators[generator.Id].Fuel);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, grid.Data.Lags[mill.Id]);
        Assert.Equal(Start, grid.Clock(mill.Id).At(Start + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Inserters_stand_still_without_power_and_swing_slower_on_too_little()
    {
        PlotObject[] Line(int y) => [Obj(Items.WoodenChest, 0, y), Obj(Items.Inserter, 1, y), Obj(Items.WoodenChest, 2, y)];
        (PlotLogistics, Dictionary<Guid, Contents>) Build(PlotObject[] line, PowerGrid grid)
        {
            var contents = new Dictionary<Guid, Contents> { [line[0].Id] = new() { Items = [new() { Item = Items.Stone, Quantity = 100 }] } };
            var ports = new Dictionary<Guid, IMachinePort>();
            foreach (var chest in new[] { line[0], line[2] })
            {
                contents.TryAdd(chest.Id, new Contents());
                ports[chest.Id] = new StoragePort(Storages.Find(chest.Kind)!, contents[chest.Id], () => { });
            }

            return (new PlotLogistics(line, contents, ports, grid), contents);
        }

        int Moved(Dictionary<Guid, Contents> contents, PlotObject chest) => contents[chest.Id].Items?.Sum(s => s.Quantity) ?? 0;

        var dark = Line(0);
        var (logistics, contents) = Build(dark, Grid(dark));
        logistics.Run(Start, Start + TimeSpan.FromMinutes(1));
        Assert.Equal(0, Moved(contents, dark[2]));

        // At full power one item takes 10 ticks: picking it up, 4 ticks over, dropping it, 4 ticks back.
        var lit = Line(0);
        var generator = Obj(Items.Generator, 1, 1);
        var pole = Obj(Items.Pole, 1, 2);
        (logistics, contents) = Build(lit, Grid([.. lit, generator, pole], Fuelled(generator, Items.Coal, 1)));
        logistics.Run(Start, Start + TimeSpan.FromSeconds(16));
        var full = Moved(contents, lit[2]);
        Assert.Equal(8, full);

        // Twenty-one inserters draw 105 kW from 100: each works at 95%.
        var crowd = Enumerable.Range(0, 20).Select(i => Obj(Items.Inserter, 10 + i % 4, i / 4)).ToList();
        var poles = new[] { Obj(Items.Pole, 1, 2), Obj(Items.Pole, 7, 2), Obj(Items.Pole, 11, 2) };
        (logistics, contents) = Build(lit, Grid([.. lit, generator, .. poles, .. crowd], Fuelled(generator, Items.Coal, 1)));
        logistics.Run(Start, Start + TimeSpan.FromSeconds(160));
        Assert.InRange(Moved(contents, lit[2]), 74, 77);
    }

    [Fact]
    public void An_inserter_feeds_coal_from_a_chest_to_the_generator_that_powers_it()
    {
        var chest = Obj(Items.WoodenChest, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var generator = Obj(Items.Generator, 2, 0);
        var pole = Obj(Items.Pole, 1, 1);
        PlotObject[] objects = [chest, inserter, generator, pole];
        var grid = Grid(objects, Fuelled(generator, Items.Wood, 1));
        var contents = new Dictionary<Guid, Contents> { [chest.Id] = new() { Items = [new() { Item = Items.Coal, Quantity = 80 }] } };
        var ports = new Dictionary<Guid, IMachinePort>
        {
            [chest.Id] = new StoragePort(Storages.Find(Items.WoodenChest)!, contents[chest.Id], () => { }),
            [generator.Id] = grid.Port(generator.Id)!,
        };

        new PlotLogistics(objects, contents, ports, grid).Run(Start, Start + TimeSpan.FromMinutes(10));

        Assert.True(grid.Changed);
        Assert.Equal(PowerRules.MaxFuel, grid.Generator(generator.Id, Start + TimeSpan.FromMinutes(10)).Fuel);
        Assert.Equal(30, contents[chest.Id].Items![0].Quantity);
    }

    [Fact]
    public void An_inserter_keeps_a_full_generator_fed_while_nobody_looks_at_the_plot()
    {
        var chest = Obj(Items.WoodenChest, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var generator = Obj(Items.Generator, 2, 0);
        var pole = Obj(Items.Pole, 1, 1);
        PlotObject[] objects = [chest, inserter, generator, pole];
        var grid = Grid(objects, Fuelled(generator, Items.WoodPellets, PowerRules.MaxFuel));
        var contents = new Dictionary<Guid, Contents> { [chest.Id] = new() { Items = [new() { Item = Items.WoodPellets, Quantity = 200 }] } };
        var ports = new Dictionary<Guid, IMachinePort>
        {
            [chest.Id] = new StoragePort(Storages.Find(Items.WoodenChest)!, contents[chest.Id], () => { }),
            [generator.Id] = grid.Port(generator.Id)!,
        };

        // The inserter alone draws 5 kW: a pellet (4,000 kJ) lasts 800 s, the full fire about 11 hours. A day later,
        // in one catch-up, it still burns, refilled piece by piece as each one was lit.
        var later = Start + TimeSpan.FromDays(1);
        new PlotLogistics(objects, contents, ports, grid).Run(Start, later);

        var fire = grid.Generator(generator.Id, later);
        Assert.True(fire.Running);
        Assert.InRange(fire.Fuel, PowerRules.MaxFuel - 1, PowerRules.MaxFuel);
        Assert.InRange(contents[chest.Id].Items![0].Quantity, 90, 95);
    }

    [Fact]
    public void A_busy_generator_is_fed_for_days_while_nobody_looks_at_the_plot()
    {
        var chest = Obj(Items.WoodenChest, 0, 0);
        var inserter = Obj(Items.Inserter, 1, 0);
        var generator = Obj(Items.Generator, 2, 0);
        var pole = Obj(Items.Pole, 1, 1);
        PlotObject[] mills = [Obj(Items.Mill, 0, 1), Obj(Items.Mill, 2, 1), Obj(Items.Mill, 1, 2)];
        PlotObject[] objects = [chest, inserter, generator, pole, .. mills];
        var grid = Grid(objects, Fuelled(generator, Items.WoodPellets, PowerRules.MaxFuel));
        var contents = new Dictionary<Guid, Contents> { [chest.Id] = new() { Items = [new() { Item = Items.WoodPellets, Quantity = 7000 }] } };
        var ports = new Dictionary<Guid, IMachinePort>
        {
            [chest.Id] = new StoragePort(Storages.Find(Items.WoodenChest)!, contents[chest.Id], () => { }),
            [generator.Id] = grid.Port(generator.Id)!,
        };

        // Three mills and the inserter draw 95 kW: a pellet lasts about 42 s, so in three days the inserter swings
        // thousands of times, far more than one catch-up's ticks of work without a pause.
        var later = Start + TimeSpan.FromDays(3);
        new PlotLogistics(objects, contents, ports, grid).Run(Start, later);

        var fire = grid.Generator(generator.Id, later);
        Assert.True(fire.Running);
        Assert.InRange(fire.Fuel, PowerRules.MaxFuel - 1, PowerRules.MaxFuel);
        // 3 days × 95 kW ÷ 4,000 kJ: 6,156 pellets lit.
        Assert.InRange(contents[chest.Id].Items![0].Quantity, 835, 850);
    }

    [Fact]
    public void A_substation_powers_a_wider_square_and_pylons_are_wired_from_far_apart()
    {
        var substation = Obj(Items.Substation, 0, 0);
        var near = Obj(Items.Mill, 4, 4);
        var pylon = Obj(Items.Pylon, 10, 0);
        var far = Obj(Items.Pylon, 30, 0);
        var pole = Obj(Items.Pole, 37, 0);
        var beyond = Obj(Items.Pole, 45, 0);
        // A pylon powers only the tiles next to it.
        var byPylon = Obj(Items.Mill, 31, 1);
        var outOfReach = Obj(Items.Mill, 32, 2);
        var networks = PowerRules.Networks([substation, near, pylon, far, pole, beyond, byPylon, outOfReach]);

        // The substation and the pylon 10 tiles from it, the pylon 20 tiles on and the pole 7 tiles past it are one network;
        // a pole 8 tiles past another pole is not wired to it.
        Assert.Equal(2, networks.Count);
        Assert.Equal([substation.Id, pylon.Id, far.Id, pole.Id], networks[0].Poles);
        Assert.Equal([near.Id, byPylon.Id], networks[0].Consumers);
        Assert.Equal([beyond.Id], networks[1].Poles);
        Assert.Empty(networks[1].Consumers);
    }

    [Fact]
    public void A_coal_power_plant_gives_a_megawatt_from_coal_and_less_on_sand()
    {
        Assert.Equal(1000, PowerRules.KwOf(Items.CoalPlant, (int)Biome.Grassland));
        Assert.Equal(1000 * PowerRules.DrySteam, PowerRules.KwOf(Items.CoalPlant, (int)Biome.Desert));
        Assert.True(PowerRules.Burns(Items.CoalPlant, Items.Coal));
        Assert.False(PowerRules.Burns(Items.CoalPlant, Items.Wood));

        // Five mills draw 150 kW: one coal (20 MJ) lasts 133 s and the plant gives each its full power.
        var pole = Obj(Items.Pole, 0, 0);
        var plant = Obj(Items.CoalPlant, 0, 2);
        var mills = Enumerable.Range(-2, 5).Select(x => Obj(Items.Mill, x, -1)).ToList();
        var grid = Grid([pole, plant, .. mills], Fuelled(plant, Items.Coal, 1));
        Assert.Equal(1, grid.Clock(mills[0].Id).Speed(Start));
        Assert.Equal(150, grid.Output(plant.Id, Start));
        Assert.InRange((grid.RunsOutAt(plant.Id)!.Value - Start).TotalSeconds, 133.3, 133.4);
    }

    [Fact]
    public void A_nuclear_plant_burns_a_fuel_rod_in_ten_minutes_at_full_power()
    {
        Assert.Equal(2500, PowerRules.KwOf(Items.NuclearPlant));
        Assert.Equal(600, PowerRules.Fuels[Items.FuelRod] / PowerRules.KwOf(Items.NuclearPlant));
        Assert.False(PowerRules.Burns(Items.NuclearPlant, Items.Coal));

        // A megafactory draws 1 MW: one rod keeps it going for 25 minutes.
        var pole = Obj(Items.Pole, 0, 0);
        var megafactory = Obj(Items.Megafactory, 2, 2);
        var plant = Obj(Items.NuclearPlant, -2, -2);
        var grid = Grid([pole, megafactory, plant], Fuelled(plant, Items.FuelRod, 1));
        Assert.Equal(1, grid.Clock(megafactory.Id).Speed(Start));
        Assert.Equal(1000, grid.Output(plant.Id, Start));
        Assert.Equal(1500, (grid.RunsOutAt(plant.Id)!.Value - Start).TotalSeconds, 3);
    }

    [Fact]
    public void An_energy_store_holds_ten_batteries_worth_and_empties_together_with_a_battery()
    {
        var pole = Obj(Items.Pole, 0, 0);
        var store = Obj(Items.EnergyStore, 1, 1);
        var battery = Obj(Items.Battery, -1, 0);
        var mill = Obj(Items.Mill, 0, -2);
        var grid = Grid([pole, store, battery, mill], new GridData { Batteries = { [store.Id] = 3_600_000, [battery.Id] = 360_000 } });
        var network = Assert.Single(grid.Networks);
        Assert.Equal(3_960_000, network.BankKj);
        Assert.Equal(450, network.BankKw);

        // The mill's 30 kW comes out of both, each by its size: the store gives ten times what the battery gives.
        Assert.Equal(1, grid.Clock(mill.Id).Speed(Start));
        Assert.Equal(-30, grid.BankRate(network, Start));
        Assert.Equal(30.0 * 10 / 11, grid.Output(store.Id, Start), 6);
        Assert.Equal(30.0 / 11, grid.Output(battery.Id, Start), 6);
        var later = Start + TimeSpan.FromSeconds(1100);
        Assert.Equal(3_600_000 - 30_000, grid.Charge(store.Id, later), 3);
        Assert.Equal(360_000 - 3000, grid.Charge(battery.Id, later), 3);

        grid.Settle(later);
        Assert.Equal(3_570_000, grid.Data.Batteries[store.Id], 3);
    }

    [Fact]
    public void Only_the_deep_levels_of_a_mine_turn_up_uranium_ore()
    {
        int? Uranium(int level) => Mining.DropsFor(Mining.Level(level), Mineral.None).Where(d => d.Item == Items.UraniumOre).Select(d => (int?)d.PerMille).FirstOrDefault();
        Assert.Null(Uranium(1));
        Assert.Null(Uranium(3));
        Assert.Equal(10, Uranium(4));
        Assert.Equal(20, Uranium(5));
        // It is rarer than silver, so a full load keeps it over the commoner ores.
        Assert.Equal(Items.UraniumOre, Mining.DropsFor(Mining.Level(5), Mineral.None)[1].Item);
    }

    private static (PowerGrid Supplier, PowerGrid Receiver, PowerLink Link, PlotObject Generator, PlotObject Mill) Cable(double kw, DateTimeOffset until, int coal = 10)
    {
        var generator = Obj(Items.Generator, 1, 0);
        var supplierTie = Obj(Items.GridTie, -1, 0);
        PlotObject[] there = [Obj(Items.Pole, 0, 0), generator, supplierTie];
        var supplier = Grid(there, Fuelled(generator, Items.Coal, coal));
        var mill = Obj(Items.Mill, 1, 0);
        var receiverTie = Obj(Items.GridTie, -1, 0);
        PlotObject[] here = [Obj(Items.Pole, 0, 0), mill, receiverTie];
        var receiver = Grid(here);
        var link = new PowerLink { Id = Guid.NewGuid(), SupplierTie = supplierTie.Id, ReceiverTie = receiverTie.Id, Kw = kw, Until = until, Supplier = supplier, Receiver = receiver };
        supplier.Exports.Add(link);
        receiver.Imports.Add(link);
        return (supplier, receiver, link, generator, mill);
    }

    [Fact]
    public void A_cable_carries_power_from_a_generator_on_one_plot_to_a_mill_on_another_while_it_is_paid_for()
    {
        var (supplier, receiver, link, generator, mill) = Cable(50, Start + TimeSpan.FromHours(1));

        // The cable draws all it can carry from the supplier, and the mill takes what it needs of it.
        Assert.Equal(1, receiver.Clock(mill.Id).Speed(Start));
        Assert.Equal(50, supplier.Output(generator.Id, Start));
        Assert.Equal(50, PowerGrid.Flow(link, Start));
        Assert.Equal(50, receiver.ImportKw(receiver.Networks[0], Start));

        // Once what was paid for runs out, the power stops and the generator rests.
        var later = Start + TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1);
        Assert.Equal(0, receiver.Clock(mill.Id).Speed(later));
        Assert.Equal(0, supplier.Output(generator.Id, later));
        Assert.Equal(Start + TimeSpan.FromHours(1), receiver.Clock(mill.Id).At(later + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_cable_gives_only_as_much_as_the_supplier_has_and_its_changes_reach_the_other_plot()
    {
        // Ten coal is 200 MJ: 4000 s at 50 kW. After that the mill on the other plot stops.
        var (supplier, receiver, _, generator, mill) = Cable(50, PowerLink.Forever);
        var out_ = supplier.RunsOutAt(generator.Id)!.Value;
        Assert.InRange((out_ - Start).TotalSeconds, 3999.9, 4000.1);
        Assert.Equal(1, receiver.Clock(mill.Id).Speed(out_ - TimeSpan.FromSeconds(1)));
        Assert.Equal(0, receiver.Clock(mill.Id).Speed(out_ + TimeSpan.FromSeconds(1)));

        // Fuel put in an hour after it went out settles the other plot first: its mill lost that hour, and works again.
        var refuel = out_ + TimeSpan.FromHours(1);
        supplier.Fuel(generator.Id, Items.Coal, 5, refuel);
        Assert.Equal(refuel, receiver.Settled);
        Assert.InRange(receiver.Data.Lags[mill.Id], TimeSpan.FromHours(1).Ticks - TimeSpan.TicksPerSecond, TimeSpan.FromHours(1).Ticks + TimeSpan.TicksPerSecond);
        Assert.Equal(1, receiver.Clock(mill.Id).Speed(refuel + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_cable_costs_ten_thousand_dollars_a_tile_and_power_the_supplier_price_per_kwh()
    {
        // Two plots side by side, ties on the same row: 250 tiles apart, 2.5 million dollars.
        var a = Obj(Items.GridTie, 10, 20);
        var b = Obj(Items.GridTie, 10, 20);
        Assert.Equal(250, PowerLines.Distance(0, 0, a, 1, 0, b));
        Assert.Equal(250 * 1_000_000L, PowerLines.CableCents(PowerLines.Distance(0, 0, a, 1, 0, b)));
        // 500 tiles: 5 million dollars.
        Assert.Equal(5_000_000_00L, PowerLines.CableCents(500));
        // 100 kW for 2 hours at 5 cents a kWh: 200 kWh, 10 dollars.
        Assert.Equal(1000, PowerLines.PowerCents(100, 2, 5));
    }

    // --- In the game ---------------------------------------------------------------------------

    private async Task GiveAsync(GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    private static (int X, int Y) FreePlot()
    {
        var skip = Interlocked.Increment(ref _plots) + 9000;
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

    /// <summary>A player with a plot that has three free tiles in a row from (tileX, tileY) east.</summary>
    private async Task<(GameClient Client, int X, int Y, int TileX, int TileY)> PlotAsync(long cents = 1_000_000)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        var (x, y, tx, ty) = await BuyPlotAsync(client);
        return (client, x, y, tx, ty);
    }

    /// <summary>Another plot for the player, with three free tiles in a row from (tileX, tileY) east.</summary>
    private static async Task<(int X, int Y, int TileX, int TileY)> BuyPlotAsync(GameClient client)
    {
        var (x, y) = FreePlot();
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        for (var ty = 20; ty < PlotLayout.Size; ty++)
        {
            for (var tx = 0; tx + 4 <= PlotLayout.Size; tx++)
            {
                // Room for a 2 × 2 machine and a pole and a generator east of it.
                if (Enumerable.Range(tx, 4).All(i => bytes[ty * PlotLayout.Size + i] == (byte)TileObject.None && bytes[(ty + 1) * PlotLayout.Size + i] == (byte)TileObject.None))
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free row.");
    }

    private async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY)
    {
        await GiveAsync(client, kind, 1);
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY });
        Assert.Equal("plot.placed", reply.Type());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static int Flour(JsonElement machine) =>
        machine.GetProperty("output").ValueKind == JsonValueKind.Null ? 0 : machine.GetProperty("output").GetProperty("quantity").GetInt32();

    [Fact]
    public async Task A_mill_grinds_only_while_a_generator_on_a_pole_near_it_has_fuel()
    {
        var (client, x, y, tx, ty) = await PlotAsync();
        using var _ = client;
        var mill = await PlaceAsync(client, x, y, Items.Mill, tx, ty);
        await GiveAsync(client, Items.Wheat, 40);
        await client.SendAsync("machine.recipe", new { machine = mill, recipe = Items.Flour });
        var loaded = (await client.SendAsync("machine.load", new { machine = mill, item = Items.Wheat, quantity = 40 })).Payload();
        Assert.True(loaded.GetProperty("powered").GetBoolean());
        Assert.Equal(0, loaded.GetProperty("speed").GetDouble());

        factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, Flour((await client.SendAsync("machine.get", new { machine = mill })).Payload()));
        var stall = Assert.Single((await client.SendAsync("stats.production", new { minutes = 10 })).Payload().GetProperty("stalls").EnumerateArray());
        Assert.Equal("no_power", stall.GetProperty("reason").GetString());

        // One wood is 5 MJ: 166.7 s for a mill drawing 30 kW, 27 batches of 6 s.
        await PlaceAsync(client, x, y, Items.Pole, tx + 2, ty);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx + 3, ty);
        await GiveAsync(client, Items.Wood, 1);
        Assert.Equal("not_fuel", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Stone, quantity = 1 })).ErrorCode());
        var fuelled = (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Wood, quantity = 1 })).Payload();
        Assert.Equal(30, fuelled.GetProperty("outputKw").GetDouble());
        Assert.Equal(1, fuelled.GetProperty("network").GetProperty("machines").GetInt32());

        factory.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(10, Flour((await client.SendAsync("machine.get", new { machine = mill })).Payload()));

        factory.Time.Advance(TimeSpan.FromHours(1));
        var state = (await client.SendAsync("machine.get", new { machine = mill })).Payload();
        Assert.Equal(20, Flour(state));
        Assert.Equal(0, state.GetProperty("speed").GetDouble());
        var logistics = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("power").GetProperty("networks")[0];
        Assert.Equal(30, logistics.GetProperty("demandKw").GetDouble());
        Assert.Equal(0, logistics.GetProperty("supplyKw").GetDouble());

        // The statistics count flour when it was made on the plot, not in the mill's own time, which is a minute
        // behind: the first 10 sacks were made over an hour ago, the other 10 after them.
        async Task<long> Produced(int minutes) => (await client.SendAsync("stats.production", new { minutes })).Payload().GetProperty("items")
            .EnumerateArray().First(i => i.GetProperty("id").GetString() == Items.Flour).GetProperty("produced").GetInt64();
        Assert.Equal(10, await Produced(60));
        Assert.Equal(20, await Produced(1440));
    }

    [Fact]
    public async Task Fuel_comes_back_out_of_a_generator_and_with_it_when_it_is_picked_up()
    {
        var (client, x, y, tx, ty) = await PlotAsync();
        using var _ = client;
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx, ty);
        await GiveAsync(client, Items.Coal, 12);
        await GiveAsync(client, Items.Wood, 1);
        Assert.Equal("generator", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 12 })).Type());
        Assert.Equal("generator_busy", (await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Wood, quantity = 1 })).ErrorCode());

        var taken = (await client.SendAsync("generator.take", new { x, y, id = generator })).Payload();
        Assert.Equal(12, taken.GetProperty("taken")[0].GetProperty("quantity").GetInt32());
        await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 5 });

        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x, y, id = generator })).Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(12, inventory.GetValueOrDefault(Items.Coal));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.Generator));
        Assert.Equal("no_object", (await client.SendAsync("generator.get", new { x, y, id = generator })).ErrorCode());
    }

    [Fact]
    public async Task An_energy_store_on_a_pylon_stores_power_and_takes_no_fuel()
    {
        var (client, x, y, tx, ty) = await PlotAsync();
        using var _ = client;
        var store = await PlaceAsync(client, x, y, Items.EnergyStore, tx, ty);
        await PlaceAsync(client, x, y, Items.Pylon, tx + 2, ty);

        var state = (await client.SendAsync("generator.get", new { x, y, id = store })).Payload();
        Assert.True(state.GetProperty("battery").GetBoolean());
        Assert.Equal(3_600_000, state.GetProperty("capacityKj").GetDouble());
        Assert.Equal(400, state.GetProperty("capacityKw").GetDouble());
        Assert.Equal(1, state.GetProperty("network").GetProperty("batteries").GetInt32());
        Assert.Equal(3_600_000, state.GetProperty("network").GetProperty("bankCapacityKj").GetDouble());

        await GiveAsync(client, Items.Coal, 1);
        Assert.Equal("no_fuel_needed", (await client.SendAsync("generator.load", new { x, y, id = store, item = Items.Coal, quantity = 1 })).ErrorCode());

        var power = (await client.SendAsync("logistics.get", new { x, y })).Payload().GetProperty("power");
        Assert.Equal(20, power.GetProperty("poleKinds").GetProperty(Items.Pylon).GetProperty("reach").GetDouble());
        Assert.Equal(4, power.GetProperty("poleKinds").GetProperty(Items.Substation).GetProperty("supply").GetInt32());
    }

    [Fact]
    public async Task A_cable_between_two_plots_of_one_owner_is_free_and_powers_a_mill_on_the_other()
    {
        var (client, x, y, tx, ty) = await PlotAsync(2_000_000_000_00);
        using var _ = client;
        var (x2, y2, tx2, ty2) = await BuyPlotAsync(client);
        var supplierTie = await PlaceAsync(client, x, y, Items.GridTie, tx, ty);
        await PlaceAsync(client, x, y, Items.Pole, tx + 1, ty);
        var generator = await PlaceAsync(client, x, y, Items.Generator, tx + 2, ty);
        var receiverTie = await PlaceAsync(client, x2, y2, Items.GridTie, tx2, ty2);
        await PlaceAsync(client, x2, y2, Items.Pole, tx2 + 1, ty2);
        var mill = await PlaceAsync(client, x2, y2, Items.Mill, tx2 + 2, ty2);
        await GiveAsync(client, Items.Coal, 10);
        await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 10 });

        Assert.Equal("bad_kw", (await client.SendAsync("powerline.build", new { x = x2, y = y2, id = receiverTie, fromX = x, fromY = y, fromId = supplierTie, kw = 1 })).ErrorCode());
        Assert.Equal("same_plot", (await client.SendAsync("powerline.build", new { x, y, id = supplierTie, fromX = x, fromY = y, fromId = supplierTie, kw = 50 })).ErrorCode());
        // Once its window was opened, a closed tie on the owner's other plot is offered to them, and to nobody else.
        Assert.False((await client.SendAsync("powertie.get", new { x, y, id = supplierTie })).Payload().GetProperty("open").GetBoolean());
        var offers = (await client.SendAsync("powertie.offers", new { x = x2, y = y2, id = receiverTie })).Payload().GetProperty("offers").EnumerateArray();
        Assert.Contains(offers, o => o.GetProperty("id").GetString() == supplierTie && o.GetProperty("mine").GetBoolean());
        var (stranger, sx, sy, stx, sty) = await PlotAsync(2_000_000_000_00);
        using var __ = stranger;
        var strangerTie = await PlaceAsync(stranger, sx, sy, Items.GridTie, stx, sty);
        var theirs = (await stranger.SendAsync("powertie.offers", new { x = sx, y = sy, id = strangerTie })).Payload().GetProperty("offers").EnumerateArray();
        Assert.DoesNotContain(theirs, o => o.GetProperty("id").GetString() == supplierTie);
        Assert.Equal("tie_closed", (await stranger.SendAsync("powerline.build", new { x = sx, y = sy, id = strangerTie, fromX = x, fromY = y, fromId = supplierTie, kw = 50 })).ErrorCode());

        var before = await client.BalanceAsync();
        var built = await client.SendAsync("powerline.build", new { x = x2, y = y2, id = receiverTie, fromX = x, fromY = y, fromId = supplierTie, kw = 50 });
        Assert.Equal("powertie", built.Type());
        var line = Assert.Single(built.Payload().GetProperty("lines").EnumerateArray());
        Assert.False(line.GetProperty("supplying").GetBoolean());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("paidUntil").ValueKind);
        Assert.Equal(50, line.GetProperty("flowKw").GetDouble());
        Assert.True(before - await client.BalanceAsync() >= 250 * PowerLines.CentsPerTile);
        Assert.Equal("line_loop", (await client.SendAsync("powerline.build", new { x, y, id = supplierTie, fromX = x2, fromY = y2, fromId = receiverTie, kw = 50 })).ErrorCode());

        await GiveAsync(client, Items.Wheat, 10);
        await client.SendAsync("machine.recipe", new { machine = mill, recipe = Items.Flour });
        Assert.Equal(1, (await client.SendAsync("machine.load", new { machine = mill, item = Items.Wheat, quantity = 10 })).Payload().GetProperty("speed").GetDouble());
        var supplying = (await client.SendAsync("generator.get", new { x, y, id = generator })).Payload();
        Assert.Equal(50, supplying.GetProperty("outputKw").GetDouble());

        // Taking the cable down stops the mill.
        var removed = await client.SendAsync("powerline.remove", new { x, y, id = supplierTie, line = line.GetProperty("id").GetString() });
        Assert.Empty(removed.Payload().GetProperty("lines").EnumerateArray());
        Assert.Equal(0, (await client.SendAsync("machine.get", new { machine = mill })).Payload().GetProperty("speed").GetDouble());
    }

    [Fact]
    public async Task Power_from_another_player_flows_only_while_paid_for_at_the_suppliers_price()
    {
        var (seller, sx, sy, stx, sty) = await PlotAsync();
        using var _ = seller;
        var (buyer, bx, by, btx, bty) = await PlotAsync(2_000_000_000_00);
        using var __ = buyer;
        var sellerTie = await PlaceAsync(seller, sx, sy, Items.GridTie, stx, sty);
        var buyerTie = await PlaceAsync(buyer, bx, by, Items.GridTie, btx, bty);

        Assert.Equal("tie_closed", (await buyer.SendAsync("powerline.build", new { x = bx, y = by, id = buyerTie, fromX = sx, fromY = sy, fromId = sellerTie, kw = 100 })).ErrorCode());
        Assert.Equal("not_owner", (await buyer.SendAsync("powertie.price", new { x = sx, y = sy, id = sellerTie, priceCents = 1, open = true })).ErrorCode());
        Assert.Equal("bad_power_price", (await seller.SendAsync("powertie.price", new { x = sx, y = sy, id = sellerTie, priceCents = -1, open = true })).ErrorCode());
        Assert.True((await seller.SendAsync("powertie.price", new { x = sx, y = sy, id = sellerTie, priceCents = 5, open = true })).Payload().GetProperty("open").GetBoolean());

        var offers = (await buyer.SendAsync("powertie.offers", new { x = bx, y = by, id = buyerTie })).Payload().GetProperty("offers").EnumerateArray().ToList();
        Assert.Contains(offers, o => o.GetProperty("id").GetString() == sellerTie && o.GetProperty("priceCents").GetInt64() == 5);

        var built = (await buyer.SendAsync("powerline.build", new { x = bx, y = by, id = buyerTie, fromX = sx, fromY = sy, fromId = sellerTie, kw = 100 })).Payload();
        var line = built.GetProperty("lines")[0];
        Assert.Equal(0, line.GetProperty("flowKw").GetDouble());
        var lineId = line.GetProperty("id").GetString();

        // 100 kW for 2 hours at 5 cents a kWh: 10 dollars to the seller.
        var sellerBefore = await seller.BalanceAsync();
        var buyerBefore = await buyer.BalanceAsync();
        Assert.Equal("bad_hours", (await buyer.SendAsync("powerline.buy", new { line = lineId, hours = 0 })).ErrorCode());
        Assert.Equal("not_owner", (await seller.SendAsync("powerline.buy", new { line = lineId, hours = 1 })).ErrorCode());
        Assert.Equal("price_changed", (await buyer.SendAsync("powerline.buy", new { line = lineId, hours = 2, priceCents = 4 })).ErrorCode());
        Assert.Equal(buyerBefore, await buyer.BalanceAsync());
        var bought = (await buyer.SendAsync("powerline.buy", new { line = lineId, hours = 2, priceCents = 5 })).Payload();
        Assert.Equal(buyerBefore - 1000, await buyer.BalanceAsync());
        Assert.Equal(sellerBefore + 1000, await seller.BalanceAsync());
        var until = bought.GetProperty("lines")[0].GetProperty("paidUntil").GetInt64();
        Assert.Equal((factory.Time.GetUtcNow() + TimeSpan.FromHours(2)).ToUnixTimeMilliseconds(), until);

        // The seller sees the cable from their side; the power is paid for but the seller's tie stands on no network yet.
        var sellerSide = (await seller.SendAsync("powertie.get", new { x = sx, y = sy, id = sellerTie })).Payload().GetProperty("lines")[0];
        Assert.True(sellerSide.GetProperty("supplying").GetBoolean());
        Assert.Equal(0, sellerSide.GetProperty("flowKw").GetDouble());

        // The seller cannot cut power that was paid for, and once they close the tie nobody buys more.
        Assert.Equal("line_paid", (await seller.SendAsync("powerline.remove", new { x = sx, y = sy, id = sellerTie, line = lineId })).ErrorCode());
        Assert.Equal("line_paid", (await seller.SendAsync("plot.pickup", new { x = sx, y = sy, id = sellerTie })).ErrorCode());
        await seller.SendAsync("powertie.price", new { x = sx, y = sy, id = sellerTie, priceCents = 5, open = false });
        Assert.Equal("tie_closed", (await buyer.SendAsync("powerline.buy", new { line = lineId, hours = 1 })).ErrorCode());

        // When the paid time is over, picking the tie up takes the cable down.
        factory.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal("plot.pickedUp", (await seller.SendAsync("plot.pickup", new { x = sx, y = sy, id = sellerTie })).Type());
        Assert.Empty((await buyer.SendAsync("powertie.get", new { x = bx, y = by, id = buyerTie })).Payload().GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task The_new_power_plants_are_locked_until_they_are_researched()
    {
        var (client, _, _, _, _) = await PlotAsync();
        using var _ = client;
        var locked = (await client.SendAsync("compendium.get")).Payload().GetProperty("locked");
        foreach (var item in new[] { Items.Transformer, Items.Substation, Items.Pylon, Items.CoalPlant, Items.EnergyStore, Items.NuclearPlant, Items.FuelRod })
        {
            Assert.Equal(JsonValueKind.String, locked.GetProperty(item).ValueKind);
        }

        Assert.Equal(TechTree.PowerDistribution, TechTree.ByItem[Items.Pylon].Id);
        Assert.Equal(TechTree.ThermalPower, TechTree.ByItem[Items.CoalPlant].Id);
        Assert.Equal(TechTree.EnergyStorage, TechTree.ByItem[Items.EnergyStore].Id);
        Assert.Equal(TechTree.NuclearPower, TechTree.ByItem[Items.NuclearPlant].Id);
        Assert.Contains(TechTree.DeepMining, TechTree.ById[TechTree.NuclearPower].Requires);
    }

    [Fact]
    public async Task Generators_and_poles_are_made_at_the_workbench_and_sold_by_the_trader()
    {
        var (client, _, _, _, _) = await PlotAsync();
        using var _ = client;
        var recipes = (await client.SendAsync("crafting.get")).Payload().GetProperty("recipes").EnumerateArray()
            .Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Contains(Items.Generator, recipes);
        Assert.Contains(Items.Pole, recipes);
        Assert.Equal("market.traded", (await client.SendAsync("market.buy", new { item = Items.Pole, quantity = 4 })).Type());
    }
}
