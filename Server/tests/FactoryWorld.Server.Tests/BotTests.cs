using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.Characters;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class BotTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    /// <summary>The bots of a test server (Bots:Count): the founders and a few made-up ones.</summary>
    public const int TestBots = 36;

    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    [Fact]
    public void Two_thousand_bots_have_names_players_could_pick_and_come_out_the_same_on_every_start()
    {
        var roster = BotRoster.First(2000);
        Assert.Equal(2000, roster.Count);
        Assert.Equal(BotRoster.Founders, roster.Take(BotRoster.Founders.Count));
        Assert.All(roster, b => Assert.True(CharacterRules.IsValidName(b.Name), b.Name));
        Assert.Equal(roster.Count, roster.Select(b => BotNames.Plain(b.Name)).Distinct().Count());
        Assert.Equal(roster.Select(b => b.Name), BotRoster.First(2000).Select(b => b.Name));
        Assert.All(Enum.GetValues<BotTrade>(), t => Assert.InRange(roster.Count(b => b.Trade == t), 300, 500));
        Assert.All(roster, b =>
        {
            Assert.Equal(7, b.Look.Length);
            Assert.InRange(b.Look[0], 0, AppearanceCatalog.SkinTones.Length - 1);
            Assert.InRange(b.Look[1], 0, AppearanceCatalog.HairStyles - 1);
            Assert.InRange(b.Look[2], 0, AppearanceCatalog.HairColors.Length - 1);
            Assert.InRange(b.Look[3], 0, AppearanceCatalog.ShirtStyles - 1);
            Assert.All(b.Look[4..], c => Assert.InRange(c, 0, AppearanceCatalog.ClothColors.Length - 1));
            Assert.InRange(b.Latitude, -60, 70);
        });
    }

    private async Task<long> XpAsync(Guid account)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterXp>().XpAsync(account, CancellationToken.None) ?? 0;
    }

    private async Task GiveMoneyAsync(Guid account, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(account, cents, "test", CancellationToken.None);
    }

    /// <summary>Lets every bot take <paramref name="steps"/> steps, the clock moving as a player's would between clicks.</summary>
    private async Task PlayAsync(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(11));
            foreach (var bot in Engine.Bots)
            {
                await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Bots_play_like_players_but_never_get_ahead_of_the_best_real_player()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        await Engine.EnsureBotsAsync(CancellationToken.None);
        Assert.Equal(Engine.Roster.Count, Engine.Bots.Count);
        Assert.Equal(Engine.Roster.Select(b => b.Name).Order(), Engine.Bots.Select(b => b.Row.CharacterName).Order());

        // With nobody to measure against, a bot may not earn any experience at all.
        Assert.Equal(0, await Engine.XpCapAsync(CancellationToken.None));
        await PlayAsync(10);
        foreach (var bot in Engine.Bots)
        {
            Assert.Equal(0, await XpAsync(bot.Row.AccountId));
        }

        // A real player is ahead; bots with money for a plot go and earn up to just below them.
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 3000, CancellationToken.None);
        }

        foreach (var bot in Engine.Bots)
        {
            await GiveMoneyAsync(bot.Row.AccountId, 5_000_00);
        }

        await PlayAsync(120);
        var xps = new Dictionary<string, long>();
        foreach (var bot in Engine.Bots)
        {
            xps[bot.Profile.Name] = await XpAsync(bot.Row.AccountId);
            Assert.True(xps[bot.Profile.Name] < 3000, $"{bot.Profile.Name} has {xps[bot.Profile.Name]} XP");
        }

        Assert.True(xps.Values.Count(xp => xp > 0) >= Engine.Roster.Count / 2, string.Join(", ", xps));

        // Bots bought plots like players do, and show up on the map.
        var plots = 0;
        foreach (var bot in Engine.Bots)
        {
            var info = await bot.Client.SendAsync("world.info", null, CancellationToken.None);
            plots += info.Payload.GetProperty("plots").GetArrayLength();
        }

        Assert.True(plots >= Engine.Roster.Count / 2, $"{plots} plots");

        // The level ranking still has the real player on top.
        var ranking = (await player.SendAsync("rankings.get", new { ranking = "level" })).Payload();
        Assert.True(ranking.GetProperty("top")[0].GetProperty("mine").GetBoolean());

        // Bots stand in the rankings next to players, under the names players see.
        var names = Engine.Roster.Select(b => b.Name).ToHashSet();
        foreach (var id in new[] { "level", "money" })
        {
            var top = (await player.SendAsync("rankings.get", new { ranking = id })).Payload().GetProperty("top").EnumerateArray();
            Assert.Contains(top, p => names.Contains(p.GetProperty("name").GetString()!));
        }

        // When the player moves on, so may the bots, and no further.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 3000, CancellationToken.None);
        }

        await PlayAsync(60);
        var grew = false;
        foreach (var bot in Engine.Bots)
        {
            var xp = await XpAsync(bot.Row.AccountId);
            Assert.True(xp < 6000, $"{bot.Profile.Name} has {xp} XP");
            grew |= xp > xps[bot.Profile.Name];
        }

        Assert.True(grew);
    }
}

/// <summary>On a database of their own, so the first test above starts with no players.</summary>
public sealed class BotLimitTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    private async Task<long> XpAsync(Guid account)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICharacterXp>().XpAsync(account, CancellationToken.None) ?? 0;
    }

    [Fact]
    public async Task A_bot_that_gets_over_the_cap_is_cut_back_and_bots_keep_few_market_offers()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 100_000, CancellationToken.None);
        }

        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Forester);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // Experience from somewhere the bot could not foresee.
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(bot.Row.AccountId, 500_000, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(bot.Row.AccountId, Items.Wood, 2000, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(bot.Row.AccountId, Items.Stone, 2000, CancellationToken.None);
        }

        await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        Assert.True(await XpAsync(bot.Row.AccountId) < await XpAsync(player.AccountId));

        for (var i = 0; i < 200; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(3));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        var mine = (await bot.Client.SendAsync("market.mine", null, CancellationToken.None)).Payload;
        var open = mine.GetProperty("listings").EnumerateArray().Count(l => l.GetProperty("open").GetBoolean());
        Assert.InRange(open, 1, BotRules.MaxListings);
        foreach (var listing in mine.GetProperty("listings").EnumerateArray())
        {
            Assert.True(listing.GetProperty("quantity").GetInt64() <= BotRules.ListingSize);
        }
    }

    [Fact]
    public async Task A_bot_builds_on_its_plot_and_smelts_in_the_furnaces_it_put_up()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Miner);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 100_000, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 20_000_00, "test", CancellationToken.None);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            await inventory.AddAsync(bot.Row.AccountId, Items.Stone, 300, CancellationToken.None);
            await inventory.AddAsync(bot.Row.AccountId, Items.IronOre, 200, CancellationToken.None);
            await inventory.AddAsync(bot.Row.AccountId, Items.Coal, 100, CancellationToken.None);
        }

        var built = new List<string>();
        for (var i = 0; i < 300 && built.Count(k => k == Items.Furnace) < 2; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            var info = await bot.Client.SendAsync("world.info", null, CancellationToken.None);
            built.Clear();
            foreach (var plot in info.Payload.GetProperty("plots").EnumerateArray())
            {
                var inside = await bot.Client.SendAsync("plot.enter", new { x = plot.GetProperty("x").GetInt32(), y = plot.GetProperty("y").GetInt32() }, CancellationToken.None);
                built.AddRange(inside.Payload.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("kind").GetString()!));
            }
        }

        Assert.True(built.Count(k => k == Items.Furnace) >= 2, string.Join(", ", built));
    }

    [Fact]
    public async Task Bots_log_in_for_a_session_and_out_again_for_hours()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        // Between sessions a bot is not online, not even right after it registered like a player.
        var online = factory.Services.GetRequiredService<OnlinePlayers>();
        Assert.All(Engine.Bots, b => Assert.False(online.IsOnline(b.Row.AccountId), b.Profile.Name));

        // New bots all come in within minutes of the server starting, not hours.
        factory.Time.Advance(BotEngine.FirstSessionWithin);
        await Engine.TickAsync(CancellationToken.None);
        Assert.All(Engine.Bots, b => Assert.NotNull(b.Row.SessionUntil));

        factory.Time.Advance(TimeSpan.FromMinutes(Engine.Roster.Max(b => b.SessionMinutes) * 1.5 + 1));
        await Engine.TickAsync(CancellationToken.None);
        var now = factory.Time.GetUtcNow();
        Assert.All(Engine.Bots, b =>
        {
            Assert.Null(b.Row.SessionUntil);
            Assert.True(b.Row.NextSessionAt >= now + TimeSpan.FromHours(b.Profile.RestHours) * 0.5);
        });
    }
}

/// <summary>On a database of their own, so that only this player sells to the bots.</summary>
public sealed class BotMarketTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    [Fact]
    public async Task Bots_buy_from_players_and_list_their_own_goods_even_at_the_xp_cap()
    {
        Assert.Equal(BotTests.TestBots, Engine.Roster.Count);
        await Engine.EnsureBotsAsync(CancellationToken.None);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player.AccountId, Items.Wood, 100, CancellationToken.None);
        }

        // Cheaper than the trader sells wood for, dearer than he buys it.
        var wood = (await player.SendAsync("market.prices", null)).Payload().GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == Items.Wood);
        var price = wood.GetProperty("sellCents").GetInt64() * 5 / 4;
        Assert.True(price < wood.GetProperty("buyCents").GetInt64());
        Assert.Equal("market.done", (await player.SendAsync("market.list", new { item = Items.Wood, quantity = 100, priceCents = price })).Type());

        // The player has hardly any XP, so the bots are at their cap all along; they work and trade anyway.
        for (var i = 0; i < 150; i++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(11));
            foreach (var bot in Engine.Bots)
            {
                await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            }
        }

        var mine = (await player.SendAsync("market.mine", null)).Payload();
        Assert.True(mine.GetProperty("listings")[0].GetProperty("done").GetInt64() > 0);

        var listed = 0;
        foreach (var bot in Engine.Bots)
        {
            var offers = (await bot.Client.SendAsync("market.mine", null, CancellationToken.None)).Payload;
            listed += offers.GetProperty("listings").GetArrayLength();
        }

        Assert.True(listed >= Engine.Roster.Count / 2, $"{listed} listings");
    }
}
