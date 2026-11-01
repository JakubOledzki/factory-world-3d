using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Crafting;

/// <summary>
/// The compendium in the character window: every item of the game, every recipe of every station that makes something
/// (the workbench and the anvil, the furnaces, the processing machines) and what the fired machines and the canteen take in,
/// read straight from the same lists the game works with, so a new item or recipe shows up there on its own.
/// </summary>
public static class Compendium
{
    public sealed record Entry(string Item, int Quantity, double Seconds, string Station,
        IReadOnlyList<(string Item, int Quantity)> Ingredients, IReadOnlyList<(string Item, int Quantity)> Extras);

    public static readonly IReadOnlyList<Entry> Recipes = Build();

    private static List<Entry> Build()
    {
        var all = new List<Entry>();
        foreach (var r in Workshop.Recipes)
            all.Add(new(r.Id, r.Quantity, r.Time.TotalSeconds, r.Station, r.Ingredients, []));
        // Each furnace level smelts the same ore, only faster.
        foreach (var level in Smelting.Levels)
            foreach (var r in Smelting.Recipes)
                all.Add(new(r.Output, 1, level.Time(r).TotalSeconds, level.Kind, [(r.Input, 1)], []));
        foreach (var machine in Processing.Machines)
            foreach (var r in machine.Recipes)
                all.Add(new(r.Id, r.Quantity, r.Time.TotalSeconds, machine.Kind, r.Ingredients, r.Extras));
        return all;
    }
}

public sealed class GetCompendiumHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "compendium.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var locked = await scope.ServiceProvider.GetRequiredService<ITechnologies>().LockedAsync(client.RequireAccount(), cancellationToken);
        static object Pieces(IEnumerable<(string Item, int Quantity)> pieces) => pieces.Select(s => new { id = s.Item, quantity = s.Quantity });
        return new Reply("compendium", new
        {
            // With how many pieces of each fill a tile of the inventory.
            items = Items.Names.Select(n => new { id = n.Key, name = n.Value, stack = Stacks.Of(n.Key) }),
            recipes = Compendium.Recipes.Select(r => new
            {
                id = r.Item,
                quantity = r.Quantity,
                seconds = r.Seconds,
                station = r.Station,
                ingredients = Pieces(r.Ingredients),
                extras = Pieces(r.Extras),
            }),
            // What the fired machines burn, and how many batches one piece lasts; coal in a furnace lasts for its own count of smelts.
            fuels = Processing.Fuels,
            furnaceCoal = Smelting.Levels.ToDictionary(l => l.Kind, l => l.SmeltsPerCoal),
            // What the works canteen serves, and how many portions each dish gives.
            canteen = Canteens.Portions,
            // The technology still to research for each item the player cannot make or buy yet.
            locked,
        });
    }
}
