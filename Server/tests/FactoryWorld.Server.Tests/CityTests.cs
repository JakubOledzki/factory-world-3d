using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class CityTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Towns AllTowns = Towns.Load();
    private static int _towns;

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void Towns_come_from_the_map_and_grow_level_by_level_on_what_they_get()
    {
        Assert.Contains(AllTowns.All, t => t.Name == "Warszawa");
        var warsaw = AllTowns.All.First(t => t.Name == "Warszawa");
        Assert.Equal(Biome.City, Map.BiomeAt(warsaw.X, warsaw.Y));
        Assert.Equal(warsaw, AllTowns.Of(warsaw.X, warsaw.Y));
        // A city plot no town is near is a town of its own.
        Assert.Equal(CityRules.LoneTownPopulation, AllTowns.Of(-5, -5).Population);

        Assert.Equal(0, CityRules.Level(0));
        Assert.Equal(1, CityRules.Level(20_000_00));
        Assert.Equal(4, CityRules.Level(long.MaxValue));
        Assert.Equal(1, CityRules.PopulationFactor(10_000), 6);
        Assert.Equal(3, CityRules.PopulationFactor(50_000_000), 6);
        Assert.Equal(0.5, CityRules.PopulationFactor(100), 6);

        // What a town wants changes by the day, is the same all day, and a bigger town wants more and newer goods.
        bool Priced(string item) => NpcPrices.Buys.ContainsKey(item);
        var today = CityRules.Needs(warsaw, 0, 100, Priced);
        Assert.Equal(3, today.Count);
        Assert.Equal(today, CityRules.Needs(warsaw, 0, 100, Priced));
        Assert.NotEqual(today, Enumerable.Range(101, 5).Select(d => CityRules.Needs(warsaw, 0, d, Priced)).First(n => !n.SequenceEqual(today)));
        Assert.All(today, n => Assert.Equal(0, CityRules.Goods.First(g => g.Item == n.Item).Level));
        var metropolis = CityRules.Needs(warsaw, 4, 100, Priced);
        Assert.Equal(7, metropolis.Count);
        // A metropolis wants cars, the one good of its own level.
        Assert.Contains(metropolis, n => n.Item == Items.Car);
        Assert.All(CityRules.Goods, g => Assert.True(Priced(g.Item), g.Item));

        Assert.Equal(150, CityRules.NeedPriceCents(100, 0, false));
        Assert.Equal(2185, CityRules.NeedPriceCents(1000, 4, true));
        Assert.Equal(1, CityRules.Accepts(100, 100));
        Assert.Equal(0.5, CityRules.Accepts(200, 100), 6);
        Assert.Equal(0, CityRules.Accepts(300, 100));
        // Never at what the trader himself asks at his cheapest.
        Assert.Equal(0, CityRules.Accepts(150, 100, 150));
        Assert.Equal(0.8, CityRules.Accepts(140, 100, 150), 6);

        Assert.True(CityRules.InBuildArea(PlotLayout.Spawn.X - 30, 59));
        Assert.False(CityRules.InBuildArea(PlotLayout.Spawn.X + 31, 20));
        Assert.False(CityRules.InBuildArea(PlotLayout.Spawn.X, 60));
        Assert.Equal(1, CityRules.Distance(Map.Width, 0, 0, Map.Width - 1, 0), 6);
    }

    [Fact]
    public void City_machines_assemble_goods_and_everything_made_in_a_city_goes_a_quarter_faster()
    {
        var motor = Processing.ByKind[Items.AssemblyHall].Recipe(Items.ElectricMotor)!;
        var faster = Processing.ByKind[Items.MultistoreyFactory].Recipe(Items.ElectricMotor)!;
        Assert.Equal(motor.Time / 2, faster.Time);
        Assert.NotNull(Processing.ByKind[Items.CitySorter].Recipe(Items.WastePaper));
        Assert.Equal(motor.Time / 1.25, Processing.For(Items.AssemblyHall, (int)Biome.City)!.Recipe(Items.ElectricMotor)!.Time);
        Assert.Equal(motor.Time, Processing.For(Items.AssemblyHall, (int)Biome.Forest)!.Recipe(Items.ElectricMotor)!.Time);
        Assert.True(Items.IsCityOnly(Items.Shop));
        Assert.False(Items.IsCityOnly(Items.AssemblyHall));
    }

    [Fact]
    public void The_assembly_hall_makes_the_goods_towns_want_and_the_trader_pays_less_than_they_cost_it_to_sell()
    {
        foreach (var item in new[] { Items.Bicycle, Items.Radio, Items.WashingMachine, Items.Car })
        {
            var recipe = Processing.ByKind[Items.AssemblyHall].Recipe(item)!;
            var inputs = recipe.Ingredients.Sum(i => NpcPrices.Buys[i.Item] * i.Quantity);
            // Putting the parts together pays, but not more than half again what they are worth.
            Assert.InRange(NpcPrices.Buys[item], inputs + 1, inputs * 3 / 2);
            Assert.Contains(CityRules.Goods, g => g.Item == item);
        }

        Assert.Contains(Items.Bicycle, TechTree.ById[TechTree.Assembly].Unlocks);
        Assert.Contains(Items.Car, TechTree.ById[TechTree.Appliances].Unlocks);
        Assert.Equal(4, CityRules.Goods.Single(g => g.Item == Items.Car).Level);
    }

    // --- In a town --------------------------------------------------------------------------------

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

    private static async Task<string> NameAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

    /// <summary>Two city plots next to each other in one town of the map, a different town each time.</summary>
    private static ((int X, int Y) First, (int X, int Y) Second, Town Town) TownPlots()
    {
        var skip = Interlocked.Increment(ref _towns) * 7 + 50;
        foreach (var town in AllTowns.All.Where(t => t.Population > 100_000).Skip(skip))
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var (x, y) = (town.X + dx, town.Y + dy);
                    if (Map.Contains(x + 1, y) && Map.BiomeAt(x, y) == Biome.City && Map.BiomeAt(x + 1, y) == Biome.City
                        && AllTowns.Of(x, y) == town && AllTowns.Of(x + 1, y) == town)
                    {
                        return ((x, y), (x + 1, y), town);
                    }
                }
            }
        }

        throw new InvalidOperationException("No town.");
    }

    private static async Task BuyAsync(GameClient client, int x, int y)
    {
        var bought = await client.SendAsync("world.buy", new { x, y });
        Assert.True(bought.Type() == "world.bought", bought.ToString());
    }

    private static async Task<(int X, int Y)> FreeForestAsync(GameClient client)
    {
        for (var y = 200; ; y++)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == Biome.Forest)
                {
                    var bought = await client.SendAsync("world.buy", new { x, y });
                    if (bought.Type() == "world.bought")
                    {
                        return (x, y);
                    }
                }
            }
        }
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY)
    {
        var placed = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation = 0 });
        Assert.True(placed.Type() == "plot.placed", placed.ToString());
        return placed.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }

    private static JsonElement Result(JsonElement reply)
    {
        Assert.True(reply.Type() == "city", reply.ToString());
        return reply.Payload().GetProperty("result");
    }

    [Fact]
    public async Task A_town_pays_more_than_the_trader_for_what_it_wants_today_and_grows_on_it()
    {
        using var client = await PlayerAsync();
        var (plot, _, town) = TownPlots();
        await BuyAsync(client, plot.X, plot.Y);
        var entered = (await client.SendAsync("plot.enter", new { x = plot.X, y = plot.Y })).Payload().GetProperty("city");
        Assert.Equal(town.Name, entered.GetProperty("name").GetString());
        Assert.Equal("Wieś", entered.GetProperty("levelName").GetString());

        var state = (await client.SendAsync("city.get", new { x = plot.X, y = plot.Y })).Payload().GetProperty("state");
        Assert.Equal(town.Name, state.GetProperty("town").GetProperty("name").GetString());
        var need = state.GetProperty("needs")[0];
        var item = need.GetProperty("id").GetString()!;
        var wanted = need.GetProperty("wanted").GetInt64();
        var price = need.GetProperty("priceCents").GetInt64();
        Assert.Equal(CityRules.NeedPriceCents(NpcPrices.Buys[item], 0, false), price);

        await GiveAsync(client, item, wanted + 5);
        var balance = await client.BalanceAsync();
        var delivered = Result(await client.SendAsync("city.deliver", new { x = plot.X, y = plot.Y, item, quantity = wanted + 5 }));
        // The town takes only what it still wants today.
        Assert.Equal(wanted, delivered.GetProperty("quantity").GetInt64());
        Assert.Equal(wanted * price, delivered.GetProperty("cents").GetInt64());
        Assert.Equal(balance + wanted * price, await client.BalanceAsync());
        Assert.Equal(5, (await client.InventoryAsync())[item]);
        Assert.Equal("need_met", (await client.SendAsync("city.deliver", new { x = plot.X, y = plot.Y, item, quantity = 1 })).ErrorCode());
        var notWanted = CityRules.Goods.Select(g => g.Item).First(i => !state.GetProperty("needs").EnumerateArray().Any(n => n.GetProperty("id").GetString() == i));
        Assert.Equal("not_wanted", (await client.SendAsync("city.deliver", new { x = plot.X, y = plot.Y, item = notWanted, quantity = 1 })).ErrorCode());

        var after = (await client.SendAsync("city.get", new { x = plot.X, y = plot.Y })).Payload().GetProperty("state");
        Assert.Equal(wanted * price, after.GetProperty("town").GetProperty("deliveredCents").GetInt64());
        Assert.Equal(wanted * price, after.GetProperty("myCents").GetInt64());
        Assert.True(after.GetProperty("contributors")[0].GetProperty("mine").GetBoolean());

        // A new day, new needs.
        factory.Time.Advance(CityRules.Day);
        var tomorrow = (await client.SendAsync("city.get", new { x = plot.X, y = plot.Y })).Payload().GetProperty("state").GetProperty("needs");
        Assert.All(tomorrow.EnumerateArray(), n => Assert.Equal(0, n.GetProperty("delivered").GetInt64()));

        // Rubbish goes into the station, and the town pays for each piece.
        balance = await client.BalanceAsync();
        var trash = Result(await client.SendAsync("city.trash", new { x = plot.X, y = plot.Y, quantity = 10 }));
        Assert.Equal(10, trash.GetProperty("quantity").GetInt64());
        Assert.Equal(balance + 10 * CityRules.TrashFeeCents, await client.BalanceAsync());
        var station = (await client.SendAsync("station.get", new { x = plot.X, y = plot.Y })).Payload();
        Assert.Contains(station.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetString() == Items.Trash && i.GetProperty("quantity").GetInt64() == 10);
        Assert.Equal(CityRules.CityRail, station.GetProperty("rates").GetProperty("feeFactor").GetDouble(), 6);
        Assert.Equal(CityRules.TrashPerDay(town, 0) - 10,
            Result(await client.SendAsync("city.trash", new { x = plot.X, y = plot.Y, quantity = 100_000 })).GetProperty("quantity").GetInt64());
        Assert.Equal("no_trash", (await client.SendAsync("city.trash", new { x = plot.X, y = plot.Y, quantity = 1 })).ErrorCode());

        var (fx, fy) = await FreeForestAsync(client);
        // Outside the city the town hall of the county opens, but the town takes nothing there.
        Assert.Equal("not_city", (await client.SendAsync("city.trash", new { x = fx, y = fy, quantity = 1 })).ErrorCode());
    }

    [Fact]
    public async Task A_shop_by_the_station_sells_to_the_townspeople_and_city_buildings_stand_only_there()
    {
        using var client = await PlayerAsync();
        var (plot, _, _) = TownPlots();
        await BuyAsync(client, plot.X, plot.Y);
        await GiveAsync(client, Items.Shop, 3);
        await GiveAsync(client, Items.Bread, 500);
        Assert.Equal("city_cramped", (await client.SendAsync("plot.place",
            new { x = plot.X, y = plot.Y, kind = Items.Shop, tileX = PlotLayout.Spawn.X + 40, tileY = 30, rotation = 0 })).ErrorCode());
        Assert.Equal("city_cramped", (await client.SendAsync("plot.place",
            new { x = plot.X, y = plot.Y, kind = Items.Shop, tileX = PlotLayout.Spawn.X, tileY = 80, rotation = 0 })).ErrorCode());
        var (fx, fy) = await FreeForestAsync(client);
        Assert.Equal("no_city", (await client.SendAsync("plot.place",
            new { x = fx, y = fy, kind = Items.Shop, tileX = 100, tileY = 30, rotation = 0 })).ErrorCode());
        await GiveAsync(client, Items.CompanyHq, 1);
        Assert.Equal("hq_off_company", (await client.SendAsync("plot.place",
            new { x = plot.X, y = plot.Y, kind = Items.CompanyHq, tileX = PlotLayout.Spawn.X - 10, tileY = 30, rotation = 0 })).ErrorCode());

        var shop = await PlaceAsync(client, plot.X, plot.Y, Items.Shop, PlotLayout.Spawn.X + 5, 30);
        var trader = NpcPrices.Buys[Items.Bread];
        Assert.Equal("not_sellable", (await client.SendAsync("shop.stock", new { id = shop, item = Items.Trash, quantity = 1, priceCents = 10 })).ErrorCode());
        Assert.Equal("bad_price", (await client.SendAsync("shop.stock", new { id = shop, item = Items.Bread, quantity = 1, priceCents = 0 })).ErrorCode());
        var stocked = await client.SendAsync("shop.stock", new { id = shop, item = Items.Bread, quantity = 500, priceCents = trader });
        Assert.True(stocked.Type() == "shop", stocked.ToString());
        Assert.Equal(100, stocked.Payload().GetProperty("items")[0].GetProperty("buyPercent").GetInt32());
        var perHour = stocked.Payload().GetProperty("customersPerHour").GetDouble();
        Assert.True(perHour > 1, perHour.ToString());

        // At the trader's price every customer buys.
        var balance = await client.BalanceAsync();
        factory.Time.Advance(TimeSpan.FromHours(2));
        var sold = (await client.SendAsync("shop.get", new { id = shop })).Payload();
        var pieces = sold.GetProperty("soldItems").GetInt64();
        Assert.InRange(pieces, (long)Math.Floor(perHour * 2) - 1, (long)Math.Ceiling(perHour * 2));
        Assert.Equal(pieces * trader, sold.GetProperty("soldCents").GetInt64());
        Assert.Equal(balance + pieces * trader, await client.BalanceAsync());
        Assert.Equal(500 - pieces, sold.GetProperty("items")[0].GetProperty("quantity").GetInt64());

        var taken = (await client.SendAsync("shop.take", new { id = shop, item = Items.Bread, quantity = 10 })).Payload();
        Assert.Equal(490 - pieces, taken.GetProperty("items")[0].GetProperty("quantity").GetInt64());
        Assert.Equal(10, (await client.InventoryAsync())[Items.Bread]);
        Assert.Equal("plot.pickedUp", (await client.SendAsync("plot.pickup", new { x = plot.X, y = plot.Y, id = shop })).Type());
        Assert.Equal(500 - pieces, (await client.InventoryAsync())[Items.Bread]);
    }

    [Fact]
    public async Task Offices_sell_their_services_to_neighbours_and_the_townsfolk_elect_a_mayor_who_taxes_and_picks_a_bonus()
    {
        using var owner = await PlayerAsync();
        using var neighbour = await PlayerAsync();
        var (first, second, _) = TownPlots();
        await BuyAsync(owner, first.X, first.Y);
        await BuyAsync(neighbour, second.X, second.Y);
        await GiveAsync(owner, Items.DesignOffice, 1);
        var office = await PlaceAsync(owner, first.X, first.Y, Items.DesignOffice, PlotLayout.Spawn.X + 5, 30);

        var offer = Result(await neighbour.SendAsync("city.get", new { x = second.X, y = second.Y }));
        Assert.Equal(JsonValueKind.Null, offer.ValueKind);
        var services = (await neighbour.SendAsync("city.get", new { x = second.X, y = second.Y })).Payload().GetProperty("state").GetProperty("services");
        Assert.Equal(office, services[0].GetProperty("id").GetString());
        Assert.Equal("own_service", (await owner.SendAsync("city.service", new { x = first.X, y = first.Y, id = office })).ErrorCode());

        var (ownerBefore, neighbourBefore) = (await owner.BalanceAsync(), await neighbour.BalanceAsync());
        var bought = Result(await neighbour.SendAsync("city.service", new { x = second.X, y = second.Y, id = office }));
        Assert.Equal(Items.Blueprint, bought.GetProperty("item").GetString());
        Assert.Equal(1, (await neighbour.InventoryAsync())[Items.Blueprint]);
        Assert.Equal(neighbourBefore - 30_00, await neighbour.BalanceAsync());
        Assert.Equal(ownerBefore + 30_00 * CityRules.ServiceOwnerPercent / 100, await owner.BalanceAsync());

        // What the owner gets back is worth less than what the trader pays for the goods, so buying from one's own office
        // through a second account only loses money.
        foreach (var (_, (item, quantity, price)) in CityRules.Services)
        {
            Assert.True(price * CityRules.ServiceOwnerPercent / 100 < quantity * NpcPrices.Buys.GetValueOrDefault(item, price), item);
        }

        // Both own a plot in the town, so both vote; the mayor rules the week after.
        var ownerName = await NameAsync(owner);
        Assert.Equal("not_candidate", (await neighbour.SendAsync("city.vote", new { x = second.X, y = second.Y, candidate = "Nikt" })).ErrorCode());
        Result(await neighbour.SendAsync("city.vote", new { x = second.X, y = second.Y, candidate = ownerName }));
        Result(await owner.SendAsync("city.vote", new { x = first.X, y = first.Y, candidate = ownerName }));
        Assert.Equal("not_mayor", (await owner.SendAsync("city.policy", new { x = first.X, y = first.Y, taxPercent = 5, bonus = "rail" })).ErrorCode());
        var week = CityRules.WeekOf(factory.Time.GetUtcNow());
        factory.Time.Advance(CityRules.WeekStart(week + 1) - factory.Time.GetUtcNow() + TimeSpan.FromHours(1));

        var hall = (await owner.SendAsync("city.get", new { x = first.X, y = first.Y })).Payload().GetProperty("state").GetProperty("hall");
        Assert.True(hall.GetProperty("isMayor").GetBoolean());
        Assert.Equal(ownerName, hall.GetProperty("mayor").GetString());
        Assert.Equal("bad_tax", (await owner.SendAsync("city.policy", new { x = first.X, y = first.Y, taxPercent = 11, bonus = "rail" })).ErrorCode());
        Assert.Equal("bad_bonus", (await owner.SendAsync("city.policy", new { x = first.X, y = first.Y, taxPercent = 5, bonus = "gold" })).ErrorCode());
        Assert.Equal("not_mayor", (await neighbour.SendAsync("city.policy", new { x = second.X, y = second.Y, taxPercent = 5, bonus = "rail" })).ErrorCode());
        var set = (await owner.SendAsync("city.policy", new { x = first.X, y = first.Y, taxPercent = 10, bonus = "rail" })).Payload().GetProperty("state");
        Assert.Equal(10, set.GetProperty("hall").GetProperty("taxPercent").GetInt32());

        // The rail bonus makes rail cheaper from every plot of the town.
        var station = (await neighbour.SendAsync("station.get", new { x = second.X, y = second.Y })).Payload();
        Assert.Equal(CityRules.CityRail * CityRules.MayorRail, station.GetProperty("rates").GetProperty("feeFactor").GetDouble(), 6);

        // The town takes a tenth of what the neighbour's shop sells: a fifth of that is the mayor's pay, the rest goes into the treasury.
        await GiveAsync(neighbour, Items.Shop, 1);
        await GiveAsync(neighbour, Items.Bread, 500);
        var shop = await PlaceAsync(neighbour, second.X, second.Y, Items.Shop, PlotLayout.Spawn.X + 10, 30);
        var trader = NpcPrices.Buys[Items.Bread];
        Assert.Equal("shop", (await neighbour.SendAsync("shop.stock", new { id = shop, item = Items.Bread, quantity = 500, priceCents = trader })).Type());
        (ownerBefore, neighbourBefore) = (await owner.BalanceAsync(), await neighbour.BalanceAsync());
        factory.Time.Advance(TimeSpan.FromHours(1));
        var sold = (await neighbour.SendAsync("shop.get", new { id = shop })).Payload().GetProperty("soldCents").GetInt64();
        Assert.True(sold > 0);
        var tax = sold / 10;
        Assert.Equal(ownerBefore + tax * TownHallRules.MayorSharePercent / 100, await owner.BalanceAsync());
        Assert.Equal(neighbourBefore + sold - tax, await neighbour.BalanceAsync());
        hall = (await owner.SendAsync("city.get", new { x = first.X, y = first.Y })).Payload().GetProperty("state").GetProperty("hall");
        Assert.Equal(tax - tax * TownHallRules.MayorSharePercent / 100, hall.GetProperty("politics").GetProperty("treasuryCents").GetInt64());
    }

    [Fact]
    public async Task Headquarters_on_a_city_plot_of_a_company_let_it_take_more_members_and_store_more()
    {
        using var client = await PlayerAsync();
        var (plot, _, _) = TownPlots();
        await BuyAsync(client, plot.X, plot.Y);
        Assert.Equal("company", (await client.SendAsync("company.create", new { name = "Biuro " + Guid.NewGuid().ToString("N")[..6] })).Type());
        var plain = (await client.SendAsync("company.get")).Payload();
        Assert.Equal(20, plain.GetProperty("maxMembers").GetInt32());
        Assert.False(plain.GetProperty("company").GetProperty("hq").GetBoolean());

        await GiveAsync(client, Items.CompanyHq, 2);
        Assert.Equal("hq_off_company", (await client.SendAsync("plot.place",
            new { x = plot.X, y = plot.Y, kind = Items.CompanyHq, tileX = PlotLayout.Spawn.X - 10, tileY = 30, rotation = 0 })).ErrorCode());
        await factory.CompanyLevelAsync(client.AccountId);
        Assert.Equal("company", (await client.SendAsync("company.addPlot", new { x = plot.X, y = plot.Y })).Type());
        await PlaceAsync(client, plot.X, plot.Y, Items.CompanyHq, PlotLayout.Spawn.X - 10, 30);
        Assert.Equal("has_one", (await client.SendAsync("plot.place",
            new { x = plot.X, y = plot.Y, kind = Items.CompanyHq, tileX = PlotLayout.Spawn.X - 15, tileY = 30, rotation = 0 })).ErrorCode());

        var hq = (await client.SendAsync("company.get")).Payload();
        Assert.Equal(30, hq.GetProperty("maxMembers").GetInt32());
        Assert.True(hq.GetProperty("company").GetProperty("hq").GetBoolean());
        Assert.Equal(150_000, hq.GetProperty("company").GetProperty("warehouse").GetProperty("capacity").GetInt64());
    }

    [Fact]
    public async Task Townspeople_do_not_pay_for_goods_what_the_trader_asks_for_them_so_buying_from_him_to_sell_in_a_shop_never_pays()
    {
        using var client = await PlayerAsync();
        var (plot, _, _) = TownPlots();
        await BuyAsync(client, plot.X, plot.Y);
        await GiveAsync(client, Items.Shop, 1);
        var item = Items.SortingMachine;
        await GiveAsync(client, item, 100);
        var shop = await PlaceAsync(client, plot.X, plot.Y, Items.Shop, PlotLayout.Spawn.X + 5, 30);
        var cheapest = new MarketPrices().CheapestSellsCents(item)!.Value;
        Assert.True(cheapest < NpcPrices.Sells[item]);
        Assert.True(cheapest < 3 * NpcPrices.Buys[item]);

        var stocked = await client.SendAsync("shop.stock", new { id = shop, item, quantity = 100, priceCents = cheapest });
        Assert.Equal(0, stocked.Payload().GetProperty("items")[0].GetProperty("buyPercent").GetInt32());
        factory.Time.Advance(TimeSpan.FromHours(10));
        Assert.Equal(0, (await client.SendAsync("shop.get", new { id = shop })).Payload().GetProperty("soldItems").GetInt64());
    }
}
