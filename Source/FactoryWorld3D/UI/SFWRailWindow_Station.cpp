// The station's tabs: stock, sending, routes and trips.
#include "UI/SFWRailWindow.h"
#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWRail"

#include "SFWRailWindowHelpers.h"

TSharedRef<SWidget> SFWRailWindow::MakeStock()
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	auto Stock = [WeakThis](bool bLoad, const FString& Item, int32 Quantity)
	{
		return [WeakThis, bLoad, Item, Quantity]() { if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin()) { This->StockAction(bLoad, Item, Quantity); } };
	};
	Add(FWUi::Heading(LOCTEXT("StationStock", "Magazyn stacji")), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(State, TEXT("items"));
	if (Items.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("StockEmpty", "Pusto. Załaduj towary z ekwipunku poniżej."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Stack : Items)
	{
		const FString Id = FWUi::Str(Stack, TEXT("id"));
		const int32 Quantity = int32(FWUi::Num(Stack, TEXT("quantity")));
		Add(FWRailText::Line(Controller.Get(), Id, FText::Format(LOCTEXT("StockLine", "{0}  × {1}"), FText::FromString(FWUi::Str(Stack, TEXT("name"))), Quantity), FWStyle::Text,
			FWUi::SmallButton(LOCTEXT("ToInventory", "Do ekwipunku"), Stock(false, Id, Quantity))), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("LoadFrom", "Załaduj z ekwipunku")), 16.f);
	int32 Loadable = 0;
	for (const FFWStack& Stack : Controller.IsValid() ? Controller->GetInventory() : TArray<FFWStack>())
	{
		if (Stack.Quantity <= 0 || Stack.Id.StartsWith(TEXT("furniture_")))
		{
			continue;
		}
		++Loadable;
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (Stack.Quantity > 10)
		{
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Ten", "10"), Stock(true, Stack.Id, 10))];
		}
		Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("All", "Wszystko"), Stock(true, Stack.Id, Stack.Quantity), true)];
		Add(FWRailText::Line(Controller.Get(), Stack.Id, FText::Format(LOCTEXT("Have", "{0}  · masz {1}"), FText::FromString(Stack.Name), Stack.Quantity), FWStyle::Text, Right), 3.f);
	}
	if (Loadable == 0)
	{
		Add(FWUi::Note(LOCTEXT("InventoryEmpty", "Ekwipunek jest pusty."), FWStyle::Muted));
	}
	Add(FWUi::Note(LOCTEXT("Inserters", "Podajnik stojący przodem do toru przed stacją ładuje na nią towary, a stojący tyłem do toru je zdejmuje."), FWStyle::Muted), 12.f);
	return Page;
}

TSharedRef<SWidget> SFWRailWindow::MakeDestination()
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	TArray<TPair<FString, FText>> Choices;
	for (const FIntPoint& P : MyPlots)
	{
		if (P != Plot)
		{
			Choices.Add({ FString::Printf(TEXT("%d,%d"), P.X, P.Y), FText::FromString(FString::Printf(TEXT("Moja działka %d × %d"), P.X, P.Y)) });
		}
	}
	for (const TSharedPtr<FJsonObject>& Player : Found)
	{
		for (const TSharedPtr<FJsonObject>& P : FWUi::Objects(Player, TEXT("plots")))
		{
			const int32 X = int32(FWUi::Num(P, TEXT("x"))), Y = int32(FWUi::Num(P, TEXT("y")));
			Choices.Add({ FString::Printf(TEXT("%d,%d"), X, Y), FText::FromString(FString::Printf(TEXT("%s · działka %d × %d"), *FWUi::Str(Player, TEXT("name")), X, Y)) });
		}
	}
	Choices.Add({ TEXT("manual"), LOCTEXT("Other", "Inna działka (podaj X i Y)") });
	auto Label = [](const FText& Text)
	{
		return SNew(SBox).WidthOverride(70.f)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
	};
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Label(LOCTEXT("Whereto", "Dokąd"))]
			+ SHorizontalBox::Slot().AutoWidth()[Form->Pick(TEXT("to"), Choices, 420.f, [WeakThis](const FString&) { if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin()) { This->Rebuild(); } })]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 0)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Label(LOCTEXT("Player", "Gracz"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("find"), LOCTEXT("PlayerName", "Nazwa gracza"), 260.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Find", "Szukaj"), [WeakThis]()
				{
					TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
					AFWPlayerController* PC = This ? This->Controller.Get() : nullptr;
					if (!PC)
					{
						return;
					}
					const FString Query = This->Form->Get(TEXT("find"));
					if (Query.Len() < 2)
					{
						PC->Toast(LOCTEXT("TwoLetters", "Wpisz co najmniej 2 litery nazwy gracza."));
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("query"), Query);
					PC->Send(TEXT("station.find"), P, [WeakThis, Query](const FFWNetReply& Reply)
					{
						TSharedPtr<SFWRailWindow> Inner = WeakThis.Pin();
						AFWPlayerController* InnerPC = Inner ? Inner->Controller.Get() : nullptr;
						if (!InnerPC)
						{
							return;
						}
						if (Reply.Type != TEXT("station.found"))
						{
							InnerPC->ToastError(Reply);
							return;
						}
						Inner->Found = FWUi::Objects(Reply.Payload, TEXT("players"));
						if (Inner->Found.Num() == 0)
						{
							InnerPC->Toast(FText::Format(LOCTEXT("NoPlayer", "Nie ma gracza z działką o nazwie zaczynającej się od „{0}”."), FText::FromString(Query)));
							return;
						}
						const TArray<TSharedPtr<FJsonObject>> Plots = FWUi::Objects(Inner->Found[0], TEXT("plots"));
						if (Plots.Num() > 0)
						{
							Inner->Form->Set(TEXT("to"), FString::Printf(TEXT("%d,%d"), int32(FWUi::Num(Plots[0], TEXT("x"))), int32(FWUi::Num(Plots[0], TEXT("y")))));
						}
						Inner->Rebuild();
					});
				})
			]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 0)
		[
			SNew(SHorizontalBox).Visibility(Form->Get(TEXT("to")) == TEXT("manual") ? EVisibility::Visible : EVisibility::Collapsed)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Label(LOCTEXT("XY", "X, Y"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("toX"), LOCTEXT("X", "x"), 100.f, FString::FromInt(Plot.X))]
			+ SHorizontalBox::Slot().AutoWidth()[Form->Field(TEXT("toY"), LOCTEXT("Y", "y"), 100.f, FString::FromInt(Plot.Y))]
		];
}

TSharedRef<SWidget> SFWRailWindow::MakeTransportChoice(bool bRoute)
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	const TSharedPtr<FJsonObject> Rates = FWUi::Obj(State, TEXT("rates"));
	TArray<TPair<FString, FText>> Ways = { { TEXT("train"), LOCTEXT("Train", "Pociągiem") } };
	if (bTruck)
	{
		Ways.Add({ TEXT("truck"), FText::Format(LOCTEXT("ByTruck", "Ciężarówką z bazy (bez opłaty, do {0} km, za olej napędowy)"), int32(FWUi::Num(Rates, TEXT("truckMaxKm"), 60.0))) });
	}
	if (!bRoute && bPort)
	{
		Ways.Add({ TEXT("sea"), LOCTEXT("BySea", "Statkiem z portu (dwa razy wolniej, za ćwierć ceny)") });
	}
	if (!bRoute && bPolar)
	{
		Ways.Add({ TEXT("sled"), FText::Format(LOCTEXT("BySled", "Psim zaprzęgiem lub skuterem (bez opłaty, do {0} km)"), int32(FWUi::Num(Rates, TEXT("sledMaxKm"), 150.0))) });
	}
	if (!bRoute && bStable)
	{
		Ways.Add({ TEXT("horse"), FText::Format(LOCTEXT("ByHorse", "Wozem konnym ze stajni (bez opłaty, do {0} km, za paszę treściwą)"), int32(FWUi::Num(Rates, TEXT("horseMaxKm"), 50.0))) });
	}
	if (Ways.Num() == 1)
	{
		Form->Set(TEXT("transport"), TEXT("train"));
		return SNullWidget::NullWidget;
	}
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(SBox).WidthOverride(70.f)[SNew(STextBlock).Text(LOCTEXT("How", "Czym")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]]
		+ SHorizontalBox::Slot().AutoWidth()[Form->Pick(TEXT("transport"), Ways, 520.f, [WeakThis](const FString&) { if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin()) { This->Rebuild(); } })];
}

FText SFWRailWindow::TripInfo(int32 Quantity, bool bRoute) const
{
	const TSharedPtr<FJsonObject> Rates = FWUi::Obj(State, TEXT("rates"));
	const FIntPoint To = Destination();
	const double KmTo = Km(Plot, To);
	const FString Way = Form->Get(TEXT("transport"));
	double Seconds = FMath::Max(FWUi::Num(Rates, TEXT("minTripSeconds")), KmTo / 100.0 * FWUi::Num(Rates, TEXT("secondsPer100Km")));
	// Cheaper from a city plot, with a freight terminal or with the mayor's rail bonus (as RailRules counts it)
	double Fee = FMath::CeilToDouble((FWUi::Num(Rates, TEXT("baseFeeCents")) + FMath::CeilToDouble(KmTo / 100.0 * Quantity * FWUi::Num(Rates, TEXT("centsPerItemPer100Km"))))
		* FWUi::Num(Rates, TEXT("feeFactor"), 1.0));
	FString Text = FString::Printf(TEXT("%s  ·  %s km"), *Where(), *FWRailText::Grouped(FMath::RoundToInt64(KmTo)).ToString());
	if (To == Plot)
	{
		return FText::FromString(Text + TEXT("\nTo jest ta sama stacja."));
	}
	if (Way == TEXT("truck"))
	{
		const double MaxKm = FWUi::Num(Rates, TEXT("truckMaxKm"), 60.0);
		const int32 Diesel = FMath::Max(1, FMath::CeilToInt(KmTo / FWUi::Num(Rates, TEXT("truckKmPerDiesel"), 25.0)));
		Seconds = FMath::Max(FWUi::Num(Rates, TEXT("truckMinTripSeconds"), 5.0), KmTo / 100.0 * FWUi::Num(Rates, TEXT("secondsPer100Km")) * FWUi::Num(Rates, TEXT("truckTrip"), 0.5));
		Text += FString::Printf(TEXT("  ·  w drodze %s  ·  bez opłaty\nCiężarówka spali %d × olej napędowy ze stacji i zabierze najwyżej %s sztuk."), *FWRailText::Clock(Seconds), Diesel,
			*FWRailText::Grouped(int64(FWUi::Num(Rates, TEXT("truckCapacity"), 1000.0))).ToString());
		if (KmTo > MaxKm)
		{
			Text += FString::Printf(TEXT("\nZa daleko: ciężarówka jedzie najwyżej %d km."), int32(MaxKm));
		}
		return FText::FromString(Text);
	}
	if (Way == TEXT("sled") || Way == TEXT("horse"))
	{
		const bool bHorse = Way == TEXT("horse");
		const double MaxKm = FWUi::Num(Rates, bHorse ? TEXT("horseMaxKm") : TEXT("sledMaxKm"), bHorse ? 50.0 : 150.0);
		Seconds *= bHorse ? FWUi::Num(Rates, TEXT("horseTrip"), 2.0) : FWUi::Num(Rates, TEXT("sledDogTrip"), 1.5);
		Text += FString::Printf(TEXT("  ·  w drodze %s  ·  bez opłaty"), *FWRailText::Clock(Seconds));
		Text += bHorse ? FString::Printf(TEXT("\nKonie zjedzą %d × pasza treściwa ze stacji."), FMath::Max(1, FMath::CeilToInt(KmTo / FWUi::Num(Rates, TEXT("horseKmPerFeed"), 10.0))))
			: FString(TEXT("\nPojadą psy (jeśli stoi psiarnia). Z benzyną na stacji i garażem pojedzie szybszy skuter."));
		if (KmTo > MaxKm)
		{
			Text += FString::Printf(TEXT("\nZa daleko: najwyżej %d km."), int32(MaxKm));
		}
		return FText::FromString(Text);
	}
	if (Way == TEXT("sea"))
	{
		Fee = FMath::FloorToDouble((Fee + 3.0) / 4.0);
		Seconds *= 2.0;
	}
	if (bRoute)
	{
		Text += FString::Printf(TEXT("  ·  w drodze %s\nPociąg zabiera do %s sztuk. Pełny kosztuje %s, mniejszy mniej; pusty nie odjeżdża i nic nie kosztuje."), *FWRailText::Clock(Seconds),
			*FWRailText::Grouped(int64(FWUi::Num(Rates, TEXT("routeCapacity")))).ToString(), *FWUi::Money(Fee).ToString());
		return FText::FromString(Text);
	}
	Text += FString::Printf(TEXT("  ·  w drodze %s  ·  opłata %s"), *FWRailText::Clock(Seconds), *FWUi::Money(Fee).ToString());
	if (Way != TEXT("sea") && Rates.IsValid() && Rates->HasField(TEXT("dieselKmPerUnit")))
	{
		Text += TEXT("\nZaładuj na stację olej napędowy, a pojedzie szybszy pociąg spalinowy.");
	}
	if (Controller.IsValid() && Fee > double(Controller->GetBalanceCents()))
	{
		Text += TEXT("\nNie masz tyle pieniędzy.");
	}
	return FText::FromString(Text);
}

TSharedRef<SWidget> SFWRailWindow::MakeSend()
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Heading(LOCTEXT("SendTrain", "Wyślij pociąg")), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(State, TEXT("items"));
	if (Items.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("LoadFirst", "Najpierw załaduj coś na stację (zakładka Magazyn)."), FWStyle::Muted));
		return Page;
	}
	TArray<TPair<FString, FText>> Goods;
	for (const TSharedPtr<FJsonObject>& Stack : Items)
	{
		Goods.Add({ FWUi::Str(Stack, TEXT("id")), FText::Format(LOCTEXT("Good", "{0} ({1})"), FText::FromString(FWUi::Str(Stack, TEXT("name"))), int32(FWUi::Num(Stack, TEXT("quantity")))) });
	}
	if (!Form->Has(TEXT("sendQuantity")))
	{
		Form->Set(TEXT("sendQuantity"), FString::FromInt(int32(FWUi::Num(Items[0], TEXT("quantity")))));
	}
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(SBox).WidthOverride(70.f)[SNew(STextBlock).Text(LOCTEXT("Goods", "Towar")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Pick(TEXT("sendItem"), Goods, 300.f, nullptr, Controller.Get())]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("Quantity", "Ilość")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]
		+ SHorizontalBox::Slot().AutoWidth()[Form->Field(TEXT("sendQuantity"), FText::GetEmpty(), 90.f)], 6.f);
	Add(MakeDestination(), 6.f);
	Add(MakeTransportChoice(false), 6.f);
	Add(Form->Check(TEXT("smuggle"), LOCTEXT("Smuggle", "Przemyt (bez cła)"), false), 6.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
		.Text_Lambda([WeakThis]() { TSharedPtr<SFWRailWindow> This = WeakThis.Pin(); return This ? This->TripInfo(int32(This->Form->Number(TEXT("sendQuantity"))), false) : FText::GetEmpty(); }), 10.f);
	const FString Way = Form->Get(TEXT("transport"));
	const FText Button = Way == TEXT("truck") ? LOCTEXT("SendTruck", "Wyślij ciężarówką") : Way == TEXT("sled") ? LOCTEXT("SendSled", "Wyślij saniami")
		: Way == TEXT("horse") ? LOCTEXT("SendCart", "Wyślij wozem") : Way == TEXT("sea") ? LOCTEXT("SendShip", "Wyślij statkiem") : LOCTEXT("SendTrainButton", "Wyślij pociąg");
	Add(SNew(SBox).HAlign(HAlign_Left)
		[
			FWUi::SmallButton(Button, [WeakThis]()
			{
				TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
				if (!This)
				{
					return;
				}
				const FIntPoint To = This->Destination();
				const FString Way = This->Form->Get(TEXT("transport"));
				const FString Item = This->Form->Get(TEXT("sendItem"));
				const int32 Quantity = int32(This->Form->Number(TEXT("sendQuantity")));
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetNumberField(TEXT("toX"), To.X);
				P->SetNumberField(TEXT("toY"), To.Y);
				P->SetStringField(TEXT("item"), Item);
				P->SetNumberField(TEXT("quantity"), Quantity);
				P->SetBoolField(TEXT("sea"), Way == TEXT("sea"));
				P->SetBoolField(TEXT("sled"), Way == TEXT("sled") || Way == TEXT("horse"));
				P->SetBoolField(TEXT("truck"), Way == TEXT("truck"));
				P->SetBoolField(TEXT("smuggle"), This->Form->Ticked(TEXT("smuggle")));
				const FString Name = This->ItemNames.Contains(Item) ? This->ItemNames[Item] : Item;
				const FString Vehicle = Way == TEXT("truck") ? TEXT("Ciężarówka") : Way == TEXT("sled") ? TEXT("Sanie") : Way == TEXT("horse") ? TEXT("Wóz konny") : Way == TEXT("sea") ? TEXT("Statek") : TEXT("Pociąg");
				const FString Went = Way == TEXT("truck") ? TEXT("ruszyła") : Way == TEXT("sled") ? TEXT("ruszyły") : Way == TEXT("horse") ? TEXT("ruszył") : Way == TEXT("sea") ? TEXT("wypłynął") : TEXT("odjechał");
				This->Act(TEXT("station.send"), P, FText::FromString(FString::Printf(TEXT("%s z %d × %s %s: %s."), *Vehicle, Quantity, *Name, *Went, *This->Where())));
			}, true)
		], 8.f);
	return Page;
}

TSharedRef<SWidget> SFWRailWindow::MakeRoutes()
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Rates = FWUi::Obj(State, TEXT("rates"));
	const TArray<TSharedPtr<FJsonObject>> Routes = FWUi::Objects(State, TEXT("routes"));
	Add(FWUi::Heading(LOCTEXT("RoutesFrom", "Kursy z tej stacji")), 0.f);
	if (Routes.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoRoutes", "Brak kursów. Kurs to pociąg, który sam odjeżdża co kilka minut i zabiera to, co czeka na stacji."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Route : Routes)
	{
		const FString Receiver = FWUi::Str(Route, TEXT("receiver"));
		const int32 ToX = int32(FWUi::Num(Route, TEXT("toX"))), ToY = int32(FWUi::Num(Route, TEXT("toY")));
		const int32 Minutes = int32(FWUi::Num(Route, TEXT("everyMinutes")));
		const double NextAt = FWUi::Num(Route, TEXT("nextAt"));
		TArray<FString> Carried;
		for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Route, TEXT("items")))
		{
			Carried.Add(FWUi::Str(Item, TEXT("name")));
		}
		FString Title = FString::Printf(TEXT("→ %s  ·  %s%s%s\n%s"), Receiver.IsEmpty() ? *FString::Printf(TEXT("moja działka %d × %d"), ToX, ToY) : *FString::Printf(TEXT("%s · %d × %d"), *Receiver, ToX, ToY),
			Minutes < 60 ? *FString::Printf(TEXT("co %d min"), Minutes) : TEXT("co godzinę"), FWUi::Bool(Route, TEXT("truck")) ? TEXT("  ·  ciężarówka") : TEXT(""),
			FWUi::Bool(Route, TEXT("smuggle")) ? TEXT("  ·  przemyt") : TEXT(""), Carried.Num() > 0 ? *FString::Join(Carried, TEXT(", ")) : TEXT("wozi wszystko ze stacji"));
		const FString ProblemCode = FWUi::Str(Route, TEXT("problem"));
		double LastAt = 0.0;
		if (!ProblemCode.IsEmpty())
		{
			Title += TEXT("\n") + FWRailText::Problem(ProblemCode).ToString();
		}
		else if (Route->TryGetNumberField(TEXT("lastAt"), LastAt))
		{
			Title += FString::Printf(TEXT("\nOstatni %s zabrał %d szt."), FWUi::Bool(Route, TEXT("truck")) ? TEXT("kurs ciężarówki") : TEXT("pociąg"), int32(FWUi::Num(Route, TEXT("lastQuantity"))));
		}
		const double Id = FWUi::Num(Route, TEXT("id"));
		Add(FWRailText::Line(Controller.Get(), FString(), FText::FromString(Title), ProblemCode.IsEmpty() || ProblemCode == TEXT("empty") ? FWStyle::Text : FWStyle::Error,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Accent)
				.Text_Lambda([WeakThis, NextAt]()
				{
					TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
					const double Seconds = This ? FMath::CeilToDouble((NextAt - This->ServerNow()) / 1000.0) : 0.0;
					return Seconds > 0.0 ? FText::FromString(FString::Printf(TEXT("następny za %s"), *FWRailText::Clock(Seconds))) : LOCTEXT("Leaving", "odjeżdża…");
				})
			]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Remove", "Usuń"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("id"), Id);
						This->Act(TEXT("station.unroute"), P, FText::GetEmpty());
					}
				})
			]), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("NewRoute", "Nowy kurs")), 18.f);
	if (Routes.Num() >= int32(FWUi::Num(Rates, TEXT("maxRoutes"))))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("MaxRoutes", "Ze stacji może odjeżdżać najwyżej {0} kursów."), int32(FWUi::Num(Rates, TEXT("maxRoutes")))), FWStyle::Muted));
		return Page;
	}
	Add(MakeDestination(), 4.f);
	TArray<TPair<FString, FText>> Every;
	for (const TSharedPtr<FJsonValue>& Value : Rates.IsValid() && Rates->HasField(TEXT("routeMinutes")) ? Rates->GetArrayField(TEXT("routeMinutes")) : TArray<TSharedPtr<FJsonValue>>())
	{
		const int32 Minutes = int32(Value->AsNumber());
		Every.Add({ FString::FromInt(Minutes), Minutes < 60 ? FText::Format(LOCTEXT("EveryMin", "co {0} min"), Minutes) : LOCTEXT("EveryHour", "co godzinę") });
	}
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(SBox).WidthOverride(70.f)[SNew(STextBlock).Text(LOCTEXT("HowOften", "Co ile")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]]
		+ SHorizontalBox::Slot().AutoWidth()[Form->Pick(TEXT("every"), Every, 200.f)], 6.f);
	Add(MakeTransportChoice(true), 6.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("WhatToCarry", "Co wozić (nic nie zaznaczone: wszystko ze stacji; najwyżej {0} rodzaje)"), int32(FWUi::Num(Rates, TEXT("maxRouteItems")))), FWStyle::Muted), 8.f);
	// Goods from the station and the inventory, picked by clicking
	TMap<FString, FString> Known;
	for (const TSharedPtr<FJsonObject>& Stack : FWUi::Objects(State, TEXT("items")))
	{
		Known.Add(FWUi::Str(Stack, TEXT("id")), FWUi::Str(Stack, TEXT("name")));
	}
	for (const FFWStack& Stack : Controller.IsValid() ? Controller->GetInventory() : TArray<FFWStack>())
	{
		if (Stack.Quantity > 0 && !Stack.Id.StartsWith(TEXT("furniture_")))
		{
			Known.Add(Stack.Id, Stack.Name);
		}
	}
	TSharedRef<SWrapBox> Choice = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
	const int32 MaxItems = int32(FWUi::Num(Rates, TEXT("maxRouteItems"), 3.0));
	for (const TPair<FString, FString>& Each : Known)
	{
		const FString Item = Each.Key;
		Choice->AddSlot()
		[
			FWUi::TabButton(FText::FromString(Each.Value), [WeakThis, Item]() { TSharedPtr<SFWRailWindow> This = WeakThis.Pin(); return This && This->RouteItems.Contains(Item); },
				[WeakThis, Item, MaxItems]()
				{
					TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
					if (!This)
					{
						return;
					}
					if (This->RouteItems.Contains(Item))
					{
						This->RouteItems.Remove(Item);
					}
					else if (This->RouteItems.Num() < MaxItems)
					{
						This->RouteItems.Add(Item);
					}
					else if (AFWPlayerController* PC = This->Controller.Get())
					{
						PC->Toast(FText::Format(LOCTEXT("TooManyKinds", "Kurs może wozić najwyżej {0} rodzajów towaru."), MaxItems));
					}
				})
		];
	}
	if (Known.Num() == 0)
	{
		Choice->AddSlot()[FWUi::Note(LOCTEXT("GoodsHere", "Towary ze stacji i z ekwipunku pojawią się tutaj."), FWStyle::Muted)];
	}
	Add(Choice, 4.f);
	Add(Form->Check(TEXT("routeSmuggle"), LOCTEXT("Smuggle2", "Przemyt (bez cła)"), false), 6.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
		.Text_Lambda([WeakThis]() { TSharedPtr<SFWRailWindow> This = WeakThis.Pin(); return This ? This->TripInfo(int32(FWUi::Num(FWUi::Obj(This->State, TEXT("rates")), TEXT("routeCapacity"))), true) : FText::GetEmpty(); }), 8.f);
	Add(SNew(SBox).HAlign(HAlign_Left)
		[
			FWUi::SmallButton(LOCTEXT("StartRoute", "Uruchom kurs"), [WeakThis]()
			{
				TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
				if (!This)
				{
					return;
				}
				const FIntPoint To = This->Destination();
				TArray<TSharedPtr<FJsonValue>> Items;
				for (const FString& Item : This->RouteItems)
				{
					Items.Add(MakeShared<FJsonValueString>(Item));
				}
				const int32 Minutes = int32(This->Form->Number(TEXT("every")));
				const bool bTruckRoute = This->Form->Get(TEXT("transport")) == TEXT("truck");
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetNumberField(TEXT("toX"), To.X);
				P->SetNumberField(TEXT("toY"), To.Y);
				P->SetArrayField(TEXT("items"), Items);
				P->SetNumberField(TEXT("everyMinutes"), Minutes);
				P->SetBoolField(TEXT("truck"), bTruckRoute);
				P->SetBoolField(TEXT("smuggle"), This->Form->Ticked(TEXT("routeSmuggle")));
				This->RouteItems.Reset();
				This->Act(TEXT("station.route"), P, FText::FromString(FString::Printf(TEXT("Kurs uruchomiony: %s do %s co %d min."), bTruckRoute ? TEXT("ciężarówka") : TEXT("pociąg"), *This->Where(), Minutes)));
			}, true)
		], 8.f);
	return Page;
}

TSharedRef<SWidget> SFWRailWindow::MakeTrips()
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	auto Trip = [this, WeakThis](const TSharedPtr<FJsonObject>& T, const FString& Route)
	{
		const double Arrives = FWUi::Num(T, TEXT("arrivesAt"));
		return FWRailText::Line(Controller.Get(), FWUi::Str(T, TEXT("item")), FText::FromString(FString::Printf(TEXT("%d × %s  %s"), int32(FWUi::Num(T, TEXT("quantity"))), *FWUi::Str(T, TEXT("name")), *Route)),
			FWStyle::Text,
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
			.Text_Lambda([WeakThis, Arrives]()
			{
				TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
				const double Seconds = This ? FMath::CeilToDouble((Arrives - This->ServerNow()) / 1000.0) : 0.0;
				return Seconds > 0.0 ? FText::FromString(FWRailText::Clock(Seconds)) : LOCTEXT("Arrived", "dojechał");
			}));
	};
	Add(FWUi::Heading(LOCTEXT("Incoming", "Przyjeżdżają")), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Incoming = FWUi::Objects(State, TEXT("incoming"));
	if (Incoming.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NothingComing", "Nic nie jedzie na tę stację."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& T : Incoming)
	{
		const FString Sender = FWUi::Str(T, TEXT("sender"));
		Add(Trip(T, FString::Printf(TEXT("z %s%d × %d"), Sender.IsEmpty() ? TEXT("") : *(Sender + TEXT(" · ")), int32(FWUi::Num(T, TEXT("fromX"))), int32(FWUi::Num(T, TEXT("fromY"))))), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("Outgoing", "Odjechały")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Outgoing = FWUi::Objects(State, TEXT("outgoing"));
	if (Outgoing.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NothingGone", "Żaden pociąg stąd nie jest w drodze."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& T : Outgoing)
	{
		const FString Receiver = FWUi::Str(T, TEXT("receiver"));
		Add(Trip(T, FString::Printf(TEXT("do %s%d × %d"), Receiver.IsEmpty() ? TEXT("") : *(Receiver + TEXT(" · ")), int32(FWUi::Num(T, TEXT("toX"))), int32(FWUi::Num(T, TEXT("toY"))))), 3.f);
	}
	return Page;
}

#undef LOCTEXT_NAMESPACE
