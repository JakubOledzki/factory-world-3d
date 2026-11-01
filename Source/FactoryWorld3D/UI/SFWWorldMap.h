#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "UObject/GCObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class SBox;
struct FSlateBrush;
class UTexture2D;
class UMaterialInstanceDynamic;
struct FFWNetReply;

/**
 * The world map (N), as in the 2D game: the Earth in 5 × 5 km plots, dragged to move, the wheel to zoom (smoothly). From
 * afar a picture of the Earth as from orbit: the biomes' colours shaded by the real relief, shallow seas along the
 * coasts, the countries' borders, clouds drifting over it and the sky behind; closer up sharper pieces of the same
 * picture, and closest each plot shows its biome from above: forests of trees, fields in rows, rock and snow on the
 * mountains, glaciers with crevasses, towns (Tools/blender/make_world_map.py and make_world_tiles.py). On it the player's own plots, the plots others own
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

	virtual void AddReferencedObjects(FReferenceCollector& Collector) override { Collector.AddReferencedObjects(Textures); Collector.AddReferencedObject(BiomeMaterial); }
	virtual FString GetReferencerName() const override { return TEXT("SFWWorldMap"); }

private:
	FVector2D ToScreen(FVector2D Plot) const;
	FVector2D ToPlot(FVector2D Local) const;
	void ClampView();
	void RequestArea();
	void RebuildPanel();
	void Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<void(const FFWNetReply&)> OnReply);

	TWeakObjectPtr<AFWPlayerController> Controller;
	TSharedPtr<FSlateBrush> MakeBrush(const TCHAR* Name);
	// The Earth from afar; its sharper pieces (rows x columns) closer up; closest the biomes from above (M_FWWorldMap,
	// told the view every frame); the clouds
	TSharedPtr<FSlateBrush> OverviewBrush;
	TArray<TSharedPtr<FSlateBrush>> DetailBrushes;
	TSharedPtr<FSlateBrush> BiomeBrush;
	TObjectPtr<UMaterialInstanceDynamic> BiomeMaterial;
	TSharedPtr<FSlateBrush> CloudBrush;
	// Zoom eases towards where the wheel set it, keeping the plot under the cursor under it
	float TargetZoom = 0.3f;
	FVector2D ZoomAnchor = FVector2D::ZeroVector;
	double Time = 0.0;
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
