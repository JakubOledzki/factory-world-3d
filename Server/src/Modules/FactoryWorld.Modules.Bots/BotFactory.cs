using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// A production line a bot puts up on its plot, the way players build their first automated factory: a chest of
/// inputs, an inserter laying them on a short belt, an inserter feeding the machine from the belt, an inserter taking
/// the products out into a second chest, and a steam generator with two poles powering the inserters (and the
/// machine, if it runs on power). The bot only fills the input chest, keeps the generator burning and empties
/// the output chest now and then; the inserters do the rest while it is away.
/// </summary>
/// <param name="Machine">What makes the products (a stone furnace, a mill, a sawmill).</param>
/// <param name="Recipe">What the machine is set to make; null for a furnace, which smelts whatever ore it gets.</param>
/// <param name="Inputs">What goes into the input chest, in the order the bot looks for it.</param>
/// <param name="Fuels">What the generator burns, the bot's own first.</param>
/// <param name="Products">What comes out, for the market.</param>
public sealed record BotLine(string Machine, string? Recipe, string[] Inputs, string[] Fuels, string[] Products)
{
    /// <summary>Everything the line is built of, as the building plan takes it from the inventory.</summary>
    public IReadOnlyList<(string Item, long Quantity)> Kit =>
        BotFactory.Layout(Machine).GroupBy(p => p.Kind).Select(g => (g.Key, (long)g.Count())).ToList();
}

/// <summary>Where the parts of a line stand, and where on a plot a line fits.</summary>
public static class BotFactory
{
    /// <summary>Rotation 1: everything faces east, so items go from left to right.</summary>
    public const int East = 1;

    /// <summary>Tiles the line covers from its input chest: 9 to the right and 3 down (the poles and generator in the third row).</summary>
    public const int Width = 9;

    public const int Depth = 3;

    /// <summary>Where the machine stands, from the input chest; a 2 × 2 machine covers this tile and those right and below it.</summary>
    public const int MachineX = 5;

    public const int OutputX = 8;

    /// <summary>The generator, from the input chest.</summary>
    public const int GeneratorX = 3;

    public const int GeneratorY = 2;

    /// <summary>
    /// The line's parts with their tiles from the input chest (dx, dy), left to right:
    /// chest → inserter → belt → belt → inserter → machine (2 × 2) → inserter → chest, and under it a pole by each
    /// pair of inserters with the generator between them. A pole powers 5 × 5 tiles around it and the poles are
    /// 5 tiles apart, so they are wired together.
    /// </summary>
    public static IReadOnlyList<(string Kind, int Dx, int Dy)> Layout(string machine) =>
    [
        (Items.WoodenChest, 0, 0),
        (Items.Inserter, 1, 0),
        (Items.Belt, 2, 0),
        (Items.Belt, 3, 0),
        (Items.Inserter, 4, 0),
        (machine, MachineX, 0),
        (Items.Inserter, 7, 0),
        (Items.WoodenChest, OutputX, 0),
        (Items.Pole, 2, 2),
        (Items.Generator, GeneratorX, GeneratorY),
        (Items.Pole, 7, 2),
    ];

    /// <summary>
    /// The input-chest tile nearest to the spawn where a whole line fits with a free tile around it, or null.
    /// <paramref name="blocked"/> is true for every tile something stands on, grows on or may not be built on.
    /// Works out in one pass over the plot how many blocked tiles each rectangle holds, so it is cheap on a big plot.
    /// </summary>
    public static (int X, int Y)? FindSpot(bool[,] blocked, (int X, int Y) spawn) =>
        FindSpot(blocked, new bool[blocked.GetLength(0), blocked.GetLength(1)], spawn);

    /// <summary>
    /// Like <see cref="FindSpot(bool[,], ValueTuple{int, int})"/>, but trees, rocks and dunes in <paramref name="clearable"/> may
    /// be in the way: the spot with the fewest of them wins (then the nearest), and the bot clears them before it builds.
    /// </summary>
    public static (int X, int Y)? FindSpot(bool[,] blocked, bool[,] clearable, (int X, int Y) spawn)
    {
        var width = blocked.GetLength(0);
        var depth = blocked.GetLength(1);
        var hard = Sums(blocked);
        var soft = Sums(clearable);
        static int In(int[,] sums, int x0, int y0, int x1, int y1) => sums[x1, y1] - sums[x0, y1] - sums[x1, y0] + sums[x0, y0];

        (int X, int Y)? best = null;
        var bestCost = (Clear: int.MaxValue, Distance: int.MaxValue);
        // With a free tile all round: from (x - 1, y - 1) to (x + Width, y + Depth).
        for (var y = 1; y + Depth < depth; y++)
        {
            for (var x = 1; x + Width < width; x++)
            {
                var (x0, y0, x1, y1) = (x - 1, y - 1, x + Width + 1, y + Depth + 1);
                if (In(hard, x0, y0, x1, y1) != 0)
                {
                    continue;
                }

                // Players build where they appear, a few steps in front; so does the bot, off the path straight down.
                var cost = (Clear: In(soft, x0, y0, x1, y1), Distance: Math.Abs(x + Width / 2 - spawn.X) + Math.Abs(y - spawn.Y - 3));
                if (y > spawn.Y + 1 && cost.CompareTo(bestCost) < 0 && (spawn.X < x - 1 || spawn.X > x + Width))
                {
                    best = (x, y);
                    bestCost = cost;
                }
            }
        }

        return best;
    }

    /// <summary>The tiles a line standing at (x, y) needs clear, the free tile round it included.</summary>
    public static IEnumerable<(int X, int Y)> Ground(int x, int y)
    {
        for (var dy = -1; dy <= Depth; dy++)
        {
            for (var dx = -1; dx <= Width; dx++)
            {
                yield return (x + dx, y + dy);
            }
        }
    }

    /// <summary>sums[x, y]: how many tiles left of x and above y are set.</summary>
    private static int[,] Sums(bool[,] tiles)
    {
        var (width, depth) = (tiles.GetLength(0), tiles.GetLength(1));
        var sums = new int[width + 1, depth + 1];
        for (var y = 0; y < depth; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sums[x + 1, y + 1] = sums[x, y + 1] + sums[x + 1, y] - sums[x, y] + (tiles[x, y] ? 1 : 0);
            }
        }

        return sums;
    }
}
