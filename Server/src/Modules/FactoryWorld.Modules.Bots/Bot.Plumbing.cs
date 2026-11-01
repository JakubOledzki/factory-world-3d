using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>Sending messages and small helpers.</summary>
public sealed partial class Bot
{
    private async Task<BotReply> SendAsync(string type, object? payload, CancellationToken cancellationToken)
    {
        var reply = await Client.SendAsync(type, payload, cancellationToken);
        if (!reply.Ok)
        {
            _logger.LogDebug("Bot {Bot}: {Type} -> {Error}", Row.CharacterName, type, reply.Error);
            return reply;
        }

        if (reply.Payload.Xp() is { } xp)
        {
            _xp = xp;
        }

        if (reply.Payload.LongOrNull("balanceCents") is { } balance)
        {
            _money = balance;
        }

        return reply;
    }

    private static string Pick(Random random, (string Crop, int Weight)[] options)
    {
        var roll = random.Next(options.Sum(o => o.Weight));
        foreach (var (crop, weight) in options)
        {
            roll -= weight;
            if (roll < 0)
            {
                return crop;
            }
        }

        return options[0].Crop;
    }
}
