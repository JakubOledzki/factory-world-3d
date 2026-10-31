#include "World/FWCharacterStudio.h"

#include "Animation/AnimSequence.h"
#include "Camera/CameraComponent.h"
#include "Components/RectLightComponent.h"
#include "Components/SkeletalMeshComponent.h"
#include "Components/SpotLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "Engine/StaticMesh.h"
#include "Materials/MaterialInstanceDynamic.h"

namespace
{
	// The room: 14 x 14 m, 7 m high, the figure in the middle facing +X (towards the camera)
	constexpr float HalfRoom = 700.f;
	constexpr float RoomHeight = 700.f;
	constexpr float PedestalHeight = 10.f;
}

AFWCharacterStudio::AFWCharacterStudio()
{
	PrimaryActorTick.bCanEverTick = true;
	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;
	Turntable = CreateDefaultSubobject<USceneComponent>(TEXT("Turntable"));
	Turntable->SetupAttachment(Root);
	Camera = CreateDefaultSubobject<UCameraComponent>(TEXT("Camera"));
	Camera->SetupAttachment(Root);
	Camera->SetFieldOfView(30.f);
	// A small studio is far darker than daylight: its own exposure, a soft vignette, shallow focus
	FPostProcessSettings& P = Camera->PostProcessSettings;
	P.bOverride_AutoExposureMinBrightness = true;
	P.AutoExposureMinBrightness = 8.f;
	P.bOverride_AutoExposureMaxBrightness = true;
	P.AutoExposureMaxBrightness = 8.f;
	P.bOverride_AutoExposureBias = true;
	P.AutoExposureBias = 0.f;
	P.bOverride_VignetteIntensity = true;
	P.VignetteIntensity = 0.55f;
	P.bOverride_BloomIntensity = true;
	P.BloomIntensity = 0.25f;
	P.bOverride_DepthOfFieldFstop = true;
	P.DepthOfFieldFstop = 2.8f;
	P.bOverride_DepthOfFieldFocalDistance = true;
}

void AFWCharacterStudio::BeginPlay()
{
	Super::BeginPlay();

	UStaticMesh* Plane = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Plane.Plane"));
	UStaticMesh* Cylinder = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cylinder.Cylinder"));
	UMaterialInterface* Surface = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/Materials/M_FWSurface.M_FWSurface"));
	auto Paneled = [this, Surface](UStaticMesh* Mesh, const FTransform& Transform, const FLinearColor& Color, float Roughness)
	{
		UStaticMeshComponent* Part = NewObject<UStaticMeshComponent>(this);
		Part->SetStaticMesh(Mesh);
		Part->SetupAttachment(Root);
		Part->SetRelativeTransform(Transform);
		// The walls must keep the sun out from whichever side it comes
		Part->bCastShadowAsTwoSided = true;
		Part->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		if (Surface)
		{
			UMaterialInstanceDynamic* Material = UMaterialInstanceDynamic::Create(Surface, this);
			Material->SetVectorParameterValue(TEXT("Color"), Color);
			Material->SetScalarParameterValue(TEXT("Roughness"), Roughness);
			Material->SetScalarParameterValue(TEXT("Metallic"), 0.f);
			Part->SetMaterial(0, Material);
		}
		Part->RegisterComponent();
	};
	if (Plane)
	{
		const float Scale = HalfRoom * 2.f / 100.f;
		const FLinearColor Wall(0.022f, 0.022f, 0.024f);
		// Floor, ceiling and four walls, each facing in
		const TPair<FVector, FVector> Sides[] = {
			{ FVector(0, 0, 0), FVector::UpVector },
			{ FVector(0, 0, RoomHeight), FVector::DownVector },
			{ FVector(-HalfRoom, 0, RoomHeight / 2), FVector::ForwardVector },
			{ FVector(HalfRoom, 0, RoomHeight / 2), FVector::BackwardVector },
			{ FVector(0, -HalfRoom, RoomHeight / 2), FVector::RightVector },
			{ FVector(0, HalfRoom, RoomHeight / 2), FVector::LeftVector },
		};
		for (int32 i = 0; i < UE_ARRAY_COUNT(Sides); ++i)
		{
			const FRotator Facing = FRotationMatrix::MakeFromZ(Sides[i].Value).Rotator();
			const bool bFloor = i == 0;
			Paneled(Plane, FTransform(Facing, Sides[i].Key, FVector(Scale, Scale, 1.f)), bFloor ? FLinearColor(0.03f, 0.03f, 0.032f) : Wall, bFloor ? 0.4f : 0.9f);
		}
	}
	if (Cylinder)
	{
		// A low round pedestal, a little glossy
		Paneled(Cylinder, FTransform(FRotator::ZeroRotator, FVector(0, 0, PedestalHeight / 2), FVector(1.7f, 1.7f, PedestalHeight / 100.f)), FLinearColor(0.012f, 0.012f, 0.013f), 0.25f);
	}

	// Three-point lighting: a warm key from the front left, a cool fill from the right, a rim from behind
	auto Rect = [this](const FVector& At, const FVector& LookAt, float Candelas, const FLinearColor& Color, float Width, float Height)
	{
		URectLightComponent* Light = NewObject<URectLightComponent>(this);
		Light->SetupAttachment(Root);
		Light->SetMobility(EComponentMobility::Movable);
		Light->SetRelativeLocation(At);
		Light->SetRelativeRotation((LookAt - At).Rotation());
		Light->SetIntensityUnits(ELightUnits::Candelas);
		Light->SetIntensity(Candelas);
		Light->SetLightColor(Color);
		Light->SetSourceWidth(Width);
		Light->SetSourceHeight(Height);
		Light->SetAttenuationRadius(2000.f);
		Light->RegisterComponent();
	};
	Rect(FVector(260, -240, 300), FVector(0, 0, 130), 2600.f, FLinearColor(1.f, 0.9f, 0.8f), 140.f, 100.f);
	Rect(FVector(280, 300, 150), FVector(0, 0, 110), 700.f, FLinearColor(0.75f, 0.85f, 1.f), 160.f, 160.f);
	Rect(FVector(0, 0, 520), FVector(0, 0, 0), 500.f, FLinearColor(1.f, 0.97f, 0.92f), 200.f, 200.f);
	USpotLightComponent* Rim = NewObject<USpotLightComponent>(this);
	Rim->SetupAttachment(Root);
	Rim->SetMobility(EComponentMobility::Movable);
	Rim->SetRelativeLocation(FVector(-280, 160, 330));
	Rim->SetRelativeRotation((FVector(0, 0, 140) - FVector(-280, 160, 330)).Rotation());
	Rim->SetIntensityUnits(ELightUnits::Candelas);
	Rim->SetIntensity(5000.f);
	Rim->SetLightColor(FLinearColor(0.85f, 0.9f, 1.f));
	Rim->SetOuterConeAngle(28.f);
	Rim->SetInnerConeAngle(16.f);
	Rim->SetAttenuationRadius(1500.f);
	Rim->RegisterComponent();

	// The mannequin, painted per region, breathing in its idle
	USkeletalMesh* Mesh = LoadObject<USkeletalMesh>(nullptr, TEXT("/Game/Characters/Mannequins/Meshes/SKM_Manny_Simple.SKM_Manny_Simple"));
	UMaterialInterface* Character = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/Materials/M_FWCharacter.M_FWCharacter"));
	if (Mesh && Character)
	{
		Figure = NewObject<USkeletalMeshComponent>(this);
		Figure->SetupAttachment(Turntable);
		Figure->SetSkeletalMesh(Mesh);
		// The mesh faces +Y; the room's camera looks from +X
		Figure->SetRelativeLocationAndRotation(FVector(0, 0, PedestalHeight), FRotator(0, -90.f, 0));
		Figure->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		Paint = UMaterialInstanceDynamic::Create(Character, this);
		for (int32 Slot = 0; Slot < Figure->GetNumMaterials(); ++Slot)
		{
			Figure->SetMaterial(Slot, Paint);
		}
		Figure->RegisterComponent();
		if (UAnimSequence* Idle = LoadObject<UAnimSequence>(nullptr, TEXT("/Game/Characters/Mannequins/Anims/Unarmed/MM_Idle.MM_Idle")))
		{
			Figure->PlayAnimation(Idle, true);
		}
	}
	UpdateCamera(0.f);
}

bool AFWCharacterStudio::HasFigure() const
{
	return Figure != nullptr;
}

void AFWCharacterStudio::SetLook(const FFWLook& Look)
{
	if (!Paint)
	{
		return;
	}
	Paint->SetVectorParameterValue(TEXT("Skin"), Look.Skin);
	Paint->SetVectorParameterValue(TEXT("Hair"), Look.Hair);
	Paint->SetVectorParameterValue(TEXT("Eyes"), Look.Eyes);
	Paint->SetVectorParameterValue(TEXT("Shirt"), Look.Shirt);
	Paint->SetVectorParameterValue(TEXT("Pants"), Look.Pants);
	Paint->SetVectorParameterValue(TEXT("Shoes"), Look.Shoes);
	Paint->SetScalarParameterValue(TEXT("HairStyle"), Look.HairStyle);
	Paint->SetScalarParameterValue(TEXT("ShirtStyle"), Look.ShirtStyle);
	Paint->SetScalarParameterValue(TEXT("FacialHair"), Look.FacialHair);
	Paint->SetScalarParameterValue(TEXT("FaceStyle"), Look.FaceStyle);
	// Modelled hair and clothes over the painted body (style 0 of hair and facial hair is none)
	Wear(TEXT("hair"), Look.HairStyle > 0 ? FString::Printf(TEXT("hair_%d"), Look.HairStyle) : FString(), Look.Hair);
	Wear(TEXT("beard"), Look.FacialHair > 0 ? FString::Printf(TEXT("beard_%d"), Look.FacialHair) : FString(), Look.Hair);
	Wear(TEXT("shirt"), FString::Printf(TEXT("shirt_%d"), Look.ShirtStyle), Look.Shirt);
	Wear(TEXT("pants"), TEXT("pants"), Look.Pants);
	Wear(TEXT("shoes"), TEXT("shoes"), Look.Shoes);
}

void AFWCharacterStudio::Wear(const FString& Kind, const FString& Piece, const FLinearColor& Color)
{
	if (!Figure)
	{
		return;
	}
	for (TPair<FString, TObjectPtr<USkeletalMeshComponent>>& Pair : Gear)
	{
		if (Pair.Key.StartsWith(Kind) && Pair.Value)
		{
			Pair.Value->SetVisibility(Pair.Key == Piece);
		}
	}
	if (Piece.IsEmpty())
	{
		return;
	}
	TObjectPtr<USkeletalMeshComponent>* Found = Gear.Find(Piece);
	if (!Found)
	{
		USkeletalMesh* Mesh = LoadObject<USkeletalMesh>(nullptr, *FString::Printf(TEXT("/Game/External/Character/%s.%s"), *Piece, *Piece));
		USkeletalMeshComponent* Part = nullptr;
		if (Mesh)
		{
			Part = NewObject<USkeletalMeshComponent>(this);
			Part->SetupAttachment(Figure);
			Part->SetSkeletalMesh(Mesh);
			Part->SetCollisionEnabled(ECollisionEnabled::NoCollision);
			Part->RegisterComponent();
			Part->SetLeaderPoseComponent(Figure);
			// Each slot is named after its material (FW_Hair, FW_Jersey...): MI_<slot> made by Tools/import_external.py
			const TArray<FName> Slots = Part->GetMaterialSlotNames();
			for (int32 Slot = 0; Slot < Slots.Num(); ++Slot)
			{
				const FString Name = FString::Printf(TEXT("MI_%s"), *Slots[Slot].ToString());
				UMaterialInterface* Base = LoadObject<UMaterialInterface>(nullptr, *FString::Printf(TEXT("/Game/External/Character/Materials/%s.%s"), *Name, *Name));
				Part->SetMaterial(Slot, UMaterialInstanceDynamic::Create(Base ? Base : Part->GetMaterial(Slot), this));
			}
		}
		// Missing pieces (Tools/setup_assets.ps1 not run) are remembered as none: the painted body shows instead
		Found = &Gear.Add(Piece, Part);
	}
	if (USkeletalMeshComponent* Part = *Found)
	{
		Part->SetVisibility(true);
		for (int32 Slot = 0; Slot < Part->GetNumMaterials(); ++Slot)
		{
			if (UMaterialInstanceDynamic* Material = Cast<UMaterialInstanceDynamic>(Part->GetMaterial(Slot)))
			{
				Material->SetVectorParameterValue(TEXT("Color"), Color);
			}
		}
	}
}

void AFWCharacterStudio::Spin(float DeltaYaw)
{
	Yaw += DeltaYaw;
	ManualUntil = FPlatformTime::Seconds() + 4.0;
}

void AFWCharacterStudio::Zoom(float Delta)
{
	ZoomTarget = FMath::Clamp(ZoomTarget + Delta, 0.f, 1.f);
}

void AFWCharacterStudio::SetZoom(float Value)
{
	ZoomTarget = FMath::Clamp(Value, 0.f, 1.f);
}

void AFWCharacterStudio::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	// A slow turntable while the player is not turning it by hand
	if (FPlatformTime::Seconds() > ManualUntil)
	{
		Yaw += 12.f * DeltaSeconds;
	}
	Turntable->SetRelativeRotation(FRotator(0, Yaw, 0));
	UpdateCamera(DeltaSeconds);
}

void AFWCharacterStudio::UpdateCamera(float DeltaSeconds)
{
	ZoomNow = DeltaSeconds > 0.f ? FMath::FInterpTo(ZoomNow, ZoomTarget, DeltaSeconds, 6.f) : ZoomTarget;
	const float Smooth = FMath::SmoothStep(0.f, 1.f, ZoomNow);
	const float Height = FMath::Lerp(98.f, PedestalHeight + 166.f, Smooth);
	const float Distance = FMath::Lerp(760.f, 115.f, Smooth);
	// The menu covers the left of the screen: the figure stands right of the middle
	const float Side = Distance * 0.13f;
	const FVector Target(0, Side, Height);
	const FVector Eye(Distance, Side, Height + FMath::Lerp(18.f, 2.f, Smooth));
	Camera->SetRelativeLocationAndRotation(Eye, (Target - Eye).Rotation());
	Camera->PostProcessSettings.DepthOfFieldFocalDistance = Distance;
}
