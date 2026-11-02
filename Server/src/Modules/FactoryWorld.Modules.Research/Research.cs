using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Research;

public enum ResearchStatus { Locked, Available, Active, Done }

/// <summary>The player's research now, after handing out XP for what finished.</summary>
/// <param name="Started">Every technology the player started, done ones included.</param>
/// <param name="Finished">Technologies that finished since the player last asked.</param>
/// <param name="Shared">Technologies the player has because their company researched them.</param>
/// <param name="Company">The company's research, when the player is in a company.</param>
public sealed record ResearchState(
    IReadOnlyDictionary<string, (DateTimeOffset StartsAt, DateTimeOffset FinishesAt)> Started,
    DateTimeOffset ServerTime,
    IReadOnlyList<string> Finished,
    XpGain? Xp,
    IReadOnlySet<string>? Shared = null,
    CompanyResearch? Company = null)
{
    public bool IsDone(string id) => Started.TryGetValue(id, out var r) && r.FinishesAt <= ServerTime;

    public ResearchStatus Status(Technology technology) =>
        IsDone(technology.Id) ? ResearchStatus.Done
        : Started.ContainsKey(technology.Id) ? ResearchStatus.Active
        : technology.Requires.All(IsDone) ? ResearchStatus.Available
        : ResearchStatus.Locked;
}

/// <summary>What a company researches together: one technology at a time, which starts once members brought its whole cost.</summary>
/// <param name="Technology">The one being collected for or researched; null when none.</param>
/// <param name="StartsAt">Null while members are still bringing its cost.</param>
/// <param name="Delivered">How much of each cost item members brought so far.</param>
/// <param name="Done">What the company researched, for every member.</param>
public sealed record CompanyResearch(
    string Name, bool Leads, string? Technology, DateTimeOffset? StartsAt, DateTimeOffset? FinishesAt,
    IReadOnlyDictionary<string, long> Delivered, IReadOnlySet<string> Done);

public sealed class ResearchService(GameDb db, IInventory inventory, IWallet wallet, ICharacterProgress progress, ICompanies companies, TimeProvider time)
    : ITechnologies, ICompanyTechnologies
{
    /// <summary>How long the research takes the player: the orbital station of their company shortens it.</summary>
    private async Task<TimeSpan> DurationAsync(Guid player, Technology technology, CancellationToken cancellationToken) =>
        technology.Time * CompanyPerks.ResearchFactor(await companies.ProjectsAsync(player, cancellationToken));

    public Task<ResearchState> StateAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Starts researching a technology whose requirements are done: its cost is used up now, the unlock comes later.</summary>
    public Task<ResearchState> StartAsync(Guid owner, string id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var technology = TechTree.ById.GetValueOrDefault(id) ?? throw new GameException("no_technology", "There is no such technology.");
            if (technology.CompanyOnly)
            {
                throw new GameException("company_only", "Only a company researches this, together.");
            }

            // One start at a time per player, so two at once cannot both pass the check below.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()) ^ 0x7265736561726368);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            var state = await ReadAsync(owner, finished, xp, cancellationToken);
            switch (state.Status(technology))
            {
                case ResearchStatus.Done:
                    throw new GameException("already_researched", "This technology is already researched.");
                case ResearchStatus.Active:
                case ResearchStatus.Available when state.Started.Keys.Any(t => !state.IsDone(t)):
                    throw new GameException("research_busy", "Another technology is being researched; wait for it or cancel it.");
                case ResearchStatus.Locked:
                    throw new GameException("research_locked", "Research the technologies it needs first.");
            }

            foreach (var (item, quantity) in technology.Cost)
            {
                await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            }

            if (technology.CostCents > 0)
            {
                await wallet.DebitAsync(owner, technology.CostCents, $"research {technology.Id}", cancellationToken);
            }

            var now = time.GetUtcNow();
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded)
                VALUES (@owner, @technology, @start, @end, false)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("technology", technology.Id);
                insert.Parameters.AddWithValue("start", now);
                insert.Parameters.AddWithValue("end", now + await DurationAsync(owner, technology, cancellationToken));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Speeds the research being done now up: data from a polar station and a server room's computing power each take seconds off it (<see cref="Items.ResearchBoost"/>).</summary>
    public Task<ResearchState> BoostAsync(Guid owner, string item, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Items.ResearchBoost.TryGetValue(item, out var seconds))
            {
                throw new GameException("not_boost", "Only research data and computing power speed research up.");
            }

            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            var now = time.GetUtcNow();
            DateTimeOffset finishes;
            await using (var select = await db.CommandAsync(
                "SELECT finishes_at FROM research.technologies WHERE owner_id = @owner AND finishes_at > @now FOR UPDATE", cancellationToken))
            {
                select.Parameters.AddWithValue("owner", owner);
                select.Parameters.AddWithValue("now", now);
                if (await select.ExecuteScalarAsync(cancellationToken) is not DateTime at)
                {
                    throw new GameException("no_research", "Nothing is being researched.");
                }

                finishes = new DateTimeOffset(at, TimeSpan.Zero);
            }

            // No more is used up than the research still needs.
            var left = finishes - now;
            var used = (int)Math.Min(quantity, Math.Ceiling(left.TotalSeconds / seconds));
            var cut = TimeSpan.FromSeconds((double)used * seconds);
            if (cut > left)
            {
                cut = left;
            }

            await inventory.RemoveAsync(owner, item, used, cancellationToken);
            await using (var update = await db.CommandAsync(
                """
                UPDATE research.technologies SET starts_at = starts_at - @cut, finishes_at = finishes_at - @cut
                WHERE owner_id = @owner AND finishes_at > @now
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("owner", owner);
                update.Parameters.AddWithValue("now", now);
                update.Parameters.AddWithValue("cut", cut);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            var (done, more) = await CompleteDueAsync(owner, cancellationToken);
            return await ReadAsync(owner, [.. finished, .. done], more ?? xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Calls off the research that is not done yet; its cost comes back.</summary>
    public Task<ResearchState> CancelAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            await using (var delete = await db.CommandAsync(
                "DELETE FROM research.technologies WHERE owner_id = @owner AND finishes_at > @now RETURNING technology", cancellationToken))
            {
                delete.Parameters.AddWithValue("owner", owner);
                delete.Parameters.AddWithValue("now", time.GetUtcNow());
                if (await delete.ExecuteScalarAsync(cancellationToken) is not string id)
                {
                    throw new GameException("no_research", "Nothing is being researched.");
                }

                if (TechTree.ById.TryGetValue(id, out var technology))
                {
                    foreach (var (item, quantity) in technology.Cost)
                    {
                        await inventory.AddAsync(owner, item, quantity, cancellationToken);
                    }

                    if (technology.CostCents > 0)
                    {
                        await wallet.CreditAsync(owner, technology.CostCents, $"research {technology.Id} cancelled", cancellationToken);
                    }
                }
            }

            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> LockedAsync(Guid player, CancellationToken cancellationToken)
    {
        var done = await ResearchedAsync(player, cancellationToken);
        return TechTree.ByItem.Where(p => !done.Contains(p.Value.Id)).ToDictionary(p => p.Key, p => p.Value.Name);
    }

    public async Task<IReadOnlySet<string>> ResearchedAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT technology FROM research.technologies WHERE owner_id = @owner AND finishes_at <= @now", cancellationToken);
        command.Parameters.AddWithValue("owner", player);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var done = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                done.Add(reader.GetString(0));
            }
        }

        if (await companies.MembershipAsync(player, cancellationToken) is { } membership)
        {
            done.UnionWith(await CompanyDoneAsync(membership.Company, cancellationToken));
        }

        return done;
    }

    public async Task RequireUnlockedAsync(Guid player, string item, CancellationToken cancellationToken)
    {
        if (TechTree.ByItem.ContainsKey(item) && (await LockedAsync(player, cancellationToken)).TryGetValue(item, out var technology))
        {
            throw new GameException("locked", $"Research {technology} first.");
        }
    }

    private async Task<(IReadOnlyList<string> Finished, XpGain? Xp)> CompleteDueAsync(Guid owner, CancellationToken cancellationToken)
    {
        // UPDATE ... RETURNING hands each finished technology's XP to exactly one request.
        await using var command = await db.CommandAsync(
            """
            UPDATE research.technologies SET rewarded = true
            WHERE owner_id = @owner AND NOT rewarded AND finishes_at <= @now
            RETURNING technology
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var finished = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                finished.Add(reader.GetString(0));
            }
        }

        var xp = finished.Sum(id => TechTree.ById.TryGetValue(id, out var t) ? t.Xp : 0);
        return xp > 0 ? (finished, await progress.AddXpAsync(owner, xp, cancellationToken)) : (finished, null);
    }

    private async Task<ResearchState> ReadAsync(Guid owner, IReadOnlyList<string> finished, XpGain? xp, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT technology, starts_at, finishes_at FROM research.technologies WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        var started = new Dictionary<string, (DateTimeOffset, DateTimeOffset)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                started[reader.GetString(0)] = (reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        var now = time.GetUtcNow();
        if (await companies.MembershipAsync(owner, cancellationToken) is not { } membership)
        {
            return new ResearchState(started, now, finished, xp);
        }

        var company = await CompanyStateAsync(membership, cancellationToken);
        var shared = new HashSet<string>();
        foreach (var id in company.Done)
        {
            if (!started.TryGetValue(id, out var own) || own.Item2 > now)
            {
                shared.Add(id);
                started[id] = (now, now);
            }
        }

        return new ResearchState(started, now, finished, xp, shared, company);
    }

    public async Task<bool> ResearchedAsync(Guid company, string technology, CancellationToken cancellationToken) =>
        (await CompanyDoneAsync(company, cancellationToken)).Contains(technology);

    /// <summary>The owner or a partner picks what the company researches next; members then bring its cost (<see cref="ContributeAsync"/>).</summary>
    public Task<ResearchState> StartCompanyAsync(Guid owner, string id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var technology = TechTree.ById.GetValueOrDefault(id) ?? throw new GameException("no_technology", "There is no such technology.");
            await RequireCompanyAsync(owner, true, cancellationToken);
            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            var state = await ReadAsync(owner, finished, xp, cancellationToken);
            var company = state.Company!;
            if (company.Done.Contains(id))
            {
                throw new GameException("already_researched", "The company has researched this already.");
            }

            if (company.Technology is not null)
            {
                throw new GameException("research_busy", "The company is researching something else; finish or cancel it first.");
            }

            // What it needs counts when the company or this player has it.
            if (!technology.Requires.All(state.IsDone))
            {
                throw new GameException("research_locked", "Research the technologies it needs first.");
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO research.company_technologies (company_id, technology) VALUES (@company, @technology)", cancellationToken))
            {
                insert.Parameters.AddWithValue("company", (await companies.MembershipAsync(owner, cancellationToken))!.Company);
                insert.Parameters.AddWithValue("technology", id);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>A member brings part of the cost of what the company researches; with the whole cost in, the research starts.</summary>
    public Task<ResearchState> ContributeAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Bring at least one.");
            }

            var membership = await RequireCompanyAsync(owner, false, cancellationToken);
            var company = await CompanyStateAsync(membership, cancellationToken);
            if (company.Technology is not { } id || company.StartsAt is not null)
            {
                throw new GameException("no_research", "The company is not collecting for any research.");
            }

            var technology = TechTree.ById[id];
            var needed = Needed(technology, item) - company.Delivered.GetValueOrDefault(item);
            if (needed <= 0)
            {
                throw new GameException("not_needed", "The research needs no more of this.");
            }

            var used = Math.Min(quantity, needed);
            if (item == TechTree.Money)
            {
                await wallet.DebitAsync(owner, used, $"company research {id}", cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(owner, item, used, cancellationToken);
            }

            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO research.company_contributions (company_id, technology, item, delivered) VALUES (@company, @technology, @item, @used)
                ON CONFLICT (company_id, technology, item) DO UPDATE SET delivered = company_contributions.delivered + excluded.delivered;
                INSERT INTO research.company_contributors (company_id, technology, account_id, item, delivered) VALUES (@company, @technology, @owner, @item, @used)
                ON CONFLICT (company_id, technology, account_id, item) DO UPDATE SET delivered = company_contributors.delivered + excluded.delivered;
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("owner", owner);
                upsert.Parameters.AddWithValue("company", membership.Company);
                upsert.Parameters.AddWithValue("technology", id);
                upsert.Parameters.AddWithValue("item", item);
                upsert.Parameters.AddWithValue("used", used);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            var delivered = new Dictionary<string, long>(company.Delivered) { [item] = company.Delivered.GetValueOrDefault(item) + used };
            if (technology.Cost.All(c => delivered.GetValueOrDefault(c.Item) >= c.Quantity) && delivered.GetValueOrDefault(TechTree.Money) >= technology.CostCents)
            {
                var now = time.GetUtcNow();
                await using var start = await db.CommandAsync(
                    "UPDATE research.company_technologies SET starts_at = @start, finishes_at = @end WHERE company_id = @company AND technology = @technology",
                    cancellationToken);
                start.Parameters.AddWithValue("company", membership.Company);
                start.Parameters.AddWithValue("technology", id);
                start.Parameters.AddWithValue("start", now);
                start.Parameters.AddWithValue("end", now + await DurationAsync(owner, technology, cancellationToken));
                await start.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, [], null, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or a partner calls off the company's research that is not done; what each member brought goes back to them
    /// (what was brought before members were counted goes to the one who calls it off).</summary>
    public Task<ResearchState> CancelCompanyAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var membership = await RequireCompanyAsync(owner, true, cancellationToken);
            var company = await CompanyStateAsync(membership, cancellationToken);
            if (company.Technology is not { } id)
            {
                throw new GameException("no_research", "The company is not researching anything.");
            }

            var back = new List<(Guid Account, string Item, long Quantity)>();
            await using (var select = await db.CommandAsync(
                "SELECT account_id, item, delivered FROM research.company_contributors WHERE company_id = @company AND technology = @technology AND delivered > 0",
                cancellationToken))
            {
                select.Parameters.AddWithValue("company", membership.Company);
                select.Parameters.AddWithValue("technology", id);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    back.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            foreach (var (item, quantity) in company.Delivered.Where(d => d.Value > 0))
            {
                var unclaimed = quantity - back.Where(b => b.Item == item).Sum(b => b.Quantity);
                if (unclaimed > 0)
                {
                    back.Add((owner, item, unclaimed));
                }
            }

            foreach (var (account, item, quantity) in back)
            {
                if (item == TechTree.Money)
                {
                    await wallet.CreditAsync(account, quantity, $"company research {id} cancelled", cancellationToken);
                }
                else
                {
                    await inventory.AddAsync(account, item, quantity, cancellationToken);
                }
            }

            await using (var delete = await db.CommandAsync(
                """
                DELETE FROM research.company_contributors WHERE company_id = @company AND technology = @technology;
                DELETE FROM research.company_contributions WHERE company_id = @company AND technology = @technology;
                DELETE FROM research.company_technologies WHERE company_id = @company AND technology = @technology;
                """, cancellationToken))
            {
                delete.Parameters.AddWithValue("company", membership.Company);
                delete.Parameters.AddWithValue("technology", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, [], null, cancellationToken);
        }, cancellationToken);

    /// <summary>How much of an item, or of <see cref="TechTree.Money"/> in cents, the research uses up.</summary>
    private static long Needed(Technology technology, string item) =>
        item == TechTree.Money ? technology.CostCents : technology.Cost.Where(c => c.Item == item).Sum(c => (long)c.Quantity);

    /// <summary>The player's company, locked so its research changes one request at a time.</summary>
    private async Task<CompanyMembership> RequireCompanyAsync(Guid player, bool leads, CancellationToken cancellationToken)
    {
        var membership = await companies.MembershipAsync(player, cancellationToken)
            ?? throw new GameException("no_company", "You are not in a company.");
        if (leads && !membership.Leads)
        {
            throw new GameException("not_allowed", "Only the owner and partners choose the company's research.");
        }

        await using var lockCompany = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken);
        lockCompany.Parameters.AddWithValue("key", BitConverter.ToInt64(membership.Company.ToByteArray()) ^ 0x636f6d70616e79);
        await lockCompany.ExecuteNonQueryAsync(cancellationToken);
        return membership;
    }

    private async Task<HashSet<string>> CompanyDoneAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT technology FROM research.company_technologies WHERE company_id = @company AND finishes_at <= @now", cancellationToken);
        command.Parameters.AddWithValue("company", company);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var done = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            done.Add(reader.GetString(0));
        }

        return done;
    }

    private async Task<CompanyResearch> CompanyStateAsync(CompanyMembership membership, CancellationToken cancellationToken)
    {
        string? current = null;
        DateTimeOffset? startsAt = null, finishesAt = null;
        await using (var command = await db.CommandAsync(
            "SELECT technology, starts_at, finishes_at FROM research.company_technologies WHERE company_id = @company AND (finishes_at IS NULL OR finishes_at > @now)",
            cancellationToken))
        {
            command.Parameters.AddWithValue("company", membership.Company);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                current = reader.GetString(0);
                startsAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                finishesAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
            }
        }

        var delivered = new Dictionary<string, long>();
        if (current is not null)
        {
            await using var command = await db.CommandAsync(
                "SELECT item, delivered FROM research.company_contributions WHERE company_id = @company AND technology = @technology", cancellationToken);
            command.Parameters.AddWithValue("company", membership.Company);
            command.Parameters.AddWithValue("technology", current);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                delivered[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        return new CompanyResearch(membership.Name, membership.Leads, current, startsAt, finishesAt, delivered,
            await CompanyDoneAsync(membership.Company, cancellationToken));
    }
}
