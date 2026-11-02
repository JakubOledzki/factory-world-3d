using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>
/// The station as inserters see it: anything can be put in (up to <see cref="RailRules.InserterStockLimit"/>
/// of one item) and anything taken out, every kind in turn. Trains that arrive while the plot's
/// machinery runs unload at the moment they arrive.
/// </summary>
public sealed class StationPorts(GameDb db, TimeProvider time) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (kind != RailRules.StationKind || RailRules.StationAt(id) is not var (x, y))
        {
            return null;
        }

        await StationStock.LockAsync(db, x, y, cancellationToken);
        var stock = await StationStock.ReadAsync(db, x, y, cancellationToken);
        var arriving = new List<(long Id, string Item, long Quantity, DateTimeOffset At)>();
        await using (var command = await db.CommandAsync(
            "SELECT id, item, quantity, arrives_at FROM world.shipments WHERE to_x = @x AND to_y = @y AND arrives_at <= @now ORDER BY arrives_at",
            cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                arriving.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        return new Port(db, x, y, stock, arriving);
    }

    private sealed class Port(
        GameDb db, int x, int y, Dictionary<string, long> stock, List<(long Id, string Item, long Quantity, DateTimeOffset At)> arriving)
        : IMachinePort
    {
        private readonly Dictionary<string, long> _changes = [];
        private readonly List<long> _arrived = [];
        private int _next;

        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Arrive(now);
            var kinds = stock.Where(s => s.Value > 0).Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
            for (var i = 0; i < kinds.Count; i++)
            {
                var item = kinds[(_next + i) % kinds.Count];
                if (wanted(item))
                {
                    return item;
                }
            }

            return null;
        }

        public void Take(string item, DateTimeOffset now)
        {
            if (stock.GetValueOrDefault(item) > 0)
            {
                Change(item, -1);
                _next++;
            }
        }

        public bool Accepts(string item, DateTimeOffset now)
        {
            Arrive(now);
            return stock.GetValueOrDefault(item) < RailRules.InserterStockLimit;
        }

        public void Put(string item, DateTimeOffset now) => Change(item, 1);

        public DateTimeOffset? NextChange(DateTimeOffset now) =>
            arriving.Where(a => a.At > now).Select(a => (DateTimeOffset?)a.At).FirstOrDefault();

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_arrived.Count > 0)
            {
                await using var delete = await db.CommandAsync("DELETE FROM world.shipments WHERE id = ANY(@ids)", cancellationToken);
                delete.Parameters.AddWithValue("ids", _arrived.ToArray());
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var (item, change) in _changes.Where(c => c.Value != 0))
            {
                await StationStock.ChangeAsync(db, x, y, item, change, cancellationToken);
            }
        }

        private void Arrive(DateTimeOffset now)
        {
            while (arriving.Count > 0 && arriving[0].At <= now)
            {
                Change(arriving[0].Item, arriving[0].Quantity);
                _arrived.Add(arriving[0].Id);
                arriving.RemoveAt(0);
            }
        }

        private void Change(string item, long change)
        {
            stock[item] = stock.GetValueOrDefault(item) + change;
            _changes[item] = _changes.GetValueOrDefault(item) + change;
        }
    }
}

/// <summary>Sends the trains of routes, and moves freight trains between plots, when they are due, whether or not anyone looks at the stations.</summary>
public sealed class RailwayDispatcher(IServiceScopeFactory scopes, ILogger<RailwayDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Many due at once (e.g. after a restart) go out a batch per moment.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<StationService>().RunDueRoutesAsync(200, stoppingToken);
                // Freight trains between a player's plots too.
                await scope.ServiceProvider.GetRequiredService<FreightService>().RunDueAsync(200, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Sending route trains failed");
            }
        }
    }
}
