using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Characters;

/// <summary>How far players and bots have come: how many characters there are, their levels and the ten highest of each.</summary>
public sealed class CharacterGauges(GameDb db, OnlinePlayers online) : IGameGauges
{
    /// <summary>Lowest level of each band of the level spread, so the gauge has a few series and not a hundred.</summary>
    public static readonly int[] Bands = [1, 2, 5, 10, 15, 20, 30, 50];

    public const int TopCount = 10;

    public async Task<IReadOnlyList<Gauge>> ReadAsync(CancellationToken cancellationToken)
    {
        var characters = new List<(Guid Account, string Name, long Xp, bool Bot)>();
        await using (var command = await db.CommandAsync("""SELECT "AccountId", "Name", "Xp" FROM characters.characters""", cancellationToken))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var account = reader.GetGuid(0);
                characters.Add((account, reader.GetString(1), reader.GetInt64(2), online.IsBot(account)));
            }
        }

        var count = new List<GaugeSample>();
        var average = new List<GaugeSample>();
        var highest = new List<GaugeSample>();
        var spread = new List<GaugeSample>();
        var top = new List<GaugeSample>();
        foreach (var bot in new[] { false, true })
        {
            var who = GameCounters.Who(bot);
            var levels = characters.Where(c => c.Bot == bot).Select(c => Leveling.LevelForXp(c.Xp)).ToList();
            count.Add(new(levels.Count, ("who", who)));
            average.Add(new(levels.Count == 0 ? 0 : levels.Average(), ("who", who)));
            highest.Add(new(levels.Count == 0 ? 0 : levels.Max(), ("who", who)));
            for (var i = 0; i < Bands.Length; i++)
            {
                var from = Bands[i];
                var to = i + 1 < Bands.Length ? Bands[i + 1] - 1 : Leveling.MaxLevel;
                spread.Add(new(levels.Count(l => l >= from && l <= to), ("who", who), ("levels", Band(i))));
            }

            top.AddRange(characters.Where(c => c.Bot == bot && c.Xp > 0).OrderByDescending(c => c.Xp).ThenBy(c => c.Account).Take(TopCount)
                .Select(c => new GaugeSample(Leveling.LevelForXp(c.Xp), ("who", who), ("name", c.Name))));
        }

        return
        [
            new("factoryworld_characters", "Characters made, players apart from bots.", count),
            new("factoryworld_character_level_average", "Average character level.", average),
            new("factoryworld_character_level_max", "Highest character level.", highest),
            new("factoryworld_characters_by_level", "Characters in each band of levels (label levels, e.g. 5-9).", spread),
            new("factoryworld_top_level", $"Level of the {TopCount} most experienced characters, by name.", top),
        ];
    }

    /// <summary>The label of band <paramref name="i"/>: "1", "2-4", ..., "50+".</summary>
    public static string Band(int i)
    {
        var from = Bands[i];
        if (i + 1 == Bands.Length)
        {
            return $"{from}+";
        }

        var to = Bands[i + 1] - 1;
        return from == to ? $"{from}" : $"{from}-{to}";
    }
}
