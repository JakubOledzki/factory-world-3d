#pragma once

#include "CoreMinimal.h"
#include "Fonts/SlateFontInfo.h"
#include "Styling/SlateTypes.h"

struct FSlateBrush;

/**
 * The look of the game's full-screen menus (login, character creator): dark smoked glass panels with rounded
 * corners, an amber accent, light text. Styles live for the whole run (function statics).
 */
namespace FWStyle
{
	const FLinearColor Accent(1.f, 0.56f, 0.12f);
	const FLinearColor Text(0.93f, 0.94f, 0.95f);
	const FLinearColor Muted(0.6f, 0.63f, 0.67f);
	const FLinearColor Error(1.f, 0.38f, 0.32f);
	const FLinearColor Good(0.55f, 0.85f, 0.6f);

	FSlateFontInfo Font(int32 Size, FName Typeface = TEXT("Regular"));
	// Panel: smoked glass with a faint light edge
	const FSlateBrush* Panel();
	// Darkening from the left edge of the screen, behind a side panel
	const FSlateBrush* SideShade();
	// A colour swatch with rounded corners (tinted by the caller)
	const FSlateBrush* Swatch();
	// The outline of a hotbar slot, and of the selected one
	const FSlateBrush* SlotFrame(bool bSelected);
	// Amber call-to-action button, and a quieter glass one
	const FButtonStyle& PrimaryButton();
	const FButtonStyle& SecondaryButton();
	const FEditableTextBoxStyle& TextBox();
}
