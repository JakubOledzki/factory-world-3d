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
#include "UI/SFWLoginWidget.h"
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
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	SAssignNew(Prompt, SFWPromptWidget)
		.Title(LOCTEXT("NewCharacter", "Nowa postać"))
		.Message(LOCTEXT("NewCharacterText", "Jak ma się nazywać twoja postać? Wygląd zmienisz później w edytorze postaci."))
		.ShowInput(true)
		.InputHint(LOCTEXT("NameHint", "Nazwa (litery, cyfry, _)"))
		.ButtonText(LOCTEXT("Create", "Utwórz postać"))
		.OnAccepted_Lambda([WeakThis](const FString& Name)
		{
			AFWPlayerController* This = WeakThis.Get();
			if (!This || Name.IsEmpty())
			{
				return;
			}
			// The first option of everything: the editor in the 2D game can change it later
			TSharedPtr<FJsonObject> Look = MakeShared<FJsonObject>();
			for (const TCHAR* Field : { TEXT("skinTone"), TEXT("hairStyle"), TEXT("hairColor"), TEXT("shirtStyle"), TEXT("shirtColor"),
				TEXT("pantsColor"), TEXT("shoesColor"), TEXT("faceStyle"), TEXT("eyeColor"), TEXT("facialHair") })
			{
				Look->SetNumberField(Field, 0);
			}
			TSharedPtr<FJsonObject> P = Payload();
			P->SetStringField(TEXT("name"), Name);
			P->SetObjectField(TEXT("appearance"), Look);
			This->Prompt->SetBusy(true);
			This->Net()->Request(TEXT("character.create"), P, [WeakThis](const FFWNetReply& Reply)
			{
				AFWPlayerController* Inner = WeakThis.Get();
				if (!Inner || !Inner->Prompt.IsValid())
				{
					return;
				}
				Inner->Prompt->SetBusy(false);
				if (Reply.Type == TEXT("character"))
				{
					Inner->ChoosePlot();
				}
				else
				{
					Inner->Prompt->SetMessage(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()), true);
				}
			});
		});
	ShowWidget(Prompt.ToSharedRef(), Prompt->GetFocusTarget());
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
		const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
		if (!This || Reply.Type != TEXT("inventory") || !Reply.Payload->TryGetArrayField(TEXT("items"), Items))
		{
			return;
		}
		This->Inventory.Reset();
		for (const TSharedPtr<FJsonValue>& Value : *Items)
		{
			const TSharedPtr<FJsonObject> Item = Value->AsObject();
			FFWStack Stack;
			Item->TryGetStringField(TEXT("id"), Stack.Id);
			Item->TryGetStringField(TEXT("name"), Stack.Name);
			Item->TryGetNumberField(TEXT("quantity"), Stack.Quantity);
			This->Names.Add(Stack.Id, Stack.Name);
			This->Inventory.Add(Stack);
		}
		This->Net()->Request(TEXT("hotbar.get"), nullptr, [WeakThis](const FFWNetReply& Bar)
		{
			if (AFWPlayerController* Inner = WeakThis.Get())
			{
				Inner->ApplyHotbar(Bar.Payload);
			}
		});
	});
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
}

#undef LOCTEXT_NAMESPACE
