#pragma once

// What the parts of SFWGarageWindow (SFWGarageWindow*.cpp) share: the garage's texts, the room plan and small widgets.
// Included once per translation unit, after LOCTEXT_NAMESPACE is defined, so a unity build sees each helper once.

#include "UI/SFWGarageWindow.h"
#include "Framework/Application/SlateApplication.h"
#include "Fonts/FontMeasure.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "Game/FWCatalog.h"
#include "Input/DragAndDrop.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

namespace
{
namespace FWGarageText
{
	const TCHAR* const Slots[] = { TEXT("head"), TEXT("body"), TEXT("legs"), TEXT("feet") };
	const TCHAR* const SlotNames[] = { TEXT("Głowa"), TEXT("Tułów"), TEXT("Nogi"), TEXT("Stopy") };

	// A piece of furniture in the room: where, how big (turned) and what it is
	struct FPiece
	{
		FString Id;
		FString Kind;
		FString Name;
		FIntRect Cells;
	};

	FIntPoint Size(const TSharedPtr<FJsonObject>& Garage, const FString& Kind, int32 Rotation)
	{
		const TSharedPtr<FJsonObject> Info = FWUi::Obj(FWUi::Obj(Garage, TEXT("furniture")), *Kind);
		const int32 W = FMath::Max(1, int32(FWUi::Num(Info, TEXT("width"), 1.0))), D = FMath::Max(1, int32(FWUi::Num(Info, TEXT("depth"), 1.0)));
		return Rotation % 2 == 0 ? FIntPoint(W, D) : FIntPoint(D, W);
	}

	TArray<FPiece> Pieces(const TSharedPtr<FJsonObject>& Garage)
	{
		TArray<FPiece> Out;
		const TSharedPtr<FJsonObject> Furniture = FWUi::Obj(Garage, TEXT("furniture"));
		for (const TSharedPtr<FJsonObject>& Object : FWUi::Objects(Garage, TEXT("objects")))
		{
			const FString Kind = FWUi::Str(Object, TEXT("kind"));
			const FIntPoint At(int32(FWUi::Num(Object, TEXT("x"))), int32(FWUi::Num(Object, TEXT("y"))));
			const FIntPoint S = Size(Garage, Kind, int32(FWUi::Num(Object, TEXT("rotation"))));
			Out.Add({ FWUi::Str(Object, TEXT("id")), Kind, FWUi::Str(FWUi::Obj(Furniture, *Kind), TEXT("name")), FIntRect(At, At + S) });
		}
		return Out;
	}

	// The first cell from the front-left corner where Kind fits, turned or not, without touching anything; X < 0 for none
	FIntVector FreeCell(const TSharedPtr<FJsonObject>& Garage, const FString& Kind)
	{
		const TSharedPtr<FJsonObject> Room = FWUi::Obj(Garage, TEXT("room"));
		const int32 Width = int32(FWUi::Num(Room, TEXT("width"), 16.0)), Depth = int32(FWUi::Num(Room, TEXT("depth"), 12.0));
		const TArray<FPiece> Placed = Pieces(Garage);
		for (int32 Rotation = 0; Rotation < 2; ++Rotation)
		{
			const FIntPoint S = Size(Garage, Kind, Rotation);
			for (int32 Y = Depth - S.Y; Y >= 0; --Y)
			{
				for (int32 X = 0; X + S.X <= Width; ++X)
				{
					const FIntRect Here(X, Y, X + S.X, Y + S.Y);
					if (!Placed.ContainsByPredicate([&Here](const FPiece& Piece) { return Piece.Cells.Intersect(Here); }))
					{
						return FIntVector(X, Y, Rotation);
					}
				}
			}
		}
		return FIntVector(-1, -1, 0);
	}

	// The room from above: the floor's grid and each piece of furniture with its name
	class SRoom : public SLeafWidget
	{
	public:
		SLATE_BEGIN_ARGS(SRoom) {}
			SLATE_ARGUMENT(FIntPoint, Room)
			SLATE_ARGUMENT(TArray<FPiece>, Pieces)
			SLATE_ARGUMENT(int32, Floor)
			// A click on the plan: the cell clicked (none: the plan only shows the room)
			SLATE_ARGUMENT(TFunction<void(FIntPoint)>, OnCell)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Room = FIntPoint(FMath::Max(InArgs._Room.X, 1), FMath::Max(InArgs._Room.Y, 1));
			Pieces = InArgs._Pieces;
			Floor = InArgs._Floor;
			OnCell = InArgs._OnCell;
			SetClipping(EWidgetClipping::ClipToBounds);
			if (OnCell)
			{
				SetCursor(EMouseCursor::Crosshairs);
			}
		}

		virtual FReply OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event) override
		{
			if (!OnCell || Event.GetEffectingButton() != EKeys::LeftMouseButton)
			{
				return FReply::Unhandled();
			}
			const FVector2D Local = Geometry.AbsoluteToLocal(Event.GetScreenSpacePosition());
			OnCell(FIntPoint(FMath::Clamp(int32(Local.X / Cell), 0, Room.X - 1), FMath::Clamp(int32(Local.Y / Cell), 0, Room.Y - 1)));
			return FReply::Handled();
		}

		virtual FVector2D ComputeDesiredSize(float) const override { return FVector2D(Cell * Room.X, Cell * Room.Y); }

		virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& Culling, FSlateWindowElementList& Out, int32 Layer,
			const FWidgetStyle& Style, bool bParentEnabled) const override
		{
			const FSlateBrush* White = FCoreStyle::Get().GetBrush(TEXT("GenericWhiteBox"));
			// Raw concrete gets lighter and warmer with each level of the floor
			const float Warm = FMath::Clamp(Floor / 10.f, 0.f, 1.f);
			FSlateDrawElement::MakeBox(Out, Layer, Geometry.ToPaintGeometry(), White, ESlateDrawEffect::None,
				FLinearColor::LerpUsingHSV(FLinearColor(0.16f, 0.17f, 0.18f), FLinearColor(0.42f, 0.33f, 0.24f), Warm));
			for (int32 X = 1; X < Room.X; ++X)
			{
				FSlateDrawElement::MakeBox(Out, Layer + 1, Geometry.ToPaintGeometry(FVector2f(1.f, Cell * Room.Y), FSlateLayoutTransform(FVector2f(X * Cell, 0.f))), White,
					ESlateDrawEffect::None, FLinearColor(1.f, 1.f, 1.f, 0.05f));
			}
			for (int32 Y = 1; Y < Room.Y; ++Y)
			{
				FSlateDrawElement::MakeBox(Out, Layer + 1, Geometry.ToPaintGeometry(FVector2f(Cell * Room.X, 1.f), FSlateLayoutTransform(FVector2f(0.f, Y * Cell))), White,
					ESlateDrawEffect::None, FLinearColor(1.f, 1.f, 1.f, 0.05f));
			}
			const FSlateFontInfo Font = FWStyle::Font(9, TEXT("Bold"));
			const TSharedRef<FSlateFontMeasure> Measure = FSlateApplication::Get().GetRenderer()->GetFontMeasureService();
			for (const FPiece& Piece : Pieces)
			{
				const FVector2f At(Piece.Cells.Min.X * Cell + 2.f, Piece.Cells.Min.Y * Cell + 2.f);
				const FVector2f Extent(Piece.Cells.Width() * Cell - 4.f, Piece.Cells.Height() * Cell - 4.f);
				const FLinearColor Color = Piece.Kind == TEXT("furniture_trash_bin") ? FWStyle::Good : Piece.Kind.Contains(TEXT("barrel")) || Piece.Kind.Contains(TEXT("cylinder"))
					|| Piece.Kind.Contains(TEXT("tyres")) || Piece.Kind.Contains(TEXT("pallet")) ? FLinearColor(0.35f, 0.33f, 0.3f) : FWStyle::Accent;
				FSlateDrawElement::MakeBox(Out, Layer + 2, Geometry.ToPaintGeometry(Extent, FSlateLayoutTransform(At)), White, ESlateDrawEffect::None, Color.CopyWithNewOpacity(0.85f));
				// The name inside when it fits, shortened when it does not
				FString Label = Piece.Name;
				while (Label.Len() > 3 && Measure->Measure(Label, Font).X > Extent.X - 6.f)
				{
					Label = Label.LeftChop(2) + TEXT("…");
					Label.RemoveFromEnd(TEXT("……"));
				}
				FSlateDrawElement::MakeText(Out, Layer + 3, Geometry.ToPaintGeometry(Extent, FSlateLayoutTransform(At + FVector2f(4.f, 3.f))), Label, Font,
					ESlateDrawEffect::None, FLinearColor(0.05f, 0.05f, 0.05f));
			}
			return Layer + 3;
		}

	private:
		static constexpr float Cell = 42.f;
		TFunction<void(FIntPoint)> OnCell;
		FIntPoint Room;
		TArray<FPiece> Pieces;
		int32 Floor = 0;
	};

	// The sorting game's bins, in the order the server lists them, with the colour of their labels
	FLinearColor BinColor(const FString& Bin)
	{
		return Bin == TEXT("metal") ? FLinearColor(0.62f, 0.67f, 0.74f) : Bin == TEXT("plastic") ? FLinearColor(1.f, 0.72f, 0.1f)
			: Bin == TEXT("glass") ? FLinearColor(0.32f, 0.78f, 0.42f) : FWStyle::Muted;
	}

	double Now() { return FPlatformTime::Seconds(); }

	// Seconds since a bin last took (true) or threw back (false) a piece
	using FBinFlash = TOptional<TPair<double, bool>>;

	// A piece of waste carried from the bag to a bin
	class FWasteDrag : public FDragDropOperation
	{
	public:
		DRAG_DROP_OPERATOR_TYPE(FWasteDrag, FDragDropOperation)

		int32 Piece = -1;
		const FSlateBrush* Brush = nullptr;
		TFunction<void()> OnEnded;

		static TSharedRef<FWasteDrag> New(int32 InPiece, const FSlateBrush* InBrush, TFunction<void()> InOnEnded)
		{
			TSharedRef<FWasteDrag> Op = MakeShared<FWasteDrag>();
			Op->Piece = InPiece;
			Op->Brush = InBrush;
			Op->OnEnded = MoveTemp(InOnEnded);
			Op->Construct();
			return Op;
		}

		virtual TSharedPtr<SWidget> GetDefaultDecorator() const override
		{
			// Held under the cursor's middle, a little larger, as if lifted
			return SNew(SBox).WidthOverride(120.f).HeightOverride(120.f).RenderTransform(FSlateRenderTransform(FVector2f(-60.f, -60.f)))
				[
					SNew(SImage).Image(Brush)
				];
		}

		virtual void OnDrop(bool bDropWasHandled, const FPointerEvent& MouseEvent) override
		{
			FDragDropOperation::OnDrop(bDropWasHandled, MouseEvent);
			if (OnEnded)
			{
				OnEnded();
			}
		}
	};

	// A piece in the open bag: picked up with the mouse, bobbing gently, shaking red when it comes back out of a wrong bin
	class SWastePiece : public SCompoundWidget
	{
	public:
		SLATE_BEGIN_ARGS(SWastePiece) {}
			SLATE_ARGUMENT(int32, Index)
			SLATE_ARGUMENT(const FSlateBrush*, Brush)
			SLATE_ARGUMENT(FString, Name)
			// Seconds since it came back out of a wrong bin (large when it did not), and whether it is away (held or on its way)
			SLATE_ATTRIBUTE(double, SinceWrong)
			SLATE_ATTRIBUTE(bool, Away)
			SLATE_ARGUMENT(TFunction<void(int32)>, OnPicked)
			SLATE_ARGUMENT(TFunction<void()>, OnDragEnded)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Index = InArgs._Index;
			Brush = InArgs._Brush;
			SinceWrong = InArgs._SinceWrong;
			Away = InArgs._Away;
			OnPicked = InArgs._OnPicked;
			OnDragEnded = InArgs._OnDragEnded;
			SetCursor(EMouseCursor::GrabHand);
			const int32 Seed = Index;
			ChildSlot
			[
				SNew(SVerticalBox)
				.Visibility_Lambda([this]() { return Away.Get() ? EVisibility::Hidden : EVisibility::Visible; })
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
				[
					SNew(SBox).WidthOverride(108.f).HeightOverride(108.f)
					.RenderTransformPivot(FVector2D(0.5f, 0.5f))
					.RenderTransform_Lambda([this, Seed]()
					{
						const double Wrong = SinceWrong.Get();
						const float Shake = Wrong < 0.5 ? float(FMath::Sin(Wrong * 48.0) * 12.0 * (1.0 - Wrong / 0.5)) : 0.f;
						const float Bob = float(FMath::Sin(Now() * 2.2 + Seed * 1.3) * 3.0);
						return TOptional<FSlateRenderTransform>(FSlateRenderTransform(FScale2f(bHover ? 1.1f : 1.f), FVector2f(Shake, Bob - (bHover ? 4.f : 0.f))));
					})
					[
						SNew(SImage).Image(Brush)
						.ColorAndOpacity_Lambda([this]()
						{
							const double Wrong = SinceWrong.Get();
							return Wrong < 0.6 ? FSlateColor(FLinearColor::LerpUsingHSV(FLinearColor(1.f, 0.35f, 0.3f), FLinearColor::White, float(Wrong / 0.6)))
								: FSlateColor(FLinearColor::White);
						})
					]
				]
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 4, 0, 0)
				[
					SNew(SBox).WidthOverride(124.f)
					[
						SNew(STextBlock).Text(FText::FromString(InArgs._Name)).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
						.Justification(ETextJustify::Center).AutoWrapText(true)
					]
				]
			];
		}

		virtual FReply OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event) override
		{
			return Event.GetEffectingButton() == EKeys::LeftMouseButton ? FReply::Handled().DetectDrag(SharedThis(this), EKeys::LeftMouseButton)
				: FReply::Unhandled();
		}

		virtual FReply OnDragDetected(const FGeometry& Geometry, const FPointerEvent& Event) override
		{
			if (OnPicked)
			{
				OnPicked(Index);
			}
			return FReply::Handled().BeginDragDrop(FWasteDrag::New(Index, Brush, OnDragEnded));
		}

		virtual void OnMouseEnter(const FGeometry& Geometry, const FPointerEvent& Event) override
		{
			SCompoundWidget::OnMouseEnter(Geometry, Event);
			bHover = true;
		}

		virtual void OnMouseLeave(const FPointerEvent& Event) override
		{
			SCompoundWidget::OnMouseLeave(Event);
			bHover = false;
		}

	private:
		int32 Index = -1;
		const FSlateBrush* Brush = nullptr;
		TAttribute<double> SinceWrong;
		TAttribute<bool> Away;
		TFunction<void(int32)> OnPicked;
		TFunction<void()> OnDragEnded;
		bool bHover = false;
	};

	// A bin to drop pieces into: it lights up while a piece is held over it, and flashes green (bouncing) when it takes
	// one and red when it throws one back
	class SWasteBin : public SCompoundWidget
	{
	public:
		SLATE_BEGIN_ARGS(SWasteBin) {}
			SLATE_ARGUMENT(const FSlateBrush*, Brush)
			SLATE_ARGUMENT(FText, Name)
			SLATE_ARGUMENT(FLinearColor, Color)
			// Seconds since this bin last took (true) or threw back (false) a piece; unset when it did neither lately
			SLATE_ATTRIBUTE(FBinFlash, Flash)
			SLATE_ARGUMENT(TFunction<void(int32)>, OnPiece)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Flash = InArgs._Flash;
			OnPiece = InArgs._OnPiece;
			const FLinearColor Color = InArgs._Color;
			ChildSlot
			[
				SNew(SBorder)
				.BorderImage(FCoreStyle::Get().GetBrush(TEXT("GenericWhiteBox")))
				.Padding(FMargin(10.f, 8.f))
				.BorderBackgroundColor_Lambda([this]()
				{
					FLinearColor Back(1.f, 1.f, 1.f, bHot ? 0.12f : 0.035f);
					const FBinFlash F = Flash.Get();
					if (F.IsSet() && F->Key < 0.6)
					{
						Back = FMath::Lerp(Back, (F->Value ? FWStyle::Good : FWStyle::Error).CopyWithNewOpacity(0.45f), 1.f - float(F->Key / 0.6));
					}
					return FSlateColor(Back);
				})
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
					[
						SNew(SBox).WidthOverride(170.f).HeightOverride(170.f)
						.RenderTransformPivot(FVector2D(0.5f, 1.f))
						.RenderTransform_Lambda([this]()
						{
							const FBinFlash F = Flash.Get();
							float Scale = bHot ? 1.06f : 1.f;
							if (F.IsSet() && F->Value && F->Key < 0.35)
							{
								Scale += float(FMath::Sin(F->Key / 0.35 * PI) * 0.08);
							}
							return TOptional<FSlateRenderTransform>(FSlateRenderTransform(FScale2f(Scale)));
						})
						[
							SNew(SImage).Image(InArgs._Brush)
						]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 0)
					[
						SNew(SBorder)
						.BorderImage(FCoreStyle::Get().GetBrush(TEXT("GenericWhiteBox")))
						.BorderBackgroundColor(Color)
						.HAlign(HAlign_Center)
						.Padding(FMargin(8.f, 4.f))
						[
							SNew(STextBlock).Text(InArgs._Name).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FLinearColor(0.05f, 0.05f, 0.05f))
						]
					]
				]
			];
		}

		virtual void OnDragEnter(const FGeometry& Geometry, const FDragDropEvent& Event) override
		{
			bHot = Event.GetOperationAs<FWasteDrag>().IsValid();
		}

		virtual void OnDragLeave(const FDragDropEvent& Event) override
		{
			bHot = false;
		}

		virtual FReply OnDragOver(const FGeometry& Geometry, const FDragDropEvent& Event) override
		{
			return Event.GetOperationAs<FWasteDrag>().IsValid() ? FReply::Handled() : FReply::Unhandled();
		}

		virtual FReply OnDrop(const FGeometry& Geometry, const FDragDropEvent& Event) override
		{
			bHot = false;
			const TSharedPtr<FWasteDrag> Op = Event.GetOperationAs<FWasteDrag>();
			if (!Op.IsValid())
			{
				return FReply::Unhandled();
			}
			if (OnPiece)
			{
				OnPiece(Op->Piece);
			}
			return FReply::Handled();
		}

	private:
		TAttribute<FBinFlash> Flash;
		TFunction<void(int32)> OnPiece;
		bool bHot = false;
	};
}
}
