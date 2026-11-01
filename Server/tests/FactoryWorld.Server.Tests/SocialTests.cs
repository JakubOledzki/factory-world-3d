using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Chat;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>The chat, pings on a plot, and likes for the factories players visit.</summary>
public sealed class SocialTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private async Task<GameClient> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        return client;
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 6000;
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

    private static async Task<(int X, int Y)> BuyAsync(GameClient client, bool open = false)
    {
        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        if (open)
        {
            Assert.Equal("world.plot", (await client.SendAsync("plot.setPublic", new { x, y, @public = true })).Type());
        }

        return (x, y);
    }

    private static async Task<List<JsonElement>> ChatAsync(GameClient client, long after = 0, (int X, int Y)? plot = null)
    {
        var reply = plot is { } p
            ? await client.SendAsync("chat.get", new { after, x = p.X, y = p.Y })
            : await client.SendAsync("chat.get", new { after });
        Assert.Equal("chat", reply.Type());
        return reply.Payload().GetProperty("messages").EnumerateArray().ToList();
    }

    private static string Name(JsonElement character) => character.Payload().GetProperty("name").GetString()!;

    [Fact]
    public async Task Everybody_reads_the_world_chat_with_the_name_of_who_wrote()
    {
        using var alice = await PlayerAsync();
        using var bob = await PlayerAsync();
        var before = (await bob.SendAsync("chat.get", new { after = 0 })).Payload().GetProperty("last").GetInt64();

        var sent = await alice.SendAsync("chat.send", new { channel = "world", text = "  Cześć\nwszystkim!  " });
        Assert.Equal("chat.sent", sent.Type());
        var message = sent.Payload().GetProperty("message");
        // Control characters become spaces and the ends are trimmed.
        Assert.Equal("Cześć wszystkim!", message.GetProperty("text").GetString());

        var read = await ChatAsync(bob, before);
        var heard = Assert.Single(read);
        Assert.Equal("Cześć wszystkim!", heard.GetProperty("text").GetString());
        Assert.Equal("world", heard.GetProperty("channel").GetString());
        Assert.Equal(Name(await alice.SendAsync("character.get")), heard.GetProperty("name").GetString());
        Assert.Equal(alice.AccountId, heard.GetProperty("from").GetGuid());

        // Asking again after the last one gives nothing new.
        Assert.Empty(await ChatAsync(bob, heard.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task A_plots_chat_and_pings_reach_only_those_who_may_come_onto_it()
    {
        using var owner = await PlayerAsync();
        using var visitor = await PlayerAsync();
        var plot = await BuyAsync(owner);
        var start = (await owner.SendAsync("chat.get")).Payload().GetProperty("last").GetInt64();

        var ping = await owner.SendAsync("chat.send", new { channel = "plot", x = plot.X, y = plot.Y, text = "Patrz tutaj!", pingX = 120, pingY = 40 });
        Assert.Equal("chat.sent", ping.Type());
        var heard = Assert.Single(await ChatAsync(owner, start, plot));
        Assert.Equal(120, heard.GetProperty("ping").GetProperty("x").GetInt32());
        Assert.Equal(40, heard.GetProperty("ping").GetProperty("y").GetInt32());

        // A private plot: nobody else hears it or may speak there, and the world chat does not carry it.
        Assert.Equal("not_owner", (await visitor.SendAsync("chat.get", new { after = start, x = plot.X, y = plot.Y })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("chat.send", new { channel = "plot", x = plot.X, y = plot.Y, text = "hej" })).ErrorCode());
        Assert.DoesNotContain(await ChatAsync(visitor, start), m => m.GetProperty("channel").GetString() == "plot");

        // Once public, visitors hear the plot and answer.
        await owner.SendAsync("plot.setPublic", new { x = plot.X, y = plot.Y, @public = true });
        Assert.Single(await ChatAsync(visitor, start, plot));
        Assert.Equal("chat.sent", (await visitor.SendAsync("chat.send", new { channel = "plot", x = plot.X, y = plot.Y, text = "Ładnie tu!" })).Type());
        Assert.Equal(2, (await ChatAsync(owner, start, plot)).Count);
    }

    [Fact]
    public async Task A_message_must_say_something_short_on_a_known_channel_and_a_ping_must_point_at_the_plot()
    {
        using var client = await PlayerAsync();
        var plot = await BuyAsync(client);
        Assert.Equal("empty_message", (await client.SendAsync("chat.send", new { channel = "world", text = "   " })).ErrorCode());
        Assert.Equal("message_too_long", (await client.SendAsync("chat.send", new { channel = "world", text = new string('a', ChatRules.MaxLength + 1) })).ErrorCode());
        Assert.Equal("bad_channel", (await client.SendAsync("chat.send", new { channel = "company", text = "hej" })).ErrorCode());
        Assert.Equal("bad_plot", (await client.SendAsync("chat.send", new { channel = "plot", text = "hej" })).ErrorCode());
        Assert.Equal("bad_ping", (await client.SendAsync("chat.send", new { channel = "world", text = "hej", pingX = 1, pingY = 1 })).ErrorCode());
        Assert.Equal("bad_ping", (await client.SendAsync("chat.send", new { channel = "plot", x = plot.X, y = plot.Y, text = "hej", pingX = 250, pingY = 1 })).ErrorCode());
        Assert.Equal("bad_ping", (await client.SendAsync("chat.send", new { channel = "plot", x = plot.X, y = plot.Y, text = "hej", pingX = 3 })).ErrorCode());

        using var stranger = await factory.ConnectAsync();
        Assert.Equal("not_logged_in", (await stranger.SendAsync("chat.send", new { channel = "world", text = "hej" })).ErrorCode());
    }

    [Fact]
    public async Task A_player_who_writes_too_fast_waits_a_moment()
    {
        using var client = await PlayerAsync();
        for (var i = 0; i < ChatRules.Burst; i++)
        {
            Assert.Equal("chat.sent", (await client.SendAsync("chat.send", new { channel = "world", text = $"wiadomość {i}" })).Type());
        }

        Assert.Equal("chat_too_fast", (await client.SendAsync("chat.send", new { channel = "world", text = "jeszcze jedna" })).ErrorCode());
        factory.Time.Advance(ChatRules.Window);
        Assert.Equal("chat.sent", (await client.SendAsync("chat.send", new { channel = "world", text = "już można" })).Type());
    }

    [Fact]
    public async Task A_client_that_was_away_gets_only_the_newest_messages()
    {
        using var client = await PlayerAsync();
        var start = (await client.SendAsync("chat.get")).Payload().GetProperty("last").GetInt64();
        for (var i = 0; i < ChatRules.MaxPerReply + 5; i++)
        {
            using var writer = await PlayerAsync();
            await writer.SendAsync("chat.send", new { channel = "world", text = $"nr {i}" });
        }

        var read = await ChatAsync(client, start);
        Assert.Equal(ChatRules.MaxPerReply, read.Count);
        Assert.Equal($"nr {ChatRules.MaxPerReply + 4}", read[^1].GetProperty("text").GetString());
        Assert.True(read.Zip(read.Skip(1)).All(p => p.First.GetProperty("id").GetInt64() < p.Second.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task Visitors_like_a_public_plot_once_and_may_take_the_like_back()
    {
        using var owner = await PlayerAsync();
        using var alice = await PlayerAsync();
        using var bob = await PlayerAsync();
        var (x, y) = await BuyAsync(owner, open: true);

        var liked = await alice.SendAsync("plot.like", new { x, y, like = true });
        Assert.Equal("plot.likes", liked.Type());
        Assert.Equal(1, liked.Payload().GetProperty("likes").GetInt32());
        Assert.True(liked.Payload().GetProperty("liked").GetBoolean());
        // Liking again does not count twice.
        Assert.Equal(1, (await alice.SendAsync("plot.like", new { x, y, like = true })).Payload().GetProperty("likes").GetInt32());
        Assert.Equal(2, (await bob.SendAsync("plot.like", new { x, y })).Payload().GetProperty("likes").GetInt32());

        // The plot tells everyone who enters how many like it, and whether they do.
        var seen = (await owner.SendAsync("plot.enter", new { x, y })).Payload();
        Assert.Equal(2, seen.GetProperty("likes").GetInt32());
        Assert.False(seen.GetProperty("liked").GetBoolean());
        Assert.True((await alice.SendAsync("plot.enter", new { x, y })).Payload().GetProperty("liked").GetBoolean());

        var unliked = await alice.SendAsync("plot.like", new { x, y, like = false });
        Assert.Equal(1, unliked.Payload().GetProperty("likes").GetInt32());
        Assert.False(unliked.Payload().GetProperty("liked").GetBoolean());
    }

    [Fact]
    public async Task Nobody_likes_their_own_plot_or_one_closed_to_visitors()
    {
        using var owner = await PlayerAsync();
        using var visitor = await PlayerAsync();
        var (x, y) = await BuyAsync(owner);
        Assert.Equal("own_plot", (await owner.SendAsync("plot.like", new { x, y })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.like", new { x, y })).ErrorCode());
        var (fx, fy) = FreePlot(Biome.Grassland);
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.like", new { x = fx, y = fy })).ErrorCode());
        Assert.Equal("bad_plot", (await visitor.SendAsync("plot.like", new { x = -1, y = -1 })).ErrorCode());
    }

    [Fact]
    public async Task The_gallery_shows_the_most_liked_public_plots_counted_every_few_minutes()
    {
        using var popular = await PlayerAsync();
        using var quiet = await PlayerAsync();
        using var closed = await PlayerAsync();
        var fans = new List<GameClient>();
        for (var i = 0; i < 3; i++)
        {
            fans.Add(await PlayerAsync());
        }

        var top = await BuyAsync(popular, open: true);
        await popular.SendAsync("world.rename", new { x = top.X, y = top.Y, name = "Huta Pod Lasem" });
        var second = await BuyAsync(quiet, open: true);
        var hidden = await BuyAsync(closed, open: true);
        foreach (var fan in fans)
        {
            await fan.SendAsync("plot.like", new { x = top.X, y = top.Y });
            await fan.SendAsync("plot.like", new { x = hidden.X, y = hidden.Y });
        }

        await fans[0].SendAsync("plot.like", new { x = second.X, y = second.Y });
        // A plot made private again leaves the gallery, likes and all.
        await closed.SendAsync("plot.setPublic", new { x = hidden.X, y = hidden.Y, @public = false });
        factory.Time.Advance(GalleryRules.Refresh);

        var gallery = (await fans[0].SendAsync("plot.gallery")).Payload().GetProperty("plots").EnumerateArray().ToList();
        Assert.DoesNotContain(gallery, p => p.GetProperty("x").GetInt32() == hidden.X && p.GetProperty("y").GetInt32() == hidden.Y);
        var first = gallery.First(p => p.GetProperty("x").GetInt32() == top.X && p.GetProperty("y").GetInt32() == top.Y);
        var next = gallery.First(p => p.GetProperty("x").GetInt32() == second.X && p.GetProperty("y").GetInt32() == second.Y);
        Assert.True(first.GetProperty("place").GetInt32() < next.GetProperty("place").GetInt32());
        Assert.Equal(3, first.GetProperty("likes").GetInt32());
        Assert.Equal("Huta Pod Lasem", first.GetProperty("name").GetString());
        Assert.Equal(Name(await popular.SendAsync("character.get")), first.GetProperty("owner").GetString());
        Assert.True(first.GetProperty("liked").GetBoolean());

        // Counted once for everybody: a like now shows only at the next count.
        await quiet.SendAsync("plot.like", new { x = top.X, y = top.Y });
        var cached = (await fans[1].SendAsync("plot.gallery")).Payload().GetProperty("plots").EnumerateArray()
            .First(p => p.GetProperty("x").GetInt32() == top.X && p.GetProperty("y").GetInt32() == top.Y);
        Assert.Equal(3, cached.GetProperty("likes").GetInt32());
        factory.Time.Advance(GalleryRules.Refresh);
        var counted = (await fans[1].SendAsync("plot.gallery")).Payload().GetProperty("plots").EnumerateArray()
            .First(p => p.GetProperty("x").GetInt32() == top.X && p.GetProperty("y").GetInt32() == top.Y);
        Assert.Equal(4, counted.GetProperty("likes").GetInt32());

        foreach (var fan in fans)
        {
            fan.Dispose();
        }
    }
}
