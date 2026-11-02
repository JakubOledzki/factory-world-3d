#pragma once

// What the parts of AFWPlot (FWPlot*.cpp) share: their log and small helpers.
// Included once per translation unit, so a unity build sees each helper once.

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

DEFINE_LOG_CATEGORY_STATIC(LogFWPlot, Log, All);

namespace
{
	// Basic shapes are 100 units across
	constexpr float ShapeSize = 100.f;
	// Lanes on the ~1.3 m band of the modelled belt
	constexpr float LaneOffset = 0.08f;
	constexpr float BandHeight = 0.11f;

	// Where the woods stand (0..1, stands above ~0.44): the same pattern as "stands" in
	// Tools/terrain_material.hlsl, so the forest floor in the ground lies under the trees
	float ForestStand(const FVector& World)
	{
		return FMath::Clamp(0.5f + 0.5f * FMath::Sin(World.X * 0.00011f + FMath::Sin(World.Y * 0.00009f) * 2.3f)
			* FMath::Cos(World.Y * 0.00013f - World.X * 0.00004f), 0.f, 1.f);
	}

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
