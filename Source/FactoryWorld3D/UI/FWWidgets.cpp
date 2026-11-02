#include "UI/FWWidgets.h"

#include "Brushes/SlateRoundedBoxBrush.h"
#include "FWPlayerController.h"
#include "UI/FWStyle.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Notifications/SProgressBar.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SCompoundWidget.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWUi"

namespace
{
	// Fades its content in and lets it grow from a little smaller, easing out, over its first moment on screen
	class SFWAppear : public SCompoundWidget
	{
	public:
		SLATE_BEGIN_ARGS(SFWAppear) {}
			SLATE_DEFAULT_SLOT(FArguments, Content)
			SLATE_ARGUMENT(float, Seconds)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Seconds = FMath::Max(InArgs._Seconds, 0.01f);
			Start = FPlatformTime::Seconds();
			SetRenderOpacity(0.f);
			SetRenderTransformPivot(FVector2D(0.5f, 0.5f));
			ChildSlot[InArgs._Content.Widget];
		}

		virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override
		{
			SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
			if (bDone)
			{
				return;
			}
			const float T = FMath::Clamp(float((FPlatformTime::Seconds() - Start) / Seconds), 0.f, 1.f);
			const float Eased = 1.f - FMath::Pow(1.f - T, 3.f);
			SetRenderOpacity(Eased);
			SetRenderTransform(FSlateRenderTransform(FScale2f(FMath::Lerp(0.965f, 1.f, Eased)), FVector2f(0.f, 10.f * (1.f - Eased))));
			bDone = T >= 1.f;
		}

	private:
		float Seconds = 0.18f;
		double Start = 0.0;
		bool bDone = false;
	};
}

namespace FWUi
{
	TSharedRef<SWidget> Appear(TSharedRef<SWidget> Content, float Seconds)
	{
		return SNew(SFWAppear).Seconds(Seconds)[Content];
	}

	TSharedRef<SWidget> Key(const FText& Text)
	{
		return SNew(SBorder).BorderImage(FWStyle::KeyCap()).Padding(FMargin(7.f, 1.f))
			[
				SNew(STextBlock).Text(Text).Font(FWStyle::Font(11, TEXT("SemiBold"))).ColorAndOpacity(FWStyle::Text)
			];
	}

	FText Money(double Cents)
	{
		FNumberFormattingOptions Options;
		Options.MinimumFractionalDigits = 2;
		Options.MaximumFractionalDigits = 2;
		return FText::Format(LOCTEXT("Money", "{0} $"), FText::AsNumber(Cents / 100.0, &Options));
	}

	FText Left(double Ms)
	{
		const int64 Minutes = int64(FMath::Max(Ms, 0.0)) / 60000;
		if (Minutes >= 24 * 60)
		{
			return FText::Format(LOCTEXT("Days", "{0} d {1} h"), Minutes / 1440, Minutes % 1440 / 60);
		}
		if (Minutes >= 60)
		{
			return FText::Format(LOCTEXT("Hours", "{0} h {1} min"), Minutes / 60, Minutes % 60);
		}
		if (Minutes >= 1)
		{
			return FText::Format(LOCTEXT("Minutes", "{0} min"), Minutes);
		}
		return FText::Format(LOCTEXT("Seconds", "{0} s"), int64(FMath::Max(Ms, 0.0)) / 1000);
	}

	FText Duration(double Seconds)
	{
		const int64 S = FMath::RoundToInt64(FMath::Max(Seconds, 0.0));
		if (S < 60)
		{
			return FText::Format(LOCTEXT("Short", "{0} s"), S);
		}
		return FText::FromString(FString::Printf(TEXT("%lld min %02lld s"), S / 60, S % 60));
	}

	double LocalNowMs()
	{
		return double((FDateTime::UtcNow() - FDateTime(1970, 1, 1)).GetTotalMilliseconds());
	}

	FString Str(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		FString Value;
		if (Object.IsValid())
		{
			Object->TryGetStringField(Field, Value);
		}
		return Value;
	}

	double Num(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field, double Default)
	{
		double Value = Default;
		if (Object.IsValid())
		{
			Object->TryGetNumberField(Field, Value);
		}
		return Value;
	}

	bool Bool(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		bool bValue = false;
		if (Object.IsValid())
		{
			Object->TryGetBoolField(Field, bValue);
		}
		return bValue;
	}

	TSharedPtr<FJsonObject> Obj(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		const TSharedPtr<FJsonObject>* Value = nullptr;
		return Object.IsValid() && Object->TryGetObjectField(Field, Value) && Value ? *Value : nullptr;
	}

	TArray<TSharedPtr<FJsonObject>> Objects(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		TArray<TSharedPtr<FJsonObject>> Result;
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Object.IsValid() && Object->TryGetArrayField(Field, Values))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				if (Value.IsValid() && Value->Type == EJson::Object)
				{
					Result.Add(Value->AsObject());
				}
			}
		}
		return Result;
	}

	TArray<FString> Strings(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		TArray<FString> Result;
		if (Object.IsValid())
		{
			Object->TryGetStringArrayField(Field, Result);
		}
		return Result;
	}

	TSharedRef<SWidget> Frame(const FText& Title, TSharedRef<SWidget> Right, TSharedPtr<SWidget> Tabs, TSharedRef<SWidget> Content, float Width, float Height)
	{
		return SNew(SOverlay)
			+ SOverlay::Slot()[SNew(SBackgroundBlur).BlurStrength(6.f)]
			+ SOverlay::Slot()[SNew(SBorder).BorderImage(FWStyle::SideShade())]
			+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
			[
				Appear(
				SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(28.f, 22.f, 28.f, 24.f))
				[
					SNew(SBox).WidthOverride(Width).HeightOverride(Height)
					[
						SNew(SVerticalBox)
						// Title: tall condensed capitals after an amber bar, and what goes on the right (e.g. the balance)
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(SHorizontalBox)
							+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)
							[
								SNew(SBox).WidthOverride(4.f).HeightOverride(26.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FWStyle::Accent)]
							]
							+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
							[
								SNew(STextBlock).Text(Title.ToUpper()).Font(FWStyle::Display(30)).ColorAndOpacity(FWStyle::Text)
							]
							+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right]
						]
						// A hairline under the title
						+ SVerticalBox::Slot().AutoHeight().Padding(0, 12, 0, 14)
						[
							SNew(SBox).HeightOverride(1.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FLinearColor(1.f, 1.f, 1.f, 0.08f))]
						]
						+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, Tabs.IsValid() ? 14.f : 0.f)
						[
							Tabs.IsValid() ? Tabs.ToSharedRef() : SNullWidget::NullWidget
						]
						+ SVerticalBox::Slot().FillHeight(1.f)[Content]
					]
				])
			]
			+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0.f, 0.f, 0.f, 30.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[Key(LOCTEXT("EscKey", "Esc"))]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("Close", "zamknij"))
				]
			];
	}

	TSharedRef<SWidget> TabButton(const FText& Text, TFunction<bool()> IsActive, TFunction<void()> OnClick)
	{
		// The chosen one sits on an amber-lit pill with amber text; the others light up under the mouse
		return SNew(SBorder)
			.Padding(0)
			.BorderImage_Lambda([IsActive]() { return IsActive() ? &FWStyle::TabButton(true).Normal : FStyleDefaults::GetNoBrush(); })
			[
				SNew(SButton)
				.ButtonStyle(&FWStyle::TabButton(false))
				.ContentPadding(FMargin(6.f, 2.f))
				.OnClicked_Lambda([OnClick]() { OnClick(); return FReply::Handled(); })
				[
					SNew(STextBlock).Text(Text).Font(FWStyle::Font(13, TEXT("SemiBold")))
					.ColorAndOpacity_Lambda([IsActive]() { return FSlateColor(IsActive() ? FWStyle::Accent : FWStyle::Text); })
				]
			];
	}

	TSharedRef<SWidget> SmallButton(const FText& Text, TFunction<void()> OnClick, bool bPrimary, bool bEnabled)
	{
		return SNew(SButton)
			.ButtonStyle(bPrimary ? &FWStyle::PrimaryButton() : &FWStyle::SecondaryButton())
			.ContentPadding(FMargin(10.f, 4.f))
			.HAlign(HAlign_Center)
			.IsEnabled(bEnabled)
			.OnClicked_Lambda([OnClick]() { OnClick(); return FReply::Handled(); })
			[
				SNew(STextBlock).Text(Text).Font(FWStyle::Font(12, bPrimary ? TEXT("Bold") : TEXT("Medium"))).ColorAndOpacity(bPrimary ? FLinearColor::White : FWStyle::Text)
			];
	}

	TSharedRef<SWidget> Heading(const FText& Text)
	{
		// A short amber bar before an uppercase heading
		return SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
			[
				SNew(SBox).WidthOverride(3.f).HeightOverride(14.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FWStyle::Accent)]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Text(Text.ToUpper()).Font(FWStyle::Display(16, TEXT("SemiBold"))).ColorAndOpacity(FWStyle::Accent)
			];
	}

	TSharedRef<SWidget> Note(const FText& Text, const FLinearColor& Color)
	{
		return SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(Color).AutoWrapText(true);
	}

	TSharedRef<SWidget> Icon(AFWPlayerController* Controller, const FString& Item, float Size)
	{
		const FSlateBrush* Brush = Controller && !Item.IsEmpty() ? Controller->GetIconBrush(Item) : nullptr;
		return SNew(SBox).WidthOverride(Size).HeightOverride(Size)
			[
				SNew(SImage).Image(Brush).Visibility(Brush ? EVisibility::HitTestInvisible : EVisibility::Hidden)
			];
	}

	TSharedRef<SWidget> Bar(TAttribute<float> Fraction, const FLinearColor& Color, float Width)
	{
		static const FProgressBarStyle Style = []()
		{
			FProgressBarStyle S;
			S.SetBackgroundImage(FSlateRoundedBoxBrush(FLinearColor(0.f, 0.f, 0.f, 0.55f), 3.f));
			S.SetFillImage(FSlateRoundedBoxBrush(FLinearColor::White, 3.f));
			S.SetMarqueeImage(FSlateRoundedBoxBrush(FLinearColor::White, 3.f));
			return S;
		}();
		return SNew(SBox).HeightOverride(7.f).WidthOverride(Width > 0.f ? FOptionalSize(Width) : FOptionalSize())
			[
				SNew(SProgressBar).Style(&Style).Percent_Lambda([Fraction]() { return TOptional<float>(FMath::Clamp(Fraction.Get(), 0.f, 1.f)); })
				.FillColorAndOpacity(Color)
			];
	}
}

#undef LOCTEXT_NAMESPACE
