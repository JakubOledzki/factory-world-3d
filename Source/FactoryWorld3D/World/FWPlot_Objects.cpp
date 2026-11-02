// Drawing the plot's objects: models, wear, cables, moving parts and machine sounds.
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
		UStaticMesh* SolarPanel = FWArt::Factory(TEXT("solar_panel"));
		if (!SolarPanel)
		{
			return false;
		}
		Place(SolarPanel, FVector::ZeroVector, FRotator::ZeroRotator, 1.25f, true);
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
	// Workshop furniture with a model of its own, at the size of its footprint
	int32 ModelTiles = 1;
	if (UStaticMesh* Model = FWArt::FurnitureModel(Kind, ModelTiles))
	{
		Place(Model, FVector::ZeroVector, FRotator::ZeroRotator, float(FWCatalog::FootprintSize(Kind)) / ModelTiles, true);
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

void AFWPlot::AddWear(FFWPlotObject& Object)
{
	static const TCHAR* const Leaks[] = { TEXT("Leaking007"), TEXT("Leaking008"), TEXT("Leaking009") };
	if (!DecalBase)
	{
		return;
	}
	FRandomStream Random(GetTypeHash(Object.Id));
	auto Make = [&](const FVector& Location, const FRotator& Rotation, const FVector& Extent, const FLinearColor& Tint, float Strength)
	{
		const TCHAR* Id = Leaks[Random.RandHelper(UE_ARRAY_COUNT(Leaks))];
		UTexture2D* Color = FWArt::Texture(*FString::Printf(TEXT("/Game/External/Decals/%s/%s_2K-PNG_Color.%s_2K-PNG_Color"), Id, Id, Id));
		UTexture2D* Mask = FWArt::Texture(*FString::Printf(TEXT("/Game/External/Decals/%s/%s_2K-PNG_Opacity.%s_2K-PNG_Opacity"), Id, Id, Id));
		if (!Color || !Mask)
		{
			return;
		}
		UMaterialInstanceDynamic* Material = UMaterialInstanceDynamic::Create(DecalBase, this);
		Material->SetTextureParameterValue(TEXT("Color"), Color);
		Material->SetTextureParameterValue(TEXT("Opacity"), Mask);
		Material->SetVectorParameterValue(TEXT("Tint"), Tint);
		Material->SetScalarParameterValue(TEXT("Strength"), Strength);
		UDecalComponent* Decal = NewObject<UDecalComponent>(this);
		Decal->SetDecalMaterial(Material);
		Decal->DecalSize = Extent;
		Decal->SetupAttachment(RootComponent);
		Decal->SetRelativeLocationAndRotation(Location, Rotation);
		Decal->SetFadeScreenSize(0.002f);
		Decal->RegisterComponent();
		Object.Decals.Add(Decal);
	};
	const float Fit = TileSize / 400.f;
	const FVector Center = FootprintCenter(Object.Kind, Object.Tile);
	const float N = FWCatalog::FootprintSize(Object.Kind);
	if (FWCatalog::IsBelt(Object.Kind) || FWCatalog::IsInserter(Object.Kind))
	{
		// Oil drips on the ground under some belts and every inserter (projected straight down)
		if (FWCatalog::IsInserter(Object.Kind) || Random.FRand() < 0.4f)
		{
			const float Spread = Random.FRandRange(90.f, 160.f) * Fit;
			Make(Center + FVector(Random.FRandRange(-80.f, 80.f) * Fit, Random.FRandRange(-40.f, 40.f) * Fit, 0.f),
				FRotator(-90.f, Random.FRandRange(0.f, 360.f), 0.f), FVector(60.f, Spread, Spread), FLinearColor(0.05f, 0.045f, 0.04f), 0.75f);
		}
	}
	else if (FWCatalog::IsStorage(Object.Kind) || Object.Kind.StartsWith(TEXT("furniture_")))
	{
		// A streak down one or two sides: rain running off the top, rust below fixings
		const int32 Count = 1 + Random.RandHelper(2);
		for (int32 k = 0; k < Count; ++k)
		{
			const float Yaw = 90.f * Random.RandHelper(4);
			const FVector Out = FRotator(0.f, Yaw, 0.f).Vector();
			const float Half = (FWCatalog::IsStorage(Object.Kind) ? 85.f : 150.f * N) * Fit;
			const float Height = (FWCatalog::IsStorage(Object.Kind) ? 120.f : 260.f) * Fit;
			Make(Center + Out * Half + FVector(0.f, 0.f, Height * 0.55f), FRotator(0.f, Yaw + 180.f, 0.f),
				FVector(30.f, Height * 0.45f, Random.FRandRange(50.f, 90.f) * Fit), FLinearColor(0.3f, 0.2f, 0.12f), 0.65f);
		}
	}
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
		// A new target: the servo whines up or down; reaching an end, the gripper clunks
		int32& Heard = InserterSwingHeard.FindOrAdd(Inserter.Id, Inserter.Swing);
		if (Heard != Inserter.Swing)
		{
			const FVector At = (*Arm)->GetComponentLocation();
			FWAudio::PlayAt(this, Inserter.Swing > Heard ? TEXT("inserter_up") : TEXT("inserter_down"), At, 0.35f);
			if (Inserter.Swing == 0 || Inserter.Swing >= Inserter.SwingTicks)
			{
				FWAudio::PlayAt(this, TEXT("inserter_clunk"), At, 0.5f);
			}
			Heard = Inserter.Swing;
		}
		const float Swing = FMath::Lerp(float(Inserter.Was), float(Inserter.Swing), Fraction) / FMath::Max(1, Inserter.SwingTicks);
		// 0 points back at the tile it takes from, 1 forward at the tile it drops on
		const float Yaw = YawOf(Inserter.Rotation) + 180.f * (1.f - FMath::SmoothStep(0.f, 1.f, Swing));
		(*Arm)->SetRelativeRotation(FRotator(0.f, Yaw, 0.f));
	}
}

void AFWPlot::UpdateMachineSounds(float DeltaSeconds)
{
	const double Now = FPlatformTime::Seconds();
	if (Now < NextSoundUpdate)
	{
		return;
	}
	NextSoundUpdate = Now + 0.4;
	const APlayerController* Player = GetWorld() ? GetWorld()->GetFirstPlayerController() : nullptr;
	if (!Player || !Player->PlayerCameraManager)
	{
		return;
	}
	const FVector Listener = Player->PlayerCameraManager->GetCameraLocation();
	struct FKindSound
	{
		const TCHAR* Sound;
		int32 Voices;
		float Volume;
		const TCHAR* Attenuation;
		float Height;
	};
	auto SoundFor = [](const FString& Kind) -> const FKindSound*
	{
		static const FKindSound Belt { TEXT("belt_loop"), 6, 0.3f, TEXT("ATT_Belt"), 60.f };
		static const FKindSound Pole { TEXT("pole_hum_loop"), 3, 0.1f, TEXT("ATT_Belt"), 600.f };
		static const FKindSound Turbine { TEXT("turbine_loop"), 3, 0.7f, TEXT("ATT_World"), 900.f };
		if (FWCatalog::IsBelt(Kind))
		{
			return &Belt;
		}
		if (Kind == TEXT("power_pole") || Kind == TEXT("power_substation") || Kind == TEXT("power_pylon"))
		{
			return &Pole;
		}
		if (Kind == TEXT("power_wind_turbine") || Kind == TEXT("power_large_wind_turbine"))
		{
			return &Turbine;
		}
		return nullptr;
	};
	// The machines of each sound, nearest first
	TMap<const FKindSound*, TArray<TPair<float, FVector>>> Near;
	for (const TPair<FString, FFWPlotObject>& Pair : Objects)
	{
		if (const FKindSound* Kind = SoundFor(Pair.Value.Kind))
		{
			const FVector At = GetActorTransform().TransformPosition(FootprintCenter(Pair.Value.Kind, Pair.Value.Tile)) + FVector(0, 0, Kind->Height);
			Near.FindOrAdd(Kind).Add({ float(FVector::DistSquared(At, Listener)), At });
		}
	}
	for (TPair<const FKindSound*, TArray<TPair<float, FVector>>>& Pair : Near)
	{
		const FKindSound& Kind = *Pair.Key;
		Pair.Value.Sort([](const TPair<float, FVector>& A, const TPair<float, FVector>& B) { return A.Key < B.Key; });
		FSoundPool& Pool = SoundPools.FindOrAdd(Kind.Sound);
		while (Pool.Voices.Num() < FMath::Min(Kind.Voices, Pair.Value.Num()))
		{
			UAudioComponent* Voice = FWAudio::Loop(RootComponent, Kind.Sound, Kind.Volume, Kind.Attenuation);
			if (!Voice)
			{
				break;
			}
			Pool.Voices.Add(Voice);
		}
		for (int32 i = 0; i < Pool.Voices.Num(); ++i)
		{
			UAudioComponent* Voice = Pool.Voices[i];
			if (!Voice)
			{
				continue;
			}
			const bool bUsed = Pair.Value.IsValidIndex(i);
			Voice->SetVolumeMultiplier(bUsed ? Kind.Volume : 0.f);
			if (bUsed)
			{
				Voice->SetWorldLocation(Pair.Value[i].Value);
			}
		}
	}
	// Kinds that are gone (every belt picked up) fall silent
	for (TPair<FString, FSoundPool>& Pair : SoundPools)
	{
		const bool bHeard = Near.Contains(SoundFor(Pair.Key == TEXT("belt_loop") ? TEXT("logistics_belt") : (Pair.Key == TEXT("pole_hum_loop") ? TEXT("power_pole") : TEXT("power_wind_turbine"))));
		for (UAudioComponent* Voice : Pair.Value.Voices)
		{
			if (Voice && !bHeard)
			{
				Voice->SetVolumeMultiplier(0.f);
			}
		}
	}
}

void AFWPlot::DrawObject(FFWPlotObject& Object)
{
	if (DrawArtObject(Object))
	{
		AddWear(Object);
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
