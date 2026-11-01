#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SEditableTextBox;
class SScrollBox;
class SVerticalBox;
struct FFWNetReply;

/**
 * The chat in the lower left corner, as in the 2D game (chat_box.gd): the last few lines fade away by themselves;
 * Enter opens the history and a line to write in, to the players on this plot or to everybody, with quick phrases.
 * It asks the server for news every 2 seconds, only for what it has not seen (chat.get after the last id), and sends
 * with chat.send. The server's own news (a first in the world, a meteorite...) stands out in gold.
 */
class SFWChat : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWChat) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	bool IsOpen() const { return bOpen; }
	void SetOpen(bool bInOpen);
	// The line to type in, to give it the keyboard when the chat opens
	TSharedPtr<SWidget> GetInput() const;
	// Also used by the test console commands: "plot" or "world"
	void SetChannel(const FString& Channel);
	void Say(const FString& Text);

private:
	void Poll();
	void Rebuild();

	struct FLine
	{
		TSharedPtr<FJsonObject> Message;
		// When it came (FPlatformTime seconds), 0 for history from before the player came
		double At = 0.0;
	};

	TWeakObjectPtr<AFWPlayerController> Controller;
	TArray<FLine> Lines;
	int64 Last = -1;
	double NextPoll = 0.0;
	bool bPolling = false;
	bool bOpen = false;
	FString Channel = TEXT("plot");
	TSharedPtr<SVerticalBox> List;
	TSharedPtr<SScrollBox> Scroll;
	TSharedPtr<SWidget> OpenPart;
	TSharedPtr<SEditableTextBox> Input;
};
