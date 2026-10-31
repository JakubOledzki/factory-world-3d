#include "Net/FWNetSubsystem.h"

#include "HAL/PlatformFileManager.h"
#include "IWebSocket.h"
#include "Misc/CommandLine.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "WebSocketsModule.h"

DEFINE_LOG_CATEGORY_STATIC(LogFWNet, Log, All);

namespace
{
	FFWNetReply MakeErrorReply(const FString& Code, const FString& Message)
	{
		FFWNetReply Reply;
		Reply.Type = TEXT("error");
		Reply.Payload = MakeShared<FJsonObject>();
		Reply.Payload->SetStringField(TEXT("code"), Code);
		Reply.Payload->SetStringField(TEXT("message"), Message);
		return Reply;
	}

	TSharedPtr<FJsonObject> Credentials(const FString& Email, const FString& Password)
	{
		TSharedPtr<FJsonObject> Payload = MakeShared<FJsonObject>();
		Payload->SetStringField(TEXT("email"), Email);
		Payload->SetStringField(TEXT("password"), Password);
		return Payload;
	}
}

FString FFWNetReply::ErrorCode() const
{
	FString Code;
	if (Payload.IsValid())
	{
		Payload->TryGetStringField(TEXT("code"), Code);
	}
	return Code;
}

FString FFWNetReply::ErrorMessage() const
{
	FString Message;
	if (Payload.IsValid())
	{
		Payload->TryGetStringField(TEXT("message"), Message);
	}
	return Message;
}

void UFWNetSubsystem::Initialize(FSubsystemCollectionBase& Collection)
{
	Super::Initialize(Collection);
	FModuleManager::LoadModuleChecked<FWebSocketsModule>(TEXT("WebSockets"));
	// -ServerUrl=wss://... on the command line wins over the .ini (handy for packaged builds)
	FParse::Value(FCommandLine::Get(), TEXT("ServerUrl="), ServerUrl);
	TickHandle = FTSTicker::GetCoreTicker().AddTicker(FTickerDelegate::CreateUObject(this, &UFWNetSubsystem::Tick), 0.5f);
}

void UFWNetSubsystem::Deinitialize()
{
	FTSTicker::GetCoreTicker().RemoveTicker(TickHandle);
	Disconnect();
	Super::Deinitialize();
}

void UFWNetSubsystem::Connect()
{
	if (State != EFWNetState::Disconnected)
	{
		return;
	}

	UE_LOG(LogFWNet, Log, TEXT("Connecting to %s"), *ServerUrl);
	Socket = FWebSocketsModule::Get().CreateWebSocket(ServerUrl);

	TWeakObjectPtr<UFWNetSubsystem> WeakThis(this);
	Socket->OnConnected().AddLambda([WeakThis]()
	{
		UFWNetSubsystem* This = WeakThis.Get();
		if (!This)
		{
			return;
		}
		This->SetState(EFWNetState::Handshaking);
		TSharedPtr<FJsonObject> Hello = MakeShared<FJsonObject>();
		Hello->SetNumberField(TEXT("protocol"), ProtocolVersion);
		This->Request(TEXT("hello"), Hello, [WeakThis](const FFWNetReply& Reply)
		{
			UFWNetSubsystem* Inner = WeakThis.Get();
			if (!Inner)
			{
				return;
			}
			if (Reply.Type == TEXT("welcome"))
			{
				Inner->SetState(EFWNetState::Ready);
			}
			else
			{
				UE_LOG(LogFWNet, Error, TEXT("Handshake rejected: %s %s"), *Reply.ErrorCode(), *Reply.ErrorMessage());
				Inner->Disconnect();
			}
		});
	});
	Socket->OnConnectionError().AddLambda([WeakThis](const FString& Error)
	{
		UE_LOG(LogFWNet, Warning, TEXT("Connection error: %s"), *Error);
		if (UFWNetSubsystem* This = WeakThis.Get())
		{
			This->FailAllPending(TEXT("disconnected"));
			This->SetState(EFWNetState::Disconnected);
		}
	});
	Socket->OnClosed().AddLambda([WeakThis](int32 StatusCode, const FString& Reason, bool bWasClean)
	{
		UE_LOG(LogFWNet, Log, TEXT("Connection closed (%d): %s"), StatusCode, *Reason);
		if (UFWNetSubsystem* This = WeakThis.Get())
		{
			This->FailAllPending(TEXT("disconnected"));
			This->SetState(EFWNetState::Disconnected);
		}
	});
	Socket->OnMessage().AddLambda([WeakThis](const FString& Text)
	{
		if (UFWNetSubsystem* This = WeakThis.Get())
		{
			This->HandleMessage(Text);
		}
	});

	SetState(EFWNetState::Connecting);
	Socket->Connect();
}

void UFWNetSubsystem::Disconnect()
{
	if (Socket.IsValid())
	{
		Socket->OnConnected().Clear();
		Socket->OnConnectionError().Clear();
		Socket->OnClosed().Clear();
		Socket->OnMessage().Clear();
		if (Socket->IsConnected())
		{
			Socket->Close();
		}
		Socket.Reset();
	}
	FailAllPending(TEXT("disconnected"));
	AccountId.Reset();
	SetState(EFWNetState::Disconnected);
}

void UFWNetSubsystem::Request(const FString& Type, TSharedPtr<FJsonObject> Payload, FFWNetCallback OnReply)
{
	if (!Socket.IsValid() || !Socket->IsConnected())
	{
		if (OnReply)
		{
			OnReply(MakeErrorReply(TEXT("not_connected"), TEXT("Brak połączenia z serwerem.")));
		}
		return;
	}

	const FString Id = LexToString(NextId++);
	TSharedRef<FJsonObject> Message = MakeShared<FJsonObject>();
	Message->SetStringField(TEXT("type"), Type);
	Message->SetStringField(TEXT("id"), Id);
	Message->SetObjectField(TEXT("payload"), Payload.IsValid() ? Payload : MakeShared<FJsonObject>());

	FString Text;
	TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
		TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Text);
	FJsonSerializer::Serialize(Message, Writer);

	Pending.Add(Id, FPending{ MoveTemp(OnReply), FPlatformTime::Seconds() });
	Socket->Send(Text);
}

void UFWNetSubsystem::Register(const FString& Email, const FString& Password, FFWNetCallback OnReply)
{
	Authenticate(TEXT("auth.register"), Credentials(Email, Password), MoveTemp(OnReply));
}

void UFWNetSubsystem::Login(const FString& Email, const FString& Password, FFWNetCallback OnReply)
{
	Authenticate(TEXT("auth.login"), Credentials(Email, Password), MoveTemp(OnReply));
}

void UFWNetSubsystem::Resume(FFWNetCallback OnReply)
{
	const FString Token = LoadToken();
	if (Token.IsEmpty())
	{
		if (OnReply)
		{
			OnReply(MakeErrorReply(TEXT("no_token"), TEXT("Brak zapisanej sesji.")));
		}
		return;
	}
	TSharedPtr<FJsonObject> Payload = MakeShared<FJsonObject>();
	Payload->SetStringField(TEXT("token"), Token);
	Authenticate(TEXT("auth.resume"), Payload, MoveTemp(OnReply));
}

void UFWNetSubsystem::Logout()
{
	const FString Token = LoadToken();
	if (!Token.IsEmpty())
	{
		TSharedPtr<FJsonObject> Payload = MakeShared<FJsonObject>();
		Payload->SetStringField(TEXT("token"), Token);
		Request(TEXT("auth.logout"), Payload, nullptr);
	}
	ClearToken();
	AccountId.Reset();
	if (State == EFWNetState::Authenticated)
	{
		SetState(EFWNetState::Ready);
	}
}

void UFWNetSubsystem::GetCharacter(FFWNetCallback OnReply)
{
	Request(TEXT("character.get"), nullptr, MoveTemp(OnReply));
}

void UFWNetSubsystem::GetWorldInfo(FFWNetCallback OnReply)
{
	Request(TEXT("world.info"), nullptr, MoveTemp(OnReply));
}

void UFWNetSubsystem::Authenticate(const FString& Type, TSharedPtr<FJsonObject> Payload, FFWNetCallback OnReply)
{
	TWeakObjectPtr<UFWNetSubsystem> WeakThis(this);
	const bool bResume = Type == TEXT("auth.resume");
	Request(Type, Payload, [WeakThis, bResume, OnReply = MoveTemp(OnReply)](const FFWNetReply& Reply)
	{
		if (UFWNetSubsystem* This = WeakThis.Get())
		{
			if (Reply.Type == TEXT("auth.ok") && Reply.Payload.IsValid())
			{
				Reply.Payload->TryGetStringField(TEXT("accountId"), This->AccountId);
				FString Token;
				if (Reply.Payload->TryGetStringField(TEXT("token"), Token) && !Token.IsEmpty())
				{
					SaveToken(Token);
				}
				UE_LOG(LogFWNet, Log, TEXT("%s: logged in as account %s"), bResume ? TEXT("auth.resume") : TEXT("auth"), *This->AccountId);
				This->SetState(EFWNetState::Authenticated);
			}
			else if (bResume && Reply.IsError() && Reply.ErrorCode() != TEXT("disconnected") && Reply.ErrorCode() != TEXT("timeout"))
			{
				// The server no longer accepts this token
				ClearToken();
			}
		}
		if (OnReply)
		{
			OnReply(Reply);
		}
	});
}

void UFWNetSubsystem::SetState(EFWNetState NewState)
{
	if (State != NewState)
	{
		State = NewState;
		OnStateChanged.Broadcast(State);
	}
}

void UFWNetSubsystem::HandleMessage(const FString& Text)
{
	TSharedPtr<FJsonObject> Message;
	if (!FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(Text), Message) || !Message.IsValid())
	{
		UE_LOG(LogFWNet, Warning, TEXT("Malformed message: %s"), *Text.Left(200));
		return;
	}

	FFWNetReply Reply;
	Message->TryGetStringField(TEXT("type"), Reply.Type);
	const TSharedPtr<FJsonObject>* Payload = nullptr;
	Reply.Payload = Message->TryGetObjectField(TEXT("payload"), Payload) ? *Payload : MakeShared<FJsonObject>();

	FString Id;
	FPending Entry;
	if (Message->TryGetStringField(TEXT("id"), Id) && Pending.RemoveAndCopyValue(Id, Entry))
	{
		if (Entry.Callback)
		{
			Entry.Callback(Reply);
		}
		return;
	}
	OnPush.Broadcast(Reply);
}

void UFWNetSubsystem::FailAllPending(const FString& Code)
{
	TMap<FString, FPending> Failed = MoveTemp(Pending);
	Pending.Reset();
	const FFWNetReply Error = MakeErrorReply(Code, TEXT("Utracono połączenie z serwerem."));
	for (TPair<FString, FPending>& Pair : Failed)
	{
		if (Pair.Value.Callback)
		{
			Pair.Value.Callback(Error);
		}
	}
}

bool UFWNetSubsystem::Tick(float DeltaTime)
{
	const double Now = FPlatformTime::Seconds();
	TArray<FString> Expired;
	for (const TPair<FString, FPending>& Pair : Pending)
	{
		if (Now - Pair.Value.SentAt > RequestTimeout)
		{
			Expired.Add(Pair.Key);
		}
	}
	for (const FString& Id : Expired)
	{
		FPending Entry;
		if (Pending.RemoveAndCopyValue(Id, Entry) && Entry.Callback)
		{
			Entry.Callback(MakeErrorReply(TEXT("timeout"), TEXT("Serwer nie odpowiedział.")));
		}
	}
	return true;
}

FString UFWNetSubsystem::TokenPath()
{
	return FPaths::Combine(FPaths::ProjectSavedDir(), TEXT("Auth"), TEXT("session.token"));
}

FString UFWNetSubsystem::LoadToken()
{
	FString Token;
	FFileHelper::LoadFileToString(Token, *TokenPath());
	return Token.TrimStartAndEnd();
}

void UFWNetSubsystem::SaveToken(const FString& Token)
{
	FFileHelper::SaveStringToFile(Token, *TokenPath());
}

void UFWNetSubsystem::ClearToken()
{
	FPlatformFileManager::Get().GetPlatformFile().DeleteFile(*TokenPath());
}
