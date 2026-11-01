#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "FWGrowth.generated.h"

class AFWPlayerController;
class AFWPlot;
class UHierarchicalInstancedStaticMeshComponent;
class UStaticMesh;

/**
 * What grows on the plot by itself, drawn as it grows, as the 2D game draws it: the fields of the farmsteads with their
 * crops (farmstead.get: young and green while growing, ripe when ready to reap) on tilled soil, and the trees the
 * foresters plant (forestry.get: saplings that grow into trees ready to cut). Asked for every few seconds while the
 * player is on the plot.
 */
UCLASS()
class FACTORYWORLD3D_API AFWGrowth : public AActor
{
	GENERATED_BODY()

public:
	AFWGrowth();

	void Init(AFWPlayerController* InController, AFWPlot* InPlot);
	AFWPlot* GetPlot() const { return Plot.Get(); }

	virtual void Tick(float DeltaSeconds) override;

	// The crop meshes from Tools/blender/make_crops.py: "soil", or a crop by its id ripe or growing
	static UStaticMesh* CropMesh(const FString& Crop, bool bRipe);

private:
	struct FGrowing
	{
		FIntPoint Tile;
		FString Kind;
		double ReadyAt = 0.0;
		double GrowSeconds = 1.0;
	};

	void Poll();
	void ApplyFields(const TSharedPtr<FJsonObject>& Payload);
	void ApplyTrees(const TSharedPtr<FJsonObject>& Payload);
	void Redraw();
	UHierarchicalInstancedStaticMeshComponent* Instances(UStaticMesh* Mesh);

	TWeakObjectPtr<AFWPlayerController> Controller;
	TWeakObjectPtr<AFWPlot> Plot;
	TArray<FGrowing> Fields;
	TArray<FGrowing> Trees;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	double NextRedraw = 0.0;
	bool bChanged = false;

	UPROPERTY()
	TMap<TObjectPtr<UStaticMesh>, TObjectPtr<UHierarchicalInstancedStaticMeshComponent>> Components;
};
