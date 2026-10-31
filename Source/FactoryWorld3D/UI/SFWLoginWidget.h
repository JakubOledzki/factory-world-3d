#pragma once

#include "CoreMinimal.h"
#include "Widgets/SCompoundWidget.h"

class UFWNetSubsystem;
class SEditableTextBox;
class STextBlock;
enum class EFWNetState : uint8;

DECLARE_DELEGATE(FOnFWLoginFinished);

// Login / register form shown over the game viewport until the player is logged in.
class SFWLoginWidget : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWLoginWidget) {}
		SLATE_ARGUMENT(TWeakObjectPtr<UFWNetSubsystem>, Net)
		SLATE_EVENT(FOnFWLoginFinished, OnFinished)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
	virtual ~SFWLoginWidget() override;

	TSharedPtr<SWidget> GetFocusTarget() const;

private:
	FReply OnLoginClicked();
	FReply OnRegisterClicked();
	void Submit(bool bRegister);
	void HandleStateChanged(EFWNetState State);
	void AfterAuth();
	void SetStatus(const FText& Text, bool bError = false);
	bool CanSubmit() const;

	TWeakObjectPtr<UFWNetSubsystem> Net;
	FOnFWLoginFinished OnFinished;
	TSharedPtr<SEditableTextBox> EmailBox;
	TSharedPtr<SEditableTextBox> PasswordBox;
	TSharedPtr<STextBlock> StatusText;
	FDelegateHandle StateHandle;
	bool bBusy = false;
	bool bTriedResume = false;
};
