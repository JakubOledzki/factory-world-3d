#include "UI/SFWRailWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWRail"

#include "SFWRailWindowHelpers.h"

void SFWRailWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	Form->Set(TEXT("transport"), TEXT("train"));
	Form->Set(TEXT("every"), TEXT("5"));
	AFWPlayerController* PC = Controller.Get();
	if (PC && PC->GetPlot())
	{
		Plot = FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
		for (const TPair<FString, FFWPlotObject>& Pair : PC->GetPlot()->GetObjects())
		{
			bPort |= Pair.Value.Kind == TEXT("furniture_port");
			bTruck |= Pair.Value.Kind == TEXT("furniture_truck_depot");
			bStable |= Pair.Value.Kind == TEXT("furniture_stable");
			// A sled needs a kennel or a snowmobile garage, and the glacier or the tundra (checked when the biome comes)
			bPolar |= Pair.Value.Kind == TEXT("furniture_dog_kennel") || Pair.Value.Kind == TEXT("furniture_snowmobile_garage");
		}
	}
	TSharedRef<SHorizontalBox> TabRow = SNew(SHorizontalBox);
	for (int32 i = 0; i < UE_ARRAY_COUNT(FWRailText::Tabs); ++i)
	{
		TabRow->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			FWUi::TabButton(FText::FromString(FWRailText::Tabs[i]), [this, i]() { return Tab == i; }, [this, i]() { ShowTab(i); })
		];
	}
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Display(22)).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Stacja kolejowa"), Right, TabRow, SAssignNew(Content, SBox), 1050.f, 800.f)
	];
	if (PC)
	{
		TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
		PC->Send(TEXT("world.info"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin(); This && Reply.Type == TEXT("world.info"))
			{
				This->WorldWidth = int32(FWUi::Num(Reply.Payload, TEXT("width"), 6933.0));
				for (const TSharedPtr<FJsonObject>& P : FWUi::Objects(Reply.Payload, TEXT("plots")))
				{
					This->MyPlots.Add(FIntPoint(int32(FWUi::Num(P, TEXT("x"))), int32(FWUi::Num(P, TEXT("y")))));
				}
				This->Rebuild();
			}
		});
		// The biome decides between a sled (glacier, tundra) and a horse cart (elsewhere)
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetNumberField(TEXT("x"), Plot.X);
		P->SetNumberField(TEXT("y"), Plot.Y);
		PC->Send(TEXT("world.plot"), P, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin(); This && Reply.Type == TEXT("world.plot"))
			{
				const int32 Biome = int32(FWUi::Num(Reply.Payload, TEXT("biome")));
				const bool bCold = Biome == 7 || Biome == 8;
				This->bPolar = This->bPolar && bCold;
				This->bStable = This->bStable && !bCold;
			}
		});
		PC->Send(TEXT("market.prices"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin(); This && Reply.Type == TEXT("market.prices"))
			{
				for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Reply.Payload, TEXT("items")))
				{
					This->ItemNames.Add(FWUi::Str(Item, TEXT("id")), FWUi::Str(Item, TEXT("name")));
				}
			}
		});
		PC->RefreshInventory();
	}
	Load();
	LoadFreight();
}

double SFWRailWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

bool SFWRailWindow::Typing() const
{
	const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
	return Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText"));
}

void SFWRailWindow::Load(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + 5.0;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), Plot.X);
	P->SetNumberField(TEXT("y"), Plot.Y);
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("station.get"), P, [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("station"))
		{
			if (!bPoll)
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		This->State = Reply.Payload;
		This->ClockOffset = FWUi::Num(Reply.Payload, TEXT("serverTime")) - FWUi::LocalNowMs();
		// Asked again by itself: countdowns run on their own, the page is not built anew under the player typing
		if (!(bPoll && (This->Typing() || This->Tab == FWRailText::Send || This->Tab == FWRailText::Routes)))
		{
			This->Rebuild();
		}
	});
}

void SFWRailWindow::LoadFreight(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	NextFreightPoll = FPlatformTime::Seconds() + 5.0;
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("freight.get"), MakeShared<FJsonObject>(), [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
		if (!This || Reply.Type != TEXT("freight"))
		{
			return;
		}
		This->Freight = Reply.Payload;
		This->ClockOffset = FWUi::Num(Reply.Payload, TEXT("serverTime")) - FWUi::LocalNowMs();
		if (This->Tab == FWRailText::Freight && !(bPoll && This->Typing()))
		{
			This->Rebuild();
		}
	});
}

void SFWRailWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, const FText& Toast)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetNumberField(TEXT("x"), Plot.X);
	Payload->SetNumberField(TEXT("y"), Plot.Y);
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, Toast](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("station"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->State = Reply.Payload;
		This->Rebuild();
		if (!Toast.IsEmpty())
		{
			Inner->Toast(Toast);
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		Inner->RefreshInventory();
		FWRailText::Wallet(Inner);
	});
}

void SFWRailWindow::FreightAct(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, const FText& Toast)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, Toast](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("freight"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->Freight = Reply.Payload;
		This->Cargo.Reset();
		This->Rebuild();
		if (!Toast.IsEmpty())
		{
			Inner->Toast(Toast);
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
	});
}

void SFWRailWindow::StockAction(bool bLoad, const FString& Item, int32 Quantity)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	Act(bLoad ? TEXT("station.load") : TEXT("station.unload"), P, FText::GetEmpty());
}

void SFWRailWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	if (Scroll.IsValid())
	{
		Scroll->SetScrollOffset(0.f);
	}
	Rebuild();
	if (Tab == FWRailText::Freight)
	{
		LoadFreight();
	}
}

double SFWRailWindow::Km(FIntPoint From, FIntPoint To) const
{
	int32 Dx = FMath::Abs(To.X - From.X);
	Dx = FMath::Min(Dx, WorldWidth - Dx);
	return FMath::Sqrt(double(Dx) * Dx + double(To.Y - From.Y) * (To.Y - From.Y)) * 5.0;
}

FIntPoint SFWRailWindow::Destination() const
{
	const FString Pick = Form->Get(TEXT("to"));
	FString X, Y;
	if (Pick.Split(TEXT(","), &X, &Y))
	{
		return FIntPoint(FCString::Atoi(*X), FCString::Atoi(*Y));
	}
	return FIntPoint(int32(Form->Number(TEXT("toX"))), int32(Form->Number(TEXT("toY"))));
}

FString SFWRailWindow::Where() const
{
	const FIntPoint To = Destination();
	for (const TSharedPtr<FJsonObject>& Player : Found)
	{
		for (const TSharedPtr<FJsonObject>& P : FWUi::Objects(Player, TEXT("plots")))
		{
			if (int32(FWUi::Num(P, TEXT("x"))) == To.X && int32(FWUi::Num(P, TEXT("y"))) == To.Y)
			{
				return FString::Printf(TEXT("%s, działka %d × %d"), *FWUi::Str(Player, TEXT("name")), To.X, To.Y);
			}
		}
	}
	return MyPlots.Contains(To) ? FString::Printf(TEXT("moja działka %d × %d"), To.X, To.Y) : FString::Printf(TEXT("działka %d × %d"), To.X, To.Y);
}

FString SFWRailWindow::PlotLabel(FIntPoint At) const
{
	const FString Name = FWUi::Str(FreightPlot(At), TEXT("name"));
	return Name.IsEmpty() ? FString::Printf(TEXT("działka %d × %d"), At.X, At.Y) : FString::Printf(TEXT("%s (%d × %d)"), *Name, At.X, At.Y);
}

TSharedPtr<FJsonObject> SFWRailWindow::FreightPlot(FIntPoint At) const
{
	for (const TSharedPtr<FJsonObject>& P : FWUi::Objects(Freight, TEXT("plots")))
	{
		if (int32(FWUi::Num(P, TEXT("x"))) == At.X && int32(FWUi::Num(P, TEXT("y"))) == At.Y)
		{
			return P;
		}
	}
	return nullptr;
}

void SFWRailWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedPtr<SWidget> Page;
	if (Tab == FWRailText::Freight)
	{
		Page = MakeFreight();
	}
	else if (!State.IsValid())
	{
		Page = FWUi::Note(LOCTEXT("LoadingStation", "Wczytywanie…"), FWStyle::Muted);
	}
	else
	{
		switch (Tab)
		{
		case FWRailText::Send: Page = MakeSend(); break;
		case FWRailText::Routes: Page = MakeRoutes(); break;
		case FWRailText::Trips: Page = MakeTrips(); break;
		default: Page = MakeStock(); break;
		}
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
	Scroll->SetScrollOffset(Offset);
}

void SFWRailWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	const double Now = FPlatformTime::Seconds();
	if (Now >= NextPoll)
	{
		Load(true);
	}
	if (Tab == FWRailText::Freight && Now >= NextFreightPoll)
	{
		LoadFreight(true);
	}
}

FReply SFWRailWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape)
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->CloseWindow();
		}
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

#undef LOCTEXT_NAMESPACE
