#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SUniformGridPanel;
class FFWItemDrag;

/**
 * The inventory (Tab or I): its tiles as the server lays them out, the hotbar under them, and with a chest open its
 * contents beside them. Items are dragged between tiles (inventory.move), onto the hotbar (hotbar.set) and between
 * the inventory and the chest (storage.put / storage.take); shift + click sends a stack straight across.
 */
class SFWInventoryWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWInventoryWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		// With a chest: its object id on the plot
		SLATE_ARGUMENT(FString, Storage)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

private:
	TSharedRef<SWidget> MakeInventoryPanel();
	TSharedRef<SWidget> MakeStoragePanel();
	void RebuildGrids();
	void DropOnInventory(const FFWItemDrag& Drag, int32 Tile);
	void DropOnHotbar(const FFWItemDrag& Drag, int32 Slot);
	void DropOnStorage(const FFWItemDrag& Drag);

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Storage;
	TSharedPtr<SUniformGridPanel> InventoryGrid;
	TSharedPtr<SUniformGridPanel> StorageGrid;
	int32 BuiltTiles = -1;
	int32 BuiltStorageTiles = -1;
};
