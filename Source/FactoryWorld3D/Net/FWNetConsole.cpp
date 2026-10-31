// Console commands for testing the server connection without the login screen:
//   fw.Register <email> <password>
//   fw.Login <email> <password>
//   fw.WorldInfo
// Results go to the LogFWNetConsole log category.

#include "Engine/GameInstance.h"
#include "Engine/World.h"
#include "HAL/IConsoleManager.h"
#include "Net/FWNetSubsystem.h"

DEFINE_LOG_CATEGORY_STATIC(LogFWNetConsole, Log, All);

namespace
{
	UFWNetSubsystem* GetNet(UWorld* World)
	{
		UGameInstance* GameInstance = World ? World->GetGameInstance() : nullptr;
		return GameInstance ? GameInstance->GetSubsystem<UFWNetSubsystem>() : nullptr;
	}

	void LogReply(const TCHAR* What, const FFWNetReply& Reply)
	{
		if (Reply.IsError())
		{
			UE_LOG(LogFWNetConsole, Warning, TEXT("%s -> error %s: %s"), What, *Reply.ErrorCode(), *Reply.ErrorMessage());
		}
		else
		{
			UE_LOG(LogFWNetConsole, Log, TEXT("%s -> %s"), What, *Reply.Type);
		}
	}

	// Runs Action once the handshake is done, connecting first if needed.
	void WhenReady(UFWNetSubsystem* Net, TFunction<void(UFWNetSubsystem*)> Action)
	{
		if (Net->GetState() == EFWNetState::Ready || Net->GetState() == EFWNetState::Authenticated)
		{
			Action(Net);
			return;
		}
		TSharedRef<FDelegateHandle> Handle = MakeShared<FDelegateHandle>();
		TWeakObjectPtr<UFWNetSubsystem> WeakNet(Net);
		*Handle = Net->OnStateChanged.AddLambda([WeakNet, Handle, Action = MoveTemp(Action)](EFWNetState State)
		{
			UFWNetSubsystem* Inner = WeakNet.Get();
			if (!Inner || (State != EFWNetState::Ready && State != EFWNetState::Disconnected))
			{
				return;
			}
			// Removing the binding destroys this lambda, so take what we need first
			TFunction<void(UFWNetSubsystem*)> Run = Action;
			Inner->OnStateChanged.Remove(*Handle);
			if (State == EFWNetState::Ready)
			{
				Run(Inner);
			}
			else
			{
				UE_LOG(LogFWNetConsole, Warning, TEXT("Could not connect to %s"), *Inner->ServerUrl);
			}
		});
		Net->Connect();
	}

	void Authenticate(const TArray<FString>& Args, UWorld* World, bool bRegister)
	{
		UFWNetSubsystem* Net = GetNet(World);
		if (!Net || Args.Num() < 2)
		{
			UE_LOG(LogFWNetConsole, Warning, TEXT("Usage: fw.%s <email> <password>"), bRegister ? TEXT("Register") : TEXT("Login"));
			return;
		}
		const FString Email = Args[0];
		const FString Password = Args[1];
		WhenReady(Net, [Email, Password, bRegister](UFWNetSubsystem* Ready)
		{
			TWeakObjectPtr<UFWNetSubsystem> WeakNet(Ready);
			FFWNetCallback Done = [WeakNet, bRegister](const FFWNetReply& Reply)
			{
				LogReply(bRegister ? TEXT("auth.register") : TEXT("auth.login"), Reply);
				UFWNetSubsystem* Net = WeakNet.Get();
				if (!Net || Reply.Type != TEXT("auth.ok"))
				{
					return;
				}
				UE_LOG(LogFWNetConsole, Log, TEXT("Account %s"), *Net->GetAccountId());
				Net->GetCharacter([](const FFWNetReply& Character) { LogReply(TEXT("character.get"), Character); });
				Net->GetWorldInfo([](const FFWNetReply& Info)
				{
					LogReply(TEXT("world.info"), Info);
					double Width = 0, Height = 0;
					if (Info.Payload.IsValid() && Info.Payload->TryGetNumberField(TEXT("width"), Width) && Info.Payload->TryGetNumberField(TEXT("height"), Height))
					{
						UE_LOG(LogFWNetConsole, Log, TEXT("World %.0f x %.0f"), Width, Height);
					}
				});
			};
			if (bRegister)
			{
				Ready->Register(Email, Password, MoveTemp(Done));
			}
			else
			{
				Ready->Login(Email, Password, MoveTemp(Done));
			}
		});
	}

	FAutoConsoleCommandWithWorldAndArgs RegisterCommand(
		TEXT("fw.Register"), TEXT("Creates an account on the Factory World server: fw.Register <email> <password>"),
		FConsoleCommandWithWorldAndArgsDelegate::CreateLambda([](const TArray<FString>& Args, UWorld* World) { Authenticate(Args, World, true); }));

	FAutoConsoleCommandWithWorldAndArgs LoginCommand(
		TEXT("fw.Login"), TEXT("Logs in to the Factory World server: fw.Login <email> <password>"),
		FConsoleCommandWithWorldAndArgsDelegate::CreateLambda([](const TArray<FString>& Args, UWorld* World) { Authenticate(Args, World, false); }));

	FAutoConsoleCommandWithWorldAndArgs WorldInfoCommand(
		TEXT("fw.WorldInfo"), TEXT("Asks the server for world.info"),
		FConsoleCommandWithWorldAndArgsDelegate::CreateLambda([](const TArray<FString>&, UWorld* World)
		{
			if (UFWNetSubsystem* Net = GetNet(World))
			{
				Net->GetWorldInfo([](const FFWNetReply& Reply) { LogReply(TEXT("world.info"), Reply); });
			}
		}));
}
