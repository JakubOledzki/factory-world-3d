using System.Text.Json;
using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Modules.World;

/// <summary>
/// A tree is felled with an axe (or saw) and a rock broken, or a load dug out of a mine, with a pickaxe in
/// <see cref="Count"/> strikes: the client tells of each strike but the last ("plot.hit"), and the last is the
/// request that gathers ("plot.harvest", "plot.mine"). The work's time is shared out over the strikes: no strike may
/// come sooner than its share (less a little for the network) after the one before, the first not sooner after the
/// last thing gathered. Strikes on something else, or after a long pause, start over.
/// </summary>
public static class StrikeRules
{
    public const int Count = 5;

    /// <summary>A strike longer than this after the one before starts the work over.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(15);

    /// <summary>The part of each strike's share of the work that must have passed (network jitter).</summary>
    public const double Tolerance = 0.8;
}

/// <summary>What is being struck: a tile of a plot, or a mine (<see cref="Mine"/> ≥ 0) on it.</summary>
public sealed record StrikeTarget(int X, int Y, int TileX, int TileY, int Mine = -1);

public sealed record HitRequest(int X, int Y, int TileX = -1, int TileY = -1, int Mine = -1);

/// <summary>The strikes of a player's connection so far, kept with the connection.</summary>
public static class Strikes
{
    private const string Key = "world.strikes";

    private sealed record Chain(StrikeTarget Target, List<DateTimeOffset> At);

    /// <summary>Notes a strike on <paramref name="target"/>; how many strikes it has had in a row, this one included
    /// (never more than one short of <see cref="StrikeRules.Count"/>: the last one gathers).</summary>
    public static int Hit(ClientContext client, TimeProvider time, StrikeTarget target)
    {
        var now = time.GetUtcNow();
        if (client.Items.GetValueOrDefault(Key) is not Chain chain || chain.Target != target || now - chain.At[^1] > StrikeRules.Pause)
        {
            chain = new Chain(target, []);
            client.Items[Key] = chain;
        }

        chain.At.Add(now);
        if (chain.At.Count > StrikeRules.Count - 1)
        {
            chain.At.RemoveAt(0);
        }

        return chain.At.Count;
    }

    /// <summary>The last strike, which gathers: refuses it unless the ones before it were struck on the same target, each
    /// no sooner than its share of <paramref name="work"/> after the one before. The strikes start over either way.</summary>
    public static void CheckLast(ClientContext client, TimeProvider time, StrikeTarget target, TimeSpan work)
    {
        var now = time.GetUtcNow();
        var chain = client.Items.GetValueOrDefault(Key) as Chain;
        client.Items.Remove(Key);
        if (chain is null || chain.Target != target || chain.At.Count < StrikeRules.Count - 1 || now - chain.At[^1] > StrikeRules.Pause)
        {
            throw new GameException("not_struck", $"It takes {StrikeRules.Count} strikes.");
        }

        var share = work / StrikeRules.Count * StrikeRules.Tolerance;
        var times = chain.At.Append(now).ToList();
        if (client.Items.GetValueOrDefault(HarvestHandler.LastHarvest) is DateTimeOffset last && times[0] - last < share)
        {
            throw new GameException("too_fast", "Wait until the last swing is done.");
        }

        for (var i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] < share)
            {
                throw new GameException("too_fast", "Wait until the last swing is done.");
            }
        }
    }
}

/// <summary>A strike of the axe or pickaxe that does not gather yet; the reply says how many strikes in a row it was.</summary>
public sealed class HitHandler(TimeProvider time) : IMessageHandler
{
    public string Type => "plot.hit";

    public Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        client.RequireAccount();
        var request = Payload.Read<HitRequest>(payload);
        var hits = Strikes.Hit(client, time, new StrikeTarget(request.X, request.Y, request.TileX, request.TileY, request.Mine));
        return Task.FromResult(new Reply("plot.hit", new { hits, strikes = StrikeRules.Count }));
    }
}
