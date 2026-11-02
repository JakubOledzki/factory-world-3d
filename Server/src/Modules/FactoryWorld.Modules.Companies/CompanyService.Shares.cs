using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Npgsql;

namespace FactoryWorld.Modules.Companies;

/// <summary>A company on the exchange: listing, trading its shares, takeovers.</summary>
public sealed partial class CompanyService
{
    // ---- Stock exchange ----

    /// <summary>The owner takes the company public, so anyone may buy its new shares, or off the exchange again.</summary>
    public Task<CompanyView> ListAsync(Guid player, bool listed, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            if (listed && !await companyTechnologies.ResearchedAsync(company, CompanyTechs.CorporateFinance, cancellationToken))
            {
                throw new GameException("research_needed", "The company researches corporate finance first.");
            }

            // The debut on the exchange is celebrated with a banquet: fine dishes from the warehouse, roasts first.
            if (listed && !await ListedAsync(company, cancellationToken))
            {
                var roasts = await StockAsync(company, Items.RoastBeef, cancellationToken);
                var cakes = await StockAsync(company, Items.Cheesecake, cancellationToken);
                if (roasts + cakes < CompanyMoney.ListingBanquet)
                {
                    throw new GameException("banquet_needed",
                        $"A listing is celebrated with a banquet: {CompanyMoney.ListingBanquet} fine dishes (roast beef or cheesecake) in the warehouse.");
                }

                var fromRoasts = Math.Min(roasts, CompanyMoney.ListingBanquet);
                if (fromRoasts > 0)
                {
                    await ChangeStockAsync(company, Items.RoastBeef, -fromRoasts, cancellationToken);
                }

                if (CompanyMoney.ListingBanquet - fromRoasts > 0)
                {
                    await ChangeStockAsync(company, Items.Cheesecake, -(CompanyMoney.ListingBanquet - fromRoasts), cancellationToken);
                }
            }

            await using (var update = await db.CommandAsync("UPDATE companies.companies SET listed = @listed WHERE id = @company", cancellationToken))
            {
                update.Parameters.AddWithValue("company", company);
                update.Parameters.AddWithValue("listed", listed);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, listed ? "listed" : "delisted", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Every company on the exchange, and every one whose shares the player holds.</summary>
    public Task<CompanyExchange> ExchangeAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var own = (await MembershipAsync(player, false, cancellationToken))?.Company;
            var values = await ValuesAsync(cancellationToken);
            var held = new Dictionary<Guid, long>();
            var totals = new Dictionary<Guid, long>();
            await using (var select = await db.CommandAsync(
                "SELECT company_id, sum(shares), sum(shares) FILTER (WHERE account_id = @player) FROM companies.shares GROUP BY company_id", cancellationToken))
            {
                select.Parameters.AddWithValue("player", player);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    totals[reader.GetGuid(0)] = Convert.ToInt64(reader.GetValue(1));
                    if (!reader.IsDBNull(2))
                    {
                        held[reader.GetGuid(0)] = Convert.ToInt64(reader.GetValue(2));
                    }
                }
            }

            return new CompanyExchange(values.Values
                .Where(v => v.Listed || held.GetValueOrDefault(v.Id) > 0 || v.Id == own)
                .Select(v =>
                {
                    var shares = totals.GetValueOrDefault(v.Id);
                    return new ListedCompany(v.Id, v.Name, v.Listed, CompanyMoney.SharePrice(v.Value, shares), shares, v.Profit, v.Value,
                        held.GetValueOrDefault(v.Id), v.Id == own);
                })
                .OrderByDescending(c => c.ValueCents).ToList());
        }, cancellationToken);

    /// <summary>Anyone buys new shares of a listed company; what they pay goes into its cash.</summary>
    public Task<CompanyExchange> BuySharesAsync(Guid player, Guid company, long shares, CancellationToken cancellationToken) =>
        TradeSharesAsync(player, company, shares, true, cancellationToken);

    /// <summary>A shareholder sells shares back to the company, which pays <see cref="CompanyMoney.BuybackPercent"/>% of their price from its cash.</summary>
    public Task<CompanyExchange> SellSharesAsync(Guid player, Guid company, long shares, CancellationToken cancellationToken) =>
        TradeSharesAsync(player, company, shares, false, cancellationToken);

    private async Task<CompanyExchange> TradeSharesAsync(Guid player, Guid company, long shares, bool buy, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(async () =>
        {
            if (shares is < 1 or > 1_000_000)
            {
                throw new GameException("bad_quantity", "Trade 1-1000000 shares.");
            }

            await RequireCharacterAsync(player, cancellationToken);
            await LockAsync(company, cancellationToken);
            await SettleAsync(company, cancellationToken);
            var price = await SharePriceAsync(company, cancellationToken);
            if (buy)
            {
                if (!await ListedAsync(company, cancellationToken))
                {
                    throw new GameException("not_listed", "This company is not on the exchange.");
                }

                var cost = price * shares;
                await wallet.DebitAsync(player, cost, $"company {company} shares", cancellationToken);
                await ChangeMoneyAsync(company, cost, 0, cancellationToken);
                await IssueSharesAsync(company, player, shares, cancellationToken);
                await LogAsync(company, player, "sharesBought", cancellationToken, quantity: shares);
            }
            else
            {
                if ((await SharesAsync(company, cancellationToken)).GetValueOrDefault(player) < shares)
                {
                    throw new GameException("not_enough_shares", "You don't hold that many shares.");
                }

                var payout = price * shares * CompanyMoney.BuybackPercent / 100;
                await ChangeMoneyAsync(company, -payout, 0, cancellationToken);
                await IssueSharesAsync(company, player, -shares, cancellationToken);
                await wallet.CreditAsync(player, payout, $"company {company} shares sold", cancellationToken);
                await LogAsync(company, player, "sharesSold", cancellationToken, quantity: shares);
            }
        }, cancellationToken);

        return await ExchangeAsync(player, cancellationToken);
    }

    /// <summary>A player holding more than half of a company's shares makes themselves its owner; the old owner becomes a partner.</summary>
    public Task<CompanyView> TakeoverAsync(Guid player, Guid company, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await RequireCharacterAsync(player, cancellationToken);
            await LockAsync(company, cancellationToken);
            var shares = await SharesAsync(company, cancellationToken);
            if (shares.GetValueOrDefault(player) * 2 <= shares.Values.Sum())
            {
                throw new GameException("no_majority", "You need more than half of the company's shares.");
            }

            var membership = await MembershipAsync(player, true, cancellationToken);
            if (membership is { } m && m.Company != company)
            {
                throw new GameException("already_in_company", "Leave your company first.");
            }

            if (membership?.Role == Roles.Owner)
            {
                throw new GameException("already_owner", "You run this company already.");
            }

            await using (var demote = await db.CommandAsync(
                "UPDATE companies.members SET role = @partner WHERE company_id = @company AND role = @owner", cancellationToken))
            {
                demote.Parameters.AddWithValue("company", company);
                demote.Parameters.AddWithValue("partner", Roles.Partner);
                demote.Parameters.AddWithValue("owner", Roles.Owner);
                await demote.ExecuteNonQueryAsync(cancellationToken);
            }

            if (membership is null)
            {
                await DeleteInvitesAsync(player, null, cancellationToken);
                await AddMemberAsync(company, player, Roles.Owner, cancellationToken);
            }
            else
            {
                await UpdateRoleAsync(player, Roles.Owner, cancellationToken);
            }

            await LogAsync(company, player, "takeover", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);
}
