using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Contracts;

public sealed class ViewHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "contracts.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var contracts = scope.ServiceProvider.GetRequiredService<ContractService>();
        return new Reply("contracts", await contracts.ViewAsync(client.RequireAccount(), cancellationToken));
    }
}

public abstract class ChangeHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await ChangeAsync(scope.ServiceProvider.GetRequiredService<ContractService>(), payload, client.RequireAccount(), cancellationToken);
        return new Reply("contracts.done", new
        {
            balanceCents = result.BalanceCents,
            completed = result.Completed,
            rewardCents = result.RewardCents,
            xp = result.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
        });
    }

    protected abstract Task<ContractResult> ChangeAsync(ContractService contracts, JsonElement? payload, Guid player, CancellationToken cancellationToken);
}

public sealed class CreateHandler(IServiceScopeFactory scopes) : ChangeHandler(scopes)
{
    public override string Type => "contracts.create";

    protected override Task<ContractResult> ChangeAsync(ContractService contracts, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        contracts.CreateAsync(player, Payload.Read<CreateRequest>(payload), cancellationToken);
}

public sealed class AcceptHandler(IServiceScopeFactory scopes) : ChangeHandler(scopes)
{
    public override string Type => "contracts.accept";

    protected override Task<ContractResult> ChangeAsync(ContractService contracts, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        contracts.AcceptAsync(player, Payload.Read<ContractIdRequest>(payload).Id, cancellationToken);
}

public sealed class DeliverHandler(IServiceScopeFactory scopes) : ChangeHandler(scopes)
{
    public override string Type => "contracts.deliver";

    protected override Task<ContractResult> ChangeAsync(ContractService contracts, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        contracts.DeliverAsync(player, Payload.Read<DeliverRequest>(payload), cancellationToken);
}

public sealed class AbandonHandler(IServiceScopeFactory scopes) : ChangeHandler(scopes)
{
    public override string Type => "contracts.abandon";

    protected override Task<ContractResult> ChangeAsync(ContractService contracts, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        contracts.AbandonAsync(player, Payload.Read<ContractIdRequest>(payload).Id, cancellationToken);
}

public sealed class CancelHandler(IServiceScopeFactory scopes) : ChangeHandler(scopes)
{
    public override string Type => "contracts.cancel";

    protected override Task<ContractResult> ChangeAsync(ContractService contracts, JsonElement? payload, Guid player, CancellationToken cancellationToken) =>
        contracts.CancelAsync(player, Payload.Read<ContractIdRequest>(payload).Id, cancellationToken);
}

/// <summary>Orders with a reward and a deadline: from NPC companies and between players. Owns the <c>contracts</c> schema.</summary>
public sealed class ContractsModule : IGameModule
{
    public string Name => "Contracts";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ContractsDbContext>(ContractsDbContext.Schema);
        services.AddScoped<ContractService>();
        services.AddSingleton<IMessageHandler, ViewHandler>();
        services.AddSingleton<IMessageHandler, CreateHandler>();
        services.AddSingleton<IMessageHandler, AcceptHandler>();
        services.AddSingleton<IMessageHandler, DeliverHandler>();
        services.AddSingleton<IMessageHandler, AbandonHandler>();
        services.AddSingleton<IMessageHandler, CancelHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<ContractsDbContext>(cancellationToken);
}
