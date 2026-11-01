using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Wallet;

/// <summary>The richest players, by money in the wallet.</summary>
public sealed class WalletRanking(GameDb db) : IPlayerRanking
{
    public string Id => "money";

    public string Name => "Najbogatsi";

    public int Order => 10;

    public string Describe(long value) => $"{Number(value / 100)},{value % 100:00} $";

    public async Task<IReadOnlyList<RankedPlayer>> TopAsync(int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT owner_id, balance_cents FROM wallet.wallets
            WHERE balance_cents > 0
            ORDER BY balance_cents DESC, owner_id LIMIT @count
            """, cancellationToken);
        command.Parameters.AddWithValue("count", count);
        var top = new List<RankedPlayer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            top.Add(new(reader.GetGuid(0), reader.GetInt64(1)));
        }

        return top;
    }

    public async Task<long?> ValueAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT balance_cents FROM wallet.wallets WHERE owner_id = @owner AND balance_cents > 0", cancellationToken);
        command.Parameters.AddWithValue("owner", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as long?;
    }

    public async Task<long> CountAboveAsync(long value, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT count(*) FROM wallet.wallets WHERE balance_cents > @value", cancellationToken);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string Number(long n) =>
        n.ToString("#,0", new System.Globalization.NumberFormatInfo { NumberGroupSeparator = " " });
}
