using System.IO.Compression;
using System.Text;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>What covers a plot. Stored in the map file and the database, so never renumber.</summary>
public enum Biome : byte
{
    Ocean = 0,
    Lake = 1,
    Forest = 2,
    Farmland = 3,
    Grassland = 4,
    Desert = 5,
    Mountains = 6,
    Tundra = 7,
    Ice = 8,
    City = 9,
    Coast = 10,
    Lakeshore = 11,

    /// <summary>Not in the map file: a few desert plots are oases (<see cref="Abstractions.Contracts.Oasis"/>).</summary>
    Oasis = 12,
}

/// <summary>A bonus or a penalty a biome gives to a branch of production.</summary>
public sealed record BiomeBonus(string Text, bool Good);

/// <param name="PriceFactor">Multiplies the plot price; 1 for most land.</param>
public sealed record BiomeInfo(Biome Id, string Name, bool Buyable, double PriceFactor, IReadOnlyList<BiomeBonus> Bonuses);

public static class Biomes
{
    private static BiomeBonus Up(string text) => new(text, true);

    private static BiomeBonus Down(string text) => new(text, false);

    public static readonly IReadOnlyDictionary<Biome, BiomeInfo> All = new BiomeInfo[]
    {
        new(Biome.Ocean, "Ocean", false, 0, []),
        new(Biome.Lake, "Jezioro", false, 0, []),
        new(Biome.Forest, "Las", true, 1, [Up("+25% drewno i przetwórstwo drewna"), Up("+20% łowiectwo"), Down("−20% rolnictwo")]),
        new(Biome.Farmland, "Żyzne gleby", true, 1.5, [Up("+30% rolnictwo"), Up("+20% hodowla")]),
        new(Biome.Grassland, "Step i łąki", true, 1, [Up("+25% hodowla"), Up("+15% energia wiatrowa")]),
        new(Biome.Desert, "Pustynia", true, 1, [Up("+30% piasek i szkło"), Up("+25% energia słoneczna"), Down("−50% rolnictwo"), Down("−50% woda ze studni"), Down("burze piaskowe")]),
        new(Biome.Mountains, "Góry", true, 1, [Up("+30% górnictwo"), Up("+15% energia wodna"), Down("−25% powierzchnia zabudowy"), Down("brak leśnictwa")]),
        new(Biome.Tundra, "Tundra", true, 1, [Up("+15% łowiectwo"), Up("+15% energia wiatrowa"), Down("−30% rolnictwo")]),
        new(Biome.Ice, "Lodowiec", true, 1, [Up("+20% energia wiatrowa"), Down("brak rolnictwa i leśnictwa")]),
        new(Biome.City, "Miasto", true, 2, [Up("+25% przemysł i montaż"), Up("−10% koszt transportu na rynek"), Down("−50% rolnictwo i leśnictwo")]),
        new(Biome.Coast, "Wybrzeże", true, 1.5, [Up("+30% rybołówstwo"), Up("+15% przetwórstwo ryb")]),
        new(Biome.Lakeshore, "Pojezierze", true, 1.5, [Up("+30% rybołówstwo"), Up("+15% przetwórstwo ryb")]),
        new(Biome.Oasis, "Oaza", true, 1.5, [Up("staw, palmy daktylowe i żyzna ziemia"), Up("+100% woda ze studni"), Up("+30% piasek i szkło"), Up("+25% energia słoneczna")]),
    }.ToDictionary(b => b.Id);
}

/// <summary>What plots cost: the n-th plot of a player costs the n-th tier, the 5th and later the last one,
/// times the biome's price factor. There is no limit on how many plots a player has.</summary>
public static class PlotPrices
{
    public static readonly long[] TierCents = [1_000_00, 10_000_00, 100_000_00, 1_000_000_00, 1_000_000_000_00];

    /// <summary>The base price (before the biome factor) of a player's next plot, who has <paramref name="owned"/>.</summary>
    public static long Base(long owned) => TierCents[(int)Math.Min(owned, TierCents.Length - 1)];

    public static long For(long owned, Biome biome) => (long)Math.Round(Base(owned) * Biomes.All[biome].PriceFactor);
}

public static class PlotRules
{
    public const int SideKm = 5;

    public const int XpPerPlot = 500;
}

/// <summary>
/// The Earth divided into 5 × 5 km plots: a grid in the Behrmann equal-area projection, so every plot
/// covers the same area. Column 0 starts at 180° W, row 0 is at the North Pole. The biome of each
/// plot comes from the map file made by tools/build_world.py; only bought plots are in the database.
/// </summary>
public sealed class WorldMap
{
    private readonly byte[] _biomes;

    public WorldMap(int width, int height, byte[] biomes)
    {
        if (biomes.Length != width * height)
        {
            throw new ArgumentException("The biome grid does not match the map size.", nameof(biomes));
        }

        Width = width;
        Height = height;
        _biomes = biomes;
    }

    public int Width { get; }

    public int Height { get; }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public Biome BiomeAt(int x, int y)
    {
        var biome = (Biome)_biomes[y * Width + x];
        return biome == Biome.Desert && Oasis.Is(x, y) ? Biome.Oasis : biome;
    }

    /// <summary>Whether the plot is open water, the sea or a lake, where no train goes.</summary>
    public bool IsWater(int x, int y) => (Biome)_biomes[y * Width + x] is Biome.Ocean or Biome.Lake;

    /// <summary>The plot at a point of the globe, in degrees (same projection as tools/build_world.py).</summary>
    public (int X, int Y) PlotAt(double latitude, double longitude)
    {
        const double radius = 6371.0072;
        var cos30 = Math.Cos(Math.PI / 6);
        var x = (longitude * Math.PI / 180 * radius * cos30 + Width * PlotRules.SideKm / 2.0) / PlotRules.SideKm;
        var y = (Height * PlotRules.SideKm / 2.0 - radius * Math.Sin(latitude * Math.PI / 180) / cos30) / PlotRules.SideKm;
        return (Math.Clamp((int)Math.Floor(x), 0, Width - 1), Math.Clamp((int)Math.Floor(y), 0, Height - 1));
    }

    /// <summary>Reads the map file: gzip of "FWW1", width and height as little-endian u16, then one byte per plot.</summary>
    public static WorldMap Read(Stream gzipped)
    {
        using var stream = new GZipStream(gzipped, CompressionMode.Decompress);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "FWW1")
        {
            throw new InvalidDataException("Not a Factory World map file.");
        }

        int width = reader.ReadUInt16(), height = reader.ReadUInt16();
        var biomes = reader.ReadBytes(width * height);
        return new WorldMap(width, height, biomes);
    }

    /// <summary>The map built into the server.</summary>
    public static WorldMap Load()
    {
        using var resource = typeof(WorldMap).Assembly.GetManifestResourceStream("biomes.bin.gz")
            ?? throw new InvalidOperationException("The world map resource is missing.");
        return Read(resource);
    }
}
