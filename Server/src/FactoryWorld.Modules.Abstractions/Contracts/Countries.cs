using System.Security.Cryptography;
using System.Text;

namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// The countries players govern: which country a plot lies in, who is a citizen and holds which office, the treasury and
/// the duties and embargoes between countries. Codes are three letters (ADM0_A3 of Natural Earth, e.g. "POL").
/// </summary>
public interface ICountries
{
    /// <summary>The country the plot lies in, or null (open sea, or no country there).</summary>
    Task<string?> CountryOfPlotAsync(int x, int y, CancellationToken cancellationToken);

    /// <summary>The country the player is a citizen of, or null.</summary>
    Task<string?> CitizenshipAsync(Guid player, CancellationToken cancellationToken);

    /// <summary>The offices (<see cref="CountryOffices"/>) the player holds in the country; empty for none.</summary>
    Task<IReadOnlySet<string>> OfficesAsync(Guid player, string country, CancellationToken cancellationToken);

    /// <summary>The account of the country's treasury in <see cref="IWallet"/> (<see cref="CountryTreasury.Account"/>), or null
    /// while the country has no government.</summary>
    Task<Guid?> TreasuryAsync(string country, CancellationToken cancellationToken);

    /// <summary>What country <paramref name="to"/> charges on goods coming from country <paramref name="from"/>.</summary>
    Task<CountryTrade> TradeAsync(string from, string to, CancellationToken cancellationToken);
}

/// <param name="Embargo">The country takes no goods from the other at all.</param>
/// <param name="TariffPercent">The duty on every good, in percent of what the trader pays for it.</param>
/// <param name="ItemTariffPercent">Duties on single goods, instead of <paramref name="TariffPercent"/>.</param>
public sealed record CountryTrade(bool Embargo, int TariffPercent, IReadOnlyDictionary<string, int> ItemTariffPercent)
{
    public static readonly CountryTrade Free = new(false, 0, new Dictionary<string, int>());

    /// <summary>The duty on one good, in percent.</summary>
    public int PercentOn(string item) => ItemTariffPercent.TryGetValue(item, out var percent) ? percent : TariffPercent;
}

public static class CountryOffices
{
    public const string President = "president";

    /// <summary>The minister of the interior: appoints the police chief and sets what is banned.</summary>
    public const string Interior = "interior";

    /// <summary>The minister of justice: grants search warrants and hears appeals.</summary>
    public const string Justice = "justice";

    public const string Finance = "finance";

    public const string Industry = "industry";

    public const string Transport = "transport";

    public const string Foreign = "foreign";

    /// <summary>The ministries a president fills.</summary>
    public static readonly IReadOnlyList<string> Ministries = [Finance, Industry, Transport, Foreign, Interior, Justice];
}

public static class CountryTreasury
{
    /// <summary>The wallet account of a country's treasury: the same id for the same code every time.</summary>
    public static Guid Account(string country) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("country-treasury:" + country.ToUpperInvariant())));
}
