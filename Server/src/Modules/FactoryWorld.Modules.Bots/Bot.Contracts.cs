using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>The bot and the contracts board.</summary>
public sealed partial class Bot
{
    /// <summary>Delivers what it can to its NPC orders and to players' orders it has taken, takes one it can fill
    /// at once, and now and then gives one out for what it needs.</summary>
    private async Task<bool> ContractsAsync(BotWorld world, CancellationToken cancellationToken)
    {
        _contractsAt = world.Now + BotRules.ContractsEvery;
        var view = await SendAsync("contracts.get", null, cancellationToken);
        if (!view.Is("contracts"))
        {
            return false;
        }

        // NPC orders pay new money like the trader does, so they count towards what the bot may take from NPCs in a session.
        var npc = _traderIncome < BotRules.MaxTraderIncome ? view.Payload.Each("npc") : [];
        foreach (var (c, isNpc) in npc.Select(c => (c, true)).Concat(view.Payload.Each("taken").Select(c => (c, false))))
        {
            var item = c.Text("item")!;
            var left = c.Long("quantity") - c.Long("delivered");
            var have = Have(item) - (Profile.Trade == BotTrade.Trader ? 0 : Math.Min(BotRules.Keep(Profile.Trade, item), Have(item) / 2));
            if (have <= 0 || left <= 0)
            {
                continue;
            }

            var quantity = Math.Min(have, left);
            if (quantity < Math.Max(1, left / 4))
            {
                continue;
            }

            var before = _money;
            var reply = await SendAsync("contracts.deliver", new { id = c.Long("id"), quantity }, cancellationToken);
            if (reply.Is("contracts.done"))
            {
                _inventory[item] = Have(item) - quantity;
                if (isNpc)
                {
                    _traderIncome += Math.Max(0, _money - before);
                }

                return true;
            }
        }

        var taken = view.Payload.Each("taken").Count();
        if (taken < 2)
        {
            foreach (var c in view.Payload.Each("board"))
            {
                var item = c.Text("item")!;
                var quantity = c.Long("quantity");
                if (Have(item) - BotRules.Keep(Profile.Trade, item) < quantity || c.Long("rewardCents") < Floor(item) * quantity)
                {
                    continue;
                }

                var accepted = await SendAsync("contracts.accept", new { id = c.Long("id") }, cancellationToken);
                if (!accepted.Is("contracts.done"))
                {
                    continue;
                }

                var delivered = await SendAsync("contracts.deliver", new { id = c.Long("id"), quantity }, cancellationToken);
                if (delivered.Is("contracts.done"))
                {
                    _inventory[item] = Have(item) - quantity;
                }

                return true;
            }
        }

        var issued = view.Payload.Each("issued").Count(c => c.Text("state") is "open" or "taken");
        var wanted = BotRules.Buys(Profile.Trade).Where(i => Have(i) < 20 && Floor(i) > 0).ToList();
        if (issued < BotRules.MaxIssued && wanted.Count > 0 && world.Random.Next(4) == 0)
        {
            var item = wanted[world.Random.Next(wanted.Count)];
            const long quantity = 20;
            var reward = (long)Math.Round(Floor(item) * quantity * (1.2 + world.Random.NextDouble() * 0.4));
            if (reward <= (_money - BotRules.Reserve) / 3)
            {
                var reply = await SendAsync("contracts.create", new { item, quantity, rewardCents = reward, hours = 24 }, cancellationToken);
                return reply.Is("contracts.done");
            }
        }

        return false;
    }
}
