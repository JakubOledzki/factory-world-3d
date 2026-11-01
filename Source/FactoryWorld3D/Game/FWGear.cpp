#include "Game/FWGear.h"

#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "Game/FWHeadComponent.h"
#include "ReferenceSkeleton.h"
#include "Materials/MaterialInstanceDynamic.h"

namespace FWGear
{
	namespace
	{
		// The skin tone the MetaHuman heads are baked in, and the gain from a palette colour to the iris tint
		const FLinearColor BakedSkin(0.25f, 0.1f, 0.055f);
		constexpr float IrisGain = 1.f;

		// Our hair and facial hair are made around the mannequin's head: on a MetaHuman head they are moved (cm, in the
		// body's bind pose) to its skull and chin, and the hair a little enlarged over its wider skull. Measured from the
		// heads Tools/make_metahumans.py builds (their skull, chin and eyes against the mannequin's, from the head bone),
		// then set by eye.
		struct FHeadFit
		{
			FVector Hair;
			FVector Beard;
		};
		const FHeadFit HeadFits[] = {
			{ FVector(0, -0.5f, 0), FVector(0, -1.8f, -1.6f) },
			{ FVector(0, 0.3f, 0), FVector(0, -1.6f, -1.0f) },
			{ FVector(0, 0.4f, 0.5f), FVector(0, -1.2f, -1.2f) },
			{ FVector(0, -1.f, -1.5f), FVector(0, -4.f, -1.8f) },
		};
		constexpr float HairScale = 1.1f;
		// The head's centre in the mannequin's bind pose: the hair grows from there
		const FVector HeadCentre(0, 2.f, 170.f);

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
		TMap<FString, TObjectPtr<USkinnedMeshComponent>>& Gear, const FString& Kind, const FString& Piece,
		const FLinearColor& Color, bool bArmsOnly)
	{
		for (TPair<FString, TObjectPtr<USkinnedMeshComponent>>& Pair : Gear)
		{
			if (Pair.Key.StartsWith(Kind) && Pair.Value)
			{
				Pair.Value->SetVisibility(Pair.Key == Piece, true);
			}
		}
		if (Piece.IsEmpty() || !Leader)
		{
			return nullptr;
		}
		TObjectPtr<USkinnedMeshComponent>* Found = Gear.Find(Piece);
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
		USkeletalMeshComponent* Part = Cast<USkeletalMeshComponent>(*Found);
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

	UFWHeadComponent* WearHead(AActor* Owner, USkinnedMeshComponent* Body, TMap<FString, TObjectPtr<USkinnedMeshComponent>>& Gear,
		const FFWLook& Look)
	{
		const FString Piece = FString::Printf(TEXT("face_%d"), Look.FaceStyle);
		for (TPair<FString, TObjectPtr<USkinnedMeshComponent>>& Pair : Gear)
		{
			if (Pair.Key.StartsWith(TEXT("face_")) && Pair.Value)
			{
				Pair.Value->SetVisibility(Pair.Key == Piece);
			}
		}
		TObjectPtr<USkinnedMeshComponent>* Found = Gear.Find(Piece);
		if (!Found)
		{
			const FString Name = FString::Printf(TEXT("SKM_FW_Face%d_FaceMesh"), Look.FaceStyle);
			USkeletalMesh* Mesh = LoadObject<USkeletalMesh>(nullptr, *FString::Printf(TEXT("/Game/MetaHumans/Built/FW_Face%d/Face/%s.%s"), Look.FaceStyle, *Name, *Name), nullptr, LOAD_NoWarn | LOAD_Quiet);
			UFWHeadComponent* Head = nullptr;
			if (Mesh && Mesh->GetRefSkeleton().FindBoneIndex(TEXT("head")) != INDEX_NONE)
			{
				// Posed from the body every frame (the face stands as built: no facial animation)
				Head = NewObject<UFWHeadComponent>(Owner);
				Head->SetupAttachment(Body);
				Head->SetSkinnedAssetAndUpdate(Mesh);
				Head->SetCollisionEnabled(ECollisionEnabled::NoCollision);
				Head->FirstPersonPrimitiveType = Body->FirstPersonPrimitiveType;
				Head->SetOwnerNoSee(Body->bOwnerNoSee);
				Head->SetCastShadow(Body->CastShadow);
				Head->bCastHiddenShadow = Body->bCastHiddenShadow;
				if (Body->bOwnerNoSee)
				{
					// Only its shadow is seen by the player: the lightest level of detail is enough
					Head->SetForcedLOD(Mesh->GetLODNum());
				}
				Head->RegisterComponent();
				for (int32 Slot = 0; Slot < Head->GetNumMaterials(); ++Slot)
				{
					if (UMaterialInterface* Base = Head->GetMaterial(Slot))
					{
						Head->SetMaterial(Slot, UMaterialInstanceDynamic::Create(Base, Owner));
					}
				}
			}
			Found = &Gear.Add(Piece, Head);
		}
		UFWHeadComponent* Head = Cast<UFWHeadComponent>(*Found);
		if (Head)
		{
			Head->SetVisibility(true, true);
			const TArray<FName> Slots = Head->GetMaterialSlotNames();
			for (int32 Slot = 0; Slot < Slots.Num(); ++Slot)
			{
				UMaterialInstanceDynamic* Material = Cast<UMaterialInstanceDynamic>(Head->GetMaterial(Slot));
				const FString SlotName = Slots[Slot].ToString();
				if (!Material)
				{
					continue;
				}
				if (SlotName.StartsWith(TEXT("head_")))
				{
					// The baked skin is the one tone all heads were built in (Tools/make_metahumans.py): scaled to the
					// palette's
					Material->SetVectorParameterValue(TEXT("Basecolor Global Multiply Post-Bake"), Look.Skin / BakedSkin);
				}
				else if (SlotName.StartsWith(TEXT("eyeLeft")) || SlotName.StartsWith(TEXT("eyeRight")))
				{
					Material->SetVectorParameterValue(TEXT("Iris Color Multiply"), Look.Eyes * IrisGain);
				}
			}
		}
		return Head;
	}

	void Dress(AActor* Owner, USkinnedMeshComponent* Body, TMap<FString, TObjectPtr<USkinnedMeshComponent>>& Gear,
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
		// The MetaHuman head over the painted one, which is then cut away
		const bool bHead = !bArmsOnly && WearHead(Owner, Body, Gear, Look) != nullptr;
		if (UMaterialInstanceDynamic* Material = Cast<UMaterialInstanceDynamic>(Body->GetMaterial(0)))
		{
			Material->SetScalarParameterValue(TEXT("HideHead"), bHead ? 1.f : 0.f);
			// Down to where the MetaHuman neck meets the shoulders (bind-pose height, cm)
			Material->SetScalarParameterValue(TEXT("HeadCut"), 149.f);
		}
		USceneComponent* Parent = Body;
		// First person: only the sleeves are seen; hair and the rest would only cost
		if (!bArmsOnly)
		{
			USkeletalMeshComponent* Hair = Wear(Owner, Parent, Body, Gear, TEXT("hair"), Look.HairStyle > 0 ? FString::Printf(TEXT("hair_%d"), Look.HairStyle) : FString(), Look.Hair);
			USkeletalMeshComponent* Beard = Wear(Owner, Parent, Body, Gear, TEXT("beard"), Look.FacialHair > 0 ? FString::Printf(TEXT("beard_%d"), Look.FacialHair) : FString(), Look.Hair);
			const FHeadFit* Fit = bHead && Look.FaceStyle >= 0 && Look.FaceStyle < UE_ARRAY_COUNT(HeadFits) ? &HeadFits[Look.FaceStyle] : nullptr;
			if (Hair)
			{
				// Enlarged about the head's centre
				const float Scale = Fit ? HairScale : 1.f;
				Hair->SetRelativeScale3D(FVector(Scale));
				Hair->SetRelativeLocation((Fit ? Fit->Hair : FVector::ZeroVector) + HeadCentre * (1.f - Scale));
			}
			if (Beard)
			{
				Beard->SetRelativeLocation(Fit ? Fit->Beard : FVector::ZeroVector);
			}
			Wear(Owner, Parent, Body, Gear, TEXT("pants"), TEXT("pants"), Look.Pants);
			Wear(Owner, Parent, Body, Gear, TEXT("shoes"), TEXT("shoes"), Look.Shoes);
		}
		Wear(Owner, Parent, Body, Gear, TEXT("shirt"), FString::Printf(TEXT("shirt_%d"), Look.ShirtStyle), Look.Shirt, bArmsOnly);
	}
}
