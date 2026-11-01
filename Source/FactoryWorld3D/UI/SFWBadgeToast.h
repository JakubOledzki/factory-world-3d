#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
struct FFWNetReply;

/**
 * Each badge the server says the player just unlocked ("achievement.unlocked", after achievements.watch), one after
 * another, as a card sliding down from the top of the screen, as in the 2D game (achievement_toast.gd). A first in
 * the world comes the same way with its own title. Album finds ("album.found") become a toast.
 */
class SFWBadgeToast : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWBadgeToast) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
	virtual ~SFWBadgeToast() override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands
	void Show(const TSharedPtr<FJsonObject>& Badge);

private:
	void OnPush(const FFWNetReply& Reply);
	void Next();

	TWeakObjectPtr<AFWPlayerController> Controller;
	FDelegateHandle PushHandle;
	TArray<TSharedPtr<FJsonObject>> Queue;
	TSharedPtr<SBox> Card;
	// When the card on show came (FPlatformTime seconds), 0 with none
	double ShownAt = 0.0;
};
