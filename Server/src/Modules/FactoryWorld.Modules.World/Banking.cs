using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryWorld.Modules.World;

/// <summary>
/// Banks. A company that researched banking puts up a bank in a town and branches of it in other towns, each on a plot of the
/// company; any player opens an account there. Money never jumps: what a player pays in or takes out travels as an export train
/// would (<see cref="ExportRouter"/>, through any rail ports, for no fee) between the nearest of their plots, their own and their
/// company's, and the nearest building of the bank. Between two accounts of one bank it takes <see cref="SameBank"/>, to another
/// bank <see cref="OtherBank"/>. Money on its way is out of the sender's hands and cannot be spent twice or called back. A town
/// has room for as many bank buildings as its level allows, one of each bank.
/// </summary>
public static class BankRules
{
    /// <summary>A transfer between two accounts of one bank, whichever of its buildings they are nearest to.</summary>
    public static readonly TimeSpan SameBank = TimeSpan.FromMinutes(1);

    /// <summary>A transfer to an account in another bank.</summary>
    public static readonly TimeSpan OtherBank = TimeSpan.FromHours(6);

    /// <summary>Paying in or taking out for a player with no plot.</summary>
    public static readonly TimeSpan NoPlot = TimeSpan.FromMinutes(30);

    /// <summary>Paying in or taking out when no way leads from the player's plots to the bank (no ports, no building left): by post.</summary>
    public static readonly TimeSpan NoRoute = OtherBank;

    /// <summary>The most one payment or transfer may move.</summary>
    public const long MaxCents = 1_000_000_000_000_00;

    /// <summary>Bank buildings a town has room for, of all banks together: one in a village, five in a metropolis.</summary>
    public static int MaxBuildings(int townLevel) => townLevel + 1;

    /// <summary>The way to the bank as long as the export train's, shorter with the company's intercontinental railway.</summary>
    public static TimeSpan Trip(ExportRoute route, bool railway) =>
        railway ? TimeSpan.FromSeconds(Math.Max(ExportRules.MinTrip.TotalSeconds, route.Trip.TotalSeconds * CompanyPerks.RailwayFactor)) : route.Trip;
}

/// <summary>A bank: the company that keeps it, its name, and its buildings.</summary>
public sealed record BankInfo(Guid Id, string Name, IReadOnlyList<BankBuilding> Buildings, long Customers);

/// <summary>A bank's first building or a branch, on a city plot.</summary>
public sealed record BankBuilding(Guid Id, string Kind, int X, int Y);

/// <summary>Money on its way: paid in (<c>deposit</c>), taken out (<c>withdraw</c>) or sent to an account (<c>transfer</c>).
/// <paramref name="FromBank"/> is null for money from a wallet, <paramref name="ToBank"/> for money to one.</summary>
public sealed record BankTransfer(
    long Id, string Kind, Guid Player, Guid To, Guid? FromBank, Guid? ToBank, long Cents, DateTimeOffset SentAt, DateTimeOffset ArrivesAt);

/// <summary>How long paying in or taking out takes the player at one bank, and from which of their plots.</summary>
public sealed record BankTrip(TimeSpan Time, (int X, int Y)? From, BankBuilding? To, ExportRoute? Route);

public sealed record BankState(
    IReadOnlyList<BankInfo> Banks, IReadOnlyDictionary<Guid, long> Accounts, IReadOnlyDictionary<Guid, BankTrip> Trips,
    IReadOnlyList<BankTransfer> Transfers, IReadOnlyDictionary<Guid, string> Names, Guid? Company, bool CompanyBanks, DateTimeOffset Now);

public sealed class BankService(
    GameDb db, LandMasses land, Towns towns, CityService cities, IWallet wallet, ICharacterNames names,
    IEnumerable<ICompanies> companies, IEnumerable<ICompanyPlots> companyPlots, IEnumerable<ICompanyTechnologies> technologies,
    TimeProvider time) : IPlacedHook, IMachineContents
{
    // --- Accounts -----------------------------------------------------------------------------------

    /// <summary>Every bank, the player's accounts and money on its way to or from them, after handing over what has arrived.</summary>
    public async Task<BankState> StateAsync(Guid player, CancellationToken cancellationToken)
    {
        await ArriveAsync(cancellationToken);
        return await ReadAsync(player, cancellationToken);
    }

    public Task<BankState> OpenAsync(Guid player, Guid bank, CancellationToken cancellationToken) =>
        ChangeAsync(player, async () =>
        {
            var banks = await BanksAsync(cancellationToken);
            if (!banks.Any(b => b.Id == bank && b.Buildings.Count > 0))
            {
                throw new GameException("no_bank", "There is no such bank.");
            }

            await using var insert = await db.CommandAsync(
                """
                INSERT INTO world.bank_accounts (company_id, player_id, balance_cents, opened_at) VALUES (@bank, @player, 0, @now)
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            insert.Parameters.AddWithValue("bank", bank);
            insert.Parameters.AddWithValue("player", player);
            insert.Parameters.AddWithValue("now", time.GetUtcNow());
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("has_account", "You have an account in this bank already.");
            }
        }, cancellationToken);

    /// <summary>Closes an empty account that no money is on its way to.</summary>
    public Task<BankState> CloseAsync(Guid player, Guid bank, CancellationToken cancellationToken) =>
        ChangeAsync(player, async () =>
        {
            var balance = await LockAccountAsync(player, bank, cancellationToken);
            if (balance > 0)
            {
                throw new GameException("not_empty", "Take the money out first.");
            }

            await using (var coming = await db.CommandAsync(
                "SELECT EXISTS (SELECT 1 FROM world.bank_transfers WHERE to_company_id = @bank AND to_player_id = @player)", cancellationToken))
            {
                coming.Parameters.AddWithValue("bank", bank);
                coming.Parameters.AddWithValue("player", player);
                if ((bool)(await coming.ExecuteScalarAsync(cancellationToken))!)
                {
                    throw new GameException("money_on_way", "Money is on its way to this account.");
                }
            }

            await using var delete = await db.CommandAsync(
                "DELETE FROM world.bank_accounts WHERE company_id = @bank AND player_id = @player", cancellationToken);
            delete.Parameters.AddWithValue("bank", bank);
            delete.Parameters.AddWithValue("player", player);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Pays money from the wallet into the account; it gets there after the trip from the player's nearest plot.</summary>
    public Task<BankState> DepositAsync(Guid player, Guid bank, long cents, CancellationToken cancellationToken) =>
        ChangeAsync(player, async () =>
        {
            CheckCents(cents);
            await LockAccountAsync(player, bank, cancellationToken);
            var (info, trip) = await TripAsync(player, bank, cancellationToken);
            await wallet.DebitAsync(player, cents, $"bank deposit {info.Name}", cancellationToken);
            await SendAsync("deposit", player, player, null, bank, cents, trip.Time, cancellationToken);
        }, cancellationToken);

    /// <summary>Takes money out of the account into the wallet; it gets there after the trip to the player's nearest plot.</summary>
    public Task<BankState> WithdrawAsync(Guid player, Guid bank, long cents, CancellationToken cancellationToken) =>
        ChangeAsync(player, async () =>
        {
            CheckCents(cents);
            await TakeAsync(player, bank, cents, cancellationToken);
            var (_, trip) = await TripAsync(player, bank, cancellationToken);
            await SendAsync("withdraw", player, player, bank, null, cents, trip.Time, cancellationToken);
        }, cancellationToken);

    /// <summary>Sends money from the player's account to someone's account in the same bank or another one.</summary>
    public Task<BankState> TransferAsync(Guid player, Guid bank, string to, Guid toBank, long cents, CancellationToken cancellationToken) =>
        ChangeAsync(player, async () =>
        {
            CheckCents(cents);
            var recipient = await names.AccountAsync(to ?? "", cancellationToken)
                ?? throw new GameException("no_player", "There is no player of that name.");
            if (recipient == player && toBank == bank)
            {
                throw new GameException("same_account", "That is the same account.");
            }

            await using (var account = await db.CommandAsync(
                "SELECT EXISTS (SELECT 1 FROM world.bank_accounts WHERE company_id = @bank AND player_id = @player)", cancellationToken))
            {
                account.Parameters.AddWithValue("bank", toBank);
                account.Parameters.AddWithValue("player", recipient);
                if (!(bool)(await account.ExecuteScalarAsync(cancellationToken))!)
                {
                    throw new GameException("no_account_there", "The player has no account in that bank.");
                }
            }

            await TakeAsync(player, bank, cents, cancellationToken);
            await SendAsync("transfer", player, recipient, bank, toBank, cents, toBank == bank ? BankRules.SameBank : BankRules.OtherBank, cancellationToken);
        }, cancellationToken);

    private async Task<BankState> ChangeAsync(Guid player, Func<Task> change, CancellationToken cancellationToken)
    {
        await ArriveAsync(cancellationToken);
        await db.InTransactionAsync(change, cancellationToken);
        return await ReadAsync(player, cancellationToken);
    }

    private static void CheckCents(long cents)
    {
        if (cents is < 1 or > BankRules.MaxCents)
        {
            throw new GameException("bad_amount", $"The amount must be 0.01-{BankRules.MaxCents / 100} $.");
        }
    }

    /// <summary>The account's balance, locked until the transaction ends.</summary>
    private async Task<long> LockAccountAsync(Guid player, Guid bank, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT balance_cents FROM world.bank_accounts WHERE company_id = @bank AND player_id = @player FOR UPDATE", cancellationToken);
        command.Parameters.AddWithValue("bank", bank);
        command.Parameters.AddWithValue("player", player);
        return await command.ExecuteScalarAsync(cancellationToken) as long?
            ?? throw new GameException("no_account", "You have no account in this bank.");
    }

    private async Task TakeAsync(Guid player, Guid bank, long cents, CancellationToken cancellationToken)
    {
        if (await LockAccountAsync(player, bank, cancellationToken) < cents)
        {
            throw new GameException("not_enough_money", "There is not that much on the account.");
        }

        await using var update = await db.CommandAsync(
            "UPDATE world.bank_accounts SET balance_cents = balance_cents - @cents WHERE company_id = @bank AND player_id = @player", cancellationToken);
        update.Parameters.AddWithValue("bank", bank);
        update.Parameters.AddWithValue("player", player);
        update.Parameters.AddWithValue("cents", cents);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SendAsync(
        string kind, Guid player, Guid to, Guid? fromBank, Guid? toBank, long cents, TimeSpan trip, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var insert = await db.CommandAsync(
            """
            INSERT INTO world.bank_transfers (kind, player_id, to_player_id, from_company_id, to_company_id, cents, sent_at, arrives_at)
            VALUES (@kind, @player, @to, @from, @toBank, @cents, @now, @at)
            """, cancellationToken);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.AddWithValue("player", player);
        insert.Parameters.AddWithValue("to", to);
        insert.Parameters.AddWithValue("from", (object?)fromBank ?? DBNull.Value);
        insert.Parameters.AddWithValue("toBank", (object?)toBank ?? DBNull.Value);
        insert.Parameters.AddWithValue("cents", cents);
        insert.Parameters.AddWithValue("now", now);
        insert.Parameters.AddWithValue("at", now + trip);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Hands over the money of every transfer that has got there; returns how many there were. The background
    /// <see cref="BankDispatcher"/> calls this every moment, so money arrives whether or not anyone looks.</summary>
    public Task<int> ArriveAsync(CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            // DELETE ... RETURNING hands each transfer to exactly one request.
            await using var command = await db.CommandAsync(
                "DELETE FROM world.bank_transfers WHERE arrives_at <= @now RETURNING kind, to_player_id, to_company_id, cents", cancellationToken);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            var arrived = new List<(string Kind, Guid To, Guid? Bank, long Cents)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    arrived.Add((reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetInt64(3)));
                }
            }

            foreach (var (kind, to, bank, cents) in arrived)
            {
                if (bank is { } b)
                {
                    // The account may have gone since only when it was closed with nothing on its way, so this opens it again.
                    await using var credit = await db.CommandAsync(
                        """
                        INSERT INTO world.bank_accounts (company_id, player_id, balance_cents, opened_at) VALUES (@bank, @player, @cents, @now)
                        ON CONFLICT (company_id, player_id) DO UPDATE SET balance_cents = bank_accounts.balance_cents + excluded.balance_cents
                        """, cancellationToken);
                    credit.Parameters.AddWithValue("bank", b);
                    credit.Parameters.AddWithValue("player", to);
                    credit.Parameters.AddWithValue("cents", cents);
                    credit.Parameters.AddWithValue("now", time.GetUtcNow());
                    await credit.ExecuteNonQueryAsync(cancellationToken);
                }
                else
                {
                    await wallet.CreditAsync(to, cents, $"bank {kind}", cancellationToken);
                }
            }

            return arrived.Count;
        }, cancellationToken);

    // --- Reading ------------------------------------------------------------------------------------

    private async Task<BankState> ReadAsync(Guid player, CancellationToken cancellationToken)
    {
        var banks = await BanksAsync(cancellationToken);
        var accounts = new Dictionary<Guid, long>();
        await using (var command = await db.CommandAsync(
            "SELECT company_id, balance_cents FROM world.bank_accounts WHERE player_id = @player", cancellationToken))
        {
            command.Parameters.AddWithValue("player", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts[reader.GetGuid(0)] = reader.GetInt64(1);
            }
        }

        var transfers = new List<BankTransfer>();
        await using (var command = await db.CommandAsync(
            """
            SELECT id, kind, player_id, to_player_id, from_company_id, to_company_id, cents, sent_at, arrives_at FROM world.bank_transfers
            WHERE player_id = @player OR to_player_id = @player ORDER BY arrives_at, id LIMIT 50
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("player", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                transfers.Add(new BankTransfer(reader.GetInt64(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetInt64(6),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
            }
        }

        var plots = await PlotsAsync(player, cancellationToken);
        var ports = await PortsAsync(cancellationToken);
        var railway = await RailwayAsync(player, cancellationToken);
        var trips = banks.ToDictionary(b => b.Id, b => Trip(b, plots, ports, railway));
        var membership = await MembershipAsync(player, cancellationToken);
        var canBank = membership is { } m && await ResearchedAsync(m.Company, cancellationToken);
        var nameOf = await names.NamesAsync(transfers.SelectMany(t => new[] { t.Player, t.To }).Distinct().ToList(), cancellationToken);
        return new BankState(banks, accounts, trips, transfers, nameOf, membership?.Company, canBank, time.GetUtcNow());
    }

    /// <summary>Every bank with a building or an account, its buildings and how many players have an account in it.</summary>
    private async Task<IReadOnlyList<BankInfo>> BanksAsync(CancellationToken cancellationToken)
    {
        var buildings = new Dictionary<Guid, List<BankBuilding>>();
        await using (var command = await db.CommandAsync(
            "SELECT object_id, company_id, kind, x, y FROM world.bank_buildings ORDER BY kind, object_id", cancellationToken))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var company = reader.GetGuid(1);
                if (!buildings.TryGetValue(company, out var list))
                {
                    buildings[company] = list = [];
                }

                list.Add(new BankBuilding(reader.GetGuid(0), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        var banks = new List<BankInfo>();
        await using (var command = await db.CommandAsync(
            """
            SELECT b.company_id, b.name, (SELECT count(*) FROM world.bank_accounts a WHERE a.company_id = b.company_id) FROM world.banks b
            ORDER BY b.name, b.company_id
            """, cancellationToken))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                banks.Add(new BankInfo(id, reader.GetString(1), buildings.GetValueOrDefault(id) ?? [], reader.GetInt64(2)));
            }
        }

        return banks;
    }

    /// <summary>The bank and how long money takes between it and the player now.</summary>
    private async Task<(BankInfo Bank, BankTrip Trip)> TripAsync(Guid player, Guid bank, CancellationToken cancellationToken)
    {
        var info = (await BanksAsync(cancellationToken)).FirstOrDefault(b => b.Id == bank)
            ?? throw new GameException("no_bank", "There is no such bank.");
        return (info, Trip(info, await PlotsAsync(player, cancellationToken), await PortsAsync(cancellationToken), await RailwayAsync(player, cancellationToken)));
    }

    /// <summary>The quickest way between any of the plots and any building of the bank, through any rail ports.</summary>
    private BankTrip Trip(BankInfo bank, IReadOnlyList<(int X, int Y)> plots, IReadOnlyList<ExportPost> ports, bool railway)
    {
        if (plots.Count == 0)
        {
            return new BankTrip(BankRules.NoPlot, null, null, null);
        }

        var targets = bank.Buildings.Select(b => new ExportPost(b.Id, b.Kind, b.X, b.Y, bank.Id, 0)).ToList();
        BankTrip? best = null;
        if (targets.Count > 0)
        {
            foreach (var (x, y) in plots)
            {
                if (ExportRouter.Find(land, x, y, targets, ports) is { } route)
                {
                    var trip = BankRules.Trip(route, railway);
                    if (best is null || trip < best.Time)
                    {
                        best = new BankTrip(trip, (x, y), bank.Buildings.First(b => b.Id == route.Market.Id), route);
                    }
                }
            }
        }

        return best ?? new BankTrip(BankRules.NoRoute, null, null, null);
    }

    /// <summary>The player's plots and those other members brought into their company.</summary>
    private async Task<IReadOnlyList<(int X, int Y)>> PlotsAsync(Guid player, CancellationToken cancellationToken)
    {
        var plots = new List<(int X, int Y)>();
        await using (var command = await db.CommandAsync("SELECT x, y FROM world.plots WHERE owner_id = @owner ORDER BY bought_at", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                plots.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }

        foreach (var registry in companyPlots)
        {
            plots.AddRange(await registry.SharedWithAsync(player, cancellationToken));
        }

        return plots.Distinct().ToList();
    }

    private async Task<IReadOnlyList<ExportPost>> PortsAsync(CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT p.object_id, p.x, p.y, l.owner_id, p.value FROM world.export_posts p
            JOIN world.plots l ON l.x = p.x AND l.y = p.y WHERE p.kind = @kind AND l.owner_id IS NOT NULL ORDER BY p.object_id
            """, cancellationToken);
        command.Parameters.AddWithValue("kind", Items.RailPort);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ports = new List<ExportPost>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ports.Add(new ExportPost(reader.GetGuid(0), Items.RailPort, reader.GetInt32(1), reader.GetInt32(2), reader.GetGuid(3), reader.GetInt64(4)));
        }

        return ports;
    }

    private async Task<bool> RailwayAsync(Guid player, CancellationToken cancellationToken)
    {
        foreach (var c in companies)
        {
            if ((await c.ProjectsAsync(player, cancellationToken)).Contains(CompanyPerks.Railway))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<CompanyMembership?> MembershipAsync(Guid player, CancellationToken cancellationToken)
    {
        foreach (var c in companies)
        {
            if (await c.MembershipAsync(player, cancellationToken) is { } membership)
            {
                return membership;
            }
        }

        return null;
    }

    private async Task<bool> ResearchedAsync(Guid company, CancellationToken cancellationToken)
    {
        foreach (var t in technologies)
        {
            if (await t.ResearchedAsync(company, CompanyTechs.Banking, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    // --- Buildings ----------------------------------------------------------------------------------

    /// <summary>
    /// A bank or a branch put on a plot: it stands on a plot of the company of the owner or a partner who puts it up, once the
    /// company researched banking; a company has one bank, its branches need it standing, and it keeps one building a town. A
    /// town has room for <see cref="BankRules.MaxBuildings"/> of all banks. Runs in the transaction that places it, so a refusal
    /// takes it back.
    /// </summary>
    public async Task PlacedAsync(Guid owner, int x, int y, PlacedOnPlot placed, CancellationToken cancellationToken)
    {
        if (!Items.IsBank(placed.Kind))
        {
            return;
        }

        var membership = await MembershipAsync(owner, cancellationToken);
        if (membership is not { Leads: true })
        {
            throw new GameException("not_allowed", "The owner or a partner of a company puts up its bank.");
        }

        PlotCompany? plot = null;
        foreach (var c in companyPlots)
        {
            plot ??= await c.CompanyAsync(owner, x, y, cancellationToken);
        }

        if (plot is not { Member: true })
        {
            throw new GameException("bank_off_company", "A bank stands on a plot of your company.");
        }

        if (!await ResearchedAsync(membership.Company, cancellationToken))
        {
            throw new GameException("locked", "Your company has not researched banking yet.");
        }

        // One building at a time decides on a town's room.
        await using (var lockAll = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
        {
            lockAll.Parameters.AddWithValue("key", 0x62_616e_6b73_2121);
            await lockAll.ExecuteNonQueryAsync(cancellationToken);
        }

        var banks = await BanksAsync(cancellationToken);
        var own = banks.FirstOrDefault(b => b.Id == membership.Company)?.Buildings ?? [];
        if (placed.Kind == Items.Bank && own.Any(b => b.Kind == Items.Bank))
        {
            throw new GameException("has_bank", "Your company has a bank already; put up branches of it.");
        }

        if (placed.Kind == Items.BankBranch && !own.Any(b => b.Kind == Items.Bank))
        {
            throw new GameException("no_bank", "A branch needs your company's bank standing in another town.");
        }

        var town = towns.Of(x, y);
        if (own.Any(b => towns.Of(b.X, b.Y) == town))
        {
            throw new GameException("bank_in_town", $"Your bank has a building in {town.Name} already.");
        }

        var level = CityRules.Level(await cities.DeliveredAsync(town, cancellationToken));
        if (banks.SelectMany(b => b.Buildings).Count(b => towns.Of(b.X, b.Y) == town) >= BankRules.MaxBuildings(level))
        {
            throw new GameException("town_full",
                $"{town.Name} ({CityRules.LevelNames[level]}) has room for {BankRules.MaxBuildings(level)} bank buildings; a bigger town for more.");
        }

        await using (var bank = await db.CommandAsync(
            """
            INSERT INTO world.banks (company_id, name, created_at) VALUES (@company, @name, @now)
            ON CONFLICT (company_id) DO UPDATE SET name = excluded.name
            """, cancellationToken))
        {
            bank.Parameters.AddWithValue("company", membership.Company);
            bank.Parameters.AddWithValue("name", membership.Name);
            bank.Parameters.AddWithValue("now", time.GetUtcNow());
            await bank.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = await db.CommandAsync(
            "INSERT INTO world.bank_buildings (object_id, company_id, kind, x, y) VALUES (@id, @company, @kind, @x, @y)", cancellationToken);
        insert.Parameters.AddWithValue("id", placed.Id);
        insert.Parameters.AddWithValue("company", membership.Company);
        insert.Parameters.AddWithValue("kind", placed.Kind);
        insert.Parameters.AddWithValue("x", x);
        insert.Parameters.AddWithValue("y", y);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A building picked up closes nothing: the accounts stay, and money still gets in and out, by the next building or by post.</summary>
    public async Task ReleaseAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var delete = await db.CommandAsync("DELETE FROM world.bank_buildings WHERE object_id = @id", cancellationToken);
        delete.Parameters.AddWithValue("id", id);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The bank a building belongs to, for the window it opens.</summary>
    public async Task<Guid?> BankOfAsync(Guid building, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT company_id FROM world.bank_buildings WHERE object_id = @id", cancellationToken);
        command.Parameters.AddWithValue("id", building);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }
}

/// <summary>Hands over money that has got where it was going, whether or not anyone looks.</summary>
public sealed class BankDispatcher(IServiceScopeFactory scopes, ILogger<BankDispatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BankService>().ArriveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Handing over bank transfers failed");
            }
        }
    }
}

public sealed record BankRequest(Guid Bank = default, long Cents = 0, string? To = null, Guid ToBank = default);

public abstract class BankHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var player = client.RequireAccount();
        var r = payload is null ? new BankRequest() : Payload.Read<BankRequest>(payload);
        var s = await RunAsync(scope.ServiceProvider.GetRequiredService<BankService>(), player, r, cancellationToken);
        var towns = scope.ServiceProvider.GetRequiredService<Towns>();
        var bankName = s.Banks.ToDictionary(b => b.Id, b => b.Name);
        string? NameOf(Guid? bank) => bank is { } b ? bankName.GetValueOrDefault(b, "?") : null;
        return new Reply("bank", new
        {
            banks = s.Banks.Select(b =>
            {
                var trip = s.Trips[b.Id];
                return new
                {
                    id = b.Id,
                    name = b.Name,
                    mine = s.Company == b.Id,
                    customers = b.Customers,
                    buildings = b.Buildings.Select(o => new { id = o.Id, kind = o.Kind, x = o.X, y = o.Y, town = towns.Of(o.X, o.Y).Name }),
                    balanceCents = s.Accounts.TryGetValue(b.Id, out var balance) ? balance : (long?)null,
                    // How long paying in or taking out takes the player, and the way it goes.
                    trip = new
                    {
                        seconds = (long)trip.Time.TotalSeconds,
                        fromX = trip.From?.X,
                        fromY = trip.From?.Y,
                        town = trip.To is { } to ? towns.Of(to.X, to.Y).Name : null,
                        landPlots = trip.Route is { } route ? Math.Round(route.LandPlots, 1) : (double?)null,
                        seaPlots = trip.Route is { } sea ? Math.Round(sea.SeaPlots, 1) : (double?)null,
                    },
                };
            }),
            transfers = s.Transfers.Select(t => new
            {
                id = t.Id,
                kind = t.Kind,
                incoming = t.To == player && t.Player != player,
                cents = t.Cents,
                from = s.Names.GetValueOrDefault(t.Player, "?"),
                to = s.Names.GetValueOrDefault(t.To, "?"),
                fromBank = NameOf(t.FromBank),
                toBank = NameOf(t.ToBank),
                sentAt = t.SentAt.ToUnixTimeMilliseconds(),
                arrivesAt = t.ArrivesAt.ToUnixTimeMilliseconds(),
            }),
            // Whether the player's company may put up a bank or branches: it researched banking.
            companyBanks = s.CompanyBanks,
            rates = new
            {
                sameBankSeconds = (long)BankRules.SameBank.TotalSeconds,
                otherBankSeconds = (long)BankRules.OtherBank.TotalSeconds,
                noPlotSeconds = (long)BankRules.NoPlot.TotalSeconds,
                minutesPerPlot = ExportRules.MinutesPerPlot,
                seaFactor = ExportRules.SeaFactor,
            },
            serverTime = s.Now.ToUnixTimeMilliseconds(),
        });
    }

    protected abstract Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken);
}

public sealed class GetBankHandler(IServiceScopeFactory scopes) : BankHandler(scopes)
{
    public override string Type => "bank.get";

    protected override Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken) =>
        banks.StateAsync(player, cancellationToken);
}

public sealed class OpenBankAccountHandler(IServiceScopeFactory scopes) : BankHandler(scopes)
{
    public override string Type => "bank.open";

    protected override Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken) =>
        banks.OpenAsync(player, r.Bank, cancellationToken);
}

public sealed class CloseBankAccountHandler(IServiceScopeFactory scopes) : BankHandler(scopes)
{
    public override string Type => "bank.close";

    protected override Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken) =>
        banks.CloseAsync(player, r.Bank, cancellationToken);
}

public sealed class DepositHandler(IServiceScopeFactory scopes) : BankHandler(scopes)
{
    public override string Type => "bank.deposit";

    protected override Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken) =>
        banks.DepositAsync(player, r.Bank, r.Cents, cancellationToken);
}

public sealed class WithdrawHandler(IServiceScopeFactory scopes) : BankHandler(scopes)
{
    public override string Type => "bank.withdraw";

    protected override Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken) =>
        banks.WithdrawAsync(player, r.Bank, r.Cents, cancellationToken);
}

public sealed class BankTransferHandler(IServiceScopeFactory scopes) : BankHandler(scopes)
{
    public override string Type => "bank.transfer";

    protected override Task<BankState> RunAsync(BankService banks, Guid player, BankRequest r, CancellationToken cancellationToken) =>
        banks.TransferAsync(player, r.Bank, r.To ?? "", r.ToBank, r.Cents, cancellationToken);
}

/// <summary>The bank a building on a plot belongs to, so clicking it opens that bank's page.</summary>
public sealed class BankOfBuildingHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "bank.building";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        client.RequireAccount();
        var r = Payload.Read<PostRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var bank = await scope.ServiceProvider.GetRequiredService<BankService>().BankOfAsync(r.Id, cancellationToken)
            ?? throw new GameException("no_bank", "There is no such bank.");
        return new Reply("bank.building", new { id = r.Id, bank });
    }
}
