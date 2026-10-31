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
			FW_MODEL("namaqualand_boulder_02", "namaqualand_boulder_02"),
			FW_MODEL("namaqualand_boulder_04", "namaqualand_boulder_04"),
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
		// Poly Haven's fir and pine are made for offline rendering: their needles are tiny geometry and cards
		// that vanish (or turn into dark triangles) at game distances, so the forest reads as dead trees.
		// Until game-ready trees with card LODs are in (see ASSETS.md), plots and their surroundings get none.
		return {};
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
			FW_MODEL("fern_02", "fern_02_a"), FW_MODEL("fern_02", "fern_02_b"),
		});
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
