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

namespace
{
	constexpr float ReachTiles = 12.f;
	constexpr float PickupSeconds = 0.6f;
}

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

	// The pickaxe (Tools/blender/make_tools.py): in the right hand while mining
	if (UStaticMesh* Pickaxe = FWArt::Factory(TEXT("tool_pickaxe")))
	{
		Tool = NewObject<UStaticMeshComponent>(this);
		Tool->SetStaticMesh(Pickaxe);
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
	Held->RegisterComponent();
}

void AFWCharacter::UpdateHeld(float DeltaSeconds)
{
	const AFWPlayerController* PC = GetFWController();
	const FString Selected = PC ? PC->GetSelectedItem() : FString();
	// A new slot: down with the old item, then up with the new one
	if (Selected != HeldItem && EquipTime < 0.f)
	{
		EquipTime = 0.f;
	}
	if (EquipTime >= 0.f)
	{
		const float Before = EquipTime;
		EquipTime += DeltaSeconds;
		if (Before < 0.18f && EquipTime >= 0.18f)
		{
			HeldItem = Selected;
			UStaticMesh* Model = HeldItem.IsEmpty() || HeldItem.StartsWith(TEXT("tool_")) ? nullptr : FWArt::ItemMesh(HeldItem);
			if (Held)
			{
				Held->SetStaticMesh(Model);
				if (Model)
				{
					// About 13 cm across, whatever the model's size
					const float Scale = 6.5f / FMath::Max(1.f, Model->GetBounds().SphereRadius);
					Held->SetWorldScale3D(FVector(Scale));
				}
			}
		}
		if (EquipTime >= 0.5f)
		{
			EquipTime = -1.f;
		}
	}
	const bool bPickaxe = HeldItem.StartsWith(TEXT("tool_pickaxe"));
	if (bPickaxe)
	{
		ToolShown = 1.f;
	}
	if (Held)
	{
		const bool bShow = Held->GetStaticMesh() != nullptr && ToolShown <= 0.f;
		Held->SetVisibility(bShow);
		if (bShow)
		{
			// Between the hands, a little above them, turning slowly like a model on a table
			HeldSpin += DeltaSeconds * 25.f;
			const FVector Hands = (Arms->GetBoneLocation(TEXT("hand_l")) + Arms->GetBoneLocation(TEXT("hand_r"))) * 0.5f;
			const FVector Middle = Held->GetStaticMesh()->GetBounds().Origin * Held->GetComponentScale();
			const FRotator Turn(0.f, GetControlRotation().Yaw + HeldSpin, 0.f);
			Held->SetWorldLocationAndRotation(Hands + Camera->GetUpVector() * 7.f + Camera->GetForwardVector() * 4.f - Turn.RotateVector(Middle), Turn);
		}
	}
}

void AFWCharacter::Scroll(const FInputActionValue& Value)
{
	const float Delta = Value.Get<float>();
	if (AFWPlayerController* PC = GetFWController(); PC && FMath::Abs(Delta) > 0.f)
	{
		const int32 Size = AFWPlayerController::HotbarSize;
		SelectSlot((PC->GetSelectedSlot() + (Delta < 0.f ? 1 : -1) + Size) % Size);
	}
}

void AFWCharacter::CurlFingers(int32 Side, float Amount)
{
	// Each finger joint bends about its own Z axis (the bind pose is already a little bent); the thumb closes less
	static const TCHAR* Fingers[] = { TEXT("index"), TEXT("middle"), TEXT("ring"), TEXT("pinky"), TEXT("thumb") };
	const TCHAR* Suffix = Side == 0 ? TEXT("_l") : TEXT("_r");
	const FReferenceSkeleton& Skeleton = Arms->GetSkinnedAsset()->GetRefSkeleton();
	const TArray<FTransform>& RefPose = Skeleton.GetRefBonePose();
	const float Sign = Side == 0 ? 1.f : -1.f;
	for (const TCHAR* Finger : Fingers)
	{
		const bool bThumb = FCString::Strcmp(Finger, TEXT("thumb")) == 0;
		for (int32 Joint = bThumb ? 2 : 1; Joint <= 3; ++Joint)
		{
			const FName Bone(*FString::Printf(TEXT("%s_0%d%s"), Finger, Joint, Suffix));
			const int32 Index = Skeleton.FindBoneIndex(Bone);
			if (Index == INDEX_NONE)
			{
				continue;
			}
			const FName Parent = Skeleton.GetBoneName(Skeleton.GetParentIndex(Index));
			// More at the knuckle, less at the tip, and the pinky closes most
			const float Degrees = (bThumb ? 30.f : (Joint == 1 ? 62.f : Joint == 2 ? 75.f : 45.f)) * Amount;
			FTransform Local = RefPose[Index];
			Local.SetRotation(Local.GetRotation() * FQuat(FVector::UpVector, FMath::DegreesToRadians(Sign * Degrees)));
			Arms->SetBoneTransformByName(Bone, Local * Arms->GetBoneTransformByName(Parent, EBoneSpaces::ComponentSpace), EBoneSpaces::ComponentSpace);
		}
	}
}

void AFWCharacter::Strike()
{
	FWAudio::Play2D(this, TEXT("mine_hit"), 0.6f, 3);
	ShakeTime = 0.18f;
	// Where the pickaxe meets the rock: straight ahead of the eye
	FHitResult Hit;
	const FVector Eye = Camera->GetComponentLocation();
	FCollisionQueryParams Params(TEXT("FWStrike"), true, this);
	if (!GetWorld()->LineTraceSingleByChannel(Hit, Eye, Eye + Camera->GetForwardVector() * 800.f, ECC_Visibility, Params))
	{
		return;
	}
	// Chips of rock fly out of the strike and fall
	const TArray<UStaticMesh*> Rocks = FWArt::Rocks();
	UMaterialInterface* RockMaterial = Rocks.IsEmpty() ? nullptr : Rocks[0]->GetMaterial(0);
	UStaticMesh* Cube = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
	for (int32 i = 0; i < 10 && Cube; ++i)
	{
		FActorSpawnParameters Spawn;
		Spawn.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
		AStaticMeshActor* Chip = GetWorld()->SpawnActor<AStaticMeshActor>(Hit.ImpactPoint + Hit.ImpactNormal * 4.f, FRotator(FMath::FRandRange(0.f, 360.f), FMath::FRandRange(0.f, 360.f), 0.f), Spawn);
		if (!Chip)
		{
			continue;
		}
		UStaticMeshComponent* Piece = Chip->GetStaticMeshComponent();
		Piece->SetMobility(EComponentMobility::Movable);
		Piece->SetStaticMesh(Cube);
		Piece->SetWorldScale3D(FVector(FMath::FRandRange(0.035f, 0.07f), FMath::FRandRange(0.03f, 0.06f), FMath::FRandRange(0.02f, 0.045f)));
		if (RockMaterial)
		{
			Piece->SetMaterial(0, RockMaterial);
		}
		Piece->SetCollisionProfileName(TEXT("PhysicsActor"));
		Piece->SetCollisionResponseToChannel(ECC_Pawn, ECR_Ignore);
		Piece->SetSimulatePhysics(true);
		Piece->SetCastShadow(false);
		const FVector Out = (Hit.ImpactNormal + FMath::VRand() * 0.7f + FVector(0, 0, 0.6f)).GetSafeNormal();
		Piece->AddImpulse(Out * FMath::FRandRange(120.f, 260.f), NAME_None, true);
		Piece->AddAngularImpulseInDegrees(FMath::VRand() * 900.f, NAME_None, true);
		Chip->SetLifeSpan(FMath::FRandRange(1.4f, 2.2f));
	}
	// A scuff where it struck, fading after a while
	if (UMaterialInterface* DecalBase = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/Materials/M_FWDecal.M_FWDecal")))
	{
		UTexture2D* Color = FWArt::Texture(TEXT("/Game/External/Decals/Leaking008/Leaking008_2K-PNG_Color.Leaking008_2K-PNG_Color"));
		UTexture2D* Mask = FWArt::Texture(TEXT("/Game/External/Decals/Leaking008/Leaking008_2K-PNG_Opacity.Leaking008_2K-PNG_Opacity"));
		if (Color && Mask)
		{
			UMaterialInstanceDynamic* Material = UMaterialInstanceDynamic::Create(DecalBase, this);
			Material->SetTextureParameterValue(TEXT("Color"), Color);
			Material->SetTextureParameterValue(TEXT("Opacity"), Mask);
			Material->SetVectorParameterValue(TEXT("Tint"), FLinearColor(0.12f, 0.11f, 0.1f));
			Material->SetScalarParameterValue(TEXT("Strength"), 0.9f);
			const FRotator Facing = (-Hit.ImpactNormal).Rotation() + FRotator(0.f, 0.f, FMath::FRandRange(0.f, 360.f));
			if (UDecalComponent* Decal = UGameplayStatics::SpawnDecalAttached(Material, FVector(8.f, 16.f, 16.f), Hit.GetComponent(), NAME_None, Hit.ImpactPoint, Facing, EAttachLocation::KeepWorldPosition, 12.f))
			{
				Decal->SetFadeOut(8.f, 4.f);
			}
		}
	}
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

void AFWCharacter::PlayGesture(EGesture InGesture)
{
	Gesture = InGesture;
	GestureTime = 0.f;
	bStruck = false;
}

void AFWCharacter::FWGesture(const FString& Name)
{
	if (Name == TEXT("mine"))
	{
		// Holds the pickaxe and swings as when mining
		ToolShown = 1.f;
	}
	PlayGesture(Name == TEXT("place") ? EGesture::Place : Name == TEXT("rotate") ? EGesture::Rotate
		: Name == TEXT("pickup") ? EGesture::Pickup : Name == TEXT("swing") ? EGesture::Swing : EGesture::None);
}

void AFWCharacter::UpdateArms(float DeltaSeconds)
{
	if (!bArmsReady || !Arms)
	{
		return;
	}
	const float Time = GetWorld()->GetTimeSeconds();
	const float Speed = GetCharacterMovement()->IsMovingOnGround() ? GetVelocity().Size2D() : 0.f;
	const float Moving = FMath::Clamp(Speed / WalkSpeed, 0.f, 1.6f);

	// The arms trail a turn of the head a little and catch up
	const FRotator View = GetControlRotation();
	const FRotator Turned = (View - LastView).GetNormalized();
	LastView = View;
	ViewLag = FMath::Vector2DInterpTo(ViewLag + FVector2D(Turned.Yaw, Turned.Pitch) * 0.6f, FVector2D::ZeroVector, DeltaSeconds, 8.f);
	ViewLag = ViewLag.ClampAxes(-6.f, 6.f);

	// Hands a little higher and closer in build mode, as if holding something up to set it down
	BuildRaise = FMath::FInterpTo(BuildRaise, bBuildMode || (Held && Held->IsVisible()) ? 1.f : 0.f, DeltaSeconds, 6.f);

	// Gestures: offsets of the right (index 1) and left hand over their length
	FVector Gest[2] = { FVector::ZeroVector, FVector::ZeroVector };
	float Length = 0.f;
	switch (Gesture)
	{
	case EGesture::Place: Length = 0.35f; break;
	case EGesture::Rotate: Length = 0.25f; break;
	case EGesture::Pickup: Length = 0.45f; break;
	case EGesture::Swing: Length = 0.6f; break;
	default: break;
	}
	if (Gesture != EGesture::None)
	{
		GestureTime += DeltaSeconds;
		const float T = FMath::Clamp(GestureTime / Length, 0.f, 1.f);
		const float Out = FMath::Sin(T * PI);
		switch (Gesture)
		{
		case EGesture::Place:  // both hands push forward and down, and come back
			Gest[0] = Gest[1] = FVector(0, 14.f, -8.f) * Out;
			break;
		case EGesture::Rotate:  // a quick turn of the right hand
			Gest[1] = FVector(-8.f, 4.f, 3.f) * Out;
			break;
		case EGesture::Pickup:  // reach forward and down, pull back to the chest
			Gest[0] = Gest[1] = FVector(0, 18.f * FMath::Sin(FMath::Min(T * 2.f, 1.f) * PI * 0.5f) - 22.f * FMath::Max(0.f, T - 0.5f), -14.f * Out);
			break;
		case EGesture::Swing:  // the right hand up and back, then down and forward in a strike
		{
			const float Raise = T < 0.45f ? FMath::SmoothStep(0.f, 1.f, T / 0.45f) : 1.f - FMath::SmoothStep(0.f, 1.f, (T - 0.45f) / 0.25f);
			const float Strike = T < 0.45f ? 0.f : FMath::Sin(FMath::Clamp((T - 0.45f) / 0.55f, 0.f, 1.f) * PI);
			Gest[1] = FVector(-4.f, -10.f, 26.f) * Raise + FVector(4.f, 20.f, -6.f) * Strike;
			Gest[0] = FVector(0, -4.f, -6.f) * Strike;
			SwingRaise = Raise;
			SwingStrike = Strike;
			break;
		}
		default:
			break;
		}
		if (Gesture == EGesture::Swing && !bStruck && T >= 0.62f)
		{
			bStruck = true;
			Strike();
		}
		if (T >= 1.f)
		{
			Gesture = EGesture::None;
		}
	}

	// Where the hands rest in the mesh's space (+Y forward, +X the character's left, Z up from the feet)
	if (Gesture != EGesture::Swing)
	{
		SwingRaise = SwingStrike = 0.f;
	}
	const float Breath = FMath::Sin(Time * 1.6f) * 0.6f;
	const float StepBob = FMath::Abs(FMath::Sin(BobPhase)) * 1.8f * Moving;
	const float StepSway = FMath::Cos(BobPhase) * 1.4f * Moving;
	const TCHAR* Sides[2][3] = { { TEXT("upperarm_l"), TEXT("lowerarm_l"), TEXT("hand_l") }, { TEXT("upperarm_r"), TEXT("lowerarm_r"), TEXT("hand_r") } };
	for (int32 Side = 0; Side < 2; ++Side)
	{
		const float Sign = Side == 0 ? 1.f : -1.f;
		FVector Hand(Sign * (21.f - 4.f * BuildRaise), 48.f + 4.f * BuildRaise, 146.f + 6.f * BuildRaise);
		Hand += FVector(StepSway * Sign * 0.3f + ViewLag.X * -0.8f, 0.f, Breath - StepBob - ViewLag.Y * 0.8f);
		Hand += Gest[Side];
		// Changing items: the hands dip out of view and come back
		if (EquipTime >= 0.f)
		{
			const float Dip = EquipTime < 0.18f ? FMath::SmoothStep(0.f, 0.18f, EquipTime) : 1.f - FMath::SmoothStep(0.18f, 0.5f, EquipTime);
			Hand += FVector(0.f, -6.f, -26.f) * Dip;
		}
		// Elbows out and down
		const FVector Elbow(Sign * 45.f, 18.f, 112.f);
		FTransform Upper = ArmRef[Side].Upper;
		FTransform Lower = ArmRef[Side].Lower;
		FTransform End = ArmRef[Side].Hand;
		const FQuat LowerRef = Lower.GetRotation();
		AnimationCore::SolveTwoBoneIK(Upper, Lower, End, Elbow, Hand, false, 1.0, 1.0);
		// The hand turns with the forearm, then twists about it, palms turned in towards each other
		const FQuat Delta = Lower.GetRotation() * LowerRef.Inverse();
		const FVector Forearm = (End.GetLocation() - Lower.GetLocation()).GetSafeNormal();
		End.SetRotation(FQuat(Forearm, FMath::DegreesToRadians(Sign * 40.f)) * Delta * ArmRef[Side].Hand.GetRotation());
		Arms->SetBoneTransformByName(Sides[Side][0], Upper, EBoneSpaces::ComponentSpace);
		Arms->SetBoneTransformByName(Sides[Side][1], Lower, EBoneSpaces::ComponentSpace);
		Arms->SetBoneTransformByName(Sides[Side][2], End, EBoneSpaces::ComponentSpace);
	}
	// The pickaxe comes out for mining and goes away a moment after
	const bool bMining = Hold == EHold::Mine || Gesture == EGesture::Swing;
	ToolShown = bMining ? 1.f : FMath::Max(0.f, ToolShown - DeltaSeconds * 1.5f);
	if (Tool)
	{
		Tool->SetVisibility(ToolShown > 0.f);
		// The handle in the fist, pointing up and forward over the hand at rest, tipped back at the top of the
		// swing and driven forward and down into the strike; the head always points ahead
		const FVector Forward = Camera->GetForwardVector();
		const FVector Right = Camera->GetRightVector();
		const FVector Up = Camera->GetUpVector();
		FVector Handle = Forward * 0.9f - Right * 0.4f + Up * 0.35f;
		Handle = FMath::Lerp(Handle, -Forward * 0.25f - Right * 0.1f + Up, SwingRaise);
		Handle = FMath::Lerp(Handle, Forward - Right * 0.1f - Up * 0.1f, SwingStrike);
		Handle.Normalize();
		FVector Head = Forward - Handle * FVector::DotProduct(Forward, Handle);
		Head = Head.IsNearlyZero() ? Up : Head.GetSafeNormal();
		const FVector Grip = Arms->GetBoneLocation(TEXT("hand_r")) - Handle * 3.f;
		Tool->SetWorldLocationAndRotation(Grip, FRotationMatrix::MakeFromZX(Handle, Head).Rotator());
	}
	// Relaxed hands, a little more closed in build mode; the right one a fist round the handle
	CurlFingers(0, 0.25f + 0.15f * BuildRaise);
	CurlFingers(1, ToolShown > 0.f && Tool ? 1.f : 0.25f + 0.15f * BuildRaise);
	// The jolt of a strike: a few millimetres, gone in a fifth of a second
	ShakeTime = FMath::Max(0.f, ShakeTime - DeltaSeconds);
	ShakeOffset = ShakeTime > 0.f ? FMath::VRand() * 0.6f * (ShakeTime / 0.18f) : FVector::ZeroVector;
}

void AFWCharacter::UpdateBodyAnimation()
{
	if (!Body)
	{
		return;
	}
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	const float Speed = GetVelocity().Size2D();
	const FString Wanted = Movement->IsFalling() ? TEXT("Jump/MM_Fall_Loop") : Speed > 500.f ? TEXT("Jog/MF_Unarmed_Jog_Fwd")
		: Speed > 40.f ? TEXT("Walk/MF_Unarmed_Walk_Fwd") : TEXT("MM_Idle");
	if (Wanted != BodyAnimation)
	{
		BodyAnimation = Wanted;
		const FString Name = FPaths::GetBaseFilename(Wanted);
		if (UAnimSequence* Animation = LoadObject<UAnimSequence>(nullptr, *FString::Printf(TEXT("/Game/Characters/Mannequins/Anims/Unarmed/%s.%s"), *Wanted, *Name), nullptr, LOAD_NoWarn | LOAD_Quiet))
		{
			Body->PlayAnimation(Animation, true);
		}
	}
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

void AFWCharacter::CreateInput()
{
	if (Mapping)
	{
		return;
	}
	Mapping = NewObject<UInputMappingContext>(this);
	auto MakeAction = [this](FName Name, EInputActionValueType Type)
	{
		UInputAction* Action = NewObject<UInputAction>(this, Name);
		Action->ValueType = Type;
		Actions.Add(Name, Action);
		return Action;
	};

	// Movement: X right, Y forward
	UInputAction* Move = MakeAction(TEXT("Move"), EInputActionValueType::Axis2D);
	Mapping->MapKey(Move, EKeys::D);
	Mapping->MapKey(Move, EKeys::A).Modifiers.Add(NewObject<UInputModifierNegate>(this));
	Mapping->MapKey(Move, EKeys::W).Modifiers.Add(NewObject<UInputModifierSwizzleAxis>(this));
	{
		FEnhancedActionKeyMapping& Back = Mapping->MapKey(Move, EKeys::S);
		Back.Modifiers.Add(NewObject<UInputModifierSwizzleAxis>(this));
		Back.Modifiers.Add(NewObject<UInputModifierNegate>(this));
	}
	Mapping->MapKey(MakeAction(TEXT("Look"), EInputActionValueType::Axis2D), EKeys::Mouse2D);
	Mapping->MapKey(MakeAction(TEXT("Rail"), EInputActionValueType::Boolean), EKeys::T);
	Mapping->MapKey(MakeAction(TEXT("Jump"), EInputActionValueType::Boolean), EKeys::SpaceBar);
	Mapping->MapKey(MakeAction(TEXT("Chat"), EInputActionValueType::Boolean), EKeys::Enter);
	Mapping->MapKey(MakeAction(TEXT("Achievements"), EInputActionValueType::Boolean), EKeys::O);
	Mapping->MapKey(MakeAction(TEXT("Sprint"), EInputActionValueType::Boolean), EKeys::LeftShift);
	Mapping->MapKey(MakeAction(TEXT("Company"), EInputActionValueType::Boolean), EKeys::K);
	Mapping->MapKey(MakeAction(TEXT("Bank"), EInputActionValueType::Boolean), EKeys::Z);
	Mapping->MapKey(MakeAction(TEXT("Interact"), EInputActionValueType::Boolean), EKeys::E);
	Mapping->MapKey(MakeAction(TEXT("Pickup"), EInputActionValueType::Boolean), EKeys::RightMouseButton);
	Mapping->MapKey(MakeAction(TEXT("Adventures"), EInputActionValueType::Boolean), EKeys::Y);
	Mapping->MapKey(MakeAction(TEXT("Primary"), EInputActionValueType::Boolean), EKeys::LeftMouseButton);
	Mapping->MapKey(MakeAction(TEXT("City"), EInputActionValueType::Boolean), EKeys::H);
	Mapping->MapKey(MakeAction(TEXT("Country"), EInputActionValueType::Boolean), EKeys::P);
	Mapping->MapKey(MakeAction(TEXT("Build"), EInputActionValueType::Boolean), EKeys::B);
	Mapping->MapKey(MakeAction(TEXT("Factory"), EInputActionValueType::Boolean), EKeys::G);
	Mapping->MapKey(MakeAction(TEXT("Rotate"), EInputActionValueType::Boolean), EKeys::R);
	Mapping->MapKey(MakeAction(TEXT("Contracts"), EInputActionValueType::Boolean), EKeys::J);
	Mapping->MapKey(MakeAction(TEXT("Stats"), EInputActionValueType::Boolean), EKeys::L);
	Mapping->MapKey(MakeAction(TEXT("Inventory"), EInputActionValueType::Boolean), EKeys::Tab);
	Mapping->MapKey(Actions[TEXT("Inventory")], EKeys::I);
	Mapping->MapKey(MakeAction(TEXT("Menu"), EInputActionValueType::Boolean), EKeys::Escape);
	Mapping->MapKey(MakeAction(TEXT("Map"), EInputActionValueType::Boolean), EKeys::N);
	// The mouse wheel steps through the hotbar
	Mapping->MapKey(MakeAction(TEXT("Scroll"), EInputActionValueType::Axis1D), EKeys::MouseWheelAxis);
	const FKey SlotKeys[] = { EKeys::One, EKeys::Two, EKeys::Three, EKeys::Four, EKeys::Five };
	for (int32 Slot = 0; Slot < 5; ++Slot)
	{
		Mapping->MapKey(MakeAction(*FString::Printf(TEXT("Slot%d"), Slot + 1), EInputActionValueType::Boolean), SlotKeys[Slot]);
	}
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

void AFWCharacter::SetupPlayerInputComponent(UInputComponent* PlayerInputComponent)
{
	Super::SetupPlayerInputComponent(PlayerInputComponent);
	CreateInput();
	UEnhancedInputComponent* Input = CastChecked<UEnhancedInputComponent>(PlayerInputComponent);
	Input->BindAction(Actions[TEXT("Move")], ETriggerEvent::Triggered, this, &AFWCharacter::Move);
	Input->BindAction(Actions[TEXT("Look")], ETriggerEvent::Triggered, this, &AFWCharacter::Look);
	Input->BindActionValueLambda(Actions[TEXT("Rail")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenRail();
		}
	});
	Input->BindAction(Actions[TEXT("Jump")], ETriggerEvent::Started, this, &ACharacter::Jump);
	Input->BindAction(Actions[TEXT("Jump")], ETriggerEvent::Completed, this, &ACharacter::StopJumping);
	Input->BindActionValueLambda(Actions[TEXT("City")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenCity();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Country")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenCountry();
		}
	});
	Input->BindAction(Actions[TEXT("Sprint")], ETriggerEvent::Started, this, &AFWCharacter::SprintStart);
	Input->BindAction(Actions[TEXT("Sprint")], ETriggerEvent::Completed, this, &AFWCharacter::SprintStop);
	Input->BindActionValueLambda(Actions[TEXT("Adventures")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenAdventures();
		}
	});
	Input->BindAction(Actions[TEXT("Interact")], ETriggerEvent::Started, this, &AFWCharacter::InteractStart);
	Input->BindAction(Actions[TEXT("Interact")], ETriggerEvent::Completed, this, &AFWCharacter::InteractStop);
	Input->BindActionValueLambda(Actions[TEXT("Chat")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenChat();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Achievements")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenAchievements();
		}
	});
	Input->BindAction(Actions[TEXT("Pickup")], ETriggerEvent::Started, this, &AFWCharacter::PickupStart);
	Input->BindAction(Actions[TEXT("Pickup")], ETriggerEvent::Completed, this, &AFWCharacter::PickupStop);
	Input->BindAction(Actions[TEXT("Primary")], ETriggerEvent::Started, this, &AFWCharacter::Primary);
	Input->BindActionValueLambda(Actions[TEXT("Company")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenCompany();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Bank")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenBank();
		}
	});
	Input->BindAction(Actions[TEXT("Build")], ETriggerEvent::Started, this, &AFWCharacter::ToggleBuild);
	Input->BindAction(Actions[TEXT("Rotate")], ETriggerEvent::Started, this, &AFWCharacter::Rotate);
	Input->BindActionValueLambda(Actions[TEXT("Factory")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenFactory();
		}
	});
	Input->BindAction(Actions[TEXT("Scroll")], ETriggerEvent::Started, this, &AFWCharacter::Scroll);
	Input->BindActionValueLambda(Actions[TEXT("Contracts")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenContracts();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Stats")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenStats();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Inventory")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenInventory();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Menu")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenSettings();
		}
	});
	Input->BindActionValueLambda(Actions[TEXT("Map")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->OpenWorldMap();
		}
	});
	for (int32 Slot = 0; Slot < 5; ++Slot)
	{
		Input->BindActionValueLambda(Actions[*FString::Printf(TEXT("Slot%d"), Slot + 1)], ETriggerEvent::Started,
			[this, Slot](const FInputActionValue&) { SelectSlot(Slot); });
	}
}

void AFWCharacter::Move(const FInputActionValue& Value)
{
	// Walking again after a console view from above
	if (GetCharacterMovement()->IsFlying())
	{
		GetCharacterMovement()->SetMovementMode(MOVE_Walking);
	}
	const FVector2D Axis = Value.Get<FVector2D>();
	const FRotator Yaw(0.f, GetControlRotation().Yaw, 0.f);
	AddMovementInput(FRotationMatrix(Yaw).GetUnitAxis(EAxis::X), Axis.Y);
	AddMovementInput(FRotationMatrix(Yaw).GetUnitAxis(EAxis::Y), Axis.X);
}

void AFWCharacter::Look(const FInputActionValue& Value)
{
	const FVector2D Axis = Value.Get<FVector2D>();
	AddControllerYawInput(Axis.X * MouseSensitivity);
	AddControllerPitchInput(-Axis.Y * MouseSensitivity);
}

void AFWCharacter::SprintStart()
{
	GetCharacterMovement()->MaxWalkSpeed = SprintSpeed;
}

void AFWCharacter::SprintStop()
{
	GetCharacterMovement()->MaxWalkSpeed = WalkSpeed;
}

void AFWCharacter::InteractStart()
{
	bInteractHeld = true;
	AFWPlot* Plot = GetPlot();
	AFWPlayerController* PC = GetFWController();
	// A shop or a works canteen opens its own window
	if (const FFWPlotObject* Shop = Plot && PC && bHasTarget && Hold == EHold::None ? Plot->FindObjectAt(TargetTile) : nullptr; Shop && AFWPlayerController::IsShop(Shop->Kind))
	{
		PC->OpenShop(Shop->Id, Shop->Kind);
		return;
	}
	if (!Plot || !PC || !bHasTarget || Hold != EHold::None)
	{
		return;
	}
	if (const FFWPlotObject* Logistic = Plot->FindObjectAt(TargetTile); Logistic && AFWPlayerController::HasOwnFilter(Logistic->Kind))
	{
		PC->OpenFilter(Logistic->Id);
	}
	else if (const FFWMine* Mine = Plot->FindMine(TargetTile))
	{
		Hold = EHold::Mine;
		HoldMine = Mine->Id;
		HoldNeeded = FMath::Max(0.2f, Mine->Seconds);
		HoldTime = 0.f;
	}
	else if (const FFWPlotObject* Farm = Plot->FindObjectAt(TargetTile); Farm && AFWPlayerController::IsFarm(Farm->Kind))
	{
		PC->OpenFarm(Farm->Id, Farm->Kind);
	}
	else if (const FFWPlotObject* Object = Plot->FindObjectAt(TargetTile); Object && FWCatalog::IsStorage(Object->Kind))
	{
		PC->OpenInventory(Object->Id);
	}
	else if (const FFWPlotObject* Chest = Plot->FindObjectAt(TargetTile); Chest && Chest->Kind == TEXT("furniture_export_chest"))
	{
		PC->OpenExport();
	}
	else if (const FFWPlotObject* Post = Plot->FindObjectAt(TargetTile); Post && (Post->Kind == TEXT("furniture_buy_market") || Post->Kind == TEXT("furniture_rail_port")))
	{
		PC->OpenExport(Post->Id);
	}
	else if (const FFWPlotObject* Computer = Plot->FindObjectAt(TargetTile); Computer && Computer->Kind == TEXT("furniture_computer"))
	{
		PC->OpenMarket();
	}
	else if (const FFWPlotObject* Table = Plot->FindObjectAt(TargetTile); Table && Table->Kind == TEXT("furniture_research_table"))
	{
		PC->OpenResearch();
	}
	else if (const FFWPlotObject* Station = Plot->FindObjectAt(TargetTile); Station && FWCatalog::StationOf(Station->Kind) >= 0)
	{
		PC->OpenStation(Station->Id, Station->Kind);
	}
	else if (const FFWPlotObject* Device = Plot->FindObjectAt(TargetTile); Device && AFWPlayerController::IsDevice(Device->Kind))
	{
		PC->OpenDevice(Device->Id, Device->Kind);
	}
}

void AFWCharacter::InteractStop()
{
	bInteractHeld = false;
	if (Hold == EHold::Mine && !bWaiting)
	{
		Hold = EHold::None;
	}
}

void AFWCharacter::PickupStart()
{
	const AFWPlot* Plot = GetPlot();
	const FFWPlotObject* Object = Plot && bHasTarget ? Plot->FindObjectAt(TargetTile) : nullptr;
	if (Object && Hold == EHold::None)
	{
		Hold = EHold::Pickup;
		HoldObject = Object->Id;
		HoldNeeded = PickupSeconds;
		HoldTime = 0.f;
	}
}

void AFWCharacter::PickupStop()
{
	if (Hold == EHold::Pickup)
	{
		Hold = EHold::None;
	}
}

void AFWCharacter::Primary()
{
	AFWPlot* Plot = GetPlot();
	AFWPlayerController* PC = GetFWController();
	if (!bBuildMode || !Plot || !PC || !bHasTarget)
	{
		return;
	}
	const FString Kind = PC->GetSelectedItem();
	if (Kind.IsEmpty() || !Plot->IsPlaceable(Kind))
	{
		PC->Toast(LOCTEXT("NothingToBuild", "Wybierz na pasku (1–5) coś do zbudowania."), true);
		return;
	}
	if (!Plot->CanPlace(Kind, TargetTile))
	{
		FWAudio::Play2D(this, TEXT("build_invalid"), 0.6f);
		PC->Toast(LOCTEXT("CantPlace", "Tu nie można tego postawić."), true);
		return;
	}
	PlayGesture(EGesture::Place);
	PC->Place(Kind, TargetTile, BuildRotation);
}

void AFWCharacter::ToggleBuild()
{
	bBuildMode = !bBuildMode;
	AFWPlayerController* PC = GetFWController();
	const AFWPlot* Plot = GetPlot();
	if (bBuildMode && PC && Plot && !Plot->IsPlaceable(PC->GetSelectedItem()))
	{
		// Pick the first hotbar slot with something to build
		for (int32 Slot = 0; Slot < AFWPlayerController::HotbarSize; ++Slot)
		{
			if (Plot->IsPlaceable(PC->GetHotbar()[Slot]))
			{
				PC->SelectSlot(Slot);
				break;
			}
		}
	}
}

void AFWCharacter::Rotate()
{
	BuildRotation = (BuildRotation + 1) & 3;
	if (bBuildMode)
	{
		FWAudio::Play2D(this, TEXT("build_rotate"), 0.5f);
		PlayGesture(EGesture::Rotate);
	}
}

void AFWCharacter::SelectSlot(int32 Slot)
{
	if (AFWPlayerController* PC = GetFWController())
	{
		PC->SelectSlot(Slot);
		const AFWPlot* Plot = GetPlot();
		bBuildMode = Plot && Plot->IsPlaceable(PC->GetSelectedItem());
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
	if (Plot && Plot->IsLand(Hit->GetComponent()))
	{
		// Steep ground is bare rock and scree
		return Hit->ImpactNormal.Z < 0.86f ? TEXT("rock") : TEXT("grass");
	}
	// Boulders and the forest's trunks are part of the plot actor too, but not built: rock; the rest is metal
	const UPrimitiveComponent* Component = Hit->GetComponent();
	return Component && Component->IsA<UInstancedStaticMeshComponent>() ? TEXT("rock") : TEXT("metal");
}

void AFWCharacter::UpdateFootsteps(float DeltaSeconds)
{
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	if (!Movement || !Movement->IsMovingOnGround())
	{
		return;
	}
	const float Speed = GetVelocity().Size2D();
	if (Speed < 40.f)
	{
		StepDistance = 0.f;
		return;
	}
	StepDistance += Speed * DeltaSeconds;
	// Longer strides when running
	const float Stride = Speed > 500.f ? 105.f : 75.f;
	if (StepDistance >= Stride)
	{
		StepDistance -= Stride;
		FWAudio::Play2D(this, FString::Printf(TEXT("step_%s"), *SurfaceUnder()), Speed > 500.f ? 0.55f : 0.4f, 6);
	}
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
	StepDistance = 0.f;
}

void AFWCharacter::UpdateTarget()
{
	bHasTarget = false;
	const AFWPlot* Plot = GetPlot();
	if (!Plot || !Plot->IsBuilt())
	{
		return;
	}
	const FVector Start = Camera->GetComponentLocation();
	const FVector End = Start + Camera->GetForwardVector() * Plot->GetTileSize() * ReachTiles;
	FHitResult Hit;
	FCollisionQueryParams Params(TEXT("FWTarget"), false, this);
	if (!GetWorld()->LineTraceSingleByChannel(Hit, Start, End, ECC_Visibility, Params))
	{
		return;
	}
	// A little into what was hit, so the side of a block counts as its tile
	const FIntPoint Tile = Plot->TileAt(Hit.ImpactPoint - Hit.ImpactNormal * 5.f);
	if (Plot->Contains(Tile))
	{
		bHasTarget = true;
		TargetTile = Tile;
	}
}

void AFWCharacter::UpdateGhost()
{
	const AFWPlot* Plot = GetPlot();
	const AFWPlayerController* PC = GetFWController();
	const FString Kind = PC ? PC->GetSelectedItem() : FString();
	if (!bBuildMode || !bHasTarget || !Plot || Kind.IsEmpty() || !Plot->IsPlaceable(Kind))
	{
		Ghost->SetVisibility(false);
		return;
	}
	const float Tile = Plot->GetTileSize();
	const int32 N = FWCatalog::FootprintSize(Kind);
	const float Height = FWCatalog::IsBelt(Kind) ? 0.15f : (FWCatalog::IsInserter(Kind) ? 0.6f : 0.7f * FMath::Min(N, 2));
	const FIntPoint F = FWCatalog::Facing(BuildRotation);
	const FRotator Facing(0.f, FMath::RadiansToDegrees(FMath::Atan2(float(F.Y), float(F.X))), 0.f);
	Ghost->SetWorldLocationAndRotation(Plot->FootprintCenter(Kind, TargetTile) + FVector(0, 0, Height * Tile * 0.5f), Facing);
	// Long and thin along the way it faces, so the rotation shows
	Ghost->SetWorldScale3D(FVector(N * Tile / 100.f, N * Tile / 100.f * (N == 1 ? 0.7f : 1.f), Height * Tile / 100.f));
	const bool bOk = Plot->CanPlace(Kind, TargetTile);
	GhostMaterial->SetVectorParameterValue(TEXT("Color"), bOk ? FLinearColor(0.2f, 1.f, 0.4f) : FLinearColor(1.f, 0.15f, 0.1f));
	Ghost->SetVisibility(true);
}

void AFWCharacter::UpdateHold(float DeltaSeconds)
{
	AFWPlayerController* PC = GetFWController();
	const AFWPlot* Plot = GetPlot();
	if (Hold == EHold::None || bWaiting || !PC || !Plot)
	{
		return;
	}
	// Looking away cancels
	if (Hold == EHold::Mine)
	{
		const FFWMine* Mine = bHasTarget ? Plot->FindMine(TargetTile) : nullptr;
		if (!Mine || Mine->Id != HoldMine)
		{
			Hold = EHold::None;
			return;
		}
	}
	else if (Hold == EHold::Pickup)
	{
		const FFWPlotObject* Object = bHasTarget ? Plot->FindObjectAt(TargetTile) : nullptr;
		if (!Object || Object->Id != HoldObject)
		{
			Hold = EHold::None;
			return;
		}
	}

	HoldTime += DeltaSeconds;
	// The pickaxe strikes while digging
	if (Hold == EHold::Mine)
	{
		MineSoundIn -= DeltaSeconds;
		if (MineSoundIn <= 0.f)
		{
			MineSoundIn = FMath::FRandRange(0.55f, 0.75f);
			PlayGesture(EGesture::Swing);
		}
	}
	if (HoldTime < HoldNeeded)
	{
		return;
	}
	if (Hold == EHold::Pickup)
	{
		Hold = EHold::None;
		PlayGesture(EGesture::Pickup);
		PC->Pickup(HoldObject);
		return;
	}
	bWaiting = true;
	TWeakObjectPtr<AFWCharacter> WeakThis(this);
	PC->Mine(HoldMine, [WeakThis](bool bOk)
	{
		if (AFWCharacter* This = WeakThis.Get())
		{
			This->bWaiting = false;
			This->HoldTime = 0.f;
			// Keep digging while E is held
			if (!bOk || !This->bInteractHeld)
			{
				This->Hold = EHold::None;
			}
		}
	});
}

void AFWCharacter::UpdateHeadBob(float DeltaSeconds)
{
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	const float Speed = Movement->IsMovingOnGround() ? GetVelocity().Size2D() : 0.f;
	// About two steps a second at walking speed, a few centimetres up and down
	BobPhase = Speed > 20.f ? BobPhase + DeltaSeconds * Speed / 70.f : FMath::FInterpTo(BobPhase, FMath::RoundToFloat(BobPhase / PI) * PI, DeltaSeconds, 6.f);
	const float Amount = FMath::Clamp(Speed / WalkSpeed, 0.f, 1.6f);
	const FVector Bob(0.f, FMath::Cos(BobPhase) * 0.8f * Amount, FMath::Abs(FMath::Sin(BobPhase)) * 2.2f * Amount);
	Camera->SetRelativeLocation(FVector(0.f, 0.f, EyeHeight - GetCapsuleComponent()->GetUnscaledCapsuleHalfHeight()) + Bob + ShakeOffset);
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

float AFWCharacter::GetProgress() const
{
	if (Hold == EHold::None)
	{
		return -1.f;
	}
	return bWaiting ? 1.f : FMath::Clamp(HoldTime / HoldNeeded, 0.f, 1.f);
}

FText AFWCharacter::GetTargetText() const
{
	const AFWPlot* Plot = GetPlot();
	const AFWPlayerController* PC = GetFWController();
	if (!Plot || !PC || !bHasTarget)
	{
		return FText::GetEmpty();
	}
	if (bBuildMode)
	{
		const FString Kind = PC->GetSelectedItem();
		if (Kind.IsEmpty() || !Plot->IsPlaceable(Kind))
		{
			return LOCTEXT("PickSomething", "Wybierz coś do zbudowania (1–5)");
		}
		static const FText Ways[] = { LOCTEXT("South", "południe"), LOCTEXT("East", "wschód"), LOCTEXT("North", "północ"), LOCTEXT("West", "zachód") };
		return FText::Format(LOCTEXT("Placing", "{0}  →  {1}"), FText::FromString(PC->ItemName(Kind)), Ways[BuildRotation]);
	}
	if (Plot->FindMine(TargetTile))
	{
		return LOCTEXT("Mine", "Kopalnia  [E] kop");
	}
	if (const FFWPlotObject* Object = Plot->FindObjectAt(TargetTile))
	{
		return FWCatalog::IsStorage(Object->Kind)
			? FText::Format(LOCTEXT("Storage", "{0}  [E] włóż surowce   [PPM] zabierz"), FText::FromString(Object->Name))
			: FText::Format(LOCTEXT("Object", "{0}  [PPM] zabierz"), FText::FromString(Object->Name));
	}
	switch (Plot->TileCode(TargetTile))
	{
	case FWCatalog::Rock: return LOCTEXT("Rock", "Skała");
	case FWCatalog::Dune: return LOCTEXT("Dune", "Wydma");
	case FWCatalog::Bush: return LOCTEXT("Bush", "Krzak");
	case FWCatalog::Rails: return LOCTEXT("Rails", "Tory");
	case FWCatalog::Station: return LOCTEXT("Station", "Stacja");
	case FWCatalog::Empty: return FText::GetEmpty();
	default: return LOCTEXT("Tree", "Drzewo");
	}
}

#undef LOCTEXT_NAMESPACE
