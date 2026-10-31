#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "FWGameSettings.generated.h"

/** Client settings from DefaultGame.ini, [/Script/FactoryWorld3D.FWGameSettings]. */
// Height above sea level of a plot whose surroundings are built from real elevation data
USTRUCT()
struct FFWPlotElevation
{
	GENERATED_BODY()

	UPROPERTY(Config)
	int32 X = 0;

	UPROPERTY(Config)
	int32 Y = 0;

	UPROPERTY(Config)
	float Meters = 0.f;
};

UCLASS(Config = Game)
class FACTORYWORLD3D_API UFWGameSettings : public UObject
{
	GENERATED_BODY()

public:
	static const UFWGameSettings& Get() { return *GetDefault<UFWGameSettings>(); }

	// How wide one plot tile is drawn, in cm. The server's tiles are 20 m, which is far too big to
	// walk among in first person, so the whole plot is drawn at this scale instead.
	UPROPERTY(Config)
	float TileSizeCm = 400.f;

	// Where a player without a plot is offered one (world grid, in plots); the nearest free plot is picked.
	UPROPERTY(Config)
	int32 StarterPlotX = 3852;

	UPROPERTY(Config)
	int32 StarterPlotY = 357;

	// How often belts and inserters are fetched from the server.
	UPROPERTY(Config)
	float LogisticsPollSeconds = 1.f;

	// Plots with real terrain (Tools/dem_extract.mjs prints the value): the forest then follows the real tree
	// line by altitude above sea level instead of height above the plot
	UPROPERTY(Config)
	TArray<FFWPlotElevation> PlotElevations;

	// Tree line in metres above sea level (Tatras: spruce up to ~1550 m, dwarf pine and grass above)
	UPROPERTY(Config)
	float ForestTopMeters = 1300.f;

	UPROPERTY(Config)
	float TreeLineMeters = 1580.f;

	const FFWPlotElevation* FindElevation(int32 PlotX, int32 PlotY) const
	{
		return PlotElevations.FindByPredicate([&](const FFWPlotElevation& E) { return E.X == PlotX && E.Y == PlotY; });
	}
};
