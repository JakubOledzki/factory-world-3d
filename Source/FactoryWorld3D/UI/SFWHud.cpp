#include "UI/SFWHud.h"

#include "FWPlayerController.h"
#include "Player/FWCharacter.h"
#include "Engine/Texture2D.h"
#include "Styling/CoreStyle.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Notifications/SProgressBar.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWHud"

namespace
{
	constexpr double ToastSeconds = 4.0;
	const FLinearColor Accent = FWStyle::Accent;

	// The keys shown in the corner, on foot and in build mode
	struct FHint
	{
		const TCHAR* Key;
		FText Label;
	};

	TArray<FHint> Hints(bool bBuild)
	{
		if (bBuild)
		{
			return { { TEXT("LPM"), LOCTEXT("Place", "postaw") }, { TEXT("R"), LOCTEXT("Rotate", "obróć") }, { TEXT("1–5"), LOCTEXT("Choose", "wybór") },
				{ TEXT("PPM"), LOCTEXT("PickUp", "zabierz") }, { TEXT("B"), LOCTEXT("LeaveBuild", "wyjdź z budowania") } };
		}
		return { { TEXT("WASD"), LOCTEXT("Move", "ruch") }, { TEXT("Shift"), LOCTEXT("Run", "bieg") }, { TEXT("Spacja"), LOCTEXT("Jump", "skok") },
			{ TEXT("E"), LOCTEXT("Use", "ścinaj · kop · otwórz") }, { TEXT("PPM"), LOCTEXT("Take", "zabierz") }, { TEXT("B"), LOCTEXT("Build", "budowanie") },
			{ TEXT("Tab"), LOCTEXT("Backpack", "plecak") }, { TEXT("U"), LOCTEXT("Garage", "garaż") }, { TEXT("Esc"), LOCTEXT("Menu", "menu") } };
	}

	TSharedRef<SWidget> HintRow(bool bBuild)
	{
		TSharedRef<SWrapBox> Row = SNew(SWrapBox).PreferredSize(760.f).InnerSlotPadding(FVector2D(14.f, 6.f));
		for (const FHint& Hint : Hints(bBuild))
		{
			Row->AddSlot()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Key(FText::FromString(Hint.Key))]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Text(Hint.Label).Font(FWStyle::Font(12, TEXT("Medium"))).ColorAndOpacity(FLinearColor(0.85f, 0.87f, 0.9f))
					.ShadowOffset(FVector2D(1, 1)).ShadowColorAndOpacity(FLinearColor(0, 0, 0, 0.7f))
				]
			];
		}
		return Row;
	}

	// The ring round the crosshair while striking with the axe or pickaxe: one arc for each strike still to come; the
	// arc of the strike just landed flies out and fades
	class SStrikeRing : public SLeafWidget
	{
	public:
		SLATE_BEGIN_ARGS(SStrikeRing) {}
			SLATE_ARGUMENT(TFunction<bool(int32&, int32&, float&)>, Strikes)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Strikes = InArgs._Strikes;
		}

		virtual FVector2D ComputeDesiredSize(float) const override { return FVector2D(Size, Size); }

		virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& Culling, FSlateWindowElementList& Out, int32 Layer,
			const FWidgetStyle& Style, bool bParentEnabled) const override
		{
			int32 Left = 0, Total = 0;
			float SinceHit = 100.f;
			if (!Strikes || !Strikes(Left, Total, SinceHit) || Total <= 0)
			{
				return Layer;
			}
			const FVector2f Center = FVector2f(Geometry.GetLocalSize()) * 0.5f;
			const float Gap = 9.f;  // degrees between arcs
			const float Arc = 360.f / Total;
			auto Draw = [&](int32 Index, float At, const FLinearColor& Color, float Thickness, int32 InLayer)
			{
				// Arcs clockwise from the top
				TArray<FVector2f> Points;
				const float From = -90.f + Index * Arc + Gap * 0.5f, To = -90.f + (Index + 1) * Arc - Gap * 0.5f;
				for (int32 Step = 0; Step <= 16; ++Step)
				{
					const float Angle = FMath::DegreesToRadians(FMath::Lerp(From, To, Step / 16.f));
					Points.Add(Center + FVector2f(FMath::Cos(Angle), FMath::Sin(Angle)) * At);
				}
				FSlateDrawElement::MakeLines(Out, InLayer, Geometry.ToPaintGeometry(), Points, ESlateDrawEffect::None, Color, true, Thickness);
			};
			for (int32 Index = 0; Index < Total; ++Index)
			{
				// Dark under every place, so the ring reads on snow as on rock
				Draw(Index, RingRadius, FLinearColor(0.f, 0.f, 0.f, 0.45f), 7.f, Layer);
				if (Index < Left)
				{
					Draw(Index, RingRadius, Index == Left - 1 && SinceHit < 0.15f ? FLinearColor::White : Accent, 4.f, Layer + 1);
				}
			}
			// The arc just struck away
			if (Left < Total && SinceHit < 0.35f)
			{
				const float T = SinceHit / 0.35f;
				Draw(Left, RingRadius + 10.f * T, FLinearColor::White.CopyWithNewOpacity(1.f - T), 4.f * (1.f - T) + 1.f, Layer + 1);
			}
			return Layer + 1;
		}

	private:
		static constexpr float Size = 90.f;
		static constexpr float RingRadius = 30.f;
		TFunction<bool(int32&, int32&, float&)> Strikes;
	};

	FText Money(int64 Cents)
	{
		FNumberFormattingOptions Options;
		Options.MinimumFractionalDigits = 2;
		Options.MaximumFractionalDigits = 2;
		return FText::Format(LOCTEXT("Money", "{0} $"), FText::AsNumber(Cents / 100.0, &Options));
	}
}

void SFWHud::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;

	TSharedRef<SHorizontalBox> HotbarBox = SNew(SHorizontalBox);
	for (int32 Slot = 0; Slot < AFWPlayerController::HotbarSize; ++Slot)
	{
		HotbarBox->AddSlot().AutoWidth().VAlign(VAlign_Bottom).Padding(5, 0)[MakeSlot(Slot)];
	}

	ChildSlot
	[
		SNew(SOverlay)
		.Visibility(EVisibility::HitTestInvisible)
		// Crosshair: a dot, amber with a faint halo when it points at something
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SBox).WidthOverride(16.f).HeightOverride(16.f)
			[
				SNew(SImage).Image(FWStyle::Swatch())
				.ColorAndOpacity_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return FSlateColor(Character && Character->HasTarget() ? Accent.CopyWithNewOpacity(0.22f) : FLinearColor::Transparent);
				})
			]
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SBox).WidthOverride(6.f).HeightOverride(6.f)
			[
				SNew(SImage).Image(FWStyle::Swatch())
				.ColorAndOpacity_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return FSlateColor(Character && Character->HasTarget() ? Accent : FLinearColor(1.f, 1.f, 1.f, 0.85f));
				})
			]
		]
		// The strikes still to come round the crosshair
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SStrikeRing).Strikes([this](int32& Left, int32& Total, float& SinceHit)
			{
				const AFWCharacter* Character = GetCharacter();
				return Character && Character->GetStrikes(Left, Total, SinceHit);
			})
		]
		// What the crosshair points at, on a small glass label, and the progress of holding the right button
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center).Padding(0, 110, 0, 0)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
			[
				SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(14.f, 6.f))
				.Visibility_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character && !Character->GetTargetText().IsEmpty() ? EVisibility::HitTestInvisible : EVisibility::Collapsed;
				})
				[
					SNew(STextBlock)
					.Font(FWStyle::Font(14, TEXT("Medium")))
					.ColorAndOpacity(FWStyle::Text)
					.Text_Lambda([this]()
					{
						const AFWCharacter* Character = GetCharacter();
						return Character ? Character->GetTargetText() : FText::GetEmpty();
					})
				]
			]
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 8, 0, 0)
			[
				SNew(SBox).WidthOverride(200.f)
				.Visibility_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character && Character->GetProgress() >= 0.f ? EVisibility::HitTestInvisible : EVisibility::Collapsed;
				})
				[
					FWUi::Bar(TAttribute<float>::CreateLambda([this]()
					{
						const AFWCharacter* Character = GetCharacter();
						return Character ? Character->GetProgress() : 0.f;
					}), Accent)
				]
			]
		]
		// Money: the balance in large condensed figures under a small label
		+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Top).Padding(28)
		[
			SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(18, 8, 18, 10))
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)
				[
					SNew(STextBlock).Text(LOCTEXT("Balance", "SALDO")).Font(FWStyle::Display(12, TEXT("SemiBold"))).ColorAndOpacity(FWStyle::Muted)
				]
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)
				[
					SNew(STextBlock)
					.Font(FWStyle::Display(30))
					.ColorAndOpacity(FWStyle::Good)
					.Text_Lambda([this]() { return Controller.IsValid() ? Money(Controller->GetBalanceCents()) : FText::GetEmpty(); })
				]
			]
		]
		// Toasts
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Top).Padding(0, 28, 0, 0)
		[
			SAssignNew(ToastBox, SVerticalBox)
		]
		// Key hints: key caps with what they do, on a faint dark glass in the corner
		+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Top).Padding(28)
		[
			SNew(SBorder).BorderImage(FWStyle::Panel()).BorderBackgroundColor(FLinearColor(1.f, 1.f, 1.f, 0.6f)).Padding(FMargin(12.f, 8.f))
			[
			SNew(SOverlay)
			+ SOverlay::Slot()
			[
				SNew(SBox).Visibility_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character && Character->IsBuildMode() ? EVisibility::Collapsed : EVisibility::HitTestInvisible;
				})
				[
					HintRow(false)
				]
			]
			+ SOverlay::Slot()
			[
				SNew(SVerticalBox)
				.Visibility_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character && Character->IsBuildMode() ? EVisibility::HitTestInvisible : EVisibility::Collapsed;
				})
				+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)
				[
					SNew(STextBlock).Text(LOCTEXT("Building", "BUDOWANIE")).Font(FWStyle::Display(20)).ColorAndOpacity(Accent)
					.ShadowOffset(FVector2D(1, 1)).ShadowColorAndOpacity(FLinearColor(0, 0, 0, 0.7f))
				]
				+ SVerticalBox::Slot().AutoHeight()[HintRow(true)]
			]
			]
		]
		// Hotbar, with the name of what is in hand above it
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0, 0, 0, 26)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 0, 0, 10)
			[
				SNew(STextBlock)
				.Font(FWStyle::Display(20, TEXT("SemiBold")))
				.ColorAndOpacity(FWStyle::Text)
				.ShadowOffset(FVector2D(1, 1))
				.ShadowColorAndOpacity(FLinearColor(0, 0, 0, 0.8f))
				.Text_Lambda([this]()
				{
					const FString Id = Controller.IsValid() ? Controller->GetSelectedItem() : FString();
					return Id.IsEmpty() ? FText::GetEmpty() : FText::FromString(Controller->ItemName(Id)).ToUpper();
				})
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				HotbarBox
			]
		]
	];
}

TSharedRef<SWidget> SFWHud::MakeSlot(int32 Slot)
{
	auto Item = [this, Slot]() -> FString
	{
		return Controller.IsValid() && Controller->GetHotbar().IsValidIndex(Slot) ? Controller->GetHotbar()[Slot] : FString();
	};
	auto Selected = [this, Slot]() { return Controller.IsValid() && Controller->GetSelectedSlot() == Slot; };
	// The chosen slot is larger and lifted a little, with an amber frame
	return SNew(SBox)
		.WidthOverride_Lambda([Selected]() { return Selected() ? 90.f : 76.f; })
		.HeightOverride_Lambda([Selected]() { return Selected() ? 90.f : 76.f; })
		.RenderTransform_Lambda([Selected]() { return TOptional<FSlateRenderTransform>(FSlateRenderTransform(FVector2f(0.f, Selected() ? -6.f : 0.f))); })
		[
			SNew(SBorder)
			.BorderImage(FWStyle::Panel())
			.Padding(0.f)
			[
				SNew(SOverlay)
				+ SOverlay::Slot()
				[
					SNew(SImage)
					.Image_Lambda([Selected]() { return FWStyle::SlotFrame(Selected()); })
				]
				// The item's own model
				+ SOverlay::Slot().Padding(8.f)
				[
					SNew(SImage)
					.Image_Lambda([this, Item]() { return IconBrush(Item()); })
					.Visibility_Lambda([this, Item]() { return IconBrush(Item()) ? EVisibility::HitTestInvisible : EVisibility::Hidden; })
				]
				+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Top).Padding(7.f, 5.f)
				[
					SNew(STextBlock)
					.Font(FWStyle::Display(14, TEXT("SemiBold")))
					.ColorAndOpacity_Lambda([Selected]() { return Selected() ? FWStyle::Accent : FWStyle::Muted; })
					.ShadowOffset(FVector2D(1, 1))
					.Text(FText::AsNumber(Slot + 1))
				]
				+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Bottom).Padding(8.f, 3.f)
				[
					SNew(STextBlock)
					.Font(FWStyle::Display(18))
					.ColorAndOpacity(FWStyle::Text)
					.ShadowOffset(FVector2D(1, 1))
					.ShadowColorAndOpacity(FLinearColor(0, 0, 0, 0.8f))
					.Text_Lambda([this, Item]()
					{
						const FString Id = Item();
						const FFWStack* Stack = Controller.IsValid() && !Id.IsEmpty() ? Controller->FindStack(Id) : nullptr;
						return Id.IsEmpty() ? FText::GetEmpty() : FText::AsNumber(Stack ? Stack->Quantity : 0);
					})
				]
			]
		];
}

const FSlateBrush* SFWHud::IconBrush(const FString& Item) const
{
	return Controller.IsValid() ? Controller->GetIconBrush(Item) : nullptr;
}

AFWCharacter* SFWHud::GetCharacter() const
{
	return Controller.IsValid() ? Cast<AFWCharacter>(Controller->GetPawn()) : nullptr;
}

void SFWHud::Tick(const FGeometry& AllottedGeometry, const double InCurrentTime, const float InDeltaTime)
{
	SCompoundWidget::Tick(AllottedGeometry, InCurrentTime, InDeltaTime);
	if (!Controller.IsValid())
	{
		return;
	}
	// Rebuild the toast list when one arrives or expires
	const double Now = FPlatformTime::Seconds();
	int32 Visible = 0;
	double Newest = 0.0;
	for (const FFWToast& Toast : Controller->GetToasts())
	{
		if (Now - Toast.ShownAt < ToastSeconds)
		{
			++Visible;
			Newest = FMath::Max(Newest, Toast.ShownAt);
		}
	}
	if (Visible == ShownToasts && Newest == LastToastAt)
	{
		return;
	}
	ShownToasts = Visible;
	LastToastAt = Newest;
	ToastBox->ClearChildren();
	for (const FFWToast& Toast : Controller->GetToasts())
	{
		if (Now - Toast.ShownAt >= ToastSeconds)
		{
			continue;
		}
		// A glass card with a coloured stripe on the left (amber news, red errors), fading out in its last half second;
		// the newest one slides in
		const double ShownAt = Toast.ShownAt;
		TSharedRef<SWidget> Card = SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(0)
			.ColorAndOpacity_Lambda([ShownAt]() { return FLinearColor(1.f, 1.f, 1.f, FMath::Clamp(float((ToastSeconds - (FPlatformTime::Seconds() - ShownAt)) / 0.5), 0.f, 1.f)); })
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(10, 0, 0, 0)
				[
					SNew(SBox).WidthOverride(4.f).HeightOverride(22.f)
					[
						SNew(SImage).Image(FCoreStyle::Get().GetBrush(TEXT("WhiteBrush"))).ColorAndOpacity(Toast.bError ? FWStyle::Error : Accent)
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(FMargin(14, 9, 18, 9))
				[
					SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Medium"))).Text(Toast.Text)
					.ColorAndOpacity(Toast.bError ? FLinearColor(1.f, 0.8f, 0.77f) : FWStyle::Text)
				]
			];
		ToastBox->AddSlot().AutoHeight().HAlign(HAlign_Center).Padding(0, 0, 0, 6)
		[
			ShownAt == Newest ? FWUi::Appear(Card, 0.2f) : Card
		];
	}
}

#undef LOCTEXT_NAMESPACE
