using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.Power;

/// <summary>A plot's power grid as it was last settled (<see cref="GridData"/> as JSON).</summary>
public sealed class PowerPlotRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public DateTimeOffset SettledAt { get; set; }

    public string Data { get; set; } = "{}";
}

/// <summary>A grid tie whose owner sells power to other players' plots, at a price per kWh.</summary>
public sealed class PowerTieRow
{
    public Guid Id { get; set; }

    public int X { get; set; }

    public int Y { get; set; }

    public long PriceCents { get; set; }

    public bool Open { get; set; }
}

/// <summary>A cable from a supplier's grid tie to a receiver's, carrying up to <see cref="Kw"/> until <see cref="PaidUntil"/>.</summary>
public sealed class PowerLineRow
{
    public Guid Id { get; set; }

    public int FromX { get; set; }

    public int FromY { get; set; }

    public Guid FromTie { get; set; }

    public int ToX { get; set; }

    public int ToY { get; set; }

    public Guid ToTie { get; set; }

    public double Kw { get; set; }

    public DateTimeOffset PaidUntil { get; set; }

    public DateTimeOffset BuiltAt { get; set; }
}

public sealed class PowerDbContext(DbContextOptions<PowerDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "power";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<PowerPlotRow>(e =>
        {
            e.ToTable("plots");
            e.HasKey(p => new { p.X, p.Y });
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.SettledAt).HasColumnName("settled_at");
            e.Property(p => p.Data).HasColumnName("data").HasColumnType("jsonb");
        });
        model.Entity<PowerTieRow>(e =>
        {
            e.ToTable("ties", t => t.HasCheckConstraint("ck_ties_price", "price_cents >= 0"));
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.X).HasColumnName("x");
            e.Property(t => t.Y).HasColumnName("y");
            e.Property(t => t.PriceCents).HasColumnName("price_cents");
            e.Property(t => t.Open).HasColumnName("open");
            e.HasIndex(t => new { t.X, t.Y });
        });
        model.Entity<PowerLineRow>(e =>
        {
            e.ToTable("lines", t => t.HasCheckConstraint("ck_lines_kw", "kw > 0"));
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.FromX).HasColumnName("from_x");
            e.Property(l => l.FromY).HasColumnName("from_y");
            e.Property(l => l.FromTie).HasColumnName("from_tie");
            e.Property(l => l.ToX).HasColumnName("to_x");
            e.Property(l => l.ToY).HasColumnName("to_y");
            e.Property(l => l.ToTie).HasColumnName("to_tie");
            e.Property(l => l.Kw).HasColumnName("kw");
            e.Property(l => l.PaidUntil).HasColumnName("paid_until");
            e.Property(l => l.BuiltAt).HasColumnName("built_at");
            e.HasIndex(l => new { l.FromX, l.FromY });
            e.HasIndex(l => new { l.ToX, l.ToY });
            e.HasIndex(l => new { l.FromTie, l.ToTie }).IsUnique();
        });
    }
}

/// <summary>A generator and the network it stands on, as the generator window shows them.</summary>
public sealed record GeneratorReply(Guid Id, PowerGrid Grid, DateTimeOffset ServerTime, IReadOnlyList<(string Item, int Quantity)> Taken);

/// <summary>
/// The power grids of plots. Within one request (one scope) a plot's grid is read once and handed
/// to everyone who asks, so belts and inserters, the machines they feed and the generators they
/// fuel all work with the same one.
/// </summary>
public sealed class PowerService(GameDb db, IPlotObjects plots, IPlotRegistry registry, IInventory inventory, TimeProvider time, IServiceProvider services)
    : IPlotPower, IPlotAutomation
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<(int, int), PowerGrid> _grids = [];

    async Task<IPowerGrid> IPlotPower.OpenAsync(int x, int y, CancellationToken cancellationToken) => await OpenAsync(x, y, cancellationToken);

    /// <summary>The cables of this request, by id, as the open grids share them.</summary>
    private readonly Dictionary<Guid, PowerLink> _links = [];

    /// <summary>A plot's grid, with the grids of every plot cables join it to, near or far, so power can flow between them.</summary>
    public async Task<PowerGrid> OpenAsync(int x, int y, CancellationToken cancellationToken)
    {
        if (_grids.TryGetValue((x, y), out var open))
        {
            return open;
        }

        var lines = await LinesAroundAsync(x, y, cancellationToken);
        // Locked in one order, so two requests opening the same plots never wait on each other.
        var linked = lines.SelectMany(l => new[] { (l.FromX, l.FromY), (l.ToX, l.ToY) }).Append((x, y)).Distinct().OrderBy(p => p.Item1).ThenBy(p => p.Item2);
        foreach (var (px, py) in linked)
        {
            if (!_grids.ContainsKey((px, py)))
            {
                await OpenOneAsync(px, py, cancellationToken);
            }
        }

        foreach (var line in lines)
        {
            Link(line);
        }

        return _grids[(x, y)];
    }

    /// <summary>Joins two open grids by a cable; nothing when they are joined already.</summary>
    public PowerLink Link(PowerLineRow line)
    {
        if (_links.TryGetValue(line.Id, out var known))
        {
            return known;
        }

        var link = new PowerLink
        {
            Id = line.Id, SupplierTie = line.FromTie, ReceiverTie = line.ToTie, Kw = line.Kw, Until = line.PaidUntil,
            Supplier = _grids[(line.FromX, line.FromY)], Receiver = _grids[(line.ToX, line.ToY)],
        };
        link.Supplier.Exports.Add(link);
        link.Receiver.Imports.Add(link);
        _links[line.Id] = link;
        return link;
    }

    /// <summary>Takes a cable out of the open grids, both settled at <paramref name="at"/> first.</summary>
    public async Task UnlinkAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!_links.Remove(id, out var link))
        {
            return;
        }

        link.Supplier!.Settle(at);
        link.Receiver!.Settle(at);
        link.Supplier.Exports.Remove(link);
        link.Receiver.Imports.Remove(link);
        link.Supplier.Replan();
        link.Receiver.Replan();
        await link.Supplier.SaveAsync(cancellationToken);
        await link.Receiver.SaveAsync(cancellationToken);
    }

    /// <summary>Every cable of the plots joined to (x, y) by cables, however many steps away.</summary>
    public async Task<IReadOnlyList<PowerLineRow>> LinesAroundAsync(int x, int y, CancellationToken cancellationToken)
    {
        var lines = new Dictionary<Guid, PowerLineRow>();
        var seen = new HashSet<(int, int)> { (x, y) };
        var queue = new Queue<(int X, int Y)>([(x, y)]);
        while (queue.TryDequeue(out var plot))
        {
            foreach (var line in await LinesOfAsync(plot.X, plot.Y, cancellationToken))
            {
                lines.TryAdd(line.Id, line);
                foreach (var end in new[] { (line.FromX, line.FromY), (line.ToX, line.ToY) })
                {
                    if (seen.Add(end))
                    {
                        queue.Enqueue(end);
                    }
                }
            }
        }

        return lines.Values.ToList();
    }

    /// <summary>The cables that start or end on a plot.</summary>
    public async Task<IReadOnlyList<PowerLineRow>> LinesOfAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT id, from_x, from_y, from_tie, to_x, to_y, to_tie, kw, paid_until, built_at FROM power.lines
            WHERE (from_x = @x AND from_y = @y) OR (to_x = @x AND to_y = @y)
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var lines = new List<PowerLineRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new PowerLineRow
            {
                Id = reader.GetGuid(0), FromX = reader.GetInt32(1), FromY = reader.GetInt32(2), FromTie = reader.GetGuid(3),
                ToX = reader.GetInt32(4), ToY = reader.GetInt32(5), ToTie = reader.GetGuid(6), Kw = reader.GetDouble(7),
                PaidUntil = reader.GetFieldValue<DateTimeOffset>(8), BuiltAt = reader.GetFieldValue<DateTimeOffset>(9),
            });
        }

        return lines;
    }

    private async Task<PowerGrid> OpenOneAsync(int x, int y, CancellationToken cancellationToken)
    {
        // A grid starts the first time it is looked at, so a machine's time is counted from a moment that stays put.
        await using (var create = await db.CommandAsync(
            "INSERT INTO power.plots (x, y, settled_at, data) VALUES (@x, @y, @now, '{}') ON CONFLICT DO NOTHING", cancellationToken))
        {
            create.Parameters.AddWithValue("x", x);
            create.Parameters.AddWithValue("y", y);
            create.Parameters.AddWithValue("now", time.GetUtcNow());
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTimeOffset settled;
        GridData data;
        await using (var read = await db.CommandAsync("SELECT settled_at, data FROM power.plots WHERE x = @x AND y = @y FOR UPDATE", cancellationToken))
        {
            read.Parameters.AddWithValue("x", x);
            read.Parameters.AddWithValue("y", y);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            settled = reader.GetFieldValue<DateTimeOffset>(0);
            data = JsonSerializer.Deserialize<GridData>(reader.GetString(1), Json) ?? new GridData();
        }

        var objects = await plots.ObjectsAsync(x, y, cancellationToken);
        var biome = (await registry.FindAsync(x, y, cancellationToken))?.Biome ?? 0;
        var grid = new PowerGrid(x, y, objects, data, settled, (d, at, ct) => WriteAsync(x, y, d, at, ct), biome);
        _grids[(x, y)] = grid;
        return grid;
    }

    public async Task<IPowerClock> ClockAsync(Guid id, CancellationToken cancellationToken)
    {
        // A grid already open knows its machines, even one just taken off the plot.
        if (_grids.Values.FirstOrDefault(g => g.Knows(id)) is { } known)
        {
            return known.Clock(id);
        }

        if (await plots.PlotOfAsync(id, cancellationToken) is { } plot)
        {
            return (await OpenAsync(plot.X, plot.Y, cancellationToken)).Clock(id);
        }

        return RealClock.Instance;
    }

    /// <summary>Settles the grid at the present moment, before objects are put on the plot or taken off.</summary>
    public async Task CatchUpAsync(int x, int y, CancellationToken cancellationToken) =>
        await db.InTransactionAsync(async () =>
        {
            var grid = await OpenAsync(x, y, cancellationToken);
            grid.Settle(time.GetUtcNow());
            await grid.SaveAsync(cancellationToken);
        }, cancellationToken);

    public Task<GeneratorReply> StateAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var grid = await GeneratorGridAsync(player, x, y, id, cancellationToken);
            return new GeneratorReply(id, grid, time.GetUtcNow(), []);
        }, cancellationToken);

    /// <summary>Puts wood or coal from the inventory by the generator's fire.</summary>
    public Task<GeneratorReply> LoadAsync(Guid player, int x, int y, Guid id, string item, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var grid = await GeneratorGridAsync(player, x, y, id, cancellationToken);
            if (grid.Objects.Any(o => o.Id == id && (Items.IsRenewable(o.Kind) || Items.IsBattery(o.Kind))))
            {
                throw new GameException("no_fuel_needed", "Water, the sun, the wind or stored power drives it; it burns nothing.");
            }

            grid.Fuel(id, item, quantity, time.GetUtcNow());
            await inventory.RemoveAsync(player, item, quantity, cancellationToken);
            await grid.SaveAsync(cancellationToken);
            return new GeneratorReply(id, grid, time.GetUtcNow(), []);
        }, cancellationToken);

    /// <summary>Takes the fuel not burning yet back into the inventory, as much as fits; the rest stays by the fire.</summary>
    public Task<GeneratorReply> TakeAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var grid = await GeneratorGridAsync(player, x, y, id, cancellationToken, PlotRight.Take);
            var taken = new List<(string, int)>();
            var now = time.GetUtcNow();
            if (grid.Generator(id, now) is { FuelItem: { } item, Fuel: > 0 } state
                && grid.Unfuel(id, now, (int)await inventory.FitAsync(player, item, state.Fuel, cancellationToken)) is { } fuel)
            {
                await inventory.AddAsync(player, fuel.Item, fuel.Quantity, cancellationToken);
                taken.Add(fuel);
                await grid.SaveAsync(cancellationToken);
            }

            return new GeneratorReply(id, grid, now, taken);
        }, cancellationToken);

    /// <summary>A generator or a battery taken off its plot: its fuel goes back to the owner, its stored power is lost, and the grid goes on without it.
    /// A grid tie taken off takes its cables with it; one selling power that is paid for stays until that time runs out.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        // Power another player paid for is owed until its time runs out: the tie that sells it stays till then.
        await using (var paid = await db.CommandAsync(
            "SELECT 1 FROM power.lines WHERE from_tie = @id AND paid_until > @now AND paid_until < @forever LIMIT 1", cancellationToken))
        {
            paid.Parameters.AddWithValue("id", id);
            paid.Parameters.AddWithValue("now", time.GetUtcNow());
            paid.Parameters.AddWithValue("forever", PowerLink.Forever);
            if (await paid.ExecuteScalarAsync(cancellationToken) is not null)
            {
                throw new GameException("line_paid", "Another player paid for power along a cable from this grid tie; it stays until that time runs out.");
            }
        }

        foreach (var link in _links.Values.Where(l => l.SupplierTie == id || l.ReceiverTie == id).ToList())
        {
            await UnlinkAsync(link.Id, time.GetUtcNow(), cancellationToken);
        }

        await using (var delete = await db.CommandAsync(
            "DELETE FROM power.lines WHERE from_tie = @id OR to_tie = @id; DELETE FROM power.ties WHERE id = @id", cancellationToken))
        {
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        // Objects are taken off only after the plot was caught up, which opened its grid.
        if (_grids.Values.FirstOrDefault(g => g.Data.Generators.ContainsKey(id) || g.Data.Batteries.ContainsKey(id)
            || g.Objects.Any(o => o.Id == id && (Items.IsGenerator(o.Kind) || Items.IsHeater(o.Kind) || Items.IsBattery(o.Kind)))) is not { } grid)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (grid.Unfuel(id, now) is { } fuel)
        {
            await inventory.AddAsync(owner, fuel.Item, fuel.Quantity, cancellationToken);
        }

        grid.Remove(id, now);
        await grid.SaveAsync(cancellationToken);
    }

    /// <summary>The grid of a plot the player may do <paramref name="right"/> on (owner or company member) with the generator on it, after the plot's belts and
    /// inserters (which may have fed it) are caught up.</summary>
    private async Task<PowerGrid> GeneratorGridAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken, PlotRight right = PlotRight.Work)
    {
        await plots.RequireOwnerAsync(player, x, y, cancellationToken, right);
        foreach (var machinery in services.GetServices<IPlotAutomation>())
        {
            if (!ReferenceEquals(machinery, this))
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }
        }

        var grid = await OpenAsync(x, y, cancellationToken);
        if (!grid.Objects.Any(o => o.Id == id && (Items.IsGenerator(o.Kind) || Items.IsRenewable(o.Kind) || Items.IsHeater(o.Kind) || Items.IsBattery(o.Kind))))
        {
            throw new GameException("no_object", "There is no such generator on this plot.");
        }

        return grid;
    }

    private async Task WriteAsync(int x, int y, GridData data, DateTimeOffset settled, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "UPDATE power.plots SET settled_at = @settled, data = @data WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("settled", settled);
        command.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(data, Json));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Plain time, for a machine that stands on no plot.</summary>
    private sealed class RealClock : IPowerClock
    {
        public static readonly RealClock Instance = new();

        public DateTimeOffset At(DateTimeOffset now) => now;

        public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now) => machineTime > now ? machineTime : now;

        public double Speed(DateTimeOffset now) => 1;
    }
}

/// <summary>Empties a generator that is being picked up.</summary>
public sealed class GeneratorContents(PowerService power) : IMachineContents
{
    public Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken) => power.ReleaseAsync(owner, id, cancellationToken);
}

public sealed record GeneratorRequest(int X, int Y, Guid Id);

public sealed record LoadGeneratorRequest(int X, int Y, Guid Id, string Item, int Quantity);

public static class PowerJson
{
    /// <summary>The plot's networks for drawing them: which poles are wired together, which machines
    /// and generators each powers and how well; sent along with the plot's logistics.</summary>
    public static object Grid(PowerGrid grid, DateTimeOffset now)
    {
        var poles = grid.Objects.Where(o => Items.IsPole(o.Kind)).ToList();
        var wires = new List<Guid[]>();
        for (var i = 0; i < poles.Count; i++)
        {
            for (var j = i + 1; j < poles.Count; j++)
            {
                if (PowerRules.Wired(poles[i], poles[j]))
                {
                    wires.Add([poles[i].Id, poles[j].Id]);
                }
            }
        }

        return new
        {
            supplyRadius = PowerRules.SupplyRadius,
            wireReach = PowerRules.WireReach,
            // Every kind of pole: the tiles it powers each way and how far it is wired; two kinds are wired as far as the shorter reach.
            poleKinds = PowerRules.Poles.ToDictionary(p => p.Key, p => new { supply = p.Value.Supply, reach = p.Value.Reach }),
            wires,
            networks = grid.Networks.Select(n => new
            {
                id = n.Id,
                poles = n.Poles,
                generators = n.Generators,
                consumers = n.Consumers,
                demandKw = grid.DemandKw(n, now),
                supplyKw = grid.SupplyKw(n, now),
                // What its grid ties send along cables to other plots and get from them now (kW).
                ties = n.Ties,
                exportKw = grid.ExportKw(n, now),
                importKw = grid.ImportKw(n, now),
                hydro = n.Hydro,
                hydroKw = grid.HydroKw(n),
                solar = n.Solar,
                solarKw = grid.SolarKw(n, now),
                wind = n.Wind,
                windKw = grid.WindKw(n, now),
                batteries = n.Batteries,
                bankKj = grid.BankKj(n, now),
                bankCapacityKj = n.BankKj,
                // kW into the batteries, less than 0 while they give power out.
                bankRateKw = grid.BankRate(n, now),
                speed = grid.Speed(n, now),
                // Unix milliseconds of when a generator next burns out, or null.
                changesAt = grid.NextChange(n, now)?.ToUnixTimeMilliseconds(),
            }),
            // What every machine that runs on power draws, by kind.
            demand = PowerRules.Demand,
            // The sun over the plot, and the sandstorm over a desert one: solar power gives nothing at night and less in a storm.
            day = Sun.IsDay(now),
            storm = grid.Storm(now),
            sunChangesAt = Sun.NextChange(now).ToUnixTimeMilliseconds(),
            stormAt = grid.Sandy ? SandWeather.Next(now).Start.ToUnixTimeMilliseconds() : (long?)null,
            stormEndsAt = grid.Sandy ? SandWeather.Next(now).End.ToUnixTimeMilliseconds() : (long?)null,
            // How strongly the wind blows (0 to 1) and when it changes; on the glacier, the blizzard that stops the turbines.
            wind = Wind.Strength(now),
            windChangesAt = Wind.NextChange(now).ToUnixTimeMilliseconds(),
            blizzard = grid.Blizzard(now),
            blizzardAt = grid.Icy ? Blizzard.Next(now).Start.ToUnixTimeMilliseconds() : (long?)null,
            blizzardEndsAt = grid.Icy ? Blizzard.Next(now).End.ToUnixTimeMilliseconds() : (long?)null,
            // On the glacier: machines no heater warms work at frostSpeed of their pace; heaters warm heaterReach tiles each way.
            icy = grid.Icy,
            cold = grid.ColdMachines(now),
            frostSpeed = Frost.Speed,
            heaterReach = Frost.HeaterReach,
            heaters = grid.Objects.Where(o => Items.IsHeater(o.Kind)).Select(o => new
            {
                id = o.Id,
                burning = grid.Generator(o.Id, now).Running,
                outAt = grid.HeaterOutAt(o.Id)?.ToUnixTimeMilliseconds(),
            }),
        };
    }

    public static object Generator(GeneratorReply reply)
    {
        var grid = reply.Grid;
        var now = reply.ServerTime;
        var state = grid.Generator(reply.Id, now);
        var network = grid.NetworkOf(reply.Id);
        var kind = grid.Objects.FirstOrDefault(o => o.Id == reply.Id)?.Kind ?? Items.Generator;
        var hydro = Items.IsHydro(kind) || Items.IsGeothermal(kind);
        var chp = kind == Items.ChpPlant;
        var solar = Items.IsSolar(kind);
        var battery = Items.IsBattery(kind);
        var wind = Items.IsWind(kind);
        var heater = Items.IsHeater(kind);
        return new
        {
            id = reply.Id,
            kind,
            // A water power plant burns nothing: it gives capacityKw all the time, more in the mountains. So does a geothermal plant.
            hydro,
            geothermal = Items.IsGeothermal(kind),
            // A solar power plant burns nothing either: it gives capacityKw by day, more on sand, less in a sandstorm unless a reed fence shelters it.
            solar,
            day = Sun.IsDay(now),
            storm = grid.Storm(now),
            sheltered = solar && grid.Sheltered(reply.Id),
            sunChangesAt = Sun.NextChange(now).ToUnixTimeMilliseconds(),
            // A wind turbine burns nothing either: it gives capacityKw times the wind's strength, nothing in a blizzard on the glacier.
            wind,
            windStrength = Wind.Strength(now),
            windChangesAt = Wind.NextChange(now).ToUnixTimeMilliseconds(),
            windBonus = wind ? Wind.Bonus(grid.Biome) : 0,
            blizzard = grid.Blizzard(now),
            // A heater gives no power: it burns heaterKw all the time and warms the machines heaterReach tiles from it on the glacier.
            heater,
            heaterKw = heater ? Frost.HeaterKw : 0,
            heaterReach = Frost.HeaterReach,
            icy = grid.Icy,
            // A combined heat and power plant warms the machines chpReach tiles from it on the glacier while it burns.
            chp,
            chpReach = PowerRules.ChpReach,
            warmed = heater || chp ? grid.Objects.Count(o => PowerRules.Demand.ContainsKey(o.Kind) && grid.Warms(grid.Objects.First(h => h.Id == reply.Id), o.TileX, o.TileY)) : 0,
            // A battery stores chargeKj of capacityKj; outputKw is less than 0 while it charges.
            battery,
            chargeKj = battery ? grid.Charge(reply.Id, now) : 0,
            capacityKj = battery ? PowerRules.StoreKj(kind) : 0,
            fuel = state.FuelItem is { } fuel && state.Fuel > 0 ? new { id = fuel, name = Items.Names[fuel], quantity = state.Fuel } : null,
            // kJ left in the piece burning now and in all the fuel.
            energy = state.Energy,
            total = state.Total,
            outputKw = heater ? 0 : grid.Output(reply.Id, now),
            capacityKw = hydro ? grid.HydroKw(reply.Id) : solar ? grid.SolarPeakKw(reply.Id) : wind ? grid.WindPeakKw(reply.Id)
                : battery ? PowerRules.StoreKw(kind) : heater ? 0 : grid.KwOf(reply.Id),
            // A steam generator, a coal and a nuclear power plant give less on sand, where there is no water to cool them.
            dry = PowerRules.Steam(kind) && grid.Sandy,
            hydroBonus = Items.IsGeothermal(kind) ? Geothermal.Biomes.GetValueOrDefault(grid.Biome) : hydro ? WaterPower.Biomes.GetValueOrDefault(grid.Biome) : 0,
            runsOutAt = grid.RunsOutAt(reply.Id)?.ToUnixTimeMilliseconds(),
            serverTime = now.ToUnixTimeMilliseconds(),
            maxFuel = PowerRules.MaxFuel,
            // What this generator burns.
            fuels = PowerRules.Fuels.Where(f => PowerRules.Burns(kind, f.Key)).Select(f => new { id = f.Key, name = Items.Names[f.Key], kj = f.Value }),
            // The network it feeds, or null when no pole reaches it.
            network = network is null ? null : new
            {
                id = network.Id,
                generators = network.Generators.Count,
                machines = network.Consumers.Count,
                demandKw = grid.DemandKw(network, now),
                supplyKw = grid.SupplyKw(network, now),
                hydro = network.Hydro.Count,
                solar = network.Solar.Count,
                wind = network.Wind.Count,
                batteries = network.Batteries.Count,
                bankKj = grid.BankKj(network, now),
                bankCapacityKj = network.BankKj,
                speed = grid.Speed(network, now),
            },
            taken = reply.Taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
        };
    }
}

public abstract class GeneratorHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<PowerService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("generator", PowerJson.Generator(reply));
    }

    protected abstract Task<GeneratorReply> RunAsync(PowerService power, Guid player, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What a generator holds and gives now.</summary>
public sealed class GetGeneratorHandler(IServiceScopeFactory scopes) : GeneratorHandler(scopes)
{
    public override string Type => "generator.get";

    protected override Task<GeneratorReply> RunAsync(PowerService power, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<GeneratorRequest>(payload);
        return power.StateAsync(player, request.X, request.Y, request.Id, cancellationToken);
    }
}

public sealed class LoadGeneratorHandler(IServiceScopeFactory scopes) : GeneratorHandler(scopes)
{
    public override string Type => "generator.load";

    protected override Task<GeneratorReply> RunAsync(PowerService power, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LoadGeneratorRequest>(payload);
        return power.LoadAsync(player, request.X, request.Y, request.Id, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class TakeGeneratorHandler(IServiceScopeFactory scopes) : GeneratorHandler(scopes)
{
    public override string Type => "generator.take";

    protected override Task<GeneratorReply> RunAsync(PowerService power, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<GeneratorRequest>(payload);
        return power.TakeAsync(player, request.X, request.Y, request.Id, cancellationToken);
    }
}

/// <summary>Generators and power plants, poles, substations and pylons that carry their power to machines on plots, and batteries. Owns the <c>power</c> schema.</summary>
public sealed class PowerModule : IGameModule
{
    public string Name => "Power";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PowerDbContext>(PowerDbContext.Schema);
        services.AddScoped<PowerService>();
        services.AddScoped<IPlotPower>(provider => provider.GetRequiredService<PowerService>());
        services.AddScoped<IPlotAutomation>(provider => provider.GetRequiredService<PowerService>());
        services.AddScoped<IMachineContents, GeneratorContents>();
        services.AddSingleton<IMessageHandler, GetGeneratorHandler>();
        services.AddSingleton<IMessageHandler, LoadGeneratorHandler>();
        services.AddSingleton<IMessageHandler, TakeGeneratorHandler>();
        services.AddScoped<PowerLineService>();
        services.AddSingleton<IMessageHandler, GetTieHandler>();
        services.AddSingleton<IMessageHandler, PriceTieHandler>();
        services.AddSingleton<IMessageHandler, BuildLineHandler>();
        services.AddSingleton<IMessageHandler, BuyLineHandler>();
        services.AddSingleton<IMessageHandler, RemoveLineHandler>();
        services.AddSingleton<IMessageHandler, TieOffersHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<PowerDbContext>(cancellationToken);
}
