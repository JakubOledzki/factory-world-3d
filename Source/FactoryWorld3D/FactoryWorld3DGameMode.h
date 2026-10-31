#pragma once

#include "CoreMinimal.h"
#include "GameFramework/GameModeBase.h"
#include "FactoryWorld3DGameMode.generated.h"

UCLASS()
class FACTORYWORLD3D_API AFactoryWorld3DGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AFactoryWorld3DGameMode();

	virtual void InitGame(const FString& MapName, const FString& Options, FString& ErrorMessage) override;
};
