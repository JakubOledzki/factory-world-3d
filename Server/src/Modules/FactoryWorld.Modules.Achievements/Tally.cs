using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Achievements;

/// <summary>A request that a handler answered without an error, as the player sent it and as it was answered.</summary>
public sealed record HandledMessage(string Type, JsonElement? Request, string ReplyType, JsonElement Reply);

/// <summary>
/// Reads what a player did from the requests the server answered and their replies, as the protocol
/// describes them, so the modules that run the game need not know about achievements.
/// </summary>
public static class Tally
{
    /// <summary>Stat → amount the message adds (or, for <see cref="Stats.IsHighest"/>, the value it shows now).</summary>
    public static Dictionary<string, long> Count(HandledMessage message)
    {
        var counts = new Dictionary<string, long>();
        var reply = message.Reply;
        if (reply.ValueKind != JsonValueKind.Object)
        {
            return counts;
        }

        switch (message.Type)
        {
            case "plot.harvest":
                // A bridge laid over a crevasse gives nothing but the bridge.
                if (Text(reply, "object") == "bridge")
                {
                    Add(counts, Stats.Bridges, 1);
                }

                foreach (var (item, quantity) in Stacks(reply, "items"))
                {
                    if (item is Items.Wood or Items.OakWood)
                    {
                        Add(counts, Stats.Wood, quantity);
                    }

                    if (item == Items.OakWood)
                    {
                        Add(counts, Stats.OakWood, quantity);
                    }

                    if (item == Items.Mushrooms)
                    {
                        Add(counts, Stats.Mushrooms, quantity);
                    }

                    if (Items.IsFossil(item))
                    {
                        Add(counts, Stats.Fossils, quantity);
                    }

                    if (item == Items.DinosaurBone)
                    {
                        Add(counts, Stats.DinosaurBones, quantity);
                    }

                    if (item == Items.Ice)
                    {
                        Add(counts, Stats.Ice, quantity);
                    }

                    if (item == Items.MeteoricIron)
                    {
                        Add(counts, Stats.Meteorites, 1);
                    }

                    Add(counts, Stats.Produced, quantity);
                }

                break;
            case "plot.mine":
                Add(counts, Stats.Mined, 1);
                foreach (var (item, quantity) in Stacks(reply, "items"))
                {
                    if (item == Items.GoldOre)
                    {
                        Add(counts, Stats.GoldOre, quantity);
                    }

                    Add(counts, Stats.Produced, quantity);
                }

                break;
            case "album.claim" when Flag(reply, "claimed"):
                Add(counts, Stats.AlbumPages, 1);
                break;
            case "expedition.claim":
                Add(counts, Stats.Expeditions, 1);
                break;
            case "puzzle.submit":
                if (Flag(reply, "firstSolve"))
                {
                    Add(counts, Stats.Puzzles, 1);
                }

                if (Flag(reply, "firstGold"))
                {
                    Add(counts, Stats.PuzzleGold, 1);
                }

                break;
            case "mentor.claim" when Text(reply, "role") == "mentor":
                Add(counts, Stats.Mentored, 1);
                break;
            case "plot.plantTree":
                Add(counts, Stats.TreesPlanted, 1);
                break;
            case "plot.till":
                Add(counts, Stats.Fields, 1);
                break;
            case "plot.reap":
                foreach (var (_, quantity) in Stacks(reply, "items"))
                {
                    Add(counts, Stats.Crops, quantity);
                    Add(counts, Stats.Produced, quantity);
                }

                break;
            case "furnace.take" or "furnace.load":
                // Ingots come out when taken, and when another ore goes in.
                foreach (var (item, quantity) in Stacks(reply, "taken"))
                {
                    if (item.EndsWith("_ingot", StringComparison.Ordinal))
                    {
                        Add(counts, Stats.Ingots, quantity);
                        Add(counts, Stats.Produced, quantity);
                    }
                }

                break;
            case "machine.take" when Text(message.Request, "what") == "output":
                foreach (var (item, quantity) in Stacks(reply, "taken"))
                {
                    Add(counts, Stats.Processed, quantity);
                    Add(counts, Stats.Produced, quantity);
                    if (item == Items.Glass)
                    {
                        Add(counts, Stats.Glass, quantity);
                    }

                    if (item == Items.Takings)
                    {
                        Add(counts, Stats.Tourists, quantity);
                    }

                    if (item is Items.Chair or Items.Table or Items.Wardrobe or Items.OakDresser)
                    {
                        Add(counts, Stats.Furniture, quantity);
                    }
                }

                break;
            case "meteor.dig":
                foreach (var (_, quantity) in Stacks(reply, "items"))
                {
                    Add(counts, Stats.Produced, quantity);
                }

                Add(counts, Stats.Meteorites, 1);
                break;
            case "hive.take":
                foreach (var (item, quantity) in Stacks(reply, "taken"))
                {
                    Add(counts, Stats.Produced, quantity);
                    if (item == Items.Honey)
                    {
                        Add(counts, Stats.Honey, quantity);
                    }
                }

                break;
            case "crafting.get" or "crafting.craft" or "crafting.cancel":
                foreach (var (_, quantity) in Stacks(reply, "done"))
                {
                    Add(counts, Stats.Crafted, quantity);
                    Add(counts, Stats.Produced, quantity);
                }

                break;
            case "plot.place":
                Add(counts, Stats.Placed, 1);
                if (reply.TryGetProperty("object", out var placed) && Text(placed, "kind") is { } placedKind && Items.IsBelt(placedKind) && placedKind != Items.Pipe)
                {
                    Add(counts, Stats.Belts, 1);
                }

                break;
            case "station.send":
                Add(counts, Stats.Trains, 1);
                break;
            case "city.deliver" or "city.trash" when reply.TryGetProperty("result", out var city) && city.ValueKind == JsonValueKind.Object
                && city.TryGetProperty("quantity", out var moved) && moved.TryGetInt64(out var pieces):
                Add(counts, message.Type == "city.deliver" ? Stats.CityDeliveries : Stats.CityTrash, pieces);
                break;
            case "world.buy":
                Add(counts, Stats.Plots, 1);
                break;
            case "world.survey":
                Add(counts, Stats.Surveys, 1);
                break;
            case "market.sell":
                if (reply.TryGetProperty("quantity", out var sold) && sold.TryGetInt64(out var n))
                {
                    Add(counts, Stats.Sold, n);
                }

                break;
            case "market.purchase" or "market.fill":
                Add(counts, Stats.Trades, 1);
                break;
            case "contracts.deliver" when reply.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True:
                Add(counts, Stats.Contracts, 1);
                break;
        }

        // Many replies show the level (after XP was gained) or the balance; keep the highest seen.
        if (reply.TryGetProperty("xp", out var xp) && xp.ValueKind == JsonValueKind.Object
            && xp.TryGetProperty("level", out var level) && level.TryGetInt64(out var l))
        {
            counts[Stats.Level] = l;
        }
        else if (message.ReplyType == "character" && reply.TryGetProperty("level", out level) && level.TryGetInt64(out l))
        {
            counts[Stats.Level] = l;
        }

        // A town's hall shows whether the player is its mayor; a shop how much it sold in all.
        if (message.ReplyType == "city" && reply.TryGetProperty("state", out var town) && town.ValueKind == JsonValueKind.Object
            && town.TryGetProperty("hall", out var hall) && hall.ValueKind == JsonValueKind.Object
            && hall.TryGetProperty("isMayor", out var isMayor) && isMayor.ValueKind == JsonValueKind.True)
        {
            counts[Stats.Mayor] = 1;
        }

        if (message.ReplyType == "shop" && reply.TryGetProperty("soldItems", out var shopSold) && shopSold.TryGetInt64(out var soldItems))
        {
            counts[Stats.ShopSold] = soldItems;
        }

        if (reply.TryGetProperty("balanceCents", out var balance) && balance.TryGetInt64(out var cents))
        {
            counts[Stats.Money] = cents;
        }

        return counts;
    }

    private static void Add(Dictionary<string, long> counts, string stat, long amount)
    {
        if (amount > 0)
        {
            counts[stat] = counts.GetValueOrDefault(stat) + amount;
        }
    }

    /// <summary>Items listed as [{ id, quantity }] under <paramref name="property"/>.</summary>
    private static IEnumerable<(string Item, long Quantity)> Stacks(JsonElement reply, string property)
    {
        if (!reply.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (Text(entry, "id") is { } item && entry.TryGetProperty("quantity", out var q) && q.TryGetInt64(out var quantity))
            {
                yield return (item, quantity);
            }
        }
    }

    private static bool Flag(JsonElement reply, string property) =>
        reply.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
