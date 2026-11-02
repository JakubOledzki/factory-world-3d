using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Accounts;
using FactoryWorld.Modules.Characters;
using FactoryWorld.Modules.World;

namespace FactoryWorld.Server;

/// <summary>
/// Commands for developing and testing the game on one's own computer: money, items and tools, a plot of a chosen
/// biome, test machines on it, or all of it at once ("dev.world"). They exist only on a server started in Development
/// with <c>Dev:Commands</c> set (appsettings.Development.json), and each also refuses a connection that is not from
/// this computer and an account whose e-mail is not in <c>Dev:Accounts</c> (the local test accounts). A public server
/// (Production) has none of them, unless it names mod accounts in <c>Mod:Accounts</c> (deploy/.env MOD_ACCOUNT):
/// those few accounts may use them from anywhere, nobody else. The game's mod menu is built on them and asks
/// "dev.access" whether to show itself.
/// </summary>
public static class DevCommands
{
    /// <summary>Where a plot is looked for when no place is given: around the starter plot.</summary>
    public static readonly (int X, int Y) Around = (3852, 357);

    /// <summary>On a development server with <c>Dev:Commands</c>, or on any server (also Production) that names mod
    /// accounts in <c>Mod:Accounts</c>; with neither the commands do not exist at all.</summary>
    public static bool Enabled(IConfiguration configuration, IHostEnvironment environment) =>
        DevMode(configuration, environment) || DevAccess.Emails(configuration, "Mod:Accounts").Count > 0;

    public static bool DevMode(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment() && configuration.GetValue("Dev:Commands", false);

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<DevAccess>();
        services.AddSingleton<IMessageHandler, DevAccessHandler>();
        services.AddSingleton<IMessageHandler, DevGiveHandler>();
        services.AddSingleton<IMessageHandler, DevToolHandler>();
        services.AddSingleton<IMessageHandler, DevPlotHandler>();
        services.AddSingleton<IMessageHandler, DevMachinesHandler>();
        services.AddSingleton<IMessageHandler, DevWorldHandler>();
    }

    /// <summary>Buys the free plot of <paramref name="biome"/> nearest (<paramref name="x"/>, <paramref name="y"/>),
    /// paying for it with money given first.</summary>
    public static async Task<(int X, int Y)> PlotAsync(IServiceProvider services, Guid player, Biome biome, int x, int y, CancellationToken cancellationToken)
    {
        var map = services.GetRequiredService<WorldMap>();
        var world = services.GetRequiredService<WorldService>();
        await services.GetRequiredService<IWallet>().CreditAsync(player, 50_000_000, "dev plot", cancellationToken);
        for (var r = 0; r < 300; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || map.BiomeAt(x + dx, y + dy) != biome)
                    {
                        continue;
                    }

                    try
                    {
                        await world.BuyAsync(player, x + dx, y + dy, cancellationToken);
                        return (x + dx, y + dy);
                    }
                    catch (GameException e) when (e.Code is "plot_taken" or "not_buyable" or "outside_map")
                    {
                    }
                }
            }
        }

        throw new GameException("no_plot", $"No free plot of {biome} near {x}:{y}.");
    }

    /// <summary>The biomes the mod menu offers a plot in: those with plots to buy.</summary>
    public static readonly Biome[] Biomes = [Biome.Grassland, Biome.Forest, Biome.Farmland, Biome.Mountains, Biome.Desert,
        Biome.Tundra, Biome.Coast, Biome.Lakeshore];

    /// <summary>The kinds of machine "dev.machines" puts on a plot, so their windows can be opened and tested.</summary>
    public static readonly string[] Machines = [Items.Workbench, Items.Furnace, Items.Sawmill, Items.Assembler, Items.Computer, Items.Shop];

    /// <summary>Puts one of each of <see cref="Machines"/> on the player's plot near where they arrive, on the first
    /// tiles that take it; what could not be placed is named with why.</summary>
    public static async Task<(List<string> Placed, List<string> Failed)> MachinesAsync(
        IServiceProvider services, Guid player, int x, int y, CancellationToken cancellationToken)
    {
        var inventory = services.GetRequiredService<IInventory>();
        var objects = services.GetRequiredService<PlotObjectService>();
        var placed = new List<string>();
        var failed = new List<string>();
        var tiles = new List<(int X, int Y)>();
        for (var ty = PlotLayout.Spawn.Y + 4; ty < PlotLayout.Spawn.Y + 40; ty += 4)
        {
            for (var tx = PlotLayout.Spawn.X - 20; tx <= PlotLayout.Spawn.X + 20; tx += 5)
            {
                tiles.Add((tx, ty));
            }
        }

        var next = 0;
        foreach (var kind in Machines)
        {
            await inventory.AddAsync(player, kind, 1, cancellationToken);
            string? why = null;
            var done = false;
            while (!done && next < tiles.Count)
            {
                var (tx, ty) = tiles[next++];
                try
                {
                    await objects.PlaceAsync(player, x, y, kind, tx, ty, 0, cancellationToken);
                    done = true;
                }
                catch (GameException e)
                {
                    why = e.Code;
                    // Locked behind research or otherwise not placeable at all: no other tile helps
                    if (e.Code is not ("blocked" or "nothing_here" or "bad_position" or "not_free" or "occupied"))
                    {
                        break;
                    }
                }
            }

            if (done)
            {
                placed.Add(kind);
            }
            else
            {
                failed.Add($"{kind}: {why}");
            }
        }

        return (placed, failed);
    }
}

/// <summary>
/// Who may use the developer commands (and so the mod menu): on a development server, a connection from this computer
/// and an account whose e-mail is listed in <c>Dev:Accounts</c>; on any server, an account listed in
/// <c>Mod:Accounts</c>, from anywhere (the launcher's game on the public server). Checked on every command, so the game
/// showing or hiding its menu is only for looks.
/// </summary>
public sealed class DevAccess(IHostEnvironment environment, IConfiguration configuration)
{
    /// <summary>The local test accounts, as accounts keep their e-mails (lower-cased, trimmed).</summary>
    public IReadOnlySet<string> Accounts { get; } = Emails(configuration, "Dev:Accounts");

    /// <summary>The mod accounts of any server.</summary>
    public IReadOnlySet<string> ModAccounts { get; } = Emails(configuration, "Mod:Accounts");

    public static IReadOnlySet<string> Emails(IConfiguration configuration, string key) =>
        (configuration.GetSection(key).Get<string[]>() ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(AccountRules.NormalizeEmail)
            .ToHashSet();

    public async Task<bool> AllowedAsync(ClientContext client, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (client.AccountId is not { } player)
        {
            return false;
        }

        var local = DevCommands.DevMode(configuration, environment) && client.IsLocal;
        if (!local && ModAccounts.Count == 0)
        {
            return false;
        }

        var email = await services.GetRequiredService<AccountService>().EmailAsync(player, cancellationToken);
        return email is not null && (ModAccounts.Contains(email) || (local && Accounts.Contains(email)));
    }

    /// <summary>The player sending a developer command, or "not_test_account" for anyone else.</summary>
    public async Task<Guid> RequireAsync(ClientContext client, IServiceProvider services, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        if (!await AllowedAsync(client, services, cancellationToken))
        {
            throw new GameException("not_test_account", "Developer commands are only for the test and mod accounts.");
        }

        return player;
    }
}

/// <summary>"dev.access": whether this player may use the mod menu and, if so, what it offers: every item (id and
/// name), the tools for the tool bar and the biomes a plot can be bought in. Never an error for a player who may
/// not; a server without developer commands answers "unknown_type", which the game takes as a no too.</summary>
public sealed class DevAccessHandler(IServiceScopeFactory scopes, DevAccess access) : IMessageHandler
{
    public string Type => "dev.access";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        if (!await access.AllowedAsync(client, scope.ServiceProvider, cancellationToken))
        {
            return new Reply("dev.access", new { allowed = false });
        }

        var items = Items.Names.OrderBy(i => i.Value, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("pl-PL"), false))
            .Select(i => new { id = i.Key, name = i.Value });
        var tools = Items.Names.Keys.Where(Items.IsTool).Select(id => new { id, name = Items.Names[id] });
        var biomes = DevCommands.Biomes.Select(b => new { id = b.ToString(), biome = (int)b });
        return new Reply("dev.access", new { allowed = true, items, tools, biomes });
    }
}

public sealed record DevGiveRequest(long Cents = 0, string? Item = null, long Quantity = 1);

/// <summary>"dev.give": money (cents) and/or an item.</summary>
public sealed class DevGiveHandler(IServiceScopeFactory scopes, DevAccess access) : IMessageHandler
{
    public string Type => "dev.give";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = await access.RequireAsync(client, scope.ServiceProvider, cancellationToken);
        var request = Payload.Read<DevGiveRequest>(payload);
        if (request.Cents > 0)
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(player, request.Cents, "dev give", cancellationToken);
        }

        if (!string.IsNullOrEmpty(request.Item))
        {
            if (!Items.Names.ContainsKey(request.Item))
            {
                throw new GameException("unknown_item", $"There is no item '{request.Item}'.");
            }

            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player, request.Item, Math.Max(1, request.Quantity), cancellationToken);
        }

        return new Reply("dev.done", new { given = true });
    }
}

public sealed record DevToolRequest(string? Tool, int? Slot = null);

/// <summary>"dev.tool": a tool straight into the tool bar (a slot, or the first free one).</summary>
public sealed class DevToolHandler(IServiceScopeFactory scopes, DevAccess access) : IMessageHandler
{
    public string Type => "dev.tool";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = await access.RequireAsync(client, scope.ServiceProvider, cancellationToken);
        var request = Payload.Read<DevToolRequest>(payload);
        if (request.Tool is null || !Items.Names.ContainsKey(request.Tool))
        {
            throw new GameException("unknown_item", $"There is no item '{request.Tool}'.");
        }

        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player, request.Tool, 1, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<Equipment>().EquipAsync(player, request.Tool, request.Slot, cancellationToken);
        return new Reply("dev.done", new { tools = await scope.ServiceProvider.GetRequiredService<IEquipment>().ToolsAsync(player, cancellationToken) });
    }
}

public sealed record DevPlotRequest(string? Biome, int? X = null, int? Y = null);

/// <summary>"dev.plot": buys the free plot of a biome ("Forest", "Grassland", "Mountains"…) nearest a place, paid for.</summary>
public sealed class DevPlotHandler(IServiceScopeFactory scopes, DevAccess access) : IMessageHandler
{
    public string Type => "dev.plot";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = await access.RequireAsync(client, scope.ServiceProvider, cancellationToken);
        var request = Payload.Read<DevPlotRequest>(payload);
        if (!Enum.TryParse<Biome>(request.Biome, true, out var biome))
        {
            throw new GameException("bad_biome", $"No biome '{request.Biome}'; one of {string.Join(", ", Enum.GetNames<Biome>())}.");
        }

        var (x, y) = await DevCommands.PlotAsync(scope.ServiceProvider, player, biome,
            request.X ?? DevCommands.Around.X, request.Y ?? DevCommands.Around.Y, cancellationToken);
        return new Reply("dev.done", new { x, y, biome = biome.ToString() });
    }
}

public sealed record DevMachinesRequest(int X, int Y);

/// <summary>"dev.machines": one of each test machine (workbench, furnace, sawmill, assembler, computer, shop) on a plot of the player's.</summary>
public sealed class DevMachinesHandler(IServiceScopeFactory scopes, DevAccess access) : IMessageHandler
{
    public string Type => "dev.machines";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = await access.RequireAsync(client, scope.ServiceProvider, cancellationToken);
        var request = Payload.Read<DevMachinesRequest>(payload);
        var (placed, failed) = await DevCommands.MachinesAsync(scope.ServiceProvider, player, request.X, request.Y, cancellationToken);
        return new Reply("dev.done", new { placed, failed });
    }
}

/// <summary>"dev.world": a test world in one go: 1 000 000 $, the largest backpack with the pickaxe and the axe in the
/// tool bar, a forest, a grassland and a mountain plot, and the test machines on the grassland.</summary>
public sealed class DevWorldHandler(IServiceScopeFactory scopes, DevAccess access) : IMessageHandler
{
    public string Type => "dev.world";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = await access.RequireAsync(client, scope.ServiceProvider, cancellationToken);
        var services = scope.ServiceProvider;
        var inventory = services.GetRequiredService<IInventory>();
        var equipment = services.GetRequiredService<Equipment>();
        var backpack = Items.Backpacks.OrderByDescending(b => b.Value.ToolSlots).ThenByDescending(b => b.Value.ExtraSlots).First().Key;
        await inventory.AddAsync(player, backpack, 1, cancellationToken);
        await equipment.EquipAsync(player, backpack, null, cancellationToken);
        var slot = 0;
        foreach (var tool in new[] { Items.Pickaxe, Items.Axe })
        {
            await inventory.AddAsync(player, tool, 1, cancellationToken);
            await equipment.EquipAsync(player, tool, slot++, cancellationToken);
        }

        var plots = new List<object>();
        (int X, int Y)? grassland = null;
        foreach (var biome in new[] { Biome.Grassland, Biome.Forest, Biome.Mountains })
        {
            var (x, y) = await DevCommands.PlotAsync(services, player, biome, DevCommands.Around.X, DevCommands.Around.Y, cancellationToken);
            grassland ??= (x, y);
            plots.Add(new { x, y, biome = biome.ToString() });
        }

        var (placed, failed) = await DevCommands.MachinesAsync(services, player, grassland!.Value.X, grassland.Value.Y, cancellationToken);
        // What is left after the plots: a million dollars to work with
        var wallet = services.GetRequiredService<IWallet>();
        await wallet.CreditAsync(player, 100_000_000, "dev world", cancellationToken);
        return new Reply("dev.done", new { plots, machines = new { placed, failed }, tools = await equipment.ToolsAsync(player, cancellationToken) });
    }
}
