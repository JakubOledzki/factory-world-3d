using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// The bot and the banks of companies: a greedy bot keeps part of its savings on an account in the bank with the most
/// customers, players' and bots' alike, and takes it out again when its wallet runs low. Banks pay no interest, so this
/// makes no money; it gives banks customers and keeps money out of the bot's reach for the market.
/// </summary>
public sealed partial class Bot
{
    private DateTimeOffset _bankAt;

    private void BankCandidates(BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (_bankAt <= world.Now && Traits.Greed >= BotRules.BanksFrom && _plots.Count > 0)
        {
            add(3, "bank", ct => BankAsync(world, ct));
        }
    }

    private async Task<bool> BankAsync(BotWorld world, CancellationToken cancellationToken)
    {
        _bankAt = world.Now + BotRules.BankEvery;
        var reply = await SendAsync("bank.get", null, cancellationToken);
        if (!reply.Is("bank"))
        {
            return false;
        }

        var banks = reply.Payload.Each("banks").Where(b => b.Each("buildings").Any()).ToList();
        if (banks.FirstOrDefault(b => b.LongOrNull("balanceCents") is not null) is not { ValueKind: JsonValueKind.Object } mine)
        {
            if (banks.Count == 0 || _money < BotRules.SavesInBankFrom)
            {
                return false;
            }

            // The bank most customers trust, and of those the one money reaches soonest.
            var chosen = banks.OrderByDescending(b => b.Long("customers")).ThenBy(b => b.Get("trip")?.Long("seconds") ?? long.MaxValue).First();
            var opened = await SendAsync("bank.open", new { bank = chosen.Text("id") }, cancellationToken);
            if (opened.Is("bank"))
            {
                _logger.LogInformation("Bot {Bot} opened an account in the bank {Bank}", Row.CharacterName, chosen.Text("name"));
            }

            return opened.Is("bank");
        }

        var bank = mine.Text("id");
        var balance = mine.Long("balanceCents");
        if (_money < BotRules.Reserve + BotRules.WithdrawBelow && balance > 0)
        {
            var cents = Math.Min(balance, BotRules.BankBatch);
            return (await SendAsync("bank.withdraw", new { bank, cents }, cancellationToken)).Is("bank");
        }

        // Savings for a plot or an office go in too: a tenth of what it has at a time.
        if (_money >= BotRules.SavesInBankFrom)
        {
            var cents = Math.Min(_money / 10, BotRules.BankBatch);
            var deposited = await SendAsync("bank.deposit", new { bank, cents }, cancellationToken);
            if (deposited.Is("bank"))
            {
                _money -= cents;
                return true;
            }
        }

        return false;
    }
}
