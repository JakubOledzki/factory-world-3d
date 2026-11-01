#pragma once

#include "CoreMinimal.h"

class UStaticMesh;
class UTexture2D;

/**
 * The photographed CC0 art in Content/External (made by Tools/setup_assets.ps1, not in git).
 * Every getter returns nothing when the art is not set up, and the game then draws placeholders.
 */
namespace FWArt
{
	UStaticMesh* Mesh(const TCHAR* Path);
	UTexture2D* Texture(const TCHAR* Path);
	// The meshes that exist among Paths
	TArray<UStaticMesh*> Meshes(std::initializer_list<const TCHAR*> Paths);

	TArray<UStaticMesh*> Rocks();
	TArray<UStaticMesh*> Trees();
	TArray<UStaticMesh*> Saplings();
	TArray<UStaticMesh*> TreeLineTrees();
	TArray<UStaticMesh*> TitanSaplings();
	// Dwarf mountain pine (kosodrzewina) from Tools/blender/make_mugo_pine.py
	TArray<UStaticMesh*> DwarfPines();
	TArray<UStaticMesh*> Bushes();
	TArray<UStaticMesh*> Grass();
	// A factory model made by Tools/blender/make_factory.py (belt_straight, inserter_arm, ...), real size for a 4 m tile
	UStaticMesh* Factory(const TCHAR* Name);
	// The workshop model of a piece of furniture (workbench, furnace, sawmill...), null when it has none of its own;
	// Tiles is the footprint it was built for (it is scaled to the object's own)
	UStaticMesh* FurnitureModel(const FString& Kind, int32& Tiles);
	// The model that shows an item (hotbar icon, held in the hand): what it builds, the tool, or the goods
	UStaticMesh* ItemMesh(const FString& Item);
	// Real elevation around this plot (Tools/dem_extract.mjs) when made, otherwise a made-up land for the biome
	UStaticMesh* Terrain(int32 Biome, int32 PlotX, int32 PlotY);

	// Ground texture sets per biome, by layer: Base, Grass, Cliff, Snow; each D (colour), N (normal), M (AO/rough/metal)
	struct FLayerSet
	{
		FString Base, Grass, Cliff, Snow;
		float GrassAmount = 0.5f;
		float SnowHeightCm = 1e7f;
	};
	FLayerSet GroundLayers(int32 Biome);
}
