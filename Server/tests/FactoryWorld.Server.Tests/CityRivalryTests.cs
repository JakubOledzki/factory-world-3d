using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Players competing in towns (the chief supplier, tenders) and for the falling meteorites.</summary>
public sealed class CityRivalryTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static readonly Towns AllTowns = Towns.Load();
    private static int _towns;

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
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(sql, CancellationToken.None);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync();
            return 0;
        }, CancellationToken.None);
    }

    private static async Task<string> NameAsync(GameClient client) =>
        (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;

    /// <summary>Two city plots next to each other in one town of the map, a different town each time.</summary>
    private static ((int X, int Y) First, (int X, int Y) Second, Town Town) TownPlots()
    {
        var skip = Interlocked.Increment(ref _towns) * 11 + 20;
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

    /// <summary>Moves the clock to just after the start of the next town week, so a tender is open for bids.</summary>
    private void NextTownWeek() =>
        factory.Time.SetUtcNow(CityRules.WeekStart(CityRules.WeekOf(factory.Time.GetUtcNow()) + 1) + TimeSpan.FromHours(1));

    private static async Task<JsonElement> CityAsync(GameClient client, string type, object payload)
    {
        var reply = await client.SendAsync(type, payload);
        Assert.True(reply.Type() == "city", $"{type}: {reply}");
        return reply.Payload();
    }

    // --- Tenders ------------------------------------------------------------------------------------

    [Fact]
    public void A_tender_wants_a_few_days_of_a_good_at_no_more_than_the_towns_price()
    {
        var warsaw = AllTowns.All.First(t => t.Name == "Warszawa");
        var (item, quantity, cap) = TenderRules.For(warsaw, 2, 100, i => NpcPrices.Buys.TryGetValue(i, out var c) ? c : null);
        Assert.Equal((item, quantity, cap), TenderRules.For(warsaw, 2, 100, i => NpcPrices.Buys.TryGetValue(i, out var c) ? c : null));
        Assert.Contains(CityRules.Goods, g => g.Item == item && g.Level <= 2);
        Assert.True(quantity >= 50);
        Assert.Equal(CityRules.NeedPriceCents(NpcPrices.Buys[item], 2, false), cap);
        Assert.Equal(10, TenderRules.DepositCents(100, 1));
        Assert.Equal(1, TenderRules.DepositCents(1, 1));
        Assert.Equal(11, TenderRules.DepositCents(101, 1));
        // The trader asks far more, so buying from it to win a tender never pays.
        Assert.True(cap < NpcPrices.Markup * NpcPrices.Buys[item]);
    }

    [Fact]
    public async Task The_lowest_sealed_bid_wins_the_tender_and_the_others_get_their_deposits_back()
    {
        NextTownWeek();
        var (first, second, town) = TownPlots();
        using var dear = await PlayerAsync();
        using var cheap = await PlayerAsync();
        using var outsider = await PlayerAsync();
        await BuyAsync(dear, first.X, first.Y);
        await BuyAsync(cheap, second.X, second.Y);
        var start = (await dear.BalanceAsync(), await cheap.BalanceAsync());

        var tender = (await CityAsync(dear, "city.get", new { x = first.X, y = first.Y })).GetProperty("state").GetProperty("tender");
        Assert.Equal(TenderRules.Open, tender.GetProperty("state").GetString());
        Assert.True(tender.GetProperty("canBid").GetBoolean());
        var cap = tender.GetProperty("capCents").GetInt64();
        var quantity = tender.GetProperty("quantity").GetInt64();
        var item = tender.GetProperty("item").GetString()!;

        Assert.Equal("not_townsman", (await outsider.SendAsync("city.tenderBid", new { x = first.X, y = first.Y, priceCents = cap })).ErrorCode());
        Assert.Equal("bad_bid", (await dear.SendAsync("city.tenderBid", new { x = first.X, y = first.Y, priceCents = cap + 1 })).ErrorCode());
        Assert.Equal("bad_bid", (await dear.SendAsync("city.tenderBid", new { x = first.X, y = first.Y, priceCents = 0 })).ErrorCode());
        Assert.Equal("no_bid", (await dear.SendAsync("city.tenderWithdraw", new { x = first.X, y = first.Y })).ErrorCode());

        await CityAsync(dear, "city.tenderBid", new { x = first.X, y = first.Y, priceCents = cap });
        Assert.Equal(start.Item1 - TenderRules.DepositCents(cap, quantity), await dear.BalanceAsync());
        // A changed bid gives the old deposit back and takes the new one.
        var changed = await CityAsync(dear, "city.tenderBid", new { x = first.X, y = first.Y, priceCents = cap - 10 });
        Assert.Equal(start.Item1 - TenderRules.DepositCents(cap - 10, quantity), await dear.BalanceAsync());
        Assert.Equal(cap - 10, changed.GetProperty("state").GetProperty("tender").GetProperty("myBid").GetProperty("priceCents").GetInt64());

        // Withdrawn and made again, the bid counts from the later moment.
        await CityAsync(cheap, "city.tenderBid", new { x = second.X, y = second.Y, priceCents = cap - 20 });
        await CityAsync(cheap, "city.tenderWithdraw", new { x = second.X, y = second.Y });
        Assert.Equal(start.Item2, await cheap.BalanceAsync());
        var bids = await CityAsync(cheap, "city.tenderBid", new { x = second.X, y = second.Y, priceCents = cap - 20 });
        Assert.Equal(2, bids.GetProperty("state").GetProperty("tender").GetProperty("bids").GetInt32());
        // Nobody sees the others' prices.
        Assert.Equal(JsonValueKind.Null, bids.GetProperty("state").GetProperty("tender").GetProperty("winner").ValueKind);

        factory.Time.SetUtcNow(CityRules.WeekStart(CityRules.WeekOf(factory.Time.GetUtcNow())) + TenderRules.Bidding + TimeSpan.FromMinutes(1));
        var won = (await CityAsync(dear, "city.get", new { x = first.X, y = first.Y })).GetProperty("state").GetProperty("tender");
        Assert.Equal(TenderRules.Won, won.GetProperty("state").GetString());
        Assert.Equal(await NameAsync(cheap), won.GetProperty("winner").GetString());
        Assert.Equal(cap - 20, won.GetProperty("priceCents").GetInt64());
        Assert.False(won.GetProperty("isWinner").GetBoolean());
        Assert.Equal(start.Item1, await dear.BalanceAsync());
        Assert.Contains((await dear.SendAsync("chat.get")).Payload().GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("text").GetString()!.Contains(town.Name));

        Assert.Equal("tender_closed", (await dear.SendAsync("city.tenderBid", new { x = first.X, y = first.Y, priceCents = 1 })).ErrorCode());
        await GiveAsync(dear, item, quantity);
        Assert.Equal("not_winner", (await dear.SendAsync("city.tenderDeliver", new { x = first.X, y = first.Y, quantity })).ErrorCode());

        var half = quantity / 2;
        await GiveAsync(cheap, item, quantity + half);
        var part = await CityAsync(cheap, "city.tenderDeliver", new { x = second.X, y = second.Y, quantity = half });
        Assert.Equal(half * (cap - 20), part.GetProperty("result").GetProperty("cents").GetInt64());
        // More than is still wanted takes only the rest.
        var done = await CityAsync(cheap, "city.tenderDeliver", new { x = second.X, y = second.Y, quantity });
        Assert.Equal(quantity - half, done.GetProperty("result").GetProperty("quantity").GetInt64());
        Assert.Equal(TenderRules.Done, done.GetProperty("state").GetProperty("tender").GetProperty("state").GetString());
        Assert.Equal(start.Item2 + quantity * (cap - 20), await cheap.BalanceAsync());
        Assert.Equal(half, (await cheap.InventoryAsync())[item]);
        Assert.Equal("not_winner", (await cheap.SendAsync("city.tenderDeliver", new { x = second.X, y = second.Y, quantity = 1 })).ErrorCode());
        // Delivered goods make the town grow.
        Assert.Equal(quantity * (cap - 20), done.GetProperty("state").GetProperty("myCents").GetInt64());
    }

    [Fact]
    public async Task A_winner_who_does_not_deliver_everything_by_the_weeks_end_loses_the_deposit()
    {
        NextTownWeek();
        var (first, _, _) = TownPlots();
        using var player = await PlayerAsync();
        await BuyAsync(player, first.X, first.Y);
        var start = await player.BalanceAsync();
        var tender = (await CityAsync(player, "city.get", new { x = first.X, y = first.Y })).GetProperty("state").GetProperty("tender");
        var cap = tender.GetProperty("capCents").GetInt64();
        var quantity = tender.GetProperty("quantity").GetInt64();
        await CityAsync(player, "city.tenderBid", new { x = first.X, y = first.Y, priceCents = cap });

        var week = CityRules.WeekOf(factory.Time.GetUtcNow());
        factory.Time.SetUtcNow(CityRules.WeekStart(week) + TenderRules.Bidding + TimeSpan.FromMinutes(1));
        await GiveAsync(player, tender.GetProperty("item").GetString()!, 1);
        await CityAsync(player, "city.tenderDeliver", new { x = first.X, y = first.Y, quantity = 1 });

        factory.Time.SetUtcNow(CityRules.WeekStart(week + 1) + TimeSpan.FromMinutes(1));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // What the tender clock does every minute.
            await scope.ServiceProvider.GetRequiredService<CityService>().SettleTendersAsync(CancellationToken.None);
        }

        Assert.Equal(start - TenderRules.DepositCents(cap, quantity) + cap, await player.BalanceAsync());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            await using var command = await db.CommandAsync("SELECT state FROM world.town_tenders WHERE week = @week AND winner_id = @player", CancellationToken.None);
            command.Parameters.AddWithValue("week", week);
            command.Parameters.AddWithValue("player", player.AccountId);
            Assert.Equal(TenderRules.Failed, await command.ExecuteScalarAsync() as string);
        }

        // This week's tender is a new one.
        var fresh = (await CityAsync(player, "city.get", new { x = first.X, y = first.Y })).GetProperty("state").GetProperty("tender");
        Assert.Equal(week + 1, fresh.GetProperty("week").GetInt32());
        Assert.Equal(TenderRules.Open, fresh.GetProperty("state").GetString());
    }

    // --- The chief supplier ------------------------------------------------------------------------

    [Fact]
    public async Task Who_delivered_the_most_last_week_is_the_chief_supplier_and_gets_more_for_deliveries()
    {
        NextTownWeek();
        var (first, second, town) = TownPlots();
        using var chief = await PlayerAsync();
        using var other = await PlayerAsync();
        await BuyAsync(chief, first.X, first.Y);
        await BuyAsync(other, second.X, second.Y);
        var week = CityRules.WeekOf(factory.Time.GetUtcNow());
        const string insert = "INSERT INTO world.town_week_deliveries (town_x, town_y, week, player_id, delivered_cents) VALUES (@x, @y, @week, @player, @cents)";
        await SqlAsync(insert, ("x", town.X), ("y", town.Y), ("week", week - 1), ("player", chief.AccountId), ("cents", CityRules.SupplierMinCents + 100_00));
        await SqlAsync(insert, ("x", town.X), ("y", town.Y), ("week", week - 1), ("player", other.AccountId), ("cents", CityRules.SupplierMinCents));

        var view = (await CityAsync(chief, "city.get", new { x = first.X, y = first.Y })).GetProperty("state");
        var supplier = view.GetProperty("supplier");
        Assert.Equal(await NameAsync(chief), supplier.GetProperty("name").GetString());
        Assert.True(supplier.GetProperty("isMe").GetBoolean());
        Assert.False((await CityAsync(other, "city.get", new { x = second.X, y = second.Y })).GetProperty("state").GetProperty("supplier").GetProperty("isMe").GetBoolean());

        var need = view.GetProperty("needs")[0];
        var item = need.GetProperty("id").GetString()!;
        var price = need.GetProperty("priceCents").GetInt64();
        await GiveAsync(chief, item, 1);
        await GiveAsync(other, item, 1);
        var paid = await CityAsync(chief, "city.deliver", new { x = first.X, y = first.Y, item, quantity = 1 });
        Assert.Equal(CityRules.SupplierPriceCents(price), paid.GetProperty("result").GetProperty("cents").GetInt64());
        Assert.True(CityRules.SupplierPriceCents(price) > price);
        var normal = await CityAsync(other, "city.deliver", new { x = second.X, y = second.Y, item, quantity = 1 });
        Assert.Equal(price, normal.GetProperty("result").GetProperty("cents").GetInt64());

        // This week's race for next week's title.
        var race = normal.GetProperty("state").GetProperty("supplier").GetProperty("race").EnumerateArray().ToList();
        Assert.Equal(2, race.Count);
        Assert.Equal(await NameAsync(chief), race[0].GetProperty("name").GetString());
        Assert.Equal(price, normal.GetProperty("state").GetProperty("supplier").GetProperty("myCents").GetInt64());
    }

    [Fact]
    public async Task Too_little_delivered_last_week_makes_nobody_the_chief_supplier()
    {
        NextTownWeek();
        var (first, _, town) = TownPlots();
        using var player = await PlayerAsync();
        await BuyAsync(player, first.X, first.Y);
        await SqlAsync("INSERT INTO world.town_week_deliveries (town_x, town_y, week, player_id, delivered_cents) VALUES (@x, @y, @week, @player, @cents)",
            ("x", town.X), ("y", town.Y), ("week", CityRules.WeekOf(factory.Time.GetUtcNow()) - 1), ("player", player.AccountId),
            ("cents", CityRules.SupplierMinCents - 1));
        var supplier = (await CityAsync(player, "city.get", new { x = first.X, y = first.Y })).GetProperty("state").GetProperty("supplier");
        Assert.Equal(JsonValueKind.Null, supplier.GetProperty("name").ValueKind);
    }

    // --- Meteorites ---------------------------------------------------------------------------------

    [Fact]
    public async Task Players_with_a_plot_near_the_meteorite_dig_it_a_piece_at_a_time()
    {
        // A fresh period, so this test has the meteorite to itself.
        factory.Time.SetUtcNow(MeteorRules.SlotStart(MeteorRules.SlotOf(factory.Time.GetUtcNow()) + 1) + TimeSpan.FromMinutes(1));
        using var digger = await PlayerAsync();
        using var far = await PlayerAsync();
        var meteor = (await digger.SendAsync("meteor.get")).Payload().GetProperty("state");
        var (mx, my) = (meteor.GetProperty("x").GetInt32(), meteor.GetProperty("y").GetInt32());
        Assert.False(meteor.GetProperty("inReach").GetBoolean());
        Assert.Equal(MeteorRules.Iron, meteor.GetProperty("ironLeft").GetInt64());
        Assert.Equal("meteor_too_far", (await digger.SendAsync("meteor.dig")).ErrorCode());

        var bought = false;
        for (var r = 0; r <= 10 && !bought; r++)
        {
            for (var dy = -r; dy <= r && !bought; dy++)
            {
                for (var dx = -r; dx <= r && !bought; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Map.Contains(mx + dx, my + dy))
                    {
                        bought = (await digger.SendAsync("world.buy", new { x = mx + dx, y = my + dy })).Type() == "world.bought";
                    }
                }
            }
        }

        Assert.True(bought);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // A first plot comes with the tool its land needs, which may be a pickaxe; otherwise the player needs one first.
            var equipment = scope.ServiceProvider.GetRequiredService<IEquipment>();
            if (!Items.Pickaxes.Keys.Any((await equipment.ToolsAsync(digger.AccountId, CancellationToken.None)).Contains))
            {
                Assert.Equal("wrong_tool", (await digger.SendAsync("meteor.dig")).ErrorCode());
                await equipment.GiveToolAsync(digger.AccountId, Items.Pickaxe, CancellationToken.None);
            }
        }

        var dug = await digger.SendAsync("meteor.dig");
        Assert.True(dug.Type() == "meteor", dug.ToString());
        Assert.Equal(Items.MeteoricIron, dug.Payload().GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(MeteorRules.Iron - 1, dug.Payload().GetProperty("state").GetProperty("ironLeft").GetInt64());
        Assert.Equal("meteor_cooldown", (await digger.SendAsync("meteor.dig")).ErrorCode());
        Assert.Equal("meteor_too_far", (await far.SendAsync("meteor.dig")).ErrorCode());

        factory.Time.Advance(MeteorRules.DigEvery);
        var again = (await digger.SendAsync("meteor.dig")).Payload().GetProperty("state");
        Assert.Equal(MeteorRules.Iron - 2, again.GetProperty("ironLeft").GetInt64());
        Assert.Equal(2, again.GetProperty("mine").GetInt64());
        Assert.Equal(2, again.GetProperty("diggers")[0].GetProperty("pieces").GetInt64());
        Assert.Equal(2, (await digger.InventoryAsync())[Items.MeteoricIron]);
        Assert.Contains((await far.SendAsync("chat.get")).Payload().GetProperty("messages").EnumerateArray(),
            m => m.GetProperty("text").GetString()!.StartsWith("Meteoryt spadł"));

        // Dug out, it gives no more.
        await SqlAsync("UPDATE world.meteors SET iron_left = 1 WHERE slot = @slot", ("slot", MeteorRules.SlotOf(factory.Time.GetUtcNow())));
        factory.Time.Advance(MeteorRules.DigEvery);
        Assert.Equal(0, (await digger.SendAsync("meteor.dig")).Payload().GetProperty("state").GetProperty("ironLeft").GetInt64());
        factory.Time.Advance(MeteorRules.DigEvery);
        Assert.Equal("meteor_empty", (await digger.SendAsync("meteor.dig")).ErrorCode());

        // The next period brings a new one.
        factory.Time.Advance(MeteorRules.Every);
        Assert.Equal(MeteorRules.Iron, (await far.SendAsync("meteor.get")).Payload().GetProperty("state").GetProperty("ironLeft").GetInt64());
    }

    [Fact]
    public void A_meteorite_lands_on_land_near_where_it_was_aimed()
    {
        var warsaw = AllTowns.All.First(t => t.Name == "Warszawa");
        var random = new Random(7);
        for (var i = 0; i < 50; i++)
        {
            var (x, y) = MeteorRules.Landing(Map, warsaw.X, warsaw.Y, random);
            Assert.True(Math.Abs(x - warsaw.X) <= MeteorRules.Scatter && Math.Abs(y - warsaw.Y) <= MeteorRules.Scatter);
            Assert.NotEqual(Biome.Ocean, Map.BiomeAt(x, y));
        }

        Assert.Equal(MeteorRules.SlotStart(10) + MeteorRules.Every, MeteorRules.SlotStart(11));
        Assert.Equal(10, MeteorRules.SlotOf(MeteorRules.SlotStart(10) + TimeSpan.FromHours(7)));
    }
}
