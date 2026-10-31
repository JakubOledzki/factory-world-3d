#pragma once

#include "CoreMinimal.h"
#include "UObject/Object.h"
#include "FWGameSettings.generated.h"

/** Client settings from DefaultGame.ini, [/Script/FactoryWorld3D.FWGameSettings]. */
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
};
