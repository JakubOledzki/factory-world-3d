#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * Banks of companies (Z; B builds in 3D), as in the 2D game (bank_window.gd): a bank stands in a town and has branches
 * in others. The player opens an account in any of them, pays money in from the wallet, takes it out and sends it to
 * other players' accounts. Money travels: what is on its way shows with a countdown (bank.get every 5 s; bank.open /
 * deposit / withdraw / transfer / close).
 */
class SFWBankWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWBankWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands
	void ShowBank(const FString& Bank);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	FString GetBank() const { return Bank; }

private:
	void Load();
	// bPoll: asked again by itself; then the page is not built anew under the player typing in it
	void Apply(const FFWNetReply& Reply, bool bPoll = false);
	void Rebuild();
	double ServerNow() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	TSharedPtr<FJsonObject> State;
	FString Bank;
	double ClockOffset = 0.0;
	int32 Transfers = -1;
	double NextPoll = 0.0;
	bool bLoading = false;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	// The amounts and the recipient typed, kept between redraws
	FString Amount = TEXT("100");
	FString Sent = TEXT("100");
	FString Recipient;
	FString TargetBank;
	TArray<TSharedPtr<FString>> BankIds;
	TMap<FString, FText> BankNames;
};
