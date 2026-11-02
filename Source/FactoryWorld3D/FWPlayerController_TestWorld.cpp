// Console commands for tests (UFUNCTION(Exec)): the test world and moving about it, screenshots.
#include "FWPlayerController.h"
#include "Components/CapsuleComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/GameInstance.h"
#include "UI/SFWFarmWindow.h"
#include "Engine/GameViewportClient.h"
#include "Engine/StaticMeshActor.h"
#include "UI/SFWFilterWindow.h"
#include "GameFramework/Character.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "UI/SFWDeviceWindow.h"
#include "Misc/Paths.h"
#include "UnrealClient.h"
#include "UI/SFWShopWindow.h"
#include "EngineUtils.h"
#include "Game/FWCatalog.h"
#include "Game/FWGameSettings.h"
#include "UI/SFWGarageWindow.h"
#include "World/FWGarageRoom.h"
#include "Net/FWNetSubsystem.h"
#include "UI/SFWHud.h"
#include "AudioMixerBlueprintLibrary.h"
#include "UI/SFWFactoryWindow.h"
#include "Components/AudioComponent.h"
#include "Engine/StaticMesh.h"
#include "UI/SFWAdventuresWindow.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "UI/SFWCityWindow.h"
#include "UI/SFWCountryWindow.h"
#include "UI/SFWPoliceWindow.h"
#include "UI/SFWCharacterCreator.h"
#include "UI/SFWInventoryWindow.h"
#include "UI/SFWContractsWindow.h"
#include "UI/SFWResearchWindow.h"
#include "UI/SFWStatsWindow.h"
#include "UI/SFWSettingsWindow.h"
#include "UI/SFWMarketWindow.h"
#include "UI/SFWStationWindow.h"
#include "UI/SFWWorldMap.h"
#include "Engine/Texture2D.h"
#include "UI/SFWExportWindow.h"
#include "UI/SFWRailWindow.h"
#include "Camera/CameraComponent.h"
#include "UI/SFWBankWindow.h"
#include "UI/SFWModWindow.h"
#include "UI/SFWCompanyWindow.h"
#include "UI/SFWLoginWidget.h"
#include "World/FWCharacterStudio.h"
#include "World/FWTrains.h"
#include "Player/FWCharacter.h"
#include "UI/SFWPromptWidget.h"
#include "World/FWEnvironment.h"
#include "World/FWGrowth.h"
#include "World/FWPlot.h"
#include "UI/SFWAchievementsWindow.h"
#include "UI/SFWBadgeToast.h"
#include "UI/SFWChat.h"

#define LOCTEXT_NAMESPACE "FWGame"

#include "FWPlayerControllerHelpers.h"

void AFWPlayerController::FWRecordStart(float Seconds)
{
	UAudioMixerBlueprintLibrary::StartRecordingOutput(this, Seconds);
}

void AFWPlayerController::FWRecordStop(const FString& Name)
{
	UAudioMixerBlueprintLibrary::StopRecordingOutput(this, EAudioRecordingExportType::WavFile, Name, FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("BouncedWavFiles")));
}

void AFWPlayerController::FWWalk(float Seconds)
{
	WalkUntil = FPlatformTime::Seconds() + Seconds;
}

void AFWPlayerController::FWPlaceNear(const FString& Kind)
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	for (int32 R = 2; R < 40; ++R)
	{
		for (int32 D = -R; D <= R; ++D)
		{
			for (const FIntPoint& Tile : { At + FIntPoint(D, R), At + FIntPoint(D, -R), At + FIntPoint(R, D), At + FIntPoint(-R, D) })
			{
				if (Plot->CanPlace(Kind, Tile))
				{
					UE_LOG(LogFWGame, Log, TEXT("Placing %s at %d,%d"), *Kind, Tile.X, Tile.Y);
					Place(Kind, Tile, 0);
					return;
				}
			}
		}
	}
}

void AFWPlayerController::FWLookAt(const FString& Kind, float Tiles, float Angle, float Pitch)
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWPlotObject* Nearest = nullptr;
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (Pair.Value.Kind == Kind && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
		{
			Nearest = &Pair.Value;
		}
	}
	if (!Nearest)
	{
		UE_LOG(LogFWGame, Warning, TEXT("No %s on the plot"), *Kind);
		return;
	}
	// The footprint's middle (plot space) to world, then a point that far out on that side, put on the ground
	const float Radians = FMath::DegreesToRadians(Angle);
	const FVector Middle = Plot->GetActorTransform().TransformPosition(Plot->FootprintCenter(Kind, Nearest->Tile));
	const FVector Out = Middle + FVector(FMath::Cos(Radians), FMath::Sin(Radians), 0.f) * Tiles * Plot->GetTileSize();
	FHitResult Hit;
	if (GetWorld()->LineTraceSingleByChannel(Hit, Out + FVector(0, 0, 100000.f), Out - FVector(0, 0, 100000.f), ECC_Visibility))
	{
		PlaceOnGround(Hit.ImpactPoint);
	}
	SetControlRotation(FRotator(Pitch, Angle + 180.f, 0.f));
}

void AFWPlayerController::FWOpenNearest(const FString& Kind)
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWPlotObject* Nearest = nullptr;
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (Pair.Value.Kind == Kind && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
		{
			Nearest = &Pair.Value;
		}
	}
	if (!Nearest)
	{
		UE_LOG(LogFWGame, Warning, TEXT("No %s on the plot"), *Kind);
		return;
	}
	FWGoto(Nearest->Tile.X - 2, Nearest->Tile.Y + 2, -45.f, -25.f);
	if (Kind == TEXT("furniture_computer"))
	{
		OpenMarket();
	}
	else if (Kind == TEXT("furniture_research_table"))
	{
		OpenResearch();
	}
	else if (FWCatalog::IsStorage(Kind))
	{
		OpenInventory(Nearest->Id);
	}
	else
	{
		OpenStation(Nearest->Id, Kind);
	}
}

void AFWPlayerController::FWGarageRoom()
{
	ToggleGarageRoom();
}

void AFWPlayerController::FWGarageStand(float X, float Y, float Yaw, float Pitch)
{
	if (bInGarage && GarageRoom && GetPawn())
	{
		GetPawn()->SetActorLocation(GarageRoom->GetActorTransform().TransformPosition(FVector(X, Y, 100.f)), false, nullptr, ETeleportType::TeleportPhysics);
		SetControlRotation(FRotator(Pitch, Yaw, 0.f));
	}
}

void AFWPlayerController::FWGarageLook(const FString& Kind, float Distance)
{
	FVector Centre;
	if (!bInGarage || !GarageRoom || !GetPawn() || !GarageRoom->CentreOf(Kind, Centre))
	{
		return;
	}
	// From the piece towards the middle of the room, where its user stands
	const FVector Room = GarageRoom->GetActorLocation();
	const FVector Feet = Centre + (Room - Centre).GetSafeNormal2D() * Distance;
	GetPawn()->SetActorLocation(FVector(Feet.X, Feet.Y, Room.Z + 100.f), false, nullptr, ETeleportType::TeleportPhysics);
	const FVector Eye = GetPawn()->GetPawnViewLocation();
	SetControlRotation((Centre - Eye).Rotation());
}

void AFWPlayerController::FWGarageTake(const FString& Kind)
{
	const FString Id = bInGarage && GarageRoom ? GarageRoom->IdOf(Kind) : FString();
	if (!Id.IsEmpty())
	{
		GaragePickup(Id);
	}
}

void AFWPlayerController::FWChestNearest()
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWPlotObject* Nearest = nullptr;
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (FWCatalog::IsStorage(Pair.Value.Kind) && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
		{
			Nearest = &Pair.Value;
		}
	}
	if (Nearest)
	{
		UE_LOG(LogFWGame, Log, TEXT("Chest %s at %d,%d"), *Nearest->Id, Nearest->Tile.X, Nearest->Tile.Y);
		FWGoto(Nearest->Tile.X - 2, Nearest->Tile.Y + 2, -45.f, -25.f);
		OpenInventory(Nearest->Id);
	}
}

void AFWPlayerController::FWChestAt(int32 TileX, int32 TileY)
{
	if (const FFWPlotObject* Object = Plot ? Plot->FindObjectAt(FIntPoint(TileX, TileY)) : nullptr)
	{
		OpenInventory(Object->Id);
	}
}

void AFWPlayerController::FWModMoney(float Dollars)
{
	if (!ModWindow.IsValid())
	{
		OpenModMenu();
	}
	if (ModWindow.IsValid())
	{
		ModWindow->GiveMoney(FMath::RoundToDouble(Dollars * 100.0));
	}
}

void AFWPlayerController::FWModFilter(const FString& Text)
{
	if (!ModWindow.IsValid())
	{
		OpenModMenu();
	}
	if (ModWindow.IsValid())
	{
		ModWindow->SetFilter(Text);
	}
}

void AFWPlayerController::FWDevWorld()
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("dev.world"), MakeShared<FJsonObject>(), [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			if (Reply.IsError())
			{
				This->ToastError(Reply);
				return;
			}
			This->Toast(LOCTEXT("DevWorld", "Świat testowy gotowy: pieniądze, narzędzia, trzy działki i maszyny"));
			This->RefreshInventory();
			This->RefreshWallet();
			// Onto the first plot (grassland), where the test machines stand
			const TArray<TSharedPtr<FJsonValue>>* Plots = nullptr;
			if (Reply.Payload.IsValid() && Reply.Payload->TryGetArrayField(TEXT("plots"), Plots) && Plots->Num() > 0)
			{
				const TSharedPtr<FJsonObject> First = (*Plots)[0]->AsObject();
				This->EnterPlot(int32(First->GetNumberField(TEXT("x"))), int32(First->GetNumberField(TEXT("y"))));
			}
		}
	});
}

void AFWPlayerController::FWDevGive(const FString& Item, int32 Quantity, float Dollars)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	if (Item != TEXT("-"))
	{
		P->SetStringField(TEXT("item"), Item);
		P->SetNumberField(TEXT("quantity"), Quantity);
	}
	P->SetNumberField(TEXT("cents"), FMath::RoundToDouble(Dollars * 100.0));
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("dev.give"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			if (Reply.IsError())
			{
				This->ToastError(Reply);
			}
			This->RefreshInventory();
			This->RefreshWallet();
		}
	});
}

void AFWPlayerController::FWAimAt(const FString& What)
{
	APawn* Walker = GetPawn();
	if (!Plot || !Walker)
	{
		return;
	}
	const FIntPoint From = Plot->TileAt(Walker->GetActorLocation());
	const FIntPoint Sides[] = { FIntPoint(0, 1), FIntPoint(1, 0), FIntPoint(0, -1), FIntPoint(-1, 0) };
	int64 Best = MAX_int64;
	FIntPoint Target(-1, -1), Stand(-1, -1);
	for (int32 Y = 1; Y < 199; ++Y)
	{
		for (int32 X = 1; X < 199; ++X)
		{
			const FIntPoint Tile(X, Y);
			if (!Plot->Contains(Tile) || (What == TEXT("rock") ? Plot->TileCode(Tile) != FWCatalog::Rock : !Plot->IsTree(Tile)))
			{
				continue;
			}
			const int64 Distance = int64(X - From.X) * (X - From.X) + int64(Y - From.Y) * (Y - From.Y);
			for (const FIntPoint& Side : Sides)
			{
				if (Distance < Best && Plot->TileCode(Tile + Side) == FWCatalog::Empty && !Plot->FindObjectAt(Tile + Side))
				{
					Best = Distance;
					Target = Tile;
					Stand = Tile + Side;
				}
			}
		}
	}
	if (Target.X < 0)
	{
		return;
	}
	PlaceOnGround(Plot->GroundAt(Stand));
	const FVector Eye = Walker->GetPawnViewLocation();
	// At the foot of it, about where the ground is under the player (its own ground height is the top of what stands there)
	const FVector Foot(Plot->TileCenter(Target).X, Plot->TileCenter(Target).Y, Eye.Z - 150.f);
	SetControlRotation((Foot - Eye).Rotation());
}

void AFWPlayerController::FWGoto(int32 TileX, int32 TileY, float Yaw, float Pitch)
{
	if (Plot && GetPawn())
	{
		PlaceOnGround(Plot->GroundAt(FIntPoint(TileX, TileY)));
		SetControlRotation(FRotator(Pitch, Yaw, 0.f));
	}
}

void AFWPlayerController::FWGotoLand(float EastMeters, float SouthMeters, float Yaw, float Pitch)
{
	if (!Plot)
	{
		return;
	}
	const float Half = Plot->GetTileSize() * 125.f;
	const FVector At = Plot->GetActorTransform().TransformPosition(FVector(Half + EastMeters * 100.f, Half + SouthMeters * 100.f, 0.f));
	FHitResult Hit;
	if (GetWorld()->LineTraceSingleByChannel(Hit, At + FVector(0, 0, 500000.f), At - FVector(0, 0, 500000.f), ECC_Visibility))
	{
		PlaceOnGround(Hit.ImpactPoint);
		SetControlRotation(FRotator(Pitch, Yaw, 0.f));
	}
}

void AFWPlayerController::FWGotoAltitude(float Meters, float Yaw, float Pitch)
{
	const TOptional<float> Elevation = Plot ? Plot->GetPlotElevation() : TOptional<float>();
	if (!Elevation.IsSet())
	{
		Toast(FText::FromString(TEXT("FWGotoAltitude: this plot has no real terrain")), true);
		return;
	}
	// Search rings around the plot for the nearest ground at or below that altitude
	const float Half = Plot->GetTileSize() * 125.f;
	for (float Radius = 2000.f; Radius < 9000.f; Radius += 250.f)
	{
		for (int32 Step = 0; Step < 48; ++Step)
		{
			const float Angle = Step * 2.f * PI / 48.f;
			const FVector At = Plot->GetActorTransform().TransformPosition(FVector(Half + FMath::Cos(Angle) * Radius * 100.f, Half + FMath::Sin(Angle) * Radius * 100.f, 0.f));
			FHitResult Hit;
			if (GetWorld()->LineTraceSingleByChannel(Hit, At + FVector(0, 0, 500000.f), At - FVector(0, 0, 500000.f), ECC_Visibility)
				&& Elevation.GetValue() + Plot->GetActorTransform().InverseTransformPosition(Hit.ImpactPoint).Z / 100.f < Meters)
			{
				// Yaw is relative to looking straight away from the plot (downhill, into the valley)
				PlaceOnGround(Hit.ImpactPoint);
				SetControlRotation(FRotator(Pitch, FMath::RadiansToDegrees(Angle) + Yaw, 0.f));
				return;
			}
		}
	}
}

void AFWPlayerController::FWMine(int32 Times)
{
	if (!Plot || !GetPawn() || Times <= 0)
	{
		return;
	}
	// The mine whose tiles are nearest
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWMine* Nearest = nullptr;
	int32 Best = MAX_int32;
	for (int32 DY = -40; DY <= 40; ++DY)
	{
		for (int32 DX = -40; DX <= 40; ++DX)
		{
			const FFWMine* Mine = Plot->FindMine(At + FIntPoint(DX, DY));
			if (Mine && DX * DX + DY * DY < Best)
			{
				Best = DX * DX + DY * DY;
				Nearest = Mine;
			}
		}
	}
	if (!Nearest)
	{
		Toast(LOCTEXT("NoMineNear", "Nie ma kopalni w pobliżu."), true);
		return;
	}
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	// The server wants the mine's working time between digs, as when holding E
	const float Seconds = Nearest->Seconds;
	Mine(Nearest->Id, [WeakThis, Times, Seconds](bool bOk)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This || !bOk || Times <= 1)
		{
			return;
		}
		FTimerHandle Handle;
		This->GetWorldTimerManager().SetTimer(Handle, FTimerDelegate::CreateWeakLambda(This, [This, Times]() { This->FWMine(Times - 1); }), Seconds + 0.1f, false);
	});
}

void AFWPlayerController::FWPlace(const FString& Kind, int32 TileX, int32 TileY, int32 Rotation)
{
	if (Plot)
	{
		Place(Kind, FIntPoint(TileX, TileY), Rotation);
	}
}

void AFWPlayerController::FWPickupAt(int32 TileX, int32 TileY)
{
	if (const FFWPlotObject* Object = Plot ? Plot->FindObjectAt(FIntPoint(TileX, TileY)) : nullptr)
	{
		Pickup(Object->Id);
	}
}

void AFWPlayerController::FWStoreAt(int32 TileX, int32 TileY)
{
	if (const FFWPlotObject* Object = Plot ? Plot->FindObjectAt(FIntPoint(TileX, TileY)) : nullptr)
	{
		PutAllInStorage(Object->Id);
	}
}

void AFWPlayerController::FWTrain(float Seconds, int32 Quantity)
{
	if (Trains)
	{
		Trains->Expect(Seconds, Quantity);
	}
}

void AFWPlayerController::FWBuildLine(int32 BeltCount)
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	auto Fits = [&](FIntPoint Start)
	{
		for (int32 i = 0; i < BeltCount + 2; ++i)
		{
			if (!Plot->CanPlace(i == 0 ? TEXT("storage_wooden_chest") : (i == 1 ? TEXT("logistics_inserter") : TEXT("logistics_belt")), Start + FIntPoint(i, 0)))
			{
				return false;
			}
		}
		// A row below for the power the inserter needs
		for (int32 i = 0; i < 3; ++i)
		{
			if (!Plot->CanPlace(TEXT("power_pole"), Start + FIntPoint(i, 1)))
			{
				return false;
			}
		}
		return true;
	};
	for (int32 R = 2; R < 30 && LineStart.X < 0; ++R)
	{
		for (int32 DX = -R; DX <= R && LineStart.X < 0; ++DX)
		{
			for (const int32 DY : { R, -R })
			{
				if (Fits(At + FIntPoint(DX, DY)))
				{
					LineStart = At + FIntPoint(DX, DY);
					break;
				}
			}
		}
	}
	if (LineStart.X < 0)
	{
		Toast(LOCTEXT("NoRoom", "Brak wolnego miejsca na linię."), true);
		return;
	}
	// Rotation 1 faces east: the inserter takes from the chest behind it and drops on the belt in front
	Place(TEXT("storage_wooden_chest"), LineStart, 1);
	Place(TEXT("logistics_inserter"), LineStart + FIntPoint(1, 0), 1);
	for (int32 i = 0; i < BeltCount; ++i)
	{
		Place(TEXT("logistics_belt"), LineStart + FIntPoint(2 + i, 0), 1);
	}
	// Inserters need electricity: a pole next to it, fed by a solar panel and a wind turbine
	Place(TEXT("power_solar_panel"), LineStart + FIntPoint(0, 1), 0);
	Place(TEXT("power_pole"), LineStart + FIntPoint(1, 1), 0);
	Place(TEXT("power_wind_turbine"), LineStart + FIntPoint(2, 1), 0);
	// Stand south-west of it, looking along the line
	FWGoto(LineStart.X - 2, LineStart.Y + 4, -40.f, -20.f);
}

void AFWPlayerController::FWViewBelts(float Height)
{
	ACharacter* MyCharacter = Cast<ACharacter>(GetPawn());
	if (!Plot || !MyCharacter)
	{
		return;
	}
	FBox Box(ForceInit);
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (FWCatalog::IsBelt(Pair.Value.Kind))
		{
			Box += Plot->TileCenter(Pair.Value.Tile);
		}
	}
	if (!Box.IsValid)
	{
		return;
	}
	// South-west of the belts and above them, looking down at their middle
	const FVector Target = Box.GetCenter();
	const FVector Eye = Target + FVector(-2.5f, 3.5f, 0.f) * Plot->GetTileSize() + FVector(0, 0, Height);
	MyCharacter->GetCharacterMovement()->SetMovementMode(MOVE_Flying);
	MyCharacter->SetActorLocation(Eye, false, nullptr, ETeleportType::TeleportPhysics);
	SetControlRotation((Target - Eye).Rotation());
}

void AFWPlayerController::FWStoreLine()
{
	if (LineStart.X >= 0)
	{
		FWStoreAt(LineStart.X, LineStart.Y);
	}
}

void AFWPlayerController::FWShot(const FString& Name)
{
	FScreenshotRequest::RequestScreenshot(FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Screenshots"), Name + TEXT(".png")), true, false);
}

#undef LOCTEXT_NAMESPACE
