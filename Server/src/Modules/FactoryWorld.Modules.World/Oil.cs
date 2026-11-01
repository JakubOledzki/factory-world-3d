using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>What a well pumps from: the oil or the gas under a plot, or the oil under the seabed off a coast plot. Stored, so never renumber.</summary>
public enum OilField : short
{
    Oil = 0,
    Gas = 1,
    Seabed = 2,
}

/// <summary>
/// Wells pump what the geological survey found under a plot: a pumpjack (on power) the oil, a gas well the gas,
/// and on the coast an offshore platform (on power) the oil under the seabed. Each stands only on a plot whose
/// owner had it surveyed and where the survey found that deposit; a bigger deposit pumps faster, holds more and
/// takes more wells. A deposit runs dry: what all its wells have pumped is counted against what it holds, and a
/// water injector on the plot forces out half as much oil again. A well wears its seals: every
/// <see cref="WearPerGasket"/> units it uses up a gasket, and without one it leaks. A leaking well pumps at half the
/// speed and fouls the ground around it, where nothing can be sown or planted, unless an oil separator stands near.
/// </summary>
public static class OilRules
{
    /// <summary>Most a well holds; full, it stops until its oil or gas is taken out.</summary>
    public const int Capacity = 200;

    /// <summary>Units a well pumps on one gasket.</summary>
    public const int WearPerGasket = 500;

    /// <summary>Spare gaskets a well keeps.</summary>
    public const int MaxGaskets = 10;

    /// <summary>A leaking well fouls the tiles this far from it each way, unless an oil separator stands within as far of it.</summary>
    public const int LeakReach = 6;

    /// <summary>Units taken out by hand give one experience point for every this many.</summary>
    public const int UnitsPerXp = 5;

    /// <summary>How much a water injector raises what an oil deposit gives up.</summary>
    public const double InjectorBonus = 1.5;

    public static OilField FieldOf(string kind) => kind switch
    {
        Items.GasWell => OilField.Gas,
        Items.OffshorePlatform => OilField.Seabed,
        _ => OilField.Oil,
    };

    public static string Product(OilField field) => field == OilField.Gas ? Items.NaturalGas : Items.CrudeOil;

    /// <summary>The deposit a well pumps from, as the survey found it.</summary>
    public static DepositSize Size(Survey survey, OilField field) => field switch
    {
        OilField.Gas => survey.Gas,
        OilField.Seabed => survey.Seabed,
        _ => survey.Oil,
    };

    /// <summary>Units a deposit gives up in all; one under the seabed holds twice as much.</summary>
    public static long Reserve(OilField field, DepositSize size, bool injector)
    {
        long units = size switch
        {
            DepositSize.Small => 4_000,
            DepositSize.Medium => 10_000,
            DepositSize.Large => 25_000,
            _ => 0,
        };
        if (field == OilField.Seabed)
        {
            units *= 2;
        }

        return field != OilField.Gas && injector ? (long)(units * InjectorBonus) : units;
    }

    /// <summary>Wells that may pump one deposit.</summary>
    public static int MaxWells(DepositSize size) => (int)size;

    /// <summary>How long a well takes to pump one unit; twice as long while it leaks.</summary>
    public static TimeSpan UnitTime(string kind, DepositSize size, bool leaking)
    {
        var seconds = (kind, size) switch
        {
            (Items.GasWell, DepositSize.Large) => 2.5,
            (Items.GasWell, DepositSize.Medium) => 4,
            (Items.GasWell, _) => 6,
            (Items.OffshorePlatform, DepositSize.Large) => 1.5,
            (Items.OffshorePlatform, DepositSize.Medium) => 2,
            (Items.OffshorePlatform, _) => 3,
            (_, DepositSize.Large) => 3,
            (_, DepositSize.Medium) => 5,
            _ => 8,
        };
        return TimeSpan.FromSeconds(leaking ? seconds * 2 : seconds);
    }

    /// <summary>Whether a tile lies within <see cref="LeakReach"/> of a tile, each way.</summary>
    public static bool Near(int ax, int ay, int bx, int by) => Math.Abs(ax - bx) <= LeakReach && Math.Abs(ay - by) <= LeakReach;
}

/// <summary>
/// One well as it works through time, in memory. It counts its units in its own time (on the power grid for a
/// pumpjack and a platform) and takes them from the deposit it shares with the plot's other wells of its kind.
/// </summary>
/// <param name="left">Units the deposit still gives up, shared by the wells pumping it.</param>
public sealed class WellPatch(string kind, DepositSize size, int stored, DateTimeOffset? startedAt, int wear, int gaskets, bool leaking)
{
    public string Kind { get; } = kind;

    public DepositSize Size { get; } = size;

    public string Product { get; } = OilRules.Product(OilRules.FieldOf(kind));

    public int Stored { get; private set; } = stored;

    public DateTimeOffset? StartedAt { get; private set; } = startedAt;

    /// <summary>Units pumped on the gasket in it now.</summary>
    public int Wear { get; private set; } = wear;

    public int Gaskets { get; private set; } = gaskets;

    public bool Leaking { get; private set; } = leaking;

    public bool Changed { get; private set; }

    public List<ProductionEntry> Made { get; } = [];

    public bool Full => Stored >= OilRules.Capacity;

    public TimeSpan UnitTime => OilRules.UnitTime(Kind, Size, Leaking);

    /// <summary>Pumps every unit it could have by <paramref name="now"/>, when its own time is <paramref name="machineNow"/>;
    /// returns how many it took from the deposit, of the <paramref name="left"/> there were.</summary>
    public int Advance(DateTimeOffset now, DateTimeOffset machineNow, long left)
    {
        DateTimeOffset Real(DateTimeOffset machine) => machine >= machineNow ? now : now - (machineNow - machine);

        var pumped = 0;
        for (var guard = 0; guard < OilRules.Capacity + 2; guard++)
        {
            if (StartedAt is not { } started)
            {
                if (!Full && left - pumped > 0)
                {
                    StartedAt = machineNow;
                    Changed = true;
                }

                return pumped;
            }

            if (Full || left - pumped <= 0)
            {
                StartedAt = null;
                Changed = true;
                return pumped;
            }

            var end = started + UnitTime;
            if (end > machineNow)
            {
                return pumped;
            }

            Stored++;
            pumped++;
            Made.Add(ProductionEntry.Made(Product, 1, Real(end)));
            if (++Wear >= OilRules.WearPerGasket && !Leaking)
            {
                if (Gaskets > 0)
                {
                    Gaskets--;
                    Wear = 0;
                }
                else
                {
                    Leaking = true;
                }
            }

            StartedAt = end;
            Changed = true;
        }

        return pumped;
    }

    public int Take(int quantity, DateTimeOffset machineNow)
    {
        var taken = Math.Min(quantity, Stored);
        if (taken == 0)
        {
            return 0;
        }

        Stored -= taken;
        if (StartedAt is null)
        {
            StartedAt = machineNow;
        }

        Changed = true;
        return taken;
    }

    /// <summary>Puts gaskets in; a leaking well is sealed with the first one at once. Returns how many went in.</summary>
    public int Seal(int quantity)
    {
        var used = 0;
        if (Leaking && quantity > 0)
        {
            Leaking = false;
            Wear = 0;
            used = 1;
        }

        var spare = Math.Min(quantity - used, OilRules.MaxGaskets - Gaskets);
        Gaskets += Math.Max(0, spare);
        used += Math.Max(0, spare);
        Changed |= used > 0;
        return used;
    }

    public DateTimeOffset? NextChange(DateTimeOffset now, IPowerClock clock, long left) =>
        StartedAt is { } started && !Full && left > 0 ? clock.When(started + UnitTime, now) : null;
}

/// <summary>A well on its plot and how it is doing.</summary>
/// <param name="Left">Units its deposit still gives up.</param>
/// <param name="Reserve">Units its deposit gives up in all.</param>
public sealed record WellOnPlot(PlotObject Object, OilField Field, WellPatch Patch, IPowerClock Clock, long Reserve, long Left, bool Separated)
{
    public bool Polluting => Patch.Leaking && !Separated;
}

/// <summary>The clock of a well that needs no power.</summary>
internal sealed class SteadyClock : IPowerClock
{
    public static readonly SteadyClock Instance = new();

    public DateTimeOffset At(DateTimeOffset now) => now;

    public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now) => machineTime > now ? machineTime : now;

    public double Speed(DateTimeOffset now) => 1;
}

public sealed class OilService(
    GameDb db, PlotService plots, SurveyService surveys, IPlotObjects objects, IInventory inventory, ICharacterProgress progress,
    IProductionStats stats, IPlotPower power, TimeProvider time) : IPlacedHook, IPlotAutomation, IMachineContents, IMachinePorts, IMachineStatuses
{
    /// <summary>Fails unless a well of <paramref name="kind"/> may go on the plot: it was surveyed, the deposit is
    /// there and it does not have all the wells it takes yet.</summary>
    public async Task RequireSiteAsync(string kind, int x, int y, IEnumerable<PlacedOnPlot> others, CancellationToken cancellationToken)
    {
        if (!Items.IsWell(kind))
        {
            return;
        }

        var survey = await surveys.FindAsync(x, y, cancellationToken)
            ?? throw new GameException("not_surveyed", "Order a geological survey of the plot first: a well goes where it found oil or gas.");
        var field = OilRules.FieldOf(kind);
        var size = OilRules.Size(survey, field);
        if (size == DepositSize.None)
        {
            throw new GameException(field == OilField.Gas ? "no_gas" : "no_oil",
                field == OilField.Gas ? "The survey found no gas under this plot." : "The survey found no oil here.");
        }

        if (others.Count(o => Items.IsWell(o.Kind) && OilRules.FieldOf(o.Kind) == field) >= OilRules.MaxWells(size))
        {
            throw new GameException("too_many_wells", "This deposit takes no more wells.");
        }
    }

    /// <summary>Fails when the tile lies in ground fouled by a leaking well.</summary>
    public async Task RequireCleanAsync(int x, int y, int tileX, int tileY, CancellationToken cancellationToken)
    {
        if (Fouled(await objects.ObjectsAsync(x, y, cancellationToken), await LeakingAsync(x, y, cancellationToken), tileX, tileY))
        {
            throw new GameException("polluted", "Oil from a leaking well fouls the ground here.");
        }
    }

    /// <summary>Whether oil from a leaking well fouls a tile: a well leaks onto the tiles around it unless an oil separator stands near it.</summary>
    public static bool Fouled(IEnumerable<PlotObject> placed, IReadOnlySet<Guid> leaking, int tileX, int tileY)
    {
        var all = placed.ToList();
        return all.Any(w => leaking.Contains(w.Id) && OilRules.Near(w.TileX, w.TileY, tileX, tileY) && !Separated(all, w));
    }

    private static bool Separated(IEnumerable<PlotObject> placed, PlotObject well) =>
        placed.Any(s => s.Kind == Items.OilSeparator && OilRules.Near(s.TileX, s.TileY, well.TileX, well.TileY));

    private async Task<HashSet<Guid>> LeakingAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT object_id FROM world.plot_wells WHERE x = @x AND y = @y AND leaking", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public Task<WellOnPlot> StateAsync(Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            return await WorkedAsync(x, y, id, automation, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes what a well holds into the player's inventory, as much as fits; the rest stays in.</summary>
    public Task<(WellOnPlot Well, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp)> TakeAsync(
        Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            var well = await WorkedAsync(x, y, id, automation, cancellationToken);
            var machineNow = well.Clock.At(time.GetUtcNow());
            var taken = new List<(string, int)>();
            if (well.Patch.Stored > 0)
            {
                var quantity = (int)await inventory.FitAsync(player, well.Patch.Product, well.Patch.Stored, cancellationToken);
                well.Patch.Take(quantity, machineNow);
                await inventory.AddAsync(player, well.Patch.Product, quantity, cancellationToken);
                taken.Add((well.Patch.Product, quantity));
            }

            await SaveAsync(x, y, well, 0, cancellationToken);
            var total = taken.Sum(t => t.Item2);
            XpGain? xp = total >= OilRules.UnitsPerXp ? await progress.AddXpAsync(player, total / OilRules.UnitsPerXp, cancellationToken) : null;
            return (well, (IReadOnlyList<(string, int)>)taken, xp);
        }, cancellationToken);

    /// <summary>Puts gaskets from the player's inventory into a well: a leaking one is sealed at once.</summary>
    public Task<(WellOnPlot Well, IReadOnlyList<(string Item, int Quantity)> Taken, XpGain? Xp)> SealAsync(
        Guid player, int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var well = await WorkedAsync(x, y, id, automation, cancellationToken);
            var have = await inventory.CountAsync(player, Items.Gasket, cancellationToken);
            if (have == 0)
            {
                throw new GameException("no_gasket", "You have no gaskets.");
            }

            var used = well.Patch.Seal((int)Math.Min(have, OilRules.MaxGaskets + 1));
            if (used == 0)
            {
                throw new GameException("well_full", $"A well keeps at most {OilRules.MaxGaskets} gaskets.");
            }

            await inventory.RemoveAsync(player, Items.Gasket, used, cancellationToken);
            await SaveAsync(x, y, well, 0, cancellationToken);
            return (await WorkedAsync(x, y, id, [], cancellationToken), (IReadOnlyList<(string, int)>)[], (XpGain?)null);
        }, cancellationToken);

    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (!Items.IsWell(placed.Kind))
        {
            return;
        }

        await using (var insert = await db.CommandAsync(
            """
            INSERT INTO world.plot_wells (object_id, x, y, owner_id, kind, stored, started_at, wear, gaskets, leaking)
            VALUES (@id, @x, @y, @owner, @kind, 0, NULL, 0, 0, false) ON CONFLICT DO NOTHING
            """, cancellationToken))
        {
            insert.Parameters.AddWithValue("id", placed.Id);
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("owner", owner);
            insert.Parameters.AddWithValue("kind", placed.Kind);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await CatchUpAsync(x, y, cancellationToken);
    }

    public Task CatchUpAsync(int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            foreach (var well in await OpenAsync(x, y, cancellationToken))
            {
                await AdvanceAsync(x, y, well, cancellationToken);
            }
        }, cancellationToken);

    /// <summary>The well is being picked up (worked up to now just before): what it holds and its spare gaskets go to the player.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("DELETE FROM world.plot_wells WHERE object_id = @id RETURNING stored, gaskets, kind", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        int stored, gaskets;
        string kind;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            (stored, gaskets, kind) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2));
        }

        if (stored > 0)
        {
            await inventory.AddAsync(owner, OilRules.Product(OilRules.FieldOf(kind)), stored, cancellationToken);
        }

        if (gaskets > 0)
        {
            await inventory.AddAsync(owner, Items.Gasket, gaskets, cancellationToken);
        }
    }

    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Items.IsWell(kind) || await objects.PlotOfAsync(id, cancellationToken) is not var (x, y))
        {
            return null;
        }

        return (await OpenAsync(x, y, cancellationToken)).FirstOrDefault(w => w.Object.Id == id) is { } well ? new Port(this, x, y, well) : null;
    }

    public async Task<IReadOnlyList<MachineStatus>> StatusesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var plotsOf = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync("SELECT DISTINCT x, y FROM world.plot_wells WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                plotsOf.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        var statuses = new List<MachineStatus>();
        foreach (var (x, y) in plotsOf)
        {
            statuses.AddRange(await OnPlotAsync(x, y, owner, cancellationToken));
        }

        return statuses;
    }

    public Task<IReadOnlyList<MachineStatus>> StatusesOnPlotAsync(int x, int y, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        OnPlotAsync(x, y, null, cancellationToken);

    /// <summary>The plot's wells, only <paramref name="owner"/>'s when given.</summary>
    private async Task<IReadOnlyList<MachineStatus>> OnPlotAsync(int x, int y, Guid? owner, CancellationToken cancellationToken)
    {
        var statuses = new List<MachineStatus>();
        var now = time.GetUtcNow();
        foreach (var opened in (await OpenAsync(x, y, cancellationToken)).Where(w => owner is null || w.Object.Owner == owner))
        {
            var well = await AdvanceAsync(x, y, opened, cancellationToken);
            var activity = well.Patch.Full ? MachineActivity.OutputFull
                : well.Clock.Speed(now) <= 0 ? MachineActivity.NoPower
                : well.Left <= 0 ? MachineActivity.NoInput
                : MachineActivity.Working;
            statuses.Add(new MachineStatus(well.Object.Id, well.Object.Kind, well.Patch.Product, activity, []));
        }

        return statuses;
    }

    private async Task<WellOnPlot> WorkedAsync(int x, int y, Guid id, IEnumerable<IPlotAutomation> automation, CancellationToken cancellationToken)
    {
        foreach (var machinery in automation)
        {
            if (!ReferenceEquals(machinery, this))
            {
                await machinery.CatchUpAsync(x, y, cancellationToken);
            }
        }

        var well = (await OpenAsync(x, y, cancellationToken)).FirstOrDefault(w => w.Object.Id == id)
            ?? throw new GameException("no_object", "There is no such well on this plot.");
        return await AdvanceAsync(x, y, well, cancellationToken);
    }

    /// <summary>Works a well up to now and writes it back with what it took from its deposit.</summary>
    private async Task<WellOnPlot> AdvanceAsync(int x, int y, WellOnPlot well, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var left = await LeftAsync(x, y, well.Field, well.Reserve, cancellationToken);
        var pumped = well.Patch.Advance(now, well.Clock.At(now), left);
        await SaveAsync(x, y, well, pumped, cancellationToken);
        return well with { Left = left - pumped };
    }

    private async Task<long> LeftAsync(int x, int y, OilField field, long reserve, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT pumped FROM world.oil_fields WHERE x = @x AND y = @y AND field = @field FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("field", (short)field);
        var pumped = await command.ExecuteScalarAsync(cancellationToken) is long p ? p : 0;
        return Math.Max(0, reserve - pumped);
    }

    /// <summary>The wells of a plot, locked until the transaction ends.</summary>
    private async Task<List<WellOnPlot>> OpenAsync(int x, int y, CancellationToken cancellationToken)
    {
        var all = await objects.ObjectsAsync(x, y, cancellationToken);
        var placed = all.Where(o => Items.IsWell(o.Kind)).ToDictionary(o => o.Id);
        if (placed.Count == 0)
        {
            return [];
        }

        var rows = new List<(Guid Id, int Stored, DateTimeOffset? StartedAt, int Wear, int Gaskets, bool Leaking)>();
        await using (var command = await db.CommandAsync(
            "SELECT object_id, stored, started_at, wear, gaskets, leaking FROM world.plot_wells WHERE x = @x AND y = @y ORDER BY object_id FOR UPDATE",
            cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetBoolean(5)));
            }
        }

        var survey = await surveys.FindAsync(x, y, cancellationToken) ?? new Survey(DepositSize.None, DepositSize.None);
        var injector = all.Any(o => o.Kind == Items.WaterInjector);
        var leaking = rows.Where(r => r.Leaking).Select(r => r.Id).ToHashSet();
        var result = new List<WellOnPlot>();
        foreach (var row in rows)
        {
            if (!placed.TryGetValue(row.Id, out var o))
            {
                continue;
            }

            var field = OilRules.FieldOf(o.Kind);
            var size = OilRules.Size(survey, field);
            var patch = new WellPatch(o.Kind, size, row.Stored, row.StartedAt, row.Wear, row.Gaskets, row.Leaking);
            var clock = RunsOnPower(o.Kind) ? await power.ClockAsync(o.Id, cancellationToken) : SteadyClock.Instance;
            var reserve = OilRules.Reserve(field, size, injector);
            result.Add(new WellOnPlot(o, field, patch, clock, reserve, reserve, Separated(all, o)));
        }

        return result;
    }

    /// <summary>Wells that run on power: the pumpjack and the offshore platform; a gas well flows by itself.</summary>
    private static bool RunsOnPower(string kind) => kind != Items.GasWell;

    private async Task SaveAsync(int x, int y, WellOnPlot well, int pumped, CancellationToken cancellationToken)
    {
        var patch = well.Patch;
        if (patch.Changed)
        {
            await using var update = await db.CommandAsync(
                """
                UPDATE world.plot_wells SET stored = @stored, started_at = @started, wear = @wear, gaskets = @gaskets, leaking = @leaking
                WHERE object_id = @id
                """, cancellationToken);
            update.Parameters.AddWithValue("id", well.Object.Id);
            update.Parameters.AddWithValue("stored", patch.Stored);
            update.Parameters.Add(new Npgsql.NpgsqlParameter("started", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)patch.StartedAt ?? DBNull.Value });
            update.Parameters.AddWithValue("wear", patch.Wear);
            update.Parameters.AddWithValue("gaskets", patch.Gaskets);
            update.Parameters.AddWithValue("leaking", patch.Leaking);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (pumped > 0)
        {
            await using var field = await db.CommandAsync(
                """
                INSERT INTO world.oil_fields (x, y, field, pumped) VALUES (@x, @y, @field, @pumped)
                ON CONFLICT (x, y, field) DO UPDATE SET pumped = world.oil_fields.pumped + @pumped
                """, cancellationToken);
            field.Parameters.AddWithValue("x", x);
            field.Parameters.AddWithValue("y", y);
            field.Parameters.AddWithValue("field", (short)well.Field);
            field.Parameters.AddWithValue("pumped", (long)pumped);
            await field.ExecuteNonQueryAsync(cancellationToken);
        }

        if (patch.Made.Count > 0)
        {
            await stats.RecordAsync(well.Object.Owner, patch.Made.ToList(), cancellationToken);
            patch.Made.Clear();
        }
    }

    /// <summary>A well as pumps see it: oil or gas comes out, gaskets go in.</summary>
    private sealed class Port(OilService service, int x, int y, WellOnPlot well) : IMachinePort
    {
        private long _left = well.Left;
        private int _pumped;

        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Advance(now);
            return well.Patch.Stored > 0 && wanted(well.Patch.Product) ? well.Patch.Product : null;
        }

        public void Take(string item, DateTimeOffset now)
        {
            Advance(now);
            well.Patch.Take(1, well.Clock.At(now));
        }

        public bool Accepts(string item, DateTimeOffset now) => item == Items.Gasket && (well.Patch.Leaking || well.Patch.Gaskets < OilRules.MaxGaskets);

        public void Put(string item, DateTimeOffset now)
        {
            Advance(now);
            well.Patch.Seal(1);
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            Advance(now);
            return well.Patch.NextChange(now, well.Clock, _left);
        }

        public Task SaveAsync(CancellationToken cancellationToken)
        {
            var pumped = _pumped;
            _pumped = 0;
            return service.SaveAsync(x, y, well, pumped, cancellationToken);
        }

        private void Advance(DateTimeOffset now)
        {
            var pumped = well.Patch.Advance(now, well.Clock.At(now), _left);
            _left -= pumped;
            _pumped += pumped;
        }
    }
}

public sealed record WellRequest(int X, int Y, Guid Id);

public abstract class WellHandler(IServiceScopeFactory scopes, TimeProvider time) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<WellRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var (well, taken, xp) = await RunAsync(
            scope.ServiceProvider.GetRequiredService<OilService>(), scope.ServiceProvider.GetServices<IPlotAutomation>(),
            client.RequireAccount(), request, cancellationToken);
        var now = time.GetUtcNow();
        var patch = well.Patch;
        return new Reply("well", new
        {
            id = well.Object.Id,
            kind = well.Object.Kind,
            name = Items.Names[well.Object.Kind],
            product = patch.Product,
            productName = Items.Names[patch.Product],
            // How big the deposit is (1-3), what it gives up in all and what is left of it.
            size = (int)patch.Size,
            reserve = well.Reserve,
            left = well.Left,
            // Seconds a unit takes with all the power it needs, and how fast it works now (0 without power).
            seconds = patch.UnitTime.TotalSeconds,
            speed = well.Clock.Speed(now),
            powered = well.Object.Kind != Items.GasWell,
            stored = patch.Stored,
            capacity = OilRules.Capacity,
            full = patch.Full,
            gaskets = patch.Gaskets,
            maxGaskets = OilRules.MaxGaskets,
            wear = patch.Wear,
            wearLimit = OilRules.WearPerGasket,
            leaking = patch.Leaking,
            // Whether its leak fouls the ground around it (no oil separator near).
            polluting = well.Polluting,
            taken = taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = xp is { } gain ? new { xp = gain.Xp, level = gain.Level, leveledUp = gain.LeveledUp } : null,
            serverTime = now.ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<(WellOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        OilService oil, IEnumerable<IPlotAutomation> automation, Guid player, WellRequest request, CancellationToken cancellationToken);
}

public sealed class GetWellHandler(IServiceScopeFactory scopes, TimeProvider time) : WellHandler(scopes, time)
{
    public override string Type => "well.get";

    protected override async Task<(WellOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        OilService oil, IEnumerable<IPlotAutomation> automation, Guid player, WellRequest request, CancellationToken cancellationToken) =>
        (await oil.StateAsync(player, request.X, request.Y, request.Id, automation, cancellationToken), [], null);
}

public sealed class TakeFromWellHandler(IServiceScopeFactory scopes, TimeProvider time) : WellHandler(scopes, time)
{
    public override string Type => "well.take";

    protected override Task<(WellOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        OilService oil, IEnumerable<IPlotAutomation> automation, Guid player, WellRequest request, CancellationToken cancellationToken) =>
        oil.TakeAsync(player, request.X, request.Y, request.Id, automation, cancellationToken);
}

public sealed class SealWellHandler(IServiceScopeFactory scopes, TimeProvider time) : WellHandler(scopes, time)
{
    public override string Type => "well.seal";

    protected override Task<(WellOnPlot, IReadOnlyList<(string Item, int Quantity)>, XpGain?)> RunAsync(
        OilService oil, IEnumerable<IPlotAutomation> automation, Guid player, WellRequest request, CancellationToken cancellationToken) =>
        oil.SealAsync(player, request.X, request.Y, request.Id, automation, cancellationToken);
}
