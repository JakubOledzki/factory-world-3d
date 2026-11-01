using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class StatisticsTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
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

    private static async Task<string> GarageObjectAsync(GameClient client, string kind)
    {
        var garage = await client.SendAsync("garage.get");
        return garage.Payload().GetProperty("objects").EnumerateArray()
            .First(o => o.GetProperty("kind").GetString() == kind).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> StatsAsync(GameClient client, int minutes = 10)
    {
        var reply = await client.SendAsync("stats.production", new { minutes });
        Assert.Equal("stats.production", reply.Type());
        return reply.Payload();
    }

    private static JsonElement? Item(JsonElement stats, string item) =>
        stats.GetProperty("items").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(i => i!.Value.GetProperty("id").GetString() == item);

    [Fact]
    public async Task A_new_player_has_empty_statistics_with_a_bar_per_minute()
    {
        using var client = await PlayerAsync();
        var stats = await StatsAsync(client);
        Assert.Equal(10, stats.GetProperty("minutes").GetInt32());
        Assert.Equal(1, stats.GetProperty("barMinutes").GetInt32());
        Assert.Empty(stats.GetProperty("items").EnumerateArray());
        Assert.Empty(stats.GetProperty("stalls").EnumerateArray());
        Assert.Equal([10, 60, 1440], stats.GetProperty("periods").EnumerateArray().Select(p => p.GetInt32()));
        Assert.Equal("bad_period", (await client.SendAsync("stats.production", new { minutes = 7 })).ErrorCode());
    }

    [Fact]
    public async Task A_furnace_counts_each_ingot_once_with_the_ore_and_coal_it_used_and_says_when_it_runs_out_of_coal()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.Furnace, 1);
        await GiveAsync(client, Items.IronOre, 6);
        await GiveAsync(client, Items.Coal, 1);
        Assert.Equal("garage", (await client.SendAsync("garage.place", new { kind = Items.Furnace, x = 7, y = 4 })).Type());
        var furnace = await GarageObjectAsync(client, Items.Furnace);
        await client.SendAsync("furnace.load", new { furnace, item = Items.IronOre, quantity = 6 });
        await client.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 1 });

        // One coal lasts 5 smelts of 10 s; the 6th ore waits for more coal.
        factory.Time.Advance(TimeSpan.FromSeconds(30));
        var working = await StatsAsync(client);
        Assert.Contains(working.GetProperty("machines").EnumerateArray(), m =>
            m.GetProperty("kind").GetString() == Items.Furnace && m.GetProperty("working").GetInt32() == 1);
        await client.SendAsync("furnace.get", new { furnace });
        factory.Time.Advance(TimeSpan.FromSeconds(60));
        await client.SendAsync("furnace.get", new { furnace });
        await client.SendAsync("furnace.get", new { furnace });

        var stats = await StatsAsync(client);
        var ingots = Item(stats, Items.IronIngot)!.Value;
        Assert.Equal(5, ingots.GetProperty("produced").GetInt64());
        Assert.Equal(0, ingots.GetProperty("consumed").GetInt64());
        Assert.Equal(5, ingots.GetProperty("producedBars").EnumerateArray().Sum(b => b.GetInt64()));
        Assert.True(ingots.GetProperty("producedPerMinute").GetDouble() > 0);
        Assert.Equal(5, Item(stats, Items.IronOre)!.Value.GetProperty("consumed").GetInt64());
        Assert.Equal(1, Item(stats, Items.Coal)!.Value.GetProperty("consumed").GetInt64());

        var stall = Assert.Single(stats.GetProperty("stalls").EnumerateArray());
        Assert.Equal(Items.Furnace, stall.GetProperty("kind").GetString());
        Assert.Equal("no_fuel", stall.GetProperty("reason").GetString());
        Assert.Equal(Items.IronIngot, stall.GetProperty("product").GetString());
        Assert.Equal(Items.Coal, Assert.Single(stall.GetProperty("missing").EnumerateArray()).GetProperty("id").GetString());

        // Ore and coal are used up and nothing of the player's makes them.
        var short_ = stats.GetProperty("shortages").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains(Items.IronOre, short_);
        Assert.Contains(Items.Coal, short_);
        Assert.DoesNotContain(Items.IronIngot, short_);

        // Taking the ingots out is not making them again.
        await client.SendAsync("furnace.take", new { furnace, what = "output" });
        Assert.Equal(5, Item(await StatsAsync(client), Items.IronIngot)!.Value.GetProperty("produced").GetInt64());
    }

    [Fact]
    public async Task Workbench_orders_count_when_done_and_old_minutes_leave_the_short_period()
    {
        using var client = await PlayerAsync();
        await GiveAsync(client, Items.IronIngot, 1);
        var workbench = await GarageObjectAsync(client, Items.Workbench);
        Assert.Equal("crafting", (await client.SendAsync("crafting.craft", new { workbench, recipe = Items.Nails })).Type());
        Assert.Null(Item(await StatsAsync(client), Items.Nails));

        factory.Time.Advance(TimeSpan.FromSeconds(10));
        await client.SendAsync("crafting.get");
        var stats = await StatsAsync(client);
        var nails = Item(stats, Items.Nails)!.Value;
        Assert.True(nails.GetProperty("produced").GetInt64() > 0);
        Assert.Equal(1, Item(stats, Items.IronIngot)!.Value.GetProperty("consumed").GetInt64());

        factory.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(Item(await StatsAsync(client), Items.Nails));
        var hour = await StatsAsync(client, 60);
        Assert.Equal(20, Item(hour, Items.Nails)!.Value.GetProperty("producedBars").GetArrayLength());
        Assert.Equal(nails.GetProperty("produced").GetInt64(), Item(hour, Items.Nails)!.Value.GetProperty("produced").GetInt64());
    }

    [Fact]
    public async Task Recording_drops_the_minutes_older_than_a_day_also_for_a_player_who_never_looks()
    {
        var owner = Guid.NewGuid();
        async Task RecordAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IProductionStats>().RecordAsync(
                owner, [ProductionEntry.Made(Items.Nails, 1, factory.Time.GetUtcNow())], CancellationToken.None);
        }

        async Task<long> MinutesAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var command = await scope.ServiceProvider.GetRequiredService<GameDb>().CommandAsync(
                "SELECT count(*) FROM stats.production WHERE owner_id = @owner", CancellationToken.None);
            command.Parameters.AddWithValue("owner", owner);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        await RecordAsync();
        factory.Time.Advance(TimeSpan.FromMinutes(5));
        await RecordAsync();
        Assert.Equal(2, await MinutesAsync());

        factory.Time.Advance(TimeSpan.FromHours(25));
        await RecordAsync();
        Assert.Equal(1, await MinutesAsync());
    }
}
