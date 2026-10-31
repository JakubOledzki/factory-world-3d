#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "Subsystems/GameInstanceSubsystem.h"
#include "Dom/JsonObject.h"
#include "FWNetSubsystem.generated.h"

class IWebSocket;

UENUM(BlueprintType)
enum class EFWNetState : uint8
{
	Disconnected,
	Connecting,
	// Socket open, waiting for "welcome"
	Handshaking,
	// Handshake done, not logged in
	Ready,
	Authenticated
};

// One server reply. Errors arrive as type "error" with payload { code, message, details? }.
struct FACTORYWORLD3D_API FFWNetReply
{
	FString Type;
	TSharedPtr<FJsonObject> Payload;

	bool IsError() const { return Type == TEXT("error"); }
	FString ErrorCode() const;
	FString ErrorMessage() const;
};

DECLARE_MULTICAST_DELEGATE_OneParam(FOnFWNetStateChanged, EFWNetState);
// Messages the server pushes without a matching request id.
DECLARE_MULTICAST_DELEGATE_OneParam(FOnFWNetPush, const FFWNetReply&);

using FFWNetCallback = TFunction<void(const FFWNetReply&)>;

/**
 * Connection to the Factory World server: JSON over WebSocket, every message is
 * { type, id, payload }. Requests get a client-chosen id that the server echoes back.
 */
UCLASS(Config = Game)
class FACTORYWORLD3D_API UFWNetSubsystem : public UGameInstanceSubsystem
{
	GENERATED_BODY()

public:
	static constexpr int32 ProtocolVersion = 1;

	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;

	void Connect();
	void Disconnect();

	EFWNetState GetState() const { return State; }
	const FString& GetAccountId() const { return AccountId; }
	bool HasSavedToken() const { return !LoadToken().IsEmpty(); }

	// Sends a request; OnReply runs on the game thread with the matching reply.
	void Request(const FString& Type, TSharedPtr<FJsonObject> Payload, FFWNetCallback OnReply);

	void Register(const FString& Email, const FString& Password, FFWNetCallback OnReply);
	void Login(const FString& Email, const FString& Password, FFWNetCallback OnReply);
	// Logs in with the token saved by a previous Login/Register.
	void Resume(FFWNetCallback OnReply);
	void Logout();
	void GetCharacter(FFWNetCallback OnReply);
	void GetWorldInfo(FFWNetCallback OnReply);

	FOnFWNetStateChanged OnStateChanged;
	FOnFWNetPush OnPush;

	UPROPERTY(Config)
	FString ServerUrl = TEXT("ws://localhost:5080/ws");

	// Seconds before an unanswered request fails with code "timeout".
	UPROPERTY(Config)
	float RequestTimeout = 15.f;

private:
	void SetState(EFWNetState NewState);
	void HandleMessage(const FString& Text);
	void FailAllPending(const FString& Code);
	void Authenticate(const FString& Type, TSharedPtr<FJsonObject> Payload, FFWNetCallback OnReply);
	bool Tick(float DeltaTime);

	static FString TokenPath();
	static FString LoadToken();
	static void SaveToken(const FString& Token);
	static void ClearToken();

	struct FPending
	{
		FFWNetCallback Callback;
		double SentAt = 0.0;
	};

	TSharedPtr<IWebSocket> Socket;
	TMap<FString, FPending> Pending;
	int64 NextId = 1;
	EFWNetState State = EFWNetState::Disconnected;
	FString AccountId;
	FTSTicker::FDelegateHandle TickHandle;
};
