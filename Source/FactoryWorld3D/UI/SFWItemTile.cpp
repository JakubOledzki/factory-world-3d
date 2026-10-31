#include "UI/SFWItemTile.h"

#include "UI/FWStyle.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

TSharedRef<FFWItemDrag> FFWItemDrag::New(const FString& Item, int32 Quantity, EFWItemPlace From, int32 Index, const FSlateBrush* Icon)
{
	TSharedRef<FFWItemDrag> Drag = MakeShared<FFWItemDrag>();
	Drag->Item = Item;
	Drag->Quantity = Quantity;
	Drag->From = From;
	Drag->Index = Index;
	Drag->Icon = Icon;
	Drag->Construct();
	return Drag;
}

TSharedPtr<SWidget> FFWItemDrag::GetDefaultDecorator() const
{
	return SNew(SBox)
		.WidthOverride(64.f)
		.HeightOverride(64.f)
		[
			SNew(SOverlay)
			+ SOverlay::Slot()
			[
				SNew(SImage).Image(Icon)
			]
			+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Bottom)
			[
				SNew(STextBlock)
				.Font(FWStyle::Font(13, TEXT("Bold")))
				.ShadowOffset(FVector2D(1, 1))
				.Text(Quantity > 1 ? FText::AsNumber(Quantity) : FText::GetEmpty())
			]
		];
}

void SFWItemTile::Construct(const FArguments& InArgs)
{
	Item = InArgs._Item;
	Quantity = InArgs._Quantity;
	Place = InArgs._Place;
	Index = InArgs._Index;
	IconOf = InArgs._IconOf;
	NameOf = InArgs._NameOf;
	OnDropped = InArgs._OnDropped;
	OnQuick = InArgs._OnQuick;
	SetToolTipText(TAttribute<FText>::CreateLambda([this]()
	{
		const FString Id = Item.Get();
		return Id.IsEmpty() || !NameOf ? FText::GetEmpty() : NameOf(Id);
	}));

	ChildSlot
	[
		SNew(SBox)
		.WidthOverride(InArgs._Size)
		.HeightOverride(InArgs._Size)
		[
			SNew(SOverlay)
			+ SOverlay::Slot()
			[
				SNew(SImage)
				.Image(FWStyle::Panel())
				.ColorAndOpacity_Lambda([this]() { return bHovered ? FLinearColor(1.4f, 1.3f, 1.2f, 1.f) : FLinearColor(0.85f, 0.85f, 0.85f, 1.f); })
			]
			+ SOverlay::Slot()
			[
				SNew(SImage).Image_Lambda([this]() { return FWStyle::SlotFrame(bHovered); })
			]
			+ SOverlay::Slot().Padding(6.f)
			[
				SNew(SImage)
				.Image_Lambda([this]() { const FString Id = Item.Get(); return Id.IsEmpty() || !IconOf ? nullptr : IconOf(Id); })
				.Visibility_Lambda([this]() { return Item.Get().IsEmpty() ? EVisibility::Hidden : EVisibility::HitTestInvisible; })
			]
			+ SOverlay::Slot().HAlign(HAlign_Left).VAlign(VAlign_Top).Padding(6.f, 3.f)
			[
				SNew(STextBlock)
				.Font(FWStyle::Font(9, TEXT("Bold")))
				.ColorAndOpacity(FWStyle::Muted)
				.Text(InArgs._Label)
			]
			+ SOverlay::Slot().HAlign(HAlign_Right).VAlign(VAlign_Bottom).Padding(6.f, 2.f)
			[
				SNew(STextBlock)
				.Font(FWStyle::Font(12, TEXT("Bold")))
				.ColorAndOpacity(FWStyle::Text)
				.ShadowOffset(FVector2D(1, 1))
				.Text_Lambda([this]()
				{
					const int32 Count = Quantity.Get();
					return Item.Get().IsEmpty() || Count <= 0 ? FText::GetEmpty() : FText::AsNumber(Count);
				})
			]
		]
	];
}

FReply SFWItemTile::OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (Event.GetEffectingButton() != EKeys::LeftMouseButton || Item.Get().IsEmpty())
	{
		return FReply::Unhandled();
	}
	if (Event.IsShiftDown() && OnQuick.IsBound())
	{
		OnQuick.Execute();
		return FReply::Handled();
	}
	return FReply::Handled().DetectDrag(SharedThis(this), EKeys::LeftMouseButton);
}

FReply SFWItemTile::OnDragDetected(const FGeometry& Geometry, const FPointerEvent& Event)
{
	const FString Id = Item.Get();
	if (Id.IsEmpty())
	{
		return FReply::Unhandled();
	}
	return FReply::Handled().BeginDragDrop(FFWItemDrag::New(Id, Quantity.Get(), Place, Index, IconOf ? IconOf(Id) : nullptr));
}

void SFWItemTile::OnDragEnter(const FGeometry& Geometry, const FDragDropEvent& Event)
{
	bHovered = Event.GetOperationAs<FFWItemDrag>().IsValid();
}

void SFWItemTile::OnDragLeave(const FDragDropEvent& Event)
{
	bHovered = false;
}

FReply SFWItemTile::OnDrop(const FGeometry& Geometry, const FDragDropEvent& Event)
{
	bHovered = false;
	if (TSharedPtr<FFWItemDrag> Drag = Event.GetOperationAs<FFWItemDrag>())
	{
		OnDropped.ExecuteIfBound(*Drag);
		return FReply::Handled();
	}
	return FReply::Unhandled();
}
