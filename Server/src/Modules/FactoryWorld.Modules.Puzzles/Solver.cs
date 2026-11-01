using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Puzzles;

/// <summary>A machine a puzzle allows: what goes in, what comes out and how fast, as the real machine does it.</summary>
/// <param name="InPerMinute">Pieces of <paramref name="Input"/> it takes a minute when kept busy.</param>
/// <param name="OutPerIn">Pieces of <paramref name="Output"/> it makes of each piece taken.</param>
public sealed record PuzzleMachine(string Kind, string Input, string Output, double InPerMinute, double OutPerIn);

/// <summary>
/// An engineering puzzle: a small yard with a chest that never runs out of <see cref="Input"/> and a chest that takes what is
/// brought. The player lays out belts, inserters and machines so that at least <see cref="Target"/> pieces of
/// <see cref="Output"/> a minute reach it, with as few objects as they can. Ids are stored, so never rename one.
/// </summary>
/// <param name="Allowed">Objects the player may use and how many of each at most.</param>
/// <param name="Gold">The most objects for a gold medal; <paramref name="Silver"/> for silver; any solution is bronze.</param>
public sealed record Puzzle(string Id, string Name, string Story, int Width, int Height, (int X, int Y) Source, (int X, int Y) Sink,
    string Input, string Output, double Target, IReadOnlyDictionary<string, int> Allowed, PuzzleMachine? Machine, int Gold, int Silver);

/// <summary>An object the player put in the yard.</summary>
public sealed record Piece(string Kind, int X, int Y, int Rotation);

/// <summary>How a layout does: the pieces a minute that reach the chest, and what is wrong with it, if anything.</summary>
public sealed record Evaluation(double Rate, IReadOnlyList<string> Problems, IReadOnlyDictionary<int, double> Flow);

/// <summary>
/// Works out how much a layout delivers, as the plot's logistics would once it runs steadily: belts carry up to
/// <see cref="BeltPerMinute"/> a minute and pass items to the belt they face (not one facing back); an inserter picks up from
/// the tile behind it and drops on the tile in front at its own pace; a machine works as fast as its input comes, up to its
/// own pace. The most that can flow is a maximum flow through that network, first of the raw material from the supply chest
/// to the machines, then of the product from the machines to the chest that takes it.
/// </summary>
public static class Solver
{
    /// <summary>One lane of a plain belt: an item moves one of 4 places a tile each 200 ms tick.</summary>
    public const double BeltPerMinute = 300;

    /// <summary>Pieces a minute an inserter moves: a swing there and back takes twice its swing ticks of 200 ms.</summary>
    public static double InserterPerMinute(string kind) => kind switch
    {
        Items.Inserter => 37.5,
        Items.FastInserter => 75,
        Items.ExpressInserter => 150,
        _ => 0,
    };

    public static bool IsBelt(string kind) => kind == Items.Belt;

    public static bool IsInserter(string kind) => InserterPerMinute(kind) > 0;

    public static (int Dx, int Dy) Facing(int rotation) => (rotation & 3) switch
    {
        0 => (0, 1),
        1 => (1, 0),
        2 => (0, -1),
        _ => (-1, 0),
    };

    /// <summary>Checks the pieces fit the puzzle (allowed, inside, not overlapping) and works out what they deliver.</summary>
    public static Evaluation Evaluate(Puzzle puzzle, IReadOnlyList<Piece> pieces)
    {
        var problems = new List<string>();
        var at = new Dictionary<(int, int), int>();
        at[puzzle.Source] = -1;
        at[puzzle.Sink] = -2;
        foreach (var (kind, count) in pieces.GroupBy(p => p.Kind).Select(g => (g.Key, g.Count())))
        {
            if (!puzzle.Allowed.TryGetValue(kind, out var most))
            {
                problems.Add($"not_allowed:{kind}");
            }
            else if (count > most)
            {
                problems.Add($"too_many:{kind}");
            }
        }

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (piece.Rotation is < 0 or > 3)
            {
                problems.Add("bad_rotation");
            }

            foreach (var tile in Footprints.Tiles(piece.Kind, piece.X, piece.Y))
            {
                if (tile.X < 0 || tile.Y < 0 || tile.X >= puzzle.Width || tile.Y >= puzzle.Height)
                {
                    problems.Add("outside");
                }
                else if (!at.TryAdd(tile, i))
                {
                    problems.Add("overlap");
                }
            }
        }

        if (problems.Count > 0)
        {
            return new Evaluation(0, problems.Distinct().ToList(), new Dictionary<int, double>());
        }

        var network = new Network(puzzle, pieces, at);
        return network.Solve();
    }

    /// <summary>The medal for a solution of <paramref name="objects"/> pieces: 3 gold, 2 silver, 1 bronze, 0 none.</summary>
    public static int Medal(Puzzle puzzle, bool solved, int objects) =>
        !solved ? 0 : objects <= puzzle.Gold ? 3 : objects <= puzzle.Silver ? 2 : 1;

    /// <summary>The yard as a flow network. Every piece is a node split in two (in → out) whose inner edge is its pace.</summary>
    private sealed class Network
    {
        private readonly Puzzle _puzzle;
        private readonly IReadOnlyList<Piece> _pieces;
        private readonly Dictionary<(int, int), int> _at;

        // Node numbers: piece i has in = 2i, out = 2i + 1; then the supply chest, the taking chest, the source and the sink of the flow.
        private int SupplyNode => 2 * _pieces.Count;
        private int TakeNode => SupplyNode + 1;
        private int Start => SupplyNode + 2;
        private int End => SupplyNode + 3;
        private int Count => SupplyNode + 4;

        public Network(Puzzle puzzle, IReadOnlyList<Piece> pieces, Dictionary<(int, int), int> at)
        {
            _puzzle = puzzle;
            _pieces = pieces;
            _at = at;
        }

        private bool IsMachine(int i) => _puzzle.Machine is { } m && _pieces[i].Kind == m.Kind;

        /// <summary>Where items go from each piece's out to another piece's in: (from piece or chest, to piece or chest), -1 supply, -2 taker.</summary>
        private List<(int From, int To)> Links()
        {
            var links = new List<(int, int)>();
            for (var i = 0; i < _pieces.Count; i++)
            {
                var p = _pieces[i];
                var (dx, dy) = Facing(p.Rotation);
                if (IsBelt(p.Kind))
                {
                    if (_at.TryGetValue((p.X + dx, p.Y + dy), out var next) && next >= 0 && IsBelt(_pieces[next].Kind)
                        && Facing(_pieces[next].Rotation) != (-dx, -dy))
                    {
                        links.Add((i, next));
                    }
                }
                else if (IsInserter(p.Kind))
                {
                    if (_at.TryGetValue((p.X - dx, p.Y - dy), out var from) && (from < 0 || !IsInserter(_pieces[from].Kind)))
                    {
                        links.Add((from, i));
                    }

                    if (_at.TryGetValue((p.X + dx, p.Y + dy), out var to) && (to < 0 || !IsInserter(_pieces[to].Kind)))
                    {
                        links.Add((i, to));
                    }
                }
            }

            return links.Distinct().ToList();
        }

        public Evaluation Solve()
        {
            var links = Links();
            var problems = new List<string>();

            // The raw material: from the supply chest to the machines' input (or, without a machine, to the taking chest).
            var raw = new MaxFlow(Count);
            var product = new MaxFlow(Count);
            var noMachine = _puzzle.Machine is null;
            for (var i = 0; i < _pieces.Count; i++)
            {
                var pace = IsBelt(_pieces[i].Kind) ? BeltPerMinute : IsInserter(_pieces[i].Kind) ? InserterPerMinute(_pieces[i].Kind) : 0;
                if (pace > 0)
                {
                    raw.Add(2 * i, 2 * i + 1, pace);
                    product.Add(2 * i, 2 * i + 1, pace);
                }
            }

            foreach (var (from, to) in links)
            {
                var fromNode = from == -1 ? SupplyNode : from == -2 ? -1 : 2 * from + 1;
                var toNode = to == -2 ? TakeNode : to == -1 ? -1 : 2 * to;
                if (fromNode < 0 || toNode < 0)
                {
                    continue;
                }

                // A machine gives its product only, and takes its raw material only.
                if (from >= 0 && IsMachine(from))
                {
                    product.Add(fromNode, toNode, double.PositiveInfinity);
                }
                else if (to >= 0 && IsMachine(to))
                {
                    raw.Add(fromNode, toNode, double.PositiveInfinity);
                }
                else
                {
                    raw.Add(fromNode, toNode, double.PositiveInfinity);
                    product.Add(fromNode, toNode, double.PositiveInfinity);
                }
            }

            raw.Add(Start, SupplyNode, double.PositiveInfinity);
            if (noMachine)
            {
                raw.Add(TakeNode, End, double.PositiveInfinity);
                var moved = raw.Run(Start, End);
                return new Evaluation(Math.Round(moved, 2), problems, Flows(raw, null));
            }

            var machine = _puzzle.Machine!;
            for (var i = 0; i < _pieces.Count; i++)
            {
                if (IsMachine(i))
                {
                    raw.Add(2 * i, End, machine.InPerMinute);
                }
            }

            raw.Run(Start, End);
            var made = 0.0;
            for (var i = 0; i < _pieces.Count; i++)
            {
                if (IsMachine(i))
                {
                    var fed = raw.FlowOn(2 * i, End);
                    made += fed;
                    product.Add(Start, 2 * i + 1, fed * machine.OutPerIn);
                }
            }

            product.Add(TakeNode, End, double.PositiveInfinity);
            var delivered = product.Run(Start, End);

            // Raw material and product on the same belt or in the same hand jam a real line.
            for (var i = 0; i < _pieces.Count; i++)
            {
                if (!IsMachine(i) && raw.Through(2 * i, 2 * i + 1) > 1e-6 && product.Through(2 * i, 2 * i + 1) > 1e-6)
                {
                    problems.Add("mixed");
                    delivered = 0;
                    break;
                }
            }

            if (made <= 0)
            {
                problems.Add("machine_idle");
            }

            return new Evaluation(Math.Round(delivered, 2), problems, Flows(raw, product));
        }

        /// <summary>Pieces a minute through each piece, for the player to see where it chokes.</summary>
        private Dictionary<int, double> Flows(MaxFlow raw, MaxFlow? product)
        {
            var flows = new Dictionary<int, double>();
            for (var i = 0; i < _pieces.Count; i++)
            {
                var through = IsMachine(i)
                    ? raw.FlowOn(2 * i, End) * (_puzzle.Machine?.OutPerIn ?? 1)
                    : raw.Through(2 * i, 2 * i + 1) + (product?.Through(2 * i, 2 * i + 1) ?? 0);
                flows[i] = Math.Round(through, 2);
            }

            return flows;
        }
    }

    /// <summary>Edmonds–Karp on a small dense graph, with real capacities.</summary>
    private sealed class MaxFlow(int nodes)
    {
        private readonly double[,] _capacity = new double[nodes, nodes];
        private readonly double[,] _flow = new double[nodes, nodes];

        public void Add(int from, int to, double capacity)
        {
            if (from != to)
            {
                _capacity[from, to] = Math.Min(double.MaxValue, _capacity[from, to] + capacity);
            }
        }

        public double FlowOn(int from, int to) => Math.Max(0, _flow[from, to]);

        public double Through(int from, int to) => FlowOn(from, to);

        public double Run(int source, int sink)
        {
            var total = 0.0;
            var parent = new int[nodes];
            while (true)
            {
                Array.Fill(parent, -1);
                parent[source] = source;
                var queue = new Queue<int>();
                queue.Enqueue(source);
                while (queue.Count > 0 && parent[sink] < 0)
                {
                    var u = queue.Dequeue();
                    for (var v = 0; v < nodes; v++)
                    {
                        if (parent[v] < 0 && _capacity[u, v] - _flow[u, v] > 1e-9)
                        {
                            parent[v] = u;
                            queue.Enqueue(v);
                        }
                    }
                }

                if (parent[sink] < 0)
                {
                    return total;
                }

                var push = double.PositiveInfinity;
                for (var v = sink; v != source; v = parent[v])
                {
                    push = Math.Min(push, _capacity[parent[v], v] - _flow[parent[v], v]);
                }

                if (double.IsInfinity(push))
                {
                    // A path without a bound: nothing in the yard limits it, which a valid yard never has.
                    return double.PositiveInfinity;
                }

                for (var v = sink; v != source; v = parent[v])
                {
                    _flow[parent[v], v] += push;
                    _flow[v, parent[v]] -= push;
                }

                total += push;
            }
        }
    }
}
