using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Export by rail. An export chest stands by a plot's station; the player sets a price for each kind of goods in it, and every
/// full hour a train takes everything priced to the nearest buying market, a building a player keeps in a town (one a town),
/// which lists the goods on the players' market at those prices when the train gets there. Trains need no track over land,
/// <see cref="MinutesPerPlot"/> a plot; they go no way over water, so to a market on another landmass a train rolls onto a ferry
/// at a rail port on the coast, which players build too, and sails <see cref="SeaFactor"/> times as slow to another port. The
/// route is the quickest one, through any ports. Whoever owns a market takes a commission on other players' goods it lists,
/// and whoever owns a port a fee for each train of another player's it takes; both go straight into their wallets.
/// </summary>
public static class ExportRules
{
    /// <summary>A train calls at every export chest each full hour.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <summary>Over land a train needs a tenth of a minute a plot: 10 plots a minute, 100 plots 10 minutes.</summary>
    public const double MinutesPerPlot = 0.1;

    /// <summary>On a ferry it goes this many times as slow as over land.</summary>
    public const double SeaFactor = 3;

    /// <summary>The shortest trip, e.g. from the town of the market itself.</summary>
    public static readonly TimeSpan MinTrip = TimeSpan.FromMinutes(1);

    /// <summary>How many pieces the chest holds in all, and of how many kinds.</summary>
    public const int Capacity = 1600;

    public const int MaxKinds = 16;

    /// <summary>The chest stands at most this many tiles from the station building or its platform track.</summary>
    public const int StationReach = 3;

    /// <summary>The commission a market's owner sets on other players' goods, in percent, and what a new market starts with.</summary>
    public const int MaxCommissionPercent = 20;

    public const int DefaultCommissionPercent = 5;

    /// <summary>The fee a port's owner sets for each train of another player's, and what a new port starts with.</summary>
    public const long MaxPortFeeCents = 1000_00;

    public const long DefaultPortFeeCents = 10_00;

    public static bool NearStation(int tileX, int tileY) =>
        RailRules.StationTiles().Any(t => Math.Max(Math.Abs(t.TileX - tileX), Math.Abs(t.TileY - tileY)) <= StationReach);

    /// <summary>The first full hour after <paramref name="at"/>.</summary>
    public static DateTimeOffset NextPickup(DateTimeOffset at)
    {
        var hour = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, 0, 0, TimeSpan.Zero);
        return hour + Every;
    }

    public static TimeSpan TripTime(double landPlots, double seaPlots) =>
        TimeSpan.FromMinutes(Math.Max(MinTrip.TotalMinutes, (landPlots + seaPlots * SeaFactor) * MinutesPerPlot));

    /// <summary>A market's commission on goods worth <paramref name="cents"/>, rounded up to a cent.</summary>
    public static long Commission(long cents, int percent) => (cents * percent + 99) / 100;
}

/// <summary>
/// The landmasses of the map: plots joined by land, side by side (around the globe's seam too), carry the same number; the sea
/// and lakes carry none. A train goes over land only between plots of one landmass. Worked out once, a row of runs of land at a time.
/// </summary>
public sealed class LandMasses
{
    private readonly int[][] _starts;
    private readonly int[][] _ends;
    private readonly int[][] _labels;

    public LandMasses(WorldMap map)
    {
        Width = map.Width;
        var height = map.Height;
        _starts = new int[height][];
        _ends = new int[height][];
        _labels = new int[height][];
        var parent = new List<int>();
        int Find(int run)
        {
            while (parent[run] != run)
            {
                parent[run] = parent[parent[run]];
                run = parent[run];
            }

            return run;
        }

        void Union(int a, int b)
        {
            (a, b) = (Find(a), Find(b));
            if (a != b)
            {
                parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }

        var first = new int[height];
        var starts = new List<int>();
        var ends = new List<int>();
        for (var y = 0; y < height; y++)
        {
            starts.Clear();
            ends.Clear();
            for (var x = 0; x < Width;)
            {
                if (map.IsWater(x, y))
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < Width && !map.IsWater(x, y))
                {
                    x++;
                }

                starts.Add(start);
                ends.Add(x - 1);
            }

            _starts[y] = starts.ToArray();
            _ends[y] = ends.ToArray();
            first[y] = parent.Count;
            for (var i = 0; i < starts.Count; i++)
            {
                parent.Add(parent.Count);
            }

            // Around the seam: land at both ends of a row is one.
            if (starts.Count > 1 && starts[0] == 0 && ends[^1] == Width - 1)
            {
                Union(first[y], first[y] + starts.Count - 1);
            }

            if (y == 0)
            {
                continue;
            }

            // Runs that touch the row above.
            var (above, here) = (0, 0);
            while (above < _starts[y - 1].Length && here < starts.Count)
            {
                if (_ends[y - 1][above] >= starts[here] && _starts[y - 1][above] <= ends[here])
                {
                    Union(first[y - 1] + above, first[y] + here);
                }

                if (_ends[y - 1][above] < ends[here])
                {
                    above++;
                }
                else
                {
                    here++;
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            _labels[y] = Enumerable.Range(first[y], _starts[y].Length).Select(Find).ToArray();
        }
    }

    public int Width { get; }

    /// <summary>The landmass of a plot, or null on water.</summary>
    public int? Of(int x, int y)
    {
        if (y < 0 || y >= _starts.Length)
        {
            return null;
        }

        var i = Array.BinarySearch(_starts[y], x);
        i = i >= 0 ? i : ~i - 1;
        return i >= 0 && x <= _ends[y][i] ? _labels[y][i] : null;
    }

    public bool Joined(int x, int y, int toX, int toY) => Of(x, y) is { } a && a == Of(toX, toY);
}

/// <summary>A buying market or a rail port, as the router sees it: <paramref name="Value"/> is the market's commission in percent
/// or the port's fee in cents; <paramref name="Owner"/> owns the plot it stands on.</summary>
public sealed record ExportPost(Guid Id, string Kind, int X, int Y, Guid Owner, long Value);

/// <summary>One stretch of an export train's way: over land or, <paramref name="BySea"/>, on a ferry from port to port.</summary>
public sealed record ExportLeg(int FromX, int FromY, int ToX, int ToY, bool BySea, double Plots);

/// <summary>The quickest way from a plot to a buying market, with the ports the train takes a ferry from and to.</summary>
public sealed record ExportRoute(ExportPost Market, IReadOnlyList<ExportPost> Ports, IReadOnlyList<ExportLeg> Legs)
{
    public double LandPlots => Legs.Where(l => !l.BySea).Sum(l => l.Plots);

    public double SeaPlots => Legs.Where(l => l.BySea).Sum(l => l.Plots);

    public TimeSpan Trip => ExportRules.TripTime(LandPlots, SeaPlots);
}

public static class ExportRouter
{
    /// <summary>The quickest way from (<paramref name="x"/>, <paramref name="y"/>) to any of the markets: straight over land within a
    /// landmass, and from any port to any other by sea; null when no market can be reached.</summary>
    public static ExportRoute? Find(LandMasses land, int x, int y, IReadOnlyList<ExportPost> markets, IReadOnlyList<ExportPost> ports)
    {
        // Nodes: the plot, then the ports, then the markets, which a train only ever ends at.
        var nodes = new List<(int X, int Y)> { (x, y) };
        nodes.AddRange(ports.Select(p => (p.X, p.Y)));
        nodes.AddRange(markets.Select(m => (m.X, m.Y)));
        var mass = nodes.Select(n => land.Of(n.X, n.Y)).ToArray();
        var time = Enumerable.Repeat(double.PositiveInfinity, nodes.Count).ToArray();
        var from = Enumerable.Repeat(-1, nodes.Count).ToArray();
        var done = new bool[nodes.Count];
        time[0] = 0;
        bool IsPort(int i) => i >= 1 && i <= ports.Count;
        while (true)
        {
            var at = -1;
            for (var i = 0; i < nodes.Count; i++)
            {
                if (!done[i] && !double.IsInfinity(time[i]) && (at < 0 || time[i] < time[at]))
                {
                    at = i;
                }
            }

            if (at < 0)
            {
                return null;
            }

            if (at > ports.Count)
            {
                return Route(at);
            }

            done[at] = true;
            for (var to = 1; to < nodes.Count; to++)
            {
                if (done[to] || to == at)
                {
                    continue;
                }

                var plots = Distance(at, to);
                var cost = mass[at] is { } m && m == mass[to] ? plots
                    : IsPort(at) && IsPort(to) ? plots * ExportRules.SeaFactor
                    : double.PositiveInfinity;
                if (time[at] + cost < time[to])
                {
                    (time[to], from[to]) = (time[at] + cost, at);
                }
            }
        }

        double Distance(int a, int b) => CityRules.Distance(land.Width, nodes[a].X, nodes[a].Y, nodes[b].X, nodes[b].Y);

        ExportRoute Route(int market)
        {
            var path = new List<int>();
            for (var i = market; i >= 0; i = from[i])
            {
                path.Insert(0, i);
            }

            var legs = new List<ExportLeg>();
            for (var k = 1; k < path.Count; k++)
            {
                var (a, b) = (path[k - 1], path[k]);
                var bySea = !(mass[a] is { } m && m == mass[b]);
                legs.Add(new ExportLeg(nodes[a].X, nodes[a].Y, nodes[b].X, nodes[b].Y, bySea, Distance(a, b)));
            }

            // Only the ports a ferry leaves from or lands at are used; one passed by over land is not.
            var used = new List<ExportPost>();
            for (var k = 1; k < path.Count; k++)
            {
                if (legs[k - 1].BySea)
                {
                    foreach (var i in new[] { path[k - 1], path[k] })
                    {
                        if (!used.Contains(ports[i - 1]))
                        {
                            used.Add(ports[i - 1]);
                        }
                    }
                }
            }

            return new ExportRoute(markets[market - 1 - ports.Count], used, legs);
        }
    }
}

/// <summary>Where buying markets may stand: in a city, one to a town.</summary>
public static class ExportSites
{
    public static async Task RequireAsync(GameDb db, WorldMap map, Towns towns, string kind, int x, int y, CancellationToken cancellationToken)
    {
        if (kind != Items.BuyMarket || map.BiomeAt(x, y) != Biome.City)
        {
            return;
        }

        var town = towns.Of(x, y);
        await using var command = await db.CommandAsync("SELECT x, y FROM world.plot_objects WHERE kind = @kind", cancellationToken);
        command.Parameters.AddWithValue("kind", Items.BuyMarket);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (towns.Of(reader.GetInt32(0), reader.GetInt32(1)) == town)
            {
                throw new GameException("town_has_market", $"{town.Name} has a buying market already: a town has room for one.");
            }
        }
    }
}

/// <summary>Goods on their way from an export chest to a buying market, to be listed at <paramref name="PriceCents"/> when they arrive.</summary>
public sealed record ExportShipment(long Id, string Item, long Quantity, long PriceCents, int MarketX, int MarketY, DateTimeOffset ArrivesAt);

/// <param name="Problem">Why the last train took nothing: <c>empty</c>, <c>no_price</c>, <c>no_market</c>, <c>no_route</c> or <c>no_money</c>.</param>
public sealed record ExportChestState(
    Guid Id, int X, int Y, IReadOnlyDictionary<string, long> Items, IReadOnlyDictionary<string, long> Prices,
    DateTimeOffset NextAt, DateTimeOffset? LastAt, long LastQuantity, string? Problem,
    ExportRoute? Route, long FeeCents, IReadOnlyList<ExportShipment> Shipments, IReadOnlyDictionary<Guid, string> Names, DateTimeOffset Now,
    int Markets);
