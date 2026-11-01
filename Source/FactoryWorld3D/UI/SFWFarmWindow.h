#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * A building that farms the land around it by itself (E at it), as in the 2D game:
 *   the farmer's farmstead, manor farm or mechanized farm: what it sows, manure, the crops it reaped
 *     (farmstead_window.gd; farmstead.get / sow / take)
 *   the forester's hut, lodge or forestry station: what it plants, the wood it cut (forester_window.gd; forestry.get,
 *     forester.plant / take)
 *   the beehive: honey and wax, the lindens around (hive_window.gd; hive.get / take)
 *   the feeder or automatic barn: feed for the animals around, what the barn collected (feeder_window.gd; feeder.get /
 *     fill / take)
 */
class SFWFarmWindow : public SCompoundWidget
{
public:
	enum class EMode : uint8 { None, Farmstead, Forester, Hive, Feeder };

	SLATE_BEGIN_ARGS(SFWFarmWindow) {}
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
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	bool Apply(const FFWNetReply& Reply);
	TSharedPtr<FJsonObject> Here() const;
	void Rebuild();
	TSharedRef<SWidget> MakeFarmstead();
	TSharedRef<SWidget> MakeForester();
	TSharedRef<SWidget> MakeHive();
	TSharedRef<SWidget> MakeFeeder();
	int32 Owned(const FString& Item) const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Object;
	FString Kind;
	EMode Mode = EMode::None;
	FIntPoint Plot = FIntPoint(-1, -1);
	// This building's own part of the reply (a farmstead or forester out of the plot's list, the hive, the feeder)
	TSharedPtr<FJsonObject> State;
	double NextPoll = 0.0;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
