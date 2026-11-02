// Console commands for tests (UFUNCTION(Exec)): driving the windows as a player would.
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

void AFWPlayerController::FWCreatorPick(const FString& Key, int32 Index)
{
	if (Creator.IsValid())
	{
		Creator->Pick(Key, Index);
	}
}

void AFWPlayerController::FWCreatorName(const FString& Name)
{
	if (Creator.IsValid())
	{
		Creator->SetName(Name);
	}
}

void AFWPlayerController::FWCreatorRandom()
{
	if (Creator.IsValid())
	{
		Creator->Randomize();
	}
}

void AFWPlayerController::FWCreatorView(float Zoom, float Yaw)
{
	if (Studio)
	{
		Studio->SetZoom(Zoom);
		Studio->Spin(Yaw);
	}
}

void AFWPlayerController::FWCreatorCreate()
{
	if (Creator.IsValid())
	{
		Creator->Submit();
	}
}

void AFWPlayerController::FWCity(int32 Tab)
{
	if (!City.IsValid())
	{
		OpenCity();
	}
	City->ShowTab(Tab);
}

void AFWPlayerController::FWCountry(int32 Tab, const FString& Code)
{
	if (!Country.IsValid())
	{
		OpenCountry();
	}
	if (!Code.IsEmpty())
	{
		Country->ShowCountry(Code);
	}
	Country->ShowTab(Tab);
}

void AFWPlayerController::FWPolice(int32 Tab)
{
	if (!Police.IsValid())
	{
		OpenPolice();
	}
	Police->ShowTab(Tab);
}

void AFWPlayerController::FWRail(int32 Tab)
{
	if (!Rail.IsValid())
	{
		OpenRail();
	}
	Rail->ShowTab(Tab);
}

void AFWPlayerController::FWRailStock(int32 Load, const FString& Item, int32 Quantity)
{
	if (!Rail.IsValid())
	{
		OpenRail();
	}
	Rail->StockAction(Load != 0, Item, Quantity);
}

void AFWPlayerController::FWExport(const FString& PostId)
{
	OpenExport(PostId);
}

void AFWPlayerController::FWDevice(const FString& Kind)
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWPlotObject* Nearest = nullptr;
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (Pair.Value.Kind == Kind && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
		{
			Nearest = &Pair.Value;
		}
	}
	if (!Nearest)
	{
		UE_LOG(LogFWGame, Warning, TEXT("No %s on the plot"), *Kind);
		return;
	}
	FWGoto(Nearest->Tile.X - 2, Nearest->Tile.Y + 2, -45.f, -25.f);
	OpenDevice(Nearest->Id, Nearest->Kind);
}

void AFWPlayerController::FWResearchPick(const FString& Technology)
{
	if (Research.IsValid())
	{
		Research->Choose(Technology);
	}
}

void AFWPlayerController::FWResearchStart()
{
	if (Research.IsValid())
	{
		Research->Start();
	}
}

void AFWPlayerController::FWContracts(int32 Tab)
{
	if (!Contracts.IsValid())
	{
		OpenContracts();
	}
	Contracts->ShowTab(Tab);
}

void AFWPlayerController::FWContractFirst(const FString& Action, const FString& List, int32 Quantity)
{
	const int64 Id = Contracts.IsValid() ? Contracts->FirstId(*List) : 0;
	if (Id == 0)
	{
		UE_LOG(LogFWGame, Warning, TEXT("No contract on %s"), *List);
		return;
	}
	TSharedPtr<FJsonObject> P = Payload();
	P->SetNumberField(TEXT("id"), double(Id));
	if (Action == TEXT("deliver"))
	{
		P->SetNumberField(TEXT("quantity"), Quantity);
	}
	Contracts->Act(*(TEXT("contracts.") + Action), P);
}

void AFWPlayerController::FWContractCreate(const FString& Item, int32 Quantity, float RewardDollars, int32 Hours)
{
	if (Contracts.IsValid())
	{
		Contracts->Create(Item, Quantity, RewardDollars * 100.0, Hours);
	}
}

void AFWPlayerController::FWStats(int32 Minutes, const FString& Item)
{
	if (!Stats.IsValid())
	{
		OpenStats();
	}
	Stats->SetPeriod(Minutes);
	if (!Item.IsEmpty())
	{
		Stats->SelectItem(Item);
	}
}

void AFWPlayerController::FWMapCenter(float X, float Y, float Zoom)
{
	if (WorldMap.IsValid())
	{
		WorldMap->CenterOn(FVector2D(X, Y), Zoom);
	}
}

void AFWPlayerController::FWMapSelect(int32 X, int32 Y)
{
	if (WorldMap.IsValid())
	{
		WorldMap->Select(FIntPoint(X, Y));
	}
}

void AFWPlayerController::FWMarketTab(int32 Tab)
{
	if (Market.IsValid())
	{
		Market->ShowTab(Tab);
	}
}

void AFWPlayerController::FWMarketItem(const FString& Item)
{
	if (Market.IsValid())
	{
		Market->OpenItem(Item);
	}
}

void AFWPlayerController::FWMarketOffer(int32 Sell, int32 Quantity, float PriceDollars)
{
	if (Market.IsValid())
	{
		Market->PutOffer(Sell != 0, Quantity, PriceDollars * 100.0);
	}
}

void AFWPlayerController::FWTrade(int32 Buy, const FString& Item, int32 Quantity)
{
	if (Market.IsValid())
	{
		Market->Trade(Buy != 0, Item, Quantity);
	}
}

void AFWPlayerController::FWSay(const FString& InChannel, const FString& Text)
{
	if (Chat.IsValid())
	{
		Chat->SetChannel(InChannel);
		Chat->Say(Text);
	}
}

void AFWPlayerController::FWAchievements(int32 Tab)
{
	if (!Achievements.IsValid())
	{
		OpenAchievements();
	}
	Achievements->ShowTab(Tab);
}

void AFWPlayerController::FWRanking(const FString& Ranking)
{
	if (!Achievements.IsValid())
	{
		OpenAchievements();
	}
	Achievements->ShowRanking(Ranking);
}

void AFWPlayerController::FWBadgeTest(const FString& Name)
{
	if (BadgeToast.IsValid())
	{
		TSharedPtr<FJsonObject> Badge = MakeShared<FJsonObject>();
		Badge->SetStringField(TEXT("name"), Name);
		Badge->SetStringField(TEXT("description"), TEXT("Przykładowa odznaka do sprawdzenia karty"));
		BadgeToast->Show(Badge);
	}
}

void AFWPlayerController::FWFilter(const FString& Kind, const FString& Item, int32 Side)
{
	if (!FilterWindow.IsValid() || ShownWidget != FilterWindow)
	{
		if (!Plot || !GetPawn())
		{
			return;
		}
		const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
		const FFWPlotObject* Nearest = nullptr;
		for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
		{
			if (Pair.Value.Kind == Kind && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
			{
				Nearest = &Pair.Value;
			}
		}
		if (!Nearest)
		{
			UE_LOG(LogFWGame, Warning, TEXT("No %s on the plot"), *Kind);
			return;
		}
		FWGoto(Nearest->Tile.X - 2, Nearest->Tile.Y + 2, -45.f, -25.f);
		OpenFilter(Nearest->Id, FWCatalog::StationOf(Kind) >= 0);
	}
	else if (!Item.IsEmpty() || Side >= 0)
	{
		FilterWindow->Choose(Item == TEXT("-") ? FString() : Item, Side);
	}
}

void AFWPlayerController::FWShop(const FString& Kind, const FString& Item, int32 Quantity, float Dollars)
{
	if (Shop.IsValid() && ShownWidget == Shop && !Item.IsEmpty())
	{
		Shop->Stock(Item, Quantity, Dollars);
		return;
	}
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWPlotObject* Nearest = nullptr;
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (Pair.Value.Kind == Kind && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
		{
			Nearest = &Pair.Value;
		}
	}
	if (!Nearest)
	{
		UE_LOG(LogFWGame, Warning, TEXT("No %s on the plot"), *Kind);
		return;
	}
	FWGoto(Nearest->Tile.X - 3, Nearest->Tile.Y + 3, -45.f, -25.f);
	OpenShop(Nearest->Id, Nearest->Kind);
}

void AFWPlayerController::FWFactory(int32 Tab)
{
	if (!Factory.IsValid() || ShownWidget != Factory)
	{
		OpenFactory();
	}
	Factory->ShowTab(Tab);
}

void AFWPlayerController::FWPlanSave(const FString& Name, int32 Radius)
{
	if (Factory.IsValid() && ShownWidget == Factory)
	{
		Factory->SavePlan(Name, Radius);
	}
}

void AFWPlayerController::FWPlanUse(int32 Index)
{
	if (Factory.IsValid() && ShownWidget == Factory)
	{
		Factory->UsePlan(Index);
	}
}

void AFWPlayerController::FWFarm(const FString& Kind)
{
	if (!Plot || !GetPawn())
	{
		return;
	}
	const FIntPoint At = Plot->TileAt(GetPawn()->GetActorLocation());
	const FFWPlotObject* Nearest = nullptr;
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		if (Pair.Value.Kind == Kind && (!Nearest || (Pair.Value.Tile - At).SizeSquared() < (Nearest->Tile - At).SizeSquared()))
		{
			Nearest = &Pair.Value;
		}
	}
	if (!Nearest)
	{
		UE_LOG(LogFWGame, Warning, TEXT("No %s on the plot"), *Kind);
		return;
	}
	FWGoto(Nearest->Tile.X - 3, Nearest->Tile.Y + 3, -45.f, -25.f);
	OpenFarm(Nearest->Id, Nearest->Kind);
}

void AFWPlayerController::FWGarage(int32 Tab, int32 AtBin)
{
	if (!GarageWindow.IsValid() || ShownWidget != GarageWindow)
	{
		OpenGarage(AtBin != 0);
	}
	GarageWindow->ShowTab(Tab);
}

void AFWPlayerController::FWGarageSort()
{
	if (GarageWindow.IsValid() && ShownWidget == GarageWindow)
	{
		GarageWindow->ToggleSorting();
	}
}

void AFWPlayerController::FWGarageSortPut(int32 Piece, const FString& Bin)
{
	if (GarageWindow.IsValid() && ShownWidget == GarageWindow)
	{
		GarageWindow->PutPiece(Piece, Bin);
	}
}

void AFWPlayerController::FWGaragePlace(const FString& Kind)
{
	if (GarageWindow.IsValid() && ShownWidget == GarageWindow)
	{
		GarageWindow->PlaceFurniture(Kind);
	}
}

void AFWPlayerController::FWGarageSorter(int32 Quantity)
{
	if (GarageWindow.IsValid() && ShownWidget == GarageWindow)
	{
		GarageWindow->LoadSorter(Quantity);
	}
}

void AFWPlayerController::FWGarageOutfit(int32 Number, int32 Save)
{
	if (GarageWindow.IsValid() && ShownWidget == GarageWindow)
	{
		GarageWindow->WearOutfit(Number, Save != 0);
	}
}

void AFWPlayerController::FWGarageHang(const FString& Item)
{
	if (GarageWindow.IsValid() && ShownWidget == GarageWindow)
	{
		GarageWindow->HangUp(Item);
	}
}

void AFWPlayerController::FWCompany(int32 Tab)
{
	if (!Company.IsValid())
	{
		OpenCompany();
	}
	Company->ShowTab(Tab);
}

void AFWPlayerController::FWCompanyDo(const FString& Type, const FString& Field, const FString& Value)
{
	if (!Company.IsValid())
	{
		OpenCompany();
	}
	TSharedPtr<FJsonObject> P = Payload();
	if (!Field.IsEmpty())
	{
		// Numbers go as numbers, the rest as text
		if (Value.IsNumeric())
		{
			P->SetNumberField(Field, FCString::Atod(*Value));
		}
		else
		{
			P->SetStringField(Field, Value);
		}
	}
	Company->Act(*Type, P);
}

void AFWPlayerController::FWBank()
{
	OpenBank();
}

void AFWPlayerController::FWAdventures(int32 Tab, const FString& Whose)
{
	if (!Adventures.IsValid())
	{
		OpenAdventures();
	}
	Adventures->ShowTab(Tab, Whose);
}

void AFWPlayerController::FWPuzzle(int32 Index)
{
	if (Adventures.IsValid())
	{
		Adventures->OpenPuzzle(Index);
	}
}

void AFWPlayerController::FWPrompt(const FString& Text)
{
	if (Prompt.IsValid())
	{
		Prompt->AcceptWith(Text);
	}
}

void AFWPlayerController::FWPromptSecondary()
{
	if (Prompt.IsValid())
	{
		Prompt->PressSecondary();
	}
}

#undef LOCTEXT_NAMESPACE
