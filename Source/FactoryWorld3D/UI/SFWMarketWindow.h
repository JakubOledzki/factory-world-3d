#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SEditableTextBox;
struct FFWNetReply;

/**
 * The market, opened at the plot's computer as in the 2D game. Three tabs:
 *   Handlarz: the NPC trader's prices (market.prices), buying and selling 1, 10 or all (market.buy / market.sell)
 *   Giełda: the players' market (market.overview); an item's board of offers to sell and to buy (market.item), buying
 *     from an offer (market.purchase), selling into a request (market.fill), and putting up one's own offer to sell
 *     or to buy at a price (market.list / market.order)
 *   Moje oferty: the player's open offers (market.mine), cancelled with market.unlist / market.cancelOrder
 */
class SFWMarketWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWMarketWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);
	void OpenItem(const FString& Item);
	void Trade(bool bBuy, const FString& Item, int32 Quantity);
	// An own offer on the open item: to sell (market.list) or to buy (market.order), price per piece in cents
	void PutOffer(bool bSell, int32 Quantity, double PriceCents);

private:
	void Load();
	void Rebuild();
	TSharedRef<SWidget> MakeTrader();
	TSharedRef<SWidget> MakeExchange();
	TSharedRef<SWidget> MakeMine();
	TSharedRef<SWidget> ItemRow(const FString& Item, const FString& Name, TSharedRef<SWidget> Right);
	TSharedRef<SWidget> SmallButton(const FText& Text, TFunction<void()> OnClick, bool bPrimary = false, bool bEnabled = true);
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	bool Matches(const FString& Name) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<SBox> Content;
	TSharedPtr<SEditableTextBox> Search;
	FString Filter;
	TSharedPtr<FJsonObject> Prices;
	TSharedPtr<FJsonObject> Overview;
	TSharedPtr<FJsonObject> Board;
	TSharedPtr<FJsonObject> Mine;
	FString BoardItem;
	// The own-offer form on an item's board
	FString OfferQuantity = TEXT("10");
	FString OfferPrice;
};
