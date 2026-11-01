using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>What the bot sees when it looks around: character, garage, inventory, wallet, plots, prices.</summary>
public sealed partial class Bot
{
    /// <summary>What a player sees when they log in: character, garage, inventory, wallet, plots, prices.</summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var character = await SendAsync("character.get", null, cancellationToken);
        if (!character.Is("character"))
        {
            _logger.LogWarning("Bot {Bot} has no character; it stays idle", Row.CharacterName);
            _broken = true;
            return;
        }

        _tools = Tools(character.Payload);
        await GarageAsync(cancellationToken);
        await CraftingAsync(cancellationToken);
        await ParseResearchAsync(cancellationToken);

        var info = await SendAsync("world.info", null, cancellationToken);
        if (info.Ok)
        {
            _mapWidth = (int)info.Payload.Long("width");
            _mapHeight = (int)info.Payload.Long("height");
            _nextPlotBaseCents = info.Payload.Long("nextBasePriceCents");
            _priceFactors.Clear();
            foreach (var biome in info.Payload.Each("biomes"))
            {
                if (biome.True("buyable"))
                {
                    _priceFactors[(int)biome.Long("id")] = biome.Double("priceFactor");
                }
            }

            var known = _plots.ToDictionary(p => (p.X, p.Y));
            foreach (var plot in info.Payload.Each("plots"))
            {
                var key = ((int)plot.Long("x"), (int)plot.Long("y"));
                if (!known.ContainsKey(key))
                {
                    _plots.Add(new BotPlot(key.Item1, key.Item2, (int)plot.Long("biome")));
                    // Anyone may come and look at how a bot plays.
                    await SendAsync("plot.setPublic", new { x = key.Item1, y = key.Item2, @public = true }, cancellationToken);
                }
            }
        }

        var prices = await SendAsync("market.prices", null, cancellationToken);
        if (prices.Ok)
        {
            _floor.Clear();
            _traderSells.Clear();
            foreach (var item in prices.Payload.Each("items"))
            {
                var id = item.Text("id")!;
                if (item.LongOrNull("sellCents") is { } floor)
                {
                    _floor[id] = floor;
                }

                if (item.LongOrNull("buyCents") is { } sells && item.Text("locked") is null)
                {
                    _traderSells[id] = sells;
                }
            }
        }

        await InventoryAsync(cancellationToken);
        var wallet = await SendAsync("wallet.get", null, cancellationToken);
        _money = wallet.Payload.Long("balanceCents");
        await AllowanceAsync(cancellationToken);
        await MyOffersAsync(cancellationToken);

        // Everything that is already finished has been handed out above.
        var again = await SendAsync("character.get", null, cancellationToken);
        _xp = again.Payload.Long("xp");
        _fresh = true;
    }

    private async Task<bool> GarageAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("garage.get", null, cancellationToken);
        if (!reply.Is("garage"))
        {
            return false;
        }

        ParseGarage(reply.Payload);
        return true;
    }

    private void ParseGarage(JsonElement garage)
    {
        var queue = garage.Each("queue").Select(q => q.GetInt64()).ToList();
        _sortQueue = queue.Count;
        _sortDoneAt = queue.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(queue.Min()) : default;
        _inventory[Items.Trash] = garage.Long("trash");
        _garage.Clear();
        _workbench = null;
        _furnace = null;
        _bin = false;
        foreach (var o in garage.Each("objects"))
        {
            var kind = o.Text("kind")!;
            _garage.Add((kind, (int)o.Long("x"), (int)o.Long("y"), (int)o.Long("rotation")));
            switch (kind)
            {
                case Items.Workbench:
                    _workbench ??= o.Id("id");
                    break;
                case Items.Furnace:
                    _furnace ??= o.Id("id");
                    break;
                case Items.TrashBin:
                    _bin = true;
                    break;
            }
        }

        if (garage.Get("furniture") is { ValueKind: JsonValueKind.Object } furniture)
        {
            _footprints.Clear();
            foreach (var f in furniture.EnumerateObject())
            {
                _footprints[f.Name] = ((int)f.Value.Long("width"), (int)f.Value.Long("depth"));
            }
        }

        foreach (var result in garage.Each("results"))
        {
            var id = result.Text("id")!;
            _inventory[id] = Have(id) + 1;
        }
    }

    private async Task<bool> CraftingAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("crafting.get", null, cancellationToken);
        if (!reply.Is("crafting"))
        {
            return false;
        }

        ParseCrafting(reply.Payload);
        if (reply.Payload.Each("done").Any())
        {
            await InventoryAsync(cancellationToken);
        }

        return true;
    }

    private void ParseCrafting(JsonElement crafting)
    {
        _recipes.Clear();
        foreach (var r in crafting.Each("recipes"))
        {
            _recipes[r.Text("id")!] = (
                r.Long("xp"),
                r.Each("ingredients").Select(i => (i.Text("id")!, i.Long("quantity"))).ToList(),
                r.Text("locked") is not null,
                r.Text("station") ?? Items.Workbench,
                Math.Max(1, r.Long("quantity")));
        }

        _craftQueue = crafting.Each("queue").Count();
    }

    private IEnumerable<(string Item, long Quantity)> Recipe(string id) =>
        _recipes.TryGetValue(id, out var r) ? r.Ingredients : [(id, long.MaxValue)];

    private long RecipeXp(string id) => _recipes.TryGetValue(id, out var r) ? r.Xp : 0;

    private bool IsLocked(string id) => !_recipes.TryGetValue(id, out var r) || r.Locked || r.Station != Items.Workbench;

    private async Task<bool> ParseResearchAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("research.get", null, cancellationToken);
        if (!reply.Is("research"))
        {
            return false;
        }

        _techs.Clear();
        foreach (var t in reply.Payload.Each("technologies"))
        {
            _techs[t.Text("id")!] = (
                t.Text("status") ?? "locked",
                t.Long("xp"),
                t.Each("cost").Select(c => (c.Text("id")!, c.Long("quantity"))).ToList());
        }

        if (reply.Payload.Each("finished").Any())
        {
            // Finished research unlocks recipes and what the trader sells.
            _recipes.Clear();
            await CraftingAsync(cancellationToken);
        }

        return true;
    }

    private async Task InventoryAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("inventory.get", null, cancellationToken);
        if (!reply.Is("inventory"))
        {
            return;
        }

        _inventory.Clear();
        foreach (var item in reply.Payload.Each("items"))
        {
            _inventory[item.Text("id")!] = item.Long("quantity");
        }
    }

    private async Task MyOffersAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("market.mine", null, cancellationToken);
        if (!reply.Is("market.mine"))
        {
            return;
        }

        _listings.Clear();
        _listings.AddRange(reply.Payload.Each("listings").Where(l => l.True("open"))
            .Select(l => (l.Long("id"), l.Text("item")!, l.Long("done") > 0)));
        _openListings = _listings.Count;
        _openOrders = reply.Payload.Each("orders").Count(o => o.True("open"));
    }

    /// <summary>
    /// Tops the wallet up as a session starts: to the first plot (and some tools) while the bot has none, so it
    /// gets to its trade in its first session rather than after days of sorting trash, and to the
    /// <see cref="BotRules.Allowance"/> it buys from players with.
    /// </summary>
    private async Task AllowanceAsync(CancellationToken cancellationToken)
    {
        if (_grant is null)
        {
            return;
        }

        var target = BotRules.Allowance;
        if (_plots.Count == 0 && _mapWidth > 0)
        {
            var first = BotRules.PlotBiomes(Profile.Trade, 0).Min(b => PlotPrice(b));
            target = Math.Max(target, first + BotRules.Reserve + BotRules.StartingExtra);
        }

        if (_money < target)
        {
            await _grant(target - _money, cancellationToken);
            _money = (await SendAsync("wallet.get", null, cancellationToken)).Payload.Long("balanceCents");
        }
    }
}
