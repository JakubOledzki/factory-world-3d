#include "FactoryWorld3DGameMode.h"

#include "EngineUtils.h"
#include "FWPlayerController.h"
#include "Player/FWCharacter.h"
#include "World/FWEnvironment.h"

AFactoryWorld3DGameMode::AFactoryWorld3DGameMode()
{
	PlayerControllerClass = AFWPlayerController::StaticClass();
	DefaultPawnClass = AFWCharacter::StaticClass();
}

void AFactoryWorld3DGameMode::InitGame(const FString& MapName, const FString& Options, FString& ErrorMessage)
{
	Super::InitGame(MapName, Options, ErrorMessage);

	UWorld* World = GetWorld();
	if (World && !TActorIterator<AFWEnvironment>(World))
	{
		World->SpawnActor<AFWEnvironment>();
	}
}
