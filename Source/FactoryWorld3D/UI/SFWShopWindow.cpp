#include "UI/SFWShopWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWShop"

namespace FWShopText
{
	bool Typing()
	{
		const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
		return Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText"));
	}

	// How many customers buy at a price: all at the trader's price, half at twice that, none at three times
	int32 BuyPercent(double PriceCents, double TraderCents)
	{
		return TraderCents > 0.0 ? int32(FMath::RoundToDouble(FMath::Clamp(1.5 - PriceCents / (2.0 * TraderCents), 0.0, 1.0) * 100.0)) : 0;
	}

	FString Duration(double Seconds)
	{
		const int64 Minutes = FMath::Max<int64>(1, int64(FMath::CeilToDouble(Seconds / 60.0)));
		return Minutes < 60 ? FString::Printf(TEXT("%lld min"), Minutes) : FString::Printf(TEXT("%lld h %lld min"), Minutes / 60, Minutes % 60);
	}
}

SFWShopWindow::EMode SFWShopWindow::ModeOf(const FString& Kind)
{
	return Kind == TEXT("furniture_shop") ? EMode::Shop : Kind == TEXT("furniture_canteen") ? EMode::Canteen : EMode::None;
}

void SFWShopWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Object = InArgs._Object;
	Kind = InArgs._Kind;
	Mode = ModeOf(Kind);
	Form = MakeShared<FFWForm>();
	AFWPlayerController* PC = Controller.Get();
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(Mode == EMode::Shop ? LOCTEXT("Shop", "Sklep") : LOCTEXT("Canteen", "Stołówka zakładowa"), Right, nullptr, SAssignNew(Content, SBox), 860.f, 720.f)
	];
	if (PC)
	{
		PC->RefreshInventory();
		if (Mode == EMode::Shop)
		{
			TWeakPtr<SFWShopWindow> WeakThis = SharedThis(this);
			PC->Send(TEXT("market.prices"), nullptr, [WeakThis](const FFWNetReply& Reply)
			{
				TSharedPtr<SFWShopWindow> This = WeakThis.Pin();
				if (!This || Reply.Type != TEXT("market.prices"))
				{
					return;
				}
				for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Reply.Payload, TEXT("items")))
				{
					double Sell = 0.0;
					if (Item->TryGetNumberField(TEXT("sellCents"), Sell))
					{
						This->Trader.Add(FWUi::Str(Item, TEXT("id")), Sell);
					}
				}
				This->SuggestPrice();
				This->Rebuild();
			});
		}
	}
	Load();
}

double SFWShopWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

void SFWShopWindow::Load(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || Mode == EMode::None)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + 5.0;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(Mode == EMode::Shop ? TEXT("id") : TEXT("canteen"), Object);
	TWeakPtr<SFWShopWindow> WeakThis = SharedThis(this);
	PC->Send(Mode == EMode::Shop ? TEXT("shop.get") : TEXT("canteen.get"), P, [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWShopWindow> This = WeakThis.Pin();
		// Asked again by itself: not built anew under the player typing, and no error toasts every few seconds
		if (This && !(bPoll && (FWShopText::Typing() || Reply.IsError())))
		{
			This->Apply(Reply);
		}
	});
}

bool SFWShopWindow::Apply(const FFWNetReply& Reply)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != (Mode == EMode::Shop ? TEXT("shop") : TEXT("canteen")))
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		if (!State.IsValid() && Content.IsValid())
		{
			Content->SetContent(FWUi::Note(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()), FWStyle::Error));
		}
		return false;
	}
	// Customers bought something: the money is in the wallet already
	if (PC && Mode == EMode::Shop && State.IsValid() && FWUi::Num(State, TEXT("soldCents")) != FWUi::Num(Reply.Payload, TEXT("soldCents")))
	{
		TWeakObjectPtr<AFWPlayerController> WeakPC(PC);
		PC->Send(TEXT("wallet.get"), nullptr, [WeakPC](const FFWNetReply& Wallet)
		{
			double Cents = 0.0;
			if (AFWPlayerController* Inner = WeakPC.Get(); Inner && Wallet.Payload.IsValid() && Wallet.Payload->TryGetNumberField(TEXT("balanceCents"), Cents))
			{
				Inner->SetBalanceCents(int64(Cents));
			}
		});
	}
	const bool bFirst = !State.IsValid();
	State = Reply.Payload;
	ClockOffset = FWUi::Num(State, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
	if (bFirst)
	{
		SuggestPrice();
	}
	Rebuild();
	return true;
}

void SFWShopWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWShopWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWShopWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (Inner && This->Apply(Reply))
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
			// Again soon, with the backpack as it is now
			This->NextPoll = FPlatformTime::Seconds() + 0.6;
		}
	});
}

void SFWShopWindow::Stock(const FString& Item, int32 Quantity, double Dollars)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("id"), Object);
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	P->SetNumberField(TEXT("priceCents"), FMath::RoundToDouble(Dollars * 100.0));
	Act(TEXT("shop.stock"), P);
}

void SFWShopWindow::SuggestPrice()
{
	const FString Item = Form->Get(TEXT("item"));
	if (Item.IsEmpty())
	{
		return;
	}
	for (const TSharedPtr<FJsonObject>& Shelf : FWUi::Objects(State, TEXT("items")))
	{
		if (FWUi::Str(Shelf, TEXT("id")) == Item)
		{
			Form->Set(TEXT("price"), FString::Printf(TEXT("%.2f"), FWUi::Num(Shelf, TEXT("priceCents")) / 100.0));
			return;
		}
	}
	if (const double* Pays = Trader.Find(Item))
	{
		Form->Set(TEXT("price"), FString::Printf(TEXT("%.2f"), *Pays / 100.0));
	}
}

void SFWShopWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = !State.IsValid() ? FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted) : Mode == EMode::Shop ? MakeShop() : MakeCanteen();
	TWeakPtr<SFWShopWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()[Page]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 18, 0, 0).HAlign(HAlign_Left)
		[
			SNew(SBox).ToolTipText(Mode == EMode::Shop ? LOCTEXT("PickUpShopTip", "Towar z półek wróci do ekwipunku") : FText::GetEmpty())
			[
				FWUi::SmallButton(LOCTEXT("PickUp", "Schowaj do ekwipunku"), [WeakThis]()
				{
					TSharedPtr<SFWShopWindow> This = WeakThis.Pin();
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

TSharedRef<SWidget> SFWShopWindow::MakeShop()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWShopWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Tax = int32(FWUi::Num(State, TEXT("taxPercent")));
	Add(SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).AutoWrapText(true)
		.Text(FText::FromString(FString::Printf(TEXT("%s · około %s klientów na godzinę. Sprzedano %d szt. za %s.%s"), *FWUi::Str(State, TEXT("town")),
			*FString::SanitizeFloat(FWUi::Num(State, TEXT("customersPerHour"))).Replace(TEXT("."), TEXT(",")), int32(FWUi::Num(State, TEXT("soldItems"))),
			*FWUi::Money(FWUi::Num(State, TEXT("soldCents"))).ToString(), Tax > 0 ? *FString::Printf(TEXT(" Burmistrz bierze %d%% podatku."), Tax) : TEXT("")))), 0.f);

	// The shelves
	Add(FWUi::Heading(LOCTEXT("Shelves", "Na półkach")), 14.f);
	const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(State, TEXT("items"));
	if (Items.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("Empty", "Półki są puste: wystaw towar, a klienci zaczną przychodzić."), FWStyle::Muted), 4.f);
	}
	for (const TSharedPtr<FJsonObject>& Item : Items)
	{
		const FString Id = FWUi::Str(Item, TEXT("id"));
		const int32 Quantity = int32(FWUi::Num(Item, TEXT("quantity")));
		double TraderCents = 0.0;
		const bool bTrader = Item->TryGetNumberField(TEXT("traderCents"), TraderCents);
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(PC, Id, 34.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
						.Text(FText::FromString(FString::Printf(TEXT("%s: %d szt. po %s"), *FWUi::Str(Item, TEXT("name")), Quantity, *FWUi::Money(FWUi::Num(Item, TEXT("priceCents"))).ToString())))
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
						.Text(FText::FromString(FString::Printf(TEXT("handlarz płaci %s · kupuje %d%% klientów"), bTrader ? *FWUi::Money(TraderCents).ToString() : TEXT("-"),
							int32(FWUi::Num(Item, TEXT("buyPercent"))))))
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					FWUi::SmallButton(LOCTEXT("TakeOff", "Zdejmij"), [WeakThis, Id, Quantity]()
					{
						if (TSharedPtr<SFWShopWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetStringField(TEXT("id"), This->Object);
							P->SetStringField(TEXT("item"), Id);
							P->SetNumberField(TEXT("quantity"), Quantity);
							This->Act(TEXT("shop.take"), P);
						}
					})
				]
			], 6.f);
	}

	// Putting goods out from the backpack
	Add(FWUi::Heading(LOCTEXT("Stock", "Wystaw z ekwipunku")), 18.f);
	TArray<TPair<FString, FText>> Options;
	for (const FFWStack& Stack : PC ? PC->GetInventory() : TArray<FFWStack>())
	{
		Options.Add(TPair<FString, FText>(Stack.Id, FText::FromString(FString::Printf(TEXT("%s (%d)"), *Stack.Name, Stack.Quantity))));
	}
	if (Options.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NothingToSell", "Plecak jest pusty."), FWStyle::Muted), 4.f);
		return Page;
	}
	if (!Form->Has(TEXT("item")))
	{
		Form->Set(TEXT("item"), Options[0].Key);
		SuggestPrice();
	}
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
		[
			Form->Pick(TEXT("item"), Options, 340.f, [WeakThis](const FString&)
			{
				if (TSharedPtr<SFWShopWindow> This = WeakThis.Pin())
				{
					This->SuggestPrice();
					This->Rebuild();
				}
			}, PC)
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(LOCTEXT("Pieces", "szt."))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Form->Field(TEXT("quantity"), LOCTEXT("Quantity", "ile"), 90.f, TEXT("10"))], 4.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(LOCTEXT("Price", "Cena ($):"))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)[Form->Field(TEXT("price"), LOCTEXT("Dollars", "$"), 120.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			// Who buys at the price typed, counted again as the player types
			SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)
			.Text_Lambda([WeakThis]()
			{
				TSharedPtr<SFWShopWindow> This = WeakThis.Pin();
				if (!This)
				{
					return FText::GetEmpty();
				}
				const double* Pays = This->Trader.Find(This->Form->Get(TEXT("item")));
				return Pays ? FText::FromString(FString::Printf(TEXT("kupi %d%% klientów"), FWShopText::BuyPercent(This->Form->Cents(TEXT("price")), *Pays)))
					: LOCTEXT("NobodyBuys", "Nikt tego nie kupi");
			})
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("Put", "Wystaw"), [WeakThis]()
			{
				if (TSharedPtr<SFWShopWindow> This = WeakThis.Pin())
				{
					This->Stock(This->Form->Get(TEXT("item")), FMath::Max(1, int32(This->Form->Number(TEXT("quantity")))), This->Form->Number(TEXT("price")));
				}
			}, true)
		], 8.f);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Klienci przychodzą sami: więcej w większym mieście i bliżej centrum. Po cenie handlarza kupuje każdy, po dwukrotnej połowa, po trzykrotnej nikt. Najwyżej %d rodzajów i %d sztuk na półkach."),
		int32(FWUi::Num(State, TEXT("maxKinds"), 8.0)), int32(FWUi::Num(State, TEXT("maxStock"), 1000.0)))), FWStyle::Muted), 12.f);
	return Page;
}

TSharedRef<SWidget> SFWShopWindow::MakeCanteen()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWShopWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Bonus = int32(FWUi::Num(State, TEXT("bonusPercent")));
	const bool bFed = FWUi::Bool(State, TEXT("fed"));
	const double FedUntil = FWUi::Num(State, TEXT("fedUntil"));
	Add(SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).AutoWrapText(true).ColorAndOpacity(bFed ? FWStyle::Accent : FWStyle::Muted)
		.Text_Lambda([WeakThis, bFed, FedUntil, Bonus]()
		{
			TSharedPtr<SFWShopWindow> This = WeakThis.Pin();
			return bFed && This ? FText::FromString(FString::Printf(TEXT("Załogi najedzone jeszcze %s: maszyny na opał i na prąd pracują o %d%% szybciej."),
				*FWShopText::Duration((FedUntil - This->ServerNow()) / 1000.0), Bonus))
				: LOCTEXT("Hungry", "Stołówka pusta: maszyny pracują zwykłym tempem.");
		}), 0.f);
	const int32 Crews = int32(FWUi::Num(State, TEXT("crews")));
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Karmi %d %s; porcja wystarcza jednej maszynie na %d min."), Crews,
		Crews == 1 ? TEXT("maszynę") : Crews < 5 ? TEXT("maszyny") : TEXT("maszyn"), int32(FWUi::Num(State, TEXT("portionMinutes"))))), FWStyle::Text), 8.f);

	Add(FWUi::Heading(LOCTEXT("Serve", "Wydaj jedzenie")), 16.f);
	bool bAny = false;
	TArray<FString> Names;
	for (const TSharedPtr<FJsonObject>& Dish : FWUi::Objects(State, TEXT("dishes")))
	{
		const FString Id = FWUi::Str(Dish, TEXT("id"));
		Names.Add(FWUi::Str(Dish, TEXT("name")).ToLower());
		const FFWStack* Have = PC ? PC->FindStack(Id) : nullptr;
		if (!Have || Have->Quantity <= 0)
		{
			continue;
		}
		bAny = true;
		const int32 Owned = Have->Quantity;
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 30.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s  ×%d  (%d porcji)"), *FWUi::Str(Dish, TEXT("name")), Owned, int32(FWUi::Num(Dish, TEXT("portions"))))))
			];
		for (const int32 Amount : { 1, 10, 0 })
		{
			const int32 Count = Amount == 0 ? Owned : Amount;
			Row->AddSlot().AutoWidth().Padding(6, 0, 0, 0)
			[
				FWUi::SmallButton(Amount == 0 ? LOCTEXT("All", "Wszystko") : FText::FromString(FString::Printf(TEXT("+%d"), Amount)), [WeakThis, Id, Count]()
				{
					if (TSharedPtr<SFWShopWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("canteen"), This->Object);
						P->SetStringField(TEXT("item"), Id);
						P->SetNumberField(TEXT("quantity"), Count);
						This->Act(TEXT("canteen.load"), P);
					}
				}, Amount == 0, Count <= Owned)
			];
		}
		Add(Row, 4.f);
	}
	if (!bAny)
	{
		Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Nie masz dań, które stołówka wydaje (%s)."), *FString::Join(Names, TEXT(", ")))), FWStyle::Muted), 4.f);
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Zapas wystarcza najwyżej na %d h. Podajnik obok stołówki sam doniesie dania z taśmy albo skrzyni. Jedna stołówka na działkę."),
		int32(FWUi::Num(State, TEXT("maxHours"))))), FWStyle::Muted), 14.f);
	return Page;
}

void SFWShopWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() >= NextPoll)
	{
		Load(true);
	}
}

FReply SFWShopWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
