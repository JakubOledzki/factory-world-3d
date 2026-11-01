using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>Settings under <c>Bots</c> in the configuration.</summary>
public sealed class BotsOptions
{
    /// <summary>Whether the bots play at all. Off, the roster is not even registered.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many bots there are: the founders first, then made-up ones (<see cref="BotRoster.First"/>).</summary>
    public int Count { get; set; } = 2000;

    /// <summary>Seconds between steps; every bot in a session takes one step per tick.</summary>
    public int TickSeconds { get; set; } = 10;

    /// <summary>How likely a bot puts a part of its new line up the wrong way round, for players to turn (0 in tests).</summary>
    public double MistakeChance { get; set; } = BotRules.MistakeChance;
}

/// <summary>Keeps the bots playing while the server runs.</summary>
public sealed class BotRunner(BotEngine engine, BotsOptions options, TimeProvider time, ILogger<BotRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        try
        {
            await engine.EnsureBotsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The bots could not be set up; they stay out of the game until the next start");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.TickSeconds)), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await engine.TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "A tick of the bots failed");
            }
        }
    }
}

/// <summary>
/// Players run by the server: they sort trash, buy plots, chop, mine, farm, smelt, craft, research, build production
/// lines on their plots, trade on the market, take orders and earn badges through the same messages a client sends, so
/// they appear in rankings and on the market like anyone else. A bot whose line does not work asks players for help
/// (<c>bots.helpWanted</c>, <c>bots.help</c>): a player comes onto its plot, puts up or turns what it asks, and is paid. Owns the <c>bots</c> schema.
/// </summary>
public sealed class BotsModule : IGameModule
{
    public string Name => "Bots";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<BotsDbContext>(BotsDbContext.Schema);
        services.AddSingleton(configuration.GetSection("Bots").Get<BotsOptions>() ?? new BotsOptions());
        services.AddSingleton<BotHelpBoard>();
        services.AddSingleton<IPlotHelpListener>(provider => provider.GetRequiredService<BotHelpBoard>());
        services.AddSingleton<IMessageHandler, HelpWantedHandler>();
        services.AddSingleton<IMessageHandler, HelpBotHandler>();
        services.AddSingleton<BotEngine>();
        services.AddScoped<IGameGauges, BotGauges>();
        services.AddHostedService<BotRunner>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<BotsDbContext>(cancellationToken);
}
