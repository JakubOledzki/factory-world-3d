#include "UI/SFWWorldMap.h"

#include "Engine/Texture2D.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Materials/MaterialInterface.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWWorldData.h"
#include "World/FWPlot.h"
#include "Net/FWNetSubsystem.h"
#include "Fonts/FontMeasure.h"
#include "Framework/Application/SlateApplication.h"
#include "Rendering/DrawElements.h"
#include "Rendering/SlateRenderer.h"
#include "UI/FWStyle.h"
#include "Styling/CoreStyle.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWWorldMap"

namespace FWMapUi
{
	// The detail pieces and the tile atlas as make_world_map.py and make_world_tiles.py lay them out; the tiles fade in
	// from TilesFrom to TilesFull pixels a plot; the nearest zoom
	constexpr int32 PieceRows = 2, PieceColumns = 3;
	constexpr float TilesFrom = 7.f, TilesFull = 16.f;
	// A biome's texture repeats every this many plots
	constexpr float TileRepeat = 5.f;
	constexpr float MaxZoom = 220.f;

	FText Money(double Cents)
	{
		FNumberFormattingOptions Options;
		Options.MinimumFractionalDigits = 2;
		Options.MaximumFractionalDigits = 2;
		return FText::Format(LOCTEXT("Money", "{0} $"), FText::AsNumber(Cents / 100.0, &Options));
	}

	FText PlotText(FIntPoint Plot)
	{
		return FText::Format(LOCTEXT("Plot", "{0}:{1}"), FText::AsNumber(Plot.X, &FNumberFormattingOptions::DefaultNoGrouping()),
			FText::AsNumber(Plot.Y, &FNumberFormattingOptions::DefaultNoGrouping()));
	}

	TSharedRef<SWidget> Row(const FText& Label, const FText& Value)
	{
		return SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)
			[
				SNew(SBox).WidthOverride(90.f)[SNew(STextBlock).Text(Label).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(STextBlock).Text(Value).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text).AutoWrapText(true)
			];
	}
}

void SFWWorldMap::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	const FIntPoint Size = FWWorldData::Size();
	if (Size.X > 0)
	{
		MapSize = FVector2D(Size);
	}
	OverviewBrush = MakeBrush(TEXT("world_overview"));
	for (int32 Row = 0; Row < FWMapUi::PieceRows; ++Row)
	{
		for (int32 Column = 0; Column < FWMapUi::PieceColumns; ++Column)
		{
			DetailBrushes.Add(MakeBrush(*FString::Printf(TEXT("world_detail_%d_%d"), Row, Column)));
		}
	}
	if (UMaterialInterface* Biomes = LoadObject<UMaterialInterface>(nullptr, TEXT("/Game/Materials/M_FWWorldMap.M_FWWorldMap")))
	{
		BiomeMaterial = UMaterialInstanceDynamic::Create(Biomes, GetTransientPackage());
		BiomeMaterial->SetScalarParameterValue(TEXT("MapW"), MapSize.X);
		BiomeMaterial->SetScalarParameterValue(TEXT("MapH"), MapSize.Y);
		BiomeMaterial->SetScalarParameterValue(TEXT("Repeat"), FWMapUi::TileRepeat);
		BiomeBrush = MakeShared<FSlateBrush>();
		BiomeBrush->SetResourceObject(BiomeMaterial);
		BiomeBrush->ImageSize = FVector2D(1920, 1080);
		BiomeBrush->DrawAs = ESlateBrushDrawType::Image;
	}
	CloudBrush = MakeBrush(TEXT("world_clouds"));
	// Open on the plot the player stands on
	if (const AFWPlayerController* PC = Controller.Get(); PC && PC->GetPlot())
	{
		Center = FVector2D(PC->GetPlot()->GetPlotX() + 0.5, PC->GetPlot()->GetPlotY() + 0.5);
		Zoom = 6.f;
	}
	TargetZoom = Zoom;
	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Top).Padding(24.f)
		[
			SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(16.f, 10.f))
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()[SNew(STextBlock).Text(LOCTEXT("Title", "Mapa świata")).Font(FWStyle::Font(20, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
					.Text(LOCTEXT("Hint", "Przeciągnij, aby przesunąć  ·  kółko: przybliż  ·  klik: działka  ·  N / Esc zamyka"))
				]
			]
		]
		+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Top).Padding(24.f)
		[
			SAssignNew(Panel, SBox).WidthOverride(340.f)
		]
		+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Bottom).Padding(24.f)
		[
			SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(12.f, 8.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(SBox).WidthOverride(12.f).HeightOverride(12.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FWStyle::Accent)]]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 16, 0)[SNew(STextBlock).Text(LOCTEXT("Mine", "twoje działki")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Text)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(SBox).WidthOverride(12.f).HeightOverride(12.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FLinearColor(0.9f, 0.3f, 0.25f))]]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)[SNew(STextBlock).Text(LOCTEXT("Others", "działki innych graczy")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Text)]
			]
		]
	];
	Send(TEXT("world.info"), nullptr, [this](const FFWNetReply& Reply)
	{
		if (Reply.Type != TEXT("world.info"))
		{
			return;
		}
		WorldInfo = Reply.Payload;
		Mine.Reset();
		const TArray<TSharedPtr<FJsonValue>>* Plots = nullptr;
		if (Reply.Payload->TryGetArrayField(TEXT("plots"), Plots))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Plots)
			{
				Mine.Add(FIntPoint(int32(Value->AsObject()->GetNumberField(TEXT("x"))), int32(Value->AsObject()->GetNumberField(TEXT("y")))));
			}
		}
	});
	RebuildPanel();
}

SFWWorldMap::~SFWWorldMap() = default;

void SFWWorldMap::Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<void(const FFWNetReply&)> OnReply)
{
	if (AFWPlayerController* PC = Controller.Get())
	{
		TWeakPtr<SFWWorldMap> WeakThis = StaticCastSharedRef<SFWWorldMap>(AsShared());
		PC->Send(Type, Payload, [WeakThis, OnReply](const FFWNetReply& Reply)
		{
			if (WeakThis.IsValid())
			{
				OnReply(Reply);
			}
		});
	}
}

TSharedPtr<FSlateBrush> SFWWorldMap::MakeBrush(const TCHAR* Name)
{
	TSharedPtr<FSlateBrush> Brush;
	if (UTexture2D* Texture = FWArt::Texture(*FString::Printf(TEXT("/Game/External/WorldMap/%s.%s"), Name, Name)))
	{
		Brush = MakeShared<FSlateBrush>();
		Brush->SetResourceObject(Texture);
		Brush->ImageSize = FVector2D(Texture->GetSizeX(), Texture->GetSizeY());
		Brush->DrawAs = ESlateBrushDrawType::Image;
		Textures.Add(Texture);
	}
	return Brush;
}

FVector2D SFWWorldMap::ToScreen(FVector2D Plot) const
{
	return ViewSize * 0.5 + (Plot - Center) * Zoom;
}

FVector2D SFWWorldMap::ToPlot(FVector2D Local) const
{
	return Center + (Local - ViewSize * 0.5) / Zoom;
}

void SFWWorldMap::ClampView()
{
	// The whole Earth fits the screen at the farthest; a plot fills a good part of it at the nearest
	Zoom = FMath::Clamp(Zoom, float(FMath::Min(ViewSize.X / MapSize.X, ViewSize.Y / MapSize.Y)), FWMapUi::MaxZoom);
	TargetZoom = FMath::Clamp(TargetZoom, float(FMath::Min(ViewSize.X / MapSize.X, ViewSize.Y / MapSize.Y)), FWMapUi::MaxZoom);
	Center.X = FMath::Fmod(Center.X + MapSize.X, MapSize.X);
	Center.Y = FMath::Clamp(Center.Y, ViewSize.Y * 0.5 / Zoom, MapSize.Y - ViewSize.Y * 0.5 / Zoom);
	if (MapSize.Y * Zoom < ViewSize.Y)
	{
		Center.Y = MapSize.Y * 0.5;
	}
}

void SFWWorldMap::CenterOn(FVector2D Plot, float PixelsPerPlot)
{
	Center = Plot;
	Zoom = TargetZoom = PixelsPerPlot;
	ClampView();
	AreaAt = 0.0;
}

void SFWWorldMap::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	ViewSize = Geometry.GetLocalSize();
	Time = CurrentTime;
	// Ease the zoom towards the wheel's, in log space, the anchor plot staying under the cursor
	if (!FMath::IsNearlyEqual(Zoom, TargetZoom, TargetZoom * 0.002f))
	{
		const FVector2D Before = ToPlot(ZoomAnchor);
		Zoom = FMath::Exp(FMath::FInterpTo(FMath::Loge(Zoom), FMath::Loge(TargetZoom), DeltaTime, 12.f));
		ClampView();
		Center += Before - ToPlot(ZoomAnchor);
	}
	else
	{
		Zoom = TargetZoom;
	}
	ClampView();
	// Others' plots only close up, where they can be told apart; asked again a moment after the view settles
	if (Zoom >= 1.5f && CurrentTime - AreaAt > 1.0)
	{
		RequestArea();
		AreaAt = CurrentTime;
	}
}

void SFWWorldMap::RequestArea()
{
	const FVector2D Low = ToPlot(FVector2D::ZeroVector), High = ToPlot(ViewSize);
	const FIntRect Rect(FIntPoint(FMath::FloorToInt(Low.X), FMath::Max(0, FMath::FloorToInt(Low.Y))), FIntPoint(FMath::CeilToInt(High.X), FMath::CeilToInt(High.Y)));
	if (Rect == AreaAsked)
	{
		return;
	}
	AreaAsked = Rect;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), Rect.Min.X);
	P->SetNumberField(TEXT("y"), Rect.Min.Y);
	P->SetNumberField(TEXT("width"), FMath::Clamp(Rect.Width(), 1, 10000));
	P->SetNumberField(TEXT("height"), FMath::Clamp(Rect.Height(), 1, 10000));
	Send(TEXT("world.area"), P, [this](const FFWNetReply& Reply)
	{
		const TArray<TSharedPtr<FJsonValue>>* Plots = nullptr;
		if (Reply.Type != TEXT("world.area") || !Reply.Payload->TryGetArrayField(TEXT("plots"), Plots))
		{
			return;
		}
		Owned.Reset();
		for (const TSharedPtr<FJsonValue>& Value : *Plots)
		{
			Owned.Add(FIntPoint(int32(Value->AsObject()->GetNumberField(TEXT("x"))), int32(Value->AsObject()->GetNumberField(TEXT("y")))));
		}
	});
}

int32 SFWWorldMap::OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect, FSlateWindowElementList& Out,
	int32 LayerId, const FWidgetStyle& WidgetStyle, bool bParentEnabled) const
{
	const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");
	// The sky behind the Earth: deep blue above, paler towards the horizon below, clouds drifting slowly through it
	{
		TArray<FSlateGradientStop> Stops;
		Stops.Add(FSlateGradientStop(FVector2D(0.f, 0.f), FLinearColor::FromSRGBColor(FColor(20, 58, 112))));
		Stops.Add(FSlateGradientStop(FVector2D(0.f, ViewSize.Y * 0.6f), FLinearColor::FromSRGBColor(FColor(78, 131, 186))));
		Stops.Add(FSlateGradientStop(FVector2D(0.f, ViewSize.Y), FLinearColor::FromSRGBColor(FColor(168, 196, 222))));
		FSlateDrawElement::MakeGradient(Out, LayerId, Geometry.ToPaintGeometry(), Stops, Orient_Horizontal);
	}
	auto Clouds = [&](int32 Layer, float CloudTile, FVector2D Offset, float Alpha)
	{
		if (!CloudBrush.IsValid() || Alpha <= 0.01f)
		{
			return;
		}
		// The texture is twice as wide as high and repeats seamlessly: laid side by side over the view
		const FVector2D Size(CloudTile * 2.f, CloudTile);
		const FVector2D Start(FMath::Fmod(FMath::Fmod(Offset.X, Size.X) - Size.X, Size.X), FMath::Fmod(FMath::Fmod(Offset.Y, Size.Y) - Size.Y, Size.Y));
		for (double Y = Start.Y - Size.Y; Y < ViewSize.Y; Y += Size.Y)
		{
			for (double X = Start.X - Size.X; X < ViewSize.X; X += Size.X)
			{
				FSlateDrawElement::MakeBox(Out, Layer, Geometry.ToPaintGeometry(Size, FSlateLayoutTransform(FVector2D(X, Y))), CloudBrush.Get(),
					ESlateDrawEffect::None, FLinearColor(1.f, 1.f, 1.f, Alpha));
			}
		}
	};
	// Far clouds in the sky, moving a little with the view (parallax)
	Clouds(LayerId, 700.f, FVector2D(Time * 6.0, 0.0) - Center * Zoom * 0.08, 0.45f);

	// The Earth from afar, its sharper pieces closer up (faded in between), each once more on either side so it wraps
	const float Sharp = FMath::Clamp((Zoom - 1.2f) / 1.3f, 0.f, 1.f);
	for (int32 Copy = -1; Copy <= 1; ++Copy)
	{
		const FVector2D Origin = ToScreen(FVector2D(Copy * MapSize.X, 0.0));
		if (Origin.X > ViewSize.X || Origin.X + MapSize.X * Zoom < 0.0)
		{
			continue;
		}
		// A soft shadow under the Earth's edges, so it lies on the sky
		FSlateDrawElement::MakeBox(Out, LayerId + 1, Geometry.ToPaintGeometry(MapSize * Zoom + FVector2D(16.f), FSlateLayoutTransform(Origin - FVector2D(8.f))),
			White, ESlateDrawEffect::None, FLinearColor(0.f, 0.f, 0.05f, 0.35f));
		if (OverviewBrush.IsValid() && Sharp < 1.f)
		{
			FSlateDrawElement::MakeBox(Out, LayerId + 1, Geometry.ToPaintGeometry(MapSize * Zoom, FSlateLayoutTransform(Origin)), OverviewBrush.Get());
		}
		if (Sharp > 0.f)
		{
			for (int32 Row = 0; Row < FWMapUi::PieceRows; ++Row)
			{
				for (int32 Column = 0; Column < FWMapUi::PieceColumns; ++Column)
				{
					const TSharedPtr<FSlateBrush>& Piece = DetailBrushes[Row * FWMapUi::PieceColumns + Column];
					if (!Piece.IsValid())
					{
						continue;
					}
					// The pieces split the map's pixels as make_world_map.py did (rounded down), 3 pixels a plot
					const FVector2D Low(FMath::FloorToDouble(MapSize.X * 3.0 * Column / FWMapUi::PieceColumns) / 3.0, FMath::FloorToDouble(MapSize.Y * 3.0 * Row / FWMapUi::PieceRows) / 3.0);
					const FVector2D High(FMath::FloorToDouble(MapSize.X * 3.0 * (Column + 1) / FWMapUi::PieceColumns) / 3.0, FMath::FloorToDouble(MapSize.Y * 3.0 * (Row + 1) / FWMapUi::PieceRows) / 3.0);
					const FVector2D At = ToScreen(Low + FVector2D(Copy * MapSize.X, 0.0)), To = ToScreen(High + FVector2D(Copy * MapSize.X, 0.0));
					if (At.X > ViewSize.X || At.Y > ViewSize.Y || To.X < 0.0 || To.Y < 0.0)
					{
						continue;
					}
					FSlateDrawElement::MakeBox(Out, LayerId + 1, Geometry.ToPaintGeometry(To - At, FSlateLayoutTransform(At)), Piece.Get(), ESlateDrawEffect::None,
						FLinearColor(1.f, 1.f, 1.f, Sharp));
				}
			}
		}
	}

	// Closest: each plot's biome from above, a little larger than the plot with faded edges so neighbours blend; the
	// water first, the land over it
	const float TileFade = FMath::Clamp((Zoom - FWMapUi::TilesFrom) / (FWMapUi::TilesFull - FWMapUi::TilesFrom), 0.f, 1.f);
	if (TileFade > 0.f && BiomeBrush.IsValid())
	{
		BiomeMaterial->SetScalarParameterValue(TEXT("CenterX"), Center.X);
		BiomeMaterial->SetScalarParameterValue(TEXT("CenterY"), Center.Y);
		BiomeMaterial->SetScalarParameterValue(TEXT("Zoom"), Zoom);
		BiomeMaterial->SetScalarParameterValue(TEXT("ViewW"), ViewSize.X);
		BiomeMaterial->SetScalarParameterValue(TEXT("ViewH"), ViewSize.Y);
		BiomeMaterial->SetScalarParameterValue(TEXT("Fade"), TileFade);
		// Only over the map: above and below it the sky shows
		const double Top = FMath::Max(0.0, ToScreen(FVector2D(0.0, 0.0)).Y), Bottom = FMath::Min(ViewSize.Y, ToScreen(MapSize).Y);
		if (Bottom > Top)
		{
			FSlateDrawElement::MakeBox(Out, LayerId + 1, Geometry.ToPaintGeometry(ViewSize, FSlateLayoutTransform(FVector2D(0.0, 0.0))), BiomeBrush.Get());
		}
	}

	// Clouds over the Earth from afar, thinning out as the view comes down through them
	const float Over = FMath::Clamp((1.6f - Zoom) / 1.1f, 0.f, 1.f);
	Clouds(LayerId + 2, 1400.f, FVector2D(Time * 14.0, Time * 2.0) - Center * Zoom * 1.15, 0.6f * Over);
	auto Mark = [&](FIntPoint Plot, const FLinearColor& Color, float MinSize, int32 Layer)
	{
		for (int32 Copy = -1; Copy <= 1; ++Copy)
		{
			const FVector2D At = ToScreen(FVector2D(Plot.X + Copy * MapSize.X, Plot.Y));
			const float Side = FMath::Max(MinSize, Zoom);
			const FVector2D Middle = At + FVector2D(Zoom * 0.5);
			if (Middle.X < -Side || Middle.Y < -Side || Middle.X > ViewSize.X + Side || Middle.Y > ViewSize.Y + Side)
			{
				continue;
			}
			FSlateDrawElement::MakeBox(Out, Layer, Geometry.ToPaintGeometry(FVector2D(Side), FSlateLayoutTransform(Middle - FVector2D(Side * 0.5))), White, ESlateDrawEffect::None, Color);
		}
	};
	// Others' plots only where they can be told apart (the last area asked may be from farther out)
	for (const FIntPoint& Plot : Owned)
	{
		if (Zoom >= 1.5f && !Mine.Contains(Plot))
		{
			Mark(Plot, FLinearColor(0.9f, 0.3f, 0.25f, 0.9f), 3.f, LayerId + 2);
		}
	}
	for (const FIntPoint& Plot : Mine)
	{
		Mark(Plot, FLinearColor(0.f, 0.f, 0.f, 0.7f), 11.f + FMath::Min(Zoom, 4.f), LayerId + 3);
		Mark(Plot, FWStyle::Accent, 8.f, LayerId + 3);
	}
	if (Selected.X >= 0)
	{
		const FVector2D At = ToScreen(FVector2D(Selected));
		const float Side = FMath::Max(14.f, Zoom);
		const FVector2D Corner = At + FVector2D(Zoom * 0.5) - FVector2D(Side * 0.5);
		TArray<FVector2D> Box = { Corner, Corner + FVector2D(Side, 0), Corner + FVector2D(Side), Corner + FVector2D(0, Side), Corner };
		FSlateDrawElement::MakeLines(Out, LayerId + 4, Geometry.ToPaintGeometry(), Box, ESlateDrawEffect::None, FLinearColor::White, true, 2.f);
	}
	// Towns: the larger the closer in
	const int32 MinPopulation = Zoom >= 8.f ? 20000 : Zoom >= 3.f ? 150000 : Zoom >= 1.f ? 1000000 : 4000000;
	const FSlateFontInfo Font = FWStyle::Font(Zoom >= 3.f ? 10 : 9);
	const FSlateFontInfo Big = FWStyle::Font(Zoom >= 3.f ? 12 : 10, TEXT("Bold"));
	int32 Drawn = 0;
	// A label only where it does not cover another one (larger towns come first)
	TArray<FSlateRect> Taken;
	const TSharedRef<FSlateFontMeasure> Measure = FSlateApplication::Get().GetRenderer()->GetFontMeasureService();
	for (const FWWorldData::FTown& Town : FWWorldData::Towns())
	{
		if (Town.Population < MinPopulation || Drawn > 160)
		{
			break;
		}
		const FVector2D At = ToScreen(FVector2D(Town.Plot) + FVector2D(0.5));
		if (At.X < -60 || At.Y < -20 || At.X > ViewSize.X + 60 || At.Y > ViewSize.Y + 20)
		{
			continue;
		}
		const FSlateFontInfo& Used = Town.Population > 1000000 ? Big : Font;
		const FVector2D TextSize = Measure->Measure(Town.Name, Used);
		const FSlateRect Rect(At.X - 4.f, At.Y - 9.f, At.X + 9.f + TextSize.X, At.Y - 7.f + TextSize.Y);
		if (Taken.ContainsByPredicate([&Rect](const FSlateRect& Other) { return FSlateRect::DoRectanglesIntersect(Rect, Other); }))
		{
			continue;
		}
		Taken.Add(Rect);
		++Drawn;
		FSlateDrawElement::MakeBox(Out, LayerId + 5, Geometry.ToPaintGeometry(FVector2D(5.f), FSlateLayoutTransform(At - FVector2D(2.5f))), White, ESlateDrawEffect::None, FLinearColor(0.f, 0.f, 0.f, 0.6f));
		FSlateDrawElement::MakeBox(Out, LayerId + 5, Geometry.ToPaintGeometry(FVector2D(3.f), FSlateLayoutTransform(At - FVector2D(1.5f))), White, ESlateDrawEffect::None, FLinearColor(1.f, 0.95f, 0.85f));
		// A soft shadow under the name keeps it readable on snow and desert alike
		FSlateDrawElement::MakeText(Out, LayerId + 5, Geometry.ToPaintGeometry(TextSize, FSlateLayoutTransform(At + FVector2D(6.f, -7.f))),
			Town.Name, Used, ESlateDrawEffect::None, FLinearColor(0.f, 0.f, 0.f, 0.75f));
		FSlateDrawElement::MakeText(Out, LayerId + 6, Geometry.ToPaintGeometry(TextSize, FSlateLayoutTransform(At + FVector2D(5.f, -8.f))),
			Town.Name, Used, ESlateDrawEffect::None, FLinearColor(0.95f, 0.93f, 0.88f));
	}
	return SCompoundWidget::OnPaint(Args, Geometry, CullingRect, Out, LayerId + 7, WidgetStyle, bParentEnabled);
}

FReply SFWWorldMap::OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (Event.GetEffectingButton() == EKeys::LeftMouseButton)
	{
		bDragging = true;
		bDragged = false;
		return FReply::Handled().CaptureMouse(SharedThis(this));
	}
	return FReply::Unhandled();
}

FReply SFWWorldMap::OnMouseMove(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (bDragging)
	{
		const FVector2D Delta = Event.GetCursorDelta() / Geometry.Scale;
		bDragged |= Delta.SizeSquared() > 0.5;
		Center -= Delta / Zoom;
		ClampView();
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

FReply SFWWorldMap::OnMouseButtonUp(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (!bDragging || Event.GetEffectingButton() != EKeys::LeftMouseButton)
	{
		return FReply::Unhandled();
	}
	bDragging = false;
	if (!bDragged)
	{
		const FVector2D Plot = ToPlot(Geometry.AbsoluteToLocal(Event.GetScreenSpacePosition()));
		Select(FIntPoint(FMath::FloorToInt(FMath::Fmod(Plot.X + MapSize.X, MapSize.X)), FMath::FloorToInt(Plot.Y)));
	}
	return FReply::Handled().ReleaseMouseCapture();
}

FReply SFWWorldMap::OnMouseWheel(const FGeometry& Geometry, const FPointerEvent& Event)
{
	// Zoom about the cursor: the plot under it stays under it
	ZoomAnchor = Geometry.AbsoluteToLocal(Event.GetScreenSpacePosition());
	TargetZoom *= FMath::Pow(1.35f, Event.GetWheelDelta());
	ClampView();
	return FReply::Handled();
}

void SFWWorldMap::Select(FIntPoint Plot)
{
	if (Plot.Y < 0 || Plot.Y >= int32(MapSize.Y))
	{
		return;
	}
	Selected = Plot;
	PlotInfo.Reset();
	RebuildPanel();
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), Plot.X);
	P->SetNumberField(TEXT("y"), Plot.Y);
	Send(TEXT("world.plot"), P, [this, Plot](const FFWNetReply& Reply)
	{
		if (Plot == Selected && Reply.Type == TEXT("world.plot"))
		{
			PlotInfo = Reply.Payload;
			RebuildPanel();
		}
	});
}

void SFWWorldMap::RebuildPanel()
{
	if (!Panel.IsValid())
	{
		return;
	}
	if (Selected.X < 0)
	{
		Panel->SetContent(SNullWidget::NullWidget);
		return;
	}
	TWeakObjectPtr<AFWPlayerController> PC = Controller;
	const FIntPoint Plot = Selected;
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			SNew(STextBlock).Font(FWStyle::Font(18, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			.Text(PlotInfo.IsValid() && !PlotInfo->GetStringField(TEXT("name")).IsEmpty()
				? FText::FromString(PlotInfo->GetStringField(TEXT("name"))) : FText::Format(LOCTEXT("PlotTitle", "Działka {0}"), FWMapUi::PlotText(Plot)))
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 2)[FWMapUi::Row(LOCTEXT("Where", "Położenie"), FWWorldData::Geo(Plot))]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 2)[FWMapUi::Row(LOCTEXT("Biome", "Teren"), FWWorldData::BiomeName(PlotInfo.IsValid() ? int32(PlotInfo->GetNumberField(TEXT("biome"))) : FWWorldData::Biome(Plot)))];
	const FString Country = FWWorldData::Country(Plot);
	if (!Country.IsEmpty())
	{
		Box->AddSlot().AutoHeight().Padding(0, 2)[FWMapUi::Row(LOCTEXT("Country", "Państwo"), FText::FromString(Country))];
	}
	if (!PlotInfo.IsValid())
	{
		Box->AddSlot().AutoHeight().Padding(0, 8, 0, 0)[SNew(STextBlock).Text(LOCTEXT("Loading", "Wczytywanie…")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
	}
	else
	{
		const TSharedPtr<FJsonObject>* Owner = nullptr;
		const bool bOwned = PlotInfo->TryGetObjectField(TEXT("owner"), Owner) && Owner && Owner->IsValid();
		const bool bMine = bOwned && (*Owner)->GetBoolField(TEXT("mine"));
		double Price = 0.0;
		const bool bForSale = !bOwned && PlotInfo->TryGetNumberField(TEXT("priceCents"), Price);
		bool bCanEnter = false;
		PlotInfo->TryGetBoolField(TEXT("canEnter"), bCanEnter);
		Box->AddSlot().AutoHeight().Padding(0, 2)
		[
			FWMapUi::Row(LOCTEXT("Owner", "Właściciel"), bMine ? LOCTEXT("You", "ty") : bOwned ? FText::FromString((*Owner)->GetStringField(TEXT("name"))) : LOCTEXT("Nobody", "nikt"))
		];
		if (bForSale)
		{
			const int64 Balance = PC.IsValid() ? PC->GetBalanceCents() : 0;
			Box->AddSlot().AutoHeight().Padding(0, 2)[FWMapUi::Row(LOCTEXT("Price", "Cena"), FWMapUi::Money(Price))];
			Box->AddSlot().AutoHeight().Padding(0, 14, 0, 0)
			[
				SNew(SButton).ButtonStyle(&FWStyle::PrimaryButton()).HAlign(HAlign_Center).IsEnabled(Balance >= Price)
				.OnClicked_Lambda([this, Plot]()
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetNumberField(TEXT("x"), Plot.X);
					P->SetNumberField(TEXT("y"), Plot.Y);
					Send(TEXT("world.buy"), P, [this, Plot](const FFWNetReply& Reply)
					{
						AFWPlayerController* Inner = Controller.Get();
						if (!Inner)
						{
							return;
						}
						if (Reply.Type != TEXT("world.bought"))
						{
							Inner->ToastError(Reply);
							return;
						}
						double Balance = 0.0;
						if (Reply.Payload->TryGetNumberField(TEXT("balanceCents"), Balance))
						{
							Inner->SetBalanceCents(int64(Balance));
						}
						Inner->PlayUiSound(TEXT("build_place"));
						Inner->Toast(FText::Format(LOCTEXT("Bought", "Kupiono działkę {0}"), FWMapUi::PlotText(Plot)));
						Mine.AddUnique(Plot);
						Select(Plot);
					});
					return FReply::Handled();
				})
				[
					SNew(STextBlock).Text(Balance >= Price ? FText::Format(LOCTEXT("Buy", "Kup za {0}"), FWMapUi::Money(Price)) : LOCTEXT("TooDear", "Za mało pieniędzy"))
					.Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(FLinearColor::White)
				]
			];
		}
		else if (!bOwned)
		{
			Box->AddSlot().AutoHeight().Padding(0, 8, 0, 0)[SNew(STextBlock).Text(LOCTEXT("NotForSale", "Tej działki nie można kupić.")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
		}
		if (bCanEnter)
		{
			Box->AddSlot().AutoHeight().Padding(0, 14, 0, 0)
			[
				SNew(SButton).ButtonStyle(bMine ? &FWStyle::PrimaryButton() : &FWStyle::SecondaryButton()).HAlign(HAlign_Center)
				.OnClicked_Lambda([PC, Plot]()
				{
					if (AFWPlayerController* Inner = PC.Get())
					{
						Inner->CloseWindow();
						Inner->GoToPlot(Plot);
					}
					return FReply::Handled();
				})
				[
					SNew(STextBlock).Text(bMine ? LOCTEXT("Enter", "Wejdź na działkę") : LOCTEXT("Visit", "Odwiedź działkę"))
					.Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(bMine ? FLinearColor::White : FWStyle::Text)
				]
			];
		}
		if (bMine)
		{
			TSharedRef<SEditableTextBox> Name = SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(LOCTEXT("NameHint", "nazwa działki"))
				.Text(FText::FromString(PlotInfo->GetStringField(TEXT("name"))));
			Box->AddSlot().AutoHeight().Padding(0, 14, 0, 0)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)[Name]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(SButton).ButtonStyle(&FWStyle::SecondaryButton())
					.OnClicked_Lambda([this, Plot, Name]()
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("x"), Plot.X);
						P->SetNumberField(TEXT("y"), Plot.Y);
						P->SetStringField(TEXT("name"), Name->GetText().ToString());
						Send(TEXT("world.rename"), P, [this, Plot](const FFWNetReply& Reply)
						{
							if (Reply.Type == TEXT("world.plot") && Plot == Selected)
							{
								PlotInfo = Reply.Payload;
								RebuildPanel();
							}
							else if (AFWPlayerController* Inner = Controller.Get(); Inner && Reply.IsError())
							{
								Inner->ToastError(Reply);
							}
						});
						return FReply::Handled();
					})
					[
						SNew(STextBlock).Text(LOCTEXT("Rename", "Zapisz nazwę")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
					]
				]
			];
		}
	}
	Panel->SetContent(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(18.f)[Box]);
}

FReply SFWWorldMap::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	// N closes it too, unless it is being typed into the plot name
	const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
	const bool bTyping = Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText"));
	if (Event.GetKey() == EKeys::Escape || (Event.GetKey() == EKeys::N && !bTyping))
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->CloseWindow();
		}
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

#undef LOCTEXT_NAMESPACE
