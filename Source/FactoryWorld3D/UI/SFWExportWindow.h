#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * The export chest by the plot's station (E at it), as in the 2D game (export_window.gd): a price for each kind of goods
 * in it, and every full hour a train takes everything priced to the nearest buying market in a town, which lists it on
 * the players' market at that price (export.get / load / unload / price). Or, opened at a buying market or a rail port
 * (bPost), that post: its owner sets the market's commission or the port's fee and sees what it earned (export.post /
 * setPost).
 */
class SFWExportWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWExportWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		// A buying market or a rail port by its object id; empty for the plot's export chest
		SLATE_ARGUMENT(FString, Post)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

private:
	void Load(bool bPoll = false);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	bool Apply(const FFWNetReply& Reply);
	void Rebuild();
	TSharedRef<SWidget> MakeChest();
	TSharedRef<SWidget> MakePost();
	double ServerNow() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Post;
	FIntPoint Plot = FIntPoint(-1, -1);
	TSharedPtr<FJsonObject> State;
	// What the trader pays for each item, to start a price from
	TMap<FString, double> Trader;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
