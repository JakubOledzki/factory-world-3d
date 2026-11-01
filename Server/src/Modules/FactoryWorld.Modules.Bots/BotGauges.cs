using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Bots;

/// <summary>The roster by trade: who is playing now, whose production line stands and who asks players for help.</summary>
public sealed class BotGauges(BotEngine engine) : IGameGauges
{
    public Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        var bots = engine.Bots.ToList();
        var count = new List<GaugeSample>();
        var lines = new List<GaugeSample>();
        var help = new List<GaugeSample>();
        foreach (var trade in Enum.GetValues<BotTrade>())
        {
            var label = ("trade", Label(trade));
            var ofTrade = bots.Where(b => b.Profile.Trade == trade).ToList();
            var playing = ofTrade.Count(b => b.Row.SessionUntil is not null);
            count.Add(new(playing, label, ("state", "playing")));
            count.Add(new(ofTrade.Count - playing, label, ("state", "resting")));
            lines.Add(new(ofTrade.Count(b => b.HasLine), label));
            help.Add(new(ofTrade.Count(b => b.Help is not null), label));
        }

        return Task.FromResult<IReadOnlyList<Gauge>>(
        [
            new("factoryworld_bots", "Bots of the roster by trade, playing a session now or resting.", count),
            new("factoryworld_bots_with_line", "Bots whose production line stands, as far as they saw since the server started.", lines),
            new("factoryworld_bots_asking_help", "Bots asking players for help with their line now.", help),
        ]);
    }

    /// <summary>The trade as a label: forester, miner, farmer, industrialist, trader, smelter.</summary>
    public static string Label(BotTrade trade) => trade.ToString().ToLowerInvariant();
}
