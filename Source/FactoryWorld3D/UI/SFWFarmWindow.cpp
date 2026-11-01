#include "UI/SFWFarmWindow.h"

#include "FWPlayerController.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWFarm"

namespace FWFarmText
{
	// 4.6153 → "4,6"; 5 → "5"
	FString Number(double Value)
	{
		FString Text = FString::Printf(TEXT("%.1f"), FMath::RoundToDouble(Value * 10.0) / 10.0);
		Text.RemoveFromEnd(TEXT(".0"));
		return Text.Replace(TEXT("."), TEXT(","));
	}

	FString Minutes(double Seconds)
	{
		return Number(Seconds / 60.0) + TEXT(" min");
	}

	// "Pszenica: 12, Marchew: 4"
	FString Stacks(const TArray<TSharedPtr<FJsonObject>>& Stacks, const TCHAR* Between)
	{
		TArray<FString> Parts;
		for (const TSharedPtr<FJsonObject>& Stack : Stacks)
		{
			Parts.Add(FString::Printf(TEXT("%s%s%d"), *FWUi::Str(Stack, TEXT("name")), Between, int32(FWUi::Num(Stack, TEXT("quantity")))));
		}
		return FString::Join(Parts, TEXT(", "));
	}

	// What the animals eat, as the plot lists it in the 2D game
	const TArray<TPair<FString, FString>>& Feeds()
	{
		static const TArray<TPair<FString, FString>> List = {
			{ TEXT("wheat"), TEXT("Pszenica") }, { TEXT("corn"), TEXT("Kukurydza") }, { TEXT("carrot"), TEXT("Marchew") },
			{ TEXT("cabbage"), TEXT("Kapusta") }, { TEXT("fish_meal"), TEXT("Mączka rybna") }, { TEXT("dates"), TEXT("Daktyle") },
			{ TEXT("concentrate_feed"), TEXT("Pasza treściwa") }, { TEXT("beet_pulp"), TEXT("Wysłodki") }, { TEXT("potato"), TEXT("Ziemniaki") },
			{ TEXT("stillage"), TEXT("Wywar") } };
		return List;
	}

	FString Title(const FString& Kind)
	{
		static const TMap<FString, FString> Names = {
			{ TEXT("furniture_farmstead"), TEXT("Zagroda rolnika") }, { TEXT("furniture_manor_farm"), TEXT("Folwark") },
			{ TEXT("furniture_mechanized_farm"), TEXT("Gospodarstwo zmechanizowane") }, { TEXT("furniture_forester_hut"), TEXT("Chata leśnika") },
			{ TEXT("furniture_forester_lodge"), TEXT("Leśniczówka") }, { TEXT("furniture_forestry_station"), TEXT("Stacja leśna") },
			{ TEXT("furniture_beehive"), TEXT("Ul") }, { TEXT("furniture_feeder"), TEXT("Karmnik") }, { TEXT("furniture_auto_barn"), TEXT("Obora automatyczna") } };
		return Names.Contains(Kind) ? Names[Kind] : Kind;
	}
}

SFWFarmWindow::EMode SFWFarmWindow::ModeOf(const FString& Kind)
{
	if (Kind == TEXT("furniture_farmstead") || Kind == TEXT("furniture_manor_farm") || Kind == TEXT("furniture_mechanized_farm"))
	{
		return EMode::Farmstead;
	}
	if (Kind == TEXT("furniture_forester_hut") || Kind == TEXT("furniture_forester_lodge") || Kind == TEXT("furniture_forestry_station"))
	{
		return EMode::Forester;
	}
	if (Kind == TEXT("furniture_beehive"))
	{
		return EMode::Hive;
	}
	return Kind == TEXT("furniture_feeder") || Kind == TEXT("furniture_auto_barn") ? EMode::Feeder : EMode::None;
}

void SFWFarmWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Object = InArgs._Object;
	Kind = InArgs._Kind;
	Mode = ModeOf(Kind);
	AFWPlayerController* PC = Controller.Get();
	if (PC && PC->GetPlot())
	{
		Plot = FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
	}
	const FString Known = PC ? PC->ItemName(Kind) : FString();
	const FText Title = FText::FromString(Known.IsEmpty() || Known == Kind ? FWFarmText::Title(Kind) : Known);
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(Title, Right, nullptr, SAssignNew(Content, SBox), 820.f, Mode == EMode::Hive ? 480.f : 700.f)
	];
	if (PC)
	{
		PC->RefreshInventory();
	}
	Load();
}

int32 SFWFarmWindow::Owned(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

TSharedPtr<FJsonObject> SFWFarmWindow::Here() const
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	if (Mode != EMode::Hive)
	{
		P->SetNumberField(TEXT("x"), Plot.X);
		P->SetNumberField(TEXT("y"), Plot.Y);
	}
	P->SetStringField(TEXT("id"), Object);
	return P;
}

void SFWFarmWindow::Load(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || Mode == EMode::None)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + (Mode == EMode::Hive || Mode == EMode::Feeder ? 5.0 : 3.0);
	static const TCHAR* Types[] = { TEXT(""), TEXT("farmstead.get"), TEXT("forestry.get"), TEXT("hive.get"), TEXT("feeder.get") };
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	PC->Send(Types[uint8(Mode)], Here(), [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWFarmWindow> This = WeakThis.Pin();
		// Asked again by itself: no error toasts every few seconds
		if (This && !(bPoll && Reply.IsError()))
		{
			This->Apply(Reply);
		}
	});
}

bool SFWFarmWindow::Apply(const FFWNetReply& Reply)
{
	static const TCHAR* Replies[] = { TEXT(""), TEXT("farmsteads"), TEXT("forestry"), TEXT("hive"), TEXT("feeder") };
	AFWPlayerController* PC = Controller.Get();
	TSharedPtr<FJsonObject> Mine;
	if (Reply.Type == Replies[uint8(Mode)] && Reply.Payload.IsValid())
	{
		if (Mode == EMode::Hive || Mode == EMode::Feeder)
		{
			Mine = FWUi::Obj(Reply.Payload, Mode == EMode::Hive ? TEXT("hive") : TEXT("feeder"));
		}
		else
		{
			// The plot's farmsteads or foresters: this one by its id
			for (const TSharedPtr<FJsonObject>& Building : FWUi::Objects(Reply.Payload, Mode == EMode::Farmstead ? TEXT("farmsteads") : TEXT("foresters")))
			{
				if (FWUi::Str(Building, TEXT("id")) == Object)
				{
					Mine = Building;
				}
			}
		}
	}
	if (!Mine.IsValid())
	{
		if (PC && Reply.IsError())
		{
			PC->ToastError(Reply);
		}
		if (!State.IsValid() && Content.IsValid())
		{
			Content->SetContent(FWUi::Note(Reply.IsError() ? FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage())
				: LOCTEXT("Gone", "Tego budynku nie ma już na działce."), FWStyle::Error));
		}
		return false;
	}
	State = Mine;
	// What came out into the backpack
	TArray<TSharedPtr<FJsonObject>> Taken = FWUi::Objects(Reply.Payload, TEXT("taken"));
	if (PC && Taken.Num() > 0)
	{
		PC->Toast(FText::Format(LOCTEXT("Taken", "Do ekwipunku: {0}"), FText::FromString(FWFarmText::Stacks(Taken, TEXT(" × ")))));
	}
	Rebuild();
	return true;
}

void SFWFarmWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWFarmWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (Inner && This->Apply(Reply))
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
		}
	});
}

void SFWFarmWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = !State.IsValid() ? FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted)
		: Mode == EMode::Farmstead ? MakeFarmstead() : Mode == EMode::Forester ? MakeForester() : Mode == EMode::Hive ? MakeHive() : MakeFeeder();
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	static const TCHAR* PickUpTips[] = { TEXT(""),
		TEXT("Plony i obornik ze środka wrócą do ekwipunku, a pola, które obsiała, znikną z działki"),
		TEXT("Drewno ze środka wróci do ekwipunku, a las, który posadził, zniknie z działki"),
		TEXT("Miód i wosk z ula wrócą do ekwipunku"), TEXT("Pasza z karmnika wróci do ekwipunku") };
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()[Page]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 18, 0, 0).HAlign(HAlign_Left)
		[
			SNew(SBox).ToolTipText(FText::FromString(PickUpTips[uint8(Mode)]))
			[
				FWUi::SmallButton(LOCTEXT("PickUp", "Schowaj do ekwipunku"), [WeakThis]()
				{
					TSharedPtr<SFWFarmWindow> This = WeakThis.Pin();
					if (AFWPlayerController* PC = This ? This->Controller.Get() : nullptr)
					{
						const FString Id = This->Object;
						PC->CloseWindow();
						PC->Pickup(Id);
					}
				})
			]
		];
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Box]);
	Scroll->SetScrollOffset(Offset);
}

namespace FWFarmText
{
	// A row of choices (what to sow or plant), the chosen one lit
	TSharedRef<SWidget> Choices(AFWPlayerController* PC, const TArray<TSharedPtr<FJsonObject>>& Kinds, const FString& Chosen, const TCHAR* IconField,
		TFunction<void(const FString&)> OnPick)
	{
		TSharedRef<SWrapBox> Row = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(8.f, 8.f));
		for (const TSharedPtr<FJsonObject>& Kind : Kinds)
		{
			const FString Id = FWUi::Str(Kind, TEXT("id"));
			Row->AddSlot()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)[FWUi::Icon(PC, FWUi::Str(Kind, IconField), 26.f)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					FWUi::SmallButton(FText::FromString(FWUi::Str(Kind, TEXT("name"))), [OnPick, Id]() { OnPick(Id); }, Id == Chosen)
				]
			];
		}
		return Row;
	}

	TSharedRef<SWidget> Store(AFWPlayerController* PC, const FString& Icon, const FString& Text, const FText& Button, bool bEnabled, TFunction<void()> OnTake)
	{
		return SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Icon, 36.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).AutoWrapText(true).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(Text))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)[FWUi::SmallButton(Button, OnTake, true, bEnabled)];
	}
}

TSharedRef<SWidget> SFWFarmWindow::MakeFarmstead()
{
	using namespace FWFarmText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Side = 2 * int32(FWUi::Num(State, TEXT("radius"))) + 1;
	const int32 Fields = int32(FWUi::Num(State, TEXT("fields"))), Ripe = int32(FWUi::Num(State, TEXT("ripe")));
	Add(SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
		.Text(FText::FromString(FString::Printf(TEXT("%s · poziom %d z 2"), *FWUi::Str(State, TEXT("name")), int32(FWUi::Num(State, TEXT("level")))))), 0.f);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Pole %d × %d: %d obsianych grządek, w tym %d dojrzałych. Zbiera jedną na %s s."),
		Side, Side, Fields, Ripe, *Number(FWUi::Num(State, TEXT("reapSeconds"))))), FWStyle::Text));

	// What it sows
	Add(FWUi::Heading(LOCTEXT("Sows", "Co sieje")), 14.f);
	const FString Crop = FWUi::Str(State, TEXT("crop"));
	const TArray<TSharedPtr<FJsonObject>> Kinds = FWUi::Objects(State, TEXT("kinds"));
	Add(Choices(PC, Kinds, Crop, TEXT("id"), [WeakThis](const FString& Picked)
	{
		TSharedPtr<SFWFarmWindow> This = WeakThis.Pin();
		if (This && Picked != FWUi::Str(This->State, TEXT("crop")))
		{
			TSharedPtr<FJsonObject> P = This->Here();
			P->SetStringField(TEXT("crop"), Picked);
			This->Act(TEXT("farmstead.sow"), P);
		}
	}), 4.f);
	for (const TSharedPtr<FJsonObject>& Picked : Kinds)
	{
		if (FWUi::Str(Picked, TEXT("id")) == Crop)
		{
			const int32 Growing = int32(FWUi::Num(Picked, TEXT("growing")));
			FString Text = FString::Printf(TEXT("%s: rośnie tu %s, na oborniku %s, daje %d z grządki."), *FWUi::Str(Picked, TEXT("name")),
				*Minutes(FWUi::Num(Picked, TEXT("growSeconds"))), *Minutes(FWUi::Num(Picked, TEXT("manureSeconds"))), int32(FWUi::Num(Picked, TEXT("yield"))));
			if (Growing < Fields)
			{
				Text += FString::Printf(TEXT(" Inne uprawy zbierze, jak dojrzeją, i w ich miejsce posieje tę (%d z %d)."), Growing, Fields);
			}
			Add(FWUi::Note(FText::FromString(Text), FWStyle::Text), 8.f);
		}
	}

	// Manure: under the seed, one a field, and the crop grows a quarter faster
	const int32 Manure = int32(FWUi::Num(State, TEXT("manure"))), MaxManure = int32(FWUi::Num(State, TEXT("maxManure")));
	const int32 Pour = FMath::Min3(10, MaxManure - Manure, Owned(TEXT("manure")));
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, TEXT("manure"), 30.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("Obornik: %d / %d  (masz %d)"), Manure, MaxManure, Owned(TEXT("manure")))))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SBox).ToolTipText(LOCTEXT("ManureTip", "Obornik z ekwipunku: idzie pod siew, po jednym na pole, i uprawa rośnie o 25% szybciej"))
			[
				FWUi::SmallButton(FText::Format(LOCTEXT("AddManure", "Dosyp {0}"), FMath::Max(Pour, 0)), [WeakThis, Pour]()
				{
					if (TSharedPtr<SFWFarmWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = This->Here();
						P->SetNumberField(TEXT("manure"), Pour);
						This->Act(TEXT("farmstead.sow"), P);
					}
				}, false, Pour > 0)
			]
		], 12.f);

	// The crops it reaped
	const int32 Crops = int32(FWUi::Num(State, TEXT("crops"))), Capacity = int32(FWUi::Num(State, TEXT("capacity")));
	const TArray<TSharedPtr<FJsonObject>> Store = FWUi::Objects(State, TEXT("store"));
	const FString StoreText = Store.Num() == 0 ? FString::Printf(TEXT("Plony: 0 / %d"), Capacity)
		: FString::Printf(TEXT("%s (razem %d / %d)"), *Stacks(Store, TEXT(": ")), Crops, Capacity);
	Add(FWFarmText::Store(PC, Store.Num() > 0 ? FWUi::Str(Store.Last(), TEXT("id")) : Crop, StoreText, LOCTEXT("TakeCrops", "Wyjmij plony"), Crops > 0, [WeakThis]()
	{
		if (TSharedPtr<SFWFarmWindow> This = WeakThis.Pin())
		{
			This->Act(TEXT("farmstead.take"), This->Here());
		}
	}), 16.f);
	Add(FWUi::Bar(Capacity > 0 ? float(Crops) / float(Capacity) : 0.f, FWStyle::Accent), 6.f);
	Add(FWUi::Note(FWUi::Bool(State, TEXT("full")) ? LOCTEXT("FarmFull", "Magazyn pełny: zagroda nie zbiera, dopóki nie wyjmiesz plonów (ręcznie albo podajnikiem).")
		: Ripe > 0 ? LOCTEXT("Reaping", "Pracuje: zbiera dojrzałe plony i od razu sieje od nowa.") : LOCTEXT("Growing", "Uprawy rosną. Pierwszy zbiór, gdy dojrzeją."),
		FWUi::Bool(State, TEXT("full")) ? FWStyle::Error : Ripe > 0 ? FWStyle::Accent : FWStyle::Muted), 8.f);

	const TSharedPtr<FJsonObject> Next = FWUi::Obj(State, TEXT("upgrade"));
	if (Next.IsValid())
	{
		const int32 NextSide = 2 * int32(FWUi::Num(Next, TEXT("radius"))) + 1;
		Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Lepszy: %s (pole %d × %d, %d plonów, uprawy rosną %s raza szybciej, zbiór co %s s). Zrobisz go przy stole warsztatowym z tego budynku."),
			*FWUi::Str(Next, TEXT("name")), NextSide, NextSide, int32(FWUi::Num(Next, TEXT("capacity"))), *Number(FWUi::Num(Next, TEXT("growSpeed"))),
			*Number(FWUi::Num(Next, TEXT("reapSeconds"))))), FWStyle::Muted), 14.f);
	}
	else
	{
		Add(FWUi::Note(LOCTEXT("BestFarm", "To najlepszy budynek rolnika."), FWStyle::Muted), 14.f);
	}
	Add(FWUi::Note(LOCTEXT("FarmHelp", "Zagroda zostawia wolne podwórze wokół budynku i drogę od drzwi do skraju pola. Pól zaoranych przez ciebie nie rusza. Postaw podajnik obok budynku, a plony same trafią na taśmę albo do skrzyni; podajnik w drugą stronę dosypie obornik."),
		FWStyle::Muted), 6.f);
	return Page;
}

TSharedRef<SWidget> SFWFarmWindow::MakeForester()
{
	using namespace FWFarmText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	static const TMap<FString, FString> Plants = { { TEXT("pine"), TEXT("sosnę") }, { TEXT("spruce"), TEXT("świerk") }, { TEXT("birch"), TEXT("brzozę") },
		{ TEXT("oak"), TEXT("dąb") }, { TEXT("linden"), TEXT("lipę") } };
	const int32 Side = 2 * int32(FWUi::Num(State, TEXT("radius"))) + 1;
	const int32 Trees = int32(FWUi::Num(State, TEXT("trees")));
	double Grow = 0.0;
	const bool bGrows = State->TryGetNumberField(TEXT("growSeconds"), Grow);
	Add(SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
		.Text(FText::FromString(FString::Printf(TEXT("%s · poziom %d z 3"), *FWUi::Str(State, TEXT("name")), int32(FWUi::Num(State, TEXT("level")))))), 0.f);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Las %d × %d pól: %d drzew, w tym %d do ścięcia. Drzewo rośnie tu %s, ścina jedno na %s s."),
		Side, Side, Trees, int32(FWUi::Num(State, TEXT("grown"))), bGrows ? *Minutes(Grow) : TEXT("—"), *Number(FWUi::Num(State, TEXT("cutSeconds"))))), FWStyle::Text));

	// What it plants
	Add(FWUi::Heading(LOCTEXT("Plants", "Co sadzi")), 14.f);
	const FString Species = FWUi::Str(State, TEXT("species"));
	const TArray<TSharedPtr<FJsonObject>> Kinds = FWUi::Objects(State, TEXT("kinds"));
	Add(Choices(PC, Kinds, Species, TEXT("sapling"), [WeakThis](const FString& Picked)
	{
		TSharedPtr<SFWFarmWindow> This = WeakThis.Pin();
		if (This && Picked != FWUi::Str(This->State, TEXT("species")))
		{
			TSharedPtr<FJsonObject> P = This->Here();
			P->SetStringField(TEXT("species"), Picked);
			This->Act(TEXT("forester.plant"), P);
		}
	}), 4.f);
	for (const TSharedPtr<FJsonObject>& Picked : Kinds)
	{
		if (FWUi::Str(Picked, TEXT("id")) != Species)
		{
			continue;
		}
		double KindGrow = 0.0;
		const bool bKindGrows = Picked->TryGetNumberField(TEXT("growSeconds"), KindGrow);
		const int32 Standing = int32(FWUi::Num(Picked, TEXT("standing")));
		FString Text = FString::Printf(TEXT("%s: rośnie tu %s, daje %d × %s. %s."), *FWUi::Str(Picked, TEXT("name")), bKindGrows ? *Minutes(KindGrow) : TEXT("—"),
			int32(FWUi::Num(Picked, TEXT("woodQuantity"))), *FWUi::Str(Picked, TEXT("woodName")).ToLower(), *FWUi::Str(Picked, TEXT("note")));
		if (Standing < Trees)
		{
			const FString Id = FWUi::Str(Picked, TEXT("id"));
			Text += FString::Printf(TEXT(" Rosnące już drzewa innych gatunków zetnie, jak urosną, i w ich miejsce posadzi %s (%d z %d)."),
				Plants.Contains(Id) ? *Plants[Id] : *FWUi::Str(Picked, TEXT("name")).ToLower(), Standing, Trees);
		}
		Add(FWUi::Note(FText::FromString(Text), FWStyle::Text), 8.f);
	}

	// The wood it cut
	const int32 Wood = int32(FWUi::Num(State, TEXT("wood"))), Capacity = int32(FWUi::Num(State, TEXT("capacity")));
	const TArray<TSharedPtr<FJsonObject>> Store = FWUi::Objects(State, TEXT("store"));
	const FString StoreText = Store.Num() > 1 ? FString::Printf(TEXT("%s (razem %d / %d)"), *Stacks(Store, TEXT(": ")), Wood, Capacity)
		: Store.Num() == 1 ? FString::Printf(TEXT("%s: %d / %d"), *FWUi::Str(Store[0], TEXT("name")), Wood, Capacity) : FString::Printf(TEXT("Drewno: %d / %d"), Wood, Capacity);
	Add(FWFarmText::Store(PC, Store.Num() > 0 ? FWUi::Str(Store.Last(), TEXT("id")) : FString(TEXT("wood")), StoreText, LOCTEXT("TakeWood", "Wyjmij drewno"), Wood > 0, [WeakThis]()
	{
		if (TSharedPtr<SFWFarmWindow> This = WeakThis.Pin())
		{
			This->Act(TEXT("forester.take"), This->Here());
		}
	}), 16.f);
	Add(FWUi::Bar(Capacity > 0 ? float(Wood) / float(Capacity) : 0.f, FWStyle::Accent), 6.f);
	const bool bFull = FWUi::Bool(State, TEXT("full"));
	const bool bCutting = FWUi::Num(State, TEXT("grown")) > 0.0;
	Add(FWUi::Note(bFull ? LOCTEXT("ForestFull", "Magazyn pełny: leśnik nie tnie, dopóki nie wyjmiesz drewna (ręcznie albo podajnikiem).")
		: bCutting ? LOCTEXT("Cutting", "Pracuje: ścina drzewa i od razu sadzi nowe w ich miejscu.") : LOCTEXT("Saplings", "Sadzonki rosną. Pierwsze drzewa będą do ścięcia, gdy urosną."),
		bFull ? FWStyle::Error : bCutting ? FWStyle::Accent : FWStyle::Muted), 8.f);

	const TSharedPtr<FJsonObject> Next = FWUi::Obj(State, TEXT("upgrade"));
	if (Next.IsValid())
	{
		const int32 NextSide = 2 * int32(FWUi::Num(Next, TEXT("radius"))) + 1;
		double NextGrow = 0.0;
		const bool bNextGrows = Next->TryGetNumberField(TEXT("growSeconds"), NextGrow);
		Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Lepszy: %s (las %d × %d, %d drewna, drzewo rośnie %s, cięcie co %s s). Zrobisz go przy stole warsztatowym z tego budynku."),
			*FWUi::Str(Next, TEXT("name")), NextSide, NextSide, int32(FWUi::Num(Next, TEXT("capacity"))), bNextGrows ? *Minutes(NextGrow) : TEXT("—"),
			*Number(FWUi::Num(Next, TEXT("cutSeconds"))))), FWStyle::Muted), 14.f);
	}
	else
	{
		Add(FWUi::Note(LOCTEXT("BestForest", "To najlepszy budynek leśny."), FWStyle::Muted), 14.f);
	}
	Add(FWUi::Note(LOCTEXT("ForestHelp", "Leśnik zostawia wolną polanę wokół budynku i drogę od drzwi do skraju lasu. Postaw tam podajnik obok budynku, a drewno samo trafi na taśmę albo do skrzyni."),
		FWStyle::Muted), 6.f);
	return Page;
}

TSharedRef<SWidget> SFWFarmWindow::MakeHive()
{
	using namespace FWFarmText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Lindens = int32(FWUi::Num(State, TEXT("lindens"))), Reach = int32(FWUi::Num(State, TEXT("reach")));
	const FString Speed = Lindens > 0
		? FString::Printf(TEXT("Lip w zasięgu %d pól: %d, pszczoły pracują %d%% normalnego tempa."), Reach, Lindens, int32(FWUi::Num(State, TEXT("speedPercent"))))
		: FString::Printf(TEXT("Brak kwitnących lip w zasięgu %d pól: posadź je wokół ula, a pszczoły będą pracować do dwóch razy szybciej."), Reach);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Słoik miodu co %s min, kawałek wosku co %d słoiki. %s"), *Number(FWUi::Num(State, TEXT("honeySeconds")) / 60.0),
		int32(FWUi::Num(State, TEXT("honeyPerWax"))), *Speed)), FWStyle::Text), 0.f);
	const int32 Honey = int32(FWUi::Num(State, TEXT("honey"))), Wax = int32(FWUi::Num(State, TEXT("wax")));
	for (const TPair<FString, FString>& Row : { TPair<FString, FString>(TEXT("honey"), FString::Printf(TEXT("Miód: %d / %d%s"), Honey, int32(FWUi::Num(State, TEXT("maxHoney"))),
		FWUi::Bool(State, TEXT("full")) ? TEXT(" (ul pełny, pszczoły czekają)") : TEXT(""))), TPair<FString, FString>(TEXT("wax"), FString::Printf(TEXT("Wosk: %d"), Wax)) })
	{
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Row.Key, 36.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(Row.Value))
			], 12.f);
	}
	Add(SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("NextJar", "Następny słoik:")), 14.f);
	Add(FWUi::Bar(float(FMath::Clamp(FWUi::Num(State, TEXT("progress")), 0.0, 1.0)), FWStyle::Accent), 4.f);
	Add(SNew(SBox).HAlign(HAlign_Left)
		[
			FWUi::SmallButton(LOCTEXT("TakeHoney", "Wybierz miód i wosk"), [WeakThis]()
			{
				if (TSharedPtr<SFWFarmWindow> This = WeakThis.Pin())
				{
					This->Act(TEXT("hive.take"), This->Here());
				}
			}, true, Honey > 0 || Wax > 0)
		], 16.f);
	return Page;
}

TSharedRef<SWidget> SFWFarmWindow::MakeFeeder()
{
	using namespace FWFarmText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWFarmWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Animals = int32(FWUi::Num(State, TEXT("animals"))), Hungry = int32(FWUi::Num(State, TEXT("hungry"))), Reach = int32(FWUi::Num(State, TEXT("reach")));
	const bool bBarn = FWUi::Bool(State, TEXT("collects"));
	const FString Around = Animals == 0 ? FString::Printf(TEXT("W zasięgu %d pól nie ma zwierząt: wypuść je obok."), Reach)
		: FString::Printf(TEXT("Zwierząt w zasięgu %d pól: %d, głodnych: %d."), Reach, Animals, Hungry);
	const int32 PerMeal = int32(FWUi::Num(State, TEXT("feedPerMeal")));
	Add(FWUi::Note(FText::FromString(bBarn
		? FString::Printf(TEXT("Sama karmi zwierzęta wokół siebie, %d sztuki paszy na posiłek, i zbiera, co dają: jajka, wełnę, mleko, mięso, obornik i młode. Podajnik obok napełni ją paszą i wyjmie zbiory. %s"), PerMeal, *Around)
		: FString::Printf(TEXT("Sam karmi głodne zwierzęta wokół siebie, %d sztuki paszy na posiłek: od razu po wypuszczeniu, po każdym zbiorze i gdy dostanie paszę. Podajnik obok też go napełni. %s"), PerMeal, *Around)),
		FWStyle::Text), 0.f);

	// The feed inside, and what the barn collected
	const int32 Feed = int32(FWUi::Num(State, TEXT("feed"))), Capacity = int32(FWUi::Num(State, TEXT("capacity")));
	const TArray<TSharedPtr<FJsonObject>> Store = FWUi::Objects(State, TEXT("store"));
	const TArray<TSharedPtr<FJsonObject>> Output = FWUi::Objects(State, TEXT("output"));
	int32 Collected = 0;
	for (const TSharedPtr<FJsonObject>& Stack : Output)
	{
		Collected += int32(FWUi::Num(Stack, TEXT("quantity")));
	}
	const FString StoreText = FString::Printf(TEXT("Pasza: %d / %d%s"), Feed, Capacity, Store.Num() > 0 ? *(TEXT(": ") + Stacks(Store, TEXT(" × ")))
		: Hungry > 0 ? TEXT(" (pusty, głodne zwierzęta czekają)") : TEXT(""));
	Add(FWFarmText::Store(PC, Store.Num() > 0 ? FWUi::Str(Store[0], TEXT("id")) : FString(TEXT("wheat")), StoreText,
		bBarn ? LOCTEXT("TakeFeedBarn", "Wyjmij paszę i zbiory") : LOCTEXT("TakeFeed", "Wyjmij paszę"), Feed > 0 || Collected > 0, [WeakThis]()
	{
		if (TSharedPtr<SFWFarmWindow> This = WeakThis.Pin())
		{
			This->Act(TEXT("feeder.take"), This->Here());
		}
	}), 14.f);
	Add(FWUi::Bar(Capacity > 0 ? float(Feed) / float(Capacity) : 0.f, FWStyle::Accent), 6.f);
	if (bBarn)
	{
		Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Zebrane: %d / %d%s"), Collected, int32(FWUi::Num(State, TEXT("maxOutput"))),
			Output.Num() > 0 ? *(TEXT(": ") + Stacks(Output, TEXT(" × "))) : TEXT(""))), FWStyle::Text), 8.f);
	}

	// Pouring feed from the backpack
	Add(FWUi::Heading(LOCTEXT("Pour", "Wsyp z ekwipunku")), 16.f);
	TSharedRef<SWrapBox> Pour = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(10.f, 8.f));
	bool bAny = false;
	for (const TPair<FString, FString>& FeedKind : Feeds())
	{
		const int32 Have = Owned(FeedKind.Key);
		if (Have <= 0)
		{
			continue;
		}
		bAny = true;
		const int32 Some = FMath::Min3(10, Have, Capacity - Feed);
		const FString Item = FeedKind.Key;
		Pour->AddSlot()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)[FWUi::Icon(PC, Item, 26.f)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SBox).ToolTipText(FText::FromString(FString::Printf(TEXT("Masz %d"), Have)))
				[
					FWUi::SmallButton(FText::FromString(FString::Printf(TEXT("%s +%d"), *FeedKind.Value, FMath::Max(Some, 0))), [WeakThis, Item, Some]()
					{
						if (TSharedPtr<SFWFarmWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = This->Here();
							P->SetStringField(TEXT("item"), Item);
							P->SetNumberField(TEXT("quantity"), Some);
							This->Act(TEXT("feeder.fill"), P);
						}
					}, false, Some > 0)
				]
			]
		];
	}
	if (bAny)
	{
		Add(Pour, 4.f);
	}
	else
	{
		Add(FWUi::Note(LOCTEXT("NoFeed", "Nie masz paszy: pszenicy, kukurydzy, marchwi, kapusty, ziemniaków, mączki rybnej, daktyli, wysłodków, wywaru ani paszy treściwej."),
			FWStyle::Muted), 4.f);
	}
	return Page;
}

void SFWFarmWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() >= NextPoll)
	{
		Load(true);
	}
}

FReply SFWFarmWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape)
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
