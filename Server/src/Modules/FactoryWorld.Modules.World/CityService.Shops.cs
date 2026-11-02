using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>The town's shops on players' plots: their stock, customers and takings.</summary>
public sealed partial class CityService
{
    // --- Shops ---------------------------------------------------------------------------------------

    /// <summary>A shop's plot, owner and kind, or a failure when the id is no shop.</summary>
    private async Task<(int X, int Y, Guid Owner)> ShopAtAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT x, y, owner_id, kind FROM world.plot_objects WHERE id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(3) != Items.Shop)
        {
            throw new GameException("no_shop", "There is no such shop.");
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2));
    }

    private async Task<List<ShopItemRow>> ShopItemsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT item, quantity, price_cents FROM world.shop_items WHERE shop_id = @id ORDER BY item", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ShopItemRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ShopItemRow { ShopId = id, Item = reader.GetString(0), Quantity = reader.GetInt64(1), PriceCents = reader.GetInt64(2) });
        }

        return items;
    }

    private async Task SaveShopItemAsync(ShopItemRow row, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            row.Quantity > 0
                ? """
                  INSERT INTO world.shop_items (shop_id, item, quantity, price_cents) VALUES (@id, @item, @quantity, @price)
                  ON CONFLICT (shop_id, item) DO UPDATE SET quantity = excluded.quantity, price_cents = excluded.price_cents
                  """
                : "DELETE FROM world.shop_items WHERE shop_id = @id AND item = @item",
            cancellationToken);
        command.Parameters.AddWithValue("id", row.ShopId);
        command.Parameters.AddWithValue("item", row.Item);
        command.Parameters.AddWithValue("quantity", row.Quantity);
        command.Parameters.AddWithValue("price", row.PriceCents);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<double> CustomersAsync(int x, int y, CancellationToken cancellationToken)
    {
        var town = towns.Of(x, y);
        var level = CityRules.Level(await DeliveredAsync(town, cancellationToken));
        var shoppers = (await PolicyAsync(town, cancellationToken)).Bonus == "shoppers";
        var hall = (1 + TownHallRules.ParkCustomers * await InvestmentAsync(town, "park", cancellationToken))
            * (await InForceAsync(town, "late_shops", cancellationToken) ? TownHallRules.LateShops : 1);
        return CityRules.Customers(town, level, CityRules.Distance(map.Width, x, y, town.X, town.Y), shoppers) * hall;
    }

    /// <summary>Works out what the townspeople bought in the shop since it was last looked at: each customer picks one of its
    /// goods and buys a piece if the price is right. The money goes to the owner, less the mayor's tax.</summary>
    private async Task<(Guid Owner, int X, int Y)> SettleAsync(Guid id, CancellationToken cancellationToken)
    {
        var (x, y, owner) = await ShopAtAsync(id, cancellationToken);
        var now = time.GetUtcNow();
        await using (var create = await db.CommandAsync(
            "INSERT INTO world.shops (id, settled_at, carry, sold_cents, sold_items) VALUES (@id, @now, 0, 0, 0) ON CONFLICT DO NOTHING", cancellationToken))
        {
            create.Parameters.AddWithValue("id", id);
            create.Parameters.AddWithValue("now", now);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        DateTimeOffset settled;
        double carry;
        await using (var read = await db.CommandAsync("SELECT settled_at, carry FROM world.shops WHERE id = @id FOR UPDATE", cancellationToken))
        {
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            (settled, carry) = (reader.GetFieldValue<DateTimeOffset>(0), reader.GetDouble(1));
        }

        if (now <= settled)
        {
            return (owner, x, y);
        }

        var stock = await ShopItemsAsync(id, cancellationToken);
        var hours = (now - settled < CityRules.ShopCatchUp ? now - settled : CityRules.ShopCatchUp).TotalHours;
        var expected = carry + (stock.Count > 0 ? (await CustomersAsync(x, y, cancellationToken)) * hours : 0);
        var customers = (long)Math.Floor(expected);
        carry = expected - customers;
        var random = new Random(HashCode.Combine(id, settled.UtcTicks));
        long revenue = 0, sold = 0;
        var changed = new HashSet<ShopItemRow>();
        for (var i = 0; i < customers; i++)
        {
            var shelf = stock.Where(s => s.Quantity > 0).ToList();
            if (shelf.Count == 0)
            {
                carry = 0;
                break;
            }

            var pick = shelf[random.Next(shelf.Count)];
            var trader = prices.BuysCents(pick.Item) ?? 0;
            if (trader > 0 && random.NextDouble() < CityRules.Accepts(pick.PriceCents, trader, prices.CheapestSellsCents(pick.Item)))
            {
                pick.Quantity--;
                revenue += pick.PriceCents;
                sold++;
                changed.Add(pick);
            }
        }

        foreach (var row in changed)
        {
            await SaveShopItemAsync(row, cancellationToken);
        }

        await using (var update = await db.CommandAsync(
            """
            UPDATE world.shops SET settled_at = @now, carry = @carry, sold_cents = sold_cents + @revenue, sold_items = sold_items + @sold
            WHERE id = @id
            """, cancellationToken))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("carry", carry);
            update.Parameters.AddWithValue("revenue", revenue);
            update.Parameters.AddWithValue("sold", sold);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await PayAsync(towns.Of(x, y), owner, null, revenue, $"shop sales: {sold} items", cancellationToken);
        return (owner, x, y);
    }

    private async Task<object> ShopStateAsync(Guid id, int x, int y, CancellationToken cancellationToken)
    {
        var stock = await ShopItemsAsync(id, cancellationToken);
        long soldCents = 0, soldItems = 0;
        await using (var command = await db.CommandAsync("SELECT sold_cents, sold_items FROM world.shops WHERE id = @id", cancellationToken))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                (soldCents, soldItems) = (reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        var town = towns.Of(x, y);
        return new
        {
            id,
            town = town.Name,
            customersPerHour = Math.Round(await CustomersAsync(x, y, cancellationToken), 1),
            soldCents,
            soldItems,
            maxKinds = CityRules.ShopKinds,
            maxStock = CityRules.ShopMaxStock,
            taxPercent = (await PolicyAsync(town, cancellationToken)).TaxPercent,
            items = stock.Select(s => new
            {
                id = s.Item,
                name = Items.Names.GetValueOrDefault(s.Item, s.Item),
                quantity = s.Quantity,
                priceCents = s.PriceCents,
                traderCents = prices.BuysCents(s.Item),
                buyPercent = prices.BuysCents(s.Item) is { } trader ? (int)Math.Round(100 * CityRules.Accepts(s.PriceCents, trader, prices.CheapestSellsCents(s.Item))) : 0,
            }),
        };
    }

    public Task<object> ShopAsync(Guid player, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (_, x, y) = await SettleAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            return await ShopStateAsync(id, x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Puts goods from the inventory on the shop's shelves at a price (which then holds for all of that item there).</summary>
    public Task<object> StockShopAsync(Guid player, Guid id, string item, long quantity, long priceCents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (_, x, y) = await SettleAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            if (prices.BuysCents(item ?? "") is null)
            {
                throw new GameException("not_sellable", "Nobody in town buys this.");
            }

            if (priceCents < 1)
            {
                throw new GameException("bad_price", "Set a price.");
            }

            var stock = await ShopItemsAsync(id, cancellationToken);
            var row = stock.FirstOrDefault(s => s.Item == item);
            if (row is null && stock.Count >= CityRules.ShopKinds)
            {
                throw new GameException("shop_full", $"A shop holds {CityRules.ShopKinds} kinds of goods.");
            }

            row ??= new ShopItemRow { ShopId = id, Item = item! };
            if (quantity < 0 || row.Quantity + quantity > CityRules.ShopMaxStock)
            {
                throw new GameException("bad_quantity", $"A shop holds at most {CityRules.ShopMaxStock} of each.");
            }

            if (quantity > 0)
            {
                await inventory.RemoveAsync(player, item!, quantity, cancellationToken);
            }

            row.Quantity += quantity;
            row.PriceCents = priceCents;
            if (row.Quantity == 0)
            {
                throw new GameException("bad_quantity", "Put at least one on the shelf.");
            }

            await SaveShopItemAsync(row, cancellationToken);
            return await ShopStateAsync(id, x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes goods off the shop's shelves back into the inventory, as many as it has room for; the rest stays.</summary>
    public Task<object> TakeFromShopAsync(Guid player, Guid id, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (_, x, y) = await SettleAsync(id, cancellationToken);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            var row = (await ShopItemsAsync(id, cancellationToken)).FirstOrDefault(s => s.Item == item);
            if (row is null || quantity < 1 || quantity > row.Quantity)
            {
                throw new GameException("bad_quantity", "The shop has not so many.");
            }

            var taken = await inventory.FitAsync(player, item, quantity, cancellationToken);
            row.Quantity -= taken;
            await SaveShopItemAsync(row, cancellationToken);
            await inventory.AddAsync(player, item, taken, cancellationToken);
            return await ShopStateAsync(id, x, y, cancellationToken);
        }, cancellationToken);

    /// <summary>A shop picked up (it is gone from the plot by now): what is left on its shelves goes back to the owner.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        foreach (var row in await ShopItemsAsync(id, cancellationToken))
        {
            await inventory.AddAsync(owner, row.Item, row.Quantity, cancellationToken);
        }

        await using var delete = await db.CommandAsync(
            "DELETE FROM world.shop_items WHERE shop_id = @id; DELETE FROM world.shops WHERE id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }
}
