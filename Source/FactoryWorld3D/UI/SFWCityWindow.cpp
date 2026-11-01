#include "UI/SFWCityWindow.h"

#include "FWPlayerController.h"
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

#define LOCTEXT_NAMESPACE "FWCity"

namespace FWCityText
{
	const TCHAR* const Tabs[] = { TEXT("Potrzeby"), TEXT("Przetarg"), TEXT("Rozwój"), TEXT("Wysypisko"), TEXT("Usługi"), TEXT("Ratusz"), TEXT("Wybory"), TEXT("Kasa") };
	enum ETab { Needs, Tender, Growth, Trash, Services, Hall, Election, Treasury };

	// Tabs only a city plot has; the rest of the county has the hall, the election, the treasury and the tender
	bool CityOnly(int32 Tab)
	{
		return Tab == Needs || Tab == Trash || Tab == Services;
	}

	FText Left(double At)
	{
		const int64 Seconds = FMath::Max<int64>(0, int64((At - FWUi::LocalNowMs()) / 1000.0));
		const int64 Days = Seconds / 86400, Hours = Seconds % 86400 / 3600;
		if (Days > 0)
		{
			return FText::Format(LOCTEXT("Days", "{0} {1} {2} h"), Days, Days == 1 ? LOCTEXT("Day", "dzień") : LOCTEXT("DaysWord", "dni"), Hours);
		}
		return FText::Format(LOCTEXT("Hours", "{0} h {1} min"), Hours, Seconds % 3600 / 60);
	}

	// The wallet as the server has it now (money moved by this window)
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

	TSharedRef<SWidget> Line(const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right = nullptr, AFWPlayerController* PC = nullptr, const FString& Item = FString())
	{
		return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, Item.IsEmpty() ? 0.f : 8.f, 0)[Item.IsEmpty() ? SNullWidget::NullWidget : FWUi::Icon(PC, Item, 30.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(Color).AutoWrapText(true)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
			];
	}
}

void SFWCityWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	if (const AFWPlayerController* PC = Controller.Get(); PC && PC->GetPlot())
	{
		Plot = FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
	}
	TSharedRef<SHorizontalBox> TabRow = SNew(SHorizontalBox);
	for (int32 i = 0; i < UE_ARRAY_COUNT(FWCityText::Tabs); ++i)
	{
		TabRow->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			SNew(SBox).Visibility_Lambda([this, i]()
			{
				const bool bInCity = FWUi::Bool(FWUi::Obj(State, TEXT("town")), TEXT("inCity")) || !State.IsValid();
				return bInCity || !FWCityText::CityOnly(i) ? EVisibility::Visible : EVisibility::Collapsed;
			})
			[
				FWUi::TabButton(FText::FromString(FWCityText::Tabs[i]), [this, i]() { return Tab == i; }, [this, i]() { ShowTab(i); })
			]
		];
	}
	TSharedRef<SWidget> Body = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			SAssignNew(Header, STextBlock).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).AutoWrapText(true)
		]
		+ SVerticalBox::Slot().FillHeight(1.f)[SAssignNew(Content, SBox)];
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Miasto"), Right, TabRow, Body, 1050.f, 800.f)
	];
	Load();
}

void SFWCityWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), Plot.X);
	P->SetNumberField(TEXT("y"), Plot.Y);
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("city.get"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
		{
			This->Apply(Reply);
		}
	});
	PC->RefreshInventory();
}

bool SFWCityWindow::Apply(const FFWNetReply& Reply)
{
	if (Reply.Type != TEXT("city"))
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->ToastError(Reply);
		}
		if (!State.IsValid() && Header.IsValid())
		{
			Header->SetText(LOCTEXT("NoTown", "Ta działka nie należy do żadnego miasta ani jego powiatu."));
		}
		return false;
	}
	State = FWUi::Obj(Reply.Payload, TEXT("state"));
	Rebuild();
	return true;
}

void SFWCityWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<FText(const TSharedPtr<FJsonObject>&)> Done)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetNumberField(TEXT("x"), Plot.X);
	Payload->SetNumberField(TEXT("y"), Plot.Y);
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, Done](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCityWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner || !This->Apply(Reply))
		{
			return;
		}
		if (Done)
		{
			Inner->Toast(Done(FWUi::Obj(Reply.Payload, TEXT("result"))));
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		Inner->RefreshInventory();
		FWCityText::Wallet(Inner);
	});
}

FText SFWCityWindow::BonusName(const FString& Bonus) const
{
	for (const TSharedPtr<FJsonObject>& Each : FWUi::Objects(FWUi::Obj(State, TEXT("hall")), TEXT("bonuses")))
	{
		if (FWUi::Str(Each, TEXT("id")) == Bonus)
		{
			return FText::FromString(FWUi::Str(Each, TEXT("name")));
		}
	}
	return LOCTEXT("NoBonus", "żadna");
}

void SFWCityWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	if (Scroll.IsValid())
	{
		Scroll->SetScrollOffset(0.f);
	}
	Rebuild();
}

void SFWCityWindow::Rebuild()
{
	if (!State.IsValid() || !Content.IsValid())
	{
		return;
	}
	const TSharedPtr<FJsonObject> Town = FWUi::Obj(State, TEXT("town"));
	const bool bInCity = FWUi::Bool(Town, TEXT("inCity"));
	if (!bInCity && FWCityText::CityOnly(Tab))
	{
		Tab = FWCityText::Hall;
	}
	FNumberFormattingOptions One;
	One.MaximumFractionalDigits = 1;
	const double Distance = FWUi::Num(Town, TEXT("distance"));
	if (bInCity)
	{
		Header->SetText(FText::Format(LOCTEXT("CityHeader", "{0} · {1} · {2} mieszkańców · {3}"), FText::FromString(FWUi::Str(Town, TEXT("name"))),
			FText::FromString(FWUi::Str(Town, TEXT("levelName"))), FText::AsNumber(int64(FWUi::Num(Town, TEXT("population")))),
			Distance < 0.5 ? LOCTEXT("Centre", "w samym centrum") : FText::Format(LOCTEXT("FromCentre", "{0} działki od centrum (im bliżej, tym więcej klientów)"), FText::AsNumber(Distance, &One))));
	}
	else
	{
		Header->SetText(FText::Format(LOCTEXT("CountyHeader", "Powiat miasta {0} · {1} · {2} działki od centrum. Prawa burmistrza obowiązują tu tak samo jak w mieście."),
			FText::FromString(FWUi::Str(Town, TEXT("name"))), FText::FromString(FWUi::Str(Town, TEXT("levelName"))), FText::AsNumber(Distance, &One)));
	}
	TSharedPtr<SWidget> Page;
	switch (Tab)
	{
	case FWCityText::Tender: Page = MakeTender(); break;
	case FWCityText::Growth: Page = MakeGrowth(); break;
	case FWCityText::Trash: Page = MakeTrash(); break;
	case FWCityText::Services: Page = MakeServices(); break;
	case FWCityText::Hall: Page = MakeHall(); break;
	case FWCityText::Election: Page = MakeElection(); break;
	case FWCityText::Treasury: Page = MakeTreasury(); break;
	default: Page = MakeNeeds(); break;
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWCityWindow::MakeNeeds()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(FText::Format(LOCTEXT("NeedsNote", "Dziś miasto kupi to, czego potrzebuje, drożej niż handlarz. Jutro zechce czego innego (zostało {0})."),
		FWCityText::Left(FWUi::Num(State, TEXT("dayEndsAt")))), FWStyle::Muted), 0.f);
	for (const TSharedPtr<FJsonObject>& Need : FWUi::Objects(State, TEXT("needs")))
	{
		const FString Item = FWUi::Str(Need, TEXT("id"));
		const int32 Wanted = int32(FWUi::Num(Need, TEXT("wanted")));
		const int32 Delivered = int32(FWUi::Num(Need, TEXT("delivered")));
		const int32 Left = Wanted - Delivered;
		const FString Key = TEXT("need:") + Item;
		auto Deliver = [WeakThis, Item, Key](bool bFromStation)
		{
			return [WeakThis, Item, Key, bFromStation]()
			{
				TSharedPtr<SFWCityWindow> This = WeakThis.Pin();
				if (!This)
				{
					return;
				}
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetStringField(TEXT("item"), Item);
				P->SetNumberField(TEXT("quantity"), This->Form->Number(Key));
				P->SetBoolField(TEXT("fromStation"), bFromStation);
				This->Act(TEXT("city.deliver"), P, [](const TSharedPtr<FJsonObject>& Result)
				{
					const double Subsidy = FWUi::Num(Result, TEXT("subsidyCents"));
					return FText::Format(LOCTEXT("Bought", "Miasto kupiło {0} szt. za {1}{2}"), int32(FWUi::Num(Result, TEXT("quantity"))), FWUi::Money(FWUi::Num(Result, TEXT("cents"))),
						Subsidy > 0.0 ? FText::Format(LOCTEXT("Subsidy", " + dotacja państwa {0}"), FWUi::Money(Subsidy)) : FText::GetEmpty());
				});
			};
		};
		Add(FWCityText::Line(FText::Format(LOCTEXT("Need", "{0}\n{1} / {2} · {3} za sztukę"), FText::FromString(FWUi::Str(Need, TEXT("name"))), Delivered, Wanted,
			FWUi::Money(FWUi::Num(Need, TEXT("priceCents")))), FWStyle::Text,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(Key, FText::GetEmpty(), 70.f, FString::FromInt(FMath::Max(Left, 1)))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("FromInventory", "Z ekwipunku"), Deliver(false), true, Left > 0)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(SBox).ToolTipText(LOCTEXT("StationTip", "Prosto ze stacji kolejowej tej działki"))[FWUi::SmallButton(LOCTEXT("FromStation", "Ze stacji"), Deliver(true), false, Left > 0)]
			], Controller.Get(), Item), 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeTender()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> T = FWUi::Obj(State, TEXT("tender"));
	const int32 Quantity = int32(FWUi::Num(T, TEXT("quantity")));
	const double Cap = FWUi::Num(T, TEXT("capCents"));
	Add(FWCityText::Line(FText::Format(LOCTEXT("TenderHead", "{0} × {1} · najwyżej {2} za sztukę"), Quantity, FText::FromString(FWUi::Str(T, TEXT("name"))), FWUi::Money(Cap)),
		FWStyle::Accent, nullptr, Controller.Get(), FWUi::Str(T, TEXT("item"))), 0.f);
	const FString TenderState = FWUi::Str(T, TEXT("state"));
	const FString Winner = FWUi::Str(T, TEXT("winner"));
	if (TenderState == TEXT("open"))
	{
		const int32 DepositPercent = int32(FWUi::Num(T, TEXT("depositPercent")));
		Add(FWUi::Note(FText::Format(LOCTEXT("TenderOpen", "Miasto zamawia dużą partię. Właściciele działek w powiecie składają ukryte oferty: cenę za sztukę. Wygrywa najniższa (przy równych: wcześniejsza). Oferta kosztuje wadium {0}% wartości zamówienia, które wraca, gdy przegrasz albo dostarczysz całość; zwycięzca, który nie zdąży do końca tygodnia, traci je. Oferty do końca za {1}."),
			DepositPercent, FWCityText::Left(FWUi::Num(T, TEXT("closesAt")))), FWStyle::Muted), 8.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("Bids", "Złożone oferty: {0}"), int32(FWUi::Num(T, TEXT("bids")))), FWStyle::Text));
		const TSharedPtr<FJsonObject> Mine = FWUi::Obj(T, TEXT("myBid"));
		if (Mine.IsValid())
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("MyBid", "Twoja oferta: {0} za sztukę (wadium {1})"), FWUi::Money(FWUi::Num(Mine, TEXT("priceCents"))),
				FWUi::Money(FWUi::Num(Mine, TEXT("depositCents")))), FWStyle::Good));
		}
		if (FWUi::Bool(T, TEXT("canBid")))
		{
			const FString Start = FString::SanitizeFloat((Mine.IsValid() ? FWUi::Num(Mine, TEXT("priceCents")) : Cap) / 100.0);
			TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("PricePer", "Cena za sztukę $")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("tenderPrice"), FText::GetEmpty(), 110.f, Start)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
				[
					SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
					.Text_Lambda([WeakThis, Quantity, DepositPercent]()
					{
						TSharedPtr<SFWCityWindow> This = WeakThis.Pin();
						return This ? FText::Format(LOCTEXT("Deposit", "wadium {0}"), FWUi::Money(FMath::CeilToDouble(This->Form->Cents(TEXT("tenderPrice")) * Quantity * DepositPercent / 100.0))) : FText::GetEmpty();
					})
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
				[
					FWUi::SmallButton(Mine.IsValid() ? LOCTEXT("ChangeBid", "Zmień ofertę") : LOCTEXT("Bid", "Złóż ofertę"), [WeakThis]()
					{
						if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
						{
							const double Price = This->Form->Cents(TEXT("tenderPrice"));
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetNumberField(TEXT("priceCents"), Price);
							This->Act(TEXT("city.tenderBid"), P, [Price](const TSharedPtr<FJsonObject>&) { return FText::Format(LOCTEXT("BidMade", "Oferta złożona: {0} za sztukę"), FWUi::Money(Price)); });
						}
					}, true)
				];
			if (Mine.IsValid())
			{
				Row->AddSlot().AutoWidth()
				[
					FWUi::SmallButton(LOCTEXT("WithdrawBid", "Wycofaj"), [WeakThis]()
					{
						if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
						{
							This->Act(TEXT("city.tenderWithdraw"), MakeShared<FJsonObject>(), [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Withdrawn", "Oferta wycofana, wadium wróciło"); });
						}
					})
				];
			}
			Add(Row, 6.f);
		}
		else
		{
			Add(FWUi::Note(LOCTEXT("OnlyOwners", "Oferty składają tylko właściciele działek w tym mieście."), FWStyle::Muted));
		}
	}
	else if (TenderState == TEXT("won"))
	{
		const int32 Delivered = int32(FWUi::Num(T, TEXT("delivered")));
		Add(FWUi::Note(FText::Format(LOCTEXT("Won", "Przetarg wygrał {0}: {1} za sztukę."), FText::FromString(Winner), FWUi::Money(FWUi::Num(T, TEXT("priceCents")))), FWStyle::Text), 8.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("WonProgress", "Dostarczono {0} z {1}. Termin mija za {2}."), Delivered, Quantity, FWCityText::Left(FWUi::Num(T, TEXT("endsAt")))), FWStyle::Text));
		if (FWUi::Bool(T, TEXT("isWinner")))
		{
			auto Deliver = [WeakThis](bool bFromStation)
			{
				return [WeakThis, bFromStation]()
				{
					if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("quantity"), This->Form->Number(TEXT("tenderQuantity")));
						P->SetBoolField(TEXT("fromStation"), bFromStation);
						This->Act(TEXT("city.tenderDeliver"), P, [](const TSharedPtr<FJsonObject>& Result)
						{
							return FText::Format(LOCTEXT("TenderDelivered", "Dostarczono do przetargu {0} szt. za {1}"), int32(FWUi::Num(Result, TEXT("quantity"))), FWUi::Money(FWUi::Num(Result, TEXT("cents"))));
						});
					}
				};
			};
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("tenderQuantity"), FText::GetEmpty(), 80.f, FString::FromInt(FMath::Max(Quantity - Delivered, 1)))]
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("FromInventory2", "Z ekwipunku"), Deliver(false), true)]
				+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("FromStation2", "Ze stacji"), Deliver(true))], 6.f);
			Add(FWUi::Note(FText::Format(LOCTEXT("DepositBack", "Wadium {0} wróci po dostarczeniu całości."), FWUi::Money(FWUi::Num(T, TEXT("depositCents")))), FWStyle::Muted));
		}
	}
	else if (TenderState == TEXT("done"))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("TenderDone", "{0} dostarczył całość po {1} za sztukę. Nowy przetarg w przyszłym tygodniu."), FText::FromString(Winner),
			FWUi::Money(FWUi::Num(T, TEXT("priceCents")))), FWStyle::Good), 8.f);
	}
	else if (TenderState == TEXT("failed"))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("TenderFailed", "{0} nie zdążył dostarczyć całości ({1} z {2}) i stracił wadium."), FText::FromString(Winner),
			int32(FWUi::Num(T, TEXT("delivered"))), Quantity), FWStyle::Error), 8.f);
	}
	else
	{
		Add(FWUi::Note(LOCTEXT("NoBids", "Nikt nie złożył oferty w tym tygodniu. Nowy przetarg w przyszłym."), FWStyle::Muted), 8.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeGrowth()
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Town = FWUi::Obj(State, TEXT("town"));
	const int32 Level = int32(FWUi::Num(Town, TEXT("level")));
	const TArray<FString> LevelNames = FWUi::Strings(Town, TEXT("levelNames"));
	TArray<double> LevelCents;
	for (const TSharedPtr<FJsonValue>& Value : Town.IsValid() && Town->HasField(TEXT("levelCents")) ? Town->GetArrayField(TEXT("levelCents")) : TArray<TSharedPtr<FJsonValue>>())
	{
		LevelCents.Add(Value->AsNumber());
	}
	const double Delivered = FWUi::Num(Town, TEXT("deliveredCents"));
	Add(FWUi::Note(LOCTEXT("GrowthNote", "Miasto rośnie na wszystkim, co gracze mu dostarczą. Większe chce więcej i droższych rzeczy, płaci więcej, ma więcej śmieci i klientów w sklepach."), FWStyle::Muted), 0.f);
	if (LevelCents.IsValidIndex(Level + 1) && LevelNames.IsValidIndex(Level + 1))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("NextLevel", "{0} → {1}: {2} / {3}"), FText::FromString(LevelNames[Level]), FText::FromString(LevelNames[Level + 1]),
			FWUi::Money(Delivered), FWUi::Money(LevelCents[Level + 1])), FWStyle::Text), 8.f);
		Add(FWUi::Bar(float((Delivered - LevelCents[Level]) / FMath::Max(1.0, LevelCents[Level + 1] - LevelCents[Level])), FWStyle::Accent));
	}
	else if (LevelNames.IsValidIndex(Level))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("MaxLevel", "{0}: największe, jakie może być. Dostarczono {1}."), FText::FromString(LevelNames[Level]), FWUi::Money(Delivered)), FWStyle::Accent), 8.f);
	}
	Add(FWUi::Heading(LOCTEXT("Top", "Najwięcej dostarczyli")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Contributors = FWUi::Objects(State, TEXT("contributors"));
	if (Contributors.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("Nobody", "Jeszcze nikt."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& C : Contributors)
	{
		const bool bMine = FWUi::Bool(C, TEXT("mine"));
		Add(FWUi::Note(FText::Format(LOCTEXT("Contributor", "{0}{1}: {2}"), FText::FromString(FWUi::Str(C, TEXT("name"))), bMine ? LOCTEXT("You", " (ty)") : FText::GetEmpty(),
			FWUi::Money(FWUi::Num(C, TEXT("cents")))), bMine ? FWStyle::Accent : FWStyle::Text), 2.f);
	}
	Add(FWUi::Note(FText::Format(LOCTEXT("MyPart", "Twój wkład: {0}"), FWUi::Money(FWUi::Num(State, TEXT("myCents")))), FWStyle::Muted));
	// The chief supplier: who delivered the most last week holds the title this week and gets a little more
	const TSharedPtr<FJsonObject> Supplier = FWUi::Obj(State, TEXT("supplier"));
	const FString Chief = FWUi::Str(Supplier, TEXT("name"));
	const int32 Percent = int32(FWUi::Num(Supplier, TEXT("percent")));
	FText Title = FText::Format(LOCTEXT("Chief", "Główny dostawca tego tygodnia: {0}"), Chief.IsEmpty() ? LOCTEXT("NoOne", "nikt") : FText::FromString(Chief));
	if (FWUi::Bool(Supplier, TEXT("isMe")))
	{
		Title = FText::Format(LOCTEXT("ChiefMe", "{0} (ty! miasto płaci ci o {1}% więcej)"), Title, Percent);
	}
	Add(FWUi::Heading(Title), 16.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("ChiefNote", "Kto w tym tygodniu dostarczy najwięcej (co najmniej {0}), ten od następnego tygodnia nosi tytuł, a miasto płaci mu za dostawy o {1}% więcej. Koniec tygodnia za {2}."),
		FWUi::Money(FWUi::Num(Supplier, TEXT("minCents"))), Percent, FWCityText::Left(FWUi::Num(Supplier, TEXT("weekEndsAt")))), FWStyle::Muted));
	for (const TSharedPtr<FJsonObject>& R : FWUi::Objects(Supplier, TEXT("race")))
	{
		const bool bMine = FWUi::Bool(R, TEXT("mine"));
		Add(FWUi::Note(FText::Format(LOCTEXT("Race", "{0}. {1}{2}: {3}"), int32(FWUi::Num(R, TEXT("place"))), FText::FromString(FWUi::Str(R, TEXT("name"))),
			bMine ? LOCTEXT("You2", " (ty)") : FText::GetEmpty(), FWUi::Money(FWUi::Num(R, TEXT("cents")))), bMine ? FWStyle::Accent : FWStyle::Text), 2.f);
	}
	Add(FWUi::Note(FText::Format(LOCTEXT("MyWeek", "Twoje dostawy w tym tygodniu: {0}"), FWUi::Money(FWUi::Num(Supplier, TEXT("myCents")))), FWStyle::Muted));
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeTrash()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Trash = FWUi::Obj(State, TEXT("trash"));
	const int32 Left = int32(FWUi::Num(Trash, TEXT("perDay")) - FWUi::Num(Trash, TEXT("taken")));
	Add(FWUi::Note(FText::Format(LOCTEXT("TrashNote", "Miasto płaci {0} za każdy wywieziony śmieć. Śmieci trafiają na stację działki: posortuj je w sortowni miejskiej albo w recyklingu."),
		FWUi::Money(FWUi::Num(Trash, TEXT("feeCents")))), FWStyle::Muted), 0.f);
	Add(FWCityText::Line(FText::Format(LOCTEXT("TrashLeft", "Dziś do wywiezienia: {0} z {1}"), Left, int32(FWUi::Num(Trash, TEXT("perDay")))), FWStyle::Text,
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("trash"), FText::GetEmpty(), 70.f, FString::FromInt(FMath::Max(Left, 1)))]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("TakeTrash", "Wywieź na stację"), [WeakThis]()
			{
				if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetNumberField(TEXT("quantity"), This->Form->Number(TEXT("trash")));
					This->Act(TEXT("city.trash"), P, [](const TSharedPtr<FJsonObject>& Result)
					{
						return FText::Format(LOCTEXT("TrashTaken", "Wywieziono {0} śmieci na stację, miasto zapłaciło {1}"), int32(FWUi::Num(Result, TEXT("quantity"))), FWUi::Money(FWUi::Num(Result, TEXT("cents"))));
					});
				}
			}, true, Left > 0)
		], Controller.Get(), TEXT("trash")), 8.f);
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeServices()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("ServicesNote", "Biura graczy z tego miasta sprzedają swoje usługi. Właściciel dostaje zapłatę, burmistrz podatek."), FWStyle::Muted), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Services = FWUi::Objects(State, TEXT("services"));
	if (Services.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoServices", "Nikt w mieście nie ma jeszcze biura projektowego, laboratorium ani warsztatu serwisowego."), FWStyle::Muted), 8.f);
	}
	for (const TSharedPtr<FJsonObject>& S : Services)
	{
		const FString Id = FWUi::Str(S, TEXT("id"));
		const bool bOwn = FWUi::Bool(S, TEXT("own"));
		const int32 Left = int32(FWUi::Num(S, TEXT("left")));
		Add(FWCityText::Line(FText::Format(LOCTEXT("Service", "{0} ({1})\n{2}× {3} za {4} · dziś jeszcze {5}"), FText::FromString(FWUi::Str(S, TEXT("name"))),
			FText::FromString(FWUi::Str(S, TEXT("owner"))), int32(FWUi::Num(S, TEXT("quantity"))), FText::FromString(FWUi::Str(S, TEXT("itemName"))),
			FWUi::Money(FWUi::Num(S, TEXT("priceCents"))), Left), FWStyle::Text,
			FWUi::SmallButton(bOwn ? LOCTEXT("Own", "Twoje") : LOCTEXT("Buy", "Kup"), [WeakThis, Id]()
			{
				if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("id"), Id);
					This->Act(TEXT("city.service"), P, [](const TSharedPtr<FJsonObject>& Result)
					{
						return FText::Format(LOCTEXT("ServiceBought", "Kupiono: {0}× {1}"), int32(FWUi::Num(Result, TEXT("quantity"))), FText::FromString(FWUi::Str(Result, TEXT("name"))));
					});
				}
			}, true, !bOwn && Left > 0), Controller.Get(), FWUi::Str(S, TEXT("item"))), 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeHall()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	auto Simple = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P, const FText& Toast)
	{
		return [WeakThis, Type, P, Toast]()
		{
			if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
			{
				This->Act(Type, P.IsValid() ? P : MakeShared<FJsonObject>(), [Toast](const TSharedPtr<FJsonObject>&) { return Toast; });
			}
		};
	};
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Hall = FWUi::Obj(State, TEXT("hall"));
	const FString Mayor = FWUi::Str(Hall, TEXT("mayor"));
	const bool bMayor = FWUi::Bool(Hall, TEXT("isMayor"));
	const bool bCanVote = FWUi::Bool(Hall, TEXT("canVote"));
	Add(FWUi::Heading(FText::Format(LOCTEXT("Mayor", "Burmistrz tego tygodnia: {0}"), Mayor.IsEmpty() ? LOCTEXT("NoMayor", "brak (nikt nie głosował)") : FText::FromString(Mayor))), 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("Taxes", "Podatek od sklepów i usług: {0}% · Od działek powiatu: {1}‰ dziennie · Premia: {2}"), int32(FWUi::Num(Hall, TEXT("taxPercent"))),
		int32(FWUi::Num(Hall, TEXT("plotTaxPerMille"))), BonusName(FWUi::Str(Hall, TEXT("bonus")))), FWStyle::Text));
	TArray<TPair<FString, FText>> Bonuses = { { FString(), LOCTEXT("WithoutBonus", "Bez premii") } };
	for (const TSharedPtr<FJsonObject>& B : FWUi::Objects(Hall, TEXT("bonuses")))
	{
		Bonuses.Add({ FWUi::Str(B, TEXT("id")), FText::FromString(FWUi::Str(B, TEXT("name"))) });
	}
	if (bMayor)
	{
		if (!Form->Has(TEXT("policyBonus")))
		{
			Form->Set(TEXT("policyBonus"), FWUi::Str(Hall, TEXT("bonus")));
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("TaxPercent", "Podatek %")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Field(TEXT("policyTax"), FText::GetEmpty(), 60.f, FString::FromInt(int32(FWUi::Num(Hall, TEXT("taxPercent")))))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Pick(TEXT("policyBonus"), Bonuses, 260.f)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("PlotTax", "Od działek ‰")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Field(TEXT("policyPlotTax"), FText::GetEmpty(), 60.f, FString::FromInt(int32(FWUi::Num(Hall, TEXT("plotTaxPerMille")))))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Set", "Ustaw"), [WeakThis]()
				{
					if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("taxPercent"), This->Form->Number(TEXT("policyTax")));
						P->SetStringField(TEXT("bonus"), This->Form->Get(TEXT("policyBonus")));
						P->SetNumberField(TEXT("plotTaxPerMille"), This->Form->Number(TEXT("policyPlotTax")));
						This->Act(TEXT("city.policy"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("PolicySet", "Ustawiono podatki i premię miasta na ten tydzień"); });
					}
				}, true)
			], 8.f);
	}
	// A festival with fine dishes from the mayor's inventory: the town pays more for a day
	double FestivalUntil = 0.0;
	if (Hall.IsValid() && Hall->TryGetNumberField(TEXT("festivalUntil"), FestivalUntil))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("Festival", "Trwa festyn: miasto płaci o {0}% więcej jeszcze {1}."), int32(FWUi::Num(Hall, TEXT("festivalPercent"))), FWCityText::Left(FestivalUntil)), FWStyle::Good), 8.f);
	}
	else if (bMayor)
	{
		Add(FWCityText::Line(FText::Format(LOCTEXT("FestivalNote", "Festyn: {0} × pieczeń lub sernik z ekwipunku, a przez {1} h miasto płaci o {2}% więcej."), int32(FWUi::Num(Hall, TEXT("festivalDishes"))),
			int32(FWUi::Num(Hall, TEXT("festivalHours"))), int32(FWUi::Num(Hall, TEXT("festivalPercent")))), FWStyle::Text,
			FWUi::SmallButton(LOCTEXT("Throw", "Wyprawiaj festyn"), Simple(TEXT("city.festival"), nullptr, LOCTEXT("FestivalOn", "Festyn trwa! Miasto płaci więcej przez całą dobę.")), true)), 8.f);
	}
	const TSharedPtr<FJsonObject> Politics = FWUi::Obj(Hall, TEXT("politics"));
	if (FWUi::Bool(Politics, TEXT("recalled")))
	{
		Add(FWUi::Note(LOCTEXT("Recalled", "Mieszkańcy odwołali burmistrza w tym tygodniu."), FWStyle::Error), 8.f);
	}
	else if (!Mayor.IsEmpty())
	{
		const TSharedPtr<FJsonObject> Recall = FWUi::Obj(Politics, TEXT("recall"));
		Add(FWCityText::Line(FText::Format(LOCTEXT("Recall", "Wotum nieufności: {0} z {1} podpisów (2/3 mieszkańców)"), int32(FWUi::Num(Recall, TEXT("signatures"))), int32(FWUi::Num(Recall, TEXT("needed")))),
			FWStyle::Text, FWUi::SmallButton(LOCTEXT("Sign", "Podpisz"), [WeakThis]()
			{
				if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
				{
					This->Act(TEXT("city.recall"), MakeShared<FJsonObject>(), [](const TSharedPtr<FJsonObject>& Result)
					{
						return FWUi::Bool(Result, TEXT("recalled")) ? LOCTEXT("MayorRecalled", "Burmistrz odwołany!") : LOCTEXT("Signed", "Podpisano wniosek o odwołanie burmistrza");
					});
				}
			}, false, bCanVote && !FWUi::Bool(Recall, TEXT("signed")))), 8.f);
	}
	// Ordinances: the mayor puts one to the vote of the townsfolk for a day
	Add(FWUi::Heading(LOCTEXT("Ordinances", "Uchwały")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Ordinances = FWUi::Objects(Politics, TEXT("ordinances"));
	if (Ordinances.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoOrdinances", "W tym tygodniu nie było uchwał."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& O : Ordinances)
	{
		const bool bOpen = FWUi::Bool(O, TEXT("open"));
		const bool bInForce = FWUi::Bool(O, TEXT("inForce"));
		const FText Status = bOpen ? FText::Format(LOCTEXT("Voting", "głosowanie trwa jeszcze {0}"), FWCityText::Left(FWUi::Num(O, TEXT("votingEnds"))))
			: bInForce ? LOCTEXT("InForce", "obowiązuje do końca tygodnia") : LOCTEXT("Rejected", "odrzucona");
		TSharedPtr<SWidget> Right;
		if (bOpen && bCanVote)
		{
			const FString Id = FWUi::Str(O, TEXT("id"));
			bool bVoted = false, bYesVote = false;
			bVoted = O->TryGetBoolField(TEXT("myVote"), bYesVote);
			auto Vote = [WeakThis, Id](bool bYes)
			{
				return [WeakThis, Id, bYes]()
				{
					if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("id"), Id);
						P->SetBoolField(TEXT("yes"), bYes);
						This->Act(TEXT("city.ordinance"), P, [bYes](const TSharedPtr<FJsonObject>&) { return bYes ? LOCTEXT("VotedYes", "Oddano głos za uchwałą") : LOCTEXT("VotedNo", "Oddano głos przeciw uchwale"); });
					}
				};
			};
			Right = SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Yes", "Za"), Vote(true), false, !(bVoted && bYesVote))]
				+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("No", "Przeciw"), Vote(false), false, !(bVoted && !bYesVote))];
		}
		Add(FWCityText::Line(FText::Format(LOCTEXT("Ordinance", "{0} · za {1}, przeciw {2} · {3}"), FText::FromString(FWUi::Str(O, TEXT("name"))), int32(FWUi::Num(O, TEXT("yes"))),
			int32(FWUi::Num(O, TEXT("no"))), Status), bInForce ? FWStyle::Good : FWStyle::Text, Right), 3.f);
	}
	if (bMayor)
	{
		TArray<TPair<FString, FText>> Kinds;
		for (const TSharedPtr<FJsonObject>& K : FWUi::Objects(Politics, TEXT("ordinanceKinds")))
		{
			Kinds.Add({ FWUi::Str(K, TEXT("id")), FText::FromString(FWUi::Str(K, TEXT("name"))) });
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Pick(TEXT("ordinanceKind"), Kinds, 380.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Propose", "Pod głosowanie"), [WeakThis]()
				{
					if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("kind"), This->Form->Get(TEXT("ordinanceKind")));
						This->Act(TEXT("city.propose"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Proposed", "Uchwała poddana pod głosowanie"); });
					}
				}, true)
			], 8.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("OrdinanceHours", "Głosowanie trwa {0} h; uchwała przechodzi, gdy więcej mieszkańców jest za niż przeciw."), int32(FWUi::Num(Politics, TEXT("ordinanceHours")))), FWStyle::Muted));
	}
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeElection()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Hall = FWUi::Obj(State, TEXT("hall"));
	const TSharedPtr<FJsonObject> Politics = FWUi::Obj(Hall, TEXT("politics"));
	const bool bCanVote = FWUi::Bool(Hall, TEXT("canVote"));
	const FString MyVote = FWUi::Str(Hall, TEXT("myVote"));
	Add(FWUi::Note(FText::Format(LOCTEXT("ElectionNote", "Wybory na przyszły tydzień (koniec za {0}). Głosują i mogą wygrać właściciele działek w powiecie miasta, także poza samym miastem."),
		FWCityText::Left(FWUi::Num(Hall, TEXT("weekEndsAt")))), FWStyle::Muted), 0.f);
	for (const TSharedPtr<FJsonObject>& C : FWUi::Objects(Politics, TEXT("people")))
	{
		const FString Name = FWUi::Str(C, TEXT("name"));
		const bool bMine = MyVote == Name;
		const FString Party = FWUi::Str(C, TEXT("party"));
		TArray<FString> Details;
		if (FWUi::Bool(C, TEXT("stands")))
		{
			Details.Add(FString::Printf(TEXT("program: podatek %d%%, premia: %s"), int32(FWUi::Num(C, TEXT("taxPercent"))), *BonusName(FWUi::Str(C, TEXT("bonus"))).ToString()));
			if (!FWUi::Str(C, TEXT("slogan")).IsEmpty())
			{
				Details.Add(FString::Printf(TEXT("„%s”"), *FWUi::Str(C, TEXT("slogan"))));
			}
		}
		if (FWUi::Num(C, TEXT("promisesMade")) > 0.0)
		{
			Details.Add(FString::Printf(TEXT("dotrzymane obietnice: %d z %d"), int32(FWUi::Num(C, TEXT("promisesKept"))), int32(FWUi::Num(C, TEXT("promisesMade")))));
		}
		for (const TSharedPtr<FJsonObject>& Sponsor : FWUi::Objects(C, TEXT("sponsors")))
		{
			Details.Add(FString::Printf(TEXT("wsparcie: %s %s"), *FWUi::Str(Sponsor, TEXT("company")), *FWUi::Money(FWUi::Num(Sponsor, TEXT("cents"))).ToString()));
		}
		FText Text = FText::Format(LOCTEXT("Candidate", "{0}{1}{2}: {3} głosów{4}"), FText::FromString(Name), FWUi::Bool(C, TEXT("me")) ? LOCTEXT("Me", " (ty)") : FText::GetEmpty(),
			Party.IsEmpty() ? FText::GetEmpty() : FText::FromString(FString::Printf(TEXT(" (%s)"), *Party)), int32(FWUi::Num(C, TEXT("votes"))),
			bMine ? LOCTEXT("YourVote", " · twój głos") : FText::GetEmpty());
		if (Details.Num() > 0)
		{
			Text = FText::Format(LOCTEXT("WithDetails", "{0}\n{1}"), Text, FText::FromString(FString::Join(Details, TEXT(" · "))));
		}
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (FWUi::Bool(C, TEXT("stands")))
		{
			const FString Key = TEXT("sponsor:") + Name;
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(Key, LOCTEXT("SponsorHint", "$ ze spółki"), 100.f, TEXT("100"))];
			Right->AddSlot().AutoWidth().Padding(0, 0, 10, 0)
			[
				FWUi::SmallButton(LOCTEXT("Back", "Wesprzyj"), [WeakThis, Name, Key]()
				{
					if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("candidate"), Name);
						P->SetNumberField(TEXT("cents"), This->Form->Cents(Key));
						This->Act(TEXT("city.sponsor"), P, [Name](const TSharedPtr<FJsonObject>&) { return FText::Format(LOCTEXT("Backed", "Spółka wsparła kandydata: {0}"), FText::FromString(Name)); });
					}
				})
			];
		}
		Right->AddSlot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("Vote", "Głosuj"), [WeakThis, Name]()
			{
				if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("candidate"), Name);
					This->Act(TEXT("city.vote"), P, [Name](const TSharedPtr<FJsonObject>&) { return FText::Format(LOCTEXT("Voted", "Oddano głos na: {0}"), FText::FromString(Name)); });
				}
			}, true, bCanVote && !bMine)
		];
		Add(FWCityText::Line(Text, bMine ? FWStyle::Accent : FWStyle::Text, Right), 4.f);
	}
	if (bCanVote)
	{
		const bool bStanding = FWUi::Bool(Politics, TEXT("standing"));
		TArray<TPair<FString, FText>> Bonuses = { { FString(), LOCTEXT("WithoutBonus2", "Bez premii") } };
		for (const TSharedPtr<FJsonObject>& B : FWUi::Objects(Hall, TEXT("bonuses")))
		{
			Bonuses.Add({ FWUi::Str(B, TEXT("id")), FText::FromString(FWUi::Str(B, TEXT("name"))) });
		}
		Add(FWUi::Heading(bStanding ? LOCTEXT("ChangeProgram", "Zmień swój program") : LOCTEXT("Stand", "Kandyduj na burmistrza")), 18.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("TaxPercent2", "Podatek %")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Field(TEXT("standTax"), FText::GetEmpty(), 60.f, TEXT("5"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Pick(TEXT("standBonus"), Bonuses, 260.f)]
			+ SHorizontalBox::Slot().AutoWidth()[Form->Field(TEXT("standSlogan"), LOCTEXT("Slogan", "Hasło wyborcze"), 280.f)], 6.f);
		TSharedRef<SHorizontalBox> Buttons = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(bStanding ? LOCTEXT("SaveProgram", "Zapisz program") : FText::Format(LOCTEXT("StandFor", "Kandyduj (kaucja {0})"), FWUi::Money(FWUi::Num(Politics, TEXT("depositCents")))),
					[WeakThis]()
					{
						if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetNumberField(TEXT("taxPercent"), This->Form->Number(TEXT("standTax")));
							P->SetStringField(TEXT("bonus"), This->Form->Get(TEXT("standBonus")));
							P->SetStringField(TEXT("slogan"), This->Form->Get(TEXT("standSlogan")));
							This->Act(TEXT("city.stand"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Standing", "Kandydujesz na burmistrza"); });
						}
					}, true)
			];
		if (bStanding)
		{
			Buttons->AddSlot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("WithdrawCandidacy", "Wycofaj kandydaturę (kaucja wraca)"), [WeakThis]()
				{
					if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("city.withdraw"), MakeShared<FJsonObject>(), [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("CandidacyWithdrawn", "Wycofałeś kandydaturę, kaucja wróciła"); });
					}
				})
			];
		}
		Add(Buttons, 6.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("DepositRule", "Kaucja wraca, gdy dostaniesz co najmniej {0}% głosów; inaczej trafia do kasy miasta. Przy twoim imieniu widać, czy jako burmistrz dotrzymałeś obietnic."),
			int32(FWUi::Num(Politics, TEXT("depositBackPercent")))), FWStyle::Muted));
	}
	return Page;
}

TSharedRef<SWidget> SFWCityWindow::MakeTreasury()
{
	TWeakPtr<SFWCityWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Hall = FWUi::Obj(State, TEXT("hall"));
	const TSharedPtr<FJsonObject> Politics = FWUi::Obj(Hall, TEXT("politics"));
	const double Cash = FWUi::Num(Politics, TEXT("treasuryCents"));
	Add(FWUi::Heading(FText::Format(LOCTEXT("Treasury", "Kasa miasta: {0}"), FWUi::Money(Cash))), 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("TreasuryNote", "Trafia tu podatek od sklepów i usług (burmistrz dostaje {0}% jako pensję) i przepadłe kaucje wyborcze."),
		int32(FWUi::Num(Politics, TEXT("mayorSharePercent")))), FWStyle::Muted));
	for (const TSharedPtr<FJsonObject>& Line : FWUi::Objects(Politics, TEXT("ledger")))
	{
		const double Cents = FWUi::Num(Line, TEXT("cents"));
		Add(FWUi::Note(FText::Format(LOCTEXT("Ledger", "{0}{1}  {2}"), FText::FromString(Cents > 0.0 ? TEXT("+") : TEXT("")), FWUi::Money(Cents), FText::FromString(FWUi::Str(Line, TEXT("reason")))),
			Cents > 0.0 ? FWStyle::Good : FWStyle::Error), 2.f);
	}
	Add(FWUi::Heading(LOCTEXT("Buildings", "Budowy z kasy miasta")), 16.f);
	for (const TSharedPtr<FJsonObject>& I : FWUi::Objects(Politics, TEXT("investments")))
	{
		const FString Id = FWUi::Str(I, TEXT("id"));
		double Next = 0.0;
		TSharedPtr<SWidget> Right;
		if (FWUi::Bool(Hall, TEXT("isMayor")) && I->TryGetNumberField(TEXT("nextCents"), Next))
		{
			Right = FWUi::SmallButton(FText::Format(LOCTEXT("Build", "Buduj za {0}"), FWUi::Money(Next)), [WeakThis, Id]()
			{
				if (TSharedPtr<SFWCityWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("kind"), Id);
					This->Act(TEXT("city.invest"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Built", "Zbudowano z kasy miasta"); });
				}
			}, true, Cash >= Next);
		}
		Add(FWCityText::Line(FText::Format(LOCTEXT("Investment", "{0} (poziom {1}/{2}): {3}"), FText::FromString(FWUi::Str(I, TEXT("name"))), int32(FWUi::Num(I, TEXT("level"))),
			int32(FWUi::Num(I, TEXT("maxLevel"))), FText::FromString(FWUi::Str(I, TEXT("effect")))), FWStyle::Text, Right), 3.f);
	}
	return Page;
}

FReply SFWCityWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
