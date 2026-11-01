using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Achievements;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class AchievementTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private static int _forests;

    private static (int X, int Y) FreeForest()
    {
        var skip = Interlocked.Increment(ref _forests) + 500;
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == Biome.Forest && found++ >= skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private async Task<GameClient> PlayerAsync(long cents = 0)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        if (cents > 0)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
        }

        return client;
    }

    private static async Task<(int X, int Y)> ChopATreeAsync(GameClient client, ServerFactory factory)
    {
        var (x, y) = FreeForest();
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var i = Array.IndexOf(tiles.ToArray(), (byte)TileObject.Tree);
        factory.Time.Advance(HarvestRules.ChopTime);
        var chopped = await client.SendAsync("plot.harvest", new { x, y, tileX = i % PlotLayout.Size, tileY = i / PlotLayout.Size });
        Assert.Equal("plot.harvested", chopped.Type());
        return (x, y);
    }

    private static Dictionary<string, JsonElement> BadgesOf(JsonElement reply) =>
        reply.Payload().GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("badges").EnumerateArray())
            .ToDictionary(b => b.GetProperty("id").GetString()!);

    [Fact]
    public async Task Buying_a_plot_and_chopping_a_tree_unlock_badges_and_a_watching_player_is_told()
    {
        using var client = await PlayerAsync(10_000_00);
        Assert.Equal("achievements.watching", (await client.SendAsync("achievements.watch")).Type());
        await ChopATreeAsync(client, factory);
        await client.SendAsync("ping");

        // A holiday treat ("season.drop") may come with the tree, as it does for players.
        var badgeNotices = client.Notices.Where(n => n.Type() != "season.drop").ToList();
        var told = badgeNotices.Select(n => n.Payload().GetProperty("id").GetString()).ToList();
        Assert.All(badgeNotices, n => Assert.Equal("achievement.unlocked", n.Type()));
        Assert.Contains("landowner", told);
        Assert.Contains("first_tree", told);
        Assert.Equal("Pierwsze drzewo", badgeNotices.Single(n => n.Payload().GetProperty("id").GetString() == "first_tree").Payload().GetProperty("name").GetString());

        var reply = await client.SendAsync("achievements.get");
        Assert.Equal("achievements", reply.Type());
        var badges = BadgesOf(reply);
        Assert.Equal(Badges.All.Count, reply.Payload().GetProperty("total").GetInt32());
        Assert.Equal(told.Count, reply.Payload().GetProperty("unlocked").GetInt32());
        Assert.Equal(JsonValueKind.Number, badges["first_tree"].GetProperty("unlockedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, badges["lumberjack"].GetProperty("unlockedAt").ValueKind);
        Assert.Equal("1 / 100", badges["lumberjack"].GetProperty("text").GetString());
        // The plot cost 1 000 $ of the 10 000 $; the most the wallet held counts.
        Assert.Equal(JsonValueKind.Null, badges["wealthy"].GetProperty("unlockedAt").ValueKind);
    }

    [Fact]
    public async Task A_player_who_does_not_watch_gets_only_replies()
    {
        using var client = await PlayerAsync(10_000_00);
        await ChopATreeAsync(client, factory);
        await client.SendAsync("ping");
        Assert.Empty(client.Notices);

        var badges = BadgesOf(await client.SendAsync("achievements.get"));
        Assert.Equal(JsonValueKind.Number, badges["first_tree"].GetProperty("unlockedAt").ValueKind);
    }

    [Fact]
    public async Task Unlocking_many_badges_unlocks_the_collector_badge_too()
    {
        var owner = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var achievements = scope.ServiceProvider.GetRequiredService<AchievementService>();

        var counts = Badges.All.Where(b => b.Stat != Stats.Badges).GroupBy(b => b.Stat).ToDictionary(g => g.Key, g => g.Max(b => b.Goal));
        var unlocked = await achievements.RecordAsync(owner, counts, CancellationToken.None);
        Assert.Equal(Badges.All.Count, unlocked.Count);
        Assert.Contains(unlocked, b => b.Id == "collector");

        // Nothing is unlocked twice, and the highest level seen stays.
        Assert.Empty(await achievements.RecordAsync(owner, new Dictionary<string, long> { [Stats.Level] = 3, [Stats.Wood] = 1 }, CancellationToken.None));
        var state = await achievements.StateAsync(owner, CancellationToken.None);
        Assert.Equal(50, state.First(s => s.Badge.Id == "level_50").Value);
        Assert.Equal(1001, state.First(s => s.Badge.Id == "forest_king").Value);
    }

    [Fact]
    public void Ingots_count_when_they_come_out_of_the_furnace_but_ore_does_not()
    {
        var reply = JsonSerializer.SerializeToElement(new
        {
            taken = new[] { new { id = Items.IronIngot, quantity = 7 }, new { id = Items.IronOre, quantity = 3 } },
            xp = new { xp = 900, level = 3, leveledUp = false },
        }, Payload.Json);
        var counts = Tally.Count(new HandledMessage("furnace.take", null, "furnace", reply));
        Assert.Equal(7, counts[Stats.Ingots]);
        Assert.Equal(7, counts[Stats.Produced]);
        Assert.Equal(3, counts[Stats.Level]);

        var request = JsonSerializer.SerializeToElement(new { machine = Guid.NewGuid(), what = "input" }, Payload.Json);
        var inputs = JsonSerializer.SerializeToElement(new { taken = new[] { new { id = Items.Wheat, quantity = 5 } } }, Payload.Json);
        Assert.Empty(Tally.Count(new HandledMessage("machine.take", request, "machine", inputs)));

        var delivered = JsonSerializer.SerializeToElement(new { balanceCents = 500, completed = true, rewardCents = 100, xp = (object?)null }, Payload.Json);
        Assert.Equal(1, Tally.Count(new HandledMessage("contracts.deliver", null, "contracts.done", delivered))[Stats.Contracts]);
    }

    [Fact]
    public async Task Rankings_list_the_richest_players_and_where_the_asking_player_stands()
    {
        using var rich = await PlayerAsync(900_000_000_00);
        using var middle = await PlayerAsync(500_000_000_00);
        using var poor = await PlayerAsync(1_00);

        var reply = await poor.SendAsync("rankings.get", new { ranking = "money" });
        Assert.Equal("rankings", reply.Type());
        var ids = reply.Payload().GetProperty("rankings").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Equal(["money", "level", Stats.Produced, "efficiency", Stats.Badges, "greenest", "challenge"], ids);

        var top = reply.Payload().GetProperty("top").EnumerateArray().ToList();
        Assert.Equal(1, top[0].GetProperty("place").GetInt32());
        Assert.Equal(900_000_000_00, top[0].GetProperty("value").GetInt64());
        Assert.Equal("900 000 000,00 $", top[0].GetProperty("text").GetString());
        Assert.Equal(500_000_000_00, top[1].GetProperty("value").GetInt64());
        Assert.False(top[0].GetProperty("mine").GetBoolean());

        var me = reply.Payload().GetProperty("me");
        Assert.Equal(100, me.GetProperty("value").GetInt64());
        Assert.Equal("1,00 $", me.GetProperty("text").GetString());
        Assert.True(me.GetProperty("place").GetInt64() >= 3);

        var first = (await rich.SendAsync("rankings.get")).Payload();
        Assert.Equal("money", first.GetProperty("ranking").GetString());
        Assert.True(first.GetProperty("top")[0].GetProperty("mine").GetBoolean());
        Assert.Equal(1, first.GetProperty("me").GetProperty("place").GetInt64());

        Assert.Equal("unknown_ranking", (await poor.SendAsync("rankings.get", new { ranking = "nope" })).ErrorCode());
    }

    [Fact]
    public async Task The_production_ranking_counts_what_players_made()
    {
        using var maker = await PlayerAsync(10_000_00);
        await ChopATreeAsync(maker, factory);

        var reply = (await maker.SendAsync("rankings.get", new { ranking = Stats.Produced })).Payload();
        var me = reply.GetProperty("me");
        Assert.True(me.GetProperty("value").GetInt64() >= 1);
        Assert.Contains(reply.GetProperty("top").EnumerateArray(), p => p.GetProperty("mine").GetBoolean());

        using var idle = await PlayerAsync();
        var none = (await idle.SendAsync("rankings.get", new { ranking = "level" })).Payload();
        Assert.Equal(JsonValueKind.Null, none.GetProperty("me").ValueKind);
    }
}
