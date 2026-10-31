#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Character.h"
#include "FWCharacter.generated.h"

class AFWPlayerController;
class AFWPlot;
class UCameraComponent;
class UInputAction;
class UInputMappingContext;
class UMaterialInstanceDynamic;
class UStaticMeshComponent;
struct FInputActionValue;

/**
 * First-person player on a plot. Looks at a tile with the crosshair; holding E mines a mine or fills
 * a chest, holding the right button picks an object up, and in build mode a ghost of the hotbar item
 * shows where the left button places it.
 */
UCLASS()
class FACTORYWORLD3D_API AFWCharacter : public ACharacter
{
	GENERATED_BODY()

public:
	AFWCharacter();

	virtual void Tick(float DeltaSeconds) override;
	virtual void SetupPlayerInputComponent(UInputComponent* PlayerInputComponent) override;

	bool IsBuildMode() const { return bBuildMode; }
	bool HasTarget() const { return bHasTarget; }
	// 0..1 while holding E or the right button on something, otherwise -1
	float GetProgress() const;
	FText GetTargetText() const;

	// Console: switches build mode as B does
	UFUNCTION(Exec)
	void FWBuildMode() { ToggleBuild(); }
	// Console: logs the movement mode and how far the feet are above the ground
	UFUNCTION(Exec)
	void FWGroundCheck();
	UFUNCTION(Exec)
	void FWJump() { Jump(); }

protected:
	virtual void BeginPlay() override;
	virtual void PawnClientRestart() override;

private:
	void CreateInput();
	void Move(const FInputActionValue& Value);
	void Look(const FInputActionValue& Value);
	void SprintStart();
	void SprintStop();
	void InteractStart();
	void InteractStop();
	void PickupStart();
	void PickupStop();
	void Primary();
	void ToggleBuild();
	void Rotate();
	void SelectSlot(int32 Slot);

	void UpdateTarget();
	void UpdateGhost();
	void UpdateHold(float DeltaSeconds);
	void UpdateHeadBob(float DeltaSeconds);

	AFWPlayerController* GetFWController() const;
	AFWPlot* GetPlot() const;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UCameraComponent> Camera;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<UStaticMeshComponent> Ghost;

	UPROPERTY()
	TObjectPtr<UMaterialInstanceDynamic> GhostMaterial;

	UPROPERTY()
	TObjectPtr<UInputMappingContext> Mapping;

	UPROPERTY()
	TMap<FName, TObjectPtr<UInputAction>> Actions;

	bool bBuildMode = false;
	int32 BuildRotation = 0;
	bool bHasTarget = false;
	FIntPoint TargetTile = FIntPoint::ZeroValue;

	enum class EHold : uint8 { None, Mine, Pickup };
	EHold Hold = EHold::None;
	float HoldTime = 0.f;
	float HoldNeeded = 1.f;
	bool bWaiting = false;
	bool bInteractHeld = false;
	int32 HoldMine = -1;
	FString HoldObject;

	float WalkSpeed = 450.f;
	float SprintSpeed = 900.f;
	float EyeHeight = 165.f;
	float BobPhase = 0.f;
};
