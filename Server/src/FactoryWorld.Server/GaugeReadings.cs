using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Server;

/// <summary>
/// The modules' gauges (<see cref="IGameGauges"/>) for <c>/metrics</c>, read anew at most every <see cref="KeptFor"/>
/// so that scrapes, every 30 s, do not query the database each time. A module whose gauges fail is left out of that
/// reading and logged; the rest still go out.
/// </summary>
public sealed class GaugeReadings(IServiceScopeFactory scopes, TimeProvider time, ILogger<GaugeReadings> logger)
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromSeconds(55);

    private readonly SemaphoreSlim _reading = new(1, 1);
    private IReadOnlyList<Gauge> _gauges = [];
    private DateTimeOffset? _at;

    public async Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        await _reading.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (_at is { } at && now - at < KeptFor)
            {
                return _gauges;
            }

            var gauges = new List<Gauge>();
            await using var scope = scopes.CreateAsyncScope();
            foreach (var source in scope.ServiceProvider.GetServices<IGameGauges>())
            {
                try
                {
                    gauges.AddRange(await source.ReadAsync(cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Gauges of {Source} could not be read", source.GetType().Name);
                }
            }

            (_gauges, _at) = (gauges, now);
            return gauges;
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary>The next <see cref="ReadAsync"/> reads anew, e.g. in tests right after a change.</summary>
    public void Forget()
    {
        _reading.Wait();
        _at = null;
        _reading.Release();
    }
}
