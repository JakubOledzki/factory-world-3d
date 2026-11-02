// Workbenches, furnaces, machines and devices: their windows and what they are told.
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

void AFWPlayerController::OpenStation(const FString& ObjectId, const FString& Kind)
{
	StationKind = FWCatalog::StationOf(Kind);
	// Stations are asked about by their own id, so the garage's work as well without a plot
	if (StationKind < 0)
	{
		return;
	}
	StationId = ObjectId;
	// The garage's furniture is named by garage.get, not the backpack
	StationName = bInGarage && GarageRoom ? GarageRoom->NameOf(Kind) : ItemName(Kind);
	StationState.Reset();
	++StationVersion;
	TSharedRef<SFWStationWindow> Window = SNew(SFWStationWindow).Controller(this)
		.Station(StationKind == 0 ? EFWStation::Crafting : StationKind == 1 ? EFWStation::Furnace : EFWStation::Machine);
	ShowWidget(Window, Window);
	RefreshInventory();
	PollStation();
}

void AFWPlayerController::PollStation()
{
	if (StationId.IsEmpty())
	{
		return;
	}
	NextStationPoll = FPlatformTime::Seconds() + 1.5;
	StationAction(*FString::Printf(TEXT("%s.get"), StationPrefix(StationKind)), nullptr, FString());
}

void AFWPlayerController::StationAction(const TCHAR* Type, const TCHAR* Field, const FString& Value, int64 Number)
{
	if (StationId.IsEmpty())
	{
		return;
	}
	TSharedPtr<FJsonObject> P = Payload();
	P->SetStringField(StationField(StationKind), StationId);
	if (Field)
	{
		if (Number >= 0)
		{
			P->SetNumberField(Field, double(Number));
		}
		else
		{
			P->SetStringField(Field, Value);
		}
	}
	const bool bGet = FString(Type).EndsWith(TEXT(".get"));
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(Type, P, [WeakThis, bGet](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			This->ApplyStation(Reply);
			if (!bGet && !Reply.IsError())
			{
				This->RefreshInventory();
			}
		}
	});
}

bool AFWPlayerController::IsDevice(const FString& Kind)
{
	return SFWDeviceWindow::ModeOf(Kind) != SFWDeviceWindow::EMode::None;
}

void AFWPlayerController::OpenDevice(const FString& ObjectId, const FString& Kind)
{
	SAssignNew(Device, SFWDeviceWindow).Controller(this).Object(ObjectId).Kind(Kind);
	ShowWidget(Device.ToSharedRef(), Device);
}

void AFWPlayerController::StationLoad(const TCHAR* Type, const FString& Item, int32 Quantity, bool bFuel)
{
	if (StationId.IsEmpty() || Quantity <= 0)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = Payload();
	P->SetStringField(StationField(StationKind), StationId);
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	P->SetBoolField(TEXT("fuel"), bFuel);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(Type, P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			This->ApplyStation(Reply);
			This->RefreshInventory();
		}
	});
}

void AFWPlayerController::ApplyStation(const FFWNetReply& Reply)
{
	if (Reply.IsError())
	{
		ToastError(Reply);
		return;
	}
	if (StationId.IsEmpty() || !Reply.Payload.IsValid())
	{
		return;
	}
	StationState = Reply.Payload;
	// The workbench reply names its recipes, the work place among them
	const TArray<TSharedPtr<FJsonValue>>* Recipes = nullptr;
	if (Reply.Payload->TryGetArrayField(TEXT("recipes"), Recipes))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Recipes)
		{
			FString Id, Name;
			if (Value->AsObject()->TryGetStringField(TEXT("id"), Id) && Value->AsObject()->TryGetStringField(TEXT("name"), Name))
			{
				Names.Add(Id, Name);
			}
		}
	}
	if (const FFWPlotObject* Object = Plot ? Plot->GetObjects().Find(StationId) : nullptr)
	{
		StationName = ItemName(Object->Kind);
	}
	StationReceivedAt = FPlatformTime::Seconds();
	Reply.Payload->TryGetNumberField(TEXT("serverTime"), StationServerMs);
	++StationVersion;
	// Finished orders and taken goods land in the inventory
	const TArray<TSharedPtr<FJsonValue>>* Done = nullptr;
	if ((Reply.Payload->TryGetArrayField(TEXT("done"), Done) || Reply.Payload->TryGetArrayField(TEXT("taken"), Done)) && Done->Num() > 0)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Done)
		{
			const TSharedPtr<FJsonObject> Item = Value->AsObject();
			Toast(FText::Format(LOCTEXT("Got", "+{0} {1}"), int32(Item->GetNumberField(TEXT("quantity"))), FText::FromString(Item->GetStringField(TEXT("name")))));
		}
		FWAudio::Play2D(this, TEXT("build_pickup"), 0.5f);
		RefreshInventory();
	}
}

#undef LOCTEXT_NAMESPACE
