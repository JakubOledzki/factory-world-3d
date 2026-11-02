using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

public sealed record PlotRequest(int X, int Y);

public sealed record AreaRequest(int X, int Y, int Width, int Height);

public abstract class WorldHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await RunAsync(scope.ServiceProvider.GetRequiredService<WorldService>(), client.RequireAccount(), payload, cancellationToken);
    }

    protected abstract Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken);

    /// <param name="surveyPriceCents">What a survey costs this player (less with geology researched); the plain price when not given.</param>
    public static object PlotJson(PlotState plot, Guid player, long? surveyPriceCents = null) => new
    {
        x = plot.X,
        y = plot.Y,
        biome = (int)plot.Biome,
        owner = plot.Owner is { } owner ? new { name = owner.Name, mine = owner.AccountId == player } : null,
        priceCents = plot.PriceCents,
        survey = plot.Survey is { } survey ? SurveyHandler.SurveyJson(survey) : null,
        surveyPriceCents = plot.Owner?.AccountId == player ? surveyPriceCents ?? Deposits.SurveyPriceCents : (long?)null,
        company = plot.Company is { } company ? new { name = company.Name, member = company.Member } : null,
        canEnter = plot.CanEnter,
        name = plot.Name,
        // Public: anyone may come to look around; private: only the owner and the members of its company.
        @public = plot.Public,
    };
}

/// <summary>The map's size, the biomes, the price list and the player's own plots.</summary>
public sealed class InfoHandler(IServiceScopeFactory scopes, WorldMap map) : WorldHandler(scopes)
{
    public override string Type => "world.info";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var mine = await world.OwnedAsync(player, cancellationToken);
        var shared = await world.SharedAsync(player, cancellationToken);
        var tax = await world.TaxAsync(player, cancellationToken);
        return new Reply("world.info", new
        {
            width = map.Width,
            height = map.Height,
            plotKm = PlotRules.SideKm,
            biomes = Biomes.All.Values.Select(b => new
            {
                id = (int)b.Id,
                name = b.Name,
                buyable = b.Buyable,
                priceFactor = b.PriceFactor,
                bonuses = b.Bonuses.Select(bonus => new { text = bonus.Text, good = bonus.Good }),
            }),
            priceTiersCents = PlotPrices.TierCents,
            nextBasePriceCents = PlotPrices.Base(mine.Count),
            plots = mine.Select(p => new { x = p.X, y = p.Y, biome = (int)p.Biome, name = p.Name }),
            sharedPlots = shared.Select(p => new { x = p.X, y = p.Y, biome = (int)p.Biome }),
            // Every plot but the first costs this many thousandths of its price a day.
            taxPerMille = PlotTaxRules.PerMillePerDay,
            tax = new { dailyCents = tax.DailyCents, paidUntil = tax.PaidUntil.ToUnixTimeMilliseconds(), owedCents = tax.OwedCents },
        });
    }
}

/// <summary>Owned plots in a rectangle of the map, to draw them.</summary>
public sealed class AreaHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.area";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<AreaRequest>(payload);
        if (request.Width is < 1 or > 10_000 || request.Height is < 1 or > 10_000)
        {
            throw new GameException("bad_area", "Ask for at most 10000 × 10000 plots.");
        }

        var plots = await world.AreaAsync(request.X, request.Y, request.Width, request.Height, cancellationToken);
        return new Reply("world.area", new
        {
            request.X,
            request.Y,
            request.Width,
            request.Height,
            plots = plots.Select(p => new { x = p.X, y = p.Y, mine = p.Owner == player }),
        });
    }
}

public sealed class GetPlotHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.plot";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotRequest>(payload);
        return new Reply("world.plot", PlotJson(
            await world.PlotAsync(player, request.X, request.Y, cancellationToken), player, await world.SurveyPriceAsync(player, cancellationToken)));
    }
}

public sealed record PublicRequest(int X, int Y, bool Public);

/// <summary>The owner opens their plot to everyone (only to look around) or keeps it to themselves and their company;
/// answers with the plot like "world.plot".</summary>
public sealed class SetPlotPublicHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plot.setPublic";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PublicRequest>(payload);
        var player = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlotService>().SetPublicAsync(player, request.X, request.Y, request.Public, cancellationToken);
        var plot = await scope.ServiceProvider.GetRequiredService<WorldService>().PlotAsync(player, request.X, request.Y, cancellationToken);
        return new Reply("world.plot", WorldHandler.PlotJson(plot, player));
    }
}

public sealed record RenamePlotRequest(int X, int Y, string? Name);

/// <summary>The owner names a plot (an empty name takes it off); answers like "world.plot".</summary>
public sealed class RenamePlotHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.rename";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<RenamePlotRequest>(payload);
        return new Reply("world.plot", PlotJson(await world.RenameAsync(player, request.X, request.Y, request.Name, cancellationToken), player));
    }
}

public sealed class BuyPlotHandler(IServiceScopeFactory scopes) : WorldHandler(scopes)
{
    public override string Type => "world.buy";

    protected override async Task<Reply> RunAsync(WorldService world, Guid player, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotRequest>(payload);
        var purchase = await world.BuyAsync(player, request.X, request.Y, cancellationToken);
        return new Reply("world.bought", new
        {
            plot = PlotJson(purchase.Plot, player),
            balanceCents = purchase.BalanceCents,
            xp = new { xp = purchase.Xp.Xp, level = purchase.Xp.Level, leveledUp = purchase.Xp.LeveledUp },
            tool = purchase.Tool is { } tool ? new { id = tool, name = Items.Names[tool] } : null,
        });
    }
}
