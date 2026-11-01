using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Abstractions;

/// <summary>
/// A self-contained part of the game server (accounts, market, production...).
/// Modules talk to each other only through their public services, never through
/// each other's tables, so any of them can later be moved to its own service.
/// </summary>
public interface IGameModule
{
    string Name { get; }

    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Runs once at startup, before the server accepts players (e.g. database migrations).</summary>
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs once at startup after every module's <see cref="InitializeAsync"/>, before the server accepts players: for data
    /// that needs other modules' tables too (e.g. objects on plots brought in line with new rules, and what they held given back).</summary>
    Task StartedAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Optional HTTP endpoints. Gameplay goes through the WebSocket instead.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
