using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.Bots;

/// <summary>The bot's production line: what it needs, building it, tending it and asking players for help with it.</summary>
public sealed partial class Bot
{
    /// <summary>What the bot looks for on the market: what its trade works with, and what it lacks for its line and its next research.</summary>
    private IEnumerable<string> Wanted() =>
        BotRules.Buys(Profile.Trade).Concat(_needs.Keys).Concat(_shortfalls.Keys).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The technologies the bot may research now: the first three on its list (what its line takes) always, the rest once its
    /// line stands, so that a bakery or a foundry does not eat the copper the line's inserters need.
    /// </summary>
    private IEnumerable<string> ResearchNow() =>
        BotRules.Research(Profile.Trade).Where((_, i) => i < 3 || _line is null || _site is not null);

    /// <summary>Whether the bot has researched everything its line is made of.</summary>
    private bool LineReady(BotLine line) => line.Kit.All(k => !IsLocked(k.Item) || Have(k.Item) >= k.Quantity);

    /// <summary>Parts of the line the bot still lacks, while none stands.</summary>
    private List<(string Item, long Quantity)> Missing(BotLine line) =>
        _site is not null ? [] : line.Kit.Select(k => (k.Item, Quantity: k.Quantity - Have(k.Item))).Where(k => k.Quantity > 0).ToList();

    /// <summary>What making the missing parts at the workbench takes, in all.</summary>
    private Dictionary<string, long> Makings(BotLine line)
    {
        var makings = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (part, quantity) in Missing(line))
        {
            // Locked recipes too: what a part not yet researched will take is kept back for it already.
            if (!_recipes.TryGetValue(part, out var recipe) || recipe.Station != Items.Workbench)
            {
                continue;
            }

            var crafts = (quantity + recipe.Makes - 1) / recipe.Makes;
            foreach (var (item, each) in recipe.Ingredients)
            {
                makings[item] = makings.GetValueOrDefault(item) + crafts * each;
            }
        }

        return makings;
    }

    /// <summary>What the bot lacks for its line and for the next technology on its list, to look for on the market.</summary>
    private Dictionary<string, long> Needs()
    {
        var needs = new Dictionary<string, long>(StringComparer.Ordinal);
        if (_line is { } line && _site is null && _plots.Count > 0 && LineReady(line))
        {
            foreach (var (item, quantity) in Makings(line))
            {
                if (quantity > Have(item))
                {
                    needs[item] = quantity - Have(item);
                }
            }
        }

        foreach (var tech in ResearchNow())
        {
            if (_techs.TryGetValue(tech, out var t) && t.Status == "available")
            {
                foreach (var (item, quantity) in t.Cost.Where(c => c.Quantity > Have(c.Item)))
                {
                    needs[item] = needs.GetValueOrDefault(item) + quantity - Have(item);
                }

                break;
            }
        }

        return needs;
    }

    /// <summary>What the bot does not sell: the cost of the technologies it can research next, and while its line is not up,
    /// the parts and what it makes them of.</summary>
    private Dictionary<string, long> Reserved()
    {
        var reserved = _line is { } line && _site is null && _plots.Count > 0
            ? Makings(line)
            : new Dictionary<string, long>(StringComparer.Ordinal);
        if (_line is not null && _site is null && _plots.Count > 0)
        {
            foreach (var (item, quantity) in _line.Kit)
            {
                reserved[item] = reserved.GetValueOrDefault(item) + quantity;
            }
        }

        foreach (var tech in ResearchNow())
        {
            if (_techs.TryGetValue(tech, out var t) && t.Status == "available")
            {
                foreach (var (item, quantity) in t.Cost)
                {
                    reserved[item] = reserved.GetValueOrDefault(item) + quantity;
                }
            }
        }

        return reserved;
    }

    /// <summary>The weight of a product the bot's research or line needs: made before anything for the market.</summary>
    private const long Needed = -1;

    /// <summary>
    /// Which of its trade's products the bot makes next for the market, and what it crafts now towards it (the product,
    /// or a part it lacks); null when nothing is worth making. Only products it can finish from what it has, short of
    /// what it keeps for its own work, of which it has fewer than it makes ahead and of which the players' market does
    /// not have plenty already; the better a product pays over what its ingredients fetch, the likelier it is picked.
    /// Something its research or line needs it makes regardless of the market.
    /// </summary>
    /// <param name="listed">Pieces of each item on the players' market; null to leave the market out, as a first look.</param>
    private (string Product, string Craft)? ProductToMake(BotWorld world, IReadOnlyDictionary<string, long>? listed)
    {
        var options = new List<(string Product, string Craft, long Weight)>();
        foreach (var product in BotRules.Products(Profile.Trade))
        {
            var needed = _reserved.GetValueOrDefault(product);
            if (IsLocked(product) || Have(product) >= BotRules.Stock(product) + needed)
            {
                continue;
            }

            if (needed <= Have(product) && listed is not null && listed.GetValueOrDefault(product) >= BotRules.MarketCap(Floor(product)))
            {
                continue;
            }

            var margin = Margin(product);
            if (NextCraft(product) is not { } craft || margin <= 0 && needed <= Have(product))
            {
                continue;
            }

            options.Add((product, craft, needed > Have(product) ? Needed : margin));
        }

        if (options.Count == 0)
        {
            return null;
        }

        // What its research or line needs comes first; otherwise the better a product pays, the likelier, so that bots
        // do not all make the same thing.
        if (options.FirstOrDefault(o => o.Weight == Needed) is { Product: not null } urgent)
        {
            return (urgent.Product, urgent.Craft);
        }

        // Having made the nails for an inserter, it goes on to the inserter rather than leave the nails lying about.
        if (options.FirstOrDefault(o => o.Product == _making) is { Product: not null } started)
        {
            return (started.Product, started.Craft);
        }

        var roll = (long)(world.Random.NextDouble() * options.Sum(o => o.Weight));
        foreach (var (product, craft, weight) in options)
        {
            roll -= weight;
            if (roll < 0)
            {
                return (product, craft);
            }
        }

        return (options[^1].Product, options[^1].Craft);
    }

    /// <summary>Looks at the market first, like a player would before making a stack of something to sell, then crafts towards the product chosen.</summary>
    private async Task<bool> CraftForMarketAsync(BotWorld world, CancellationToken cancellationToken)
    {
        if (await OverviewAsync(world, cancellationToken) is not { } overview)
        {
            return false;
        }

        var listed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in overview.Each("items"))
        {
            listed[entry.Text("id")!] = entry.Long("listed");
        }

        if (ProductToMake(world, listed) is not { } making)
        {
            _making = null;
            return false;
        }

        _making = making.Craft == making.Product ? null : making.Product;
        return await CraftAsync(making.Craft, cancellationToken);
    }

    /// <summary>What one craft of <paramref name="product"/> fetches over what its ingredients do, at what the trader pays for each.</summary>
    private long Margin(string product) =>
        _recipes.TryGetValue(product, out var recipe) && Floor(product) > 0
            ? Floor(product) * recipe.Makes - recipe.Ingredients.Sum(i => Floor(i.Item) * i.Quantity)
            : 0;

    /// <summary>
    /// What the bot crafts first to make one <paramref name="product"/>: the product itself when it has the ingredients,
    /// else the deepest part it lacks (nails before an inserter before a fast inserter); null when even with the parts made
    /// from what it has, it cannot finish the product. What the bot keeps for its work and its line stays untouched.
    /// </summary>
    private string? NextCraft(string product)
    {
        var (first, missing) = Plan(product, 1);
        return missing.Count == 0 ? first : null;
    }

    /// <summary>
    /// Works out <paramref name="crafts"/> crafts of <paramref name="product"/> from what the bot has spare, making the parts
    /// it can at the workbench: the first craft to start with, and what it still lacks that it cannot make itself (wood,
    /// ingots, stone), to gather or to buy.
    /// </summary>
    private (string? First, Dictionary<string, long> Missing) Plan(string product, long crafts)
    {
        var products = BotRules.Products(Profile.Trade);
        var stock = new Dictionary<string, long>(StringComparer.Ordinal);
        var missing = new Dictionary<string, long>(StringComparer.Ordinal);
        long Held(string item) => products.Contains(item) || item == product ? _reserved.GetValueOrDefault(item) : KeepForWork(item);
        long Spare(string item) => stock.TryGetValue(item, out var left) ? left : stock[item] = Math.Max(0, Have(item) - Held(item));
        bool Craftable(string item) => _recipes.TryGetValue(item, out var r) && !r.Locked && r.Station == Items.Workbench;

        string? first = null;
        if (Craftable(product))
        {
            Make(product, crafts, 0);
        }

        return (first, missing);

        void Make(string item, long count, int depth)
        {
            var ready = true;
            foreach (var (ingredient, each) in _recipes[item].Ingredients)
            {
                var need = each * count;
                var spare = Spare(ingredient);
                if (spare >= need)
                {
                    stock[ingredient] = spare - need;
                    continue;
                }

                if (depth >= BotRules.PartsDepth || !Craftable(ingredient))
                {
                    // Bought, it has to cover what the bot holds back of it too before any is spare.
                    missing[ingredient] = missing.GetValueOrDefault(ingredient, Math.Max(0, Held(ingredient) - Have(ingredient))) + need - spare;
                    stock[ingredient] = 0;
                    ready = false;
                    continue;
                }

                var makes = _recipes[ingredient].Makes;
                var parts = (need - spare + makes - 1) / makes;
                var before = missing.Count;
                Make(ingredient, parts, depth + 1);
                ready &= missing.Count == before;
                stock[ingredient] = Spare(ingredient) + parts * makes - need;
            }

            // The first craft whose ingredients are all at hand is the one to start with.
            if (ready)
            {
                first ??= item;
            }
        }
    }

    /// <summary>
    /// What the bot lacks for a few crafts of the products worth making, that it cannot make itself: rather than plant and
    /// chop for wood it does not have, it buys it from players or other bots when someone sells it cheaply enough.
    /// </summary>
    private Dictionary<string, long> Shortfalls()
    {
        var shortfalls = new Dictionary<string, long>(StringComparer.Ordinal);
        if (_workbench is null)
        {
            return shortfalls;
        }

        foreach (var product in BotRules.Products(Profile.Trade))
        {
            if (IsLocked(product) || Have(product) >= BotRules.Stock(product) || Margin(product) <= 0)
            {
                continue;
            }

            var crafts = Math.Max(1, (BotRules.Stock(product) - Have(product) + _recipes[product].Makes - 1) / _recipes[product].Makes);
            foreach (var (item, quantity) in Plan(product, Math.Min(crafts, BotRules.ShortfallCrafts)).Missing)
            {
                shortfalls[item] = Math.Max(shortfalls.GetValueOrDefault(item), quantity);
            }
        }

        // What it means to put up on its plots (a bakery wants sand), and what its workshop is short of.
        foreach (var kind in _plots.Where(p => p.Tiles is not null).SelectMany(StillToBuild).Distinct().Where(k => Have(k) == 0 && !IsLocked(k)))
        {
            foreach (var (item, quantity) in Plan(kind, 1).Missing)
            {
                shortfalls[item] = Math.Max(shortfalls.GetValueOrDefault(item), quantity);
            }
        }

        foreach (var (item, quantity) in _workshopNeeds.Values.SelectMany(n => n))
        {
            shortfalls[item] = Math.Max(shortfalls.GetValueOrDefault(item), quantity);
        }

        return shortfalls;
    }

    /// <summary>
    /// What the bot lacks for its products that someone has up for sale at a price it pays, as the market overview some bot
    /// fetched last shows it; empty before any did.
    /// </summary>
    private Dictionary<string, long> MaterialsOnSale()
    {
        var onSale = new Dictionary<string, long>(StringComparer.Ordinal);
        if (_shortfalls.Count == 0 || _commons.Overview is not { } overview)
        {
            return onSale;
        }

        foreach (var entry in overview.Overview.Each("items"))
        {
            var id = entry.Text("id")!;
            if (_shortfalls.TryGetValue(id, out var lacking) && entry.LongOrNull("lowestCents") is { } lowest
                && Limit(id, Floor(id)) is { } limit && lowest <= limit)
            {
                onSale[id] = lacking;
            }
        }

        return onSale;
    }

    /// <summary>What the bot could do about its line: look at its plots, tend it, build it or get its parts together.</summary>
    private void LineCandidates(BotLine line, BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        if (_plots.Count == 0)
        {
            return;
        }

        if (_plots.Any(p => p.Tiles is null))
        {
            // Whether a line stands already shows only on the plot.
            add(10, "look over plots", ct => LookOverAsync(world, ct));
            return;
        }

        if (_site is { } site)
        {
            if (_fixes.Count > 0)
            {
                FixCandidates(site, world, add);
                return;
            }

            if (_tendAt <= world.Now)
            {
                add(10, "tend line", ct => TendAsync(line, site, world, ct));
            }

            return;
        }

        if (!LineReady(line))
        {
            return;
        }

        var missing = Missing(line);
        if (_placeFailures >= 3)
        {
            // After a while the bot tries again: the plot may have changed, and it may have the tools now.
            _gaveUpAt = _gaveUpAt == default ? world.Now : _gaveUpAt;
            if (world.Now - _gaveUpAt >= BotRules.RetryBuildAfter)
            {
                (_placeFailures, _gaveUpAt) = (0, default);
            }
        }

        // Parts players can bring and it cannot make (nothing on the workbench, nothing to make them of) it waits for no
        // longer than this: it puts the line up without them and asks for them.
        var making = _craftQueue > 0 || missing.Any(m => !IsLocked(m.Item) && Has(Recipe(m.Item)));
        var forHelp = missing.Count > 0 && missing.All(m => BotRules.HelpParts.Contains(m.Item)) && missing.Sum(m => m.Quantity) <= BotRules.MaxHelpTasks
            && !making && _wantingSince != default && world.Now - _wantingSince >= BotRules.PartsWait;
        if (missing.Count == 0 || forHelp)
        {
            if (_plots.FirstOrDefault(p => p.Clearing.Count > 0) is { } clearing)
            {
                add(14, "clear ground", ct => ClearAsync(clearing, ct));
            }
            else if (_placeFailures < 3)
            {
                add(12, "build line", ct => BuildLineAsync(line, world, missing, ct));
            }

            return;
        }

        if (_wantingSince == default)
        {
            _wantingSince = world.Now;
        }

        var craftable = _workbench is not null && _craftQueue < 5;
        var part = missing.Select(m => m.Item).FirstOrDefault(p => craftable && !IsLocked(p) && Has(Recipe(p)));
        if (part is not null)
        {
            add(10, "craft line part", ct => CraftAsync(part, ct));
        }
        else if (craftable && _needs.ContainsKey(Items.Nails) && !_needs.ContainsKey(Items.IronIngot) && !IsLocked(Items.Nails) && Has(Recipe(Items.Nails)))
        {
            add(6, "craft nails", ct => CraftAsync(Items.Nails, ct));
        }

        // A part it cannot make it may buy from the trader, dear: money leaves the game that way.
        var dear = missing.Select(m => m.Item).FirstOrDefault(p =>
            _traderSells.TryGetValue(p, out var price) && price <= Floor(p) * BotRules.MaxPartMarkup && _money >= price + BotRules.Reserve + 20_00);
        if (dear is not null)
        {
            add(3, "buy line part", ct => TradeAsync("market.buy", dear, 1, ct));
        }
    }

    /// <summary>
    /// What the bot does about what is wrong with its line: asks players to set it right, and when nobody came for
    /// <see cref="BotRules.HelpWait"/>, sets right itself what it can. A line it cannot finish even then it takes down,
    /// to put it up again once it has all the parts.
    /// </summary>
    private void FixCandidates(BotLineSite site, BotWorld world, Action<int, string, Func<CancellationToken, Task<bool>>> add)
    {
        var waited = world.Now - _fixesSince;
        var doable = _fixes.Any(f => f.Object is not null || Have(f.Kind) > 0);
        // Meanwhile it makes the parts it asked for, if it can by now, to put them up itself should nobody come.
        if (_fixes.FirstOrDefault(f => f.Object is null && Have(f.Kind) == 0 && _workbench is not null && _craftQueue < 5
                && !IsLocked(f.Kind) && Has(Recipe(f.Kind))) is { } craftable)
        {
            add(6, "craft line part", ct => CraftAsync(craftable.Kind, ct));
        }

        if (waited >= BotRules.HelpWait && doable || waited >= BotRules.HelpWait * 2)
        {
            add(20, "fix line", ct => FixAsync(site, world, ct));
        }
        else
        {
            add(20, "ask for help", _ => Task.FromResult(Ask(_fixReason, site.Plot, world)));
        }
    }

    /// <summary>Turns and puts up itself what it asked players to; what it cannot, it takes the line down for after a long wait.</summary>
    private async Task<bool> FixAsync(BotLineSite site, BotWorld world, CancellationToken cancellationToken)
    {
        var (x, y) = (site.Plot.X, site.Plot.Y);
        foreach (var fix in _fixes.ToList())
        {
            BotReply? reply = fix.Object is { } id
                ? await SendAsync("plot.rotate", new { x, y, id, rotation = fix.Rotation }, cancellationToken)
                : Have(fix.Kind) > 0
                    ? await SendAsync("plot.place", new { x, y, kind = fix.Kind, tileX = fix.TileX, tileY = fix.TileY, rotation = fix.Rotation }, cancellationToken)
                    : null;
            if (reply is { Ok: true })
            {
                _fixes.Remove(fix);
            }
        }

        if (_fixes.Count > 0 && world.Now - _fixesSince >= BotRules.HelpWait * 2)
        {
            var ids = site.Parts.Select(p => p.Id).Distinct().ToList();
            await SendAsync("plot.pickupMany", new { x, y, ids }, cancellationToken);
            _fixes.Clear();
            _wantingSince = world.Now;
            _logger.LogInformation("Bot {Bot} took down its unfinished line on {X}:{Y}", Row.CharacterName, x, y);
        }

        await InventoryAsync(cancellationToken);
        LookAgain(site);
        return true;
    }

    /// <summary>The line changed under the bot's hands: it looks at the plot again before it tends the line.</summary>
    private void LookAgain(BotLineSite site)
    {
        _site = null;
        site.Plot.Tiles = null;
        _stalls = 0;
        _lastInputs = -1;
        _tendAt = default;
        _recipeSet = false;
    }

    private async Task<bool> LookOverAsync(BotWorld world, CancellationToken cancellationToken)
    {
        var looked = false;
        foreach (var plot in _plots.Where(p => p.Tiles is null).ToList())
        {
            looked |= await EnterAsync(plot, world, cancellationToken);
        }

        return looked;
    }

    /// <summary>Finds the bot's line among what stands on the plot: its machine with the chests and the generator in their places.</summary>
    private void FindSite(BotPlot plot)
    {
        if (_line is not { } line || _site is not null && _site.Plot != plot)
        {
            return;
        }

        _site = null;
        foreach (var machine in plot.Objects.Where(o => o.Kind == line.Machine))
        {
            var (x, y) = (machine.X - BotFactory.MachineX, machine.Y);
            var input = plot.Objects.FirstOrDefault(o => o.Kind == Items.WoodenChest && o.X == x && o.Y == y);
            var output = plot.Objects.FirstOrDefault(o => o.Kind == Items.WoodenChest && o.X == x + BotFactory.OutputX && o.Y == y);
            var generator = plot.Objects.FirstOrDefault(o =>
                o.Kind == Items.Generator && o.X == x + BotFactory.GeneratorX && o.Y == y + BotFactory.GeneratorY);
            if (input.Kind is null || output.Kind is null || generator.Kind is null)
            {
                continue;
            }

            var parts = plot.Objects.Where(o => o.X >= x && o.X < x + BotFactory.Width && o.Y >= y && o.Y < y + BotFactory.Depth).ToList();
            _site = new BotLineSite(plot, input.Id, machine.Id, output.Id, generator.Id, parts);
            return;
        }
    }

    /// <summary>Tiles nothing may be built on: something stands, grows or lies there.</summary>
    private static bool[,] Blocked(BotPlot plot)
    {
        var blocked = new bool[plot.Size, plot.Size];
        for (var y = 0; y < plot.Size; y++)
        {
            for (var x = 0; x < plot.Size; x++)
            {
                blocked[x, y] = plot.Tiles![y * plot.Size + x] != Tiles.None || plot.Busy.Contains((x, y));
            }
        }

        return blocked;
    }

    /// <summary>Puts the whole line up at once, with a building plan, where it fits nearest to the spawn.</summary>
    /// <remarks>
    /// Without the <paramref name="missing"/> parts the line goes up all the same, and players are asked to put them in;
    /// with all of them, the bot may put a belt or an inserter up the wrong way round (<see cref="BotCommons.MistakeChance"/>),
    /// for players to turn.
    /// </remarks>
    private async Task<bool> BuildLineAsync(
        BotLine line, BotWorld world, IReadOnlyList<(string Item, long Quantity)> missing, CancellationToken cancellationToken)
    {
        foreach (var plot in _plots.Where(p => p.Tiles is not null))
        {
            if (BotFactory.FindSpot(Blocked(plot), plot.Spawn) is not { } spot)
            {
                continue;
            }

            var parts = BotFactory.Layout(line.Machine).Select(p => (p.Kind, X: spot.X + p.Dx, Y: spot.Y + p.Dy, Rotation: BotFactory.East)).ToList();
            var left = new List<HelpTask>();
            foreach (var (item, quantity) in missing)
            {
                for (var i = 0; i < quantity && parts.FindLastIndex(p => p.Kind == item) is var at and >= 0; i++)
                {
                    left.Add(new HelpTask(item, parts[at].X, parts[at].Y, BotFactory.East));
                    parts.RemoveAt(at);
                }
            }

            (int X, int Y)? wrong = null;
            if (left.Count == 0 && !_mistaken && world.Random.NextDouble() < _commons.MistakeChance)
            {
                var movers = parts.Select((p, i) => (p.Kind, i)).Where(m => m.Kind is Items.Belt or Items.Inserter).ToList();
                var at = movers[world.Random.Next(movers.Count)].i;
                parts[at] = parts[at] with { Rotation = (BotFactory.East + 2) % 4 };
                wrong = (parts[at].X, parts[at].Y);
                _mistaken = true;
            }

            var objects = parts.Select(p => new { kind = p.Kind, tileX = p.X, tileY = p.Y, rotation = p.Rotation }).ToList();
            var reply = await SendAsync("plot.placeMany", new { x = plot.X, y = plot.Y, objects }, cancellationToken);
            if (!reply.Is("plot.placedMany"))
            {
                // Something the bot did not see is in the way: it looks again before the next try.
                _placeFailures++;
                plot.Tiles = null;
                return false;
            }

            foreach (var o in reply.Payload.Each("objects"))
            {
                var (kind, ox, oy) = (o.Text("kind")!, (int)o.Long("tileX"), (int)o.Long("tileY"));
                plot.Objects.Add((o.Id("id") ?? Guid.Empty, kind, ox, oy));
                plot.Busy.UnionWith(Footprints.Tiles(kind, ox, oy));
                if ((ox, oy) == wrong && o.Id("id") is { } turned)
                {
                    _fixes.Add(new HelpTask(kind, ox, oy, BotFactory.East, turned));
                    _fixReason = HelpReasons.Turned;
                }
            }

            if (left.Count > 0)
            {
                _fixes.AddRange(left);
                _fixReason = HelpReasons.Parts;
                plot.Busy.UnionWith(left.Select(l => (l.TileX, l.TileY)));
            }

            _fixesSince = world.Now;

            FindSite(plot);
            await InventoryAsync(cancellationToken);
            _wantingSince = default;
            _tendAt = world.Now;
            _recipeSet = false;
            _logger.LogInformation("Bot {Bot} built a {Machine} line on {X}:{Y}", Row.CharacterName, line.Machine, plot.X, plot.Y);
            return true;
        }

        // No room anywhere: the bot makes some where the fewest trees and rocks stand in the way.
        foreach (var plot in _plots.Where(p => p.Tiles is not null))
        {
            var clearable = new bool[plot.Size, plot.Size];
            var blocked = Blocked(plot);
            for (var y = 0; y < plot.Size; y++)
            {
                for (var x = 0; x < plot.Size; x++)
                {
                    clearable[x, y] = blocked[x, y] && !plot.Busy.Contains((x, y)) && plot.Tiles![y * plot.Size + x] is Tiles.Tree or Tiles.Rock or Tiles.Dune;
                    blocked[x, y] &= !clearable[x, y];
                }
            }

            if (BotFactory.FindSpot(blocked, clearable, plot.Spawn) is { } spot)
            {
                plot.Clearing.Clear();
                plot.Clearing.AddRange(BotFactory.Ground(spot.X, spot.Y).Where(t => clearable[t.X, t.Y]));
                return true;
            }
        }

        _placeFailures++;
        return false;
    }

    /// <summary>Fells or breaks the next thing on the ground the line is to stand on.</summary>
    private async Task<bool> ClearAsync(BotPlot plot, CancellationToken cancellationToken)
    {
        plot.Clearing.RemoveAll(t => plot.Tiles![t.Y * plot.Size + t.X] == Tiles.None);
        if (plot.Clearing.Count == 0)
        {
            return false;
        }

        var next = plot.Clearing[0];
        plot.Targets.Remove(next);
        plot.Targets.Insert(0, next);
        if (await HarvestAsync(plot, cancellationToken))
        {
            return true;
        }

        if (plot.Tiles![next.Y * plot.Size + next.X] != Tiles.None && !plot.Targets.Contains(next))
        {
            // Out of the bot's reach (no tool for it): it gives the clearing up and looks for ground without it.
            plot.Clearing.Clear();
            plot.Busy.Add(next);
            _placeFailures++;
        }

        return false;
    }

    /// <summary>
    /// Looks after the line: takes the products out, fills the input chest, keeps the generator burning and sets the
    /// machine's recipe. When nothing came out twice in a row although everything was there, the line is stalled and the
    /// bot asks players for help; without fuel it cannot get, it asks for fuel.
    /// </summary>
    private async Task<bool> TendAsync(BotLine line, BotLineSite site, BotWorld world, CancellationToken cancellationToken)
    {
        _tendAt = world.Now + BotRules.TendEvery;
        var (x, y) = (site.Plot.X, site.Plot.Y);
        var output = await SendAsync("storage.get", new { x, y, id = site.Output }, cancellationToken);
        if (!output.Is("storage"))
        {
            // Gone (or taken down): the bot looks at the plot again.
            _site = null;
            site.Plot.Tiles = null;
            return false;
        }

        long taken = 0;
        foreach (var (item, quantity) in Stored(output.Payload))
        {
            if ((await SendAsync("storage.take", new { x, y, id = site.Output, item, quantity = 0 }, cancellationToken)).Ok)
            {
                taken += quantity;
            }
        }

        var input = await SendAsync("storage.get", new { x, y, id = site.Input }, cancellationToken);
        var inside = input.Is("storage") ? Stored(input.Payload) : [];
        var before = line.Inputs.Sum(i => inside.GetValueOrDefault(i));
        await InventoryAsync(cancellationToken);

        // The generator first: without power the inserters stand.
        var generator = await SendAsync("generator.get", new { x, y, id = site.Generator }, cancellationToken);
        var fuel = generator.Payload.Get("fuel")?.Long("quantity") ?? 0;
        var burning = generator.Payload.Get("fuel")?.Text("id");
        if (fuel < 10)
        {
            var spare = line.Fuels.FirstOrDefault(f => (burning is null || burning == f) && Have(f) > 0);
            if (spare is not null)
            {
                var loaded = await SendAsync("generator.load", new { x, y, id = site.Generator, item = spare, quantity = Math.Min(Have(spare), 20) }, cancellationToken);
                if (loaded.Ok)
                {
                    fuel += Math.Min(Have(spare), 20);
                    _inventory[spare] = Have(spare) - Math.Min(Have(spare), 20);
                }
            }
        }

        // A furnace gets its coal from the bot's hand: coal in the chest would sit behind the ore once the furnace is full of it.
        var heated = true;
        if (line.Machine == Items.Furnace)
        {
            var furnace = await SendAsync("furnace.get", new { furnace = site.Machine }, cancellationToken);
            var coal = furnace.Payload.Long("fuel");
            if (coal < 10 && Have(Items.Coal) > 0)
            {
                var quantity = Math.Min(Have(Items.Coal), 20);
                if ((await SendAsync("furnace.load", new { furnace = site.Machine, item = Items.Coal, quantity }, cancellationToken)).Ok)
                {
                    coal += quantity;
                    _inventory[Items.Coal] = Have(Items.Coal) - quantity;
                }
            }

            heated = coal > 0;
        }

        foreach (var (item, quantity) in Refill(line, inside))
        {
            if ((await SendAsync("storage.put", new { x, y, id = site.Input, item, quantity }, cancellationToken)).Ok)
            {
                inside[item] = inside.GetValueOrDefault(item) + quantity;
                _inventory[item] = Have(item) - quantity;
            }
        }

        if (line.Recipe is { } recipe && !_recipeSet)
        {
            _recipeSet = (await SendAsync("machine.recipe", new { machine = site.Machine, recipe }, cancellationToken)).Ok;
        }

        // Stalled: it had all it needs since the last look, and used none of it.
        var complete = heated && line.Inputs.Any(i => inside.GetValueOrDefault(i) > 0);
        _stalls = taken == 0 && fuel > 0 && complete && _lastInputs >= 0 && before >= _lastInputs ? _stalls + 1 : 0;
        _lastInputs = line.Inputs.Sum(i => inside.GetValueOrDefault(i));

        if (_stalls >= BotRules.StalledAfter)
        {
            await StallAsync(site, world, cancellationToken);
        }

        return true;
    }

    /// <summary>What to put into the input chest: one kind of ore at a time for a furnace (mixed, the inserter would jam with the
    /// other kind in its hand), the grain or the wood for the others.</summary>
    private List<(string Item, long Quantity)> Refill(BotLine line, Dictionary<string, long> inside)
    {
        var puts = new List<(string, long)>();
        if (line.Machine == Items.Furnace)
        {
            var ore = BotRules.Ores.FirstOrDefault(o => inside.GetValueOrDefault(o) > 0)
                ?? BotRules.Ores.Where(o => Have(o) > 0).OrderByDescending(Have).FirstOrDefault();
            if (ore is not null && inside.GetValueOrDefault(ore) < 20 && Have(ore) > 0)
            {
                puts.Add((ore, Math.Min(Have(ore), 40)));
            }

            return puts;
        }

        var input = line.Inputs[0];
        // Wood is the sawmill's fuel too: some stays back for the generator.
        var spare = Have(input) - (line.Fuels.Contains(input) ? 20 : 0);
        if (inside.GetValueOrDefault(input) < 30 && spare > 0)
        {
            puts.Add((input, Math.Min(spare, 60)));
        }

        return puts;
    }

    private static Dictionary<string, long> Stored(JsonElement storage) =>
        storage.Each("items").GroupBy(i => i.Text("id")!).ToDictionary(g => g.Key, g => g.Sum(i => i.Long("quantity")), StringComparer.Ordinal);

    /// <summary>
    /// The line stands still: the bot takes the belts and inserters up (whatever jammed them comes back to the inventory) and
    /// puts them back, all but the inserter that feeds the machine, which it asks players to put in place.
    /// </summary>
    private async Task StallAsync(BotLineSite site, BotWorld world, CancellationToken cancellationToken)
    {
        var (x, y) = (site.Plot.X, site.Plot.Y);
        var input = site.Parts.First(p => p.Id == site.Input);
        var feeder = (X: input.X + BotFactory.MachineX - 1, Y: input.Y);
        var moving = site.Parts.Where(p => p.Kind is Items.Inserter or Items.Belt).ToList();
        foreach (var part in moving)
        {
            await SendAsync("plot.pickup", new { x, y, id = part.Id }, cancellationToken);
        }

        var objects = moving.Where(p => (p.X, p.Y) != feeder)
            .Select(p => new { kind = p.Kind, tileX = p.X, tileY = p.Y, rotation = BotFactory.East }).ToList();
        if (objects.Count > 0)
        {
            await SendAsync("plot.placeMany", new { x, y, objects }, cancellationToken);
        }

        _fixes.Clear();
        _fixes.Add(new HelpTask(Items.Inserter, feeder.X, feeder.Y, BotFactory.East));
        _fixReason = HelpReasons.Stalled;
        _fixesSince = world.Now;
        await InventoryAsync(cancellationToken);
        LookAgain(site);
        _logger.LogInformation("Bot {Bot} took its stalled line on {X}:{Y} apart", Row.CharacterName, x, y);
    }

    /// <summary>
    /// Puts up a call for help over the plot: an exclamation mark on the world map, as long as the bot can pay. A player
    /// who sets everything right gets the fee, and for each part they put up what the trader pays for it and a quarter more,
    /// never more than the trader charges for the parts. A call the bot left up (a player is at it) it takes over again.
    /// </summary>
    private bool Ask(string reason, BotPlot plot, BotWorld world)
    {
        if (_help is not null || _fixes.Count == 0)
        {
            return false;
        }

        if (_commons.Help.Of(Row.Name) is { } standing)
        {
            _help = standing;
            _helpUntil = world.Now + BotRules.HelpWait;
            return true;
        }

        var parts = _fixes.Where(f => f.Object is null).Select(f => f.Kind).ToList();
        var pay = BotRules.HelpFee + parts.Sum(p => (long)Math.Ceiling(Math.Max(Floor(p), 1) * BotRules.HelpMarkup));
        // Never more than the trader charges for the same parts, so buying them from him to bring them here pays nobody.
        if (parts.Count > 0 && parts.All(_traderSells.ContainsKey))
        {
            pay = Math.Min(pay, (long)(parts.Sum(p => _traderSells[p]) * BotRules.HelpBelowTrader));
        }

        if (pay + BotRules.Reserve > _money)
        {
            return false;
        }

        _help = new HelpRequest(Row.Name, Row.CharacterName, Row.AccountId, plot.X, plot.Y, reason, _fixes.ToList(), pay, world.Now);
        _helpUntil = world.Now + BotRules.HelpWait;
        _commons.Help.Post(_help);
        _logger.LogInformation("Bot {Bot} asks for help on {X}:{Y}: {Reason}", Row.CharacterName, plot.X, plot.Y, reason);
        return true;
    }

    /// <summary>A player set the line right on the bot's plot and was paid: the bot looks at its line again and tends it.</summary>
    private async Task<string> HelpedAsync(HelpRequest answered, BotWorld world, CancellationToken cancellationToken)
    {
        _help = null;
        _fixes.Clear();
        _wantingSince = default;
        await InventoryAsync(cancellationToken);
        _money = (await SendAsync("wallet.get", null, cancellationToken)).Payload.Long("balanceCents");
        if (_site is { } site)
        {
            LookAgain(site);
        }
        else if (_plots.FirstOrDefault(p => p.X == answered.X && p.Y == answered.Y) is { } plot)
        {
            plot.Tiles = null;
        }

        return "helped: line set right";
    }

    /// <summary>The market overview: one bot fetches it, and the others use it for a few seconds, as the server sums it up that often anyway.</summary>
    private async Task<JsonElement?> OverviewAsync(BotWorld world, CancellationToken cancellationToken)
    {
        if (_commons.Overview is { } kept && world.Now - kept.At < BotRules.OverviewFor && world.Now >= kept.At)
        {
            return kept.Overview;
        }

        var reply = await SendAsync("market.overview", null, cancellationToken);
        if (!reply.Is("market.overview"))
        {
            return null;
        }

        _commons.Overview = (reply.Payload, world.Now);
        return reply.Payload;
    }
}
