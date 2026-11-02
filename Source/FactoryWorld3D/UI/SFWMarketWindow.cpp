#include "UI/SFWMarketWindow.h"

#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWMarket"

namespace
{
	FText Money(double Cents)
	{
		FNumberFormattingOptions Options;
		Options.MinimumFractionalDigits = 2;
		Options.MaximumFractionalDigits = 2;
		return FText::Format(LOCTEXT("Money", "{0} $"), FText::AsNumber(Cents / 100.0, &Options));
	}

	FString JsonStr(const FJsonObject& Object, const TCHAR* Field)
	{
		FString Value;
		Object.TryGetStringField(Field, Value);
		return Value;
	}

	bool Num(const FJsonObject& Object, const TCHAR* Field, double& Out)
	{
		return Object.TryGetNumberField(Field, Out);
	}

	TArray<TSharedPtr<FJsonObject>> Objects(const TSharedPtr<FJsonObject>& Object, const TCHAR* Field)
	{
		TArray<TSharedPtr<FJsonObject>> Result;
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Object.IsValid() && Object->TryGetArrayField(Field, Values))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				if (Value.IsValid() && Value->Type == EJson::Object)
				{
					Result.Add(Value->AsObject());
				}
			}
		}
		return Result;
	}

	// The market's groups of items, in the server's order (ItemCategories); what is in none goes under "other"
	struct FCategory
	{
		FString Id;
		FText Name;
	};

	const TArray<FCategory>& Categories()
	{
		static const TArray<FCategory> All =
		{
			{ TEXT("raw"), LOCTEXT("CatRaw", "Surowce") },
			{ TEXT("materials"), LOCTEXT("CatMaterials", "Materiały i wyroby") },
			{ TEXT("food"), LOCTEXT("CatFood", "Żywność i rolnictwo") },
			{ TEXT("fuel"), LOCTEXT("CatFuel", "Paliwa") },
			{ TEXT("machines"), LOCTEXT("CatMachines", "Maszyny i budynki") },
			{ TEXT("logistics"), LOCTEXT("CatLogistics", "Logistyka i magazyny") },
			{ TEXT("power"), LOCTEXT("CatPower", "Energia") },
			{ TEXT("equipment"), LOCTEXT("CatEquipment", "Narzędzia i ubrania") },
			{ TEXT("other"), LOCTEXT("CatOther", "Inne") },
		};
		return All;
	}

	const TCHAR* const MineCategory = TEXT("mine");

	FString CategoryOf(const FJsonObject& Item)
	{
		const FString Id = JsonStr(Item, TEXT("category"));
		return Categories().ContainsByPredicate([&Id](const FCategory& C) { return C.Id == Id; }) ? Id : FString(TEXT("other"));
	}

	// A change from the usual price, as the trader shows it: green when it favours the player
	TSharedRef<SWidget> Change(const FJsonObject& Item, const TCHAR* Field, bool bHighIsGood)
	{
		double Percent = 0.0;
		if (!Item.TryGetNumberField(Field, Percent) || FMath::Abs(Percent) < 1.0)
		{
			return SNullWidget::NullWidget;
		}
		const bool bGood = (Percent > 0.0) == bHighIsGood;
		return SNew(STextBlock).Font(FWStyle::Font(9, TEXT("Bold"))).ColorAndOpacity(bGood ? FWStyle::Good : FWStyle::Error)
			.Text(FText::Format(LOCTEXT("Percent", "{0}{1}%"), FText::FromString(Percent > 0.0 ? TEXT("+") : TEXT("")), FMath::RoundToInt(Percent)));
	}
}

void SFWMarketWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	auto TabButton = [this](int32 Index, const FText& Text)
	{
		return FWUi::TabButton(Text, [this, Index]() { return Tab == Index; }, [this, Index]() { ShowTab(Index); });
	};
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Rynek"),
			SNew(STextBlock).Font(FWStyle::Display(24)).ColorAndOpacity(FWStyle::Good)
			.Text_Lambda([this]() { return Controller.IsValid() ? Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); }),
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth()[TabButton(0, LOCTEXT("Trader", "Handlarz: od ręki"))]
			+ SHorizontalBox::Slot().AutoWidth()[TabButton(1, LOCTEXT("Exchange", "Giełda graczy: oferty"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 18, 0)[TabButton(2, LOCTEXT("Mine", "Moje oferty"))]
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SAssignNew(Search, SEditableTextBox)
				.Style(&FWStyle::TextBox())
				.HintText(LOCTEXT("Search", "Szukaj przedmiotu…"))
				.OnTextChanged_Lambda([this](const FText& Text) { Filter = Text.ToString(); ListTab = -1; Rebuild(); })
			],
			SAssignNew(Content, SBox), 1200.f, 780.f)
	];
	Load();
}

void SFWMarketWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWMarketWindow> WeakThis = SharedThis(this);
	auto Keep = [WeakThis](TSharedPtr<FJsonObject> SFWMarketWindow::* Field, const TCHAR* Expected)
	{
		return [WeakThis, Field, Expected](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWMarketWindow> This = WeakThis.Pin(); This && Reply.Type == Expected)
			{
				(*This).*Field = Reply.Payload;
				This->Rebuild();
			}
		};
	};
	PC->Send(TEXT("market.prices"), nullptr, Keep(&SFWMarketWindow::Prices, TEXT("market.prices")));
	PC->Send(TEXT("market.overview"), nullptr, Keep(&SFWMarketWindow::Overview, TEXT("market.overview")));
	PC->Send(TEXT("market.mine"), nullptr, Keep(&SFWMarketWindow::Mine, TEXT("market.mine")));
	if (!BoardItem.IsEmpty())
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetStringField(TEXT("item"), BoardItem);
		PC->Send(TEXT("market.item"), P, Keep(&SFWMarketWindow::Board, TEXT("market.item")));
	}
	// The counts on the rows come from the inventory: built again once it is here
	PC->RefreshInventory([WeakThis]()
	{
		if (TSharedPtr<SFWMarketWindow> This = WeakThis.Pin())
		{
			This->Rebuild();
		}
	});
	Rebuild();
}

void SFWMarketWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	Rebuild();
}

void SFWMarketWindow::OpenItem(const FString& Item)
{
	Tab = 1;
	BoardItem = Item;
	Board.Reset();
	OfferPrice.Reset();
	Load();
}

void SFWMarketWindow::ShowCategory(const FString& InCategory)
{
	Category = InCategory;
	ListTab = -1;
	Rebuild();
}

bool SFWMarketWindow::Matches(const FString& Name) const
{
	return Filter.IsEmpty() || Name.Contains(Filter, ESearchCase::IgnoreCase);
}

int32 SFWMarketWindow::Have(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	int32 Count = Stack ? Stack->Quantity : 0;
	for (const FPendingSale& Sale : PendingSales)
	{
		if (Sale.Item == Item)
		{
			Count -= Sale.Quantity;
		}
	}
	return FMath::Max(0, Count);
}

bool SFWMarketWindow::Has(const FString& Item) const
{
	return Have(Item) > 0;
}

bool SFWMarketWindow::InCategory(const FJsonObject& Item) const
{
	if (Category.IsEmpty())
	{
		return true;
	}
	if (Category == MineCategory)
	{
		return Has(JsonStr(Item, TEXT("id")));
	}
	return CategoryOf(Item) == Category;
}

TSharedRef<SWidget> SFWMarketWindow::Hint(const FText& Text)
{
	return SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(12.f, 8.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				SNew(SBox).WidthOverride(3.f).HeightOverride(30.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FWStyle::Info)]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).AutoWrapText(true).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text).Text(Text)
			]
		];
}

TSharedRef<SWidget> SFWMarketWindow::MakeCategories(const TSharedPtr<FJsonObject>& Source)
{
	// How many items each group has, as the search leaves them
	TMap<FString, int32> Counts;
	int32 AllCount = 0, MineCount = 0;
	for (const TSharedPtr<FJsonObject>& Item : Objects(Source, TEXT("items")))
	{
		if (!Matches(JsonStr(*Item, TEXT("name"))))
		{
			continue;
		}
		++AllCount;
		++Counts.FindOrAdd(CategoryOf(*Item));
		MineCount += Has(JsonStr(*Item, TEXT("id"))) ? 1 : 0;
	}
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox);
	auto Row = [this, &Box](const FString& Id, const FText& Name, int32 Count, float Gap)
	{
		const bool bEmpty = Count == 0 && Id != Category;
		Box->AddSlot().AutoHeight().Padding(0, 0, 0, Gap)
		[
			SNew(SBorder)
			.Padding(0)
			.BorderImage_Lambda([this, Id]() { return Category == Id ? &FWStyle::TabButton(true).Normal : FStyleDefaults::GetNoBrush(); })
			[
				SNew(SButton)
				.ButtonStyle(&FWStyle::TabButton(false))
				.ContentPadding(FMargin(10.f, 5.f))
				.OnClicked_Lambda([this, Id]() { ShowCategory(Id); return FReply::Handled(); })
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock).Text(Name).Font(FWStyle::Font(12, TEXT("SemiBold")))
						.ColorAndOpacity_Lambda([this, Id, bEmpty]() { return FSlateColor(Category == Id ? FWStyle::Accent : bEmpty ? FWStyle::Muted : FWStyle::Text); })
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)
					[
						SNew(STextBlock).MinDesiredWidth(26.f).Justification(ETextJustify::Right).Text(FText::AsNumber(Count)).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
					]
				]
			]
		];
	};
	Row(FString(), LOCTEXT("CatAll", "Wszystko"), AllCount, 2.f);
	Row(MineCategory, LOCTEXT("CatMine", "Masz w plecaku"), MineCount, 12.f);
	for (const FCategory& C : Categories())
	{
		Row(C.Id, C.Name, Counts.FindRef(C.Id), 2.f);
	}
	return SNew(SBox).WidthOverride(240.f)[SNew(SScrollBox) + SScrollBox::Slot()[Box]];
}

void SFWMarketWindow::Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWMarketWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWMarketWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.IsError())
		{
			Inner->ToastError(Reply);
			return;
		}
		double Balance = 0.0;
		if (Reply.Payload.IsValid() && Reply.Payload->TryGetNumberField(TEXT("balanceCents"), Balance))
		{
			Inner->SetBalanceCents(int64(Balance));
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		This->Load();
	});
}

void SFWMarketWindow::PutOffer(bool bSell, int32 Quantity, double PriceCents)
{
	if (BoardItem.IsEmpty() || Quantity <= 0 || PriceCents <= 0.0)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), BoardItem);
	P->SetNumberField(TEXT("quantity"), Quantity);
	P->SetNumberField(TEXT("priceCents"), FMath::RoundToDouble(PriceCents));
	Send(bSell ? TEXT("market.list") : TEXT("market.order"), P);
}

void SFWMarketWindow::Trade(bool bBuy, const FString& Item, int32 Quantity)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || Quantity <= 0)
	{
		return;
	}
	// A trade with the trader changes only the money and the inventory: no need to load the whole market again
	const int32 Seq = ++TradeSeq;
	if (!bBuy)
	{
		PendingSales.Add({ Seq, Item, Quantity });
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	TWeakPtr<SFWMarketWindow> WeakThis = SharedThis(this);
	PC->Send(bBuy ? TEXT("market.buy") : TEXT("market.sell"), P, [WeakThis, Seq](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWMarketWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.IsError())
		{
			This->PendingSales.RemoveAll([Seq](const FPendingSale& Sale) { return Sale.Seq == Seq; });
			This->Rebuild();
			Inner->ToastError(Reply);
			return;
		}
		double Balance = 0.0;
		if (Reply.Payload.IsValid() && Reply.Payload->TryGetNumberField(TEXT("balanceCents"), Balance))
		{
			Inner->SetBalanceCents(int64(Balance));
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
	});
	// Asked right behind the trade: the server answers one request at a time, in order, so this inventory has the trade in it
	PC->RefreshInventory([WeakThis, Seq]()
	{
		if (TSharedPtr<SFWMarketWindow> This = WeakThis.Pin())
		{
			This->PendingSales.RemoveAll([Seq](const FPendingSale& Sale) { return Sale.Seq <= Seq; });
			This->Rebuild();
		}
	});
	Rebuild();
}

void SFWMarketWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	// Built anew on every answer: the list stays where it was scrolled to, unless the tab changed
	const float Offset = List.IsValid() && ListTab == Tab ? List->GetScrollOffset() : 0.f;
	List.Reset();
	Content->SetContent(Tab == 0 ? MakeTrader() : Tab == 1 ? MakeExchange() : MakeMine());
	ListTab = Tab;
	if (List.IsValid() && Offset > 0.f)
	{
		List->SetScrollOffset(Offset);
	}
}

TSharedRef<SWidget> SFWMarketWindow::SmallButton(const FText& Text, TFunction<void()> OnClick, bool bPrimary, bool bEnabled)
{
	return SNew(SButton)
		.ButtonStyle(bPrimary ? &FWStyle::PrimaryButton() : &FWStyle::SecondaryButton())
		.ContentPadding(FMargin(8.f, 3.f))
		.IsEnabled(bEnabled)
		.OnClicked_Lambda([OnClick]() { OnClick(); return FReply::Handled(); })
		[
			SNew(STextBlock).Text(Text).Font(FWStyle::Font(10, bPrimary ? TEXT("Bold") : TEXT("Regular"))).ColorAndOpacity(bPrimary ? FLinearColor::White : FWStyle::Text)
		];
}

TSharedRef<SWidget> SFWMarketWindow::ItemRow(const FString& Item, const FString& Name, TSharedRef<SWidget> Right)
{
	const FSlateBrush* Icon = Controller.IsValid() ? Controller->GetIconBrush(Item) : nullptr;
	const int32 Count = Have(Item);
	return SNew(SFWCard).Padding(FMargin(8.f, 5.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
			[
				SNew(SBox).WidthOverride(30.f).HeightOverride(30.f)[SNew(SImage).Image(Icon).Visibility(Icon ? EVisibility::Visible : EVisibility::Hidden)]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()[SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Font(FWStyle::Font(9)).ColorAndOpacity(FWStyle::Muted)
					.Text(Count > 0 ? FText::Format(LOCTEXT("Have", "masz {0}"), Count) : FText::GetEmpty())
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right]
		];
}

TSharedRef<SWidget> SFWMarketWindow::MakeTrader()
{
	// The columns of a row: what the player pays the trader, and what the trader pays the player
	const float BuyWidth = 270.f, SellWidth = 300.f, ExchangeWidth = 84.f;
	// The scroll bar always takes its place, so the column headings stay over their columns
	TSharedRef<SScrollBox> Rows = SNew(SScrollBox).ScrollBarAlwaysVisible(true);
	List = Rows;
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			Hint(LOCTEXT("TraderHint", "Handlarz kupuje i sprzedaje od ręki, po swojej cenie: kliknij i gotowe. Chcesz dostać więcej niż płaci "
				"handlarz albo kupić taniej? Kliknij „Giełda” przy przedmiocie i wystaw własną ofertę dla innych graczy."))
		];
	if (Prices.IsValid())
	{
		if (const TSharedPtr<FJsonObject>* Event = nullptr; Prices->TryGetObjectField(TEXT("event"), Event) && Event && Event->IsValid())
		{
			Page->AddSlot().AutoHeight().Padding(0, 0, 0, 8)
			[
				SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
				[
					SNew(STextBlock).AutoWrapText(true).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Accent)
					.Text(FText::Format(LOCTEXT("Event", "{0}: {1}"), FText::FromString(JsonStr(**Event, TEXT("name"))), FText::FromString(JsonStr(**Event, TEXT("description")))))
				]
			];
		}
	}
	// Column headings, over the rows' price columns
	auto ColumnHeading = [](const FText& Text, const FLinearColor& Color, float Width)
	{
		return SNew(SBox).WidthOverride(Width)
			[
				SNew(STextBlock).Text(Text.ToUpper()).Font(FWStyle::Font(10, TEXT("Bold"))).ColorAndOpacity(Color)
			];
	};
	Page->AddSlot().AutoHeight().Padding(8, 0, 28, 4)
	[
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f).Padding(38, 0, 0, 0)[SNew(STextBlock).Text(LOCTEXT("ColItem", "PRZEDMIOT")).Font(FWStyle::Font(10, TEXT("Bold"))).ColorAndOpacity(FWStyle::Muted)]
		+ SHorizontalBox::Slot().AutoWidth()[ColumnHeading(LOCTEXT("ColBuy", "Kupujesz od handlarza"), FWStyle::Text, BuyWidth)]
		+ SHorizontalBox::Slot().AutoWidth()[ColumnHeading(LOCTEXT("ColSell", "Sprzedajesz handlarzowi"), FWStyle::Good, SellWidth)]
		+ SHorizontalBox::Slot().AutoWidth()[ColumnHeading(LOCTEXT("ColExchange", "Gracze"), FWStyle::Info, ExchangeWidth)]
	];
	Page->AddSlot().FillHeight(1.f)[Rows];

	auto AddItem = [this, &Rows, BuyWidth, SellWidth, ExchangeWidth](const FJsonObject& Item)
	{
		const FString Id = JsonStr(Item, TEXT("id"));
		const FString Name = JsonStr(Item, TEXT("name"));
		double Buy = 0.0, Sell = 0.0;
		const bool bBuys = Num(Item, TEXT("buyCents"), Buy);
		const bool bSells = Num(Item, TEXT("sellCents"), Sell);
		const int32 Count = Have(Id);
		const int64 Balance = Controller.IsValid() ? Controller->GetBalanceCents() : 0;
		auto Price = [](const FText& Text, const FLinearColor& Color, TSharedRef<SWidget> Change)
		{
			return SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(Color).Text(Text).MinDesiredWidth(112.f)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(4, 0, 6, 0)[Change];
		};
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		// Buying from the trader, and selling to it
		auto BuyCell = [&]() -> TSharedRef<SWidget>
		{
			if (!bBuys)
			{
				return SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("NotSold", "nie sprzedaje"));
			}
			return SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)[Price(Money(Buy), FWStyle::Text, Change(Item, TEXT("buyChange"), false))]
				+ SHorizontalBox::Slot().AutoWidth().Padding(2, 0)[SmallButton(LOCTEXT("Buy1", "1"), [this, Id]() { Trade(true, Id, 1); }, false, Balance >= Buy)]
				+ SHorizontalBox::Slot().AutoWidth().Padding(2, 0, 14, 0)[SmallButton(LOCTEXT("Buy10", "10"), [this, Id]() { Trade(true, Id, 10); }, false, Balance >= Buy * 10)];
		};
		auto SellCell = [&]() -> TSharedRef<SWidget>
		{
			if (!bSells)
			{
				return SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("NotBought", "nie skupuje"));
			}
			return SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)[Price(Money(Sell), FWStyle::Good, Change(Item, TEXT("sellChange"), true))]
				+ SHorizontalBox::Slot().AutoWidth().Padding(2, 0)[SmallButton(LOCTEXT("Sell1", "1"), [this, Id]() { Trade(false, Id, 1); }, false, Count >= 1)]
				+ SHorizontalBox::Slot().AutoWidth().Padding(2, 0, 14, 0)
				[
					SmallButton(LOCTEXT("SellAll", "wszystko"), [this, Id]() { Trade(false, Id, Have(Id)); }, true, Count >= 1)
				];
		};
		Right->AddSlot().AutoWidth()[SNew(SBox).WidthOverride(BuyWidth).VAlign(VAlign_Center)[BuyCell()]];
		Right->AddSlot().AutoWidth()[SNew(SBox).WidthOverride(SellWidth).VAlign(VAlign_Center)[SellCell()]];
		// The same item on the players' market
		Right->AddSlot().AutoWidth()
		[
			SNew(SBox).WidthOverride(ExchangeWidth).VAlign(VAlign_Center)
			[
				SNew(SButton)
				.ButtonStyle(&FWStyle::SecondaryButton())
				.ContentPadding(FMargin(8.f, 3.f))
				.ToolTipText(LOCTEXT("ToExchange", "Oferty innych graczy i wystawienie własnej"))
				.OnClicked_Lambda([this, Id]() { OpenItem(Id); return FReply::Handled(); })
				[
					SNew(STextBlock).Text(LOCTEXT("ExchangeButton", "Giełda")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Info)
				]
			]
		];
		Rows->AddSlot().Padding(0, 0, 8, 4)[ItemRow(Id, Name, Right)];
	};

	// Every item, under its group's heading; or only the chosen group
	const TArray<TSharedPtr<FJsonObject>> Items = Objects(Prices, TEXT("items"));
	auto IsShown = [this](const FJsonObject& Item) { return Matches(JsonStr(Item, TEXT("name"))) && InCategory(Item); };
	int32 Shown = 0;
	for (const FCategory& C : Categories())
	{
		bool bHeading = !Category.IsEmpty();
		for (const TSharedPtr<FJsonObject>& Item : Items)
		{
			if (CategoryOf(*Item) != C.Id || !IsShown(*Item))
			{
				continue;
			}
			if (!bHeading)
			{
				Rows->AddSlot().Padding(0, Shown > 0 ? 10 : 0, 8, 6)[FWUi::Heading(C.Name)];
				bHeading = true;
			}
			AddItem(*Item);
			++Shown;
		}
	}
	if (Prices.IsValid() && Shown == 0)
	{
		Rows->AddSlot()[FWUi::Note(Category == MineCategory ? LOCTEXT("TraderNoneMine", "Nie masz nic, co handlarz skupuje albo sprzedaje.") : LOCTEXT("TraderNone", "Nic tu nie pasuje."), FWStyle::Muted)];
	}
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 14, 0)[MakeCategories(Prices)]
		+ SHorizontalBox::Slot().FillWidth(1.f)[Page];
}

TSharedRef<SWidget> SFWMarketWindow::MakeExchange()
{
	// The groups, left; their items, in the middle; the chosen item's board of offers, right
	TSharedRef<SScrollBox> Items = SNew(SScrollBox);
	List = Items;
	double QuickSale = 0.0;
	bool bQuickSale = false;
	int32 Count = 0;
	for (const TSharedPtr<FJsonObject>& Item : Objects(Overview, TEXT("items")))
	{
		const FString Id = JsonStr(*Item, TEXT("id"));
		const FString Name = JsonStr(*Item, TEXT("name"));
		if (Id == BoardItem)
		{
			bQuickSale = Num(*Item, TEXT("quickSaleCents"), QuickSale);
		}
		double Listed = 0.0, Wanted = 0.0, Lowest = 0.0, Highest = 0.0;
		Num(*Item, TEXT("listed"), Listed);
		Num(*Item, TEXT("wanted"), Wanted);
		const bool bLowest = Num(*Item, TEXT("lowestCents"), Lowest);
		const bool bHighest = Num(*Item, TEXT("highestCents"), Highest);
		// Without a search or a group only what has offers or what the player has: the list of every item is long
		const bool bBrowsing = !Filter.IsEmpty() || !Category.IsEmpty();
		if (!Matches(Name) || !InCategory(*Item) || (!bBrowsing && Listed <= 0.0 && Wanted <= 0.0 && !Has(Id)))
		{
			continue;
		}
		++Count;
		FText Offers = FText::GetEmpty();
		if (bLowest && bHighest)
		{
			Offers = FText::Format(LOCTEXT("FromTo", "sprzedają od {0} · kupują za {1}"), Money(Lowest), Money(Highest));
		}
		else if (bLowest)
		{
			Offers = FText::Format(LOCTEXT("From", "sprzedają od {0}"), Money(Lowest));
		}
		else if (bHighest)
		{
			Offers = FText::Format(LOCTEXT("Wanted", "kupują za {0}"), Money(Highest));
		}
		else
		{
			Offers = LOCTEXT("NoOffersYet", "brak ofert");
		}
		Items->AddSlot().Padding(0, 0, 6, 4)
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ButtonColorAndOpacity(Id == BoardItem ? FLinearColor(4.f, 2.2f, 0.5f, 1.f) : FLinearColor::White)
			.OnClicked_Lambda([this, Id]() { OpenItem(Id); return FReply::Handled(); })
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Id, 26.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()[SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Text(Offers).Font(FWStyle::Font(9)).ColorAndOpacity(bLowest || bHighest ? FWStyle::Info : FWStyle::Muted)
					]
				]
			]
		];
	}
	if (Overview.IsValid() && Count == 0)
	{
		Items->AddSlot()[FWUi::Note(LOCTEXT("ExchangeEmpty", "Nikt teraz niczego nie wystawia. Wybierz grupę po lewej albo wpisz nazwę, żeby zobaczyć wszystkie przedmioty."), FWStyle::Muted)];
	}

	TSharedRef<SVerticalBox> BoardBox = SNew(SVerticalBox);
	if (BoardItem.IsEmpty())
	{
		BoardBox->AddSlot().AutoHeight()[FWUi::Note(LOCTEXT("Pick", "Wybierz przedmiot z listy. Tu zobaczysz, kto go sprzedaje i kto kupuje, i wystawisz własną ofertę."), FWStyle::Muted)];
	}
	else
	{
		const FString Name = Board.IsValid() ? JsonStr(*Board, TEXT("name")) : BoardItem;
		BoardBox->AddSlot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(Controller.Get(), BoardItem, 40.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Display(24)).ColorAndOpacity(FWStyle::Text)]
		];
		BoardBox->AddSlot().AutoHeight().Padding(0, 4, 0, 14)
		[
			SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
			.Text(FText::Format(LOCTEXT("BoardFacts", "Masz: {0} szt.   ·   Handlarz płaci od ręki: {1}"),
				Have(BoardItem), bQuickSale ? Money(QuickSale) : LOCTEXT("NoQuickSale", "nie skupuje")))
		];
		auto Offers = [this](const TCHAR* Field, bool bListings)
		{
			TSharedRef<SVerticalBox> Box = SNew(SVerticalBox);
			for (const TSharedPtr<FJsonObject>& Offer : Objects(Board, Field))
			{
				double Id = 0.0, Quantity = 0.0, Price = 0.0;
				Num(*Offer, TEXT("id"), Id);
				Num(*Offer, TEXT("quantity"), Quantity);
				Num(*Offer, TEXT("priceCents"), Price);
				bool bMine = false;
				Offer->TryGetBoolField(TEXT("mine"), bMine);
				const int64 OfferId = int64(Id);
				const int32 Take = bListings ? int32(Quantity) : FMath::Min(int32(Quantity), Have(BoardItem));
				Box->AddSlot().AutoHeight().Padding(0, 0, 0, 3)
				[
					SNew(SFWCard).Padding(FMargin(10.f, 4.f))
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
						[
							SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(bMine ? FWStyle::Accent : FWStyle::Text)
							.Text(FText::Format(LOCTEXT("Offer", "{0} szt. × {1}  ·  {2}"), int32(Quantity), Money(Price),
								bMine ? LOCTEXT("YourOffer", "twoja oferta") : FText::FromString(JsonStr(*Offer, TEXT("owner")))))
						]
						+ SHorizontalBox::Slot().AutoWidth()
						[
							SmallButton(bListings ? LOCTEXT("BuyOffer", "Kup") : LOCTEXT("FillOffer", "Sprzedaj"), [this, OfferId, Take, bListings]()
							{
								TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
								P->SetNumberField(TEXT("id"), double(OfferId));
								P->SetNumberField(TEXT("quantity"), Take);
								Send(bListings ? TEXT("market.purchase") : TEXT("market.fill"), P);
							}, true, !bMine && Take > 0)
						]
					]
				];
			}
			if (Objects(Board, Field).IsEmpty())
			{
				Box->AddSlot().AutoHeight()[SNew(STextBlock).Text(LOCTEXT("NoOffers", "brak ofert")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)];
			}
			return Box;
		};
		auto Section = [&BoardBox](const FText& Title, const FText& Explain)
		{
			BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 2)[FWUi::Heading(Title)];
			BoardBox->AddSlot().AutoHeight().Padding(11, 0, 0, 6)[SNew(STextBlock).AutoWrapText(true).Text(Explain).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)];
		};
		Section(LOCTEXT("Selling", "Sprzedają"), LOCTEXT("SellingHint", "Oferty sprzedaży innych graczy: tu kupujesz."));
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 14)[Offers(TEXT("listings"), true)];
		Section(LOCTEXT("Buying", "Kupują"), LOCTEXT("BuyingHint", "Gracze, którzy chcą kupić: tu sprzedajesz to, co masz w plecaku."));
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 16)[Offers(TEXT("orders"), false)];
		// The player's own offer: how many, at what price each, to sell or to buy
		auto Field = [this](const FText& Placeholder, FString* Value)
		{
			return SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(Placeholder).Text(FText::FromString(*Value))
				.OnTextChanged_Lambda([Value](const FText& Text) { *Value = Text.ToString(); });
		};
		auto Offer = [this](bool bSell)
		{
			const int32 Quantity = FCString::Atoi(*OfferQuantity);
			const double Price = FCString::Atod(*OfferPrice.Replace(TEXT(","), TEXT(".")));
			if (Quantity <= 0 || Price <= 0.0)
			{
				if (AFWPlayerController* PC = Controller.Get())
				{
					PC->Toast(LOCTEXT("BadOffer", "Podaj liczbę sztuk i cenę za sztukę."), true);
				}
				return;
			}
			PutOffer(bSell, Quantity, Price * 100.0);
		};
		Section(LOCTEXT("Yours", "Wystaw własną ofertę"), LOCTEXT("YoursHint",
			"„Wystaw na sprzedaż”: sztuki od razu opuszczają plecak i czekają na kupca. „Chcę kupić”: pieniądze czekają, aż ktoś sprzeda. "
			"Ofertę wycofasz w „Moich ofertach”, a to, co nie zeszło, wróci. Giełda bierze 3% od każdej sprzedaży."));
		BoardBox->AddSlot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)[Field(LOCTEXT("Qty", "sztuk"), &OfferQuantity)]
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)[Field(LOCTEXT("Price", "cena/szt. ($)"), &OfferPrice)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[SmallButton(LOCTEXT("List", "Wystaw na sprzedaż"), [Offer]() { Offer(true); }, true)]
			+ SHorizontalBox::Slot().AutoWidth()[SmallButton(LOCTEXT("Order", "Chcę kupić"), [Offer]() { Offer(false); })]
		];
	}
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			Hint(LOCTEXT("ExchangeHint", "Giełda to handel z innymi graczami: sami ustalacie ceny. Wybierz przedmiot, a zobaczysz, kto go sprzedaje "
				"i kto kupuje, albo wystaw własną ofertę. Sprzedaż od ręki, bez czekania, jest u handlarza."))
		]
		+ SVerticalBox::Slot().FillHeight(1.f)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 14, 0)[MakeCategories(Overview)]
			+ SHorizontalBox::Slot().AutoWidth()[SNew(SBox).WidthOverride(300.f)[Items]]
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(18, 0, 0, 0)[SNew(SScrollBox) + SScrollBox::Slot()[BoardBox]]
		];
}

TSharedRef<SWidget> SFWMarketWindow::MakeMine()
{
	TSharedRef<SScrollBox> Rows = SNew(SScrollBox);
	List = Rows;
	auto Add = [this, &Rows](const TCHAR* Field, bool bListing)
	{
		const TArray<TSharedPtr<FJsonObject>> Offers = Objects(Mine, Field);
		Rows->AddSlot().Padding(0, 4, 8, 2)
		[
			FWUi::Heading(bListing ? LOCTEXT("MineSelling", "Sprzedajesz") : LOCTEXT("MineBuying", "Kupujesz"))
		];
		Rows->AddSlot().Padding(11, 0, 8, 6)
		[
			SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
			.Text(bListing ? LOCTEXT("MineSellingHint", "Twoje oferty sprzedaży na giełdzie: czekają, aż ktoś kupi.")
				: LOCTEXT("MineBuyingHint", "Twoje oferty kupna na giełdzie: czekają, aż ktoś sprzeda."))
		];
		for (const TSharedPtr<FJsonObject>& Offer : Offers)
		{
			bool bOpen = false;
			Offer->TryGetBoolField(TEXT("open"), bOpen);
			double Id = 0.0, Quantity = 0.0, Done = 0.0, Price = 0.0;
			Num(*Offer, TEXT("id"), Id);
			Num(*Offer, TEXT("quantity"), Quantity);
			Num(*Offer, TEXT("done"), Done);
			Num(*Offer, TEXT("priceCents"), Price);
			const int64 OfferId = int64(Id);
			const FString Item = JsonStr(*Offer, TEXT("item"));
			Rows->AddSlot().Padding(0, 0, 8, 4)
			[
				ItemRow(Item, JsonStr(*Offer, TEXT("name")),
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(bOpen ? FWStyle::Text : FWStyle::Muted)
						.Text(FText::Format(bListing ? LOCTEXT("MyListing", "sprzedane {0} z {1} po {2}") : LOCTEXT("MyOrder", "kupione {0} z {1} po {2}"),
							int32(Done), int32(Quantity), Money(Price)))
					]
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SmallButton(bOpen ? LOCTEXT("Withdraw", "Wycofaj") : LOCTEXT("Closed", "zamknięta"), [this, OfferId, bListing]()
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetNumberField(TEXT("id"), double(OfferId));
							Send(bListing ? TEXT("market.unlist") : TEXT("market.cancelOrder"), P);
						}, false, bOpen)
					])
			];
		}
		if (Offers.IsEmpty())
		{
			Rows->AddSlot().Padding(11, 0, 8, 10)[SNew(STextBlock).Text(LOCTEXT("NoneMine", "brak")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
		}
	};
	Add(TEXT("listings"), true);
	Add(TEXT("orders"), false);
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			Hint(LOCTEXT("MineHint", "Twoje oferty na giełdzie graczy. Wycofana oferta zwraca to, co nie zeszło: sztuki do plecaka, pieniądze do portfela."))
		]
		+ SVerticalBox::Slot().FillHeight(1.f)[Rows];
}

FReply SFWMarketWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
