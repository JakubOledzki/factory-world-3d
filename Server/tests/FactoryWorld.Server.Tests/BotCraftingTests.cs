using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.Research;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Bots making belts, inserters and chests for the players' market; on a database of their own, so that no other test's offers count.</summary>
public sealed class BotCraftingTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly HashSet<string> Logistics =
    [
        Items.Belt, Items.Inserter, Items.WoodenChest, Items.IronChest, Items.Pole, Items.Splitter, Items.FastBelt,
        Items.UndergroundBelt, Items.FastInserter, Items.LongInserter, Items.LoadingChest, Items.ReceivingChest,
    ];

    private static readonly HashSet<string> MadeOfParts =
    [
        Items.Splitter, Items.FastBelt, Items.UndergroundBelt, Items.FastInserter, Items.LongInserter, Items.LoadingChest, Items.ReceivingChest,
    ];

    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    [Fact]
    public async Task An_industrial_bot_makes_belts_inserters_and_chests_with_their_parts_and_lists_them_below_the_trader()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Industrialist);
        await LeadAsync();
        foreach (var technology in new[] { TechTree.Logistics, TechTree.Automation, TechTree.Storage, TechTree.Sorting, TechTree.FastLogistics })
        {
            await ResearchAsync(bot, technology);
        }

        await GiveAsync(bot, (Items.IronIngot, 600), (Items.CopperIngot, 300), (Items.Wood, 300), (Items.Stone, 100));

        for (var i = 0; i < 600; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        var made = await MadeAsync(bot);
        Assert.True(made.Count(Logistics.Contains) >= 3, string.Join(", ", made));
        // Splitters, fast belts, fast inserters and loading chests are made of belts, inserters and chests the bot made first.
        Assert.True(made.Any(MadeOfParts.Contains), string.Join(", ", made));

        var prices = await PricesAsync(bot);
        var listings = Listings(await bot.Client.SendAsync("market.mine", null, CancellationToken.None)).ToList();
        Assert.Contains(listings, l => Logistics.Contains(l.Item));
        foreach (var (item, quantity, price) in listings)
        {
            Assert.True(quantity <= BotRules.ListingSize);
            // Never so cheap that the trader would pay more, never dearer than the trader sells it for.
            Assert.True(price > prices[item].Pays, $"{item} at {price}");
            if (prices[item].Sells is { } sells)
            {
                Assert.True(price < sells, $"{item} at {price}, the trader sells at {sells}");
            }
        }
    }

    [Fact]
    public async Task A_bot_makes_no_more_of_what_the_players_market_has_plenty_of()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Miner);
        await LeadAsync();
        foreach (var technology in new[] { TechTree.Logistics, TechTree.Storage })
        {
            await ResearchAsync(bot, technology);
        }

        await GiveAsync(bot, (Items.IronIngot, 800));

        // A player has more iron chests up for sale than the market needs; too dear for the bot to buy them up.
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        var floor = (await PricesAsync(bot))[Items.IronChest].Pays;
        var plenty = BotRules.MarketCap(floor) + 4;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player.AccountId, Items.IronChest, plenty, CancellationToken.None);
        }

        var listed = await player.SendAsync("market.list", new { item = Items.IronChest, quantity = plenty, priceCents = floor * 3 / 2 });
        Assert.Equal("market.done", listed.Type());

        for (var i = 0; i < 150; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        Assert.DoesNotContain(Items.IronChest, await MadeAsync(bot));

        // Once the chests are gone from the market, the bot makes them again.
        var id = (await player.SendAsync("market.mine", null)).Payload().GetProperty("listings")[0].GetProperty("id").GetInt64();
        Assert.Equal("market.done", (await player.SendAsync("market.unlist", new { id })).Type());
        for (var i = 0; i < 150; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        Assert.Contains(Items.IronChest, await MadeAsync(bot));
    }

    [Fact]
    public async Task A_bot_buys_from_players_the_materials_it_lacks_for_its_products()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Forester);
        await LeadAsync();
        foreach (var technology in new[] { TechTree.Logistics, TechTree.Storage })
        {
            await ResearchAsync(bot, technology);
        }

        // Wood and nails for its chests and piles, but no stone for heaps.
        await GiveAsync(bot, (Items.Wood, 400), (Items.Nails, 100));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 200_00, "test", CancellationToken.None);
        }

        // A player sells stone dearer than a bot pays for a mere bargain, but for what heaps still pay off at.
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player.AccountId, Items.Stone, 200, CancellationToken.None);
        }

        var floor = (await PricesAsync(bot))[Items.Stone].Pays;
        var price = floor * 145 / 100;
        Assert.True(price > floor * 1.3 && price <= floor * BotRules.MaterialsMarkup);
        Assert.Equal("market.done", (await player.SendAsync("market.list", new { item = Items.Stone, quantity = 200, priceCents = price })).Type());

        for (var i = 0; i < 300; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        var sold = (await player.SendAsync("market.mine", null)).Payload().GetProperty("listings")[0].GetProperty("done").GetInt64();
        Assert.True(sold > 0, "The bot bought no stone");
        var made = await MadeAsync(bot);
        Assert.True(made.Contains(Items.Heap), string.Join(", ", made));
    }

    [Fact]
    public async Task A_farming_bot_puts_up_a_bakery_and_a_pickling_barrel_and_sells_what_they_make()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.First(b => b.Profile.Trade == BotTrade.Farmer);
        await LeadAsync();
        foreach (var technology in new[] { TechTree.Milling, TechTree.Logistics, TechTree.Automation, TechTree.Pickling, TechTree.Baking })
        {
            await ResearchAsync(bot, technology);
        }

        await GiveAsync(bot, (Items.Stone, 300), (Items.Sand, 60), (Items.IronIngot, 80), (Items.CopperIngot, 30), (Items.Wood, 200),
            (Items.Nails, 60), (Items.Cabbage, 80), (Items.Flour, 80), (Items.Coal, 60));

        for (var i = 0; i < 600; i++)
        {
            factory.Time.Advance(TimeSpan.FromMinutes(1));
            await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
        }

        var built = new List<string>();
        var info = await bot.Client.SendAsync("world.info", null, CancellationToken.None);
        foreach (var plot in info.Payload.GetProperty("plots").EnumerateArray())
        {
            var inside = await bot.Client.SendAsync("plot.enter", new { x = plot.GetProperty("x").GetInt32(), y = plot.GetProperty("y").GetInt32() }, CancellationToken.None);
            built.AddRange(inside.Payload.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("kind").GetString()!));
        }

        Assert.True(built.Contains(Items.Bakery) || built.Contains(Items.PicklingBarrel), string.Join(", ", built));
        var made = await MadeAsync(bot);
        Assert.True(made.Contains(Items.Bread) || made.Contains(Items.Sauerkraut), string.Join(", ", made));
    }

    /// <summary>A player far ahead, so that the bots are not held at a low XP cap.</summary>
    private async Task LeadAsync()
    {
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterProgress>().AddXpAsync(player.AccountId, 1_000_000, CancellationToken.None);
    }

    private async Task GiveAsync(Bot bot, params (string Item, long Quantity)[] items)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        foreach (var (item, quantity) in items)
        {
            await inventory.AddAsync(bot.Row.AccountId, item, quantity, CancellationToken.None);
        }
    }

    /// <summary>The bot researches a technology from start to end, its cost given.</summary>
    private async Task ResearchAsync(Bot bot, string technology)
    {
        await GiveAsync(bot, TechTree.ById[technology].Cost.Select(c => (c.Item, (long)c.Quantity)).ToArray());
        Assert.Equal("research", (await bot.Client.SendAsync("research.start", new { technology }, CancellationToken.None)).Type);
        factory.Time.Advance(TechTree.ById[technology].Time);
        await bot.Client.SendAsync("research.get", null, CancellationToken.None);
    }

    /// <summary>What the bot has in its inventory or on the market, of what it may have made.</summary>
    private static async Task<HashSet<string>> MadeAsync(Bot bot)
    {
        var made = new HashSet<string>(StringComparer.Ordinal);
        var inventory = await bot.Client.SendAsync("inventory.get", null, CancellationToken.None);
        foreach (var item in inventory.Payload.GetProperty("items").EnumerateArray())
        {
            made.Add(item.GetProperty("id").GetString()!);
        }

        foreach (var (item, _, _) in Listings(await bot.Client.SendAsync("market.mine", null, CancellationToken.None)))
        {
            made.Add(item);
        }

        return made;
    }

    private static IEnumerable<(string Item, long Quantity, long Price)> Listings(BotReply mine) =>
        mine.Payload.GetProperty("listings").EnumerateArray()
            .Where(l => l.GetProperty("open").GetBoolean())
            .Select(l => (l.GetProperty("item").GetString()!, l.GetProperty("quantity").GetInt64(), l.GetProperty("priceCents").GetInt64()));

    /// <summary>What the trader pays for each item, and what it sells it for, if it does.</summary>
    private static async Task<Dictionary<string, (long Pays, long? Sells)>> PricesAsync(Bot bot)
    {
        var prices = await bot.Client.SendAsync("market.prices", null, CancellationToken.None);
        return prices.Payload.GetProperty("items").EnumerateArray()
            .Where(i => i.TryGetProperty("sellCents", out var s) && s.ValueKind == JsonValueKind.Number)
            .ToDictionary(
                i => i.GetProperty("id").GetString()!,
                i => (i.GetProperty("sellCents").GetInt64(),
                    i.TryGetProperty("buyCents", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : (long?)null));
    }
}
