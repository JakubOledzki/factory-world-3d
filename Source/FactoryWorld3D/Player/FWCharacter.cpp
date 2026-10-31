#include "Player/FWCharacter.h"

#include "Camera/CameraComponent.h"
#include "Components/CapsuleComponent.h"
#include "Components/StaticMeshComponent.h"
#include "EnhancedInputComponent.h"
#include "EnhancedInputSubsystems.h"
#include "Engine/LocalPlayer.h"
#include "Engine/StaticMesh.h"
#include "FWPlayerController.h"
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
	Mapping->MapKey(MakeAction(TEXT("Jump"), EInputActionValueType::Boolean), EKeys::SpaceBar);
	Mapping->MapKey(MakeAction(TEXT("Sprint"), EInputActionValueType::Boolean), EKeys::LeftShift);
	Mapping->MapKey(MakeAction(TEXT("Interact"), EInputActionValueType::Boolean), EKeys::E);
	Mapping->MapKey(MakeAction(TEXT("Pickup"), EInputActionValueType::Boolean), EKeys::RightMouseButton);
	Mapping->MapKey(MakeAction(TEXT("Primary"), EInputActionValueType::Boolean), EKeys::LeftMouseButton);
	Mapping->MapKey(MakeAction(TEXT("Build"), EInputActionValueType::Boolean), EKeys::B);
	Mapping->MapKey(MakeAction(TEXT("Rotate"), EInputActionValueType::Boolean), EKeys::R);
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
	Input->BindAction(Actions[TEXT("Jump")], ETriggerEvent::Started, this, &ACharacter::Jump);
	Input->BindAction(Actions[TEXT("Jump")], ETriggerEvent::Completed, this, &ACharacter::StopJumping);
	Input->BindAction(Actions[TEXT("Sprint")], ETriggerEvent::Started, this, &AFWCharacter::SprintStart);
	Input->BindAction(Actions[TEXT("Sprint")], ETriggerEvent::Completed, this, &AFWCharacter::SprintStop);
	Input->BindAction(Actions[TEXT("Interact")], ETriggerEvent::Started, this, &AFWCharacter::InteractStart);
	Input->BindAction(Actions[TEXT("Interact")], ETriggerEvent::Completed, this, &AFWCharacter::InteractStop);
	Input->BindAction(Actions[TEXT("Pickup")], ETriggerEvent::Started, this, &AFWCharacter::PickupStart);
	Input->BindAction(Actions[TEXT("Pickup")], ETriggerEvent::Completed, this, &AFWCharacter::PickupStop);
	Input->BindAction(Actions[TEXT("Primary")], ETriggerEvent::Started, this, &AFWCharacter::Primary);
	Input->BindAction(Actions[TEXT("Build")], ETriggerEvent::Started, this, &AFWCharacter::ToggleBuild);
	Input->BindAction(Actions[TEXT("Rotate")], ETriggerEvent::Started, this, &AFWCharacter::Rotate);
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
	AddControllerYawInput(Axis.X);
	AddControllerPitchInput(-Axis.Y);
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
	if (!Plot || !PC || !bHasTarget || Hold != EHold::None)
	{
		return;
	}
	if (const FFWMine* Mine = Plot->FindMine(TargetTile))
	{
		Hold = EHold::Mine;
		HoldMine = Mine->Id;
		HoldNeeded = FMath::Max(0.2f, Mine->Seconds);
		HoldTime = 0.f;
	}
	else if (const FFWPlotObject* Object = Plot->FindObjectAt(TargetTile); Object && FWCatalog::IsStorage(Object->Kind))
	{
		PC->PutAllInStorage(Object->Id);
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
		PC->Toast(LOCTEXT("CantPlace", "Tu nie można tego postawić."), true);
		return;
	}
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
	if (HoldTime < HoldNeeded)
	{
		return;
	}
	if (Hold == EHold::Pickup)
	{
		Hold = EHold::None;
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
	Camera->SetRelativeLocation(FVector(0.f, 0.f, EyeHeight - GetCapsuleComponent()->GetUnscaledCapsuleHalfHeight()) + Bob);
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
