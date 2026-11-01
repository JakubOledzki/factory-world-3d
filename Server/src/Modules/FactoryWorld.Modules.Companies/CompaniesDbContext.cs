using FactoryWorld.Modules.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FactoryWorld.Modules.Companies;

/// <summary>A company: players who run plots together and share a warehouse.</summary>
public sealed class CompanyRow
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The company's money, in cents.</summary>
    public long CashCents { get; set; }

    /// <summary>What it earned since the last dividend.</summary>
    public long IncomeCents { get; set; }

    public int DividendPercent { get; set; } = CompanyMoney.DefaultDividendPercent;

    /// <summary>When the last dividend was settled; the next one is due a day later.</summary>
    public DateTimeOffset DividendAt { get; set; }

    /// <summary>Whether it is on the stock exchange, so anyone may buy its shares.</summary>
    public bool Listed { get; set; }
}

/// <summary>How many shares of a company a player holds, whether or not they are in it.</summary>
public sealed class ShareRow
{
    public Guid CompanyId { get; set; }

    public Guid AccountId { get; set; }

    public long Shares { get; set; }
}

/// <summary>What a company earned (less what it spent on goods and wages) on one day.</summary>
public sealed class IncomeRow
{
    public Guid CompanyId { get; set; }

    public DateOnly Day { get; set; }

    public long Cents { get; set; }
}

/// <summary>A big project the company took on.</summary>
public sealed class ProjectRow
{
    public Guid CompanyId { get; set; }

    public required string Project { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it is finished; set once every part is in.</summary>
    public DateTimeOffset? BuiltAt { get; set; }

    /// <summary>Whether its reward went into the warehouse.</summary>
    public bool Rewarded { get; set; }
}

/// <summary>How much of one part members brought for a big project.</summary>
public sealed class ProjectPartRow
{
    public Guid CompanyId { get; set; }

    public required string Project { get; set; }

    public required string Item { get; set; }

    public long Delivered { get; set; }
}

/// <summary>A wonder of the world and the one company that brought every part for it first.</summary>
public sealed class WonderRow
{
    public required string Project { get; set; }

    public Guid CompanyId { get; set; }

    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>How much the company delivered towards one of a week's orders.</summary>
public sealed class OrderRow
{
    public Guid CompanyId { get; set; }

    public int Week { get; set; }

    public int Slot { get; set; }

    public long Delivered { get; set; }
}

/// <summary>A task for members, with its wage set aside from the cash.</summary>
public sealed class TaskRow
{
    public long Id { get; set; }

    public Guid CompanyId { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }

    public long Delivered { get; set; }

    public long WageCents { get; set; }

    public long PaidCents { get; set; }

    public Guid? AssigneeId { get; set; }

    public Guid CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DoneAt { get; set; }
}

/// <summary>A standing deal between two companies.</summary>
public sealed class DealRow
{
    public long Id { get; set; }

    public Guid SupplierId { get; set; }

    public Guid BuyerId { get; set; }

    public Guid OfferedBy { get; set; }

    public required string Item { get; set; }

    public long PerDay { get; set; }

    public long PriceCents { get; set; }

    public int Days { get; set; }

    public required string State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public int SettledDays { get; set; }

    public int MissedDays { get; set; }
}

/// <summary>A season's winner (place 0 only marks the season closed).</summary>
public sealed class SeasonRow
{
    public int Season { get; set; }

    public int Place { get; set; }

    public Guid CompanyId { get; set; }

    public required string Name { get; set; }

    public long ValueCents { get; set; }
}

/// <summary>A player in a company; a player is in at most one.</summary>
public sealed class MemberRow
{
    public Guid AccountId { get; set; }

    public Guid CompanyId { get; set; }

    public required string Role { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
}

/// <summary>An invitation the company's owner sent, until the player answers it.</summary>
public sealed class InviteRow
{
    public Guid CompanyId { get; set; }

    public Guid AccountId { get; set; }

    public DateTimeOffset InvitedAt { get; set; }
}

/// <summary>A plot its owner brought into their company.</summary>
public sealed class CompanyPlotRow
{
    public int X { get; set; }

    public int Y { get; set; }

    public Guid CompanyId { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>The member in charge of building on it, if any.</summary>
    public Guid? ManagerId { get; set; }

    /// <summary>The shares its owner got for bringing it in; they go when the plot leaves the company.</summary>
    public long Shares { get; set; }
}

/// <summary>How many of one item lie in a company's warehouse.</summary>
public sealed class WarehouseRow
{
    public Guid CompanyId { get; set; }

    public required string Item { get; set; }

    public long Quantity { get; set; }
}

/// <summary>Something that happened in a company, so members see who did what.</summary>
public sealed class LogRow
{
    public long Id { get; set; }

    public Guid CompanyId { get; set; }

    public DateTimeOffset At { get; set; }

    public Guid AccountId { get; set; }

    public required string Action { get; set; }

    public string? Item { get; set; }

    public long Quantity { get; set; }

    public Guid? Other { get; set; }
}

public sealed class CompaniesDbContext(DbContextOptions<CompaniesDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "companies";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<CompanyRow>(e =>
        {
            e.ToTable("companies");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.Name).HasColumnName("name").HasMaxLength(CompanyRules.MaxNameLength);
            e.Property(c => c.NormalizedName).HasColumnName("normalized_name").HasMaxLength(CompanyRules.MaxNameLength);
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.Property(c => c.CashCents).HasColumnName("cash_cents").HasDefaultValue(0L);
            e.Property(c => c.IncomeCents).HasColumnName("income_cents").HasDefaultValue(0L);
            e.Property(c => c.DividendPercent).HasColumnName("dividend_percent").HasDefaultValue(CompanyMoney.DefaultDividendPercent);
            e.Property(c => c.DividendAt).HasColumnName("dividend_at").HasDefaultValueSql("now()");
            e.Property(c => c.Listed).HasColumnName("listed").HasDefaultValue(false);
            e.HasIndex(c => c.NormalizedName).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_companies_cash", "cash_cents >= 0"));
        });
        model.Entity<ShareRow>(e =>
        {
            e.ToTable("shares");
            e.HasKey(s => new { s.CompanyId, s.AccountId });
            e.Property(s => s.CompanyId).HasColumnName("company_id");
            e.Property(s => s.AccountId).HasColumnName("account_id");
            e.Property(s => s.Shares).HasColumnName("shares");
            e.HasIndex(s => s.AccountId);
        });
        model.Entity<IncomeRow>(e =>
        {
            e.ToTable("income");
            e.HasKey(i => new { i.CompanyId, i.Day });
            e.Property(i => i.CompanyId).HasColumnName("company_id");
            e.Property(i => i.Day).HasColumnName("day");
            e.Property(i => i.Cents).HasColumnName("cents");
            e.HasIndex(i => i.Day);
        });
        model.Entity<ProjectRow>(e =>
        {
            e.ToTable("projects");
            e.HasKey(p => new { p.CompanyId, p.Project });
            e.Property(p => p.CompanyId).HasColumnName("company_id");
            e.Property(p => p.Project).HasColumnName("project").HasMaxLength(32);
            e.Property(p => p.StartedAt).HasColumnName("started_at");
            e.Property(p => p.BuiltAt).HasColumnName("built_at");
            e.Property(p => p.Rewarded).HasColumnName("rewarded");
        });
        model.Entity<ProjectPartRow>(e =>
        {
            e.ToTable("project_parts");
            e.HasKey(p => new { p.CompanyId, p.Project, p.Item });
            e.Property(p => p.CompanyId).HasColumnName("company_id");
            e.Property(p => p.Project).HasColumnName("project").HasMaxLength(32);
            e.Property(p => p.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(p => p.Delivered).HasColumnName("delivered");
        });
        model.Entity<WonderRow>(e =>
        {
            e.ToTable("wonders");
            e.HasKey(w => w.Project);
            e.Property(w => w.Project).HasColumnName("project").HasMaxLength(32);
            e.Property(w => w.CompanyId).HasColumnName("company_id");
            e.Property(w => w.ClaimedAt).HasColumnName("claimed_at");
        });
        model.Entity<OrderRow>(e =>
        {
            e.ToTable("orders");
            e.HasKey(o => new { o.CompanyId, o.Week, o.Slot });
            e.Property(o => o.CompanyId).HasColumnName("company_id");
            e.Property(o => o.Week).HasColumnName("week");
            e.Property(o => o.Slot).HasColumnName("slot");
            e.Property(o => o.Delivered).HasColumnName("delivered");
        });
        model.Entity<TaskRow>(e =>
        {
            e.ToTable("tasks");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.CompanyId).HasColumnName("company_id");
            e.Property(t => t.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(t => t.Quantity).HasColumnName("quantity");
            e.Property(t => t.Delivered).HasColumnName("delivered");
            e.Property(t => t.WageCents).HasColumnName("wage_cents");
            e.Property(t => t.PaidCents).HasColumnName("paid_cents");
            e.Property(t => t.AssigneeId).HasColumnName("assignee_id");
            e.Property(t => t.CreatedBy).HasColumnName("created_by");
            e.Property(t => t.CreatedAt).HasColumnName("created_at");
            e.Property(t => t.DoneAt).HasColumnName("done_at");
            e.HasIndex(t => t.CompanyId);
        });
        model.Entity<DealRow>(e =>
        {
            e.ToTable("deals");
            e.Property(d => d.Id).HasColumnName("id");
            e.Property(d => d.SupplierId).HasColumnName("supplier_id");
            e.Property(d => d.BuyerId).HasColumnName("buyer_id");
            e.Property(d => d.OfferedBy).HasColumnName("offered_by");
            e.Property(d => d.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(d => d.PerDay).HasColumnName("per_day");
            e.Property(d => d.PriceCents).HasColumnName("price_cents");
            e.Property(d => d.Days).HasColumnName("days");
            e.Property(d => d.State).HasColumnName("state").HasMaxLength(16);
            e.Property(d => d.CreatedAt).HasColumnName("created_at");
            e.Property(d => d.AcceptedAt).HasColumnName("accepted_at");
            e.Property(d => d.SettledDays).HasColumnName("settled_days");
            e.Property(d => d.MissedDays).HasColumnName("missed_days");
            e.HasIndex(d => d.SupplierId);
            e.HasIndex(d => d.BuyerId);
        });
        model.Entity<SeasonRow>(e =>
        {
            e.ToTable("seasons");
            e.HasKey(s => new { s.Season, s.Place });
            e.Property(s => s.Season).HasColumnName("season");
            e.Property(s => s.Place).HasColumnName("place");
            e.Property(s => s.CompanyId).HasColumnName("company_id");
            e.Property(s => s.Name).HasColumnName("name").HasMaxLength(CompanyRules.MaxNameLength);
            e.Property(s => s.ValueCents).HasColumnName("value_cents");
            e.HasIndex(s => s.CompanyId);
        });
        model.Entity<MemberRow>(e =>
        {
            e.ToTable("members");
            // One row per player: nobody is in two companies.
            e.HasKey(m => m.AccountId);
            e.Property(m => m.AccountId).HasColumnName("account_id");
            e.Property(m => m.CompanyId).HasColumnName("company_id");
            e.Property(m => m.Role).HasColumnName("role").HasMaxLength(16);
            e.Property(m => m.JoinedAt).HasColumnName("joined_at");
            e.HasIndex(m => m.CompanyId);
        });
        model.Entity<InviteRow>(e =>
        {
            e.ToTable("invites");
            e.HasKey(i => new { i.CompanyId, i.AccountId });
            e.Property(i => i.CompanyId).HasColumnName("company_id");
            e.Property(i => i.AccountId).HasColumnName("account_id");
            e.Property(i => i.InvitedAt).HasColumnName("invited_at");
            e.HasIndex(i => i.AccountId);
        });
        model.Entity<CompanyPlotRow>(e =>
        {
            e.ToTable("plots");
            // A plot is in one company at most.
            e.HasKey(p => new { p.X, p.Y });
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.CompanyId).HasColumnName("company_id");
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.ManagerId).HasColumnName("manager_id");
            e.Property(p => p.Shares).HasColumnName("shares");
            e.HasIndex(p => p.CompanyId);
        });
        model.Entity<WarehouseRow>(e =>
        {
            e.ToTable("warehouse", t => t.HasCheckConstraint("ck_warehouse_quantity", "quantity >= 0"));
            e.HasKey(w => new { w.CompanyId, w.Item });
            e.Property(w => w.CompanyId).HasColumnName("company_id");
            e.Property(w => w.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(w => w.Quantity).HasColumnName("quantity");
        });
        model.Entity<LogRow>(e =>
        {
            e.ToTable("log");
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.CompanyId).HasColumnName("company_id");
            e.Property(l => l.At).HasColumnName("at");
            e.Property(l => l.AccountId).HasColumnName("account_id");
            e.Property(l => l.Action).HasColumnName("action").HasMaxLength(16);
            e.Property(l => l.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(l => l.Quantity).HasColumnName("quantity");
            e.Property(l => l.Other).HasColumnName("other");
            e.HasIndex(l => new { l.CompanyId, l.Id });
        });
    }
}
