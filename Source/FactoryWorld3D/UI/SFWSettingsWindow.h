#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;

// The player's own settings, kept in GameUserSettings.ini (this machine only)
struct FFWUserSettings
{
	float MasterVolume = 1.f;
	float MouseSensitivity = 1.f;
	float FieldOfView = 90.f;
	// 0 low .. 3 epic (the engine's scalability levels)
	int32 Quality = 3;

	static FFWUserSettings Load();
	void Save() const;
};

/**
 * The menu on Esc: sound volume, mouse sensitivity, field of view and graphics quality, applied at once and kept
 * for next time; back to the game, or quit.
 */
class SFWSettingsWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWSettingsWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;

private:
	TSharedRef<SWidget> Slider(const FText& Label, float Min, float Max, float* Value, TFunction<FText(float)> Show);
	void Apply();

	TWeakObjectPtr<AFWPlayerController> Controller;
	FFWUserSettings Settings;
};
