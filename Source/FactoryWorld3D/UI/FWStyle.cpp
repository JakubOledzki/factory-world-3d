#include "UI/FWStyle.h"

#include "Brushes/SlateColorBrush.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "Fonts/CompositeFont.h"
#include "Misc/Paths.h"
#include "Sound/SoundBase.h"
#include "Styling/CoreStyle.h"

namespace FWStyle
{
	namespace
	{
		// A family of the font files in Content/Fonts/Barlow, one typeface per weight; null when the files are missing
		TSharedPtr<const FCompositeFont> Family(const TCHAR* Name, TArrayView<const TPair<FName, const TCHAR*>> Weights)
		{
			const FString Folder = FPaths::ProjectContentDir() / TEXT("Fonts/Barlow");
			TSharedRef<FStandaloneCompositeFont> Font = MakeShared<FStandaloneCompositeFont>();
			for (const TPair<FName, const TCHAR*>& Weight : Weights)
			{
				const FString File = Folder / FString::Printf(TEXT("%s-%s.ttf"), Name, Weight.Value);
				if (!FPaths::FileExists(File))
				{
					return nullptr;
				}
				Font->DefaultTypeface.AppendFont(Weight.Key, File, EFontHinting::Default, EFontLoadingPolicy::LazyLoad);
			}
			return Font;
		}

		const TSharedPtr<const FCompositeFont>& Body()
		{
			static const TPair<FName, const TCHAR*> Weights[] = {
				{ TEXT("Regular"), TEXT("Regular") }, { TEXT("Medium"), TEXT("Medium") }, { TEXT("SemiBold"), TEXT("SemiBold") }, { TEXT("Bold"), TEXT("Bold") } };
			static const TSharedPtr<const FCompositeFont> Font = Family(TEXT("Barlow"), Weights);
			return Font;
		}

		const TSharedPtr<const FCompositeFont>& Titles()
		{
			static const TPair<FName, const TCHAR*> Weights[] = { { TEXT("SemiBold"), TEXT("SemiBold") }, { TEXT("Bold"), TEXT("Bold") } };
			static const TSharedPtr<const FCompositeFont> Font = Family(TEXT("BarlowCondensed"), Weights);
			return Font;
		}
	}

	FSlateFontInfo Font(int32 Size, FName Typeface)
	{
		// Barlow sits a little smaller than the engine's Roboto at the same size
		return Body().IsValid() ? FSlateFontInfo(Body(), Size + 1, Typeface) : FCoreStyle::GetDefaultFontStyle(Typeface, Size);
	}

	FSlateFontInfo Display(int32 Size, FName Typeface)
	{
		if (!Titles().IsValid())
		{
			return Font(Size, TEXT("Bold"));
		}
		FSlateFontInfo Title(Titles(), Size, Typeface == TEXT("SemiBold") ? Typeface : FName(TEXT("Bold")));
		Title.LetterSpacing = 40;
		return Title;
	}

	const FSlateBrush* Panel()
	{
		static const FSlateRoundedBoxBrush Brush(FLinearColor(0.016f, 0.02f, 0.026f, 0.86f), 12.f, FLinearColor(1.f, 1.f, 1.f, 0.09f), 1.f);
		return &Brush;
	}

	const FSlateBrush* Card(bool bHovered)
	{
		static const FSlateRoundedBoxBrush Quiet(FLinearColor(1.f, 1.f, 1.f, 0.035f), 8.f, FLinearColor(1.f, 1.f, 1.f, 0.06f), 1.f);
		static const FSlateRoundedBoxBrush Lit(FLinearColor(1.f, 1.f, 1.f, 0.07f), 8.f, FLinearColor(1.f, 0.58f, 0.16f, 0.45f), 1.f);
		return bHovered ? &Lit : &Quiet;
	}

	const FSlateBrush* SideShade()
	{
		static const FSlateColorBrush Brush(FLinearColor(0.f, 0.f, 0.f, 0.5f));
		return &Brush;
	}

	const FSlateBrush* Swatch()
	{
		static const FSlateRoundedBoxBrush Brush(FLinearColor::White, 4.f, FLinearColor(1.f, 1.f, 1.f, 0.35f), 1.f);
		return &Brush;
	}

	const FSlateBrush* KeyCap()
	{
		static const FSlateRoundedBoxBrush Brush(FLinearColor(1.f, 1.f, 1.f, 0.08f), 4.f, FLinearColor(1.f, 1.f, 1.f, 0.35f), 1.f);
		return &Brush;
	}

	const FSlateBrush* SlotFrame(bool bSelected)
	{
		static const FSlateRoundedBoxBrush Quiet(FLinearColor::Transparent, 10.f, FLinearColor(1.f, 1.f, 1.f, 0.14f), 1.f);
		static const FSlateRoundedBoxBrush Lit(FLinearColor(1.f, 0.58f, 0.16f, 0.12f), 10.f, Accent, 2.f);
		return bSelected ? &Lit : &Quiet;
	}

	namespace
	{
		FSlateSound UiSound(const TCHAR* Name)
		{
			FSlateSound Sound;
			// Clicks and hovers from Tools/make_audio.py, when they are set up
			if (UObject* Wave = LoadObject<USoundBase>(nullptr, *FString::Printf(TEXT("/Game/External/Audio/%s.%s"), Name, Name), nullptr, LOAD_NoWarn | LOAD_Quiet))
			{
				Sound.SetResourceObject(Wave);
			}
			return Sound;
		}

		FButtonStyle MakeButton(const FLinearColor& Normal, const FLinearColor& Hovered, const FLinearColor& Pressed, const FLinearColor& Edge,
			const FLinearColor& HoveredEdge)
		{
			FButtonStyle Style;
			Style.SetNormal(FSlateRoundedBoxBrush(Normal, 7.f, Edge, 1.f));
			Style.SetHovered(FSlateRoundedBoxBrush(Hovered, 7.f, HoveredEdge, 1.f));
			Style.SetPressed(FSlateRoundedBoxBrush(Pressed, 7.f, HoveredEdge, 1.f));
			Style.SetDisabled(FSlateRoundedBoxBrush(Normal * FLinearColor(1.f, 1.f, 1.f, 0.35f), 7.f, Edge * FLinearColor(1.f, 1.f, 1.f, 0.35f), 1.f));
			Style.SetNormalPadding(FMargin(14.f, 8.f));
			Style.SetPressedPadding(FMargin(14.f, 9.f, 14.f, 7.f));
			Style.SetPressedSound(UiSound(TEXT("ui_click")));
			Style.SetHoveredSound(UiSound(TEXT("ui_hover")));
			return Style;
		}
	}

	const FButtonStyle& PrimaryButton()
	{
		static const FButtonStyle Style = MakeButton(FLinearColor(0.88f, 0.44f, 0.07f), FLinearColor(1.f, 0.56f, 0.14f), FLinearColor(0.66f, 0.31f, 0.04f),
			FLinearColor(1.f, 0.78f, 0.45f, 0.55f), FLinearColor(1.f, 0.88f, 0.65f, 0.9f));
		return Style;
	}

	const FButtonStyle& SecondaryButton()
	{
		static const FButtonStyle Style = MakeButton(FLinearColor(1.f, 1.f, 1.f, 0.06f), FLinearColor(1.f, 1.f, 1.f, 0.13f), FLinearColor(1.f, 1.f, 1.f, 0.03f),
			FLinearColor(1.f, 1.f, 1.f, 0.16f), FLinearColor(1.f, 0.58f, 0.16f, 0.6f));
		return Style;
	}

	const FButtonStyle& TabButton(bool bActive)
	{
		static const FButtonStyle Plain = MakeButton(FLinearColor(1.f, 1.f, 1.f, 0.f), FLinearColor(1.f, 1.f, 1.f, 0.08f), FLinearColor(1.f, 1.f, 1.f, 0.04f),
			FLinearColor(1.f, 1.f, 1.f, 0.f), FLinearColor(1.f, 1.f, 1.f, 0.14f));
		static const FButtonStyle Chosen = MakeButton(FLinearColor(1.f, 0.58f, 0.16f, 0.14f), FLinearColor(1.f, 0.58f, 0.16f, 0.2f),
			FLinearColor(1.f, 0.58f, 0.16f, 0.1f), FLinearColor(1.f, 0.58f, 0.16f, 0.5f), FLinearColor(1.f, 0.58f, 0.16f, 0.8f));
		return bActive ? Chosen : Plain;
	}

	const FEditableTextBoxStyle& TextBox()
	{
		static const FEditableTextBoxStyle Style = []
		{
			FEditableTextBoxStyle S = FCoreStyle::Get().GetWidgetStyle<FEditableTextBoxStyle>("NormalEditableTextBox");
			S.SetBackgroundImageNormal(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.05f), 7.f, FLinearColor(1.f, 1.f, 1.f, 0.16f), 1.f));
			S.SetBackgroundImageHovered(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.08f), 7.f, FLinearColor(1.f, 1.f, 1.f, 0.28f), 1.f));
			S.SetBackgroundImageFocused(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.08f), 7.f, FLinearColor(1.f, 0.58f, 0.16f, 0.9f), 1.f));
			S.SetBackgroundImageReadOnly(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.03f), 7.f, FLinearColor(1.f, 1.f, 1.f, 0.1f), 1.f));
			S.SetForegroundColor(FSlateColor(Text));
			S.SetFocusedForegroundColor(FSlateColor(Text));
			S.SetPadding(FMargin(12.f, 9.f));
			S.SetFont(Font(13));
			return S;
		}();
		return Style;
	}

	const FScrollBarStyle& ScrollBar()
	{
		static const FScrollBarStyle Style = []
		{
			FScrollBarStyle S = FCoreStyle::Get().GetWidgetStyle<FScrollBarStyle>("ScrollBar");
			const FSlateRoundedBoxBrush Track(FLinearColor(1.f, 1.f, 1.f, 0.03f), 3.f);
			S.SetVerticalBackgroundImage(Track);
			S.SetHorizontalBackgroundImage(Track);
			S.SetNormalThumbImage(FSlateRoundedBoxBrush(FLinearColor(1.f, 1.f, 1.f, 0.18f), 3.f));
			S.SetHoveredThumbImage(FSlateRoundedBoxBrush(FLinearColor(1.f, 0.58f, 0.16f, 0.6f), 3.f));
			S.SetDraggedThumbImage(FSlateRoundedBoxBrush(FLinearColor(1.f, 0.58f, 0.16f, 0.85f), 3.f));
			S.SetThickness(6.f);
			return S;
		}();
		return Style;
	}
}
