#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SEditableTextBox;
class SScrollBox;
struct FFWNetReply;

/**
 * The market, opened at the plot's computer as in the 2D game. Items are sorted into groups (raw materials, food,
 * machines, ...; the server's ItemCategories), picked in a column on the left, and each tab says in a line what it is
 * for, so that a player knows where to sell at once and where to put up an offer. Three tabs:
 *   Handlarz: the NPC trader's prices (market.prices), buying and selling 1, 10 or all (market.buy / market.sell); a trade
 *     asks only for the inventory again, right behind it, and a sale shows its pieces gone before the server answers
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
	// A group of items ("" every item, "mine" what the player has)
	void ShowCategory(const FString& InCategory);
	void Trade(bool bBuy, const FString& Item, int32 Quantity);
	// An own offer on the open item: to sell (market.list) or to buy (market.order), price per piece in cents
	void PutOffer(bool bSell, int32 Quantity, double PriceCents);

private:
	void Load();
	void Rebuild();
	TSharedRef<SWidget> MakeTrader();
	TSharedRef<SWidget> MakeExchange();
	TSharedRef<SWidget> MakeMine();
	// The column of groups on the left, with how many of Source's items (as the search leaves them) each has
	TSharedRef<SWidget> MakeCategories(const TSharedPtr<FJsonObject>& Source);
	// A line under the tabs: what this tab is for
	TSharedRef<SWidget> Hint(const FText& Text);
	bool InCategory(const FJsonObject& Item) const;
	bool Has(const FString& Item) const;
	TSharedRef<SWidget> ItemRow(const FString& Item, const FString& Name, TSharedRef<SWidget> Right);
	TSharedRef<SWidget> SmallButton(const FText& Text, TFunction<void()> OnClick, bool bPrimary = false, bool bEnabled = true);
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	bool Matches(const FString& Name) const;
	// How many of an item the player has, less what is being sold to the trader right now
	int32 Have(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<SBox> Content;
	// The tab's long list as last built, so a rebuild keeps where the player had scrolled to
	TSharedPtr<SScrollBox> List;
	int32 ListTab = -1;
	TSharedPtr<SEditableTextBox> Search;
	FString Filter;
	FString Category;
	TSharedPtr<FJsonObject> Prices;
	TSharedPtr<FJsonObject> Overview;
	TSharedPtr<FJsonObject> Board;
	TSharedPtr<FJsonObject> Mine;
	FString BoardItem;
	// The own-offer form on an item's board
	FString OfferQuantity = TEXT("10");
	FString OfferPrice;
	// Sales to the trader sent but not yet in the inventory: their pieces are shown gone at once
	struct FPendingSale
	{
		int32 Seq = 0;
		FString Item;
		int32 Quantity = 0;
	};
	TArray<FPendingSale> PendingSales;
	int32 TradeSeq = 0;
};
