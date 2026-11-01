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

#define LOCTEXT_NAMESPACE "FWGarage"

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
		SLATE_END_ARGS()

		void Construct(const FArguments& InArgs)
		{
			Room = FIntPoint(FMath::Max(InArgs._Room.X, 1), FMath::Max(InArgs._Room.Y, 1));
			Pieces = InArgs._Pieces;
			Floor = InArgs._Floor;
			SetClipping(EWidgetClipping::ClipToBounds);
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

void SFWGarageWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	auto MakeTab = [WeakThis](const FText& Text, int32 Index)
	{
		return FWUi::TabButton(Text, [WeakThis, Index]() { TSharedPtr<SFWGarageWindow> This = WeakThis.Pin(); return This && This->Tab == Index; },
			[WeakThis, Index]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->ShowTab(Index); } });
	};
	TSharedRef<SHorizontalBox> Tabs = SNew(SHorizontalBox);
	const FText Names[] = { LOCTEXT("TabSorting", "Segregacja"), LOCTEXT("TabSorter", "Sortownia"), LOCTEXT("TabUpgrades", "Ulepszenia"),
		LOCTEXT("TabWardrobe", "Garderoba"), LOCTEXT("TabFurniture", "Meble") };
	for (int32 Index = 0; Index < UE_ARRAY_COUNT(Names); ++Index)
	{
		Tabs->AddSlot().AutoWidth()[MakeTab(Names[Index], Index)];
	}
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Garaż"), Right, Tabs, SAssignNew(Content, SBox), 920.f, 820.f)
	];
	if (AFWPlayerController* PC = Controller.Get())
	{
		PC->RefreshInventory();
	}
	LoadGarage();
}

SFWGarageWindow::~SFWGarageWindow()
{
	// Walking away from the bin stops sorting by hand; the open bag goes back to the bin
	if (AFWPlayerController* PC = Controller.Get(); PC && HasRound())
	{
		PC->Send(TEXT("recycling.stop"), nullptr, nullptr);
	}
}

double SFWGarageWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

int32 SFWGarageWindow::Owned(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

FString SFWGarageWindow::FurnitureId(const FString& Kind) const
{
	for (const TSharedPtr<FJsonObject>& Object : FWUi::Objects(Garage, TEXT("objects")))
	{
		if (FWUi::Str(Object, TEXT("kind")) == Kind)
		{
			return FWUi::Str(Object, TEXT("id"));
		}
	}
	return FString();
}

void SFWGarageWindow::ShowTab(int32 InTab)
{
	Tab = FMath::Clamp(InTab, 0, 4);
	LoadTab();
	Rebuild();
}

void SFWGarageWindow::LoadGarage(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + 5.0;
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("garage.get"), nullptr, [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		if (This && !(bPoll && Reply.IsError()))
		{
			This->ApplyGarage(Reply);
		}
	});
}

void SFWGarageWindow::ApplyGarage(const FFWNetReply& Reply)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != TEXT("garage"))
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		return;
	}
	const bool bFirst = !Garage.IsValid();
	Garage = Reply.Payload;
	ClockOffset = FWUi::Num(Garage, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
	// What the finished bags gave
	const TArray<TSharedPtr<FJsonObject>> Results = FWUi::Objects(Garage, TEXT("results"));
	for (const TSharedPtr<FJsonObject>& Item : Results)
	{
		Recent.Insert(TPair<FString, FString>(FWUi::Str(Item, TEXT("id")), FWUi::Str(Item, TEXT("name"))), 0);
		LastBag = FText::Format(LOCTEXT("BagDone", "Worek posegregowany! +1 {0}"), FText::FromString(FWUi::Str(Item, TEXT("name"))));
		if (PC)
		{
			PC->Toast(FText::Format(LOCTEXT("Found", "+1 {0}"), FText::FromString(FWUi::Str(Item, TEXT("name")))));
		}
	}
	if (Recent.Num() > 18)
	{
		Recent.SetNum(18);
	}
	if (PC && Results.Num() > 0)
	{
		PC->RefreshInventory();
	}
	// A bag sorted earlier the old way (by waiting) is asked about again when it should be done
	const TArray<TSharedPtr<FJsonValue>>* Queue = nullptr;
	if (Garage->TryGetArrayField(TEXT("queue"), Queue) && Queue->Num() > 0)
	{
		const double DoneAt = (*Queue)[0]->AsNumber();
		NextPoll = FPlatformTime::Seconds() + FMath::Clamp((DoneAt - ServerNow()) / 1000.0 + 0.15, 0.3, 5.0);
	}
	if (bFirst)
	{
		LoadTab();
	}
	Rebuild();
}

void SFWGarageWindow::Sort()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bSortPending)
	{
		return;
	}
	bSortPending = true;
	LastBag = FText::GetEmpty();
	WrongAt.Reset();
	FWAudio::Play2D(PC, TEXT("sort_pick"), 0.8f);
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("count"), 1);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("recycling.sort"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
		{
			This->bSortPending = false;
			This->ApplyGarage(Reply);
		}
	});
}

bool SFWGarageWindow::HasRound() const
{
	const TSharedPtr<FJsonObject>* Round = nullptr;
	return Garage.IsValid() && Garage->TryGetObjectField(TEXT("round"), Round) && Round && Round->IsValid();
}

void SFWGarageWindow::ToggleSorting()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !Garage.IsValid())
	{
		return;
	}
	if (!HasRound())
	{
		Sort();
		return;
	}
	// Putting the bag back unsorted
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("recycling.stop"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
		{
			This->ApplyGarage(Reply);
		}
	});
}

void SFWGarageWindow::PutPiece(int32 Piece, const FString& Bin)
{
	AFWPlayerController* PC = Controller.Get();
	DraggingPiece = -1;
	if (!PC || !HasRound() || PendingPiece >= 0)
	{
		return;
	}
	// The piece stays out of the bag until the server has said where it went
	PendingPiece = Piece;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("piece"), Piece);
	P->SetStringField(TEXT("bin"), Bin);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("recycling.put"), P, [WeakThis, Piece, Bin](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->PendingPiece = -1;
		AFWPlayerController* Inner = This->Controller.Get();
		if (Reply.Type != TEXT("garage"))
		{
			// Out of step with the server (e.g. the bag was closed elsewhere): show the garage as it is
			if (Inner)
			{
				Inner->ToastError(Reply);
			}
			This->LoadGarage();
			return;
		}
		const TSharedPtr<FJsonObject> Put = FWUi::Obj(Reply.Payload, TEXT("put"));
		const bool bCorrect = Put.IsValid() && Put->GetBoolField(TEXT("correct"));
		This->FlashBin = Bin;
		This->FlashAt = FWGarageText::Now();
		This->bFlashGood = bCorrect;
		if (bCorrect)
		{
			FWAudio::Play2D(Inner, FString::Printf(TEXT("sort_%s"), *Bin), 0.9f);
			const TSharedPtr<FJsonObject>* Round = nullptr;
			if (!(Reply.Payload->TryGetObjectField(TEXT("round"), Round) && Round && Round->IsValid()))
			{
				FWAudio::Play2D(Inner, TEXT("sort_done"), 0.8f);
			}
		}
		else
		{
			This->WrongAt.Add(Piece, FWGarageText::Now());
			FWAudio::Play2D(Inner, TEXT("sort_wrong"), 0.8f);
		}
		This->ApplyGarage(Reply);
	});
}

const FSlateBrush* SFWGarageWindow::WasteBrush(const FString& Name)
{
	if (const TSharedPtr<FSlateBrush>* Found = WasteBrushes.Find(Name))
	{
		return Found->Get();
	}
	TSharedPtr<FSlateBrush> Brush;
	const FString Asset = FString::Printf(TEXT("icon_%s"), *Name);
	if (UTexture2D* Texture = FWArt::Texture(*FString::Printf(TEXT("/Game/External/Icons/%s.%s"), *Asset, *Asset)))
	{
		Brush = MakeShared<FSlateBrush>();
		Brush->SetResourceObject(Texture);
		Brush->ImageSize = FVector2D(Texture->GetSizeX(), Texture->GetSizeY());
		WasteTextures.Emplace(Texture);
	}
	WasteBrushes.Add(Name, Brush);
	return Brush.Get();
}

void SFWGarageWindow::LoadTab()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !Garage.IsValid())
	{
		return;
	}
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (Tab == 1 && !FurnitureId(TEXT("furniture_sorting_machine")).IsEmpty())
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		SendSorter(TEXT("sorter.get"), P);
	}
	else if (Tab == 2)
	{
		PC->Send(TEXT("garage.upgrades"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
			if (!This)
			{
				return;
			}
			if (Reply.Type == TEXT("garage.upgrades"))
			{
				This->Upgrades = Reply.Payload;
				This->Rebuild();
			}
			else if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
		});
	}
	else if (Tab == 3 && !FurnitureId(TEXT("furniture_wardrobe")).IsEmpty())
	{
		SendWardrobe(TEXT("wardrobe.get"), MakeShared<FJsonObject>());
	}
}

void SFWGarageWindow::SendSorter(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetStringField(TEXT("sorter"), FurnitureId(TEXT("furniture_sorting_machine")));
	const bool bChange = FCString::Strcmp(Type, TEXT("sorter.get")) != 0;
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, bChange](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("sorter"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->Sorter = Reply.Payload;
		This->ClockOffset = FWUi::Num(This->Sorter, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
		if (bChange)
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
			TArray<FString> Taken;
			for (const TSharedPtr<FJsonObject>& Stack : FWUi::Objects(This->Sorter, TEXT("taken")))
			{
				Taken.Add(FString::Printf(TEXT("%s × %d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity")))));
			}
			if (Taken.Num() > 0)
			{
				Inner->Toast(FText::Format(LOCTEXT("Taken", "Do ekwipunku: {0}"), FText::FromString(FString::Join(Taken, TEXT(", ")))));
			}
		}
		This->Rebuild();
	});
}

void SFWGarageWindow::LoadSorter(int32 Quantity)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("quantity"), Quantity);
	SendSorter(TEXT("sorter.load"), P);
}

void SFWGarageWindow::SendWardrobe(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetStringField(TEXT("id"), FurnitureId(TEXT("furniture_wardrobe")));
	const bool bChange = FCString::Strcmp(Type, TEXT("wardrobe.get")) != 0;
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, bChange](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("wardrobe"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->Wardrobe = Reply.Payload;
		if (bChange)
		{
			// Clothes went between the wardrobe, the backpack and the character
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
			Inner->RefreshLook();
		}
		This->Rebuild();
	});
}

void SFWGarageWindow::WearOutfit(int32 Number, bool bSave)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("number"), Number);
	if (bSave)
	{
		const FString Name = Form->Get(FString::Printf(TEXT("outfit%d"), Number)).TrimStartAndEnd();
		P->SetStringField(TEXT("name"), Name.IsEmpty() ? FString::Printf(TEXT("Komplet %d"), Number) : Name);
	}
	SendWardrobe(bSave ? TEXT("wardrobe.save") : TEXT("wardrobe.wear"), P);
}

void SFWGarageWindow::HangUp(const FString& Item)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), 1);
	SendWardrobe(TEXT("wardrobe.put"), P);
}

void SFWGarageWindow::PlaceFurniture(const FString& Kind)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !Garage.IsValid())
	{
		return;
	}
	const FIntVector Cell = FWGarageText::FreeCell(Garage, Kind);
	if (Cell.X < 0)
	{
		PC->Toast(LOCTEXT("NoRoom", "W garażu nie ma na to miejsca: schowaj coś do ekwipunku."), true);
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("kind"), Kind);
	P->SetNumberField(TEXT("x"), Cell.X);
	P->SetNumberField(TEXT("y"), Cell.Y);
	P->SetNumberField(TEXT("rotation"), Cell.Z);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("garage.place"), P, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		if (AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr)
		{
			if (Reply.Type == TEXT("garage"))
			{
				Inner->PlayUiSound(TEXT("build_place"));
				Inner->RefreshInventory();
			}
			This->ApplyGarage(Reply);
		}
	});
}

void SFWGarageWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = !Garage.IsValid() ? FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted)
		: Tab == 0 ? MakeSorting() : Tab == 1 ? MakeSorter() : Tab == 2 ? MakeUpgrades() : Tab == 3 ? MakeWardrobe() : MakeFurniture();
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWGarageWindow::MakeSorting()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Trash = int32(FWUi::Num(Garage, TEXT("trash")));
	const bool bOpen = HasRound();
	Add(SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
		.Text(FText::FromString(FString::Printf(TEXT("%s · %s"), *FWUi::Str(Garage, TEXT("rank")),
			TEXT("tu zaczyna każdy: worek z kosza wysypujesz i segregujesz ręcznie do koszy na metal, plastik i szkło, a każdy posegregowany worek daje losowy surowiec i 5 XP.")))), 0.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)[FWUi::Icon(PC, TEXT("trash"), 56.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock).Font(FWStyle::Font(20, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FString::Printf(TEXT("Worki śmieci: %d"), Trash)))
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)
				.Text(LOCTEXT("BagRules", "W worku 5 przedmiotów: przeciągnij każdy do właściwego kosza"))
			]
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SBox).WidthOverride(220.f)
			[
				FWUi::SmallButton(bOpen ? LOCTEXT("PutBack", "Odłóż worek") : LOCTEXT("OpenBag", "Otwórz worek"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->ToggleSorting();
					}
				}, !bOpen, bOpen || (Trash > 0 && !bSortPending))
			]
		], 14.f);

	Add(MakeSortingGame(), 14.f);

	// What the last bags gave
	Add(FWUi::Heading(LOCTEXT("Recent", "Ostatnio wysegregowane")), 18.f);
	if (Recent.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoneYet", "Jeszcze nic w tym podejściu."), FWStyle::Muted), 4.f);
	}
	else
	{
		TSharedRef<SWrapBox> Row = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
		for (const TPair<FString, FString>& Item : Recent)
		{
			Row->AddSlot()[SNew(SBox).ToolTipText(FText::FromString(Item.Value))[FWUi::Icon(PC, Item.Key, 38.f)]];
		}
		Add(Row, 6.f);
	}
	Add(FWUi::Note(LOCTEXT("SortingHelp", "Segregujesz ręcznie, więc okno musi być otwarte: zamknięcie odkłada otwarty worek z powrotem do kosza. Przedmiot wrzucony do złego kosza wraca do worka. Automatyczna sortownia (Rynek, 1000 $) to osobna maszyna: włóż do niej do 50 worków, a posegreguje je sama, także gdy cię nie ma. Śmieci kupisz na rynku."),
		FWStyle::Muted), 18.f);
	return Page;
}

TSharedRef<SWidget> SFWGarageWindow::MakeSortingGame()
{
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Area = SNew(SVerticalBox);
	const TSharedPtr<FJsonObject> Round = HasRound() ? FWUi::Obj(Garage, TEXT("round")) : nullptr;
	if (!Round.IsValid())
	{
		// No bag open: the last bag's result, or how to start
		const bool bDone = !LastBag.IsEmpty();
		Area->AddSlot().AutoHeight().HAlign(HAlign_Center).Padding(0, 34, 0, 6)
		[
			SNew(STextBlock).Font(FWStyle::Font(bDone ? 22 : 15, TEXT("Bold"))).ColorAndOpacity(bDone ? FWStyle::Good : FWStyle::Muted)
			.Text(bDone ? LastBag : LOCTEXT("NoBag", "Otwórz worek, a jego zawartość wysypie się tutaj."))
		];
		if (bDone)
		{
			Area->AddSlot().AutoHeight().HAlign(HAlign_Center).Padding(0, 0, 0, 30)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("NextBagHint", "Otwórz następny worek, aby segregować dalej."))
			];
		}
	}
	else
	{
		const int32 Left = int32(FWUi::Num(Round, TEXT("left")));
		const int32 Total = FWUi::Objects(Round, TEXT("pieces")).Num();
		Area->AddSlot().AutoHeight().Padding(0, 0, 0, 8)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
				.Text(LOCTEXT("DragHint", "Przeciągnij każdy przedmiot do właściwego kosza"))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::Format(LOCTEXT("Left", "Zostało {0} z {1}"), FText::AsNumber(Left), FText::AsNumber(Total)))
			]
		];
		Area->AddSlot().AutoHeight().Padding(0, 0, 0, 10)
		[
			FWUi::Bar(float(Total - Left) / FMath::Max(Total, 1), FWStyle::Accent)
		];
		// The bag's pieces, on their places; a sorted one leaves an empty place
		TSharedRef<SHorizontalBox> Pieces = SNew(SHorizontalBox);
		for (const TSharedPtr<FJsonObject>& Piece : FWUi::Objects(Round, TEXT("pieces")))
		{
			const int32 Index = int32(FWUi::Num(Piece, TEXT("index")));
			TSharedRef<SWidget> Place = SNew(SBox).WidthOverride(132.f).HeightOverride(150.f);
			if (!Piece->GetBoolField(TEXT("sorted")))
			{
				Place = SNew(SBox).WidthOverride(132.f).HeightOverride(150.f).HAlign(HAlign_Center)
					[
						SNew(FWGarageText::SWastePiece)
						.Index(Index)
						.Brush(WasteBrush(FWUi::Str(Piece, TEXT("kind"))))
						.Name(FWUi::Str(Piece, TEXT("name")))
						.SinceWrong_Lambda([WeakThis, Index]()
						{
							TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
							const double* At = This ? This->WrongAt.Find(Index) : nullptr;
							return At ? FWGarageText::Now() - *At : 100.0;
						})
						.Away_Lambda([WeakThis, Index]()
						{
							TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
							return This && (This->DraggingPiece == Index || This->PendingPiece == Index);
						})
						.OnPicked([WeakThis](int32 Picked)
						{
							if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
							{
								This->DraggingPiece = Picked;
								FWAudio::Play2D(This->Controller.Get(), TEXT("sort_pick"), 0.6f);
							}
						})
						.OnDragEnded([WeakThis]()
						{
							// Dropped anywhere but on a bin: back into the bag
							if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
							{
								This->DraggingPiece = -1;
							}
						})
					];
			}
			Pieces->AddSlot().FillWidth(1.f).HAlign(HAlign_Center)[Place];
		}
		Area->AddSlot().AutoHeight().Padding(0, 4, 0, 18)[Pieces];
	}

	// The bins stand there all the time, to drop pieces in while a bag is open
	TSharedRef<SHorizontalBox> Bins = SNew(SHorizontalBox);
	for (const TSharedPtr<FJsonObject>& Bin : FWUi::Objects(Garage, TEXT("bins")))
	{
		const FString Id = FWUi::Str(Bin, TEXT("id"));
		Bins->AddSlot().FillWidth(1.f).HAlign(HAlign_Center).Padding(8, 0)
		[
			SNew(FWGarageText::SWasteBin)
			.Brush(WasteBrush(FString::Printf(TEXT("bin_%s"), *Id)))
			.Name(FText::FromString(FWUi::Str(Bin, TEXT("name"))))
			.Color(FWGarageText::BinColor(Id))
			.Flash_Lambda([WeakThis, Id]()
			{
				TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
				return This && This->FlashBin == Id ? FWGarageText::FBinFlash(TPair<double, bool>(FWGarageText::Now() - This->FlashAt, This->bFlashGood))
					: FWGarageText::FBinFlash();
			})
			.OnPiece([WeakThis, Id](int32 Piece)
			{
				if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
				{
					This->PutPiece(Piece, Id);
				}
			})
		];
	}
	Area->AddSlot().AutoHeight()[Bins];

	return SNew(SBorder)
		.BorderImage(FCoreStyle::Get().GetBrush(TEXT("GenericWhiteBox")))
		.BorderBackgroundColor(FLinearColor(0.f, 0.f, 0.f, 0.28f))
		.Padding(FMargin(18.f, 16.f))
		[
			Area
		];
}

TSharedRef<SWidget> SFWGarageWindow::MakeSorter()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (FurnitureId(TEXT("furniture_sorting_machine")).IsEmpty())
	{
		const bool bHave = Owned(TEXT("furniture_sorting_machine")) > 0;
		TSharedRef<SVerticalBox> Page = SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				FWUi::Note(bHave ? LOCTEXT("SorterInBag", "Automatyczna sortownia jest w plecaku: postaw ją w garażu, a posegreguje śmieci sama.")
					: LOCTEXT("NoSorter", "W garażu nie ma automatycznej sortowni. Kupisz ją na rynku (komputer na działce) i postawisz tutaj."), FWStyle::Text)
			];
		if (bHave)
		{
			Page->AddSlot().AutoHeight().Padding(0, 10, 0, 0).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("PlaceSorter", "Postaw w garażu"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->PlaceFurniture(TEXT("furniture_sorting_machine"));
					}
				}, true)
			];
		}
		return Page;
	}
	if (!Sorter.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Queued = int32(FWUi::Num(Sorter, TEXT("queued"))), MaxQueue = int32(FWUi::Num(Sorter, TEXT("maxQueue")));
	const int32 Trash = Owned(TEXT("trash"));
	const int32 Room = MaxQueue - Queued;
	Add(FWUi::Heading(LOCTEXT("Queue", "Kolejka do segregacji")), 0.f);
	TSharedRef<SHorizontalBox> QueueRow = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(PC, TEXT("trash"), 40.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("Śmieci: %d / %d  (masz %d)"), Queued, MaxQueue, Trash)))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(LOCTEXT("TakeTrash", "Wyjmij"), [WeakThis]()
			{
				if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("item"), TEXT("trash"));
					This->SendSorter(TEXT("sorter.take"), P);
				}
			}, false, Queued > 0)
		];
	for (const int32 Amount : { 1, 10, 0 })
	{
		const int32 Count = Amount == 0 ? FMath::Min(Trash, Room) : Amount;
		QueueRow->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(Amount == 0 ? LOCTEXT("LoadAll", "Włóż wszystko") : FText::FromString(FString::Printf(TEXT("Włóż %d"), Amount)), [WeakThis, Count]()
			{
				if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
				{
					This->LoadSorter(Count);
				}
			}, Amount == 0, Count >= 1 && Count <= Trash && Count <= Room)
		];
	}
	Add(QueueRow, 6.f);
	double Started = 0.0;
	const bool bWorking = Sorter->TryGetNumberField(TEXT("startedAt"), Started);
	const double Total = FWUi::Num(Sorter, TEXT("sortSeconds"), 10.0) * 1000.0;
	Add(SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(bWorking ? FWStyle::Accent : Queued > 0 ? FWStyle::Error : FWStyle::Muted)
		.Text_Lambda([WeakThis, bWorking, Started, Total, Queued]()
		{
			TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
			if (bWorking && This)
			{
				return FText::FromString(FString::Printf(TEXT("Segreguje: %d s do następnego przedmiotu"), int32(FMath::CeilToDouble(FMath::Max(Started + Total - This->ServerNow(), 0.0) / 1000.0))));
			}
			return Queued > 0 ? LOCTEXT("SorterFull", "Sortownia jest pełna: wyjmij posegregowane przedmioty, aby segregowała dalej.")
				: LOCTEXT("SorterEmpty", "Włóż śmieci, a sortownia zacznie segregować.");
		}), 10.f);
	if (bWorking)
	{
		Add(FWUi::Bar(TAttribute<float>::CreateLambda([WeakThis, Started, Total]()
			{
				TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
				return This ? float(FMath::Clamp((This->ServerNow() - Started) / Total, 0.0, 1.0)) : 0.f;
			}), FWStyle::Accent), 4.f);
	}
	Add(FWUi::Heading(FText::FromString(FString::Printf(TEXT("Posegregowane  %d / %d"), int32(FWUi::Num(Sorter, TEXT("storedCount"))), int32(FWUi::Num(Sorter, TEXT("maxStored")))))), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Stored = FWUi::Objects(Sorter, TEXT("stored"));
	if (Stored.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("Empty", "pusto"), FWStyle::Muted), 4.f);
	}
	for (const TSharedPtr<FJsonObject>& Stack : Stored)
	{
		const FString Id = FWUi::Str(Stack, TEXT("id"));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 30.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity"))))))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("Take", "Wyjmij"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), Id);
						This->SendSorter(TEXT("sorter.take"), P);
					}
				})
			], 4.f);
	}
	if (Stored.Num() > 0)
	{
		Add(SNew(SBox).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("TakeAll", "Wyjmij wszystko"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->SendSorter(TEXT("sorter.take"), MakeShared<FJsonObject>());
					}
				}, true)
			], 8.f);
	}
	TArray<FString> Chances;
	for (const TSharedPtr<FJsonObject>& Drop : FWUi::Objects(Sorter, TEXT("drops")))
	{
		Chances.Add(FString::Printf(TEXT("%s %d%%"), *FWUi::Str(Drop, TEXT("name")).ToLower(), int32(FWUi::Num(Drop, TEXT("percent")))));
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Sortownia segreguje sama, także gdy cię nie ma: worek w %d s, 5 XP za każdy wyjęty przedmiot. Mieści %d worków w kolejce i %d posegregowanych przedmiotów; gdy jest pełna, staje, dopóki czegoś nie wyjmiesz.\nZ worka: %s."),
		int32(FWUi::Num(Sorter, TEXT("sortSeconds"))), MaxQueue, int32(FWUi::Num(Sorter, TEXT("maxStored"))), *FString::Join(Chances, TEXT(", ")))), FWStyle::Muted), 16.f);
	return Page;
}

TSharedRef<SWidget> SFWGarageWindow::MakeUpgrades()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (!Upgrades.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TArray<TSharedPtr<FJsonObject>> Tracks = FWUi::Objects(Upgrades, TEXT("tracks"));
	int32 Total = 0, Most = 0;
	TSharedPtr<FJsonObject> Chosen;
	TSharedRef<SWrapBox> TrackTabs = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
	for (const TSharedPtr<FJsonObject>& T : Tracks)
	{
		Total += int32(FWUi::Num(T, TEXT("level")));
		Most += int32(FWUi::Num(T, TEXT("maxLevel")));
		const FString Id = FWUi::Str(T, TEXT("id"));
		if (Id == Track)
		{
			Chosen = T;
		}
		TrackTabs->AddSlot()
		[
			FWUi::SmallButton(FText::FromString(FString::Printf(TEXT("%s  %d/%d"), *FWUi::Str(T, TEXT("name")), int32(FWUi::Num(T, TEXT("level"))), int32(FWUi::Num(T, TEXT("maxLevel"))))),
				[WeakThis, Id]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->Track = Id;
						This->Rebuild();
					}
				}, Id == Track)
		];
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Twój lokal: %s. Ulepszenia: %d z %d. Każdy poziom daje premię na zawsze."), *FWUi::Str(Upgrades, TEXT("rank")), Total, Most)), FWStyle::Muted), 0.f);
	Add(TrackTabs, 8.f);
	if (!Chosen.IsValid())
	{
		return Page;
	}
	const int32 Level = int32(FWUi::Num(Chosen, TEXT("level")));
	const TArray<TSharedPtr<FJsonObject>> Steps = FWUi::Objects(Chosen, TEXT("steps"));
	const FString Unit = FWUi::Str(Chosen, TEXT("unit"));
	Add(SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
		.Text(FText::FromString(FString::Printf(TEXT("Teraz: %s"), Level == 0 ? *FWUi::Str(Chosen, TEXT("start")) : *FWUi::Str(Steps[Level - 1], TEXT("name"))))), 14.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Good)
		.Text(FText::FromString(FString::Printf(TEXT("%s %d%s (każdy poziom: +%d%s)"), *FWUi::Str(Chosen, TEXT("bonusName")), int32(FWUi::Num(Chosen, TEXT("bonus"))), *Unit,
			int32(FWUi::Num(Chosen, TEXT("perLevel"))), *Unit))), 2.f);

	// The next level: what it costs and needs
	if (Steps.IsValidIndex(Level))
	{
		const TSharedPtr<FJsonObject>& Step = Steps[Level];
		const double Price = FWUi::Num(Step, TEXT("priceCents"));
		bool bReady = PC && double(PC->GetBalanceCents()) >= Price;
		TSharedRef<SVerticalBox> Card = SNew(SVerticalBox);
		Card->AddSlot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
				.Text(FText::FromString(FString::Printf(TEXT("Poziom %d: %s"), int32(FWUi::Num(Step, TEXT("level"))), *FWUi::Str(Step, TEXT("name")))))
			]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
				.Text(FText::FromString(FString::Printf(TEXT("+%d%s"), int32(FWUi::Num(Chosen, TEXT("perLevel"))), *Unit)))
			]
		];
		Card->AddSlot().AutoHeight().Padding(0, 6, 0, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(bReady ? FWStyle::Text : FWStyle::Error).Text(FWUi::Money(Price))
		];
		TSharedRef<SWrapBox> Materials = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(16.f, 6.f));
		for (const TSharedPtr<FJsonObject>& Material : FWUi::Objects(Step, TEXT("materials")))
		{
			const FString Id = FWUi::Str(Material, TEXT("id"));
			const int32 Need = int32(FWUi::Num(Material, TEXT("quantity"))), Have = Owned(Id);
			bReady &= Have >= Need;
			Materials->AddSlot()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Icon(PC, Id, 28.f)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Have >= Need ? FWStyle::Text : FWStyle::Error)
					.Text(FText::FromString(FString::Printf(TEXT("%s %d/%d"), *FWUi::Str(Material, TEXT("name")), FMath::Min(Have, Need), Need)))
				]
			];
		}
		Card->AddSlot().AutoHeight().Padding(0, 8, 0, 0)[Materials];
		for (const TSharedPtr<FJsonObject>& Need : FWUi::Objects(Step, TEXT("requires")))
		{
			int32 OtherLevel = 0;
			for (const TSharedPtr<FJsonObject>& T : Tracks)
			{
				if (FWUi::Str(T, TEXT("id")) == FWUi::Str(Need, TEXT("track")))
				{
					OtherLevel = int32(FWUi::Num(T, TEXT("level")));
				}
			}
			const bool bMet = OtherLevel >= int32(FWUi::Num(Need, TEXT("level")));
			bReady &= bMet;
			Card->AddSlot().AutoHeight().Padding(0, 4, 0, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(bMet ? FWStyle::Good : FWStyle::Error)
				.Text(FText::FromString(FString::Printf(TEXT("%s Wymaga: %s na poziomie %d"), bMet ? TEXT("✓") : TEXT("✗"), *FWUi::Str(Need, TEXT("name")), int32(FWUi::Num(Need, TEXT("level"))))))
			];
		}
		const FString TrackId = FWUi::Str(Chosen, TEXT("id"));
		Card->AddSlot().AutoHeight().Padding(0, 10, 0, 0).HAlign(HAlign_Left)
		[
			SNew(SBox).ToolTipText(bReady ? FText::GetEmpty() : LOCTEXT("NotReady", "Brakuje pieniędzy, materiałów albo innego ulepszenia."))
			[
				FWUi::SmallButton(LOCTEXT("Upgrade", "Ulepsz"), [WeakThis, TrackId]()
				{
					TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
					AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
					if (!Inner)
					{
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("track"), TrackId);
					Inner->Send(TEXT("garage.upgrade"), P, [WeakThis](const FFWNetReply& Reply)
					{
						TSharedPtr<SFWGarageWindow> Again = WeakThis.Pin();
						AFWPlayerController* Owner = Again ? Again->Controller.Get() : nullptr;
						if (!Owner)
						{
							return;
						}
						if (Reply.Type != TEXT("garage.upgrades"))
						{
							Owner->ToastError(Reply);
							return;
						}
						Again->Upgrades = Reply.Payload;
						Owner->SetBalanceCents(int64(FWUi::Num(Reply.Payload, TEXT("balanceCents"))));
						Owner->PlayUiSound(TEXT("build_place"));
						Owner->RefreshInventory();
						Owner->Toast(LOCTEXT("Upgraded", "Garaż ulepszony"));
						Again->Rebuild();
					});
				}, true, bReady)
			]
		];
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(14.f)[Card], 14.f);
	}
	else
	{
		Add(SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).Text(LOCTEXT("Maxed", "Ulepszone do końca.")), 14.f);
	}

	// All levels of the track
	Add(FWUi::Heading(LOCTEXT("AllLevels", "Wszystkie poziomy")), 18.f);
	for (int32 Index = 0; Index < Steps.Num(); ++Index)
	{
		const bool bDone = Index < Level;
		const FLinearColor Color = bDone ? FWStyle::Good : Index == Level ? FWStyle::Accent : FWStyle::Muted;
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)
			[
				SNew(SBox).WidthOverride(46.f)[SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Color)
					.Text(FText::FromString(FString::Printf(TEXT("%s %d."), bDone ? TEXT("✓") : Index == Level ? TEXT("▶") : TEXT(" "), Index + 1)))]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)[SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Color).Text(FText::FromString(FWUi::Str(Steps[Index], TEXT("name"))))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(Color)
				.Text(FText::FromString(FString::Printf(TEXT("+%d%s"), (Index + 1) * int32(FWUi::Num(Chosen, TEXT("perLevel"))), *Unit)))
			], 2.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWGarageWindow::MakeWardrobe()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (FurnitureId(TEXT("furniture_wardrobe")).IsEmpty())
	{
		const bool bHave = Owned(TEXT("furniture_wardrobe")) > 0;
		TSharedRef<SVerticalBox> Page = SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				FWUi::Note(bHave ? LOCTEXT("WardrobeInBag", "Szafa na ubrania jest w plecaku: postaw ją w garażu, a zapiszesz w niej komplety ubrań.")
					: LOCTEXT("NoWardrobe", "W garażu nie ma szafy na ubrania. Zrobisz ją albo kupisz i postawisz tutaj."), FWStyle::Text)
			];
		if (bHave)
		{
			Page->AddSlot().AutoHeight().Padding(0, 10, 0, 0).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("PlaceWardrobe", "Postaw w garażu"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->PlaceFurniture(TEXT("furniture_wardrobe"));
					}
				}, true)
			];
		}
		return Page;
	}
	if (!Wardrobe.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	auto Piece = [PC](const TSharedPtr<FJsonObject>& Pieces, int32 Slot, float Size) -> TSharedRef<SWidget>
	{
		const TSharedPtr<FJsonObject> Item = FWUi::Obj(Pieces, FWGarageText::Slots[Slot]);
		return SNew(SBox).WidthOverride(Size + 6.f).HeightOverride(Size + 6.f).HAlign(HAlign_Center).VAlign(VAlign_Center)
			.ToolTipText(FText::FromString(FString::Printf(TEXT("%s: %s"), FWGarageText::SlotNames[Slot], Item.IsValid() ? *FWUi::Str(Item, TEXT("name")) : TEXT("nic"))))
			[
				Item.IsValid() ? FWUi::Icon(PC, FWUi::Str(Item, TEXT("id")), Size)
					: StaticCastSharedRef<SWidget>(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(0.f)[SNew(SBox).WidthOverride(Size).HeightOverride(Size)])
			];
	};
	Add(FWUi::Heading(LOCTEXT("Outfits", "Komplety")), 0.f);
	for (const TSharedPtr<FJsonObject>& Outfit : FWUi::Objects(Wardrobe, TEXT("outfits")))
	{
		const int32 Number = int32(FWUi::Num(Outfit, TEXT("number")));
		const bool bSaved = FWUi::Bool(Outfit, TEXT("saved"));
		const FString Key = FString::Printf(TEXT("outfit%d"), Number);
		if (!Form->Has(Key) && bSaved)
		{
			Form->Set(Key, FWUi::Str(Outfit, TEXT("name")));
		}
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				Form->Field(Key, FText::FromString(FString::Printf(TEXT("Komplet %d"), Number)), 180.f)
			];
		const TSharedPtr<FJsonObject> Pieces = FWUi::Obj(Outfit, TEXT("pieces"));
		for (int32 Slot = 0; Slot < 4; ++Slot)
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)[Piece(Pieces, Slot, 30.f)];
		}
		Row->AddSlot().FillWidth(1.f);
		Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(LOCTEXT("Wear", "Załóż"), [WeakThis, Number]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->WearOutfit(Number, false); } }, true, bSaved)
		];
		Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(LOCTEXT("SaveOutfit", "Zapisz to, co mam na sobie"), [WeakThis, Number]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->WearOutfit(Number, true); } })
		];
		if (bSaved)
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
			[
				FWUi::SmallButton(LOCTEXT("Forget", "Usuń"), [WeakThis, Number]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("number"), Number);
						This->Form->Set(FString::Printf(TEXT("outfit%d"), Number), FString());
						This->SendWardrobe(TEXT("wardrobe.forget"), P);
					}
				})
			];
		}
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(8.f)[Row], 6.f);
	}
	Add(FWUi::Note(LOCTEXT("OutfitsHint", "Zapisz, co masz na sobie, a potem przebieraj się jednym kliknięciem. Ubrania bierze z szafy albo z ekwipunku, a zdjęte wiesza w szafie."), FWStyle::Muted), 6.f);

	Add(FWUi::Heading(LOCTEXT("Worn", "Na sobie")), 16.f);
	TSharedRef<SHorizontalBox> Worn = SNew(SHorizontalBox);
	for (int32 Slot = 0; Slot < 4; ++Slot)
	{
		Worn->AddSlot().AutoWidth().Padding(0, 0, 8, 0)[Piece(FWUi::Obj(Wardrobe, TEXT("worn")), Slot, 40.f)];
	}
	Add(Worn, 4.f);

	Add(FWUi::Heading(LOCTEXT("Stored", "W szafie")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Stored = FWUi::Objects(Wardrobe, TEXT("stored"));
	if (Stored.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("WardrobeEmpty", "Pusto."), FWStyle::Muted), 4.f);
	}
	for (const TSharedPtr<FJsonObject>& Stack : Stored)
	{
		const FString Id = FWUi::Str(Stack, TEXT("id"));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 28.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity"))))))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("TakeOut", "Wyjmij"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), Id);
						P->SetNumberField(TEXT("quantity"), 1);
						This->SendWardrobe(TEXT("wardrobe.take"), P);
					}
				})
			], 4.f);
	}

	Add(FWUi::Heading(LOCTEXT("Hang", "Powieś z ekwipunku")), 16.f);
	bool bAny = false;
	for (const FFWStack& Stack : PC ? PC->GetInventory() : TArray<FFWStack>())
	{
		if (!Stack.Id.StartsWith(TEXT("clothes_")))
		{
			continue;
		}
		bAny = true;
		const FString Id = Stack.Id;
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 28.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *Stack.Name, Stack.Quantity)))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("HangUp", "Powieś"), [WeakThis, Id]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->HangUp(Id); } })
			], 4.f);
	}
	if (!bAny)
	{
		Add(FWUi::Note(LOCTEXT("NoClothes", "W ekwipunku nie ma ubrań."), FWStyle::Muted), 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWGarageWindow::MakeFurniture()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Room = FWUi::Obj(Garage, TEXT("room"));
	const FIntPoint Size(int32(FWUi::Num(Room, TEXT("width"), 16.0)), int32(FWUi::Num(Room, TEXT("depth"), 12.0)));
	const TArray<FWGarageText::FPiece> Pieces = FWGarageText::Pieces(Garage);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Garaż z góry: %s × %s m."), *FString::SanitizeFloat(Size.X * FWUi::Num(Room, TEXT("cellSize"), 0.5)),
		*FString::SanitizeFloat(Size.Y * FWUi::Num(Room, TEXT("cellSize"), 0.5)))), FWStyle::Muted), 0.f);
	Add(SNew(SBox).HAlign(HAlign_Center)
		[
			SNew(FWGarageText::SRoom).Room(Size).Pieces(Pieces).Floor(int32(FWUi::Num(FWUi::Obj(Garage, TEXT("upgrades")), TEXT("floor"))))
		], 10.f);

	// From the backpack into the room
	const TSharedPtr<FJsonObject> Furniture = FWUi::Obj(Garage, TEXT("furniture"));
	bool bAny = false;
	TSharedRef<SVerticalBox> Bag = SNew(SVerticalBox);
	for (const FFWStack& Stack : PC ? PC->GetInventory() : TArray<FFWStack>())
	{
		if (!Furniture.IsValid() || !Furniture->HasField(Stack.Id))
		{
			continue;
		}
		bAny = true;
		const FString Kind = Stack.Id;
		Bag->AddSlot().AutoHeight().Padding(0, 3)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Kind, 30.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *Stack.Name, Stack.Quantity)))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("Place", "Postaw w garażu"), [WeakThis, Kind]() { if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin()) { This->PlaceFurniture(Kind); } }, true)
			]
		];
	}
	Add(FWUi::Heading(LOCTEXT("FromBag", "Z plecaka")), 16.f);
	Add(bAny ? StaticCastSharedRef<SWidget>(Bag) : FWUi::Note(LOCTEXT("NoFurniture", "W plecaku nie ma mebli do garażu."), FWStyle::Muted), 4.f);

	// In the room
	Add(FWUi::Heading(LOCTEXT("InRoom", "W garażu")), 16.f);
	for (const FWGarageText::FPiece& Piece : Pieces)
	{
		const FString Id = Piece.Id;
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Piece.Kind, 26.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(Piece.Name))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("PickUp", "Schowaj do ekwipunku"), [WeakThis, Id]()
				{
					TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
					AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
					if (!Inner)
					{
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("id"), Id);
					Inner->Send(TEXT("garage.pickup"), P, [WeakThis](const FFWNetReply& Reply)
					{
						TSharedPtr<SFWGarageWindow> Again = WeakThis.Pin();
						if (AFWPlayerController* Owner = Again ? Again->Controller.Get() : nullptr)
						{
							if (Reply.Type == TEXT("garage"))
							{
								Owner->PlayUiSound(TEXT("build_pickup"));
								Owner->RefreshInventory();
							}
							Again->ApplyGarage(Reply);
						}
					});
				})
			], 3.f);
	}
	return Page;
}

void SFWGarageWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() < NextPoll)
	{
		return;
	}
	const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
	if (Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText")))
	{
		NextPoll = FPlatformTime::Seconds() + 2.0;
		return;
	}
	if (HasRound())
	{
		NextPoll = FPlatformTime::Seconds() + 5.0;
	}
	else
	{
		LoadGarage(true);
	}
	if (Tab == 1 && Sorter.IsValid())
	{
		SendSorter(TEXT("sorter.get"), MakeShared<FJsonObject>());
	}
}

FReply SFWGarageWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::U)
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
