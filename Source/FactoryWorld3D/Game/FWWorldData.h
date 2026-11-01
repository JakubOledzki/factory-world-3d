#pragma once

#include "CoreMinimal.h"

/**
 * The world's plots as the 2D game knows them (its client/world files, copied to Content/External/WorldData by
 * Tools/build_world_map.mjs): the biome and country of every 5 × 5 km plot, and the towns. Loaded once, on first use;
 * empty when the files are not there.
 */
namespace FWWorldData
{
	struct FTown
	{
		FIntPoint Plot;
		int32 Population = 0;
		FString Name;
	};

	bool IsLoaded();
	FIntPoint Size();
	// Biome id as on the server (0 ocean ... 12 oasis), and the country's Polish name ("" at sea)
	int32 Biome(FIntPoint Plot);
	FString Country(FIntPoint Plot);
	// Towns largest first
	const TArray<FTown>& Towns();
	// The biome's Polish name
	FText BiomeName(int32 Biome);
	// Latitude and longitude of a plot's middle, as text (e.g. "49,2° N, 20,0° E")
	FText Geo(FIntPoint Plot);
}
