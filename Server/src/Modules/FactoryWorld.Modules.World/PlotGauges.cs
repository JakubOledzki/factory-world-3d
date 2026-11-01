using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>Plots bought, players apart from bots.</summary>
public sealed class PlotGauges(IPlotRegistry plots, OnlinePlayers online) : IGameGauges
{
    public async Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        var counts = await plots.CountsAsync(cancellationToken);
        var owned = new List<GaugeSample>();
        var owners = new List<GaugeSample>();
        foreach (var bot in new[] { false, true })
        {
            var theirs = counts.Where(c => online.IsBot(c.Key) == bot).ToList();
            owned.Add(new(theirs.Sum(c => c.Value), ("who", GameCounters.Who(bot))));
            owners.Add(new(theirs.Count, ("who", GameCounters.Who(bot))));
        }

        return
        [
            new("factoryworld_plots_owned", "Plots bought.", owned),
            new("factoryworld_plot_owners", "Players who own at least one plot.", owners),
        ];
    }
}
