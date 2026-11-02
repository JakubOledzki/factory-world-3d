// The backpack, the hotbar, storages, filters, shops and farms.
#include "FWPlayerController.h"
#include "Components/CapsuleComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/GameInstance.h"
#include "UI/SFWFarmWindow.h"
#include "Engine/GameViewportClient.h"
#include "Engine/StaticMeshActor.h"
#include "UI/SFWFilterWindow.h"
#include "GameFramework/Character.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "UI/SFWDeviceWindow.h"
#include "Misc/Paths.h"
#include "UnrealClient.h"
#include "UI/SFWShopWindow.h"
#include "EngineUtils.h"
#include "Game/FWCatalog.h"
#include "Game/FWGameSettings.h"
#include "UI/SFWGarageWindow.h"
#include "World/FWGarageRoom.h"
#include "Net/FWNetSubsystem.h"
#include "UI/SFWHud.h"
#include "AudioMixerBlueprintLibrary.h"
#include "UI/SFWFactoryWindow.h"
#include "Components/AudioComponent.h"
#include "Engine/StaticMesh.h"
#include "UI/SFWAdventuresWindow.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "UI/SFWCityWindow.h"
#include "UI/SFWCountryWindow.h"
#include "UI/SFWPoliceWindow.h"
#include "UI/SFWCharacterCreator.h"
#include "UI/SFWInventoryWindow.h"
#include "UI/SFWContractsWindow.h"
#include "UI/SFWResearchWindow.h"
#include "UI/SFWStatsWindow.h"
#include "UI/SFWSettingsWindow.h"
#include "UI/SFWMarketWindow.h"
#include "UI/SFWStationWindow.h"
#include "UI/SFWWorldMap.h"
#include "Engine/Texture2D.h"
#include "UI/SFWExportWindow.h"
#include "UI/SFWRailWindow.h"
#include "Camera/CameraComponent.h"
#include "UI/SFWBankWindow.h"
#include "UI/SFWModWindow.h"
#include "UI/SFWCompanyWindow.h"
#include "UI/SFWLoginWidget.h"
#include "World/FWCharacterStudio.h"
#include "World/FWTrains.h"
#include "Player/FWCharacter.h"
#include "UI/SFWPromptWidget.h"
#include "World/FWEnvironment.h"
#include "World/FWGrowth.h"
#include "World/FWPlot.h"
#include "UI/SFWAchievementsWindow.h"
#include "UI/SFWBadgeToast.h"
#include "UI/SFWChat.h"

#define LOCTEXT_NAMESPACE "FWGame"

#include "FWPlayerControllerHelpers.h"

void AFWPlayerController::RefreshInventory(TFunction<void()> OnDone)
{
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("inventory.get"), nullptr, [WeakThis, OnDone = MoveTemp(OnDone)](const FFWNetReply& Reply)
	{
		AFWPlayerController* This = WeakThis.Get();
		if (!This || Reply.Type != TEXT("inventory"))
		{
			if (This && OnDone)
			{
				OnDone();
			}
			return;
		}
		This->ApplyInventory(Reply.Payload);
		if (OnDone)
		{
			OnDone();
		}
		This->Net()->Request(TEXT("hotbar.get"), nullptr, [WeakThis](const FFWNetReply& Bar)
		{
			if (AFWPlayerController* Inner = WeakThis.Get())
			{
				Inner->ApplyHotbar(Bar.Payload);
			}
		});
	});
}

void AFWPlayerController::ApplyInventory(const TSharedPtr<FJsonObject>& InventoryPayload)
{
	const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
	if (!InventoryPayload.IsValid() || !InventoryPayload->TryGetArrayField(TEXT("items"), Items))
	{
		return;
	}
	InventoryPayload->TryGetNumberField(TEXT("slots"), InventorySlots);
	Inventory.Reset();
	for (const TSharedPtr<FJsonValue>& Value : *Items)
	{
		const TSharedPtr<FJsonObject> Item = Value->AsObject();
		FFWStack Stack;
		Item->TryGetStringField(TEXT("id"), Stack.Id);
		Item->TryGetStringField(TEXT("name"), Stack.Name);
		Item->TryGetNumberField(TEXT("quantity"), Stack.Quantity);
		Item->TryGetNumberField(TEXT("stack"), Stack.StackSize);
		const TArray<TSharedPtr<FJsonValue>>* Tiles = nullptr;
		if (Item->TryGetArrayField(TEXT("tiles"), Tiles))
		{
			for (const TSharedPtr<FJsonValue>& Tile : *Tiles)
			{
				Stack.Tiles.Add(int32(Tile->AsNumber()));
			}
		}
		int32 Slot = -1;
		if (Stack.Tiles.IsEmpty() && Item->TryGetNumberField(TEXT("slot"), Slot))
		{
			Stack.Tiles.Add(Slot);
		}
		Names.Add(Stack.Id, Stack.Name);
		Inventory.Add(Stack);
	}
}

void AFWPlayerController::ApplyStorage(const TSharedPtr<FJsonObject>& StoragePayload)
{
	if (!StoragePayload.IsValid())
	{
		return;
	}
	StoragePayload->TryGetStringField(TEXT("id"), Storage.Id);
	StoragePayload->TryGetStringField(TEXT("name"), Storage.Name);
	StoragePayload->TryGetNumberField(TEXT("slots"), Storage.Slots);
	StoragePayload->TryGetNumberField(TEXT("capacity"), Storage.Capacity);
	StoragePayload->TryGetNumberField(TEXT("stored"), Storage.Stored);
	Storage.Items.Reset();
	const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
	if (StoragePayload->TryGetArrayField(TEXT("items"), Items))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Items)
		{
			const TSharedPtr<FJsonObject> Item = Value->AsObject();
			FFWStack Stack;
			Item->TryGetStringField(TEXT("id"), Stack.Id);
			Item->TryGetStringField(TEXT("name"), Stack.Name);
			Item->TryGetNumberField(TEXT("quantity"), Stack.Quantity);
			Names.Add(Stack.Id, Stack.Name);
			Storage.Items.Add(Stack);
		}
	}
}

const FSlateBrush* AFWPlayerController::GetIconBrush(const FString& Item)
{
	if (Item.IsEmpty())
	{
		return nullptr;
	}
	if (const TSharedPtr<FSlateBrush>* Found = IconBrushes.Find(Item))
	{
		return Found->Get();
	}
	TSharedPtr<FSlateBrush> Brush;
	if (UTexture2D* Texture = GetIcon(Item))
	{
		Brush = MakeShared<FSlateBrush>();
		Brush->SetResourceObject(Texture);
		Brush->ImageSize = FVector2D(Texture->GetSizeX(), Texture->GetSizeY());
		IconTextures.Add(Texture);
	}
	IconBrushes.Add(Item, Brush);
	return Brush.Get();
}

void AFWPlayerController::OpenInventory(const FString& StorageId)
{
	// The backpack opens anywhere, in the garage too (without a plot); a chest is on a plot
	if (!StorageId.IsEmpty() && !Plot)
	{
		return;
	}
	Storage = FFWStorageView();
	Storage.Id = StorageId;
	TSharedRef<SFWInventoryWindow> Window = SNew(SFWInventoryWindow).Controller(this).Storage(StorageId);
	ShowWidget(Window, Window);
	RefreshInventory();
	if (!StorageId.IsEmpty())
	{
		FWAudio::Play2D(this, TEXT("chest_open"), 0.6f);
		TSharedPtr<FJsonObject> P = PlotPayload(Plot);
		P->SetStringField(TEXT("id"), StorageId);
		TWeakObjectPtr<AFWPlayerController> WeakThis(this);
		Net()->Request(TEXT("storage.get"), P, [WeakThis](const FFWNetReply& Reply)
		{
			if (AFWPlayerController* This = WeakThis.Get(); This && Reply.Type == TEXT("storage"))
			{
				This->ApplyStorage(Reply.Payload);
			}
			else if (This)
			{
				This->ToastError(Reply);
			}
		});
	}
}

bool AFWPlayerController::HasOwnFilter(const FString& Kind)
{
	return SFWFilterWindow::RoleOf(Kind) != SFWFilterWindow::ERole::None;
}

void AFWPlayerController::OpenFilter(const FString& ObjectId, bool bMachine)
{
	const FFWPlotObject* Found = Plot ? Plot->GetObjects().Find(ObjectId) : nullptr;
	SFWFilterWindow::ERole FilterRole = Found ? SFWFilterWindow::RoleOf(Found->Kind) : SFWFilterWindow::ERole::None;
	if (FilterRole == SFWFilterWindow::ERole::None)
	{
		FilterRole = bMachine ? SFWFilterWindow::ERole::Machine : SFWFilterWindow::ERole::Storage;
	}
	SAssignNew(FilterWindow, SFWFilterWindow).Controller(this).Object(ObjectId).Role(FilterRole);
	ShowWidget(FilterWindow.ToSharedRef(), FilterWindow);
}

bool AFWPlayerController::IsShop(const FString& Kind)
{
	return SFWShopWindow::ModeOf(Kind) != SFWShopWindow::EMode::None;
}

void AFWPlayerController::OpenShop(const FString& ObjectId, const FString& Kind)
{
	SAssignNew(Shop, SFWShopWindow).Controller(this).Object(ObjectId).Kind(Kind);
	ShowWidget(Shop.ToSharedRef(), Shop);
}

void AFWPlayerController::SetHotbarSlot(int32 Slot, const FString& Item)
{
	TSharedPtr<FJsonObject> P = Payload();
	P->SetNumberField(TEXT("slot"), Slot);
	if (Item.IsEmpty())
	{
		P->SetField(TEXT("item"), MakeShared<FJsonValueNull>());
	}
	else
	{
		P->SetStringField(TEXT("item"), Item);
	}
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(TEXT("hotbar.set"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			Reply.Type == TEXT("hotbar") ? This->ApplyHotbar(Reply.Payload) : This->ToastError(Reply);
		}
	});
}

void AFWPlayerController::OpenFactory()
{
	SAssignNew(Factory, SFWFactoryWindow).Controller(this);
	ShowWidget(Factory.ToSharedRef(), Factory);
}

void AFWPlayerController::StorageTransfer(bool bPut, const FString& Item, int32 Quantity)
{
	if (!Plot || Storage.Id.IsEmpty() || Item.IsEmpty() || Quantity <= 0)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = PlotPayload(Plot);
	P->SetStringField(TEXT("id"), Storage.Id);
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	TWeakObjectPtr<AFWPlayerController> WeakThis(this);
	Net()->Request(bPut ? TEXT("storage.put") : TEXT("storage.take"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWPlayerController* This = WeakThis.Get())
		{
			if (Reply.Type == TEXT("storage"))
			{
				This->ApplyStorage(Reply.Payload);
				This->RefreshInventory();
			}
			else
			{
				This->ToastError(Reply);
			}
		}
	});
}

bool AFWPlayerController::IsFarm(const FString& Kind)
{
	return SFWFarmWindow::ModeOf(Kind) != SFWFarmWindow::EMode::None;
}

void AFWPlayerController::OpenFarm(const FString& ObjectId, const FString& Kind)
{
	SAssignNew(Farm, SFWFarmWindow).Controller(this).Object(ObjectId).Kind(Kind);
	ShowWidget(Farm.ToSharedRef(), Farm);
}

void AFWPlayerController::StorageTransferAll(bool bPut)
{
	if (bPut)
	{
		PutAllInStorage(Storage.Id);
		return;
	}
	for (const FFWStack& Stack : TArray<FFWStack>(Storage.Items))
	{
		StorageTransfer(false, Stack.Id, Stack.Quantity);
	}
}

void AFWPlayerController::ApplyHotbar(const TSharedPtr<FJsonObject>& HotbarPayload)
{
	const TArray<TSharedPtr<FJsonValue>>* Slots = nullptr;
	if (!HotbarPayload.IsValid() || !HotbarPayload->TryGetArrayField(TEXT("slots"), Slots))
	{
		return;
	}
	bool bEmpty = true;
	for (int32 i = 0; i < HotbarSize; ++i)
	{
		Hotbar[i] = Slots->IsValidIndex(i) && !(*Slots)[i]->IsNull() ? (*Slots)[i]->AsString() : FString();
		bEmpty &= Hotbar[i].IsEmpty();
	}
	// A new player's hotbar is empty: put what can be built here on it once
	if (bEmpty && !bHotbarFilled && Plot)
	{
		bHotbarFilled = true;
		int32 Slot = 0;
		for (const FFWStack& Stack : Inventory)
		{
			if (Slot < HotbarSize && Plot->IsPlaceable(Stack.Id))
			{
				TSharedPtr<FJsonObject> P = Payload();
				P->SetNumberField(TEXT("slot"), Slot++);
				P->SetStringField(TEXT("item"), Stack.Id);
				TWeakObjectPtr<AFWPlayerController> WeakThis(this);
				Net()->Request(TEXT("hotbar.set"), P, [WeakThis](const FFWNetReply& Reply)
				{
					if (AFWPlayerController* This = WeakThis.Get(); This && Reply.Type == TEXT("hotbar"))
					{
						This->ApplyHotbar(Reply.Payload);
					}
				});
			}
		}
	}
}

const FFWStack* AFWPlayerController::FindStack(const FString& Item) const
{
	return Inventory.FindByPredicate([&](const FFWStack& Stack) { return Stack.Id == Item; });
}

FString AFWPlayerController::ItemName(const FString& Item) const
{
	// Names stay known after the last piece is used up
	const FString* Name = Names.Find(Item);
	return Name && !Name->IsEmpty() ? *Name : Item;
}

void AFWPlayerController::SelectSlot(int32 Slot)
{
	SelectedSlot = FMath::Clamp(Slot, 0, HotbarSize - 1);
}

UTexture2D* AFWPlayerController::GetIcon(const FString& Item) const
{
	const UStaticMesh* Mesh = Item.IsEmpty() ? nullptr : FWArt::ItemMesh(Item);
	if (!Mesh)
	{
		return nullptr;
	}
	const FString Name = FString::Printf(TEXT("icon_%s"), *Mesh->GetName());
	if (UTexture2D* Icon = FWArt::Texture(*FString::Printf(TEXT("/Game/External/Icons/%s.%s"), *Name, *Name)))
	{
		return Icon;
	}
	// A model without its icon rendered yet (Tools/blender/render_icons.py): the crate's, as before it had a model
	return FWArt::Texture(TEXT("/Game/External/Icons/icon_crate.icon_crate"));
}

#undef LOCTEXT_NAMESPACE
