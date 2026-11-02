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

AFWPlot::AFWPlot()
{
	PrimaryActorTick.bCanEverTick = true;
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));

	static ConstructorHelpers::FObjectFinder<UStaticMesh> Cube(TEXT("/Engine/BasicShapes/Cube.Cube"));
	static ConstructorHelpers::FObjectFinder<UStaticMesh> Cylinder(TEXT("/Engine/BasicShapes/Cylinder.Cylinder"));
	static ConstructorHelpers::FObjectFinder<UStaticMesh> Sphere(TEXT("/Engine/BasicShapes/Sphere.Sphere"));
	static ConstructorHelpers::FObjectFinder<UStaticMesh> Cone(TEXT("/Engine/BasicShapes/Cone.Cone"));
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> Surface(TEXT("/Game/Materials/M_FWSurface.M_FWSurface"));
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> Ground(TEXT("/Game/Materials/M_FWGround.M_FWGround"));
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> Terrain(TEXT("/Game/Materials/M_FWTerrain.M_FWTerrain"));
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> Decal(TEXT("/Game/Materials/M_FWDecal.M_FWDecal"));
	CubeMesh = Cube.Object;
	CylinderMesh = Cylinder.Object;
	SphereMesh = Sphere.Object;
	ConeMesh = Cone.Object;
	SurfaceBase = Surface.Object;
	GroundBase = Ground.Object;
	TerrainBase = Terrain.Object;
	DecalBase = Decal.Object;
}

UMaterialInterface* AFWPlot::SurfaceMaterial(const FLinearColor& Color, float Roughness, float Metallic)
{
	const uint32 Key = HashCombine(GetTypeHash(Color.ToFColor(true)), HashCombine(GetTypeHash(Roughness), GetTypeHash(Metallic)));
	if (TObjectPtr<UMaterialInstanceDynamic>* Found = Materials.Find(Key))
	{
		return *Found;
	}
	UMaterialInstanceDynamic* Material = UMaterialInstanceDynamic::Create(SurfaceBase, this);
	Material->SetVectorParameterValue(TEXT("Color"), Color);
	Material->SetScalarParameterValue(TEXT("Roughness"), Roughness);
	Material->SetScalarParameterValue(TEXT("Metallic"), Metallic);
	Materials.Add(Key, Material);
	return Material;
}

UStaticMeshComponent* AFWPlot::MakeMesh(UStaticMesh* Mesh, const FLinearColor& Color, const FTransform& Transform, bool bCollide)
{
	UStaticMeshComponent* Component = NewObject<UStaticMeshComponent>(this);
	Component->SetStaticMesh(Mesh);
	Component->SetMaterial(0, SurfaceMaterial(Color));
	Component->SetupAttachment(RootComponent);
	Component->SetRelativeTransform(Transform);
	Component->SetCollisionEnabled(bCollide ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::NoCollision);
	Component->RegisterComponent();
	return Component;
}

UHierarchicalInstancedStaticMeshComponent* AFWPlot::MakeInstances(UStaticMesh* Mesh, const FLinearColor& Color, float Roughness, bool bCollide)
{
	UHierarchicalInstancedStaticMeshComponent* Component = NewObject<UHierarchicalInstancedStaticMeshComponent>(this);
	Component->SetStaticMesh(Mesh);
	Component->SetMaterial(0, SurfaceMaterial(Color, Roughness));
	Component->SetupAttachment(RootComponent);
	Component->SetCollisionEnabled(bCollide ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::NoCollision);
	Component->SetCullDistances(0, 0);
	Component->RegisterComponent();
	return Component;
}

void AFWPlot::Build(const TSharedPtr<FJsonObject>& Plot)
{
	TileSize = UFWGameSettings::Get().TileSizeCm;
	PlotX = IntField(Plot, TEXT("x"));
	PlotY = IntField(Plot, TEXT("y"));
	Size = IntField(Plot, TEXT("size"), 250);
	const int32 Biome = IntField(Plot, TEXT("biome"));

	const TSharedPtr<FJsonObject>* SpawnObject = nullptr;
	if (Plot->TryGetObjectField(TEXT("spawn"), SpawnObject))
	{
		Spawn = FIntPoint(IntField(*SpawnObject, TEXT("x")), IntField(*SpawnObject, TEXT("y")));
	}

	Placeable.Reset();
	const TArray<TSharedPtr<FJsonValue>>* PlaceableList = nullptr;
	if (Plot->TryGetArrayField(TEXT("placeable"), PlaceableList))
	{
		for (const TSharedPtr<FJsonValue>& Value : *PlaceableList)
		{
			Placeable.Add(Value->AsString());
		}
	}

	Mines.Reset();
	const TArray<TSharedPtr<FJsonValue>>* MineList = nullptr;
	double DefaultSeconds = 5.0;
	Plot->TryGetNumberField(TEXT("mineSeconds"), DefaultSeconds);
	if (Plot->TryGetArrayField(TEXT("mines"), MineList))
	{
		for (const TSharedPtr<FJsonValue>& Value : *MineList)
		{
			const TSharedPtr<FJsonObject> M = Value->AsObject();
			FFWMine Mine;
			Mine.Id = IntField(M, TEXT("id"));
			const int32 X = IntField(M, TEXT("x"));
			const int32 Y = IntField(M, TEXT("y"));
			Mine.Rect = FIntRect(X, Y, X + IntField(M, TEXT("width"), 1), Y + IntField(M, TEXT("height"), 1));
			double Seconds = DefaultSeconds;
			M->TryGetNumberField(TEXT("seconds"), Seconds);
			Mine.Seconds = float(Seconds);
			Mine.Units = IntField(M, TEXT("units"));
			Mines.Add(Mine);
		}
	}

	// Trees of other species have their own tile codes
	TreeCodes = { FWCatalog::Tree };
	const TArray<TSharedPtr<FJsonValue>>* TreeKinds = nullptr;
	if (Plot->TryGetArrayField(TEXT("treeKinds"), TreeKinds))
	{
		for (const TSharedPtr<FJsonValue>& Value : *TreeKinds)
		{
			TreeCodes.Add(uint8(IntField(Value->AsObject(), TEXT("tile"), 1)));
		}
	}

	FString TilesText;
	Plot->TryGetStringField(TEXT("tiles"), TilesText);
	Tiles = Gunzip(TilesText);
	if (Tiles.Num() != Size * Size)
	{
		UE_LOG(LogFWPlot, Warning, TEXT("Plot tiles: expected %d bytes, got %d"), Size * Size, Tiles.Num());
		Tiles.SetNumZeroed(Size * Size);
	}

	BuildGround(Biome);
	GroundCoverMeshes = FWArt::Grass();
	double Seconds = 0.0;
	if (Plot->TryGetNumberField(TEXT("chopSeconds"), Seconds) && Seconds > 0.0)
	{
		ChopSeconds = float(Seconds);
	}
	if (Plot->TryGetNumberField(TEXT("rockSeconds"), Seconds) && Seconds > 0.0)
	{
		RockSeconds = float(Seconds);
	}
	BuildTerrain(Tiles, TreeCodes);

	const TArray<TSharedPtr<FJsonValue>>* ObjectList = nullptr;
	if (Plot->TryGetArrayField(TEXT("objects"), ObjectList))
	{
		for (const TSharedPtr<FJsonValue>& Value : *ObjectList)
		{
			AddObject(Value->AsObject());
		}
	}
	UE_LOG(LogFWPlot, Log, TEXT("Built plot %d:%d, biome %d, %d objects, %d mines"), PlotX, PlotY, Biome, Objects.Num(), Mines.Num());
}

FVector AFWPlot::TileCenter(FIntPoint Tile) const
{
	return GetActorTransform().TransformPosition(FVector((Tile.X + 0.5f) * TileSize, (Tile.Y + 0.5f) * TileSize, 0.f));
}

FVector AFWPlot::FootprintCenter(const FString& Kind, FIntPoint Tile) const
{
	const float Shift = 0.5f * (FWCatalog::FootprintHigh(Kind) - FWCatalog::FootprintLow(Kind));
	return TileCenter(Tile) + FVector(Shift * TileSize, Shift * TileSize, 0.f);
}

FIntPoint AFWPlot::TileAt(const FVector& WorldLocation) const
{
	const FVector Local = GetActorTransform().InverseTransformPosition(WorldLocation);
	return FIntPoint(FMath::FloorToInt(Local.X / TileSize), FMath::FloorToInt(Local.Y / TileSize));
}

uint8 AFWPlot::TileCode(FIntPoint Tile) const
{
	return Contains(Tile) && Tiles.IsValidIndex(Tile.Y * Size + Tile.X) ? Tiles[Tile.Y * Size + Tile.X] : uint8(255);
}

FVector AFWPlot::SpawnLocation() const
{
	return GroundAt(Spawn);
}

FVector AFWPlot::GroundAt(FIntPoint Tile) const
{
	// The top of whatever stands there, so a body placed on it stands rather than drops or sticks
	const FVector Center = TileCenter(Tile);
	FHitResult Hit;
	if (GetWorld()->LineTraceSingleByChannel(Hit, Center + FVector(0, 0, 5000.f), Center - FVector(0, 0, 5000.f), ECC_Visibility))
	{
		return Hit.ImpactPoint;
	}
	return Center;
}

const FFWMine* AFWPlot::FindMine(FIntPoint Tile) const
{
	for (const FFWMine& Mine : Mines)
	{
		if (Tile.X >= Mine.Rect.Min.X && Tile.X < Mine.Rect.Max.X && Tile.Y >= Mine.Rect.Min.Y && Tile.Y < Mine.Rect.Max.Y)
		{
			return &Mine;
		}
	}
	return nullptr;
}

const FFWPlotObject* AFWPlot::FindObjectAt(FIntPoint Tile) const
{
	const FString* Id = Occupied.Find(Tile);
	return Id ? Objects.Find(*Id) : nullptr;
}

bool AFWPlot::CanPlace(const FString& Kind, FIntPoint Tile) const
{
	if (!IsPlaceable(Kind))
	{
		return false;
	}
	const int32 Low = FWCatalog::FootprintLow(Kind);
	const int32 High = FWCatalog::FootprintHigh(Kind);
	for (int32 DY = -Low; DY <= High; ++DY)
	{
		for (int32 DX = -Low; DX <= High; ++DX)
		{
			const FIntPoint Cell = Tile + FIntPoint(DX, DY);
			if (!Contains(Cell) || TileCode(Cell) != FWCatalog::Empty || Occupied.Contains(Cell))
			{
				return false;
			}
		}
	}
	return true;
}

void AFWPlot::AddObject(const TSharedPtr<FJsonObject>& Json)
{
	if (!Json.IsValid())
	{
		return;
	}
	FFWPlotObject Object;
	Object.Id = StringField(Json, TEXT("id"));
	Object.Kind = StringField(Json, TEXT("kind"));
	Object.Name = StringField(Json, TEXT("name"));
	Object.Tile = FIntPoint(IntField(Json, TEXT("tileX")), IntField(Json, TEXT("tileY")));
	Object.Rotation = IntField(Json, TEXT("rotation")) & 3;
	RemoveObject(Object.Id);

	const int32 Low = FWCatalog::FootprintLow(Object.Kind);
	const int32 High = FWCatalog::FootprintHigh(Object.Kind);
	for (int32 DY = -Low; DY <= High; ++DY)
	{
		for (int32 DX = -Low; DX <= High; ++DX)
		{
			Occupied.Add(Object.Tile + FIntPoint(DX, DY), Object.Id);
		}
	}
	DrawObject(Object);
	const FString Kind = Object.Kind;
	Objects.Add(Object.Id, MoveTemp(Object));
	RebuildCables();
	if (FWCatalog::IsBelt(Kind) || FWCatalog::IsInserter(Kind))
	{
		RebuildBelts();
	}
}

void AFWPlot::RemoveObject(const FString& Id)
{
	FFWPlotObject Removed;
	if (!Objects.RemoveAndCopyValue(Id, Removed))
	{
		return;
	}
	InserterArms.Remove(Id);
	DrawnBends.Remove(Id);
	ON_SCOPE_EXIT { RebuildCables(); };
	for (UDecalComponent* Decal : Removed.Decals)
	{
		if (Decal)
		{
			Decal->DestroyComponent();
		}
	}
	for (UStaticMeshComponent* Mesh : Removed.Meshes)
	{
		if (Mesh)
		{
			Mesh->DestroyComponent();
		}
	}
	for (auto It = Occupied.CreateIterator(); It; ++It)
	{
		if (It.Value() == Id)
		{
			It.RemoveCurrent();
		}
	}
	if (FWCatalog::IsBelt(Removed.Kind) || FWCatalog::IsInserter(Removed.Kind))
	{
		RebuildBelts();
	}
}

double AFWPlot::ServerNow() const
{
	return ServerAtSync + (FPlatformTime::Seconds() - LocalAtSync) * 1000.0;
}

void AFWPlot::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	UpdateGroundCover();
	AnimateParts(DeltaSeconds);
	UpdateMachineSounds(DeltaSeconds);
	if (PendingScatterBiome >= 0 && TerrainMesh)
	{
		FHitResult Probe;
		const FVector Corner = GetActorTransform().TransformPosition(FVector(-20000.f, -20000.f, 0.f));
		if (TerrainMesh->LineTraceComponent(Probe, Corner + FVector(0, 0, 400000.f), Corner - FVector(0, 0, 100000.f), FCollisionQueryParams(TEXT("FWProbe"), true)))
		{
			const int32 Biome = PendingScatterBiome;
			PendingScatterBiome = -1;
			ScatterSurroundings(Biome);
			PlantForest(Biome);
			PlantDwarfPine(Biome);
		}
	}
	if (SimTime <= 0.0 || !HasLogistics())
	{
		return;
	}
	const double Now = ServerNow();
	// Never run far ahead of the server: it may know of an inserter that picked something up
	int32 Steps = 0;
	while (Now - SimTime >= TickMs && Steps < 10)
	{
		StepLogistics();
		SimTime += TickMs;
		++Steps;
	}
	if (Now - SimTime >= TickMs)
	{
		SimTime = Now - FMath::Fmod(Now - SimTime, TickMs);
	}
	DrawItems();
}
