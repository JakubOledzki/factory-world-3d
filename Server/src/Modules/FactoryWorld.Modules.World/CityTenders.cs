using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>How much one player delivered to a town in one week (<see cref="CityRules.WeekOf"/>), for the chief supplier's title.</summary>
public sealed class TownWeekDeliveryRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid PlayerId { get; set; }

    public long DeliveredCents { get; set; }
}

/// <summary>A town's tender of one week: a large order that goes to the lowest sealed bid.</summary>
public sealed class TownTenderRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }

    /// <summary>The most the town pays a piece; no bid may ask more.</summary>
    public long CapCents { get; set; }

    /// <summary>When bidding ends and the lowest bid wins.</summary>
    public DateTimeOffset ClosesAt { get; set; }

    /// <summary>The winner delivers until then; whatever is missing then costs them their deposit.</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary><see cref="TenderRules.Open"/>, <see cref="TenderRules.Won"/>, <see cref="TenderRules.Done"/>,
    /// <see cref="TenderRules.Failed"/> or <see cref="TenderRules.Unclaimed"/>.</summary>
    public required string State { get; set; }

    public Guid? WinnerId { get; set; }

    /// <summary>The winning price a piece.</summary>
    public long PriceCents { get; set; }

    /// <summary>The winner's deposit, held until the order is complete.</summary>
    public long DepositCents { get; set; }

    public long Delivered { get; set; }
}

/// <summary>A sealed bid in a town's tender, with the deposit taken for it.</summary>
public sealed class TenderBidRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid BidderId { get; set; }

    public long PriceCents { get; set; }

    public long DepositCents { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
/// Town tenders: every week a town puts out one large order for goods it wants. Its townsfolk (owners of plots in the
/// town) make sealed bids, a price a piece no higher than the town's usual one, each with a deposit of
/// <see cref="DepositPercent"/>% of the whole order. When bidding closes the lowest bid wins (the earlier one of equal
/// bids), the others get their deposits back, and the winner delivers until the week's end, paid at their price for every
/// piece. Done in full, they get their deposit back; short, they lose it.
/// </summary>
public static class TenderRules
{
    public const string Open = "open";
    public const string Won = "won";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Unclaimed = "unclaimed";

    public const int DepositPercent = 10;

    /// <summary>Bidding runs this long from the start of the week; delivering the rest of it.</summary>
    public static readonly TimeSpan Bidding = TimeSpan.FromDays(3);

    /// <summary>A tender wants as much as the town wants of the good in this many days.</summary>
    public const int Days = 5;

    public static long DepositCents(long priceCents, long quantity) => Math.Max(1, (priceCents * quantity * DepositPercent + 99) / 100);

    /// <summary>The good, quantity and highest price of a town's tender in a week: picked by the town and the week from what it wants at its level.</summary>
    public static (string Item, long Quantity, long CapCents) For(Town town, int level, int week, Func<string, long?> traderBuysCents)
    {
        var open = CityRules.Goods.Where(g => g.Level <= level && traderBuysCents(g.Item) is not null).ToList();
        var seed = (ulong)(uint)town.X * 0x9e3779b97f4a7c15UL ^ (ulong)(uint)town.Y * 0xc2b2ae3d27d4eb4fUL ^ (ulong)(uint)week * 0x165667b19e3779f9UL;
        seed ^= seed >> 33;
        seed *= 0xff51afd7ed558ccdUL;
        seed ^= seed >> 33;
        var good = open[(int)(seed % (ulong)open.Count)];
        var quantity = Math.Max(50, (long)Math.Round(good.Quantity * CityRules.PopulationFactor(town.Population) * (1 + 0.5 * level) * Days));
        return (good.Item, quantity, CityRules.NeedPriceCents(traderBuysCents(good.Item)!.Value, level, false));
    }
}

/// <summary>A town's tender as one player sees it: everything but the other bidders' prices until bidding closes.</summary>
public sealed record TenderView(TownTenderRow Tender, int Bids, TenderBidRow? Mine, bool CanBid, Guid Viewer)
{
    public IEnumerable<Guid> People => Tender.WinnerId is { } w ? [w] : [];

    public object Json(Func<Guid, string> name) => new
    {
        week = Tender.Week,
        item = Tender.Item,
        name = Items.Names.GetValueOrDefault(Tender.Item, Tender.Item),
        quantity = Tender.Quantity,
        capCents = Tender.CapCents,
        depositPercent = TenderRules.DepositPercent,
        // Unix milliseconds.
        closesAt = Tender.ClosesAt.ToUnixTimeMilliseconds(),
        endsAt = Tender.EndsAt.ToUnixTimeMilliseconds(),
        state = Tender.State,
        bids = Bids,
        canBid = CanBid,
        myBid = Mine is { } m ? new { priceCents = m.PriceCents, depositCents = m.DepositCents } : null,
        winner = Tender.WinnerId is { } w ? name(w) : null,
        isWinner = Tender.WinnerId == Viewer,
        priceCents = Tender.WinnerId is null ? (long?)null : Tender.PriceCents,
        depositCents = Tender.WinnerId is null ? (long?)null : Tender.DepositCents,
        delivered = Tender.Delivered,
    };
}

public sealed partial class CityService
{
    /// <summary>A delivery makes the town grow and counts towards the player's share and this week's chief supplier.</summary>
    private async Task GrowAsync(Town town, Guid player, long cents, CancellationToken cancellationToken)
    {
        await using var grow = await db.CommandAsync(
            """
            INSERT INTO world.town_progress (town_x, town_y, delivered_cents) VALUES (@x, @y, @cents)
            ON CONFLICT (town_x, town_y) DO UPDATE SET delivered_cents = world.town_progress.delivered_cents + excluded.delivered_cents;
            INSERT INTO world.town_contributions (town_x, town_y, player_id, delivered_cents) VALUES (@x, @y, @player, @cents)
            ON CONFLICT (town_x, town_y, player_id) DO UPDATE SET delivered_cents = world.town_contributions.delivered_cents + excluded.delivered_cents;
            INSERT INTO world.town_week_deliveries (town_x, town_y, week, player_id, delivered_cents) VALUES (@x, @y, @week, @player, @cents)
            ON CONFLICT (town_x, town_y, week, player_id) DO UPDATE SET delivered_cents = world.town_week_deliveries.delivered_cents + excluded.delivered_cents
            """, cancellationToken);
        grow.Parameters.AddWithValue("x", town.X);
        grow.Parameters.AddWithValue("y", town.Y);
        grow.Parameters.AddWithValue("week", CityRules.WeekOf(time.GetUtcNow()));
        grow.Parameters.AddWithValue("cents", cents);
        grow.Parameters.AddWithValue("player", player);
        await grow.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The town's chief supplier in <paramref name="week"/>: who delivered the most the week before, if enough.</summary>
    public async Task<Guid?> SupplierAsync(Town town, int week, CancellationToken cancellationToken) =>
        (await WeekSuppliersAsync(town, week - 1, 1, cancellationToken)) is [var best] && best.Cents >= CityRules.SupplierMinCents ? best.Player : null;

    /// <summary>Who delivered the most to the town in a week, best first.</summary>
    public async Task<IReadOnlyList<(Guid Player, long Cents)>> WeekSuppliersAsync(Town town, int week, int count, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT player_id, delivered_cents FROM world.town_week_deliveries
            WHERE town_x = @x AND town_y = @y AND week = @week AND delivered_cents > 0
            ORDER BY delivered_cents DESC, player_id LIMIT @count
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        command.Parameters.AddWithValue("count", count);
        var found = new List<(Guid, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add((reader.GetGuid(0), reader.GetInt64(1)));
        }

        return found;
    }

    private async Task<long> WeekDeliveredAsync(Town town, int week, Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT delivered_cents FROM world.town_week_deliveries WHERE town_x = @x AND town_y = @y AND week = @week AND player_id = @player",
            cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        command.Parameters.AddWithValue("player", player);
        return await command.ExecuteScalarAsync(cancellationToken) is long cents ? cents : 0;
    }

    // --- Tenders -------------------------------------------------------------------------------------

    /// <summary>This week's tender of the town, put out the first time anybody looks, and brought up to now.</summary>
    private async Task<TownTenderRow> TenderAsync(Town town, int level, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var week = CityRules.WeekOf(now);
        if (await ReadTenderAsync(town, week, true, cancellationToken) is not { } tender)
        {
            var (item, quantity, cap) = TenderRules.For(town, level, week, prices.BuysCents);
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.town_tenders (town_x, town_y, week, item, quantity, cap_cents, closes_at, ends_at, state, price_cents, deposit_cents, delivered)
                VALUES (@x, @y, @week, @item, @quantity, @cap, @closes, @ends, @state, 0, 0, 0)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("x", town.X);
                insert.Parameters.AddWithValue("y", town.Y);
                insert.Parameters.AddWithValue("week", week);
                insert.Parameters.AddWithValue("item", item);
                insert.Parameters.AddWithValue("quantity", quantity);
                insert.Parameters.AddWithValue("cap", cap);
                insert.Parameters.AddWithValue("closes", CityRules.WeekStart(week) + TenderRules.Bidding);
                insert.Parameters.AddWithValue("ends", CityRules.WeekStart(week + 1));
                insert.Parameters.AddWithValue("state", TenderRules.Open);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            tender = (await ReadTenderAsync(town, week, true, cancellationToken))!;
        }

        return await SettleTenderAsync(town, tender, cancellationToken);
    }

    private async Task<TownTenderRow?> ReadTenderAsync(Town town, int week, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            $"""
            SELECT item, quantity, cap_cents, closes_at, ends_at, state, winner_id, price_cents, deposit_cents, delivered
            FROM world.town_tenders WHERE town_x = @x AND town_y = @y AND week = @week {(forUpdate ? "FOR UPDATE" : "")}
            """, cancellationToken);
        command.Parameters.AddWithValue("x", town.X);
        command.Parameters.AddWithValue("y", town.Y);
        command.Parameters.AddWithValue("week", week);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TownTenderRow
            {
                TownX = town.X,
                TownY = town.Y,
                Week = week,
                Item = reader.GetString(0),
                Quantity = reader.GetInt64(1),
                CapCents = reader.GetInt64(2),
                ClosesAt = reader.GetFieldValue<DateTimeOffset>(3),
                EndsAt = reader.GetFieldValue<DateTimeOffset>(4),
                State = reader.GetString(5),
                WinnerId = reader.IsDBNull(6) ? null : reader.GetGuid(6),
                PriceCents = reader.GetInt64(7),
                DepositCents = reader.GetInt64(8),
                Delivered = reader.GetInt64(9),
            }
            : null;
    }

    private async Task<List<TenderBidRow>> BidsAsync(TownTenderRow tender, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT bidder_id, price_cents, deposit_cents, at FROM world.tender_bids
            WHERE town_x = @x AND town_y = @y AND week = @week ORDER BY price_cents, at, bidder_id
            """, cancellationToken);
        command.Parameters.AddWithValue("x", tender.TownX);
        command.Parameters.AddWithValue("y", tender.TownY);
        command.Parameters.AddWithValue("week", tender.Week);
        var bids = new List<TenderBidRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bids.Add(new TenderBidRow
            {
                TownX = tender.TownX,
                TownY = tender.TownY,
                Week = tender.Week,
                BidderId = reader.GetGuid(0),
                PriceCents = reader.GetInt64(1),
                DepositCents = reader.GetInt64(2),
                At = reader.GetFieldValue<DateTimeOffset>(3),
            });
        }

        return bids;
    }

    private async Task DeleteBidAsync(TownTenderRow tender, Guid bidder, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "DELETE FROM world.tender_bids WHERE town_x = @x AND town_y = @y AND week = @week AND bidder_id = @bidder", cancellationToken);
        command.Parameters.AddWithValue("x", tender.TownX);
        command.Parameters.AddWithValue("y", tender.TownY);
        command.Parameters.AddWithValue("week", tender.Week);
        command.Parameters.AddWithValue("bidder", bidder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SaveTenderAsync(TownTenderRow tender, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            UPDATE world.town_tenders SET state = @state, winner_id = @winner, price_cents = @price, deposit_cents = @deposit, delivered = @delivered
            WHERE town_x = @x AND town_y = @y AND week = @week
            """, cancellationToken);
        command.Parameters.AddWithValue("x", tender.TownX);
        command.Parameters.AddWithValue("y", tender.TownY);
        command.Parameters.AddWithValue("week", tender.Week);
        command.Parameters.AddWithValue("state", tender.State);
        command.Parameters.AddWithValue("winner", (object?)tender.WinnerId ?? DBNull.Value);
        command.Parameters.AddWithValue("price", tender.PriceCents);
        command.Parameters.AddWithValue("deposit", tender.DepositCents);
        command.Parameters.AddWithValue("delivered", tender.Delivered);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Brings a tender (locked by the caller) up to now: once bidding closes the lowest bid wins and the others get their
    /// deposits back; once the week is over a winner who did not deliver everything loses theirs.</summary>
    private async Task<TownTenderRow> SettleTenderAsync(Town town, TownTenderRow tender, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (tender.State == TenderRules.Open && now >= tender.ClosesAt)
        {
            var bids = await BidsAsync(tender, cancellationToken);
            if (bids.Count == 0)
            {
                tender.State = TenderRules.Unclaimed;
            }
            else
            {
                var best = bids[0];
                tender.State = TenderRules.Won;
                tender.WinnerId = best.BidderId;
                tender.PriceCents = best.PriceCents;
                tender.DepositCents = best.DepositCents;
                foreach (var lost in bids.Skip(1))
                {
                    await wallet.CreditAsync(lost.BidderId, lost.DepositCents, $"tender {town.Name}: deposit back", cancellationToken);
                }

                await using (var clear = await db.CommandAsync(
                    "DELETE FROM world.tender_bids WHERE town_x = @x AND town_y = @y AND week = @week", cancellationToken))
                {
                    clear.Parameters.AddWithValue("x", tender.TownX);
                    clear.Parameters.AddWithValue("y", tender.TownY);
                    clear.Parameters.AddWithValue("week", tender.Week);
                    await clear.ExecuteNonQueryAsync(cancellationToken);
                }

                var winner = (await names.NamesAsync([best.BidderId], cancellationToken)).GetValueOrDefault(best.BidderId, "?");
                var item = Items.Names.GetValueOrDefault(tender.Item, tender.Item);
                db.AfterCommit(() => announcements.Announce(
                    $"Przetarg miasta {town.Name} ({Number(tender.Quantity)} × {item}) wygrywa {winner}: {Money(tender.PriceCents)} za sztukę, z {bids.Count} ofert."));
            }

            await SaveTenderAsync(tender, cancellationToken);
        }

        if (tender.State == TenderRules.Won && now >= tender.EndsAt && tender.Delivered < tender.Quantity)
        {
            // The deposit stays with the town.
            tender.State = TenderRules.Failed;
            await SaveTenderAsync(tender, cancellationToken);
        }

        return tender;
    }

    private async Task<TenderView> TenderViewAsync(Town town, int level, Guid player, bool townsman, CancellationToken cancellationToken) =>
        await db.InTransactionAsync(async () =>
        {
            await LockTownAsync(town, cancellationToken);
            var tender = await TenderAsync(town, level, cancellationToken);
            var bids = tender.State == TenderRules.Open ? await BidsAsync(tender, cancellationToken) : [];
            return new TenderView(tender, bids.Count, bids.FirstOrDefault(b => b.BidderId == player),
                townsman && tender.State == TenderRules.Open, player);
        }, cancellationToken);

    /// <summary>A townsman bids for this week's tender (or changes their bid): <paramref name="priceCents"/> a piece, no more than the town's
    /// highest price, with the deposit taken now (a changed bid gives the old deposit back first).</summary>
    public Task BidAsync(Guid player, int x, int y, long priceCents, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            if (!(await TownsfolkAsync(town, cancellationToken)).Contains(player))
            {
                throw new GameException("not_townsman", "Only players who own a plot in the town bid here.");
            }

            await LockTownAsync(town, cancellationToken);
            var tender = await TenderAsync(town, CityRules.Level(await DeliveredAsync(town, cancellationToken)), cancellationToken);
            if (tender.State != TenderRules.Open)
            {
                throw new GameException("tender_closed", "Bidding for this week's tender is over.");
            }

            if (priceCents < 1 || priceCents > tender.CapCents)
            {
                throw new GameException("bad_bid", $"Bid between 0.01 and {tender.CapCents / 100.0:0.00} $ a piece.");
            }

            if ((await BidsAsync(tender, cancellationToken)).FirstOrDefault(b => b.BidderId == player) is { } old)
            {
                await wallet.CreditAsync(player, old.DepositCents, $"tender {town.Name}: deposit of the old bid back", cancellationToken);
            }

            var deposit = TenderRules.DepositCents(priceCents, tender.Quantity);
            await wallet.DebitAsync(player, deposit, $"tender {town.Name}: deposit", cancellationToken);
            await using var upsert = await db.CommandAsync(
                """
                INSERT INTO world.tender_bids (town_x, town_y, week, bidder_id, price_cents, deposit_cents, at) VALUES (@x, @y, @week, @bidder, @price, @deposit, @at)
                ON CONFLICT (town_x, town_y, week, bidder_id) DO UPDATE SET price_cents = excluded.price_cents, deposit_cents = excluded.deposit_cents, at = excluded.at
                """, cancellationToken);
            upsert.Parameters.AddWithValue("x", tender.TownX);
            upsert.Parameters.AddWithValue("y", tender.TownY);
            upsert.Parameters.AddWithValue("week", tender.Week);
            upsert.Parameters.AddWithValue("bidder", player);
            upsert.Parameters.AddWithValue("price", priceCents);
            upsert.Parameters.AddWithValue("deposit", deposit);
            upsert.Parameters.AddWithValue("at", time.GetUtcNow());
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Takes the player's bid back while bidding is open, with its deposit.</summary>
    public Task WithdrawBidAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await LockTownAsync(town, cancellationToken);
            var tender = await TenderAsync(town, CityRules.Level(await DeliveredAsync(town, cancellationToken)), cancellationToken);
            if (tender.State != TenderRules.Open)
            {
                throw new GameException("tender_closed", "Bidding for this week's tender is over.");
            }

            var bid = (await BidsAsync(tender, cancellationToken)).FirstOrDefault(b => b.BidderId == player)
                ?? throw new GameException("no_bid", "You have no bid in this tender.");
            await DeleteBidAsync(tender, player, cancellationToken);
            await wallet.CreditAsync(player, bid.DepositCents, $"tender {town.Name}: bid withdrawn, deposit back", cancellationToken);
        }, cancellationToken);

    /// <summary>The winner delivers towards the tender, from the inventory or from the station of their plot in the county, and is paid their
    /// price for every piece; the last piece brings the deposit back.</summary>
    public Task<(long Quantity, long Cents, XpGain Xp)> DeliverTenderAsync(
        Guid player, int x, int y, long quantity, bool fromStation, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var town = RequireCounty(x, y);
            await plots.RequireOwnerAsync(player, x, y, cancellationToken, fromStation ? PlotRight.Take : PlotRight.Work);
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Deliver at least one.");
            }

            await LockTownAsync(town, cancellationToken);
            var tender = await TenderAsync(town, CityRules.Level(await DeliveredAsync(town, cancellationToken)), cancellationToken);
            if (tender.State != TenderRules.Won || tender.WinnerId != player)
            {
                throw new GameException("not_winner", "Only the winner of the tender delivers, until the week's end.");
            }

            var taken = Math.Min(quantity, tender.Quantity - tender.Delivered);
            if (fromStation)
            {
                await StationStock.ChangeAsync(db, x, y, tender.Item, -taken, cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(player, tender.Item, taken, cancellationToken);
            }

            var cents = taken * tender.PriceCents;
            tender.Delivered += taken;
            await GrowAsync(town, player, cents, cancellationToken);
            await wallet.CreditAsync(player, cents, $"tender {town.Name}: {taken} {tender.Item}", cancellationToken);
            if (tender.Delivered >= tender.Quantity)
            {
                tender.State = TenderRules.Done;
                await wallet.CreditAsync(player, tender.DepositCents, $"tender {town.Name}: done, deposit back", cancellationToken);
            }

            await SaveTenderAsync(tender, cancellationToken);
            var xp = await progress.AddXpAsync(player, Math.Max(1, cents / 1000), cancellationToken);
            return (taken, cents, xp);
        }, cancellationToken);

    /// <summary>Settles every tender whose bidding closed or whose week ended, whether or not anyone looks: the losers get their deposits back.</summary>
    public async Task SettleTendersAsync(CancellationToken cancellationToken)
    {
        var due = new List<(int X, int Y, int Week)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT town_x, town_y, week FROM world.town_tenders
            WHERE (state = @open AND closes_at <= @now) OR (state = @won AND ends_at <= @now AND delivered < quantity)
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("open", TenderRules.Open);
            command.Parameters.AddWithValue("won", TenderRules.Won);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                due.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        foreach (var (x, y, week) in due)
        {
            await db.InTransactionAsync(async () =>
            {
                var town = towns.Of(x, y);
                await LockTownAsync(town, cancellationToken);
                if (await ReadTenderAsync(town, week, true, cancellationToken) is { } tender)
                {
                    await SettleTenderAsync(town, tender, cancellationToken);
                }

                return 0;
            }, cancellationToken);
        }
    }

    private static string Number(long n) => n.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    private static string Money(long cents) => $"{Number(cents / 100)},{cents % 100:00} $";
}

/// <summary>Settles the towns' tenders whose bidding closed, so losers get their deposits back without anybody looking.</summary>
public sealed class TenderClock(IServiceScopeFactory scopes, ILogger<TenderClock> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CityService>().SettleTendersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Settling town tenders failed");
            }
        }
    }
}

public sealed record TenderBidRequest(int X, int Y, long PriceCents);

public sealed record TenderDeliverRequest(int X, int Y, long Quantity, bool FromStation = false);

public sealed class CityTenderBidHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.tenderBid";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<TenderBidRequest>(payload);
        await cities.BidAsync(player, r.X, r.Y, r.PriceCents, cancellationToken);
        return null;
    }
}

public sealed class CityTenderWithdrawHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.tenderWithdraw";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<CityRequest>(payload);
        await cities.WithdrawBidAsync(player, r.X, r.Y, cancellationToken);
        return null;
    }
}

public sealed class CityTenderDeliverHandler(IServiceScopeFactory scopes) : CityHandler(scopes)
{
    public override string Type => "city.tenderDeliver";

    protected override async Task<object?> RunAsync(CityService cities, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var r = Payload.Read<TenderDeliverRequest>(payload);
        var (quantity, cents, xp) = await cities.DeliverTenderAsync(player, r.X, r.Y, r.Quantity, r.FromStation, cancellationToken);
        return new { quantity, cents, xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } };
    }
}
