using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

public sealed record PlotOwner(Guid AccountId, string? Name);

/// <param name="PriceCents">What the asking player would pay for it now; null when it cannot be bought.</param>
/// <param name="Survey">What lies under the plot, shown only to its owner once they paid for a survey.</param>
/// <param name="Company">The company its owner brought it into, if any.</param>
/// <param name="CanEnter">Whether the asking player may go onto it: its owner, a member of its company, or anyone when it is public.</param>
/// <param name="Name">What its owner called it, or null.</param>
/// <param name="Public">Whether its owner lets anyone come onto it to look around.</param>
public sealed record PlotState(
    int X, int Y, Biome Biome, PlotOwner? Owner, long? PriceCents, Survey? Survey = null, PlotCompany? Company = null, bool CanEnter = false,
    string? Name = null, bool Public = false);

/// <param name="Tool">The tool that came with the plot, or null.</param>
public sealed record Purchase(PlotState Plot, long BalanceCents, XpGain Xp, string? Tool);

public sealed class WorldService(
    GameDb db, WorldMap map, IWallet wallet, ICharacterProgress progress, ICharacterNames names, IEquipment equipment, SurveyService surveys,
    IEnumerable<ICompanyPlots> companies, CityService cities, PlotTaxService taxes, Countries countries, INationalPolicy nation, TimeProvider time)
{
    /// <summary>What a geological survey costs the player.</summary>
    public Task<long> SurveyPriceAsync(Guid player, CancellationToken cancellationToken) => surveys.PriceAsync(player, cancellationToken);

    /// <summary>Most owned plots one area request returns.</summary>
    public const int MaxAreaPlots = 5000;

    /// <summary>The player's plot tax, with the days since it was last paid paid now.</summary>
    public Task<PlotTax> TaxAsync(Guid owner, CancellationToken cancellationToken) => taxes.SettleAsync(owner, cancellationToken);

    public async Task<long> CountOwnedAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT count(*) FROM world.plots WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<(int X, int Y, Biome Biome, string? Name)>> OwnedAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT x, y, biome, name FROM world.plots WHERE owner_id = @owner ORDER BY bought_at", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plots = new List<(int, int, Biome, string?)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            plots.Add((reader.GetInt32(0), reader.GetInt32(1), (Biome)reader.GetInt16(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return plots;
    }

    /// <summary>Gives one of the player's plots a name, or takes it off with an empty one.</summary>
    public async Task<PlotState> RenameAsync(Guid player, int x, int y, string? name, CancellationToken cancellationToken)
    {
        name = PlotNames.Clean(name);
        await using (var update = await db.CommandAsync(
            "UPDATE world.plots SET name = @name WHERE x = @x AND y = @y AND owner_id = @owner", cancellationToken))
        {
            update.Parameters.AddWithValue("x", x);
            update.Parameters.AddWithValue("y", y);
            update.Parameters.AddWithValue("owner", player);
            update.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("not_owner", "Only its owner names a plot.");
            }
        }

        return await PlotAsync(player, x, y, cancellationToken);
    }

    /// <summary>Owned plots inside a rectangle of the grid, with whose they are.</summary>
    /// <summary>Plots other members brought into the player's company.</summary>
    public async Task<IReadOnlyList<(int X, int Y, Biome Biome)>> SharedAsync(Guid player, CancellationToken cancellationToken)
    {
        var plots = new List<(int, int, Biome)>();
        foreach (var registry in companies)
        {
            plots.AddRange((await registry.SharedWithAsync(player, cancellationToken)).Select(p => (p.X, p.Y, map.BiomeAt(p.X, p.Y))));
        }

        return plots;
    }

    public async Task<IReadOnlyList<(int X, int Y, Guid Owner)>> AreaAsync(int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT x, y, owner_id FROM world.plots
            WHERE x >= @x AND x < @x + @width AND y >= @y AND y < @y + @height
            LIMIT @limit
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("width", width);
        command.Parameters.AddWithValue("height", height);
        command.Parameters.AddWithValue("limit", MaxAreaPlots);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plots = new List<(int, int, Guid)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            plots.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2)));
        }

        return plots;
    }

    public async Task<PlotState> PlotAsync(Guid viewer, int x, int y, CancellationToken cancellationToken)
    {
        var biome = BiomeAt(x, y);
        Guid? found = null;
        string? plotName = null;
        var open = false;
        await using (var command = await db.CommandAsync("SELECT owner_id, name, public FROM world.plots WHERE x = @x AND y = @y", cancellationToken))
        {
            command.Parameters.AddWithValue("x", x);
            command.Parameters.AddWithValue("y", y);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                (found, plotName) = (reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                open = reader.GetBoolean(2);
            }
        }

        if (found is { } owner)
        {
            var name = (await names.NamesAsync([owner], cancellationToken)).GetValueOrDefault(owner);
            // Deposits stay secret from everyone but the owner who paid for the survey.
            var survey = owner == viewer ? await surveys.FindAsync(x, y, cancellationToken) : null;
            PlotCompany? company = null;
            foreach (var registry in companies)
            {
                company ??= await registry.CompanyAsync(viewer, x, y, cancellationToken);
            }

            return new PlotState(x, y, biome, new PlotOwner(owner, name), null, survey, company, owner == viewer || company?.Member == true || open, plotName, open);
        }

        long? price = null;
        if (Biomes.All[biome].Buyable)
        {
            try
            {
                price = (await PriceAsync(viewer, x, y, biome, cancellationToken)).Total;
            }
            catch (GameException e) when (e.Code == "embargo")
            {
                // The country sells no plots to the viewer's country.
            }
        }

        return new PlotState(x, y, biome, null, price);
    }

    /// <summary>What the player pays for a free plot: the price of their next plot in the biome, less in a town that makes plots
    /// cheaper; the country's government makes it cheaper for its citizens or adds a fee for foreigners (which goes to its treasury).</summary>
    private async Task<(long Total, long Fee)> PriceAsync(Guid buyer, int x, int y, Biome biome, CancellationToken cancellationToken)
    {
        var price = (long)Math.Round(PlotPrices.For(await CountOwnedAsync(buyer, cancellationToken), biome) * await cities.PlotPriceFactorAsync(x, y, cancellationToken));
        return countries.At(x, y) is { } country ? await nation.PlotPriceAsync(buyer, country.Code, price, cancellationToken) : (price, 0);
    }

    /// <summary>Buys a free plot: money out, plot in, both or neither. The price depends on how many plots the player has.</summary>
    public Task<Purchase> BuyAsync(Guid owner, int x, int y, CancellationToken cancellationToken)
    {
        var biome = BiomeAt(x, y);
        if (!Biomes.All[biome].Buyable)
        {
            throw new GameException("not_buyable", "Water cannot be bought.");
        }

        return db.InTransactionAsync(async () =>
        {
            // One purchase at a time per player, so two at once cannot both pay the lower price.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()));
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            // The days before are taxed by the plots the player had then; who owes the tax buys nothing more.
            if (await taxes.SettleAsync(owner, cancellationToken) is { Due: true } tax)
            {
                throw PlotTaxService.Due(tax);
            }

            var (total, fee) = await PriceAsync(owner, x, y, biome, cancellationToken);
            var price = total - fee;
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO world.plots (x, y, owner_id, biome, price_cents, bought_at)
                VALUES (@x, @y, @owner, @biome, @price, @at)
                ON CONFLICT DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("x", x);
                insert.Parameters.AddWithValue("y", y);
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("biome", (short)biome);
                insert.Parameters.AddWithValue("price", price);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("plot_taken", "Someone already owns this plot.");
                }
            }

            var balance = await wallet.DebitAsync(owner, price, $"plot {x}:{y}", cancellationToken);
            if (fee > 0)
            {
                await nation.ChargePlotFeeAsync(owner, countries.At(x, y)!.Code, fee, x, y, cancellationToken);
                balance = await wallet.BalanceAsync(owner, cancellationToken);
            }

            var xp = await progress.AddXpAsync(owner, PlotRules.XpPerPlot, cancellationToken);
            // A forest comes with an axe, a desert with a shovel, mountains with a pickaxe, the steppe with shears.
            var tool = PlotLayout.StarterTool(biome);
            if (tool is not null)
            {
                await equipment.GiveToolAsync(owner, tool, cancellationToken);
            }

            var name = (await names.NamesAsync([owner], cancellationToken)).GetValueOrDefault(owner);
            return new Purchase(new PlotState(x, y, biome, new PlotOwner(owner, name), null, CanEnter: true), balance, xp, tool);
        }, cancellationToken);
    }

    private Biome BiomeAt(int x, int y) =>
        map.Contains(x, y) ? map.BiomeAt(x, y) : throw new GameException("bad_plot", "There is no such plot.");
}

/// <summary>Who owns a plot, for other modules (e.g. companies).</summary>
public sealed class PlotRegistry(GameDb db) : IPlotRegistry
{
    public async Task<OwnedPlot?> FindAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT owner_id, biome, price_cents FROM world.plots WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new OwnedPlot(x, y, reader.GetGuid(0), reader.GetInt16(1), reader.GetInt64(2)) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountsAsync(CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT owner_id, count(*)::int FROM world.plots GROUP BY owner_id", cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<Guid, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetGuid(0)] = reader.GetInt32(1);
        }

        return counts;
    }
}
