using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Rivalry;

/// <summary>One week's contest: who does the most of one thing from Monday to Sunday.</summary>
/// <param name="One">The unit the score is counted in, after 1, 2 and 5: "sztabka", "sztabki", "sztabek".</param>
public sealed record Challenge(string Id, string Name, string Description, string One, string Few, string Many);

/// <summary>
/// The challenge of the week: every week (Monday 00:00 UTC to the next) the players compete in one thing, the next
/// one on the list each week. The three best get money and a trophy once the week is over; the server's bots take no part.
/// Every challenge counts work that takes time or that a town allows only so much of a day, never trades between
/// players, which two accounts of one person could repeat for nothing.
/// </summary>
public static class Challenges
{
    public static readonly IReadOnlyList<Challenge> All =
    [
        new("ingots", "Hutnik tygodnia", "Najwięcej sztabek wyjętych z pieców.", "sztabka", "sztabki", "sztabek"),
        new("trees", "Leśnik tygodnia", "Najwięcej drzew posadzonych ręką.", "drzewo", "drzewa", "drzew"),
        new("crafted", "Rzemieślnik tygodnia", "Najwięcej rzeczy zrobionych przy stole warsztatowym i kowadle.", "rzecz", "rzeczy", "rzeczy"),
        new("crops", "Rolnik tygodnia", "Najwięcej zebranych plonów.", "plon", "plony", "plonów"),
        new("city", "Dostawca tygodnia", "Najwięcej sztuk towaru dostarczonych miastom.", "sztuka", "sztuki", "sztuk"),
        new("mined", "Górnik tygodnia", "Najwięcej rudy i kamienia wykopanego kilofem.", "sztuka", "sztuki", "sztuk"),
        new("processed", "Przetwórca tygodnia", "Najwięcej produktów wyjętych z maszyn.", "sztuka", "sztuki", "sztuk"),
        new("wood", "Drwal tygodnia", "Najwięcej drewna ściętego na działkach.", "kłoda", "kłody", "kłód"),
        new("trash", "Sprzątacz tygodnia", "Najwięcej śmieci wywiezionych z miast.", "worek", "worki", "worków"),
    ];

    /// <summary>What the first, second and third get.</summary>
    public static readonly IReadOnlyList<long> PrizesCents = [2_000_00, 1_000_00, 500_00];

    /// <summary>Weeks start on Monday, 00:00 UTC; this is the first Monday after the Unix epoch.</summary>
    private const long FirstMonday = 4 * 86_400;

    private const long WeekSeconds = 7 * 86_400;

    public static int Week(DateTimeOffset at) => (int)Math.Floor((at.ToUnixTimeSeconds() - FirstMonday) / (double)WeekSeconds);

    public static DateTimeOffset WeekStart(int week) => DateTimeOffset.FromUnixTimeSeconds(FirstMonday + week * WeekSeconds);

    public static Challenge For(int week) => All[((week % All.Count) + All.Count) % All.Count];

    /// <summary>How much a request the server answered adds to the score of <paramref name="challenge"/>.</summary>
    public static long Score(Challenge challenge, string type, JsonElement? request, JsonElement reply)
    {
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return (challenge.Id, type) switch
        {
            // Ingots come out when taken, and when another ore goes in.
            ("ingots", "furnace.take" or "furnace.load") => Rules.Stacks(reply, "taken").Where(s => s.Item.EndsWith("_ingot", StringComparison.Ordinal)).Sum(s => s.Quantity),
            ("trees", "plot.plantTree") => 1,
            ("crafted", "crafting.get" or "crafting.craft" or "crafting.cancel") => Rules.Stacks(reply, "done").Sum(s => s.Quantity),
            ("crops", "plot.reap") => Rules.Stacks(reply, "items").Sum(s => s.Quantity),
            ("city", "city.deliver" or "city.tenderDeliver") when reply.TryGetProperty("result", out var city) && city.ValueKind == JsonValueKind.Object
                && city.TryGetProperty("quantity", out var moved) && moved.TryGetInt64(out var pieces) => pieces,
            ("mined", "plot.mine") => Rules.Stacks(reply, "items").Sum(s => s.Quantity),
            ("processed", "machine.take") when Rules.Text(request, "what") == "output" => Rules.Stacks(reply, "taken").Sum(s => s.Quantity),
            ("wood", "plot.harvest") => Rules.Stacks(reply, "items").Where(s => s.Item is Items.Wood or Items.OakWood).Sum(s => s.Quantity),
            ("trash", "city.trash") when reply.TryGetProperty("result", out var trash) && trash.ValueKind == JsonValueKind.Object
                && trash.TryGetProperty("quantity", out var bags) && bags.TryGetInt64(out var taken) => taken,
            _ => 0,
        };
    }
}

/// <summary>Reading the replies the server sent, as the protocol in the README describes them.</summary>
internal static class Rules
{
    /// <summary>Items listed as [{ id, quantity }] under <paramref name="property"/>.</summary>
    public static IEnumerable<(string Item, long Quantity)> Stacks(JsonElement reply, string property)
    {
        if (!reply.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (Text(entry, "id") is { } item && entry.TryGetProperty("quantity", out var q) && q.TryGetInt64(out var quantity) && quantity > 0)
            {
                yield return (item, quantity);
            }
        }
    }

    public static string? Text(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The character's level a reply shows (after XP was gained), if it shows one.</summary>
    public static int? Level(string replyType, JsonElement reply)
    {
        if (reply.TryGetProperty("xp", out var xp) && xp.ValueKind == JsonValueKind.Object
            && xp.TryGetProperty("level", out var level) && level.TryGetInt32(out var l))
        {
            return l;
        }

        return replyType == "character" && reply.TryGetProperty("level", out level) && level.TryGetInt32(out l) ? l : null;
    }

    /// <summary>12345 as "12 345".</summary>
    public static string Number(long n)
    {
        var digits = Math.Abs(n).ToString();
        var grouped = new System.Text.StringBuilder(n < 0 ? "-" : "");
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                grouped.Append(' ');
            }

            grouped.Append(digits[i]);
        }

        return grouped.ToString();
    }

    /// <summary>Polish noun after a number: 1 sztabka, 2 sztabki, 5 sztabek.</summary>
    public static string Count(long n, string one, string few, string many)
    {
        var form = n == 1 ? one
            : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few
            : many;
        return $"{Number(n)} {form}";
    }

    /// <summary>Cents as "1 234,50 $", like the client shows money.</summary>
    public static string Money(long cents) => $"{Number(cents / 100)},{Math.Abs(cents % 100):00} $";
}
