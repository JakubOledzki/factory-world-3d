using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Research;

/// <summary>How far players and bots got in the technology tree: on their own, without what their company researched.</summary>
public sealed class ResearchGauges(GameDb db, OnlinePlayers online, TimeProvider time) : IGameGauges
{
    public async Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        var done = new Dictionary<Guid, int>();
        var running = new Dictionary<bool, int> { [false] = 0, [true] = 0 };
        var known = new Dictionary<bool, HashSet<string>> { [false] = [], [true] = [] };
        var branches = new Dictionary<(bool Bot, string Branch), int>();
        await using (var command = await db.CommandAsync(
            "SELECT owner_id, technology, finishes_at <= @now FROM research.technologies", cancellationToken))
        {
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var owner = reader.GetGuid(0);
                var bot = online.IsBot(owner);
                if (!reader.GetBoolean(2))
                {
                    running[bot]++;
                    continue;
                }

                var technology = reader.GetString(1);
                done[owner] = done.GetValueOrDefault(owner) + 1;
                known[bot].Add(technology);
                if (TechTree.BranchOf.TryGetValue(technology, out var branch))
                {
                    branches[(bot, branch.Id)] = branches.GetValueOrDefault((bot, branch.Id)) + 1;
                }
            }
        }

        var average = new List<GaugeSample>();
        var highest = new List<GaugeSample>();
        foreach (var bot in new[] { false, true })
        {
            var counts = done.Where(d => online.IsBot(d.Key) == bot).Select(d => d.Value).ToList();
            average.Add(new(counts.Count == 0 ? 0 : counts.Average(), ("who", GameCounters.Who(bot))));
            highest.Add(new(counts.Count == 0 ? 0 : counts.Max(), ("who", GameCounters.Who(bot))));
        }

        return
        [
            new("factoryworld_technologies", "Technologies in the tree.", [new(TechTree.Technologies.Count)]),
            new("factoryworld_research_done_average", "Technologies researched per player who researched any.", average),
            new("factoryworld_research_done_max", "Most technologies one player researched.", highest),
            new("factoryworld_research_running", "Researches going on now.",
                running.Select(r => new GaugeSample(r.Value, ("who", GameCounters.Who(r.Key)))).ToList()),
            new("factoryworld_research_known", "Technologies at least one of them researched.",
                known.Select(k => new GaugeSample(k.Value.Count, ("who", GameCounters.Who(k.Key)))).ToList()),
            new("factoryworld_research_done_by_branch", "Researched technologies, summed over players, by branch of the tree.",
                branches.Select(b => new GaugeSample(b.Value, ("who", GameCounters.Who(b.Key.Bot)), ("branch", b.Key.Branch))).ToList()),
        ];
    }
}
