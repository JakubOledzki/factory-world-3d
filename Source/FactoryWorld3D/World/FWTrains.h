#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "FWTrains.generated.h"

class AFWPlayerController;
class AFWPlot;
class UStaticMeshComponent;

/**
 * Trains on the track across the plot, as the 2D game draws them (plot_trains.gd), from what the station knows
 * (station.get): a train coming to the station rolls in from the east and slows down to stop at the platform when it
 * arrives, stands a moment while it is unloaded and leaves to the west; a train sent from here stands at the platform
 * while it is loaded and pulls out to the west. A bigger load comes with more wagons. One train on the track at a time.
 * The models come from Tools/blender/make_trains.py.
 */
UCLASS()
class FACTORYWORLD3D_API AFWTrains : public AActor
{
	GENERATED_BODY()

public:
	AFWTrains();

	void Init(AFWPlayerController* InController, AFWPlot* InPlot);
	AFWPlot* GetPlot() const { return Plot.Get(); }

	virtual void Tick(float DeltaSeconds) override;

	// For the tests: a train that arrives in Seconds with Quantity goods
	void Expect(float Seconds, int32 Quantity);

private:
	struct FTrain
	{
		FString Key;
		double Start = 0.0;
		double StopAt = 0.0;
		double LeaveAt = 0.0;
		int32 Quantity = 0;
		int32 Wagons = 1;
		uint32 Seed = 0;
	};

	void FindTrack();
	void Poll();
	void Apply(const TSharedPtr<FJsonObject>& Station);
	void Build(const FTrain& Train);
	void Clear();
	double ServerNow() const;
	bool Busy() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	TWeakObjectPtr<AFWPlot> Plot;
	TArray<FTrain> Trains;
	// The train on the track and its cars, each with its offset (cm) from the train's middle
	FString Shown;
	TArray<TPair<TObjectPtr<UStaticMeshComponent>, float>> Cars;
	// The track: its row, where trains stop (the middle of the platform), in the plot's own coordinates (cm)
	int32 RailRow = -1;
	float StopX = 0.f;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;

	UPROPERTY()
	TArray<TObjectPtr<UStaticMeshComponent>> Parts;
};
