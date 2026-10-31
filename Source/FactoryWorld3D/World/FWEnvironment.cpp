#include "World/FWEnvironment.h"

#include "Components/DirectionalLightComponent.h"
#include "Components/ExponentialHeightFogComponent.h"
#include "Components/SkyAtmosphereComponent.h"
#include "Components/SkyLightComponent.h"
#include "Components/PostProcessComponent.h"
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

	// Afternoon sun low enough to rake across the ground; physical units (lux), with exposure to match
	Sun = CreateDefaultSubobject<UDirectionalLightComponent>(TEXT("Sun"));
	Sun->SetupAttachment(Root);
	Sun->SetMobility(EComponentMobility::Movable);
	Sun->SetRelativeRotation(FRotator(-27.f, -128.f, 0.f));
	Sun->SetIntensity(75000.f);
	Sun->SetTemperature(5600.f);
	Sun->bUseTemperature = true;
	Sun->SetLightSourceAngle(0.5357f);
	Sun->SetAtmosphereSunLight(true);
	Sun->bCastCloudShadows = true;
	// The cloud shadow map costs ~1.9 ms at its default 512 px over 150 km. A quarter of the texels over 60 km
	// is ~0.6 ms and still finer on the ground (234 m per texel instead of 293 m); the land ends ~10 km out
	Sun->CloudShadowMapResolutionScale = 0.5f;
	Sun->CloudShadowExtent = 60.f;
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
	// Thin haze over valleys; the land rises over a kilometre, so it falls off slowly
	Fog->SetFogDensity(0.0035f);
	Fog->SetFogHeightFalloff(0.035f);
	Fog->SetStartDistance(3000.f);
	Fog->SetVolumetricFog(true);
	Fog->SetVolumetricFogExtinctionScale(0.6f);

	// Film-like image: exposure in a narrow daylight range, filmic curve, restrained bloom and vignette
	Post = CreateDefaultSubobject<UPostProcessComponent>(TEXT("Post"));
	Post->SetupAttachment(Root);
	Post->bUnbound = true;
	FPostProcessSettings& P = Post->Settings;
	P.bOverride_AutoExposureMethod = true;
	P.AutoExposureMethod = AEM_Histogram;
	P.bOverride_AutoExposureMinBrightness = true;
	P.AutoExposureMinBrightness = 12.5f;
	P.bOverride_AutoExposureMaxBrightness = true;
	P.AutoExposureMaxBrightness = 14.8f;
	P.bOverride_AutoExposureBias = true;
	P.AutoExposureBias = 0.3f;
	P.bOverride_AutoExposureSpeedUp = true;
	P.AutoExposureSpeedUp = 2.f;
	P.bOverride_AutoExposureSpeedDown = true;
	P.AutoExposureSpeedDown = 1.f;
	P.bOverride_BloomIntensity = true;
	P.BloomIntensity = 0.35f;
	P.bOverride_VignetteIntensity = true;
	P.VignetteIntensity = 0.25f;
	P.bOverride_ColorSaturation = true;
	P.ColorSaturation = FVector4(0.95f, 0.95f, 0.95f, 1.f);
	P.bOverride_ColorContrast = true;
	P.ColorContrast = FVector4(1.06f, 1.06f, 1.06f, 1.f);
	P.bOverride_AmbientOcclusionIntensity = true;
	P.AmbientOcclusionIntensity = 0.6f;
	P.bOverride_LumenFinalGatherQuality = true;
	P.LumenFinalGatherQuality = 2.f;
	P.bOverride_LumenSceneLightingQuality = true;
	P.LumenSceneLightingQuality = 2.f;
	P.bOverride_LumenReflectionQuality = true;
	P.LumenReflectionQuality = 2.f;
	P.bOverride_FilmGrainIntensity = true;
	P.FilmGrainIntensity = 0.04f;

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
