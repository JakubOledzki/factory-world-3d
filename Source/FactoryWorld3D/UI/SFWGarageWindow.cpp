#include "UI/SFWGarageWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "Fonts/FontMeasure.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "Game/FWCatalog.h"
#include "Input/DragAndDrop.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWGarage"

#include "SFWGarageWindowHelpers.h"

void SFWGarageWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	bAtBin = InArgs._AtBin;
	Form = MakeShared<FFWForm>();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	auto MakeTab = [WeakThis](const FText& Text, int32 Index)
	{
		return FWUi::TabButton(Text, [WeakThis, Index]() { TSharedPtr<SFWGarageWindow> This = WeakThis.Pin(); return This && This->Tab == Index; },
			[WeakThis, Index]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->ShowTab(Index); } });
	};
	TSharedRef<SHorizontalBox> Tabs = SNew(SHorizontalBox);
	const FText Names[] = { LOCTEXT("TabSorting", "Segregacja"), LOCTEXT("TabSorter", "Sortownia"), LOCTEXT("TabUpgrades", "Ulepszenia"),
		LOCTEXT("TabWardrobe", "Garderoba"), LOCTEXT("TabFurniture", "Meble") };
	for (int32 Index = 0; Index < UE_ARRAY_COUNT(Names); ++Index)
	{
		Tabs->AddSlot().AutoWidth()[MakeTab(Names[Index], Index)];
	}
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Display(22)).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Garaż"), Right, Tabs, SAssignNew(Content, SBox), 920.f, 820.f)
	];
	if (AFWPlayerController* PC = Controller.Get())
	{
		PC->RefreshInventory();
	}
	LoadGarage();
}

SFWGarageWindow::~SFWGarageWindow()
{
	// Walking away from the bin stops sorting by hand; the open bag goes back to the bin
	if (AFWPlayerController* PC = Controller.Get(); PC && HasRound())
	{
		PC->Send(TEXT("recycling.stop"), nullptr, nullptr);
	}
}

double SFWGarageWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

int32 SFWGarageWindow::Owned(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

FString SFWGarageWindow::FurnitureId(const FString& Kind) const
{
	for (const TSharedPtr<FJsonObject>& Object : FWUi::Objects(Garage, TEXT("objects")))
	{
		if (FWUi::Str(Object, TEXT("kind")) == Kind)
		{
			return FWUi::Str(Object, TEXT("id"));
		}
	}
	return FString();
}

void SFWGarageWindow::ShowTab(int32 InTab)
{
	Tab = FMath::Clamp(InTab, 0, 4);
	LoadTab();
	Rebuild();
}

void SFWGarageWindow::LoadGarage(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + 5.0;
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("garage.get"), nullptr, [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		if (This && !(bPoll && Reply.IsError()))
		{
			This->ApplyGarage(Reply);
		}
	});
}

void SFWGarageWindow::ApplyGarage(const FFWNetReply& Reply)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != TEXT("garage"))
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		return;
	}
	const bool bFirst = !Garage.IsValid();
	Garage = Reply.Payload;
	ClockOffset = FWUi::Num(Garage, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
	// What the finished bags gave
	const TArray<TSharedPtr<FJsonObject>> Results = FWUi::Objects(Garage, TEXT("results"));
	for (const TSharedPtr<FJsonObject>& Item : Results)
	{
		Recent.Insert(TPair<FString, FString>(FWUi::Str(Item, TEXT("id")), FWUi::Str(Item, TEXT("name"))), 0);
		LastBag = FText::Format(LOCTEXT("BagDone", "Worek posegregowany! +1 {0}"), FText::FromString(FWUi::Str(Item, TEXT("name"))));
		if (PC)
		{
			PC->Toast(FText::Format(LOCTEXT("Found", "+1 {0}"), FText::FromString(FWUi::Str(Item, TEXT("name")))));
		}
	}
	if (Recent.Num() > 18)
	{
		Recent.SetNum(18);
	}
	if (PC && Results.Num() > 0)
	{
		PC->RefreshInventory();
	}
	// A bag sorted earlier the old way (by waiting) is asked about again when it should be done
	const TArray<TSharedPtr<FJsonValue>>* Queue = nullptr;
	if (Garage->TryGetArrayField(TEXT("queue"), Queue) && Queue->Num() > 0)
	{
		const double DoneAt = (*Queue)[0]->AsNumber();
		NextPoll = FPlatformTime::Seconds() + FMath::Clamp((DoneAt - ServerNow()) / 1000.0 + 0.15, 0.3, 5.0);
	}
	if (bFirst)
	{
		LoadTab();
	}
	Rebuild();
}

void SFWGarageWindow::LoadTab()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !Garage.IsValid())
	{
		return;
	}
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (Tab == 1 && !FurnitureId(TEXT("furniture_sorting_machine")).IsEmpty())
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		SendSorter(TEXT("sorter.get"), P);
	}
	else if (Tab == 2)
	{
		PC->Send(TEXT("garage.upgrades"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
			if (!This)
			{
				return;
			}
			if (Reply.Type == TEXT("garage.upgrades"))
			{
				This->Upgrades = Reply.Payload;
				This->Rebuild();
			}
			else if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
		});
	}
	else if (Tab == 3 && !FurnitureId(TEXT("furniture_wardrobe")).IsEmpty())
	{
		SendWardrobe(TEXT("wardrobe.get"), MakeShared<FJsonObject>());
	}
}

void SFWGarageWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = !Garage.IsValid() ? FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted)
		: Tab == 0 ? MakeSorting() : Tab == 1 ? MakeSorter() : Tab == 2 ? MakeUpgrades() : Tab == 3 ? MakeWardrobe() : MakeFurniture();
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page]);
	Scroll->SetScrollOffset(Offset);
}

void SFWGarageWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() < NextPoll)
	{
		return;
	}
	const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
	if (Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText")))
	{
		NextPoll = FPlatformTime::Seconds() + 2.0;
		return;
	}
	if (HasRound())
	{
		NextPoll = FPlatformTime::Seconds() + 5.0;
	}
	else
	{
		LoadGarage(true);
	}
	if (Tab == 1 && Sorter.IsValid())
	{
		SendSorter(TEXT("sorter.get"), MakeShared<FJsonObject>());
	}
}

FReply SFWGarageWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::U)
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
