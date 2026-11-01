using System.IO.Compression;
using System.Text;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.World;

/// <summary>
/// The countries of the map, made by tools/build_countries.py from Natural Earth's borders: every plot of land lies in one
/// (the sea, lakes and Antarctica in none). Players govern them in the Politics module.
/// </summary>
public sealed class Countries
{
    private readonly byte[] _grid;
    private readonly int _width;
    private readonly Dictionary<string, CountryInfo> _byCode;

    public Countries(int width, IReadOnlyList<CountryInfo> all, byte[] grid)
    {
        _width = width;
        _grid = grid;
        All = all;
        _byCode = all.ToDictionary(c => c.Code);
    }

    /// <summary>By name.</summary>
    public IReadOnlyList<CountryInfo> All { get; }

    public CountryInfo? At(int x, int y)
    {
        var i = (long)y * _width + x;
        return x < 0 || x >= _width || i < 0 || i >= _grid.Length || _grid[i] == 0 ? null : All[_grid[i] - 1];
    }

    public CountryInfo? Find(string? code) => code is not null && _byCode.TryGetValue(code, out var country) ? country : null;

    /// <summary>Reads the file: gzip of "FWC1", width, height and the number of countries as little-endian u16, a line
    /// "code, name, label column, label row, plots" for each, then one byte per plot (0 for none, i for the i-th country).</summary>
    public static Countries Read(Stream gzipped)
    {
        using var stream = new GZipStream(gzipped, CompressionMode.Decompress);
        using var reader = new BinaryReader(stream);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "FWC1")
        {
            throw new InvalidDataException("Not a Factory World countries file.");
        }

        int width = reader.ReadUInt16(), height = reader.ReadUInt16(), count = reader.ReadUInt16();
        var all = new List<CountryInfo>(count);
        var line = new List<byte>();
        while (all.Count < count)
        {
            var b = reader.ReadByte();
            if (b != '\n')
            {
                line.Add(b);
                continue;
            }

            var parts = Encoding.UTF8.GetString(line.ToArray()).Split('\t');
            all.Add(new CountryInfo(parts[0], parts[1], int.Parse(parts[4])));
            line.Clear();
        }

        var grid = reader.ReadBytes(width * height);
        if (grid.Length != width * height)
        {
            throw new InvalidDataException("The countries file is cut short.");
        }

        return new Countries(width, all, grid);
    }

    public static Countries Load()
    {
        using var resource = typeof(Countries).Assembly.GetManifestResourceStream("countries.bin.gz")
            ?? throw new InvalidOperationException("The countries resource is missing.");
        return Read(resource);
    }
}

/// <summary>The countries as other modules see them, and the plots players own in each.</summary>
public sealed class CountryMap(Countries countries, GameDb db) : ICountryMap
{
    public IReadOnlyList<CountryInfo> All => countries.All;

    public CountryInfo? At(int x, int y) => countries.At(x, y);

    public CountryInfo? Find(string? code) => countries.Find(code);

    public async Task<IReadOnlyDictionary<string, int>> PlotsAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT x, y FROM world.plots WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plots = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (countries.At(reader.GetInt32(0), reader.GetInt32(1)) is { } country)
            {
                plots[country.Code] = plots.GetValueOrDefault(country.Code) + 1;
            }
        }

        return plots;
    }

    public async Task<IReadOnlyDictionary<string, int>> BoughtPlotsAsync(CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT x, y FROM world.plots", cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plots = new Dictionary<string, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (countries.At(reader.GetInt32(0), reader.GetInt32(1)) is { } country)
            {
                plots[country.Code] = plots.GetValueOrDefault(country.Code) + 1;
            }
        }

        return plots;
    }
}
