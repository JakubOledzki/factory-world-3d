namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>Where the heat of the earth comes close enough to the surface for a geothermal plant.</summary>
public static class Geothermal
{
    /// <summary>What a geothermal plant gives (kW) in the mountains, before the biome's factor.</summary>
    public const double Kw = 250;

    /// <summary>Biomes with hot rock under them (by the world's biome numbers), and how many times its power a plant gives there:
    /// the mountains as it is, the tundra less, the glacier more, as on a volcanic island.</summary>
    public static readonly IReadOnlyDictionary<int, double> Biomes = new Dictionary<int, double>
    {
        [6] = 1,
        [7] = 0.8,
        [8] = 1.2,
    };

    public static bool Allowed(int biome) => Biomes.ContainsKey(biome);

    /// <summary>What a geothermal plant gives (kW) on a plot of the biome.</summary>
    public static double KwIn(int biome) => Kw * Biomes.GetValueOrDefault(biome);
}
