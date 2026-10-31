#pragma once

#include "CoreMinimal.h"
#include "GameFramework/PlayerController.h"
#include "FWPlayerController.generated.h"

class SFWLoginWidget;

// Shows the login screen on start and hands control to the player once logged in.
UCLASS()
class FACTORYWORLD3D_API AFWPlayerController : public APlayerController
{
	GENERATED_BODY()

protected:
	virtual void BeginPlay() override;
	virtual void EndPlay(const EEndPlayReason::Type EndPlayReason) override;

private:
	void ShowLogin();
	void HideLogin();

	TSharedPtr<SFWLoginWidget> LoginWidget;
};
