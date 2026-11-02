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

AFWPlayerController::AFWPlayerController()
{
	PrimaryActorTick.bCanEverTick = true;
	Hotbar.SetNum(HotbarSize);
}

UFWNetSubsystem* AFWPlayerController::Net() const
{
	return GetGameInstance() ? GetGameInstance()->GetSubsystem<UFWNetSubsystem>() : nullptr;
}

void AFWPlayerController::BeginPlay()
{
	Super::BeginPlay();
	// No engine debug text over the game (it still goes to the log)
	if (GEngine && !GIsEditor)
	{
		GEngine->bEnableOnScreenDebugMessages = false;
	}
	if (!IsLocalController() || !Net())
	{
		return;
	}
	// Development auto-login from Saved/DevLogin.ini (Email=..., Password=...); never in Shipping builds,
	// and the file stays out of git with the rest of Saved/
	FString AutoEmail, AutoPassword;
#if !UE_BUILD_SHIPPING
	TArray<FString> DevLines;
	if (FFileHelper::LoadFileToStringArray(DevLines, *FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("DevLogin.ini"))))
	{
		for (const FString& Line : DevLines)
		{
			FString Key, Value;
			if (!Line.StartsWith(TEXT(";")) && Line.Split(TEXT("="), &Key, &Value))
			{
				Key.TrimStartAndEndInline();
				Value.TrimStartAndEndInline();
				if (Key == TEXT("Email"))
				{
					AutoEmail = Value;
				}
				else if (Key == TEXT("Password"))
				{
					AutoPassword = Value;
				}
			}
		}
		if (!AutoEmail.IsEmpty())
		{
			UE_LOG(LogFWGame, Log, TEXT("Dev auto-login as %s"), *AutoEmail);
		}
	}
#endif
	SAssignNew(LoginWidget, SFWLoginWidget)
		.Net(Net())
		.AutoEmail(AutoEmail)
		.AutoPassword(AutoPassword)
		.OnFinished(FOnFWLoginFinished::CreateUObject(this, &AFWPlayerController::OnLoggedIn));
	ShowWidget(LoginWidget.ToSharedRef(), LoginWidget->GetFocusTarget());

	FString TestPath;
	TArray<FString> Lines;
	if (FParse::Value(FCommandLine::Get(), TEXT("FWAutoTest="), TestPath) && FFileHelper::LoadFileToStringArray(Lines, *TestPath))
	{
		for (const FString& Line : Lines)
		{
			FString Delay, Command;
			if (!Line.StartsWith(TEXT("#")) && Line.TrimStartAndEnd().Split(TEXT(" "), &Delay, &Command))
			{
				AutoTest.Add({ FCString::Atod(*Delay), Command });
			}
		}
		AutoTestAt = FPlatformTime::Seconds();
	}
}

void AFWPlayerController::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	HideWidget();
	if (Hud.IsValid())
	{
		if (UGameViewportClient* Viewport = GetWorld()->GetGameViewport())
		{
			Viewport->RemoveViewportWidgetContent(Hud.ToSharedRef());
		}
		Hud.Reset();
	}
	Super::EndPlay(EndPlayReason);
}

void AFWPlayerController::ShowWidget(TSharedRef<SWidget> Widget, TSharedPtr<SWidget> Focus)
{
	HideWidget();
	UGameViewportClient* Viewport = GetWorld() ? GetWorld()->GetGameViewport() : nullptr;
	if (!Viewport)
	{
		return;
	}
	ShownWidget = Widget;
	Viewport->AddViewportWidgetContent(Widget, 100);
	FInputModeUIOnly InputMode;
	if (Focus.IsValid())
	{
		InputMode.SetWidgetToFocus(Focus);
	}
	InputMode.SetLockMouseToViewportBehavior(EMouseLockMode::DoNotLock);
	SetInputMode(InputMode);
	SetShowMouseCursor(true);
}

void AFWPlayerController::HideWidget()
{
	if (ShownWidget.IsValid())
	{
		if (UGameViewportClient* Viewport = GetWorld() ? GetWorld()->GetGameViewport() : nullptr)
		{
			Viewport->RemoveViewportWidgetContent(ShownWidget.ToSharedRef());
		}
		ShownWidget.Reset();
	}
	SetInputMode(FInputModeGameOnly());
	SetShowMouseCursor(false);
}

void AFWPlayerController::OnLoggedIn()
{
	HideWidget();
	LoginWidget.Reset();
	CheckModMenu();
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->GetCharacter([WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			if (Reply.Type == TEXT("character.none"))
			{
				This->AskForCharacter();
			}
			else if (Reply.Type == TEXT("character"))
			{
				This->ApplyLook(Reply.Payload);
				This->ChoosePlot();
			}
			else
			{
				This->ToastError(Reply);
			}
		}
	});
}

void AFWPlayerController::OpenSettings()
{
	TSharedRef<SFWSettingsWindow> Window = SNew(SFWSettingsWindow).Controller(this);
	ShowWidget(Window, Window);
}

double AFWPlayerController::ServerNowMs() const
{
	return StationServerMs + (FPlatformTime::Seconds() - StationReceivedAt) * 1000.0;
}

void AFWPlayerController::Send(const TCHAR* Type, TSharedPtr<FJsonObject> P, TFunction<void(const FFWNetReply&)> OnReply)
{
	Net()->Request(Type, P.IsValid() ? P : Payload(), MoveTemp(OnReply));
}

void AFWPlayerController::PlayUiSound(const TCHAR* Sound)
{
	FWAudio::Play2D(this, Sound, 0.5f);
}

void AFWPlayerController::CloseWindow()
{
	Market.Reset();
	StationId.Reset();
	StationState.Reset();
	if (!Storage.Id.IsEmpty())
	{
		FWAudio::Play2D(this, TEXT("chest_close"), 0.6f);
		Storage = FFWStorageView();
	}
	if (Chat.IsValid() && Chat->IsOpen())
	{
		Chat->SetOpen(false);
	}
	HideWidget();
}

void AFWPlayerController::ApplyUserSettings(const FFWUserSettings& Settings)
{
	FWAudio::ApplyMasterVolume(Settings.MasterVolume * UFWGameSettings::Get().MasterVolume);
	if (AFWCharacter* Walker = Cast<AFWCharacter>(GetPawn()))
	{
		Walker->ApplyUserSettings(Settings.MouseSensitivity, Settings.FieldOfView);
	}
}

void AFWPlayerController::RefreshWallet()
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("wallet.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		double Cents = 0.0;
		if (AFWPlayerController* This = WeakThis.Get(); This && Reply.Payload.IsValid() && Reply.Payload->TryGetNumberField(TEXT("balanceCents"), Cents))
		{
			This->BalanceCents = int64(Cents);
		}
	});
}

void AFWPlayerController::Toast(const FText& Text, bool bError)
{
	Toasts.Add({ Text, FPlatformTime::Seconds(), bError });
	if (Toasts.Num() > 6)
	{
		Toasts.RemoveAt(0);
	}
	UE_LOG(LogFWGame, Log, TEXT("Toast%s: %s"), bError ? TEXT(" (error)") : TEXT(""), *Text.ToString());
}

void AFWPlayerController::ToastError(const FFWNetReply& Reply)
{
	Toast(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()), true);
}

void AFWPlayerController::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	const double Now = FPlatformTime::Seconds();
	if (AutoTest.Num() > 0 && Now >= AutoTestAt + AutoTest[0].Key)
	{
		const FString Command = AutoTest[0].Value;
		AutoTest.RemoveAt(0);
		AutoTestAt = Now;
		UE_LOG(LogFWGame, Log, TEXT("AutoTest: %s"), *Command);
		ConsoleCommand(Command);
	}
	if (Plot && Plot->HasLogistics() && !bPolling && Now >= NextPollAt)
	{
		NextPollAt = Now + UFWGameSettings::Get().LogisticsPollSeconds;
		PollLogistics();
	}
	// What grows on the plot by itself, for the plot stood on
	if (Plot && (!Growth || Growth->GetPlot() != Plot))
	{
		if (Growth)
		{
			Growth->Destroy();
		}
		Growth = GetWorld()->SpawnActor<AFWGrowth>();
		Growth->Init(this, Plot);
	}
	UpdateAmbience(DeltaSeconds);
	// The garage room follows what the garage window changes (furniture, upgrades)
	if (bInGarage && Now >= NextGaragePoll)
	{
		PollGarageRoom();
	}
	if (!StationId.IsEmpty() && Now >= NextStationPoll)
	{
		PollStation();
	}
	// The trains of the plot stood on
	if (Plot && Plot->IsBuilt() && (!Trains || Trains->GetPlot() != Plot))
	{
		if (Trains)
		{
			Trains->Destroy();
		}
		Trains = GetWorld()->SpawnActor<AFWTrains>();
		Trains->Init(this, Plot);
	}
	if (Now < WalkUntil)
	{
		if (APawn* Walker = GetPawn())
		{
			Walker->AddMovementInput(Walker->GetActorForwardVector(), 1.f);
		}
	}
}

void AFWPlayerController::UpdateAmbience(float DeltaSeconds)
{
	if (!Plot || !PlayerCameraManager || !IsLocalController())
	{
		return;
	}
	if (!WindSound)
	{
		FWAudio::ApplyMasterVolume(UFWGameSettings::Get().MasterVolume);
		WindSound = FWAudio::Loop(PlayerCameraManager->GetTransformComponent(), TEXT("amb_wind_loop"), 0.f);
		ForestSound = FWAudio::Loop(PlayerCameraManager->GetTransformComponent(), TEXT("amb_forest_loop"), 0.f);
		NextGustAt = FPlatformTime::Seconds() + 6.0;
		NextBirdAt = FPlatformTime::Seconds() + 3.0;
		if (!WindSound)
		{
			return;
		}
	}
	const FVector Ear = PlayerCameraManager->GetCameraLocation();
	// Forest around: a few samples about the listener
	float Forest = 0.f;
	for (int32 i = 0; i < 6; ++i)
	{
		const float Angle = i * PI / 3.f;
		Forest += Plot->ForestAt(Ear + FVector(FMath::Cos(Angle), FMath::Sin(Angle), 0.f) * 2500.f);
	}
	Forest = FMath::Clamp(Forest / 6.f * 1.6f, 0.f, 1.f);
	// Higher above the plot, more exposed: the wind grows; trees break it
	const float Height = Plot->GetActorTransform().InverseTransformPosition(Ear).Z;
	const float Exposure = FMath::Clamp(0.6f + Height / 25000.f, 0.5f, 1.4f);
	const double Now = FPlatformTime::Seconds();
	if (Now >= NextGustAt)
	{
		NextGustAt = Now + FMath::FRandRange(9.0, 22.0);
		FWAudio::Play2D(this, TEXT("amb_gust"), 0.5f * Exposure * (1.f - 0.6f * Forest), 3);
		GustBoost = 0.35f;
	}
	GustBoost = FMath::Max(0.f, GustBoost - DeltaSeconds * 0.08f);
	WindLevel = FMath::FInterpTo(WindLevel, (0.6f + GustBoost) * Exposure * (1.f - 0.55f * Forest), DeltaSeconds, 1.5f);
	ForestLevel = FMath::FInterpTo(ForestLevel, 0.06f + 0.8f * Forest, DeltaSeconds, 1.5f);
	WindSound->SetVolumeMultiplier(WindLevel);
	if (ForestSound)
	{
		ForestSound->SetVolumeMultiplier(ForestLevel);
	}
	// Birds sing from the trees: often in the forest, now and then out on the meadow
	if (Now >= NextBirdAt)
	{
		NextBirdAt = Now + FMath::Lerp(18.0, 4.0, double(Forest)) * FMath::FRandRange(0.6, 1.4);
		const float Angle = FMath::FRandRange(0.f, 2 * PI);
		const FVector Where = Ear + FVector(FMath::Cos(Angle), FMath::Sin(Angle), 0.f) * FMath::FRandRange(1500.f, 4000.f) + FVector(0, 0, 600.f);
		FWAudio::PlayAt(this, TEXT("amb_bird"), Where, 0.5f, 4, TEXT("ATT_World"), nullptr);
	}
}

#undef LOCTEXT_NAMESPACE
