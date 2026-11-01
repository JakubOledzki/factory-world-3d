namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>Pieces of an item a player's factory made or used up at one moment, for the production statistics.</summary>
public sealed record ProductionEntry(string Item, long Produced, long Consumed, DateTimeOffset At)
{
    public static ProductionEntry Made(string item, long quantity, DateTimeOffset at) => new(item, quantity, 0, at);

    public static ProductionEntry Used(string item, long quantity, DateTimeOffset at) => new(item, 0, quantity, at);

    /// <summary><paramref name="count"/> batches of <paramref name="quantity"/> each, the first done at
    /// <paramref name="first"/> and the next ones every <paramref name="step"/>.</summary>
    public static IEnumerable<ProductionEntry> Batches(
        string item, long quantity, bool made, DateTimeOffset first, TimeSpan step, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var at = first + step * i;
            yield return made ? Made(item, quantity, at) : Used(item, quantity, at);
        }
    }
}

/// <summary>What each player's factory makes and uses up over time: machines, the workbench, gathering on plots.</summary>
public interface IProductionStats
{
    /// <summary>Counts the entries; call it in the transaction that saves the work, so it counts only once that is saved.</summary>
    Task RecordAsync(Guid owner, IReadOnlyCollection<ProductionEntry> entries, CancellationToken cancellationToken);
}

/// <summary>What a machine is doing now. <paramref name="Missing"/> lists what it waits for when it stands idle.</summary>
/// <param name="Product">What it makes (its recipe), or null when nothing is set yet.</param>
/// <param name="Fired">Whether it burns fuel, so smoke rises from it while it works.</param>
public sealed record MachineStatus(Guid Id, string Kind, string? Product, MachineActivity Activity, IReadOnlyList<string> Missing, bool Fired = false);

public enum MachineActivity
{
    Working,

    /// <summary>Not enough of an ingredient (or ore) for the next batch.</summary>
    NoInput,

    /// <summary>A fired machine with nothing left to burn.</summary>
    NoFuel,

    /// <summary>Its products fill it up; they must be taken out.</summary>
    OutputFull,

    /// <summary>Nothing set to make.</summary>
    NoRecipe,

    /// <summary>A machine that runs on power has none: no pole reaches it, or its generators burnt out.</summary>
    NoPower,
}

/// <summary>Offered by each module that keeps machines (furnaces, mills...), so the statistics can show where the factory stalls.</summary>
public interface IMachineStatuses
{
    Task<IReadOnlyList<MachineStatus>> StatusesAsync(Guid owner, CancellationToken cancellationToken);

    /// <summary>What each machine on one plot is doing, whoever owns it; <paramref name="ids"/> are the objects standing there.</summary>
    Task<IReadOnlyList<MachineStatus>> StatusesOnPlotAsync(int x, int y, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
