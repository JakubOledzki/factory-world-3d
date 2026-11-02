#pragma once

#include "CoreMinimal.h"
#include "Game/FWGear.h"
#include "GameFramework/Character.h"
#include "FWCharacter.generated.h"

class AFWPlayerController;
class AFWPlot;
class UCameraComponent;
class UInputAction;
class UInputMappingContext;
class UMaterialInstanceDynamic;
class UPoseableMeshComponent;
class USkeletalMeshComponent;
class UStaticMeshComponent;
struct FInputActionValue;

/**
 * First-person player on a plot. Looks at a tile with the crosshair; holding E swings the axe at a tree or the
 * pickaxe at a rock or a mine (five strikes fell, break or dig a load, shown by a ring of five round the crosshair),
 * holding the right button picks an object up, and in build mode a ghost of the hotbar item shows where the left
 * button places it.
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
	// 0..1 while holding the right button on something, otherwise -1
	float GetProgress() const;
	// Strikes of the axe or pickaxe: how many are left of how many, and seconds since the last one struck; false when
	// nothing is being struck
	bool GetStrikes(int32& OutLeft, int32& OutTotal, float& OutSinceHit) const;
	static constexpr int32 StrikeCount = 5;
	FText GetTargetText() const;

	// Console: switches build mode as B does
	UFUNCTION(Exec)
	void FWBuildMode() { ToggleBuild(); }
	// Console: logs the movement mode and how far the feet are above the ground
	UFUNCTION(Exec)
	void FWGroundCheck();
	UFUNCTION(Exec)
	void FWJump() { Jump(); }
	// Console: holds E down (1) or lets it go (0), as when striking with the axe or pickaxe
	UFUNCTION(Exec)
	void FWStrike(int32 bHold) { bHold ? InteractStart() : InteractStop(); }

	// The character's look (from the server): the first-person arms wear its sleeves and skin, and the body that
	// casts the player's shadow all of it
	void SetLook(const FFWLook& Look);
	// From the settings menu
	void ApplyUserSettings(float InMouseSensitivity, float FieldOfView);

	// One-off movements of the first-person arms
	enum class EGesture : uint8 { None, Place, Rotate, Pickup, Swing };
	void PlayGesture(EGesture Gesture);
	// Console: plays a gesture (place, rotate, pickup, swing)
	UFUNCTION(Exec)
	void FWGesture(const FString& Name);
	// Console: picks a hotbar slot (0-based), as 1-5 or the mouse wheel do
	UFUNCTION(Exec)
	void FWSelect(int32 Slot) { SelectSlot(Slot); }
	// Console: tries a shirt (0 t-shirt, 1 tank top, 2 hoodie) in a colour (hex), for screenshots
	UFUNCTION(Exec)
	void FWDress(int32 ShirtStyle, const FString& ShirtColor = TEXT("e67e22"));

protected:
	virtual void BeginPlay() override;
	virtual void PawnClientRestart() override;
	virtual void OnJumped_Implementation() override;
	virtual void Landed(const FHitResult& Hit) override;

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
	// First-person arms: hands held in front by two-bone IK, swaying with breath, steps and turning, plus gestures
	void CreateBody();
	void UpdateArms(float DeltaSeconds);
	void UpdateBodyAnimation();
	// Fingers: Amount 0 straight (the bind pose) .. 1 a fist round a handle
	void CurlFingers(int32 Side, float Amount);
	// The axe meets the tree or the pickaxe the rock: sound, a jolt of the view, chips flying and a mark where it struck
	void Strike();
	// Starts swinging at a mine (MineId) or at the tree or rock on Tile (MineId < 0); Seconds is the whole work, shared
	// out over the strikes. On the same target as just before, the strikes go on where they were.
	void BeginStrike(int32 MineId, FIntPoint Tile, float Seconds, bool bAxe);
	// A strike landed: counted by the server, the last one gathering
	void OnImpact();
	// Footsteps by the surface underfoot: "grass", "rock" or "metal" (anything built)
	void UpdateFootsteps(float DeltaSeconds);
	FString SurfaceUnder(const FHitResult* Hit = nullptr) const;
	float StepDistance = 0.f;
	float MouseSensitivity = 1.f;

	UPROPERTY()
	TObjectPtr<UPoseableMeshComponent> Arms;
	UPROPERTY()
	TObjectPtr<USkeletalMeshComponent> Body;
	UPROPERTY()
	TMap<FString, TObjectPtr<USkinnedMeshComponent>> ArmsGear;
	UPROPERTY()
	TMap<FString, TObjectPtr<USkinnedMeshComponent>> BodyGear;
	struct FArmRef
	{
		FTransform Upper, Lower, Hand;
	};
	FArmRef ArmRef[2];
	bool bArmsReady = false;
	FRotator LastView = FRotator::ZeroRotator;
	FVector2D ViewLag = FVector2D::ZeroVector;
	EGesture Gesture = EGesture::None;
	float GestureTime = 0.f;
	float BuildRaise = 0.f;
	FString BodyAnimation;
	UPROPERTY()
	TObjectPtr<UStaticMeshComponent> Tool;
	float ToolShown = 0.f;
	// The hotbar item in the hands: a small model of it held up between them (the pickaxe in the right hand);
	// changing slots lowers the hands and brings the new one up
	UPROPERTY()
	TObjectPtr<UStaticMeshComponent> Held;
	FString HeldItem;
	float EquipTime = -1.f;
	float HeldSpin = 0.f;
	void UpdateHeld(float DeltaSeconds);
	void Scroll(const FInputActionValue& Value);
	float SwingRaise = 0.f;
	float SwingStrike = 0.f;
	bool bStruck = false;
	float ShakeTime = 0.f;
	FVector ShakeOffset = FVector::ZeroVector;
	// Striking: the target, strikes landed so far, the time each strike takes, how long a swing lasts, when the last one
	// landed (world seconds), and whether it is the axe (a tree) or the pickaxe
	FIntPoint StrikeTile = FIntPoint(-1, -1);
	int32 StrikeHits = 0;
	float StrikeInterval = 1.f;
	float SwingLength = 0.6f;
	float LastImpact = -100.f;
	bool bStrikeAxe = false;
	UPROPERTY()
	TObjectPtr<UStaticMesh> PickaxeMesh;
	UPROPERTY()
	TObjectPtr<UStaticMesh> AxeMesh;

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

	enum class EHold : uint8 { None, Strike, Pickup };
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
