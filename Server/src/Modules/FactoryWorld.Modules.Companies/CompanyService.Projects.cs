using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Npgsql;

namespace FactoryWorld.Modules.Companies;

/// <summary>A company's projects, orders, tasks and deals between companies.</summary>
public sealed partial class CompanyService
{
    // ---- Big projects ----

    /// <summary>The owner or a partner takes on a big project; the company must have researched big projects.</summary>
    public Task<CompanyView> StartProjectAsync(Guid player, string? id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var project = BigProjects.ById.GetValueOrDefault(id ?? "") ?? throw new GameException("no_project", "There is no such project.");
            var company = await RequireLeadAsync(player, cancellationToken);
            if (!await companyTechnologies.ResearchedAsync(company, CompanyTechs.Megaprojects, cancellationToken))
            {
                throw new GameException("research_needed", "The company researches big projects first.");
            }

            var states = await ProjectsAsync(company, cancellationToken);
            if (states.First(p => p.Project.Id == project.Id).TakenBy is not null)
            {
                throw new GameException("wonder_taken", "Another company built this wonder of the world first.");
            }

            var now = time.GetUtcNow();
            if (project.Requires?.Any(r => states.FirstOrDefault(p => p.Project.Id == r)?.BuiltAt is not { } built || built > now) == true)
            {
                throw new GameException("project_locked", "The company builds the projects this one needs first.");
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO companies.projects (company_id, project, started_at, rewarded) VALUES (@company, @project, @at, false) ON CONFLICT DO NOTHING",
                cancellationToken))
            {
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("project", project.Id);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("project_started", "The company took this project on already.");
                }
            }

            await LogAsync(company, player, "projectStarted", cancellationToken, item: project.Id);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>A member brings parts for a big project, from their inventory or (when they may take) from the warehouse; with all parts in, building starts.</summary>
    public Task<CompanyView> GiveToProjectAsync(
        Guid player, string? id, string? item, long quantity, bool fromWarehouse, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            var project = BigProjects.ById.GetValueOrDefault(id ?? "") ?? throw new GameException("no_project", "There is no such project.");
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            var state = (await ProjectsAsync(company, cancellationToken)).First(p => p.Project.Id == project.Id);
            if (state.TakenBy is not null)
            {
                throw new GameException("wonder_taken", "Another company built this wonder of the world first.");
            }

            if (!state.Started || state.BuiltAt is not null)
            {
                throw new GameException("project_closed", "The company is not collecting parts for this project.");
            }

            var needed = project.Parts.Where(p => p.Item == item).Sum(p => p.Quantity) - state.Delivered.GetValueOrDefault(item!);
            if (needed <= 0)
            {
                throw new GameException("not_needed", "The project needs no more of this.");
            }

            var used = Math.Min(quantity, needed);
            if (fromWarehouse)
            {
                if (!Roles.MayTake(role))
                {
                    throw new GameException("not_allowed", "Workers do not take from the warehouse.");
                }

                await ChangeStockAsync(company, item!, -used, cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(player, item!, used, cancellationToken);
            }

            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO companies.project_parts (company_id, project, item, delivered) VALUES (@company, @project, @item, @used)
                ON CONFLICT (company_id, project, item) DO UPDATE SET delivered = project_parts.delivered + excluded.delivered
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("company", company);
                upsert.Parameters.AddWithValue("project", project.Id);
                upsert.Parameters.AddWithValue("item", item!);
                upsert.Parameters.AddWithValue("used", used);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            var delivered = new Dictionary<string, long>(state.Delivered) { [item!] = state.Delivered.GetValueOrDefault(item!) + used };
            if (project.Parts.All(p => delivered.GetValueOrDefault(p.Item) >= p.Quantity))
            {
                if (project.Wonder)
                {
                    await ClaimWonderAsync(company, project, cancellationToken);
                }

                await using var build = await db.CommandAsync(
                    "UPDATE companies.projects SET built_at = @at WHERE company_id = @company AND project = @project", cancellationToken);
                build.Parameters.AddWithValue("company", company);
                build.Parameters.AddWithValue("project", project.Id);
                build.Parameters.AddWithValue("at", time.GetUtcNow() + project.Build);
                await build.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "projectPart", cancellationToken, item, used);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Orders ----

    /// <summary>Delivers from the warehouse towards one of this week's orders; the reward comes with the last piece.</summary>
    public Task<CompanyView> DeliverOrderAsync(Guid player, int slot, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, role) = await RequireMemberAsync(player, cancellationToken);
            if (!Roles.MayTake(role))
            {
                throw new GameException("not_allowed", "Workers do not take from the warehouse.");
            }

            var week = CompanyOrders.Week(time.GetUtcNow());
            var order = (await OrdersAsync(company, cancellationToken)).FirstOrDefault(o => o.Order.Slot == slot)
                ?? throw new GameException("no_order", "There is no such order this week.");
            var left = order.Order.Quantity - order.Delivered;
            if (left <= 0)
            {
                throw new GameException("order_done", "This order is complete.");
            }

            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Deliver at least one.");
            }

            var used = Math.Min(quantity, left);
            await ChangeStockAsync(company, order.Order.Item, -used, cancellationToken);
            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO companies.orders (company_id, week, slot, delivered) VALUES (@company, @week, @slot, @used)
                ON CONFLICT (company_id, week, slot) DO UPDATE SET delivered = orders.delivered + excluded.delivered
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("company", company);
                upsert.Parameters.AddWithValue("week", week);
                upsert.Parameters.AddWithValue("slot", slot);
                upsert.Parameters.AddWithValue("used", used);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            if (used == left)
            {
                await ChangeMoneyAsync(company, order.Order.RewardCents, order.Order.RewardCents, cancellationToken);
                await LogAsync(company, player, "order", cancellationToken, order.Order.Item, order.Order.RewardCents);
            }
            else
            {
                await LogAsync(company, player, "orderPart", cancellationToken, order.Order.Item, used);
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    // ---- Tasks and wages ----

    /// <summary>The owner or the treasurer sets a task: bring so many of an item into the warehouse, for a wage set aside from the cash now.</summary>
    public Task<CompanyView> CreateTaskAsync(
        Guid player, string? item, long quantity, long wageCents, string? assignee, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, quantity);
            if (wageCents is < 0 or > CompanyMoney.MaxCents)
            {
                throw new GameException("bad_amount", "That is not a wage.");
            }

            var company = await RequireMoneyAsync(player, cancellationToken);
            Guid? who = string.IsNullOrWhiteSpace(assignee) ? null : await MemberByNameAsync(company, assignee, cancellationToken);
            if (await CountAsync("SELECT count(*) FROM companies.tasks WHERE company_id = @company AND done_at IS NULL", company, cancellationToken)
                >= CompanyTasks.MaxOpen)
            {
                throw new GameException("too_many_tasks", $"A company has at most {CompanyTasks.MaxOpen} open tasks.");
            }

            await ChangeMoneyAsync(company, -wageCents, 0, cancellationToken);
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO companies.tasks (company_id, item, quantity, delivered, wage_cents, paid_cents, assignee_id, created_by, created_at)
                VALUES (@company, @item, @quantity, 0, @wage, 0, @assignee, @player, @at)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("item", item!);
                insert.Parameters.AddWithValue("quantity", quantity);
                insert.Parameters.AddWithValue("wage", wageCents);
                insert.Parameters.Add(new NpgsqlParameter("assignee", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)who ?? DBNull.Value });
                insert.Parameters.AddWithValue("player", player);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "task", cancellationToken, item, quantity, who);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>A member brings items for a task from their inventory into the warehouse and is paid the wage for them at once.</summary>
    public Task<CompanyView> DeliverTaskAsync(Guid player, long id, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (company, _) = await RequireMemberAsync(player, cancellationToken);
            var task = await TaskAsync(company, id, cancellationToken);
            if (task.Assignee is { } who && who != player)
            {
                throw new GameException("not_your_task", "This task is somebody else's.");
            }

            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Bring at least one.");
            }

            var used = Math.Min(quantity, task.Quantity - task.Delivered);
            await RequireRoomAsync(company, used, cancellationToken);
            await inventory.RemoveAsync(player, task.Item, used, cancellationToken);
            await ChangeStockAsync(company, task.Item, used, cancellationToken);
            var done = task.Delivered + used == task.Quantity;
            var pay = done ? task.WageCents - task.PaidCents : (long)(task.WageCents * (decimal)used / task.Quantity);
            await using (var update = await db.CommandAsync(
                """
                UPDATE companies.tasks SET delivered = delivered + @used, paid_cents = paid_cents + @pay, done_at = CASE WHEN @done THEN @now END
                WHERE id = @id
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("used", used);
                update.Parameters.AddWithValue("pay", pay);
                update.Parameters.AddWithValue("done", done);
                update.Parameters.AddWithValue("now", time.GetUtcNow());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (pay > 0)
            {
                await wallet.CreditAsync(player, pay, $"company {company} task {id}", cancellationToken);
                // A wage is spent already (set aside from the cash); now it counts against the earnings.
                await ChangeMoneyAsync(company, 0, -pay, cancellationToken);
            }

            await LogAsync(company, player, done ? "taskDone" : "taskPart", cancellationToken, task.Item, used);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or the treasurer calls a task off; the wage nobody earned goes back into the cash.</summary>
    public Task<CompanyView> CancelTaskAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var company = await RequireMoneyAsync(player, cancellationToken);
            var task = await TaskAsync(company, id, cancellationToken);
            await ChangeMoneyAsync(company, task.WageCents - task.PaidCents, 0, cancellationToken);
            await using (var delete = await db.CommandAsync("DELETE FROM companies.tasks WHERE id = @id", cancellationToken))
            {
                delete.Parameters.AddWithValue("id", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "taskCancelled", cancellationToken, task.Item, task.Quantity);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    private async Task<TaskState> TaskAsync(Guid company, long id, CancellationToken cancellationToken) =>
        (await TasksAsync(company, cancellationToken, id)).FirstOrDefault(t => t.DoneAt is null)
            ?? throw new GameException("no_task", "There is no such open task.");

    // ---- Deals between companies ----

    /// <summary>The owner or the treasurer offers another company a standing deal: <paramref name="sell"/> when this company delivers.</summary>
    public Task<CompanyView> OfferDealAsync(
        Guid player, string? other, string? item, long perDay, long priceCents, int days, bool sell, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            CheckStack(item, perDay);
            if (perDay > Deals.MaxPerDay || days is < 1 or > Deals.MaxDays || priceCents is < 0 or > CompanyMoney.MaxCents)
            {
                throw new GameException("bad_deal", $"A deal runs 1-{Deals.MaxDays} days for at most {Deals.MaxPerDay} a day.");
            }

            var company = await RequireMoneyAsync(player, cancellationToken);
            Guid partner;
            await using (var find = await db.CommandAsync("SELECT id FROM companies.companies WHERE normalized_name = @name", cancellationToken))
            {
                find.Parameters.AddWithValue("name", (other ?? "").Trim().ToLowerInvariant());
                partner = await find.ExecuteScalarAsync(cancellationToken) is Guid id && id != company
                    ? id
                    : throw new GameException("no_such_company", "There is no other company with this name.");
            }

            if (await CountAsync(
                    "SELECT count(*) FROM companies.deals WHERE (supplier_id = @company OR buyer_id = @company) AND state IN ('offered', 'active')",
                    company, cancellationToken) >= Deals.MaxOpen)
            {
                throw new GameException("too_many_deals", $"A company has at most {Deals.MaxOpen} offers and deals at once.");
            }

            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO companies.deals (supplier_id, buyer_id, offered_by, item, per_day, price_cents, days, state, created_at, settled_days, missed_days)
                VALUES (@supplier, @buyer, @company, @item, @perDay, @price, @days, 'offered', @at, 0, 0)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("supplier", sell ? company : partner);
                insert.Parameters.AddWithValue("buyer", sell ? partner : company);
                insert.Parameters.AddWithValue("company", company);
                insert.Parameters.AddWithValue("item", item!);
                insert.Parameters.AddWithValue("perDay", perDay);
                insert.Parameters.AddWithValue("price", priceCents);
                insert.Parameters.AddWithValue("days", days);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await LogAsync(company, player, "dealOffered", cancellationToken, item, perDay);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>The other company's owner or treasurer accepts an offer; the first day is settled a day later.</summary>
    public Task<CompanyView> AcceptDealAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var company = await RequireMoneyAsync(player, cancellationToken);
            await using (var update = await db.CommandAsync(
                """
                UPDATE companies.deals SET state = 'active', accepted_at = @now
                WHERE id = @id AND state = 'offered' AND offered_by <> @company AND (supplier_id = @company OR buyer_id = @company)
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("company", company);
                update.Parameters.AddWithValue("now", time.GetUtcNow());
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("no_deal", "There is no such offer for your company.");
                }
            }

            await LogAsync(company, player, "dealAccepted", cancellationToken);
            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);

    /// <summary>Either company's owner or treasurer withdraws or declines an offer nobody accepted yet.</summary>
    public Task<CompanyView> CancelDealAsync(Guid player, long id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var company = await RequireMoneyAsync(player, cancellationToken);
            await using (var update = await db.CommandAsync(
                "UPDATE companies.deals SET state = 'cancelled' WHERE id = @id AND state = 'offered' AND (supplier_id = @company OR buyer_id = @company)",
                cancellationToken))
            {
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("company", company);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("no_deal", "There is no such offer for your company.");
                }
            }

            return await ReadAsync(player, cancellationToken);
        }, cancellationToken);
}
