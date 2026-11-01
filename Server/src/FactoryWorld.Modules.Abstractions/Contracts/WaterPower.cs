namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>Where water power works: a water wheel or a hydro plant goes up only on plots by moving water.</summary>
public static class WaterPower
{
    /// <summary>Biomes with water to turn a wheel (by the world's biome numbers), and how many times its power
    /// a plant gives there: mountain streams +15%, a lake or the sea as it is.</summary>
    public static readonly IReadOnlyDictionary<int, double> Biomes = new Dictionary<int, double>
    {
        [6] = 1.15,
        [10] = 1,
        [11] = 1,
    };

    public static bool Allowed(int biome) => Biomes.ContainsKey(biome);
}
