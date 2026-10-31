#include "Game/FWArt.h"

#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Misc/PackageName.h"

namespace FWArt
{
	namespace
	{
		template <typename T>
		T* Load(const TCHAR* Path)
		{
			// Check first, so a missing optional asset does not log a load error
			const FString Package = FPackageName::ObjectPathToPackageName(FString(Path));
			if (!FPackageName::DoesPackageExist(Package))
			{
				return nullptr;
			}
			return LoadObject<T>(nullptr, Path);
		}

		const TCHAR* const Models = TEXT("/Game/External/Models/");
	}

	UStaticMesh* Mesh(const TCHAR* Path)
	{
		return Load<UStaticMesh>(Path);
	}

	UTexture2D* Texture(const TCHAR* Path)
	{
		return Load<UTexture2D>(Path);
	}

	TArray<UStaticMesh*> Meshes(std::initializer_list<const TCHAR*> Paths)
	{
		TArray<UStaticMesh*> Found;
		for (const TCHAR* Path : Paths)
		{
			if (UStaticMesh* Loaded = Mesh(Path))
			{
				Found.Add(Loaded);
			}
		}
		return Found;
	}

#define FW_MODEL(Set, Name) TEXT("/Game/External/Models/" Set "/" Set "/StaticMeshes/" Name "." Name)

	TArray<UStaticMesh*> Rocks()
	{
		return Meshes({
			FW_MODEL("boulder_01", "boulder_01"),
			// (The Namaqualand boulders are African sandstone with orange lichen: wrong for the Tatras)
			FW_MODEL("rock_09", "rock_09"),
			FW_MODEL("rock_moss_set_01", "rock_moss_set_01_rock01"),
			FW_MODEL("rock_moss_set_01", "rock_moss_set_01_rock02"),
			FW_MODEL("rock_moss_set_01", "rock_moss_set_01_rock03"),
			FW_MODEL("rock_moss_set_01", "rock_moss_set_01_rock04"),
			FW_MODEL("rock_moss_set_01", "rock_moss_set_01_rock05"),
		});
	}

	TArray<UStaticMesh*> Trees()
	{
		// Game-ready Nanite pines from Epic's Project Titan sample, copied locally by Tools/migrate_titan.ps1
		// (Titan's Arctic firs are left out: their needles are tinted icy blue)
		// (not in git); none when that has not been run
		return Meshes({
			TEXT("/Game/Environment/Foliage/Trees/SM_Pine_A.SM_Pine_A"),
			TEXT("/Game/Environment/Foliage/Trees/SM_Pine_B.SM_Pine_B"),
		});
	}

	TArray<UStaticMesh*> TreeLineTrees()
	{
		// Thinner, lichen-covered pines where the forest gives out near the tree line
		return Meshes({
			TEXT("/Game/Environment/Foliage/Trees/SM_Pine_A_Lichen.SM_Pine_A_Lichen"),
			TEXT("/Game/Environment/Foliage/Trees/SM_Pine_B_Lichen.SM_Pine_B_Lichen"),
		});
	}

	TArray<UStaticMesh*> TitanSaplings()
	{
		return Meshes({
			TEXT("/Game/Environment/Foliage/Trees/SM_Sapling_A.SM_Sapling_A"),
			TEXT("/Game/Environment/Foliage/Trees/SM_Sapling_B.SM_Sapling_B"),
		});
	}

	TArray<UStaticMesh*> DwarfPines()
	{
		TArray<UStaticMesh*> Found;
		for (int32 i = 1; i <= 5; ++i)
		{
			const FString Name = FString::Printf(TEXT("mugo_pine_%02d"), i);
			if (UStaticMesh* Shrub = Mesh(*FString::Printf(TEXT("/Game/External/Mugo/%s/%s/StaticMeshes/%s.%s"), *Name, *Name, *Name, *Name)))
			{
				Found.Add(Shrub);
			}
		}
		return Found;
	}

	TArray<UStaticMesh*> Saplings()
	{
		return Meshes({
			FW_MODEL("fir_sapling_medium", "fir_sapling_medium_a_LOD0"), FW_MODEL("fir_sapling_medium", "fir_sapling_medium_b_LOD0"),
			FW_MODEL("fir_sapling_medium", "fir_sapling_medium_c_LOD0"),
		});
	}

	TArray<UStaticMesh*> Bushes()
	{
		return Meshes({
			FW_MODEL("shrub_01", "shrub_01"), FW_MODEL("shrub_02", "shrub_02_a"), FW_MODEL("shrub_02", "shrub_02_b"),
			FW_MODEL("shrub_02", "shrub_02_c"), FW_MODEL("shrub_02", "shrub_02_d"),
		});
	}

	TArray<UStaticMesh*> Grass()
	{
		return Meshes({
			FW_MODEL("grass_medium_01", "grass_medium_01_mid_a_LOD0"), FW_MODEL("grass_medium_01", "grass_medium_01_mid_b_LOD0"),
			FW_MODEL("grass_medium_01", "grass_medium_01_small_a_LOD0"), FW_MODEL("grass_medium_01", "grass_medium_01_tall_a_LOD0"),
		});
	}

	UStaticMesh* Factory(const TCHAR* Name)
	{
		return Mesh(*FString::Printf(TEXT("/Game/External/Factory/%s/%s/StaticMeshes/%s.%s"), Name, Name, Name, Name));
	}

	UStaticMesh* ItemMesh(const FString& Item)
	{
		const TCHAR* Name = TEXT("crate");
		if (Item.StartsWith(TEXT("tool_pickaxe")))
		{
			Name = TEXT("tool_pickaxe");
		}
		else if (Item.Contains(TEXT("belt")) || Item.Contains(TEXT("splitter")))
		{
			Name = TEXT("belt_straight");
		}
		else if (Item.Contains(TEXT("inserter")))
		{
			Name = TEXT("inserter_base");
		}
		else if (Item.Contains(TEXT("chest")) || Item.StartsWith(TEXT("storage_")))
		{
			Name = TEXT("chest");
		}
		else if (Item.Contains(TEXT("pole")) || Item.Contains(TEXT("pylon")) || Item.Contains(TEXT("substation")))
		{
			Name = TEXT("pole");
		}
		else if (Item.Contains(TEXT("solar")))
		{
			Name = TEXT("solar_panel");
		}
		else if (Item.Contains(TEXT("turbine")))
		{
			Name = TEXT("turbine_tower");
		}
		else if (Item.StartsWith(TEXT("furniture_")) || Item.StartsWith(TEXT("power_")))
		{
			Name = TEXT("machine");
		}
		else if (Item.EndsWith(TEXT("_ore")) || Item == TEXT("coal"))
		{
			Name = TEXT("chunk_ore");
		}
		else if (Item == TEXT("stone") || Item == TEXT("gravel") || Item == TEXT("sand"))
		{
			Name = TEXT("chunk_stone");
		}
		else if (Item.EndsWith(TEXT("_ingot")))
		{
			Name = TEXT("ingot");
		}
		return Factory(Name);
	}

	UStaticMesh* Terrain(int32 Biome, int32 PlotX, int32 PlotY)
	{
		const FString Real = FString::Printf(TEXT("terrain_dem_%d_%d"), PlotX, PlotY);
		if (UStaticMesh* Found = Mesh(*FString::Printf(TEXT("/Game/External/Models/%s/%s/StaticMeshes/%s.%s"), *Real, *Real, *Real, *Real)))
		{
			return Found;
		}
		// Mountains get the high range; everything else rolling hills
		return Biome == 6 || Biome == 8
			? Mesh(FW_MODEL("terrain_mountains", "terrain_mountains"))
			: Mesh(FW_MODEL("terrain_hills", "terrain_hills"));
	}

#undef FW_MODEL

	FLayerSet GroundLayers(int32 Biome)
	{
		FLayerSet Set;
		Set.Cliff = TEXT("rock_face_03");
		Set.Snow = TEXT("snow_02");
		switch (Biome)
		{
		case 6: // mountains
			Set.Base = TEXT("rocky_terrain_02");
			Set.Grass = TEXT("aerial_grass_rock");
			Set.GrassAmount = 0.8f;
			Set.SnowHeightCm = 115000.f;
			break;
		case 8: // ice
			Set.Base = TEXT("snow_02");
			Set.Grass = TEXT("gravel_ground_01");
			Set.GrassAmount = 0.2f;
			Set.SnowHeightCm = 0.f;
			break;
		case 5: case 12: // desert, oasis
			Set.Base = TEXT("coast_sand_01");
			Set.Grass = TEXT("dirt");
			Set.GrassAmount = 0.3f;
			break;
		case 2: // forest
			Set.Base = TEXT("forest_ground_04");
			Set.Grass = TEXT("aerial_grass_rock");
			Set.GrassAmount = 0.6f;
			break;
		default:
			Set.Base = TEXT("dirt");
			Set.Grass = TEXT("aerial_grass_rock");
			Set.GrassAmount = 0.8f;
			break;
		}
		return Set;
	}
}
