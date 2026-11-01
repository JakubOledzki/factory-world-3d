#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
class SVerticalBox;
struct FFWNetReply;

/**
 * The filter of something on the plot's belts, as in the 2D game (filter_window.gd, splitter_window.gd):
 *   an inserter or a pump (E at it): the one item it picks up, and its condition: fill the storage in front up to so
 *     many, keep so many in the one behind (filter.set, inserter.condition)
 *   a storage or a machine (the "Filtr" button of its window): the one item it lets in (filter.set)
 *   a splitter (E at it): the items it sorts out to one side, everything else to the other (splitter.set)
 * Everything comes from the plot's logistics (logistics.get with the item names).
 */
class SFWFilterWindow : public SCompoundWidget
{
public:
	enum class ERole : uint8 { None, Inserter, Splitter, Storage, Machine };

	SLATE_BEGIN_ARGS(SFWFilterWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		SLATE_ARGUMENT(FString, Object)
		SLATE_ARGUMENT(ERole, Role)
	SLATE_END_ARGS()

	// What opens its own filter window on E: inserters, pumps and splitters
	static ERole RoleOf(const FString& Kind);

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

	// Also for the tests: picks an item (empty clears), or for a splitter adds it on Side (0 left, 1 right)
	void Choose(const FString& Item, int32 Side = -1);

private:
	void Load();
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload);
	bool Apply(const FFWNetReply& Reply);
	void SetSplitter(const TArray<FString>& Items, int32 Side);
	void Rebuild();
	void RebuildChoices();
	FString NameOf(const FString& Item) const;
	FString Way(int32 Side) const;
	void Close();

	TWeakObjectPtr<AFWPlayerController> Controller;
	FString Object;
	FString Kind;
	FString ObjectName;
	int32 Rotation = 0;
	ERole Role = ERole::None;
	FIntPoint Plot = FIntPoint(-1, -1);
	bool bLoaded = false;
	// From the plot's logistics: the item names, this object's filter, condition, splitter items and side, and the only
	// items a storage takes at all (a pile takes only wood)
	TMap<FString, FString> Names;
	FString Filter;
	int32 Below = 0;
	int32 Keep = 0;
	TArray<FString> SplitItems;
	int32 SplitSide = 1;
	TArray<FString> Allowed;
	FString Search;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
	TSharedPtr<SVerticalBox> Choices;
};
