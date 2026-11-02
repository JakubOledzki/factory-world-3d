#pragma once

#include "CoreMinimal.h"
#include "Fonts/SlateFontInfo.h"
#include "Styling/SlateTypes.h"

struct FSlateBrush;

/**
 * The look of the game's interface, shared by every window and menu: dark smoked glass panels with a faint light
 * edge, an amber accent, light text, and the Barlow typeface (Content/Fonts/Barlow, SIL Open Font License 1.1; full
 * Polish alphabet): Barlow for text, Barlow Condensed for titles. Styles live for the whole run (function statics).
 */
namespace FWStyle
{
	const FLinearColor Accent(1.f, 0.58f, 0.16f);
	const FLinearColor Text(0.93f, 0.94f, 0.95f);
	const FLinearColor Muted(0.6f, 0.64f, 0.69f);
	const FLinearColor Error(1.f, 0.4f, 0.34f);
	const FLinearColor Good(0.56f, 0.87f, 0.6f);
	const FLinearColor Info(0.5f, 0.74f, 1.f);

	// Body text: Regular, Medium, SemiBold or Bold
	FSlateFontInfo Font(int32 Size, FName Typeface = TEXT("Regular"));
	// Titles and headings, narrower and taller: SemiBold or Bold
	FSlateFontInfo Display(int32 Size, FName Typeface = TEXT("Bold"));

	// Panel: smoked glass with a faint light edge
	const FSlateBrush* Panel();
	// A row or card inside a panel, a shade lighter, and the same when the mouse is over it
	const FSlateBrush* Card(bool bHovered = false);
	// Darkening of the screen behind a window
	const FSlateBrush* SideShade();
	// A colour swatch with rounded corners (tinted by the caller)
	const FSlateBrush* Swatch();
	// A key on the keyboard, as drawn in hints ("Esc")
	const FSlateBrush* KeyCap();
	// The outline of a hotbar slot, and of the selected one
	const FSlateBrush* SlotFrame(bool bSelected);
	// Amber call-to-action button, and a quieter glass one
	const FButtonStyle& PrimaryButton();
	const FButtonStyle& SecondaryButton();
	// A tab of a window: plain, and the chosen one
	const FButtonStyle& TabButton(bool bActive);
	const FEditableTextBoxStyle& TextBox();
	const FScrollBarStyle& ScrollBar();
}
