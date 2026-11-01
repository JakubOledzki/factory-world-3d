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
 * The player's garage (U), as in the 2D game (garage.gd and its windows). Tabs:
 *   Segregacja: sorting trash by hand at the bin, bag after bag (recycling.sort / stop, garage.get)
 *   Sortownia: the automatic sorting machine (sorter_window.gd; sorter.get / load / take)
 *   Ulepszenia: floor, walls, lighting, shelves and heating, level by level for money and materials, each with a
 *     bonus for good (upgrades_window.gd; garage.upgrades / upgrade)
 *   Garderoba: the wardrobe's clothes and saved outfits (wardrobe_window.gd; wardrobe.get / put / take / save / wear /
 *     forget)
 *   Meble: the room from above with its furniture, putting furniture from the backpack in and taking it out
 *     (garage.place / pickup)
 */
class SFWGarageWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWGarageWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	virtual ~SFWGarageWindow() override;

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also for the tests
	void ShowTab(int32 InTab);
	void ToggleSorting();
	void PlaceFurniture(const FString& Kind);
	void LoadSorter(int32 Quantity);
	void WearOutfit(int32 Number, bool bSave);
	void HangUp(const FString& Item);

private:
	void LoadGarage(bool bPoll = false);
	void ApplyGarage(const FFWNetReply& Reply);
	void LoadTab();
	void Sort();
	void Rebuild();
	TSharedRef<SWidget> MakeSorting();
	TSharedRef<SWidget> MakeSorter();
	TSharedRef<SWidget> MakeUpgrades();
	TSharedRef<SWidget> MakeWardrobe();
	TSharedRef<SWidget> MakeFurniture();
	FString FurnitureId(const FString& Kind) const;
	void SendSorter(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	void SendWardrobe(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	double ServerNow() const;
	int32 Owned(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	int32 Tab = 0;
	TSharedPtr<FJsonObject> Garage;
	TSharedPtr<FJsonObject> Sorter;
	TSharedPtr<FJsonObject> Upgrades;
	TSharedPtr<FJsonObject> Wardrobe;
	FString Track = TEXT("floor");
	// Sorting by hand goes on bag after bag while the window is open
	bool bHandSorting = false;
	bool bSortPending = false;
	// What came out of the last bags, newest first: item id and name
	TArray<TPair<FString, FString>> Recent;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
