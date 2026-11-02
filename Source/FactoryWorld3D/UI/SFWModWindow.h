#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SEditableTextBox;
class SVerticalBox;
struct FFWNetReply;

/**
 * The mod menu (F8) of a test account: money into the wallet, any item into the backpack, a tool into the
 * tool bar, a plot of a chosen biome, or the whole test world at once. It is built on the server's developer commands
 * (Server/src/FactoryWorld.Server/DevCommands.cs), which work only for the local test accounts of a development server
 * (Dev:Accounts) and the mod accounts of any server (Mod:Accounts); "dev.access" said this player is one and what
 * there is to give.
 */
class SFWModWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWModWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		// The "dev.access" reply: items, tools and biomes
		SLATE_ARGUMENT(TSharedPtr<FJsonObject>, Access)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also used by the test console commands (FWMod...)
	void GiveMoney(double Cents);
	void GiveItem(const FString& Item, int32 Quantity);
	void GiveTool(const FString& Tool);
	void BuyPlot(const FString& Biome);
	void SetFilter(const FString& Text);

private:
	TSharedRef<SWidget> MoneySection();
	TSharedRef<SWidget> ToolSection();
	TSharedRef<SWidget> PlotSection();
	TSharedRef<SWidget> WorldSection();
	TSharedRef<SWidget> ItemSection();
	void RebuildItems();
	// Sends a developer command; on success refreshes the wallet and the backpack and shows Done(reply) as a toast
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<FText(const FFWNetReply&)> Done);

	TWeakObjectPtr<AFWPlayerController> Controller;
	TSharedPtr<FJsonObject> Access;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SVerticalBox> ItemList;
	TSharedPtr<SEditableTextBox> SearchBox;
	FString Filter;
};
