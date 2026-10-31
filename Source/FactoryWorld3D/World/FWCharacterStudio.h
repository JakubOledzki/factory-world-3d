#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "FWCharacterStudio.generated.h"

class UCameraComponent;
class UMaterialInstanceDynamic;
class USkeletalMeshComponent;

// A character's look as the preview draws it: colours from the server's palettes, styles as indices
struct FFWLook
{
	FLinearColor Skin = FLinearColor::White;
	FLinearColor Hair = FLinearColor::Black;
	FLinearColor Eyes = FLinearColor::Black;
	FLinearColor Shirt = FLinearColor::White;
	FLinearColor Pants = FLinearColor::Gray;
	FLinearColor Shoes = FLinearColor::Gray;
	int32 HairStyle = 0;
	int32 ShirtStyle = 0;
	int32 FacialHair = 0;
	int32 FaceStyle = 0;
};

/**
 * The character creator's photo studio: a closed dark room high above the land (so nothing of the world shows
 * or lights it), a pedestal, three-point lighting and the UE5 mannequin painted as the picked character
 * (M_FWCharacter). The figure turns slowly on its own until the player drags it; the camera zooms from the
 * whole figure to the face. Without the mannequin (Tools/copy_mannequins.ps1 not run) the room stays empty.
 */
UCLASS()
class FACTORYWORLD3D_API AFWCharacterStudio : public AActor
{
	GENERATED_BODY()

public:
	AFWCharacterStudio();

	void SetLook(const FFWLook& Look);
	// Turns the figure (degrees); stops the turntable for a few seconds
	void Spin(float DeltaYaw);
	// 0 = whole figure, 1 = face
	void Zoom(float Delta);
	void SetZoom(float Value);
	bool HasFigure() const;

	virtual void Tick(float DeltaSeconds) override;

protected:
	virtual void BeginPlay() override;

private:
	void UpdateCamera(float DeltaSeconds);

	UPROPERTY()
	TObjectPtr<USceneComponent> Root;
	UPROPERTY()
	TObjectPtr<USceneComponent> Turntable;
	UPROPERTY()
	TObjectPtr<USkeletalMeshComponent> Figure;
	UPROPERTY()
	TObjectPtr<UCameraComponent> Camera;
	UPROPERTY()
	TObjectPtr<UMaterialInstanceDynamic> Paint;

	float Yaw = 200.f;
	float ZoomTarget = 0.f;
	float ZoomNow = 0.f;
	double ManualUntil = 0.0;
};
