#pragma once

#include "CoreMinimal.h"
#include "Widgets/SWidget.h"

class AFWPlayerController;

/**
 * What a window's player typed and picked, kept between its redraws (the windows build their pages anew on every
 * answer from the server), and the fields and pickers that edit it, in the look of FWStyle.
 */
class FFWForm : public TSharedFromThis<FFWForm>
{
public:
	// A text field for Key, Default until the player types
	TSharedRef<SWidget> Field(const FString& Key, const FText& Hint, float Width, const FString& Default = FString());
	// A drop-down for Key from (id, name) options; OnPicked runs when the player picks (not when built)
	TSharedRef<SWidget> Pick(const FString& Key, const TArray<TPair<FString, FText>>& Options, float Width, TFunction<void(const FString&)> OnPicked = nullptr,
		AFWPlayerController* IconsFrom = nullptr);
	// A tick box for Key ("1" / "0")
	TSharedRef<SWidget> Check(const FString& Key, const FText& Text, bool bDefault);

	FString Get(const FString& Key) const;
	void Set(const FString& Key, const FString& Value) { Values.Add(Key, Value); }
	bool Has(const FString& Key) const { return Values.Contains(Key); }
	double Number(const FString& Key) const;
	// Dollars typed, in cents
	double Cents(const FString& Key) const { return FMath::RoundToDouble(Number(Key) * 100.0); }
	bool Ticked(const FString& Key) const { return Get(Key) == TEXT("1"); }

private:
	TMap<FString, FString> Values;
	TMap<FString, TArray<TSharedPtr<FString>>> Options;
	TMap<FString, TMap<FString, FText>> Names;
};
