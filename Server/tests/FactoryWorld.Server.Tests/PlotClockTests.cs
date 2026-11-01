using System.IO.Compression;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>A plot works only while its owner, or for a company plot any member of the company, is in the game.</summary>
public sealed class PlotClockTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();
    private static int _plots;

    private PlotClockKeeper Keeper => factory.Services.GetRequiredService<PlotClockKeeper>();

    private Task CheckAsync() => Keeper.CheckAsync(CancellationToken.None);

    private async Task<DateTimeOffset> PlotTimeAsync((int X, int Y) plot)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPlotClock>().NowAsync(plot.X, plot.Y, CancellationToken.None);
    }

    /// <summary>A new player with a character, and the token to come back with.</summary>
    private async Task<(GameClient Client, string Token, string Name)> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        var token = await client.RegisterAsync();
        var look = new { skinTone = 0, hairStyle = 0, hairColor = 0, shirtStyle = 0, shirtColor = 0, pantsColor = 0, shoesColor = 0 };
        var name = "P" + Guid.NewGuid().ToString("N")[..10];
        Assert.Equal("character", (await client.SendAsync("character.create", new { name, appearance = look })).Type());
        return (client, token, name);
    }

    /// <summary>Closes the player's connection and waits until the server has noticed.</summary>
    private async Task LeaveAsync(GameClient client)
    {
        var account = client.AccountId;
        client.Dispose();
        var online = factory.Services.GetRequiredService<OnlinePlayers>();
        for (var i = 0; i < 100 && online.IsOnline(account); i++)
        {
            await Task.Delay(50);
        }

        Assert.False(online.IsOnline(account));
    }

    private async Task<GameClient> ComeBackAsync(string token)
    {
        var client = await factory.ConnectAsync();
        var resumed = await client.SendAsync("auth.resume", new { token });
        Assert.Equal("auth.ok", resumed.Type());
        return client;
    }

    private static (int X, int Y) FreePlot(Biome biome)
    {
        var skip = Interlocked.Increment(ref _plots) + 9800;
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

    private async Task<(int X, int Y)> BuyAsync(GameClient client, Biome biome)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000, "test", CancellationToken.None);
        }

        var plot = FreePlot(biome);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = plot.X, y = plot.Y })).Type());
        return plot;
    }

    [Fact]
    public async Task A_plot_stands_still_once_its_owner_has_been_away_longer_than_the_grace_and_goes_on_when_they_come_back()
    {
        var (client, token, _) = await PlayerAsync();
        var plot = await BuyAsync(client, Biome.Forest);

        // An oak takes 22.5 minutes in a forest.
        var entered = await client.SendAsync("plot.enter", new { x = plot.X, y = plot.Y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(entered.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        gzip.CopyTo(unpacked);
        var tiles = unpacked.ToArray();
        var tile = Enumerable.Range(0, tiles.Length).Where(i => tiles[i] == (byte)TileObject.None && i / PlotLayout.Size > 20)
            .Select(i => (X: i % PlotLayout.Size, Y: i / PlotLayout.Size)).First(t => t != PlotLayout.Spawn);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, Items.SaplingOak, 1, CancellationToken.None);
        }

        Assert.Equal("plot.tree", (await client.SendAsync("plot.plantTree", new { x = plot.X, y = plot.Y, tileX = tile.X, tileY = tile.Y, sapling = Items.SaplingOak })).Type());

        // While the owner plays, the plot keeps time with the clock.
        await CheckAsync();
        factory.Time.Advance(TimeSpan.FromMinutes(10));
        await CheckAsync();
        Assert.Equal(factory.Time.GetUtcNow(), await PlotTimeAsync(plot));

        // A dropped connection changes nothing within the grace...
        await LeaveAsync(client);
        factory.Time.Advance(PlotClockRules.Grace - TimeSpan.FromSeconds(10));
        await CheckAsync();
        Assert.Equal(factory.Time.GetUtcNow(), await PlotTimeAsync(plot));

        // ...after it, the plot stands still however long the owner stays away.
        factory.Time.Advance(TimeSpan.FromSeconds(10));
        await CheckAsync();
        var stopped = await PlotTimeAsync(plot);
        factory.Time.Advance(TimeSpan.FromHours(2));
        await CheckAsync();
        Assert.Equal(stopped, await PlotTimeAsync(plot));
        Assert.Equal(factory.Time.GetUtcNow() - TimeSpan.FromHours(2), stopped);

        // Back in the game, the oak has grown only for the 11.5 minutes the plot worked, and grows on from there.
        using var back = await ComeBackAsync(token);
        await CheckAsync();
        Assert.Equal(stopped, await PlotTimeAsync(plot));
        Assert.Equal("not_grown", (await back.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = tile.X, tileY = tile.Y })).ErrorCode());
        factory.Time.Advance(TimeSpan.FromMinutes(11));
        await CheckAsync();
        Assert.Equal("plot.harvested", (await back.SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = tile.X, tileY = tile.Y })).Type());

        // The report of the time away says the plot stood still.
        var report = (await back.SendAsync("report.away")).Payload();
        Assert.True(report.GetProperty("away").GetBoolean());
        Assert.Equal(1, report.GetProperty("plots").GetInt32());
        Assert.Equal(0, report.GetProperty("companyPlots").GetInt32());
    }

    [Fact]
    public async Task A_company_plot_works_while_any_member_plays_and_the_owners_other_plots_stand_still()
    {
        var (owner, _, _) = await PlayerAsync();
        var (member, _, memberName) = await PlayerAsync();
        Assert.Equal("company", (await owner.SendAsync("company.create", new { name = "Huta " + Guid.NewGuid().ToString("N")[..8] })).Type());
        var invitation = await owner.SendAsync("company.invite", new { name = memberName });
        Assert.Equal("company", invitation.Type());
        var company = (await member.SendAsync("company.get")).Payload().GetProperty("invitations")[0].GetProperty("company").GetGuid();
        Assert.Equal("company", (await member.SendAsync("company.accept", new { company })).Type());
        await factory.CompanyLevelAsync(owner.AccountId);

        var shared = await BuyAsync(owner, Biome.Grassland);
        var own = await BuyAsync(owner, Biome.Grassland);
        Assert.Equal("company", (await owner.SendAsync("company.addPlot", new { x = shared.X, y = shared.Y })).Type());

        await CheckAsync();
        await LeaveAsync(owner);
        factory.Time.Advance(PlotClockRules.Grace + TimeSpan.FromSeconds(1));
        await CheckAsync();
        var ownStopped = await PlotTimeAsync(own);
        factory.Time.Advance(TimeSpan.FromHours(1));
        await CheckAsync();

        Assert.Equal(factory.Time.GetUtcNow(), await PlotTimeAsync(shared));
        Assert.Equal(ownStopped, await PlotTimeAsync(own));

        // With the member gone too, the company plot stands still as well.
        await LeaveAsync(member);
        factory.Time.Advance(PlotClockRules.Grace + TimeSpan.FromSeconds(1));
        await CheckAsync();
        var sharedStopped = await PlotTimeAsync(shared);
        factory.Time.Advance(TimeSpan.FromHours(1));
        await CheckAsync();
        Assert.Equal(sharedStopped, await PlotTimeAsync(shared));
    }

    [Fact]
    public async Task After_the_server_was_down_plots_stood_still_from_the_last_check_until_their_players_are_back()
    {
        var (client, _, _) = await PlayerAsync();
        var plot = await BuyAsync(client, Biome.Grassland);
        await CheckAsync();
        var down = factory.Time.GetUtcNow();

        factory.Time.Advance(TimeSpan.FromHours(3));
        await Keeper.PauseAfterDowntimeAsync(CancellationToken.None);
        Assert.Equal(down, await PlotTimeAsync(plot));

        // The owner is still in the game: the next check sets the plot going again, three hours behind the clock.
        await CheckAsync();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(factory.Time.GetUtcNow() - TimeSpan.FromHours(3), await PlotTimeAsync(plot));
        client.Dispose();
    }
}
