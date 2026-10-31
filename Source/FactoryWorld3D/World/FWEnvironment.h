#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "FWEnvironment.generated.h"

class UDirectionalLightComponent;
class UExponentialHeightFogComponent;
class UPostProcessComponent;
class USkyAtmosphereComponent;
class USkyLightComponent;
class UStaticMeshComponent;
class UVolumetricCloudComponent;

/**
 * Physically based outdoor lighting in one actor: sun, sky atmosphere, real-time
 * captured sky light, volumetric clouds and height fog, plus a ground plane.
 * The game mode spawns one when the level has none.
 */
UCLASS()
class FACTORYWORLD3D_API AFWEnvironment : public AActor
{
	GENERATED_BODY()

public:
	AFWEnvironment();

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<UDirectionalLightComponent> Sun;

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<USkyAtmosphereComponent> SkyAtmosphere;

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<USkyLightComponent> SkyLight;

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<UVolumetricCloudComponent> Clouds;

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<UExponentialHeightFogComponent> Fog;

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<UStaticMeshComponent> Ground;

	UPROPERTY(VisibleAnywhere, Category = "Environment")
	TObjectPtr<UPostProcessComponent> Post;
};
