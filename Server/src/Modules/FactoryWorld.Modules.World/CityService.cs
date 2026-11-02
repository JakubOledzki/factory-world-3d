using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

public sealed partial class CityService(
    GameDb db, WorldMap map, Towns towns, PlotService plots, IInventory inventory, IWallet wallet, ICharacterProgress progress,
    ICharacterNames names, IMarketPrices prices, BeachService beach, IAnnouncements announcements, Countries countries,
    INationalPolicy nation, IServiceProvider services, TimeProvider time) : IMachineContents
{
    /// <summary>The town of a city plot; fails for any other plot.</summary>
    public Town RequireTown(int x, int y)
    {
        if (!map.Contains(x, y) || map.BiomeAt(x, y) != Biome.City)
        {
            throw new GameException("not_city", "This plot is not in a city.");
        }

        return towns.Of(x, y);
    }

    public Town? TownOf(int x, int y) => map.Contains(x, y) && map.BiomeAt(x, y) == Biome.City ? towns.Of(x, y) : null;

    /// <summary>The town whose county the plot lies in, whose mayor's laws hold there: a city plot's own town, or the town that
    /// governs the countryside around (<see cref="Towns.CountyOf"/>); null for the sea and land no town reaches.</summary>
    public Town? CountyOf(int x, int y) => TownOf(x, y) ?? (map.Contains(x, y) ? towns.CountyOf(x, y) : null);

    /// <summary>The town whose county the plot lies in; fails for a plot in none.</summary>
    public Town RequireCounty(int x, int y) => CountyOf(x, y) ?? throw new GameException("not_county", "This plot is in no town's county.");

    public async Task<long> DeliveredAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT delivered_cents FROM world.town_progress WHERE town_x = @x AND town_y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        return await command.ExecuteScalarAsync(cancellationToken) is long cents ? cents : 0;
    }

    /// <summary>The mayor and their policy this week: the winner of last week's vote, worked out once and kept.</summary>
    public async Task<TownPolicy> PolicyAsync(Town town, CancellationToken cancellationToken)
    {
        var week = CityRules.WeekOf(time.GetUtcNow());
        if (await ReadPolicyAsync(town, week, cancellationToken) is { } policy)
        {
            return policy;
        }

        Guid? mayor = null;
        await using (var count = await db.CommandAsync(
            """
            SELECT candidate_id FROM world.town_votes WHERE town_x = @x AND town_y = @y AND week = @week
            GROUP BY candidate_id ORDER BY count(*) DESC, candidate_id LIMIT 1
            """, cancellationToken))
        {
            count.Parameters.AddWithValue("x", town.X);
            count.Parameters.AddWithValue("y", town.Y);
            count.Parameters.AddWithValue("week", week - 1);
            mayor = await count.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        // The first look in a new week also counts the votes of the weeks before for those who stood, so their deposits are settled.
        return await db.InTransactionAsync(async () =>
        {
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.town_policies (town_x, town_y, week, mayor_id, tax_percent, bonus, recalled) VALUES (@x, @y, @week, @mayor, 0, NULL, FALSE)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("x", town.X);
                insert.Parameters.AddWithValue("y", town.Y);
                insert.Parameters.AddWithValue("week", week);
                insert.Parameters.AddWithValue("mayor", (object?)mayor ?? DBNull.Value);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    await SettleCandidatesAsync(town, week, cancellationToken);
                }
            }

            return (await ReadPolicyAsync(town, week, cancellationToken))!;
        }, cancellationToken);
    }

    private async Task<TownPolicy?> ReadPolicyAsync(Town town, int week, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT mayor_id, tax_percent, bonus, recalled, plot_tax_per_mille FROM world.town_policies WHERE town_x = @x AND town_y = @y AND week = @week",
            cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TownPolicy(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3),
                reader.GetInt32(4))
            : null;
    }

    /// <summary>What the town wants today, how much of it came already and what it pays a piece.</summary>
    public async Task<IReadOnlyList<(string Item, long Wanted, long Delivered, long PriceCents)>> NeedsAsync(
        Town town, int level, TownPolicy policy, CancellationToken cancellationToken)
    {
        var day = CityRules.DayOf(time.GetUtcNow());
        var delivered = await DayDeliveriesAsync(town, day, cancellationToken);
        var festival = await FestivalUntilAsync(town, cancellationToken) > time.GetUtcNow();
        var hall = 1 + TownHallRules.MarketHallPremium * await InvestmentAsync(town, "market_hall", cancellationToken);
        return CityRules.Needs(town, level, day, item => prices.BuysCents(item) is not null)
            .Select(n => (n.Item, n.Wanted, delivered.GetValueOrDefault(n.Item),
                BelowTrader(n.Item, (long)Math.Round(CityRules.NeedPriceCents(prices.BuysCents(n.Item)!.Value, level, policy.Bonus == "demand", festival) * hall))))
            .ToList();
    }

    /// <summary>What the town pays a piece, less than the trader asks for it even in a sale: the town pays from nothing, and
    /// goods bought from the trader to deliver here must not pay.</summary>
    private long BelowTrader(string item, long priceCents) =>
        prices.CheapestSellsCents(item) is { } cheapest ? Math.Min(priceCents, cheapest * CityRules.MaxPayOfChargePercent / 100) : priceCents;

    /// <summary>When the town's festival ends, or null when it never held one.</summary>
    public async Task<DateTimeOffset?> FestivalUntilAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT until FROM world.town_festivals WHERE town_x = @x AND town_y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        return await command.ExecuteScalarAsync(cancellationToken) is DateTimeOffset until ? until : null;
    }

    /// <summary>The mayor holds a festival: <see cref="CityRules.FestivalDishes"/> fine dishes from their inventory, roasts first, and the town
    /// pays more for what it wants for <see cref="CityRules.FestivalTime"/> (from when the one going on ends).</summary>
    public Task<DateTimeOffset> FestivalAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            if ((await PolicyAsync(town, cancellationToken)).Mayor != player)
            {
                throw new GameException("not_mayor", "Only this week's mayor holds a festival.");
            }

            var roasts = await inventory.CountAsync(player, Items.RoastBeef, cancellationToken);
            var cakes = await inventory.CountAsync(player, Items.Cheesecake, cancellationToken);
            if (roasts + cakes < CityRules.FestivalDishes)
            {
                throw new GameException("banquet_needed", $"A festival takes {CityRules.FestivalDishes} fine dishes: roast beef or cheesecake.");
            }

            var fromRoasts = Math.Min(roasts, CityRules.FestivalDishes);
            if (fromRoasts > 0)
            {
                await inventory.RemoveAsync(player, Items.RoastBeef, fromRoasts, cancellationToken);
            }

            if (CityRules.FestivalDishes - fromRoasts > 0)
            {
                await inventory.RemoveAsync(player, Items.Cheesecake, CityRules.FestivalDishes - fromRoasts, cancellationToken);
            }

            var now = time.GetUtcNow();
            var until = (await FestivalUntilAsync(town, cancellationToken) is { } going && going > now ? going : now) + CityRules.FestivalTime;
            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO world.town_festivals (town_x, town_y, until) VALUES (@x, @y, @until)
                ON CONFLICT (town_x, town_y) DO UPDATE SET until = @until
                """, cancellationToken);
            upsert.Parameters.AddWithValue("x", town.X);
            upsert.Parameters.AddWithValue("y", town.Y);
            upsert.Parameters.AddWithValue("until", until);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
            await progress.AddXpAsync(player, (int)CityRules.FestivalDishes, cancellationToken);
            return until;
        }, cancellationToken);

    private async Task<Dictionary<string, long>> DayDeliveriesAsync(Town town, int day, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT item, quantity FROM world.town_deliveries WHERE town_x = @x AND town_y = @y AND day = @day", cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("day", day);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var delivered = new Dictionary<string, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            delivered[reader.GetString(0)] = reader.GetInt64(1);
        }

        return delivered;
    }

    private async Task AddDayDeliveryAsync(Town town, int day, string item, long quantity, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            INSERT INTO world.town_deliveries (town_x, town_y, day, item, quantity) VALUES (@x, @y, @day, @item, @quantity)
            ON CONFLICT (town_x, town_y, day, item) DO UPDATE SET quantity = world.town_deliveries.quantity + excluded.quantity
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("day", day);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("quantity", quantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>One town at a time changes, so two deliveries cannot both fill the same need.</summary>
    private async Task LockTownAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken);
        command.Parameters.AddWithValue("key", 0x746f776eL << 32 | (long)(town.X & 0xffff) << 16 | (long)(town.Y & 0xffff));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Delivers goods the town wants, from the inventory or from the station of the city plot: the town pays for as many as it still
    /// wants today, grows on them, and the player gets experience.</summary>
    public Task<(long Quantity, long Cents, long Subsidy, XpGain Xp, int Level)> DeliverAsync(
        Guid player, int x, int y, string item, long quantity, bool fromStation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireTown(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, fromStation ? PlotRight.Take : PlotRight.Work);
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Deliver at least one.");
            }

            await LockTownAsync(town, cancellationToken);
            var before = CityRules.Level(await DeliveredAsync(town, cancellationToken));
            var policy = await PolicyAsync(town, cancellationToken);
            var need = (await NeedsAsync(town, before, policy, cancellationToken)).FirstOrDefault(n => n.Item == item);
            if (need.Item is null)
            {
                throw new GameException("not_wanted", "The town does not want this today.");
            }

            var taken = Math.Min(quantity, need.Wanted - need.Delivered);
            if (taken <= 0)
            {
                throw new GameException("need_met", "The town has got all it wants of this today.");
            }

            if (fromStation)
            {
                await StationStock.ChangeAsync(db, x, y, item, -taken, cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(player, item, taken, cancellationToken);
            }

            // The town's chief supplier of last week gets a little more for everything it wants.
            var price = await SupplierAsync(town, CityRules.WeekOf(time.GetUtcNow()), cancellationToken) == player
                ? BelowTrader(item, CityRules.SupplierPriceCents(need.PriceCents))
                : need.PriceCents;
            var cents = taken * price;
            await AddDayDeliveryAsync(town, CityRules.DayOf(time.GetUtcNow()), item, taken, cancellationToken);
            await GrowAsync(town, player, cents, cancellationToken);
            await wallet.CreditAsync(player, cents, $"town {town.Name}: {taken} {item}", cancellationToken);
            var xp = await progress.AddXpAsync(player, Math.Max(1, cents / 1000), cancellationToken);
            // The country's subsidy for the goods, if its government pays one, from its treasury.
            var subsidy = countries.At(x, y) is { } country ? await nation.SubsidizeAsync(player, country.Code, item, cents, cancellationToken) : 0;
            return (taken, cents, subsidy, xp, CityRules.Level(await DeliveredAsync(town, cancellationToken)));
        }, cancellationToken);

    /// <summary>Takes rubbish away from the town into the city plot's station, for a fee the town pays; the town has only so much a day.</summary>
    public Task<(long Quantity, long Cents)> TakeTrashAsync(Guid player, int x, int y, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireTown(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Take);
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Take at least one.");
            }

            await LockTownAsync(town, cancellationToken);
            var day = CityRules.DayOf(time.GetUtcNow());
            var left = CityRules.TrashPerDay(town, CityRules.Level(await DeliveredAsync(town, cancellationToken)))
                - (await DayDeliveriesAsync(town, day, cancellationToken)).GetValueOrDefault(Items.Trash);
            var taken = Math.Min(quantity, left);
            if (taken <= 0)
            {
                throw new GameException("no_trash", "The town has no more rubbish today.");
            }

            await AddDayDeliveryAsync(town, day, Items.Trash, taken, cancellationToken);
            await StationStock.ChangeAsync(db, x, y, Items.Trash, taken, cancellationToken);
            var cents = taken * CityRules.TrashFeeCents;
            // With the recycling ordinance the treasury adds half again, as far as it has money.
            if (await InForceAsync(town, "recycling", cancellationToken))
            {
                var extra = Math.Min(cents * TownHallRules.RecyclingPercent / 100, await TreasuryAsync(town, cancellationToken));
                await ChangeTreasuryAsync(town, -extra, "program recyklingu", cancellationToken);
                cents += extra;
            }

            await wallet.CreditAsync(player, cents, $"town {town.Name}: {taken} rubbish taken away", cancellationToken);
            return (taken, cents);
        }, cancellationToken);

    /// <summary>The offices in the plots of a town that sell services.</summary>
    private async Task<IReadOnlyList<(Guid Id, int X, int Y, string Kind, Guid Owner)>> ServicesOfAsync(Town town, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT id, x, y, kind, owner_id FROM world.plot_objects
            WHERE kind = ANY(@kinds) AND x BETWEEN @x - 40 AND @x + 40 AND y BETWEEN @y - 40 AND @y + 40
            ORDER BY kind, id
            """, cancellationToken);
        command.Parameters.AddWithValue("kinds", CityRules.Services.Keys.ToArray());
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var found = new List<(Guid, int, int, string, Guid)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add((reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetGuid(4)));
        }

        return found.Where(s => TownOf(s.Item2, s.Item3) == town).ToList();
    }

    private async Task<int> SalesTodayAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT count FROM world.service_sales WHERE id = @id AND day = @day", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("day", CityRules.DayOf(time.GetUtcNow()));
        return await command.ExecuteScalarAsync(cancellationToken) is int count ? count : 0;
    }

    /// <summary>Pays an owner what came in, less the mayor's tax (unless the mayor is the one who paid or got paid), which goes into
    /// the treasury, all but the mayor's pay.</summary>
    private async Task PayAsync(Town town, Guid owner, Guid? payer, long cents, string reason, CancellationToken cancellationToken)
    {
        if (cents <= 0)
        {
            return;
        }

        var policy = await PolicyAsync(town, cancellationToken);
        var tax = policy.Mayor is { } mayor && mayor != owner && mayor != payer ? cents * policy.TaxPercent / 100 : 0;
        await wallet.CreditAsync(owner, cents - tax, reason, cancellationToken);
        var pay = tax * TownHallRules.MayorSharePercent / 100;
        if (pay > 0)
        {
            await wallet.CreditAsync(policy.Mayor!.Value, pay, $"mayor's pay in {town.Name}: {reason}", cancellationToken);
        }

        await ChangeTreasuryAsync(town, tax - pay, $"podatek: {reason}", cancellationToken);
    }

    /// <summary>Buys an office's service: the buyer pays, its owner gets a part (less the mayor's tax), and the buyer gets what it makes.</summary>
    public Task<(string Item, int Quantity, long Cents)> BuyServiceAsync(Guid player, int x, int y, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireTown(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
            var office = (await ServicesOfAsync(town, cancellationToken)).FirstOrDefault(s => s.Id == id);
            if (office.Kind is null)
            {
                throw new GameException("no_service", "There is no such office in this town.");
            }

            if (office.Owner == player)
            {
                throw new GameException("own_service", "This office is yours: make it yourself.");
            }

            await using (var lockRow = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockRow.Parameters.AddWithValue("key", BitConverter.ToInt64(id.ToByteArray()));
                await lockRow.ExecuteNonQueryAsync(cancellationToken);
            }

            var limit = await ServiceSalesPerDayAsync(town, cancellationToken);
            if (await SalesTodayAsync(id, cancellationToken) >= limit)
            {
                throw new GameException("service_busy", $"An office serves {limit} customers a day; come back tomorrow.");
            }

            var (item, quantity, price) = CityRules.Services[office.Kind];
            await wallet.DebitAsync(player, price, $"service {office.Kind} in {town.Name}", cancellationToken);
            await inventory.ReceiveAsync(player, item, quantity, cancellationToken);
            await PayAsync(town, office.Owner, player, price * CityRules.ServiceOwnerPercent / 100, $"service {office.Kind} sold", cancellationToken);
            await using (var count = await db.CommandAsync(
                """
                INSERT INTO world.service_sales (id, day, count) VALUES (@id, @day, 1)
                ON CONFLICT (id, day) DO UPDATE SET count = world.service_sales.count + 1
                """, cancellationToken))
            {
                count.Parameters.AddWithValue("id", id);
                count.Parameters.AddWithValue("day", CityRules.DayOf(time.GetUtcNow()));
                await count.ExecuteNonQueryAsync(cancellationToken);
            }

            return (item, quantity, price);
        }, cancellationToken);

    private async Task<int> ServiceSalesPerDayAsync(Town town, CancellationToken cancellationToken) =>
        await InForceAsync(town, "busy_offices", cancellationToken) ? TownHallRules.BusyOfficeSales : CityRules.ServiceSalesPerDay;

    /// <summary>The players who own a plot in the town's county: they elect its mayor and vote on its ordinances.</summary>
    private async Task<IReadOnlyList<Guid>> TownsfolkAsync(Town town, CancellationToken cancellationToken)
    {
        var (left, top, right, bottom) = towns.CountyBounds(town);
        await using var command = await db.CommandAsync(
            "SELECT x, y, owner_id FROM world.plots WHERE x BETWEEN @left AND @right AND y BETWEEN @top AND @bottom", cancellationToken);
        command.Parameters.AddWithValue("left", Math.Min(left, town.X));
        command.Parameters.AddWithValue("right", Math.Max(right, town.X));
        command.Parameters.AddWithValue("top", Math.Min(top, town.Y));
        command.Parameters.AddWithValue("bottom", Math.Max(bottom, town.Y));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var owners = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (CountyOf(reader.GetInt32(0), reader.GetInt32(1)) == town)
            {
                owners.Add(reader.GetGuid(2));
            }
        }

        return owners.Distinct().ToList();
    }

    /// <summary>Votes for next week's mayor: a player who owns a plot in the town votes for one who does too, once a week (a new vote replaces the old).</summary>
    public Task VoteAsync(Guid player, int x, int y, string candidate, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            var folk = await TownsfolkAsync(town, cancellationToken);
            if (!folk.Contains(player))
            {
                throw new GameException("not_townsman", "Only players who own a plot in the town's county vote here.");
            }

            var chosen = await names.AccountAsync(candidate ?? "", cancellationToken);
            if (chosen is not { } id || !folk.Contains(id))
            {
                throw new GameException("not_candidate", "Vote for a player who owns a plot in the town.");
            }

            await using var vote = await db.CommandAsync(
                """
                INSERT INTO world.town_votes (town_x, town_y, week, voter_id, candidate_id) VALUES (@x, @y, @week, @voter, @candidate)
                ON CONFLICT (town_x, town_y, week, voter_id) DO UPDATE SET candidate_id = excluded.candidate_id
                """, cancellationToken);
            vote.Parameters.AddWithValue("x", town.X);
            vote.Parameters.AddWithValue("y", town.Y);
            vote.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
            vote.Parameters.AddWithValue("voter", player);
            vote.Parameters.AddWithValue("candidate", id);
            await vote.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>The mayor sets this week's tax on shops and services, the town's bonus and, unless null, the tax on the county's plots.</summary>
    public Task SetPolicyAsync(Guid player, int x, int y, int taxPercent, string? bonus, int? plotTaxPerMille, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            if ((await PolicyAsync(town, cancellationToken)).Mayor != player)
            {
                throw new GameException("not_mayor", "Only this week's mayor sets the town's policy.");
            }

            if (taxPercent < 0 || taxPercent > CityRules.MaxTaxPercent)
            {
                throw new GameException("bad_tax", $"The tax is 0-{CityRules.MaxTaxPercent}%.");
            }

            if (plotTaxPerMille is < 0 or > CityRules.MaxPlotTaxPerMille)
            {
                throw new GameException("bad_plot_tax", $"The plot tax is 0-{CityRules.MaxPlotTaxPerMille} per mille a day.");
            }

            if (bonus is not null && !CityRules.Bonuses.ContainsKey(bonus))
            {
                throw new GameException("bad_bonus", "There is no such bonus.");
            }

            await using var update = await db.CommandAsync(
                """
                UPDATE world.town_policies SET tax_percent = @tax, bonus = @bonus, plot_tax_per_mille = COALESCE(@plot_tax, plot_tax_per_mille)
                WHERE town_x = @x AND town_y = @y AND week = @week
                """, cancellationToken);
            update.Parameters.AddWithValue("plot_tax", NpgsqlDbType.Integer, (object?)plotTaxPerMille ?? DBNull.Value);
            update.Parameters.AddWithValue("x", town.X);
            update.Parameters.AddWithValue("y", town.Y);
            update.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
            update.Parameters.AddWithValue("tax", taxPercent);
            update.Parameters.AddWithValue("bonus", (object?)bonus ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Everything the town window shows, from any plot of the town's county.</summary>
    public async Task<object> ViewAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        var town = RequireCounty(x, y);
        await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Work);
        var now = time.GetUtcNow();
        var delivered = await DeliveredAsync(town, cancellationToken);
        var level = CityRules.Level(delivered);
        var policy = await PolicyAsync(town, cancellationToken);
        var needs = await NeedsAsync(town, level, policy, cancellationToken);
        var day = CityRules.DayOf(now);
        var trashTaken = (await DayDeliveriesAsync(town, day, cancellationToken)).GetValueOrDefault(Items.Trash);

        var contributors = new List<(Guid Id, long Cents)>();
        long mine = 0;
        await using (var command = await db.CommandAsync(
            """
            SELECT player_id, delivered_cents FROM world.town_contributions WHERE town_x = @x AND town_y = @y
            ORDER BY delivered_cents DESC, player_id LIMIT 5;
            SELECT delivered_cents FROM world.town_contributions WHERE town_x = @x AND town_y = @y AND player_id = @player
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("player", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                contributors.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            await reader.NextResultAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                mine = reader.GetInt64(0);
            }
        }

        var offices = await ServicesOfAsync(town, cancellationToken);
        var salesLimit = await ServiceSalesPerDayAsync(town, cancellationToken);
        var sales = new Dictionary<Guid, int>();
        foreach (var office in offices)
        {
            sales[office.Id] = await SalesTodayAsync(office.Id, cancellationToken);
        }

        var folk = await TownsfolkAsync(town, cancellationToken);
        var votes = new Dictionary<Guid, int>();
        Guid? myVote = null;
        await using (var command = await db.CommandAsync(
            "SELECT voter_id, candidate_id FROM world.town_votes WHERE town_x = @x AND town_y = @y AND week = @week", cancellationToken))
        {
            command.Parameters.AddWithValue("x", town.X);
            command.Parameters.AddWithValue("y", town.Y);
            command.Parameters.AddWithValue("week", CityRules.WeekOf(now));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var candidate = reader.GetGuid(1);
                votes[candidate] = votes.GetValueOrDefault(candidate) + 1;
                if (reader.GetGuid(0) == player)
                {
                    myVote = candidate;
                }
            }
        }

        var week = CityRules.WeekOf(now);
        var supplier = await SupplierAsync(town, week, cancellationToken);
        var race = await WeekSuppliersAsync(town, week, CityRules.SupplierRaceShown, cancellationToken);
        var myWeek = await WeekDeliveredAsync(town, week, player, cancellationToken);
        var tender = await TenderViewAsync(town, level, player, folk.Contains(player), cancellationToken);
        var named = await names.NamesAsync(
            contributors.Select(c => c.Id).Concat(offices.Select(o => o.Owner)).Concat(folk).Concat(policy.Mayor is { } m ? [m] : [])
                .Concat(supplier is { } s ? [s] : []).Concat(race.Select(r => r.Player)).Concat(tender.People).Distinct().ToList(),
            cancellationToken);
        string Name(Guid id) => named.GetValueOrDefault(id, "?");
        return new
        {
            town = new
            {
                name = town.Name,
                x = town.X,
                y = town.Y,
                population = town.Population,
                level,
                levelName = CityRules.LevelNames[level],
                levelNames = CityRules.LevelNames,
                deliveredCents = delivered,
                levelCents = CityRules.LevelCents,
                distance = Math.Round(CityRules.Distance(map.Width, x, y, town.X, town.Y), 1),
                // False on a plot of the county outside the city: the town hall works from there, deliveries, rubbish and services do not.
                inCity = TownOf(x, y) is not null,
            },
            dayEndsAt = CityRules.DayStart(day + 1).ToUnixTimeMilliseconds(),
            needs = needs.Select(n => new
            {
                id = n.Item,
                name = Items.Names.GetValueOrDefault(n.Item, n.Item),
                wanted = n.Wanted,
                delivered = n.Delivered,
                priceCents = n.PriceCents,
            }),
            contributors = contributors.Select(c => new { name = Name(c.Id), cents = c.Cents, mine = c.Id == player }),
            myCents = mine,
            // The chief supplier: who delivered the most last week holds the title this week and gets supplierPercent more for deliveries.
            supplier = new
            {
                name = supplier is { } chief ? Name(chief) : null,
                isMe = supplier == player,
                percent = CityRules.SupplierBonusPercent,
                minCents = CityRules.SupplierMinCents,
                weekEndsAt = CityRules.WeekStart(week + 1).ToUnixTimeMilliseconds(),
                race = race.Select((r, i) => new { place = i + 1, name = Name(r.Player), cents = r.Cents, mine = r.Player == player }),
                myCents = myWeek,
            },
            tender = tender.Json(Name),
            trash = new
            {
                perDay = CityRules.TrashPerDay(town, level),
                taken = trashTaken,
                feeCents = CityRules.TrashFeeCents,
            },
            services = offices.Select(o => new
            {
                id = o.Id,
                kind = o.Kind,
                name = Items.Names[o.Kind],
                owner = Name(o.Owner),
                own = o.Owner == player,
                item = CityRules.Services[o.Kind].Item,
                itemName = Items.Names[CityRules.Services[o.Kind].Item],
                quantity = CityRules.Services[o.Kind].Quantity,
                priceCents = CityRules.Services[o.Kind].PriceCents,
                left = salesLimit - sales[o.Id],
            }),
            hall = new
            {
                week,
                weekEndsAt = CityRules.WeekStart(week + 1).ToUnixTimeMilliseconds(),
                mayor = policy.Mayor is { } mayor ? Name(mayor) : null,
                isMayor = policy.Mayor == player,
                taxPercent = policy.TaxPercent,
                maxTaxPercent = CityRules.MaxTaxPercent,
                plotTaxPerMille = policy.PlotTaxPerMille,
                maxPlotTaxPerMille = CityRules.MaxPlotTaxPerMille,
                bonus = policy.Bonus,
                bonuses = CityRules.Bonuses.Select(b => new { id = b.Key, name = b.Value }),
                // A festival the mayor holds with fine dishes: the town pays festivalPercent more while it lasts (Unix milliseconds).
                festivalUntil = await FestivalUntilAsync(town, cancellationToken) is { } festival && festival > now ? festival.ToUnixTimeMilliseconds() : (long?)null,
                festivalDishes = CityRules.FestivalDishes,
                festivalPercent = (int)Math.Round((CityRules.FestivalBonus - 1) * 100),
                festivalHours = CityRules.FestivalTime.TotalHours,
                canVote = folk.Contains(player),
                myVote = myVote is { } v ? Name(v) : null,
                candidates = folk.Select(f => new { name = Name(f), votes = votes.GetValueOrDefault(f), me = f == player })
                    .OrderByDescending(c => c.votes).ThenBy(c => c.name),
                politics = await HallPoliticsAsync(player, town, policy, folk, votes, cancellationToken),
            },
        };
    }

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

    // --- What cities change elsewhere -----------------------------------------------------------------

    /// <summary>What rail fees from a plot are multiplied by: less with the country's railway, in a city, with a freight terminal,
    /// and anywhere in a town's county with the town's station and the mayor's bonus.</summary>
    public async Task<double> RailFactorAsync(int x, int y, CancellationToken cancellationToken)
    {
        var national = countries.At(x, y) is { } country ? await nation.RailFactorAsync(country.Code, cancellationToken) : 1;
        if (CountyOf(x, y) is not { } town)
        {
            return national;
        }

        var factor = national * (1 - TownHallRules.StationRail * await InvestmentAsync(town, "station", cancellationToken));
        if (TownOf(x, y) is not null)
        {
            factor *= CityRules.CityRail;
            if (await beach.HasAsync(x, y, Items.FreightTerminal, 0, cancellationToken))
            {
                factor *= CityRules.TerminalRail;
            }
        }

        if ((await PolicyAsync(town, cancellationToken)).Bonus == "rail")
        {
            factor *= CityRules.MayorRail;
        }

        return factor;
    }

    public async Task<int> MaxRoutesAsync(int x, int y, CancellationToken cancellationToken) =>
        TownOf(x, y) is not null && await beach.HasAsync(x, y, Items.FreightTerminal, 0, cancellationToken) ? CityRules.TerminalRoutes : RailRules.MaxRoutes;

    /// <summary>What the price of a plot in a town's county is multiplied by: less with the mayor's "plots" bonus and with the
    /// settlers' ordinance.</summary>
    public async Task<double> PlotPriceFactorAsync(int x, int y, CancellationToken cancellationToken) =>
        CountyOf(x, y) is { } town
            ? ((await PolicyAsync(town, cancellationToken)).Bonus == "plots" ? CityRules.CheapPlots : 1)
                * (await InForceAsync(town, "settlers", cancellationToken) ? TownHallRules.SettlersPlots : 1)
            : 1;
}
