using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Accounts;

public sealed record CredentialsRequest(string Email, string Password);

public sealed record TokenRequest(string Token);

/// <summary>Base for auth handlers: each request gets its own database scope.</summary>
public abstract class AccountHandler(IServiceScopeFactory scopes, OnlinePlayers online) : IMessageHandler
{
    private const string FailedLoginsKey = "accounts.failedLogins";

    public abstract string Type { get; }

    protected OnlinePlayers Online => online;

    public bool RequiresAccount => false;

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await HandleAsync(payload, client, scope.ServiceProvider.GetRequiredService<AccountService>(), cancellationToken);
    }

    protected abstract Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, AccountService accounts, CancellationToken cancellationToken);

    /// <summary>The account now plays on this connection only: wherever else it was logged in is closed.</summary>
    protected Reply LoggedIn(ClientContext client, LoginResult result)
    {
        online.Leave(client);
        client.AccountId = result.AccountId;
        online.Enter(client, result.AccountId);
        client.Items.Remove(FailedLoginsKey);
        return new Reply("auth.ok", new { accountId = result.AccountId, token = result.Token });
    }

    /// <summary>Runs a login attempt, counting failures so one connection cannot guess passwords forever.</summary>
    protected static async Task<Reply> GuardedAsync(ClientContext client, Func<Task<Reply>> attempt)
    {
        var failures = client.Items.TryGetValue(FailedLoginsKey, out var n) ? (int)n : 0;
        if (failures >= AccountRules.MaxFailedLogins)
        {
            throw new GameException("too_many_attempts", "Too many failed attempts. Reconnect and try again.");
        }

        try
        {
            return await attempt();
        }
        catch (GameException)
        {
            client.Items[FailedLoginsKey] = failures + 1;
            throw;
        }
    }
}

public sealed class RegisterHandler(IServiceScopeFactory scopes, OnlinePlayers online) : AccountHandler(scopes, online)
{
    public override string Type => "auth.register";

    protected override async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, AccountService accounts, CancellationToken cancellationToken)
    {
        var request = Payload.Read<CredentialsRequest>(payload);
        return LoggedIn(client, await accounts.RegisterAsync(request.Email ?? "", request.Password ?? "", cancellationToken));
    }
}

public sealed class LoginHandler(IServiceScopeFactory scopes, OnlinePlayers online) : AccountHandler(scopes, online)
{
    public override string Type => "auth.login";

    protected override Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, AccountService accounts, CancellationToken cancellationToken) =>
        GuardedAsync(client, async () =>
        {
            var request = Payload.Read<CredentialsRequest>(payload);
            return LoggedIn(client, await accounts.LoginAsync(request.Email ?? "", request.Password ?? "", cancellationToken));
        });
}

public sealed class ResumeHandler(IServiceScopeFactory scopes, OnlinePlayers online) : AccountHandler(scopes, online)
{
    public override string Type => "auth.resume";

    protected override Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, AccountService accounts, CancellationToken cancellationToken) =>
        GuardedAsync(client, async () =>
        {
            var request = Payload.Read<TokenRequest>(payload);
            var accountId = await accounts.ResumeAsync(request.Token ?? "", cancellationToken);
            return LoggedIn(client, new LoginResult(accountId, request.Token!));
        });
}

public sealed class LogoutHandler(IServiceScopeFactory scopes, OnlinePlayers online) : AccountHandler(scopes, online)
{
    public override string Type => "auth.logout";

    protected override async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, AccountService accounts, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TokenRequest>(payload);
        await accounts.LogoutAsync(request.Token ?? "", cancellationToken);
        Online.Leave(client);
        client.AccountId = null;
        return new Reply("auth.loggedOut");
    }
}
