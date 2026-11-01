using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>The production line a bot builds, without a database.</summary>
public sealed class BotFactoryRulesTests
{
    [Fact]
    public void A_bot_line_is_a_chest_an_inserter_two_belts_an_inserter_the_machine_an_inserter_and_a_chest_on_power()
    {
        var layout = BotFactory.Layout(Items.Furnace);
        var row = layout.Where(p => p.Dy == 0).OrderBy(p => p.Dx).Select(p => p.Kind);
        Assert.Equal(
            [Items.WoodenChest, Items.Inserter, Items.Belt, Items.Belt, Items.Inserter, Items.Furnace, Items.Inserter, Items.WoodenChest],
            row);

        // Each pole powers 5 × 5 tiles: every inserter is in reach of one, and the poles reach each other.
        var poles = layout.Where(p => p.Kind == Items.Pole).ToList();
        Assert.Equal(2, poles.Count);
        Assert.All(layout.Where(p => p.Kind == Items.Inserter), i =>
            Assert.Contains(poles, p => Math.Abs(p.Dx - i.Dx) <= 2 && Math.Abs(p.Dy - i.Dy) <= 2));
        Assert.True(Math.Abs(poles[0].Dx - poles[1].Dx) <= 7);
        Assert.Single(layout, p => p.Kind == Items.Generator);

        // Nothing overlaps, the 2 × 2 machine included, and all of it fits in the line's rectangle.
        var tiles = layout.SelectMany(p => Footprints.Tiles(p.Kind, p.Dx, p.Dy)).ToList();
        Assert.Equal(tiles.Count, tiles.Distinct().Count());
        Assert.All(tiles, t => Assert.True(t.X is >= 0 and < BotFactory.Width && t.Y is >= 0 and < BotFactory.Depth, t.ToString()));

        var kit = new BotLine(Items.Mill, Items.Flour, [Items.Wheat], [Items.Wood], [Items.Flour]).Kit.ToDictionary(k => k.Item, k => k.Quantity);
        Assert.Equal(3, kit[Items.Inserter]);
        Assert.Equal(2, kit[Items.Belt]);
        Assert.Equal(1, kit[Items.Mill]);
    }

    [Fact]
    public void Every_trade_but_the_trader_has_a_line_and_researches_what_it_is_made_of_first()
    {
        Assert.Null(BotRules.Line(BotTrade.Trader));
        foreach (var trade in Enum.GetValues<BotTrade>().Where(t => t != BotTrade.Trader))
        {
            Assert.NotNull(BotRules.Line(trade));
            Assert.Contains("automation", BotRules.Research(trade));
        }

        Assert.Equal(Items.Sawmill, BotRules.Line(BotTrade.Forester)!.Machine);
        Assert.Equal(Items.Mill, BotRules.Line(BotTrade.Farmer)!.Machine);
        Assert.Equal(Items.Furnace, BotRules.Line(BotTrade.Smelter)!.Machine);
        // What the lines make goes to the players' market.
        Assert.Contains(Items.Planks, BotRules.Goods);
        Assert.Contains(Items.Flour, BotRules.Goods);
        Assert.Contains(Items.IronIngot, BotRules.Goods);
    }

    [Fact]
    public void A_line_goes_on_the_free_ground_nearest_to_the_spawn_with_room_all_round()
    {
        var blocked = new bool[40, 40];
        var spawn = (X: 20, Y: 2);
        var spot = BotFactory.FindSpot(blocked, spawn);
        Assert.NotNull(spot);
        Assert.True(spot.Value.Y > spawn.Y + 1);
        // Not across the path straight down from the spawn.
        Assert.True(spawn.X < spot.Value.X - 1 || spawn.X > spot.Value.X + BotFactory.Width);

        // A tree in the middle of that spot sends the line elsewhere, clear of the tree and a tile around it.
        var (tx, ty) = (spot.Value.X + 4, spot.Value.Y + 1);
        blocked[tx, ty] = true;
        var other = BotFactory.FindSpot(blocked, spawn)!.Value;
        Assert.False(tx >= other.X - 1 && tx <= other.X + BotFactory.Width && ty >= other.Y - 1 && ty <= other.Y + BotFactory.Depth);

        // No room at all: no line.
        var full = new bool[40, 40];
        for (var x = 0; x < 40; x += 4)
        {
            for (var y = 0; y < 40; y++)
            {
                full[x, y] = true;
            }
        }

        Assert.Null(BotFactory.FindSpot(full, spawn));
    }
}

/// <summary>A bot building its line and working it, on a database of its own.</summary>
public sealed class BotLineTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    private static long StoredIn(JsonElement storage, string item) =>
        storage.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("id").GetString() == item)
            .Sum(i => i.GetProperty("quantity").GetInt64());

    private async Task GiveAsync(Bot bot, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(bot.Row.AccountId, item, quantity, CancellationToken.None);
    }

    private static async Task<Dictionary<string, JsonElement>> StoragesAsync(Bot bot, int x, int y) =>
        (await bot.Client.SendAsync("logistics.get", new { x, y }, CancellationToken.None)).Payload
            .GetProperty("storages").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);

    private static async Task<(int X, int Y, JsonElement Plot)?> LinePlotAsync(Bot bot)
    {
        var info = await bot.Client.SendAsync("world.info", null, CancellationToken.None);
        foreach (var plot in info.Payload.GetProperty("plots").EnumerateArray())
        {
            var (x, y) = (plot.GetProperty("x").GetInt32(), plot.GetProperty("y").GetInt32());
            var inside = await bot.Client.SendAsync("plot.enter", new { x, y }, CancellationToken.None);
            var kinds = inside.Payload.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("kind").GetString()).ToList();
            if (kinds.Count(k => k == Items.Inserter) >= 3 && kinds.Contains(Items.Generator) && kinds.Count(k => k == Items.Belt) >= 2)
            {
                return (x, y, inside.Payload);
            }
        }

        return null;
    }

    [Fact]
    public async Task A_smelter_bot_researches_builds_a_furnace_line_with_inserters_and_belts_and_it_smelts_while_the_bot_is_away()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Smelter);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 100_000, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 20_000_00, "test", CancellationToken.None);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            // What a smelter would have bought from miners over a few sessions.
            foreach (var (item, quantity) in new[]
                     {
                         (Items.IronOre, 300L), (Items.Coal, 150L), (Items.Wood, 300L), (Items.Stone, 200L),
                         (Items.IronIngot, 120L), (Items.CopperIngot, 100L), (Items.Nails, 60L),
                     })
            {
                await inventory.AddAsync(bot.Row.AccountId, item, quantity, CancellationToken.None);
            }
        }

        var actions = new List<string?>();
        for (var i = 0; i < 600 && !bot.HasLine; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            actions.Add(await Engine.StepAsync(bot.Profile.Name, CancellationToken.None));
        }

        Assert.True(bot.HasLine, "The bot built no line: " + string.Join(", ", actions.GroupBy(a => a ?? "-").Select(g => $"{g.Key} ×{g.Count()}")));
        var line = await LinePlotAsync(bot);
        if (line is null)
        {
            var info = await bot.Client.SendAsync("world.info", null, CancellationToken.None);
            var dump = new List<string>();
            foreach (var p in info.Payload.GetProperty("plots").EnumerateArray())
            {
                var inside = await bot.Client.SendAsync("plot.enter", new { x = p.GetProperty("x").GetInt32(), y = p.GetProperty("y").GetInt32() }, CancellationToken.None);
                dump.Add(string.Join(",", inside.Payload.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("kind").GetString())));
            }

            Assert.Fail($"No whole line on its plots: {string.Join(" | ", dump)}; last steps: {string.Join(", ", actions.TakeLast(15))}; help: {bot.Help?.Reason}");
        }

        var (x, y, plot) = line!.Value;
        var objects = plot.GetProperty("objects").EnumerateArray().ToList();
        var furnace = objects.First(o => o.GetProperty("kind").GetString() == Items.Furnace
            && objects.Count(c => c.GetProperty("kind").GetString() == Items.WoodenChest
                && c.GetProperty("tileY").GetInt32() == o.GetProperty("tileY").GetInt32()) >= 2);
        var (fx, fy) = (furnace.GetProperty("tileX").GetInt32(), furnace.GetProperty("tileY").GetInt32());
        string IdAt(string kind, int tx, int ty) => objects.First(o => o.GetProperty("kind").GetString() == kind
            && o.GetProperty("tileX").GetInt32() == tx && o.GetProperty("tileY").GetInt32() == ty).GetProperty("id").GetString()!;
        var input = IdAt(Items.WoodenChest, fx - BotFactory.MachineX, fy);
        var output = IdAt(Items.WoodenChest, fx - BotFactory.MachineX + BotFactory.OutputX, fy);

        // The ore it was given went into its furnaces meanwhile; miners have brought more.
        await GiveAsync(bot, Items.IronOre, 200);
        await GiveAsync(bot, Items.Coal, 60);

        // The bot fills the line on its next looks: ore in the chest, coal in the furnace, fuel in the generator.
        Dictionary<string, JsonElement> storages = [];
        for (var i = 0; i < 30; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            storages = await StoragesAsync(bot, x, y);
            if (StoredIn(storages[input], Items.IronOre) > 0 && StoredIn(storages[output], Items.IronIngot) == 0)
            {
                break;
            }
        }

        var ore = StoredIn(storages[input], Items.IronOre);
        Assert.True(ore > 0, storages[input].ToString());

        // Then it goes away, and the inserters, the belts and the furnace go on without it.
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        storages = await StoragesAsync(bot, x, y);
        Assert.True(StoredIn(storages[output], Items.IronIngot) > 0, storages[output].ToString());
        Assert.True(StoredIn(storages[input], Items.IronOre) < ore, storages[input].ToString());

        // An inserter goes missing: the line stands still, and after a few looks the bot takes it apart and calls for help over
        // its plot: the inserter that feeds the furnace is to go back.
        await GiveAsync(bot, Items.IronOre, 200);
        var feeder = IdAt(Items.Inserter, fx - 1, fy);
        Assert.Equal("plot.pickedUp", (await bot.Client.SendAsync("plot.pickup", new { x, y, id = feeder }, CancellationToken.None)).Type);
        for (var i = 0; i < 120 && bot.Help is null; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        Assert.NotNull(bot.Help);
        Assert.Equal(HelpReasons.Stalled, bot.Help.Reason);
        var wanted = (await player.SendAsync("bots.helpWanted")).Payload().GetProperty("requests").EnumerateArray().ToList();
        var call = Assert.Single(wanted);
        Assert.Equal(x, call.GetProperty("x").GetInt32());
        Assert.Equal(y, call.GetProperty("y").GetInt32());
        Assert.Equal(bot.Row.CharacterName, call.GetProperty("bot").GetString());
        var task = Assert.Single(call.GetProperty("tasks").EnumerateArray());
        Assert.Equal(Items.Inserter, task.GetProperty("kind").GetString());
        Assert.Equal((fx - 1, fy), (task.GetProperty("tileX").GetInt32(), task.GetProperty("tileY").GetInt32()));

        // A player takes the call on, comes onto the bot's plot and puts an inserter of their own where it asks.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player.AccountId, Items.Inserter, 1, CancellationToken.None);
        }

        var pay = call.GetProperty("payCents").GetInt64();
        Assert.True(pay >= BotRules.HelpFee, pay.ToString());
        var before = await player.BalanceAsync();
        Assert.Equal("bots.helping", (await player.SendAsync("bots.help", new { x, y })).Type());
        Assert.Empty((await player.SendAsync("bots.helpWanted")).Payload().GetProperty("requests").EnumerateArray());
        var entered = await player.SendAsync("plot.enter", new { x, y });
        Assert.Equal("plot", entered.Type());
        Assert.Single(entered.Payload().GetProperty("help").GetProperty("tasks").EnumerateArray());
        var placed = await player.SendAsync("plot.place", new { x, y, kind = Items.Inserter, tileX = fx - 1, tileY = fy, rotation = BotFactory.East });
        Assert.Equal("plot.placed", placed.Type());
        Assert.True(placed.Payload().GetProperty("help").GetProperty("finished").GetBoolean());
        Assert.Equal(before + pay, await player.BalanceAsync());
        Assert.Equal("no_help_needed", (await player.SendAsync("bots.help", new { x, y })).ErrorCode());

        await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        Assert.Null(bot.Help);
        for (var i = 0; i < 10 && !bot.HasLine; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        var again = await LinePlotAsync(bot);
        Assert.NotNull(again);
        // The inserter the player put up is the bot's now.
        Assert.Equal("plot.pickedUp", (await bot.Client.SendAsync("plot.pickup", new
        {
            x, y,
            id = again.Value.Plot.GetProperty("objects").EnumerateArray().First(o => o.GetProperty("kind").GetString() == Items.Inserter
                && o.GetProperty("tileX").GetInt32() == fx - 1 && o.GetProperty("tileY").GetInt32() == fy).GetProperty("id").GetString(),
        }, CancellationToken.None)).Type);
        Assert.Empty((await player.SendAsync("bots.helpWanted")).Payload().GetProperty("requests").EnumerateArray());
    }
}

/// <summary>Players helping bots on the bots' plots, on a database of their own.</summary>
public sealed class BotHelpTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    private BotHelpBoard Board => factory.Services.GetRequiredService<BotHelpBoard>();

    /// <summary>A bot with a plot (bought for it) and money to pay for help with.</summary>
    private async Task<(Bot Bot, int X, int Y)> BotWithPlotAsync(BotTrade trade)
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == trade);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 5_000_00, "test", CancellationToken.None);
        }

        var info = (await bot.Client.SendAsync("world.info", null, CancellationToken.None)).Payload;
        if (info.GetProperty("plots").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } owned)
        {
            return (bot, owned.GetProperty("x").GetInt32(), owned.GetProperty("y").GetInt32());
        }

        for (var x = 100; ; x++)
        {
            var bought = await bot.Client.SendAsync("world.buy", new { x, y = 500 }, CancellationToken.None);
            if (bought.Is("world.bought"))
            {
                return (bot, x, 500);
            }
        }
    }

    private async Task GiveAsync(Guid account, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(account, item, quantity, CancellationToken.None);
    }

    private async Task<long> BalanceAsync(Guid account)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(account, CancellationToken.None);
    }

    /// <summary>A free tile of the plot, away from the spawn and the railway.</summary>
    private static async Task<(int X, int Y)> FreeTileAsync(Bot bot, int x, int y, int skip = 0)
    {
        var plot = (await bot.Client.SendAsync("plot.enter", new { x, y }, CancellationToken.None)).Payload;
        foreach (var (tx, ty) in Enumerable.Range(20, 40).SelectMany(ty => Enumerable.Range(20, 40).Select(tx => (tx, ty))))
        {
            var placed = await bot.Client.SendAsync("plot.place", new { x, y, kind = Items.WoodenChest, tileX = tx, tileY = ty, rotation = 0 }, CancellationToken.None);
            if (placed.Is("plot.placed"))
            {
                await bot.Client.SendAsync("plot.pickup", new { x, y, id = placed.Payload.GetProperty("object").GetProperty("id").GetGuid() }, CancellationToken.None);
                if (skip-- == 0)
                {
                    return (tx, ty);
                }
            }
        }

        _ = plot;
        throw new InvalidOperationException("No free tile.");
    }

    [Fact]
    public async Task The_player_turns_the_bots_belt_and_puts_up_its_inserter_and_the_bot_pays_once_both_are_right()
    {
        var (bot, x, y) = await BotWithPlotAsync(BotTrade.Farmer);
        await GiveAsync(bot.Row.AccountId, Items.Belt, 1);
        await GiveAsync(bot.Row.AccountId, Items.WoodenChest, 5);
        var (bx, by) = await FreeTileAsync(bot, x, y);
        var (ix, iy) = await FreeTileAsync(bot, x, y, 1);
        var belt = (await bot.Client.SendAsync("plot.place", new { x, y, kind = Items.Belt, tileX = bx, tileY = by, rotation = 3 }, CancellationToken.None))
            .Payload.GetProperty("object").GetProperty("id").GetGuid();

        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        using var other = await factory.ConnectAsync();
        await other.RegisterWithCharacterAsync();
        await GiveAsync(player.AccountId, Items.Inserter, 2);
        await GiveAsync(player.AccountId, Items.Belt, 1);

        // Without leave a player may not even come onto the bot's plot, let alone build there.
        Assert.Equal("not_owner", (await player.SendAsync("plot.enter", new { x, y })).ErrorCode());
        Assert.Equal("no_help_needed", (await player.SendAsync("bots.help", new { x, y })).ErrorCode());

        const long pay = 30_00;
        Board.Post(new HelpRequest(bot.Row.Name, bot.Row.CharacterName, bot.Row.AccountId, x, y, HelpReasons.Parts,
            [new HelpTask(Items.Belt, bx, by, 1, belt), new HelpTask(Items.Inserter, ix, iy, 1)], pay, factory.Time.GetUtcNow()));
        var call = Assert.Single((await other.SendAsync("bots.helpWanted")).Payload().GetProperty("requests").EnumerateArray());
        Assert.Equal(2, call.GetProperty("tasks").GetArrayLength());
        Assert.Equal(Items.Inserter, call.GetProperty("needs")[0].GetProperty("id").GetString());

        // One player takes it on; the other cannot any more, and the map shows no mark meanwhile.
        var helping = await player.SendAsync("bots.help", new { x, y });
        Assert.Equal("bots.helping", helping.Type());
        Assert.Equal(pay, helping.Payload().GetProperty("payCents").GetInt64());
        Assert.Equal("help_taken", (await other.SendAsync("bots.help", new { x, y })).ErrorCode());
        Assert.Empty((await other.SendAsync("bots.helpWanted")).Payload().GetProperty("requests").EnumerateArray());
        Assert.Equal("not_owner", (await other.SendAsync("plot.enter", new { x, y })).ErrorCode());

        var entered = await player.SendAsync("plot.enter", new { x, y });
        Assert.Equal("plot", entered.Type());
        Assert.DoesNotContain("build", entered.Payload().GetProperty("rights").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(2, entered.Payload().GetProperty("help").GetProperty("tasks").GetArrayLength());

        // Only what the call asks: nothing elsewhere, nothing else, no taking things up.
        Assert.Equal("not_help_task", (await player.SendAsync("plot.place", new { x, y, kind = Items.Inserter, tileX = ix + 1, tileY = iy, rotation = 1 })).ErrorCode());
        Assert.Equal("not_help_task", (await player.SendAsync("plot.place", new { x, y, kind = Items.Belt, tileX = ix, tileY = iy, rotation = 1 })).ErrorCode());
        Assert.Equal("not_owner", (await player.SendAsync("plot.pickup", new { x, y, id = belt })).ErrorCode());
        Assert.Equal("not_owner", (await player.SendAsync("plot.placeMany", new
        {
            x, y, objects = new[] { new { kind = Items.Inserter, tileX = ix, tileY = iy, rotation = 1 } },
        })).ErrorCode());

        // The inserter goes up the wrong way first: one task of two is not done yet, and nobody is paid.
        var balance = await player.BalanceAsync();
        var botMoney = await BalanceAsync(bot.Row.AccountId);
        var placed = await player.SendAsync("plot.place", new { x, y, kind = Items.Inserter, tileX = ix, tileY = iy, rotation = 0 });
        Assert.Equal("plot.placed", placed.Type());
        Assert.Equal(0, placed.Payload().GetProperty("help").GetProperty("done").GetInt32());
        var inserter = placed.Payload().GetProperty("object").GetProperty("id").GetGuid();
        var turned = await player.SendAsync("plot.rotate", new { x, y, id = inserter, rotation = 1 });
        Assert.Equal(1, turned.Payload().GetProperty("help").GetProperty("done").GetInt32());
        Assert.False(turned.Payload().GetProperty("help").GetProperty("finished").GetBoolean());
        Assert.Equal(balance, await player.BalanceAsync());

        // Turning the belt the bot put up wrong finishes it: the bot pays the player.
        var done = await player.SendAsync("plot.rotate", new { x, y, id = belt, rotation = 1 });
        Assert.Equal("plot.rotated", done.Type());
        var help = done.Payload().GetProperty("help");
        Assert.True(help.GetProperty("finished").GetBoolean());
        Assert.Equal(balance + pay, help.GetProperty("balanceCents").GetInt64());
        Assert.Equal(botMoney - pay, await BalanceAsync(bot.Row.AccountId));
        Assert.Equal(1, (await player.InventoryAsync()).GetValueOrDefault(Items.Inserter));

        // The leave is used up: the player cannot turn anything again, nor come back, and is paid once.
        Assert.Equal("not_owner", (await player.SendAsync("plot.rotate", new { x, y, id = belt, rotation = 2 })).ErrorCode());
        Assert.Equal("not_owner", (await player.SendAsync("plot.enter", new { x, y })).ErrorCode());
        Assert.Equal("no_help_needed", (await player.SendAsync("bots.help", new { x, y })).ErrorCode());
        Assert.Equal(balance + pay, await player.BalanceAsync());

        // The inserter is the bot's: the bot takes it up into its own inventory.
        Assert.Equal("plot.pickedUp", (await bot.Client.SendAsync("plot.pickup", new { x, y, id = inserter }, CancellationToken.None)).Type);
    }

    [Fact]
    public async Task A_bot_that_cannot_pay_is_not_helped_and_a_helper_who_ran_out_of_time_lets_another_player_help()
    {
        var (bot, x, y) = await BotWithPlotAsync(BotTrade.Miner);
        await GiveAsync(bot.Row.AccountId, Items.WoodenChest, 5);
        var (ix, iy) = await FreeTileAsync(bot, x, y);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        using var other = await factory.ConnectAsync();
        await other.RegisterWithCharacterAsync();
        await GiveAsync(player.AccountId, Items.Inserter, 1);
        await GiveAsync(other.AccountId, Items.Inserter, 1);

        Board.Post(new HelpRequest(bot.Row.Name, bot.Row.CharacterName, bot.Row.AccountId, x, y, HelpReasons.Stalled,
            [new HelpTask(Items.Inserter, ix, iy, 1)], 1_000_000_00, factory.Time.GetUtcNow()));
        Assert.Equal("bots.helping", (await player.SendAsync("bots.help", new { x, y })).Type());

        // The bot cannot pay: nothing goes up and nothing changes hands, and the call stays for later.
        Assert.Equal("owner_cannot_pay", (await player.SendAsync("plot.place", new { x, y, kind = Items.Inserter, tileX = ix, tileY = iy, rotation = 1 })).ErrorCode());
        Assert.Equal(1, (await player.InventoryAsync()).GetValueOrDefault(Items.Inserter));
        Assert.False(Board.Withdraw(bot.Row.Name));

        // The first helper's leave runs out: the call is open again and another player may take it on.
        factory.Time.Advance(BotRules.HelpLeave + TimeSpan.FromSeconds(1));
        Assert.Single((await other.SendAsync("bots.helpWanted")).Payload().GetProperty("requests").EnumerateArray());
        Assert.Equal("not_owner", (await player.SendAsync("plot.place", new { x, y, kind = Items.Inserter, tileX = ix, tileY = iy, rotation = 1 })).ErrorCode());
        Assert.Equal("bots.helping", (await other.SendAsync("bots.help", new { x, y })).Type());
        Assert.True(Board.Withdraw(bot.Row.Name) is false);
        factory.Time.Advance(BotRules.HelpLeave + TimeSpan.FromSeconds(1));
        Assert.True(Board.Withdraw(bot.Row.Name));
        Assert.Equal("not_owner", (await other.SendAsync("plot.enter", new { x, y })).ErrorCode());
    }

    [Fact]
    public async Task A_bot_that_put_its_line_up_wrong_asks_a_player_to_turn_it_and_the_line_works_after()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Smelter);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        Engine.Commons.MistakeChance = 1;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                // A bot is never ahead of the best player, so one has played a lot.
                await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 100_000, CancellationToken.None);
                await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 20_000_00, "test", CancellationToken.None);
                var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
                foreach (var (item, quantity) in new[]
                         {
                             (Items.IronOre, 300L), (Items.Coal, 150L), (Items.Wood, 300L), (Items.Stone, 200L),
                             (Items.IronIngot, 120L), (Items.CopperIngot, 40L), (Items.Nails, 60L),
                         })
                {
                    await inventory.AddAsync(bot.Row.AccountId, item, quantity, CancellationToken.None);
                }
            }

            for (var i = 0; i < 600 && bot.Help is null; i++)
            {
                factory.Time.Advance(TimeSpan.FromMinutes(1));
                await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            }

            Assert.NotNull(bot.Help);
            Assert.Equal(HelpReasons.Turned, bot.Help.Reason);
            var task = Assert.Single(bot.Help.Tasks);
            Assert.NotNull(task.Object);
            var (x, y) = (bot.Help.X, bot.Help.Y);
            Assert.Equal("bots.helping", (await player.SendAsync("bots.help", new { x, y })).Type());
            var turned = await player.SendAsync("plot.rotate", new { x, y, id = task.Object, rotation = task.Rotation });
            Assert.True(turned.Payload().GetProperty("help").GetProperty("finished").GetBoolean());

            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            Assert.Null(bot.Help);
            for (var i = 0; i < 10 && !bot.HasLine; i++)
            {
                factory.Time.Advance(TimeSpan.FromMinutes(1));
                await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            }

            Assert.True(bot.HasLine);
        }
        finally
        {
            Engine.Commons.MistakeChance = 0;
        }
    }
}

/// <summary>What bots take out of the economy and put into it, on a database of their own.</summary>
public sealed class BotEconomyTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    [Fact]
    public async Task A_bot_takes_little_from_the_npc_trader_in_a_session_and_bots_do_not_buy_from_bots_to_sell_on()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var seller = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Forester);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            await inventory.AddAsync(seller.Row.AccountId, Items.WastePaper, 5000, CancellationToken.None);
            await inventory.AddAsync(seller.Row.AccountId, Items.Cabbage, 40, CancellationToken.None);
        }

        // A bot lists cabbage cheap, as a player could; no bot needs cabbage for its work.
        var cabbage = (await seller.Client.SendAsync("market.prices", null, CancellationToken.None)).Payload.GetProperty("items")
            .EnumerateArray().First(i => i.GetProperty("id").GetString() == Items.Cabbage).GetProperty("sellCents").GetInt64();
        Assert.Equal("market.done", (await seller.Client.SendAsync("market.list",
            new { item = Items.Cabbage, quantity = 40, priceCents = cabbage * 11 / 10 }, CancellationToken.None)).Type);

        var counters = factory.Services.GetRequiredService<GameCounters>();
        long Sold() => counters.MoneyTotals().Where(t => t.Bot && t.Reason == "sell").Sum(t => t.InCents);
        var sold = Sold();
        for (var i = 0; i < 100; i++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(11));
            foreach (var bot in Engine.Bots)
            {
                await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            }
        }

        // One session for every bot: the trader paid each of them no more than the session's limit.
        Assert.True(Sold() - sold <= Engine.Bots.Count * (BotRules.MaxTraderIncome + 1_00), $"{Sold() - sold} cents");
        var mine = (await seller.Client.SendAsync("market.mine", null, CancellationToken.None)).Payload;
        var listing = mine.GetProperty("listings").EnumerateArray().First(l => l.GetProperty("item").GetString() == Items.Cabbage);
        Assert.Equal(0, listing.GetProperty("done").GetInt64());
    }
}
