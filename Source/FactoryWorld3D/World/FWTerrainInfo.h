#pragma once

#include "CoreMinimal.h"
#include "Engine/AssetUserData.h"
#include "FWTerrainInfo.generated.h"

/**
 * Facts about a generated land mesh that the game needs at runtime. Tools/import_external.py attaches it to
 * terrains built from real elevation data (it reads RawAssets/processed/dem_<x>_<y>.json).
 */
UCLASS(BlueprintType, EditInlineNew)
class FACTORYWORLD3D_API UFWTerrainInfo : public UAssetUserData
{
	GENERATED_BODY()

public:
	// Height of the plot above sea level, in metres; the mesh's Z = 0 is the plot
	UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Terrain")
	float PlotElevationMeters = 0.f;
};
