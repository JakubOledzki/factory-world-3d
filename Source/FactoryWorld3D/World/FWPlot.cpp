#include "World/FWPlot.h"

#include "Components/HierarchicalInstancedStaticMeshComponent.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Game/FWArt.h"
#include "GameFramework/Pawn.h"
#include "GameFramework/PlayerController.h"
#include "Game/FWCatalog.h"
#include "Game/FWGameSettings.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Misc/Base64.h"
#include "Misc/ScopeExit.h"
#include "Misc/Compression.h"
#include "UObject/ConstructorHelpers.h"

DEFINE_LOG_CATEGORY_STATIC(LogFWPlot, Log, All);

namespace
{
	// Basic shapes are 100 units across
	constexpr float ShapeSize = 100.f;
	// Lanes on the ~1.3 m band of the modelled belt
	constexpr float LaneOffset = 0.08f;
	constexpr float BandHeight = 0.11f;

	TArray<uint8> Gunzip(const FString& Base64)
	{
		TArray<uint8> Compressed;
		TArray<uint8> Result;
		if (!FBase64::Decode(Base64, Compressed) || Compressed.Num() < 18)
		{
			return Result;
		}
		// The gzip trailer ends with the uncompressed size (little endian)
		const int32 N = Compressed.Num();
		const int32 RawSize = Compressed[N - 4] | (Compressed[N - 3] << 8) | (Compressed[N - 2] << 16) | (Compressed[N - 1] << 24);
		Result.SetNumUninitialized(RawSize);
		if (!FCompression::UncompressMemory(NAME_Gzip, Result.GetData(), RawSize, Compressed.GetData(), N))
		{
			Result.Reset();
		}
		return Result;
	}

	float YawOf(int32 Rotation)
	{
		const FIntPoint F = FWCatalog::Facing(Rotation);
		return FMath::RadiansToDegrees(FMath::Atan2(float(F.Y), float(F.X)));
	}

	FString StringField(const TSharedPtr<FJsonObject>& Object, const TCHAR* Name)
	{
		FString Value;
		Object->TryGetStringField(Name, Value);
		return Value;
	}

	int32 IntField(const TSharedPtr<FJsonObject>& Object, const TCHAR* Name, int32 Default = 0)
	{
		int32 Value = Default;
		Object->TryGetNumberField(Name, Value);
		return Value;
	}
}

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
	CubeMesh = Cube.Object;
	CylinderMesh = Cylinder.Object;
	SphereMesh = Sphere.Object;
	ConeMesh = Cone.Object;
	SurfaceBase = Surface.Object;
	GroundBase = Ground.Object;
	TerrainBase = Terrain.Object;
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
	TSet<uint8> TreeCodes = { FWCatalog::Tree };
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

UMaterialInterface* AFWPlot::MakeGroundMaterial(int32 Biome)
{
	const FWArt::FLayerSet GroundSet = FWArt::GroundLayers(Biome);
	auto LayerTexture = [](const FString& Set, const TCHAR* Kind)
	{
		// The sets come in 4K or 2K
		for (const TCHAR* Resolution : { TEXT("4k"), TEXT("2k") })
		{
			const FString Name = FString::Printf(TEXT("%s_%s_%s"), *Set, Kind, Resolution);
			if (UTexture2D* Found = FWArt::Texture(*FString::Printf(TEXT("/Game/External/Textures/%s/%s.%s"), *Set, *Name, *Name)))
			{
				return Found;
			}
		}
		return static_cast<UTexture2D*>(nullptr);
	};
	UTexture2D* BaseColor = LayerTexture(GroundSet.Base, TEXT("diff"));
	if (!TerrainBase || !BaseColor)
	{
		// No photographed ground set up: the plain tinted ground
		UMaterialInstanceDynamic* Plain = UMaterialInstanceDynamic::Create(GroundBase, this);
		Plain->SetVectorParameterValue(TEXT("Color"), FWCatalog::BiomeColor(Biome));
		return Plain;
	}
	UMaterialInstanceDynamic* Ground = UMaterialInstanceDynamic::Create(TerrainBase, this);
	const TPair<const TCHAR*, FString> Sets[] = { { TEXT("Base"), GroundSet.Base }, { TEXT("Grass"), GroundSet.Grass }, { TEXT("Cliff"), GroundSet.Cliff }, { TEXT("Snow"), GroundSet.Snow } };
	for (const TPair<const TCHAR*, FString>& Set : Sets)
	{
		const TPair<const TCHAR*, const TCHAR*> Maps[] = { { TEXT("D"), TEXT("diff") }, { TEXT("N"), TEXT("nor_gl") }, { TEXT("M"), TEXT("arm") } };
		for (const TPair<const TCHAR*, const TCHAR*>& Map : Maps)
		{
			if (UTexture2D* Texture = LayerTexture(Set.Value, Map.Value))
			{
				Ground->SetTextureParameterValue(*FString::Printf(TEXT("%s%s"), Set.Key, Map.Key), Texture);
			}
		}
	}
	Ground->SetScalarParameterValue(TEXT("GrassAmount"), GroundSet.GrassAmount);
	Ground->SetScalarParameterValue(TEXT("SnowHeight"), GroundSet.SnowHeightCm);
	Ground->SetScalarParameterValue(TEXT("Scale"), 600.f);
	return Ground;
}

void AFWPlot::BuildGround(int32 Biome)
{
	const float Extent = Size * TileSize;
	UMaterialInterface* Ground = MakeGroundMaterial(Biome);

	// The plot itself: flat, solid, the same ground as the land around
	UStaticMeshComponent* Plot = MakeMesh(CubeMesh, FLinearColor::White,
		FTransform(FRotator::ZeroRotator, FVector(Extent / 2, Extent / 2, -50.f), FVector(Extent / ShapeSize, Extent / ShapeSize, 1.f)));
	Plot->SetMaterial(0, Ground);

	if (UStaticMesh* Land = FWArt::Terrain(Biome, PlotX, PlotY))
	{
		// Made for a 1 km plot centred in it (Tools/blender/make_terrain.py): scale it to this plot and put its middle on the plot's
		const float Fit = Extent / 100000.f;
		const FBoxSphereBounds Bounds = Land->GetBounds();
		TerrainMesh = MakeMesh(Land, FLinearColor::White, FTransform::Identity, true);
		TerrainMesh->SetMaterial(0, Ground);
		TerrainMesh->SetRelativeScale3D(FVector(Fit, Fit, 1.f));
		TerrainMesh->SetRelativeLocation(FVector(Extent / 2 - Bounds.Origin.X * Fit, Extent / 2 - Bounds.Origin.Y * Fit, -1.f));
		// The land's collision is cooked in the background: scatter once traces hit it (see Tick)
		PendingScatterBiome = Biome;
	}
	else
	{
		UMaterialInstanceDynamic* Around = UMaterialInstanceDynamic::Create(GroundBase, this);
		Around->SetVectorParameterValue(TEXT("Color"), FWCatalog::BiomeColor(Biome) * 0.8f);
		UStaticMeshComponent* Land2 = MakeMesh(CubeMesh, FLinearColor::White,
			FTransform(FRotator::ZeroRotator, FVector(Extent / 2, Extent / 2, -70.f), FVector(Extent * 6 / ShapeSize, Extent * 6 / ShapeSize, 1.f)));
		Land2->SetMaterial(0, Around);
	}
}

UHierarchicalInstancedStaticMeshComponent* AFWPlot::MakeArtInstances(UStaticMesh* Mesh, bool bCollide)
{
	UHierarchicalInstancedStaticMeshComponent* Component = NewObject<UHierarchicalInstancedStaticMeshComponent>(this);
	Component->SetStaticMesh(Mesh);
	Component->SetupAttachment(RootComponent);
	Component->SetCollisionEnabled(bCollide ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::NoCollision);
	Component->RegisterComponent();
	return Component;
}

FTransform AFWPlot::ArtTransform(UStaticMesh* Mesh, const FVector& Ground, float Yaw, float Size2D, float Height, float Sink, const FVector& Normal, float Tilt) const
{
	// Scale a scanned mesh to a size (across, or tall when Height > 0) and stand its bottom on the ground,
	// leaning partly with the slope (Tilt 0..1) and sunk deeper the steeper it is, so no edge floats
	const FBoxSphereBounds Bounds = Mesh->GetBounds();
	const float Scale = Height > 0.f
		? Height / FMath::Max(1.f, 2.f * Bounds.BoxExtent.Z)
		: Size2D / FMath::Max(1.f, 2.f * FMath::Max(Bounds.BoxExtent.X, Bounds.BoxExtent.Y));
	const FVector Up = FMath::Lerp(FVector::UpVector, Normal.GetSafeNormal(), Tilt).GetSafeNormal();
	const FQuat Rotation = FQuat::FindBetweenNormals(FVector::UpVector, Up) * FQuat(FVector::UpVector, FMath::DegreesToRadians(Yaw));
	const float Steep = 1.f - FMath::Clamp(Normal.GetSafeNormal().Z, 0.f, 1.f);
	const float Bottom = (Bounds.Origin.Z - Bounds.BoxExtent.Z) * Scale;
	const float Depth = (Sink + Steep * 0.6f) * Scale * Bounds.BoxExtent.Z * 2.f;
	return FTransform(Rotation, Ground - Up * (Bottom + Depth), FVector(Scale));
}

void AFWPlot::ScatterSurroundings(int32 Biome)
{
	const TArray<UStaticMesh*> TreeMeshes = FWArt::Trees();
	const TArray<UStaticMesh*> RockMeshes = FWArt::Rocks();
	const TArray<UStaticMesh*> BushMeshes = FWArt::Bushes();
	const TArray<UStaticMesh*> GrassMeshes = FWArt::Grass();
	if (!TerrainMesh || (TreeMeshes.IsEmpty() && RockMeshes.IsEmpty()))
	{
		return;
	}
	TMap<UStaticMesh*, TArray<FTransform>> Placed;
	const float Extent = Size * TileSize;
	const FVector Middle(Extent / 2, Extent / 2, 0.f);
	FRandomStream Random(PlotX * 7919 + PlotY);
	const bool bForest = Biome != 5 && Biome != 8 && Biome != 12;
	const int32 Count = bForest ? 9000 : 3000;
	for (int32 i = 0; i < Count; ++i)
	{
		// Around the plot, thicker near it
		const float Angle = Random.FRandRange(0.f, 2 * PI);
		const float Distance = Extent * 0.5f + 4000.f + FMath::Pow(Random.FRand(), 1.6f) * 260000.f;
		FVector At = Middle + FVector(FMath::Cos(Angle), FMath::Sin(Angle), 0.f) * Distance * 1.15f;
		if (FMath::Abs(At.X - Middle.X) < Extent * 0.5f + 3000.f && FMath::Abs(At.Y - Middle.Y) < Extent * 0.5f + 3000.f)
		{
			continue;
		}
		FHitResult Hit;
		const FVector World = GetActorTransform().TransformPosition(At);
		if (!TerrainMesh->LineTraceComponent(Hit, World + FVector(0, 0, 400000.f), World - FVector(0, 0, 100000.f), FCollisionQueryParams(TEXT("FWScatter"), true)))
		{
			continue;
		}
		const FVector Ground = GetActorTransform().InverseTransformPosition(Hit.ImpactPoint);
		const float Flatness = Hit.ImpactNormal.Z;
		const float Roll = Random.FRand();
		if (bForest && !TreeMeshes.IsEmpty() && Flatness > 0.82f && Ground.Z < 60000.f && Roll < 0.7f)
		{
			UStaticMesh* Mesh = TreeMeshes[Random.RandHelper(TreeMeshes.Num())];
			Placed.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Ground, Random.FRandRange(0.f, 360.f), 0.f, Random.FRandRange(1100.f, 2200.f), 0.02f));
		}
		else if (!GrassMeshes.IsEmpty() && Roll > 0.45f && Roll < 0.85f && Flatness > 0.85f && Distance < Extent * 0.5f + 60000.f)
		{
			// Tufts of grass and ferns on the gentle ground near the plot
			UStaticMesh* Mesh = GrassMeshes[Random.RandHelper(GrassMeshes.Num())];
			Placed.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Ground, Random.FRandRange(0.f, 360.f), 0.f, Random.FRandRange(40.f, 110.f), 0.05f, Hit.ImpactNormal, 0.8f));
		}
		else if (!RockMeshes.IsEmpty() && Roll < 0.85f)
		{
			UStaticMesh* Mesh = RockMeshes[Random.RandHelper(RockMeshes.Num())];
			Placed.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Ground, Random.FRandRange(0.f, 360.f), Random.FRandRange(300.f, 1800.f), 0.f, 0.3f, Hit.ImpactNormal, 0.7f));
		}
		else if (!BushMeshes.IsEmpty() && Flatness > 0.8f)
		{
			UStaticMesh* Mesh = BushMeshes[Random.RandHelper(BushMeshes.Num())];
			Placed.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Ground, Random.FRandRange(0.f, 360.f), 0.f, Random.FRandRange(80.f, 220.f), 0.08f, Hit.ImpactNormal, 0.5f));
		}
	}
	int32 Total = 0;
	for (TPair<UStaticMesh*, TArray<FTransform>>& Pair : Placed)
	{
		Total += Pair.Value.Num();
		// Scattered surroundings stay out of ray tracing: they would fill its always-resident memory
		UHierarchicalInstancedStaticMeshComponent* Scatter = MakeArtInstances(Pair.Key, false);
		Scatter->SetVisibleInRayTracing(false);
		Scatter->AddInstances(Pair.Value, false);
	}
	UE_LOG(LogFWPlot, Log, TEXT("Scattered %d trees, rocks and bushes around the plot"), Total);
}

void AFWPlot::BuildTerrain(const TArray<uint8>& Codes, const TSet<uint8>& TreeCodes)
{
	const float T = TileSize / ShapeSize;
	const TArray<UStaticMesh*> RockMeshes = FWArt::Rocks();
	const TArray<UStaticMesh*> TreeMeshes = FWArt::Trees();
	const TArray<UStaticMesh*> BushMeshes = FWArt::Bushes();

	UHierarchicalInstancedStaticMeshComponent* Trunks = MakeInstances(CylinderMesh, FLinearColor(0.12f, 0.07f, 0.04f), 0.9f, true);
	UHierarchicalInstancedStaticMeshComponent* Crowns = MakeInstances(ConeMesh, FLinearColor(0.03f, 0.09f, 0.03f), 0.85f, false);
	UHierarchicalInstancedStaticMeshComponent* Rocks = MakeInstances(SphereMesh, FLinearColor(0.13f, 0.125f, 0.115f), 0.8f, true);
	UHierarchicalInstancedStaticMeshComponent* Dunes = MakeInstances(SphereMesh, FLinearColor(0.6f, 0.47f, 0.28f), 0.95f, false);
	UHierarchicalInstancedStaticMeshComponent* Bushes = MakeInstances(SphereMesh, FLinearColor(0.05f, 0.12f, 0.04f), 0.9f, false);
	UHierarchicalInstancedStaticMeshComponent* Rails = MakeInstances(CubeMesh, FLinearColor(0.25f, 0.24f, 0.24f), 0.35f, false);
	UHierarchicalInstancedStaticMeshComponent* Sleepers = MakeInstances(CubeMesh, FLinearColor(0.18f, 0.12f, 0.08f), 0.9f, false);
	UHierarchicalInstancedStaticMeshComponent* Platform = MakeInstances(CubeMesh, FLinearColor(0.45f, 0.44f, 0.42f), 0.75f, true);
	UHierarchicalInstancedStaticMeshComponent* MineRock = MakeInstances(CubeMesh, FLinearColor(0.15f, 0.14f, 0.13f), 0.85f, true);
	Rails->SetMaterial(0, SurfaceMaterial(FLinearColor(0.25f, 0.24f, 0.24f), 0.35f, 1.f));

	TArray<FTransform> TrunkT, CrownT, RockT, DuneT, BushT, RailT, SleeperT, PlatformT, MineT;
	// Modelled track and platform pieces (one per tile, 4 m) when the factory models are set up
	UStaticMesh* RailArt = FWArt::Factory(TEXT("rails"));
	UStaticMesh* PlatformArt = FWArt::Factory(TEXT("platform"));
	TArray<FTransform> RailArtT, PlatformArtT;
	const FVector ArtScale(TileSize / 400.f);
	TMap<UStaticMesh*, TArray<FTransform>> ArtSolid, ArtSoft;
	for (int32 Y = 0; Y < Size; ++Y)
	{
		for (int32 X = 0; X < Size; ++X)
		{
			const uint8 Code = Codes[Y * Size + X];
			FRandomStream Random(Y * Size + X + 1);
			if (Code == FWCatalog::Empty)
			{
				// Free ground: grass comes from UpdateGroundCover around the player
				continue;
			}
			const FVector Center = TileCenter(FIntPoint(X, Y));
			const FVector Jitter(Random.FRandRange(-0.2f, 0.2f) * TileSize, Random.FRandRange(-0.2f, 0.2f) * TileSize, 0.f);
			const FRotator Spin(0.f, Random.FRandRange(0.f, 360.f), 0.f);
			if (TreeCodes.Contains(Code))
			{
				if (!TreeMeshes.IsEmpty())
				{
					UStaticMesh* Mesh = TreeMeshes[Random.RandHelper(TreeMeshes.Num())];
					ArtSolid.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Center + Jitter, Spin.Yaw, 0.f, Random.FRandRange(900.f, 1600.f), 0.02f));
					continue;
				}
				const float Height = Random.FRandRange(1.6f, 2.6f) * TileSize;
				TrunkT.Add(FTransform(Spin, Center + Jitter + FVector(0, 0, Height * 0.25f), FVector(0.12f * T, 0.12f * T, Height * 0.5f / ShapeSize)));
				CrownT.Add(FTransform(Spin, Center + Jitter + FVector(0, 0, Height * 0.65f), FVector(0.7f * T, 0.7f * T, Height * 0.8f / ShapeSize)));
			}
			else if (Code == FWCatalog::Rock)
			{
				if (!RockMeshes.IsEmpty())
				{
					UStaticMesh* Mesh = RockMeshes[Random.RandHelper(RockMeshes.Num())];
					// Kept inside their own tile, so they never reach into a neighbouring belt or machine
					ArtSolid.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Center + Jitter * 0.3f, Spin.Yaw, Random.FRandRange(0.5f, 0.75f) * TileSize, 0.f, 0.2f));
					continue;
				}
				const float S = Random.FRandRange(0.45f, 0.8f) * T;
				RockT.Add(FTransform(FRotator(Random.FRandRange(-20.f, 20.f), Spin.Yaw, Random.FRandRange(-20.f, 20.f)), Center + Jitter + FVector(0, 0, S * ShapeSize * 0.2f),
					FVector(S * Random.FRandRange(0.8f, 1.3f), S, S * Random.FRandRange(0.5f, 0.9f))));
			}
			else if (Code == FWCatalog::Dune)
			{
				DuneT.Add(FTransform(Spin, Center, FVector(1.3f * T, 1.1f * T, 0.35f * T)));
			}
			else if (Code == FWCatalog::Bush)
			{
				if (!BushMeshes.IsEmpty())
				{
					UStaticMesh* Mesh = BushMeshes[Random.RandHelper(BushMeshes.Num())];
					ArtSoft.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Center + Jitter, Spin.Yaw, 0.f, Random.FRandRange(90.f, 200.f), 0.05f));
					continue;
				}
				const float S = Random.FRandRange(0.3f, 0.5f) * T;
				BushT.Add(FTransform(Spin, Center + Jitter + FVector(0, 0, S * 30.f), FVector(S, S, S * 0.75f)));
			}
			else if (Code == FWCatalog::Rails)
			{
				// Rails run along the row (east-west)
				if (RailArt)
				{
					RailArtT.Add(FTransform(FRotator::ZeroRotator, Center, ArtScale));
					continue;
				}
				for (const float Side : { -0.18f, 0.18f })
				{
					RailT.Add(FTransform(FRotator::ZeroRotator, Center + FVector(0, Side * TileSize, 12.f), FVector(T, 0.03f * T, 0.06f)));
				}
				for (const float Along : { -0.25f, 0.25f })
				{
					SleeperT.Add(FTransform(FRotator::ZeroRotator, Center + FVector(Along * TileSize, 0, 4.f), FVector(0.12f * T, 0.6f * T, 0.08f)));
				}
			}
			else if (Code == FWCatalog::Station)
			{
				if (PlatformArt)
				{
					PlatformArtT.Add(FTransform(FRotator::ZeroRotator, Center, ArtScale));
					continue;
				}
				PlatformT.Add(FTransform(FRotator::ZeroRotator, Center + FVector(0, 0, 40.f), FVector(T, T, 0.8f)));
			}
			else if (Code == FWCatalog::Mine)
			{
				if (!RockMeshes.IsEmpty())
				{
					// Big boulders heaped over the mine's tiles
					UStaticMesh* Mesh = RockMeshes[Random.RandHelper(FMath::Min(3, RockMeshes.Num()))];
					ArtSolid.FindOrAdd(Mesh).Add(ArtTransform(Mesh, Center, Spin.Yaw, Random.FRandRange(1.3f, 1.8f) * TileSize, 0.f, 0.3f));
					continue;
				}
				const float H = Random.FRandRange(0.9f, 1.6f);
				MineT.Add(FTransform(FRotator(0, Spin.Yaw, 0), Center + FVector(0, 0, H * TileSize * 0.3f), FVector(0.95f * T, 0.95f * T, H * 0.6f * T)));
			}
		}
	}
	Trunks->AddInstances(TrunkT, false);
	Crowns->AddInstances(CrownT, false);
	Rocks->AddInstances(RockT, false);
	Dunes->AddInstances(DuneT, false);
	Bushes->AddInstances(BushT, false);
	Rails->AddInstances(RailT, false);
	Sleepers->AddInstances(SleeperT, false);
	Platform->AddInstances(PlatformT, false);
	MineRock->AddInstances(MineT, false);
	if (RailArt)
	{
		MakeArtInstances(RailArt, false)->AddInstances(RailArtT, false);
	}
	if (PlatformArt)
	{
		MakeArtInstances(PlatformArt, true)->AddInstances(PlatformArtT, false);
	}
	for (TPair<UStaticMesh*, TArray<FTransform>>& Pair : ArtSolid)
	{
		MakeArtInstances(Pair.Key, true)->AddInstances(Pair.Value, false);
	}
	for (TPair<UStaticMesh*, TArray<FTransform>>& Pair : ArtSoft)
	{
		MakeArtInstances(Pair.Key, false)->AddInstances(Pair.Value, false);
	}

	// A dark entrance on the front of each mine
	for (const FFWMine& Mine : Mines)
	{
		const FVector Front = TileCenter(FIntPoint(Mine.Rect.Min.X + Mine.Rect.Width() / 2, Mine.Rect.Max.Y - 1)) + FVector(0, TileSize * 0.48f, TileSize * 0.3f);
		MakeMesh(CubeMesh, FLinearColor(0.01f, 0.01f, 0.01f), FTransform(FRotator::ZeroRotator, Front, FVector(0.5f * T, 0.05f * T, 0.55f * T)), false);
		for (const float Side : { -0.3f, 0.3f })
		{
			MakeMesh(CubeMesh, FLinearColor(0.3f, 0.2f, 0.1f),
				FTransform(FRotator::ZeroRotator, Front + FVector(Side * TileSize, 4.f, 0.f), FVector(0.06f * T, 0.06f * T, 0.6f * T)), false);
		}
	}
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

UStaticMeshComponent* AFWPlot::MakeArtMesh(UStaticMesh* Mesh, const FTransform& Transform, bool bCollide)
{
	UStaticMeshComponent* Component = NewObject<UStaticMeshComponent>(this);
	Component->SetStaticMesh(Mesh);
	Component->SetupAttachment(RootComponent);
	Component->SetRelativeTransform(Transform);
	Component->SetCollisionEnabled(bCollide ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::NoCollision);
	Component->RegisterComponent();
	return Component;
}

bool AFWPlot::DrawArtObject(FFWPlotObject& Object)
{
	// The models are built for a 4 m tile
	const float Fit = TileSize / 400.f;
	const FVector Center = FootprintCenter(Object.Kind, Object.Tile);
	const FRotator Facing(0.f, YawOf(Object.Rotation), 0.f);
	const FString& Kind = Object.Kind;
	auto Place = [&](UStaticMesh* Mesh, const FVector& Offset, const FRotator& Turn, float Scale, bool bCollide) -> UStaticMeshComponent*
	{
		UStaticMeshComponent* Component = MakeArtMesh(Mesh, FTransform(Facing + Turn, Center + Facing.RotateVector(Offset * Fit), FVector(Scale * Fit)), bCollide);
		Object.Meshes.Add(Component);
		return Component;
	};

	if (FWCatalog::IsBelt(Kind))
	{
		UStaticMesh* Straight = FWArt::Factory(TEXT("belt_straight"));
		if (!Straight)
		{
			return false;
		}
		// Curves are swapped in by RefreshBeltArt once the belt's neighbours are known
		Place(Straight, FVector::ZeroVector, FRotator::ZeroRotator, 1.f, false);
		DrawnBends.Add(Object.Id, 0);
		return true;
	}
	if (FWCatalog::IsInserter(Kind))
	{
		UStaticMesh* Base = FWArt::Factory(TEXT("inserter_base"));
		UStaticMesh* Arm = FWArt::Factory(TEXT("inserter_arm"));
		if (!Base || !Arm)
		{
			return false;
		}
		// 1.5x: the arm then reaches to the edge of the tiles it takes from and drops on
		constexpr float InserterScale = 1.5f;
		Place(Base, FVector::ZeroVector, FRotator::ZeroRotator, InserterScale, true);
		// The arm turns about the top of the column; AnimateParts swings it between the tiles behind and in front
		InserterArms.Add(Object.Id, Place(Arm, FVector(0, 0, 82.f * InserterScale), FRotator(0.f, 180.f, 0.f), InserterScale, false));
		return true;
	}
	if (FWCatalog::IsStorage(Kind))
	{
		UStaticMesh* Chest = FWArt::Factory(TEXT("chest"));
		if (!Chest)
		{
			return false;
		}
		const float N = FWCatalog::FootprintSize(Kind);
		// A chest you could stand behind: about 2.1 x 1.6 x 1.2 m
		Place(Chest, FVector::ZeroVector, FRotator::ZeroRotator, N * (Kind.Contains(TEXT("iron")) ? 1.4f : 1.25f), true);
		return true;
	}
	if (Kind == TEXT("power_pole") || Kind == TEXT("power_substation") || Kind == TEXT("power_pylon"))
	{
		UStaticMesh* Pole = FWArt::Factory(TEXT("pole"));
		if (!Pole)
		{
			return false;
		}
		Place(Pole, FVector::ZeroVector, FRotator(0.f, 90.f, 0.f), Kind == TEXT("power_pylon") ? 1.8f : 1.f, true);
		return true;
	}
	if (Kind == TEXT("power_solar_panel"))
	{
		UStaticMesh* Panel = FWArt::Factory(TEXT("solar_panel"));
		if (!Panel)
		{
			return false;
		}
		Place(Panel, FVector::ZeroVector, FRotator::ZeroRotator, 1.25f, true);
		return true;
	}
	if (Kind == TEXT("power_wind_turbine") || Kind == TEXT("power_large_wind_turbine"))
	{
		UStaticMesh* Tower = FWArt::Factory(TEXT("turbine_tower"));
		UStaticMesh* Rotor = FWArt::Factory(TEXT("turbine_rotor"));
		if (!Tower || !Rotor)
		{
			return false;
		}
		const float Scale = Kind == TEXT("power_large_wind_turbine") ? 2.f : 1.f;
		Place(Tower, FVector::ZeroVector, FRotator::ZeroRotator, Scale, true);
		UStaticMeshComponent* Spinning = Place(Rotor, FVector(165.f, 0.f, 1440.f) * Scale, FRotator::ZeroRotator, Scale, false);
		Spinning->SetVisibleInRayTracing(false);
		TurbineRotors.Add(Spinning);
		return true;
	}
	// Anything else that stands on the plot: the generic machine at the size of its footprint
	if (UStaticMesh* Machine = FWArt::Factory(TEXT("machine")))
	{
		Place(Machine, FVector::ZeroVector, FRotator::ZeroRotator, float(FWCatalog::FootprintSize(Kind)), true);
		return true;
	}
	return false;
}

void AFWPlot::RefreshBeltArt()
{
	UStaticMesh* Straight = FWArt::Factory(TEXT("belt_straight"));
	UStaticMesh* Curve = FWArt::Factory(TEXT("belt_curve"));
	if (!Straight || !Curve)
	{
		return;
	}
	const float Fit = TileSize / 400.f;
	for (const TPair<FIntPoint, FBelt>& Pair : Belts)
	{
		const FBelt& Belt = Pair.Value;
		FFWPlotObject* Object = Objects.Find(Belt.Id);
		const int32* Drawn = DrawnBends.Find(Belt.Id);
		if (!Object || !Drawn || *Drawn == Belt.Bend || Object->Meshes.Num() != 1)
		{
			continue;
		}
		// The curve model comes in from its back (-X) and leaves to its left (+Y): turned a quarter so it
		// leaves the way the belt faces, and mirrored for belts fed from their left
		const float Yaw = YawOf(Belt.Rotation);
		const FTransform Transform = Belt.Bend == 0
			? FTransform(FRotator(0.f, Yaw, 0.f), TileCenter(Pair.Key), FVector(Fit))
			: Belt.Bend > 0
				? FTransform(FRotator(0.f, Yaw - 90.f, 0.f), TileCenter(Pair.Key), FVector(Fit))
				: FTransform(FRotator(0.f, Yaw + 90.f, 0.f), TileCenter(Pair.Key), FVector(Fit, -Fit, Fit));
		Object->Meshes[0]->SetStaticMesh(Belt.Bend == 0 ? Straight : Curve);
		Object->Meshes[0]->SetRelativeTransform(Transform);
		DrawnBends[Belt.Id] = Belt.Bend;
	}
}

void AFWPlot::RebuildCables()
{
	if (!Cables)
	{
		Cables = NewObject<UInstancedStaticMeshComponent>(this);
		Cables->SetStaticMesh(CylinderMesh);
		Cables->SetMaterial(0, SurfaceMaterial(FLinearColor(0.02f, 0.02f, 0.022f), 0.45f, 0.f));
		Cables->SetupAttachment(RootComponent);
		Cables->SetCollisionEnabled(ECollisionEnabled::NoCollision);
		Cables->SetVisibleInRayTracing(false);
		Cables->RegisterComponent();
	}
	Cables->ClearInstances();

	// Poles join the poles within wire reach (7 tiles) and feed what stands within 2 tiles of them
	const float Fit = TileSize / 400.f;
	auto IsPole = [](const FString& Kind) { return Kind == TEXT("power_pole") || Kind == TEXT("power_substation") || Kind == TEXT("power_pylon"); };
	auto Powered = [](const FString& Kind) { return Kind.StartsWith(TEXT("power_")) || FWCatalog::IsInserter(Kind) || Kind.StartsWith(TEXT("furniture_")); };
	TArray<const FFWPlotObject*> Poles;
	for (const TPair<FString, FFWPlotObject>& Pair : Objects)
	{
		if (IsPole(Pair.Value.Kind))
		{
			Poles.Add(&Pair.Value);
		}
	}
	// Where a cable attaches: the pole's crossarm, or the top of a powered object
	auto Attach = [&](const FFWPlotObject& Object, float Side) -> FVector
	{
		if (IsPole(Object.Kind))
		{
			return FootprintCenter(Object.Kind, Object.Tile) + FVector(0.f, Side * 95.f * Fit, 790.f * Fit);
		}
		if (FWCatalog::IsInserter(Object.Kind))
		{
			// The terminal box on the base, beside the turning arm
			const FRotator Facing(0.f, YawOf(Object.Rotation), 0.f);
			return FootprintCenter(Object.Kind, Object.Tile) + Facing.RotateVector(FVector(-30.f, 45.f, 40.f) * Fit);
		}
		const float Height = Object.Kind == TEXT("power_wind_turbine") ? 200.f : 120.f;
		return FootprintCenter(Object.Kind, Object.Tile) + FVector(0.f, 0.f, Height * Fit);
	};
	TArray<FTransform> Segments;
	auto Span = [&](const FVector& A, const FVector& B)
	{
		// A catenary approximated by a parabola: sag about 3% of the span, at least 30 cm
		const float Length = FVector::Dist(A, B);
		const float Sag = FMath::Max(30.f * Fit, Length * 0.03f);
		const int32 Count = FMath::Clamp(FMath::CeilToInt(Length / (60.f * Fit)), 4, 48);
		FVector Previous = A;
		for (int32 i = 1; i <= Count; ++i)
		{
			const float T = float(i) / Count;
			const FVector Point = FMath::Lerp(A, B, T) - FVector(0.f, 0.f, 4.f * Sag * T * (1.f - T));
			const FVector Delta = Point - Previous;
			// The cylinder is 100 units tall along Z: stretch it along the segment, 2.4 cm thick
			Segments.Add(FTransform(FRotationMatrix::MakeFromZ(Delta).Rotator(), (Point + Previous) * 0.5f,
				FVector(0.024f * Fit, 0.024f * Fit, Delta.Size() / 100.f)));
			Previous = Point;
		}
	};
	for (int32 i = 0; i < Poles.Num(); ++i)
	{
		const FFWPlotObject& Pole = *Poles[i];
		for (int32 j = i + 1; j < Poles.Num(); ++j)
		{
			const FIntPoint D = Poles[j]->Tile - Pole.Tile;
			if (FMath::Max(FMath::Abs(D.X), FMath::Abs(D.Y)) <= 7)
			{
				for (const float Side : { -1.f, 1.f })
				{
					Span(Attach(Pole, Side), Attach(*Poles[j], Side));
				}
			}
		}
		for (const TPair<FString, FFWPlotObject>& Pair : Objects)
		{
			const FFWPlotObject& Other = Pair.Value;
			const FIntPoint D = Other.Tile - Pole.Tile;
			if (!IsPole(Other.Kind) && Powered(Other.Kind) && FMath::Max(FMath::Abs(D.X), FMath::Abs(D.Y)) <= 2)
			{
				Span(Attach(Pole, 0.f) - FVector(0, 0, 60.f * Fit), Attach(Other, 0.f));
			}
		}
	}
	Cables->AddInstances(Segments, false);
}

void AFWPlot::AnimateParts(float DeltaSeconds)
{
	// Rotors turn slowly whatever the wind; inserter arms follow their swing between pick-up and drop
	for (UStaticMeshComponent* Rotor : TurbineRotors)
	{
		if (Rotor)
		{
			Rotor->AddLocalRotation(FRotator(0.f, 0.f, 70.f * DeltaSeconds));
		}
	}
	if (InserterArms.IsEmpty())
	{
		return;
	}
	const float Fraction = FMath::Clamp(float((ServerNow() - SimTime) / TickMs), 0.f, 1.f);
	for (const TPair<FIntPoint, FInserter>& Pair : Inserters)
	{
		const FInserter& Inserter = Pair.Value;
		const TObjectPtr<UStaticMeshComponent>* Arm = InserterArms.Find(Inserter.Id);
		if (!Arm || !*Arm)
		{
			continue;
		}
		const float Swing = FMath::Lerp(float(Inserter.Was), float(Inserter.Swing), Fraction) / FMath::Max(1, Inserter.SwingTicks);
		// 0 points back at the tile it takes from, 1 forward at the tile it drops on
		const float Yaw = YawOf(Inserter.Rotation) + 180.f * (1.f - FMath::SmoothStep(0.f, 1.f, Swing));
		(*Arm)->SetRelativeRotation(FRotator(0.f, Yaw, 0.f));
	}
}

void AFWPlot::DrawObject(FFWPlotObject& Object)
{
	if (DrawArtObject(Object))
	{
		return;
	}
	const float T = TileSize / ShapeSize;
	const FVector Center = FootprintCenter(Object.Kind, Object.Tile);
	const FRotator Facing(0.f, YawOf(Object.Rotation), 0.f);
	const FLinearColor Color = FWCatalog::ColorFor(Object.Kind);
	auto Add = [&](UStaticMesh* Mesh, const FLinearColor& C, const FVector& Offset, const FVector& Scale, bool bCollide = true)
	{
		// Offsets are in the object's own frame: X along the way it faces
		Object.Meshes.Add(MakeMesh(Mesh, C, FTransform(Facing, Center + Facing.RotateVector(Offset), Scale), bCollide));
	};

	if (FWCatalog::IsBelt(Object.Kind))
	{
		// Frame, then the dark band on top; items ride on the band
		Add(CubeMesh, FLinearColor(0.3f, 0.3f, 0.32f), FVector(0, 0, 0.05f * TileSize), FVector(T, 0.98f * T, 0.1f * T), false);
		Add(CubeMesh, Color, FVector(0, 0, 0.1f * TileSize), FVector(T, 0.8f * T, 0.02f * T), false);
		// Chevron showing the way it runs
		for (const float Side : { -1.f, 1.f })
		{
			UStaticMeshComponent* Arrow = MakeMesh(CubeMesh, FLinearColor(0.8f, 0.6f, 0.1f),
				FTransform(Facing + FRotator(0, -Side * 35.f, 0), Center + Facing.RotateVector(FVector(0.15f * TileSize, Side * 0.1f * TileSize, 0.115f * TileSize)),
					FVector(0.3f * T, 0.03f * T, 0.01f * T)), false);
			Object.Meshes.Add(Arrow);
		}
	}
	else if (FWCatalog::IsInserter(Object.Kind))
	{
		Add(CylinderMesh, FLinearColor(0.2f, 0.2f, 0.2f), FVector(0, 0, 0.06f * TileSize), FVector(0.35f * T, 0.35f * T, 0.12f * T));
		Add(CylinderMesh, Color, FVector(0, 0, 0.3f * TileSize), FVector(0.1f * T, 0.1f * T, 0.4f * T));
		Add(CubeMesh, Color, FVector(0, 0, 0.5f * TileSize), FVector(1.2f * T, 0.08f * T, 0.08f * T), false);
	}
	else if (Object.Kind == TEXT("power_pole") || Object.Kind == TEXT("power_substation") || Object.Kind == TEXT("power_pylon"))
	{
		const float H = Object.Kind == TEXT("power_pylon") ? 2.5f : 1.4f;
		Add(CylinderMesh, FLinearColor(0.3f, 0.2f, 0.12f), FVector(0, 0, 0.5f * H * TileSize), FVector(0.06f * T, 0.06f * T, H * T));
		Add(CubeMesh, FLinearColor(0.3f, 0.2f, 0.12f), FVector(0, 0, 0.95f * H * TileSize), FVector(0.05f * T, 0.5f * T, 0.05f * T), false);
	}
	else if (Object.Kind == TEXT("power_solar_panel"))
	{
		Add(CylinderMesh, FLinearColor(0.4f, 0.4f, 0.42f), FVector(0, 0, 0.2f * TileSize), FVector(0.06f * T, 0.06f * T, 0.4f * T));
		Object.Meshes.Add(MakeMesh(CubeMesh, FLinearColor(0.02f, 0.04f, 0.1f), FTransform(FRotator(25.f, Facing.Yaw, 0.f),
			Center + FVector(0, 0, 0.45f * TileSize), FVector(0.85f * T, 0.85f * T, 0.03f * T))));
		Object.Meshes.Last()->SetMaterial(0, SurfaceMaterial(FLinearColor(0.02f, 0.04f, 0.1f), 0.15f, 0.6f));
	}
	else if (Object.Kind == TEXT("power_wind_turbine"))
	{
		const float H = 3.f;
		Add(CylinderMesh, FLinearColor(0.85f, 0.86f, 0.88f), FVector(0, 0, 0.5f * H * TileSize), FVector(0.12f * T, 0.12f * T, H * T));
		Add(CubeMesh, FLinearColor(0.85f, 0.86f, 0.88f), FVector(0.1f * TileSize, 0, H * TileSize), FVector(0.35f * T, 0.15f * T, 0.15f * T), false);
		for (int32 Blade = 0; Blade < 3; ++Blade)
		{
			Object.Meshes.Add(MakeMesh(CubeMesh, FLinearColor(0.9f, 0.9f, 0.92f),
				FTransform(FRotator(0.f, Facing.Yaw, Blade * 120.f), Center + Facing.RotateVector(FVector(0.3f * TileSize, 0, H * TileSize))
					+ FRotator(0.f, Facing.Yaw, Blade * 120.f).RotateVector(FVector(0, 0, 0.6f * TileSize)), FVector(0.03f * T, 0.12f * T, 1.2f * T)), false));
		}
	}
	else if (FWCatalog::IsStorage(Object.Kind))
	{
		Add(CubeMesh, Color, FVector(0, 0, 0.3f * TileSize), FVector(0.8f * T, 0.6f * T, 0.6f * T));
		Add(CubeMesh, Color * 0.6f, FVector(0, 0, 0.62f * TileSize), FVector(0.82f * T, 0.62f * T, 0.05f * T), false);
	}
	else
	{
		// A machine or building: a block on a slab the size of its square
		const float N = FWCatalog::FootprintSize(Object.Kind);
		Add(CubeMesh, FLinearColor(0.35f, 0.35f, 0.35f), FVector(0, 0, 0.03f * TileSize), FVector(N * T, N * T, 0.06f * T), false);
		Add(CubeMesh, Color, FVector(0, 0, 0.5f * TileSize * FMath::Min(N, 2.f)), FVector(0.85f * N * T, 0.85f * N * T, FMath::Min(N, 2.f) * T));
	}
}

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

double AFWPlot::ServerNow() const
{
	return ServerAtSync + (FPlatformTime::Seconds() - LocalAtSync) * 1000.0;
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

void AFWPlot::UpdateGroundCover()
{
	// Dense grass only around the player: thousands of tufts within a few tens of metres, laid out
	// again when they have walked away and thinning towards the edge
	const APlayerController* PC = GetWorld()->GetFirstPlayerController();
	const APawn* Viewer = PC ? PC->GetPawn() : nullptr;
	if (!Viewer || GroundCoverMeshes.IsEmpty())
	{
		return;
	}
	const FVector Here = GetActorTransform().InverseTransformPosition(Viewer->GetActorLocation());
	if (FVector::Dist2D(Here, GroundCoverCentre) < 1000.f)
	{
		return;
	}
	GroundCoverCentre = Here;
	// Two layers: thick short grass close by, sparser bigger clumps out to 150 m so the meadow does
	// not turn into flat ground just past the near patch
	struct FLayer { float Inner, Radius, Spacing, NearDensity, FarDensity, MinSize, MaxSize; };
	const FLayer CoverLayers[] = { { 0.f, 3500.f, 45.f, 0.85f, 0.1f, 30.f, 85.f }, { 3000.f, 15000.f, 220.f, 0.7f, 0.15f, 70.f, 150.f } };
	for (int32 L = 0; L < 2; ++L)
	{
		const FLayer& Layer = CoverLayers[L];
		TArray<TArray<FTransform>> ByMesh;
		ByMesh.SetNum(GroundCoverMeshes.Num());
		const int32 Cells = FMath::CeilToInt(Layer.Radius / Layer.Spacing);
		for (int32 GY = -Cells; GY <= Cells; ++GY)
		{
			for (int32 GX = -Cells; GX <= Cells; ++GX)
			{
				// Cells are fixed to the world grid, so a tuft stays put when the patch is laid again
				const int32 WX = FMath::FloorToInt(Here.X / Layer.Spacing) + GX;
				const int32 WY = FMath::FloorToInt(Here.Y / Layer.Spacing) + GY;
				FRandomStream Random((WX * 73856093) ^ (WY * 19349663) ^ (L * 83492791));
				const FVector At(WX * Layer.Spacing + Random.FRandRange(0.f, Layer.Spacing), WY * Layer.Spacing + Random.FRandRange(0.f, Layer.Spacing), 0.f);
				const float Distance = FVector::Dist2D(At, Here);
				if (Distance > Layer.Radius || Distance < Layer.Inner
					|| Random.FRand() > FMath::Lerp(Layer.NearDensity, Layer.FarDensity, Distance / Layer.Radius))
				{
					continue;
				}
				const FIntPoint Tile = TileAt(GetActorTransform().TransformPosition(At));
				if (!Contains(Tile) || TileCode(Tile) != FWCatalog::Empty || Occupied.Contains(Tile))
				{
					continue;
				}
				const int32 Index = Random.RandHelper(GroundCoverMeshes.Num());
				ByMesh[Index].Add(ArtTransform(GroundCoverMeshes[Index], At, Random.FRandRange(0.f, 360.f), 0.f, Random.FRandRange(Layer.MinSize, Layer.MaxSize), 0.05f));
			}
		}
		for (int32 i = 0; i < GroundCoverMeshes.Num(); ++i)
		{
			const int32 Part = L * GroundCoverMeshes.Num() + i;
			if (!GroundCoverParts.IsValidIndex(Part))
			{
				UHierarchicalInstancedStaticMeshComponent* Component = MakeArtInstances(GroundCoverMeshes[i], false);
				Component->SetVisibleInRayTracing(false);
				Component->SetCastShadow(L == 0);
				GroundCoverParts.Add(Component);
			}
			GroundCoverParts[Part]->ClearInstances();
			GroundCoverParts[Part]->AddInstances(ByMesh[i], false);
		}
	}
}

void AFWPlot::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);
	UpdateGroundCover();
	AnimateParts(DeltaSeconds);
	if (PendingScatterBiome >= 0 && TerrainMesh)
	{
		FHitResult Probe;
		const FVector Corner = GetActorTransform().TransformPosition(FVector(-20000.f, -20000.f, 0.f));
		if (TerrainMesh->LineTraceComponent(Probe, Corner + FVector(0, 0, 400000.f), Corner - FVector(0, 0, 100000.f), FCollisionQueryParams(TEXT("FWProbe"), true)))
		{
			const int32 Biome = PendingScatterBiome;
			PendingScatterBiome = -1;
			ScatterSurroundings(Biome);
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
