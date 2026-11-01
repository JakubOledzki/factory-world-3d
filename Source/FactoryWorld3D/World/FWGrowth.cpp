#include "World/FWGrowth.h"

#include "Components/HierarchicalInstancedStaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "World/FWPlot.h"

namespace FWGrowthRules
{
	// How often the farmsteads and foresters are asked for, and how often what grows is drawn again
	constexpr double PollSeconds = 6.0;
	constexpr double RedrawSeconds = 2.0;

	// How long each crop grows by hand when its farmstead does not say (the plot's crops of the 2D game)
	double CropSeconds(const FString& Crop)
	{
		static const TMap<FString, double> Seconds = { { TEXT("wheat"), 300.0 }, { TEXT("carrot"), 420.0 }, { TEXT("cabbage"), 480.0 },
			{ TEXT("corn"), 600.0 }, { TEXT("potato"), 900.0 }, { TEXT("sunflower"), 1200.0 }, { TEXT("sugar_beet"), 720.0 }, { TEXT("flax"), 600.0 },
			{ TEXT("hemp"), 840.0 } };
		const double* Found = Seconds.Find(Crop);
		return Found ? *Found : 600.0;
	}

	bool Farms(const FString& Kind)
	{
		return Kind == TEXT("furniture_farmstead") || Kind == TEXT("furniture_manor_farm") || Kind == TEXT("furniture_mechanized_farm");
	}

	bool Forests(const FString& Kind)
	{
		return Kind == TEXT("furniture_forester_hut") || Kind == TEXT("furniture_forester_lodge") || Kind == TEXT("furniture_forestry_station");
	}

	// The same pick every time for a tile
	uint32 Hash(FIntPoint Tile)
	{
		return HashCombine(GetTypeHash(Tile.X * 73856093), GetTypeHash(Tile.Y * 19349663));
	}

	// Scale a mesh to a height (cm), standing its bottom at Ground
	FTransform Standing(UStaticMesh* Mesh, const FVector& Ground, float Yaw, float HeightCm, float Squash = 1.f)
	{
		const FBoxSphereBounds Bounds = Mesh->GetBounds();
		const float Scale = HeightCm / FMath::Max(1.f, 2.f * Bounds.BoxExtent.Z);
		const float Bottom = (Bounds.Origin.Z - Bounds.BoxExtent.Z) * Scale * Squash;
		return FTransform(FRotator(0.f, Yaw, 0.f), Ground - FVector(0, 0, Bottom), FVector(Scale, Scale, Scale * Squash));
	}
}

AFWGrowth::AFWGrowth()
{
	PrimaryActorTick.bCanEverTick = true;
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
}

UStaticMesh* AFWGrowth::CropMesh(const FString& Crop, bool bRipe)
{
	const FString Name = Crop == TEXT("soil") ? FString(TEXT("crop_soil")) : FString::Printf(TEXT("crop_%s_%s"), *Crop, bRipe ? TEXT("ripe") : TEXT("young"));
	return FWArt::Mesh(*FString::Printf(TEXT("/Game/External/Crops/%s/%s/StaticMeshes/%s.%s"), *Name, *Name, *Name, *Name));
}

void AFWGrowth::Init(AFWPlayerController* InController, AFWPlot* InPlot)
{
	Controller = InController;
	Plot = InPlot;
	if (InPlot)
	{
		SetActorTransform(InPlot->GetActorTransform());
	}
	NextPoll = 0.0;
}

UHierarchicalInstancedStaticMeshComponent* AFWGrowth::Instances(UStaticMesh* Mesh)
{
	if (TObjectPtr<UHierarchicalInstancedStaticMeshComponent>* Found = Components.Find(Mesh))
	{
		return *Found;
	}
	UHierarchicalInstancedStaticMeshComponent* Component = NewObject<UHierarchicalInstancedStaticMeshComponent>(this);
	Component->SetStaticMesh(Mesh);
	Component->SetupAttachment(RootComponent);
	Component->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Component->SetVisibleInRayTracing(false);
	// Wind only near the player, as the forest around the plot
	Component->SetWorldPositionOffsetDisableDistance(5000);
	Component->RegisterComponent();
	Components.Add(Mesh, Component);
	return Component;
}

void AFWGrowth::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	const double Now = FPlatformTime::Seconds();
	if (Now >= NextPoll)
	{
		NextPoll = Now + FWGrowthRules::PollSeconds;
		Poll();
	}
	if (bChanged || Now >= NextRedraw)
	{
		NextRedraw = Now + FWGrowthRules::RedrawSeconds;
		bChanged = false;
		Redraw();
	}
}

void AFWGrowth::Poll()
{
	AFWPlayerController* PC = Controller.Get();
	AFWPlot* OnPlot = Plot.Get();
	if (!PC || !OnPlot)
	{
		return;
	}
	bool bFarms = false, bForests = false;
	for (const TPair<FString, FFWPlotObject>& Pair : OnPlot->GetObjects())
	{
		bFarms |= FWGrowthRules::Farms(Pair.Value.Kind);
		bForests |= FWGrowthRules::Forests(Pair.Value.Kind);
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), OnPlot->GetPlotX());
	P->SetNumberField(TEXT("y"), OnPlot->GetPlotY());
	TWeakObjectPtr<AFWGrowth> WeakThis(this);
	// Fields sown by hand too: always asked for, fine when there are none
	PC->Send(TEXT("farmstead.get"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (AFWGrowth* This = WeakThis.Get(); This && Reply.Type == TEXT("farmsteads"))
		{
			This->ApplyFields(Reply.Payload);
		}
	});
	if (bForests || Trees.Num() > 0)
	{
		PC->Send(TEXT("forestry.get"), P, [WeakThis](const FFWNetReply& Reply)
		{
			if (AFWGrowth* This = WeakThis.Get(); This && Reply.Type == TEXT("forestry"))
			{
				This->ApplyTrees(Reply.Payload);
			}
		});
	}
}

void AFWGrowth::ApplyFields(const TSharedPtr<FJsonObject>& Payload)
{
	double Server = 0.0;
	if (Payload->TryGetNumberField(TEXT("serverTime"), Server))
	{
		ClockOffset = Server - FDateTime::UtcNow().ToUnixTimestampDecimal() * 1000.0;
	}
	// How long a crop grows at each farmstead: its own, on manure faster
	TMap<FString, TMap<FString, double>> Growing;
	const TArray<TSharedPtr<FJsonValue>>* Farmsteads = nullptr;
	if (Payload->TryGetArrayField(TEXT("farmsteads"), Farmsteads))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Farmsteads)
		{
			const TSharedPtr<FJsonObject> Farm = Value->AsObject();
			const bool bManure = Farm->GetNumberField(TEXT("manure")) > 0.0;
			TMap<FString, double>& Seconds = Growing.Add(Farm->GetStringField(TEXT("id")));
			const TArray<TSharedPtr<FJsonValue>>* Kinds = nullptr;
			if (Farm->TryGetArrayField(TEXT("kinds"), Kinds))
			{
				for (const TSharedPtr<FJsonValue>& KindValue : *Kinds)
				{
					const TSharedPtr<FJsonObject> Kind = KindValue->AsObject();
					Seconds.Add(Kind->GetStringField(TEXT("id")), Kind->GetNumberField(bManure ? TEXT("manureSeconds") : TEXT("growSeconds")));
				}
			}
		}
	}
	Fields.Reset();
	const TArray<TSharedPtr<FJsonValue>>* List = nullptr;
	if (Payload->TryGetArrayField(TEXT("fields"), List))
	{
		for (const TSharedPtr<FJsonValue>& Value : *List)
		{
			const TSharedPtr<FJsonObject> Field = Value->AsObject();
			FGrowing Item;
			Item.Tile = FIntPoint(int32(Field->GetNumberField(TEXT("tileX"))), int32(Field->GetNumberField(TEXT("tileY"))));
			Item.Kind = Field->GetStringField(TEXT("crop"));
			Field->TryGetNumberField(TEXT("readyAt"), Item.ReadyAt);
			FString Farm;
			const TMap<FString, double>* Seconds = Field->TryGetStringField(TEXT("farmstead"), Farm) ? Growing.Find(Farm) : nullptr;
			const double* Own = Seconds ? Seconds->Find(Item.Kind) : nullptr;
			Item.GrowSeconds = Own ? *Own : FWGrowthRules::CropSeconds(Item.Kind);
			Fields.Add(Item);
		}
	}
	bChanged = true;
}

void AFWGrowth::ApplyTrees(const TSharedPtr<FJsonObject>& Payload)
{
	// How long each species grows at the foresters
	TMap<FString, double> Seconds;
	const TArray<TSharedPtr<FJsonValue>>* Foresters = nullptr;
	if (Payload->TryGetArrayField(TEXT("foresters"), Foresters))
	{
		for (const TSharedPtr<FJsonValue>& Value : *Foresters)
		{
			const TArray<TSharedPtr<FJsonValue>>* Kinds = nullptr;
			if (Value->AsObject()->TryGetArrayField(TEXT("kinds"), Kinds))
			{
				for (const TSharedPtr<FJsonValue>& KindValue : *Kinds)
				{
					double Grow = 0.0;
					if (KindValue->AsObject()->TryGetNumberField(TEXT("growSeconds"), Grow))
					{
						Seconds.Add(KindValue->AsObject()->GetStringField(TEXT("id")), Grow);
					}
				}
			}
		}
	}
	Trees.Reset();
	const TArray<TSharedPtr<FJsonValue>>* List = nullptr;
	if (Payload->TryGetArrayField(TEXT("trees"), List))
	{
		for (const TSharedPtr<FJsonValue>& Value : *List)
		{
			const TSharedPtr<FJsonObject> Tree = Value->AsObject();
			FGrowing Item;
			Item.Tile = FIntPoint(int32(Tree->GetNumberField(TEXT("tileX"))), int32(Tree->GetNumberField(TEXT("tileY"))));
			Tree->TryGetStringField(TEXT("species"), Item.Kind);
			Tree->TryGetNumberField(TEXT("readyAt"), Item.ReadyAt);
			const double* Grow = Seconds.Find(Item.Kind);
			Item.GrowSeconds = Grow ? *Grow : 600.0;
			Trees.Add(Item);
		}
	}
	bChanged = true;
}

void AFWGrowth::Redraw()
{
	AFWPlot* OnPlot = Plot.Get();
	if (!OnPlot)
	{
		return;
	}
	for (TPair<TObjectPtr<UStaticMesh>, TObjectPtr<UHierarchicalInstancedStaticMeshComponent>>& Pair : Components)
	{
		Pair.Value->ClearInstances();
	}
	const double Now = FDateTime::UtcNow().ToUnixTimestampDecimal() * 1000.0 + ClockOffset;
	const float Tile = OnPlot->GetTileSize();
	const FTransform ToLocal = GetActorTransform().Inverse();
	TMap<UStaticMesh*, TArray<FTransform>> Placed;

	// Fields: tilled soil, and the crop on it, small and green while growing, ripe and full when ready
	UStaticMesh* Soil = CropMesh(TEXT("soil"), false);
	for (const FGrowing& Field : Fields)
	{
		const FVector Ground = ToLocal.TransformPosition(OnPlot->TileCenter(Field.Tile));
		const float Grown = float(FMath::Clamp(1.0 - (Field.ReadyAt - Now) / FMath::Max(Field.GrowSeconds * 1000.0, 1.0), 0.0, 1.0));
		const bool bRipe = Grown >= 1.f;
		// Rows the same way on every tile of a field, turned half round now and then so tiles do not repeat
		const float Yaw = (FWGrowthRules::Hash(Field.Tile) & 1) ? 180.f : 0.f;
		const float Cover = Tile / 400.f;
		if (Soil)
		{
			Placed.FindOrAdd(Soil).Add(FTransform(FRotator(0.f, Yaw, 0.f), Ground, FVector(Cover, Cover, 1.f)));
		}
		UStaticMesh* Crop = CropMesh(Field.Kind, bRipe);
		if (!Crop)
		{
			Crop = CropMesh(TEXT("wheat"), bRipe);
		}
		if (Crop)
		{
			// Sprouting low, then taller as it grows
			const float Height = bRipe ? 1.f : FMath::Lerp(0.15f, 0.95f, Grown);
			Placed.FindOrAdd(Crop).Add(FTransform(FRotator(0.f, Yaw, 0.f), Ground, FVector(Cover, Cover, Height)));
		}
	}

	// The foresters' trees: saplings growing into grown trees, ready to cut
	const TArray<UStaticMesh*> Grown = FWArt::Trees();
	TArray<UStaticMesh*> Young = FWArt::TitanSaplings();
	if (Young.IsEmpty())
	{
		Young = FWArt::Saplings();
	}
	for (const FGrowing& Tree : Trees)
	{
		const uint32 Pick = FWGrowthRules::Hash(Tree.Tile);
		const FVector Jitter(float(int32(Pick % 61) - 30) / 100.f * Tile * 0.4f, float(int32((Pick / 61) % 61) - 30) / 100.f * Tile * 0.4f, 0.f);
		const FVector Ground = ToLocal.TransformPosition(OnPlot->TileCenter(Tree.Tile)) + Jitter;
		const float Progress = float(FMath::Clamp(1.0 - (Tree.ReadyAt - Now) / FMath::Max(Tree.GrowSeconds * 1000.0, 1.0), 0.0, 1.0));
		const float Yaw = float(Pick % 360);
		if (Progress >= 1.f && !Grown.IsEmpty())
		{
			UStaticMesh* Mesh = Grown[Pick % Grown.Num()];
			Placed.FindOrAdd(Mesh).Add(FWGrowthRules::Standing(Mesh, Ground, Yaw, 900.f + float(Pick % 400)));
		}
		else if (!Young.IsEmpty())
		{
			UStaticMesh* Mesh = Young[Pick % Young.Num()];
			Placed.FindOrAdd(Mesh).Add(FWGrowthRules::Standing(Mesh, Ground, Yaw, FMath::Lerp(80.f, 450.f, Progress)));
		}
	}
	for (TPair<UStaticMesh*, TArray<FTransform>>& Pair : Placed)
	{
		Instances(Pair.Key)->AddInstances(Pair.Value, false);
	}
}
