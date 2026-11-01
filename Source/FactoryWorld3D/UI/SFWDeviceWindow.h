#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * A device on the plot that works by itself (E at it), as in the 2D game:
 *   a power source: steam generator, power plants, water wheel, solar panel, wind turbine, battery, heater
 *     (generator_window.gd; generator.get / load / take)
 *   a mining drill by a mine (drill_window.gd; drill.get / take)
 *   an oil or gas well (well_window.gd; well.get / take / seal)
 *   a grid tie, the end of a power cable to another plot (grid_tie_window.gd; powertie.get / price / offers,
 *     powerline.build / buy / remove)
 */
class SFWDeviceWindow : public SCompoundWidget
{
public:
	enum class EMode : uint8 { None, Generator, Drill, Well, Tie };

	SLATE_BEGIN_ARGS(SFWDeviceWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		SLATE_ARGUMENT(FString, Object)
		SLATE_ARGUMENT(FString, Kind)
	SLATE_END_ARGS()

	// Which window a placed object's kind opens (None for any other)
	static EMode ModeOf(const FString& Kind);

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

private:
	void Load(bool bPoll = false);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, bool bInventory);
	bool Apply(const FFWNetReply& Reply);
	TSharedPtr<FJsonObject> Here() const;
	void Rebuild();
	TSharedRef<SWidget> MakeGenerator();
	TSharedRef<SWidget> MakeDrill();
	TSharedRef<SWidget> MakeWell();
	TSharedRef<SWidget> MakeTie();
	void FindOffers();
	double ServerNow() const;
	int32 Owned(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Object;
	FString Kind;
	EMode Mode = EMode::None;
	FIntPoint Plot = FIntPoint(-1, -1);
	TSharedPtr<FJsonObject> State;
	// The grid tie's offers of a cable, once asked for
	TArray<TSharedPtr<FJsonObject>> Offers;
	bool bOffersAsked = false;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
