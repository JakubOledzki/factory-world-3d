#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class STextBlock;
struct FFWNetReply;

/**
 * Achievements and rivalry (O), as in the 2D game (achievements_window.gd). Three tabs:
 *   Osiągnięcia: badges for milestones in groups, with the progress towards each (achievements.get)
 *   Rankingi: the best players in the ranking picked (rankings.get), and the player's own place
 *   Rywalizacja: the challenge of the week and last week's winners (rivalry.get), the meteorite with digging it
 *     (meteor.get / meteor.dig), the wonders of the world (company.wonders) and the firsts in the world
 */
class SFWAchievementsWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWAchievementsWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);
	void ShowRanking(const FString& Ranking);

	// A round medal: gold when won, dark when not yet
	static TSharedRef<SWidget> Medal(bool bWon, float Size = 40.f);

private:
	void Load();
	void Show(const FFWNetReply& Reply);
	TSharedRef<SWidget> MakeBadges(const TSharedPtr<FJsonObject>& State);
	TSharedRef<SWidget> MakeRanking(const TSharedPtr<FJsonObject>& State);
	TSharedRef<SWidget> MakeRivalry(const TSharedPtr<FJsonObject>& Rivalry, const TSharedPtr<FJsonObject>& Meteor, const TArray<TSharedPtr<FJsonObject>>& Wonders);
	TSharedRef<SWidget> Podium(const TArray<TSharedPtr<FJsonObject>>& Entries, const TCHAR* TextField);
	void Dig();

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	FString Ranking;
	bool bLoading = false;
	bool bAgain = false;
	TSharedPtr<SBox> Content;
	TSharedPtr<STextBlock> Summary;
};
