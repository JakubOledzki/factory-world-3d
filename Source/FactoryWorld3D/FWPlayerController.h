#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "GameFramework/PlayerController.h"
#include "FWPlayerController.generated.h"

class AFWCharacterStudio;
class SFWMarketWindow;
class UTexture2D;
class UAudioComponent;
class AFWPlot;
class SFWCharacterCreator;
class SFWHud;
class SFWResearchWindow;
class SFWContractsWindow;
class SFWStatsWindow;
class SFWLoginWidget;
class SFWPromptWidget;
class SWidget;
class UFWNetSubsystem;
struct FFWNetReply;

struct FFWStack
{
	FString Id;
	FString Name;
	int32 Quantity = 0;
	// The inventory tiles it lies on, and how many fill one
	TArray<int32> Tiles;
	int32 StackSize = 0;
};

// An open chest (or other storage) on the plot, as storage.get describes it
struct FFWStorageView
{
	FString Id;
	FString Name;
	int32 Slots = 0;
	int32 Capacity = 0;
	int32 Stored = 0;
	TArray<FFWStack> Items;
};

struct FFWUserSettings;
struct FSlateBrush;

struct FFWToast
{
	FText Text;
	double ShownAt = 0.0;
	bool bError = false;
};

/**
 * Drives the session: login, character creation, getting a plot and entering it, then keeps the
 * player's inventory, hotbar and wallet and runs the plot actions the character asks for.
 */
UCLASS()
class FACTORYWORLD3D_API AFWPlayerController : public APlayerController
{
	GENERATED_BODY()

public:
	static constexpr int32 HotbarSize = 5;

	AFWPlayerController();

	AFWPlot* GetPlot() const { return Plot; }

	// What the HUD shows
	int64 GetBalanceCents() const { return BalanceCents; }
	const TArray<FString>& GetHotbar() const { return Hotbar; }
	int32 GetSelectedSlot() const { return SelectedSlot; }
	FString GetSelectedItem() const { return Hotbar.IsValidIndex(SelectedSlot) ? Hotbar[SelectedSlot] : FString(); }
	const FFWStack* FindStack(const FString& Item) const;
	const TArray<FFWStack>& GetInventory() const { return Inventory; }
	int32 GetInventorySlots() const { return InventorySlots; }
	const FFWStorageView& GetStorage() const { return Storage; }
	const FSlateBrush* GetIconBrush(const FString& Item);

	// Windows over the game: the inventory (with a chest beside it) and the settings menu
	void OpenInventory(const FString& StorageId = FString());
	void OpenSettings();
	// The research table (E at it), contracts (J) and production statistics (L), as in the 2D game
	void OpenResearch();
	void OpenContracts();
	void OpenStats();
	void RefreshWallet();
	void CloseWindow();
	void ApplyUserSettings(const FFWUserSettings& Settings);
	// Inventory moves, each answered by the server with the new state
	void MoveTile(int32 From, int32 To);
	void SetHotbarSlot(int32 Slot, const FString& Item);
	void StorageTransfer(bool bPut, const FString& Item, int32 Quantity);
	void StorageTransferAll(bool bPut);
	// The market, at the plot's computer
	void OpenMarket();
	// A request to the server for the windows; replies go to the callback
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<void(const FFWNetReply&)> OnReply);
	void SetBalanceCents(int64 Cents) { BalanceCents = Cents; }
	void PlayUiSound(const TCHAR* Sound);
	void RefreshInventory();
	// A work place's window (workbench, furnace, machine): its state as the server last sent it
	void OpenStation(const FString& ObjectId, const FString& Kind);
	TSharedPtr<FJsonObject> GetStationState() const { return StationState; }
	int32 GetStationVersion() const { return StationVersion; }
	FString GetStationName() const { return StationName; }
	// Sends "<station>.<action>" for the open work place with one more field (string, or a number when >= 0)
	void StationAction(const TCHAR* Type, const TCHAR* Field, const FString& Value, int64 Number = -1);
	void StationLoad(const TCHAR* Type, const FString& Item, int32 Quantity, bool bFuel = false);
	// The server's clock now (ms), from the last station reply
	double ServerNowMs() const;
	FString ItemName(const FString& Item) const;
	const TArray<FFWToast>& GetToasts() const { return Toasts; }
	bool IsInWorld() const { return Plot != nullptr; }

	void SelectSlot(int32 Slot);
	// The icon of an item for the HUD, rendered from its model (Tools/blender/render_icons.py)
	UTexture2D* GetIcon(const FString& Item) const;
	void Toast(const FText& Text, bool bError = false);
	void ToastError(const FFWNetReply& Reply);

	// Plot actions; each refreshes what it changes and reports errors as toasts
	void Mine(int32 MineId, TFunction<void(bool bOk)> Done);
	void Place(const FString& Kind, FIntPoint Tile, int32 Rotation);
	void Pickup(const FString& ObjectId);
	void PutAllInStorage(const FString& ObjectId);

	// Console commands for testing without mouse and keyboard; a file of "seconds command" lines passed
	// with -FWAutoTest=<path> runs them one after another.
	UFUNCTION(Exec)
	void FWPrompt(const FString& Text);
	UFUNCTION(Exec)
	void FWGoto(int32 TileX, int32 TileY, float Yaw = 0.f, float Pitch = -30.f);
	// Stands at a point of the land around the plot: metres from the plot's middle (+X east, +Y south)
	UFUNCTION(Exec)
	void FWGotoLand(float EastMeters, float SouthMeters, float Yaw = 0.f, float Pitch = 0.f);
	// Walks downhill from the plot until the ground is below Meters above sea level (plots with real terrain)
	UFUNCTION(Exec)
	void FWGotoAltitude(float Meters, float Yaw = 0.f, float Pitch = 0.f);
	UFUNCTION(Exec)
	void FWMine(int32 Times = 1);
	UFUNCTION(Exec)
	void FWPlace(const FString& Kind, int32 TileX, int32 TileY, int32 Rotation);
	UFUNCTION(Exec)
	void FWPickupAt(int32 TileX, int32 TileY);
	UFUNCTION(Exec)
	void FWStoreAt(int32 TileX, int32 TileY);
	UFUNCTION(Exec)
	void FWShot(const FString& Name);
	// Finds a free row near the player and builds chest → inserter → belts eastwards, then looks at it
	UFUNCTION(Exec)
	void FWBuildLine(int32 BeltCount = 5);
	// Puts the mined resources in the chest of that line
	UFUNCTION(Exec)
	void FWStoreLine();
	// Hovers above the belts of the plot, looking at them
	UFUNCTION(Exec)
	void FWViewBelts(float Height = 700.f);
	// Character creator: pick an option (appearance key, index), type the name, randomize, zoom (0-1), create
	UFUNCTION(Exec)
	void FWCreatorPick(const FString& Key, int32 Index);
	UFUNCTION(Exec)
	void FWCreatorName(const FString& Name);
	UFUNCTION(Exec)
	void FWCreatorRandom();
	UFUNCTION(Exec)
	void FWCreatorView(float Zoom, float Yaw = 0.f);
	UFUNCTION(Exec)
	void FWCreatorCreate();
	// Windows and inventory moves for tests: the inventory, a chest at a tile, the menu, a tile move
	UFUNCTION(Exec)
	void FWInventory() { OpenInventory(); }
	UFUNCTION(Exec)
	void FWChestAt(int32 TileX, int32 TileY);
	// Opens the storage nearest the player, and goes to stand by it
	UFUNCTION(Exec)
	void FWChestNearest();
	UFUNCTION(Exec)
	void FWClose() { CloseWindow(); }
	// Opens the nearest object of a kind (e.g. furniture_workbench) and stands by it
	UFUNCTION(Exec)
	void FWOpenNearest(const FString& Kind);
	// Market for tests: open it, pick a tab (0-2), an item's board, trade with the NPC trader (1 buy / 0 sell)
	UFUNCTION(Exec)
	void FWMarket() { OpenMarket(); }
	UFUNCTION(Exec)
	void FWMarketTab(int32 Tab);
	UFUNCTION(Exec)
	void FWMarketItem(const FString& Item);
	UFUNCTION(Exec)
	void FWTrade(int32 Buy, const FString& Item, int32 Quantity);
	UFUNCTION(Exec)
	void FWMarketOffer(int32 Sell, int32 Quantity, float PriceDollars);
	// Places an item on the nearest free spot around the player
	UFUNCTION(Exec)
	void FWPlaceNear(const FString& Kind);
	// Stands Tiles away from the nearest object of a kind, on the side at Angle (0 east, 90 south), looking at it
	UFUNCTION(Exec)
	void FWLookAt(const FString& Kind, float Tiles, float Angle, float Pitch = -12.f);
	UFUNCTION(Exec)
	void FWStationDo(const FString& Type, const FString& Field, const FString& Value) { StationAction(*Type, Field.IsEmpty() ? nullptr : *Field, Value); }
	UFUNCTION(Exec)
	void FWStationLoad(const FString& Type, const FString& Item, int32 Quantity) { StationLoad(*Type, Item, Quantity); }
	UFUNCTION(Exec)
	void FWMenu() { OpenSettings(); }
	// Research, contracts and statistics for tests: open them; pick a technology and start it; a contracts tab, an
	// action on the first order of a list (deliver/accept/abandon/cancel), a new order; a statistics period and item
	UFUNCTION(Exec)
	void FWResearch() { OpenResearch(); }
	UFUNCTION(Exec)
	void FWResearchPick(const FString& Technology);
	UFUNCTION(Exec)
	void FWResearchStart();
	UFUNCTION(Exec)
	void FWContracts(int32 Tab = 0);
	UFUNCTION(Exec)
	void FWContractFirst(const FString& Action, const FString& List, int32 Quantity = 1);
	UFUNCTION(Exec)
	void FWContractCreate(const FString& Item, int32 Quantity, float RewardDollars, int32 Hours);
	UFUNCTION(Exec)
	void FWStats(int32 Minutes = 10, const FString& Item = TEXT(""));
	UFUNCTION(Exec)
	void FWMoveTile(int32 From, int32 To) { MoveTile(From, To); }
	UFUNCTION(Exec)
	void FWStorage(int32 Put, const FString& Item, int32 Quantity) { StorageTransfer(Put != 0, Item, Quantity); }
	// Records what the game plays to Saved/BouncedWavFiles/<Name>.wav (to check the mix without listening)
	UFUNCTION(Exec)
	void FWRecordStart(float Seconds = 10.f);
	// Walks straight ahead for a while (footsteps without a keyboard)
	UFUNCTION(Exec)
	void FWWalk(float Seconds);
	UFUNCTION(Exec)
	void FWRecordStop(const FString& Name);

protected:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;
	virtual void Tick(float DeltaSeconds) override;

private:
	UFWNetSubsystem* Net() const;
	void ShowWidget(TSharedRef<SWidget> Widget, TSharedPtr<SWidget> Focus);
	void HideWidget();
	void OnLoggedIn();
	void AskForCharacter();
	// Dresses the player's arms and body as the character the server describes (its "appearance")
	void ApplyLook(const TSharedPtr<FJsonObject>& Described);
	void OpenCreator(const TSharedPtr<FJsonObject>& Options);
	void CloseCreator();
	void ChoosePlot();
	void OfferStarterPlot(int32 Radius, int32 Index);
	void EnterPlot(int32 X, int32 Y);
	void PollLogistics();
	void ApplyHotbar(const TSharedPtr<FJsonObject>& Payload);
	void ApplyInventory(const TSharedPtr<FJsonObject>& Payload);
	void ApplyStorage(const TSharedPtr<FJsonObject>& Payload);
	// Stands the pawn on a ground point (its feet there), walking and at rest
	void PlaceOnGround(const FVector& Feet);
	// Wind, forest, gusts and birds around the player, by height and how much forest stands about
	void UpdateAmbience(float DeltaSeconds);

	UPROPERTY()
	TObjectPtr<AFWPlot> Plot;

	UPROPERTY()
	TObjectPtr<AFWCharacterStudio> Studio;
	TSharedPtr<SFWCharacterCreator> Creator;

	UPROPERTY()
	TObjectPtr<UAudioComponent> WindSound;
	UPROPERTY()
	TObjectPtr<UAudioComponent> ForestSound;
	double NextGustAt = 0.0;
	double NextBirdAt = 0.0;
	float GustBoost = 0.f;
	float WindLevel = 0.f;
	float ForestLevel = 0.f;
	double WalkUntil = 0.0;

	TSharedPtr<SFWMarketWindow> Market;
	TSharedPtr<SFWLoginWidget> LoginWidget;
	TSharedPtr<SFWPromptWidget> Prompt;
	TSharedPtr<SWidget> ShownWidget;
	TSharedPtr<SFWHud> Hud;
	TSharedPtr<SFWResearchWindow> Research;
	TSharedPtr<SFWContractsWindow> Contracts;
	TSharedPtr<SFWStatsWindow> Stats;

	TArray<FFWStack> Inventory;
	int32 InventorySlots = 0;
	FFWStorageView Storage;
	FString StationId;
	FString StationName;
	int32 StationKind = -1;
	TSharedPtr<FJsonObject> StationState;
	int32 StationVersion = 0;
	double StationServerMs = 0.0;
	double StationReceivedAt = 0.0;
	double NextStationPoll = 0.0;
	void ApplyStation(const FFWNetReply& Reply);
	void PollStation();
	TMap<FString, TSharedPtr<FSlateBrush>> IconBrushes;
	// The textures of those brushes: Slate does not keep them from the garbage collector
	UPROPERTY()
	TArray<TObjectPtr<UTexture2D>> IconTextures;
	TMap<FString, FString> Names;
	TArray<FString> Hotbar;
	int32 SelectedSlot = 0;
	int64 BalanceCents = 0;
	TArray<FFWToast> Toasts;
	bool bHotbarFilled = false;
	bool bPolling = false;
	double NextPollAt = 0.0;

	FIntPoint LineStart = FIntPoint(-1, -1);
	TArray<TPair<double, FString>> AutoTest;
	double AutoTestAt = 0.0;
};
