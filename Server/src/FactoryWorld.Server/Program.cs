using Npgsql;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Accounts;
using FactoryWorld.Modules.Achievements;
using FactoryWorld.Modules.Bots;
using FactoryWorld.Modules.Characters;
using FactoryWorld.Modules.Chat;
using FactoryWorld.Modules.Contracts;
using FactoryWorld.Modules.Companies;
using FactoryWorld.Modules.Crafting;
using FactoryWorld.Modules.Inventory;
using FactoryWorld.Modules.Law;
using FactoryWorld.Modules.Logistics;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Politics;
using FactoryWorld.Modules.Power;
using FactoryWorld.Modules.Recycling;
using FactoryWorld.Modules.Research;
using FactoryWorld.Modules.Rivalry;
using FactoryWorld.Modules.Session;
using FactoryWorld.Modules.Tutorial;
using FactoryWorld.Modules.Statistics;
using FactoryWorld.Modules.Wallet;
using FactoryWorld.Modules.World;
using FactoryWorld.Modules.Reports;
using FactoryWorld.Modules.Collections;
using FactoryWorld.Modules.Expeditions;
using FactoryWorld.Modules.Seasons;
using FactoryWorld.Modules.Mentoring;
using FactoryWorld.Modules.Puzzles;
using FactoryWorld.Modules.Timelapse;
using FactoryWorld.Server;

var builder = WebApplication.CreateBuilder(args);

// Every module of the modular monolith is listed here, in one place.
IGameModule[] modules =
[
    new SessionModule(),
    new AccountsModule(),
    new CharactersModule(),
    new InventoryModule(),
    new WalletModule(),
    new MarketModule(),
    new RecyclingModule(),
    new WorldModule(),
    new CraftingModule(),
    new LogisticsModule(),
    new PowerModule(),
    new TutorialModule(),
    new StatisticsModule(),
    new ContractsModule(),
    new CompaniesModule(),
    new PoliticsModule(),
    new ResearchModule(),
    new AchievementsModule(),
    new RivalryModule(),
    new BotsModule(),
    new LawModule(),
    new ChatModule(),
    new ReportsModule(),
    new CollectionsModule(),
    new ExpeditionsModule(),
    new SeasonsModule(),
    new MentoringModule(),
    new PuzzlesModule(),
    new TimelapseModule(),
];

foreach (var module in modules)
{
    module.RegisterServices(builder.Services, builder.Configuration);
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MessageDispatcher>();
builder.Services.AddSingleton<OnlinePlayers>();
builder.Services.AddSingleton<ServerLoad>();
builder.Services.AddSingleton<GameCounters>();
builder.Services.AddSingleton<GaugeReadings>();
builder.Services.AddHostedService<LoadReport>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
        .AllowAnyHeader()
        .AllowAnyMethod()));

var connectionString = builder.Configuration.GetConnectionString("Game");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Set the ConnectionStrings:Game setting to a PostgreSQL connection string.");
}

builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "postgres");
builder.Services.AddSingleton(NpgsqlDataSource.Create(TunedForGame(connectionString)));
builder.Services.AddScoped<GameDb>();

var app = builder.Build();

foreach (var module in modules)
{
    await module.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);
}

foreach (var module in modules)
{
    await module.StartedAsync(app.Services, app.Lifetime.ApplicationStopping);
}

app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapHealthChecks("/health");
app.MapGet("/metrics", MetricsEndpoint.HandleAsync);
app.MapGet("/", () => Results.Text("Factory World server"));
app.Map("/ws", GameSocketEndpoint.HandleAsync);

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

app.Run();

// Every request borrows a pooled connection for a few short queries. By default Npgsql resets each connection
// when it goes back to the pool (one extra round trip) and PostgreSQL parses and plans every query again; with
// thousands of players that overhead, not the queries, saturated the database (tools/loadtest). The pool is also
// kept small: Npgsql's default of 100 equals PostgreSQL's whole connection limit, so under load nothing else could
// connect, and with 30 the load test answered faster, not slower. Settings written in the connection string itself
// win over these defaults.
static string TunedForGame(string connectionString)
{
    // The plain builder knows only the keys actually written; compared without spaces, as Npgsql accepts both forms.
    var given = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }.Keys
        .Cast<string>()
        .Select(k => k.Replace(" ", "").ToLowerInvariant())
        .ToHashSet();
    var tuned = new NpgsqlConnectionStringBuilder(connectionString);
    if (!given.Contains("noresetonclose"))
    {
        tuned.NoResetOnClose = true;
    }

    if (!given.Contains("maxautoprepare"))
    {
        tuned.MaxAutoPrepare = 64;
    }

    if (!given.Contains("autoprepareminusages"))
    {
        tuned.AutoPrepareMinUsages = 2;
    }

    if (!given.Contains("maxpoolsize") && !given.Contains("maximumpoolsize"))
    {
        tuned.MaxPoolSize = 30;
    }

    return tuned.ConnectionString;
}

// Exposed for WebApplicationFactory in tests.
public partial class Program;
