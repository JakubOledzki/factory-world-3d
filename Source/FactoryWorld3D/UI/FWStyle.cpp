#include "UI/FWStyle.h"

#include "Brushes/SlateColorBrush.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "Sound/SoundBase.h"
#include "Styling/CoreStyle.h"

namespace FWStyle
{
	FSlateFontInfo Font(int32 Size, FName Typeface)
	{
		return FCoreStyle::GetDefaultFontStyle(Typeface, Size);
	}

	const FSlateBrush* Panel()
	{
		static const FSlateRoundedBoxBrush Brush(FLinearColor(0.012f, 0.016f, 0.02f, 0.84f), 10.f, FLinearColor(1.f, 1.f, 1.f, 0.08f), 1.f);
		return &Brush;
	}

	const FSlateBrush* SideShade()
	{
		static const FSlateColorBrush Brush(FLinearColor(0.f, 0.f, 0.f, 0.45f));
		return &Brush;
	}

	const FSlateBrush* Swatch()
	{
		static const FSlateRoundedBoxBrush Brush(FLinearColor::White, 4.f, FLinearColor(1.f, 1.f, 1.f, 0.35f), 1.f);
		return &Brush;
	}

	namespace
	{
		FButtonStyle MakeButton(const FLinearColor& Normal, const FLinearColor& Hovered, const FLinearColor& Pressed, const FLinearColor& Edge)
		{
			FButtonStyle Style;
			Style.SetNormal(FSlateRoundedBoxBrush(Normal, 6.f, Edge, 1.f));
			Style.SetHovered(FSlateRoundedBoxBrush(Hovered, 6.f, Edge, 1.f));
			Style.SetPressed(FSlateRoundedBoxBrush(Pressed, 6.f, Edge, 1.f));
			Style.SetDisabled(FSlateRoundedBoxBrush(Normal * FLinearColor(1.f, 1.f, 1.f, 0.35f), 6.f, Edge * FLinearColor(1.f, 1.f, 1.f, 0.35f), 1.f));
			Style.SetNormalPadding(FMargin(12.f, 8.f));
			// Clicks and hovers from Tools/make_audio.py, when they are set up
			if (UObject* Click = LoadObject<USoundBase>(nullptr, TEXT("/Game/External/Audio/ui_click.ui_click"), nullptr, LOAD_NoWarn | LOAD_Quiet))
			{
				FSlateSound Sound;
				Sound.SetResourceObject(Click);
				Style.SetPressedSound(Sound);
			}
			if (UObject* Hover = LoadObject<USoundBase>(nullptr, TEXT("/Game/External/Audio/ui_hover.ui_hover"), nullptr, LOAD_NoWarn | LOAD_Quiet))
			{
				FSlateSound Sound;
				Sound.SetResourceObject(Hover);
				Style.SetHoveredSound(Sound);
			}
			Style.SetPressedPadding(FMargin(12.f, 9.f, 12.f, 7.f));
			return Style;
		}
	}

	const FButtonStyle& PrimaryButton()
	{
		static const FButtonStyle Style = MakeButton(FLinearColor(0.85f, 0.42f, 0.06f), FLinearColor(1.f, 0.55f, 0.12f), FLinearColor(0.65f, 0.3f, 0.04f), FLinearColor(1.f, 0.75f, 0.4f, 0.5f));
		return Style;
	}

	const FButtonStyle& SecondaryButton()
	{
		static const FButtonStyle Style = MakeButton(FLinearColor(1.f, 1.f, 1.f, 0.06f), FLinearColor(1.f, 1.f, 1.f, 0.14f), FLinearColor(1.f, 1.f, 1.f, 0.03f), FLinearColor(1.f, 1.f, 1.f, 0.18f));
		return Style;
	}

	const FEditableTextBoxStyle& TextBox()
	{
		static const FEditableTextBoxStyle Style = []
		{
			FEditableTextBoxStyle S = FCoreStyle::Get().GetWidgetStyle<FEditableTextBoxStyle>("NormalEditableTextBox");
			S.SetBackgroundImageNormal(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.05f), 6.f, FLinearColor(1.f, 1.f, 1.f, 0.16f), 1.f));
			S.SetBackgroundImageHovered(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.08f), 6.f, FLinearColor(1.f, 1.f, 1.f, 0.28f), 1.f));
			S.SetBackgroundImageFocused(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.08f), 6.f, FLinearColor(1.f, 0.56f, 0.12f, 0.9f), 1.f));
			S.SetBackgroundImageReadOnly(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.03f), 6.f, FLinearColor(1.f, 1.f, 1.f, 0.1f), 1.f));
			S.SetForegroundColor(FSlateColor(Text));
			S.SetFocusedForegroundColor(FSlateColor(Text));
			S.SetPadding(FMargin(12.f, 9.f));
			S.SetFont(Font(13));
			return S;
		}();
		return Style;
	}
}
