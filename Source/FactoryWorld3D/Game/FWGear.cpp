#include "Game/FWGear.h"

#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "Materials/MaterialInstanceDynamic.h"

namespace FWGear
{
	namespace
	{
		FLinearColor PaletteColor(const TSharedPtr<FJsonObject>& Appearance, const TSharedPtr<FJsonObject>& Options, const TCHAR* Key, const TCHAR* Palette)
		{
			int32 Index = 0;
			const TArray<TSharedPtr<FJsonValue>>* Colors = nullptr;
			if (Appearance.IsValid())
			{
				Appearance->TryGetNumberField(Key, Index);
			}
			if (Options.IsValid() && Options->TryGetArrayField(Palette, Colors) && Colors->IsValidIndex(Index))
			{
				// sRGB hex, as the 2D game draws them
				return FLinearColor::FromSRGBColor(FColor::FromHex((*Colors)[Index]->AsString()));
			}
			return FLinearColor::Gray;
		}

		int32 Index(const TSharedPtr<FJsonObject>& Appearance, const TCHAR* Key)
		{
			int32 Value = 0;
			if (Appearance.IsValid())
			{
				Appearance->TryGetNumberField(Key, Value);
			}
			return Value;
		}
	}

	FFWLook MakeLook(const TSharedPtr<FJsonObject>& Appearance, const TSharedPtr<FJsonObject>& Options)
	{
		FFWLook Look;
		Look.Skin = PaletteColor(Appearance, Options, TEXT("skinTone"), TEXT("skinTones"));
		Look.Hair = PaletteColor(Appearance, Options, TEXT("hairColor"), TEXT("hairColors"));
		Look.Eyes = PaletteColor(Appearance, Options, TEXT("eyeColor"), TEXT("eyeColors"));
		Look.Shirt = PaletteColor(Appearance, Options, TEXT("shirtColor"), TEXT("clothColors"));
		Look.Pants = PaletteColor(Appearance, Options, TEXT("pantsColor"), TEXT("clothColors"));
		Look.Shoes = PaletteColor(Appearance, Options, TEXT("shoesColor"), TEXT("clothColors"));
		Look.HairStyle = Index(Appearance, TEXT("hairStyle"));
		Look.ShirtStyle = Index(Appearance, TEXT("shirtStyle"));
		Look.FacialHair = Index(Appearance, TEXT("facialHair"));
		Look.FaceStyle = Index(Appearance, TEXT("faceStyle"));
		return Look;
	}

	USkeletalMeshComponent* Wear(AActor* Owner, USceneComponent* Parent, USkinnedMeshComponent* Leader,
		TMap<FString, TObjectPtr<USkeletalMeshComponent>>& Gear, const FString& Kind, const FString& Piece,
		const FLinearColor& Color, bool bArmsOnly)
	{
		for (TPair<FString, TObjectPtr<USkeletalMeshComponent>>& Pair : Gear)
		{
			if (Pair.Key.StartsWith(Kind) && Pair.Value)
			{
				Pair.Value->SetVisibility(Pair.Key == Piece);
			}
		}
		if (Piece.IsEmpty() || !Leader)
		{
			return nullptr;
		}
		TObjectPtr<USkeletalMeshComponent>* Found = Gear.Find(Piece);
		if (!Found)
		{
			USkeletalMesh* Mesh = LoadObject<USkeletalMesh>(nullptr, *FString::Printf(TEXT("/Game/External/Character/%s.%s"), *Piece, *Piece), nullptr, LOAD_NoWarn | LOAD_Quiet);
			USkeletalMeshComponent* Part = nullptr;
			if (Mesh)
			{
				Part = NewObject<USkeletalMeshComponent>(Owner);
				Part->SetupAttachment(Parent ? Parent : Leader);
				Part->SetSkeletalMesh(Mesh);
				Part->SetCollisionEnabled(ECollisionEnabled::NoCollision);
				// Follow the leader's look in the world: first-person or the hidden body casting the shadow
				Part->FirstPersonPrimitiveType = Leader->FirstPersonPrimitiveType;
				Part->SetOwnerNoSee(Leader->bOwnerNoSee);
				Part->SetOnlyOwnerSee(Leader->bOnlyOwnerSee);
				Part->SetCastShadow(Leader->CastShadow);
				Part->bCastHiddenShadow = Leader->bCastHiddenShadow;
				Part->RegisterComponent();
				Part->SetLeaderPoseComponent(Leader);
				// Each slot is named after its material (FW_Hair, FW_Jersey...): MI_<slot> made by Tools/import_external.py
				const TArray<FName> Slots = Part->GetMaterialSlotNames();
				for (int32 Slot = 0; Slot < Slots.Num(); ++Slot)
				{
					const FString Name = FString::Printf(TEXT("MI_%s"), *Slots[Slot].ToString());
					UMaterialInterface* Base = LoadObject<UMaterialInterface>(nullptr, *FString::Printf(TEXT("/Game/External/Character/Materials/%s.%s"), *Name, *Name));
					UMaterialInstanceDynamic* Material = UMaterialInstanceDynamic::Create(Base ? Base : Part->GetMaterial(Slot), Owner);
					if (bArmsOnly && Base)
					{
						// The same fabric through the arms-only parent: copy the textures over
						if (UMaterialInterface* ArmsParent = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/Materials/M_FWClothArms.M_FWClothArms")))
						{
							UMaterialInstanceDynamic* Arms = UMaterialInstanceDynamic::Create(ArmsParent, Owner);
							for (const TCHAR* Texture : { TEXT("Diffuse"), TEXT("ARM"), TEXT("NormalMap") })
							{
								UTexture* Value = nullptr;
								if (Base->GetTextureParameterValue(FHashedMaterialParameterInfo(Texture), Value) && Value)
								{
									Arms->SetTextureParameterValue(Texture, Value);
								}
							}
							float Scale = 18.f;
							if (Base->GetScalarParameterValue(FHashedMaterialParameterInfo(TEXT("Scale")), Scale))
							{
								Arms->SetScalarParameterValue(TEXT("Scale"), Scale);
							}
							Material = Arms;
						}
					}
					Part->SetMaterial(Slot, Material);
				}
			}
			// Missing pieces (Tools/setup_assets.ps1 not run) are remembered as none: the painted body shows instead
			Found = &Gear.Add(Piece, Part);
		}
		USkeletalMeshComponent* Part = *Found;
		if (Part)
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
		return Part;
	}

	void Dress(AActor* Owner, USkinnedMeshComponent* Body, TMap<FString, TObjectPtr<USkeletalMeshComponent>>& Gear,
		const FFWLook& Look, bool bArmsOnly)
	{
		if (!Body)
		{
			return;
		}
		UMaterialInterface* Paint = LoadObject<UMaterialInterface>(nullptr, bArmsOnly
			? TEXT("/Game/Materials/M_FWCharacterArms.M_FWCharacterArms") : TEXT("/Game/Materials/M_FWCharacter.M_FWCharacter"));
		if (Paint)
		{
			UMaterialInstanceDynamic* Material = Cast<UMaterialInstanceDynamic>(Body->GetMaterial(0));
			if (!Material || Material->Parent != Paint)
			{
				Material = UMaterialInstanceDynamic::Create(Paint, Owner);
				for (int32 Slot = 0; Slot < Body->GetNumMaterials(); ++Slot)
				{
					Body->SetMaterial(Slot, Material);
				}
			}
			Material->SetVectorParameterValue(TEXT("Skin"), Look.Skin);
			Material->SetVectorParameterValue(TEXT("Hair"), Look.Hair);
			Material->SetVectorParameterValue(TEXT("Eyes"), Look.Eyes);
			Material->SetVectorParameterValue(TEXT("Shirt"), Look.Shirt);
			Material->SetVectorParameterValue(TEXT("Pants"), Look.Pants);
			Material->SetVectorParameterValue(TEXT("Shoes"), Look.Shoes);
			Material->SetScalarParameterValue(TEXT("HairStyle"), Look.HairStyle);
			Material->SetScalarParameterValue(TEXT("ShirtStyle"), Look.ShirtStyle);
			Material->SetScalarParameterValue(TEXT("FacialHair"), Look.FacialHair);
			Material->SetScalarParameterValue(TEXT("FaceStyle"), Look.FaceStyle);
		}
		USceneComponent* Parent = Body;
		// First person: only the sleeves are seen; hair and the rest would only cost
		if (!bArmsOnly)
		{
			Wear(Owner, Parent, Body, Gear, TEXT("hair"), Look.HairStyle > 0 ? FString::Printf(TEXT("hair_%d"), Look.HairStyle) : FString(), Look.Hair);
			Wear(Owner, Parent, Body, Gear, TEXT("beard"), Look.FacialHair > 0 ? FString::Printf(TEXT("beard_%d"), Look.FacialHair) : FString(), Look.Hair);
			Wear(Owner, Parent, Body, Gear, TEXT("pants"), TEXT("pants"), Look.Pants);
			Wear(Owner, Parent, Body, Gear, TEXT("shoes"), TEXT("shoes"), Look.Shoes);
		}
		Wear(Owner, Parent, Body, Gear, TEXT("shirt"), FString::Printf(TEXT("shirt_%d"), Look.ShirtStyle), Look.Shirt, bArmsOnly);
	}
}
