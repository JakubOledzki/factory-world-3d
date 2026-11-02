#include "Game/FWCatalog.h"

#define LOCTEXT_NAMESPACE "FWCatalog"

namespace FWCatalog
{
	namespace
	{
		// Same as the 2D client's Footprints.GROWN and BIG
		const TMap<FString, int32>& Sizes()
		{
			static const TMap<FString, int32> Map = {
				{ TEXT("furniture_furnace"), 2 }, { TEXT("furniture_mill"), 2 }, { TEXT("furniture_oil_press"), 2 },
				{ TEXT("furniture_pickling_barrel"), 2 }, { TEXT("furniture_bakery"), 2 }, { TEXT("furniture_kitchen"), 2 },
				{ TEXT("furniture_sawmill"), 2 }, { TEXT("furniture_quarry"), 2 }, { TEXT("furniture_crusher"), 2 },
				{ TEXT("furniture_stonemason"), 2 }, { TEXT("furniture_smokehouse"), 2 }, { TEXT("furniture_cannery"), 2 },
				{ TEXT("furniture_distillery"), 2 }, { TEXT("furniture_glassworks"), 2 }, { TEXT("furniture_cement_kiln"), 2 },
				{ TEXT("furniture_concrete_mixer"), 2 }, { TEXT("furniture_ice_melter"), 2 }, { TEXT("furniture_cold_store"), 2 },
				{ TEXT("furniture_paper_mill"), 2 }, { TEXT("furniture_carpentry"), 2 }, { TEXT("furniture_charcoal_kiln"), 2 },
				{ TEXT("furniture_tar_kiln"), 2 }, { TEXT("furniture_dairy"), 2 }, { TEXT("furniture_slaughterhouse"), 2 },
				{ TEXT("furniture_potato_distillery"), 2 }, { TEXT("furniture_machine_shop"), 2 },
				{ TEXT("furniture_multistorey_factory"), 2 },
				{ TEXT("furniture_brick_furnace"), 3 }, { TEXT("furniture_electric_sawmill"), 3 }, { TEXT("furniture_foundry"), 3 },
				{ TEXT("furniture_refinery"), 3 }, { TEXT("furniture_cracker"), 3 }, { TEXT("furniture_chemical_plant"), 3 },
				{ TEXT("furniture_arc_furnace"), 3 },
				{ TEXT("furniture_blast_furnace"), 4 }, { TEXT("furniture_industrial_sawmill"), 4 },
				{ TEXT("furniture_forester_hut"), 3 }, { TEXT("furniture_forester_lodge"), 3 }, { TEXT("furniture_forestry_station"), 3 },
				{ TEXT("power_solar_tower"), 3 }, { TEXT("power_large_wind_turbine"), 3 }, { TEXT("furniture_ice_hotel"), 3 },
				{ TEXT("furniture_assembly_hall"), 3 }, { TEXT("furniture_robotic_hall"), 3 }, { TEXT("furniture_oxygen_converter"), 3 },
				{ TEXT("furniture_rolling_mill"), 3 }, { TEXT("furniture_continuous_caster"), 3 }, { TEXT("storage_automated_warehouse"), 3 },
				{ TEXT("power_fusion_plant"), 3 }, { TEXT("power_coal_plant"), 3 }, { TEXT("power_nuclear_plant"), 4 },
				{ TEXT("power_energy_store"), 2 }, { TEXT("furniture_canteen"), 3 }, { TEXT("furniture_biogas_plant"), 3 },
				{ TEXT("furniture_farmstead"), 3 }, { TEXT("furniture_manor_farm"), 3 }, { TEXT("furniture_buy_market"), 3 },
				{ TEXT("furniture_bank_branch"), 3 }, { TEXT("furniture_assembler"), 2 }, { TEXT("furniture_fast_assembler"), 3 },
				{ TEXT("furniture_express_assembler"), 4 }, { TEXT("furniture_roller_mill"), 3 }, { TEXT("furniture_flour_works"), 4 },
				{ TEXT("furniture_mechanized_quarry"), 3 }, { TEXT("furniture_open_pit_mine"), 4 }, { TEXT("furniture_cone_crusher"), 3 },
				{ TEXT("furniture_ore_processing_plant"), 4 }, { TEXT("furniture_spinning_mill"), 2 }, { TEXT("furniture_loom"), 2 },
				{ TEXT("furniture_sugar_factory"), 2 }, { TEXT("furniture_jeweler"), 2 }, { TEXT("furniture_electrolyzer"), 2 },
				{ TEXT("furniture_tree_nursery"), 2 }, { TEXT("furniture_auto_barn"), 2 }, { TEXT("power_fuel_cell"), 2 },
				{ TEXT("furniture_coke_oven"), 3 }, { TEXT("power_biomass_plant"), 3 }, { TEXT("power_chp_plant"), 3 },
				{ TEXT("power_geothermal_plant"), 3 }, { TEXT("furniture_mechanized_farm"), 4 }, { TEXT("furniture_steel_combine"), 5 },
				{ TEXT("furniture_megafactory"), 5 }, { TEXT("furniture_bank"), 5 }, { TEXT("furniture_rail_port"), 9 },
			};
			return Map;
		}
	}

	int32 FootprintSize(const FString& Kind)
	{
		const int32* Size = Sizes().Find(Kind);
		return Size ? *Size : 1;
	}

	bool IsBelt(const FString& Kind)
	{
		return Kind == TEXT("logistics_belt") || Kind == TEXT("logistics_fast_belt") || Kind == TEXT("logistics_express_belt")
			|| Kind == TEXT("logistics_splitter") || Kind == TEXT("logistics_underground_belt");
	}

	int32 StationOf(const FString& Kind)
	{
		static const TSet<FString> Crafting = { TEXT("furniture_workbench"), TEXT("furniture_anvil"), TEXT("furniture_shipyard"), TEXT("furniture_tailor") };
		static const TSet<FString> Furnaces = { TEXT("furniture_furnace"), TEXT("furniture_brick_furnace"), TEXT("furniture_blast_furnace") };
		// The same list as the 2D client's plot.gd (MACHINES)
		static const TSet<FString> Machines = { TEXT("furniture_mill"), TEXT("furniture_oil_press"), TEXT("furniture_pickling_barrel"), TEXT("furniture_bakery"), TEXT("furniture_kitchen"),TEXT("furniture_sawmill"), TEXT("furniture_electric_sawmill"), TEXT("furniture_industrial_sawmill"),TEXT("furniture_assembler"), TEXT("furniture_fast_assembler"), TEXT("furniture_express_assembler"),TEXT("furniture_quarry"), TEXT("furniture_crusher"), TEXT("furniture_stonemason"), TEXT("furniture_foundry"),TEXT("furniture_roller_mill"), TEXT("furniture_flour_works"), TEXT("furniture_mechanized_quarry"), TEXT("furniture_open_pit_mine"),TEXT("furniture_cone_crusher"), TEXT("furniture_ore_processing_plant"),TEXT("furniture_fishing_boat"), TEXT("furniture_rowing_boat"), TEXT("furniture_smokehouse"), TEXT("furniture_cannery"), TEXT("furniture_salt_pan"),TEXT("furniture_fish_farm"), TEXT("furniture_distillery"), TEXT("furniture_refinery"), TEXT("furniture_cracker"), TEXT("furniture_chemical_plant"),TEXT("furniture_glassworks"), TEXT("furniture_arc_furnace"), TEXT("furniture_water_well"), TEXT("furniture_desalinator"), TEXT("furniture_drying_yard"),TEXT("furniture_cement_kiln"), TEXT("furniture_concrete_mixer"), TEXT("furniture_ice_melter"), TEXT("furniture_cold_store"), TEXT("furniture_server_room"),TEXT("furniture_ice_core_drill"), TEXT("furniture_polar_station"), TEXT("furniture_ice_hotel"), TEXT("furniture_viewpoint"),TEXT("furniture_assembly_hall"), TEXT("furniture_multistorey_factory"), TEXT("furniture_city_sorter"), TEXT("furniture_design_office"),TEXT("furniture_laboratory"), TEXT("furniture_service_workshop"), TEXT("furniture_paper_mill"), TEXT("furniture_carpentry"), TEXT("furniture_charcoal_kiln"),TEXT("furniture_tar_kiln"), TEXT("furniture_machine_shop"), TEXT("furniture_robotic_hall"), TEXT("furniture_oxygen_converter"), TEXT("furniture_rolling_mill"),TEXT("furniture_continuous_caster"), TEXT("furniture_steel_combine"), TEXT("furniture_megafactory"),TEXT("furniture_slaughterhouse"), TEXT("furniture_dairy"), TEXT("furniture_potato_distillery"), TEXT("furniture_biogas_plant"),TEXT("furniture_coke_oven"), TEXT("furniture_jeweler"), TEXT("furniture_spinning_mill"), TEXT("furniture_loom"), TEXT("furniture_sugar_factory"), TEXT("furniture_electrolyzer") };
		return Crafting.Contains(Kind) ? 0 : Furnaces.Contains(Kind) ? 1 : Machines.Contains(Kind) ? 2 : -1;
	}

	bool IsInserter(const FString& Kind)
	{
		return Kind.StartsWith(TEXT("logistics_")) && Kind.EndsWith(TEXT("inserter"));
	}

	bool IsStorage(const FString& Kind)
	{
		return Kind.StartsWith(TEXT("storage_"));
	}

	int32 BeltSpeed(const FString& Kind)
	{
		if (Kind == TEXT("logistics_fast_belt"))
		{
			return 2;
		}
		return Kind == TEXT("logistics_express_belt") ? 3 : 1;
	}

	FIntPoint Facing(int32 Rotation)
	{
		static const FIntPoint Ways[] = { FIntPoint(0, 1), FIntPoint(1, 0), FIntPoint(0, -1), FIntPoint(-1, 0) };
		return Ways[Rotation & 3];
	}

	FIntPoint LeftOf(int32 Rotation)
	{
		const FIntPoint F = Facing(Rotation);
		return FIntPoint(F.Y, -F.X);
	}

	FLinearColor ColorFor(const FString& Id)
	{
		static const TMap<FString, FLinearColor> Known = {
			{ TEXT("stone"), FLinearColor(0.35f, 0.34f, 0.32f) },
			{ TEXT("coal"), FLinearColor(0.03f, 0.03f, 0.03f) },
			{ TEXT("iron_ore"), FLinearColor(0.42f, 0.22f, 0.15f) },
			{ TEXT("copper_ore"), FLinearColor(0.55f, 0.3f, 0.12f) },
			{ TEXT("tin_ore"), FLinearColor(0.6f, 0.62f, 0.6f) },
			{ TEXT("silver_ore"), FLinearColor(0.8f, 0.8f, 0.82f) },
			{ TEXT("gold_ore"), FLinearColor(0.9f, 0.65f, 0.1f) },
			{ TEXT("wood"), FLinearColor(0.4f, 0.25f, 0.12f) },
			{ TEXT("logistics_belt"), FLinearColor(0.12f, 0.12f, 0.13f) },
			{ TEXT("logistics_fast_belt"), FLinearColor(0.45f, 0.08f, 0.05f) },
			{ TEXT("logistics_express_belt"), FLinearColor(0.08f, 0.2f, 0.5f) },
			{ TEXT("logistics_inserter"), FLinearColor(0.85f, 0.6f, 0.08f) },
			{ TEXT("storage_wooden_chest"), FLinearColor(0.45f, 0.3f, 0.15f) },
			{ TEXT("storage_iron_chest"), FLinearColor(0.4f, 0.42f, 0.45f) },
		};
		if (const FLinearColor* Color = Known.Find(Id))
		{
			return *Color;
		}
		// Muted, stable colour from the name
		const uint32 Hash = GetTypeHash(Id);
		const float Hue = (Hash % 360);
		return FLinearColor::MakeFromHSV8(uint8(Hue / 360.f * 255.f), 110, 170).Desaturate(0.2f);
	}

	FLinearColor BiomeColor(int32 Biome)
	{
		switch (Biome)
		{
		case 2: return FLinearColor(0.09f, 0.14f, 0.05f);   // forest
		case 3: return FLinearColor(0.16f, 0.13f, 0.06f);   // farmland
		case 4: return FLinearColor(0.2f, 0.2f, 0.08f);     // grassland
		case 5: return FLinearColor(0.55f, 0.42f, 0.24f);   // desert
		case 6: return FLinearColor(0.11f, 0.105f, 0.095f); // mountains
		case 7: return FLinearColor(0.22f, 0.22f, 0.15f);   // tundra
		case 8: return FLinearColor(0.75f, 0.8f, 0.85f);    // ice
		case 9: return FLinearColor(0.22f, 0.22f, 0.22f);   // city
		case 10:
		case 11: return FLinearColor(0.3f, 0.27f, 0.17f);   // coast, lakeshore
		case 12: return FLinearColor(0.5f, 0.4f, 0.22f);    // oasis
		default: return FLinearColor(0.15f, 0.16f, 0.1f);
		}
	}

	FText ErrorText(const FString& Code, const FString& Fallback)
	{
		// The server names the tool it wants ("You need Siekiera in your hand.")
		if (Code == TEXT("wrong_tool"))
		{
			return Fallback.Contains(TEXT("Siekiera")) ? LOCTEXT("WrongToolAxe", "Potrzebujesz siekiery w pasku narzędzi.")
				: Fallback.Contains(TEXT("Kilof")) ? LOCTEXT("WrongToolPickaxe", "Potrzebujesz kilofa w pasku narzędzi.")
				: Fallback.Contains(TEXT("opata")) ? LOCTEXT("WrongToolShovel", "Potrzebujesz łopaty w pasku narzędzi.")
				: LOCTEXT("WrongTool", "Potrzebujesz do tego innego narzędzia w pasku narzędzi.");
		}
		static const TMap<FString, FText> Texts = {
			{ TEXT("not_struck"), LOCTEXT("NotStruck", "Trzeba uderzyć pięć razy.") },
			{ TEXT("not_enough_money"), LOCTEXT("NotEnoughMoney", "Za mało pieniędzy. Zarób w garażu w Factory World.") },
			{ TEXT("not_enough_items"), LOCTEXT("NotEnoughItems", "Nie masz tego przedmiotu.") },
			{ TEXT("inventory_full"), LOCTEXT("InventoryFull", "Ekwipunek jest pełny.") },
			{ TEXT("blocked"), LOCTEXT("Blocked", "To pole jest zajęte.") },
			{ TEXT("nothing_here"), LOCTEXT("NothingHere", "Tu nic nie ma.") },
			{ TEXT("no_object"), LOCTEXT("NoObject", "Tego obiektu już nie ma.") },
			{ TEXT("too_far"), LOCTEXT("TooFar", "Za daleko.") },
			{ TEXT("storm"), LOCTEXT("Storm", "Trwa burza, praca na zewnątrz stoi.") },
			{ TEXT("unknown_item"), LOCTEXT("UnknownItem", "Nieznany przedmiot.") },
			{ TEXT("not_owner"), LOCTEXT("NotOwner", "To nie twoja działka.") },
			{ TEXT("mine_empty"), LOCTEXT("MineEmpty", "Kopalnia jest wyczerpana. Odnowi się za chwilę.") },
			{ TEXT("too_fast"), LOCTEXT("TooFast", "Za szybko.") },
			{ TEXT("plot_tax_due"), LOCTEXT("TaxDue", "Masz zaległy podatek od działek.") },
			{ TEXT("plot_taken"), LOCTEXT("PlotTaken", "Ktoś już kupił tę działkę.") },
			{ TEXT("locked"), LOCTEXT("Locked", "Najpierw to zbadaj.") },
			{ TEXT("name_taken"), LOCTEXT("NameTaken", "Ta nazwa jest zajęta.") },
			{ TEXT("invalid_name"), LOCTEXT("InvalidName", "Nazwa: od 3 znaków, tylko litery, cyfry i _.") },
			{ TEXT("storage_full"), LOCTEXT("StorageFull", "Skrzynia jest pełna.") },
			{ TEXT("timeout"), LOCTEXT("Timeout", "Serwer nie odpowiedział.") },
			{ TEXT("not_connected"), LOCTEXT("NotConnected", "Brak połączenia z serwerem.") },
			{ TEXT("disconnected"), LOCTEXT("Disconnected", "Utracono połączenie z serwerem.") },
		};
		if (const FText* Text = Texts.Find(Code))
		{
			return *Text;
		}
		return FText::Format(LOCTEXT("Generic", "Błąd: {0}"), FText::FromString(Fallback.IsEmpty() ? Code : Fallback));
	}
}

#undef LOCTEXT_NAMESPACE
