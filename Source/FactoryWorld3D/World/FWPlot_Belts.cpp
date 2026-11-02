// Belts and the items on them: the network, the logistics from the server and its local steps.
#include "World/FWPlot.h"
#include "Components/DecalComponent.h"
#include "Components/HierarchicalInstancedStaticMeshComponent.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "Components/AudioComponent.h"
#include "World/FWTerrainInfo.h"
#include "GameFramework/Pawn.h"
#include "GameFramework/PlayerController.h"
#include "Game/FWCatalog.h"
#include "Game/FWGameSettings.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/Base64.h"
#include "Misc/ScopeExit.h"
#include "Misc/Compression.h"
#include "UObject/ConstructorHelpers.h"
#include "FWPlotHelpers.h"

void AFWPlot::RebuildBelts()
{
	TMap<FIntPoint, FBelt> Old = MoveTemp(Belts);
	TMap<FString, FBelt> OldById;
	for (TPair<FIntPoint, FBelt>& Pair : Old)
	{
		OldById.Add(Pair.Value.Id, MoveTemp(Pair.Value));
	}
	TMap<FString, FInserter> OldInserters;
	for (TPair<FIntPoint, FInserter>& Pair : Inserters)
	{
		OldInserters.Add(Pair.Value.Id, Pair.Value);
	}
	Belts.Reset();
	Inserters.Reset();

	for (const TPair<FString, FFWPlotObject>& Pair : Objects)
	{
		const FFWPlotObject& Object = Pair.Value;
		if (FWCatalog::IsBelt(Object.Kind))
		{
			FBelt Belt;
			if (FBelt* Previous = OldById.Find(Object.Id))
			{
				Belt.Items = Previous->Items;
			}
			Belt.Id = Object.Id;
			Belt.Rotation = Object.Rotation;
			Belt.Speed = FWCatalog::BeltSpeed(Object.Kind);
			Belt.Items.SetNum(BeltSlots * BeltLanes);
			Belts.Add(Object.Tile, MoveTemp(Belt));
		}
		else if (FWCatalog::IsInserter(Object.Kind))
		{
			FInserter Inserter = OldInserters.FindRef(Object.Id);
			Inserter.Id = Object.Id;
			Inserter.Rotation = Object.Rotation;
			Inserters.Add(Object.Tile, Inserter);
		}
	}

	for (TPair<FIntPoint, FBelt>& Pair : Belts)
	{
		Pair.Value.Bend = BendOf(Pair.Key);
	}
	for (TPair<FIntPoint, FBelt>& Pair : Belts)
	{
		Pair.Value.Out = Connect(Pair.Key, FWCatalog::Facing(Pair.Value.Rotation));
	}
	BeltOrder.Reset();
	TSet<FIntPoint> Seen;
	TArray<FIntPoint> Starts;
	Belts.GetKeys(Starts);
	for (const FIntPoint& Start : Starts)
	{
		AddDownstream(Start, Seen);
	}
	RefreshBeltArt();
}

int32 AFWPlot::BendOf(FIntPoint Tile) const
{
	const FBelt& Belt = Belts[Tile];
	auto Feeds = [&](FIntPoint From)
	{
		const FBelt* Other = Belts.Find(Tile + From);
		return Other && FWCatalog::Facing(Other->Rotation) == FIntPoint(-From.X, -From.Y);
	};
	const FIntPoint Left = FWCatalog::LeftOf(Belt.Rotation);
	const bool bFromLeft = Feeds(Left);
	const bool bFromRight = Feeds(FIntPoint(-Left.X, -Left.Y));
	const FIntPoint Back = FWCatalog::Facing(Belt.Rotation);
	if (Feeds(FIntPoint(-Back.X, -Back.Y)) || bFromLeft == bFromRight)
	{
		return 0;
	}
	return bFromLeft ? -1 : 1;
}

AFWPlot::FBeltLink AFWPlot::Connect(FIntPoint Tile, FIntPoint Way) const
{
	FBeltLink Link;
	const FIntPoint Target = Tile + Way;
	const FBelt* Next = Belts.Find(Target);
	if (!Next)
	{
		return Link;
	}
	const FIntPoint NextFacing = FWCatalog::Facing(Next->Rotation);
	if (NextFacing == FIntPoint(-Way.X, -Way.Y))
	{
		return Link;
	}
	Link.bValid = true;
	Link.To = Target;
	if (NextFacing == Way || Next->Bend != 0)
	{
		return Link;
	}
	// Fed from the side: onto the near lane, a place in
	const int32 Near = FWCatalog::LeftOf(Next->Rotation) == FIntPoint(-Way.X, -Way.Y) ? 0 : 1;
	Link.Into[0] = Link.Into[1] = Near;
	Link.Entry = 1;
	return Link;
}

void AFWPlot::AddDownstream(FIntPoint Tile, TSet<FIntPoint>& Seen)
{
	if (Seen.Contains(Tile))
	{
		return;
	}
	Seen.Add(Tile);
	const FBelt& Belt = Belts[Tile];
	if (Belt.Out.bValid)
	{
		AddDownstream(Belt.Out.To, Seen);
	}
	BeltOrder.Add(Tile);
}

void AFWPlot::ApplyLogistics(const TSharedPtr<FJsonObject>& State)
{
	State->TryGetNumberField(TEXT("tickMs"), TickMs);
	State->TryGetNumberField(TEXT("beltSlots"), BeltSlots);
	State->TryGetNumberField(TEXT("beltLanes"), BeltLanes);
	const int32 PerBelt = BeltSlots * BeltLanes;

	TMap<FString, TSharedPtr<FJsonObject>> Known;
	const TArray<TSharedPtr<FJsonValue>>* BeltList = nullptr;
	if (State->TryGetArrayField(TEXT("belts"), BeltList))
	{
		for (const TSharedPtr<FJsonValue>& Value : *BeltList)
		{
			const TSharedPtr<FJsonObject> Belt = Value->AsObject();
			Known.Add(StringField(Belt, TEXT("id")), Belt);
		}
	}
	// The compact form lists only belts that carry something
	for (TPair<FIntPoint, FBelt>& Pair : Belts)
	{
		FBelt& Belt = Pair.Value;
		Belt.Moved.Reset();
		Belt.Items.Init(FString(), PerBelt);
		if (const TSharedPtr<FJsonObject>* Found = Known.Find(Belt.Id))
		{
			const TArray<TSharedPtr<FJsonValue>>* Items = nullptr;
			if ((*Found)->TryGetArrayField(TEXT("items"), Items))
			{
				for (int32 i = 0; i < FMath::Min(PerBelt, Items->Num()); ++i)
				{
					if (!(*Items)[i]->IsNull())
					{
						Belt.Items[i] = (*Items)[i]->AsString();
					}
				}
			}
		}
	}

	TMap<FString, TSharedPtr<FJsonObject>> KnownInserters;
	const TArray<TSharedPtr<FJsonValue>>* InserterList = nullptr;
	if (State->TryGetArrayField(TEXT("inserters"), InserterList))
	{
		for (const TSharedPtr<FJsonValue>& Value : *InserterList)
		{
			const TSharedPtr<FJsonObject> Inserter = Value->AsObject();
			KnownInserters.Add(StringField(Inserter, TEXT("id")), Inserter);
		}
	}
	for (TPair<FIntPoint, FInserter>& Pair : Inserters)
	{
		FInserter& Inserter = Pair.Value;
		if (const TSharedPtr<FJsonObject>* Found = KnownInserters.Find(Inserter.Id))
		{
			Inserter.Hand = StringField(*Found, TEXT("hand"));
			Inserter.Swing = Inserter.Was = IntField(*Found, TEXT("swing"));
			Inserter.SwingTicks = IntField(*Found, TEXT("swingTicks"), Inserter.SwingTicks);
			double Speed = 1.0;
			(*Found)->TryGetNumberField(TEXT("speed"), Speed);
			Inserter.Speed = float(Speed);
		}
	}

	double Simulated = 0.0, ServerTime = 0.0;
	State->TryGetNumberField(TEXT("simulatedAt"), Simulated);
	State->TryGetNumberField(TEXT("serverTime"), ServerTime);
	SimTime = Simulated;
	ServerAtSync = ServerTime > 0.0 ? ServerTime : Simulated;
	LocalAtSync = FPlatformTime::Seconds();
}

void AFWPlot::StepLogistics()
{
	int32 Phases = 0;
	for (const FIntPoint& Tile : BeltOrder)
	{
		Belts[Tile].Moved.Reset();
		Phases = FMath::Max(Phases, Belts[Tile].Speed);
	}
	for (int32 Phase = 0; Phase < Phases; ++Phase)
	{
		for (const FIntPoint& Tile : BeltOrder)
		{
			FBelt& Belt = Belts[Tile];
			if (Belt.Speed <= Phase)
			{
				continue;
			}
			for (int32 Lane = 0; Lane < BeltLanes; ++Lane)
			{
				const int32 Start = Lane * BeltSlots;
				const int32 End = Start + BeltSlots - 1;
				if (!Belt.Items[End].IsEmpty() && PassOn(Belt, Lane, Belt.Items[End], Belt.Moved.FindRef(End) + 1))
				{
					Belt.Items[End].Reset();
					Belt.Moved.Remove(End);
				}
				for (int32 i = End - 1; i >= Start; --i)
				{
					if (!Belt.Items[i].IsEmpty() && Belt.Items[i + 1].IsEmpty())
					{
						Belt.Items[i + 1] = MoveTemp(Belt.Items[i]);
						Belt.Items[i].Reset();
						Belt.Moved.Add(i + 1, Belt.Moved.FindRef(i) + 1);
						Belt.Moved.Remove(i);
					}
				}
			}
		}
	}
	for (TPair<FIntPoint, FInserter>& Pair : Inserters)
	{
		FInserter& Inserter = Pair.Value;
		Inserter.Was = Inserter.Swing;
		if (Inserter.Speed <= 0.f)
		{
			continue;
		}
		if (!Inserter.Hand.IsEmpty() && Inserter.Swing < Inserter.SwingTicks)
		{
			++Inserter.Swing;
		}
		else if (Inserter.Hand.IsEmpty() && Inserter.Swing > 0)
		{
			--Inserter.Swing;
		}
	}
}

bool AFWPlot::PassOn(FBelt& Belt, int32 Lane, const FString& Item, int32 Moved)
{
	if (!Belt.Out.bValid)
	{
		return false;
	}
	FBelt* Next = Belts.Find(Belt.Out.To);
	if (!Next)
	{
		return false;
	}
	const int32 At = Belt.Out.Into[Lane] * BeltSlots + Belt.Out.Entry;
	if (!Next->Items[At].IsEmpty())
	{
		return false;
	}
	Next->Items[At] = Item;
	Next->Moved.Add(At, Moved);
	return true;
}

FVector AFWPlot::BeltPoint(FIntPoint Tile, const FBelt& Belt, float Along, float Lateral) const
{
	const FIntPoint F = FWCatalog::Facing(Belt.Rotation);
	const FVector2D Dir(F.X, F.Y);
	FVector2D At;
	FVector2D Way = Dir;
	if (Belt.Bend == 0)
	{
		At = Dir * (Along - 0.5f);
	}
	else
	{
		// Round a quarter circle about the corner between the side it comes in from and the way out
		const FIntPoint L = FWCatalog::LeftOf(Belt.Rotation);
		const FVector2D Side = FVector2D(L.X, L.Y) * (Belt.Bend < 0 ? 1.f : -1.f);
		const FVector2D Corner = (Side + Dir) * 0.5f;
		const float A = Along * PI / 2;
		const FVector2D E = -Dir * 0.5f;
		const FVector2D X = -Side * 0.5f;
		At = Corner + E * FMath::Cos(A) + X * FMath::Sin(A);
		Way = (-E * FMath::Sin(A) + X * FMath::Cos(A)).GetSafeNormal();
	}
	const FVector2D Left(Way.Y, -Way.X);
	const FVector2D P = At + Left * Lateral;
	return TileCenter(Tile) + FVector(P.X * TileSize, P.Y * TileSize, (BandHeight + 0.06f) * TileSize);
}

UInstancedStaticMeshComponent* AFWPlot::ItemInstances(const FString& Item)
{
	if (TObjectPtr<UInstancedStaticMeshComponent>* Found = ItemMeshes.Find(Item))
	{
		return *Found;
	}
	UInstancedStaticMeshComponent* Component = NewObject<UInstancedStaticMeshComponent>(this);
	// Ore and stone as rough chunks, bars as ingots, anything else boxed up; tinted per item
	const TCHAR* Model = Item.EndsWith(TEXT("_ore")) || Item == TEXT("coal") ? TEXT("chunk_ore")
		: Item == TEXT("stone") || Item == TEXT("gravel") || Item == TEXT("sand") ? TEXT("chunk_stone")
		: Item.EndsWith(TEXT("_ingot")) ? TEXT("ingot") : TEXT("crate");
	UStaticMesh* Art = FWArt::Factory(Model);
	Component->SetStaticMesh(Art ? Art : CubeMesh.Get());
	if (!Art || FCString::Strcmp(Model, TEXT("crate")) != 0)
	{
		Component->SetMaterial(0, SurfaceMaterial(FWCatalog::ColorFor(Item), Item.EndsWith(TEXT("_ingot")) ? 0.35f : 0.85f,
			Item.EndsWith(TEXT("_ingot")) ? 1.f : 0.f));
	}
	Component->SetupAttachment(RootComponent);
	Component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Component->SetCastShadow(true);
	Component->RegisterComponent();
	ItemMeshes.Add(Item, Component);
	return Component;
}

void AFWPlot::DrawItems()
{
	const float Fraction = FMath::Clamp(float((ServerNow() - SimTime) / TickMs), 0.f, 1.f);
	// Item models are real size for a 4 m tile; the cube placeholder is 1 m
	const float ItemScale = FWArt::Factory(TEXT("crate")) ? TileSize / 400.f : 0.12f * TileSize / ShapeSize;
	TMap<FString, TArray<FTransform>> ByItem;
	for (const TPair<FIntPoint, FBelt>& Pair : Belts)
	{
		const FBelt& Belt = Pair.Value;
		for (int32 i = 0; i < Belt.Items.Num(); ++i)
		{
			if (Belt.Items[i].IsEmpty())
			{
				continue;
			}
			const int32 Lane = i < BeltSlots ? 0 : 1;
			float At = ((i % BeltSlots) + 0.5f) / BeltSlots;
			if (const int32* Moved = Belt.Moved.Find(i))
			{
				At -= (1.f - Fraction) * *Moved / BeltSlots;
			}
			const FVector P = BeltPoint(Pair.Key, Belt, At, Lane == 0 ? LaneOffset : -LaneOffset);
			ByItem.FindOrAdd(Belt.Items[i]).Add(FTransform(FRotator(0, YawOf(Belt.Rotation), 0), P, FVector(ItemScale)));
		}
	}
	for (const TPair<FIntPoint, FInserter>& Pair : Inserters)
	{
		const FInserter& Inserter = Pair.Value;
		if (Inserter.Hand.IsEmpty())
		{
			continue;
		}
		// The hand swings from the tile behind to the tile in front
		const float Swing = FMath::Lerp(float(Inserter.Was), float(Inserter.Swing), Fraction) / FMath::Max(1, Inserter.SwingTicks);
		const FIntPoint F = FWCatalog::Facing(Inserter.Rotation);
		const FVector Way(F.X * TileSize, F.Y * TileSize, 0.f);
		const FVector P = TileCenter(Pair.Key) + Way * FMath::Lerp(-0.6f, 0.6f, Swing) + FVector(0, 0, 0.55f * TileSize);
		ByItem.FindOrAdd(Inserter.Hand).Add(FTransform(FRotator::ZeroRotator, P, FVector(ItemScale)));
	}
	for (TPair<FString, TObjectPtr<UInstancedStaticMeshComponent>>& Pair : ItemMeshes)
	{
		if (!ByItem.Contains(Pair.Key))
		{
			Pair.Value->ClearInstances();
		}
	}
	for (TPair<FString, TArray<FTransform>>& Pair : ByItem)
	{
		UInstancedStaticMeshComponent* Instances = ItemInstances(Pair.Key);
		if (Instances->GetInstanceCount() == Pair.Value.Num())
		{
			Instances->BatchUpdateInstancesTransforms(0, Pair.Value, true, true);
		}
		else
		{
			Instances->ClearInstances();
			Instances->AddInstances(Pair.Value, false, true);
		}
	}
}
