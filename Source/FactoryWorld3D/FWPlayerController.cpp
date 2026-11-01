#include "FWPlayerController.h"

#include "Components/CapsuleComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/GameInstance.h"
#include "Engine/GameViewportClient.h"
#include "Engine/StaticMeshActor.h"
#include "GameFramework/Character.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "UnrealClient.h"
#include "EngineUtils.h"
#include "Game/FWCatalog.h"
#include "Game/FWGameSettings.h"
#include "Net/FWNetSubsystem.h"
#include "UI/SFWHud.h"
#include "AudioMixerBlueprintLibrary.h"
#include "Components/AudioComponent.h"
#include "Engine/StaticMesh.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "UI/SFWCharacterCreator.h"
#include "UI/SFWInventoryWindow.h"
#include "UI/SFWSettingsWindow.h"
#include "UI/SFWMarketWindow.h"
#include "UI/SFWStationWindow.h"
#include "Engine/Texture2D.h"
#include "UI/SFWExportWindow.h"
#include "UI/SFWRailWindow.h"
#include "Camera/CameraComponent.h"
#include "UI/SFWLoginWidget.h"
#include "World/FWCharacterStudio.h"
#include "Player/FWCharacter.h"
#include "UI/SFWPromptWidget.h"
#include "World/FWEnvironment.h"
#include "World/FWPlot.h"

#define LOCTEXT_NAMESPACE "FWGame"

DEFINE_LOG_CATEGORY_STATIC(LogFWGame, Log, All);

namespace
{
	TSharedPtr<FJsonObject> Payload()
	{
		return MakeShared<FJsonObject>();
	}

	TSharedPtr<FJsonObject> PlotPayload(const AFWPlot* Plot)
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetNumberField(TEXT("x"), Plot->GetPlotX());
		P->SetNumberField(TEXT("y"), Plot->GetPlotY());
		return P;
	}

	FText Price(double Cents)
	{
		FNumberFormattingOptions Options;
		Options.MaximumFractionalDigits = 2;
		return FText::Format(LOCTEXT("Price", "{0} $"), FText::AsNumber(Cents / 100.0, &Options));
	}

	// Offsets around the starter plot, nearest ring first
	TArray<FIntPoint> Spiral(int32 Radius)
	{
		TArray<FIntPoint> Points;
		for (int32 R = 0; R <= Radius; ++R)
		{
			for (int32 DY = -R; DY <= R; ++DY)
			{
				for (int32 DX = -R; DX <= R; ++DX)
				{
					if (FMath::Max(FMath::Abs(DX), FMath::Abs(DY)) == R)
					{
						Points.Add(FIntPoint(DX, DY));
					}
				}
			}
		}
		return Points;
	}
}

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

void AFWPlayerController::FWCreatorPick(const FString& Key, int32 Index)
{
	if (Creator.IsValid())
	{
		Creator->Pick(Key, Index);
	}
}

void AFWPlayerController::FWCreatorName(const FString& Name)
{
	if (Creator.IsValid())
	{
		Creator->SetName(Name);
	}
}

void AFWPlayerController::FWCreatorRandom()
{
	if (Creator.IsValid())
	{
		Creator->Randomize();
	}
}

void AFWPlayerController::FWCreatorView(float Zoom, float Yaw)
{
	if (Studio)
	{
		Studio->SetZoom(Zoom);
		Studio->Spin(Yaw);
	}
}

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

void AFWPlayerController::FWCreatorCreate()
{
	if (Creator.IsValid())
	{
		Creator->Submit();
	}
}

void AFWPlayerController::ChoosePlot()
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->GetWorldInfo([WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		const TArray<TSharedPtr<FJsonValue>>* Plots = nullptr;
		if (Reply.Type == TEXT("world.info") && Reply.Payload->TryGetArrayField(TEXT("plots"), Plots) && Plots->Num() > 0)
		{
			const TSharedPtr<FJsonObject> First = (*Plots)[0]->AsObject();
			This->EnterPlot(int32(First->GetNumberField(TEXT("x"))), int32(First->GetNumberField(TEXT("y"))));
		}
		else if (Reply.IsError())
		{
			This->ToastError(Reply);
		}
		else
		{
			This->OfferStarterPlot(6, 0);
		}
	});
}

void AFWPlayerController::OfferStarterPlot(int32 Radius, int32 Index)
{
	static const TArray<FIntPoint> Offsets = Spiral(6);
	if (!Offsets.IsValidIndex(Index))
	{
		Toast(LOCTEXT("NoFreePlot", "Nie znaleziono wolnej działki w pobliżu."), true);
		return;
	}
	const UFWGameSettings& Settings = UFWGameSettings::Get();
	const FIntPoint At = FIntPoint(Settings.StarterPlotX, Settings.StarterPlotY) + Offsets[Index];
	TSharedPtr<FJsonObject> P = Payload();
	P->SetNumberField(TEXT("x"), At.X);
	P->SetNumberField(TEXT("y"), At.Y);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("world.plot"), P, [WeakThis, At, Radius, Index](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		double PriceCents = 0.0;
		if (Reply.Type != TEXT("world.plot") || !Reply.Payload->TryGetNumberField(TEXT("priceCents"), PriceCents))
		{
			// Water or taken: try the next one
			This->OfferStarterPlot(Radius, Index + 1);
			return;
		}
		const FText Message = FText::Format(
			LOCTEXT("OfferText", "Nie masz jeszcze działki. Wolna działka {0}:{1} kosztuje {2}. Kupić ją?"),
			FText::AsNumber(At.X, &FNumberFormattingOptions::DefaultNoGrouping()),
			FText::AsNumber(At.Y, &FNumberFormattingOptions::DefaultNoGrouping()), Price(PriceCents));
		SAssignNew(This->Prompt, SFWPromptWidget)
			.Title(LOCTEXT("FirstPlot", "Pierwsza działka"))
			.Message(Message)
			.ButtonText(LOCTEXT("Buy", "Kup działkę"))
			.SecondaryText(LOCTEXT("LookAround", "Rozejrzyj się (bez działki)"))
			.OnSecondary_Lambda([WeakThis]()
			{
				if (AFWPlayerController* Inner = WeakThis.Get())
				{
					Inner->HideWidget();
					Inner->Prompt.Reset();
					Inner->Toast(LOCTEXT("NoPlotYet", "Nie masz jeszcze działki. Zarób na nią w garażu w Factory World i zaloguj się ponownie."));
					if (!Inner->Hud.IsValid())
					{
						SAssignNew(Inner->Hud, SFWHud).Controller(Inner);
						Inner->GetWorld()->GetGameViewport()->AddViewportWidgetContent(Inner->Hud.ToSharedRef(), 10);
					}
					Inner->RefreshWallet();
				}
			})
			.OnAccepted_Lambda([WeakThis, At](const FString&)
			{
				AFWPlayerController* Inner = WeakThis.Get();
				if (!Inner)
				{
					return;
				}
				TSharedPtr<FJsonObject> Buy = Payload();
				Buy->SetNumberField(TEXT("x"), At.X);
				Buy->SetNumberField(TEXT("y"), At.Y);
				Inner->Prompt->SetBusy(true);
				Inner->Net()->Request(TEXT("world.buy"), Buy, [WeakThis, At](const FFWNetReply& Bought)
				{
					AFWPlayerController* Buyer = WeakThis.Get();
					if (!Buyer || !Buyer->Prompt.IsValid())
					{
						return;
					}
					Buyer->Prompt->SetBusy(false);
					if (Bought.Type == TEXT("world.bought"))
					{
						Buyer->EnterPlot(At.X, At.Y);
					}
					else
					{
						Buyer->Prompt->SetMessage(FWCatalog::ErrorText(Bought.ErrorCode(), Bought.ErrorMessage()), true);
					}
				});
			});
		This->ShowWidget(This->Prompt.ToSharedRef(), nullptr);
	});
}

void AFWPlayerController::EnterPlot(int32 X, int32 Y)
{
	TSharedPtr<FJsonObject> P = Payload();
	P->SetNumberField(TEXT("x"), X);
	P->SetNumberField(TEXT("y"), Y);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("plot.enter"), P, [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("plot"))
		{
			This->ToastError(Reply);
			return;
		}
		UWorld* World = This->GetWorld();
		// The plot replaces the test ground and blocks
		for (TActorIterator<AFWEnvironment> It(World); It; ++It)
		{
			It->Ground->SetVisibility(false);
			It->Ground->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		}
		for (TActorIterator<AStaticMeshActor> It(World); It; ++It)
		{
			if (It->ActorHasTag(TEXT("FWTestBlock")))
			{
				It->SetActorHiddenInGame(true);
				It->SetActorEnableCollision(false);
			}
		}
		if (This->Plot)
		{
			This->Plot->Destroy();
		}
		This->Plot = World->SpawnActor<AFWPlot>();
		This->Plot->Build(Reply.Payload);
		This->PlaceOnGround(This->Plot->SpawnLocation());

		This->HideWidget();
		This->Prompt.Reset();
		if (!This->Hud.IsValid())
		{
			SAssignNew(This->Hud, SFWHud).Controller(This);
			World->GetGameViewport()->AddViewportWidgetContent(This->Hud.ToSharedRef(), 10);
		}
		// The player's own settings from the last time
		This->ApplyUserSettings(FFWUserSettings::Load());
		This->Toast(FText::Format(LOCTEXT("Welcome", "Działka {0}:{1}"),
			FText::AsNumber(This->Plot->GetPlotX(), &FNumberFormattingOptions::DefaultNoGrouping()),
			FText::AsNumber(This->Plot->GetPlotY(), &FNumberFormattingOptions::DefaultNoGrouping())));
		This->RefreshInventory();
		This->RefreshWallet();
		This->NextPollAt = 0.0;
	});
}

void AFWPlayerController::RefreshInventory()
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("inventory.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This || Reply.Type != TEXT("inventory"))
		{
			return;
		}
		This->ApplyInventory(Reply.Payload);
		This->Net()->Request(TEXT("hotbar.get"), nullptr, [WeakThis](const FFWNetReply& Bar)
		{
			if (AFWPlayerController* Inner = WeakThis.Get())
			{
				Inner->ApplyHotbar(Bar.Payload);
			}
		});
	});
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

void AFWPlayerController::FWRail(int32 Tab)
{
	if (!Rail.IsValid())
	{
		OpenRail();
	}
	Rail->ShowTab(Tab);
}

void AFWPlayerController::FWRailStock(int32 Load, const FString& Item, int32 Quantity)
{
	if (!Rail.IsValid())
	{
		OpenRail();
	}
	Rail->StockAction(Load != 0, Item, Quantity);
}

void AFWPlayerController::FWExport(const FString& PostId)
{
	OpenExport(PostId);
}

void AFWPlayerController::ApplyInventory(const TSharedPtr<FJsonObject>& InventoryPayload)
{
	const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
	if (!InventoryPayload.IsValid() || !InventoryPayload->TryGetArrayField(TEXT("items"), Items))
	{
		return;
	}
	InventoryPayload->TryGetNumberField(TEXT("slots"), InventorySlots);
	Inventory.Reset();
	for (const TSharedPtr<FJsonValue>& Value : *Items)
	{
		const TSharedPtr<FJsonObject> Item = Value->AsObject();
		FFWStack Stack;
		Item->TryGetStringField(TEXT("id"), Stack.Id);
		Item->TryGetStringField(TEXT("name"), Stack.Name);
		Item->TryGetNumberField(TEXT("quantity"), Stack.Quantity);
		Item->TryGetNumberField(TEXT("stack"), Stack.StackSize);
		const TArray<TSharedPtr<FJsonValue>>* Tiles = nullptr;
		if (Item->TryGetArrayField(TEXT("tiles"), Tiles))
		{
			for (const TSharedPtr<FJsonValue>& Tile : *Tiles)
			{
				Stack.Tiles.Add(int32(Tile->AsNumber()));
			}
		}
		int32 Slot = -1;
		if (Stack.Tiles.IsEmpty() && Item->TryGetNumberField(TEXT("slot"), Slot))
		{
			Stack.Tiles.Add(Slot);
		}
		Names.Add(Stack.Id, Stack.Name);
		Inventory.Add(Stack);
	}
}

void AFWPlayerController::ApplyStorage(const TSharedPtr<FJsonObject>& StoragePayload)
{
	if (!StoragePayload.IsValid())
	{
		return;
	}
	StoragePayload->TryGetStringField(TEXT("id"), Storage.Id);
	StoragePayload->TryGetStringField(TEXT("name"), Storage.Name);
	StoragePayload->TryGetNumberField(TEXT("slots"), Storage.Slots);
	StoragePayload->TryGetNumberField(TEXT("capacity"), Storage.Capacity);
	StoragePayload->TryGetNumberField(TEXT("stored"), Storage.Stored);
	Storage.Items.Reset();
	const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
	if (StoragePayload->TryGetArrayField(TEXT("items"), Items))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Items)
		{
			const TSharedPtr<FJsonObject> Item = Value->AsObject();
			FFWStack Stack;
			Item->TryGetStringField(TEXT("id"), Stack.Id);
			Item->TryGetStringField(TEXT("name"), Stack.Name);
			Item->TryGetNumberField(TEXT("quantity"), Stack.Quantity);
			Names.Add(Stack.Id, Stack.Name);
			Storage.Items.Add(Stack);
		}
	}
}

const FSlateBrush* AFWPlayerController::GetIconBrush(const FString& Item)
{
	if (Item.IsEmpty())
	{
		return nullptr;
	}
	if (const TSharedPtr<FSlateBrush>* Found = IconBrushes.Find(Item))
	{
		return Found->Get();
	}
	TSharedPtr<FSlateBrush> Brush;
	if (UTexture2D* Texture = GetIcon(Item))
	{
		Brush = MakeShared<FSlateBrush>();
		Brush->SetResourceObject(Texture);
		Brush->ImageSize = FVector2D(Texture->GetSizeX(), Texture->GetSizeY());
	}
	IconBrushes.Add(Item, Brush);
	return Brush.Get();
}

void AFWPlayerController::OpenInventory(const FString& StorageId)
{
	if (!Plot)
	{
		return;
	}
	Storage = FFWStorageView();
	Storage.Id = StorageId;
	TSharedRef<SFWInventoryWindow> Window = SNew(SFWInventoryWindow).Controller(this).Storage(StorageId);
	ShowWidget(Window, Window);
	RefreshInventory();
	if (!StorageId.IsEmpty())
	{
		FWAudio::Play2D(this, TEXT("chest_open"), 0.6f);
		TSharedPtr<FJsonObject> P = PlotPayload(Plot);
		P->SetStringField(TEXT("id"), StorageId);
		TWeakObjectPtr<AFWPlayerController> WeakThis(this);
		Net()->Request(TEXT("storage.get"), P, [WeakThis](const FFWNetReply& Reply)
		{
			if (AFWPlayerController* This = WeakThis.Get(); This && Reply.Type == TEXT("storage"))
			{
				This->ApplyStorage(Reply.Payload);
			}
			else if (This)
			{
				This->ToastError(Reply);
			}
		});
	}
}

void AFWPlayerController::OpenSettings()
{
	TSharedRef<SFWSettingsWindow> Window = SNew(SFWSettingsWindow).Controller(this);
	ShowWidget(Window, Window);
}

namespace
{
	const TCHAR* StationPrefix(int32 Kind)
	{
		return Kind == 0 ? TEXT("crafting") : Kind == 1 ? TEXT("furnace") : TEXT("machine");
	}

	const TCHAR* StationField(int32 Kind)
	{
		return Kind == 0 ? TEXT("workbench") : Kind == 1 ? TEXT("furnace") : TEXT("machine");
	}
}

void AFWPlayerController::OpenStation(const FString& ObjectId, const FString& Kind)
{
	StationKind = FWCatalog::StationOf(Kind);
	if (StationKind < 0 || !Plot)
	{
		return;
	}
	StationId = ObjectId;
	StationName = ItemName(Kind);
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

double AFWPlayerController::ServerNowMs() const
{
	return StationServerMs + (FPlatformTime::Seconds() - StationReceivedAt) * 1000.0;
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
	else if (FWCatalog::IsStorage(Kind))
	{
		OpenInventory(Nearest->Id);
	}
	else
	{
		OpenStation(Nearest->Id, Kind);
	}
}

void AFWPlayerController::OpenMarket()
{
	SAssignNew(Market, SFWMarketWindow).Controller(this);
	ShowWidget(Market.ToSharedRef(), Market);
	RefreshWallet();
}

void AFWPlayerController::Send(const TCHAR* Type, TSharedPtr<FJsonObject> P, TFunction<void(const FFWNetReply&)> OnReply)
{
	Net()->Request(Type, P.IsValid() ? P : Payload(), MoveTemp(OnReply));
}

void AFWPlayerController::PlayUiSound(const TCHAR* Sound)
{
	FWAudio::Play2D(this, Sound, 0.5f);
}

void AFWPlayerController::FWMarketTab(int32 Tab)
{
	if (Market.IsValid())
	{
		Market->ShowTab(Tab);
	}
}

void AFWPlayerController::FWMarketItem(const FString& Item)
{
	if (Market.IsValid())
	{
		Market->OpenItem(Item);
	}
}

void AFWPlayerController::FWMarketOffer(int32 Sell, int32 Quantity, float PriceDollars)
{
	if (Market.IsValid())
	{
		Market->PutOffer(Sell != 0, Quantity, PriceDollars * 100.0);
	}
}

void AFWPlayerController::FWTrade(int32 Buy, const FString& Item, int32 Quantity)
{
	if (Market.IsValid())
	{
		Market->Trade(Buy != 0, Item, Quantity);
	}
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

void AFWPlayerController::MoveTile(int32 From, int32 To)
{
	TSharedPtr<FJsonObject> P = Payload();
	P->SetNumberField(TEXT("from"), From);
	P->SetNumberField(TEXT("to"), To);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("inventory.move"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			Reply.Type == TEXT("inventory") ? This->ApplyInventory(Reply.Payload) : This->ToastError(Reply);
		}
	});
}

void AFWPlayerController::SetHotbarSlot(int32 Slot, const FString& Item)
{
	TSharedPtr<FJsonObject> P = Payload();
	P->SetNumberField(TEXT("slot"), Slot);
	if (Item.IsEmpty())
	{
		P->SetField(TEXT("item"), MakeShared<FJsonValueNull>());
	}
	else
	{
		P->SetStringField(TEXT("item"), Item);
	}
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("hotbar.set"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			Reply.Type == TEXT("hotbar") ? This->ApplyHotbar(Reply.Payload) : This->ToastError(Reply);
		}
	});
}

void AFWPlayerController::StorageTransfer(bool bPut, const FString& Item, int32 Quantity)
{
	if (!Plot || Storage.Id.IsEmpty() || Item.IsEmpty() || Quantity <= 0)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetStringField(TEXT("id"), Storage.Id);
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(bPut ? TEXT("storage.put") : TEXT("storage.take"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			if (Reply.Type == TEXT("storage"))
			{
				This->ApplyStorage(Reply.Payload);
				This->RefreshInventory();
			}
			else
			{
				This->ToastError(Reply);
			}
		}
	});
}

void AFWPlayerController::StorageTransferAll(bool bPut)
{
	if (bPut)
	{
		PutAllInStorage(Storage.Id);
		return;
	}
	for (const FFWStack& Stack : TArray<FFWStack>(Storage.Items))
	{
		StorageTransfer(false, Stack.Id, Stack.Quantity);
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

void AFWPlayerController::ApplyHotbar(const TSharedPtr<FJsonObject>& HotbarPayload)
{
	const TArray<TSharedPtr<FJsonValue>>* Slots = nullptr;
	if (!HotbarPayload.IsValid() || !HotbarPayload->TryGetArrayField(TEXT("slots"), Slots))
	{
		return;
	}
	bool bEmpty = true;
	for (int32 i = 0; i < HotbarSize; ++i)
	{
		Hotbar[i] = Slots->IsValidIndex(i) && !(*Slots)[i]->IsNull() ? (*Slots)[i]->AsString() : FString();
		bEmpty &= Hotbar[i].IsEmpty();
	}
	// A new player's hotbar is empty: put what can be built here on it once
	if (bEmpty && !bHotbarFilled && Plot)
	{
		bHotbarFilled = true;
		int32 Slot = 0;
		for (const FFWStack& Stack : Inventory)
		{
			if (Slot < HotbarSize && Plot->IsPlaceable(Stack.Id))
			{
				TSharedPtr<FJsonObject> P = Payload();
				P->SetNumberField(TEXT("slot"), Slot++);
				P->SetStringField(TEXT("item"), Stack.Id);
				TWeakObjectPtr<AFWPlayerController> WeakThis(this);
				Net()->Request(TEXT("hotbar.set"), P, [WeakThis](const FFWNetReply& Reply)
				{
					if (AFWPlayerController* This = WeakThis.Get(); This && Reply.Type == TEXT("hotbar"))
					{
						This->ApplyHotbar(Reply.Payload);
					}
				});
			}
		}
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

const FFWStack* AFWPlayerController::FindStack(const FString& Item) const
{
	return Inventory.FindByPredicate([&](const FFWStack& Stack) { return Stack.Id == Item; });
}

FString AFWPlayerController::ItemName(const FString& Item) const
{
	// Names stay known after the last piece is used up
	const FString* Name = Names.Find(Item);
	return Name && !Name->IsEmpty() ? *Name : Item;
}

void AFWPlayerController::SelectSlot(int32 Slot)
{
	SelectedSlot = FMath::Clamp(Slot, 0, HotbarSize - 1);
}

UTexture2D* AFWPlayerController::GetIcon(const FString& Item) const
{
	const UStaticMesh* Mesh = Item.IsEmpty() ? nullptr : FWArt::ItemMesh(Item);
	if (!Mesh)
	{
		return nullptr;
	}
	const FString Name = FString::Printf(TEXT("icon_%s"), *Mesh->GetName());
	return FWArt::Texture(*FString::Printf(TEXT("/Game/External/Icons/%s.%s"), *Name, *Name));
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

void AFWPlayerController::Mine(int32 MineId, TFunction<void(bool)> Done)
{
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetNumberField(TEXT("mine"), MineId);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("plot.mine"), P, [WeakThis, Done = MoveTemp(Done)](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		const bool bOk = Reply.Type == TEXT("plot.mined");
		if (bOk)
		{
			TArray<FString> Parts;
			const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
			if (Reply.Payload->TryGetArrayField(TEXT("items"), Items))
			{
				for (const TSharedPtr<FJsonValue>& Value : *Items)
				{
					const TSharedPtr<FJsonObject> Item = Value->AsObject();
					Parts.Add(FString::Printf(TEXT("+%d %s"), int32(Item->GetNumberField(TEXT("quantity"))), *Item->GetStringField(TEXT("name"))));
				}
			}
			This->Toast(Parts.IsEmpty() ? LOCTEXT("MinedNothing", "Nic tym razem.") : FText::FromString(FString::Join(Parts, TEXT(", "))));
			This->RefreshInventory();
		}
		else
		{
			This->ToastError(Reply);
		}
		if (Done)
		{
			Done(bOk);
		}
	});
}

void AFWPlayerController::Place(const FString& Kind, FIntPoint Tile, int32 Rotation)
{
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetStringField(TEXT("kind"), Kind);
	P->SetNumberField(TEXT("tileX"), Tile.X);
	P->SetNumberField(TEXT("tileY"), Tile.Y);
	P->SetNumberField(TEXT("rotation"), Rotation & 3);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("plot.place"), P, [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This || !This->Plot)
		{
			return;
		}
		const TSharedPtr<FJsonObject>* Object = nullptr;
		if (Reply.Type == TEXT("plot.placed") && Reply.Payload->TryGetObjectField(TEXT("object"), Object))
		{
			This->Plot->AddObject(*Object);
			FWAudio::Play2D(This, TEXT("build_place"), 0.7f);
			This->RefreshInventory();
		}
		else
		{
			This->ToastError(Reply);
		}
	});
}

void AFWPlayerController::Pickup(const FString& ObjectId)
{
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetStringField(TEXT("id"), ObjectId);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("plot.pickup"), P, [WeakThis, ObjectId](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This || !This->Plot)
		{
			return;
		}
		if (Reply.Type == TEXT("plot.pickedUp"))
		{
			This->Plot->RemoveObject(ObjectId);
			FWAudio::Play2D(This, TEXT("build_pickup"), 0.6f);
			This->Toast(LOCTEXT("PickedUp", "Zabrano do ekwipunku."));
			This->RefreshInventory();
		}
		else
		{
			This->ToastError(Reply);
		}
	});
}

void AFWPlayerController::PutAllInStorage(const FString& ObjectId)
{
	// Everything but what can be built or used as a tool
	TArray<FFWStack> ToPut;
	for (const FFWStack& Stack : Inventory)
	{
		if (Stack.Quantity > 0 && !Plot->IsPlaceable(Stack.Id) && !Stack.Id.StartsWith(TEXT("tool_")))
		{
			ToPut.Add(Stack);
		}
	}
	if (ToPut.IsEmpty())
	{
		Toast(LOCTEXT("NothingToPut", "Nie masz nic do włożenia."));
		return;
	}
	// The lid opens and drops shut again
	if (const FFWPlotObject* Chest = Plot->GetObjects().Find(ObjectId))
	{
		const FVector At = Plot->GetActorTransform().TransformPosition(Plot->FootprintCenter(Chest->Kind, Chest->Tile)) + FVector(0, 0, 60.f);
		FWAudio::PlayAt(this, TEXT("chest_open"), At, 0.7f);
		FTimerHandle Close;
		GetWorldTimerManager().SetTimer(Close, FTimerDelegate::CreateWeakLambda(this, [this, At] { FWAudio::PlayAt(this, TEXT("chest_close"), At, 0.7f); }), 0.9f, false);
	}
	TSharedRef<int32> Left = MakeShared<int32>(ToPut.Num());
	TSharedRef<TArray<FString>> Put = MakeShared<TArray<FString>>();
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	for (const FFWStack& Stack : ToPut)
	{
		TSharedPtr<FJsonObject> P = PlotPayload(Plot);
		P->SetStringField(TEXT("id"), ObjectId);
		P->SetStringField(TEXT("item"), Stack.Id);
		P->SetNumberField(TEXT("quantity"), Stack.Quantity);
		Net()->Request(TEXT("storage.put"), P, [WeakThis, Left, Put, Stack](const FFWNetReply& Reply)
		{
			AFWPlayerController* This = WeakThis.Get();
			if (!This)
			{
				return;
			}
			if (Reply.IsError())
			{
				This->ToastError(Reply);
			}
			else
			{
				Put->Add(FString::Printf(TEXT("%d %s"), Stack.Quantity, *Stack.Name));
			}
			if (Reply.Type == TEXT("storage"))
			{
				This->ApplyStorage(Reply.Payload);
			}
			if (--(*Left) == 0)
			{
				if (!Put->IsEmpty())
				{
					This->Toast(FText::Format(LOCTEXT("Stored", "Włożono do skrzyni: {0}"), FText::FromString(FString::Join(*Put, TEXT(", ")))));
				}
				This->RefreshInventory();
			}
		});
	}
}

void AFWPlayerController::PollLogistics()
{
	bPolling = true;
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetBoolField(TEXT("compact"), true);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("logistics.get"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			This->bPolling = false;
			if (This->Plot && Reply.Type == TEXT("logistics"))
			{
				This->Plot->ApplyLogistics(Reply.Payload);
			}
		}
	});
}

void AFWPlayerController::FWPrompt(const FString& Text)
{
	if (Prompt.IsValid())
	{
		Prompt->AcceptWith(Text);
	}
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

void AFWPlayerController::PlaceOnGround(const FVector& Feet)
{
	ACharacter* MyCharacter = Cast<ACharacter>(GetPawn());
	if (!MyCharacter)
	{
		return;
	}
	UCharacterMovementComponent* Movement = MyCharacter->GetCharacterMovement();
	Movement->StopMovementImmediately();
	Movement->SetMovementMode(MOVE_Walking);
	const float HalfHeight = MyCharacter->GetCapsuleComponent()->GetScaledCapsuleHalfHeight();
	MyCharacter->SetActorLocation(Feet + FVector(0, 0, HalfHeight + 2.f), false, nullptr, ETeleportType::TeleportPhysics);
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
	UpdateAmbience(DeltaSeconds);
	if (!StationId.IsEmpty() && Now >= NextStationPoll)
	{
		PollStation();
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
