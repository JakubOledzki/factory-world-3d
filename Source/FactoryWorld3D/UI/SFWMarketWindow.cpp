#include "UI/SFWMarketWindow.h"

#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
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

	FString Str(const FJsonObject& Object, const TCHAR* Field)
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
		return SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ButtonColorAndOpacity_Lambda([this, Index]() { return Tab == Index ? FLinearColor(4.f, 2.2f, 0.5f, 1.f) : FLinearColor::White; })
			.OnClicked_Lambda([this, Index]() { ShowTab(Index); return FReply::Handled(); })
			[
				SNew(STextBlock).Text(Text).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			];
	};
	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot()[SNew(SBackgroundBlur).BlurStrength(4.f)]
		+ SOverlay::Slot()[SNew(SBorder).BorderImage(FWStyle::SideShade())]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SBorder)
			.BorderImage(FWStyle::Panel())
			.Padding(24.f)
			[
				SNew(SBox)
				.WidthOverride(880.f)
				.HeightOverride(720.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 14)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
						[
							SNew(STextBlock).Text(LOCTEXT("Title", "Rynek")).Font(FWStyle::Font(24, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
						]
						+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
						[
							SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
							.Text_Lambda([this]() { return Controller.IsValid() ? Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); })
						]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 12)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[TabButton(0, LOCTEXT("Trader", "Handlarz"))]
						+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[TabButton(1, LOCTEXT("Exchange", "Giełda graczy"))]
						+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 18, 0)[TabButton(2, LOCTEXT("Mine", "Moje oferty"))]
						+ SHorizontalBox::Slot().FillWidth(1.f)
						[
							SAssignNew(Search, SEditableTextBox)
							.Style(&FWStyle::TextBox())
							.HintText(LOCTEXT("Search", "Szukaj przedmiotu…"))
							.OnTextChanged_Lambda([this](const FText& Text) { Filter = Text.ToString(); Rebuild(); })
						]
					]
					+ SVerticalBox::Slot().FillHeight(1.f)
					[
						SAssignNew(Content, SBox)
					]
				]
			]
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0.f, 0.f, 0.f, 32.f)
		[
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("Close", "Esc zamyka"))
		]
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
	PC->RefreshInventory();
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

bool SFWMarketWindow::Matches(const FString& Name) const
{
	return Filter.IsEmpty() || Name.Contains(Filter, ESearchCase::IgnoreCase);
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
	if (Quantity <= 0)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	Send(bBuy ? TEXT("market.buy") : TEXT("market.sell"), P);
}

void SFWMarketWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	Content->SetContent(Tab == 0 ? MakeTrader() : Tab == 1 ? MakeExchange() : MakeMine());
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
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(8.f, 5.f))
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
					.Text(Stack && Stack->Quantity > 0 ? FText::Format(LOCTEXT("Have", "masz {0}"), Stack->Quantity) : FText::GetEmpty())
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right]
		];
}

TSharedRef<SWidget> SFWMarketWindow::MakeTrader()
{
	TSharedRef<SScrollBox> List = SNew(SScrollBox);
	if (Prices.IsValid())
	{
		if (const TSharedPtr<FJsonObject>* Event = nullptr; Prices->TryGetObjectField(TEXT("event"), Event) && Event && Event->IsValid())
		{
			List->AddSlot().Padding(0, 0, 0, 8)
			[
				SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
				[
					SNew(STextBlock).AutoWrapText(true).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Accent)
					.Text(FText::Format(LOCTEXT("Event", "{0}: {1}"), FText::FromString(Str(**Event, TEXT("name"))), FText::FromString(Str(**Event, TEXT("description")))))
				]
			];
		}
		for (const TSharedPtr<FJsonObject>& Item : Objects(Prices, TEXT("items")))
		{
			const FString Id = Str(*Item, TEXT("id"));
			const FString Name = Str(*Item, TEXT("name"));
			if (!Matches(Name))
			{
				continue;
			}
			double Buy = 0.0, Sell = 0.0;
			const bool bBuys = Num(*Item, TEXT("buyCents"), Buy);
			const bool bSells = Num(*Item, TEXT("sellCents"), Sell);
			const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Id) : nullptr;
			const int32 Have = Stack ? Stack->Quantity : 0;
			const int64 Balance = Controller.IsValid() ? Controller->GetBalanceCents() : 0;
			TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
			// Buying from the trader
			Right->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)
			[
				SNew(SBox).WidthOverride(140.f).HAlign(HAlign_Right)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
						.Text(bBuys ? FText::Format(LOCTEXT("BuyAt", "kup {0}"), Money(Buy)) : FText::GetEmpty())
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(4, 0, 0, 0)[Change(*Item, TEXT("buyChange"), false)]
				]
			];
			Right->AddSlot().AutoWidth().Padding(2, 0)[SmallButton(LOCTEXT("Buy1", "1"), [this, Id]() { Trade(true, Id, 1); }, false, bBuys && Balance >= Buy)];
			Right->AddSlot().AutoWidth().Padding(2, 0, 14, 0)[SmallButton(LOCTEXT("Buy10", "10"), [this, Id]() { Trade(true, Id, 10); }, false, bBuys && Balance >= Buy * 10)];
			// Selling to the trader
			Right->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)
			[
				SNew(SBox).WidthOverride(140.f).HAlign(HAlign_Right)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
						.Text(bSells ? FText::Format(LOCTEXT("SellAt", "sprzedaj {0}"), Money(Sell)) : FText::GetEmpty())
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(4, 0, 0, 0)[Change(*Item, TEXT("sellChange"), true)]
				]
			];
			Right->AddSlot().AutoWidth().Padding(2, 0)[SmallButton(LOCTEXT("Sell1", "1"), [this, Id]() { Trade(false, Id, 1); }, false, bSells && Have >= 1)];
			Right->AddSlot().AutoWidth().Padding(2, 0)[SmallButton(LOCTEXT("SellAll", "wszystko"), [this, Id, Have]() { Trade(false, Id, Have); }, true, bSells && Have >= 1)];
			List->AddSlot().Padding(0, 0, 8, 4)[ItemRow(Id, Name, Right)];
		}
	}
	return List;
}

TSharedRef<SWidget> SFWMarketWindow::MakeExchange()
{
	// The items, left; the chosen one's board of offers, right
	TSharedRef<SScrollBox> Items = SNew(SScrollBox);
	for (const TSharedPtr<FJsonObject>& Item : Objects(Overview, TEXT("items")))
	{
		const FString Id = Str(*Item, TEXT("id"));
		const FString Name = Str(*Item, TEXT("name"));
		double Listed = 0.0, Wanted = 0.0, Lowest = 0.0;
		Num(*Item, TEXT("listed"), Listed);
		Num(*Item, TEXT("wanted"), Wanted);
		const bool bLowest = Num(*Item, TEXT("lowestCents"), Lowest);
		// Busy items first would need sorting; a filter by name is enough here, and empty ones only when searched
		if (!Matches(Name) || (Filter.IsEmpty() && Listed <= 0.0 && Wanted <= 0.0 && !(Controller.IsValid() && Controller->FindStack(Id))))
		{
			continue;
		}
		Items->AddSlot().Padding(0, 0, 6, 4)
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ButtonColorAndOpacity(Id == BoardItem ? FLinearColor(4.f, 2.2f, 0.5f, 1.f) : FLinearColor::White)
			.OnClicked_Lambda([this, Id]() { OpenItem(Id); return FReply::Handled(); })
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)[SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
					.Text(bLowest ? FText::Format(LOCTEXT("From", "od {0}"), Money(Lowest)) : FText::GetEmpty())
				]
			]
		];
	}
	TSharedRef<SVerticalBox> BoardBox = SNew(SVerticalBox);
	if (BoardItem.IsEmpty())
	{
		BoardBox->AddSlot().AutoHeight()[SNew(STextBlock).AutoWrapText(true).Text(LOCTEXT("Pick", "Wybierz przedmiot po lewej (wpisz nazwę, żeby zobaczyć wszystkie). Tu zobaczysz oferty innych graczy i wystawisz swoją.")).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)];
	}
	else
	{
		const FString Name = Board.IsValid() ? Str(*Board, TEXT("name")) : BoardItem;
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 10)[SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Font(18, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)];
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
				const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(BoardItem) : nullptr;
				const int32 Take = bListings ? int32(Quantity) : FMath::Min(int32(Quantity), Stack ? Stack->Quantity : 0);
				Box->AddSlot().AutoHeight().Padding(0, 0, 0, 3)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(bMine ? FWStyle::Accent : FWStyle::Text)
						.Text(FText::Format(LOCTEXT("Offer", "{0} szt. × {1}  ·  {2}"), int32(Quantity), Money(Price), FText::FromString(Str(*Offer, TEXT("owner")))))
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
				];
			}
			if (Objects(Board, Field).IsEmpty())
			{
				Box->AddSlot().AutoHeight()[SNew(STextBlock).Text(LOCTEXT("NoOffers", "brak")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)];
			}
			return Box;
		};
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 4)[SNew(STextBlock).Text(LOCTEXT("Selling", "Sprzedają")).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Muted)];
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 12)[Offers(TEXT("listings"), true)];
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 4)[SNew(STextBlock).Text(LOCTEXT("Buying", "Kupują")).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Muted)];
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 16)[Offers(TEXT("orders"), false)];
		// The player's own offer: how many, at what price each, to sell or to buy
		auto Field = [this](const FText& Hint, FString* Value)
		{
			return SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(Hint).Text(FText::FromString(*Value))
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
		BoardBox->AddSlot().AutoHeight().Padding(0, 0, 0, 6)[SNew(STextBlock).Text(LOCTEXT("Yours", "Twoja oferta")).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Muted)];
		BoardBox->AddSlot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)[Field(LOCTEXT("Qty", "sztuk"), &OfferQuantity)]
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)[Field(LOCTEXT("Price", "cena za sztukę ($)"), &OfferPrice)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[SmallButton(LOCTEXT("List", "Wystaw"), [Offer]() { Offer(true); }, true)]
			+ SHorizontalBox::Slot().AutoWidth()[SmallButton(LOCTEXT("Order", "Chcę kupić"), [Offer]() { Offer(false); })]
		];
	}
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth()[SNew(SBox).WidthOverride(330.f)[Items]]
		+ SHorizontalBox::Slot().FillWidth(1.f).Padding(18, 0, 0, 0)[SNew(SScrollBox) + SScrollBox::Slot()[BoardBox]];
}

TSharedRef<SWidget> SFWMarketWindow::MakeMine()
{
	TSharedRef<SScrollBox> List = SNew(SScrollBox);
	auto Add = [this, &List](const TCHAR* Field, bool bListing)
	{
		for (const TSharedPtr<FJsonObject>& Offer : Objects(Mine, Field))
		{
			bool bOpen = false;
			Offer->TryGetBoolField(TEXT("open"), bOpen);
			double Id = 0.0, Quantity = 0.0, Done = 0.0, Price = 0.0;
			Num(*Offer, TEXT("id"), Id);
			Num(*Offer, TEXT("quantity"), Quantity);
			Num(*Offer, TEXT("done"), Done);
			Num(*Offer, TEXT("priceCents"), Price);
			const int64 OfferId = int64(Id);
			const FString Item = Str(*Offer, TEXT("item"));
			List->AddSlot().Padding(0, 0, 8, 4)
			[
				ItemRow(Item, Str(*Offer, TEXT("name")),
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(bOpen ? FWStyle::Text : FWStyle::Muted)
						.Text(FText::Format(bListing ? LOCTEXT("MyListing", "sprzedajesz {0}/{1} × {2}") : LOCTEXT("MyOrder", "kupujesz {0}/{1} × {2}"),
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
	};
	Add(TEXT("listings"), true);
	Add(TEXT("orders"), false);
	if (Objects(Mine, TEXT("listings")).IsEmpty() && Objects(Mine, TEXT("orders")).IsEmpty())
	{
		List->AddSlot()[SNew(STextBlock).Text(LOCTEXT("NoneMine", "Nie masz żadnych ofert.")).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)];
	}
	return List;
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
