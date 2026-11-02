// The furniture tab: the room's plan and placing furniture from the backpack.
#include "UI/SFWGarageWindow.h"
#include "Framework/Application/SlateApplication.h"
#include "Fonts/FontMeasure.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "Game/FWCatalog.h"
#include "Input/DragAndDrop.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWGarage"

#include "SFWGarageWindowHelpers.h"

void SFWGarageWindow::PlaceFurnitureAt(const FString& Kind, FIntPoint Cell, int32 Rotation)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("kind"), Kind);
	P->SetNumberField(TEXT("x"), Cell.X);
	P->SetNumberField(TEXT("y"), Cell.Y);
	P->SetNumberField(TEXT("rotation"), Rotation & 3);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("garage.place"), P, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("garage"))
		{
			// Off the floor or on something else: say so and keep it chosen for another try
			Inner->ToastError(Reply);
			return;
		}
		This->Placing.Reset();
		Inner->PlayUiSound(TEXT("build_place"));
		Inner->RefreshInventory();
		Inner->RefreshGarageRoom(Reply.Payload);
		This->ApplyGarage(Reply);
	});
}

void SFWGarageWindow::PlaceFurniture(const FString& Kind)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !Garage.IsValid())
	{
		return;
	}
	const FIntVector Cell = FWGarageText::FreeCell(Garage, Kind);
	if (Cell.X < 0)
	{
		PC->Toast(LOCTEXT("NoRoom", "W garażu nie ma na to miejsca: schowaj coś do ekwipunku."), true);
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("kind"), Kind);
	P->SetNumberField(TEXT("x"), Cell.X);
	P->SetNumberField(TEXT("y"), Cell.Y);
	P->SetNumberField(TEXT("rotation"), Cell.Z);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("garage.place"), P, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		if (AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr)
		{
			if (Reply.Type == TEXT("garage"))
			{
				Inner->PlayUiSound(TEXT("build_place"));
				Inner->RefreshInventory();
			}
			This->ApplyGarage(Reply);
		}
	});
}

TSharedRef<SWidget> SFWGarageWindow::MakeFurniture()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Room = FWUi::Obj(Garage, TEXT("room"));
	const FIntPoint Size(int32(FWUi::Num(Room, TEXT("width"), 16.0)), int32(FWUi::Num(Room, TEXT("depth"), 12.0)));
	const TArray<FWGarageText::FPiece> Pieces = FWGarageText::Pieces(Garage);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Garaż z góry: %s × %s m."), *FString::SanitizeFloat(Size.X * FWUi::Num(Room, TEXT("cellSize"), 0.5)),
		*FString::SanitizeFloat(Size.Y * FWUi::Num(Room, TEXT("cellSize"), 0.5)))), FWStyle::Muted), 0.f);
	Add(SNew(SBox).HAlign(HAlign_Center)
		[
			SNew(FWGarageText::SRoom).Room(Size).Pieces(Pieces).Floor(int32(FWUi::Num(FWUi::Obj(Garage, TEXT("upgrades")), TEXT("floor"))))
			.OnCell([WeakThis](FIntPoint Cell)
			{
				TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
				if (This && !This->Placing.IsEmpty())
				{
					This->PlaceFurnitureAt(This->Placing, Cell, This->PlacingRotation);
				}
			})
		], 10.f);

	// From the backpack into the room
	const TSharedPtr<FJsonObject> Furniture = FWUi::Obj(Garage, TEXT("furniture"));
	bool bAny = false;
	TSharedRef<SVerticalBox> Bag = SNew(SVerticalBox);
	for (const FFWStack& Stack : PC ? PC->GetInventory() : TArray<FFWStack>())
	{
		if (!Furniture.IsValid() || !Furniture->HasField(Stack.Id))
		{
			continue;
		}
		bAny = true;
		const FString Kind = Stack.Id;
		Bag->AddSlot().AutoHeight().Padding(0, 3)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Kind, 30.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *Stack.Name, Stack.Quantity)))
			]
			// Carried in the room with its ghost at the crosshair, or put on a cell of the plan above
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
			[
				PC && PC->IsInGarage()
					? FWUi::SmallButton(LOCTEXT("Carry", "Postaw w garażu"), [WeakThis, Kind]()
					{
						TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
						if (AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr)
						{
							Inner->CarryToGarage(Kind);
						}
					}, true)
					: SNullWidget::NullWidget
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(Placing == Kind ? LOCTEXT("Choosing", "Kliknij miejsce na planie") : LOCTEXT("Place", "Na planie"), [WeakThis, Kind]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->Placing = This->Placing == Kind ? FString() : Kind;
						This->Rebuild();
					}
				}, !PC || !PC->IsInGarage())
			]
		];
	}
	Add(FWUi::Heading(LOCTEXT("FromBag", "Z plecaka")), 16.f);
	if (!Placing.IsEmpty())
	{
		static const TCHAR* Facing[] = { TEXT("przodem do bramy"), TEXT("przodem w prawo"), TEXT("przodem do tylnej ściany"), TEXT("przodem w lewo") };
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				FWUi::Note(FText::FromString(FString::Printf(TEXT("Kliknij na planie, gdzie postawić: %s (%s)."), PC ? *PC->ItemName(Placing) : TEXT(""),
					Facing[PlacingRotation & 3])), FWStyle::Accent)
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)
			[
				FWUi::SmallButton(LOCTEXT("Turn", "Obróć"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->PlacingRotation = (This->PlacingRotation + 1) % 4;
						This->Rebuild();
					}
				})
			], 4.f);
	}
	Add(bAny ? StaticCastSharedRef<SWidget>(Bag) : FWUi::Note(LOCTEXT("NoFurniture", "W plecaku nie ma mebli do garażu."), FWStyle::Muted), 4.f);

	// In the room
	Add(FWUi::Heading(LOCTEXT("InRoom", "W garażu")), 16.f);
	for (const FWGarageText::FPiece& Piece : Pieces)
	{
		const FString Id = Piece.Id;
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Piece.Kind, 26.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(Piece.Name))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("PickUp", "Schowaj do ekwipunku"), [WeakThis, Id]()
				{
					TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
					AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
					if (!Inner)
					{
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("id"), Id);
					Inner->Send(TEXT("garage.pickup"), P, [WeakThis](const FFWNetReply& Reply)
					{
						TSharedPtr<SFWGarageWindow> Again = WeakThis.Pin();
						if (AFWPlayerController* Owner = Again ? Again->Controller.Get() : nullptr)
						{
							if (Reply.Type == TEXT("garage"))
							{
								Owner->PlayUiSound(TEXT("build_pickup"));
								Owner->RefreshInventory();
							}
							Again->ApplyGarage(Reply);
						}
					});
				})
			], 3.f);
	}
	return Page;
}

#undef LOCTEXT_NAMESPACE
