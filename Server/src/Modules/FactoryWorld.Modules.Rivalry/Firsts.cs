using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Rivalry;

/// <summary>Something only one player on the server can be the first to do.</summary>
public sealed record WorldFirst(string Id, string Name, string Description);

/// <summary>
/// "First in the world": milestones (a level, a million dollars, the first nuclear plant...) that go to the one player
/// who reaches them before anybody else, for good. Read from the requests the server answered, like the badges.
/// </summary>
public static class Firsts
{
    public const string Millionaire = "millionaire";
    public const string NuclearPlant = "nuclear_plant";
    public const string FusionPlant = "fusion_plant";
    public const string Robot = "industrial_robot";
    public const string Satellite = "satellite";
    public const string Rocket = "launch_rocket";
    public const string Metropolis = "metropolis";
    public const string WholeTree = "whole_tree";

    public static string Level(int level) => $"level_{level}";

    /// <summary>The levels that have a first of their own.</summary>
    public static readonly IReadOnlyList<int> Levels = [10, 25, 50, 100];

    public const long MillionaireCents = 1_000_000_00;

    /// <summary>The town level of a metropolis (<c>CityRules.LevelNames</c> in the world module).</summary>
    public const int MetropolisLevel = 4;

    public static readonly IReadOnlyList<WorldFirst> All =
    [
        new(Level(10), "Poziom 10", "Pierwszy gracz na serwerze z 10. poziomem postaci."),
        new(Level(25), "Poziom 25", "Pierwszy gracz na serwerze z 25. poziomem postaci."),
        new(Level(50), "Poziom 50", "Pierwszy gracz na serwerze z 50. poziomem postaci."),
        new(Level(100), "Mistrz", "Pierwszy gracz na serwerze z najwyższym, 100. poziomem postaci."),
        new(Millionaire, "Pierwszy milioner", "Pierwszy gracz z milionem dolarów w portfelu."),
        new(Metropolis, "Ojciec metropolii", "Jego dostawa pierwsza na serwerze zrobiła z miasta metropolię."),
        new(NuclearPlant, "Ujarzmiony atom", "Pierwsza elektrownia jądrowa postawiona na działce."),
        new(FusionPlant, "Słońce na Ziemi", "Pierwsza elektrownia fuzyjna postawiona na działce."),
        new(Robot, "Pierwszy robot", "Pierwszy robot przemysłowy wyjęty z maszyny albo zrobiony przy stole."),
        new(Satellite, "Pierwszy satelita", "Pierwszy satelita wyjęty z maszyny albo zrobiony przy stole."),
        new(Rocket, "Pierwsza rakieta", "Pierwsza rakieta nośna wyjęta z maszyny albo zrobiona przy stole."),
        new(WholeTree, "Wszechwiedzący", "Pierwszy gracz, który zbadał całe drzewko technologii (poza badaniami tylko dla spółek)."),
    ];

    public static readonly IReadOnlyDictionary<string, WorldFirst> ById = All.ToDictionary(f => f.Id);

    private static readonly IReadOnlyDictionary<string, string> MadeItems = new Dictionary<string, string>
    {
        [Items.IndustrialRobot] = Robot,
        [Items.Satellite] = Satellite,
        [Items.LaunchRocket] = Rocket,
    };

    private static readonly IReadOnlyDictionary<string, string> PlacedKinds = new Dictionary<string, string>
    {
        [Items.NuclearPlant] = NuclearPlant,
        [Items.FusionPlant] = FusionPlant,
    };

    /// <summary>The firsts a request the server answered shows the player reached (whether or not somebody else has them already).</summary>
    public static IReadOnlyList<string> Reached(string type, JsonElement? request, string replyType, JsonElement reply)
    {
        var reached = new List<string>();
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return reached;
        }

        switch (type)
        {
            case "plot.place" when reply.TryGetProperty("object", out var placed):
                AddKind(reached, placed);
                break;
            case "plot.placeMany" when reply.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array:
                foreach (var placed in objects.EnumerateArray())
                {
                    AddKind(reached, placed);
                }

                break;
            case "crafting.get" or "crafting.craft" or "crafting.cancel":
                AddMade(reached, reply, "done");
                break;
            case "machine.take" when Rules.Text(request, "what") == "output":
                AddMade(reached, reply, "taken");
                break;
            case "city.deliver" when reply.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("level", out var town) && town.TryGetInt32(out var townLevel) && townLevel >= MetropolisLevel:
                reached.Add(Metropolis);
                break;
        }

        if (replyType == "research" && reply.TryGetProperty("technologies", out var technologies) && technologies.ValueKind == JsonValueKind.Array)
        {
            var own = technologies.EnumerateArray()
                .Where(t => !(t.TryGetProperty("companyOnly", out var only) && only.ValueKind == JsonValueKind.True))
                .ToList();
            if (own.Count > 0 && own.All(t => Rules.Text(t, "status") == "done"))
            {
                reached.Add(WholeTree);
            }
        }

        if (Rules.Level(replyType, reply) is { } level)
        {
            reached.AddRange(Levels.Where(l => level >= l).Select(Level));
        }

        if (reply.TryGetProperty("balanceCents", out var balance) && balance.TryGetInt64(out var cents) && cents >= MillionaireCents)
        {
            reached.Add(Millionaire);
        }

        return reached;
    }

    private static void AddKind(List<string> reached, JsonElement placed)
    {
        if (Rules.Text(placed, "kind") is { } kind && PlacedKinds.TryGetValue(kind, out var first))
        {
            reached.Add(first);
        }
    }

    private static void AddMade(List<string> reached, JsonElement reply, string property)
    {
        foreach (var (item, _) in Rules.Stacks(reply, property))
        {
            if (MadeItems.TryGetValue(item, out var first))
            {
                reached.Add(first);
            }
        }
    }
}
