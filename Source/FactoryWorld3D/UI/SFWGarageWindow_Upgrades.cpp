// The garage's upgrades tab.
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

TSharedRef<SWidget> SFWGarageWindow::MakeUpgrades()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (!Upgrades.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TArray<TSharedPtr<FJsonObject>> Tracks = FWUi::Objects(Upgrades, TEXT("tracks"));
	int32 Total = 0, Most = 0;
	TSharedPtr<FJsonObject> Chosen;
	TSharedRef<SWrapBox> TrackTabs = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
	for (const TSharedPtr<FJsonObject>& T : Tracks)
	{
		Total += int32(FWUi::Num(T, TEXT("level")));
		Most += int32(FWUi::Num(T, TEXT("maxLevel")));
		const FString Id = FWUi::Str(T, TEXT("id"));
		if (Id == Track)
		{
			Chosen = T;
		}
		TrackTabs->AddSlot()
		[
			FWUi::SmallButton(FText::FromString(FString::Printf(TEXT("%s  %d/%d"), *FWUi::Str(T, TEXT("name")), int32(FWUi::Num(T, TEXT("level"))), int32(FWUi::Num(T, TEXT("maxLevel"))))),
				[WeakThis, Id]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->Track = Id;
						This->Rebuild();
					}
				}, Id == Track)
		];
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Twój lokal: %s. Ulepszenia: %d z %d. Każdy poziom daje premię na zawsze."), *FWUi::Str(Upgrades, TEXT("rank")), Total, Most)), FWStyle::Muted), 0.f);
	Add(TrackTabs, 8.f);
	if (!Chosen.IsValid())
	{
		return Page;
	}
	const int32 Level = int32(FWUi::Num(Chosen, TEXT("level")));
	const TArray<TSharedPtr<FJsonObject>> Steps = FWUi::Objects(Chosen, TEXT("steps"));
	const FString Unit = FWUi::Str(Chosen, TEXT("unit"));
	Add(SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
		.Text(FText::FromString(FString::Printf(TEXT("Teraz: %s"), Level == 0 ? *FWUi::Str(Chosen, TEXT("start")) : *FWUi::Str(Steps[Level - 1], TEXT("name"))))), 14.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Good)
		.Text(FText::FromString(FString::Printf(TEXT("%s %d%s (każdy poziom: +%d%s)"), *FWUi::Str(Chosen, TEXT("bonusName")), int32(FWUi::Num(Chosen, TEXT("bonus"))), *Unit,
			int32(FWUi::Num(Chosen, TEXT("perLevel"))), *Unit))), 2.f);

	// The next level: what it costs and needs
	if (Steps.IsValidIndex(Level))
	{
		const TSharedPtr<FJsonObject>& Step = Steps[Level];
		const double Price = FWUi::Num(Step, TEXT("priceCents"));
		bool bReady = PC && double(PC->GetBalanceCents()) >= Price;
		TSharedRef<SVerticalBox> Card = SNew(SVerticalBox);
		Card->AddSlot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
				.Text(FText::FromString(FString::Printf(TEXT("Poziom %d: %s"), int32(FWUi::Num(Step, TEXT("level"))), *FWUi::Str(Step, TEXT("name")))))
			]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
				.Text(FText::FromString(FString::Printf(TEXT("+%d%s"), int32(FWUi::Num(Chosen, TEXT("perLevel"))), *Unit)))
			]
		];
		Card->AddSlot().AutoHeight().Padding(0, 6, 0, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(bReady ? FWStyle::Text : FWStyle::Error).Text(FWUi::Money(Price))
		];
		TSharedRef<SWrapBox> Materials = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(16.f, 6.f));
		for (const TSharedPtr<FJsonObject>& Material : FWUi::Objects(Step, TEXT("materials")))
		{
			const FString Id = FWUi::Str(Material, TEXT("id"));
			const int32 Need = int32(FWUi::Num(Material, TEXT("quantity"))), Have = Owned(Id);
			bReady &= Have >= Need;
			Materials->AddSlot()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Icon(PC, Id, 28.f)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Have >= Need ? FWStyle::Text : FWStyle::Error)
					.Text(FText::FromString(FString::Printf(TEXT("%s %d/%d"), *FWUi::Str(Material, TEXT("name")), FMath::Min(Have, Need), Need)))
				]
			];
		}
		Card->AddSlot().AutoHeight().Padding(0, 8, 0, 0)[Materials];
		for (const TSharedPtr<FJsonObject>& Need : FWUi::Objects(Step, TEXT("requires")))
		{
			int32 OtherLevel = 0;
			for (const TSharedPtr<FJsonObject>& T : Tracks)
			{
				if (FWUi::Str(T, TEXT("id")) == FWUi::Str(Need, TEXT("track")))
				{
					OtherLevel = int32(FWUi::Num(T, TEXT("level")));
				}
			}
			const bool bMet = OtherLevel >= int32(FWUi::Num(Need, TEXT("level")));
			bReady &= bMet;
			Card->AddSlot().AutoHeight().Padding(0, 4, 0, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(bMet ? FWStyle::Good : FWStyle::Error)
				.Text(FText::FromString(FString::Printf(TEXT("%s Wymaga: %s na poziomie %d"), bMet ? TEXT("✓") : TEXT("✗"), *FWUi::Str(Need, TEXT("name")), int32(FWUi::Num(Need, TEXT("level"))))))
			];
		}
		const FString TrackId = FWUi::Str(Chosen, TEXT("id"));
		Card->AddSlot().AutoHeight().Padding(0, 10, 0, 0).HAlign(HAlign_Left)
		[
			SNew(SBox).ToolTipText(bReady ? FText::GetEmpty() : LOCTEXT("NotReady", "Brakuje pieniędzy, materiałów albo innego ulepszenia."))
			[
				FWUi::SmallButton(LOCTEXT("Upgrade", "Ulepsz"), [WeakThis, TrackId]()
				{
					TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
					AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
					if (!Inner)
					{
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("track"), TrackId);
					Inner->Send(TEXT("garage.upgrade"), P, [WeakThis](const FFWNetReply& Reply)
					{
						TSharedPtr<SFWGarageWindow> Again = WeakThis.Pin();
						AFWPlayerController* Owner = Again ? Again->Controller.Get() : nullptr;
						if (!Owner)
						{
							return;
						}
						if (Reply.Type != TEXT("garage.upgrades"))
						{
							Owner->ToastError(Reply);
							return;
						}
						Again->Upgrades = Reply.Payload;
						Owner->SetBalanceCents(int64(FWUi::Num(Reply.Payload, TEXT("balanceCents"))));
						Owner->PlayUiSound(TEXT("build_place"));
						Owner->RefreshInventory();
						Owner->Toast(LOCTEXT("Upgraded", "Garaż ulepszony"));
						Again->Rebuild();
					});
				}, true, bReady)
			]
		];
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(14.f)[Card], 14.f);
	}
	else
	{
		Add(SNew(STextBlock).Font(FWStyle::Display(18, TEXT("SemiBold"))).ColorAndOpacity(FWStyle::Accent).Text(LOCTEXT("Maxed", "Ulepszone do końca.")), 14.f);
	}

	// All levels of the track
	Add(FWUi::Heading(LOCTEXT("AllLevels", "Wszystkie poziomy")), 18.f);
	for (int32 Index = 0; Index < Steps.Num(); ++Index)
	{
		const bool bDone = Index < Level;
		const FLinearColor Color = bDone ? FWStyle::Good : Index == Level ? FWStyle::Accent : FWStyle::Muted;
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)
			[
				SNew(SBox).WidthOverride(46.f)[SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Color)
					.Text(FText::FromString(FString::Printf(TEXT("%s %d."), bDone ? TEXT("✓") : Index == Level ? TEXT("▶") : TEXT(" "), Index + 1)))]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)[SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Color).Text(FText::FromString(FWUi::Str(Steps[Index], TEXT("name"))))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Color)
				.Text(FText::FromString(FString::Printf(TEXT("+%d%s"), (Index + 1) * int32(FWUi::Num(Chosen, TEXT("perLevel"))), *Unit)))
			], 2.f);
	}
	return Page;
}

#undef LOCTEXT_NAMESPACE
