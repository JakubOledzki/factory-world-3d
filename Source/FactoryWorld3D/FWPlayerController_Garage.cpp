// The garage: its window and the room the player walks in.
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

void AFWPlayerController::OpenGarage(bool bAtBin)
{
	SAssignNew(GarageWindow, SFWGarageWindow).Controller(this).AtBin(bAtBin);
	ShowWidget(GarageWindow.ToSharedRef(), GarageWindow);
}

void AFWPlayerController::RefreshGarageRoom(const TSharedPtr<FJsonObject>& Garage)
{
	if (bInGarage && GarageRoom && Garage.IsValid())
	{
		GarageRoom->Build(Garage);
	}
}

void AFWPlayerController::GaragePickup(const FString& Id)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("id"), Id);
	const FString Kind = GarageRoom ? GarageRoom->KindOf(Id) : FString();
	const FString Name = GarageRoom ? GarageRoom->NameOf(Kind) : FString();
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Send(TEXT("garage.pickup"), P, [WeakThis, Kind, Name](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("garage"))
		{
			This->ToastError(Reply);
			return;
		}
		if (This->GarageRoom)
		{
			This->GarageRoom->Build(Reply.Payload);
		}
		This->PlayUiSound(TEXT("build_pickup"));
		This->RefreshInventory();
		// Taken up to be put somewhere else: carried with its ghost at the crosshair
		AFWCharacter* Walker = Cast<AFWCharacter>(This->GetPawn());
		if (Walker && This->bInGarage && !Kind.IsEmpty())
		{
			Walker->SetGaragePlacing(Kind);
			This->Toast(FText::Format(LOCTEXT("Carrying", "Niesiesz: {0}. [LPM] postaw w innym miejscu, [R] obróć, [PPM] zostaw w plecaku."), FText::FromString(Name)));
		}
		else
		{
			This->Toast(FText::Format(LOCTEXT("PutAway", "{0} w plecaku. Postawisz to z powrotem klawiszem B albo w oknie garażu, zakładka Meble."), FText::FromString(Name)));
		}
	});
}

void AFWPlayerController::GaragePlace(const FString& Kind, FIntPoint Cell, int32 Rotation)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("kind"), Kind);
	P->SetNumberField(TEXT("x"), Cell.X);
	P->SetNumberField(TEXT("y"), Cell.Y);
	P->SetNumberField(TEXT("rotation"), Rotation & 3);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Send(TEXT("garage.place"), P, [WeakThis, Kind](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("garage"))
		{
			// Still carried, for another try
			This->ToastError(Reply);
			return;
		}
		if (This->GarageRoom)
		{
			This->GarageRoom->Build(Reply.Payload);
		}
		AFWCharacter* Walker = Cast<AFWCharacter>(This->GetPawn());
		if (Walker && Walker->GetGaragePlacing() == Kind)
		{
			Walker->SetGaragePlacing(FString());
		}
		This->PlayUiSound(TEXT("build_place"));
		This->RefreshInventory();
	});
}

void AFWPlayerController::CarryToGarage(const FString& Kind)
{
	AFWCharacter* Walker = Cast<AFWCharacter>(GetPawn());
	if (!bInGarage || !Walker)
	{
		return;
	}
	CloseWindow();
	Walker->SetGaragePlacing(Kind);
}

void AFWPlayerController::ToggleGarageRoom()
{
	APawn* Walker = GetPawn();
	if (!Walker)
	{
		return;
	}
	CloseWindow();
	if (bInGarage && !Plot)
	{
		// No plot to go back to yet: the door offers one to buy, and the player stays in the garage until then
		ChoosePlot();
		return;
	}
	if (bInGarage)
	{
		// Out of the door, back where the player stood on the plot
		bInGarage = false;
		Walker->SetActorLocation(PlotSpot.GetLocation(), false, nullptr, ETeleportType::TeleportPhysics);
		SetControlRotation(PlotSpot.Rotator());
		return;
	}
	if (!GarageRoom)
	{
		// High above the plot, out of sight of it and of its mountains
		GarageRoom = GetWorld()->SpawnActor<AFWGarageRoom>((Plot ? Plot->GetActorLocation() : FVector::ZeroVector) + FVector(0.f, 0.f, 500000.f), FRotator::ZeroRotator);
	}
	PlotSpot = FTransform(GetControlRotation(), Walker->GetActorLocation());
	bInGarage = true;
	NextGaragePoll = 0.0;
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Send(TEXT("garage.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This || !This->bInGarage || !This->GarageRoom || !This->GetPawn())
		{
			return;
		}
		if (Reply.Type != TEXT("garage"))
		{
			This->ToastError(Reply);
			return;
		}
		This->GarageRoom->Build(Reply.Payload);
		const FTransform Entrance = This->GarageRoom->Entrance();
		This->GetPawn()->SetActorLocation(Entrance.GetLocation(), false, nullptr, ETeleportType::TeleportPhysics);
		This->SetControlRotation(Entrance.Rotator());
		This->Toast(LOCTEXT("InGarage", "Garaż: E przy koszu, sortowni, szafie i tablicy ulepszeń; brama albo U wychodzi"));
	});
}

void AFWPlayerController::PollGarageRoom()
{
	NextGaragePoll = FPlatformTime::Seconds() + 4.0;
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Send(TEXT("garage.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (This && This->bInGarage && This->GarageRoom && Reply.Type == TEXT("garage"))
		{
			This->GarageRoom->Build(Reply.Payload);
		}
	});
}

bool AFWPlayerController::GarageTarget(FString& OutKind, FString& OutId) const
{
	if (!bInGarage || !GarageRoom || !PlayerCameraManager)
	{
		return false;
	}
	const FVector Eye = PlayerCameraManager->GetCameraLocation();
	return GarageRoom->Pick(Eye, Eye + PlayerCameraManager->GetCameraRotation().Vector() * 260.f, GetPawn(), OutKind, OutId);
}

FText AFWPlayerController::GarageHint() const
{
	FString Kind, Id;
	if (!GarageTarget(Kind, Id))
	{
		return FText::GetEmpty();
	}
	if (Kind == TEXT("door"))
	{
		return LOCTEXT("HintDoor", "Brama  [E] wyjdź na działkę");
	}
	if (Kind == TEXT("board"))
	{
		return LOCTEXT("HintBoard", "Tablica  [E] ulepszenia garażu");
	}
	const FText Name = FText::FromString(GarageRoom->NameOf(Kind));
	if (Kind == TEXT("furniture_pallet"))
	{
		return FText::Format(LOCTEXT("HintPallet", "{0}  [E] rynek: kupuj i sprzedawaj   [PPM] przenieś"), Name);
	}
	static const TMap<FString, FText> Uses = {
		{ TEXT("furniture_trash_bin"), LOCTEXT("UseBin", "segreguj śmieci") }, { TEXT("furniture_sorting_machine"), LOCTEXT("UseSorter", "sortownia") },
		{ TEXT("furniture_wardrobe"), LOCTEXT("UseWardrobe", "garderoba") }, { TEXT("furniture_workbench"), LOCTEXT("UseBench", "wytwarzaj") },
		{ TEXT("furniture_anvil"), LOCTEXT("UseAnvil", "wytwarzaj") }, { TEXT("furniture_furnace"), LOCTEXT("UseFurnace", "wytapiaj") } };
	const FText* Use = Uses.Find(Kind);
	return Use ? FText::Format(LOCTEXT("HintUse", "{0}  [E] {1}   [PPM] przenieś"), Name, *Use)
		: FText::Format(LOCTEXT("HintFurniture", "{0}  [E] meble garażu   [PPM] przenieś"), Name);
}

void AFWPlayerController::GarageInteract()
{
	FString Kind, Id;
	if (!GarageTarget(Kind, Id))
	{
		return;
	}
	if (Kind == TEXT("door"))
	{
		ToggleGarageRoom();
		return;
	}
	if (FWCatalog::StationOf(Kind) >= 0)
	{
		// The workbench, anvil and furnace work as on the plot
		OpenStation(Id, Kind);
		return;
	}
	// The delivery pallet is where the market's goods come and go
	if (Kind == TEXT("furniture_pallet"))
	{
		OpenMarket();
		return;
	}
	const int32 Tab = Kind == TEXT("board") ? 2 : Kind == TEXT("furniture_trash_bin") ? 0 : Kind == TEXT("furniture_sorting_machine") ? 1
		: Kind == TEXT("furniture_wardrobe") ? 3 : 4;
	OpenGarage(Kind == TEXT("furniture_trash_bin"));
	GarageWindow->ShowTab(Tab);
}

#undef LOCTEXT_NAMESPACE
