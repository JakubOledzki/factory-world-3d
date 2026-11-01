using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Logistics;

/// <summary>
/// The loading and receiving chests as freight trains between plots see them. They are storages like any other chest; a train
/// takes from every loading chest of the plot and puts into the receiving chests, first those whose filter is set to the item, then
/// those that take anything, in the order they stand (top row first).
/// </summary>
public sealed class FreightChests(GameDb db, LogisticsService logistics) : IFreightChests
{
    public Task<IReadOnlyDictionary<string, long>> LoadingAsync(int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await logistics.RunAsync(x, y, cancellationToken);
            return (IReadOnlyDictionary<string, long>)Chests(state, Items.IsLoadingChest)
                .SelectMany(c => c.Contents.Items ?? [])
                .GroupBy(s => s.Item)
                .ToDictionary(g => g.Key, g => g.Sum(s => (long)s.Quantity));
        }, cancellationToken);

    public Task<bool> TakeAsync(int x, int y, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await logistics.RunAsync(x, y, cancellationToken);
            var chests = Chests(state, Items.IsLoadingChest);
            foreach (var (item, quantity) in goods)
            {
                if (chests.Sum(c => (long)(c.Contents.Items?.FirstOrDefault(s => s.Item == item)?.Quantity ?? 0)) < quantity)
                {
                    return false;
                }
            }

            var changed = new List<(Guid, Contents)>();
            foreach (var (item, quantity) in goods)
            {
                var left = quantity;
                foreach (var (o, contents) in chests)
                {
                    if (left == 0)
                    {
                        break;
                    }

                    var taken = Storages.Remove(contents.Items ??= [], item, (int)Math.Min(left, int.MaxValue));
                    if (taken > 0)
                    {
                        left -= taken;
                        changed.Add((o.Id, contents));
                    }
                }
            }

            await logistics.WriteAsync(x, y, changed.DistinctBy(c => c.Item1).ToList(), cancellationToken);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> UnloadAsync(int x, int y, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await logistics.RunAsync(x, y, cancellationToken);
            var chests = Chests(state, Items.IsReceivingChest);
            var left = new Dictionary<string, long>();
            var changed = new List<(Guid, Contents)>();
            foreach (var (item, quantity) in goods)
            {
                var rest = quantity;
                // Chests set to take this item first, then those that take anything.
                foreach (var (o, contents) in chests.Where(c => c.Contents.Only == item).Concat(chests.Where(c => c.Contents.Only is null)))
                {
                    if (rest == 0)
                    {
                        break;
                    }

                    var stacks = contents.Items ??= [];
                    var put = (int)Math.Min(rest, Storages.Room(Storages.Find(o.Kind)!, stacks, item));
                    if (put > 0)
                    {
                        Storages.Add(stacks, item, put);
                        rest -= put;
                        changed.Add((o.Id, contents));
                    }
                }

                if (rest > 0)
                {
                    left[item] = rest;
                }
            }

            await logistics.WriteAsync(x, y, changed.DistinctBy(c => c.Item1).ToList(), cancellationToken);
            return (IReadOnlyDictionary<string, long>)left;
        }, cancellationToken);

    private static List<(PlotObject Object, Contents Contents)> Chests(LogisticsState state, Func<string, bool> kind) =>
        state.Objects.Where(o => kind(o.Kind)).OrderBy(o => o.TileY).ThenBy(o => o.TileX)
            .Select(o => (o, state.Contents.GetValueOrDefault(o.Id) ?? new Contents())).ToList();
}

/// <summary>Every storage on a plot (chests, piles, silos, tanks) as a police search sees it.</summary>
public sealed class PlotStorage(GameDb db, LogisticsService logistics) : IPlotStorage
{
    public Task<IReadOnlyDictionary<string, long>> ContentsAsync(int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await logistics.RunAsync(x, y, cancellationToken);
            return (IReadOnlyDictionary<string, long>)Stored(state)
                .SelectMany(c => c.Contents.Items ?? [])
                .GroupBy(s => s.Item)
                .ToDictionary(g => g.Key, g => g.Sum(s => (long)s.Quantity));
        }, cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> SeizeAsync(int x, int y, IReadOnlyDictionary<string, long> goods, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var state = await logistics.RunAsync(x, y, cancellationToken);
            var storages = Stored(state);
            var taken = new Dictionary<string, long>();
            var changed = new List<(Guid, Contents)>();
            foreach (var (item, quantity) in goods)
            {
                var left = quantity;
                foreach (var (o, contents) in storages)
                {
                    if (left == 0)
                    {
                        break;
                    }

                    var n = Storages.Remove(contents.Items ??= [], item, (int)Math.Min(left, int.MaxValue));
                    if (n > 0)
                    {
                        left -= n;
                        taken[item] = taken.GetValueOrDefault(item) + n;
                        changed.Add((o.Id, contents));
                    }
                }
            }

            await logistics.WriteAsync(x, y, changed.DistinctBy(c => c.Item1).ToList(), cancellationToken);
            return (IReadOnlyDictionary<string, long>)taken;
        }, cancellationToken);

    private static List<(PlotObject Object, Contents Contents)> Stored(LogisticsState state) =>
        state.Objects.Where(o => Storages.Find(o.Kind) is not null && state.Contents.ContainsKey(o.Id))
            .Select(o => (o, state.Contents[o.Id])).ToList();
}
