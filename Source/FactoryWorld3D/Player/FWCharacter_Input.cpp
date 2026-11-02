// Input: bindings, moving and looking, the hotbar and build-mode keys.
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

void AFWCharacter::Scroll(const FInputActionValue& Value)
{
	const float Delta = Value.Get<float>();
	if (AFWPlayerController* PC = GetFWController(); PC && FMath::Abs(Delta) > 0.f)
	{
		const int32 Size = AFWPlayerController::HotbarSize;
		SelectSlot((PC->GetSelectedSlot() + (Delta < 0.f ? 1 : -1) + Size) % Size);
	}
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
	Mapping->MapKey(MakeAction(TEXT("Garage"), EInputActionValueType::Boolean), EKeys::U);
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
	Mapping->MapKey(MakeAction(TEXT("ModMenu"), EInputActionValueType::Boolean), EKeys::F8);
	// The mouse wheel steps through the hotbar
	Mapping->MapKey(MakeAction(TEXT("Scroll"), EInputActionValueType::Axis1D), EKeys::MouseWheelAxis);
	const FKey SlotKeys[] = { EKeys::One, EKeys::Two, EKeys::Three, EKeys::Four, EKeys::Five };
	for (int32 Slot = 0; Slot < 5; ++Slot)
	{
		Mapping->MapKey(MakeAction(*FString::Printf(TEXT("Slot%d"), Slot + 1), EInputActionValueType::Boolean), SlotKeys[Slot]);
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
	Input->BindActionValueLambda(Actions[TEXT("Garage")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		if (AFWPlayerController* PC = GetFWController())
		{
			PC->ToggleGarageRoom();
		}
	});
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
	Input->BindActionValueLambda(Actions[TEXT("ModMenu")], ETriggerEvent::Started, [this](const FInputActionValue&)
	{
		// Nothing at all for a player without the menu: F8 is just a key
		if (AFWPlayerController* PC = GetFWController(); PC && PC->HasModMenu())
		{
			PC->OpenModMenu();
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

#undef LOCTEXT_NAMESPACE
