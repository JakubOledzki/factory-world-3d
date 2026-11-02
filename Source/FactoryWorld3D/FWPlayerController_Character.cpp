// The character: asking for it, the creator, the look on the figure.
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

void AFWPlayerController::AskForCharacter()
{
	// The creator offers exactly what the server allows
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("character.options"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			if (Reply.Type == TEXT("character.options") && Reply.Payload.IsValid())
			{
				This->OpenCreator(Reply.Payload);
			}
			else
			{
				This->ToastError(Reply);
			}
		}
	});
}

void AFWPlayerController::ApplyLook(const TSharedPtr<FJsonObject>& Described)
{
	const TSharedPtr<FJsonObject>* Appearance = nullptr;
	if (!Described.IsValid() || !Described->TryGetObjectField(TEXT("appearance"), Appearance))
	{
		return;
	}
	// The palettes the indices point into
	const TSharedPtr<FJsonObject> Look = *Appearance;
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("character.options"), nullptr, [WeakThis, Look](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (This && Reply.Type == TEXT("character.options"))
		{
			if (AFWCharacter* Player = Cast<AFWCharacter>(This->GetPawn()))
			{
				Player->SetLook(FWGear::MakeLook(Look, Reply.Payload));
			}
		}
	});
}

void AFWPlayerController::OpenCreator(const TSharedPtr<FJsonObject>& Options)
{
	// The studio stands high above the land, out of the world's sight and light
	FActorSpawnParameters Spawn;
	Spawn.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
	Studio = GetWorld()->SpawnActor<AFWCharacterStudio>(FVector(0, 0, 2000000.f), FRotator::ZeroRotator, Spawn);
	if (Studio)
	{
		SetViewTarget(Studio);
		if (!Studio->HasFigure())
		{
			UE_LOG(LogFWGame, Warning, TEXT("Character creator: no mannequin (run Tools/copy_mannequins.ps1); the preview is empty"));
		}
	}
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	SAssignNew(Creator, SFWCharacterCreator)
		.Options(Options)
		.Studio(Studio)
		.OnCreate_Lambda([WeakThis](const FString& Name, TSharedPtr<FJsonObject> Appearance)
		{
			AFWPlayerController* This = WeakThis.Get();
			if (!This || !This->Creator.IsValid())
			{
				return;
			}
			TSharedPtr<FJsonObject> P = Payload();
			P->SetStringField(TEXT("name"), Name);
			P->SetObjectField(TEXT("appearance"), Appearance);
			This->Creator->SetBusy(true);
			This->Net()->Request(TEXT("character.create"), P, [WeakThis](const FFWNetReply& Reply)
			{
				AFWPlayerController* Inner = WeakThis.Get();
				if (!Inner || !Inner->Creator.IsValid())
				{
					return;
				}
				Inner->Creator->SetBusy(false);
				if (Reply.Type == TEXT("character"))
				{
					UE_LOG(LogFWGame, Log, TEXT("Character created"));
					FWAudio::Play2D(Inner, TEXT("ui_confirm"), 0.7f);
					Inner->CloseCreator();
					Inner->ApplyLook(Reply.Payload);
					Inner->ChoosePlot();
				}
				else
				{
					Inner->Creator->SetError(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()));
				}
			});
		});
	ShowWidget(Creator.ToSharedRef(), Creator->GetFocusTarget());
	UE_LOG(LogFWGame, Log, TEXT("Character creator open"));
}

void AFWPlayerController::CloseCreator()
{
	HideWidget();
	Creator.Reset();
	if (Studio)
	{
		Studio->Destroy();
		Studio = nullptr;
	}
	if (GetPawn())
	{
		SetViewTarget(GetPawn());
	}
}

void AFWPlayerController::RefreshLook()
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->GetCharacter([WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get(); This && Reply.Type == TEXT("character"))
		{
			This->ApplyLook(Reply.Payload);
		}
	});
}

#undef LOCTEXT_NAMESPACE
