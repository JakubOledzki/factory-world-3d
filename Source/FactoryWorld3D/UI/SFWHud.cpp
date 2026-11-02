#include "UI/SFWHud.h"

#include "FWPlayerController.h"
#include "Player/FWCharacter.h"
#include "Engine/Texture2D.h"
#include "Styling/CoreStyle.h"
#include "UI/FWStyle.h"
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
	const FLinearColor Panel(0.02f, 0.025f, 0.03f, 0.72f);
	const FLinearColor Accent(1.f, 0.62f, 0.15f);

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
	const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");

	TSharedRef<SHorizontalBox> HotbarBox = SNew(SHorizontalBox);
	for (int32 Slot = 0; Slot < AFWPlayerController::HotbarSize; ++Slot)
	{
		HotbarBox->AddSlot().AutoWidth().VAlign(VAlign_Bottom).Padding(5, 0)[MakeSlot(Slot)];
	}

	ChildSlot
	[
		SNew(SOverlay)
		.Visibility(EVisibility::HitTestInvisible)
		// Crosshair
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SBox).WidthOverride(6.f).HeightOverride(6.f)
			[
				SNew(SBorder).BorderImage(White).BorderBackgroundColor_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character && Character->HasTarget() ? Accent : FLinearColor(1.f, 1.f, 1.f, 0.8f);
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
		// What the crosshair points at, and the progress of holding E or the right button
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center).Padding(0, 90, 0, 0)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
			[
				SNew(STextBlock)
				.Font(FCoreStyle::GetDefaultFontStyle("Regular", 13))
				.ShadowOffset(FVector2D(1, 1))
				.Text_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character ? Character->GetTargetText() : FText::GetEmpty();
				})
			]
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 6, 0, 0)
			[
				SNew(SBox).WidthOverride(220.f).HeightOverride(8.f)
				.Visibility_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					return Character && Character->GetProgress() >= 0.f ? EVisibility::HitTestInvisible : EVisibility::Collapsed;
				})
				[
					SNew(SProgressBar)
					.FillColorAndOpacity(Accent)
					.Percent_Lambda([this]()
					{
						const AFWCharacter* Character = GetCharacter();
						return Character ? TOptional<float>(Character->GetProgress()) : TOptional<float>();
					})
				]
			]
		]
		// Money
		+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Top).Padding(24)
		[
			SNew(SBorder).BorderImage(White).BorderBackgroundColor(Panel).Padding(FMargin(14, 8))
			[
				SNew(STextBlock)
				.Font(FCoreStyle::GetDefaultFontStyle("Bold", 16))
				.ColorAndOpacity(FLinearColor(0.55f, 0.9f, 0.45f))
				.Text_Lambda([this]() { return Controller.IsValid() ? Money(Controller->GetBalanceCents()) : FText::GetEmpty(); })
			]
		]
		// Toasts
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Top).Padding(0, 28, 0, 0)
		[
			SAssignNew(ToastBox, SVerticalBox)
		]
		// Key hints
		+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Top).Padding(24)
		[
			SNew(SBorder).BorderImage(White).BorderBackgroundColor(Panel).Padding(FMargin(12, 8))
			[
				SNew(STextBlock)
				.Font(FCoreStyle::GetDefaultFontStyle("Regular", 11))
				.ColorAndOpacity(FLinearColor(0.8f, 0.82f, 0.85f))
				.Text_Lambda([this]()
				{
					const AFWCharacter* Character = GetCharacter();
					if (Character && Character->IsBuildMode())
					{
						return LOCTEXT("BuildHints", "BUDOWANIE   LPM postaw   R obróć   1–5 wybór   PPM (przytrzymaj) zabierz   B wyjdź");
					}
					return LOCTEXT("Hints", "WASD ruch   Shift bieg   Spacja skok   E ścinaj / kop (przytrzymaj) · otwórz skrzynię   PPM (przytrzymaj) zabierz   B budowanie   1–5 pasek   Tab plecak   Esc menu");
				})
			]
		]
		// Hotbar
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0, 0, 0, 24)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 0, 0, 8)
			[
				SNew(STextBlock)
				.Font(FWStyle::Font(14, TEXT("Bold")))
				.ColorAndOpacity(FWStyle::Text)
				.ShadowOffset(FVector2D(1, 1))
				.ShadowColorAndOpacity(FLinearColor(0, 0, 0, 0.8f))
				.Text_Lambda([this]()
				{
					const FString Id = Controller.IsValid() ? Controller->GetSelectedItem() : FString();
					return Id.IsEmpty() ? FText::GetEmpty() : FText::FromString(Controller->ItemName(Id));
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
	return SNew(SBox)
		.WidthOverride_Lambda([Selected]() { return Selected() ? 92.f : 80.f; })
		.HeightOverride_Lambda([Selected]() { return Selected() ? 92.f : 80.f; })
		[
			SNew(SBorder)
			.BorderImage(FWStyle::Panel())
			.BorderBackgroundColor_Lambda([Selected]() { return Selected() ? FLinearColor(1.f, 0.8f, 0.55f, 1.f) : FLinearColor::White; })
			.Padding(0.f)
			[
				SNew(SOverlay)
				// The selected slot: an amber frame
				+ SOverlay::Slot()
				[
					SNew(SImage)
					.Image_Lambda([Selected]() { return FWStyle::SlotFrame(Selected()); })
				]
				// The item's own model
				+ SOverlay::Slot().Padding(6.f)
				[
					SNew(SImage)
					.Image_Lambda([this, Item]() { return IconBrush(Item()); })
					.Visibility_Lambda([this, Item]() { return IconBrush(Item()) ? EVisibility::HitTestInvisible : EVisibility::Hidden; })
				]
				+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Top).Padding(7.f, 4.f)
				[
					SNew(STextBlock)
					.Font(FWStyle::Font(10, TEXT("Bold")))
					.ColorAndOpacity_Lambda([Selected]() { return Selected() ? FWStyle::Accent : FWStyle::Muted; })
					.ShadowOffset(FVector2D(1, 1))
					.Text(FText::AsNumber(Slot + 1))
				]
				+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Bottom).Padding(7.f, 3.f)
				[
					SNew(STextBlock)
					.Font(FWStyle::Font(13, TEXT("Bold")))
					.ColorAndOpacity(FWStyle::Text)
					.ShadowOffset(FVector2D(1, 1))
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
	const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");
	for (const FFWToast& Toast : Controller->GetToasts())
	{
		if (Now - Toast.ShownAt >= ToastSeconds)
		{
			continue;
		}
		ToastBox->AddSlot().AutoHeight().HAlign(HAlign_Center).Padding(0, 0, 0, 6)
		[
			SNew(SBorder).BorderImage(White)
			.BorderBackgroundColor(Toast.bError ? FLinearColor(0.35f, 0.04f, 0.03f, 0.85f) : Panel)
			.Padding(FMargin(16, 8))
			[
				SNew(STextBlock).Font(FCoreStyle::GetDefaultFontStyle("Regular", 13)).Text(Toast.Text)
			]
		];
	}
}

#undef LOCTEXT_NAMESPACE
