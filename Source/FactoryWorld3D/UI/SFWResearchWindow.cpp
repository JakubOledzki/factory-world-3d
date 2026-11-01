#include "UI/SFWResearchWindow.h"

#include "Brushes/SlateRoundedBoxBrush.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SCanvas.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWResearch"

namespace FWResearchTree
{
	const FVector2D CardSize(224.f, 70.f);
	const FVector2D Gap(52.f, 22.f);
	const FVector2D Margin(18.f, 40.f);
	const FLinearColor Board(0.045f, 0.047f, 0.055f, 0.92f);
	const FLinearColor Line(0.29f, 0.29f, 0.32f);

	FLinearColor Ring(const FString& Status)
	{
		if (Status == TEXT("done"))
		{
			return FWStyle::Good;
		}
		if (Status == TEXT("active"))
		{
			return FWStyle::Accent;
		}
		return Status == TEXT("available") ? FLinearColor(0.79f, 0.76f, 0.7f, 0.6f) : FLinearColor(0.24f, 0.24f, 0.26f);
	}

	// Rounded card and ring brushes, one per look, kept for the run
	const FSlateBrush* Rounded(const FLinearColor& Fill, const FLinearColor& Outline, float Width, float Radius)
	{
		static TMap<FString, TSharedPtr<FSlateRoundedBoxBrush>> Brushes;
		const FString Key = FString::Printf(TEXT("%s|%s|%g|%g"), *Fill.ToString(), *Outline.ToString(), Width, Radius);
		TSharedPtr<FSlateRoundedBoxBrush>& Brush = Brushes.FindOrAdd(Key);
		if (!Brush.IsValid())
		{
			Brush = MakeShared<FSlateRoundedBoxBrush>(Fill, Radius, Outline, Width);
		}
		return Brush.Get();
	}

	struct FLink
	{
		FVector2D From;
		FVector2D To;
		FLinearColor Color;
		bool bGlow = false;
	};

	// Draws the column titles and the curves between the cards underneath them
	class SLinks : public SLeafWidget
	{
	public:
		SLATE_BEGIN_ARGS(SLinks) {}
			SLATE_ARGUMENT(TArray<FLink>, Links)
			SLATE_ARGUMENT(TArray<FVector2D>, Columns)
			SLATE_ARGUMENT(FVector2D, Size)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Links = InArgs._Links;
			Columns = InArgs._Columns;
			Size = InArgs._Size;
		}

		virtual FVector2D ComputeDesiredSize(float) const override { return Size; }

		virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect, FSlateWindowElementList& Out,
			int32 LayerId, const FWidgetStyle& WidgetStyle, bool bParentEnabled) const override
		{
			const FSlateFontInfo Font = FWStyle::Font(9, TEXT("Bold"));
			for (int32 Column = 0; Column < Columns.Num(); ++Column)
			{
				const FVector2D At = Columns[Column];
				FSlateDrawElement::MakeText(Out, LayerId, Geometry.ToPaintGeometry(FVector2D(CardSize.X, 14.f), FSlateLayoutTransform(At - FVector2D(0.f, 22.f))),
					FText::Format(LOCTEXT("Stage", "ETAP {0}"), Column + 1), Font, ESlateDrawEffect::None, FLinearColor(FWStyle::Muted.R, FWStyle::Muted.G, FWStyle::Muted.B, 0.7f));
				FSlateDrawElement::MakeLines(Out, LayerId, Geometry.ToPaintGeometry(), TArray<FVector2D>{ At - FVector2D(0.f, 6.f), At + FVector2D(CardSize.X, -6.f) },
					ESlateDrawEffect::None, FLinearColor(0.17f, 0.18f, 0.19f), true, 1.f);
			}
			for (const FLink& Link : Links)
			{
				// A cubic curve leaving and entering level, as a Hermite spline (tangents three times the handles)
				const float Bend = FMath::Max(float(Link.To.X - Link.From.X) * 0.5f, 30.f);
				const FVector2D Tangent(Bend * 3.f, 0.f);
				if (Link.bGlow)
				{
					FSlateDrawElement::MakeSpline(Out, LayerId + 1, Geometry.ToPaintGeometry(), Link.From, Tangent, Link.To, Tangent, 8.f,
						ESlateDrawEffect::None, FLinearColor(Link.Color.R, Link.Color.G, Link.Color.B, 0.18f));
				}
				FSlateDrawElement::MakeSpline(Out, LayerId + 2, Geometry.ToPaintGeometry(), Link.From, Tangent, Link.To, Tangent, 2.5f, ESlateDrawEffect::None, Link.Color);
				for (const FVector2D& End : { Link.From, Link.To })
				{
					FSlateDrawElement::MakeBox(Out, LayerId + 3, Geometry.ToPaintGeometry(FVector2D(8.f), FSlateLayoutTransform(End - FVector2D(4.f))),
						Rounded(Link.Color, Link.Color, 0.f, 4.f), ESlateDrawEffect::None, FLinearColor::White);
				}
			}
			return LayerId + 3;
		}

	private:
		TArray<FLink> Links;
		TArray<FVector2D> Columns;
		FVector2D Size = FVector2D::ZeroVector;
	};
}

void SFWResearchWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	TSharedRef<SWidget> Body = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 14, 0)
		[
			SNew(SBorder).BorderImage(FWResearchTree::Rounded(FWResearchTree::Board, FLinearColor(0.17f, 0.18f, 0.19f), 1.f, 8.f)).Padding(10.f)
			[
				SAssignNew(TreeBox, SBox)
			]
		]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			SNew(SBox).WidthOverride(350.f)
			[
				SNew(SScrollBox) + SScrollBox::Slot().Padding(0, 0, 10, 0)[SAssignNew(Details, SBox)]
			]
		];
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Stół badawczy – drzewko technologii"), Right, SAssignNew(Tabs, SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f)),
			Body, 1500.f, 800.f)
	];
	Load();
}

void SFWResearchWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bLoading)
	{
		return;
	}
	bLoading = true;
	NextPoll = ServerNow() + 500.0;
	TWeakPtr<SFWResearchWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("research.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin())
		{
			This->bLoading = false;
			This->Apply(Reply);
		}
	});
	PC->RefreshInventory();
}

void SFWResearchWindow::Apply(const FFWNetReply& Reply)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != TEXT("research"))
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		return;
	}
	State = Reply.Payload;
	ClockOffset = FWUi::Num(State, TEXT("serverTime")) - FWUi::LocalNowMs();
	const TArray<TSharedPtr<FJsonObject>> All = Technologies();
	if ((Selected.IsEmpty() || !Technology(Selected).IsValid()) && All.Num() > 0)
	{
		// The one being researched, else one ready to research, else the first
		TSharedPtr<FJsonObject> Pick = Active();
		for (int32 i = 0; !Pick.IsValid() && i < All.Num(); ++i)
		{
			Pick = FWUi::Str(All[i], TEXT("status")) == TEXT("available") ? All[i] : nullptr;
		}
		Selected = FWUi::Str(Pick.IsValid() ? Pick : All[0], TEXT("id"));
	}
	if (Branch.IsEmpty())
	{
		Branch = FWUi::Str(Technology(Selected), TEXT("branch"));
	}
	const TArray<TSharedPtr<FJsonObject>> Finished = FWUi::Objects(State, TEXT("finished"));
	if (PC && Finished.Num() > 0)
	{
		TArray<FString> Names;
		for (const TSharedPtr<FJsonObject>& Done : Finished)
		{
			Names.Add(FWUi::Str(Done, TEXT("name")));
		}
		PC->Toast(FText::Format(LOCTEXT("Researched", "Zbadano: {0}"), FText::FromString(FString::Join(Names, TEXT(", ")))));
		PC->PlayUiSound(TEXT("build_place"));
	}
	Rebuild();
}

void SFWResearchWindow::Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bBusy)
	{
		return;
	}
	bBusy = true;
	RebuildDetails();
	TWeakPtr<SFWResearchWindow> WeakThis = SharedThis(this);
	const bool bMoney = FWUi::Str(Payload, TEXT("item")) == TEXT("money") || FCString::Strcmp(Type, TEXT("research.companyCancel")) == 0;
	PC->Send(Type, Payload, [WeakThis, bMoney](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWResearchWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		This->bBusy = false;
		This->Apply(Reply);
		// The inventory comes back a moment later; the costs are shown again then
		This->DetailsAgainAt = FPlatformTime::Seconds() + 0.7;
		if (Reply.Type == TEXT("research"))
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
			if (bMoney)
			{
				Inner->RefreshWallet();
			}
		}
	});
}

TArray<TSharedPtr<FJsonObject>> SFWResearchWindow::Technologies() const
{
	return FWUi::Objects(State, TEXT("technologies"));
}

TSharedPtr<FJsonObject> SFWResearchWindow::Technology(const FString& Id) const
{
	for (const TSharedPtr<FJsonObject>& Each : Technologies())
	{
		if (FWUi::Str(Each, TEXT("id")) == Id)
		{
			return Each;
		}
	}
	return nullptr;
}

TSharedPtr<FJsonObject> SFWResearchWindow::Active() const
{
	for (const TSharedPtr<FJsonObject>& Each : Technologies())
	{
		if (FWUi::Str(Each, TEXT("status")) == TEXT("active"))
		{
			return Each;
		}
	}
	return nullptr;
}

double SFWResearchWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

int32 SFWResearchWindow::Count(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

bool SFWResearchWindow::CanPay(const TSharedPtr<FJsonObject>& Tech) const
{
	for (const TSharedPtr<FJsonObject>& Cost : FWUi::Objects(Tech, TEXT("cost")))
	{
		if (Count(FWUi::Str(Cost, TEXT("id"))) < int32(FWUi::Num(Cost, TEXT("quantity"))))
		{
			return false;
		}
	}
	return true;
}

FString SFWResearchWindow::IconOf(const TSharedPtr<FJsonObject>& Tech) const
{
	// What it unlocks first; one that unlocks no item (like bigger grippers) names an item of its own
	const TArray<TSharedPtr<FJsonObject>> Unlocks = FWUi::Objects(Tech, TEXT("unlocks"));
	return Unlocks.Num() > 0 ? FWUi::Str(Unlocks[0], TEXT("id")) : FWUi::Str(Tech, TEXT("icon"));
}

FLinearColor SFWResearchWindow::StatusColor(const FString& Status) const
{
	return Status == TEXT("done") ? FWStyle::Good : Status == TEXT("active") ? FWStyle::Accent : Status == TEXT("available") ? FWStyle::Text : FWStyle::Muted;
}

FText SFWResearchWindow::StatusText(const TSharedPtr<FJsonObject>& Tech) const
{
	const FString Status = FWUi::Str(Tech, TEXT("status"));
	if (Status == TEXT("done"))
	{
		return LOCTEXT("Done", "Zbadane");
	}
	if (Status == TEXT("active"))
	{
		return FText::Format(LOCTEXT("Active", "Badanie: {0}"), FWUi::Duration(FMath::CeilToDouble((FWUi::Num(Tech, TEXT("finishesAt")) - ServerNow()) / 1000.0)));
	}
	if (Status == TEXT("available"))
	{
		if (FWUi::Bool(Tech, TEXT("companyOnly")))
		{
			return LOCTEXT("CompanyOnly", "Tylko dla spółek");
		}
		return CanPay(Tech) ? LOCTEXT("Ready", "Gotowe do zbadania") : LOCTEXT("Short", "Brakuje materiałów");
	}
	TArray<FString> Missing;
	for (const FString& Need : FWUi::Strings(Tech, TEXT("requires")))
	{
		if (FWUi::Str(Technology(Need), TEXT("status")) != TEXT("done"))
		{
			Missing.Add(FWUi::Str(Technology(Need), TEXT("name")));
		}
	}
	if (Missing.Num() > 1)
	{
		return FText::Format(LOCTEXT("NeedsMany", "Wymaga {0} badań"), Missing.Num());
	}
	return FText::Format(LOCTEXT("Needs", "Wymaga: {0}"), FText::FromString(FString::Join(Missing, TEXT(", "))));
}

void SFWResearchWindow::Choose(const FString& Id)
{
	if (!Technology(Id).IsValid())
	{
		return;
	}
	Selected = Id;
	const FString Its = FWUi::Str(Technology(Id), TEXT("branch"));
	if (Its != Branch)
	{
		Branch = Its;
	}
	Rebuild();
}

void SFWResearchWindow::OpenBranch(const FString& Id)
{
	if (Id == Branch)
	{
		return;
	}
	Branch = Id;
	// Its most interesting technology: the one being researched, else one ready, else the first
	TSharedPtr<FJsonObject> Pick;
	for (const TCHAR* Wanted : { TEXT("active"), TEXT("available"), TEXT("") })
	{
		for (const TSharedPtr<FJsonObject>& Each : Technologies())
		{
			if (!Pick.IsValid() && FWUi::Str(Each, TEXT("branch")) == Id && (!*Wanted || FWUi::Str(Each, TEXT("status")) == Wanted))
			{
				Pick = Each;
			}
		}
	}
	Selected = FWUi::Str(Pick, TEXT("id"));
	Rebuild();
}

void SFWResearchWindow::Rebuild()
{
	if (!State.IsValid() || !Tabs.IsValid())
	{
		return;
	}
	Tabs->ClearChildren();
	for (const TSharedPtr<FJsonObject>& Each : FWUi::Objects(State, TEXT("branches")))
	{
		const FString Id = FWUi::Str(Each, TEXT("id"));
		int32 Done = 0, Total = 0;
		bool bActive = false;
		for (const TSharedPtr<FJsonObject>& Tech : Technologies())
		{
			if (FWUi::Str(Tech, TEXT("branch")) == Id)
			{
				++Total;
				Done += FWUi::Str(Tech, TEXT("status")) == TEXT("done");
				bActive |= FWUi::Str(Tech, TEXT("status")) == TEXT("active");
			}
		}
		// The open branch in amber; others amber while researched, green when all done
		const FLinearColor Color = Id == Branch || bActive ? FWStyle::Accent : Done == Total ? FWStyle::Good : FWStyle::Text;
		Tabs->AddSlot()
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ContentPadding(FMargin(8.f, 3.f))
			.ToolTipText(FText::FromString(FWUi::Str(Each, TEXT("name")) + (bActive ? TEXT(" (trwa badanie)") : TEXT(""))))
			.OnClicked_Lambda([this, Id]() { OpenBranch(Id); return FReply::Handled(); })
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Icon(Controller.Get(), FWUi::Str(Each, TEXT("icon")), 22.f)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(Color)
					.Text(FText::Format(LOCTEXT("Tab", "{0}  {1}/{2}"), FText::FromString(FWUi::Str(Each, TEXT("name"))), Done, Total))
				]
			]
		];
	}
	TreeBox->SetContent(MakeTree());
	RebuildDetails();
}

TSharedRef<SWidget> SFWResearchWindow::MakeTree()
{
	using namespace FWResearchTree;
	TArray<TSharedPtr<FJsonObject>> Members;
	TMap<FString, TSharedPtr<FJsonObject>> Inside;
	for (const TSharedPtr<FJsonObject>& Tech : Technologies())
	{
		if (FWUi::Str(Tech, TEXT("branch")) == Branch)
		{
			Members.Add(Tech);
			Inside.Add(FWUi::Str(Tech, TEXT("id")), Tech);
		}
	}
	// How deep each lies in its branch: 0 when it needs nothing from the branch
	TMap<FString, int32> Depth;
	TFunction<int32(const FString&)> DepthOf = [&](const FString& Id) -> int32
	{
		if (const int32* Known = Depth.Find(Id))
		{
			return *Known;
		}
		Depth.Add(Id, 0);
		int32 D = 0;
		for (const FString& Need : FWUi::Strings(Inside[Id], TEXT("requires")))
		{
			if (Inside.Contains(Need))
			{
				D = FMath::Max(D, DepthOf(Need) + 1);
			}
		}
		Depth.Add(Id, D);
		return D;
	};
	TArray<TArray<TSharedPtr<FJsonObject>>> Stages;
	for (const TSharedPtr<FJsonObject>& Tech : Members)
	{
		const int32 D = DepthOf(FWUi::Str(Tech, TEXT("id")));
		if (Stages.Num() <= D)
		{
			Stages.SetNum(D + 1);
		}
		Stages[D].Add(Tech);
	}
	// Each card level with the middle of the cards it needs, as far as the cards above let it
	TMap<FString, FVector2D> Places;
	float Bottom = 0.f;
	TArray<FVector2D> Titles;
	for (int32 D = 0; D < Stages.Num(); ++D)
	{
		TMap<FString, float> Wanted;
		for (const TSharedPtr<FJsonObject>& Tech : Stages[D])
		{
			float Sum = 0.f;
			int32 Parents = 0;
			for (const FString& Need : FWUi::Strings(Tech, TEXT("requires")))
			{
				if (const FVector2D* At = Places.Find(Need))
				{
					Sum += At->Y;
					++Parents;
				}
			}
			Wanted.Add(FWUi::Str(Tech, TEXT("id")), Parents > 0 ? Sum / Parents : -1.f);
		}
		TArray<TSharedPtr<FJsonObject>> Column = Stages[D];
		if (D > 0)
		{
			Column.StableSort([&Wanted](const TSharedPtr<FJsonObject>& A, const TSharedPtr<FJsonObject>& B)
			{
				return Wanted[FWUi::Str(A, TEXT("id"))] < Wanted[FWUi::Str(B, TEXT("id"))];
			});
		}
		const float X = Margin.X + D * (CardSize.X + Gap.X);
		Titles.Add(FVector2D(X, Margin.Y));
		float Next = Margin.Y;
		for (const TSharedPtr<FJsonObject>& Tech : Column)
		{
			const float Y = FMath::Max(Next, Wanted[FWUi::Str(Tech, TEXT("id"))]);
			Places.Add(FWUi::Str(Tech, TEXT("id")), FVector2D(X, Y));
			Next = Y + CardSize.Y + Gap.Y;
		}
		Bottom = FMath::Max(Bottom, Next - float(Gap.Y));
	}
	TArray<FLink> Links;
	TSharedRef<SCanvas> Canvas = SNew(SCanvas);
	for (const TSharedPtr<FJsonObject>& Tech : Members)
	{
		const FString Id = FWUi::Str(Tech, TEXT("id"));
		TArray<TSharedPtr<FJsonObject>> Outside;
		for (const FString& Need : FWUi::Strings(Tech, TEXT("requires")))
		{
			if (const FVector2D* From = Places.Find(Need))
			{
				const FString FromStatus = FWUi::Str(Technology(Need), TEXT("status"));
				const FString ToStatus = FWUi::Str(Tech, TEXT("status"));
				// Green from what is done, amber into what is being researched
				const FLinearColor Color = FromStatus == TEXT("done") ? (ToStatus == TEXT("active") ? FWStyle::Accent : FWStyle::Good) : Line;
				Links.Add({ *From + FVector2D(CardSize.X, CardSize.Y * 0.5), Places[Id] + FVector2D(0.0, CardSize.Y * 0.5), Color, FromStatus == TEXT("done") });
			}
			else if (TSharedPtr<FJsonObject> Other = Technology(Need))
			{
				Outside.Add(Other);
			}
		}
		Canvas->AddSlot().Position(Places[Id]).Size(CardSize)[MakeCard(Tech, Outside)];
	}
	const FVector2D Size(Margin.X * 2 + Stages.Num() * (CardSize.X + Gap.X) - Gap.X, Bottom + Margin.X);
	return SNew(SScrollBox).Orientation(Orient_Horizontal)
		+ SScrollBox::Slot()
		[
			SNew(SScrollBox)
			+ SScrollBox::Slot()
			[
				SNew(SOverlay)
				+ SOverlay::Slot()[SNew(SLinks).Links(Links).Columns(Titles).Size(Size)]
				+ SOverlay::Slot()[SNew(SBox).WidthOverride(Size.X).HeightOverride(Size.Y)[Canvas]]
			]
		];
}

TSharedRef<SWidget> SFWResearchWindow::MakeCard(const TSharedPtr<FJsonObject>& Tech, const TArray<TSharedPtr<FJsonObject>>& Outside)
{
	using namespace FWResearchTree;
	const FString Id = FWUi::Str(Tech, TEXT("id"));
	const FString Status = FWUi::Str(Tech, TEXT("status"));
	const bool bSelected = Id == Selected;
	const bool bLocked = Status == TEXT("locked");
	const FLinearColor RingColor = Ring(Status);
	// What it needs from other branches: their icons along the top, ringed green once done
	TSharedRef<SHorizontalBox> Chips = SNew(SHorizontalBox);
	for (int32 i = 0; i < FMath::Min(Outside.Num(), 5); ++i)
	{
		const bool bDone = FWUi::Str(Outside[i], TEXT("status")) == TEXT("done");
		Chips->AddSlot().AutoWidth().Padding(2, 0)
		[
			SNew(SBorder).BorderImage(Rounded(FLinearColor(0.08f, 0.08f, 0.09f), bDone ? FWStyle::Good : FLinearColor(0.35f, 0.36f, 0.38f), 1.5f, 10.f)).Padding(2.f)
			.ToolTipText(FText::FromString(FWUi::Str(Outside[i], TEXT("name"))))
			[
				FWUi::Icon(Controller.Get(), IconOf(Outside[i]), 14.f)
			]
		];
	}
	if (Outside.Num() > 5)
	{
		Chips->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(2, 0)
		[
			SNew(STextBlock).Text(FText::Format(LOCTEXT("More", "+{0}"), Outside.Num() - 5)).Font(FWStyle::Font(9, TEXT("Bold"))).ColorAndOpacity(FWStyle::Muted)
		];
	}
	TWeakPtr<SFWResearchWindow> WeakThis = SharedThis(this);
	TSharedRef<SWidget> Ring = SNew(SOverlay)
		+ SOverlay::Slot()
		[
			SNew(SBorder).BorderImage(Rounded(FLinearColor(0.08f, 0.08f, 0.09f), RingColor, 2.5f, 23.f)).Padding(8.f).HAlign(HAlign_Center).VAlign(VAlign_Center)
			.ColorAndOpacity(bLocked ? FLinearColor(1.f, 1.f, 1.f, 0.4f) : FLinearColor::White)
			[
				FWUi::Icon(Controller.Get(), IconOf(Tech), 30.f)
			]
		]
		+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Bottom)
		[
			SNew(SBorder).BorderImage(Rounded(FWStyle::Good, FWStyle::Good, 0.f, 8.f)).Padding(FMargin(3.f, 0.f))
			.Visibility(Status == TEXT("done") ? EVisibility::HitTestInvisible : EVisibility::Collapsed)
			[
				SNew(STextBlock).Text(FText::FromString(TEXT("✓"))).Font(FWStyle::Font(9, TEXT("Bold"))).ColorAndOpacity(FLinearColor(0.08f, 0.13f, 0.05f))
			]
		];
	const FLinearColor Fill = bLocked ? FLinearColor(0.1f, 0.1f, 0.115f) : FLinearColor(0.145f, 0.15f, 0.165f);
	return SNew(SButton)
		.ButtonStyle(&FCoreStyle::Get().GetWidgetStyle<FButtonStyle>("NoBorder"))
		.ContentPadding(0.f)
		.ToolTipText(FText::FromString(FWUi::Str(Tech, TEXT("name"))))
		.OnClicked_Lambda([WeakThis, Id]()
		{
			if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin())
			{
				This->Choose(Id);
			}
			return FReply::Handled();
		})
		[
			SNew(SBorder).BorderImage(Rounded(FLinearColor::Transparent, bSelected ? FWStyle::Accent : FLinearColor::Transparent, 2.f, 13.f)).Padding(3.f)
			[
				SNew(SBorder).BorderImage(Rounded(Fill, RingColor, 2.f, 10.f)).Padding(FMargin(8.f, 6.f))
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[SNew(SBox).WidthOverride(46.f).HeightOverride(46.f)[Ring]]
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)[Chips]
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(STextBlock).Text(FText::FromString(FWUi::Str(Tech, TEXT("name")))).Font(FWStyle::Font(11, TEXT("Bold")))
							.ColorAndOpacity(bLocked ? FWStyle::Muted : FWStyle::Text).OverflowPolicy(ETextOverflowPolicy::Ellipsis)
						]
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(StatusColor(Status)).OverflowPolicy(ETextOverflowPolicy::Ellipsis)
							.Text_Lambda([WeakThis, Tech]()
							{
								TSharedPtr<SFWResearchWindow> This = WeakThis.Pin();
								return This ? This->StatusText(Tech) : FText::GetEmpty();
							})
						]
						+ SVerticalBox::Slot().AutoHeight().Padding(0, 3, 0, 0)
						[
							SNew(SBox).Visibility(Status == TEXT("active") ? EVisibility::Visible : EVisibility::Collapsed)
							[
								FWUi::Bar(TAttribute<float>::CreateLambda([WeakThis, Tech]()
								{
									TSharedPtr<SFWResearchWindow> This = WeakThis.Pin();
									const double Start = FWUi::Num(Tech, TEXT("startsAt")), End = FWUi::Num(Tech, TEXT("finishesAt"));
									return This && End > Start ? float((This->ServerNow() - Start) / (End - Start)) : 0.f;
								}), FWStyle::Accent)
							]
						]
					]
				]
			]
		];
}

TSharedRef<SWidget> SFWResearchWindow::CostRow(const FString& Item, const FString& Name, const FText& Amount, const FLinearColor& Color)
{
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Item, 26.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(STextBlock).Text(Amount).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(Color)];
}

void SFWResearchWindow::RebuildDetails()
{
	if (!Details.IsValid())
	{
		return;
	}
	const TSharedPtr<FJsonObject> Tech = Technology(Selected);
	if (!Tech.IsValid())
	{
		Details->SetContent(FWUi::Note(LOCTEXT("Loading", "Wczytywanie drzewka…"), FWStyle::Muted));
		return;
	}
	const FString Status = FWUi::Str(Tech, TEXT("status"));
	const TSharedPtr<FJsonObject> Busy = Active();
	const bool bCompanyOnly = FWUi::Bool(Tech, TEXT("companyOnly"));
	TWeakPtr<SFWResearchWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox);
	auto Add = [&Box](TSharedRef<SWidget> Widget, float Top = 4.f) { Box->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(Controller.Get(), IconOf(Tech), 44.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Text(FText::FromString(FWUi::Str(Tech, TEXT("name")))).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).AutoWrapText(true)
		], 0.f);
	Add(FWUi::Note(FText::FromString(FWUi::Str(Tech, TEXT("description"))), FWStyle::Text), 8.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(StatusColor(Status)).AutoWrapText(true)
		.Text_Lambda([WeakThis, Tech]() { TSharedPtr<SFWResearchWindow> This = WeakThis.Pin(); return This ? This->StatusText(Tech) : FText::GetEmpty(); }), 6.f);
	if (Status == TEXT("active"))
	{
		Add(FWUi::Bar(TAttribute<float>::CreateLambda([WeakThis, Tech]()
		{
			TSharedPtr<SFWResearchWindow> This = WeakThis.Pin();
			const double Start = FWUi::Num(Tech, TEXT("startsAt")), End = FWUi::Num(Tech, TEXT("finishesAt"));
			return This && End > Start ? float((This->ServerNow() - Start) / (End - Start)) : 0.f;
		}), FWStyle::Accent), 6.f);
	}
	const TArray<FString> Requires = FWUi::Strings(Tech, TEXT("requires"));
	if (Requires.Num() > 0)
	{
		Add(SNew(STextBlock).Text(LOCTEXT("Requires", "Wymaga:")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted), 10.f);
		for (const FString& Need : Requires)
		{
			const TSharedPtr<FJsonObject> Other = Technology(Need);
			FString Label = FWUi::Str(Other, TEXT("name"));
			if (FWUi::Str(Other, TEXT("branch")) != FWUi::Str(Tech, TEXT("branch")))
			{
				for (const TSharedPtr<FJsonObject>& Each : FWUi::Objects(State, TEXT("branches")))
				{
					if (FWUi::Str(Each, TEXT("id")) == FWUi::Str(Other, TEXT("branch")))
					{
						Label += FString::Printf(TEXT("  (%s)"), *FWUi::Str(Each, TEXT("name")));
					}
				}
			}
			const bool bDone = FWUi::Str(Other, TEXT("status")) == TEXT("done");
			Add(SNew(SButton).ButtonStyle(&FCoreStyle::Get().GetWidgetStyle<FButtonStyle>("NoBorder")).ContentPadding(0.f).ToolTipText(LOCTEXT("Show", "Pokaż w drzewku"))
				.OnClicked_Lambda([WeakThis, Need]() { if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin()) { This->Choose(Need); } return FReply::Handled(); })
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), IconOf(Other), 24.f)]
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock).Text(FText::FromString(Label + (bDone ? TEXT("  ✓") : TEXT("")))).Font(FWStyle::Font(11))
						.ColorAndOpacity(StatusColor(FWUi::Str(Other, TEXT("status"))))
					]
				], 2.f);
		}
	}
	if (FWUi::Bool(Tech, TEXT("shared")))
	{
		Add(FWUi::Note(LOCTEXT("Shared", "Zbadała to twoja spółka: masz to, dopóki w niej jesteś."), FWStyle::Good), 8.f);
	}
	if (bCompanyOnly)
	{
		Add(FWUi::Note(LOCTEXT("CompanyOnlyNote", "Tylko dla spółek: bada ją spółka, razem, dla wszystkich członków."), FWStyle::Accent), 8.f);
	}
	const TArray<TSharedPtr<FJsonObject>> Unlocks = FWUi::Objects(Tech, TEXT("unlocks"));
	if (Unlocks.Num() > 0)
	{
		Add(SNew(STextBlock).Text(LOCTEXT("Unlocks", "Odblokowuje:")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted), 10.f);
		for (const TSharedPtr<FJsonObject>& Unlock : Unlocks)
		{
			Add(CostRow(FWUi::Str(Unlock, TEXT("id")), FWUi::Str(Unlock, TEXT("name")), FText::GetEmpty(), FWStyle::Text), 2.f);
		}
	}
	if (Status != TEXT("done"))
	{
		Add(SNew(STextBlock).Text(LOCTEXT("Uses", "Zużywa (wymagane/masz):")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted), 10.f);
		for (const TSharedPtr<FJsonObject>& Cost : FWUi::Objects(Tech, TEXT("cost")))
		{
			const int32 Need = int32(FWUi::Num(Cost, TEXT("quantity")));
			const int32 Have = Count(FWUi::Str(Cost, TEXT("id")));
			Add(CostRow(FWUi::Str(Cost, TEXT("id")), FWUi::Str(Cost, TEXT("name")), FText::FromString(FString::Printf(TEXT("%d/%d"), Need, Have)),
				Have >= Need || Status == TEXT("active") ? FWStyle::Good : FWStyle::Error), 2.f);
		}
		if (FWUi::Num(Tech, TEXT("costCents")) > 0.0)
		{
			Add(CostRow(FString(), LOCTEXT("Money", "Pieniądze").ToString(), FWUi::Money(FWUi::Num(Tech, TEXT("costCents"))), FWStyle::Text), 2.f);
		}
		// The cost goes in at once, so it must all be in the inventory together
		const int32 CostTiles = int32(FWUi::Num(Tech, TEXT("costTiles")));
		const int32 Slots = Controller.IsValid() ? Controller->GetInventorySlots() : 0;
		if (CostTiles > 0 && !bCompanyOnly)
		{
			const bool bFits = Slots < 1 || CostTiles <= Slots;
			Add(FWUi::Note(bFits ? FText::Format(LOCTEXT("Tiles", "Koszt zajmuje {0} pól ekwipunku."), CostTiles)
				: FText::Format(LOCTEXT("TilesTooMany", "Koszt zajmuje {0} pól ekwipunku, a masz {1}: załóż większy plecak, żeby zmieścić go naraz."), CostTiles, Slots),
				bFits ? FWStyle::Muted : FWStyle::Error), 6.f);
		}
	}
	Add(SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
		.Text(FText::Format(LOCTEXT("Time", "Czas: {0}  ·  {1} XP"), FWUi::Duration(FWUi::Num(Tech, TEXT("seconds"))), int32(FWUi::Num(Tech, TEXT("xp"))))), 8.f);

	if (FWUi::Obj(State, TEXT("company")).IsValid())
	{
		Add(MakeCompany(Tech), 12.f);
	}
	if ((Status == TEXT("available") || Status == TEXT("locked")) && !bCompanyOnly)
	{
		FText Label = LOCTEXT("Start", "Zbadaj");
		if (Status == TEXT("locked"))
		{
			Label = LOCTEXT("First", "Najpierw zbadaj wymagane");
		}
		else if (Busy.IsValid())
		{
			Label = FText::Format(LOCTEXT("Busy", "Trwa inne badanie: {0}"), FText::FromString(FWUi::Str(Busy, TEXT("name"))));
		}
		else if (!CanPay(Tech))
		{
			Label = LOCTEXT("Missing", "Brakuje materiałów");
		}
		Add(FWUi::SmallButton(Label, [WeakThis]() { if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin()) { This->Start(); } }, true,
			Status == TEXT("available") && !Busy.IsValid() && CanPay(Tech) && !bBusy), 14.f);
	}
	if (Status == TEXT("active"))
	{
		Add(FWUi::SmallButton(LOCTEXT("Cancel", "Przerwij badanie"), [WeakThis]() { if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin()) { This->Cancel(); } }, false, !bBusy), 14.f);
		Add(FWUi::Note(LOCTEXT("CancelNote", "Wszystko, co zużyło badanie, wróci do ekwipunku."), FWStyle::Muted), 4.f);
		// Research data and computing power speed it up: a button for each held
		bool bAny = false;
		for (const TSharedPtr<FJsonObject>& Boost : FWUi::Objects(State, TEXT("boosts")))
		{
			const FString Item = FWUi::Str(Boost, TEXT("id"));
			const int32 Have = Count(Item);
			if (Have < 1)
			{
				continue;
			}
			if (!bAny)
			{
				Add(SNew(STextBlock).Text(LOCTEXT("Boost", "Przyspiesz badanie:")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted), 10.f);
				bAny = true;
			}
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)
				[
					CostRow(Item, FString::Printf(TEXT("%s (masz %d, każda −%s)"), *FWUi::Str(Boost, TEXT("name")), Have, *FWUi::Duration(FWUi::Num(Boost, TEXT("seconds"))).ToString()),
						FText::GetEmpty(), FWStyle::Text)
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(6, 0, 0, 0)
				[
					FWUi::SmallButton(LOCTEXT("Use", "Użyj"), [WeakThis, Item, Have]()
					{
						if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetStringField(TEXT("item"), Item);
							P->SetNumberField(TEXT("quantity"), Have);
							This->Send(TEXT("research.boost"), P);
						}
					}, false, !bBusy)
				], 2.f);
		}
	}
	Add(FWUi::Note(LOCTEXT("OneAtATime", "Naraz bada się jedną technologię. Badanie trwa dalej, gdy odejdziesz od stołu."), FWStyle::Muted), 16.f);
	Add(FWUi::Note(LOCTEXT("Market", "Czego jeszcze nie zbadasz, tego nie ma dla ciebie na rynku: ani u handlarza, ani u innych graczy."), FWStyle::Muted), 4.f);
	Details->SetContent(Box);
}

TSharedRef<SWidget> SFWResearchWindow::MakeCompany(const TSharedPtr<FJsonObject>& Tech)
{
	// The company's research: members bring the cost together, then it is researched once for all of them
	const TSharedPtr<FJsonObject> Company = FWUi::Obj(State, TEXT("company"));
	const FString Name = FWUi::Str(Company, TEXT("name"));
	const FString Current = FWUi::Str(Company, TEXT("technology"));
	const bool bLeads = FWUi::Bool(Company, TEXT("leads"));
	const FString Id = FWUi::Str(Tech, TEXT("id"));
	const FString Status = FWUi::Str(Tech, TEXT("status"));
	TWeakPtr<SFWResearchWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin())
		{
			This->Send(Type, P.IsValid() ? P : MakeShared<FJsonObject>());
		}
	};
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox);
	auto Add = [&Box](TSharedRef<SWidget> Widget, float Top = 4.f) { Box->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	if (Current == Id)
	{
		double Starts = 0.0;
		if (!Company->TryGetNumberField(TEXT("startsAt"), Starts))
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("Collecting", "Spółka {0} zbiera na to badanie. Dołóż, co masz:"), FText::FromString(Name)), FWStyle::Accent), 0.f);
			for (const TSharedPtr<FJsonObject>& Part : FWUi::Objects(Company, TEXT("delivered")))
			{
				const FString Item = FWUi::Str(Part, TEXT("id"));
				const int32 Needed = int32(FWUi::Num(Part, TEXT("needed")));
				const int32 Delivered = int32(FWUi::Num(Part, TEXT("delivered")));
				if (Item == TEXT("money"))
				{
					Add(CostRow(FString(), LOCTEXT("Money", "Pieniądze").ToString(), FText::Format(LOCTEXT("MoneyOf", "{0}/{1}"), FWUi::Money(Delivered), FWUi::Money(Needed)),
						Delivered >= Needed ? FWStyle::Good : FWStyle::Text), 2.f);
					if (Delivered < Needed)
					{
						Add(SNew(SHorizontalBox)
							+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)
							[
								SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(LOCTEXT("Dollars", "kwota $"))
								.OnTextChanged_Lambda([WeakThis](const FText& Text) { if (TSharedPtr<SFWResearchWindow> This = WeakThis.Pin()) { This->MoneyText = Text.ToString(); } })
							]
							+ SHorizontalBox::Slot().AutoWidth()
							[
								FWUi::SmallButton(LOCTEXT("Give", "Dołóż z portfela"), [WeakThis, Do, Needed, Delivered]()
								{
									TSharedPtr<SFWResearchWindow> This = WeakThis.Pin();
									const double Cents = This ? FMath::RoundToDouble(FCString::Atod(*This->MoneyText.Replace(TEXT(","), TEXT("."))) * 100.0) : 0.0;
									if (Cents > 0.0)
									{
										TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
										P->SetStringField(TEXT("item"), TEXT("money"));
										P->SetNumberField(TEXT("quantity"), FMath::Min(Cents, double(Needed - Delivered)));
										Do(TEXT("research.contribute"), P);
									}
								}, false, !bBusy)
							], 2.f);
					}
					continue;
				}
				const int32 Have = Count(Item);
				Add(SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f)
					[
						CostRow(Item, FWUi::Str(Part, TEXT("name")), FText::FromString(FString::Printf(TEXT("%d/%d"), Delivered, Needed)), Delivered >= Needed ? FWStyle::Good : FWStyle::Text)
					]
					+ SHorizontalBox::Slot().AutoWidth().Padding(6, 0, 0, 0)
					[
						FWUi::SmallButton(LOCTEXT("Add", "Dołóż"), [Do, Item, Have, Needed, Delivered]()
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetStringField(TEXT("item"), Item);
							P->SetNumberField(TEXT("quantity"), FMath::Min(Have, Needed - Delivered));
							Do(TEXT("research.contribute"), P);
						}, false, Delivered < Needed && Have > 0 && !bBusy)
					], 2.f);
			}
		}
		else
		{
			const double Left = FWUi::Num(Company, TEXT("finishesAt")) - ServerNow();
			Add(FWUi::Note(FText::Format(LOCTEXT("CompanyResearching", "Spółka {0} bada to dla wszystkich członków: zostało {1}."), FText::FromString(Name),
				FWUi::Duration(FMath::CeilToDouble(Left / 1000.0))), FWStyle::Accent), 0.f);
		}
		if (bLeads)
		{
			Add(FWUi::SmallButton(LOCTEXT("CompanyCancel", "Odwołaj badanie spółki"), [Do]() { Do(TEXT("research.companyCancel"), nullptr); }, false, !bBusy), 8.f);
		}
	}
	else if (!Current.IsEmpty())
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("CompanyBusy", "Spółka bada teraz: {0}."), FText::FromString(FWUi::Str(Technology(Current), TEXT("name")))), FWStyle::Muted), 0.f);
	}
	else if (Status != TEXT("done") && bLeads)
	{
		Add(FWUi::SmallButton(LOCTEXT("CompanyStart", "Badaj w spółce"), [Do, Id]()
		{
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("technology"), Id);
			Do(TEXT("research.companyStart"), P);
		}, true, Status != TEXT("locked") && !bBusy), 0.f);
	}
	else if (Status != TEXT("done"))
	{
		Add(FWUi::Note(LOCTEXT("CompanyLeads", "Badania spółki wybierają prezes i wspólnicy."), FWStyle::Muted), 0.f);
	}
	return Box;
}

void SFWResearchWindow::Start()
{
	if (Selected.IsEmpty())
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("technology"), Selected);
	Send(TEXT("research.start"), P);
}

void SFWResearchWindow::Cancel()
{
	Send(TEXT("research.cancel"), MakeShared<FJsonObject>());
}

void SFWResearchWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (DetailsAgainAt > 0.0 && FPlatformTime::Seconds() >= DetailsAgainAt)
	{
		DetailsAgainAt = 0.0;
		RebuildDetails();
	}
	// Research done: the server hands it out (and the XP) on the next look
	const TSharedPtr<FJsonObject> Busy = Active();
	const double Now = ServerNow();
	if (Busy.IsValid() && Now >= FWUi::Num(Busy, TEXT("finishesAt")) && Now >= NextPoll && !bLoading)
	{
		Load();
	}
}

FReply SFWResearchWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
