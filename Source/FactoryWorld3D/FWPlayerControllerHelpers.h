#pragma once

// What the parts of AFWPlayerController (FWPlayerController*.cpp) share: their log and small helpers. Included
// after the including file defines LOCTEXT_NAMESPACE; once per translation unit, so unity builds see it once.

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "World/FWPlot.h"

DEFINE_LOG_CATEGORY_STATIC(LogFWGame, Log, All);

namespace
{
	TSharedPtr<FJsonObject> Payload()
	{
		return MakeShared<FJsonObject>();
	}

	TSharedPtr<FJsonObject> PlotPayload(const AFWPlot* Plot)
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetNumberField(TEXT("x"), Plot->GetPlotX());
		P->SetNumberField(TEXT("y"), Plot->GetPlotY());
		return P;
	}

	FText Price(double Cents)
	{
		FNumberFormattingOptions Options;
		Options.MaximumFractionalDigits = 2;
		return FText::Format(LOCTEXT("Price", "{0} $"), FText::AsNumber(Cents / 100.0, &Options));
	}

	// Offsets around the starter plot, nearest ring first
	TArray<FIntPoint> Spiral(int32 Radius)
	{
		TArray<FIntPoint> Points;
		for (int32 R = 0; R <= Radius; ++R)
		{
			for (int32 DY = -R; DY <= R; ++DY)
			{
				for (int32 DX = -R; DX <= R; ++DX)
				{
					if (FMath::Max(FMath::Abs(DX), FMath::Abs(DY)) == R)
					{
						Points.Add(FIntPoint(DX, DY));
					}
				}
			}
		}
		return Points;
	}
}

namespace
{
	const TCHAR* StationPrefix(int32 Kind)
	{
		return Kind == 0 ? TEXT("crafting") : Kind == 1 ? TEXT("furnace") : TEXT("machine");
	}

	const TCHAR* StationField(int32 Kind)
	{
		return Kind == 0 ? TEXT("workbench") : Kind == 1 ? TEXT("furnace") : TEXT("machine");
	}
}
