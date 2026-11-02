// Opening the game's other windows: city, country, rail, research, market, map, chat and the rest.
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

void AFWPlayerController::OpenCity()
{
	SAssignNew(City, SFWCityWindow).Controller(this);
	ShowWidget(City.ToSharedRef(), City);
}

void AFWPlayerController::OpenCountry()
{
	SAssignNew(Country, SFWCountryWindow).Controller(this);
	ShowWidget(Country.ToSharedRef(), Country);
}

void AFWPlayerController::OpenPolice()
{
	SAssignNew(Police, SFWPoliceWindow).Controller(this);
	ShowWidget(Police.ToSharedRef(), Police);
}

void AFWPlayerController::OpenRail()
{
	SAssignNew(Rail, SFWRailWindow).Controller(this);
	ShowWidget(Rail.ToSharedRef(), Rail);
}

void AFWPlayerController::OpenExport(const FString& PostId)
{
	SAssignNew(Export, SFWExportWindow).Controller(this).Post(PostId);
	ShowWidget(Export.ToSharedRef(), Export);
}

void AFWPlayerController::OpenResearch()
{
	SAssignNew(Research, SFWResearchWindow).Controller(this);
	ShowWidget(Research.ToSharedRef(), Research);
}

void AFWPlayerController::OpenContracts()
{
	SAssignNew(Contracts, SFWContractsWindow).Controller(this);
	ShowWidget(Contracts.ToSharedRef(), Contracts);
	RefreshWallet();
}

void AFWPlayerController::OpenStats()
{
	SAssignNew(Stats, SFWStatsWindow).Controller(this);
	ShowWidget(Stats.ToSharedRef(), Stats);
}

void AFWPlayerController::OpenMarket()
{
	SAssignNew(Market, SFWMarketWindow).Controller(this);
	ShowWidget(Market.ToSharedRef(), Market);
	RefreshWallet();
}

void AFWPlayerController::OpenWorldMap()
{
	SAssignNew(WorldMap, SFWWorldMap).Controller(this);
	ShowWidget(WorldMap.ToSharedRef(), WorldMap);
}

void AFWPlayerController::OpenChat()
{
	if (!Chat.IsValid())
	{
		return;
	}
	if (ShownWidget.IsValid())
	{
		CloseWindow();
	}
	Chat->SetOpen(true);
	FInputModeUIOnly InputMode;
	InputMode.SetWidgetToFocus(Chat->GetInput());
	InputMode.SetLockMouseToViewportBehavior(EMouseLockMode::DoNotLock);
	SetInputMode(InputMode);
	SetShowMouseCursor(true);
}

void AFWPlayerController::CloseChat()
{
	if (Chat.IsValid())
	{
		Chat->SetOpen(false);
	}
	SetInputMode(FInputModeGameOnly());
	SetShowMouseCursor(false);
}

void AFWPlayerController::OpenAchievements()
{
	SAssignNew(Achievements, SFWAchievementsWindow).Controller(this);
	ShowWidget(Achievements.ToSharedRef(), Achievements);
}

void AFWPlayerController::OpenCompany()
{
	SAssignNew(Company, SFWCompanyWindow).Controller(this);
	ShowWidget(Company.ToSharedRef(), Company);
}

void AFWPlayerController::OpenBank()
{
	SAssignNew(Bank, SFWBankWindow).Controller(this);
	ShowWidget(Bank.ToSharedRef(), Bank);
}

void AFWPlayerController::CheckModMenu()
{
	// Only a development server knows "dev.access"; elsewhere it answers unknown_type and the menu stays hidden
	ModAccess.Reset();
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("dev.access"), MakeShared<FJsonObject>(), [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		bool bAllowed = false;
		if (This && Reply.Type == TEXT("dev.access") && Reply.Payload.IsValid() && Reply.Payload->TryGetBoolField(TEXT("allowed"), bAllowed) && bAllowed)
		{
			This->ModAccess = Reply.Payload;
			UE_LOG(LogFWGame, Log, TEXT("Mod menu available (F8)"));
		}
	});
}

void AFWPlayerController::OpenModMenu()
{
	if (!ModAccess.IsValid())
	{
		Toast(LOCTEXT("NoModMenu", "Mod menu jest tylko dla konta testowego na serwerze deweloperskim."), true);
		return;
	}
	SAssignNew(ModWindow, SFWModWindow).Controller(this).Access(ModAccess);
	ShowWidget(ModWindow.ToSharedRef(), ModWindow);
}

void AFWPlayerController::OpenAdventures()
{
	SAssignNew(Adventures, SFWAdventuresWindow).Controller(this);
	ShowWidget(Adventures.ToSharedRef(), Adventures);
}

#undef LOCTEXT_NAMESPACE
