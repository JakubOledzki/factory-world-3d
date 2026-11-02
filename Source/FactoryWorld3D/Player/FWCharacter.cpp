#include "Player/FWCharacter.h"

#include "Camera/CameraComponent.h"
#include "Animation/AnimSequence.h"
#include "Components/CapsuleComponent.h"
#include "Components/DecalComponent.h"
#include "Components/PoseableMeshComponent.h"
#include "Engine/StaticMeshActor.h"
#include "Game/FWArt.h"
#include "Kismet/GameplayStatics.h"
#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "TwoBoneIK.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "EnhancedInputComponent.h"
#include "EnhancedInputSubsystems.h"
#include "Engine/LocalPlayer.h"
#include "Engine/StaticMesh.h"
#include "FWPlayerController.h"
#include "Game/FWAudio.h"
#include "Game/FWCatalog.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "InputAction.h"
#include "InputMappingContext.h"
#include "InputModifiers.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "UObject/ConstructorHelpers.h"
#include "World/FWPlot.h"

#define LOCTEXT_NAMESPACE "FWCharacter"

#include "FWCharacterHelpers.h"

AFWCharacter::AFWCharacter()
{
	PrimaryActorTick.bCanEverTick = true;
	GetCapsuleComponent()->InitCapsuleSize(38.f, 92.f);
	bUseControllerRotationYaw = true;

	Camera = CreateDefaultSubobject<UCameraComponent>(TEXT("Camera"));
	Camera->SetupAttachment(GetCapsuleComponent());
	Camera->bUsePawnControlRotation = true;
	Camera->SetFieldOfView(90.f);
	// The arms are drawn with their own narrower field of view and pulled towards the eye, so they keep their
	// shape at the edges of the screen and never sink into a wall
	Camera->bEnableFirstPersonFieldOfView = true;
	Camera->FirstPersonFieldOfView = 70.f;
	Camera->bEnableFirstPersonScale = true;
	Camera->FirstPersonScale = 0.3f;

	// A person, not a tile: eyes ~165 cm above the feet whatever TileSizeCm is
	Camera->SetRelativeLocation(FVector(0.f, 0.f, EyeHeight - 92.f));

	// Grounded and snappy: stronger gravity, a short jump, quick start and stop without gliding
	UCharacterMovementComponent* Movement = GetCharacterMovement();
	Movement->SetMovementMode(MOVE_Walking);
	Movement->DefaultLandMovementMode = MOVE_Walking;
	Movement->GravityScale = 1.6f;
	Movement->MaxWalkSpeed = WalkSpeed;
	Movement->MaxAcceleration = 2400.f;
	Movement->GroundFriction = 10.f;
	Movement->BrakingDecelerationWalking = 3000.f;
	Movement->BrakingFrictionFactor = 1.f;
	Movement->JumpZVelocity = 520.f;
	Movement->AirControl = 0.15f;
	Movement->FallingLateralFriction = 0.5f;
	Movement->MaxStepHeight = 50.f;
	Movement->SetWalkableFloorAngle(50.f);

	static ConstructorHelpers::FObjectFinder<UStaticMesh> Cube(TEXT("/Engine/BasicShapes/Cube.Cube"));
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> GhostBase(TEXT("/Game/Materials/M_FWGhost.M_FWGhost"));
	Ghost = CreateDefaultSubobject<UStaticMeshComponent>(TEXT("Ghost"));
	Ghost->SetupAttachment(GetCapsuleComponent());
	Ghost->SetUsingAbsoluteLocation(true);
	Ghost->SetUsingAbsoluteRotation(true);
	Ghost->SetUsingAbsoluteScale(true);
	Ghost->SetStaticMesh(Cube.Object);
	Ghost->SetMaterial(0, GhostBase.Object);
	Ghost->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Ghost->SetCastShadow(false);
	Ghost->SetVisibility(false);
}

void AFWCharacter::BeginPlay()
{
	Super::BeginPlay();
	GhostMaterial = Ghost->CreateDynamicMaterialInstance(0);
	CreateBody();
}

void AFWCharacter::CreateBody()
{
	USkeletalMesh* Mannequin = LoadObject<USkeletalMesh>(nullptr, TEXT("/Game/Characters/Mannequins/Meshes/SKM_Manny_Simple.SKM_Manny_Simple"), nullptr, LOAD_NoWarn | LOAD_Quiet);
	if (!Mannequin || !IsLocallyControlled())
	{
		return;
	}
	// The whole body: not seen by the player, only in its shadow (and by others, later)
	Body = NewObject<USkeletalMeshComponent>(this);
	Body->SetupAttachment(GetCapsuleComponent());
	Body->SetSkeletalMesh(Mannequin);
	Body->SetRelativeLocationAndRotation(FVector(0, 0, -GetCapsuleComponent()->GetUnscaledCapsuleHalfHeight()), FRotator(0, -90.f, 0));
	Body->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Body->SetOwnerNoSee(true);
	Body->bCastHiddenShadow = true;
	Body->RegisterComponent();

	// The arms: the same mannequin drawn only for the player, in first person, its body cut away by the
	// "...Arms" materials; it casts no shadow (the body does)
	Arms = NewObject<UPoseableMeshComponent>(this);
	Arms->SetupAttachment(Camera);
	Arms->SetSkinnedAssetAndUpdate(Mannequin);
	// The mesh faces +Y: turned to look along the camera, its eyes (~0, 8, 168 in the bind pose) on the camera
	const FRotator Turn(0, -90.f, 0);
	Arms->SetRelativeLocationAndRotation(-Turn.RotateVector(FVector(0.f, 8.f, 168.f)), Turn);
	Arms->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Arms->SetOnlyOwnerSee(true);
	Arms->SetCastShadow(false);
	Arms->FirstPersonPrimitiveType = EFirstPersonPrimitiveType::FirstPerson;
	Arms->RegisterComponent();
	// The bind pose of both arms, to solve from every frame
	const TCHAR* Sides[2][3] = { { TEXT("upperarm_l"), TEXT("lowerarm_l"), TEXT("hand_l") }, { TEXT("upperarm_r"), TEXT("lowerarm_r"), TEXT("hand_r") } };
	for (int32 Side = 0; Side < 2; ++Side)
	{
		ArmRef[Side].Upper = Arms->GetBoneTransformByName(Sides[Side][0], EBoneSpaces::ComponentSpace);
		ArmRef[Side].Lower = Arms->GetBoneTransformByName(Sides[Side][1], EBoneSpaces::ComponentSpace);
		ArmRef[Side].Hand = Arms->GetBoneTransformByName(Sides[Side][2], EBoneSpaces::ComponentSpace);
	}
	bArmsReady = true;
	SetLook(FFWLook());

	// The pickaxe and the axe (Tools/blender/make_tools.py): in the right hand while striking
	PickaxeMesh = FWArt::Factory(TEXT("tool_pickaxe"));
	AxeMesh = FWArt::Factory(TEXT("tool_axe"));
	if (PickaxeMesh)
	{
		Tool = NewObject<UStaticMeshComponent>(this);
		Tool->SetStaticMesh(PickaxeMesh);
		Tool->SetupAttachment(Arms);
		// Its grip in the palm, the head forward over the knuckles (the tool's +Z handle, +X head)
		Tool->SetRelativeLocationAndRotation(FVector(-9.f, 1.5f, -3.f), FRotator(0.f, 0.f, 0.f));
		// The import already turned metres into centimetres
		Tool->SetRelativeScale3D(FVector(1.f));
		Tool->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		Tool->SetOnlyOwnerSee(true);
		Tool->SetCastShadow(false);
		Tool->FirstPersonPrimitiveType = EFirstPersonPrimitiveType::FirstPerson;
		Tool->SetVisibility(false);
		Tool->RegisterComponent();
	}
	Held = NewObject<UStaticMeshComponent>(this);
	Held->SetupAttachment(Arms);
	Held->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Held->SetOnlyOwnerSee(true);
	Held->SetCastShadow(false);
	Held->FirstPersonPrimitiveType = EFirstPersonPrimitiveType::FirstPerson;
	Held->SetVisibility(false);
	// The models are Nanite meshes, which the first-person pass drew black: their ordinary meshes instead
	Held->bDisallowNanite = true;
	Held->RegisterComponent();
}

void AFWCharacter::ApplyUserSettings(float InMouseSensitivity, float FieldOfView)
{
	MouseSensitivity = InMouseSensitivity;
	Camera->SetFieldOfView(FieldOfView);
}

void AFWCharacter::SetLook(const FFWLook& Look)
{
	FWGear::Dress(this, Body, BodyGear, Look);
	FWGear::Dress(this, Arms, ArmsGear, Look, true);
}

void AFWCharacter::FWDress(int32 ShirtStyle, const FString& ShirtColor)
{
	FFWLook Look;
	Look.Skin = FLinearColor::FromSRGBColor(FColor::FromHex(TEXT("d9a07a")));
	Look.Shirt = FLinearColor::FromSRGBColor(FColor::FromHex(ShirtColor));
	Look.Pants = FLinearColor::FromSRGBColor(FColor::FromHex(TEXT("34495e")));
	Look.Shoes = FLinearColor::FromSRGBColor(FColor::FromHex(TEXT("7f5539")));
	Look.ShirtStyle = ShirtStyle;
	SetLook(Look);
}

AFWPlayerController* AFWCharacter::GetFWController() const
{
	return Cast<AFWPlayerController>(GetController());
}

AFWPlot* AFWCharacter::GetPlot() const
{
	const AFWPlayerController* PC = GetFWController();
	return PC ? PC->GetPlot() : nullptr;
}

void AFWCharacter::PawnClientRestart()
{
	Super::PawnClientRestart();
	CreateInput();
	if (const APlayerController* PC = Cast<APlayerController>(GetController()))
	{
		if (UEnhancedInputLocalPlayerSubsystem* Input = ULocalPlayer::GetSubsystem<UEnhancedInputLocalPlayerSubsystem>(PC->GetLocalPlayer()))
		{
			Input->ClearAllMappings();
			Input->AddMappingContext(Mapping, 0);
		}
	}
}

void AFWCharacter::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	UpdateTarget();
	UpdateGhost();
	UpdateHold(DeltaSeconds);
	UpdateHeadBob(DeltaSeconds);
	UpdateFootsteps(DeltaSeconds);
	UpdateHeld(DeltaSeconds);
	UpdateArms(DeltaSeconds);
	UpdateBodyAnimation();
}

FString AFWCharacter::SurfaceUnder(const FHitResult* Hit) const
{
	FHitResult Below;
	if (!Hit)
	{
		const FVector Feet = GetActorLocation() - FVector(0, 0, GetCapsuleComponent()->GetScaledCapsuleHalfHeight());
		FCollisionQueryParams Params(TEXT("FWStep"), false, this);
		if (!GetWorld()->LineTraceSingleByChannel(Below, Feet + FVector(0, 0, 20.f), Feet - FVector(0, 0, 60.f), ECC_Visibility, Params))
		{
			return TEXT("grass");
		}
		Hit = &Below;
	}
	const AFWPlot* Plot = GetPlot();
	const UStaticMeshComponent* Model = Cast<UStaticMeshComponent>(Hit->GetComponent());
	const FString Name = Model && Model->GetStaticMesh() ? Model->GetStaticMesh()->GetName().ToLower() : FString();
	if ((Plot && Plot->IsLand(Hit->GetComponent())) || !Model || Name.StartsWith(TEXT("terrain")))
	{
		// The land (the plot's and the terrain around it): steep ground is bare rock and scree; high up, snow (as the
		// ground material draws them); grass and earth elsewhere
		return Hit->ImpactNormal.Z < 0.86f ? TEXT("rock") : Plot && Plot->IsSnowAt(Hit->ImpactPoint.Z) ? TEXT("snow") : TEXT("grass");
	}
	// The garage's floor is smooth concrete, not scree: its own set (Tools/make_footsteps.py), a heel on concrete with
	// the weight of the step and the room's short echo
	if (Name.StartsWith(TEXT("garage_")))
	{
		return TEXT("concrete");
	}
	// Belts, machines, rails and the like ring as metal (by their model); boulders, trunks, foundations, floors and
	// the rest are stone and concrete
	for (const TCHAR* Metal : { TEXT("belt"), TEXT("machine"), TEXT("grate"), TEXT("rail"), TEXT("splitter"), TEXT("merger"),
		TEXT("sorter"), TEXT("inserter"), TEXT("chest"), TEXT("assembler"), TEXT("turbine"), TEXT("panel"), TEXT("train") })
	{
		if (Name.Contains(Metal))
		{
			return TEXT("metal");
		}
	}
	return TEXT("rock");
}

void AFWCharacter::UpdateFootsteps(float DeltaSeconds)
{
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	// One step each time the head bob comes down (BobPhase crosses a multiple of pi), so what is heard keeps time with
	// what is seen: about two steps a second at walking speed. A step every 75 cm, as before, was six a second, which
	// on the garage's hard floor sounded like someone tapping their feet fast
	const int32 Step = FMath::FloorToInt(BobPhase / PI);
	const bool bNewStep = Step != LastStep;
	LastStep = Step;
	const float Speed = GetVelocity().Size2D();
	if (!bNewStep || Speed < 40.f || !Movement || !Movement->IsMovingOnGround())
	{
		return;
	}
	const FString Surface = SurfaceUnder();
	// Indoors (the garage's concrete) a step is quieter than out on the land
	const float Volume = (Speed > 500.f ? 0.55f : 0.4f) * (Surface == TEXT("concrete") ? 0.8f : 1.f);
	FWAudio::Play2D(this, FString::Printf(TEXT("step_%s"), *Surface), Volume * FMath::FRandRange(0.85f, 1.1f), 6);
}

void AFWCharacter::OnJumped_Implementation()
{
	Super::OnJumped_Implementation();
	FWAudio::Play2D(this, TEXT("jump"), 0.4f);
}

void AFWCharacter::Landed(const FHitResult& Hit)
{
	Super::Landed(Hit);
	FWAudio::Play2D(this, FString::Printf(TEXT("land_%s"), *SurfaceUnder(&Hit)), 0.6f);
}

void AFWCharacter::FWGroundCheck()
{
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	const FVector Feet = GetActorLocation() - FVector(0, 0, GetCapsuleComponent()->GetScaledCapsuleHalfHeight());
	FHitResult Hit;
	FCollisionQueryParams Params(TEXT("FWGround"), false, this);
	const bool bHit = GetWorld()->LineTraceSingleByChannel(Hit, Feet + FVector(0, 0, 10.f), Feet - FVector(0, 0, 10000.f), ECC_Visibility, Params);
	UE_LOG(LogTemp, Log, TEXT("FWGroundCheck: mode=%s feetZ=%.1f groundZ=%s gap=%s eyeAboveFeet=%.1f speed=%.0f"),
		*UEnum::GetValueAsString(Movement->MovementMode.GetValue()), Feet.Z,
		bHit ? *FString::Printf(TEXT("%.1f"), Hit.ImpactPoint.Z) : TEXT("none"),
		bHit ? *FString::Printf(TEXT("%.1f"), Feet.Z - Hit.ImpactPoint.Z) : TEXT("-"),
		Camera->GetComponentLocation().Z - Feet.Z, GetVelocity().Size());
}

#undef LOCTEXT_NAMESPACE
