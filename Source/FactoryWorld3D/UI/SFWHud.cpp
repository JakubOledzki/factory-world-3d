#include "UI/SFWHud.h"

#include "FWPlayerController.h"
#include "Player/FWCharacter.h"
#include "Styling/CoreStyle.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Notifications/SProgressBar.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWHud"

namespace
{
	constexpr double ToastSeconds = 4.0;
	const FLinearColor Panel(0.02f, 0.025f, 0.03f, 0.72f);
	const FLinearColor Accent(1.f, 0.62f, 0.15f);

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
		HotbarBox->AddSlot().AutoWidth().Padding(4, 0)[MakeSlot(Slot)];
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
					return LOCTEXT("Hints", "WASD ruch   Shift bieg   Spacja skok   E (przytrzymaj) kop / wkładaj do skrzyni   PPM (przytrzymaj) zabierz   B budowanie   1–5 pasek");
				})
			]
		]
		// Hotbar
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0, 0, 0, 24)
		[
			HotbarBox
		]
	];
}

TSharedRef<SWidget> SFWHud::MakeSlot(int32 Slot)
{
	const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");
	auto Item = [this, Slot]() -> FString
	{
		return Controller.IsValid() && Controller->GetHotbar().IsValidIndex(Slot) ? Controller->GetHotbar()[Slot] : FString();
	};
	return SNew(SBorder)
		.BorderImage(White)
		.Padding(2.f)
		.BorderBackgroundColor_Lambda([this, Slot]()
		{
			return Controller.IsValid() && Controller->GetSelectedSlot() == Slot ? Accent : FLinearColor(0.f, 0.f, 0.f, 0.5f);
		})
		[
			SNew(SBorder)
			.BorderImage(White)
			.BorderBackgroundColor(Panel)
			[
				SNew(SBox).WidthOverride(150.f).HeightOverride(70.f).Padding(FMargin(8, 4))
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock)
						.Font(FCoreStyle::GetDefaultFontStyle("Bold", 10))
						.ColorAndOpacity(FLinearColor(0.6f, 0.62f, 0.65f))
						.Text(FText::AsNumber(Slot + 1))
					]
					+ SVerticalBox::Slot().FillHeight(1.f)
					[
						SNew(STextBlock)
						.Font(FCoreStyle::GetDefaultFontStyle("Regular", 10))
						.AutoWrapText(true)
						.Text_Lambda([this, Item]()
						{
							const FString Id = Item();
							return Id.IsEmpty() || !Controller.IsValid() ? FText::GetEmpty() : FText::FromString(Controller->ItemName(Id));
						})
					]
					+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)
					[
						SNew(STextBlock)
						.Font(FCoreStyle::GetDefaultFontStyle("Bold", 11))
						.Text_Lambda([this, Item]()
						{
							const FString Id = Item();
							const FFWStack* Stack = Controller.IsValid() && !Id.IsEmpty() ? Controller->FindStack(Id) : nullptr;
							return Id.IsEmpty() ? FText::GetEmpty() : FText::AsNumber(Stack ? Stack->Quantity : 0);
						})
					]
				]
			]
		];
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
