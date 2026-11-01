#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;

// What kind of work place a window shows
enum class EFWStation : uint8 { Crafting, Furnace, Machine };

/**
 * The window of a work place on the plot, built from the server's reply and rebuilt with each new one:
 *   Crafting (workbench, anvil, shipyard, tailor): the recipes with what they need and what the player has, an
 *     order button each, and the queue with progress and cancelling (crafting.get / craft / cancel)
 *   Furnace: ore in, coal, ingots out, the fire's progress (furnace.get / load / take)
 *   Machine: its recipe, what each batch needs and holds, fuel for fired ones, the output, speed and service
 *     (machine.get / recipe / load / take / service)
 */
class SFWStationWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWStationWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		SLATE_ARGUMENT(EFWStation, Station)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

private:
	void Rebuild();
	TSharedRef<SWidget> MakeCrafting(const FJsonObject& State);
	TSharedRef<SWidget> MakeFurnace(const FJsonObject& State);
	TSharedRef<SWidget> MakeMachine(const FJsonObject& State);
	// Parts
	TSharedRef<SWidget> ItemChip(const FString& Item, const FText& Text, bool bShort = false);
	TSharedRef<SWidget> Button(const FText& Text, TFunction<void()> OnClick, bool bPrimary = false);
	TSharedRef<SWidget> Progress(TFunction<float()> Fraction);
	// 0..1 through a job that started at StartedAt (server ms) and takes Seconds
	float JobFraction(double StartedAt, double Seconds) const;
	int32 Have(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	EFWStation Station = EFWStation::Crafting;
	TSharedPtr<SBox> Content;
	int32 BuiltVersion = -1;
};
