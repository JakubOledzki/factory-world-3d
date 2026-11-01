#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * Production statistics (L), as in the 2D game (stats_window.gd): how much of each item the factory makes and uses up
 * per minute over the last 10 minutes, hour or day (stats.production), as a bar chart for the chosen item, and where
 * it stalls: machines standing still for want of something, and items used up faster than they are made. Asked
 * again every 5 seconds while open.
 */
class SFWStatsWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWStatsWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands: 10, 60 or 1440 minutes; an item's chart
	void SetPeriod(int32 Minutes);
	void SelectItem(const FString& Item);

private:
	void Load();
	void Rebuild();
	TSharedRef<SWidget> MakeBottlenecks();
	TSharedRef<SWidget> MakeTable();

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Minutes = 10;
	TSharedPtr<FJsonObject> State;
	FString Selected;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	bool bLoading = false;
	double NextPoll = 0.0;
};
