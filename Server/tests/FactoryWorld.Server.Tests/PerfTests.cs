using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace FactoryWorld.Server.Tests;

/// <summary>A test that runs only with FW_PERF=1: it measures, it does not check the rules.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FW_PERF") != "1")
        {
            Skip = "Performance measurement; run with FW_PERF=1.";
        }
    }
}

/// <summary>
/// How much the server does for the requests a player in the game sends most often: the time, the
/// database commands and the memory allocated per request. Run before and after a change:
/// <c>FW_PERF=1 dotnet test -c Release --filter PerfTests --logger "console;verbosity=detailed"</c>;
/// with FW_PERF_OUT set the table is also written to that file, with FW_PERF_SQL=1 the commands of each request too.
/// KB allocated counts the test client as well (about 70 KB a request, see "ping").
/// </summary>
public sealed class PerfTests(ServerFactory factory, ITestOutputHelper output) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private const int East = 1;

    /// <summary>Furnace lines on the factory plot: chest → inserter → furnace → inserter → chest, each with a pole and a generator.</summary>
    private const int Lines = 8;

    private const int Rounds = 40;

    private sealed class Commands : IDisposable
    {
        private readonly ActivityListener _listener;
        private long _count;

        public Commands()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = _ => Interlocked.Increment(ref _count),
                ActivityStopped = a =>
                {
                    if (Recording)
                    {
                        lock (Recorded)
                        {
                            Recorded.Add(a.GetTagItem("db.query.text")?.ToString() ?? a.DisplayName);
                        }
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public long Count => Interlocked.Read(ref _count);

        /// <summary>While set, the SQL of each command (a batch counts once) is kept in <see cref="Recorded"/>.</summary>
        public bool Recording { get; set; }

        public List<string> Recorded { get; } = [];

        public void Dispose() => _listener.Dispose();
    }

    private readonly record struct Result(string Name, double Ms, double Queries, double KbAllocated, double KbReply);

    /// <summary>With FW_PERF_SQL=1, the commands of each request are listed after the table.</summary>
    private static readonly bool ListCommands = Environment.GetEnvironmentVariable("FW_PERF_SQL") == "1";

    private readonly StringBuilder _commandLists = new();

    private async Task<Result> MeasureAsync(string name, Commands commands, Func<int, Task<string>> request, Action? between = null)
    {
        // Warm up: the first calls compile and prepare.
        for (var i = 0; i < 5; i++)
        {
            between?.Invoke();
            await request(i);
        }

        long queries = 0, allocated = 0, bytes = 0;
        var clock = new Stopwatch();
        for (var i = 0; i < Rounds; i++)
        {
            between?.Invoke();
            commands.Recording = ListCommands && i == Rounds - 1;
            var q = commands.Count;
            var a = GC.GetTotalAllocatedBytes(precise: false);
            clock.Start();
            var reply = await request(i);
            clock.Stop();
            allocated += GC.GetTotalAllocatedBytes(precise: false) - a;
            queries += commands.Count - q;
            bytes += Encoding.UTF8.GetByteCount(reply);
            commands.Recording = false;
        }

        if (commands.Recorded.Count > 0)
        {
            _commandLists.AppendLine($"{name}:");
            foreach (var g in commands.Recorded.GroupBy(t => t.ReplaceLineEndings(" ")).OrderByDescending(g => g.Count()))
            {
                _commandLists.AppendLine($"  {g.Count()}× {(g.Key.Length > 140 ? g.Key[..140] + "…" : g.Key)}");
            }

            commands.Recorded.Clear();
        }

        return new Result(name, clock.Elapsed.TotalMilliseconds / Rounds, (double)queries / Rounds, allocated / 1024.0 / Rounds, bytes / 1024.0 / Rounds);
    }

    [PerfFact]
    public async Task Requests_a_player_sends_most_often()
    {
        using var commands = new Commands();
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var (x, y) = await FactoryPlotAsync(client);

        async Task<string> Send(string type, object? payload = null) => (await client.SendAsync(type, payload)).GetRawText();

        // Others on the plot, as when a company works it together; presence only keeps them in memory.
        var presence = factory.Services.GetRequiredService<PlotPresence>();
        for (var i = 0; i < 2000; i++)
        {
            presence.Update(Guid.NewGuid(), i % 50 == 0 ? x : x + 1 + i % 7, y, 10, 10, "down", false);
        }

        // FW_PERF_PROFILE=logistics.get (or another request type): that request over and over for a minute first,
        // for a profiler to attach to (dotnet-trace ps).
        if (Environment.GetEnvironmentVariable("FW_PERF_PROFILE") is { Length: > 0 } profiled)
        {
            var until = Stopwatch.StartNew();
            while (until.Elapsed < TimeSpan.FromSeconds(60))
            {
                factory.Time.Advance(TimeSpan.FromSeconds(1));
                await Send(profiled, new { x, y });
            }
        }

        var results = new List<Result>
        {
            await MeasureAsync("plot.presence", commands, _ => Send("plot.presence", new { x, y, posX = 12.5, posY = 30.25, facing = "left", walking = true })),
            await MeasureAsync("logistics.get (1 s later)", commands, _ => Send("logistics.get", new { x, y }), () => factory.Time.Advance(TimeSpan.FromSeconds(1))),
            await MeasureAsync("plot.enter", commands, _ => Send("plot.enter", new { x, y })),
            await MeasureAsync("inventory.get", commands, _ => Send("inventory.get")),
            await MeasureAsync("garage.get", commands, _ => Send("garage.get")),
            await MeasureAsync("wallet.get", commands, _ => Send("wallet.get")),
            await MeasureAsync("market.prices", commands, _ => Send("market.prices")),
            await MeasureAsync("world.area", commands, i => Send("world.area", new { x = 1000 + i * 37 % 4000, y = 800 + i * 53 % 1500, width = 100, height = 60 })),
            await MeasureAsync("world.plot", commands, i => Send("world.plot", new { x = 1000 + i * 37 % 4000, y = 800 + i * 53 % 1500 })),
            await MeasureAsync("crafting.get", commands, _ => Send("crafting.get")),
            await MeasureAsync("research.get", commands, _ => Send("research.get")),
            await MeasureAsync("ping", commands, _ => Send("ping")),
        };

        var table = new StringBuilder();
        table.AppendLine($"| Request | ms | DB commands | KB allocated | KB reply |");
        table.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var r in results)
        {
            table.AppendLine($"| {r.Name} | {r.Ms:0.00} | {r.Queries:0.0} | {r.KbAllocated:0} | {r.KbReply:0.0} |");
        }

        table.Append(_commandLists);
        output.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("FW_PERF_OUT") is { Length: > 0 } file)
        {
            await File.WriteAllTextAsync(file, table.ToString());
        }
    }

    /// <summary>Lines on the big factory plot, each <see cref="BigLineObjects"/> objects: about a thousand in all, as a player far into the game has.</summary>
    private const int BigLines = 50;

    private const int BigLineBelts = 10;

    private const int BigLineObjects = 10 + BigLineBelts;

    /// <summary>
    /// A plot with about a thousand objects (furnace lines feeding belts into chests, with poles and generators) and the requests
    /// sent while players are on it: the owner and visitors of the public plot asking for its logistics every second all at once.
    /// <c>FW_PERF=1 dotnet test -c Release --filter Big_factory_plot --logger "console;verbosity=detailed"</c>.
    /// </summary>
    [PerfFact]
    public async Task Big_factory_plot()
    {
        using var commands = new Commands();
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var build = Stopwatch.StartNew();
        var (x, y, places, chest, furnace, generator) = await BigFactoryPlotAsync(client);
        build.Stop();

        async Task<string> Send(GameClient c, string type, object? payload = null) => (await c.SendAsync(type, payload)).GetRawText();

        var results = new List<Result>
        {
            await MeasureAsync("logistics.get (1 s later)", commands, _ => Send(client, "logistics.get", new { x, y }), () => factory.Time.Advance(TimeSpan.FromSeconds(1))),
            await MeasureAsync("logistics.get, short form as the game asks (1 s later)", commands,
                _ => Send(client, "logistics.get", new { x, y, compact = true }), () => factory.Time.Advance(TimeSpan.FromSeconds(1))),
            await MeasureAsync("logistics.get (same moment)", commands, _ => Send(client, "logistics.get", new { x, y })),
            await MeasureAsync("logistics.get (1 min later)", commands, _ => Send(client, "logistics.get", new { x, y }), () => factory.Time.Advance(TimeSpan.FromMinutes(1))),
            await MeasureAsync("plot.enter", commands, _ => Send(client, "plot.enter", new { x, y })),
            await MeasureAsync("plot.presence", commands, _ => Send(client, "plot.presence", new { x, y, posX = 12.5, posY = 30.25, facing = "left", walking = true })),
            await MeasureAsync("forestry.get", commands, _ => Send(client, "forestry.get", new { x, y })),
            await MeasureAsync("station.get", commands, _ => Send(client, "station.get", new { x, y })),
            // Windows of the objects on the plot.
            await MeasureAsync("storage.get", commands, _ => Send(client, "storage.get", new { x, y, id = chest }), () => factory.Time.Advance(TimeSpan.FromSeconds(1))),
            await MeasureAsync("furnace.get", commands, _ => Send(client, "furnace.get", new { furnace }), () => factory.Time.Advance(TimeSpan.FromSeconds(1))),
            await MeasureAsync("generator.get", commands, _ => Send(client, "generator.get", new { x, y, id = generator }), () => factory.Time.Advance(TimeSpan.FromSeconds(1))),
        };

        // Visitors of the public plot, everyone asking for its logistics at the same moment, as their clients do every second.
        Assert.Equal("world.plot", (await client.SendAsync("plot.setPublic", new { x, y, @public = true })).Type());
        var visitors = new List<GameClient>();
        for (var i = 0; i < 9; i++)
        {
            var visitor = await factory.ConnectAsync();
            await visitor.RegisterWithCharacterAsync();
            visitors.Add(visitor);
        }

        var everyone = visitors.Prepend(client).ToList();
        var together = Stopwatch.StartNew();
        const int Seconds = 10;
        for (var second = 0; second < Seconds; second++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAll(everyone.Select(c => Send(c, "logistics.get", new { x, y, compact = true })));
        }

        together.Stop();

        var table = new StringBuilder();
        table.AppendLine($"Objects: {BigLines * BigLineObjects}; built in {build.Elapsed.TotalSeconds:0.0} s; " +
                         $"plot.place of the last 100: {places.TakeLast(100).Average():0.0} ms");
        table.AppendLine($"{everyone.Count} players asking for the logistics at once: {together.Elapsed.TotalMilliseconds / Seconds:0} ms a second of the game");
        table.AppendLine();
        table.AppendLine($"| Request | ms | DB commands | KB allocated | KB reply |");
        table.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var r in results)
        {
            table.AppendLine($"| {r.Name} | {r.Ms:0.00} | {r.Queries:0.0} | {r.KbAllocated:0} | {r.KbReply:0.0} |");
        }

        table.Append(_commandLists);
        output.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("FW_PERF_OUT") is { Length: > 0 } file)
        {
            await File.WriteAllTextAsync(file, table.ToString());
        }

        foreach (var visitor in visitors)
        {
            visitor.Dispose();
        }
    }

    /// <summary>A grassland plot of the player's with <see cref="BigLines"/> lines: chest → inserter → furnace → inserter → belts → inserter → chest,
    /// powered by a generator and three poles. Returns the milliseconds each "plot.place" took, in order.</summary>
    private async Task<(int X, int Y, List<double> Places, string Chest, string Furnace, string Generator)> BigFactoryPlotAsync(GameClient client)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 100_000_000, "test", CancellationToken.None);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            foreach (var (item, quantity) in new[]
                     {
                         (Items.WoodenChest, 2 * BigLines), (Items.Inserter, 3 * BigLines), (Items.Furnace, BigLines), (Items.Pole, 3 * BigLines),
                         (Items.Generator, BigLines), (Items.Belt, BigLineBelts * BigLines), (Items.Coal, 60 * BigLines), (Items.IronOre, 200 * BigLines),
                     })
            {
                await inventory.AddAsync(client.AccountId, item, quantity, CancellationToken.None);
            }
        }

        var (x, y) = FreePlot(Biome.Grassland, 7);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        string chest = "", furnace = "", generator = "";
        const int Width = 7 + BigLineBelts;
        bool Free(int tx, int ty) => Enumerable.Range(tx, Width).All(i => bytes[ty * PlotLayout.Size + i] == (byte)TileObject.None);

        var places = new List<double>();
        async Task<string> Place(string kind, int tileX, int tileY)
        {
            var clock = Stopwatch.StartNew();
            var id = await PlaceAsync(client, x, y, kind, tileX, tileY);
            places.Add(clock.Elapsed.TotalMilliseconds);
            return id;
        }

        var placed = 0;
        for (var ty = 10; ty + 3 < PlotLayout.Size && placed < BigLines; ty++)
        {
            var before = placed;
            for (var tx = 2; tx + Width + 1 <= PlotLayout.Size && placed < BigLines; tx += Width + 1)
            {
                if (!Enumerable.Range(ty, 3).All(row => Free(tx, row)))
                {
                    continue;
                }

                var source = chest = await Place(Items.WoodenChest, tx, ty);
                await Place(Items.Inserter, tx + 1, ty);
                furnace = await Place(Items.Furnace, tx + 2, ty);
                await Place(Items.Inserter, tx + 4, ty);
                for (var b = 0; b < BigLineBelts; b++)
                {
                    await Place(Items.Belt, tx + 5 + b, ty);
                }

                await Place(Items.Inserter, tx + 5 + BigLineBelts, ty);
                await Place(Items.WoodenChest, tx + 6 + BigLineBelts, ty);
                await client.SendAsync("storage.put", new { x, y, id = source, item = Items.IronOre, quantity = 200 });
                await client.SendAsync("storage.put", new { x, y, id = source, item = Items.Coal, quantity = 40 });
                await Place(Items.Pole, tx + 2, ty + 2);
                generator = await Place(Items.Generator, tx + 3, ty + 2);
                await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 });
                await Place(Items.Pole, tx + 9, ty + 2);
                await Place(Items.Pole, tx + 5 + BigLineBelts, ty + 1);
                placed++;
            }

            if (placed > before)
            {
                ty += 3;
            }
        }

        Assert.Equal(BigLines, placed);
        return (x, y, places, chest, furnace, generator);
    }

    /// <summary>A grassland plot of the player's with <see cref="Lines"/> powered furnace lines working on it.</summary>
    private async Task<(int X, int Y)> FactoryPlotAsync(GameClient client)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 100_000_000, "test", CancellationToken.None);
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            foreach (var (item, quantity) in new[]
                     {
                         (Items.WoodenChest, 2 * Lines), (Items.Inserter, 2 * Lines), (Items.Furnace, Lines), (Items.Pole, Lines),
                         (Items.Generator, Lines), (Items.Coal, 60 * Lines), (Items.IronOre, 200 * Lines),
                     })
            {
                await inventory.AddAsync(client.AccountId, item, quantity, CancellationToken.None);
            }
        }

        var (x, y) = FreePlot(Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());
        var plot = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(plot.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        bool Free(int tx, int ty) => Enumerable.Range(tx, 6).All(i => bytes[ty * PlotLayout.Size + i] == (byte)TileObject.None);

        var placed = 0;
        for (var ty = 20; ty + 3 < PlotLayout.Size && placed < Lines; ty++)
        {
            for (var tx = 2; tx + 7 <= PlotLayout.Size && placed < Lines; tx += 7)
            {
                if (!Free(tx, ty) || !Free(tx, ty + 1) || !Free(tx, ty - 1) || !Free(tx, ty + 2) || !Free(tx, ty + 3))
                {
                    continue;
                }

                // A furnace covers 2 × 2 tiles, from its own to the right and below.
                var source = await PlaceAsync(client, x, y, Items.WoodenChest, tx, ty);
                await PlaceAsync(client, x, y, Items.Inserter, tx + 1, ty);
                await PlaceAsync(client, x, y, Items.Furnace, tx + 2, ty);
                await PlaceAsync(client, x, y, Items.Inserter, tx + 4, ty);
                await PlaceAsync(client, x, y, Items.WoodenChest, tx + 5, ty);
                await client.SendAsync("storage.put", new { x, y, id = source, item = Items.IronOre, quantity = 200 });
                await client.SendAsync("storage.put", new { x, y, id = source, item = Items.Coal, quantity = 40 });
                await PlaceAsync(client, x, y, Items.Pole, tx + 2, ty + 2);
                var generator = await PlaceAsync(client, x, y, Items.Generator, tx + 3, ty + 2);
                await client.SendAsync("generator.load", new { x, y, id = generator, item = Items.Coal, quantity = 20 });
                placed++;
                ty += 4;
                break;
            }
        }

        Assert.Equal(Lines, placed);
        return (x, y);
    }

    /// <param name="skip">Plots of the biome to pass over first, for a second test on the same server.</param>
    private static (int X, int Y) FreePlot(Biome biome, int skip = 0)
    {
        var found = 0;
        for (var y = 0; y < Map.Height; y += 5)
        {
            for (var x = 0; x < Map.Width; x += 5)
            {
                if (Map.BiomeAt(x, y) == biome && found++ >= 4000 + skip)
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("No such plot.");
    }

    private static async Task<string> PlaceAsync(GameClient client, int x, int y, string kind, int tileX, int tileY, int rotation = East)
    {
        var reply = await client.SendAsync("plot.place", new { x, y, kind, tileX, tileY, rotation });
        Assert.True(reply.Type() == "plot.placed", reply.GetRawText());
        return reply.Payload().GetProperty("object").GetProperty("id").GetString()!;
    }
}
