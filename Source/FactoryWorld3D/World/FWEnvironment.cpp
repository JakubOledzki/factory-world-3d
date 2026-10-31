#include "World/FWEnvironment.h"

#include "Components/DirectionalLightComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Components/VolumetricCloudComponent.h"
#include "Engine/StaticMesh.h"
#include "Materials/MaterialInterface.h"
#include "UObject/ConstructorHelpers.h"

AFWEnvironment::AFWEnvironment()
{
	PrimaryActorTick.bCanEverTick = false;

	USceneComponent* Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;

	// Late-morning sun; intensity in lux, as the sky atmosphere expects
	Sun = CreateDefaultSubobject<UDirectionalLightComponent>(TEXT("Sun"));
	Sun->SetupAttachment(Root);
	Sun->SetMobility(EComponentMobility::Movable);
	Sun->SetRelativeRotation(FRotator(-38.f, -35.f, 0.f));
	Sun->SetIntensity(10.f);
	Sun->SetLightSourceAngle(0.5357f);
	Sun->SetAtmosphereSunLight(true);
	Sun->bCastCloudShadows = true;
	Sun->SetDynamicShadowCascades(4);

	SkyAtmosphere = CreateDefaultSubobject<USkyAtmosphereComponent>(TEXT("SkyAtmosphere"));
	SkyAtmosphere->SetupAttachment(Root);

	SkyLight = CreateDefaultSubobject<USkyLightComponent>(TEXT("SkyLight"));
	SkyLight->SetupAttachment(Root);
	SkyLight->SetMobility(EComponentMobility::Movable);
	SkyLight->bRealTimeCapture = true;
	SkyLight->SetIntensity(1.f);

	Clouds = CreateDefaultSubobject<UVolumetricCloudComponent>(TEXT("Clouds"));
	Clouds->SetupAttachment(Root);
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> CloudMaterial(
		TEXT("/Engine/EngineSky/VolumetricClouds/m_SimpleVolumetricCloud_Inst.m_SimpleVolumetricCloud_Inst"));
	if (CloudMaterial.Succeeded())
	{
		Clouds->SetMaterial(CloudMaterial.Object);
	}

	Fog = CreateDefaultSubobject<UExponentialHeightFogComponent>(TEXT("Fog"));
	Fog->SetupAttachment(Root);
	Fog->SetFogDensity(0.01f);
	Fog->SetFogHeightFalloff(0.2f);
	Fog->SetVolumetricFog(true);
	Fog->SetVolumetricFogExtinctionScale(0.6f);

	// 2 km x 2 km test ground
	Ground = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("Ground"));
	Ground->SetupAttachment(Root);
	Ground->SetRelativeScale3D(FVector(2000.f, 2000.f, 1.f));
	static ConstructorHelpers::FObjectFinder<UStaticMesh> Plane(TEXT("/Engine/BasicShapes/Plane.Plane"));
	if (Plane.Succeeded())
	{
		Ground->SetStaticMesh(Plane.Object);
	}
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> GroundMaterial(
		TEXT("/Engine/EngineMaterials/DefaultMaterial.DefaultMaterial"));
	if (GroundMaterial.Succeeded())
	{
		Ground->SetMaterial(0, GroundMaterial.Object);
	}
}
