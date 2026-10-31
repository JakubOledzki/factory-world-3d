#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "FWPlot.generated.h"

class UHierarchicalInstancedStaticMeshComponent;
class UInstancedStaticMeshComponent;
class UMaterialInstanceDynamic;
class UMaterialInterface;
class UStaticMesh;
class UDecalComponent;
class UStaticMeshComponent;

/** One object standing on the plot, with the components that draw it. */
struct FFWPlotObject
{
	FString Id;
	FString Kind;
	FString Name;
	FIntPoint Tile = FIntPoint::ZeroValue;
	int32 Rotation = 0;
	TArray<TObjectPtr<UStaticMeshComponent>> Meshes;
	TArray<TObjectPtr<UDecalComponent>> Decals;
};

struct FFWMine
{
	int32 Id = 0;
	FIntRect Rect;
	float Seconds = 5.f;
	int32 Units = 0;
};

/**
 * A plot in 3D, built from a plot.enter reply: ground tinted by biome, the terrain features from
 * "tiles" as instanced meshes, the objects standing on it, and the items moving on its belts.
 * Tile (x, y) covers [x, x+1) × [y, y+1) tiles from the actor origin; +Y is south.
 * Everything is placeholder art at the scale of UFWGameSettings::TileSizeCm.
 */
UCLASS()
class FACTORYWORLD3D_API AFWPlot : public AActor
{
	GENERATED_BODY()

public:
	AFWPlot();

	virtual void Tick(float DeltaSeconds) override;

	void Build(const TSharedPtr<FJsonObject>& Plot);

	int32 GetPlotX() const { return PlotX; }
	int32 GetPlotY() const { return PlotY; }
	float GetTileSize() const { return TileSize; }
	bool IsBuilt() const { return Size > 0; }
	TOptional<float> GetPlotElevation() const { return PlotElevationMeters; }

	FVector TileCenter(FIntPoint Tile) const;
	// Centre of the square an object of Kind covers when standing on Tile.
	FVector FootprintCenter(const FString& Kind, FIntPoint Tile) const;
	FIntPoint TileAt(const FVector& WorldLocation) const;
	bool Contains(FIntPoint Tile) const { return Tile.X >= 0 && Tile.Y >= 0 && Tile.X < Size && Tile.Y < Size; }
	uint8 TileCode(FIntPoint Tile) const;
	// Where the player stands on arrival: the top of the ground at the spawn tile
	FVector SpawnLocation() const;
	FVector GroundAt(FIntPoint Tile) const;

	const FFWMine* FindMine(FIntPoint Tile) const;
	const FFWPlotObject* FindObjectAt(FIntPoint Tile) const;
	bool IsPlaceable(const FString& Kind) const { return Placeable.Contains(Kind); }
	const TMap<FString, FFWPlotObject>& GetObjects() const { return Objects; }
	// Whether Kind fits on Tile as far as the client knows (the server has the last word).
	bool CanPlace(const FString& Kind, FIntPoint Tile) const;

	void AddObject(const TSharedPtr<FJsonObject>& Object);
	void RemoveObject(const FString& Id);

	// Applies a logistics.get reply: what lies on the belts and what the inserters hold.
	void ApplyLogistics(const TSharedPtr<FJsonObject>& State);
	bool HasLogistics() const { return Belts.Num() > 0 || Inserters.Num() > 0; }

	// Makes a component for a static mesh with a placeholder material, attached to the plot.
	UStaticMeshComponent* MakeMesh(UStaticMesh* Mesh, const FLinearColor& Color, const FTransform& Transform, bool bCollide = true);
	UMaterialInterface* SurfaceMaterial(const FLinearColor& Color, float Roughness = 0.6f, float Metallic = 0.f);

	UPROPERTY()
	TObjectPtr<UStaticMesh> CubeMesh;
	UPROPERTY()
	TObjectPtr<UStaticMesh> CylinderMesh;
	UPROPERTY()
	TObjectPtr<UStaticMesh> SphereMesh;
	UPROPERTY()
	TObjectPtr<UStaticMesh> ConeMesh;

private:
	struct FBeltLink
	{
		FIntPoint To = FIntPoint::ZeroValue;
		int32 Into[2] = { 0, 1 };
		int32 Entry = 0;
		bool bValid = false;
	};

	struct FBelt
	{
		FString Id;
		int32 Rotation = 0;
		int32 Speed = 1;
		int32 Bend = 0;
		TArray<FString> Items;
		TMap<int32, int32> Moved;
		FBeltLink Out;
	};

	struct FInserter
	{
		FString Id;
		int32 Rotation = 0;
		FString Hand;
		int32 Swing = 0;
		int32 Was = 0;
		int32 SwingTicks = 4;
		float Speed = 1.f;
	};

	void BuildGround(int32 Biome);
	UMaterialInterface* MakeGroundMaterial(int32 Biome);
	void ScatterSurroundings(int32 Biome);
	// Conifer forest on the slopes around the plot: thick in the valleys, thinning to the tree line
	void PlantForest(int32 Biome);
	// Dwarf mountain pine thickets in a belt above the forest, and clumps in its clearings
	void PlantDwarfPine(int32 Biome);
	// Clear strip between the plot and the forest (cm), left for building
	static constexpr float ForestMargin = 4000.f;
	void UpdateGroundCover();
	UHierarchicalInstancedStaticMeshComponent* MakeArtInstances(UStaticMesh* Mesh, bool bCollide);
	FTransform ArtTransform(UStaticMesh* Mesh, const FVector& Ground, float Yaw, float Size2D, float Height, float Sink,
		const FVector& Normal = FVector::UpVector, float Tilt = 0.f) const;
	void BuildTerrain(const TArray<uint8>& Codes, const TSet<uint8>& TreeCodes);
	UHierarchicalInstancedStaticMeshComponent* MakeInstances(UStaticMesh* Mesh, const FLinearColor& Color, float Roughness, bool bCollide);
	void DrawObject(FFWPlotObject& Object);
	// The modelled version of an object, when the factory models are set up; false to draw a placeholder
	bool DrawArtObject(FFWPlotObject& Object);
	// Oil stains under belts and inserters, leaks down the sides of chests and machines
	void AddWear(FFWPlotObject& Object);
	UStaticMeshComponent* MakeArtMesh(UStaticMesh* Mesh, const FTransform& Transform, bool bCollide);
	// Belts whose bend changed get the curved model (or the straight one back)
	void RefreshBeltArt();
	void AnimateParts(float DeltaSeconds);
	// Sagging cables from each pole to the poles and powered objects in its reach
	void RebuildCables();
	void RebuildBelts();
	FBeltLink Connect(FIntPoint Tile, FIntPoint Way) const;
	int32 BendOf(FIntPoint Tile) const;
	void AddDownstream(FIntPoint Tile, TSet<FIntPoint>& Seen);
	void StepLogistics();
	bool PassOn(FBelt& Belt, int32 Lane, const FString& Item, int32 Moved);
	double ServerNow() const;
	// Point on the belt at Tile, Along its length (0 in, 1 out) and Lateral tiles to the left, in world space.
	FVector BeltPoint(FIntPoint Tile, const FBelt& Belt, float Along, float Lateral) const;
	void DrawItems();
	UInstancedStaticMeshComponent* ItemInstances(const FString& Item);

	int32 PlotX = 0;
	int32 PlotY = 0;
	int32 Size = 0;
	float TileSize = 400.f;
	TArray<uint8> Tiles;
	FIntPoint Spawn = FIntPoint::ZeroValue;
	TSet<FString> Placeable;
	TArray<FFWMine> Mines;
	TMap<FString, FFWPlotObject> Objects;
	TMap<FIntPoint, FString> Occupied;

	TMap<FIntPoint, FBelt> Belts;
	TMap<FIntPoint, FInserter> Inserters;
	TArray<FIntPoint> BeltOrder;
	int32 BeltSlots = 4;
	int32 BeltLanes = 2;
	double TickMs = 200.0;
	double SimTime = 0.0;
	double ServerAtSync = 0.0;
	double LocalAtSync = 0.0;

	UPROPERTY()
	TObjectPtr<UMaterialInterface> SurfaceBase;
	UPROPERTY()
	TObjectPtr<UMaterialInterface> GroundBase;
	UPROPERTY()
	TObjectPtr<UMaterialInterface> TerrainBase;
	UPROPERTY()
	TObjectPtr<UMaterialInterface> DecalBase;
	UPROPERTY()
	TObjectPtr<UStaticMeshComponent> TerrainMesh;
	int32 PendingScatterBiome = -1;
	// Plot height above sea level when the land is real terrain (FWTerrainInfo on the mesh, or the config); unset otherwise
	TOptional<float> PlotElevationMeters;
	UPROPERTY()
	TObjectPtr<UMaterialInstanceDynamic> GroundMaterial;
	UPROPERTY()
	TMap<FString, TObjectPtr<UStaticMeshComponent>> InserterArms;
	UPROPERTY()
	TArray<TObjectPtr<UStaticMeshComponent>> TurbineRotors;
	TMap<FString, int32> DrawnBends;
	UPROPERTY()
	TObjectPtr<UInstancedStaticMeshComponent> Cables;
	UPROPERTY()
	TArray<TObjectPtr<UStaticMesh>> GroundCoverMeshes;
	UPROPERTY()
	TArray<TObjectPtr<UHierarchicalInstancedStaticMeshComponent>> GroundCoverParts;
	FVector GroundCoverCentre = FVector(1e9);
	UPROPERTY()
	TMap<uint32, TObjectPtr<UMaterialInstanceDynamic>> Materials;
	UPROPERTY()
	TMap<FString, TObjectPtr<UInstancedStaticMeshComponent>> ItemMeshes;
};
