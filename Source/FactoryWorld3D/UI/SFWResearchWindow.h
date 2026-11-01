#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SWrapBox;
struct FFWNetReply;

/**
 * The research table, as in the 2D game (research_panel.gd): a technology tree for each branch of industry, picked
 * with the tabs on top. In a tree each technology is a card in the column of its depth within the branch, joined by
 * curves to the ones it needs; what it needs from other branches shows as small icons on the card. The chosen one
 * shows its cost, time and what it unlocks on the right, with starting (research.start), cancelling
 * (research.cancel) and speeding up with research data (research.boost). A company's research is brought together
 * by its members (research.companyStart / contribute / companyCancel). One technology at a time; it goes on while
 * the player is away.
 */
class SFWResearchWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWResearchWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands
	void Choose(const FString& Id);
	void OpenBranch(const FString& Id);
	void Start();
	void Cancel();

private:
	void Load();
	void Apply(const FFWNetReply& Reply);
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	void Rebuild();
	void RebuildDetails();
	TSharedRef<SWidget> MakeTree();
	TSharedRef<SWidget> MakeCard(const TSharedPtr<FJsonObject>& Technology, const TArray<TSharedPtr<FJsonObject>>& Outside);
	TSharedRef<SWidget> MakeCompany(const TSharedPtr<FJsonObject>& Technology);
	TSharedRef<SWidget> CostRow(const FString& Item, const FString& Name, const FText& Amount, const FLinearColor& Color);

	TArray<TSharedPtr<FJsonObject>> Technologies() const;
	TSharedPtr<FJsonObject> Technology(const FString& Id) const;
	TSharedPtr<FJsonObject> Active() const;
	FString IconOf(const TSharedPtr<FJsonObject>& Technology) const;
	FText StatusText(const TSharedPtr<FJsonObject>& Technology) const;
	FLinearColor StatusColor(const FString& Status) const;
	int32 Count(const FString& Item) const;
	bool CanPay(const TSharedPtr<FJsonObject>& Technology) const;
	double ServerNow() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	TSharedPtr<FJsonObject> State;
	FString Selected;
	FString Branch;
	double ClockOffset = 0.0;
	bool bLoading = false;
	bool bBusy = false;
	double NextPoll = 0.0;
	double DetailsAgainAt = 0.0;
	TSharedPtr<SWrapBox> Tabs;
	TSharedPtr<SBox> TreeBox;
	TSharedPtr<SBox> Details;
	// The money a member adds to the company's research, as typed
	FString MoneyText;
};
