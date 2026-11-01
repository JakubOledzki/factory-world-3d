using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Smog. Every machine that burns fuel smokes as it stands on a plot, each kind as much as <see cref="Emissions"/> says, and a
/// scrubber takes <see cref="ScrubberCleans"/> of a plot's smoke away. A plot breathes its own smoke and
/// <see cref="NeighbourShare"/> of what each of the eight plots around it gives off, so a neighbour's coal plant reaches it too.
/// Up to <see cref="Harmless"/> nothing happens; above it crops grow slower, down to <see cref="SlowestCrops"/> of their speed.
/// </summary>
public static class Smog
{
    /// <summary>What each kind of machine gives off.</summary>
    public static readonly IReadOnlyDictionary<string, int> Emissions = new Dictionary<string, int>
    {
        [Items.Furnace] = 2,
        [Items.BrickFurnace] = 4,
        [Items.BlastFurnace] = 8,
        [Items.Foundry] = 6,
        [Items.Generator] = 3,
        [Items.CoalPlant] = 20,
        [Items.GasPlant] = 8,
        [Items.DieselGenerator] = 5,
        [Items.Refinery] = 10,
        [Items.Cracker] = 8,
        [Items.ChemicalPlant] = 8,
        [Items.Distillery] = 4,
        [Items.PotatoDistillery] = 2,
        [Items.CharcoalKiln] = 4,
        [Items.TarKiln] = 3,
        [Items.Smokehouse] = 2,
        [Items.Glassworks] = 4,
        [Items.CementKiln] = 6,
        [Items.OxygenConverter] = 10,
        [Items.SteelCombine] = 15,
    };

    /// <summary>Smoke a scrubber takes from its own plot's.</summary>
    public const int ScrubberCleans = 20;

    /// <summary>What a plot breathes of the smoke of each plot around it.</summary>
    public const double NeighbourShare = 0.5;

    /// <summary>Smog a plot takes without harm.</summary>
    public const int Harmless = 20;

    /// <summary>Smog above <see cref="Harmless"/> that halves how fast crops grow.</summary>
    public const int HalfSpeed = 100;

    public const double SlowestCrops = 0.5;

    /// <summary>What a plot gives off: its machines' smoke less what its scrubbers clean, never below 0.</summary>
    public static int Given(IEnumerable<(string Kind, int Count)> objects)
    {
        var smoke = 0;
        var scrubbers = 0;
        foreach (var (kind, count) in objects)
        {
            smoke += Emissions.GetValueOrDefault(kind) * count;
            scrubbers += kind == Items.Scrubber ? count : 0;
        }

        return Math.Max(0, smoke - scrubbers * ScrubberCleans);
    }

    /// <summary>How fast crops grow in smog of <paramref name="level"/>: 1 up to <see cref="Harmless"/>, then slower.</summary>
    public static double CropSpeed(double level) =>
        Math.Clamp(1 - Math.Max(0, level - Harmless) / HalfSpeed * (1 - SlowestCrops), SlowestCrops, 1);
}

/// <param name="Own">What the plot gives off itself.</param>
/// <param name="Neighbours">What it breathes from the plots around it.</param>
public sealed record SmogLevel(int Own, double Neighbours)
{
    public double Level => Own + Neighbours;

    public double CropSpeed => Smog.CropSpeed(Level);
}

public sealed class SmogService(GameDb db)
{
    /// <summary>The smog over a plot, from its own machines and those of the eight plots around it.</summary>
    public async Task<SmogLevel> LevelAsync(int x, int y, CancellationToken cancellationToken)
    {
        var kinds = Smog.Emissions.Keys.Append(Items.Scrubber).ToArray();
        var plots = new Dictionary<(int, int), List<(string, int)>>();
        await using (var command = await db.CommandAsync(
            """
            SELECT x, y, kind, count(*) FROM world.plot_objects
            WHERE x BETWEEN @x - 1 AND @x + 1 AND y BETWEEN @y - 1 AND @y + 1 AND kind = ANY(@kinds)
            GROUP BY x, y, kind
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("kinds", kinds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var at = (reader.GetInt32(0), reader.GetInt32(1));
                if (!plots.TryGetValue(at, out var list))
                {
                    plots[at] = list = [];
                }

                list.Add((reader.GetString(2), (int)reader.GetInt64(3)));
            }
        }

        var own = plots.TryGetValue((x, y), out var mine) ? Smog.Given(mine) : 0;
        var neighbours = plots.Where(p => p.Key != (x, y)).Sum(p => Smog.Given(p.Value) * Smog.NeighbourShare);
        return new SmogLevel(own, neighbours);
    }

    public static object Json(SmogLevel smog) => new
    {
        own = smog.Own,
        neighbours = smog.Neighbours,
        level = smog.Level,
        // How fast crops grow here, 0.5 to 1.
        cropSpeed = smog.CropSpeed,
        harmless = Smog.Harmless,
        scrubberCleans = Smog.ScrubberCleans,
    };
}
