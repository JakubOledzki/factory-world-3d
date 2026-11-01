#include "UI/SFWExportWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWExport"

namespace FWExportText
{
	FText Problem(const FString& Code)
	{
		static const TMap<FString, FText> Problems = {
			{ TEXT("empty"), LOCTEXT("Empty", "Ostatni pociąg odjechał pusty: skrzynia była pusta.") },
			{ TEXT("no_price"), LOCTEXT("NoPrice", "Ostatni pociąg nic nie zabrał: żaden towar w skrzyni nie ma ceny.") },
			{ TEXT("no_market"), LOCTEXT("NoMarket", "Ostatni pociąg nic nie zabrał: na świecie nie ma jeszcze żadnego rynku skupu.") },
			{ TEXT("no_route"), LOCTEXT("NoRoute", "Ostatni pociąg nic nie zabrał: do żadnego rynku skupu nie da się dojechać, brakuje portów pociągowych.") },
			{ TEXT("no_money"), LOCTEXT("NoMoney", "Ostatni pociąg nic nie zabrał: zabrakło pieniędzy na prowizję i opłaty portów.") } };
		return Problems.Contains(Code) ? Problems[Code] : LOCTEXT("Nothing", "Ostatni pociąg nic nie zabrał.");
	}

	FString Clock(double Seconds)
	{
		const int64 S = int64(FMath::Max(Seconds, 0.0));
		return S >= 3600 ? FString::Printf(TEXT("%lld:%02lld:%02lld"), S / 3600, S % 3600 / 60, S % 60) : FString::Printf(TEXT("%lld:%02lld"), S / 60, S % 60);
	}

	FString Duration(int64 Seconds)
	{
		return Seconds < 3600 ? FString::Printf(TEXT("%lld min %02lld s"), Seconds / 60, Seconds % 60) : FString::Printf(TEXT("%lld h %02lld min"), Seconds / 3600, Seconds % 3600 / 60);
	}

	FString Plots(double Plots)
	{
		return FString::Printf(TEXT("%.1f dz."), Plots).Replace(TEXT("."), TEXT(",")).Replace(TEXT(",dz"), TEXT(" dz"));
	}

	void Wallet(AFWPlayerController* PC)
	{
		TWeakObjectPtr<AFWPlayerController> Weak(PC);
		PC->Send(TEXT("wallet.get"), nullptr, [Weak](const FFWNetReply& Reply)
		{
			double Cents = 0.0;
			if (AFWPlayerController* Inner = Weak.Get(); Inner && Reply.Payload.IsValid() && Reply.Payload->TryGetNumberField(TEXT("balanceCents"), Cents))
			{
				Inner->SetBalanceCents(int64(Cents));
			}
		});
	}
}

void SFWExportWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Post = InArgs._Post;
	Form = MakeShared<FFWForm>();
	AFWPlayerController* PC = Controller.Get();
	if (PC && PC->GetPlot())
	{
		Plot = FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
	}
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(Post.IsEmpty() ? LOCTEXT("Chest", "Skrzynia eksportowa") : LOCTEXT("Post", "Rynek skupu i port pociągowy"), Right, nullptr, SAssignNew(Content, SBox), 900.f, 760.f)
	];
	if (PC && Post.IsEmpty())
	{
		TWeakPtr<SFWExportWindow> WeakThis = SharedThis(this);
		PC->Send(TEXT("market.prices"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWExportWindow> This = WeakThis.Pin(); This && Reply.Type == TEXT("market.prices"))
			{
				// What the trader pays for a piece: a price to start from
				for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Reply.Payload, TEXT("items")))
				{
					double Sell = 0.0;
					if (Item->TryGetNumberField(TEXT("sellCents"), Sell))
					{
						This->Trader.Add(FWUi::Str(Item, TEXT("id")), Sell);
					}
				}
				This->Rebuild();
			}
		});
		PC->RefreshInventory();
	}
	Load();
}

double SFWExportWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

void SFWExportWindow::Load(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + (Post.IsEmpty() ? 5.0 : 10.0);
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	if (Post.IsEmpty())
	{
		P->SetNumberField(TEXT("x"), Plot.X);
		P->SetNumberField(TEXT("y"), Plot.Y);
	}
	else
	{
		P->SetStringField(TEXT("id"), Post);
	}
	TWeakPtr<SFWExportWindow> WeakThis = SharedThis(this);
	PC->Send(Post.IsEmpty() ? TEXT("export.get") : TEXT("export.post"), P, [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWExportWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		// Asked again by itself: not built anew under the player typing a price
		const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
		const bool bTyping = Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText"));
		if ((bPoll && bTyping) || (bPoll && Reply.IsError()))
		{
			return;
		}
		This->Apply(Reply);
	});
}

bool SFWExportWindow::Apply(const FFWNetReply& Reply)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != (Post.IsEmpty() ? TEXT("export") : TEXT("export.post")))
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		// Nothing to show yet (no export chest on the plot, say): the reason instead of the page
		if (!State.IsValid() && Content.IsValid())
		{
			Content->SetContent(FWUi::Note(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()), FWStyle::Error));
		}
		return false;
	}
	// The train took the goods and paid the fees
	if (PC && State.IsValid() && FWUi::Num(State, TEXT("lastAt")) != FWUi::Num(Reply.Payload, TEXT("lastAt")))
	{
		FWExportText::Wallet(PC);
	}
	State = Reply.Payload;
	ClockOffset = FWUi::Num(State, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
	Rebuild();
	return true;
}

void SFWExportWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	if (Post.IsEmpty())
	{
		Payload->SetNumberField(TEXT("x"), Plot.X);
		Payload->SetNumberField(TEXT("y"), Plot.Y);
	}
	TWeakPtr<SFWExportWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWExportWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (Inner && This->Apply(Reply))
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
		}
	});
}

void SFWExportWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = !State.IsValid() ? FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted) : Post.IsEmpty() ? MakeChest() : MakePost();
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWExportWindow::MakeChest()
{
	TWeakPtr<SFWExportWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Rates = FWUi::Obj(State, TEXT("rates"));
	const double NextAt = FWUi::Num(State, TEXT("nextAt"));
	Add(SNew(STextBlock).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).AutoWrapText(true)
		.Text_Lambda([WeakThis, NextAt]()
		{
			TSharedPtr<SFWExportWindow> This = WeakThis.Pin();
			const double Seconds = This ? FMath::CeilToDouble((NextAt - This->ServerNow()) / 1000.0) : 0.0;
			return Seconds > 0.0 ? FText::FromString(FString::Printf(TEXT("Następny pociąg za %s: zabierze towar z ceną na rynek skupu."), *FWExportText::Clock(Seconds)))
				: LOCTEXT("TrainLoading", "Pociąg ładuje towar…");
		}), 0.f);
	// The way to the nearest buying market
	const TSharedPtr<FJsonObject> Route = FWUi::Obj(State, TEXT("route"));
	FString RouteText;
	if (!Route.IsValid())
	{
		RouteText = FWUi::Num(State, TEXT("markets")) == 0.0 ? TEXT("Na świecie nie ma jeszcze żadnego rynku skupu, więc nic nie da się wystawić. Rynek skupu buduje się w mieście (po badaniu „Rynek skupu”).")
			: TEXT("Żaden rynek skupu nie leży na tym lądzie, a pociągi nie jeżdżą po wodzie: potrzebny jest port pociągowy na wybrzeżu tego lądu i drugi tam, gdzie stoi rynek.");
	}
	else
	{
		const TSharedPtr<FJsonObject> Market = FWUi::Obj(Route, TEXT("market"));
		RouteText = FString::Printf(TEXT("Najbliższy rynek skupu: %s (%s, prowizja %d%%). Droga: %s lądem"), *FWUi::Str(Route, TEXT("town")), *FWUi::Str(Market, TEXT("owner")),
			int32(FWUi::Num(Route, TEXT("commissionPercent"))), *FWExportText::Plots(FWUi::Num(Route, TEXT("landPlots"))));
		if (FWUi::Num(Route, TEXT("seaPlots")) > 0.0)
		{
			RouteText += FString::Printf(TEXT(" i %s promem (3 × wolniej)"), *FWExportText::Plots(FWUi::Num(Route, TEXT("seaPlots"))));
		}
		RouteText += FString::Printf(TEXT(", %s jazdy."), *FWExportText::Duration(int64(FWUi::Num(Route, TEXT("tripSeconds")))));
		TArray<FString> Ports;
		for (const TSharedPtr<FJsonObject>& Port : FWUi::Objects(Route, TEXT("ports")))
		{
			Ports.Add(FString::Printf(TEXT("%d × %d (%s, %s)"), int32(FWUi::Num(Port, TEXT("x"))), int32(FWUi::Num(Port, TEXT("y"))), *FWUi::Str(Port, TEXT("owner")),
				*FWUi::Money(FWUi::Num(Port, TEXT("value"))).ToString()));
		}
		if (Ports.Num() > 0)
		{
			RouteText += FString::Printf(TEXT("\nPorty pociągowe: %s. Za przeprawę zapłacisz %s."), *FString::Join(Ports, TEXT(", ")), *FWUi::Money(FWUi::Num(Route, TEXT("portFeesCents"))).ToString());
		}
	}
	Add(FWUi::Note(FText::FromString(RouteText), FWStyle::Text), 6.f);
	const FString ProblemCode = FWUi::Str(State, TEXT("problem"));
	double LastAt = 0.0;
	Add(FWUi::Note(!ProblemCode.IsEmpty() ? FWExportText::Problem(ProblemCode) : !State->TryGetNumberField(TEXT("lastAt"), LastAt)
		? LOCTEXT("FirstTrain", "Pierwszy pociąg przyjedzie o pełnej godzinie i zabierze każdy towar, który ma cenę.")
		: FText::Format(LOCTEXT("LastTook", "Ostatni pociąg zabrał {0} szt."), int32(FWUi::Num(State, TEXT("lastQuantity")))), FWStyle::Muted), 4.f);
	int32 Used = 0;
	for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(State, TEXT("items")))
	{
		Used += int32(FWUi::Num(Item, TEXT("quantity")));
	}
	const int32 Capacity = int32(FWUi::Num(State, TEXT("capacity")));
	Add(FWUi::Bar(Capacity > 0 ? float(Used) / Capacity : 0.f, FWStyle::Accent), 12.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("Fill", "W skrzyni {0} / {1} szt. (najwyżej {2} rodzajów). Cena za sztukę, po której pociąg wystawi towar (0 = zostaje w skrzyni):"), Used, Capacity,
		int32(FWUi::Num(State, TEXT("maxKinds")))), FWStyle::Muted), 4.f);
	const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(State, TEXT("items"));
	const TArray<TSharedPtr<FJsonObject>> Prices = FWUi::Objects(State, TEXT("prices"));
	if (Items.Num() == 0 && Prices.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("ChestEmpty", "Skrzynia jest pusta. Włóż towar albo dowieź go podajnikiem z taśmy."), FWStyle::Muted), 4.f);
	}
	auto Row = [this, WeakThis](const FString& Item, const FString& Name, int32 Quantity, const TSharedPtr<FJsonObject>& Entry)
	{
		double Price = 0.0;
		const bool bPriced = Entry->TryGetNumberField(TEXT("priceCents"), Price);
		const FString Key = TEXT("price:") + Item;
		const FString Start = FString::SanitizeFloat((bPriced ? Price : Trader.FindRef(Item)) / 100.0);
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[SNew(SBox).ToolTipText(LOCTEXT("PriceTip", "Cena za sztukę na rynku graczy"))[Form->Field(Key, LOCTEXT("Dollars", "$"), 100.f, Start)]]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(bPriced ? LOCTEXT("Change", "Zmień") : LOCTEXT("Set", "Ustaw"), [WeakThis, Item, Key]()
				{
					if (TSharedPtr<SFWExportWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), Item);
						P->SetNumberField(TEXT("priceCents"), This->Form->Cents(Key));
						This->Act(TEXT("export.price"), P);
					}
				}, !bPriced)
			];
		if (Quantity > 0)
		{
			Right->AddSlot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Take", "Wyjmij"), [WeakThis, Item, Quantity]()
				{
					if (TSharedPtr<SFWExportWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), Item);
						P->SetNumberField(TEXT("quantity"), Quantity);
						This->Act(TEXT("export.unload"), P);
					}
				})
			];
		}
		return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Item, 28.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(Quantity > 0 ? FWStyle::Text : FWStyle::Muted)
					.Text(FText::FromString(FString::Printf(TEXT("%s\n%s"), *Name, Quantity > 0 ? *FString::Printf(TEXT("%d szt."), Quantity) : TEXT("brak w skrzyni"))))
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right]
			];
	};
	for (const TSharedPtr<FJsonObject>& Item : Items)
	{
		Add(Row(FWUi::Str(Item, TEXT("id")), FWUi::Str(Item, TEXT("name")), int32(FWUi::Num(Item, TEXT("quantity"))), Item), 3.f);
	}
	for (const TSharedPtr<FJsonObject>& Price : Prices)
	{
		Add(Row(FWUi::Str(Price, TEXT("id")), FWUi::Str(Price, TEXT("name")), 0, Price), 3.f);
	}
	// Putting goods in from the inventory
	Add(FWUi::Heading(LOCTEXT("PutIn", "Włóż z ekwipunku")), 16.f);
	TArray<TPair<FString, FText>> Goods;
	for (const FFWStack& Stack : Controller.IsValid() ? Controller->GetInventory() : TArray<FFWStack>())
	{
		if (Stack.Quantity > 0)
		{
			Goods.Add({ Stack.Id, FText::FromString(FString::Printf(TEXT("%s (%d)"), *Stack.Name, Stack.Quantity)) });
		}
	}
	if (Goods.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("InventoryEmpty", "Ekwipunek jest pusty."), FWStyle::Muted));
	}
	else
	{
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Pick(TEXT("loadItem"), Goods, 320.f, nullptr, Controller.Get())]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("loadQuantity"), FText::GetEmpty(), 90.f, TEXT("100"))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Put", "Włóż"), [WeakThis]()
				{
					if (TSharedPtr<SFWExportWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), This->Form->Get(TEXT("loadItem")));
						P->SetNumberField(TEXT("quantity"), This->Form->Number(TEXT("loadQuantity")));
						This->Act(TEXT("export.load"), P);
					}
				}, true)
			], 4.f);
	}
	// On the way to the market
	Add(FWUi::Heading(LOCTEXT("OnWay", "W drodze na rynek")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Shipments = FWUi::Objects(State, TEXT("shipments"));
	if (Shipments.Num() == 0)
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("NoShipments", "Żaden pociąg z towarem z tej skrzyni nie jest w drodze. Pociąg przyjeżdża co {0} min, o pełnej godzinie."),
			int32(FWUi::Num(Rates, TEXT("everyMinutes")))), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Trip : Shipments)
	{
		const double Arrives = FWUi::Num(Trip, TEXT("arrivesAt"));
		Add(SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), FWUi::Str(Trip, TEXT("item")), 26.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
					.Text(FText::FromString(FString::Printf(TEXT("%d × %s po %s · %s"), int32(FWUi::Num(Trip, TEXT("quantity"))), *FWUi::Str(Trip, TEXT("name")),
						*FWUi::Money(FWUi::Num(Trip, TEXT("priceCents"))).ToString(), *FWUi::Str(Trip, TEXT("town")))))
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
					.Text_Lambda([WeakThis, Arrives]()
					{
						TSharedPtr<SFWExportWindow> This = WeakThis.Pin();
						const double Seconds = This ? FMath::CeilToDouble((Arrives - This->ServerNow()) / 1000.0) : 0.0;
						return Seconds > 0.0 ? FText::FromString(FWExportText::Clock(Seconds)) : LOCTEXT("AtMarket", "na rynku");
					})
				]
			], 3.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWExportWindow::MakePost()
{
	TWeakPtr<SFWExportWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const bool bMarket = FWUi::Str(State, TEXT("kind")) == TEXT("furniture_buy_market");
	const double Value = FWUi::Num(State, TEXT("value"));
	const bool bMine = FWUi::Bool(State, TEXT("mine"));
	if (bMarket)
	{
		Add(FWUi::Heading(FText::Format(LOCTEXT("MarketTitle", "Rynek skupu · {0}"), FText::FromString(FWUi::Str(State, TEXT("town"))))), 0.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("MarketAbout", "Pociągi ze skrzyń eksportowych przy stacjach przywożą tu co godzinę towar z najbliższej okolicy i wystawiają go na rynek graczy po cenach sprzedawców. Jeden rynek skupu na miasto; na świecie stoi ich teraz {0}."),
			int32(FWUi::Num(State, TEXT("markets")))), FWStyle::Muted));
		Add(FWUi::Note(FText::Format(LOCTEXT("MarketOwner", "Prowadzi: {0}.\nProwizja od cudzych towarów: {1}% wartości, płatna przy wystawieniu."), FText::FromString(FWUi::Str(State, TEXT("owner"))), int32(Value)), FWStyle::Text), 10.f);
	}
	else
	{
		Add(FWUi::Heading(FText::Format(LOCTEXT("PortTitle", "Port pociągowy · {0} × {1}"), int32(FWUi::Num(State, TEXT("x"))), int32(FWUi::Num(State, TEXT("y"))))), 0.f);
		Add(FWUi::Note(LOCTEXT("PortAbout", "Pociągi nie jeżdżą po wodzie: tu wjeżdżają na prom i płyną do innego portu pociągowego, trzy razy wolniej niż lądem. Pociąg wybiera najszybszą drogę do rynku skupu, przez dowolne porty."), FWStyle::Muted));
		Add(FWUi::Note(FText::Format(LOCTEXT("PortOwner", "Właściciel: {0}.\nOpłata za przeprawę cudzego pociągu: {1}."), FText::FromString(FWUi::Str(State, TEXT("owner"))), FWUi::Money(Value)), FWStyle::Text), 10.f);
	}
	if (bMine)
	{
		Add(FWUi::Heading(FText::Format(LOCTEXT("Earned", "Zarobiono {0} ({1} {2})."), FWUi::Money(FWUi::Num(State, TEXT("earnedCents"))), int32(FWUi::Num(State, TEXT("uses"))),
			bMarket ? LOCTEXT("Listings", "wystawień") : LOCTEXT("Crossings", "przepraw"))), 14.f);
		const double MaxValue = FWUi::Num(State, TEXT("maxValue"));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
				.Text(bMarket ? FText::Format(LOCTEXT("CommissionHint", "Prowizja % (0–{0})"), int32(MaxValue)) : FText::Format(LOCTEXT("FeeHint", "Opłata $ (0–{0})"), FWUi::Money(MaxValue)))
			]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("value"), FText::GetEmpty(), 100.f, FString::SanitizeFloat(bMarket ? Value : Value / 100.0))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Save", "Zapisz"), [WeakThis, bMarket]()
				{
					if (TSharedPtr<SFWExportWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("id"), This->Post);
						P->SetNumberField(TEXT("value"), bMarket ? FMath::RoundToDouble(This->Form->Number(TEXT("value"))) : This->Form->Cents(TEXT("value")));
						This->Act(TEXT("export.setPost"), P);
					}
				}, true)
			], 6.f);
	}
	return Page;
}

void SFWExportWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() >= NextPoll)
	{
		Load(true);
	}
}

FReply SFWExportWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
