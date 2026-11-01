using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Wallet;

/// <summary>Money in wallets, players apart from bots, and the richest of each by name.</summary>
public sealed class WalletGauges(GameDb db, OnlinePlayers online, ICharacterNames names) : IGameGauges
{
    public const int TopCount = 10;

    public async Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        var bots = online.BotAccounts;
        var total = new Dictionary<bool, (long Cents, long Wallets)> { [false] = (0, 0), [true] = (0, 0) };
        await using (var command = await db.CommandAsync(
            "SELECT owner_id = ANY(@bots), COALESCE(SUM(balance_cents), 0)::bigint, COUNT(*) FROM wallet.wallets GROUP BY 1", cancellationToken))
        {
            command.Parameters.AddWithValue("bots", bots);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                total[reader.GetBoolean(0)] = (reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        var richest = new List<(Guid Account, long Cents, bool Bot)>();
        foreach (var bot in new[] { false, true })
        {
            await using var command = await db.CommandAsync(
                """
                SELECT owner_id, balance_cents FROM wallet.wallets
                WHERE balance_cents > 0 AND (owner_id = ANY(@bots)) = @bot
                ORDER BY balance_cents DESC, owner_id LIMIT @count
                """, cancellationToken);
            command.Parameters.AddWithValue("bots", bots);
            command.Parameters.AddWithValue("bot", bot);
            command.Parameters.AddWithValue("count", TopCount);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                richest.Add((reader.GetGuid(0), reader.GetInt64(1), bot));
            }
        }

        var named = await names.NamesAsync(richest.Select(r => r.Account).ToList(), cancellationToken);
        return
        [
            new("factoryworld_wallet_money_cents", "Cents in all wallets.",
                total.Select(t => new GaugeSample(t.Value.Cents, ("who", GameCounters.Who(t.Key)))).ToList()),
            new("factoryworld_wallets", "Wallets (accounts that ever had money).",
                total.Select(t => new GaugeSample(t.Value.Wallets, ("who", GameCounters.Who(t.Key)))).ToList()),
            new("factoryworld_richest_money_cents", $"Cents in the wallets of the {TopCount} richest players and bots, by name.",
                richest.Where(r => named.ContainsKey(r.Account))
                    .Select(r => new GaugeSample(r.Cents, ("who", GameCounters.Who(r.Bot)), ("name", named[r.Account]))).ToList()),
        ];
    }
}
