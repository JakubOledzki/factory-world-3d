using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Crafting;

/// <summary>
/// A works canteen stands on 3 × 3 tiles of a plot and feeds the crews of its machines. While it has food, every machine of the plot that
/// burns fuel or runs on power works <see cref="Bonus"/> times as fast. Each such machine eats one portion every <see cref="PortionTime"/>;
/// a dish gives <see cref="Portions"/> of them. The canteen keeps when its food runs out (<c>fed_until</c>): what is put in, by hand or by an
/// inserter, moves that time on by as long as its portions feed the machines the plot has then.
/// </summary>
public static class Canteens
{
    /// <summary>How much faster the plot's machines work while the canteen has food.</summary>
    public const double Bonus = 1.2;

    /// <summary>How long one portion feeds the crew of one machine.</summary>
    public static readonly TimeSpan PortionTime = TimeSpan.FromMinutes(10);

    /// <summary>How far ahead a canteen can be stocked.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromHours(48);

    /// <summary>What the canteen serves and how many portions each dish gives.</summary>
    public static readonly IReadOnlyDictionary<string, int> Portions = new Dictionary<string, int>
    {
        [Items.Bread] = 2,
        [Items.ScrambledEggs] = 2,
        [Items.Bigos] = 2,
        [Items.Fries] = 1,
        [Items.VegetableSoup] = 3,
        [Items.MushroomSoup] = 3,
        [Items.FishSoup] = 3,
        [Items.BerryPierogi] = 2,
        [Items.WorkersLunch] = 4,
    };

    /// <summary>Whether a machine of <paramref name="kind"/> has a crew the canteen feeds: one that burns fuel or runs on power.</summary>
    public static bool Eats(string kind) => Processing.ByKind.TryGetValue(kind, out var machine) && (machine.Fired || machine.Powered);

    /// <summary>How many machines of the plot the canteen feeds, never fewer than one.</summary>
    public static int Crews(IEnumerable<PlotObject> objects) => Math.Max(1, objects.Where(o => Eats(o.Kind)).Select(o => o.Id).Distinct().Count());

    /// <summary>How long <paramref name="portions"/> feed <paramref name="crews"/> machines.</summary>
    public static TimeSpan Lasts(int portions, int crews) => PortionTime * portions / crews;

    /// <summary>When the food of the canteen <paramref name="id"/> runs out, or null when it never had any.</summary>
    public static async Task<DateTimeOffset?> FedUntilAsync(GameDb db, Guid id, CancellationToken cancellationToken, bool locked = false)
    {
        await using var command = await db.CommandAsync(
            "SELECT fed_until FROM crafting.canteens WHERE object_id = @id" + (locked ? " FOR UPDATE" : ""), cancellationToken);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is DateTimeOffset at ? at : null;
    }

    /// <summary>Whether any canteen among <paramref name="objects"/> has food at <paramref name="now"/>.</summary>
    public static async Task<bool> FedAsync(GameDb db, IEnumerable<PlotObject> objects, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var id in objects.Where(o => o.Kind == Items.Canteen).Select(o => o.Id).Distinct())
        {
            if (await FedUntilAsync(db, id, cancellationToken) > now)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task WriteAsync(GameDb db, Guid owner, Guid id, DateTimeOffset fedUntil, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO crafting.canteens (object_id, owner_id, fed_until) VALUES (@id, @owner, @until)
            ON CONFLICT (object_id) DO UPDATE SET fed_until = @until
            """, cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = fedUntil });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record CanteenReply(Guid Id, DateTimeOffset? FedUntil, int Crews, DateTimeOffset ServerTime, int Served);

public sealed class CanteenService(GameDb db, IEnumerable<IPlacedObjects> places, IInventory inventory, IPlotObjects plots, TimeProvider time)
{
    public Task<CanteenReply> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await Workshop.RequireAsync(places, owner, id, Items.Canteen, cancellationToken);
            return new CanteenReply(id, await Canteens.FedUntilAsync(db, id, cancellationToken), await CrewsAsync(id, cancellationToken), time.GetUtcNow(), 0);
        }, cancellationToken);

    /// <summary>Serves dishes from the inventory: the food lasts as long as their portions feed the plot's machines.</summary>
    public Task<CanteenReply> LoadAsync(Guid owner, Guid id, string item, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            if (!Canteens.Portions.TryGetValue(item, out var portions))
            {
                throw new GameException("not_food", "The canteen does not serve this.");
            }

            await Workshop.RequireAsync(places, owner, id, Items.Canteen, cancellationToken);
            var now = time.GetUtcNow();
            var crews = await CrewsAsync(id, cancellationToken);
            var from = await Canteens.FedUntilAsync(db, id, cancellationToken, locked: true) is { } until && until > now ? until : now;
            var fedUntil = from + Canteens.Lasts(portions * quantity, crews);
            if (fedUntil - now > Canteens.MaxAhead)
            {
                throw new GameException("canteen_full", $"The canteen keeps food for at most {Canteens.MaxAhead.TotalHours:0} hours.");
            }

            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            await Canteens.WriteAsync(db, owner, id, fedUntil, cancellationToken);
            return new CanteenReply(id, fedUntil, crews, now, portions * quantity);
        }, cancellationToken);

    private async Task<int> CrewsAsync(Guid id, CancellationToken cancellationToken) =>
        await plots.PlotOfAsync(id, cancellationToken) is { } at ? Canteens.Crews(await plots.ObjectsAsync(at.X, at.Y, cancellationToken)) : 1;
}

/// <summary>The canteen as inserters see it: dishes go in, nothing comes out.</summary>
public sealed class CanteenPorts(GameDb db, IPlotObjects plots) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (kind != Items.Canteen)
        {
            return null;
        }

        var crews = await plots.PlotOfAsync(id, cancellationToken) is { } at ? Canteens.Crews(await plots.ObjectsAsync(at.X, at.Y, cancellationToken)) : 1;
        return new Port(db, owner, id, crews, await Canteens.FedUntilAsync(db, id, cancellationToken, locked: true));
    }

    private sealed class Port(GameDb db, Guid owner, Guid id, int crews, DateTimeOffset? fedUntil) : IMachinePort
    {
        private bool _changed;

        public string? Output(DateTimeOffset now, Func<string, bool> wanted) => null;

        public void Take(string item, DateTimeOffset now)
        {
        }

        public bool Accepts(string item, DateTimeOffset now) =>
            Canteens.Portions.ContainsKey(item) && (fedUntil is not { } until || until - now < Canteens.MaxAhead);

        public void Put(string item, DateTimeOffset now)
        {
            var from = fedUntil is { } until && until > now ? until : now;
            fedUntil = from + Canteens.Lasts(Canteens.Portions[item], crews);
            _changed = true;
        }

        public DateTimeOffset? NextChange(DateTimeOffset now) => null;

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_changed && fedUntil is { } until)
            {
                await Canteens.WriteAsync(db, owner, id, until, cancellationToken);
            }
        }
    }
}

/// <summary>A canteen picked up keeps nothing: what was served is eaten.</summary>
public sealed class CanteenContents(GameDb db) : IMachineContents
{
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM crafting.canteens WHERE object_id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed record CanteenRequest(Guid Canteen);

public sealed record LoadCanteenRequest(Guid Canteen, string Item, int Quantity);

public abstract class CanteenHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<CanteenService>(), client.RequireAccount(), payload, cancellationToken);
        var fed = reply.FedUntil is { } until && until > reply.ServerTime;
        return new Reply("canteen", new
        {
            id = reply.Id,
            kind = Items.Canteen,
            name = Items.Names[Items.Canteen],
            fed,
            // Unix milliseconds of when the food runs out, while there is any.
            fedUntil = fed ? reply.FedUntil!.Value.ToUnixTimeMilliseconds() : (long?)null,
            serverTime = reply.ServerTime.ToUnixTimeMilliseconds(),
            crews = reply.Crews,
            bonusPercent = (int)Math.Round((Canteens.Bonus - 1) * 100),
            portionMinutes = Canteens.PortionTime.TotalMinutes,
            maxHours = Canteens.MaxAhead.TotalHours,
            served = reply.Served,
            dishes = Canteens.Portions.Select(d => new { id = d.Key, name = Items.Names[d.Key], portions = d.Value }),
        });
    }

    protected abstract Task<CanteenReply> RunAsync(CanteenService canteens, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

public sealed class GetCanteenHandler(IServiceScopeFactory scopes) : CanteenHandler(scopes)
{
    public override string Type => "canteen.get";

    protected override Task<CanteenReply> RunAsync(CanteenService canteens, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        canteens.StateAsync(owner, Payload.Read<CanteenRequest>(payload).Canteen, cancellationToken);
}

public sealed class LoadCanteenHandler(IServiceScopeFactory scopes) : CanteenHandler(scopes)
{
    public override string Type => "canteen.load";

    protected override Task<CanteenReply> RunAsync(CanteenService canteens, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LoadCanteenRequest>(payload);
        return canteens.LoadAsync(owner, request.Canteen, request.Item ?? "", request.Quantity, cancellationToken);
    }
}
