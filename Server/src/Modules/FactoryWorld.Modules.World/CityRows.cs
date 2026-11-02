using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>How much a town has got delivered in all, for its level.</summary>
public sealed class TownProgressRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public long DeliveredCents { get; set; }
}

/// <summary>How much one player delivered to a town in all.</summary>
public sealed class TownContributionRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public Guid PlayerId { get; set; }

    public long DeliveredCents { get; set; }
}

/// <summary>How many pieces of an item a town got on a day; "trash" counts the rubbish taken away.</summary>
public sealed class TownDeliveryRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Day { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }
}

/// <summary>A shop's sales: until when they are worked out, the part of a customer carried over, and what it sold in all.</summary>
public sealed class ShopRow
{
    public Guid Id { get; set; }

    public DateTimeOffset SettledAt { get; set; }

    public double Carry { get; set; }

    public long SoldCents { get; set; }

    public long SoldItems { get; set; }
}

public sealed class ShopItemRow
{
    public Guid ShopId { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }

    public long PriceCents { get; set; }
}

/// <summary>Services an office sold on a day.</summary>
public sealed class ServiceSaleRow
{
    public Guid Id { get; set; }

    public int Day { get; set; }

    public int Count { get; set; }
}

public sealed class TownVoteRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid VoterId { get; set; }

    public Guid CandidateId { get; set; }
}

/// <summary>A town's mayor for a week (elected the week before; null when nobody voted) and what they set.</summary>
public sealed class TownPolicyRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public int Week { get; set; }

    public Guid? MayorId { get; set; }

    public int TaxPercent { get; set; }

    public string? Bonus { get; set; }

    /// <summary>Two thirds of the townsfolk recalled the mayor before the week was out.</summary>
    public bool Recalled { get; set; }

    /// <summary>The mayor's tax on the plots of the county, in thousandths of their worth a day.</summary>
    public int PlotTaxPerMille { get; set; }
}

public sealed record TownPolicy(Guid? Mayor, int TaxPercent, string? Bonus, bool Recalled = false, int PlotTaxPerMille = 0);

/// <summary>A town's festival, which the mayor holds with fine dishes: it wants goods dearer until <see cref="Until"/>.</summary>
public sealed class TownFestivalRow
{
    public int TownX { get; set; }

    public int TownY { get; set; }

    public DateTimeOffset Until { get; set; }
}
