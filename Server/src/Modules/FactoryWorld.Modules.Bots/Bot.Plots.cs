using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>The bot on its plots: buying them, gathering, farming, planting and building on them.</summary>
public sealed partial class Bot
{
    private long PlotPrice(int biome) => (long)Math.Round(_nextPlotBaseCents * _priceFactors.GetValueOrDefault(biome, 1.0));

    /// <summary>Looks for a free plot of a wanted biome in a spiral out from home and buys the first one found.</summary>
    private async Task<bool> BuyPlotAsync(int[] biomes, CancellationToken cancellationToken)
    {
        var (hx, hy) = Home();
        var looked = 0;
        for (var r = _searchRadius; r <= 80 && looked < 250; r++)
        {
            _searchRadius = r;
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                    {
                        continue;
                    }

                    var x = ((hx + dx) % _mapWidth + _mapWidth) % _mapWidth;
                    var y = hy + dy;
                    if (y < 0 || y >= _mapHeight)
                    {
                        continue;
                    }

                    looked++;
                    var plot = await SendAsync("world.plot", new { x, y }, cancellationToken);
                    if (!plot.Is("world.plot") || plot.Payload.Get("owner") is not null || plot.Payload.LongOrNull("priceCents") is not { } price)
                    {
                        continue;
                    }

                    var biome = (int)plot.Payload.Long("biome");
                    if (!biomes.Contains(biome) || price + BotRules.Reserve > _money)
                    {
                        continue;
                    }

                    var bought = await SendAsync("world.buy", new { x, y }, cancellationToken);
                    if (!bought.Is("world.bought"))
                    {
                        continue;
                    }

                    _plots.Add(new BotPlot(x, y, biome));
                    await SendAsync("plot.setPublic", new { x, y, @public = true }, cancellationToken);
                    if (bought.Payload.Get("tool")?.Text("id") is { } given && _tools.Count == 0)
                    {
                        _tools = [given];
                    }

                    _nextPlotBaseCents = 0;
                    _fresh = false;
                    Goals.Drop(BotGoals.Plot);
                    _logger.LogInformation("Bot {Bot} bought plot {X}:{Y}", Row.CharacterName, x, y);
                    return true;
                }
            }
        }

        if (_searchRadius >= 80)
        {
            _searchRadius = 0;
        }

        return false;
    }

    /// <summary>
    /// Holds money back for the next plot once the bot has a share of its price (<see cref="BotRules.SaveForPlotFrom"/>), so
    /// that it does not spend it at the market the way a player saving up would not; the first plot it buys as soon as it can.
    /// </summary>
    private void SaveForPlot(long price, BotWorld world)
    {
        if (_plots.Count == 0 || _money < price * BotRules.SaveForPlotFrom)
        {
            Goals.Drop(BotGoals.Plot);
            return;
        }

        if (Goals[BotGoals.Plot] is not { } goal || goal.Cents != price)
        {
            Goals.Set(new BotGoal(BotGoals.Plot, price, null, Goals[BotGoals.Plot]?.Since ?? world.Now));
        }
    }

    /// <summary>
    /// Delivers the town of its city plot what it wants today and the bot has spare, like a player supplying a town.
    /// What the town pays counts towards <see cref="BotRules.MaxTraderIncome"/>, since it is new money like the trader's.
    /// </summary>
    private async Task<bool> SupplyTownAsync(BotPlot plot, BotWorld world, CancellationToken cancellationToken)
    {
        plot.SupplyAt = world.Now + BotRules.SupplyEvery;
        var city = await SendAsync("city.get", new { x = plot.X, y = plot.Y }, cancellationToken);
        if (!city.Is("city") || city.Payload.Get("state") is not { } state)
        {
            return false;
        }

        var delivered = false;
        foreach (var need in state.Each("needs"))
        {
            var item = need.Text("id") ?? "";
            var left = need.Long("wanted") - need.Long("delivered");
            var spare = Have(item) - KeepForWork(item);
            if (left <= 0 || spare <= 0 || _traderIncome >= BotRules.MaxTraderIncome)
            {
                continue;
            }

            var price = Math.Max(need.Long("priceCents"), 1);
            var quantity = Math.Min(Math.Min(left, spare), Math.Max(1, (BotRules.MaxTraderIncome - _traderIncome) / price));
            var reply = await SendAsync("city.deliver", new { x = plot.X, y = plot.Y, item, quantity }, cancellationToken);
            if (!reply.Is("city") || reply.Payload.Get("result") is not { } result)
            {
                continue;
            }

            var cents = result.Long("cents") + result.Long("subsidyCents");
            _money += cents;
            _traderIncome += cents;
            _inventory[item] = Have(item) - result.Long("quantity");
            delivered = true;
            _logger.LogInformation("Bot {Bot} delivered {Quantity} {Item} to the town at {X}:{Y}", Row.CharacterName, result.Long("quantity"), item, plot.X, plot.Y);
        }

        return delivered;
    }

    /// <summary>The plot at the bot's home, in the map's projection (Behrmann, as the world module draws it).</summary>
    private (int X, int Y) Home()
    {
        const double radius = 6371.0072;
        const int sideKm = 5;
        var cos30 = Math.Cos(Math.PI / 6);
        var x = (Profile.Longitude * Math.PI / 180 * radius * cos30 + _mapWidth * sideKm / 2.0) / sideKm;
        var y = (_mapHeight * sideKm / 2.0 - radius * Math.Sin(Profile.Latitude * Math.PI / 180) / cos30) / sideKm;
        return (Math.Clamp((int)Math.Floor(x), 0, _mapWidth - 1), Math.Clamp((int)Math.Floor(y), 0, _mapHeight - 1));
    }

    private async Task<bool> EnterAsync(BotPlot plot, BotWorld world, CancellationToken cancellationToken)
    {
        var reply = await SendAsync("plot.enter", new { x = plot.X, y = plot.Y }, cancellationToken);
        if (!reply.Is("plot"))
        {
            return false;
        }

        var p = reply.Payload;
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(p.Text("tiles")!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        plot.Tiles = tiles.ToArray();
        plot.Size = (int)p.Long("size");
        plot.Spawn = ((int)(p.Get("spawn")?.Long("x") ?? 0), (int)(p.Get("spawn")?.Long("y") ?? 0));
        _tools = Tools(p);

        var wanted = plot.Biome switch
        {
            Biomes.Mountains => new[] { Tiles.Rock },
            Biomes.Desert => [Tiles.Dune, Tiles.Rock],
            _ => [Tiles.Tree, Tiles.Rock],
        };
        var busy = new HashSet<(int, int)>();
        plot.Objects.Clear();
        plot.Busy.Clear();
        foreach (var o in p.Each("objects"))
        {
            var (kind, ox, oy) = (o.Text("kind")!, (int)o.Long("tileX"), (int)o.Long("tileY"));
            busy.Add((ox, oy));
            plot.Busy.UnionWith(Footprints.Tiles(kind, ox, oy));
            plot.Objects.Add((o.Id("id") ?? Guid.Empty, kind, ox, oy));
        }

        foreach (var m in p.Each("mines"))
        {
            var (mx, my) = ((int)m.Long("x"), (int)m.Long("y"));
            for (var dx = 0; dx < Math.Max(1, m.Long("width")); dx++)
            {
                for (var dy = 0; dy < Math.Max(1, m.Long("height")); dy++)
                {
                    plot.Busy.Add((mx + dx, my + dy));
                }
            }
        }

        if (p.Get("station") is { } station)
        {
            var (sx, sy) = ((int)station.Long("x"), (int)station.Long("y"));
            for (var dx = -1; dx <= station.Long("width"); dx++)
            {
                for (var dy = -1; dy <= station.Long("height"); dy++)
                {
                    plot.Busy.Add((sx + dx, sy + dy));
                }
            }
        }

        if (p.LongOrNull("railRow") is { } rail)
        {
            for (var x = 0; x < plot.Size; x++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    plot.Busy.Add((x, (int)rail + dy));
                }
            }
        }

        plot.Busy.Add(plot.Spawn);
        FindSite(plot);

        plot.Fields.Clear();
        foreach (var f in p.Each("fields"))
        {
            var at = ((int)f.Long("tileX"), (int)f.Long("tileY"));
            busy.Add(at);
            plot.Fields.Add((at.Item1, at.Item2, f.Text("crop"), f.LongOrNull("readyAt") is { } ready ? DateTimeOffset.FromUnixTimeMilliseconds(ready) : null));
        }

        var trees = new Dictionary<(int, int), DateTimeOffset>();
        foreach (var t in p.Each("trees"))
        {
            var at = ((int)t.Long("tileX"), (int)t.Long("tileY"));
            busy.Add(at);
            trees[at] = DateTimeOffset.FromUnixTimeMilliseconds(t.Long("readyAt"));
        }

        plot.Planted = trees.Count;
        plot.Busy.UnionWith(busy);
        plot.Mines.Clear();
        foreach (var m in p.Each("mines"))
        {
            var upgrade = m.Get("upgrade");
            var cost = upgrade?.Each("cost").ToDictionary(c => c.Text("id")!, c => c.Long("quantity")) ?? [];
            plot.Mines.Add((
                (int)m.Long("id"), (int)m.Long("units"),
                m.LongOrNull("refillAt") is { } refill ? DateTimeOffset.FromUnixTimeMilliseconds(refill) : null,
                cost.GetValueOrDefault(Items.IronIngot), cost.GetValueOrDefault(Items.Wood),
                upgrade is not null && cost.Keys.All(k => k is Items.IronIngot or Items.Wood)));
        }

        // Players work near where they appear; so does the bot.
        plot.Targets.Clear();
        plot.Free.Clear();
        var size = plot.Size;
        var near = Enumerable.Range(0, plot.Tiles.Length)
            .Select(i => (X: i % size, Y: i / size))
            .Where(t => t.Y > plot.Spawn.Y && !busy.Contains(t))
            .OrderBy(t => Math.Abs(t.X - plot.Spawn.X) + Math.Abs(t.Y - plot.Spawn.Y));
        foreach (var t in near)
        {
            var tile = plot.Tiles[t.Y * size + t.X];
            if (wanted.Contains(tile) && plot.Targets.Count < 300)
            {
                plot.Targets.Add(t);
            }
            else if (tile == Tiles.None && plot.Free.Count < 300)
            {
                plot.Free.Add(t);
            }

            if (plot.Targets.Count >= 300 && plot.Free.Count >= 300)
            {
                break;
            }
        }

        // A planted tree that has grown is cut like any other.
        foreach (var ((x, y), ready) in trees)
        {
            if (ready <= world.Now)
            {
                plot.Targets.Insert(0, (x, y));
            }
        }

        return true;
    }

    private async Task<bool> WorkPlotAsync(BotPlot plot, BotWorld world, CancellationToken cancellationToken)
    {
        if (plot.Tiles is null && !await EnterAsync(plot, world, cancellationToken))
        {
            return false;
        }

        if (plot.Biome == Biomes.Farmland)
        {
            return await FarmAsync(plot, world, cancellationToken);
        }

        if (plot.Biome == Biomes.Mountains)
        {
            var mine = plot.Mines.FirstOrDefault(m => m.Units > 0 || m.RefillAt is { } at && at <= world.Now);
            if (mine.Units > 0 || mine.RefillAt is not null)
            {
                if (!await HoldAsync(Items.Pickaxe, cancellationToken))
                {
                    return false;
                }

                var mined = await SendAsync("plot.mine", new { x = plot.X, y = plot.Y, mine = mine.Id }, cancellationToken);
                var index = plot.Mines.IndexOf(mine);
                if (mined.Is("plot.mined"))
                {
                    var m = mined.Payload.Get("mine")!.Value;
                    plot.Mines[index] = mine with { Units = (int)m.Long("units"), RefillAt = m.LongOrNull("refillAt") is { } r ? DateTimeOffset.FromUnixTimeMilliseconds(r) : null };
                    foreach (var item in mined.Payload.Each("items"))
                    {
                        _inventory[item.Text("id")!] = Have(item.Text("id")!) + item.Long("quantity");
                    }

                    return true;
                }

                if (mined.Error == "mine_empty")
                {
                    plot.Mines[index] = mine with { Units = 0, RefillAt = world.Now.AddMinutes(10) };
                }

                return false;
            }
        }

        return await HarvestAsync(plot, cancellationToken);
    }

    private async Task<bool> HarvestAsync(BotPlot plot, CancellationToken cancellationToken)
    {
        while (plot.Targets.Count > 0)
        {
            var (x, y) = plot.Targets[0];
            var tool = plot.Tiles![y * plot.Size + x] switch
            {
                Tiles.Rock => Items.Pickaxe,
                Tiles.Dune => Items.Shovel,
                _ => Items.Axe,
            };
            if (!await HoldAsync(tool, cancellationToken))
            {
                // Without the tool, the rest of that kind is out of reach too.
                plot.Targets.RemoveAll(t => plot.Tiles[t.Y * plot.Size + t.X] == plot.Tiles[y * plot.Size + x]);
                continue;
            }

            var reply = await SendAsync("plot.harvest", new { x = plot.X, y = plot.Y, tileX = x, tileY = y }, cancellationToken);
            if (reply.Error == "too_fast")
            {
                return false;
            }

            plot.Targets.RemoveAt(0);
            plot.Tiles[y * plot.Size + x] = Tiles.None;
            if (reply.Is("plot.harvested"))
            {
                foreach (var item in reply.Payload.Each("items"))
                {
                    _inventory[item.Text("id")!] = Have(item.Text("id")!) + item.Long("quantity");
                }

                // A saw may have felled the trees next to it too.
                foreach (var tile in reply.Payload.Each("felled"))
                {
                    var (tx, ty) = ((int)tile.Long("tileX"), (int)tile.Long("tileY"));
                    plot.Targets.Remove((tx, ty));
                    plot.Tiles[ty * plot.Size + tx] = Tiles.None;
                }

                return true;
            }
        }

        return false;
    }

    private async Task<bool> FarmAsync(BotPlot plot, BotWorld world, CancellationToken cancellationToken)
    {
        var ripe = plot.Fields.FindIndex(f => f.Crop is not null && f.ReadyAt is { } at && at <= world.Now);
        if (ripe >= 0)
        {
            var f = plot.Fields[ripe];
            var reply = await SendAsync("plot.reap", new { x = plot.X, y = plot.Y, tileX = f.X, tileY = f.Y }, cancellationToken);
            if (!reply.Is("plot.field"))
            {
                plot.Fields.RemoveAt(ripe);
                return false;
            }

            plot.Fields[ripe] = (f.X, f.Y, null, null);
            foreach (var item in reply.Payload.Each("items"))
            {
                _inventory[item.Text("id")!] = Have(item.Text("id")!) + item.Long("quantity");
            }

            return true;
        }

        var empty = plot.Fields.FindIndex(f => f.Crop is null);
        if (empty >= 0)
        {
            var f = plot.Fields[empty];
            var crop = Pick(world.Random, BotRules.Crops);
            var reply = await SendAsync("plot.plant", new { x = plot.X, y = plot.Y, tileX = f.X, tileY = f.Y, crop }, cancellationToken);
            if (!reply.Is("plot.field"))
            {
                plot.Fields.RemoveAt(empty);
                return false;
            }

            var field = reply.Payload.Get("field");
            plot.Fields[empty] = (f.X, f.Y, crop, field?.LongOrNull("readyAt") is { } ready ? DateTimeOffset.FromUnixTimeMilliseconds(ready) : null);
            return true;
        }

        if (plot.Fields.Count < BotRules.MaxFields && plot.Free.Count > 0 && await HoldAsync(Items.Hoe, cancellationToken))
        {
            var (x, y) = plot.Free[0];
            plot.Free.RemoveAt(0);
            var reply = await SendAsync("plot.till", new { x = plot.X, y = plot.Y, tileX = x, tileY = y }, cancellationToken);
            if (reply.Is("plot.field"))
            {
                plot.Fields.Add((x, y, null, null));
                return true;
            }
        }

        return false;
    }

    private async Task<bool> PlantTreeAsync(BotPlot plot, BotWorld world, CancellationToken cancellationToken)
    {
        if (plot.Tiles is null && !await EnterAsync(plot, world, cancellationToken))
        {
            return false;
        }

        while (plot.Free.Count > 0)
        {
            var (x, y) = plot.Free[world.Random.Next(Math.Min(plot.Free.Count, 40))];
            plot.Free.Remove((x, y));
            var reply = await SendAsync("plot.plantTree", new { x = plot.X, y = plot.Y, tileX = x, tileY = y }, cancellationToken);
            if (reply.Is("plot.tree"))
            {
                plot.Planted++;
                _inventory[Items.Sapling] = Math.Max(0, Have(Items.Sapling) - 1);
                return true;
            }

            if (reply.Error != "blocked")
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>What of the plot's plan in <see cref="BotRules.Builds"/> does not stand there yet, in the plan's order.</summary>
    private IEnumerable<string> StillToBuild(BotPlot plot)
    {
        var standing = plot.Objects.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach (var kind in BotRules.Builds(Profile.Trade, plot.Biome))
        {
            if (standing.GetValueOrDefault(kind) > 0)
            {
                standing[kind]--;
            }
            else
            {
                yield return kind;
            }
        }
    }

    /// <summary>Puts something from the inventory on a free tile near the spawn, off the path straight ahead of it.</summary>
    private async Task<bool> BuildAsync(BotPlot plot, string kind, CancellationToken cancellationToken)
    {
        foreach (var (x, y) in plot.Free.Where(t => t.X != plot.Spawn.X).Take(20).ToList())
        {
            plot.Free.Remove((x, y));
            var reply = await SendAsync("plot.place", new { x = plot.X, y = plot.Y, kind, tileX = x, tileY = y, rotation = 0 }, cancellationToken);
            if (reply.Is("plot.placed"))
            {
                // A machine covers more tiles than the one it stands on.
                plot.Free.RemoveAll(t => Footprints.Covers(kind, x, y, t.X, t.Y));
                plot.Objects.Add((reply.Payload.Get("object")?.Id("id") ?? Guid.Empty, kind, x, y));
                plot.Busy.UnionWith(Footprints.Tiles(kind, x, y));
                _inventory[kind] = Math.Max(0, Have(kind) - 1);
                return true;
            }

            if (reply.Error != "blocked")
            {
                return false;
            }
        }

        return false;
    }

    private async Task<bool> UpgradeMineAsync(BotPlot plot, CancellationToken cancellationToken)
    {
        var mine = plot.Mines.First(m => m.Upgradable && Have(Items.IronIngot) >= m.UpgradeIron && Have(Items.Wood) >= m.UpgradeWood);
        var reply = await SendAsync("plot.upgradeMine", new { x = plot.X, y = plot.Y, mine = mine.Id }, cancellationToken);
        plot.Tiles = null;
        if (!reply.Is("plot.mineUpgraded"))
        {
            return false;
        }

        await InventoryAsync(cancellationToken);
        return true;
    }

    /// <summary>Gets the tool into the bot's hand: from the inventory, or bought from the trader.</summary>
    private async Task<bool> HoldAsync(string tool, CancellationToken cancellationToken)
    {
        if (_tools.Contains(tool) || tool == Items.Axe && Items.BestSaw(_tools) is not null)
        {
            return true;
        }

        if (Have(tool) == 0)
        {
            if (!_traderSells.TryGetValue(tool, out var price) || _money < price + BotRules.Reserve)
            {
                return false;
            }

            if (!await TradeAsync("market.buy", tool, 1, cancellationToken))
            {
                return false;
            }
        }

        var reply = await SendAsync("character.equip", new { item = tool }, cancellationToken);
        if (!reply.Is("character"))
        {
            return false;
        }

        _tools = Tools(reply.Payload);
        await InventoryAsync(cancellationToken);
        return _tools.Contains(tool);
    }

    /// <summary>The tools in a character's (or a plot's) tool bar.</summary>
    private static List<string> Tools(JsonElement payload) =>
        payload.Each("tools").Where(t => t.ValueKind == JsonValueKind.Object).Select(t => t.Text("id")).OfType<string>().ToList();
}
