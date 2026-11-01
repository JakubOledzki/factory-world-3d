#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
struct FFWNetReply;

namespace FWTimelapse
{
	class SMap;
}

/**
 * The player's factory as a whole (G), as in the 2D game. Tabs:
 *   Plany: building plans kept on the server by name (plans_window.gd; plans.get / save / delete): the objects around
 *     the player saved as a plan, and a plan put up in front of the player
 *   Timelapse: each day someone walked onto the plot, seen from above, played one after another (timelapse.get)
 *   Galeria: the most liked public factories and a visit to them; liking the plot one stands on (plot.gallery,
 *     plot.like)
 */
class SFWFactoryWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWFactoryWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also for the tests
	void ShowTab(int32 InTab);
	void SavePlan(const FString& Name, int32 Radius);
	void UsePlan(int32 Index);

private:
	void Load();
	void Rebuild();
	TSharedRef<SWidget> MakePlans();
	TSharedRef<SWidget> MakeTimelapse();
	TSharedRef<SWidget> MakeGallery();
	void ApplyPlans(const FFWNetReply& Reply);
	void ShowFrame(int32 Frame);
	FString NameOf(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<FJsonObject> Plans;
	TSharedPtr<FJsonObject> Timelapse;
	TSharedPtr<FJsonObject> Gallery;
	// The plot stood on: its likes, and whether the player likes it
	int32 Likes = 0;
	bool bLiked = false;
	bool bOwnPlot = true;
	int32 Frame = 0;
	// What every object is called (the backpack knows only what it holds)
	TMap<FString, FString> ItemNames;
	bool bPlaying = false;
	double NextFrameAt = 0.0;
	TSharedPtr<FWTimelapse::SMap> Map;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};

/**
 * "Gdy cię nie było": what came into the wallet and went out of it while the player was away, and what the machines
 * made (away_report_window.gd; report.away). The server hands it out once, on the first return after a break.
 */
class SFWAwayWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWAwayWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		SLATE_ARGUMENT(TSharedPtr<FJsonObject>, Report)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

private:
	TWeakObjectPtr<AFWPlayerController> Controller;
};
