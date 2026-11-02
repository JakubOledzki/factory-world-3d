using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FactoryWorld.Server.Tests;

/// <summary>The same server as a public one would run: Production, without the development settings.</summary>
public sealed class ProductionServerFactory : ServerFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(Environments.Production);
    }
}

/// <summary>A development server with more test accounts allowed the developer commands than test@gmail.com
/// (appsettings.Development.json), one per test, as the tests share a database.</summary>
public sealed class DevServerFactory : ServerFactory
{
    public static readonly string[] TestAccounts = ["dev-world@example.com", "dev-give@example.com", "dev-access@example.com", "dev-remote@example.com"];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        for (var i = 0; i < TestAccounts.Length; i++)
        {
            // Index 0 stays test@gmail.com from appsettings.Development.json
            builder.UseSetting($"Dev:Accounts:{i + 1}", TestAccounts[i].ToUpperInvariant());
        }
    }
}

/// <summary>Developer commands give a test world on one's own development server, only to the listed test accounts,
/// and exist nowhere else.</summary>
public sealed class DevCommandsTests(DevServerFactory factory) : IClassFixture<DevServerFactory>
{
    [Fact]
    public async Task A_test_world_has_money_tools_three_plots_and_machines()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync("dev-world@example.com");

        var world = await client.SendAsync("dev.world");
        Assert.Equal("dev.done", world.Type());
        var plots = world.Payload().GetProperty("plots").EnumerateArray().Select(p => p.GetProperty("biome").GetString()).ToList();
        Assert.Equal(["Grassland", "Forest", "Mountains"], plots);
        var tools = world.Payload().GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains(Items.Pickaxe, tools);
        Assert.Contains(Items.Axe, tools);
        Assert.NotEmpty(world.Payload().GetProperty("machines").GetProperty("placed").EnumerateArray());
        Assert.True(await client.BalanceAsync() >= 100_000_000);
    }

    [Fact]
    public async Task Money_items_and_a_plot_of_a_biome_on_demand()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync("dev-give@example.com");

        Assert.Equal("dev.done", (await client.SendAsync("dev.give", new { cents = 12_345, item = Items.Wood, quantity = 7 })).Type());
        Assert.Equal(12_345, await client.BalanceAsync());
        Assert.Equal(7, (await client.InventoryAsync())[Items.Wood]);
        Assert.Equal("unknown_item", (await client.SendAsync("dev.give", new { item = "nothing" })).ErrorCode());

        var plot = (await client.SendAsync("dev.plot", new { biome = "forest" })).Payload();
        Assert.Equal("Forest", plot.GetProperty("biome").GetString());
        Assert.Equal("bad_biome", (await client.SendAsync("dev.plot", new { biome = "moon" })).ErrorCode());
    }

    [Fact]
    public async Task The_mod_menu_is_for_the_listed_test_accounts_only()
    {
        using var listed = await factory.ConnectAsync();
        await listed.RegisterWithCharacterAsync("Dev-Access@Example.com");
        var access = (await listed.SendAsync("dev.access")).Payload();
        Assert.True(access.GetProperty("allowed").GetBoolean());
        Assert.Contains(access.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.Wood);
        Assert.Contains(access.GetProperty("tools").EnumerateArray(), t => t.GetProperty("id").GetString() == Items.Axe);
        Assert.Contains(access.GetProperty("biomes").EnumerateArray(), b => b.GetProperty("id").GetString() == "Forest");

        using var other = await factory.ConnectAsync();
        await other.RegisterWithCharacterAsync();
        Assert.False((await other.SendAsync("dev.access")).Payload().GetProperty("allowed").GetBoolean());
        foreach (var type in new[] { "dev.world", "dev.give", "dev.tool", "dev.plot", "dev.machines" })
        {
            Assert.Equal("not_test_account", (await other.SendAsync(type, new { cents = 100_000, tool = Items.Axe, biome = "forest" })).ErrorCode());
        }

        Assert.Equal(0, await other.BalanceAsync());
    }

    [Fact]
    public async Task A_connection_from_another_computer_is_refused_even_for_a_test_account()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync("dev-remote@example.com");
        await using var scope = factory.Services.CreateAsyncScope();
        var access = factory.Services.GetRequiredService<DevAccess>();

        var local = new ClientContext { ConnectionId = Guid.NewGuid(), AccountId = client.AccountId, IsLocal = true };
        Assert.True(await access.AllowedAsync(local, scope.ServiceProvider, CancellationToken.None));
        var remote = new ClientContext { ConnectionId = Guid.NewGuid(), AccountId = client.AccountId, IsLocal = false };
        Assert.False(await access.AllowedAsync(remote, scope.ServiceProvider, CancellationToken.None));
        Assert.Equal("not_test_account",
            (await Assert.ThrowsAsync<GameException>(() => access.RequireAsync(remote, scope.ServiceProvider, CancellationToken.None))).Code);
    }
}

/// <summary>A Production server has no developer commands at all.</summary>
public sealed class DevCommandsProductionTests(ProductionServerFactory factory) : IClassFixture<ProductionServerFactory>
{
    [Fact]
    public async Task A_production_server_does_not_know_the_developer_commands()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        foreach (var type in new[] { "dev.access", "dev.world", "dev.give", "dev.tool", "dev.plot", "dev.machines" })
        {
            Assert.Equal("unknown_type", (await client.SendAsync(type)).ErrorCode());
        }

        Assert.Equal(0, await client.BalanceAsync());
    }
}

/// <summary>A public server (Production) that names a mod account, as deploy/.env MOD_ACCOUNT does.</summary>
public sealed class ModProductionServerFactory : ServerFactory
{
    public const string ModAccount = "mod@example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("Mod:Accounts:0", ModAccount);
    }
}

/// <summary>On a public server only the named mod account has the mod menu, from any computer; nobody else gets
/// anything from the developer commands.</summary>
public sealed class ModProductionTests(ModProductionServerFactory factory) : IClassFixture<ModProductionServerFactory>
{
    [Fact]
    public async Task Only_the_mod_account_has_the_mod_menu_from_any_computer()
    {
        using var mod = await factory.ConnectAsync();
        await mod.RegisterWithCharacterAsync(ModProductionServerFactory.ModAccount);
        Assert.True((await mod.SendAsync("dev.access")).Payload().GetProperty("allowed").GetBoolean());
        Assert.Equal("dev.done", (await mod.SendAsync("dev.give", new { cents = 100_000 })).Type());
        Assert.Equal(100_000, await mod.BalanceAsync());

        await using var scope = factory.Services.CreateAsyncScope();
        var remote = new ClientContext { ConnectionId = Guid.NewGuid(), AccountId = mod.AccountId, IsLocal = false };
        Assert.True(await factory.Services.GetRequiredService<DevAccess>().AllowedAsync(remote, scope.ServiceProvider, CancellationToken.None));

        using var other = await factory.ConnectAsync();
        await other.RegisterWithCharacterAsync();
        Assert.False((await other.SendAsync("dev.access")).Payload().GetProperty("allowed").GetBoolean());
        foreach (var type in new[] { "dev.world", "dev.give", "dev.tool", "dev.plot", "dev.machines" })
        {
            Assert.Equal("not_test_account", (await other.SendAsync(type, new { cents = 100_000, tool = Items.Axe, biome = "forest" })).ErrorCode());
        }

        Assert.Equal(0, await other.BalanceAsync());
    }
}
