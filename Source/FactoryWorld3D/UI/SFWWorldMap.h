#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "UObject/GCObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
struct FSlateBrush;
class UTexture2D;
struct FFWNetReply;

/**
 * The world map (N), as in the 2D game: the Earth in 5 × 5 km plots coloured by biome with the countries' borders
 * (Tools/build_world_map.mjs), dragged to move, the wheel to zoom. On it the player's own plots, the plots others own
 * in view (world.area) and the larger towns. A click picks a plot: its biome, country, owner and price (world.plot),
 * with buying it (world.buy), going there (plot.enter) and naming an own plot (world.rename).
 */
class SFWWorldMap : public SCompoundWidget, public FGCObject
{
public:
	SLATE_BEGIN_ARGS(SFWWorldMap) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);
	virtual ~SFWWorldMap() override;

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual FReply OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnMouseButtonUp(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnMouseMove(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual FReply OnMouseWheel(const FGeometry& Geometry, const FPointerEvent& Event) override;
	virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect, FSlateWindowElementList& OutDrawElements,
		int32 LayerId, const FWidgetStyle& WidgetStyle, bool bParentEnabled) const override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands: centre on a plot at a zoom (screen pixels per plot), pick a plot
	void CenterOn(FVector2D Plot, float PixelsPerPlot);
	void Select(FIntPoint Plot);

	virtual void AddReferencedObjects(FReferenceCollector& Collector) override { Collector.AddReferencedObjects(Textures); }
	virtual FString GetReferencerName() const override { return TEXT("SFWWorldMap"); }

private:
	FVector2D ToScreen(FVector2D Plot) const;
	FVector2D ToPlot(FVector2D Local) const;
	void ClampView();
	void RequestArea();
	void RebuildPanel();
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<void(const FFWNetReply&)> OnReply);

	TWeakObjectPtr<AFWPlayerController> Controller;
	// Smooth from afar; each plot a crisp square close up
	TSharedPtr<FSlateBrush> MapBrush;
	TSharedPtr<FSlateBrush> SharpBrush;
	// Their textures, held while the map is open: Slate does not keep them from the garbage collector
	TArray<TObjectPtr<UTexture2D>> Textures;
	TSharedPtr<SBox> Panel;
	FVector2D MapSize = FVector2D(6933, 2942);
	// The plot at the centre of the view, and how many screen pixels a plot takes
	FVector2D Center = FVector2D(3800, 700);
	float Zoom = 0.3f;
	FVector2D ViewSize = FVector2D(1920, 1080);
	bool bDragging = false;
	bool bDragged = false;
	// The player's plots (world.info) and others' in view (world.area)
	TArray<FIntPoint> Mine;
	TArray<FIntPoint> Owned;
	FIntRect AreaAsked;
	double AreaAt = 0.0;
	// The picked plot and what the server says of it
	FIntPoint Selected = FIntPoint(-1, -1);
	TSharedPtr<FJsonObject> PlotInfo;
	TSharedPtr<FJsonObject> WorldInfo;
};
