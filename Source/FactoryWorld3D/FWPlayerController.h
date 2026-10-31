#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "GameFramework/PlayerController.h"
#include "FWPlayerController.generated.h"

class AFWCharacterStudio;
class UAudioComponent;
class AFWPlot;
class SFWCharacterCreator;
class SFWHud;
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
};

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
	FString ItemName(const FString& Item) const;
	const TArray<FFWToast>& GetToasts() const { return Toasts; }
	bool IsInWorld() const { return Plot != nullptr; }

	void SelectSlot(int32 Slot);
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
	void OpenCreator(const TSharedPtr<FJsonObject>& Options);
	void CloseCreator();
	void ChoosePlot();
	void OfferStarterPlot(int32 Radius, int32 Index);
	void EnterPlot(int32 X, int32 Y);
	void RefreshInventory();
	void RefreshWallet();
	void PollLogistics();
	void ApplyHotbar(const TSharedPtr<FJsonObject>& Payload);
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

	TSharedPtr<SFWLoginWidget> LoginWidget;
	TSharedPtr<SFWPromptWidget> Prompt;
	TSharedPtr<SWidget> ShownWidget;
	TSharedPtr<SFWHud> Hud;

	TArray<FFWStack> Inventory;
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
