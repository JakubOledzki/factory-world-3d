// Plots: choosing the first, entering and visiting, and what the player does on one.
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
			.SecondaryText(LOCTEXT("ToGarage", "Idź do garażu"))
			.OnSecondary_Lambda([WeakThis]()
			{
				if (AFWPlayerController* Inner = WeakThis.Get())
				{
					Inner->HideWidget();
					Inner->Prompt.Reset();
					if (!Inner->Hud.IsValid())
					{
						SAssignNew(Inner->Hud, SFWHud).Controller(Inner);
						Inner->GetWorld()->GetGameViewport()->AddViewportWidgetContent(Inner->Hud.ToSharedRef(), 10);
					}
					Inner->RefreshWallet();
					// Without a plot the player starts in the garage and earns the first plot there
					if (!Inner->bInGarage)
					{
						Inner->ToggleGarageRoom();
					}
					Inner->Toast(LOCTEXT("NoPlotYet", "Nie masz jeszcze działki. Zarób na nią w garażu: segreguj śmieci przy koszu i sprzedawaj surowce. Brama garażu pokaże wolną działkę."));
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
		// Bought from the garage: the player is on the plot now
		This->bInGarage = false;

		This->HideWidget();
		This->Prompt.Reset();
		if (!This->Hud.IsValid())
		{
			SAssignNew(This->Hud, SFWHud).Controller(This);
			World->GetGameViewport()->AddViewportWidgetContent(This->Hud.ToSharedRef(), 10);
		}
		// The chat in the corner and the badge cards over everything, made once
		if (!This->Chat.IsValid())
		{
			SAssignNew(This->Chat, SFWChat).Controller(This);
			World->GetGameViewport()->AddViewportWidgetContent(This->Chat.ToSharedRef(), 20);
			SAssignNew(This->BadgeToast, SFWBadgeToast).Controller(This);
			World->GetGameViewport()->AddViewportWidgetContent(This->BadgeToast.ToSharedRef(), 200);
		}
		// The player's own settings from the last time
		This->ApplyUserSettings(FFWUserSettings::Load());
		This->Toast(FText::Format(LOCTEXT("Welcome", "Działka {0}:{1}"),
			FText::AsNumber(This->Plot->GetPlotX(), &FNumberFormattingOptions::DefaultNoGrouping()),
			FText::AsNumber(This->Plot->GetPlotY(), &FNumberFormattingOptions::DefaultNoGrouping())));
		This->RefreshInventory();
		This->RefreshWallet();
		This->EnteredPlot = Reply.Payload;
		This->CheckAwayReport();
		This->NextPollAt = 0.0;
	});
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

void AFWPlayerController::VisitPlot(int32 X, int32 Y)
{
	CloseWindow();
	if (X < 0)
	{
		ChoosePlot();
	}
	else
	{
		EnterPlot(X, Y);
	}
}

void AFWPlayerController::CheckAwayReport()
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("report.away"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		bool bAway = false;
		if (This && Reply.Type == TEXT("report.away") && Reply.Payload->TryGetBoolField(TEXT("away"), bAway) && bAway && !This->ShownWidget.IsValid())
		{
			TSharedRef<SFWAwayWindow> Window = SNew(SFWAwayWindow).Controller(This).Report(Reply.Payload);
			This->ShowWidget(Window, Window);
		}
	});
}

void AFWPlayerController::StrikeHit(int32 MineId, FIntPoint Tile)
{
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	if (MineId >= 0)
	{
		P->SetNumberField(TEXT("mine"), MineId);
	}
	else
	{
		P->SetNumberField(TEXT("tileX"), Tile.X);
		P->SetNumberField(TEXT("tileY"), Tile.Y);
	}
	Net()->Request(TEXT("plot.hit"), P, [](const FFWNetReply&) {});
}

void AFWPlayerController::Harvest(FIntPoint Tile, TFunction<void(bool)> Done)
{
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetNumberField(TEXT("tileX"), Tile.X);
	P->SetNumberField(TEXT("tileY"), Tile.Y);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("plot.harvest"), P, [WeakThis, Tile, Done = MoveTemp(Done)](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		const bool bOk = Reply.Type == TEXT("plot.harvested");
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
			This->Toast(Parts.IsEmpty() ? LOCTEXT("HarvestedNothing", "Nic tym razem.") : FText::FromString(FString::Join(Parts, TEXT(", "))));
			if (This->Plot)
			{
				This->Plot->ClearTile(Tile);
				const TArray<TSharedPtr<FJsonValue>>* Felled = nullptr;
				if (Reply.Payload->TryGetArrayField(TEXT("felled"), Felled))
				{
					for (const TSharedPtr<FJsonValue>& Value : *Felled)
					{
						const TSharedPtr<FJsonObject> Other = Value->AsObject();
						This->Plot->ClearTile(FIntPoint(int32(Other->GetNumberField(TEXT("tileX"))), int32(Other->GetNumberField(TEXT("tileY")))));
					}
				}
			}
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

#undef LOCTEXT_NAMESPACE
