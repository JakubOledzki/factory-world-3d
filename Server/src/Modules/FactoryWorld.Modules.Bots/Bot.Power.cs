using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// The bot selling power: once it has researched power distribution and its line stands, a greedy bot makes a grid tie,
/// puts it up by a pole of its line and opens it to other players at the price they ask nearby. Players lay the cables
/// and pay for the power up front, as from any player.
/// </summary>
public sealed partial class Bot
{
    private bool _tieOpen;

    private void PowerCandidates(BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (_site is not { } site || Traits.Greed < BotRules.SellsPowerFrom || IsLocked(Items.GridTie))
        {
            return;
        }

        var tie = site.Plot.Objects.FirstOrDefault(o => o.Kind == Items.GridTie);
        if (tie.Kind is not null)
        {
            Goals.Drop(BotGoals.Power);
            if (!_tieOpen)
            {
                add(6, "sell power", ct => OpenTieAsync(site.Plot, tie.Id, ct));
            }

            return;
        }

        Goals.Set(new BotGoal(BotGoals.Power, 0, null, Goals[BotGoals.Power]?.Since ?? world.Now));
        if (Have(Items.GridTie) > 0)
        {
            add(8, "put up grid tie", ct => PlaceTieAsync(site, ct));
            return;
        }

        var (first, missing) = Plan(Items.GridTie, 1);
        if (missing.Count == 0 && first is not null && _workbench is not null && _craftQueue < 5)
        {
            add(5, "craft grid tie", ct => CraftAsync(first, ct));
        }
        else if (missing.FirstOrDefault(m => _traderSells.TryGetValue(m.Key, out var price) && price * m.Value + BotRules.Reserve <= Spare / 4) is { Key: not null } buy)
        {
            // What it cannot make (steel, concrete) it buys from the trader, dear: money leaves the game that way.
            add(3, "buy power parts", ct => TradeAsync("market.buy", buy.Key, buy.Value, ct));
        }
    }

    /// <summary>Puts the grid tie on a free tile by a pole of the line, where the line's power reaches it.</summary>
    private async Task<bool> PlaceTieAsync(BotLineSite site, CancellationToken cancellationToken)
    {
        var plot = site.Plot;
        var blocked = Blocked(plot);
        var size = Footprints.Size(Items.GridTie);
        foreach (var pole in plot.Objects.Where(o => o.Kind == Items.Pole))
        {
            for (var dy = -BotRules.PoleReach; dy <= BotRules.PoleReach; dy++)
            {
                for (var dx = -BotRules.PoleReach; dx <= BotRules.PoleReach; dx++)
                {
                    var (tx, ty) = (pole.X + dx, pole.Y + dy);
                    var tiles = Footprints.Tiles(Items.GridTie, tx, ty).ToList();
                    if (size > 1 && (dx, dy) == (0, 0) || tiles.Any(t => t.X < 0 || t.Y < 0 || t.X >= plot.Size || t.Y >= plot.Size || blocked[t.X, t.Y]))
                    {
                        continue;
                    }

                    var reply = await SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind = Items.GridTie, tileX = tx, tileY = ty, rotation = 0 }, cancellationToken);
                    if (!reply.Is("plot.placed"))
                    {
                        continue;
                    }

                    var placed = reply.Payload.Get("object");
                    plot.Objects.Add((placed?.Id("id") ?? Guid.Empty, Items.GridTie, tx, ty));
                    plot.Busy.UnionWith(tiles);
                    await InventoryAsync(cancellationToken);
                    _logger.LogInformation("Bot {Bot} put up a grid tie on {X}:{Y}", Row.CharacterName, plot.X, plot.Y);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Opens the grid tie to other players at the price others nearby ask, never below it, or the bots' own price.</summary>
    private async Task<bool> OpenTieAsync(BotPlot plot, Guid id, CancellationToken cancellationToken)
    {
        var offers = await SendAsync("powertie.offers", new { x = plot.X, y = plot.Y, id }, cancellationToken);
        var prices = offers.Is("powertie.offers")
            ? offers.Payload.Each("offers").Where(o => !o.True("mine")).Select(o => o.Long("priceCents")).Order().ToList()
            : [];
        var priceCents = prices.Count > 0 ? prices[prices.Count / 2] : BotRules.PowerPriceCents;
        var reply = await SendAsync("powertie.price", new { x = plot.X, y = plot.Y, id, priceCents, open = true }, cancellationToken);
        _tieOpen = reply.Is("powertie");
        if (_tieOpen)
        {
            _logger.LogInformation("Bot {Bot} sells power on {X}:{Y} at {Price} cents a kWh", Row.CharacterName, plot.X, plot.Y, priceCents);
        }

        return _tieOpen;
    }
}
