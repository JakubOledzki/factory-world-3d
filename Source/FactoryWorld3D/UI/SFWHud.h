#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class AFWCharacter;
class AFWPlayerController;
class SVerticalBox;
struct FSlateBrush;

// In-game overlay: crosshair, money, hotbar, toasts, the current action's progress and key hints.
class SFWHud : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWHud) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
	virtual void Tick(const FGeometry& AllottedGeometry, const double InCurrentTime, const float InDeltaTime) override;

private:
	TSharedRef<SWidget> MakeSlot(int32 Slot);
	AFWCharacter* GetCharacter() const;
	// The brush drawing an item's icon (its model, rendered once), null while there is none
	const FSlateBrush* IconBrush(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	TSharedPtr<SVerticalBox> ToastBox;
	int32 ShownToasts = -1;
	double LastToastAt = -1.0;
};
