#include "UI/SFWCompanyWindow.h"

#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Input/SComboBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWCompany"

#include "SFWCompanyWindowHelpers.h"

void SFWCompanyWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	TSharedRef<SHorizontalBox> TabRow = SNew(SHorizontalBox);
	for (int32 i = 0; i < UE_ARRAY_COUNT(FWCompanyText::Tabs); ++i)
	{
		TabRow->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			SNew(SBox)
			.Visibility_Lambda([this, i]()
			{
				// Without a company only founding, the exchange and the rankings make sense
				const bool bMember = FWUi::Obj(State, TEXT("company")).IsValid();
				return bMember || i == FWCompanyText::Company || i == FWCompanyText::Exchange || i == FWCompanyText::Ranking ? EVisibility::Visible : EVisibility::Collapsed;
			})
			[
				FWUi::TabButton(FText::FromString(FWCompanyText::Tabs[i]), [this, i]() { return Tab == i; }, [this, i]() { ShowTab(i); })
			]
		];
	}
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Display(22)).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Spółka"), Right, TabRow, SAssignNew(Content, SBox), 1100.f, 800.f)
	];
	if (AFWPlayerController* PC = Controller.Get())
	{
		TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
		PC->Send(TEXT("market.prices"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin(); This && Reply.Type == TEXT("market.prices"))
			{
				This->Catalog = FWUi::Objects(Reply.Payload, TEXT("items"));
				This->Catalog.Sort([](const TSharedPtr<FJsonObject>& A, const TSharedPtr<FJsonObject>& B) { return FWUi::Str(A, TEXT("name")) < FWUi::Str(B, TEXT("name")); });
				This->Rebuild();
			}
		});
		PC->Send(TEXT("world.info"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin(); This && Reply.Type == TEXT("world.info"))
			{
				This->MyPlots = FWUi::Objects(Reply.Payload, TEXT("plots"));
				for (const TSharedPtr<FJsonObject>& Biome : FWUi::Objects(Reply.Payload, TEXT("biomes")))
				{
					This->BiomeNames.Add(int32(FWUi::Num(Biome, TEXT("id"))), FWUi::Str(Biome, TEXT("name")));
				}
				This->Rebuild();
			}
		});
	}
	Load();
}

void SFWCompanyWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("company.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("company"))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		This->State = Reply.Payload;
		This->Rebuild();
	});
	PC->RefreshInventory();
	LoadTab();
}

void SFWCompanyWindow::LoadTab()
{
	// The exchange and the rankings come from their own messages, asked for when their tab opens
	AFWPlayerController* PC = Controller.Get();
	if (!PC || (Tab != FWCompanyText::Exchange && Tab != FWCompanyText::Ranking))
	{
		return;
	}
	const bool bExchange = Tab == FWCompanyText::Exchange;
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	PC->Send(bExchange ? TEXT("company.exchange") : TEXT("company.rankings"), MakeShared<FJsonObject>(), [WeakThis, bExchange](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type != (bExchange ? TEXT("company.exchange") : TEXT("company.rankings")))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		(bExchange ? This->Exchange : This->Rankings) = Reply.Payload;
		This->Rebuild();
	});
}

void SFWCompanyWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	if (Scroll.IsValid())
	{
		Scroll->SetScrollOffset(0.f);
	}
	Rebuild();
	LoadTab();
}

void SFWCompanyWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	const FString Sent = Type;
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload.IsValid() ? Payload : MakeShared<FJsonObject>(), [WeakThis, Sent](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		// Shares come back as the exchange; everything else as the company
		if (Reply.Type == TEXT("company.exchange"))
		{
			This->Exchange = Reply.Payload;
			This->Load();
		}
		else if (Reply.Type == TEXT("company"))
		{
			This->State = Reply.Payload;
			This->Rebuild();
			if (Sent == TEXT("company.takeover"))
			{
				This->LoadTab();
			}
		}
		else
		{
			Inner->ToastError(Reply);
			return;
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		Inner->RefreshInventory();
		FWCompanyText::Wallet(Inner);
	});
}

TSharedPtr<FJsonObject> SFWCompanyWindow::Pay(std::initializer_list<TPair<const TCHAR*, FString>> Strings, std::initializer_list<TPair<const TCHAR*, double>> Numbers) const
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	for (const TPair<const TCHAR*, FString>& Each : Strings)
	{
		P->SetStringField(Each.Key, Each.Value);
	}
	for (const TPair<const TCHAR*, double>& Each : Numbers)
	{
		P->SetNumberField(Each.Key, Each.Value);
	}
	return P;
}

int32 SFWCompanyWindow::Owned(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

int32 SFWCompanyWindow::Stored(const TSharedPtr<FJsonObject>& Company, const FString& Item)
{
	for (const TSharedPtr<FJsonObject>& Stack : FWUi::Objects(FWUi::Obj(Company, TEXT("warehouse")), TEXT("items")))
	{
		if (FWUi::Str(Stack, TEXT("id")) == Item)
		{
			return int32(FWUi::Num(Stack, TEXT("quantity")));
		}
	}
	return 0;
}

TSharedRef<SWidget> SFWCompanyWindow::Field(const FString& Key, const FText& Hint, float Width, const FString& Default)
{
	if (!Inputs.Contains(Key))
	{
		Inputs.Add(Key, Default);
	}
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	return SNew(SBox).WidthOverride(Width)
		[
			SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(Hint).Text(FText::FromString(Inputs[Key]))
			.OnTextChanged_Lambda([WeakThis, Key](const FText& Text) { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Inputs.Add(Key, Text.ToString()); } })
		];
}

FString SFWCompanyWindow::Value(const FString& Key) const
{
	const FString* Found = Inputs.Find(Key);
	return Found ? Found->TrimStartAndEnd() : FString();
}

TSharedRef<SWidget> SFWCompanyWindow::Picker(const FString& Key, const TArray<TPair<FString, FText>>& Options, float Width)
{
	TArray<TSharedPtr<FString>>& Ids = PickerOptions.FindOrAdd(Key);
	TMap<FString, FText>& Names = PickerNames.FindOrAdd(Key);
	Ids.Reset();
	Names.Reset();
	for (const TPair<FString, FText>& Option : Options)
	{
		Ids.Add(MakeShared<FString>(Option.Key));
		Names.Add(Option.Key, Option.Value);
	}
	if (!Inputs.Contains(Key) && Options.Num() > 0)
	{
		Inputs.Add(Key, Options[0].Key);
	}
	TSharedPtr<FString> Chosen;
	for (const TSharedPtr<FString>& Id : Ids)
	{
		Chosen = *Id == Inputs.FindRef(Key) ? Id : Chosen;
	}
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	return SNew(SBox).WidthOverride(Width)
		[
			SNew(SComboBox<TSharedPtr<FString>>)
			.OptionsSource(&Ids)
			.InitiallySelectedItem(Chosen)
			.MaxListHeight(380.f)
			.OnGenerateWidget_Lambda([WeakThis, Key](TSharedPtr<FString> Id)
			{
				TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
				const bool bItem = Key.StartsWith(TEXT("item"));
				return SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, bItem ? 6.f : 0.f, 0)
					[
						bItem && This ? FWUi::Icon(This->Controller.Get(), *Id, 20.f) : SNullWidget::NullWidget
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).Text(This ? This->PickerNames[Key].FindRef(*Id) : FText::FromString(*Id))
					];
			})
			.OnSelectionChanged_Lambda([WeakThis, Key](TSharedPtr<FString> Id, ESelectInfo::Type How)
			{
				TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
				if (!This || !Id.IsValid())
				{
					return;
				}
				This->Inputs.Add(Key, *Id);
				// A role or a manager picked takes effect at once
				if (How != ESelectInfo::Direct && (Key.StartsWith(TEXT("role:")) || Key.StartsWith(TEXT("manager:"))))
				{
					const FString Who = Key.RightChop(Key.Find(TEXT(":")) + 1);
					if (Key.StartsWith(TEXT("role:")))
					{
						This->Act(TEXT("company.role"), This->Pay({ { TEXT("name"), Who }, { TEXT("role"), *Id } }, {}));
					}
					else
					{
						FString X, Y;
						Who.Split(TEXT(","), &X, &Y);
						This->Act(TEXT("company.assign"), This->Pay({ { TEXT("name"), *Id } }, { { TEXT("x"), FCString::Atod(*X) }, { TEXT("y"), FCString::Atod(*Y) } }));
					}
				}
				else
				{
					This->Rebuild();
				}
			})
			[
				SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
				.Text_Lambda([WeakThis, Key]()
				{
					TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
					return This ? This->PickerNames.FindOrAdd(Key).FindRef(This->Inputs.FindRef(Key)) : FText::GetEmpty();
				})
			]
		];
}

TSharedRef<SWidget> SFWCompanyWindow::ItemPicker(const FString& Key, bool bTraderSellsOnly)
{
	TArray<TPair<FString, FText>> Options;
	for (const TSharedPtr<FJsonObject>& Entry : Catalog)
	{
		if (!bTraderSellsOnly || Entry->HasTypedField<EJson::Number>(TEXT("buyCents")))
		{
			Options.Add({ FWUi::Str(Entry, TEXT("id")), FText::FromString(FWUi::Str(Entry, TEXT("name"))) });
		}
	}
	return Picker(Key, Options, 260.f);
}

TSharedRef<SWidget> SFWCompanyWindow::ItemLine(const FString& Item, const FText& Text, TSharedPtr<SWidget> Right)
{
	return SNew(SFWCard).Padding(FMargin(8.f, 4.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Item, 26.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text).AutoWrapText(true)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
		];
}

TSharedRef<SWidget> SFWCompanyWindow::TextLine(const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right)
{
	return SNew(SFWCard).Padding(FMargin(10.f, 5.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(Color).AutoWrapText(true)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
		];
}

void SFWCompanyWindow::Rebuild()
{
	if (!Content.IsValid() || !State.IsValid())
	{
		return;
	}
	const TSharedPtr<FJsonObject> Company = FWUi::Obj(State, TEXT("company"));
	if (!Company.IsValid() && Tab != FWCompanyText::Exchange && Tab != FWCompanyText::Ranking)
	{
		Tab = FWCompanyText::Company;
	}
	TSharedPtr<SWidget> Page;
	switch (Tab)
	{
	case FWCompanyText::Cash: Page = MakeCash(Company); break;
	case FWCompanyText::Projects: Page = MakeProjects(Company); break;
	case FWCompanyText::Orders: Page = MakeOrders(Company); break;
	case FWCompanyText::Deals: Page = MakeDeals(Company); break;
	case FWCompanyText::Exchange: Page = MakeExchange(); break;
	case FWCompanyText::Ranking: Page = MakeRankings(); break;
	default: Page = Company.IsValid() ? MakeCompany(Company) : MakeWithout(); break;
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWCompanyWindow::MakeWithout()
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("About", "Spółka to kilku graczy budujących jedną fabrykę: członkowie wnoszą swoje działki, pracują na wszystkich naraz, mają wspólny magazyn, kasę i badania."), FWStyle::Muted), 0.f);
	Add(FWUi::Heading(LOCTEXT("Found", "Załóż spółkę")), 14.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("name"), LOCTEXT("NameHint", "Nazwa spółki (3–24 znaki)"), 360.f)]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("Create", "Załóż"), [WeakThis]()
			{
				if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
				{
					This->Act(TEXT("company.create"), This->Pay({ { TEXT("name"), This->Value(TEXT("name")) } }, {}));
				}
			}, true)
		], 6.f);
	Add(FWUi::Heading(LOCTEXT("Invitations", "Zaproszenia")), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Invitations = FWUi::Objects(State, TEXT("invitations"));
	if (Invitations.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoInvitations", "Nikt cię jeszcze nie zaprosił. Prezes spółki zaprasza po nazwie postaci."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Invitation : Invitations)
	{
		const FString Id = FWUi::Str(Invitation, TEXT("company"));
		Add(TextLine(FText::Format(LOCTEXT("Invitation", "{0}  ({1})"), FText::FromString(FWUi::Str(Invitation, TEXT("name"))), FWCompanyText::Members(int32(FWUi::Num(Invitation, TEXT("members"))))), FWStyle::Text,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(LOCTEXT("Join", "Dołącz"), [WeakThis, Id]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(TEXT("company.accept"), This->Pay({ { TEXT("company"), Id } }, {})); } }, true)
			]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Decline", "Odrzuć"), [WeakThis, Id]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(TEXT("company.decline"), This->Pay({ { TEXT("company"), Id } }, {})); } })
			]));
	}
	return Page;
}

FText SFWCompanyWindow::LogText(const TSharedPtr<FJsonObject>& Entry) const
{
	const FText Who = FText::FromString(FWUi::Str(Entry, TEXT("who")));
	const FString OtherName = FWUi::Str(Entry, TEXT("other"));
	const FText Other = FText::FromString(OtherName.IsEmpty() ? TEXT("?") : OtherName);
	const int32 Quantity = int32(FWUi::Num(Entry, TEXT("quantity")));
	const FString NameString = FWUi::Str(Entry, TEXT("name"));
	const FText Name = FText::FromString(NameString.IsEmpty() ? FWUi::Str(Entry, TEXT("item")) : NameString);
	const FText Item = FText::FromString(FWUi::Str(Entry, TEXT("item")));
	const FString Action = FWUi::Str(Entry, TEXT("action"));
	const FText Money = FWUi::Money(Quantity);
	if (Action == TEXT("founded")) return FText::Format(LOCTEXT("LogFounded", "{0} zakłada spółkę"), Who);
	if (Action == TEXT("joined")) return FText::Format(LOCTEXT("LogJoined", "{0} dołącza do spółki"), Who);
	if (Action == TEXT("left")) return FText::Format(LOCTEXT("LogLeft", "{0} odchodzi ze spółki"), Who);
	if (Action == TEXT("kicked")) return FText::Format(LOCTEXT("LogKicked", "{0} usuwa ze spółki gracza {1}"), Who, Other);
	if (Action == TEXT("role")) return FText::Format(LOCTEXT("LogRole", "{0}: {1} ma teraz rolę {2}"), Who, Other, FText::FromString(FWCompanyText::RoleName(FWUi::Str(Entry, TEXT("item"))).ToString().ToLower()));
	if (Action == TEXT("plotAdded")) return FText::Format(LOCTEXT("LogPlotAdded", "{0} wnosi działkę {1} (udziały: {2})"), Who, Item, Quantity);
	if (Action == TEXT("plotRemoved")) return FText::Format(LOCTEXT("LogPlotRemoved", "{0} zabiera działkę {1}"), Who, Item);
	if (Action == TEXT("put")) return FText::Format(LOCTEXT("LogPut", "{0} wkłada {1} × {2}"), Who, Quantity, Name);
	if (Action == TEXT("take")) return FText::Format(LOCTEXT("LogTake", "{0} wyjmuje {1} × {2}"), Who, Quantity, Name);
	if (Action == TEXT("deposit")) return FText::Format(LOCTEXT("LogDeposit", "{0} wpłaca {1}"), Who, Money);
	if (Action == TEXT("withdraw")) return FText::Format(LOCTEXT("LogWithdraw", "{0} wypłaca {1}"), Who, Money);
	if (Action == TEXT("lobby")) return FText::Format(LOCTEXT("LogLobby", "{0} wydaje {1}: {2}"), Who, Money, Item);
	if (Action == TEXT("sell")) return FText::Format(LOCTEXT("LogSell", "{0} sprzedaje handlarzowi {1} × {2}"), Who, Quantity, Name);
	if (Action == TEXT("buy")) return FText::Format(LOCTEXT("LogBuy", "{0} kupuje u handlarza {1} × {2}"), Who, Quantity, Name);
	if (Action == TEXT("dividend")) return FText::Format(LOCTEXT("LogDividend", "Dywidenda: udziałowcy dostają {0}"), Money);
	if (Action == TEXT("dividendSet")) return FText::Format(LOCTEXT("LogDividendSet", "{0} ustawia dywidendę na {1}%"), Who, Quantity);
	if (Action == TEXT("assigned")) return FText::Format(LOCTEXT("LogAssigned", "{0}: działka {1} ma kierownika {2}"), Who, Item, OtherName.IsEmpty() ? LOCTEXT("Nobody", "(nikt)") : Other);
	if (Action == TEXT("listed")) return FText::Format(LOCTEXT("LogListed", "{0} wprowadza spółkę na giełdę"), Who);
	if (Action == TEXT("delisted")) return FText::Format(LOCTEXT("LogDelisted", "{0} wycofuje spółkę z giełdy"), Who);
	if (Action == TEXT("sharesBought")) return FText::Format(LOCTEXT("LogSharesBought", "{0} kupuje {1} akcji"), Who, Quantity);
	if (Action == TEXT("sharesSold")) return FText::Format(LOCTEXT("LogSharesSold", "{0} sprzedaje spółce {1} akcji"), Who, Quantity);
	if (Action == TEXT("takeover")) return FText::Format(LOCTEXT("LogTakeover", "{0} przejmuje spółkę"), Who);
	if (Action == TEXT("projectStarted")) return FText::Format(LOCTEXT("LogProjectStarted", "{0} rozpoczyna wielki projekt"), Who);
	if (Action == TEXT("projectPart")) return FText::Format(LOCTEXT("LogProjectPart", "{0} dokłada do projektu {1} × {2}"), Who, Quantity, Name);
	if (Action == TEXT("projectDone")) return LOCTEXT("LogProjectDone", "Wielki projekt gotowy!");
	if (Action == TEXT("wonderLost")) return LOCTEXT("LogWonderLost", "Inna spółka zbudowała cud świata pierwsza; części wróciły do magazynu");
	if (Action == TEXT("order")) return FText::Format(LOCTEXT("LogOrder", "{0} dostarcza zlecenie: {1} (nagroda {2})"), Who, Name, Money);
	if (Action == TEXT("orderPart")) return FText::Format(LOCTEXT("LogOrderPart", "{0} dostarcza zlecenie: {1} × {2}"), Who, Name, Quantity);
	if (Action == TEXT("task")) return FText::Format(LOCTEXT("LogTask", "{0} zleca zadanie: {1} × {2}{3}"), Who, Quantity, Name, OtherName.IsEmpty() ? FText::GetEmpty() : FText::Format(LOCTEXT("ForOther", " dla {0}"), Other));
	if (Action == TEXT("taskPart") || Action == TEXT("taskDone")) return FText::Format(LOCTEXT("LogTaskPart", "{0} przynosi {1} × {2} do zadania{3}"), Who, Quantity, Name, Action == TEXT("taskDone") ? LOCTEXT("AndEnds", " i kończy je") : FText::GetEmpty());
	if (Action == TEXT("taskCancelled")) return FText::Format(LOCTEXT("LogTaskCancelled", "{0} odwołuje zadanie: {1}"), Who, Name);
	if (Action == TEXT("dealOffered")) return FText::Format(LOCTEXT("LogDealOffered", "{0} wysyła ofertę umowy: {1}"), Who, Name);
	if (Action == TEXT("dealAccepted")) return FText::Format(LOCTEXT("LogDealAccepted", "{0} przyjmuje umowę"), Who);
	if (Action == TEXT("dealMissed")) return FText::Format(LOCTEXT("LogDealMissed", "Umowa: zabrakło towaru ({0}), kara {1}"), Name, Money);
	if (Action == TEXT("dealBroken")) return FText::Format(LOCTEXT("LogDealBroken", "Umowa na {0} zerwana: kupcowi zabrakło pieniędzy"), Name);
	if (Action == TEXT("season")) return FText::Format(LOCTEXT("LogSeason", "Sezon {0}: {1}. miejsce i nagroda w kasie!"), FWCompanyText::Season(FCString::Atoi(*FWUi::Str(Entry, TEXT("item")))), Quantity);
	return FText::Format(LOCTEXT("LogOther", "{0}: {1}"), Who, FText::FromString(Action));
}

FReply SFWCompanyWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
