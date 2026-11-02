using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>A tree falls to the axe, a rock or a mine's load to the pickaxe, in five strikes the server counts.</summary>
public sealed class StrikeTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 9900;
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

    /// <summary>A player with a forest plot (which comes with an axe in hand) and a tree on it.</summary>
    private async Task<(GameClient Client, object Tree, object OtherTree)> ForestAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var plot = FreePlot(Biome.Forest);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = plot.X, y = plot.Y })).Type());
        var tiles = PlotLayout.Generate(plot.X, plot.Y, Biome.Forest);
        var trees = Enumerable.Range(0, tiles.Length).Where(i => tiles[i] == (byte)TileObject.Tree).Take(2)
            .Select(i => (object)new { x = plot.X, y = plot.Y, tileX = i % PlotLayout.Size, tileY = i / PlotLayout.Size }).ToList();
        return (client, trees[0], trees[1]);
    }

    [Fact]
    public async Task A_tree_falls_at_the_fifth_strike_and_not_before()
    {
        var (client, tree, _) = await ForestAsync();
        using var _ = client;

        // Without the strikes before it, the swing that should fell it does nothing.
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("not_struck", (await client.SendAsync("plot.harvest", tree)).ErrorCode());

        for (var hit = 1; hit < StrikeRules.Count; hit++)
        {
            factory.Time.Advance(HarvestRules.ChopTime / StrikeRules.Count);
            var reply = await client.SendAsync("plot.hit", tree);
            Assert.Equal(hit, reply.Payload().GetProperty("hits").GetInt32());
            Assert.Equal(StrikeRules.Count, reply.Payload().GetProperty("strikes").GetInt32());
        }

        factory.Time.Advance(HarvestRules.ChopTime / StrikeRules.Count);
        Assert.Equal("plot.harvested", (await client.SendAsync("plot.harvest", tree)).Type());
    }

    [Fact]
    public async Task Strikes_too_quick_on_another_tree_or_after_a_long_pause_do_not_count()
    {
        var (client, tree, other) = await ForestAsync();
        using var _ = client;

        // Swinging faster than the axe allows.
        Assert.Equal("too_fast", (await factory.StrikeAsync(client, "plot.harvest", tree, HarvestRules.ChopTime / StrikeRules.Count / 2)).ErrorCode());

        // Four strikes on one tree do not fell another.
        for (var hit = 1; hit < StrikeRules.Count; hit++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            await client.SendAsync("plot.hit", other);
        }

        factory.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("not_struck", (await client.SendAsync("plot.harvest", tree)).ErrorCode());

        // A long pause starts the strikes over.
        for (var hit = 1; hit < StrikeRules.Count; hit++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            await client.SendAsync("plot.hit", tree);
        }

        factory.Time.Advance(StrikeRules.Pause + TimeSpan.FromSeconds(1));
        Assert.Equal(1, (await client.SendAsync("plot.hit", tree)).Payload().GetProperty("hits").GetInt32());

        // Struck right, it falls.
        Assert.Equal("plot.harvested", (await factory.StrikeAsync(client, "plot.harvest", tree)).Type());
    }
}
