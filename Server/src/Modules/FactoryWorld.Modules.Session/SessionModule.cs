using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Session;

/// <summary>Connection handshake and keep-alive. Accounts and login arrive in stage 1.</summary>
public sealed class SessionModule : IGameModule
{
    public string Name => "Session";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IMessageHandler, HelloHandler>();
        services.AddSingleton<IMessageHandler, PingHandler>();
    }
}
