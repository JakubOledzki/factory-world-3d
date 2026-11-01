using System.Net.Mail;

namespace FactoryWorld.Modules.Accounts;

public static class AccountRules
{
    public const int MaxEmailLength = 254;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    /// <summary>Failed logins allowed on one connection before it must reconnect.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static bool IsValidEmail(string email) =>
        email.Length <= MaxEmailLength
        && MailAddress.TryCreate(email, out var address)
        && address.Address == email
        && address.Host.Contains('.');

    public static bool IsValidPassword(string password) =>
        password.Length is >= MinPasswordLength and <= MaxPasswordLength;
}
