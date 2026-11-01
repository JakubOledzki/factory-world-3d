#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * The player's company (K), as in the 2D game (company_window.gd). Tabs:
 *   Spółka: members and roles, invitations, the plots brought in, the shared warehouse, what happened lately; without a
 *     company founding one and the invitations from others (company.get / create / accept / invite / role / kick /
 *     addPlot / removePlot / assign / put / take / leave)
 *   Kasa: cash, paying in and out, selling from and buying into the warehouse, shares and the dividend
 *   Projekty: the big projects members bring parts to (company.project.start / give)
 *   Zlecenia: the weekly orders and the tasks with a wage (company.order.deliver, company.task.*)
 *   Umowy: standing deals with other companies (company.deal.*)
 *   Giełda: the stock exchange (company.exchange, company.shares.buy / sell, company.list, company.takeover)
 *   Ranking: the season's rankings of companies (company.rankings)
 */
class SFWCompanyWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWCompanyWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);
	// Sends a company message; on success shows the new state and refreshes the inventory and the wallet
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);

private:
	void Load();
	void LoadTab();
	void Rebuild();
	TSharedRef<SWidget> MakeWithout();
	TSharedRef<SWidget> MakeCompany(const TSharedPtr<FJsonObject>& Company);
	TSharedRef<SWidget> MakeCash(const TSharedPtr<FJsonObject>& Company);
	TSharedRef<SWidget> MakeProjects(const TSharedPtr<FJsonObject>& Company);
	TSharedRef<SWidget> MakeOrders(const TSharedPtr<FJsonObject>& Company);
	TSharedRef<SWidget> MakeDeals(const TSharedPtr<FJsonObject>& Company);
	TSharedRef<SWidget> MakeExchange();
	TSharedRef<SWidget> MakeRankings();

	// Pieces
	TSharedRef<SWidget> ItemLine(const FString& Item, const FText& Text, TSharedPtr<SWidget> Right = nullptr);
	TSharedRef<SWidget> TextLine(const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right = nullptr);
	TSharedRef<SWidget> Field(const FString& Key, const FText& Hint, float Width, const FString& Default = FString());
	FString Value(const FString& Key) const;
	TSharedRef<SWidget> Picker(const FString& Key, const TArray<TPair<FString, FText>>& Options, float Width);
	TSharedRef<SWidget> ItemPicker(const FString& Key, bool bTraderBuysOnly);
	TSharedPtr<FJsonObject> Pay(std::initializer_list<TPair<const TCHAR*, FString>> Strings, std::initializer_list<TPair<const TCHAR*, double>> Numbers) const;
	int32 Owned(const FString& Item) const;
	static int32 Stored(const TSharedPtr<FJsonObject>& Company, const FString& Item);
	FText LogText(const TSharedPtr<FJsonObject>& Entry) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	TSharedPtr<FJsonObject> State;
	TSharedPtr<FJsonObject> Exchange;
	TSharedPtr<FJsonObject> Rankings;
	// The player's own plots (world.info) and every item the trader deals in (market.prices)
	TArray<TSharedPtr<FJsonObject>> MyPlots;
	TMap<int32, FString> BiomeNames;
	TArray<TSharedPtr<FJsonObject>> Catalog;
	// What the player typed or picked, by field, kept between redraws
	TMap<FString, FString> Inputs;
	// The options of the pickers, kept alive for their combo boxes
	TMap<FString, TArray<TSharedPtr<FString>>> PickerOptions;
	TMap<FString, TMap<FString, FText>> PickerNames;
};
