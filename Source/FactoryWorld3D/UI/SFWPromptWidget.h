#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class SEditableTextBox;
class STextBlock;

DECLARE_DELEGATE_OneParam(FOnFWPromptAccepted, const FString& /*Input*/);

// A centred panel with a title, a message, an optional text field and a button: used for creating
// the character and buying the first plot.
class SFWPromptWidget : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWPromptWidget) : _ShowInput(false) {}
		SLATE_ARGUMENT(FText, Title)
		SLATE_ARGUMENT(FText, Message)
		SLATE_ARGUMENT(bool, ShowInput)
		SLATE_ARGUMENT(FText, InputHint)
		SLATE_ARGUMENT(FText, ButtonText)
		SLATE_EVENT(FOnFWPromptAccepted, OnAccepted)
		// Optional second button, e.g. to close the panel without choosing
		SLATE_ARGUMENT(FText, SecondaryText)
		SLATE_EVENT(FSimpleDelegate, OnSecondary)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	void SetMessage(const FText& Text, bool bError = false);
	void SetBusy(bool bInBusy) { bBusy = bInBusy; }
	TSharedPtr<SWidget> GetFocusTarget() const;
	// Fills the text field (if any) and presses the button, for console testing
	void AcceptWith(const FString& Text);
	// Presses the second button, for console testing
	void PressSecondary() { OnSecondary.ExecuteIfBound(); }

private:
	FReply Accept();

	FOnFWPromptAccepted OnAccepted;
	FSimpleDelegate OnSecondary;
	TSharedPtr<SEditableTextBox> Input;
	TSharedPtr<STextBlock> MessageText;
	bool bBusy = false;
};
