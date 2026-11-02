using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>The export chest as inserters see it: anything but fluids goes in while there is room; nothing comes out.</summary>
public sealed class ExportChestPorts(GameDb db) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (kind != Items.ExportChest)
        {
            return null;
        }

        int x, y;
        await using (var command = await db.CommandAsync("SELECT x, y FROM world.export_chests WHERE object_id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            (x, y) = (reader.GetInt32(0), reader.GetInt32(1));
        }

        await ExportService.LockAsync(db, x, y, cancellationToken);
        return new Port(db, x, y, await ExportService.ReadStockAsync(db, x, y, cancellationToken));
    }

    private sealed class Port(GameDb db, int x, int y, Dictionary<string, long> stock) : IMachinePort
    {
        private readonly Dictionary<string, long> _added = [];

        public string? Output(DateTimeOffset now, Func<string, bool> wanted) => null;

        public void Take(string item, DateTimeOffset now)
        {
        }

        public bool Accepts(string item, DateTimeOffset now) => ExportService.Room(stock, item) > 0;

        public void Put(string item, DateTimeOffset now)
        {
            stock[item] = stock.GetValueOrDefault(item) + 1;
            _added[item] = _added.GetValueOrDefault(item) + 1;
        }

        public DateTimeOffset? NextChange(DateTimeOffset now) => null;

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            foreach (var (item, added) in _added)
            {
                await ExportService.ChangeStockAsync(db, x, y, item, added, cancellationToken);
            }
        }
    }
}

/// <summary>Sends the export trains and lists what they bring when it is time, whether or not anyone looks.</summary>
public sealed class ExportDispatcher(IServiceScopeFactory scopes, ILogger<ExportDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExportService>().RunDueAsync(200, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Sending export trains failed");
            }
        }
    }
}
