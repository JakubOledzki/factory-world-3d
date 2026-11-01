using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Husbandry and gastronomy: what farmers and cooks make that the rest of the game needs.</summary>
public sealed class HusbandryGastronomyTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    // --- The rules, without a database ----------------------------------------------------------

    [Fact]
    public void A_slaughterhouse_gives_hide_tallow_and_bones_beside_the_meat_and_stops_when_they_fill_up()
    {
        var slaughterhouse = Processing.ByKind[Items.Slaughterhouse];
        var start = DateTimeOffset.UnixEpoch;
        var s = Processing.Start(slaughterhouse, MachineState.Empty with { Recipe = Items.Meat, Inputs = [3] }, start);
        var after = Processing.Advance(slaughterhouse, s, start + TimeSpan.FromMinutes(3));
        Assert.Equal(36, after.Output);
        Assert.Equal([3, 12, 18], after.Extras!);
        Assert.True(after.HasOutput);

        // With no room left for the bones, it waits.
        var full = Processing.Start(slaughterhouse,
            MachineState.Empty with { Recipe = Items.Meat, Inputs = [3], Extras = [0, 0, Processing.MaxOutput - 6] }, start);
        Assert.Equal(2, Processing.Advance(slaughterhouse, full, start + TimeSpan.FromMinutes(3)).Inputs[0]);
    }

    [Fact]
    public void A_fed_crew_works_faster_and_the_canteen_feeds_only_machines_with_fire_or_power()
    {
        Assert.True(Canteens.Eats(Items.Bakery));
        Assert.False(Canteens.Eats(Items.Slaughterhouse));
        Assert.False(Canteens.Eats(Items.Canteen));
        var bread = Processing.ByKind[Items.Bakery].Recipes.First(r => r.Id == Items.Bread).Time;
        var fed = Processing.For(Items.Bakery, 0, fed: true)!.Recipes.First(r => r.Id == Items.Bread).Time;
        Assert.Equal(bread / Canteens.Bonus, fed);
        // Workers' lunch is four portions: with two machines on the plot it lasts 20 minutes.
        Assert.Equal(TimeSpan.FromMinutes(20), Canteens.Lasts(Canteens.Portions[Items.WorkersLunch], 2));
    }

    [Fact]
    public void Meals_give_hand_work_a_bonus_for_a_while_and_the_fine_dishes_come_from_research()
    {
        Assert.Equal(30, Items.Meals[Items.MinersBreakfast].Bonuses[Industries.Mining]);
        Assert.Equal(30, Items.Meals[Items.LumberjackGoulash].Bonuses[Industries.Forestry]);
        Assert.All(Industries.HandWork, i => Assert.Equal(20, Items.Meals[Items.CheeseSandwich].Bonuses[i]));
        Assert.True(Items.IsFineDish(Items.RoastBeef) && Items.IsFineDish(Items.Cheesecake));
        Assert.Equal(TechTree.FineCuisine, TechTree.ByItem[Items.RoastBeef].Id);
        Assert.Equal(TechTree.Butchery, TechTree.ByItem[Items.Slaughterhouse].Id);
        Assert.Equal(TechTree.WorksCanteens, TechTree.ByItem[Items.Canteen].Id);
        Assert.Contains(TechTree.Branches, b => b.Id == "husbandry");
    }

    [Fact]
    public void Animals_leave_manure_and_breed_and_a_horse_eats_concentrate_and_hauls()
    {
        Assert.Equal(2, Husbandry.ById[Items.Cow].Manure);
        Assert.Equal(10, Husbandry.ById[Items.Cow].BreedEvery);
        var horse = Husbandry.ById[Items.Horse];
        Assert.True(horse.Works);
        Assert.Equal([Items.ConcentrateFeed], horse.Diet);
        Assert.True(Husbandry.HorseBonus > 1);
        Assert.Equal(4.0 / 3, Farming.ManureSpeed, 6);
    }

    [Fact]
    public void Ethanol_burns_in_diesel_generators_and_snowmobiles_and_horse_carts_need_feed()
    {
        Assert.True(PowerRules.Burns(Items.DieselGenerator, Items.Ethanol));
        Assert.True(PowerRules.Fuels.ContainsKey(Items.Ethanol));
        Assert.Equal(2, SledRules.EthanolFuel(50));
        Assert.Equal(5, SledRules.HorseFeed(41));
        Assert.Equal(1, SledRules.HorseFeed(3));
    }

    [Fact]
    public void A_festival_makes_the_town_pay_more_and_industries_need_provisions()
    {
        Assert.Equal((long)Math.Round(150 * CityRules.FestivalBonus), CityRules.NeedPriceCents(100, 0, false, true));
        Assert.Contains(Items.Provisions, Workshop.Find(Items.BlastFurnace)!.Ingredients.Select(i => i.Item));
        Assert.Contains(Items.Provisions, Workshop.Find(Items.Refinery)!.Ingredients.Select(i => i.Item));
    }

    [Fact]
    public void Provisions_are_researched_in_the_food_branch_right_after_the_field_kitchen_that_packs_them()
    {
        var provisions = TechTree.ById[TechTree.Provisions];
        Assert.Equal("food", TechTree.BranchOf[TechTree.Provisions].Id);
        Assert.Equal(TechTree.Cooking, Assert.Single(provisions.Requires));
        Assert.Same(provisions, TechTree.ByItem[Items.Provisions]);
        Assert.NotNull(Processing.Machines.Single(m => m.Kind == Items.Kitchen).Recipe(Items.Provisions));
    }

    // --- On a plot -------------------------------------------------------------------------------

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

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 12000;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    /// <summary>Buys a grassland plot and finds a tile with a free 3 × 3 square around it and room for more east of it.</summary>
    private async Task<(int X, int Y, int TileX, int TileY)> PlotAsync(GameClient client)
    {
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
        for (var ty = 25; ty < PlotLayout.Size - 10; ty++)
        {
            for (var tx = 10; tx < PlotLayout.Size - 10; tx++)
            {
                var free = true;
                for (var dy = -1; dy <= 1 && free; dy++)
                {
                    for (var dx = -1; dx <= 4 && free; dx++)
                    {
                        free = bytes[(ty + dy) * PlotLayout.Size + tx + dx] == (byte)TileObject.None && (tx + dx, ty + dy) != PlotLayout.Spawn;
                    }
                }

                if (free)
                {
                    return (x, y, tx, ty);
                }
            }
        }

        throw new InvalidOperationException("No free square.");
    }

    private async Task ResearchedAsync(GameClient client, string technology)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryWorld.Modules.Abstractions.GameDb>();
        await using var command = await db.CommandAsync(
            "INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded) VALUES (@owner, @id, @at, @at, true)", CancellationToken.None);
        command.Parameters.AddWithValue("owner", client.AccountId);
        command.Parameters.AddWithValue("id", technology);
        command.Parameters.AddWithValue("at", factory.Time.GetUtcNow() - TimeSpan.FromMinutes(1));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_slaughterhouse_on_a_plot_cuts_a_cow_and_everything_it_gives_goes_to_the_inventory()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);
        await GiveAsync(client, Items.Slaughterhouse, 1);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Slaughterhouse, tileX = tx, tileY = ty });
        Assert.Equal("plot.placed", placed.Type());
        var machine = placed.Payload().GetProperty("object").GetProperty("id").GetString()!;
        await GiveAsync(client, Items.Cow, 1);
        Assert.Equal("locked", (await client.SendAsync("machine.recipe", new { machine, recipe = Items.Meat })).ErrorCode());
        await ResearchedAsync(client, TechTree.Butchery);
        Assert.Equal("machine", (await client.SendAsync("machine.recipe", new { machine, recipe = Items.Meat })).Type());
        Assert.Equal("machine", (await client.SendAsync("machine.load", new { machine, item = Items.Cow, quantity = 1 })).Type());

        factory.Time.Advance(TimeSpan.FromSeconds(61));
        var state = (await client.SendAsync("machine.get", new { machine })).Payload();
        Assert.Equal(12, state.GetProperty("output").GetProperty("quantity").GetInt32());
        Assert.Equal(3, state.GetProperty("extras").GetArrayLength());

        await client.SendAsync("machine.take", new { machine, what = "output" });
        var inventory = await client.InventoryAsync();
        Assert.Equal(12, inventory.GetValueOrDefault(Items.Meat));
        Assert.Equal(1, inventory.GetValueOrDefault(Items.Hide));
        Assert.Equal(4, inventory.GetValueOrDefault(Items.Tallow));
        Assert.Equal(6, inventory.GetValueOrDefault(Items.Bones));
    }

    [Fact]
    public async Task A_canteen_is_stocked_with_dishes_and_keeps_track_of_how_long_they_feed_the_plot()
    {
        using var client = await PlayerAsync();
        var (x, y, tx, ty) = await PlotAsync(client);
        await GiveAsync(client, Items.Canteen, 1);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Canteen, tileX = tx, tileY = ty });
        Assert.Equal("plot.placed", placed.Type());
        var canteen = placed.Payload().GetProperty("object").GetProperty("id").GetString()!;
        await GiveAsync(client, Items.Bakery, 1);
        Assert.Equal("plot.placed", (await client.SendAsync("plot.place", new { x, y, kind = Items.Bakery, tileX = tx + 3, tileY = ty })).Type());

        var empty = (await client.SendAsync("canteen.get", new { canteen })).Payload();
        Assert.False(empty.GetProperty("fed").GetBoolean());
        Assert.Equal(1, empty.GetProperty("crews").GetInt32());

        await GiveAsync(client, Items.WorkersLunch, 3);
        await GiveAsync(client, Items.Coal, 1);
        Assert.Equal("not_food", (await client.SendAsync("canteen.load", new { canteen, item = Items.Coal, quantity = 1 })).ErrorCode());
        var loaded = (await client.SendAsync("canteen.load", new { canteen, item = Items.WorkersLunch, quantity = 3 })).Payload();
        Assert.True(loaded.GetProperty("fed").GetBoolean());
        // Twelve portions feed the bakery's crew for two hours.
        Assert.Equal(TimeSpan.FromHours(2).TotalMilliseconds, loaded.GetProperty("fedUntil").GetInt64() - loaded.GetProperty("serverTime").GetInt64());
        Assert.Equal(0, (await client.InventoryAsync()).GetValueOrDefault(Items.WorkersLunch));

        factory.Time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        Assert.False((await client.SendAsync("canteen.get", new { canteen })).Payload().GetProperty("fed").GetBoolean());
    }

    [Fact]
    public async Task A_meal_eaten_on_the_go_shows_on_the_character_until_it_wears_off()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.MinersBreakfast, 1);
        await GiveAsync(client, Items.Coal, 1);
        Assert.Equal("not_a_meal", (await client.SendAsync("character.eat", new { item = Items.Coal })).ErrorCode());
        var ate = (await client.SendAsync("character.eat", new { item = Items.MinersBreakfast })).Payload();
        var meal = ate.GetProperty("meal");
        Assert.Equal(Items.MinersBreakfast, meal.GetProperty("id").GetString());
        Assert.Equal(30, meal.GetProperty("bonuses").GetProperty(Industries.Mining).GetInt32());
        Assert.Equal(0, (await client.InventoryAsync()).GetValueOrDefault(Items.MinersBreakfast));

        factory.Time.Advance(TimeSpan.FromMinutes(31));
        var later = (await client.SendAsync("character.get")).Payload();
        Assert.Equal(JsonValueKind.Null, later.GetProperty("meal").ValueKind);
    }
}
