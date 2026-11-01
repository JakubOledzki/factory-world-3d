#include "UI/SFWStatsWindow.h"

#include "Brushes/SlateRoundedBoxBrush.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWStats"

namespace FWStatsChart
{
	const FLinearColor Made = FLinearColor::FromSRGBColor(FColor(0x8f, 0xd6, 0x5a));
	const FLinearColor Used = FLinearColor::FromSRGBColor(FColor(0xe8, 0x89, 0x4a));

	// A rate per minute the Polish way: "2,5", whole numbers without the comma
	FString Rate(double Value)
	{
		const double Rounded = FMath::RoundToDouble(Value * 10.0) / 10.0;
		if (FMath::IsNearlyEqual(Rounded, FMath::RoundToDouble(Rounded)))
		{
			return FString::Printf(TEXT("%d"), int32(FMath::RoundToDouble(Rounded)));
		}
		return FString::Printf(TEXT("%.1f"), Rounded).Replace(TEXT("."), TEXT(","));
	}

	TArray<double> Numbers(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		TArray<double> Result;
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Object.IsValid() && Object->TryGetArrayField(Field, Values))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				Result.Add(Value->AsNumber());
			}
		}
		return Result;
	}

	// Bars of what was made and used up, side by side, one pair per stretch of time, oldest on the left
	class SBars : public SLeafWidget
	{
	public:
		SLATE_BEGIN_ARGS(SBars) {}
			SLATE_ARGUMENT(TArray<double>, Made)
			SLATE_ARGUMENT(TArray<double>, Used)
			SLATE_ARGUMENT(int32, BarMinutes)
			SLATE_ARGUMENT(FText, Period)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			MadeBars = InArgs._Made;
			UsedBars = InArgs._Used;
			BarMinutes = FMath::Max(1, InArgs._BarMinutes);
			Period = InArgs._Period;
		}

		virtual FVector2D ComputeDesiredSize(float) const override { return FVector2D(400.f, 170.f); }

		virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect, FSlateWindowElementList& Out,
			int32 LayerId, const FWidgetStyle& WidgetStyle, bool bParentEnabled) const override
		{
			const FVector2D Size = Geometry.GetLocalSize();
			const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");
			const FSlateFontInfo Font = FWStyle::Font(9);
			const FLinearColor Axis(FWStyle::Muted.R, FWStyle::Muted.G, FWStyle::Muted.B, 0.5f);
			const float Top = 6.f, Bottom = Size.Y - 18.f, Left = 52.f, Width = Size.X - Left;
			double Peak = 1.0;
			for (double V : MadeBars)
			{
				Peak = FMath::Max(Peak, V);
			}
			for (double V : UsedBars)
			{
				Peak = FMath::Max(Peak, V);
			}
			FSlateDrawElement::MakeLines(Out, LayerId, Geometry.ToPaintGeometry(), TArray<FVector2D>{ FVector2D(Left, Bottom), FVector2D(Size.X, Bottom) }, ESlateDrawEffect::None, Axis);
			FSlateDrawElement::MakeLines(Out, LayerId, Geometry.ToPaintGeometry(), TArray<FVector2D>{ FVector2D(Left, Top), FVector2D(Size.X, Top) }, ESlateDrawEffect::None, Axis * FLinearColor(1, 1, 1, 0.4f));
			// Per minute, as in the table, even where one bar covers several minutes
			auto Label = [&](const FString& Text, FVector2D At)
			{
				FSlateDrawElement::MakeText(Out, LayerId + 1, Geometry.ToPaintGeometry(FVector2D(200.f, 14.f), FSlateLayoutTransform(At)), Text, Font, ESlateDrawEffect::None, FWStyle::Muted);
			};
			Label(Rate(Peak / BarMinutes) + TEXT("/min"), FVector2D(0.f, Top - 2.f));
			Label(TEXT("0"), FVector2D(0.f, Bottom - 12.f));
			Label(Period.ToString() + TEXT(" temu"), FVector2D(Left, Bottom + 3.f));
			Label(TEXT("teraz"), FVector2D(Size.X - 30.f, Bottom + 3.f));
			const int32 N = FMath::Max(1, MadeBars.Num());
			const float Slot = Width / N;
			const float Bar = FMath::Max(1.f, Slot * 0.38f);
			for (int32 i = 0; i < MadeBars.Num(); ++i)
			{
				const float X = Left + Slot * i + Slot * 0.1f;
				const float MadeHeight = float(MadeBars[i] / Peak) * (Bottom - Top);
				const float UsedHeight = UsedBars.IsValidIndex(i) ? float(UsedBars[i] / Peak) * (Bottom - Top) : 0.f;
				if (MadeHeight > 0.f)
				{
					FSlateDrawElement::MakeBox(Out, LayerId + 1, Geometry.ToPaintGeometry(FVector2D(Bar, MadeHeight), FSlateLayoutTransform(FVector2D(X, Bottom - MadeHeight))), White, ESlateDrawEffect::None, Made);
				}
				if (UsedHeight > 0.f)
				{
					FSlateDrawElement::MakeBox(Out, LayerId + 1, Geometry.ToPaintGeometry(FVector2D(Bar, UsedHeight), FSlateLayoutTransform(FVector2D(X + Bar, Bottom - UsedHeight))), White, ESlateDrawEffect::None, Used);
				}
			}
			return LayerId + 1;
		}

	private:
		TArray<double> MadeBars;
		TArray<double> UsedBars;
		int32 BarMinutes = 1;
		FText Period;
	};

	FText PeriodName(int32 Minutes)
	{
		return Minutes == 60 ? LOCTEXT("Hour", "1 godzina") : Minutes == 1440 ? LOCTEXT("Day", "24 godziny") : LOCTEXT("TenMinutes", "10 min");
	}
}

void SFWStatsWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	TSharedRef<SHorizontalBox> Periods = SNew(SHorizontalBox);
	for (int32 Each : { 10, 60, 1440 })
	{
		Periods->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			FWUi::TabButton(FWStatsChart::PeriodName(Each), [this, Each]() { return Minutes == Each; }, [this, Each]() { SetPeriod(Each); })
		];
	}
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Statystyki produkcji"), SNullWidget::NullWidget, Periods, SAssignNew(Content, SBox), 820.f, 780.f)
	];
	Load();
}

void SFWStatsWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bLoading)
	{
		return;
	}
	bLoading = true;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("minutes"), Minutes);
	TWeakPtr<SFWStatsWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("stats.production"), P, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWStatsWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->bLoading = false;
		if (Reply.Type != TEXT("stats.production"))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		This->State = Reply.Payload;
		This->Rebuild();
		// The period changed while this was on its way
		if (int32(FWUi::Num(Reply.Payload, TEXT("minutes"))) != This->Minutes)
		{
			This->Load();
		}
	});
}

void SFWStatsWindow::SetPeriod(int32 InMinutes)
{
	Minutes = InMinutes;
	Load();
}

void SFWStatsWindow::SelectItem(const FString& Item)
{
	Selected = Item;
	Rebuild();
}

TSharedRef<SWidget> SFWStatsWindow::MakeBottlenecks()
{
	static const TMap<FString, FText> Reasons = {
		{ TEXT("no_input"), LOCTEXT("NoInput", "czeka na składniki") }, { TEXT("no_fuel"), LOCTEXT("NoFuel", "brak paliwa") },
		{ TEXT("output_full"), LOCTEXT("OutputFull", "pełna, odbierz produkty") }, { TEXT("no_recipe"), LOCTEXT("NoRecipe", "nie wybrano przepisu") },
		{ TEXT("no_power"), LOCTEXT("NoPower", "brak prądu") } };
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox);
	auto Warning = [this, &Box](const FString& Item, const FText& Text)
	{
		Box->AddSlot().AutoHeight().Padding(0, 2)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Item, 24.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[FWUi::Note(Text, FWStyle::Accent)]
		];
	};
	for (const TSharedPtr<FJsonObject>& Stall : FWUi::Objects(State, TEXT("stalls")))
	{
		FString What = FWUi::Str(Stall, TEXT("name"));
		const FString Product = FWUi::Str(Stall, TEXT("productName"));
		if (!Product.IsEmpty())
		{
			What += FString::Printf(TEXT(" (%s)"), *Product);
		}
		const int32 Count = int32(FWUi::Num(Stall, TEXT("count"), 1.0));
		if (Count > 1)
		{
			What = FString::Printf(TEXT("%d × %s"), Count, *What);
		}
		const FString Code = FWUi::Str(Stall, TEXT("reason"));
		FString Reason = Reasons.Contains(Code) ? Reasons[Code].ToString() : Code;
		TArray<FString> Missing;
		for (const TSharedPtr<FJsonObject>& Each : FWUi::Objects(Stall, TEXT("missing")))
		{
			Missing.Add(FWUi::Str(Each, TEXT("name")).ToLower());
		}
		if (Missing.Num() > 0)
		{
			Reason += TEXT(": ") + FString::Join(Missing, Code == TEXT("no_fuel") ? TEXT(" lub ") : TEXT(", "));
		}
		Warning(FWUi::Str(Stall, TEXT("kind")), FText::Format(LOCTEXT("Stalls", "{0} stoi, {1}"), FText::FromString(What), FText::FromString(Reason)));
	}
	for (const TSharedPtr<FJsonObject>& Shortage : FWUi::Objects(State, TEXT("shortages")))
	{
		Warning(FWUi::Str(Shortage, TEXT("id")), FText::Format(LOCTEXT("Shortage", "{0}: zużywasz {1}/min, a robisz {2}/min. Zapas się kończy."),
			FText::FromString(FWUi::Str(Shortage, TEXT("name"))), FText::FromString(FWStatsChart::Rate(FWUi::Num(Shortage, TEXT("consumedPerMinute")))),
			FText::FromString(FWStatsChart::Rate(FWUi::Num(Shortage, TEXT("producedPerMinute"))))));
	}
	if (Box->NumSlots() == 0)
	{
		Box->AddSlot().AutoHeight()
		[
			FWUi::Note(LOCTEXT("NoStalls", "Nie widać wąskich gardeł: maszyny pracują, a niczego nie zużywasz szybciej, niż robisz."), FWStyle::Good)
		];
	}
	for (const TSharedPtr<FJsonObject>& Machine : FWUi::Objects(State, TEXT("machines")))
	{
		const int32 Working = int32(FWUi::Num(Machine, TEXT("working"))), Total = int32(FWUi::Num(Machine, TEXT("total")));
		Box->AddSlot().AutoHeight().Padding(0, 2)
		[
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(Working == Total ? FWStyle::Good : FWStyle::Muted)
			.Text(FText::Format(LOCTEXT("Machine", "{0}: pracuje {1} z {2}"), FText::FromString(FWUi::Str(Machine, TEXT("name"))), Working, Total))
		];
	}
	return Box;
}

TSharedRef<SWidget> SFWStatsWindow::MakeTable()
{
	auto Row = [this](const FString& Item, const FText& Name, const FText& Made, const FText& Used, const FText& Net, const FLinearColor& NameColor,
		const FLinearColor& NetColor, bool bHeader)
	{
		TSharedRef<SHorizontalBox> Line = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Item, 24.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Text(Name).Font(FWStyle::Font(11, bHeader ? TEXT("Bold") : TEXT("Regular"))).ColorAndOpacity(NameColor)
			];
		const TPair<FText, FLinearColor> Columns[] = { { Made, bHeader ? FWStyle::Muted : FWStatsChart::Made }, { Used, bHeader ? FWStyle::Muted : FWStatsChart::Used }, { Net, NetColor } };
		for (const TPair<FText, FLinearColor>& Column : Columns)
		{
			Line->AddSlot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SBox).WidthOverride(100.f).HAlign(HAlign_Right)
				[
					SNew(STextBlock).Text(Column.Key).Font(FWStyle::Font(11, bHeader ? TEXT("Bold") : TEXT("Regular"))).ColorAndOpacity(Column.Value)
				]
			];
		}
		return Line;
	};
	TSharedRef<SVerticalBox> Table = SNew(SVerticalBox);
	Table->AddSlot().AutoHeight().Padding(8, 0, 8, 4)
	[
		Row(FString(), LOCTEXT("Item", "Przedmiot"), LOCTEXT("Made", "Produkcja"), LOCTEXT("Used", "Zużycie"), LOCTEXT("Net", "Bilans"), FWStyle::Muted, FWStyle::Muted, true)
	];
	const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(State, TEXT("items"));
	if (Items.Num() == 0)
	{
		Table->AddSlot().AutoHeight().Padding(8, 4)[FWUi::Note(LOCTEXT("Nothing", "W tym czasie fabryka nic nie zrobiła ani nie zużyła."), FWStyle::Muted)];
	}
	TWeakPtr<SFWStatsWindow> WeakThis = SharedThis(this);
	for (const TSharedPtr<FJsonObject>& Item : Items)
	{
		const FString Id = FWUi::Str(Item, TEXT("id"));
		// Rounded as shown, so the balance adds up with the two numbers beside it
		const double Made = FMath::RoundToDouble(FWUi::Num(Item, TEXT("producedPerMinute")) * 10.0) / 10.0;
		const double Used = FMath::RoundToDouble(FWUi::Num(Item, TEXT("consumedPerMinute")) * 10.0) / 10.0;
		const double Net = Made - Used;
		const bool bChosen = Id == Selected;
		Table->AddSlot().AutoHeight().Padding(0, 1)
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ButtonColorAndOpacity(bChosen ? FLinearColor(4.f, 2.2f, 0.5f, 1.f) : FLinearColor(1.f, 1.f, 1.f, 0.35f))
			.ContentPadding(FMargin(8.f, 2.f))
			.ToolTipText(FText::Format(LOCTEXT("Totals", "Razem: zrobiono {0}, zużyto {1}"), int32(FWUi::Num(Item, TEXT("produced"))), int32(FWUi::Num(Item, TEXT("consumed")))))
			.OnClicked_Lambda([WeakThis, Id]() { if (TSharedPtr<SFWStatsWindow> This = WeakThis.Pin()) { This->SelectItem(Id); } return FReply::Handled(); })
			[
				Row(Id, FText::FromString(FWUi::Str(Item, TEXT("name"))), FText::FromString(FWStatsChart::Rate(Made)), FText::FromString(FWStatsChart::Rate(Used)),
					FText::FromString((Net > 0.0 ? TEXT("+") : TEXT("")) + FWStatsChart::Rate(Net)), bChosen ? FWStyle::Accent : FWStyle::Text,
					Net > 0.0 ? FWStyle::Good : Net < 0.0 ? FWStyle::Error : FWStyle::Muted, false)
			]
		];
	}
	return Table;
}

void SFWStatsWindow::Rebuild()
{
	if (!State.IsValid() || !Content.IsValid())
	{
		return;
	}
	const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(State, TEXT("items"));
	TSharedPtr<FJsonObject> Chosen;
	for (const TSharedPtr<FJsonObject>& Item : Items)
	{
		Chosen = !Chosen.IsValid() && FWUi::Str(Item, TEXT("id")) == Selected ? Item : Chosen;
	}
	if (!Chosen.IsValid() && Items.Num() > 0)
	{
		Chosen = Items[0];
		Selected = FWUi::Str(Chosen, TEXT("id"));
	}
	const int32 Period = int32(FWUi::Num(State, TEXT("minutes"), Minutes));
	const int32 BarMinutes = FMath::Max(1, int32(FWUi::Num(State, TEXT("barMinutes"), 1.0)));
	TArray<double> Empty;
	Empty.Init(0.0, Period / BarMinutes);
	auto Legend = [](const FText& Text, const FLinearColor& Color)
	{
		return SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 5, 0)[SNew(SBox).WidthOverride(12.f).HeightOverride(12.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(Color)]]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)];
	};
	// Asked again every few seconds: the list stays where it was scrolled
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(
		SAssignNew(Scroll, SScrollBox)
		+ SScrollBox::Slot().Padding(0, 0, 12, 0)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()[FWUi::Heading(LOCTEXT("Bottlenecks", "Wąskie gardła"))]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 14)[MakeBottlenecks()]
			+ SVerticalBox::Slot().AutoHeight()
			[
				FWUi::Heading(Chosen.IsValid() ? FText::Format(LOCTEXT("ChartOf", "Wykres: {0}"), FText::FromString(FWUi::Str(Chosen, TEXT("name")))) : LOCTEXT("Chart", "Wykres"))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 4)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 14, 0)[Legend(LOCTEXT("LegendMade", "Produkcja"), FWStatsChart::Made)]
				+ SHorizontalBox::Slot().AutoWidth()[Legend(LOCTEXT("LegendUsed", "Zużycie"), FWStatsChart::Used)]
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 4, 0, 14)
			[
				SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(10.f)
				[
					SNew(FWStatsChart::SBars)
					.Made(Chosen.IsValid() ? FWStatsChart::Numbers(Chosen, TEXT("producedBars")) : Empty)
					.Used(Chosen.IsValid() ? FWStatsChart::Numbers(Chosen, TEXT("consumedBars")) : Empty)
					.BarMinutes(BarMinutes)
					.Period(FWStatsChart::PeriodName(Period))
				]
			]
			+ SVerticalBox::Slot().AutoHeight()[MakeTable()]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 10, 0, 0)
			[
				FWUi::Note(LOCTEXT("Explain", "Na minutę. Liczy się to, co zrobiły piece, maszyny i stół warsztatowy, oraz to, co zebrałeś, wykopałeś i posegregowałeś. Kliknij przedmiot, aby zobaczyć jego wykres."), FWStyle::Muted)
			]
		]);
	Scroll->SetScrollOffset(Offset);
}

void SFWStatsWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (CurrentTime >= NextPoll)
	{
		if (NextPoll > 0.0)
		{
			Load();
		}
		NextPoll = CurrentTime + 5.0;
	}
}

FReply SFWStatsWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::L)
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
