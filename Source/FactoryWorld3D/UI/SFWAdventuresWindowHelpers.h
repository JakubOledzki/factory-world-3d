#pragma once

// What the parts of SFWAdventuresWindow (SFWAdventuresWindow*.cpp) share: the texts and the puzzle board.
// Included once per translation unit, after LOCTEXT_NAMESPACE is defined, so a unity build sees each helper once.

#include "UI/SFWAdventuresWindow.h"
#include "Brushes/SlateRoundedBoxBrush.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SUniformGridPanel.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

namespace
{
namespace FWAdventureText
{
	const TCHAR* const Tabs[] = { TEXT("Wydarzenie"), TEXT("Album"), TEXT("Wyprawy"), TEXT("Zagadki"), TEXT("Mentor"), TEXT("Gablota") };
	enum ETab { Event, Album, Expeditions, Puzzles, Mentor, Showcase };
	const TCHAR* const Requests[] = { TEXT("season.get"), TEXT("album.get"), TEXT("expedition.get"), TEXT("puzzle.get"), TEXT("mentor.get"), TEXT("showcase.get") };

	FText Medal(int32 Medal)
	{
		static const FText Medals[] = { FText::GetEmpty(), LOCTEXT("Bronze", "Brąz"), LOCTEXT("Silver", "Srebro"), LOCTEXT("Gold", "Złoto") };
		return Medals[FMath::Clamp(Medal, 0, 3)];
	}

	FLinearColor MedalColor(int32 Medal)
	{
		static const FLinearColor Colors[] = { FLinearColor::White, FLinearColor::FromSRGBColor(FColor(0xc2, 0x7c, 0x3a)),
			FLinearColor::FromSRGBColor(FColor(0xc9, 0xcd, 0xd3)), FLinearColor::FromSRGBColor(FColor(0xf5, 0xb0, 0x41)) };
		return Colors[FMath::Clamp(Medal, 0, 3)];
	}

	FText Date(double Ms)
	{
		const FDateTime When = FDateTime::FromUnixTimestamp(int64(Ms / 1000.0));
		return FText::FromString(FString::Printf(TEXT("%02d.%02d.%d"), When.GetDay(), When.GetMonth(), When.GetYear()));
	}

	FString Duration(int64 Seconds)
	{
		Seconds = FMath::Max<int64>(Seconds, 0);
		return Seconds >= 3600 ? FString::Printf(TEXT("%lld h %02lld min"), Seconds / 3600, Seconds % 3600 / 60) : FString::Printf(TEXT("%lld min %02lld s"), Seconds / 60, Seconds % 60);
	}

	FString Number(double Value)
	{
		return FMath::IsNearlyEqual(Value, FMath::RoundToDouble(Value)) ? FString::Printf(TEXT("%d"), int32(FMath::RoundToDouble(Value)))
			: FString::Printf(TEXT("%.1f"), Value).Replace(TEXT("."), TEXT(","));
	}

	TSharedRef<SWidget> Line(const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right = nullptr, AFWPlayerController* PC = nullptr, const FString& Item = FString(), float IconSize = 30.f)
	{
		return SNew(SFWCard).Padding(FMargin(10.f, 6.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, Item.IsEmpty() ? 0.f : 10.f, 0)[Item.IsEmpty() ? SNullWidget::NullWidget : FWUi::Icon(PC, Item, IconSize)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(Color).AutoWrapText(true)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
			];
	}
}
}

// Declared in SFWAdventuresWindow.h, so not in an anonymous namespace: only constants and inline classes
namespace FWPuzzle
{
	constexpr float Cell = 52.f;
	// Arrows for the four ways a piece faces: 0 south, 1 east, 2 north, 3 west, as on the server
	const TCHAR* const Arrows[] = { TEXT("↓"), TEXT("→"), TEXT("↑"), TEXT("←") };

	struct FPiece
	{
		FString Kind;
		int32 X = 0;
		int32 Y = 0;
		int32 Rotation = 1;
	};

	/**
	 * The yard of an engineering puzzle (puzzle_board.gd): a grid with the supply chest and the chest that takes the
	 * product, where the player puts belts, inserters and machines. A click puts the chosen piece down (on a piece:
	 * turns it), a right click takes it away, R turns the next piece. After a check the server's flow through each
	 * piece (pieces a minute) is drawn on it.
	 */
	class SBoard : public SLeafWidget
	{
	public:
		SLATE_BEGIN_ARGS(SBoard) {}
			SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Controller = InArgs._Controller;
		}

		void ShowPuzzle(const TSharedPtr<FJsonObject>& InPuzzle)
		{
			Puzzle = InPuzzle;
			Pieces.Reset();
			Flow.Reset();
			for (const TSharedPtr<FJsonObject>& Piece : FWUi::Objects(FWUi::Obj(Puzzle, TEXT("best")), TEXT("layout")))
			{
				Pieces.Add({ FWUi::Str(Piece, TEXT("kind")), int32(FWUi::Num(Piece, TEXT("x"))), int32(FWUi::Num(Piece, TEXT("y"))), int32(FWUi::Num(Piece, TEXT("rotation"))) });
			}
			const TArray<TSharedPtr<FJsonObject>> Allowed = FWUi::Objects(Puzzle, TEXT("allowed"));
			Kind = Allowed.Num() > 0 ? FWUi::Str(Allowed[0], TEXT("id")) : FString();
		}

		void Clear()
		{
			Pieces.Reset();
			Flow.Reset();
		}

		int32 SizeOf(const FString& PieceKind) const
		{
			for (const TSharedPtr<FJsonObject>& Allowed : FWUi::Objects(Puzzle, TEXT("allowed")))
			{
				if (FWUi::Str(Allowed, TEXT("id")) == PieceKind)
				{
					return FMath::Max(1, int32(FWUi::Num(Allowed, TEXT("size"), 1.0)));
				}
			}
			return 1;
		}

		// The piece covering cell (x, y), or -1; a piece of size 2 covers its own cell and those right of and below it
		int32 PieceAt(int32 X, int32 Y) const
		{
			for (int32 i = 0; i < Pieces.Num(); ++i)
			{
				const int32 S = SizeOf(Pieces[i].Kind), Low = (S - 1) / 2, High = S / 2;
				if (X >= Pieces[i].X - Low && X <= Pieces[i].X + High && Y >= Pieces[i].Y - Low && Y <= Pieces[i].Y + High)
				{
					return i;
				}
			}
			return -1;
		}

		FIntPoint Point(const TCHAR* Name) const
		{
			const TSharedPtr<FJsonObject> P = FWUi::Obj(Puzzle, Name);
			return P.IsValid() ? FIntPoint(int32(FWUi::Num(P, TEXT("x"))), int32(FWUi::Num(P, TEXT("y")))) : FIntPoint(-9, -9);
		}

		TArray<TSharedPtr<FJsonValue>> PiecesJson() const
		{
			TArray<TSharedPtr<FJsonValue>> Out;
			for (const FPiece& Piece : Pieces)
			{
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetStringField(TEXT("kind"), Piece.Kind);
				P->SetNumberField(TEXT("x"), Piece.X);
				P->SetNumberField(TEXT("y"), Piece.Y);
				P->SetNumberField(TEXT("rotation"), Piece.Rotation);
				Out.Add(MakeShared<FJsonValueObject>(P));
			}
			return Out;
		}

		virtual bool SupportsKeyboardFocus() const override { return true; }

		virtual FVector2D ComputeDesiredSize(float) const override
		{
			return FVector2D(FWUi::Num(Puzzle, TEXT("width")) * Cell, FWUi::Num(Puzzle, TEXT("height")) * Cell);
		}

		virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override
		{
			if (Event.GetKey() == EKeys::R)
			{
				Rotation = (Rotation + 1) % 4;
				return FReply::Handled();
			}
			return FReply::Unhandled();
		}

		virtual FReply OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event) override
		{
			const FVector2D Local = Geometry.AbsoluteToLocal(Event.GetScreenSpacePosition());
			const FIntPoint At(FMath::FloorToInt(Local.X / Cell), FMath::FloorToInt(Local.Y / Cell));
			if (!Puzzle.IsValid() || At.X < 0 || At.Y < 0 || At.X >= int32(FWUi::Num(Puzzle, TEXT("width"))) || At.Y >= int32(FWUi::Num(Puzzle, TEXT("height")))
				|| At == Point(TEXT("source")) || At == Point(TEXT("sink")))
			{
				return FReply::Handled().SetUserFocus(SharedThis(this));
			}
			const int32 Index = PieceAt(At.X, At.Y);
			if (Event.GetEffectingButton() == EKeys::RightMouseButton)
			{
				if (Index >= 0)
				{
					Pieces.RemoveAt(Index);
				}
			}
			else if (Index >= 0)
			{
				Pieces[Index].Rotation = (Pieces[Index].Rotation + 1) % 4;
			}
			else if (!Kind.IsEmpty())
			{
				Pieces.Add({ Kind, At.X, At.Y, Rotation });
			}
			Flow.Reset();
			if (OnChanged)
			{
				OnChanged();
			}
			return FReply::Handled().SetUserFocus(SharedThis(this));
		}

		virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect, FSlateWindowElementList& Out,
			int32 LayerId, const FWidgetStyle& WidgetStyle, bool bParentEnabled) const override
		{
			if (!Puzzle.IsValid())
			{
				return LayerId;
			}
			const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");
			const int32 W = int32(FWUi::Num(Puzzle, TEXT("width"))), H = int32(FWUi::Num(Puzzle, TEXT("height")));
			FSlateDrawElement::MakeBox(Out, LayerId, Geometry.ToPaintGeometry(FVector2D(W, H) * Cell, FSlateLayoutTransform()), White, ESlateDrawEffect::None,
				FLinearColor::FromSRGBColor(FColor(0x1d, 0x1f, 0x1a)));
			const FLinearColor GridColor(1.f, 1.f, 1.f, 0.08f);
			for (int32 X = 0; X <= W; ++X)
			{
				FSlateDrawElement::MakeLines(Out, LayerId + 1, Geometry.ToPaintGeometry(), TArray<FVector2D>{ FVector2D(X * Cell, 0.f), FVector2D(X * Cell, H * Cell) }, ESlateDrawEffect::None, GridColor);
			}
			for (int32 Y = 0; Y <= H; ++Y)
			{
				FSlateDrawElement::MakeLines(Out, LayerId + 1, Geometry.ToPaintGeometry(), TArray<FVector2D>{ FVector2D(0.f, Y * Cell), FVector2D(W * Cell, Y * Cell) }, ESlateDrawEffect::None, GridColor);
			}
			auto Icon = [&](const FString& Item, const FVector2D& Position, float Side, float Alpha)
			{
				if (const FSlateBrush* Brush = Controller.IsValid() ? Controller->GetIconBrush(Item) : nullptr)
				{
					FSlateDrawElement::MakeBox(Out, LayerId + 3, Geometry.ToPaintGeometry(FVector2D(Side), FSlateLayoutTransform(Position)), Brush, ESlateDrawEffect::None, FLinearColor(1.f, 1.f, 1.f, Alpha));
				}
			};
			auto Chest = [&](FIntPoint At, const FString& Item, const FLinearColor& Color)
			{
				const FVector2D Corner = FVector2D(At) * Cell + FVector2D(3.f);
				FSlateDrawElement::MakeBox(Out, LayerId + 2, Geometry.ToPaintGeometry(FVector2D(Cell - 6.f), FSlateLayoutTransform(Corner)), White, ESlateDrawEffect::None, Color);
				Icon(Item, Corner + FVector2D(5.f), Cell - 16.f, 1.f);
			};
			Chest(Point(TEXT("source")), FWUi::Str(FWUi::Obj(Puzzle, TEXT("input")), TEXT("id")), FLinearColor::FromSRGBColor(FColor(0x3d, 0x6b, 0x2f)));
			Chest(Point(TEXT("sink")), FWUi::Str(FWUi::Obj(Puzzle, TEXT("output")), TEXT("id")), FLinearColor::FromSRGBColor(FColor(0x7a, 0x5a, 0x1c)));
			const FSlateFontInfo ArrowFont = FWStyle::Font(14, TEXT("Bold"));
			const FSlateFontInfo FlowFont = FWStyle::Font(9, TEXT("Bold"));
			for (int32 i = 0; i < Pieces.Num(); ++i)
			{
				const FPiece& Piece = Pieces[i];
				const int32 S = SizeOf(Piece.Kind), Low = (S - 1) / 2;
				const FVector2D Corner = FVector2D(Piece.X - Low, Piece.Y - Low) * Cell + FVector2D(3.f);
				const FVector2D Side = FVector2D(S * Cell - 6.f);
				FSlateDrawElement::MakeBox(Out, LayerId + 2, Geometry.ToPaintGeometry(Side, FSlateLayoutTransform(Corner)), White, ESlateDrawEffect::None, FLinearColor::FromSRGBColor(FColor(0x2c, 0x2f, 0x36)));
				TArray<FVector2D> Outline = { Corner, Corner + FVector2D(Side.X, 0.f), Corner + Side, Corner + FVector2D(0.f, Side.Y), Corner };
				FSlateDrawElement::MakeLines(Out, LayerId + 3, Geometry.ToPaintGeometry(), Outline, ESlateDrawEffect::None, FLinearColor::FromSRGBColor(FColor(0x5d, 0x63, 0x70)), true, 1.5f);
				const float Inset = S == 1 ? 6.f : 14.f;
				Icon(Piece.Kind, Corner + FVector2D(Inset), Side.X - 2.f * Inset, 0.85f);
				if (S == 1)
				{
					FSlateDrawElement::MakeText(Out, LayerId + 4, Geometry.ToPaintGeometry(FVector2D(18.f), FSlateLayoutTransform(Corner + FVector2D(Side.X - 16.f, 0.f))),
						FString(Arrows[Piece.Rotation % 4]), ArrowFont, ESlateDrawEffect::None, FWStyle::Accent);
				}
				if (Flow.IsValidIndex(i) && Flow[i] > 0.0)
				{
					FSlateDrawElement::MakeText(Out, LayerId + 4, Geometry.ToPaintGeometry(FVector2D(Side.X, 14.f), FSlateLayoutTransform(Corner + FVector2D(3.f, Side.Y - 15.f))),
						FWAdventureText::Number(Flow[i]) + TEXT("/min"), FlowFont, ESlateDrawEffect::None, FWStyle::Good);
				}
			}
			return LayerId + 4;
		}

		TWeakObjectPtr<AFWPlayerController> Controller;
		TSharedPtr<FJsonObject> Puzzle;
		TArray<FPiece> Pieces;
		TArray<double> Flow;
		FString Kind;
		int32 Rotation = 1;
		TFunction<void()> OnChanged;
	};
}
