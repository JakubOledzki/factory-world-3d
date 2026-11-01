using System.Security.Cryptography;
using FactoryWorld.Modules.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryWorld.Modules.Accounts;

public sealed record LoginResult(Guid AccountId, string Token);

public sealed class AccountService(AccountsDbContext db, TimeProvider time)
{
    private static readonly PasswordHasher<Account> Hasher = new();

    /// <summary>Verified against when the e-mail is unknown, so both cases take the same time.</summary>
    private static readonly string DummyHash = Hasher.HashPassword(null!, "not-a-real-password");

    /// <summary>
    /// Password hashing is slow on purpose (PBKDF2) and takes a whole core for a moment. Only this many run at once,
    /// so that a wave of logins (e.g. after a restart) waits in line instead of taking every core from the game.
    /// </summary>
    private static readonly SemaphoreSlim PasswordWork = new(Math.Max(1, Environment.ProcessorCount - 1));

    public async Task<LoginResult> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        email = AccountRules.NormalizeEmail(email);
        if (!AccountRules.IsValidEmail(email))
        {
            throw new GameException("invalid_email", "This e-mail address is not valid.");
        }

        if (!AccountRules.IsValidPassword(password))
        {
            throw new GameException("weak_password",
                $"Password must be {AccountRules.MinPasswordLength}-{AccountRules.MaxPasswordLength} characters long.");
        }

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = "",
            CreatedAt = time.GetUtcNow(),
        };
        account.PasswordHash = await HashingAsync(() => Hasher.HashPassword(account, password), cancellationToken);
        db.Accounts.Add(account);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new GameException("email_taken", "An account with this e-mail already exists.");
        }

        return await StartSessionAsync(account.Id, cancellationToken);
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        email = AccountRules.NormalizeEmail(email);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Email == email, cancellationToken);

        var result = await HashingAsync(() => Hasher.VerifyHashedPassword(account!, account?.PasswordHash ?? DummyHash, password), cancellationToken);
        if (account is null || result == PasswordVerificationResult.Failed)
        {
            throw new GameException("invalid_credentials", "Wrong e-mail or password.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            account.PasswordHash = await HashingAsync(() => Hasher.HashPassword(account, password), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await StartSessionAsync(account.Id, cancellationToken);
    }

    /// <summary>Logs in with a token saved on the device by an earlier login.</summary>
    public async Task<Guid> ResumeAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
        if (session is null || session.ExpiresAt <= time.GetUtcNow())
        {
            throw new GameException("session_expired", "Your session has expired. Log in again.");
        }

        return session.AccountId;
    }

    public async Task LogoutAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        await db.Sessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>A new login ends the account's sessions on every other device: one account plays in one place.</summary>
    private async Task<LoginResult> StartSessionAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await db.Sessions.Where(s => s.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        db.Sessions.Add(new Session
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now + AccountRules.SessionLifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new LoginResult(accountId, token);
    }

    private static async Task<T> HashingAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await PasswordWork.WaitAsync(cancellationToken);
        try
        {
            return work();
        }
        finally
        {
            PasswordWork.Release();
        }
    }

    private static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
