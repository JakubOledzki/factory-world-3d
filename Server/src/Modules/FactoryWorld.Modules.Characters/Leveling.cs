namespace FactoryWorld.Modules.Characters;

/// <summary>Total XP to reach level L is 200 × (L − 1)² + 4 × (L − 1)³: level 2 at 204 XP, level 10 at 19 116 XP,
/// level 100 at 5 841 396 XP, about three months of play for a player with a large factory.</summary>
public static class Leveling
{
    public const int MaxLevel = 100;

    public static long TotalXpForLevel(int level)
    {
        long n = level - 1;
        return 200 * n * n + 4 * n * n * n;
    }

    public static int LevelForXp(long xp)
    {
        // The table is short and grows fast, so a binary search over the levels is exact without floating point.
        int low = 1, high = MaxLevel;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (TotalXpForLevel(mid) <= xp)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }
}
