using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Accounts;

/// <summary>Registration, login and sessions. Owns the <c>accounts</c> database schema.</summary>
public sealed class AccountsModule : IGameModule
{
    public string Name => "Accounts";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AccountsDbContext>(AccountsDbContext.Schema);
        services.AddScoped<AccountService>();
        services.AddSingleton<IMessageHandler, RegisterHandler>();
        services.AddSingleton<IMessageHandler, LoginHandler>();
        services.AddSingleton<IMessageHandler, ResumeHandler>();
        services.AddSingleton<IMessageHandler, LogoutHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<AccountsDbContext>(cancellationToken);
}
