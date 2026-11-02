#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "FWGarageRoom.generated.h"

class UMaterialInstanceDynamic;
class UPointLightComponent;
class UStaticMesh;
class UStaticMeshComponent;

/**
 * The player's garage as a room, as the 2D game draws it (garage.gd): 8 x 6 m, the roller door in the front wall,
 * a lamp under the ceiling, and the furniture where garage.get puts it (16 x 12 cells of 0.5 m). Its upgrades show:
 * the floor and the walls change their surface level by level, the light gets brighter with more lamps, more shelves
 * go up on the back wall, the heating brings a stove and radiators. The models come from Tools/blender/make_garage.py.
 * It stands high above the plot, out of sight of it.
 */
UCLASS()
class FACTORYWORLD3D_API AFWGarageRoom : public AActor
{
	GENERATED_BODY()

public:
	AFWGarageRoom();

	// Lays out the furniture and the upgrades from a garage.get reply (again only when they changed)
	void Build(const TSharedPtr<FJsonObject>& Garage);

	// Where the player stands on coming in: inside the door, looking into the room
	FTransform Entrance() const;

	// What a line from the eye hits: a piece of furniture (its kind and id), "door" or "board"; false for nothing
	bool Pick(const FVector& From, const FVector& To, const AActor* Ignore, FString& OutKind, FString& OutId) const;

	// A piece of furniture's name by its kind, from garage.get
	FString NameOf(const FString& Kind) const;
	// The kind of the piece with this id, empty for none; the id of the first piece of a kind, empty for none
	FString KindOf(const FString& Id) const;
	FString IdOf(const FString& Kind) const;
	// The middle of the first piece of this kind (its bounds), false for none
	bool CentreOf(const FString& Kind, FVector& OutCentre) const;

	// Whether this kind can stand in the garage (garage.get lists it among the furniture)
	bool IsFurniture(const FString& Kind) const;
	// Cells a piece of this kind takes (along x and y) turned by Rotation quarter turns
	FIntPoint FootprintOf(const FString& Kind, int32 Rotation) const { return Footprint(Kind, Rotation); }
	// The model a piece of this kind is drawn with
	UStaticMesh* MeshOf(const FString& Kind) const;
	// Where a piece of this kind, turned by Rotation quarter turns, would stand with its middle where a line from the eye
	// meets the floor (or under what it hits first, Ignore aside): its front-left cell (as garage.place takes it), where its
	// model goes in the world, and whether it fits there without touching anything; false when the line reaches nothing
	bool Placement(const FString& Kind, int32 Rotation, const FVector& From, const FVector& To, const AActor* Ignore, FIntPoint& OutCell,
		FTransform& OutTransform, bool& bOutFits) const;

private:
	UStaticMeshComponent* Piece(const TCHAR* Model, const FTransform& Transform, bool bCollide = true);
	UMaterialInstanceDynamic* Surface(const FString& Set, const FLinearColor& Tint);
	void Shell();
	void Upgrades(const TSharedPtr<FJsonObject>& Levels);

	// Cells taken (width along x, depth along y) by a piece of this kind turned by Rotation quarter turns
	FIntPoint Footprint(const FString& Kind, int32 Rotation) const;
	// Where a piece's model goes in the room (relative to it), its front-left cell at Cell
	static FTransform PieceTransform(const FIntPoint& Cell, const FIntPoint& Size, int32 Rotation);

	FString Built;
	TSharedPtr<FJsonObject> Furniture;
	// The cells each piece of furniture stands on, to tell where another one fits
	TArray<FIntRect> Taken;

	UPROPERTY()
	TObjectPtr<UStaticMeshComponent> Floor;
	UPROPERTY()
	TObjectPtr<UStaticMeshComponent> Walls;
	UPROPERTY()
	TArray<TObjectPtr<UStaticMeshComponent>> Pieces;
	UPROPERTY()
	TArray<TObjectPtr<UPointLightComponent>> Lights;
	UPROPERTY()
	TObjectPtr<UMaterialInterface> ScanMaterial;

	// What each piece of furniture is, by its component: kind and id
	TMap<TWeakObjectPtr<const UPrimitiveComponent>, TPair<FString, FString>> Picks;
};
