using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Characters;

/// <summary>One step of the level rewards: from <paramref name="Level"/> on, work in <paramref name="Industry"/>
/// goes <paramref name="Percent"/> faster (trade: the trader pays that much more), on top of the clothes.</summary>
public sealed record LevelBonus(int Level, string Industry, int Percent);

/// <summary>What the character's level gives: every fifth level adds a bonus to one industry, the same kind clothes
/// give (<see cref="Industries"/>). By level 100 each kind of work goes 15% faster and the trader pays 5% more.</summary>
public static class LevelBonuses
{
    public static readonly IReadOnlyList<LevelBonus> All =
    [
        new(5, Industries.Forestry, 5),
        new(10, Industries.Mining, 5),
        new(15, Industries.Farming, 5),
        new(20, Industries.Trade, 1),
        new(25, Industries.Crafting, 5),
        new(30, Industries.Fishing, 5),
        new(35, Industries.Forestry, 5),
        new(40, Industries.Trade, 1),
        new(45, Industries.Mining, 5),
        new(50, Industries.Crafting, 5),
        new(55, Industries.Farming, 5),
        new(60, Industries.Trade, 1),
        new(65, Industries.Fishing, 5),
        new(70, Industries.Forestry, 5),
        new(75, Industries.Mining, 5),
        new(80, Industries.Trade, 1),
        new(85, Industries.Crafting, 5),
        new(90, Industries.Farming, 5),
        new(95, Industries.Fishing, 5),
        new(100, Industries.Trade, 1),
    ];

    /// <summary>Percent per industry reached by <paramref name="level"/>; industries with nothing yet left out.</summary>
    public static IReadOnlyDictionary<string, int> For(int level) =>
        All.Where(b => b.Level <= level)
            .GroupBy(b => b.Industry)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Percent));

    /// <summary>The bonuses of the clothes and of the level added up per industry.</summary>
    public static IReadOnlyDictionary<string, int> With(IReadOnlyDictionary<string, int> clothes, int level) =>
        clothes.Concat(For(level))
            .GroupBy(b => b.Key)
            .ToDictionary(g => g.Key, g => g.Sum(b => b.Value));
}
