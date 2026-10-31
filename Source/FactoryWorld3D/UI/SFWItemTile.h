#pragma once

#include "CoreMinimal.h"
#include "Input/DragAndDrop.h"
#include "Widgets/SCompoundWidget.h"

struct FSlateBrush;

// Where a dragged item comes from or is dropped: a tile of the inventory, a hotbar slot or a storage's contents
enum class EFWItemPlace : uint8 { Inventory, Hotbar, Storage };

// An item being dragged between tiles
class FFWItemDrag : public FDragDropOperation
{
public:
	DRAG_DROP_OPERATOR_TYPE(FFWItemDrag, FDragDropOperation)

	FString Item;
	int32 Quantity = 0;
	EFWItemPlace From = EFWItemPlace::Inventory;
	int32 Index = 0;
	const FSlateBrush* Icon = nullptr;

	static TSharedRef<FFWItemDrag> New(const FString& Item, int32 Quantity, EFWItemPlace From, int32 Index, const FSlateBrush* Icon);
	virtual TSharedPtr<SWidget> GetDefaultDecorator() const override;
};

DECLARE_DELEGATE_OneParam(FOnFWItemDropped, const FFWItemDrag& /*Dragged*/);
DECLARE_DELEGATE(FOnFWItemQuick);

/**
 * One tile of an item grid: the item's icon and count, dragged with the left button, dropped on another tile;
 * shift + click sends it straight across (inventory <-> chest).
 */
class SFWItemTile : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWItemTile) : _Size(72.f) {}
		SLATE_ATTRIBUTE(FString, Item)
		SLATE_ATTRIBUTE(int32, Quantity)
		SLATE_ATTRIBUTE(FText, Label)
		SLATE_ARGUMENT(EFWItemPlace, Place)
		SLATE_ARGUMENT(int32, Index)
		SLATE_ARGUMENT(float, Size)
		// The icon of an item (from the HUD's cache)
		SLATE_ARGUMENT(TFunction<const FSlateBrush*(const FString&)>, IconOf)
		SLATE_ARGUMENT(TFunction<FText(const FString&)>, NameOf)
		SLATE_EVENT(FOnFWItemDropped, OnDropped)
		SLATE_EVENT(FOnFWItemQuick, OnQuick)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual FReply OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnDragDetected(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual void OnDragEnter(const FGeometry& Geometry, const FDragDropEvent& Event) override;
	virtual void OnDragLeave(const FDragDropEvent& Event) override;
	virtual FReply OnDrop(const FGeometry& Geometry, const FDragDropEvent& Event) override;

private:
	TAttribute<FString> Item;
	TAttribute<int32> Quantity;
	EFWItemPlace Place = EFWItemPlace::Inventory;
	int32 Index = 0;
	TFunction<const FSlateBrush*(const FString&)> IconOf;
	TFunction<FText(const FString&)> NameOf;
	FOnFWItemDropped OnDropped;
	FOnFWItemQuick OnQuick;
	bool bHovered = false;
};
