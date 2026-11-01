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
 * A country as its players govern it (P), as in the 2D game (country_window.gd): citizenship, the president and the
 * ministers, the laws (plot tax, the fee foreigners pay, tariffs, a subsidy), the treasury and what it builds
 * (country.get / citizenship / law / appoint / invest), the election (country.vote / stand / withdraw / sponsor /
 * recall), diplomacy (country.alliance / embargo), the ranking of countries (country.list) and the parties (party.*).
 * Opens on the player's own country, else on the ranking.
 */
class SFWCountryWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWCountryWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);
	void ShowCountry(const FString& Code);

private:
	void LoadCountry();
	void LoadRanking();
	void LoadParties();
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<FText(const TSharedPtr<FJsonObject>&)> Done);
	void PartyAct(const TCHAR* Type, const FString& Name);
	void Rebuild();
	TSharedRef<SWidget> MakeCountry();
	TSharedRef<SWidget> MakeGovernment();
	TSharedRef<SWidget> MakeElection();
	TSharedRef<SWidget> MakeDiplomacy();
	TSharedRef<SWidget> MakeRanking();
	TSharedRef<SWidget> MakeParties();
	FText OfficeTitle(const FString& Office) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Code;
	int32 Tab = 4;
	TSharedPtr<FJsonObject> State;
	TSharedPtr<FJsonObject> Ranking;
	TSharedPtr<FJsonObject> Parties;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	TSharedPtr<STextBlock> Header;
};
