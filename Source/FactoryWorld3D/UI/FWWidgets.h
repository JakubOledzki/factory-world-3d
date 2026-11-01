#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SWidget.h"

class AFWPlayerController;

/**
 * Pieces the game's windows share, in the look of FWStyle: the window frame with its title and tabs, buttons,
 * headings, item rows, and reading the server's JSON.
 */
namespace FWUi
{
	// "1 234,50 $"
	FText Money(double Cents);
	// Time left as "2 d 5 h", "3 h 12 min", "8 min" or "40 s"; and a duration as "45 s" or "2 min 05 s"
	FText Left(double Ms);
	FText Duration(double Seconds);
	// Milliseconds since 1970 on this computer (add the server's offset for the server's clock)
	double LocalNowMs();

	FString Str(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field);
	double Num(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field, double Default = 0.0);
	bool Bool(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field);
	TSharedPtr<FJsonObject> Obj(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field);
	TArray<TSharedPtr<FJsonObject>> Objects(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field);
	TArray<FString> Strings(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field);

	// A window in the middle of the blurred screen: title, something on the right of it (e.g. the balance), a row of
	// tabs (may be null) and the content; "Esc zamyka" under it
	TSharedRef<SWidget> Frame(const FText& Title, TSharedRef<SWidget> Right, TSharedPtr<SWidget> Tabs, TSharedRef<SWidget> Content,
		float Width, float Height);
	// A tab lit while IsActive says so
	TSharedRef<SWidget> TabButton(const FText& Text, TFunction<bool()> IsActive, TFunction<void()> OnClick);
	TSharedRef<SWidget> SmallButton(const FText& Text, TFunction<void()> OnClick, bool bPrimary = false, bool bEnabled = true);
	TSharedRef<SWidget> Heading(const FText& Text);
	TSharedRef<SWidget> Note(const FText& Text, const FLinearColor& Color);
	// The item's icon (rendered from its model), or nothing
	TSharedRef<SWidget> Icon(AFWPlayerController* Controller, const FString& Item, float Size);
	// A thin bar filled to Fraction (0-1)
	TSharedRef<SWidget> Bar(TAttribute<float> Fraction, const FLinearColor& Color, float Width = 0.f);
}
