#include "UI/SFWFactoryWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Input/SSlider.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWFactory"

namespace FWTimelapse
{
	// How long a day of the timelapse stays on the screen
	constexpr double PlayEvery = 0.35;

	FLinearColor ColorOf(const FString& Kind)
	{
		if (Kind.StartsWith(TEXT("logistics_")))
		{
			return FLinearColor::FromSRGBColor(FColor(0xd9, 0xa5, 0x20));
		}
		if (Kind.StartsWith(TEXT("storage_")))
		{
			return FLinearColor::FromSRGBColor(FColor(0x8b, 0x6b, 0x3d));
		}
		if (Kind.StartsWith(TEXT("power_")))
		{
			return FLinearColor::FromSRGBColor(FColor(0x4f, 0xa3, 0xe0));
		}
		if (Kind.StartsWith(TEXT("decor_")))
		{
			return FLinearColor::FromSRGBColor(FColor(0xe0, 0x5a, 0x8a));
		}
		return Kind.StartsWith(TEXT("furniture_")) ? FLinearColor::FromSRGBColor(FColor(0xc8, 0xc4, 0xbb)) : FLinearColor::FromSRGBColor(FColor(0x7d, 0x8a, 0x72));
	}

	// The plot from above: each object a square the size of its footprint
	class SMap : public SLeafWidget
	{
	public:
		SLATE_BEGIN_ARGS(SMap) {}
		SLATE_END_ARGS()

		void Construct(const FArguments&)
		{
			SetClipping(EWidgetClipping::ClipToBounds);
		}

		// Shows the square of View tiles from Corner
		void Show(FIntPoint InCorner, int32 View, const TArray<TSharedPtr<FJsonValue>>& InObjects)
		{
			Corner = InCorner;
			Tiles = FMath::Max(View, 1);
			Objects.Reset();
			for (const TSharedPtr<FJsonValue>& Value : InObjects)
			{
				const TArray<TSharedPtr<FJsonValue>>* Parts = nullptr;
				if (Value->TryGetArray(Parts) && Parts->Num() >= 3)
				{
					Objects.Add({ (*Parts)[0]->AsString(), FIntPoint(int32((*Parts)[1]->AsNumber()), int32((*Parts)[2]->AsNumber())) });
				}
			}
			Invalidate(EInvalidateWidgetReason::Paint);
		}

		virtual FVector2D ComputeDesiredSize(float) const override { return FVector2D(Side, Side); }

		virtual int32 OnPaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& Culling, FSlateWindowElementList& Out, int32 Layer,
			const FWidgetStyle& Style, bool bParentEnabled) const override
		{
			const FSlateBrush* White = FCoreStyle::Get().GetBrush(TEXT("GenericWhiteBox"));
			FSlateDrawElement::MakeBox(Out, Layer, Geometry.ToPaintGeometry(), White, ESlateDrawEffect::None, FLinearColor::FromSRGBColor(FColor(0x26, 0x30, 0x1f)));
			const float Cell = Side / float(Tiles);
			for (const TPair<FString, FIntPoint>& Object : Objects)
			{
				const int32 Size = FWCatalog::FootprintSize(Object.Key);
				const int32 Low = (Size - 1) / 2;
				const FVector2f At(float(Object.Value.X - Low - Corner.X) * Cell, float(Object.Value.Y - Low - Corner.Y) * Cell);
				if (At.X < -Cell * Size || At.Y < -Cell * Size || At.X >= Side || At.Y >= Side)
				{
					continue;
				}
				const float Extent = FMath::Max(float(Size) * Cell - 0.6f, 1.2f);
				FSlateDrawElement::MakeBox(Out, Layer + 1, Geometry.ToPaintGeometry(FVector2f(Extent, Extent), FSlateLayoutTransform(At + FVector2f(0.3f, 0.3f))), White,
					ESlateDrawEffect::None, ColorOf(Object.Key));
			}
			return Layer + 1;
		}

	private:
		static constexpr float Side = 520.f;
		FIntPoint Corner = FIntPoint(0, 0);
		int32 Tiles = 1;
		TArray<TPair<FString, FIntPoint>> Objects;
	};
}

namespace FWFactoryText
{
	FString Biome(int32 Id)
	{
		static const TCHAR* Names[] = { TEXT("ocean"), TEXT("jezioro"), TEXT("las"), TEXT("pola uprawne"), TEXT("łąki"), TEXT("pustynia"), TEXT("góry"),
			TEXT("tundra"), TEXT("lodowiec"), TEXT("miasto"), TEXT("wybrzeże"), TEXT("brzeg jeziora"), TEXT("oaza") };
		return Id >= 0 && Id < UE_ARRAY_COUNT(Names) ? Names[Id] : FString();
	}

	void Toast(AFWPlayerController* PC, const FString& Text, bool bError = false)
	{
		if (PC)
		{
			PC->Toast(FText::FromString(Text), bError);
		}
	}
}

void SFWFactoryWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	SAssignNew(Map, FWTimelapse::SMap);
	if (const AFWPlayerController* PC = Controller.Get(); PC && PC->GetEnteredPlot().IsValid())
	{
		Likes = int32(FWUi::Num(PC->GetEnteredPlot(), TEXT("likes")));
		bLiked = FWUi::Bool(PC->GetEnteredPlot(), TEXT("liked"));
		bOwnPlot = FWUi::Strings(PC->GetEnteredPlot(), TEXT("rights")).Contains(TEXT("owner"));
	}
	TWeakPtr<SFWFactoryWindow> WeakThis = SharedThis(this);
	auto MakeTab = [WeakThis](const FText& Text, int32 Index)
	{
		return FWUi::TabButton(Text, [WeakThis, Index]() { TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin(); return This && This->Tab == Index; },
			[WeakThis, Index]() { if (TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin()) { This->ShowTab(Index); } });
	};
	TSharedRef<SHorizontalBox> Tabs = SNew(SHorizontalBox);
	Tabs->AddSlot().AutoWidth()[MakeTab(LOCTEXT("TabPlans", "Plany"), 0)];
	Tabs->AddSlot().AutoWidth()[MakeTab(LOCTEXT("TabTimelapse", "Timelapse"), 1)];
	Tabs->AddSlot().AutoWidth()[MakeTab(LOCTEXT("TabGallery", "Galeria fabryk"), 2)];
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Fabryka"), SNullWidget::NullWidget, Tabs, SAssignNew(Content, SBox), 900.f, 800.f)
	];
	// The names of all objects, for the plans
	if (AFWPlayerController* PC = Controller.Get(); PC && PC->GetPlot())
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetNumberField(TEXT("x"), PC->GetPlot()->GetPlotX());
		P->SetNumberField(TEXT("y"), PC->GetPlot()->GetPlotY());
		P->SetBoolField(TEXT("names"), true);
		PC->Send(TEXT("logistics.get"), P, [WeakThis](const FFWNetReply& Reply)
		{
			TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
			const TSharedPtr<FJsonObject> Names = This ? FWUi::Obj(Reply.Payload, TEXT("names")) : nullptr;
			if (!Names.IsValid())
			{
				return;
			}
			for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : Names->Values)
			{
				This->ItemNames.Add(Pair.Key, Pair.Value->AsString());
			}
			if (This->Tab == 0)
			{
				This->Rebuild();
			}
		});
	}
	Load();
}

FString SFWFactoryWindow::NameOf(const FString& Item) const
{
	const FString* Name = ItemNames.Find(Item);
	return Name ? *Name : Controller.IsValid() ? Controller->ItemName(Item) : Item;
}

void SFWFactoryWindow::ShowTab(int32 InTab)
{
	Tab = FMath::Clamp(InTab, 0, 2);
	bPlaying = false;
	Load();
}

void SFWFactoryWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Rebuild();
	TWeakPtr<SFWFactoryWindow> WeakThis = SharedThis(this);
	if (Tab == 0)
	{
		PC->Send(TEXT("plans.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin())
			{
				This->ApplyPlans(Reply);
			}
		});
	}
	else if (Tab == 1 && PC->GetPlot())
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetNumberField(TEXT("x"), PC->GetPlot()->GetPlotX());
		P->SetNumberField(TEXT("y"), PC->GetPlot()->GetPlotY());
		PC->Send(TEXT("timelapse.get"), P, [WeakThis](const FFWNetReply& Reply)
		{
			TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
			if (!This || Reply.Type != TEXT("timelapse"))
			{
				return;
			}
			This->Timelapse = Reply.Payload;
			// The film plays at once, from the first day
			const int32 Frames = FWUi::Objects(This->Timelapse, TEXT("frames")).Num();
			This->Frame = 0;
			This->bPlaying = Frames > 1;
			This->NextFrameAt = FPlatformTime::Seconds() + FWTimelapse::PlayEvery;
			This->Rebuild();
		});
	}
	else if (Tab == 2)
	{
		PC->Send(TEXT("plot.gallery"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
			if (This && Reply.Type == TEXT("plot.gallery"))
			{
				This->Gallery = Reply.Payload;
				This->Rebuild();
			}
		});
	}
}

void SFWFactoryWindow::ApplyPlans(const FFWNetReply& Reply)
{
	if (Reply.Type != TEXT("plans"))
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->ToastError(Reply);
		}
		return;
	}
	Plans = Reply.Payload;
	if (Tab == 0)
	{
		Rebuild();
	}
}

void SFWFactoryWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = Tab == 0 ? MakePlans() : Tab == 1 ? MakeTimelapse() : MakeGallery();
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page]);
}

void SFWFactoryWindow::SavePlan(const FString& Name, int32 Radius)
{
	AFWPlayerController* PC = Controller.Get();
	AFWPlot* Plot = PC ? PC->GetPlot() : nullptr;
	if (!Plot || !PC->GetPawn())
	{
		return;
	}
	// What stands around the player, by its place from the top-left corner of all of it, as the server keeps plans
	const FIntPoint At = Plot->TileAt(PC->GetPawn()->GetActorLocation());
	TArray<const FFWPlotObject*> Around;
	FIntPoint Corner(MAX_int32, MAX_int32);
	for (const TPair<FString, FFWPlotObject>& Pair : Plot->GetObjects())
	{
		const FIntPoint D = Pair.Value.Tile - At;
		if (FMath::Abs(D.X) <= Radius && FMath::Abs(D.Y) <= Radius)
		{
			Around.Add(&Pair.Value);
			Corner = FIntPoint(FMath::Min(Corner.X, Pair.Value.Tile.X), FMath::Min(Corner.Y, Pair.Value.Tile.Y));
		}
	}
	TArray<TSharedPtr<FJsonValue>> Objects;
	for (const FFWPlotObject* Object : Around)
	{
		TSharedPtr<FJsonObject> O = MakeShared<FJsonObject>();
		O->SetStringField(TEXT("kind"), Object->Kind);
		O->SetNumberField(TEXT("dx"), Object->Tile.X - Corner.X);
		O->SetNumberField(TEXT("dy"), Object->Tile.Y - Corner.Y);
		O->SetNumberField(TEXT("rotation"), Object->Rotation);
		Objects.Add(MakeShared<FJsonValueObject>(O));
	}
	if (Objects.Num() == 0)
	{
		FWFactoryText::Toast(PC, FString::Printf(TEXT("W promieniu %d pól wokół ciebie nic nie stoi."), Radius), true);
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("name"), Name.TrimStartAndEnd());
	P->SetArrayField(TEXT("objects"), Objects);
	TWeakPtr<SFWFactoryWindow> WeakThis = SharedThis(this);
	const int32 Count = Objects.Num();
	PC->Send(TEXT("plans.save"), P, [WeakThis, Count](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type == TEXT("plans"))
		{
			FWFactoryText::Toast(This->Controller.Get(), FString::Printf(TEXT("Plan zapisany: %d obiektów."), Count));
			This->Form->Set(TEXT("name"), FString());
		}
		This->ApplyPlans(Reply);
	});
}

void SFWFactoryWindow::UsePlan(int32 Index)
{
	AFWPlayerController* PC = Controller.Get();
	AFWPlot* Plot = PC ? PC->GetPlot() : nullptr;
	const TArray<TSharedPtr<FJsonObject>> List = FWUi::Objects(Plans, TEXT("plans"));
	if (!Plot || !PC->GetPawn() || !List.IsValidIndex(Index))
	{
		return;
	}
	// Put up a few tiles in front of the player, as the 2D game puts it under the mouse
	const FVector Ahead = PC->GetPawn()->GetActorLocation() + PC->GetPawn()->GetActorForwardVector().GetSafeNormal2D() * Plot->GetTileSize() * 4.f;
	const TArray<TSharedPtr<FJsonObject>> Objects = FWUi::Objects(List[Index], TEXT("objects"));
	// The plan's middle on that tile
	FIntPoint Extent(0, 0);
	for (const TSharedPtr<FJsonObject>& Object : Objects)
	{
		Extent = FIntPoint(FMath::Max(Extent.X, int32(FWUi::Num(Object, TEXT("dx")))), FMath::Max(Extent.Y, int32(FWUi::Num(Object, TEXT("dy")))));
	}
	const FIntPoint Origin = Plot->TileAt(Ahead) - FIntPoint(Extent.X / 2, Extent.Y / 2);
	TMap<FString, int32> Needed;
	for (const TSharedPtr<FJsonObject>& Object : Objects)
	{
		Needed.FindOrAdd(FWUi::Str(Object, TEXT("kind")))++;
	}
	// The objects missing from the backpack, said before anything is built
	TArray<FString> Missing;
	for (const TPair<FString, int32>& Need : Needed)
	{
		const FFWStack* Have = PC->FindStack(Need.Key);
		const int32 Short = Need.Value - (Have ? Have->Quantity : 0);
		if (Short > 0)
		{
			Missing.Add(FString::Printf(TEXT("%s × %d"), *NameOf(Need.Key), Short));
		}
	}
	if (Missing.Num() > 0)
	{
		FWFactoryText::Toast(PC, TEXT("Brakuje: ") + FString::Join(Missing, TEXT(", ")), true);
		return;
	}
	for (const TSharedPtr<FJsonObject>& Object : Objects)
	{
		PC->Place(FWUi::Str(Object, TEXT("kind")), Origin + FIntPoint(int32(FWUi::Num(Object, TEXT("dx"))), int32(FWUi::Num(Object, TEXT("dy")))),
			int32(FWUi::Num(Object, TEXT("rotation"))));
	}
	FWFactoryText::Toast(PC, FString::Printf(TEXT("Stawiam plan „%s”: %d obiektów."), *FWUi::Str(List[Index], TEXT("name")), Objects.Num()));
	PC->CloseWindow();
}

TSharedRef<SWidget> SFWFactoryWindow::MakePlans()
{
	TWeakPtr<SFWFactoryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("PlansAbout", "Plan to zapisany kawałek fabryki: taśmy, podajniki, maszyny i skrzynie w tym samym układzie. Stań w środku tego, co chcesz zapisać, i zapisz to pod nazwą; potem postaw plan gdzie indziej, gdy masz w plecaku jego obiekty. Plan o tej samej nazwie zostaje zastąpiony."),
		FWStyle::Muted), 0.f);

	Add(FWUi::Heading(LOCTEXT("SavePlan", "Zapisz plan")), 16.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[Form->Field(TEXT("name"), LOCTEXT("PlanName", "Nazwa planu"), 300.f)]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(LOCTEXT("Radius", "pola wokół ciebie:"))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[Form->Field(TEXT("radius"), LOCTEXT("RadiusHint", "pól"), 70.f, TEXT("6"))]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("Save", "Zapisz"), [WeakThis]()
			{
				if (TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin())
				{
					This->SavePlan(This->Form->Get(TEXT("name")), FMath::Clamp(int32(This->Form->Number(TEXT("radius"))), 1, 30));
				}
			}, true)
		], 4.f);

	Add(FWUi::Heading(LOCTEXT("YourPlans", "Twoje plany")), 18.f);
	if (!Plans.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted), 4.f);
		return Page;
	}
	const TArray<TSharedPtr<FJsonObject>> List = FWUi::Objects(Plans, TEXT("plans"));
	if (List.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoPlans", "Nie masz jeszcze zapisanych planów."), FWStyle::Muted), 4.f);
	}
	for (int32 Index = 0; Index < List.Num(); ++Index)
	{
		const TSharedPtr<FJsonObject>& Plan = List[Index];
		const TArray<TSharedPtr<FJsonObject>> Objects = FWUi::Objects(Plan, TEXT("objects"));
		// What it takes: the kinds of objects and how many of each
		TMap<FString, int32> Needed;
		for (const TSharedPtr<FJsonObject>& Object : Objects)
		{
			Needed.FindOrAdd(FWUi::Str(Object, TEXT("kind")))++;
		}
		TArray<FString> Parts;
		for (const TPair<FString, int32>& Need : Needed)
		{
			Parts.Add(FString::Printf(TEXT("%s × %d"), *NameOf(Need.Key), Need.Value));
		}
		const double Id = FWUi::Num(Plan, TEXT("id"));
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
						.Text(FText::FromString(FString::Printf(TEXT("%s (%d obiektów)"), *FWUi::Str(Plan, TEXT("name")), Objects.Num())))
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true).Text(FText::FromString(FString::Join(Parts, TEXT(", "))))
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)
				[
					FWUi::SmallButton(LOCTEXT("Use", "Postaw przed sobą"), [WeakThis, Index]()
					{
						if (TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin())
						{
							This->UsePlan(Index);
						}
					}, true)
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
				[
					FWUi::SmallButton(LOCTEXT("Delete", "Usuń"), [WeakThis, Id]()
					{
						TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
						if (AFWPlayerController* PC = This ? This->Controller.Get() : nullptr)
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetNumberField(TEXT("id"), Id);
							PC->Send(TEXT("plans.delete"), P, [WeakThis](const FFWNetReply& Reply)
							{
								if (TSharedPtr<SFWFactoryWindow> Inner = WeakThis.Pin())
								{
									Inner->ApplyPlans(Reply);
								}
							});
						}
					})
				]
			], 6.f);
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Najwyżej %d planów po %d obiektów."), int32(FWUi::Num(Plans, TEXT("maxPlans"), 30.0)),
		int32(FWUi::Num(Plans, TEXT("maxObjects"), 100.0)))), FWStyle::Muted), 10.f);
	return Page;
}

void SFWFactoryWindow::ShowFrame(int32 InFrame)
{
	const TArray<TSharedPtr<FJsonObject>> Frames = FWUi::Objects(Timelapse, TEXT("frames"));
	if (Frames.Num() == 0 || !Map.IsValid())
	{
		return;
	}
	Frame = FMath::Clamp(InFrame, 0, Frames.Num() - 1);
	const TArray<TSharedPtr<FJsonValue>>* Objects = nullptr;
	static const TArray<TSharedPtr<FJsonValue>> None;
	// The square around what was built, with a margin, so a small factory is not a speck on the whole plot; a lone
	// workbench far off (the few farthest objects each way) does not stretch it
	const int32 Size = int32(FWUi::Num(Timelapse, TEXT("size"), 250.0));
	TArray<int32> Xs, Ys;
	for (const TSharedPtr<FJsonObject>& Day : Frames)
	{
		const TArray<TSharedPtr<FJsonValue>>* DayObjects = nullptr;
		if (Day->TryGetArrayField(TEXT("objects"), DayObjects))
		{
			for (const TSharedPtr<FJsonValue>& Value : *DayObjects)
			{
				const TArray<TSharedPtr<FJsonValue>>* Parts = nullptr;
				if (Value->TryGetArray(Parts) && Parts->Num() >= 3)
				{
					Xs.Add(int32((*Parts)[1]->AsNumber()));
					Ys.Add(int32((*Parts)[2]->AsNumber()));
				}
			}
		}
	}
	int32 View = Size;
	FIntPoint Corner(0, 0);
	if (Xs.Num() > 0)
	{
		Xs.Sort();
		Ys.Sort();
		const int32 Cut = Xs.Num() / 20;
		const FIntPoint Low(Xs[Cut], Ys[Cut]), High(Xs[Xs.Num() - 1 - Cut], Ys[Ys.Num() - 1 - Cut]);
		View = FMath::Clamp(FMath::Max(High.X - Low.X, High.Y - Low.Y) + 8, 24, Size);
		const FIntPoint Middle((Low.X + High.X) / 2, (Low.Y + High.Y) / 2);
		Corner = FIntPoint(FMath::Clamp(Middle.X - View / 2, 0, Size - View), FMath::Clamp(Middle.Y - View / 2, 0, Size - View));
	}
	Map->Show(Corner, View, Frames[Frame]->TryGetArrayField(TEXT("objects"), Objects) ? *Objects : None);
}

TSharedRef<SWidget> SFWFactoryWindow::MakeTimelapse()
{
	TWeakPtr<SFWFactoryWindow> WeakThis = SharedThis(this);
	if (!Timelapse.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	const TArray<TSharedPtr<FJsonObject>> Frames = FWUi::Objects(Timelapse, TEXT("frames"));
	ShowFrame(Frame);
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
				.Text_Lambda([WeakThis]()
				{
					TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
					const TArray<TSharedPtr<FJsonObject>> All = This ? FWUi::Objects(This->Timelapse, TEXT("frames")) : TArray<TSharedPtr<FJsonObject>>();
					if (!All.IsValidIndex(This ? This->Frame : -1))
					{
						return LOCTEXT("NoFrames", "Jeszcze nic nie zapisano");
					}
					const TSharedPtr<FJsonObject>& Day = All[This->Frame];
					TArray<FString> Parts;
					FWUi::Str(Day, TEXT("day")).ParseIntoArray(Parts, TEXT("-"));
					const TArray<TSharedPtr<FJsonValue>>* Objects = nullptr;
					const int32 Count = Day->TryGetArrayField(TEXT("objects"), Objects) ? Objects->Num() : 0;
					return FText::FromString(Parts.Num() == 3 ? FString::Printf(TEXT("%s.%s.%s  ·  %d obiektów  ·  dzień %d z %d"), *Parts[2], *Parts[1], *Parts[0], Count, This->Frame + 1, All.Num())
						: FWUi::Str(Day, TEXT("day")));
				})
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SBox).WidthOverride(130.f)
				[
					FWUi::SmallButton(bPlaying ? LOCTEXT("Pause", "❚❚ Pauza") : LOCTEXT("Play", "▶ Odtwórz"), [WeakThis]()
					{
						TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
						const int32 Count = This ? FWUi::Objects(This->Timelapse, TEXT("frames")).Num() : 0;
						if (Count < 2)
						{
							return;
						}
						if (!This->bPlaying && This->Frame >= Count - 1)
						{
							This->Frame = 0;
						}
						This->bPlaying = !This->bPlaying;
						This->NextFrameAt = FPlatformTime::Seconds() + FWTimelapse::PlayEvery;
						This->Rebuild();
					}, true, Frames.Num() > 1)
				]
			]
		]
		+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 12, 0, 0)[Map.ToSharedRef()]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 10, 0, 0)
		[
			SNew(SSlider).MinValue(0.f).MaxValue(float(FMath::Max(Frames.Num() - 1, 1))).StepSize(1.f).MouseUsesStep(true)
			.Value_Lambda([WeakThis]() { TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin(); return This ? float(This->Frame) : 0.f; })
			.OnValueChanged_Lambda([WeakThis](float Value)
			{
				if (TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin())
				{
					This->bPlaying = false;
					This->ShowFrame(FMath::RoundToInt(Value));
				}
			})
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 10, 0, 0)
		[
			FWUi::Note(LOCTEXT("TimelapseNote", "Każdego dnia, gdy ktoś wchodzi na działkę, zapisuje się jej widok z góry: żółte taśmy i podajniki, brązowe skrzynie, niebieski prąd, jasne maszyny. Wejdź jutro, a film wydłuży się o kolejny dzień."),
				FWStyle::Muted)
		];
}

TSharedRef<SWidget> SFWFactoryWindow::MakeGallery()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWFactoryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("GalleryAbout", "Działki publiczne, które gracze polubili najczęściej. Odwiedź je, podejrzyj układ taśm i maszyn, a jeśli ci się spodobają, polub je."),
		FWStyle::Muted), 0.f);

	// The plot stood on
	if (PC && PC->GetPlot())
	{
		const FIntPoint Here(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s %d × %d  ·  polubienia: %d"), bOwnPlot ? TEXT("Twoja działka") : TEXT("Odwiedzasz działkę"), Here.X, Here.Y, Likes)))
			];
		if (!bOwnPlot)
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)
			[
				FWUi::SmallButton(bLiked ? LOCTEXT("Unlike", "♥ Lubisz ją (cofnij)") : LOCTEXT("Like", "♡ Podoba mi się"), [WeakThis, Here]()
				{
					TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
					AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
					if (!Inner)
					{
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetNumberField(TEXT("x"), Here.X);
					P->SetNumberField(TEXT("y"), Here.Y);
					P->SetBoolField(TEXT("like"), !This->bLiked);
					Inner->Send(TEXT("plot.like"), P, [WeakThis](const FFWNetReply& Reply)
					{
						TSharedPtr<SFWFactoryWindow> Again = WeakThis.Pin();
						if (!Again)
						{
							return;
						}
						if (Reply.Type != TEXT("plot.likes"))
						{
							if (AFWPlayerController* Owner = Again->Controller.Get())
							{
								Owner->ToastError(Reply);
							}
							return;
						}
						Again->Likes = int32(FWUi::Num(Reply.Payload, TEXT("likes")));
						Again->bLiked = FWUi::Bool(Reply.Payload, TEXT("liked"));
						Again->Rebuild();
					});
				}, !bLiked)
			];
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)
			[
				FWUi::SmallButton(LOCTEXT("Home", "Wróć do siebie"), [WeakThis]()
				{
					TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
					if (AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr)
					{
						Inner->VisitPlot(-1, -1);
					}
				})
			];
		}
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(12.f)[Row], 12.f);
	}

	Add(FWUi::Heading(LOCTEXT("MostLiked", "Najczęściej lubiane fabryki")), 16.f);
	if (!Gallery.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted), 4.f);
		return Page;
	}
	const TArray<TSharedPtr<FJsonObject>> Plots = FWUi::Objects(Gallery, TEXT("plots"));
	if (Plots.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoGallery", "Nikt jeszcze nie polubił żadnej fabryki. Udostępnij swoją działkę na mapie świata, a inni będą mogli ją odwiedzić."), FWStyle::Text), 4.f);
	}
	for (const TSharedPtr<FJsonObject>& Entry : Plots)
	{
		const FIntPoint At(int32(FWUi::Num(Entry, TEXT("x"))), int32(FWUi::Num(Entry, TEXT("y"))));
		const FString Name = FWUi::Str(Entry, TEXT("name"));
		const bool bHere = PC && PC->GetPlot() && At == FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 14, 0)
				[
					SNew(STextBlock).Font(FWStyle::Font(20, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).Text(FText::FromString(FString::Printf(TEXT("%d."), int32(FWUi::Num(Entry, TEXT("place"))))))
				]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
						.Text(FText::FromString(Name.IsEmpty() ? FString::Printf(TEXT("Działka %d × %d"), At.X, At.Y) : Name))
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
						.Text(FText::FromString(FString::Printf(TEXT("%s  ·  %s  ·  polubienia: %d%s"), *FWUi::Str(Entry, TEXT("owner")),
							*FWFactoryText::Biome(int32(FWUi::Num(Entry, TEXT("biome"), -1.0))), int32(FWUi::Num(Entry, TEXT("likes"))),
							FWUi::Bool(Entry, TEXT("liked")) ? TEXT("  ·  lubisz ją") : TEXT(""))))
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					FWUi::SmallButton(LOCTEXT("Visit", "Odwiedź"), [WeakThis, At]()
					{
						TSharedPtr<SFWFactoryWindow> This = WeakThis.Pin();
						if (AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr)
						{
							Inner->VisitPlot(At.X, At.Y);
						}
					}, true, !bHere)
				]
			], 6.f);
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Lista odświeża się co %d min."), FMath::Max(1, int32(FMath::RoundToDouble(FWUi::Num(Gallery, TEXT("refreshSeconds"), 120.0) / 60.0))))),
		FWStyle::Muted), 10.f);
	return Page;
}

void SFWFactoryWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (!bPlaying || Tab != 1 || FPlatformTime::Seconds() < NextFrameAt)
	{
		return;
	}
	NextFrameAt = FPlatformTime::Seconds() + FWTimelapse::PlayEvery;
	const int32 Count = FWUi::Objects(Timelapse, TEXT("frames")).Num();
	if (Frame >= Count - 1)
	{
		bPlaying = false;
		Rebuild();
		return;
	}
	ShowFrame(Frame + 1);
}

FReply SFWFactoryWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::G)
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->CloseWindow();
		}
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

void SFWAwayWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	const TSharedPtr<FJsonObject> Report = InArgs._Report;
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Minutes = int32(FWUi::Num(Report, TEXT("minutes")));
	const FString Away = Minutes >= 48 * 60 ? FString::Printf(TEXT("%d dni"), Minutes / (24 * 60))
		: Minutes >= 60 ? FString::Printf(TEXT("%d h %02d min"), Minutes / 60, Minutes % 60) : FString::Printf(TEXT("%d min"), Minutes);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Nie było cię %s. Oto, co się w tym czasie działo."), *Away)), FWStyle::Text), 0.f);
	const double Net = FWUi::Num(Report, TEXT("netCents"));
	Add(SNew(STextBlock).Font(FWStyle::Font(22, TEXT("Bold"))).ColorAndOpacity(Net >= 0.0 ? FWStyle::Good : FWStyle::Error)
		.Text(FText::FromString(FString::Printf(TEXT("Razem: %s%s"), Net > 0.0 ? TEXT("+") : TEXT(""), *FWUi::Money(Net).ToString()))), 10.f);
	auto Section = [&Add](const FText& Title, const TArray<TSharedPtr<FJsonObject>>& Rows, const FLinearColor& Color)
	{
		if (Rows.Num() == 0)
		{
			return;
		}
		Add(FWUi::Heading(Title), 14.f);
		for (const TSharedPtr<FJsonObject>& Row : Rows)
		{
			const double Cents = FWUi::Num(Row, TEXT("cents"));
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)[SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FWUi::Str(Row, TEXT("name"))))]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(STextBlock).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(Color)
					.Text(FText::FromString(FString::Printf(TEXT("%s%s"), Cents > 0.0 ? TEXT("+") : TEXT(""), *FWUi::Money(Cents).ToString())))
				], 3.f);
		}
	};
	const TArray<TSharedPtr<FJsonObject>> Income = FWUi::Objects(Report, TEXT("income"));
	const TArray<TSharedPtr<FJsonObject>> Expenses = FWUi::Objects(Report, TEXT("expenses"));
	const TArray<TSharedPtr<FJsonObject>> Made = FWUi::Objects(Report, TEXT("made"));
	Section(LOCTEXT("Income", "Wpływy"), Income, FWStyle::Good);
	Section(LOCTEXT("Expenses", "Wydatki"), Expenses, FWStyle::Error);
	if (Made.Num() > 0)
	{
		Add(FWUi::Heading(LOCTEXT("Made", "Twoje maszyny wyprodukowały")), 14.f);
		for (const TSharedPtr<FJsonObject>& Item : Made)
		{
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), FWUi::Str(Item, TEXT("id")), 28.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
					.Text(FText::FromString(FString::Printf(TEXT("%d × %s"), int32(FWUi::Num(Item, TEXT("quantity"))), *FWUi::Str(Item, TEXT("name")))))
				], 3.f);
		}
	}
	if (Income.Num() == 0 && Expenses.Num() == 0 && Made.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("Quiet", "Tym razem nic się nie zmieniło. Pociągi eksportowe, banki i sklepy pracują, także gdy cię nie ma."), FWStyle::Muted), 12.f);
	}
	TWeakPtr<SFWAwayWindow> WeakThis = SharedThis(this);
	Add(SNew(SBox).HAlign(HAlign_Left)
		[
			FWUi::SmallButton(LOCTEXT("ToWork", "Do roboty!"), [WeakThis]()
			{
				TSharedPtr<SFWAwayWindow> This = WeakThis.Pin();
				if (AFWPlayerController* PC = This ? This->Controller.Get() : nullptr)
				{
					PC->CloseWindow();
				}
			}, true)
		], 18.f);
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("AwayTitle", "Gdy cię nie było"), SNullWidget::NullWidget, nullptr, SNew(SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page], 640.f, 560.f)
	];
}

FReply SFWAwayWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::Enter)
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
