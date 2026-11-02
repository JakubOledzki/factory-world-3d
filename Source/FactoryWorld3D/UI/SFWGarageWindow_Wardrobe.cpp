// The wardrobe: outfits, wearing and hanging up clothes.
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

void SFWGarageWindow::SendWardrobe(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetStringField(TEXT("id"), FurnitureId(TEXT("furniture_wardrobe")));
	const bool bChange = FCString::Strcmp(Type, TEXT("wardrobe.get")) != 0;
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, bChange](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("wardrobe"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->Wardrobe = Reply.Payload;
		if (bChange)
		{
			// Clothes went between the wardrobe, the backpack and the character
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
			Inner->RefreshLook();
		}
		This->Rebuild();
	});
}

void SFWGarageWindow::WearOutfit(int32 Number, bool bSave)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("number"), Number);
	if (bSave)
	{
		const FString Name = Form->Get(FString::Printf(TEXT("outfit%d"), Number)).TrimStartAndEnd();
		P->SetStringField(TEXT("name"), Name.IsEmpty() ? FString::Printf(TEXT("Komplet %d"), Number) : Name);
	}
	SendWardrobe(bSave ? TEXT("wardrobe.save") : TEXT("wardrobe.wear"), P);
}

void SFWGarageWindow::HangUp(const FString& Item)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), 1);
	SendWardrobe(TEXT("wardrobe.put"), P);
}

TSharedRef<SWidget> SFWGarageWindow::MakeWardrobe()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (FurnitureId(TEXT("furniture_wardrobe")).IsEmpty())
	{
		const bool bHave = Owned(TEXT("furniture_wardrobe")) > 0;
		TSharedRef<SVerticalBox> Page = SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				FWUi::Note(bHave ? LOCTEXT("WardrobeInBag", "Szafa na ubrania jest w plecaku: postaw ją w garażu, a zapiszesz w niej komplety ubrań.")
					: LOCTEXT("NoWardrobe", "W garażu nie ma szafy na ubrania. Zrobisz ją albo kupisz i postawisz tutaj."), FWStyle::Text)
			];
		if (bHave)
		{
			Page->AddSlot().AutoHeight().Padding(0, 10, 0, 0).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("PlaceWardrobe", "Postaw w garażu"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->PlaceFurniture(TEXT("furniture_wardrobe"));
					}
				}, true)
			];
		}
		return Page;
	}
	if (!Wardrobe.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	auto Piece = [PC](const TSharedPtr<FJsonObject>& Pieces, int32 Slot, float Size) -> TSharedRef<SWidget>
	{
		const TSharedPtr<FJsonObject> Item = FWUi::Obj(Pieces, FWGarageText::Slots[Slot]);
		return SNew(SBox).WidthOverride(Size + 6.f).HeightOverride(Size + 6.f).HAlign(HAlign_Center).VAlign(VAlign_Center)
			.ToolTipText(FText::FromString(FString::Printf(TEXT("%s: %s"), FWGarageText::SlotNames[Slot], Item.IsValid() ? *FWUi::Str(Item, TEXT("name")) : TEXT("nic"))))
			[
				Item.IsValid() ? FWUi::Icon(PC, FWUi::Str(Item, TEXT("id")), Size)
					: StaticCastSharedRef<SWidget>(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(0.f)[SNew(SBox).WidthOverride(Size).HeightOverride(Size)])
			];
	};
	Add(FWUi::Heading(LOCTEXT("Outfits", "Komplety")), 0.f);
	for (const TSharedPtr<FJsonObject>& Outfit : FWUi::Objects(Wardrobe, TEXT("outfits")))
	{
		const int32 Number = int32(FWUi::Num(Outfit, TEXT("number")));
		const bool bSaved = FWUi::Bool(Outfit, TEXT("saved"));
		const FString Key = FString::Printf(TEXT("outfit%d"), Number);
		if (!Form->Has(Key) && bSaved)
		{
			Form->Set(Key, FWUi::Str(Outfit, TEXT("name")));
		}
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				Form->Field(Key, FText::FromString(FString::Printf(TEXT("Komplet %d"), Number)), 180.f)
			];
		const TSharedPtr<FJsonObject> Pieces = FWUi::Obj(Outfit, TEXT("pieces"));
		for (int32 Slot = 0; Slot < 4; ++Slot)
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)[Piece(Pieces, Slot, 30.f)];
		}
		Row->AddSlot().FillWidth(1.f);
		Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(LOCTEXT("Wear", "Załóż"), [WeakThis, Number]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->WearOutfit(Number, false); } }, true, bSaved)
		];
		Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(LOCTEXT("SaveOutfit", "Zapisz to, co mam na sobie"), [WeakThis, Number]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->WearOutfit(Number, true); } })
		];
		if (bSaved)
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
			[
				FWUi::SmallButton(LOCTEXT("Forget", "Usuń"), [WeakThis, Number]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("number"), Number);
						This->Form->Set(FString::Printf(TEXT("outfit%d"), Number), FString());
						This->SendWardrobe(TEXT("wardrobe.forget"), P);
					}
				})
			];
		}
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(8.f)[Row], 6.f);
	}
	Add(FWUi::Note(LOCTEXT("OutfitsHint", "Zapisz, co masz na sobie, a potem przebieraj się jednym kliknięciem. Ubrania bierze z szafy albo z ekwipunku, a zdjęte wiesza w szafie."), FWStyle::Muted), 6.f);

	Add(FWUi::Heading(LOCTEXT("Worn", "Na sobie")), 16.f);
	TSharedRef<SHorizontalBox> Worn = SNew(SHorizontalBox);
	for (int32 Slot = 0; Slot < 4; ++Slot)
	{
		Worn->AddSlot().AutoWidth().Padding(0, 0, 8, 0)[Piece(FWUi::Obj(Wardrobe, TEXT("worn")), Slot, 40.f)];
	}
	Add(Worn, 4.f);

	Add(FWUi::Heading(LOCTEXT("Stored", "W szafie")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Stored = FWUi::Objects(Wardrobe, TEXT("stored"));
	if (Stored.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("WardrobeEmpty", "Pusto."), FWStyle::Muted), 4.f);
	}
	for (const TSharedPtr<FJsonObject>& Stack : Stored)
	{
		const FString Id = FWUi::Str(Stack, TEXT("id"));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 28.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity"))))))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("TakeOut", "Wyjmij"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), Id);
						P->SetNumberField(TEXT("quantity"), 1);
						This->SendWardrobe(TEXT("wardrobe.take"), P);
					}
				})
			], 4.f);
	}

	Add(FWUi::Heading(LOCTEXT("Hang", "Powieś z ekwipunku")), 16.f);
	bool bAny = false;
	for (const FFWStack& Stack : PC ? PC->GetInventory() : TArray<FFWStack>())
	{
		if (!Stack.Id.StartsWith(TEXT("clothes_")))
		{
			continue;
		}
		bAny = true;
		const FString Id = Stack.Id;
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 28.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *Stack.Name, Stack.Quantity)))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("HangUp", "Powieś"), [WeakThis, Id]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->HangUp(Id); } })
			], 4.f);
	}
	if (!bAny)
	{
		Add(FWUi::Note(LOCTEXT("NoClothes", "W ekwipunku nie ma ubrań."), FWStyle::Muted), 4.f);
	}
	return Page;
}

#undef LOCTEXT_NAMESPACE
