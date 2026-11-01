using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>Until when a player has paid the tax on their plots. One row per player, made the first time it is looked at.</summary>
public sealed class PlotTaxRow
{
    public Guid OwnerId { get; set; }

    public DateTimeOffset PaidUntil { get; set; }
}

public static class PlotTaxRules
{
    /// <summary>Every plot but the first one bought costs this many thousandths of its worth a day: its price and what stands on it.
    /// The government of the country it lies in sets it between 3 and 7 (Politics module); this is the rate where there is none.</summary>
    public const int PerMillePerDay = 5;

    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>The tax a day on plots worth <paramref name="taxedCents"/> together.</summary>
    public static long DailyCents(long taxedCents, int perMille = PerMillePerDay) => taxedCents * perMille / 1000;
}

/// <param name="DailyCents">What the player's plots cost a day now.</param>
/// <param name="PaidUntil">The tax is paid up to here; whole days after it are owed.</param>
/// <param name="DaysOwed">Whole days the player had no money for; while any are owed, nothing can be built on their plots.</param>
public sealed record PlotTax(long DailyCents, DateTimeOffset PaidUntil, int DaysOwed)
{
    public long OwedCents => DailyCents * DaysOwed;

    public bool Due => DaysOwed > 0;
}

/// <summary>
/// The daily tax on plots. Nothing runs in the background: whenever a player's plot is entered or built on, or they open
/// the map or buy a plot, the whole days since <see cref="PlotTaxRow.PaidUntil"/> are paid from their wallet, as many as it covers.
/// Each plot is taxed at the rate of the country it lies in, and the country's treasury gets its share. On a plot of a town's county
/// with a mayor the mayor's own rate (<see cref="TownPolicy.PlotTaxPerMille"/>) comes on top, for the town
/// (<see cref="CityService.CollectPlotTaxAsync"/>), except on the mayor's own plots; the mayor and the rate are this week's, for all
/// the days paid at once.
/// </summary>
public sealed class PlotTaxService(
    GameDb db, IWallet wallet, IMarketPrices prices, Countries countries, INationalPolicy nation, IServiceProvider services, TimeProvider time)
{
    // Asked for when needed: the town hall works with plots, whose service settles this tax.
    private CityService Cities => services.GetRequiredService<CityService>();

    public Task<PlotTax> SettleAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var now = time.GetUtcNow();
            DateTimeOffset paidUntil;
            // Created on the first look, so the tax counts from then on; locked, so two requests at once pay a day once.
            await using (var create = await db.CommandAsync(
                "INSERT INTO world.plot_taxes (owner_id, paid_until) VALUES (@owner, @now) ON CONFLICT DO NOTHING", cancellationToken))
            {
                create.Parameters.AddWithValue("owner", owner);
                create.Parameters.AddWithValue("now", now);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var row = await db.CommandAsync("SELECT paid_until FROM world.plot_taxes WHERE owner_id = @owner FOR UPDATE", cancellationToken))
            {
                row.Parameters.AddWithValue("owner", owner);
                await using var reader = await row.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                paidUntil = reader.GetFieldValue<DateTimeOffset>(0);
            }

            var byCountry = new Dictionary<string, long>();
            var byTown = new Dictionary<Town, long>();
            foreach (var ((country, town), cents) in await TaxedCentsAsync(owner, cancellationToken))
            {
                var national = PlotTaxRules.DailyCents(
                    cents, country == "" ? PlotTaxRules.PerMillePerDay : await nation.PlotTaxPerMilleAsync(country, cancellationToken));
                byCountry[country] = byCountry.GetValueOrDefault(country) + national;
                if (town is not null && await Cities.PolicyAsync(town, cancellationToken) is { Mayor: { } mayor, PlotTaxPerMille: > 0 } policy && mayor != owner)
                {
                    byTown[town] = byTown.GetValueOrDefault(town) + PlotTaxRules.DailyCents(cents, policy.PlotTaxPerMille);
                }
            }

            var daily = byCountry.Values.Sum() + byTown.Values.Sum();
            var days = (int)Math.Min(int.MaxValue, Math.Max(0, (now - paidUntil).Ticks / PlotTaxRules.Day.Ticks));
            if (days == 0)
            {
                return new PlotTax(daily, paidUntil, 0);
            }

            var paid = daily == 0 ? days : (int)Math.Min(days, await wallet.BalanceAsync(owner, cancellationToken) / daily);
            if (paid > 0 && daily > 0)
            {
                await wallet.DebitAsync(owner, paid * daily, $"plot tax {paid} days", cancellationToken);
                foreach (var (country, cents) in byCountry.Where(c => c.Key != "" && c.Value > 0))
                {
                    await nation.CollectPlotTaxAsync(country, paid * cents, cancellationToken);
                }

                foreach (var (town, cents) in byTown.Where(t => t.Value > 0))
                {
                    await Cities.CollectPlotTaxAsync(town, paid * cents, $"podatek od działek powiatu: {paid} dni", cancellationToken);
                }
            }

            paidUntil += PlotTaxRules.Day * paid;
            await using (var update = await db.CommandAsync("UPDATE world.plot_taxes SET paid_until = @until WHERE owner_id = @owner", cancellationToken))
            {
                update.Parameters.AddWithValue("owner", owner);
                update.Parameters.AddWithValue("until", paidUntil);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            return new PlotTax(daily, paidUntil, days - paid);
        }, cancellationToken);

    /// <summary>Settles the tax of the owner of plot <paramref name="x"/>, <paramref name="y"/>, if somebody owns it.</summary>
    public async Task<PlotTax?> SettlePlotAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT owner_id FROM world.plots WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid owner ? await SettleAsync(owner, cancellationToken) : null;
    }

    /// <summary>Fails with <c>plot_tax_due</c> while the owner of the plot owes the tax.</summary>
    public async Task RequirePaidAsync(int x, int y, CancellationToken cancellationToken)
    {
        if (await SettlePlotAsync(x, y, cancellationToken) is { Due: true } tax)
        {
            throw Due(tax);
        }
    }

    public static GameException Due(PlotTax tax) =>
        new("plot_tax_due", $"The plot tax of {tax.OwedCents / 100.0:0.00} $ is due; nothing can be built until it is paid.");

    /// <summary>What the player's plots are worth, all but the first one they bought, by the country they lie in ("" for none) and the
    /// town whose county they lie in (null for none): what they paid for each plot, and for everything standing on it what the trader
    /// pays for one (or, for what it does not buy, asks).</summary>
    private async Task<Dictionary<(string Country, Town? Town), long>> TaxedCentsAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            WITH taxed AS (SELECT x, y, price_cents FROM world.plots WHERE owner_id = @owner ORDER BY bought_at, x, y OFFSET 1)
            SELECT x, y, NULL, price_cents FROM taxed
            UNION ALL
            SELECT o.x, o.y, o.kind, COUNT(*) FROM world.plot_objects o JOIN taxed t ON o.x = t.x AND o.y = t.y GROUP BY o.x, o.y, o.kind
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var cents = new Dictionary<(string, Town?), long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var (x, y) = (reader.GetInt32(0), reader.GetInt32(1));
            var key = (countries.At(x, y)?.Code ?? "", Cities.CountyOf(x, y));
            var worth = reader.IsDBNull(2)
                ? reader.GetInt64(3)
                : reader.GetInt64(3) * (prices.BuysCents(reader.GetString(2)) ?? prices.SellsCents(reader.GetString(2)) ?? 0);
            cents[key] = cents.GetValueOrDefault(key) + worth;
        }

        return cents;
    }
}
