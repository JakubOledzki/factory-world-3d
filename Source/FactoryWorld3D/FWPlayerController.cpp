#include "FWPlayerController.h"

#include "Engine/GameInstance.h"
#include "Engine/GameViewportClient.h"
#include "Net/FWNetSubsystem.h"
#include "UI/SFWLoginWidget.h"

void AFWPlayerController::BeginPlay()
{
	Super::BeginPlay();
	if (IsLocalController())
	{
		ShowLogin();
	}
}

void AFWPlayerController::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
	HideLogin();
	Super::EndPlay(EndPlayReason);
}

void AFWPlayerController::ShowLogin()
{
	UGameViewportClient* Viewport = GetWorld() ? GetWorld()->GetGameViewport() : nullptr;
	UFWNetSubsystem* Net = GetGameInstance() ? GetGameInstance()->GetSubsystem<UFWNetSubsystem>() : nullptr;
	if (!Viewport || !Net || LoginWidget.IsValid())
	{
		return;
	}

	SAssignNew(LoginWidget, SFWLoginWidget)
		.Net(Net)
		.OnFinished(FOnFWLoginFinished::CreateUObject(this, &AFWPlayerController::HideLogin));
	Viewport->AddViewportWidgetContent(LoginWidget.ToSharedRef(), 100);

	FInputModeUIOnly InputMode;
	InputMode.SetWidgetToFocus(LoginWidget);
	InputMode.SetLockMouseToViewportBehavior(EMouseLockMode::DoNotLock);
	SetInputMode(InputMode);
	SetShowMouseCursor(true);
}

void AFWPlayerController::HideLogin()
{
	if (!LoginWidget.IsValid())
	{
		return;
	}
	if (UGameViewportClient* Viewport = GetWorld() ? GetWorld()->GetGameViewport() : nullptr)
	{
		Viewport->RemoveViewportWidgetContent(LoginWidget.ToSharedRef());
	}
	LoginWidget.Reset();

	SetInputMode(FInputModeGameOnly());
	SetShowMouseCursor(false);
}
