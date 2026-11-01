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
 * A building that serves people (E at it), as in the 2D game:
 *   a shop in a city: goods on its shelves at a price, the townspeople buy by themselves (shop_window.gd; shop.get /
 *     stock / take); at the trader's price every customer buys, at twice that half of them, at three times nobody
 *   a works canteen: dishes feed the crews of the plot's machines, which work faster while there is food
 *     (canteen_window.gd; canteen.get / load)
 */
class SFWShopWindow : public SCompoundWidget
{
public:
	enum class EMode : uint8 { None, Shop, Canteen };

	SLATE_BEGIN_ARGS(SFWShopWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		SLATE_ARGUMENT(FString, Object)
		SLATE_ARGUMENT(FString, Kind)
	SLATE_END_ARGS()

	static EMode ModeOf(const FString& Kind);

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also for the tests: puts Quantity of Item on the shelves at Dollars a piece
	void Stock(const FString& Item, int32 Quantity, double Dollars);

private:
	void Load(bool bPoll = false);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	bool Apply(const FFWNetReply& Reply);
	void Rebuild();
	TSharedRef<SWidget> MakeShop();
	TSharedRef<SWidget> MakeCanteen();
	// The price to start from: the shelf's, else what the trader pays
	void SuggestPrice();
	double ServerNow() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Object;
	FString Kind;
	EMode Mode = EMode::None;
	TSharedPtr<FJsonObject> State;
	// Item → what the trader pays for it, in cents
	TMap<FString, double> Trader;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
