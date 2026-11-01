using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// What a bot is like, from 0 to 1 each, worked out from its name so it stays the same on every start: how far it means
/// to grow (plots, factories), how much it cares for its town and country (votes, offices), how keen it is on money made
/// from money (banks, power, ports), how much it dares (smuggling, the fence, bribes) and how much it likes to work with
/// others (companies). It keeps two thousand bots from all running for mayor or smuggling at once.
/// </summary>
public sealed record BotTraits(double Ambition, double Civic, double Greed, double Risk, double Loyalty)
{
    public static BotTraits Of(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("traits:" + name));
        double At(int i) => BitConverter.ToUInt16(hash, i * 2) / (double)ushort.MaxValue;
        return new BotTraits(At(0), At(1), At(2), At(3), At(4));
    }
}

/// <summary>What a bot has set its mind on, and since when; it lasts across sessions and restarts.</summary>
/// <param name="Kind">One of <see cref="BotGoals"/>' kinds.</param>
/// <param name="Cents">What the bot saves up for it: money it does not spend on goods meanwhile.</param>
/// <param name="Target">What exactly: a plot's coordinates, a town, a company's name.</param>
public sealed record BotGoal(string Kind, long Cents, string? Target, DateTimeOffset Since);

/// <summary>
/// A bot's goals: the long plans a player makes between sessions (save up for the next plot, found a company, run for
/// mayor, open a bank). Each goal holds back the money it needs, so the bot saves for it instead of spending it on goods.
/// Kept as JSON in <c>bots.bots.goals</c>.
/// </summary>
public sealed class BotGoals
{
    /// <summary>Saving up for the next plot.</summary>
    public const string Plot = "plot";

    /// <summary>Founding a company, or finding one to join.</summary>
    public const string Company = "company";

    /// <summary>Running for an office: mayor or president.</summary>
    public const string Office = "office";

    /// <summary>Putting up a bank of its company.</summary>
    public const string Bank = "bank";

    /// <summary>Putting up a power plant to sell power from.</summary>
    public const string Power = "power";

    /// <summary>Putting up a rail port or a buying market.</summary>
    public const string Port = "port";

    private readonly Dictionary<string, BotGoal> _goals = new(StringComparer.Ordinal);

    public IReadOnlyCollection<BotGoal> All => _goals.Values;

    /// <summary>Money the goals hold back.</summary>
    public long SavingCents => _goals.Values.Sum(g => Math.Max(g.Cents, 0));

    public BotGoal? this[string kind] => _goals.GetValueOrDefault(kind);

    /// <summary>Whether the goals changed since they were last saved.</summary>
    public bool Changed { get; private set; }

    public void Set(BotGoal goal)
    {
        if (_goals.GetValueOrDefault(goal.Kind) != goal)
        {
            _goals[goal.Kind] = goal;
            Changed = true;
        }
    }

    public void Drop(string kind) => Changed |= _goals.Remove(kind);

    public string Save()
    {
        Changed = false;
        return JsonSerializer.Serialize(_goals.Values.OrderBy(g => g.Kind, StringComparer.Ordinal));
    }

    public static BotGoals Load(string? json)
    {
        var goals = new BotGoals();
        if (string.IsNullOrEmpty(json))
        {
            return goals;
        }

        try
        {
            foreach (var goal in JsonSerializer.Deserialize<List<BotGoal>>(json) ?? [])
            {
                goals._goals[goal.Kind] = goal;
            }
        }
        catch (JsonException)
        {
            // A goal of an older shape: the bot makes up its mind again.
        }

        return goals;
    }
}
