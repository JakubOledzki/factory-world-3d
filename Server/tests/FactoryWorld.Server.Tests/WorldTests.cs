using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class WorldTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    /// <summary>Plots of a biome, each used by one test only so tests on the shared database don't collide.</summary>
    private static IEnumerable<(int X, int Y)> PlotsOf(Biome biome, int skip)
    {
        var found = 0;
        for (var y = 0; y < Map.Height; y += 7)
        {
            for (var x = 0; x < Map.Width; x += 7)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                {
                    yield return (x, y);
                }
            }
        }
    }

    private async Task GiveMoneyAsync(GameClient client, long cents)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, cents, "test", CancellationToken.None);
    }

    [Fact]
    public void Plots_are_5_km_and_cover_the_globe()
    {
        // Equal-area grid: width × height × 25 km² is the Earth's surface (510 million km²).
        Assert.InRange(Map.Width * (long)Map.Height * 25, 509_000_000, 511_000_000);
    }

    [Theory]
    [InlineData(52.23, 21.01, Biome.City)] // Warszawa
    [InlineData(23.4, 12.0, Biome.Desert)] // Sahara
    [InlineData(0.0, -150.0, Biome.Ocean)] // Pacyfik
    [InlineData(-80.0, 30.0, Biome.Ice)] // Antarktyda
    [InlineData(-3.0, -62.0, Biome.Forest)] // Amazonia
    public void The_map_follows_the_real_earth(double latitude, double longitude, Biome expected)
    {
        var (x, y) = Map.PlotAt(latitude, longitude);

        // Small places can be off by a plot or two; look at the 5 × 5 plots around the point.
        var around = from dy in Enumerable.Range(-2, 5) from dx in Enumerable.Range(-2, 5) select Map.BiomeAt(x + dx, y + dy);
        Assert.Contains(expected, around);
    }

    [Fact]
    public void Plot_prices_rise_tenfold_then_jump_to_a_billion()
    {
        long[] dollars = [1_000, 10_000, 100_000, 1_000_000, 1_000_000_000, 1_000_000_000];
        for (var owned = 0; owned < dollars.Length; owned++)
        {
            Assert.Equal(dollars[owned] * 100, PlotPrices.For(owned, Biome.Forest));
        }

        Assert.Equal(2_000_00, PlotPrices.For(0, Biome.City));
    }

    [Fact]
    public async Task Info_lists_the_map_biomes_and_prices()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var info = (await client.SendAsync("world.info")).Payload();

        Assert.Equal(Map.Width, info.GetProperty("width").GetInt32());
        Assert.Equal(5, info.GetProperty("plotKm").GetInt32());
        Assert.Equal(13, info.GetProperty("biomes").GetArrayLength());
        Assert.Equal(1_000_00, info.GetProperty("nextBasePriceCents").GetInt64());
        Assert.Equal(0, info.GetProperty("plots").GetArrayLength());
    }

    [Fact]
    public async Task Buying_a_plot_takes_money_and_gives_xp_and_the_next_costs_more()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await GiveMoneyAsync(client, 1_500_00);
        var plots = PlotsOf(Biome.Forest, 0).Take(2).ToList();

        var plot = await client.SendAsync("world.plot", new { x = plots[0].X, y = plots[0].Y });
        Assert.Equal(1_000_00, plot.Payload().GetProperty("priceCents").GetInt64());

        var bought = await client.SendAsync("world.buy", new { x = plots[0].X, y = plots[0].Y });
        Assert.Equal("world.bought", bought.Type());
        Assert.Equal(500_00, bought.Payload().GetProperty("balanceCents").GetInt64());
        Assert.Equal(PlotRules.XpPerPlot, bought.Payload().GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.True(bought.Payload().GetProperty("plot").GetProperty("owner").GetProperty("mine").GetBoolean());

        var second = await client.SendAsync("world.plot", new { x = plots[1].X, y = plots[1].Y });
        Assert.Equal(10_000_00, second.Payload().GetProperty("priceCents").GetInt64());
        var tooExpensive = await client.SendAsync("world.buy", new { x = plots[1].X, y = plots[1].Y });
        Assert.Equal("not_enough_money", tooExpensive.ErrorCode());

        // The failed purchase left nothing behind.
        var info = (await client.SendAsync("world.info")).Payload();
        Assert.Equal(1, info.GetProperty("plots").GetArrayLength());
        Assert.Equal(10_000_00, info.GetProperty("nextBasePriceCents").GetInt64());
        Assert.Equal(500_00, await client.BalanceAsync());
    }

    [Fact]
    public async Task Every_plot_but_the_first_is_taxed_daily_on_its_price_and_what_stands_on_it_and_who_owes_the_tax_builds_and_buys_nothing()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await GiveMoneyAsync(client, 11_000_00);
        var plots = PlotsOf(Biome.Forest, 20).Take(3).ToList();
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = plots[0].X, y = plots[0].Y })).Type());
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x = plots[1].X, y = plots[1].Y })).Type());

        // Two chests stand on the second plot and one on the first, which is not taxed.
        long chest;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            chest = scope.ServiceProvider.GetRequiredService<IMarketPrices>().BuysCents(Items.WoodenChest)!.Value;
            var db = scope.ServiceProvider.GetRequiredService<GameDb>();
            foreach (var (plot, tile) in new[] { (plots[1], 10), (plots[1], 12), (plots[0], 10) })
            {
                await using var insert = await db.CommandAsync(
                    "INSERT INTO world.plot_objects (id, x, y, tile_x, tile_y, owner_id, kind, rotation) VALUES (@id, @x, @y, @t, @t, @owner, @kind, 0)",
                    CancellationToken.None);
                insert.Parameters.AddWithValue("id", Guid.NewGuid());
                insert.Parameters.AddWithValue("x", plot.X);
                insert.Parameters.AddWithValue("y", plot.Y);
                insert.Parameters.AddWithValue("t", tile);
                insert.Parameters.AddWithValue("owner", client.AccountId);
                insert.Parameters.AddWithValue("kind", Items.WoodenChest);
                await insert.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }

        // 0.5% a day of the 10 000 $ the second plot cost and of the two chests on it.
        var daily = (10_000_00 + 2 * chest) * 5 / 1000;
        await GiveMoneyAsync(client, 2 * daily);
        factory.Time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(1));
        var tax = (await client.SendAsync("world.info")).Payload().GetProperty("tax");
        Assert.Equal(daily, tax.GetProperty("dailyCents").GetInt64());
        Assert.Equal(0, tax.GetProperty("owedCents").GetInt64());
        Assert.Equal(0, await client.BalanceAsync());

        // A day with no money for it: entering the plot still works, building and buying do not.
        factory.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal("plot", (await client.SendAsync("plot.enter", new { x = plots[1].X, y = plots[1].Y })).Type());
        Assert.Equal("plot_tax_due", (await client.SendAsync("plot.place",
            new { x = plots[1].X, y = plots[1].Y, kind = Items.WoodenChest, tileX = 20, tileY = 20, rotation = 0 })).ErrorCode());
        Assert.Equal("plot_tax_due", (await client.SendAsync("world.buy", new { x = plots[2].X, y = plots[2].Y })).ErrorCode());
        Assert.Equal(daily, (await client.SendAsync("world.info")).Payload().GetProperty("tax").GetProperty("owedCents").GetInt64());

        // Money that comes in pays the debt the next time the plots are looked at.
        await GiveMoneyAsync(client, daily + 10_00);
        Assert.Equal(0, (await client.SendAsync("world.info")).Payload().GetProperty("tax").GetProperty("owedCents").GetInt64());
        Assert.Equal(10_00, await client.BalanceAsync());
    }

    [Fact]
    public async Task A_plot_has_only_one_owner()
    {
        using var first = await factory.ConnectAsync();
        await first.RegisterWithCharacterAsync();
        await GiveMoneyAsync(first, 1_000_00);
        using var second = await factory.ConnectAsync();
        await second.RegisterWithCharacterAsync();
        await GiveMoneyAsync(second, 1_000_00);
        var (x, y) = PlotsOf(Biome.Grassland, 10).First();

        Assert.Equal("world.bought", (await first.SendAsync("world.buy", new { x, y })).Type());
        Assert.Equal("plot_taken", (await second.SendAsync("world.buy", new { x, y })).ErrorCode());

        var seen = (await second.SendAsync("world.plot", new { x, y })).Payload();
        Assert.False(seen.GetProperty("owner").GetProperty("mine").GetBoolean());
        Assert.StartsWith("P", seen.GetProperty("owner").GetProperty("name").GetString());
        Assert.Equal(1_000_00, await second.BalanceAsync());

        var area = (await second.SendAsync("world.area", new { x = x - 5, y = y - 5, width = 10, height = 10 })).Payload();
        var owned = Assert.Single(area.GetProperty("plots").EnumerateArray());
        Assert.Equal(x, owned.GetProperty("x").GetInt32());
        Assert.False(owned.GetProperty("mine").GetBoolean());
    }

    [Fact]
    public async Task Water_and_plots_off_the_map_cannot_be_bought()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await GiveMoneyAsync(client, 1_000_00);
        var (x, y) = PlotsOf(Biome.Ocean, 0).First();

        Assert.Equal("not_buyable", (await client.SendAsync("world.buy", new { x, y })).ErrorCode());
        Assert.Equal("bad_plot", (await client.SendAsync("world.buy", new { x = -1, y = 0 })).ErrorCode());
        Assert.Equal("bad_plot", (await client.SendAsync("world.plot", new { x = Map.Width, y = 0 })).ErrorCode());
        Assert.Equal(JsonValueKind.Null, (await client.SendAsync("world.plot", new { x, y })).Payload().GetProperty("priceCents").ValueKind);
    }
}
