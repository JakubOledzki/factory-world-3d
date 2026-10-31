#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWCharacterStudio;
class SEditableTextBox;
class STextBlock;
struct FFWLook;

DECLARE_DELEGATE_TwoParams(FOnFWCreateCharacter, const FString& /*Name*/, TSharedPtr<FJsonObject> /*Appearance*/);

/**
 * The character creator over the studio preview: the name and one row per part of the look, stepping through
 * exactly what the server offers (character.options), as in the 2D game's editor. Dragging over the preview
 * turns the figure, the mouse wheel zooms to the face.
 */
class SFWCharacterCreator : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWCharacterCreator) {}
		SLATE_ARGUMENT(TSharedPtr<FJsonObject>, Options)
		SLATE_ARGUMENT(TWeakObjectPtr<AFWCharacterStudio>, Studio)
		SLATE_EVENT(FOnFWCreateCharacter, OnCreate)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	TSharedPtr<SWidget> GetFocusTarget() const;
	void SetBusy(bool bInBusy);
	void SetError(const FText& Text);

	// Also used by the test console commands
	void Pick(const FString& Key, int32 Index);
	void Randomize();
	void SetName(const FString& Name);
	void Submit();

	virtual FReply OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnMouseButtonUp(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnMouseMove(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnMouseWheel(const FGeometry& Geometry, const FPointerEvent& Event) override;

private:
	struct FRow
	{
		FText Label;
		FString Key;
		// A palette of colours (hex), or a number of styles with their names
		TArray<FString> Palette;
		TArray<FText> StyleNames;
		int32 Count() const { return Palette.Num() > 0 ? Palette.Num() : StyleNames.Num(); }
	};

	TSharedRef<SWidget> MakeRow(int32 RowIndex);
	void Step(int32 RowIndex, int32 Delta);
	FText ValueText(int32 RowIndex) const;
	FSlateColor SwatchColor(int32 RowIndex) const;
	EVisibility SwatchVisibility(int32 RowIndex) const;
	FLinearColor PaletteColor(const FString& Key) const;
	void ApplyLook();
	bool CanSubmit() const;

	TArray<FRow> Rows;
	TMap<FString, int32> Values;
	TWeakObjectPtr<AFWCharacterStudio> Studio;
	FOnFWCreateCharacter OnCreate;
	TSharedPtr<SEditableTextBox> NameBox;
	TSharedPtr<STextBlock> ErrorText;
	bool bBusy = false;
	bool bDragging = false;
};
