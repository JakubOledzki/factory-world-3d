#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
struct FFWNetReply;

/**
 * Orders with a reward and a deadline (J), as in the 2D game (contracts_window.gd): NPC companies give the player
 * orders of their own, and players put orders on a board for each other (contracts.get). Three tabs:
 *   Do dostarczenia: the companies' orders and the players' ones taken, delivered from the inventory, also bit by bit
 *     (contracts.deliver), or given up (contracts.abandon)
 *   Tablica zleceń: other players' orders, taken with contracts.accept
 *   Moje zlecenia: a form for a new order (contracts.create: the reward leaves the wallet at once) and the orders
 *     given, open ones cancelled with contracts.cancel
 */
class SFWContractsWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWContractsWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	void Create(const FString& Item, int32 Quantity, double RewardCents, int32 Hours);
	// The first order on a list ("npc", "taken", "board", "issued"), or 0
	int64 FirstId(const TCHAR* List) const;

private:
	void Load();
	void Rebuild();
	TSharedRef<SWidget> MakeDelivering();
	TSharedRef<SWidget> MakeBoard();
	TSharedRef<SWidget> MakeIssued();
	TSharedRef<SWidget> MakeForm();
	// A card for an open order: icon, name and amount, who, progress, reward and time left, then Buttons
	TSharedRef<SWidget> Card(const TSharedPtr<FJsonObject>& Contract, const FText& Who, TSharedRef<SWidget> Buttons);
	TSharedRef<SWidget> Summary(const TSharedPtr<FJsonObject>& Contract, const FText& Detail, const FLinearColor& Color);
	FText Issuer(const TSharedPtr<FJsonObject>& Contract) const;
	int32 Count(const FString& Item) const;
	double ServerNow() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<SBox> Content;
	TSharedPtr<FJsonObject> View;
	// Every item there is (from the trader's price list), for the new order's picker
	TArray<TSharedPtr<FString>> ItemIds;
	TMap<FString, FString> ItemNames;
	double ClockOffset = 0.0;
	bool bLoading = false;
	double NextCheck = 0.0;
	// The new order form, kept between redraws
	FString NewItem = TEXT("iron_ingot");
	FString NewQuantity = TEXT("50");
	FString NewReward = TEXT("500");
	FString NewHours = TEXT("24");
	// How many to deliver, by order id, as typed
	TMap<int64, FString> Amounts;
};
