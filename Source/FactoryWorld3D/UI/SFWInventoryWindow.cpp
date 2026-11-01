#include "UI/SFWInventoryWindow.h"

#include "FWPlayerController.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "UI/SFWItemTile.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SUniformGridPanel.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWInventory"

namespace
{
	constexpr int32 Columns = 8;
	constexpr float TileSize = 68.f;
}

void SFWInventoryWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Storage = InArgs._Storage;

	TSharedRef<SHorizontalBox> Panels = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth()[MakeInventoryPanel()];
	if (!Storage.IsEmpty())
	{
		Panels->AddSlot().AutoWidth().Padding(24.f, 0.f, 0.f, 0.f)[MakeStoragePanel()];
	}

	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot()
		[
			SNew(SBackgroundBlur).BlurStrength(4.f)
		]
		+ SOverlay::Slot()
		[
			SNew(SBorder).BorderImage(FWStyle::SideShade())
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			Panels
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0.f, 0.f, 0.f, 32.f)
		[
			SNew(STextBlock)
			.Font(FWStyle::Font(11))
			.ColorAndOpacity(FWStyle::Muted)
			.Text(Storage.IsEmpty()
				? LOCTEXT("Hints", "Przeciągnij, aby przenieść  ·  na pasek: przypisz  ·  Tab / Esc zamyka")
				: LOCTEXT("ChestHints", "Przeciągnij między plecakiem a skrzynią  ·  Shift + klik: cały stos na drugą stronę  ·  E / Esc zamyka"))
		]
	];
	RebuildGrids();
}

TSharedRef<SWidget> SFWInventoryWindow::MakeInventoryPanel()
{
	TSharedRef<SHorizontalBox> Hotbar = SNew(SHorizontalBox);
	for (int32 Slot = 0; Slot < AFWPlayerController::HotbarSize; ++Slot)
	{
		Hotbar->AddSlot().AutoWidth().Padding(3.f)
		[
			SNew(SFWItemTile)
			.Size(TileSize)
			.Place(EFWItemPlace::Hotbar)
			.Index(Slot)
			.Label(FText::AsNumber(Slot + 1))
			.Item_Lambda([this, Slot]() { return Controller.IsValid() && Controller->GetHotbar().IsValidIndex(Slot) ? Controller->GetHotbar()[Slot] : FString(); })
			.Quantity_Lambda([this, Slot]()
			{
				const FFWStack* Stack = Controller.IsValid() && Controller->GetHotbar().IsValidIndex(Slot) ? Controller->FindStack(Controller->GetHotbar()[Slot]) : nullptr;
				return Stack ? Stack->Quantity : 0;
			})
			.IconOf([this](const FString& Id) { return Controller.IsValid() ? Controller->GetIconBrush(Id) : nullptr; })
			.NameOf([this](const FString& Id) { return Controller.IsValid() ? FText::FromString(Controller->ItemName(Id)) : FText::GetEmpty(); })
			.OnDropped_Lambda([this, Slot](const FFWItemDrag& Drag) { DropOnHotbar(Drag, Slot); })
		];
	}
	return SNew(SBorder)
		.BorderImage(FWStyle::Panel())
		.Padding(22.f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 12)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)
				[
					SNew(STextBlock).Text(LOCTEXT("Inventory", "Plecak")).Font(FWStyle::Font(20, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Bottom)
				[
					SNew(STextBlock)
					.Font(FWStyle::Font(11))
					.ColorAndOpacity(FWStyle::Muted)
					.Text_Lambda([this]()
					{
						if (!Controller.IsValid())
						{
							return FText::GetEmpty();
						}
						int32 Used = 0;
						for (const FFWStack& Stack : Controller->GetInventory())
						{
							Used += Stack.Tiles.Num();
						}
						return FText::Format(LOCTEXT("Used", "{0} / {1} pól"), Used, Controller->GetInventorySlots());
					})
				]
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SAssignNew(InventoryGrid, SUniformGridPanel).SlotPadding(FMargin(3.f))
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 16, 0, 6)
			[
				SNew(STextBlock).Text(LOCTEXT("Hotbar", "Pasek szybkiego wyboru")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				Hotbar
			]
		];
}

TSharedRef<SWidget> SFWInventoryWindow::MakeStoragePanel()
{
	auto Move = [this](bool bPut)
	{
		return [this, bPut]()
		{
			if (AFWPlayerController* PC = Controller.Get())
			{
				PC->StorageTransferAll(bPut);
			}
			return FReply::Handled();
		};
	};
	return SNew(SBorder)
		.BorderImage(FWStyle::Panel())
		.Padding(22.f)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 12)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)
				[
					SNew(STextBlock)
					.Font(FWStyle::Font(20, TEXT("Bold")))
					.ColorAndOpacity(FWStyle::Text)
					.Text_Lambda([this]() { return Controller.IsValid() ? FText::FromString(Controller->GetStorage().Name) : FText::GetEmpty(); })
				]
				// The one item it lets in, as the "Filtr" button of the 2D game
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 14, 0)
				[
					FWUi::SmallButton(LOCTEXT("Filter", "Filtr"), [this]()
					{
						if (AFWPlayerController* PC = Controller.Get(); PC && !PC->GetStorage().Id.IsEmpty())
						{
							PC->OpenFilter(PC->GetStorage().Id);
						}
					})
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Bottom)
				[
					SNew(STextBlock)
					.Font(FWStyle::Font(11))
					.ColorAndOpacity(FWStyle::Muted)
					.Text_Lambda([this]()
					{
						if (!Controller.IsValid())
						{
							return FText::GetEmpty();
						}
						const FFWStorageView& View = Controller->GetStorage();
						return FText::Format(LOCTEXT("Stored", "{0} / {1} szt."), View.Stored, View.Capacity);
					})
				]
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(SBorder)
				.BorderImage(FWStyle::SideShade())
				.Padding(0.f)
				[
					SAssignNew(StorageGrid, SUniformGridPanel).SlotPadding(FMargin(3.f))
				]
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 16, 0, 0)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)
				[
					SNew(SButton).ButtonStyle(&FWStyle::SecondaryButton()).HAlign(HAlign_Center).OnClicked_Lambda(Move(true))
					[
						SNew(STextBlock).Text(LOCTEXT("PutAll", "Włóż wszystko")).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)
					]
				]
				+ SHorizontalBox::Slot().FillWidth(1.f).Padding(6, 0, 0, 0)
				[
					SNew(SButton).ButtonStyle(&FWStyle::PrimaryButton()).HAlign(HAlign_Center).OnClicked_Lambda(Move(false))
					[
						SNew(STextBlock).Text(LOCTEXT("TakeAll", "Weź wszystko")).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FLinearColor::White)
					]
				]
			]
		];
}

void SFWInventoryWindow::RebuildGrids()
{
	const AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	// The grid has a tile per server tile; what lies on each is looked up when drawn
	const int32 Tiles = FMath::Max(PC->GetInventorySlots(), Columns);
	if (Tiles != BuiltTiles && InventoryGrid.IsValid())
	{
		BuiltTiles = Tiles;
		InventoryGrid->ClearChildren();
		for (int32 Tile = 0; Tile < Tiles; ++Tile)
		{
			auto Find = [this, Tile]() -> const FFWStack*
			{
				return Controller.IsValid() ? Controller->GetInventory().FindByPredicate([Tile](const FFWStack& S) { return S.Tiles.Contains(Tile); }) : nullptr;
			};
			InventoryGrid->AddSlot(Tile % Columns, Tile / Columns)
			[
				SNew(SFWItemTile)
				.Size(TileSize)
				.Place(EFWItemPlace::Inventory)
				.Index(Tile)
				.Item_Lambda([Find]() { const FFWStack* S = Find(); return S ? S->Id : FString(); })
				.Quantity_Lambda([Find, Tile]()
				{
					// What lies on this tile: full stacks first, the rest on the last tile
					const FFWStack* S = Find();
					if (!S)
					{
						return 0;
					}
					const int32 Size = FMath::Max(1, S->StackSize);
					const int32 Order = S->Tiles.IndexOfByKey(Tile);
					return FMath::Clamp(S->Quantity - Order * Size, 0, Size);
				})
				.IconOf([this](const FString& Id) { return Controller.IsValid() ? Controller->GetIconBrush(Id) : nullptr; })
				.NameOf([this](const FString& Id) { return Controller.IsValid() ? FText::FromString(Controller->ItemName(Id)) : FText::GetEmpty(); })
				.OnDropped_Lambda([this, Tile](const FFWItemDrag& Drag) { DropOnInventory(Drag, Tile); })
				.OnQuick_Lambda([this, Find]()
				{
					const FFWStack* S = Find();
					if (S && !Storage.IsEmpty() && Controller.IsValid())
					{
						Controller->StorageTransfer(true, S->Id, S->Quantity);
					}
				})
			];
		}
	}
	if (!Storage.IsEmpty() && StorageGrid.IsValid())
	{
		const int32 StorageTiles = FMath::Clamp(PC->GetStorage().Slots, 1, 48);
		if (StorageTiles != BuiltStorageTiles)
		{
			BuiltStorageTiles = StorageTiles;
			StorageGrid->ClearChildren();
			for (int32 Tile = 0; Tile < StorageTiles; ++Tile)
			{
				auto Find = [this, Tile]() -> const FFWStack*
				{
					return Controller.IsValid() && Controller->GetStorage().Items.IsValidIndex(Tile) ? &Controller->GetStorage().Items[Tile] : nullptr;
				};
				StorageGrid->AddSlot(Tile % 6, Tile / 6)
				[
					SNew(SFWItemTile)
					.Size(TileSize)
					.Place(EFWItemPlace::Storage)
					.Index(Tile)
					.Item_Lambda([Find]() { const FFWStack* S = Find(); return S ? S->Id : FString(); })
					.Quantity_Lambda([Find]() { const FFWStack* S = Find(); return S ? S->Quantity : 0; })
					.IconOf([this](const FString& Id) { return Controller.IsValid() ? Controller->GetIconBrush(Id) : nullptr; })
					.NameOf([this](const FString& Id) { return Controller.IsValid() ? FText::FromString(Controller->ItemName(Id)) : FText::GetEmpty(); })
					.OnDropped_Lambda([this](const FFWItemDrag& Drag) { DropOnStorage(Drag); })
					.OnQuick_Lambda([this, Find]()
					{
						const FFWStack* S = Find();
						if (S && Controller.IsValid())
						{
							Controller->StorageTransfer(false, S->Id, S->Quantity);
						}
					})
				];
			}
		}
	}
}

void SFWInventoryWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	RebuildGrids();
}

void SFWInventoryWindow::DropOnInventory(const FFWItemDrag& Drag, int32 Tile)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	if (Drag.From == EFWItemPlace::Inventory && Drag.Index != Tile)
	{
		PC->MoveTile(Drag.Index, Tile);
	}
	else if (Drag.From == EFWItemPlace::Storage)
	{
		PC->StorageTransfer(false, Drag.Item, Drag.Quantity);
	}
	else if (Drag.From == EFWItemPlace::Hotbar)
	{
		// Dragged off the bar: the slot is emptied
		PC->SetHotbarSlot(Drag.Index, FString());
	}
}

void SFWInventoryWindow::DropOnHotbar(const FFWItemDrag& Drag, int32 Slot)
{
	if (AFWPlayerController* PC = Controller.Get(); PC && Drag.From != EFWItemPlace::Storage)
	{
		PC->SetHotbarSlot(Slot, Drag.Item);
		if (Drag.From == EFWItemPlace::Hotbar && Drag.Index != Slot)
		{
			PC->SetHotbarSlot(Drag.Index, FString());
		}
	}
}

void SFWInventoryWindow::DropOnStorage(const FFWItemDrag& Drag)
{
	if (AFWPlayerController* PC = Controller.Get(); PC && Drag.From == EFWItemPlace::Inventory)
	{
		PC->StorageTransfer(true, Drag.Item, Drag.Quantity);
	}
}

FReply SFWInventoryWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	const FKey Key = Event.GetKey();
	if (Key == EKeys::Escape || Key == EKeys::Tab || Key == EKeys::I || Key == EKeys::E)
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->CloseWindow();
		}
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

#undef LOCTEXT_NAMESPACE
