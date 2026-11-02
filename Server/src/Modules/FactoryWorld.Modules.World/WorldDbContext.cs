using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

public sealed class WorldDbContext(DbContextOptions<WorldDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "world";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Plot>(e =>
        {
            e.ToTable("plots");
            // One row per plot, so two players can never both buy the same one.
            e.HasKey(p => new { p.X, p.Y });
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Biome).HasColumnName("biome");
            e.Property(p => p.PriceCents).HasColumnName("price_cents");
            e.Property(p => p.BoughtAt).HasColumnName("bought_at");
            e.Property(p => p.Name).HasColumnName("name").HasMaxLength(PlotNames.MaxLength);
            e.Property(p => p.Public).HasColumnName("public").HasDefaultValue(false);
            e.HasIndex(p => p.OwnerId);
        });
        model.Entity<PlotHarvest>(e =>
        {
            e.ToTable("plot_harvests");
            e.HasKey(h => new { h.X, h.Y, h.Square });
            e.Property(h => h.X).HasColumnName("x");
            e.Property(h => h.Y).HasColumnName("y");
            e.Property(h => h.Square).HasColumnName("square");
            e.Property(h => h.HarvestedAt).HasColumnName("harvested_at");
        });
        model.Entity<PlotField>(e =>
        {
            e.ToTable("plot_fields");
            e.HasKey(f => new { f.X, f.Y, f.TileX, f.TileY });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.TileX).HasColumnName("tile_x");
            e.Property(f => f.TileY).HasColumnName("tile_y");
            e.Property(f => f.Crop).HasColumnName("crop").HasMaxLength(32);
            e.Property(f => f.PlantedAt).HasColumnName("planted_at");
            e.Property(f => f.FarmsteadId).HasColumnName("farmstead_id");
            e.HasIndex(f => f.FarmsteadId);
        });
        model.Entity<PlotTree>(e =>
        {
            e.ToTable("plot_trees");
            e.HasKey(t => new { t.X, t.Y, t.TileX, t.TileY });
            e.Property(t => t.X).HasColumnName("x");
            e.Property(t => t.Y).HasColumnName("y");
            e.Property(t => t.TileX).HasColumnName("tile_x");
            e.Property(t => t.TileY).HasColumnName("tile_y");
            e.Property(t => t.ReadyAt).HasColumnName("ready_at");
            e.Property(t => t.ForesterId).HasColumnName("forester_id");
            e.Property(t => t.Species).HasColumnName("species").HasMaxLength(16).HasDefaultValue(Trees.Pine);
            e.HasIndex(t => t.ForesterId);
        });
        model.Entity<PlotAnimalRow>(e =>
        {
            e.ToTable("plot_animals");
            e.HasKey(a => new { a.X, a.Y, a.TileX, a.TileY });
            e.Property(a => a.X).HasColumnName("x");
            e.Property(a => a.Y).HasColumnName("y");
            e.Property(a => a.TileX).HasColumnName("tile_x");
            e.Property(a => a.TileY).HasColumnName("tile_y");
            e.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(a => a.ReadyAt).HasColumnName("ready_at");
            e.Property(a => a.Meals).HasColumnName("meals").HasDefaultValue(0);
        });
        model.Entity<PlotMine>(e =>
        {
            e.ToTable("plot_mines");
            e.HasKey(m => new { m.X, m.Y, m.Mine });
            e.Property(m => m.X).HasColumnName("x");
            e.Property(m => m.Y).HasColumnName("y");
            e.Property(m => m.Mine).HasColumnName("mine");
            e.Property(m => m.Used).HasColumnName("used");
            e.Property(m => m.EmptyAt).HasColumnName("empty_at");
            e.Property(m => m.Level).HasColumnName("level").HasDefaultValue(1);
        });
        model.Entity<PlotObjectRow>(e =>
        {
            e.ToTable("plot_objects");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.X).HasColumnName("x");
            e.Property(o => o.Y).HasColumnName("y");
            e.Property(o => o.TileX).HasColumnName("tile_x");
            e.Property(o => o.TileY).HasColumnName("tile_y");
            e.Property(o => o.OwnerId).HasColumnName("owner_id");
            e.Property(o => o.Kind).HasColumnName("kind").HasMaxLength(32);
            e.Property(o => o.Rotation).HasColumnName("rotation");
            // One object per tile.
            e.HasIndex(o => new { o.X, o.Y, o.TileX, o.TileY }).IsUnique();
        });
        model.Entity<PlotForesterRow>(e =>
        {
            e.ToTable("plot_foresters");
            e.HasKey(f => f.ObjectId);
            e.Property(f => f.ObjectId).HasColumnName("object_id");
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Wood).HasColumnName("wood");
            e.Property(f => f.OakWood).HasColumnName("oak_wood").HasDefaultValue(0);
            e.Property(f => f.BusyUntil).HasColumnName("busy_until");
            e.Property(f => f.Species).HasColumnName("species").HasMaxLength(16).HasDefaultValue(Trees.Pine);
            e.HasIndex(f => new { f.X, f.Y });
            e.HasIndex(f => f.OwnerId);
        });

        model.Entity<PlotFarmsteadRow>(e =>
        {
            e.ToTable("plot_farmsteads");
            e.HasKey(f => f.ObjectId);
            e.Property(f => f.ObjectId).HasColumnName("object_id");
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Crop).HasColumnName("crop").HasMaxLength(32);
            e.Property(f => f.Store).HasColumnName("store").HasDefaultValue("{}");
            e.Property(f => f.Manure).HasColumnName("manure").HasDefaultValue(0);
            e.Property(f => f.BusyUntil).HasColumnName("busy_until");
            e.HasIndex(f => new { f.X, f.Y });
            e.HasIndex(f => f.OwnerId);
        });

        model.Entity<PlotFeederRow>(e =>
        {
            e.ToTable("plot_feeders");
            e.HasKey(f => f.ObjectId);
            e.Property(f => f.ObjectId).HasColumnName("object_id");
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.OwnerId).HasColumnName("owner_id");
            e.Property(f => f.Store).HasColumnName("store").HasDefaultValue("{}");
            e.Property(f => f.Output).HasColumnName("output").HasDefaultValue("{}");
            e.HasIndex(f => new { f.X, f.Y });
        });

        model.Entity<PlotDrillRow>(e =>
        {
            e.ToTable("plot_drills");
            e.HasKey(d => d.ObjectId);
            e.Property(d => d.ObjectId).HasColumnName("object_id");
            e.Property(d => d.X).HasColumnName("x");
            e.Property(d => d.Y).HasColumnName("y");
            e.Property(d => d.OwnerId).HasColumnName("owner_id");
            e.Property(d => d.Mine).HasColumnName("mine");
            e.Property(d => d.Ores).HasColumnName("ores").HasColumnType("jsonb");
            e.Property(d => d.StartedAt).HasColumnName("started_at");
            e.Property(d => d.Waiting).HasColumnName("waiting");
            e.HasIndex(d => new { d.X, d.Y });
            e.HasIndex(d => d.OwnerId);
        });

        model.Entity<PlotWellRow>(e =>
        {
            e.ToTable("plot_wells");
            e.HasKey(w => w.ObjectId);
            e.Property(w => w.ObjectId).HasColumnName("object_id");
            e.Property(w => w.X).HasColumnName("x");
            e.Property(w => w.Y).HasColumnName("y");
            e.Property(w => w.OwnerId).HasColumnName("owner_id");
            e.Property(w => w.Kind).HasColumnName("kind");
            e.Property(w => w.Stored).HasColumnName("stored");
            e.Property(w => w.StartedAt).HasColumnName("started_at");
            e.Property(w => w.Wear).HasColumnName("wear");
            e.Property(w => w.Gaskets).HasColumnName("gaskets");
            e.Property(w => w.Leaking).HasColumnName("leaking");
            e.HasIndex(w => new { w.X, w.Y });
            e.HasIndex(w => w.OwnerId);
        });

        model.Entity<OilFieldRow>(e =>
        {
            e.ToTable("oil_fields");
            e.HasKey(f => new { f.X, f.Y, f.Field });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.Field).HasColumnName("field");
            e.Property(f => f.Pumped).HasColumnName("pumped");
        });

        model.Entity<BeachFind>(e =>
        {
            e.ToTable("beach_finds");
            e.HasKey(f => new { f.X, f.Y, f.Day, f.TileX, f.TileY });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.Day).HasColumnName("day");
            e.Property(f => f.TileX).HasColumnName("tile_x");
            e.Property(f => f.TileY).HasColumnName("tile_y");
        });

        model.Entity<SurveyRow>(e =>
        {
            e.ToTable("surveys");
            e.HasKey(s => new { s.X, s.Y });
            e.Property(s => s.X).HasColumnName("x");
            e.Property(s => s.Y).HasColumnName("y");
            e.Property(s => s.Oil).HasColumnName("oil");
            e.Property(s => s.Gas).HasColumnName("gas");
            e.Property(s => s.SurveyedAt).HasColumnName("surveyed_at");
        });
        model.Entity<StationItem>(e =>
        {
            e.ToTable("station_items");
            e.HasKey(i => new { i.X, i.Y, i.Item });
            e.Property(i => i.X).HasColumnName("x");
            e.Property(i => i.Y).HasColumnName("y");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(i => i.Quantity).HasColumnName("quantity");
        });
        model.Entity<ShipmentRow>(e =>
        {
            e.ToTable("shipments");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.FromX).HasColumnName("from_x");
            e.Property(s => s.FromY).HasColumnName("from_y");
            e.Property(s => s.ToX).HasColumnName("to_x");
            e.Property(s => s.ToY).HasColumnName("to_y");
            e.Property(s => s.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.SenderId).HasColumnName("sender_id");
            e.Property(s => s.ArrivesAt).HasColumnName("arrives_at");
            e.HasIndex(s => new { s.ToX, s.ToY, s.ArrivesAt });
            e.HasIndex(s => new { s.FromX, s.FromY });
        });
        model.Entity<TrainRouteRow>(e =>
        {
            e.ToTable("train_routes");
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.FromX).HasColumnName("from_x");
            e.Property(r => r.FromY).HasColumnName("from_y");
            e.Property(r => r.ToX).HasColumnName("to_x");
            e.Property(r => r.ToY).HasColumnName("to_y");
            e.Property(r => r.Items).HasColumnName("items");
            e.Property(r => r.EveryMinutes).HasColumnName("every_minutes");
            e.Property(r => r.ByTruck).HasColumnName("by_truck");
            e.Property(r => r.NextDepartureAt).HasColumnName("next_departure_at");
            e.Property(r => r.LastDepartedAt).HasColumnName("last_departed_at");
            e.Property(r => r.LastQuantity).HasColumnName("last_quantity");
            e.Property(r => r.LastProblem).HasColumnName("last_problem").HasMaxLength(32);
            e.Property(r => r.Smuggle).HasColumnName("smuggle").HasDefaultValue(false);
            e.HasIndex(r => r.NextDepartureAt);
            e.HasIndex(r => new { r.FromX, r.FromY });
            e.HasIndex(r => new { r.ToX, r.ToY });
        });
        model.Entity<FreightRouteRow>(e =>
        {
            e.ToTable("freight_routes");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.FromX).HasColumnName("from_x");
            e.Property(r => r.FromY).HasColumnName("from_y");
            e.Property(r => r.ToX).HasColumnName("to_x");
            e.Property(r => r.ToY).HasColumnName("to_y");
            e.Property(r => r.Items).HasColumnName("items");
            e.Property(r => r.Quantities).HasColumnName("quantities");
            e.Property(r => r.State).HasColumnName("state").HasMaxLength(16);
            e.Property(r => r.NextAt).HasColumnName("next_at");
            e.Property(r => r.CarriedItems).HasColumnName("carried_items");
            e.Property(r => r.CarriedQuantities).HasColumnName("carried_quantities");
            e.Property(r => r.Stock).HasColumnName("stock");
            e.Property(r => r.Trips).HasColumnName("trips");
            e.Property(r => r.Delivered).HasColumnName("delivered");
            e.Property(r => r.LastFeeCents).HasColumnName("last_fee_cents");
            e.Property(r => r.LastProblem).HasColumnName("last_problem").HasMaxLength(32);
            e.Property(r => r.Stopping).HasColumnName("stopping");
            e.Property(r => r.Smuggle).HasColumnName("smuggle").HasDefaultValue(false);
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.HasIndex(r => r.NextAt);
            e.HasIndex(r => r.OwnerId);
        });
        model.Entity<ExportChestRow>(e =>
        {
            e.ToTable("export_chests");
            e.HasKey(c => c.ObjectId);
            e.Property(c => c.ObjectId).HasColumnName("object_id");
            e.Property(c => c.X).HasColumnName("x");
            e.Property(c => c.Y).HasColumnName("y");
            e.Property(c => c.NextPickupAt).HasColumnName("next_pickup_at");
            e.Property(c => c.LastPickupAt).HasColumnName("last_pickup_at");
            e.Property(c => c.LastQuantity).HasColumnName("last_quantity");
            e.Property(c => c.LastProblem).HasColumnName("last_problem").HasMaxLength(32);
            e.HasIndex(c => c.NextPickupAt);
            e.HasIndex(c => new { c.X, c.Y }).IsUnique();
        });
        model.Entity<ExportItemRow>(e =>
        {
            e.ToTable("export_items");
            e.HasKey(i => new { i.X, i.Y, i.Item });
            e.Property(i => i.X).HasColumnName("x");
            e.Property(i => i.Y).HasColumnName("y");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(i => i.Quantity).HasColumnName("quantity");
        });
        model.Entity<ExportPriceRow>(e =>
        {
            e.ToTable("export_prices");
            e.HasKey(i => new { i.X, i.Y, i.Item });
            e.Property(i => i.X).HasColumnName("x");
            e.Property(i => i.Y).HasColumnName("y");
            e.Property(i => i.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(i => i.PriceCents).HasColumnName("price_cents");
        });
        model.Entity<ExportPostRow>(e =>
        {
            e.ToTable("export_posts");
            e.HasKey(p => p.ObjectId);
            e.Property(p => p.ObjectId).HasColumnName("object_id");
            e.Property(p => p.Kind).HasColumnName("kind").HasMaxLength(64);
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.Value).HasColumnName("value");
            e.Property(p => p.EarnedCents).HasColumnName("earned_cents");
            e.Property(p => p.Uses).HasColumnName("uses");
        });
        model.Entity<ExportShipmentRow>(e =>
        {
            e.ToTable("export_shipments");
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.SellerId).HasColumnName("seller_id");
            e.Property(s => s.FromX).HasColumnName("from_x");
            e.Property(s => s.FromY).HasColumnName("from_y");
            e.Property(s => s.MarketX).HasColumnName("market_x");
            e.Property(s => s.MarketY).HasColumnName("market_y");
            e.Property(s => s.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.PriceCents).HasColumnName("price_cents");
            e.Property(s => s.ArrivesAt).HasColumnName("arrives_at");
            e.HasIndex(s => s.ArrivesAt);
            e.HasIndex(s => new { s.FromX, s.FromY });
        });
        model.Entity<BankRow>(e =>
        {
            e.ToTable("banks");
            e.HasKey(b => b.CompanyId);
            e.Property(b => b.CompanyId).HasColumnName("company_id");
            e.Property(b => b.Name).HasColumnName("name").HasMaxLength(64);
            e.Property(b => b.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<BankBuildingRow>(e =>
        {
            e.ToTable("bank_buildings");
            e.HasKey(b => b.ObjectId);
            e.Property(b => b.ObjectId).HasColumnName("object_id");
            e.Property(b => b.CompanyId).HasColumnName("company_id");
            e.Property(b => b.Kind).HasColumnName("kind").HasMaxLength(64);
            e.Property(b => b.X).HasColumnName("x");
            e.Property(b => b.Y).HasColumnName("y");
            e.HasIndex(b => b.CompanyId);
        });
        model.Entity<BankAccountRow>(e =>
        {
            e.ToTable("bank_accounts");
            e.HasKey(a => new { a.CompanyId, a.PlayerId });
            e.Property(a => a.CompanyId).HasColumnName("company_id");
            e.Property(a => a.PlayerId).HasColumnName("player_id");
            e.Property(a => a.BalanceCents).HasColumnName("balance_cents");
            e.Property(a => a.OpenedAt).HasColumnName("opened_at");
            e.HasIndex(a => a.PlayerId);
        });
        model.Entity<BankTransferRow>(e =>
        {
            e.ToTable("bank_transfers");
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(t => t.PlayerId).HasColumnName("player_id");
            e.Property(t => t.ToPlayerId).HasColumnName("to_player_id");
            e.Property(t => t.FromCompanyId).HasColumnName("from_company_id");
            e.Property(t => t.ToCompanyId).HasColumnName("to_company_id");
            e.Property(t => t.Cents).HasColumnName("cents");
            e.Property(t => t.SentAt).HasColumnName("sent_at");
            e.Property(t => t.ArrivesAt).HasColumnName("arrives_at");
            e.HasIndex(t => t.ArrivesAt);
            e.HasIndex(t => t.PlayerId);
            e.HasIndex(t => new { t.ToPlayerId, t.ToCompanyId });
        });
        model.Entity<TownProgressRow>(e =>
        {
            e.ToTable("town_progress");
            e.HasKey(t => new { t.TownX, t.TownY });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.DeliveredCents).HasColumnName("delivered_cents");
        });
        model.Entity<TownContributionRow>(e =>
        {
            e.ToTable("town_contributions");
            e.HasKey(t => new { t.TownX, t.TownY, t.PlayerId });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.PlayerId).HasColumnName("player_id");
            e.Property(t => t.DeliveredCents).HasColumnName("delivered_cents");
        });
        model.Entity<TownDeliveryRow>(e =>
        {
            e.ToTable("town_deliveries");
            e.HasKey(t => new { t.TownX, t.TownY, t.Day, t.Item });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.Day).HasColumnName("day");
            e.Property(t => t.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(t => t.Quantity).HasColumnName("quantity");
        });
        model.Entity<ShopRow>(e =>
        {
            e.ToTable("shops");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.SettledAt).HasColumnName("settled_at");
            e.Property(s => s.Carry).HasColumnName("carry");
            e.Property(s => s.SoldCents).HasColumnName("sold_cents");
            e.Property(s => s.SoldItems).HasColumnName("sold_items");
        });
        model.Entity<ShopItemRow>(e =>
        {
            e.ToTable("shop_items");
            e.HasKey(s => new { s.ShopId, s.Item });
            e.Property(s => s.ShopId).HasColumnName("shop_id");
            e.Property(s => s.Item).HasColumnName("item").HasMaxLength(64);
            e.Property(s => s.Quantity).HasColumnName("quantity");
            e.Property(s => s.PriceCents).HasColumnName("price_cents");
        });
        model.Entity<ServiceSaleRow>(e =>
        {
            e.ToTable("service_sales");
            e.HasKey(s => new { s.Id, s.Day });
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.Day).HasColumnName("day");
            e.Property(s => s.Count).HasColumnName("count");
        });
        model.Entity<PlotPauseRow>(e =>
        {
            e.ToTable("plot_pauses");
            e.HasKey(p => new { p.X, p.Y });
            e.Property(p => p.X).HasColumnName("x");
            e.Property(p => p.Y).HasColumnName("y");
            e.Property(p => p.Paused).HasColumnName("paused");
            e.Property(p => p.PausedSince).HasColumnName("paused_since");
        });
        model.Entity<PlotClockCheckRow>(e =>
        {
            e.ToTable("plot_clock");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(c => c.CheckedAt).HasColumnName("checked_at");
        });
        model.Entity<PlotTaxRow>(e =>
        {
            e.ToTable("plot_taxes");
            e.HasKey(t => t.OwnerId);
            e.Property(t => t.OwnerId).HasColumnName("owner_id");
            e.Property(t => t.PaidUntil).HasColumnName("paid_until");
        });
        model.Entity<SavedPlanRow>(e =>
        {
            e.ToTable("plans");
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Name).HasColumnName("name").HasMaxLength(PlanRules.MaxName);
            e.Property(p => p.Objects).HasColumnName("objects").HasColumnType("jsonb");
            e.Property(p => p.SavedAt).HasColumnName("saved_at");
            // A player's plans have names of their own.
            e.HasIndex(p => new { p.OwnerId, p.Name }).IsUnique();
        });
        model.Entity<PlotLikeRow>(e =>
        {
            e.ToTable("plot_likes");
            // A player likes a plot once.
            e.HasKey(l => new { l.X, l.Y, l.PlayerId });
            e.Property(l => l.X).HasColumnName("x");
            e.Property(l => l.Y).HasColumnName("y");
            e.Property(l => l.PlayerId).HasColumnName("player_id");
            e.Property(l => l.LikedAt).HasColumnName("liked_at");
            e.HasIndex(l => l.PlayerId);
        });
        model.Entity<TownVoteRow>(e =>
        {
            e.ToTable("town_votes");
            e.HasKey(v => new { v.TownX, v.TownY, v.Week, v.VoterId });
            e.Property(v => v.TownX).HasColumnName("town_x");
            e.Property(v => v.TownY).HasColumnName("town_y");
            e.Property(v => v.Week).HasColumnName("week");
            e.Property(v => v.VoterId).HasColumnName("voter_id");
            e.Property(v => v.CandidateId).HasColumnName("candidate_id");
        });
        model.Entity<TownPolicyRow>(e =>
        {
            e.ToTable("town_policies");
            e.HasKey(p => new { p.TownX, p.TownY, p.Week });
            e.Property(p => p.TownX).HasColumnName("town_x");
            e.Property(p => p.TownY).HasColumnName("town_y");
            e.Property(p => p.Week).HasColumnName("week");
            e.Property(p => p.MayorId).HasColumnName("mayor_id");
            e.Property(p => p.TaxPercent).HasColumnName("tax_percent");
            e.Property(p => p.Bonus).HasColumnName("bonus").HasMaxLength(16);
            e.Property(p => p.Recalled).HasColumnName("recalled");
            e.Property(p => p.PlotTaxPerMille).HasColumnName("plot_tax_per_mille").HasDefaultValue(0);
        });
        model.Entity<TownTreasuryRow>(e =>
        {
            e.ToTable("town_treasuries", t => t.HasCheckConstraint("ck_town_treasuries_cents", "cents >= 0"));
            e.HasKey(t => new { t.TownX, t.TownY });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.Cents).HasColumnName("cents");
        });
        model.Entity<TownLedgerRow>(e =>
        {
            e.ToTable("town_ledger");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.TownX).HasColumnName("town_x");
            e.Property(l => l.TownY).HasColumnName("town_y");
            e.Property(l => l.AmountCents).HasColumnName("amount_cents");
            e.Property(l => l.BalanceAfterCents).HasColumnName("balance_after_cents");
            e.Property(l => l.Reason).HasColumnName("reason").HasMaxLength(200);
            e.Property(l => l.CreatedAt).HasColumnName("created_at");
            e.HasIndex(l => new { l.TownX, l.TownY, l.Id });
        });
        model.Entity<TownInvestmentRow>(e =>
        {
            e.ToTable("town_investments");
            e.HasKey(i => new { i.TownX, i.TownY, i.Kind });
            e.Property(i => i.TownX).HasColumnName("town_x");
            e.Property(i => i.TownY).HasColumnName("town_y");
            e.Property(i => i.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(i => i.Level).HasColumnName("level");
        });
        model.Entity<TownRecallRow>(e =>
        {
            e.ToTable("town_recalls");
            e.HasKey(r => new { r.TownX, r.TownY, r.Week, r.SignerId });
            e.Property(r => r.TownX).HasColumnName("town_x");
            e.Property(r => r.TownY).HasColumnName("town_y");
            e.Property(r => r.Week).HasColumnName("week");
            e.Property(r => r.SignerId).HasColumnName("signer_id");
        });
        model.Entity<TownCandidateRow>(e =>
        {
            e.ToTable("town_candidates");
            e.HasKey(c => new { c.TownX, c.TownY, c.Week, c.PlayerId });
            e.Property(c => c.TownX).HasColumnName("town_x");
            e.Property(c => c.TownY).HasColumnName("town_y");
            e.Property(c => c.Week).HasColumnName("week");
            e.Property(c => c.PlayerId).HasColumnName("player_id");
            e.Property(c => c.TaxPercent).HasColumnName("tax_percent");
            e.Property(c => c.Bonus).HasColumnName("bonus").HasMaxLength(16);
            e.Property(c => c.Slogan).HasColumnName("slogan").HasMaxLength(TownHallRules.MaxSloganLength);
            e.Property(c => c.DepositCents).HasColumnName("deposit_cents");
            e.Property(c => c.Settled).HasColumnName("settled");
            e.HasIndex(c => c.PlayerId);
        });
        model.Entity<TownSponsorshipRow>(e =>
        {
            e.ToTable("town_sponsorships");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.TownX).HasColumnName("town_x");
            e.Property(s => s.TownY).HasColumnName("town_y");
            e.Property(s => s.Week).HasColumnName("week");
            e.Property(s => s.CandidateId).HasColumnName("candidate_id");
            e.Property(s => s.Company).HasColumnName("company").HasMaxLength(64);
            e.Property(s => s.Cents).HasColumnName("cents");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.HasIndex(s => new { s.TownX, s.TownY, s.Week });
        });
        model.Entity<TownOrdinanceRow>(e =>
        {
            e.ToTable("town_ordinances");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id");
            e.Property(o => o.TownX).HasColumnName("town_x");
            e.Property(o => o.TownY).HasColumnName("town_y");
            e.Property(o => o.Week).HasColumnName("week");
            e.Property(o => o.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(o => o.ProposedBy).HasColumnName("proposed_by");
            e.Property(o => o.VotingEnds).HasColumnName("voting_ends");
            e.HasIndex(o => new { o.TownX, o.TownY, o.Week, o.Kind }).IsUnique();
        });
        model.Entity<TownOrdinanceVoteRow>(e =>
        {
            e.ToTable("town_ordinance_votes");
            e.HasKey(v => new { v.OrdinanceId, v.VoterId });
            e.Property(v => v.OrdinanceId).HasColumnName("ordinance_id");
            e.Property(v => v.VoterId).HasColumnName("voter_id");
            e.Property(v => v.Yes).HasColumnName("yes");
        });
        model.Entity<TownFestivalRow>(e =>
        {
            e.ToTable("town_festivals");
            e.HasKey(f => new { f.TownX, f.TownY });
            e.Property(f => f.TownX).HasColumnName("town_x");
            e.Property(f => f.TownY).HasColumnName("town_y");
            e.Property(f => f.Until).HasColumnName("until");
        });
        model.Entity<MeteorRow>(e =>
        {
            e.ToTable("meteors", t => t.HasCheckConstraint("ck_meteors_left", "iron_left >= 0 AND iridium_left >= 0"));
            e.HasKey(m => m.Slot);
            e.Property(m => m.Slot).HasColumnName("slot").ValueGeneratedNever();
            e.Property(m => m.X).HasColumnName("x");
            e.Property(m => m.Y).HasColumnName("y");
            e.Property(m => m.IronLeft).HasColumnName("iron_left");
            e.Property(m => m.IridiumLeft).HasColumnName("iridium_left");
            e.Property(m => m.FellAt).HasColumnName("fell_at");
        });
        model.Entity<MeteorDigRow>(e =>
        {
            e.ToTable("meteor_digs");
            e.HasKey(d => new { d.Slot, d.PlayerId });
            e.Property(d => d.Slot).HasColumnName("slot");
            e.Property(d => d.PlayerId).HasColumnName("player_id");
            e.Property(d => d.Pieces).HasColumnName("pieces");
            e.Property(d => d.LastAt).HasColumnName("last_at");
        });
        model.Entity<TownWeekDeliveryRow>(e =>
        {
            e.ToTable("town_week_deliveries");
            e.HasKey(d => new { d.TownX, d.TownY, d.Week, d.PlayerId });
            e.Property(d => d.TownX).HasColumnName("town_x");
            e.Property(d => d.TownY).HasColumnName("town_y");
            e.Property(d => d.Week).HasColumnName("week");
            e.Property(d => d.PlayerId).HasColumnName("player_id");
            e.Property(d => d.DeliveredCents).HasColumnName("delivered_cents");
            // The chief supplier is the best of a week.
            e.HasIndex(d => new { d.TownX, d.TownY, d.Week, d.DeliveredCents });
        });
        model.Entity<TownTenderRow>(e =>
        {
            e.ToTable("town_tenders", t =>
            {
                t.HasCheckConstraint("ck_town_tenders_delivered", "delivered >= 0 AND delivered <= quantity");
                t.HasCheckConstraint("ck_town_tenders_money", "price_cents >= 0 AND deposit_cents >= 0");
            });
            e.HasKey(t => new { t.TownX, t.TownY, t.Week });
            e.Property(t => t.TownX).HasColumnName("town_x");
            e.Property(t => t.TownY).HasColumnName("town_y");
            e.Property(t => t.Week).HasColumnName("week");
            e.Property(t => t.Item).HasColumnName("item").HasMaxLength(32);
            e.Property(t => t.Quantity).HasColumnName("quantity");
            e.Property(t => t.CapCents).HasColumnName("cap_cents");
            e.Property(t => t.ClosesAt).HasColumnName("closes_at");
            e.Property(t => t.EndsAt).HasColumnName("ends_at");
            e.Property(t => t.State).HasColumnName("state").HasMaxLength(16);
            e.Property(t => t.WinnerId).HasColumnName("winner_id");
            e.Property(t => t.PriceCents).HasColumnName("price_cents");
            e.Property(t => t.DepositCents).HasColumnName("deposit_cents");
            e.Property(t => t.Delivered).HasColumnName("delivered");
            // The tender clock looks for tenders that are due.
            e.HasIndex(t => t.State);
        });
        model.Entity<TenderBidRow>(e =>
        {
            e.ToTable("tender_bids", t => t.HasCheckConstraint("ck_tender_bids_money", "price_cents > 0 AND deposit_cents > 0"));
            e.HasKey(b => new { b.TownX, b.TownY, b.Week, b.BidderId });
            e.Property(b => b.TownX).HasColumnName("town_x");
            e.Property(b => b.TownY).HasColumnName("town_y");
            e.Property(b => b.Week).HasColumnName("week");
            e.Property(b => b.BidderId).HasColumnName("bidder_id");
            e.Property(b => b.PriceCents).HasColumnName("price_cents");
            e.Property(b => b.DepositCents).HasColumnName("deposit_cents");
            e.Property(b => b.At).HasColumnName("at");
        });
        model.Entity<PlotFireRow>(e =>
        {
            e.ToTable("plot_fires");
            e.HasKey(f => new { f.X, f.Y });
            e.Property(f => f.X).HasColumnName("x");
            e.Property(f => f.Y).HasColumnName("y");
            e.Property(f => f.CheckedAt).HasColumnName("checked_at");
            e.Property(f => f.LastAt).HasColumnName("last_at");
            e.Property(f => f.LastX).HasColumnName("last_x");
            e.Property(f => f.LastY).HasColumnName("last_y");
            e.Property(f => f.LastBurnt).HasColumnName("last_burnt");
            e.Property(f => f.LastStopped).HasColumnName("last_stopped");
        });
        model.Entity<PlotSprayRow>(e =>
        {
            e.ToTable("plot_sprays");
            e.HasKey(s => new { s.X, s.Y });
            e.Property(s => s.X).HasColumnName("x");
            e.Property(s => s.Y).HasColumnName("y");
            e.Property(s => s.Until).HasColumnName("until");
        });
        model.Entity<HiveRow>(e =>
        {
            e.ToTable("hives");
            e.HasKey(h => h.ObjectId);
            e.Property(h => h.ObjectId).HasColumnName("object_id");
            e.Property(h => h.X).HasColumnName("x");
            e.Property(h => h.Y).HasColumnName("y");
            e.Property(h => h.OwnerId).HasColumnName("owner_id");
            e.Property(h => h.Honey).HasColumnName("honey");
            e.Property(h => h.Wax).HasColumnName("wax");
            e.Property(h => h.Made).HasColumnName("made");
            e.Property(h => h.Progress).HasColumnName("progress");
            e.Property(h => h.At).HasColumnName("at");
        });
        model.Entity<ForestLedgerRow>(e =>
        {
            e.ToTable("forest_ledger");
            e.HasKey(l => new { l.PlayerId, l.Day });
            e.Property(l => l.PlayerId).HasColumnName("player_id");
            e.Property(l => l.Day).HasColumnName("day");
            e.Property(l => l.Planted).HasColumnName("planted");
            e.Property(l => l.Cut).HasColumnName("cut");
        });
        model.Entity<FootprintVersionRow>(e =>
        {
            e.ToTable("footprint_versions");
            e.HasKey(v => v.Version);
            e.Property(v => v.Version).HasColumnName("version").ValueGeneratedNever();
            e.Property(v => v.At).HasColumnName("at");
        });
    }
}
