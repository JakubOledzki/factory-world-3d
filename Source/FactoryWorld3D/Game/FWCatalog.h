#pragma once

#include "CoreMinimal.h"

class FJsonObject;

/** What the client knows about object kinds, items and server errors without asking the server. */
namespace FWCatalog
{
	// Tile codes in plot.enter "tiles" (trees of other species come from "treeKinds").
	enum ETile : uint8
	{
		Empty = 0,
		Tree = 1,
		Rock = 2,
		Dune = 3,
		Rails = 4,
		Station = 5,
		Bush = 6,
		Mine = 7,
	};

	// Tiles a square object covers along one side (as the server's Footprints).
	int32 FootprintSize(const FString& Kind);
	// How far its square reaches up/left and down/right of its own tile.
	inline int32 FootprintLow(const FString& Kind) { return (FootprintSize(Kind) - 1) / 2; }
	inline int32 FootprintHigh(const FString& Kind) { return FootprintSize(Kind) / 2; }

	bool IsBelt(const FString& Kind);
	bool IsInserter(const FString& Kind);
	// Work places with a window: 0 workbench (crafting), 1 furnace, 2 processing machine, -1 none
	int32 StationOf(const FString& Kind);
	bool IsStorage(const FString& Kind);
	// Places a tick an item moves on a belt of this kind.
	int32 BeltSpeed(const FString& Kind);

	// Tile step for a rotation: 0 south (+Y), 1 east (+X), 2 north, 3 west.
	FIntPoint Facing(int32 Rotation);
	// The tile to the left of something going the way Rotation faces.
	FIntPoint LeftOf(int32 Rotation);

	// A stable colour per object kind or item id, for placeholder art.
	FLinearColor ColorFor(const FString& Id);
	FLinearColor BiomeColor(int32 Biome);

	// Polish text for a server error reply.
	FText ErrorText(const FString& Code, const FString& Fallback);
}
