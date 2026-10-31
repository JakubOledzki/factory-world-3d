#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"

class AActor;
class USceneComponent;
class USkeletalMeshComponent;
class USkinnedMeshComponent;

// A character's look as it is drawn: colours from the server's palettes, styles as indices
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
 * Dressing the mannequin in the character's hair and clothes (Tools/blender/make_character_gear.py, imported to
 * /Game/External/Character): one skeletal mesh per piece following a leader's pose, coloured per material slot.
 */
namespace FWGear
{
	// The look from a character's "appearance" (indices) and the palettes of "character.options"
	FFWLook MakeLook(const TSharedPtr<FJsonObject>& Appearance, const TSharedPtr<FJsonObject>& Options);

	// Shows the piece (e.g. "hair_3") of a kind ("hair"), creating it the first time, and hides the others of that
	// kind; an empty piece hides them all. ArmsOnly pieces use the first-person "...Arms" materials. Returns the piece.
	USkeletalMeshComponent* Wear(AActor* Owner, USceneComponent* Parent, USkinnedMeshComponent* Leader,
		TMap<FString, TObjectPtr<USkeletalMeshComponent>>& Gear, const FString& Kind, const FString& Piece,
		const FLinearColor& Color, bool bArmsOnly = false);

	// Dresses a figure in the whole look (body paint on its own material, every piece over it)
	void Dress(AActor* Owner, USkinnedMeshComponent* Body, TMap<FString, TObjectPtr<USkeletalMeshComponent>>& Gear,
		const FFWLook& Look, bool bArmsOnly = false);
}
