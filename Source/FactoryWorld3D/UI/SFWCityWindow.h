#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
class STextBlock;
struct FFWNetReply;

/**
 * The town the plot belongs to (H, for the town hall), as in the 2D game (city_window.gd): what it wants today
 * (city.deliver), the week's tender (city.tenderBid / tenderWithdraw / tenderDeliver), how it grows and who supplies it
 * most, the rubbish it pays to have taken away (city.trash), the services of players' offices (city.service), the town
 * hall with the mayor's tax, bonus, festival, ordinances and the recall (city.policy / festival / propose / ordinance /
 * recall), the election (city.vote / stand / withdraw / sponsor) and the treasury with its buildings (city.invest). On a
 * plot of the county outside the town only the hall, the election, the treasury and the tender.
 */
class SFWCityWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWCityWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);

private:
	void Load();
	// Sends a city message for this plot; Done makes the toast from the reply's result
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<FText(const TSharedPtr<FJsonObject>&)> Done);
	bool Apply(const FFWNetReply& Reply);
	void Rebuild();
	TSharedRef<SWidget> MakeNeeds();
	TSharedRef<SWidget> MakeTender();
	TSharedRef<SWidget> MakeGrowth();
	TSharedRef<SWidget> MakeTrash();
	TSharedRef<SWidget> MakeServices();
	TSharedRef<SWidget> MakeHall();
	TSharedRef<SWidget> MakeElection();
	TSharedRef<SWidget> MakeTreasury();
	FText BonusName(const FString& Bonus) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FIntPoint Plot = FIntPoint(-1, -1);
	int32 Tab = 0;
	TSharedPtr<FJsonObject> State;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	TSharedPtr<STextBlock> Header;
};
