// The plot's land: terrain, ground, forest and what grows and lies around it.
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
	GroundMaterial = Ground;
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
	SnowHeightCm = GroundSet.SnowHeightCm;
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
		const FFWPlotElevation* Override = UFWGameSettings::Get().FindElevation(PlotX, PlotY);
		if (Override)
		{
			PlotElevationMeters = Override->Meters;
		}
		else if (const UFWTerrainInfo* Info = Land->GetAssetUserData<UFWTerrainInfo>())
		{
			PlotElevationMeters = Info->PlotElevationMeters;
		}
		if (PlotElevationMeters.IsSet() && GroundMaterial)
		{
			// Forest floor under the stands and canopy colour far away, by the same rule as the trees
			// (PlantForest); the plot and its margin stay clear
			const float Height = PlotElevationMeters.GetValue();
			const FVector2D Bands = UFWGameSettings::Get().ForestBandsAbovePlot(PlotElevationMeters);
			const FVector Centre = GetActorTransform().TransformPosition(FVector(Extent / 2, Extent / 2, 0.f));
			// The material reckons altitude from world Z: shift by the plot's own world height
			GroundMaterial->SetScalarParameterValue(TEXT("PlotElevation"), Height - Centre.Z / 100.f);
			GroundMaterial->SetScalarParameterValue(TEXT("ForestTop"), Height + Bands.X);
			GroundMaterial->SetScalarParameterValue(TEXT("TreeLine"), Height + Bands.Y);
			GroundMaterial->SetScalarParameterValue(TEXT("ClearX"), Centre.X);
			GroundMaterial->SetScalarParameterValue(TEXT("ClearY"), Centre.Y);
			GroundMaterial->SetScalarParameterValue(TEXT("ClearHalf"), Extent * 0.5f + ForestMargin);
		}
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
		if (!GrassMeshes.IsEmpty() && Roll > 0.45f && Roll < 0.85f && Flatness > 0.85f && Distance < Extent * 0.5f + 60000.f)
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

void AFWPlot::PlantForest(int32 Biome)
{
	const TArray<UStaticMesh*> Trees = FWArt::Trees();
	const TArray<UStaticMesh*> TreeLine = FWArt::TreeLineTrees();
	const TArray<UStaticMesh*> Young = FWArt::TitanSaplings();
	if (!TerrainMesh || Trees.IsEmpty() || Biome == 5 || Biome == 8 || Biome == 12)
	{
		return;
	}
	// Dense up to the forest top, thinning to the tree line: by default bands above the plot, so the plot stands
	// at a forest edge; or the real tree line by altitude (config, see UFWGameSettings)
	const FVector2D Bands = UFWGameSettings::Get().ForestBandsAbovePlot(PlotElevationMeters) * 100.f;
	const float ForestTop = Bands.X;
	const float TreeLineTop = Bands.Y;
	// A bounded budget, thinning past ~6 km where a tree is a few pixels and the canopy colour
	// in the ground carries the forest
	constexpr int32 Budget = 28000;
	// Past ~1 km trees cast no shadows: the canopy colour in the ground shades the far forest
	constexpr float ShadowDistance = 100000.f;
	int32 Count = 0;
	TArray<FTransform> Trunks;
	const float Extent = Size * TileSize;
	const FVector Middle(Extent / 2, Extent / 2, 0.f);
	FRandomStream Random(PlotX * 31337 + PlotY * 7);
	TMap<UStaticMesh*, TArray<FTransform>> Placed[2];
	for (int32 i = 0; i < 1200000; ++i)
	{
		const float Angle = Random.FRandRange(0.f, 2 * PI);
		// Out to the edge of the land (~9 km), the valleys where the real tree line lets trees grow included
		const float Distance = Extent * 0.5f + 6000.f + FMath::Pow(Random.FRand(), 1.8f) * 780000.f;
		if (Random.FRand() > FMath::Exp(-FMath::Max(0.f, Distance - 600000.f) / 250000.f))
		{
			continue;
		}
		const FVector At = Middle + FVector(FMath::Cos(Angle), FMath::Sin(Angle), 0.f) * Distance * 1.15f;
		// Keep the plot and a strip around it clear, so the forest never covers the building area
		if (FMath::Abs(At.X - Middle.X) < Extent * 0.5f + ForestMargin && FMath::Abs(At.Y - Middle.Y) < Extent * 0.5f + ForestMargin)
		{
			continue;
		}
		// Woods grow in closed stands with clearings between them. The pattern is the "stands" one in
		// Tools/terrain_material.hlsl, so the ground under a stand is forest floor and far stands read as canopy
		const FVector World = GetActorTransform().TransformPosition(At);
		const float Stand = ForestStand(World);
		if (Random.FRand() > FMath::SmoothStep(0.4f, 0.48f, Stand))
		{
			continue;
		}
		FHitResult Hit;
		if (!TerrainMesh->LineTraceComponent(Hit, World + FVector(0, 0, 400000.f), World - FVector(0, 0, 100000.f), FCollisionQueryParams(TEXT("FWForest"), true)))
		{
			continue;
		}
		const FVector Ground = GetActorTransform().InverseTransformPosition(Hit.ImpactPoint);
		// A wavy edge, not a contour line: the limits shift up to ~70 m with the lie of the land
		const float Edge = FMath::PerlinNoise2D(FVector2D(At.X, At.Y) / 25000.f) * 7000.f;
		if (Hit.ImpactNormal.Z < 0.78f || Ground.Z > TreeLineTop + Edge)
		{
			continue;
		}
		const float Line = FMath::Clamp((Ground.Z - ForestTop - Edge) / (TreeLineTop - ForestTop), 0.f, 1.f);
		if (Random.FRand() < Line * 0.85f)
		{
			continue;
		}
		const TArray<UStaticMesh*>& Pool = !Young.IsEmpty() && Stand < 0.5f && Random.FRand() < 0.5f ? Young
			: (!TreeLine.IsEmpty() && Line > 0.f && Random.FRand() < Line ? TreeLine : Trees);
		UStaticMesh* Mesh = Pool[Random.RandHelper(Pool.Num())];
		// Trees standing upright whatever the slope, a little smaller towards the tree line
		const float Scale = Random.FRandRange(1.f, 1.45f) * FMath::Lerp(1.f, 0.7f, Line);
		const FBoxSphereBounds Bounds = Mesh->GetBounds();
		const float Bottom = (Bounds.Origin.Z - Bounds.BoxExtent.Z) * Scale;
		const FTransform Tree(FRotator(0.f, Random.FRandRange(0.f, 360.f), 0.f), Ground - FVector(0, 0, Bottom + 40.f), FVector(Scale));
		Placed[Distance > Extent * 0.5f + ShadowDistance ? 1 : 0].FindOrAdd(Mesh).Add(Tree);
		// Grown trees within walking reach get a trunk: an invisible cylinder, much cheaper than the
		// tree mesh as collision; far ones none
		if (Distance < Extent * 0.5f + 40000.f && Pool != Young)
		{
			Trunks.Add(FTransform(FRotator::ZeroRotator, Ground + FVector(0, 0, 300.f * Scale), FVector(0.5f * Scale, 0.5f * Scale, 6.f * Scale)));
		}
		if (++Count >= Budget)
		{
			break;
		}
	}
	UStaticMesh* Cylinder = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cylinder.Cylinder"));
	if (Cylinder && Trunks.Num() > 0)
	{
		UHierarchicalInstancedStaticMeshComponent* Collision = MakeArtInstances(Cylinder, true);
		Collision->SetVisibility(false);
		Collision->SetHiddenInGame(true);
		Collision->SetCastShadow(false);
		Collision->AddInstances(Trunks, false);
	}
	for (int32 Far = 0; Far < 2; ++Far)
	{
		for (TPair<UStaticMesh*, TArray<FTransform>>& Pair : Placed[Far])
		{
			UHierarchicalInstancedStaticMeshComponent* Forest = MakeArtInstances(Pair.Key, false);
			// Wind (world position offset) only near the player: far trees stand still, which keeps their
			// virtual shadow pages cached; and no ray tracing for thousands of foliage instances
			Forest->SetWorldPositionOffsetDisableDistance(5000);
			Forest->SetVisibleInRayTracing(false);
			Forest->SetCastShadow(Far == 0);
			Forest->AddInstances(Pair.Value, false);
		}
	}
	UE_LOG(LogFWPlot, Log, TEXT("Planted %d trees around the plot (%d with trunk collision), forest to %.0f m and tree line %.0f m above the plot"),
		Count, Trunks.Num(), ForestTop / 100.f, TreeLineTop / 100.f);
}

void AFWPlot::PlantDwarfPine(int32 Biome)
{
	const TArray<UStaticMesh*> Shrubs = FWArt::DwarfPines();
	if (!TerrainMesh || Shrubs.IsEmpty() || Biome == 5 || Biome == 8 || Biome == 12)
	{
		return;
	}
	// Dwarf mountain pine (kosodrzewina) grows in a belt right above the spruce: from the forest top up to
	// DwarfPineBandMeters higher, in thickets with gaps. Lower down it only takes clearings in the forest,
	// in clumps, some of them right beside the plot
	const UFWGameSettings& Settings = UFWGameSettings::Get();
	const float ForestTop = Settings.ForestBandsAbovePlot(PlotElevationMeters).X * 100.f;
	const float BeltTop = ForestTop + Settings.DwarfPineBandMeters * 100.f;
	constexpr int32 Budget = 24000;
	// Shrubs a metre or two high vanish into the slope past a kilometre or two
	constexpr float Reach = 150000.f;
	const float Extent = Size * TileSize;
	const FVector Middle(Extent / 2, Extent / 2, 0.f);
	FRandomStream Random(PlotX * 7919 + PlotY * 104729);
	TMap<UStaticMesh*, TArray<FTransform>> Placed;
	TArray<FTransform> Thickets;
	int32 Count = 0;
	// Where the nearest thicket is (metres east and south of the plot centre), for screenshots
	FVector Nearest = FVector::ZeroVector;
	float NearestDistance = TNumericLimits<float>::Max();
	for (int32 i = 0; i < 400000 && Count < Budget; ++i)
	{
		const float Angle = Random.FRandRange(0.f, 2 * PI);
		const float Distance = Extent * 0.5f + ForestMargin + FMath::Pow(Random.FRand(), 1.6f) * Reach;
		const FVector At = Middle + FVector(FMath::Cos(Angle), FMath::Sin(Angle), 0.f) * Distance;
		if (FMath::Abs(At.X - Middle.X) < Extent * 0.5f + ForestMargin && FMath::Abs(At.Y - Middle.Y) < Extent * 0.5f + ForestMargin)
		{
			continue;
		}
		// Thickets: patches tens of metres across, with gaps between them
		const float Clump = FMath::PerlinNoise2D(FVector2D(At.X, At.Y) / 9000.f) + 0.5f * FMath::PerlinNoise2D(FVector2D(At.X, At.Y) / 2500.f);
		if (Random.FRand() > FMath::SmoothStep(0.0f, 0.12f, Clump))
		{
			continue;
		}
		FHitResult Hit;
		const FVector World = GetActorTransform().TransformPosition(At);
		if (!TerrainMesh->LineTraceComponent(Hit, World + FVector(0, 0, 400000.f), World - FVector(0, 0, 100000.f), FCollisionQueryParams(TEXT("FWDwarfPine"), true)))
		{
			continue;
		}
		// Not on rock faces or steep scree
		if (Hit.ImpactNormal.Z < 0.8f)
		{
			continue;
		}
		const FVector Ground = GetActorTransform().InverseTransformPosition(Hit.ImpactPoint);
		const float Edge = FMath::PerlinNoise2D(FVector2D(At.X, At.Y) / 20000.f) * 5000.f;
		if (Ground.Z > BeltTop + Edge)
		{
			continue;
		}
		if (Ground.Z < ForestTop + Edge)
		{
			// Below the belt: only in clearings, and sparser
			if (ForestStand(World) > 0.38f || Random.FRand() > 0.5f)
			{
				continue;
			}
		}
		else if (Random.FRand() < FMath::Clamp((Ground.Z - BeltTop + 6000.f) / 6000.f, 0.f, 1.f) * 0.8f)
		{
			// Thinning out towards the top of the belt
			continue;
		}
		UStaticMesh* Mesh = Shrubs[Random.RandHelper(Shrubs.Num())];
		const float Scale = Random.FRandRange(0.7f, 1.3f);
		// Leaning with the slope a little, the base sunk into the ground
		const FVector Up = FMath::Lerp(FVector::UpVector, Hit.ImpactNormal, 0.5f).GetSafeNormal();
		const FQuat Lean = FQuat::FindBetweenNormals(FVector::UpVector, Up) * FQuat(FVector::UpVector, Random.FRandRange(0.f, 2 * PI));
		Placed.FindOrAdd(Mesh).Add(FTransform(Lean, Ground - FVector(0, 0, 15.f), FVector(Scale)));
		if (Distance < NearestDistance)
		{
			NearestDistance = Distance;
			Nearest = (Ground - Middle) / 100.f;
		}
		// Thickets near the plot cannot be walked through: an invisible drum over most of the shrub
		if (Distance < Extent * 0.5f + 40000.f)
		{
			const float Radius = FMath::Min(Mesh->GetBounds().BoxExtent.X, Mesh->GetBounds().BoxExtent.Y) * 0.6f * Scale;
			Thickets.Add(FTransform(FRotator::ZeroRotator, Ground + FVector(0, 0, 60.f), FVector(Radius / 50.f, Radius / 50.f, 1.2f)));
		}
		++Count;
	}
	for (TPair<UStaticMesh*, TArray<FTransform>>& Pair : Placed)
	{
		UHierarchicalInstancedStaticMeshComponent* Belt = MakeArtInstances(Pair.Key, false);
		// Wind only within 15 m (swaying needles redraw their shadows); no ray tracing for thousands of shrubs
		Belt->SetWorldPositionOffsetDisableDistance(1500);
		Belt->SetVisibleInRayTracing(false);
		Belt->AddInstances(Pair.Value, false);
	}
	UStaticMesh* Cylinder = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cylinder.Cylinder"));
	if (Cylinder && Thickets.Num() > 0)
	{
		UHierarchicalInstancedStaticMeshComponent* Collision = MakeArtInstances(Cylinder, true);
		Collision->SetVisibility(false);
		Collision->SetHiddenInGame(true);
		Collision->SetCastShadow(false);
		Collision->AddInstances(Thickets, false);
	}
	UE_LOG(LogFWPlot, Log, TEXT("Planted %d dwarf pines (%d with collision), belt %.0f-%.0f m above the plot, nearest at %.0f m E %.0f m S"),
		Count, Thickets.Num(), ForestTop / 100.f, BeltTop / 100.f, Nearest.X, Nearest.Y);
}

void AFWPlot::BuildTerrain(const TArray<uint8>& Codes, const TSet<uint8>& TreeTiles)
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
			if (TreeTiles.Contains(Code))
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
	TerrainInstances = { Trunks, Crowns, Rocks };
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
		UHierarchicalInstancedStaticMeshComponent* Solid = MakeArtInstances(Pair.Key, true);
		Solid->AddInstances(Pair.Value, false);
		TerrainInstances.Add(Solid);
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

void AFWPlot::ClearTile(FIntPoint Tile)
{
	if (!Contains(Tile))
	{
		return;
	}
	Tiles[Tile.Y * Size + Tile.X] = FWCatalog::Empty;
	// Whatever was set on the tile, found by where it stands (in the plot's space), shrinks to nothing; the indices
	// stay as they are, so the instances of other tiles keep theirs
	const FTransform Plot = GetActorTransform();
	for (const TWeakObjectPtr<UInstancedStaticMeshComponent>& Weak : TerrainInstances)
	{
		UInstancedStaticMeshComponent* Instances = Weak.Get();
		if (!Instances)
		{
			continue;
		}
		bool bChanged = false;
		for (int32 Index = 0; Index < Instances->GetInstanceCount(); ++Index)
		{
			FTransform Instance;
			Instances->GetInstanceTransform(Index, Instance, true);
			const FVector Local = Plot.InverseTransformPosition(Instance.GetLocation());
			if (FMath::FloorToInt(Local.X / TileSize) == Tile.X && FMath::FloorToInt(Local.Y / TileSize) == Tile.Y && !Instance.GetScale3D().IsNearlyZero())
			{
				Instance.SetScale3D(FVector(0.001f));
				Instance.AddToTranslation(FVector(0.f, 0.f, -10000.f));
				Instances->UpdateInstanceTransform(Index, Instance, true, false, true);
				bChanged = true;
			}
		}
		if (bChanged)
		{
			Instances->MarkRenderStateDirty();
		}
	}
}

float AFWPlot::ForestAt(const FVector& World) const
{
	if (!TerrainMesh)
	{
		return 0.f;
	}
	const FVector Local = GetActorTransform().InverseTransformPosition(World);
	const float Extent = Size * TileSize;
	const float Out = FMath::Max(FMath::Abs(Local.X - Extent / 2), FMath::Abs(Local.Y - Extent / 2)) - (Extent * 0.5f + ForestMargin);
	if (Out < 0.f)
	{
		return 0.f;
	}
	const FVector2D Bands = UFWGameSettings::Get().ForestBandsAbovePlot(PlotElevationMeters) * 100.f;
	const float Height = 1.f - FMath::SmoothStep(Bands.X, Bands.Y, Local.Z);
	return Height * FMath::SmoothStep(0.38f, 0.48f, ForestStand(World)) * FMath::SmoothStep(0.f, 3000.f, Out);
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
