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

namespace FWPuzzle
{
	class SBoard;
}

/**
 * Everything to do besides the factory (Y), as in the 2D game (adventures_window.gd). Tabs:
 *   Wydarzenie: the holiday on now, its treats and the decorations they buy (season.get / buy)
 *   Album: pages of rare finds, a full page gives XP (album.get / claim)
 *   Wyprawy: expeditions to far regions that come back with loot (expedition.get / send / claim)
 *   Zagadki: engineering puzzles laid out on a board of belts, inserters and machines (puzzle.get / submit)
 *   Mentor: the mentor of a new player and the mentor's charges (mentor.get / choose / claim)
 *   Gablota: the trophy case, the player's own or another's (showcase.get)
 */
class SFWAdventuresWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWAdventuresWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands: a tab (and whose trophy case), a puzzle by its index
	void ShowTab(int32 Tab, const FString& Player = FString());
	void OpenPuzzle(int32 Index);

private:
	void Load();
	void Show(int32 ForTab, const TSharedPtr<FJsonObject>& State);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<void(const TSharedPtr<FJsonObject>&)> Done);
	void Rebuild();
	TSharedRef<SWidget> MakeEvent();
	TSharedRef<SWidget> MakeAlbum();
	TSharedRef<SWidget> MakeExpeditions();
	TSharedRef<SWidget> MakePuzzles();
	TSharedRef<SWidget> MakeEditor();
	TSharedRef<SWidget> MakeMentor();
	TSharedRef<SWidget> MakeShowcase();
	void SubmitPuzzle();

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	bool bLoading = false;
	bool bAgain = false;
	FString ShowcaseOf;
	TSharedPtr<FJsonObject> State;
	// When the expeditions came (FPlatformTime seconds), for their countdowns from the server's "now"
	double StateAt = 0.0;
	// The puzzle open in the editor (null for the list), its board and what the last check said
	TSharedPtr<FJsonObject> Puzzle;
	TSharedPtr<FWPuzzle::SBoard> Board;
	FText Result;
	FLinearColor ResultColor = FLinearColor::White;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	TSharedPtr<STextBlock> Summary;
};
