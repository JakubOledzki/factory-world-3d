// Using the world: interacting, striking, picking up, placing, and what is aimed at.
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
#include "World/FWGarageRoom.h"
#include "World/FWPlot.h"

#define LOCTEXT_NAMESPACE "FWCharacter"

#include "FWCharacterHelpers.h"

void AFWCharacter::Strike()
{
	FWAudio::Play2D(this, bStrikeAxe ? TEXT("axe_chop") : TEXT("mine_hit"), 0.6f, 3);
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
	// Chips of rock, or of pale wood from under the bark
	const TArray<UStaticMesh*> Rocks = FWArt::Rocks();
	AFWPlot* Plot = GetPlot();
	UMaterialInterface* RockMaterial = bStrikeAxe ? (Plot ? Plot->SurfaceMaterial(FLinearColor(0.62f, 0.45f, 0.26f), 0.85f) : nullptr)
		: Rocks.IsEmpty() ? nullptr : Rocks[0]->GetMaterial(0);
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
		Piece->SetWorldScale3D(bStrikeAxe ? FVector(FMath::FRandRange(0.04f, 0.08f), FMath::FRandRange(0.02f, 0.04f), FMath::FRandRange(0.006f, 0.012f))
			: FVector(FMath::FRandRange(0.035f, 0.07f), FMath::FRandRange(0.03f, 0.06f), FMath::FRandRange(0.02f, 0.045f)));
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
			Material->SetVectorParameterValue(TEXT("Tint"), bStrikeAxe ? FLinearColor(0.5f, 0.36f, 0.2f) : FLinearColor(0.12f, 0.11f, 0.1f));
			Material->SetScalarParameterValue(TEXT("Strength"), 0.9f);
			const FRotator Facing = (-Hit.ImpactNormal).Rotation() + FRotator(0.f, 0.f, FMath::FRandRange(0.f, 360.f));
			if (UDecalComponent* Decal = UGameplayStatics::SpawnDecalAttached(Material, FVector(8.f, 16.f, 16.f), Hit.GetComponent(), NAME_None, Hit.ImpactPoint, Facing, EAttachLocation::KeepWorldPosition, 12.f))
			{
				Decal->SetFadeOut(8.f, 4.f);
			}
		}
	}
}

void AFWCharacter::InteractStart()
{
	bInteractHeld = true;
	// In the garage room, E uses what is looked at there
	if (AFWPlayerController* InGarage = GetFWController(); InGarage && InGarage->IsInGarage())
	{
		InGarage->GarageInteract();
		return;
	}
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
		BeginStrike(Mine->Id, TargetTile, Mine->Seconds, false);
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
	else if (!Plot->FindObjectAt(TargetTile) && Plot->IsTree(TargetTile))
	{
		BeginStrike(-1, TargetTile, Plot->GetChopSeconds(), true);
	}
	else if (!Plot->FindObjectAt(TargetTile) && Plot->TileCode(TargetTile) == FWCatalog::Rock)
	{
		BeginStrike(-1, TargetTile, Plot->GetRockSeconds(), false);
	}
}

void AFWCharacter::BeginStrike(int32 MineId, FIntPoint Tile, float Seconds, bool bAxe)
{
	const float Now = GetWorld()->GetTimeSeconds();
	// The same target struck again soon after goes on from the strikes it had (the server keeps them as long)
	const bool bSame = MineId >= 0 ? HoldMine == MineId : HoldMine < 0 && StrikeTile == Tile;
	if (!bSame || Now - LastImpact > 14.f)
	{
		StrikeHits = 0;
	}
	Hold = EHold::Strike;
	HoldMine = MineId;
	StrikeTile = Tile;
	StrikeInterval = FMath::Max(0.25f, Seconds / StrikeCount);
	SwingLength = FMath::Clamp(StrikeInterval, 0.42f, 0.85f);
	bStrikeAxe = bAxe;
	if (Tool)
	{
		UStaticMesh* InHand = bAxe && AxeMesh ? AxeMesh.Get() : PickaxeMesh.Get();
		if (InHand && Tool->GetStaticMesh() != InHand)
		{
			Tool->SetStaticMesh(InHand);
		}
	}
}

void AFWCharacter::OnImpact()
{
	AFWPlayerController* PC = GetFWController();
	if (!PC)
	{
		return;
	}
	LastImpact = GetWorld()->GetTimeSeconds();
	++StrikeHits;
	if (StrikeHits < StrikeCount)
	{
		PC->StrikeHit(HoldMine, StrikeTile);
		return;
	}
	// The last strike gathers: a tree falls or a rock breaks (and that is the end of it), a mine gives a load (and
	// digging goes on while E is held)
	bWaiting = true;
	TWeakObjectPtr<AFWCharacter> WeakThis(this);
	auto Done = [WeakThis](bool bOk)
	{
		if (AFWCharacter* This = WeakThis.Get())
		{
			This->bWaiting = false;
			This->StrikeHits = 0;
			if (!bOk || !This->bInteractHeld || This->HoldMine < 0)
			{
				This->Hold = EHold::None;
			}
		}
	};
	if (HoldMine >= 0)
	{
		PC->Mine(HoldMine, Done);
	}
	else
	{
		PC->Harvest(StrikeTile, Done);
	}
}

bool AFWCharacter::GetStrikes(int32& OutLeft, int32& OutTotal, float& OutSinceHit) const
{
	const float Now = GetWorld() ? GetWorld()->GetTimeSeconds() : 0.f;
	// While swinging, and for a moment after letting go of E with strikes landed, as long as the same target is looked at
	const AFWPlot* Plot = GetPlot();
	const bool bOnTarget = bHasTarget && Plot && (HoldMine >= 0 ? Plot->FindMine(TargetTile) && Plot->FindMine(TargetTile)->Id == HoldMine : TargetTile == StrikeTile);
	if (!(Hold == EHold::Strike || (StrikeHits > 0 && Now - LastImpact < 3.f)) || !bOnTarget)
	{
		return false;
	}
	OutLeft = StrikeCount - StrikeHits;
	OutTotal = StrikeCount;
	OutSinceHit = Now - LastImpact;
	return true;
}

void AFWCharacter::InteractStop()
{
	bInteractHeld = false;
	if (Hold == EHold::Strike && !bWaiting)
	{
		Hold = EHold::None;
	}
}

void AFWCharacter::PickupStart()
{
	// In the garage: the piece of furniture looked at, put away into the backpack
	if (AFWPlayerController* PC = GetFWController(); PC && PC->IsInGarage())
	{
		// Carrying one to its place: it stays in the backpack
		if (!GaragePlacing.IsEmpty())
		{
			PC->Toast(FText::Format(LOCTEXT("LeftInBag", "{0} zostaje w plecaku."),
				FText::FromString(PC->GetGarageRoom() ? PC->GetGarageRoom()->NameOf(GaragePlacing) : PC->ItemName(GaragePlacing))));
			SetGaragePlacing(FString());
			return;
		}
		FString Kind, Id;
		if (Hold == EHold::None && PC->GetGarageTarget(Kind, Id) && Kind != TEXT("door") && Kind != TEXT("board"))
		{
			Hold = EHold::Pickup;
			bGaragePickup = true;
			HoldObject = Id;
			HoldNeeded = PickupSeconds;
			HoldTime = 0.f;
		}
		return;
	}
	bGaragePickup = false;
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
	// In the garage: the piece carried is put down where its ghost stands
	if (PC && PC->IsInGarage())
	{
		FIntPoint Cell;
		FTransform Where;
		bool bFits = false;
		if (GaragePlacing.IsEmpty() || !GaragePlacement(Cell, Where, bFits))
		{
			return;
		}
		if (!bFits)
		{
			FWAudio::Play2D(this, TEXT("build_invalid"), 0.6f);
			PC->Toast(LOCTEXT("GarageBlocked", "Tu coś już stoi: wybierz wolne miejsce."), true);
			return;
		}
		PlayGesture(EGesture::Place);
		PC->GaragePlace(GaragePlacing, Cell, BuildRotation & 3);
		return;
	}
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

void AFWCharacter::SetGaragePlacing(const FString& Kind)
{
	if (GaragePlacing == Kind)
	{
		return;
	}
	GaragePlacing = Kind;
	if (!Kind.IsEmpty())
	{
		bBuildMode = false;
		FWAudio::Play2D(this, TEXT("build_rotate"), 0.4f);
	}
}

bool AFWCharacter::GaragePlacement(FIntPoint& OutCell, FTransform& OutTransform, bool& bOutFits) const
{
	const AFWPlayerController* PC = GetFWController();
	const AFWGarageRoom* Room = PC && PC->IsInGarage() ? PC->GetGarageRoom() : nullptr;
	if (!Room || GaragePlacing.IsEmpty() || !PC->PlayerCameraManager)
	{
		return false;
	}
	const FVector Eye = PC->PlayerCameraManager->GetCameraLocation();
	return Room->Placement(GaragePlacing, BuildRotation & 3, Eye, Eye + PC->PlayerCameraManager->GetCameraRotation().Vector() * 600.f, this, OutCell, OutTransform, bOutFits);
}

bool AFWCharacter::UpdateGarageGhost()
{
	const AFWPlayerController* PC = GetFWController();
	if (!PC || !PC->IsInGarage())
	{
		// Out of the garage nothing is carried
		GaragePlacing.Reset();
		return false;
	}
	FIntPoint Cell;
	FTransform Where;
	bool bFits = false;
	const AFWGarageRoom* Room = PC->GetGarageRoom();
	if (!Room || !GaragePlacement(Cell, Where, bFits))
	{
		Ghost->SetVisibility(false);
		return true;
	}
	// A see-through box the size of the piece, green where it fits and red where something stands, as on a plot (the
	// piece's own model with the ghost's material hardly showed in the dim garage)
	const UStaticMesh* PieceMesh = Room->MeshOf(GaragePlacing);
	const float Height = FMath::Clamp(PieceMesh ? PieceMesh->GetBounds().BoxExtent.Z * 2.f : 80.f, 20.f, 250.f);
	const FIntPoint Size = Room->FootprintOf(GaragePlacing, BuildRotation & 3);
	Ghost->SetWorldLocationAndRotation(Where.GetLocation() + FVector(0.f, 0.f, Height * 0.5f + 1.f), Room->GetActorRotation());
	// The cube is 1 m; the footprint's cells are 50 cm, a little smaller so neighbours show apart
	Ghost->SetWorldScale3D(FVector(Size.X * 0.5f - 0.04f, Size.Y * 0.5f - 0.04f, Height / 100.f));
	// The ghost's colour is light of its own: the game's exposure is set for daylight (EV100 12.5 at the least,
	// FWEnvironment), at which a colour of 1 is next to black, so it glows as bright as a lit wall
	GhostMaterial->SetVectorParameterValue(TEXT("Color"), (bFits ? FLinearColor(0.2f, 1.f, 0.4f) : FLinearColor(1.f, 0.15f, 0.1f)) * GarageGhostGlow);
	GhostMaterial->SetScalarParameterValue(TEXT("Opacity"), 0.32f);
	Ghost->SetVisibility(true);
	return true;
}

void AFWCharacter::UpdateGhost()
{
	if (UpdateGarageGhost())
	{
		return;
	}
	GhostMaterial->SetScalarParameterValue(TEXT("Opacity"), 0.4f);
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
	// Putting the garage's furniture away: held on the same piece, as on a plot
	if (Hold == EHold::Pickup && bGaragePickup && PC)
	{
		FString Kind, Id;
		if (!PC->IsInGarage() || !PC->GetGarageTarget(Kind, Id) || Id != HoldObject)
		{
			Hold = EHold::None;
			return;
		}
		HoldTime += DeltaSeconds;
		if (HoldTime >= HoldNeeded)
		{
			Hold = EHold::None;
			PlayGesture(EGesture::Pickup);
			PC->GaragePickup(HoldObject);
		}
		return;
	}
	if (Hold == EHold::None || bWaiting || !PC || !Plot)
	{
		return;
	}
	if (Hold == EHold::Strike)
	{
		// Looking away from what is struck stops it, and the strikes start over
		const FFWMine* Mine = bHasTarget && HoldMine >= 0 ? Plot->FindMine(TargetTile) : nullptr;
		const bool bOnTarget = bHasTarget && (HoldMine >= 0 ? Mine && Mine->Id == HoldMine : TargetTile == StrikeTile);
		if (!bOnTarget)
		{
			Hold = EHold::None;
			StrikeHits = 0;
			return;
		}
		// The next swing so that it lands a strike's time after the last one (a swing lands 62% of the way through)
		if (Gesture == EGesture::None && GetWorld()->GetTimeSeconds() - LastImpact >= StrikeInterval - 0.62f * SwingLength + 0.03f)
		{
			PlayGesture(EGesture::Swing);
		}
		return;
	}
	// Looking away cancels picking up
	const FFWPlotObject* Object = bHasTarget ? Plot->FindObjectAt(TargetTile) : nullptr;
	if (!Object || Object->Id != HoldObject)
	{
		Hold = EHold::None;
		return;
	}
	HoldTime += DeltaSeconds;
	if (HoldTime < HoldNeeded)
	{
		return;
	}
	Hold = EHold::None;
	PlayGesture(EGesture::Pickup);
	PC->Pickup(HoldObject);
}

float AFWCharacter::GetProgress() const
{
	if (Hold != EHold::Pickup)
	{
		return -1.f;
	}
	return bWaiting ? 1.f : FMath::Clamp(HoldTime / HoldNeeded, 0.f, 1.f);
}

FText AFWCharacter::GetTargetText() const
{
	if (const AFWPlayerController* InGarage = GetFWController(); InGarage && InGarage->IsInGarage())
	{
		if (!GaragePlacing.IsEmpty())
		{
			static const FText Ways[] = { LOCTEXT("ToDoor", "przodem do bramy"), LOCTEXT("ToRight", "przodem w prawo"), LOCTEXT("ToBack", "przodem do tylnej ściany"),
				LOCTEXT("ToLeft", "przodem w lewo") };
			return FText::Format(LOCTEXT("GaragePlacing", "{0} ({1})  [LPM] postaw   [R] obróć   [PPM] zostaw w plecaku"),
				FText::FromString(InGarage->GetGarageRoom() ? InGarage->GetGarageRoom()->NameOf(GaragePlacing) : InGarage->ItemName(GaragePlacing)),
				Ways[BuildRotation & 3]);
		}
		return InGarage->GarageHint();
	}
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
		return LOCTEXT("Mine", "Kopalnia  [E] kop kilofem");
	}
	if (const FFWPlotObject* Object = Plot->FindObjectAt(TargetTile))
	{
		return FWCatalog::IsStorage(Object->Kind)
			? FText::Format(LOCTEXT("Storage", "{0}  [E] włóż surowce   [PPM] zabierz"), FText::FromString(Object->Name))
			: FText::Format(LOCTEXT("Object", "{0}  [PPM] zabierz"), FText::FromString(Object->Name));
	}
	switch (Plot->TileCode(TargetTile))
	{
	case FWCatalog::Rock: return LOCTEXT("Rock", "Skała  [E] rozbijaj kilofem");
	case FWCatalog::Dune: return LOCTEXT("Dune", "Wydma");
	case FWCatalog::Bush: return LOCTEXT("Bush", "Krzak");
	case FWCatalog::Rails: return LOCTEXT("Rails", "Tory");
	case FWCatalog::Station: return LOCTEXT("Station", "Stacja");
	case FWCatalog::Empty: return FText::GetEmpty();
	default: return Plot->IsTree(TargetTile) ? LOCTEXT("Tree", "Drzewo  [E] ścinaj siekierą") : FText::GetEmpty();
	}
}

#undef LOCTEXT_NAMESPACE
