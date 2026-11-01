namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>One value of a gauge: its labels (name and value) and the number.</summary>
public sealed record GaugeSample(IReadOnlyList<KeyValuePair<string, string>> Labels, double Value)
{
    public GaugeSample(double value, params (string Name, string Value)[] labels)
        : this(labels.Select(l => KeyValuePair.Create(l.Name, l.Value)).ToList(), value)
    {
    }
}

/// <summary>A gauge for <c>/metrics</c>: what the game looks like now, e.g. the money in wallets or the open market offers.</summary>
/// <param name="Name">The Prometheus name, starting with <c>factoryworld_</c>.</param>
/// <param name="Help">What it measures, in English.</param>
public sealed record Gauge(string Name, string Help, IReadOnlyList<GaugeSample> Samples);

/// <summary>
/// The state of a module's part of the game for Grafana. Each module that keeps something worth watching may offer
/// one: <c>services.AddScoped&lt;IGameGauges, MyGauges&gt;()</c>. Read at most once a minute and only from the module's own
/// tables; keep the labels few (no per-account labels, except a short ranking), since each combination is a series.
/// Players and bots are told apart with <see cref="OnlinePlayers.IsBot"/> and the <c>who</c> label
/// (<see cref="GameCounters.Who"/>).
/// </summary>
public interface IGameGauges
{
    Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken);
}
