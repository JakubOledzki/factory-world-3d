#include "World/FWTrains.h"

#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "World/FWPlot.h"

namespace FWTrainRules
{
	// Seconds a train takes to roll in, stands at the platform and takes to leave; how far (cm) it comes from and goes
	// to beyond the station. Slower and farther than in the 2D game, for a train seen from the ground at full size.
	constexpr double RollIn = 18.0;
	constexpr double Stand = 8.0;
	constexpr double RollOut = 20.0;
	constexpr float Run = 32000.f;
	// Goods per wagon, and most wagons a train has
	constexpr int32 PerWagon = 400;
	constexpr int32 MaxWagons = 5;
	// The world wraps east to west after this many plots, as the server counts the trip
	constexpr int32 WorldWidth = 6933;
	// The top of the rails above the ground (Tools/blender/make_factory.py rails), cm
	constexpr float RailTop = 30.f;
	// Lengths of the cars over their buffers (Tools/blender/make_trains.py), cm, and the gap between them
	constexpr float LocomotiveLength = 1490.f;
	constexpr float WagonLength = 1390.f;
	constexpr float Gap = 20.f;
}

AFWTrains::AFWTrains()
{
	PrimaryActorTick.bCanEverTick = true;
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
}

void AFWTrains::Init(AFWPlayerController* InController, AFWPlot* InPlot)
{
	Controller = InController;
	Plot = InPlot;
	if (InPlot)
	{
		SetActorTransform(InPlot->GetActorTransform());
	}
	FindTrack();
	NextPoll = 0.0;
}

double AFWTrains::ServerNow() const
{
	return FDateTime::UtcNow().ToUnixTimestampDecimal() * 1000.0 + ClockOffset;
}

void AFWTrains::FindTrack()
{
	AFWPlot* OnPlot = Plot.Get();
	RailRow = -1;
	if (!OnPlot || !OnPlot->IsBuilt())
	{
		return;
	}
	// The track crosses the whole plot along a row; the platform stands next to it
	for (int32 Y = 0; OnPlot->Contains(FIntPoint(0, Y)); ++Y)
	{
		if (OnPlot->TileCode(FIntPoint(0, Y)) == FWCatalog::Rails)
		{
			RailRow = Y;
			break;
		}
	}
	if (RailRow < 0)
	{
		return;
	}
	int32 Low = MAX_int32, High = MIN_int32;
	for (int32 Y = RailRow - 4; Y <= RailRow + 4; ++Y)
	{
		for (int32 X = 0; OnPlot->Contains(FIntPoint(X, FMath::Max(Y, 0))); ++X)
		{
			if (OnPlot->Contains(FIntPoint(X, Y)) && OnPlot->TileCode(FIntPoint(X, Y)) == FWCatalog::Station)
			{
				Low = FMath::Min(Low, X);
				High = FMath::Max(High, X);
			}
		}
	}
	const float Tile = OnPlot->GetTileSize();
	StopX = Low <= High ? (Low + High + 1) * 0.5f * Tile : 125.f * Tile;
}

bool AFWTrains::Busy() const
{
	const double Now = ServerNow();
	for (const FTrain& Train : Trains)
	{
		if (Now >= Train.Start - 2000.0 && Now <= Train.LeaveAt + FWTrainRules::RollOut * 1000.0)
		{
			return true;
		}
	}
	return false;
}

void AFWTrains::Poll()
{
	AFWPlayerController* PC = Controller.Get();
	AFWPlot* OnPlot = Plot.Get();
	if (!PC || !OnPlot)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), OnPlot->GetPlotX());
	P->SetNumberField(TEXT("y"), OnPlot->GetPlotY());
	TWeakObjectPtr<AFWTrains> WeakThis(this);
	PC->Send(TEXT("station.get"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWTrains* This = WeakThis.Get(); This && Reply.Type == TEXT("station"))
		{
			This->Apply(Reply.Payload);
		}
	});
}

void AFWTrains::Apply(const TSharedPtr<FJsonObject>& Station)
{
	AFWPlot* OnPlot = Plot.Get();
	if (!OnPlot)
	{
		return;
	}
	double Server = 0.0;
	if (Station->TryGetNumberField(TEXT("serverTime"), Server))
	{
		ClockOffset = Server - FDateTime::UtcNow().ToUnixTimestampDecimal() * 1000.0;
	}
	const TSharedPtr<FJsonObject>* Rates = nullptr;
	double MinTrip = 10.0, Per100Km = 5.0;
	if (Station->TryGetObjectField(TEXT("rates"), Rates))
	{
		(*Rates)->TryGetNumberField(TEXT("minTripSeconds"), MinTrip);
		(*Rates)->TryGetNumberField(TEXT("secondsPer100Km"), Per100Km);
	}
	TMap<FString, FTrain> Found;
	auto Add = [&Found](const FString& Key, double Start, double StopAt, double LeaveAt, int32 Quantity)
	{
		FTrain& Train = Found.FindOrAdd(Key);
		if (Train.Key.IsEmpty())
		{
			Train.Key = Key;
			Train.Start = Start;
			Train.StopAt = StopAt;
			Train.LeaveAt = LeaveAt;
			Train.Seed = GetTypeHash(Key);
		}
		Train.Quantity += Quantity;
	};
	const TArray<TSharedPtr<FJsonValue>>* Incoming = nullptr;
	if (Station->TryGetArrayField(TEXT("incoming"), Incoming))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Incoming)
		{
			const TSharedPtr<FJsonObject> Trip = Value->AsObject();
			const double Arrives = Trip->GetNumberField(TEXT("arrivesAt"));
			const FString Key = FString::Printf(TEXT("in %.0f %d %d"), Arrives, int32(Trip->GetNumberField(TEXT("fromX"))), int32(Trip->GetNumberField(TEXT("fromY"))));
			Add(Key, Arrives - FWTrainRules::RollIn * 1000.0, Arrives, Arrives + FWTrainRules::Stand * 1000.0, int32(Trip->GetNumberField(TEXT("quantity"))));
		}
	}
	const TArray<TSharedPtr<FJsonValue>>* Outgoing = nullptr;
	if (Station->TryGetArrayField(TEXT("outgoing"), Outgoing))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Outgoing)
		{
			// It left when it arrives less the trip, worked out like the server does
			const TSharedPtr<FJsonObject> Trip = Value->AsObject();
			int32 Dx = FMath::Abs(int32(Trip->GetNumberField(TEXT("toX"))) - OnPlot->GetPlotX());
			Dx = FMath::Min(Dx, FWTrainRules::WorldWidth - Dx);
			const int32 Dy = int32(Trip->GetNumberField(TEXT("toY"))) - OnPlot->GetPlotY();
			const double Km = FMath::Sqrt(double(Dx * Dx + Dy * Dy)) * 5.0;
			const double Seconds = FMath::Max(MinTrip, Km / 100.0 * Per100Km);
			const double Left = Trip->GetNumberField(TEXT("arrivesAt")) - Seconds * 1000.0;
			const FString Key = FString::Printf(TEXT("out %.0f %d %d"), Left, int32(Trip->GetNumberField(TEXT("toX"))), int32(Trip->GetNumberField(TEXT("toY"))));
			Add(Key, Left, Left, Left + FWTrainRules::Stand * 1000.0, int32(Trip->GetNumberField(TEXT("quantity"))));
		}
	}
	// A train that arrived is unloaded and gone from the station's list, but it still stands and leaves
	const double Now = ServerNow();
	for (const FTrain& Train : Trains)
	{
		if (!Found.Contains(Train.Key) && Train.LeaveAt + FWTrainRules::RollOut * 1000.0 > Now)
		{
			Found.Add(Train.Key, Train);
		}
	}
	Found.GenerateValueArray(Trains);
	Trains.Sort([](const FTrain& A, const FTrain& B) { return A.StopAt < B.StopAt; });
	for (FTrain& Train : Trains)
	{
		Train.Wagons = FMath::Clamp(FMath::CeilToInt(Train.Quantity / float(FWTrainRules::PerWagon)), 1, FWTrainRules::MaxWagons);
	}
}

void AFWTrains::Expect(float Seconds, int32 Quantity)
{
	FTrain Train;
	const double Arrives = ServerNow() + Seconds * 1000.0;
	Train.Key = FString::Printf(TEXT("test %.0f"), Arrives);
	Train.Start = Arrives - FWTrainRules::RollIn * 1000.0;
	Train.StopAt = Arrives;
	Train.LeaveAt = Arrives + FWTrainRules::Stand * 1000.0;
	Train.Quantity = Quantity;
	Train.Wagons = FMath::Clamp(FMath::CeilToInt(Quantity / float(FWTrainRules::PerWagon)), 1, FWTrainRules::MaxWagons);
	Train.Seed = GetTypeHash(Train.Key);
	Trains.Add(Train);
	Trains.Sort([](const FTrain& A, const FTrain& B) { return A.StopAt < B.StopAt; });
}

void AFWTrains::Clear()
{
	for (UStaticMeshComponent* Part : Parts)
	{
		if (Part)
		{
			Part->DestroyComponent();
		}
	}
	Parts.Reset();
	Cars.Reset();
	Shown.Reset();
}

void AFWTrains::Build(const FTrain& Train)
{
	Clear();
	Shown = Train.Key;
	// The locomotive at the front (west), the wagons behind it, the whole train centred on the platform
	TArray<TPair<UStaticMesh*, float>> Names;
	Names.Add({ FWArt::Factory(TEXT("train_locomotive")), FWTrainRules::LocomotiveLength });
	FRandomStream Random(int32(Train.Seed));
	for (int32 i = 0; i < Train.Wagons; ++i)
	{
		Names.Add({ FWArt::Factory(Random.FRand() < 0.5f ? TEXT("train_wagon_open") : TEXT("train_wagon")), FWTrainRules::WagonLength });
	}
	float Total = -FWTrainRules::Gap;
	for (const TPair<UStaticMesh*, float>& Car : Names)
	{
		Total += Car.Value + FWTrainRules::Gap;
	}
	float At = -Total / 2;
	for (const TPair<UStaticMesh*, float>& Car : Names)
	{
		const float Middle = At + Car.Value / 2;
		At += Car.Value + FWTrainRules::Gap;
		if (!Car.Key)
		{
			continue;
		}
		UStaticMeshComponent* Part = NewObject<UStaticMeshComponent>(this);
		Part->SetStaticMesh(Car.Key);
		Part->SetupAttachment(RootComponent);
		Part->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		Part->RegisterComponent();
		Parts.Add(Part);
		Cars.Add({ Part, Middle });
	}
}

void AFWTrains::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	AFWPlot* OnPlot = Plot.Get();
	if (!OnPlot)
	{
		return;
	}
	if (RailRow < 0)
	{
		FindTrack();
		if (RailRow < 0)
		{
			return;
		}
	}
	const double Seconds = FPlatformTime::Seconds();
	if (Seconds >= NextPoll)
	{
		// More often while a train is at the station
		NextPoll = Seconds + (Busy() ? 2.5 : 6.0);
		Poll();
	}
	const double Now = ServerNow();
	const FTrain* Train = Trains.FindByPredicate([Now](const FTrain& Each)
	{
		return Now >= Each.Start && Now <= Each.LeaveAt + FWTrainRules::RollOut * 1000.0;
	});
	if (!Train)
	{
		if (!Shown.IsEmpty())
		{
			Clear();
		}
		return;
	}
	if (Shown != Train->Key)
	{
		Build(*Train);
	}
	// Slowing down to the platform: the distance left shrinks with the square of the time left; pulling away the same
	float X = StopX;
	if (Now < Train->StopAt)
	{
		const float R = float((Train->StopAt - Now) / (FWTrainRules::RollIn * 1000.0));
		X += FWTrainRules::Run * R * R;
	}
	else if (Now > Train->LeaveAt)
	{
		const float S = float((Now - Train->LeaveAt) / (FWTrainRules::RollOut * 1000.0));
		X -= FWTrainRules::Run * S * S;
	}
	const float Tile = OnPlot->GetTileSize();
	const float Y = (RailRow + 0.5f) * Tile;
	for (const TPair<TObjectPtr<UStaticMeshComponent>, float>& Car : Cars)
	{
		Car.Key->SetRelativeLocation(FVector(X + Car.Value, Y, FWTrainRules::RailTop));
	}
}
