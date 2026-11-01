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
 * The police of the countries players govern, as in the 2D game (police_window.gd), opened from the country window.
 * Prawo: the country's laws, licences, cases and bans, reporting a player, joining the police; the government appoints
 * the chief and sets what is banned (law.get / buyLicence / appeal / report / apply / resign / appointChief / setLaws).
 * Służba: the officer's desk: trains that crossed the border, cases to sentence, reports, investigations and warrants,
 * and for the chief the officers (law.inspect / sentence / dismiss / investigate / requestWarrant / search / closeReport /
 * setRank / fire / hire). Sąd: appeals and warrants (law.judge / decideWarrant). Paser: the fence (fence.get / sell /
 * compartment, law.bribe). Asked again every 8 seconds while open.
 */
class SFWPoliceWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWPoliceWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);

private:
	void Load(bool bPoll = false);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	void Rebuild();
	TSharedRef<SWidget> MakeLaw();
	TSharedRef<SWidget> MakeDuty();
	TSharedRef<SWidget> MakeCourt();
	TSharedRef<SWidget> MakeFence();
	FText CaseText(const TSharedPtr<FJsonObject>& Case, bool bWithSuspect) const;
	FText FoundText(const TSharedPtr<FJsonObject>& Found) const;
	double ServerNow() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<FJsonObject> State;
	TSharedPtr<FJsonObject> Fence;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	// What the last inspection or search found, and the case whose sentence form is open
	FText Finding;
	int64 Sentencing = -1;
};
