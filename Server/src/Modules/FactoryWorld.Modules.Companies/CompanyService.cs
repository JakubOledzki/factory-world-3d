using System.Text.RegularExpressions;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Npgsql;

namespace FactoryWorld.Modules.Companies;

/// <summary>
/// A company is a few players building one factory: its owner invites players, members bring their
/// plots in, and everyone works on all of them with the rights of their role. Members share a
/// warehouse, and a log shows who put in or took out what.
/// </summary>
public static partial class CompanyRules
{
    public const int MinNameLength = 3;

    public const int MaxNameLength = 24;

    /// <summary>Most members, counting open invitations.</summary>
    public const int MaxMembers = 20;

    /// <summary>Most items, of all kinds together, the warehouse holds.</summary>
    public const long WarehouseCapacity = 50_000;

    /// <summary>With a headquarters on one of its plots (a city building) a company takes more members and stores more.</summary>
    public const int HqMaxMembers = 30;

    public const long HqWarehouseCapacity = 150_000;

    public static int MaxMembersFor(bool hq) => hq ? HqMaxMembers : MaxMembers;

    public static long CapacityFor(bool hq) => hq ? HqWarehouseCapacity : WarehouseCapacity;

    /// <summary>How many of the latest log entries members see.</summary>
    public const int LogLength = 30;

    /// <summary>Character level a player needs before bringing a plot into a company, so that fresh accounts
    /// can't buy plots at the first, cheapest prices and hand them over.</summary>
    public const int PlotMinLevel = 10;

    /// <summary>Letters, digits, spaces and <c>- _ . &amp;</c>, starting and ending with a letter or digit.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}](?:[\p{L}\p{N} _.&-]*[\p{L}\p{N}])?$")]
    public static partial Regex NamePattern();

    public static string CheckName(string? name)
    {
        var trimmed = Regex.Replace(name ?? "", @"\s+", " ").Trim();
        if (trimmed.Length is < MinNameLength or > MaxNameLength || !NamePattern().IsMatch(trimmed))
        {
            throw new GameException("invalid_company_name", $"A company name has {MinNameLength}-{MaxNameLength} letters, digits or spaces.");
        }

        return trimmed;
    }
}

/// <summary>A member's role: what they may do in the company and on its plots.</summary>
public static class Roles
{
    /// <summary>Runs the company: invites and removes members, gives roles; otherwise a partner and a treasurer.</summary>
    public const string Owner = "owner";

    /// <summary>Builds on the company's plots, sets up train routes and takes from its warehouse, storages and stations; chooses research and big projects.</summary>
    public const string Partner = "partner";

    /// <summary>Manages the company's cash: pays out, buys and sells for it, sets tasks with wages and signs deals with other companies.</summary>
    public const string Treasurer = "treasurer";

    /// <summary>Builds only on the plots the owner put them in charge of; elsewhere works like a worker.</summary>
    public const string Manager = "manager";

    /// <summary>Sets up train routes and sends trains from every company plot, and takes from storages and stations.</summary>
    public const string Logistician = "logistician";

    /// <summary>Works on the company's plots (gathers, farms, mines, runs machines) and puts things in, but takes nothing out and builds nothing.</summary>
    public const string Worker = "worker";

    public static readonly IReadOnlyList<string> All = [Owner, Partner, Treasurer, Manager, Logistician, Worker];

    private static readonly IReadOnlySet<PlotRight> Leading = new HashSet<PlotRight> { PlotRight.Work, PlotRight.Take, PlotRight.Build, PlotRight.Routes };

    private static readonly IReadOnlySet<PlotRight> Building = new HashSet<PlotRight> { PlotRight.Work, PlotRight.Take, PlotRight.Build };

    private static readonly IReadOnlySet<PlotRight> Taking = new HashSet<PlotRight> { PlotRight.Work, PlotRight.Take };

    private static readonly IReadOnlySet<PlotRight> Shipping = new HashSet<PlotRight> { PlotRight.Work, PlotRight.Take, PlotRight.Routes };

    private static readonly IReadOnlySet<PlotRight> Working = new HashSet<PlotRight> { PlotRight.Work };

    /// <param name="manages">Whether the member is in charge of this plot (only a manager's role cares).</param>
    public static IReadOnlySet<PlotRight> Rights(string role, bool manages = false) => role switch
    {
        Owner or Partner => Leading,
        Treasurer => Taking,
        Logistician => Shipping,
        Manager => manages ? Building : Working,
        _ => Working,
    };

    public static bool MayTake(string role) => role != Worker;

    /// <summary>Pays out of the company's cash, buys and sells for it, sets wages and signs deals.</summary>
    public static bool HandlesMoney(string role) => role is Owner or Treasurer;

    /// <summary>Chooses the company's research and big projects.</summary>
    public static bool Leads(string role) => role is Owner or Partner;
}

public sealed record Member(Guid AccountId, string Name, string Role, DateTimeOffset JoinedAt);

/// <param name="Manager">The member in charge of building on it, if any.</param>
public sealed record CompanyPlot(int X, int Y, int Biome, Guid OwnerId, string OwnerName, string? Manager = null);

public sealed record LogEntry(DateTimeOffset At, string Who, string Action, string? Item, long Quantity, string? Other);

public sealed record Invitation(Guid CompanyId, string CompanyName, int Members);

public sealed record CompanyState(
    Guid Id, string Name, string Role, IReadOnlyList<Member> Members, IReadOnlyList<string> Invited, IReadOnlyList<CompanyPlot> Plots,
    IReadOnlyDictionary<string, long> Warehouse, IReadOnlyList<LogEntry> Log, bool Hq = false, CompanyEconomy? Economy = null);

/// <param name="Company">The player's company, or null when they are in none.</param>
/// <param name="Invitations">Companies that invited the player.</param>
public sealed record CompanyView(Guid Player, CompanyState? Company, IReadOnlyList<Invitation> Invitations);

public sealed partial class CompanyService(
    GameDb db, IInventory inventory, ICharacterNames names, IPlotRegistry plots, IPlotObjects objects, IWallet wallet,
    IMarketPrices prices, ITechnologies technologies, ICompanyTechnologies companyTechnologies, ICharacterXp xp, IAnnouncements announcements,
    TimeProvider time)
{
    /// <summary>Whether a headquarters stands on any of the company's plots.</summary>
    public async Task<bool> HasHqAsync(Guid company, CancellationToken cancellationToken)
    {
        var at = new List<(int, int)>();
        await using (var select = await db.CommandAsync("SELECT x, y FROM companies.plots WHERE company_id = @company", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                at.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        foreach (var (x, y) in at)
        {
            if ((await objects.ObjectsAsync(x, y, cancellationToken)).Any(o => o.Kind == Items.CompanyHq))
            {
                return true;
            }
        }

        return false;
    }

    public Task<CompanyView> ViewAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(() => ReadAsync(player, cancellationToken), cancellationToken);

    /// <summary>Founds a company with the player as its owner.</summary>
    public Task<CompanyView> CreateAsync(Guid player, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var checkedName = CompanyRules.CheckName(name);
            await RequireCharacterAsync(player, cancellationToken);
            if (await MembershipAsync(player, false, cancellationToken) is not null)
            {
                throw new GameException("already_in_company", "Leave your company first.");
            }

            var id = Guid.NewGuid();
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO companies.companies (id, name, normalized_name, created_at, dividend_at) VALUES (@id, @name, @normalized, @at, @at)
                ON CONFLICT (normalized_name) DO NOTHING
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("name", checkedName);
                insert.Parameters.AddWithValue("normalized", checkedName.ToLowerInvariant());
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("company_name_taken", "A company with this name exists already.");
                }
            }

            await AddMemberAsync(id, player, Roles.Owner, cancellationToken);
            await IssueSharesAsync(id, player, CompanyMoney.FounderShares, cancellationToken);
            await LogAsync(id, player, "founded", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner invites a player by their character's name.</summary>
    public Task<CompanyView> InviteAsync(Guid player, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            var invited = await names.AccountAsync(name ?? "", cancellationToken)
                ?? throw new GameException("no_such_player", "There is no player with this name.");
            if (invited == player || await MembershipAsync(invited, false, cancellationToken) is not null)
            {
                throw new GameException("already_in_company", "This player is in a company already.");
            }

            if (await CountAsync("SELECT count(*) FROM companies.members WHERE company_id = @company", company, cancellationToken)
                + await CountAsync("SELECT count(*) FROM companies.invites WHERE company_id = @company", company, cancellationToken)
                >= CompanyRules.MaxMembersFor(await HasHqAsync(company, cancellationToken)))
            {
                throw new GameException("company_full", "The company has as many members as it can take.");
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO companies.invites (company_id, account_id, invited_at) VALUES (@company, @account, @at) ON CONFLICT DO NOTHING",
                cancellationToken))
            {
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("account", invited);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner takes back an invitation nobody answered yet.</summary>
    public Task<CompanyView> UninviteAsync(Guid player, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            if (await names.AccountAsync(name ?? "", cancellationToken) is { } invited)
            {
                await DeleteInvitesAsync(invited, company, cancellationToken);
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Joins a company that invited the player, as a worker; other invitations lapse.</summary>
    public Task<CompanyView> AcceptAsync(Guid player, Guid company, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await LockAsync(company, cancellationToken);
            if (await MembershipAsync(player, false, cancellationToken) is not null)
            {
                throw new GameException("already_in_company", "Leave your company first.");
            }

            if (await DeleteInvitesAsync(player, company, cancellationToken) == 0)
            {
                throw new GameException("no_invite", "This company has not invited you.");
            }

            await DeleteInvitesAsync(player, null, cancellationToken);
            await AddMemberAsync(company, player, Roles.Worker, cancellationToken);
            await LogAsync(company, player, "joined", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    public Task<CompanyView> DeclineAsync(Guid player, Guid company, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await DeleteInvitesAsync(player, company, cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Leaves the company, taking the player's plots out of it. The owner hands the company to
    /// another member first; the last member to leave closes it and gets what is left in the warehouse.
    /// </summary>
    public Task<CompanyView> LeaveAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            var members = await CountAsync("SELECT count(*) FROM companies.members WHERE company_id = @company", company, cancellationToken);
            if (role == Roles.Owner && members > 1)
            {
                throw new GameException("hand_over_first", "Make another member the owner before you leave.");
            }

            await RemoveMemberAsync(company, player, cancellationToken);
            if (members > 1)
            {
                await LogAsync(company, player, "left", cancellationToken);
            }
            else
            {
                await CloseAsync(company, player, cancellationToken);
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner removes a member; the member's plots leave the company with them.</summary>
    public Task<CompanyView> KickAsync(Guid player, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            var member = await MemberByNameAsync(company, name, cancellationToken);
            if (member == player)
            {
                throw new GameException("not_yourself", "Leave the company instead.");
            }

            await RemoveMemberAsync(company, member, cancellationToken);
            await LogAsync(company, player, "kicked", cancellationToken, other: member);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner gives a member another role; making someone the owner makes the old owner a partner.</summary>
    public Task<CompanyView> SetRoleAsync(Guid player, string? name, string? role, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Roles.All.Contains(role ?? ""))
            {
                throw new GameException("bad_role", $"A role is one of: {string.Join(", ", Roles.All)}.");
            }

            var (company, _) = await RequireOwnerAsync(player, cancellationToken);
            var member = await MemberByNameAsync(company, name, cancellationToken);
            if (member == player)
            {
                throw new GameException("not_yourself", "Make another member the owner instead.");
            }

            if (role == Roles.Owner)
            {
                await UpdateRoleAsync(player, Roles.Partner, cancellationToken);
            }

            await UpdateRoleAsync(member, role!, cancellationToken);
            await LogAsync(company, player, "role", cancellationToken, item: role, other: member);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Brings one of the player's own plots into their company.</summary>
    public Task<CompanyView> AddPlotAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireMemberAsync(player, cancellationToken);
            if (await plots.FindAsync(x, y, cancellationToken) is not { } plot || plot.Owner != player)
            {
                throw new GameException("not_owner", "This is not your plot.");
            }

            if (await xp.LevelAsync(player, cancellationToken) < CompanyRules.PlotMinLevel)
            {
                throw new GameException("level_too_low", $"Bringing a plot into a company takes level {CompanyRules.PlotMinLevel}.");
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO companies.plots (x, y, company_id, owner_id, shares) VALUES (@x, @y, @company, @owner, @shares) ON CONFLICT DO NOTHING",
                cancellationToken))
            {
                // What the plot cost buys its owner shares, at the price they have now; they go back when the plot leaves.
                var shares = plot.PriceCents / await SharePriceAsync(company, cancellationToken);
                insert.Parameters.AddWithValue("x", x);
                insert.Parameters.AddWithValue("y", y);
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("owner", player);
                insert.Parameters.AddWithValue("shares", shares);
                if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    await IssueSharesAsync(company, player, shares, cancellationToken);
                    await LogAsync(company, player, "plotAdded", cancellationToken, item: $"{x}:{y}", quantity: shares);
                }
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes one of the player's plots back out of the company; what others put on it stays there.</summary>
    public Task<CompanyView> RemovePlotAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireMemberAsync(player, cancellationToken);
            await using (var delete = await db.CommandAsync(
                "DELETE FROM companies.plots WHERE x = @x AND y = @y AND owner_id = @owner AND company_id = @company RETURNING shares", cancellationToken))
            {
                delete.Parameters.AddWithValue("x", x);
                delete.Parameters.AddWithValue("y", y);
                delete.Parameters.AddWithValue("owner", player);
                delete.Parameters.AddWithValue("company", company);
                if (await delete.ExecuteScalarAsync(cancellationToken) is not long shares)
                {
                    throw new GameException("not_owner", "This is not your plot.");
                }

                // The shares the plot bought go with it, as far as the owner still has them.
                await IssueSharesAsync(company, player, -shares, cancellationToken);
            }

            await LogAsync(company, player, "plotRemoved", cancellationToken, item: $"{x}:{y}");
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Any member puts items from their inventory into the warehouse.</summary>
    public Task<CompanyView> PutAsync(Guid player, string? item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            var (company, _) = await RequireMemberAsync(player, cancellationToken);
            await LockAsync(company, cancellationToken);
            var stored = await CountAsync(
                "SELECT coalesce(sum(quantity), 0) FROM companies.warehouse WHERE company_id = @company", company, cancellationToken);
            var capacity = CompanyRules.CapacityFor(await HasHqAsync(company, cancellationToken));
            if (stored + quantity > capacity)
            {
                throw new GameException("warehouse_full", $"The warehouse holds {capacity} items.");
            }

            await inventory.RemoveAsync(player, item!, quantity, cancellationToken);
            await ChangeStockAsync(company, item!, quantity, cancellationToken);
            await LogAsync(company, player, "put", cancellationToken, item, quantity);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner and partners take items out of the warehouse into their inventory, as many as fit; the rest stays.</summary>
    public Task<CompanyView> TakeAsync(Guid player, string? item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            if (!Roles.MayTake(role))
            {
                throw new GameException("not_allowed", "Workers do not take from the warehouse.");
            }

            var taken = await inventory.FitAsync(player, item!, quantity, cancellationToken);
            await ChangeStockAsync(company, item!, -taken, cancellationToken);
            await inventory.AddAsync(player, item!, taken, cancellationToken);
            await LogAsync(company, player, "take", cancellationToken, item, taken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    private static void CheckStack(string? item, long quantity)
    {
        if (!Items.Exists(item ?? ""))
        {
            throw new GameException("unknown_item", "Unknown item.");
        }

        if (quantity is < 1 or > CompanyRules.HqWarehouseCapacity)
        {
            throw new GameException("bad_quantity", $"Quantity must be 1-{CompanyRules.HqWarehouseCapacity}.");
        }
    }

    private async Task RequireCharacterAsync(Guid player, CancellationToken cancellationToken)
    {
        if (!(await names.NamesAsync([player], cancellationToken)).ContainsKey(player))
        {
            throw new GameException("no_character", "Create a character first.");
        }
    }

    /// <summary>The player's company and role, locked until the transaction ends when <paramref name="forUpdate"/>.</summary>
    private async Task<(Guid Company, string Role)?> MembershipAsync(Guid player, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT company_id, role FROM companies.members WHERE account_id = @player" + (forUpdate ? " FOR UPDATE" : ""), cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1)) : null;
    }

    private async Task<(Guid Company, string Role)> RequireMemberAsync(Guid player, CancellationToken cancellationToken)
    {
        var membership = await MembershipAsync(player, false, cancellationToken)
            ?? throw new GameException("no_company", "You are not in a company.");
        // Changes to one company happen one at a time; then read the role again, it may have changed meanwhile.
        await LockAsync(membership.Company, cancellationToken);
        return await MembershipAsync(player, true, cancellationToken) ?? throw new GameException("no_company", "You are not in a company.");
    }

    private async Task<(Guid Company, string Role)> RequireOwnerAsync(Guid player, CancellationToken cancellationToken)
    {
        var membership = await RequireMemberAsync(player, cancellationToken);
        return membership.Role == Roles.Owner
            ? membership
            : throw new GameException("not_allowed", "Only the company's owner may do this.");
    }

    private async Task LockAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT 1 FROM companies.companies WHERE id = @id FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("id", company);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new GameException("no_company", "This company no longer exists.");
        }
    }

    private async Task<Guid> MemberByNameAsync(Guid company, string? name, CancellationToken cancellationToken)
    {
        if (await names.AccountAsync(name ?? "", cancellationToken) is { } account
            && await MembershipAsync(account, false, cancellationToken) is { } membership && membership.Company == company)
        {
            return account;
        }

        throw new GameException("not_member", "This player is not in your company.");
    }

    private async Task<long> CountAsync(string sql, Guid company, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(sql, cancellationToken);
        command.Parameters.AddWithValue("company", company);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task AddMemberAsync(Guid company, Guid player, string role, CancellationToken cancellationToken)
    {
        await using var insert = await db.CommandAsync(
            "INSERT INTO companies.members (account_id, company_id, role, joined_at) VALUES (@player, @company, @role, @at) ON CONFLICT DO NOTHING",
            cancellationToken);
        insert.Parameters.AddWithValue("player", player);
        insert.Parameters.AddWithValue("company", company);
        insert.Parameters.AddWithValue("role", role);
        insert.Parameters.AddWithValue("at", time.GetUtcNow());
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new GameException("already_in_company", "Leave your company first.");
        }
    }

    private async Task UpdateRoleAsync(Guid player, string role, CancellationToken cancellationToken)
    {
        await using var update = await db.CommandAsync("UPDATE companies.members SET role = @role WHERE account_id = @player", cancellationToken);
        update.Parameters.AddWithValue("player", player);
        update.Parameters.AddWithValue("role", role);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RemoveMemberAsync(Guid company, Guid player, CancellationToken cancellationToken)
    {
        // Their shares stay theirs, but for those their plots bought, which leave with the plots; tasks given to them and plots
        // they were in charge of are free again.
        await using (var plotsOut = await db.CommandAsync(
            "WITH gone AS (DELETE FROM companies.plots WHERE company_id = @company AND owner_id = @player RETURNING shares) SELECT COALESCE(sum(shares), 0)::bigint FROM gone",
            cancellationToken))
        {
            plotsOut.Parameters.AddWithValue("company", company);
            plotsOut.Parameters.AddWithValue("player", player);
            await IssueSharesAsync(company, player, -(long)(await plotsOut.ExecuteScalarAsync(cancellationToken))!, cancellationToken);
        }

        await using var delete = await db.CommandAsync(
            """
            UPDATE companies.plots SET manager_id = NULL WHERE company_id = @company AND manager_id = @player;
            UPDATE companies.tasks SET assignee_id = NULL WHERE company_id = @company AND assignee_id = @player AND done_at IS NULL;
            DELETE FROM companies.members WHERE company_id = @company AND account_id = @player;
            """, cancellationToken);
        delete.Parameters.AddWithValue("company", company);
        delete.Parameters.AddWithValue("player", player);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The last member left: the cash (with wages nobody earned) is shared out by shares, among members who left and investors
    /// too, the rest of it and what is in the warehouse go to the last member, and the company is gone.</summary>
    private async Task CloseAsync(Guid company, Guid player, CancellationToken cancellationToken)
    {
        long cents;
        await using (var cash = await db.CommandAsync(
            """
            SELECT c.cash_cents + coalesce((SELECT sum(t.wage_cents - t.paid_cents) FROM companies.tasks t WHERE t.company_id = c.id AND t.done_at IS NULL), 0)::bigint
            FROM companies.companies c WHERE c.id = @company
            """, cancellationToken))
        {
            cash.Parameters.AddWithValue("company", company);
            cents = await cash.ExecuteScalarAsync(cancellationToken) is long found ? found : 0;
        }

        if (cents > 0)
        {
            var shares = await SharesAsync(company, cancellationToken);
            var total = shares.Values.Sum();
            var left = cents;
            foreach (var (holder, held) in shares.Where(s => s.Key != player))
            {
                var part = (long)((Int128)cents * held / total);
                if (part > 0)
                {
                    await wallet.CreditAsync(holder, part, $"company {company} closed: {held} shares", cancellationToken);
                    left -= part;
                }
            }

            if (left > 0)
            {
                await wallet.CreditAsync(player, left, $"company {company} closed", cancellationToken);
            }
        }

        var stock = new List<(string, long)>();
        await using (var select = await db.CommandAsync(
            "SELECT item, quantity FROM companies.warehouse WHERE company_id = @company AND quantity > 0", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stock.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var (item, quantity) in stock)
        {
            await inventory.AddAsync(player, item, quantity, cancellationToken);
        }

        await using var delete = await db.CommandAsync(
            """
            DELETE FROM companies.warehouse WHERE company_id = @company;
            DELETE FROM companies.invites WHERE company_id = @company;
            DELETE FROM companies.plots WHERE company_id = @company;
            DELETE FROM companies.log WHERE company_id = @company;
            DELETE FROM companies.shares WHERE company_id = @company;
            DELETE FROM companies.income WHERE company_id = @company;
            DELETE FROM companies.project_parts WHERE company_id = @company;
            DELETE FROM companies.projects WHERE company_id = @company;
            DELETE FROM companies.orders WHERE company_id = @company;
            DELETE FROM companies.tasks WHERE company_id = @company;
            UPDATE companies.deals SET state = 'cancelled' WHERE (supplier_id = @company OR buyer_id = @company) AND state IN ('offered', 'active');
            DELETE FROM companies.companies WHERE id = @company;
            """, cancellationToken);
        delete.Parameters.AddWithValue("company", company);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes the player's invitation from one company, or from all when <paramref name="company"/> is null.</summary>
    private async Task<int> DeleteInvitesAsync(Guid player, Guid? company, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync(
            "DELETE FROM companies.invites WHERE account_id = @player AND (@company::uuid IS NULL OR company_id = @company)", cancellationToken);
        delete.Parameters.AddWithValue("player", player);
        delete.Parameters.Add(new NpgsqlParameter("company", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)company ?? DBNull.Value });
        return await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ChangeStockAsync(Guid company, string item, long change, CancellationToken cancellationToken)
    {
        // One conditional statement: two members at once can never take out more than there is.
        await using var command = await db.CommandAsync(change > 0
            ? """
              INSERT INTO companies.warehouse (company_id, item, quantity) VALUES (@company, @item, @change)
              ON CONFLICT (company_id, item) DO UPDATE SET quantity = warehouse.quantity + excluded.quantity
              RETURNING quantity
              """
            : """
              UPDATE companies.warehouse SET quantity = quantity + @change
              WHERE company_id = @company AND item = @item AND quantity + @change >= 0
              RETURNING quantity
              """, cancellationToken);
        command.Parameters.AddWithValue("company", company);
        command.Parameters.AddWithValue("item", item);
        command.Parameters.AddWithValue("change", change);
        if (await command.ExecuteScalarAsync(cancellationToken) is not long left)
        {
            throw new GameException("not_enough_items", "The warehouse doesn't have that many.");
        }

        if (left == 0)
        {
            await using var delete = await db.CommandAsync(
                "DELETE FROM companies.warehouse WHERE company_id = @company AND item = @item", cancellationToken);
            delete.Parameters.AddWithValue("company", company);
            delete.Parameters.AddWithValue("item", item);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task LogAsync(
        Guid company, Guid player, string action, CancellationToken cancellationToken, string? item = null, long quantity = 0, Guid? other = null)
    {
        await using var insert = await db.CommandAsync(
            """
            INSERT INTO companies.log (company_id, at, account_id, action, item, quantity, other)
            VALUES (@company, @at, @player, @action, @item, @quantity, @other)
            """, cancellationToken);
        insert.Parameters.AddWithValue("company", company);
        insert.Parameters.AddWithValue("at", time.GetUtcNow());
        insert.Parameters.AddWithValue("player", player);
        insert.Parameters.AddWithValue("action", action);
        insert.Parameters.AddWithValue("item", (object?)item ?? DBNull.Value);
        insert.Parameters.AddWithValue("quantity", quantity);
        insert.Parameters.Add(new NpgsqlParameter("other", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)other ?? DBNull.Value });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<CompanyView> ReadAsync(Guid player, CancellationToken cancellationToken)
    {
        if (await MembershipAsync(player, false, cancellationToken) is not { } membership)
        {
            return new CompanyView(player, null, await InvitationsAsync(player, cancellationToken));
        }

        var company = membership.Company;
        await SettleAsync(company, cancellationToken);
        string name;
        await using (var select = await db.CommandAsync("SELECT name FROM companies.companies WHERE id = @company", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            name = (string)(await select.ExecuteScalarAsync(cancellationToken))!;
        }

        var members = new List<(Guid, string, DateTimeOffset)>();
        await using (var select = await db.CommandAsync(
            "SELECT account_id, role, joined_at FROM companies.members WHERE company_id = @company ORDER BY joined_at", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add((reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        var invited = await GuidsAsync("SELECT account_id FROM companies.invites WHERE company_id = @company ORDER BY invited_at", company, cancellationToken);

        var plotRows = new List<(int, int, Guid, Guid?)>();
        await using (var select = await db.CommandAsync(
            "SELECT x, y, owner_id, manager_id FROM companies.plots WHERE company_id = @company ORDER BY owner_id, x, y", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                plotRows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3)));
            }
        }

        var warehouse = new SortedDictionary<string, long>();
        await using (var select = await db.CommandAsync(
            "SELECT item, quantity FROM companies.warehouse WHERE company_id = @company", cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                warehouse[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        var log = new List<(DateTimeOffset, Guid, string, string?, long, Guid?)>();
        await using (var select = await db.CommandAsync(
            "SELECT at, account_id, action, item, quantity, other FROM companies.log WHERE company_id = @company ORDER BY id DESC LIMIT @limit",
            cancellationToken))
        {
            select.Parameters.AddWithValue("company", company);
            select.Parameters.AddWithValue("limit", CompanyRules.LogLength);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                log.Add((reader.GetFieldValue<DateTimeOffset>(0), reader.GetGuid(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
            }
        }

        var economy = await EconomyAsync(company, cancellationToken);
        var everyone = members.Select(m => m.Item1).Concat(invited).Concat(log.Select(l => l.Item2))
            .Concat(log.Where(l => l.Item6 is not null).Select(l => l.Item6!.Value)).Concat(economy.People).ToHashSet();
        var known = await names.NamesAsync(everyone, cancellationToken);
        string NameOf(Guid id) => known.GetValueOrDefault(id, "?");
        economy = economy with { Names = known };

        var plotStates = new List<CompanyPlot>();
        foreach (var (x, y, owner, manager) in plotRows)
        {
            var biome = (await plots.FindAsync(x, y, cancellationToken))?.Biome ?? 0;
            plotStates.Add(new CompanyPlot(x, y, biome, owner, NameOf(owner), manager is { } m ? NameOf(m) : null));
        }

        return new CompanyView(player, new CompanyState(
            company, name, membership.Role,
            members.Select(m => new Member(m.Item1, NameOf(m.Item1), m.Item2, m.Item3)).ToList(),
            invited.Select(NameOf).ToList(),
            plotStates,
            warehouse,
            log.Select(l => new LogEntry(l.Item1, NameOf(l.Item2), l.Item3, l.Item4, l.Item5, l.Item6 is { } o ? NameOf(o) : null)).ToList(),
            await HasHqAsync(company, cancellationToken),
            economy),
            []);
    }

    private async Task<IReadOnlyList<Invitation>> InvitationsAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(
            """
            SELECT c.id, c.name, (SELECT count(*) FROM companies.members m WHERE m.company_id = c.id)
            FROM companies.invites i JOIN companies.companies c ON c.id = i.company_id
            WHERE i.account_id = @player ORDER BY i.invited_at
            """, cancellationToken);
        select.Parameters.AddWithValue("player", player);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var invitations = new List<Invitation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            invitations.Add(new Invitation(reader.GetGuid(0), reader.GetString(1), (int)reader.GetInt64(2)));
        }

        return invitations;
    }

    private async Task<List<Guid>> GuidsAsync(string sql, Guid company, CancellationToken cancellationToken)
    {
        await using var select = await db.CommandAsync(sql, cancellationToken);
        select.Parameters.AddWithValue("company", company);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}

/// <summary>What members may do on their company's plots, for the world module, and companies as other modules see them.</summary>
public sealed class CompanyPlots(GameDb db, TimeProvider time) : ICompanyPlots, ICompanies
{
    private static readonly IReadOnlySet<PlotRight> None = new HashSet<PlotRight>();

    public async Task<IReadOnlySet<PlotRight>> RightsAsync(Guid player, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT m.role, p.manager_id IS NOT DISTINCT FROM m.account_id FROM companies.plots p JOIN companies.members m ON m.company_id = p.company_id
            WHERE p.x = @x AND p.y = @y AND m.account_id = @player
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Roles.Rights(reader.GetString(0), reader.GetBoolean(1)) : None;
    }

    public async Task<CompanyMembership?> MembershipAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT c.id, c.name, m.role FROM companies.members m JOIN companies.companies c ON c.id = m.company_id WHERE m.account_id = @player",
            cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CompanyMembership(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), Roles.Leads(reader.GetString(2)))
            : null;
    }

    public async Task<IReadOnlyList<Guid>> MembersAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT account_id FROM companies.members WHERE company_id = @company", cancellationToken);
        command.Parameters.AddWithValue("company", company);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(reader.GetGuid(0));
        }

        return members;
    }

    public async Task<IReadOnlySet<string>> ProjectsAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT p.project FROM companies.projects p JOIN companies.members m ON m.company_id = p.company_id
            WHERE m.account_id = @player AND p.built_at <= @now
            """, cancellationToken);
        command.Parameters.AddWithValue("player", player);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var done = new HashSet<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            done.Add(reader.GetString(0));
        }

        return done;
    }

    public async Task<PlotCompany?> CompanyAsync(Guid viewer, int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT c.name, EXISTS (SELECT 1 FROM companies.members m WHERE m.company_id = c.id AND m.account_id = @viewer)
            FROM companies.plots p JOIN companies.companies c ON c.id = p.company_id
            WHERE p.x = @x AND p.y = @y
            """, cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        command.Parameters.AddWithValue("viewer", viewer);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new PlotCompany(reader.GetString(0), reader.GetBoolean(1)) : null;
    }

    public async Task<IReadOnlyList<(int X, int Y)>> SharedWithAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT p.x, p.y FROM companies.plots p JOIN companies.members m ON m.company_id = p.company_id
            WHERE m.account_id = @player AND p.owner_id <> @player
            ORDER BY p.owner_id, p.x, p.y
            """, cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var shared = new List<(int, int)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            shared.Add((reader.GetInt32(0), reader.GetInt32(1)));
        }

        return shared;
    }
}
