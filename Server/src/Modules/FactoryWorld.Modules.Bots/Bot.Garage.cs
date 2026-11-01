using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>The bot in its garage: sorting trash, crafting, research, and the furnace and workshops it works.</summary>
public sealed partial class Bot
{
    private async Task<bool> SortAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("recycling.sort", new { count = 1 }, cancellationToken);
        if (!reply.Is("garage"))
        {
            return false;
        }

        ParseGarage(reply.Payload);
        return true;
    }

    private async Task<bool> PlaceInGarageAsync(string kind, CancellationToken cancellationToken)
    {
        if (FreeGarageCell(kind) is not { } cell)
        {
            return false;
        }

        var reply = await SendAsync("garage.place", new { kind, x = cell.X, y = cell.Y, rotation = 0 }, cancellationToken);
        if (!reply.Is("garage"))
        {
            return false;
        }

        ParseGarage(reply.Payload);
        _inventory[kind] = Math.Max(0, Have(kind) - 1);
        return true;
    }

    /// <summary>A cell where a piece of furniture fits without touching anything, scanning from the front-left corner.</summary>
    private (int X, int Y)? FreeGarageCell(string kind)
    {
        if (!_footprints.TryGetValue(kind, out var size))
        {
            return null;
        }

        var room = (Width: 16, Depth: 12);
        var taken = new bool[room.Width, room.Depth];
        foreach (var (k, x, y, rotation) in _garage)
        {
            var (w, d) = _footprints.TryGetValue(k, out var f) ? (rotation % 2 == 0 ? f : (f.Depth, f.Width)) : (1, 1);
            for (var i = x - 1; i <= x + w; i++)
            {
                for (var j = y - 1; j <= y + d; j++)
                {
                    if (i >= 0 && j >= 0 && i < room.Width && j < room.Depth)
                    {
                        taken[i, j] = true;
                    }
                }
            }
        }

        for (var y = 2; y + size.Depth <= room.Depth; y++)
        {
            for (var x = 0; x + size.Width <= room.Width; x++)
            {
                var fits = true;
                for (var i = x; i < x + size.Width && fits; i++)
                {
                    for (var j = y; j < y + size.Depth && fits; j++)
                    {
                        fits = !taken[i, j];
                    }
                }

                if (fits)
                {
                    return (x, y);
                }
            }
        }

        return null;
    }

    private async Task<bool> CraftAsync(string recipe, CancellationToken cancellationToken)
    {
        var reply = await SendAsync("crafting.craft", new { workbench = _workbench, recipe }, cancellationToken);
        if (!reply.Is("crafting"))
        {
            if (reply.Error == "locked")
            {
                _recipes[recipe] = _recipes[recipe] with { Locked = true };
            }

            return false;
        }

        ParseCrafting(reply.Payload);
        await InventoryAsync(cancellationToken);
        return true;
    }

    private async Task<bool> ResearchAsync(string technology, CancellationToken cancellationToken)
    {
        var reply = await SendAsync("research.start", new { technology }, cancellationToken);
        if (!reply.Is("research"))
        {
            _techs.Remove(technology);
            return false;
        }

        await ParseResearchAsync(cancellationToken);
        await InventoryAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Looks after one machine of its workshop the way a player does: takes the products out, sets it to whatever pays best
    /// of what the bot has the ingredients for (and the market is not full of), fills it for a few batches and keeps its
    /// fire going. What the recipe it is set to still lacks the bot looks for on the market.
    /// </summary>
    private async Task<bool> WorkshopAsync(Guid id, BotWorld world, CancellationToken cancellationToken)
    {
        _workshopAt[id] = world.Now + BotRules.WorkshopEvery;
        var reply = await SendAsync("machine.get", new { machine = id }, cancellationToken);
        if (!reply.Is("machine"))
        {
            return false;
        }

        var machine = reply.Payload;
        if (machine.True("powered"))
        {
            _workshopAt[id] = world.Now + TimeSpan.FromDays(1);
            return false;
        }

        var recipes = machine.Each("recipes")
            .Where(r => r.Text("locked") is null)
            .Select(r => (Id: r.Text("id")!, Makes: r.Long("quantity"), Ingredients: r.Each("ingredients").Select(i => (Item: i.Text("id")!, Quantity: i.Long("quantity"))).ToList()))
            .ToList();
        _workshopGoods.UnionWith(recipes.Select(r => r.Id));

        var acted = false;
        if (machine.Get("output") is { ValueKind: JsonValueKind.Object } output && output.Long("quantity") > 0)
        {
            var taken = await SendAsync("machine.take", new { machine = id, what = "output" }, cancellationToken);
            if (taken.Error == "inventory_full" && await MakeRoomAsync(output.Text("id")!, cancellationToken))
            {
                taken = await SendAsync("machine.take", new { machine = id, what = "output" }, cancellationToken);
            }

            acted = taken.Ok;
            await InventoryAsync(cancellationToken);
        }

        long Spare(string item) => Math.Max(0, Have(item) - KeepForWork(item));
        long Value((string Id, long Makes, List<(string Item, long Quantity)> Ingredients) r) =>
            Floor(r.Id) * r.Makes - r.Ingredients.Sum(i => Floor(i.Item) * i.Quantity);
        var listed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in _commons.Overview?.Overview.Each("items") ?? [])
        {
            listed[entry.Text("id")!] = entry.Long("listed");
        }

        // Only what pays, and only while what it made before is selling.
        bool Worth((string Id, long Makes, List<(string Item, long Quantity)> Ingredients) r) =>
            Value(r) > 0 && Have(r.Id) < BotRules.WorkshopStock && listed.GetValueOrDefault(r.Id) < BotRules.MarketCap(Floor(r.Id));
        var current = recipes.FirstOrDefault(r => r.Id == machine.Text("recipe"));
        var best = recipes.Where(r => Worth(r) && r.Ingredients.All(i => Spare(i.Item) >= i.Quantity)).MaxBy(Value);
        if (best.Id is not null && best.Id != current.Id && machine.Long("batches") == 0)
        {
            if (!(await SendAsync("machine.recipe", new { machine = id, recipe = best.Id }, cancellationToken)).Ok)
            {
                return acted;
            }

            current = best;
            acted = true;
            await InventoryAsync(cancellationToken);
        }

        if (current.Id is null)
        {
            _workshopNeeds.Remove(id);
            return acted;
        }

        var needs = new Dictionary<string, long>(StringComparer.Ordinal);
        if (Worth(current))
        {
            var inside = machine.Each("inputs").ToDictionary(i => i.Text("id")!, i => i.Long("quantity"), StringComparer.Ordinal);
            foreach (var (item, each) in current.Ingredients)
            {
                var wanted = Math.Min(50, each * BotRules.WorkshopBatches) - inside.GetValueOrDefault(item);
                var quantity = Math.Min(wanted, Spare(item));
                if (quantity > 0 && (await SendAsync("machine.load", new { machine = id, item, quantity }, cancellationToken)).Ok)
                {
                    _inventory[item] = Have(item) - quantity;
                    acted = true;
                }

                if (wanted > quantity)
                {
                    needs[item] = wanted - quantity + Math.Max(0, KeepForWork(item) - Have(item));
                }
            }
        }

        _workshopNeeds[id] = needs;
        if (machine.True("fired") && (machine.Get("fuel") is not { ValueKind: JsonValueKind.Object } fuel || fuel.Long("quantity") < 3))
        {
            var burn = new[] { Items.Coal, Items.Charcoal, Items.Wood }.FirstOrDefault(f => Spare(f) > 0 && current.Ingredients.All(i => i.Item != f));
            if (burn is not null)
            {
                var quantity = Math.Min(Spare(burn), 10);
                if ((await SendAsync("machine.load", new { machine = id, item = burn, quantity, fuel = true }, cancellationToken)).Ok)
                {
                    _inventory[burn] = Have(burn) - quantity;
                    acted = true;
                }
            }
        }

        return acted;
    }

    private async Task<bool> FurnaceAsync(Guid furnace, BotWorld world, CancellationToken cancellationToken)
    {
        var state = await SendAsync("furnace.get", new { furnace }, cancellationToken);
        if (!state.Is("furnace"))
        {
            if (furnace == _furnace)
            {
                _furnace = null;
            }

            return false;
        }

        var output = state.Payload.Get("output")?.Long("quantity") ?? 0;
        if (output > 0)
        {
            var taken = await SendAsync("furnace.take", new { furnace, what = "output" }, cancellationToken);
            if (taken.Ok)
            {
                await InventoryAsync(cancellationToken);
                return true;
            }
        }

        // A furnace shows its coal as a number.
        var fuel = state.Payload.Long("fuel");
        var maxFuel = state.Payload.Long("maxFuel");
        if (fuel < 5 && Have(Items.Coal) > 0)
        {
            var coal = Math.Min(Have(Items.Coal), Math.Max(1, maxFuel - fuel));
            var loaded = await SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = Math.Min(coal, 10) }, cancellationToken);
            if (loaded.Ok)
            {
                await InventoryAsync(cancellationToken);
                return true;
            }
        }

        var input = state.Payload.Get("input");
        var inside = input?.Long("quantity") ?? 0;
        var ore = input?.Text("id") ?? BotRules.Ores.OrderByDescending(Have).FirstOrDefault(o => Have(o) > 0);
        if (ore is not null && Have(ore) > 0 && inside < state.Payload.Long("maxInput"))
        {
            var quantity = Math.Min(Have(ore), Math.Min(20, state.Payload.Long("maxInput") - inside));
            var loaded = await SendAsync("furnace.load", new { furnace, item = ore, quantity }, cancellationToken);
            if (loaded.Ok)
            {
                await InventoryAsync(cancellationToken);
                return true;
            }
        }

        return false;
    }
}
