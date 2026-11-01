#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "UObject/StrongObjectPtr.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
class UTexture2D;
struct FFWNetReply;
struct FSlateBrush;

/**
 * The player's garage (U), as in the 2D game (garage.gd and its windows). Tabs:
 *   Segregacja: sorting trash by hand at the bin, a small game: a bag's five pieces are dragged into the metal, plastic
 *     and glass bins; a wrong bin sends the piece back, and the bag is done when all are in (recycling.sort / put / stop,
 *     garage.get; the server checks every piece)
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
	// Puts piece Piece of the open bag into Bin ("metal", "plastic", "glass")
	void PutPiece(int32 Piece, const FString& Bin);
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
	TSharedRef<SWidget> MakeSortingGame();
	const FSlateBrush* WasteBrush(const FString& Name);
	bool HasRound() const;
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
	bool bSortPending = false;
	// The sorting game: the piece being dragged and the one on its way to the server (-1 for none), when each piece last
	// came back out of a wrong bin, and the bin that last took or threw back a piece
	int32 DraggingPiece = -1;
	int32 PendingPiece = -1;
	TMap<int32, double> WrongAt;
	FString FlashBin;
	double FlashAt = -10.0;
	bool bFlashGood = false;
	// What the last finished bag gave, shown until the next one is opened
	FText LastBag;
	TMap<FString, TSharedPtr<FSlateBrush>> WasteBrushes;
	TArray<TStrongObjectPtr<UTexture2D>> WasteTextures;
	// What came out of the last bags, newest first: item id and name
	TArray<TPair<FString, FString>> Recent;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
