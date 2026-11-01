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

namespace FWCompanyText
{
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

	const TCHAR* const Tabs[] = { TEXT("Spółka"), TEXT("Kasa"), TEXT("Projekty"), TEXT("Zlecenia"), TEXT("Umowy"), TEXT("Giełda"), TEXT("Ranking") };
	enum ETab { Company, Cash, Projects, Orders, Deals, Exchange, Ranking };

	FText RoleName(const FString& Role)
	{
		static const TMap<FString, FText> Names = {
			{ TEXT("owner"), LOCTEXT("Owner", "Prezes") }, { TEXT("partner"), LOCTEXT("Partner", "Wspólnik") }, { TEXT("treasurer"), LOCTEXT("Treasurer", "Skarbnik") },
			{ TEXT("manager"), LOCTEXT("Manager", "Kierownik działki") }, { TEXT("logistician"), LOCTEXT("Logistician", "Logistyk") }, { TEXT("worker"), LOCTEXT("Worker", "Pracownik") } };
		return Names.Contains(Role) ? Names[Role] : FText::FromString(Role);
	}

	FText RoleHelp(const FString& Role)
	{
		static const TMap<FString, FText> Help = {
			{ TEXT("owner"), LOCTEXT("OwnerHelp", "Zaprasza i usuwa członków, nadaje role, przydziela działki kierownikom, a poza tym może to samo co wspólnik i skarbnik.") },
			{ TEXT("partner"), LOCTEXT("PartnerHelp", "Buduje na działkach spółki, ustawia rozkłady pociągów, wyjmuje z magazynu, skrzyń i stacji, wybiera badania i wielkie projekty.") },
			{ TEXT("treasurer"), LOCTEXT("TreasurerHelp", "Zarządza kasą: wypłaca, kupuje i sprzedaje dla spółki, zleca zadania z pensją i podpisuje umowy z innymi spółkami.") },
			{ TEXT("manager"), LOCTEXT("ManagerHelp", "Buduje tylko na działkach, które przydzielił mu prezes; na pozostałych pracuje jak pracownik.") },
			{ TEXT("logistician"), LOCTEXT("LogisticianHelp", "Ustawia rozkłady pociągów i wysyła pociągi ze wszystkich działek spółki, wyjmuje ze skrzyń i stacji.") },
			{ TEXT("worker"), LOCTEXT("WorkerHelp", "Pracuje na działkach spółki: zbiera, kopie, uprawia i obsługuje maszyny, wkłada do magazynu, ale nic nie wyjmuje i nie buduje.") } };
		return Help.Contains(Role) ? Help[Role] : FText::GetEmpty();
	}

	FText Season(int32 Season)
	{
		static const TCHAR* const Months[] = { TEXT("styczeń"), TEXT("luty"), TEXT("marzec"), TEXT("kwiecień"), TEXT("maj"), TEXT("czerwiec"), TEXT("lipiec"),
			TEXT("sierpień"), TEXT("wrzesień"), TEXT("październik"), TEXT("listopad"), TEXT("grudzień") };
		return FText::FromString(FString::Printf(TEXT("%s %d"), Months[FMath::Clamp(Season % 100 - 1, 0, 11)], Season / 100));
	}

	FText Ago(double At)
	{
		const int64 Seconds = int64((FWUi::LocalNowMs() - At) / 1000.0);
		if (Seconds < 60)
		{
			return LOCTEXT("JustNow", "przed chwilą");
		}
		if (Seconds < 3600)
		{
			return FText::Format(LOCTEXT("MinAgo", "{0} min temu"), Seconds / 60);
		}
		if (Seconds < 86400)
		{
			return FText::Format(LOCTEXT("HoursAgo", "{0} godz. temu"), Seconds / 3600);
		}
		return FText::Format(LOCTEXT("DaysAgo", "{0} dni temu"), Seconds / 86400);
	}

	FText In(double At)
	{
		const int64 Seconds = int64((At - FWUi::LocalNowMs()) / 1000.0);
		if (Seconds <= 0)
		{
			return LOCTEXT("Now", "teraz");
		}
		if (Seconds < 3600)
		{
			return FText::Format(LOCTEXT("InMin", "za {0} min"), FMath::Max<int64>(1, Seconds / 60));
		}
		if (Seconds < 86400)
		{
			return FText::Format(LOCTEXT("InHours", "za {0} godz."), Seconds / 3600);
		}
		return FText::Format(LOCTEXT("InDays", "za {0} dni"), Seconds / 86400);
	}

	FText Members(int32 Count)
	{
		if (Count == 1)
		{
			return LOCTEXT("OneMember", "1 członek");
		}
		const bool bFew = Count % 10 >= 2 && Count % 10 <= 4 && !(Count % 100 >= 12 && Count % 100 <= 14);
		return FText::Format(bFew ? LOCTEXT("FewMembers", "{0} członkowie") : LOCTEXT("ManyMembers", "{0} członków"), Count);
	}
}

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
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
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
	return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(8.f, 4.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Item, 26.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text).AutoWrapText(true)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
		];
}

TSharedRef<SWidget> SFWCompanyWindow::TextLine(const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right)
{
	return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
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

TSharedRef<SWidget> SFWCompanyWindow::MakeCompany(const TSharedPtr<FJsonObject>& Company)
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	const FString Role = FWUi::Str(Company, TEXT("role"));
	const bool bOwner = Role == TEXT("owner");
	const bool bMayTake = Role != TEXT("worker");
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(SNew(STextBlock).Font(FWStyle::Font(18, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FWUi::Str(Company, TEXT("name")))), 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("YourRole", "Twoja rola: {0}. {1}"), FWCompanyText::RoleName(Role), FWCompanyText::RoleHelp(Role)), FWStyle::Text));

	// Members, their roles; the owner invites and removes
	const TArray<TSharedPtr<FJsonObject>> Members = FWUi::Objects(Company, TEXT("members"));
	Add(FWUi::Heading(FText::Format(LOCTEXT("MembersOf", "Członkowie ({0} z {1})"), Members.Num(), int32(FWUi::Num(State, TEXT("maxMembers"))))), 16.f);
	TArray<FString> Managers;
	for (const TSharedPtr<FJsonObject>& Member : Members)
	{
		const FString Name = FWUi::Str(Member, TEXT("name"));
		const FString MemberRole = FWUi::Str(Member, TEXT("role"));
		const bool bYou = FWUi::Bool(Member, TEXT("you"));
		if (MemberRole == TEXT("manager"))
		{
			Managers.Add(Name);
		}
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		const int32 Plots = int32(FWUi::Num(Member, TEXT("plots")));
		if (Plots > 0)
		{
			Right->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				SNew(STextBlock).Text(FText::Format(LOCTEXT("PlotsOf", "działki: {0}"), Plots)).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
			];
		}
		if (bOwner && !bYou)
		{
			const FString Key = TEXT("role:") + Name;
			Inputs.Add(Key, MemberRole);
			TArray<TPair<FString, FText>> Roles;
			for (const TCHAR* Each : { TEXT("partner"), TEXT("treasurer"), TEXT("manager"), TEXT("logistician"), TEXT("worker") })
			{
				Roles.Add({ Each, FWCompanyText::RoleName(Each) });
			}
			Roles.Add({ TEXT("owner"), LOCTEXT("HandOver", "Przekaż prezesurę") });
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[Picker(Key, Roles, 190.f)];
			Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Kick", "Usuń"), Do(TEXT("company.kick"), Pay({ { TEXT("name"), Name } }, {})))];
		}
		else
		{
			Right->AddSlot().AutoWidth().VAlign(VAlign_Center)[SNew(STextBlock).Text(FWCompanyText::RoleName(MemberRole)).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
		}
		Add(TextLine(FText::FromString(Name + (bYou ? TEXT("  (ty)") : TEXT(""))), MemberRole == TEXT("owner") ? FWStyle::Accent : FWStyle::Text, Right), 3.f);
	}
	if (bOwner)
	{
		for (const FString& Name : FWUi::Strings(Company, TEXT("invited")))
		{
			Add(TextLine(FText::Format(LOCTEXT("Invited", "{0}  (zaproszony, czeka)"), FText::FromString(Name)), FWStyle::Muted,
				FWUi::SmallButton(LOCTEXT("Uninvite", "Cofnij"), Do(TEXT("company.uninvite"), Pay({ { TEXT("name"), Name } }, {})))), 3.f);
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("invite"), LOCTEXT("InviteHint", "Nazwa postaci"), 260.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Invite", "Zaproś"), [WeakThis]()
				{
					TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin();
					if (This && !This->Value(TEXT("invite")).IsEmpty())
					{
						This->Act(TEXT("company.invite"), This->Pay({ { TEXT("name"), This->Value(TEXT("invite")) } }, {}));
					}
				}, true)
			], 6.f);
	}

	// Plots brought in, and the player's own still outside
	Add(FWUi::Heading(LOCTEXT("Plots", "Działki spółki")), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Plots = FWUi::Objects(Company, TEXT("plots"));
	if (Plots.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoPlots", "Żadnej jeszcze nie wniesiono. Wnieś swoją działkę, żeby inni mogli na niej pracować; za jej cenę dostaniesz udziały."), FWStyle::Muted));
	}
	TSet<FIntPoint> Brought;
	for (const TSharedPtr<FJsonObject>& Plot : Plots)
	{
		const FIntPoint At(int32(FWUi::Num(Plot, TEXT("x"))), int32(FWUi::Num(Plot, TEXT("y"))));
		Brought.Add(At);
		const bool bMine = FWUi::Bool(Plot, TEXT("mine"));
		const FString Where = FString::Printf(TEXT("%s %d:%d  ·  %s"), *BiomeNames.FindRef(int32(FWUi::Num(Plot, TEXT("biome")))), At.X, At.Y,
			bMine ? TEXT("twoja") : *FWUi::Str(Plot, TEXT("owner")));
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (bOwner)
		{
			const FString Key = FString::Printf(TEXT("manager:%d,%d"), At.X, At.Y);
			Inputs.Add(Key, FWUi::Str(Plot, TEXT("manager")));
			TArray<TPair<FString, FText>> Options = { { FString(), LOCTEXT("NoManager", "Kierownik: nikt") } };
			for (const FString& Name : Managers)
			{
				Options.Add({ Name, FText::Format(LOCTEXT("ManagerIs", "Kierownik: {0}"), FText::FromString(Name)) });
			}
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[Picker(Key, Options, 220.f)];
		}
		else if (!FWUi::Str(Plot, TEXT("manager")).IsEmpty())
		{
			Right->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted).Text(FText::Format(LOCTEXT("ManagedBy", "kierownik: {0}"), FText::FromString(FWUi::Str(Plot, TEXT("manager")))))
			];
		}
		if (bMine)
		{
			Right->AddSlot().AutoWidth()
			[
				SNew(SBox).ToolTipText(LOCTEXT("RemoveTip", "Działka przestanie być wspólna. To, co postawili na niej inni, zostanie na miejscu."))
				[
					FWUi::SmallButton(LOCTEXT("Remove", "Zabierz"), Do(TEXT("company.removePlot"), Pay({}, { { TEXT("x"), double(At.X) }, { TEXT("y"), double(At.Y) } })))
				]
			];
		}
		Add(TextLine(FText::FromString(Where), FWStyle::Text, Right), 3.f);
	}
	for (const TSharedPtr<FJsonObject>& Plot : MyPlots)
	{
		const FIntPoint At(int32(FWUi::Num(Plot, TEXT("x"))), int32(FWUi::Num(Plot, TEXT("y"))));
		if (Brought.Contains(At))
		{
			continue;
		}
		Add(TextLine(FText::FromString(FString::Printf(TEXT("%s %d:%d  ·  tylko twoja"), *BiomeNames.FindRef(int32(FWUi::Num(Plot, TEXT("biome")))), At.X, At.Y)), FWStyle::Muted,
			FWUi::SmallButton(LOCTEXT("Bring", "Wnieś do spółki"), Do(TEXT("company.addPlot"), Pay({}, { { TEXT("x"), double(At.X) }, { TEXT("y"), double(At.Y) } })))), 3.f);
	}

	// The shared warehouse: taking out (not for workers) and putting in from the inventory
	const TSharedPtr<FJsonObject> Warehouse = FWUi::Obj(Company, TEXT("warehouse"));
	const int32 Capacity = int32(FWUi::Num(Warehouse, TEXT("capacity")));
	const int32 StoredCount = int32(FWUi::Num(Warehouse, TEXT("stored")));
	Add(FWUi::Heading(LOCTEXT("Warehouse", "Wspólny magazyn")), 18.f);
	Add(FWUi::Bar(Capacity > 0 ? float(StoredCount) / Capacity : 0.f, FWStyle::Accent), 4.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("Fill", "Zapełnienie: {0} z {1} sztuk"), StoredCount, Capacity), FWStyle::Muted));
	const TArray<TSharedPtr<FJsonObject>> Stock = FWUi::Objects(Warehouse, TEXT("items"));
	if (Stock.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("Empty", "Pusto."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Stack : Stock)
	{
		const FString Id = FWUi::Str(Stack, TEXT("id"));
		const int32 Quantity = int32(FWUi::Num(Stack, TEXT("quantity")));
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (bMayTake)
		{
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(LOCTEXT("Take10", "Weź 10"), Do(TEXT("company.take"), Pay({ { TEXT("item"), Id } }, { { TEXT("quantity"), 10.0 } })), false, Quantity >= 10)
			];
			Right->AddSlot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("TakeAll", "Wszystko"), Do(TEXT("company.take"), Pay({ { TEXT("item"), Id } }, { { TEXT("quantity"), double(Quantity) } })), true)
			];
		}
		Add(ItemLine(Id, FText::FromString(FString::Printf(TEXT("%s  ×%d"), *FWUi::Str(Stack, TEXT("name")), Quantity)), Right), 3.f);
	}
	if (!bMayTake)
	{
		Add(FWUi::Note(LOCTEXT("WorkerPuts", "Jako pracownik tylko wkładasz do magazynu; wyjmują pozostałe role."), FWStyle::Muted));
	}
	Add(FWUi::Heading(LOCTEXT("PutIn", "Włóż z ekwipunku")), 12.f);
	const int32 Room = Capacity - StoredCount;
	const TArray<FFWStack>& Inventory = Controller.IsValid() ? Controller->GetInventory() : TArray<FFWStack>();
	if (Inventory.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("InventoryEmpty", "Ekwipunek jest pusty."), FWStyle::Muted));
	}
	for (const FFWStack& Stack : Inventory)
	{
		if (Stack.Quantity <= 0)
		{
			continue;
		}
		const FString Id = Stack.Id;
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		for (int32 Amount : { 1, 10, 0 })
		{
			const int32 Count = Amount == 0 ? FMath::Min(Stack.Quantity, Room) : Amount;
			Right->AddSlot().AutoWidth().Padding(4, 0, 0, 0)
			[
				FWUi::SmallButton(Amount == 0 ? LOCTEXT("All", "Wszystko") : FText::Format(LOCTEXT("Plus", "+{0}"), Amount),
					Do(TEXT("company.put"), Pay({ { TEXT("item"), Id } }, { { TEXT("quantity"), double(Count) } })), Amount == 0, Count >= 1 && Count <= Stack.Quantity && Count <= Room)
			];
		}
		Add(ItemLine(Id, FText::FromString(FString::Printf(TEXT("%s  ×%d"), *Stack.Name, Stack.Quantity)), Right), 3.f);
	}

	// What happened lately
	Add(FWUi::Heading(LOCTEXT("Lately", "Ostatnio w spółce")), 18.f);
	for (const TSharedPtr<FJsonObject>& Entry : FWUi::Objects(Company, TEXT("log")))
	{
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth()[SNew(SBox).WidthOverride(110.f)[SNew(STextBlock).Text(FWCompanyText::Ago(FWUi::Num(Entry, TEXT("at")))).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)]]
			+ SHorizontalBox::Slot().FillWidth(1.f)[SNew(STextBlock).Text(LogText(Entry)).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text).AutoWrapText(true)], 2.f);
	}
	const bool bLast = Members.Num() == 1;
	Add(SNew(SBox).HAlign(HAlign_Left).ToolTipText(bLast ? LOCTEXT("CloseTip", "Jesteś ostatnim członkiem: to, co jest w magazynie, wróci do twojego ekwipunku, a kasa do portfela.")
		: bOwner ? LOCTEXT("OwnerLeaveTip", "Najpierw przekaż prezesurę innemu członkowi.") : LOCTEXT("LeaveTip", "Twoje działki przestaną być wspólne; udziały zostaną twoje."))
		[
			FWUi::SmallButton(bLast ? LOCTEXT("CloseCompany", "Zamknij spółkę") : LOCTEXT("Leave", "Opuść spółkę"), Do(TEXT("company.leave"), nullptr))
		], 18.f);
	return Page;
}

TSharedRef<SWidget> SFWCompanyWindow::MakeCash(const TSharedPtr<FJsonObject>& Company)
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	// Sends Type with the cents typed in the field Key
	auto MoneyAct = [WeakThis](const TCHAR* Type, const FString& Key)
	{
		return [WeakThis, Type, Key]()
		{
			if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
			{
				const double Cents = FMath::RoundToDouble(FCString::Atod(*This->Value(Key).Replace(TEXT(","), TEXT("."))) * 100.0);
				if (Cents > 0.0)
				{
					This->Act(Type, This->Pay({}, { { TEXT("cents"), Cents } }));
				}
			}
		};
	};
	const TSharedPtr<FJsonObject> Economy = FWUi::Obj(Company, TEXT("economy"));
	const bool bMoney = FWUi::Bool(Company, TEXT("money"));
	const FString Role = FWUi::Str(Company, TEXT("role"));
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Heading(FText::Format(LOCTEXT("Cash", "Kasa spółki: {0}"), FWUi::Money(FWUi::Num(Economy, TEXT("cashCents"))))), 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("Earned", "Zarobione od ostatniej dywidendy: {0}. Zarobione w ostatnim tygodniu: {1}. Wartość spółki: {2} (kasa, magazyn po cenach handlarza i dziesięć razy tygodniowy zarobek)."),
		FWUi::Money(FWUi::Num(Economy, TEXT("incomeCents"))), FWUi::Money(FWUi::Num(Economy, TEXT("profitCents"))), FWUi::Money(FWUi::Num(Economy, TEXT("valueCents")))), FWStyle::Muted));
	Add(FWUi::Heading(LOCTEXT("Deposit", "Wpłata")), 14.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("DepositNote", "Każdy członek może wpłacić pieniądze do kasy i dostaje za nie nowe udziały po {0} za udział."), FWUi::Money(FWUi::Num(Economy, TEXT("sharePriceCents")))), FWStyle::Muted));
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("deposit"), LOCTEXT("Dollars", "kwota $"), 160.f, TEXT("100"))]
		+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("PayIn", "Wpłać"), MoneyAct(TEXT("company.deposit"), TEXT("deposit")), true)], 4.f);
	if (bMoney)
	{
		Add(FWUi::Heading(LOCTEXT("Withdraw", "Wypłata")), 14.f);
		Add(FWUi::Note(LOCTEXT("WithdrawNote", "Pieniądze z kasy na twój portfel, np. żeby kupić działkę dla spółki."), FWStyle::Muted));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("withdraw"), LOCTEXT("Dollars2", "kwota $"), 160.f, TEXT("100"))]
			+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("PayOut", "Wypłać"), MoneyAct(TEXT("company.withdraw"), TEXT("withdraw")), true)], 4.f);
	}
	// Selling from the warehouse to the trader, buying into it
	if (Role != TEXT("worker"))
	{
		Add(FWUi::Heading(LOCTEXT("SellOut", "Sprzedaż z magazynu handlarzowi")), 18.f);
		int32 Sold = 0;
		for (const TSharedPtr<FJsonObject>& Stack : FWUi::Objects(FWUi::Obj(Company, TEXT("warehouse")), TEXT("items")))
		{
			const FString Id = FWUi::Str(Stack, TEXT("id"));
			double Price = 0.0;
			for (const TSharedPtr<FJsonObject>& Entry : Catalog)
			{
				if (FWUi::Str(Entry, TEXT("id")) == Id)
				{
					Price = FWUi::Num(Entry, TEXT("sellCents"));
				}
			}
			if (Price <= 0.0)
			{
				continue;
			}
			++Sold;
			const int32 Quantity = int32(FWUi::Num(Stack, TEXT("quantity")));
			Add(ItemLine(Id, FText::Format(LOCTEXT("SellLine", "{0}  ×{1}  ·  {2} za sztukę"), FText::FromString(FWUi::Str(Stack, TEXT("name"))), Quantity, FWUi::Money(Price)),
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
				[
					FWUi::SmallButton(LOCTEXT("Sell10", "Sprzedaj 10"), Do(TEXT("company.sell"), Pay({ { TEXT("item"), Id } }, { { TEXT("quantity"), 10.0 } })), false, Quantity >= 10)
				]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					FWUi::SmallButton(LOCTEXT("SellAll", "Wszystko"), Do(TEXT("company.sell"), Pay({ { TEXT("item"), Id } }, { { TEXT("quantity"), double(Quantity) } })), true)
				]), 3.f);
		}
		if (Sold == 0)
		{
			Add(FWUi::Note(LOCTEXT("NothingToSell", "W magazynie nie ma nic, co handlarz kupuje."), FWStyle::Muted));
		}
	}
	if (bMoney)
	{
		Add(FWUi::Heading(LOCTEXT("BuyIn", "Zakup do magazynu")), 14.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[ItemPicker(TEXT("itemBuy"), true)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("buyQuantity"), LOCTEXT("Pieces", "szt."), 90.f, TEXT("100"))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("BuyFromCash", "Kup z kasy"), [WeakThis]()
				{
					if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("company.buy"), This->Pay({ { TEXT("item"), This->Value(TEXT("itemBuy")) } }, { { TEXT("quantity"), FCString::Atod(*This->Value(TEXT("buyQuantity"))) } }));
					}
				}, true)
			], 4.f);
	}
	// Shares and the dividend
	const TSharedPtr<FJsonObject> Shares = FWUi::Obj(Economy, TEXT("shares"));
	Add(FWUi::Heading(FText::Format(LOCTEXT("Shares", "Udziały ({0})"), int32(FWUi::Num(Shares, TEXT("total"))))), 18.f);
	FNumberFormattingOptions One;
	One.MaximumFractionalDigits = 1;
	for (const TSharedPtr<FJsonObject>& Holder : FWUi::Objects(Shares, TEXT("holders")))
	{
		Add(TextLine(FText::FromString(FWUi::Str(Holder, TEXT("name")) + (FWUi::Bool(Holder, TEXT("you")) ? TEXT("  (ty)") : TEXT(""))), FWStyle::Text,
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
			.Text(FText::Format(LOCTEXT("Holding", "{0}  ·  {1}%"), int32(FWUi::Num(Holder, TEXT("shares"))), FText::AsNumber(FWUi::Num(Holder, TEXT("percent")), &One)))), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("Dividend", "Dywidenda")), 14.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("DividendNote", "Raz na dobę {0}% tego, co spółka zarobiła, trafia do udziałowców według udziałów. Następna: {1}."),
		int32(FWUi::Num(Economy, TEXT("dividendPercent"))), FWCompanyText::In(FWUi::Num(Economy, TEXT("nextDividendAt")))), FWStyle::Muted));
	if (Role == TEXT("owner"))
	{
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("Part", "Część zarobku na dywidendę (%):")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("dividend"), FText::GetEmpty(), 70.f, FString::FromInt(int32(FWUi::Num(Economy, TEXT("dividendPercent")))))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Set", "Ustaw"), [WeakThis]()
				{
					if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("company.dividend"), This->Pay({}, { { TEXT("percent"), FCString::Atod(*This->Value(TEXT("dividend"))) } }));
					}
				})
			], 4.f);
	}
	const TArray<TSharedPtr<FJsonObject>> Trophies = FWUi::Objects(Economy, TEXT("trophies"));
	if (Trophies.Num() > 0)
	{
		Add(FWUi::Heading(LOCTEXT("Trophies", "Trofea sezonów")), 14.f);
		for (const TSharedPtr<FJsonObject>& Trophy : Trophies)
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("Trophy", "{0}: {1}. miejsce ({2} zarobku)"), FWCompanyText::Season(int32(FWUi::Num(Trophy, TEXT("season")))),
				int32(FWUi::Num(Trophy, TEXT("place"))), FWUi::Money(FWUi::Num(Trophy, TEXT("valueCents")))), FWStyle::Accent));
		}
	}
	return Page;
}

TSharedRef<SWidget> SFWCompanyWindow::MakeProjects(const TSharedPtr<FJsonObject>& Company)
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	const TSharedPtr<FJsonObject> Economy = FWUi::Obj(Company, TEXT("economy"));
	const bool bMayTake = FWUi::Str(Company, TEXT("role")) != TEXT("worker");
	const bool bMayBuild = FWUi::Bool(Economy, TEXT("mayBuild"));
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("ProjectsAbout", "Wielkie budowy, których nikt nie postawi sam: członkowie znoszą tysiące części, a potem budowa trwa kilka dni."), FWStyle::Muted), 0.f);
	if (!bMayBuild)
	{
		Add(FWUi::Note(LOCTEXT("NeedResearch", "Najpierw spółka musi zbadać Wielkie budowy (technologia tylko dla spółek, w drzewku badań)."), FWStyle::Error));
	}
	for (const TSharedPtr<FJsonObject>& Project : FWUi::Objects(Economy, TEXT("projects")))
	{
		const FString Id = FWUi::Str(Project, TEXT("id"));
		const bool bStarted = FWUi::Bool(Project, TEXT("started"));
		double BuiltAt = 0.0;
		const bool bBuilt = Project->TryGetNumberField(TEXT("builtAt"), BuiltAt);
		const FString TakenBy = FWUi::Str(Project, TEXT("takenBy"));
		FText Status = LOCTEXT("NotStarted", "nierozpoczęty");
		if (bBuilt)
		{
			Status = BuiltAt <= FWUi::LocalNowMs() ? LOCTEXT("Ready", "gotowy") : FText::Format(LOCTEXT("Building", "w budowie, gotowy {0}"), FWCompanyText::In(BuiltAt));
		}
		else if (bStarted)
		{
			Status = LOCTEXT("Collecting", "zbieranie części");
		}
		if (!TakenBy.IsEmpty())
		{
			Status = FText::Format(LOCTEXT("TakenBy", "zbudowała już spółka {0}"), FText::FromString(TakenBy));
		}
		const bool bWonder = FWUi::Bool(Project, TEXT("wonder"));
		Add(FWUi::Heading(FText::Format(LOCTEXT("Project", "{0}{1}  ({2})"), bWonder ? LOCTEXT("Wonder", "Cud świata: ") : FText::GetEmpty(),
			FText::FromString(FWUi::Str(Project, TEXT("name"))), Status)), 18.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("ProjectNote", "{0} Budowa trwa {1} dni."), FText::FromString(FWUi::Str(Project, TEXT("description"))),
			int32(FWUi::Num(Project, TEXT("buildSeconds")) / 86400.0)), FWStyle::Muted));
		const TArray<FString> Requires = FWUi::Strings(Project, TEXT("requires"));
		if (Requires.Num() > 0)
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("Requires", "Wymaga: {0}."), FText::FromString(FString::Join(Requires, TEXT(", ")))), FWStyle::Muted));
		}
		if (bWonder && TakenBy.IsEmpty())
		{
			Add(FWUi::Note(LOCTEXT("Race", "Wyścig spółek: buduje tylko ta, która pierwsza zbierze wszystkie części. Pozostałe dostaną swoje części z powrotem do magazynu."), FWStyle::Accent));
		}
		if (!TakenBy.IsEmpty())
		{
			continue;
		}
		if (!bStarted && FWUi::Bool(Company, TEXT("leads")) && bMayBuild)
		{
			Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("Start", "Rozpocznij projekt"), Do(TEXT("company.project.start"), Pay({ { TEXT("project"), Id } }, {})), true)], 6.f);
		}
		for (const TSharedPtr<FJsonObject>& Part : FWUi::Objects(Project, TEXT("parts")))
		{
			const FString Item = FWUi::Str(Part, TEXT("id"));
			const int32 Needed = int32(FWUi::Num(Part, TEXT("needed")));
			const int32 Delivered = int32(FWUi::Num(Part, TEXT("delivered")));
			TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
			if (bStarted && !bBuilt && Delivered < Needed)
			{
				const int32 Have = Owned(Item);
				Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
				[
					FWUi::SmallButton(LOCTEXT("FromInventory", "Z ekwipunku"), Do(TEXT("company.project.give"), Pay({ { TEXT("project"), Id }, { TEXT("item"), Item } },
						{ { TEXT("quantity"), double(FMath::Min(Have, Needed - Delivered)) } })), false, Have > 0)
				];
				if (bMayTake)
				{
					const int32 InStore = Stored(Company, Item);
					TSharedPtr<FJsonObject> P = Pay({ { TEXT("project"), Id }, { TEXT("item"), Item } }, { { TEXT("quantity"), double(FMath::Min(InStore, Needed - Delivered)) } });
					P->SetBoolField(TEXT("fromWarehouse"), true);
					Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("FromWarehouse", "Z magazynu"), Do(TEXT("company.project.give"), P), false, InStore > 0)];
				}
			}
			Add(ItemLine(Item, FText::Format(LOCTEXT("PartLine", "{0}  {1}/{2}"), FText::FromString(FWUi::Str(Part, TEXT("name"))), Delivered, Needed), Right), 3.f);
			Add(FWUi::Bar(Needed > 0 ? float(Delivered) / Needed : 0.f, FWStyle::Good), 2.f);
		}
	}
	return Page;
}

TSharedRef<SWidget> SFWCompanyWindow::MakeOrders(const TSharedPtr<FJsonObject>& Company)
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	const TSharedPtr<FJsonObject> Economy = FWUi::Obj(Company, TEXT("economy"));
	const bool bMayTake = FWUi::Str(Company, TEXT("role")) != TEXT("worker");
	const bool bMoney = FWUi::Bool(Company, TEXT("money"));
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Heading(LOCTEXT("Weekly", "Zlecenia tygodnia")), 0.f);
	Add(FWUi::Note(LOCTEXT("WeeklyNote", "Duże zamówienia handlarza tylko dla spółek: dostarcz całość z magazynu do końca tygodnia, a nagroda trafi do kasy."), FWStyle::Muted));
	for (const TSharedPtr<FJsonObject>& Order : FWUi::Objects(Economy, TEXT("orders")))
	{
		const FString Item = FWUi::Str(Order, TEXT("id"));
		const int32 Quantity = int32(FWUi::Num(Order, TEXT("quantity")));
		const int32 Delivered = int32(FWUi::Num(Order, TEXT("delivered")));
		TSharedPtr<SWidget> Right;
		if (bMayTake && Delivered < Quantity)
		{
			const int32 InStore = Stored(Company, Item);
			Right = FWUi::SmallButton(LOCTEXT("DeliverStore", "Dostarcz z magazynu"), Do(TEXT("company.order.deliver"), Pay({}, { { TEXT("slot"), FWUi::Num(Order, TEXT("slot")) },
				{ TEXT("quantity"), double(FMath::Min(InStore, Quantity - Delivered)) } })), true, InStore > 0);
		}
		else if (Delivered >= Quantity)
		{
			Right = SNew(STextBlock).Text(LOCTEXT("Done", "wykonane")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Good);
		}
		Add(ItemLine(Item, FText::Format(LOCTEXT("OrderLine", "{0}  {1}/{2}  ·  nagroda {3}  ·  do {4}"), FText::FromString(FWUi::Str(Order, TEXT("name"))), Delivered, Quantity,
			FWUi::Money(FWUi::Num(Order, TEXT("rewardCents"))), FWCompanyText::In(FWUi::Num(Order, TEXT("endsAt")))), Right), 3.f);
		Add(FWUi::Bar(Quantity > 0 ? float(Delivered) / Quantity : 0.f, FWStyle::Good), 2.f);
	}
	Add(FWUi::Heading(LOCTEXT("Tasks", "Zadania z pensją")), 18.f);
	Add(FWUi::Note(LOCTEXT("TasksNote", "Przynieś do magazynu tyle sztuk, ile chce zadanie, a pensja z kasy przyjdzie od razu, za każdą sztukę po równo."), FWStyle::Muted));
	const TArray<TSharedPtr<FJsonObject>> Tasks = FWUi::Objects(Economy, TEXT("tasks"));
	if (Tasks.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoTasks", "Nie ma żadnych zadań."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Task : Tasks)
	{
		const FString Item = FWUi::Str(Task, TEXT("item"));
		const int32 Quantity = int32(FWUi::Num(Task, TEXT("quantity")));
		const int32 Delivered = int32(FWUi::Num(Task, TEXT("delivered")));
		const FString Assignee = FWUi::Str(Task, TEXT("assignee"));
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (FWUi::Bool(Task, TEXT("done")))
		{
			Right->AddSlot().AutoWidth()[SNew(STextBlock).Text(LOCTEXT("TaskDone", "wykonane")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Good)];
		}
		else
		{
			if (FWUi::Bool(Task, TEXT("yours")))
			{
				const int32 Have = Owned(Item);
				Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
				[
					FWUi::SmallButton(LOCTEXT("Bring", "Przynieś"), Do(TEXT("company.task.deliver"), Pay({}, { { TEXT("id"), FWUi::Num(Task, TEXT("id")) },
						{ TEXT("quantity"), double(FMath::Min(Have, Quantity - Delivered)) } })), true, Have > 0)
				];
			}
			if (bMoney)
			{
				Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("CancelTask", "Odwołaj"), Do(TEXT("company.task.cancel"), Pay({}, { { TEXT("id"), FWUi::Num(Task, TEXT("id")) } })))];
			}
		}
		Add(ItemLine(Item, FText::Format(LOCTEXT("TaskLine", "{0}  {1}/{2}  ·  pensja {3}  ·  {4}"), FText::FromString(FWUi::Str(Task, TEXT("name"))), Delivered, Quantity,
			FWUi::Money(FWUi::Num(Task, TEXT("wageCents"))), Assignee.IsEmpty() ? LOCTEXT("ForAll", "dla każdego") : FText::Format(LOCTEXT("For", "dla: {0}"), FText::FromString(Assignee))), Right), 3.f);
	}
	if (bMoney)
	{
		TArray<TPair<FString, FText>> Who = { { FString(), LOCTEXT("ForEveryone", "Dla każdego") } };
		for (const TSharedPtr<FJsonObject>& Member : FWUi::Objects(Company, TEXT("members")))
		{
			Who.Add({ FWUi::Str(Member, TEXT("name")), FText::FromString(FWUi::Str(Member, TEXT("name"))) });
		}
		Add(FWUi::Heading(LOCTEXT("NewTask", "Nowe zadanie")), 14.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[ItemPicker(TEXT("itemTask"), false)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("taskQuantity"), LOCTEXT("Pieces2", "szt."), 80.f, TEXT("500"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("taskWage"), LOCTEXT("Wage", "pensja $"), 100.f, TEXT("100"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Picker(TEXT("taskWho"), Who, 170.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Order", "Zleć"), [WeakThis]()
				{
					if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("company.task.create"), This->Pay({ { TEXT("item"), This->Value(TEXT("itemTask")) }, { TEXT("name"), This->Value(TEXT("taskWho")) } },
							{ { TEXT("quantity"), FCString::Atod(*This->Value(TEXT("taskQuantity"))) }, { TEXT("wageCents"), FMath::RoundToDouble(FCString::Atod(*This->Value(TEXT("taskWage"))) * 100.0) } }));
					}
				}, true)
			], 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCompanyWindow::MakeDeals(const TSharedPtr<FJsonObject>& Company)
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	static const TMap<FString, FText> States = {
		{ TEXT("offered"), LOCTEXT("Offered", "oferta") }, { TEXT("active"), LOCTEXT("Active", "trwa") }, { TEXT("done"), LOCTEXT("Finished", "zakończona") },
		{ TEXT("broken"), LOCTEXT("Broken", "zerwana") }, { TEXT("cancelled"), LOCTEXT("Cancelled", "wycofana") } };
	const TSharedPtr<FJsonObject> Economy = FWUi::Obj(Company, TEXT("economy"));
	const bool bMoney = FWUi::Bool(Company, TEXT("money"));
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("DealsAbout", "Stałe umowy z innymi spółkami: co dobę towar przechodzi z magazynu dostawcy do magazynu kupca, a pieniądze w drugą stronę. Dostawca, któremu zabraknie towaru, płaci kupcowi połowę dziennej ceny; kupiec bez pieniędzy zrywa umowę."), FWStyle::Muted), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Deals = FWUi::Objects(Economy, TEXT("deals"));
	if (Deals.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoDeals", "Spółka nie ma jeszcze żadnych umów."), FWStyle::Muted), 8.f);
	}
	for (const TSharedPtr<FJsonObject>& Deal : Deals)
	{
		const FString DealState = FWUi::Str(Deal, TEXT("state"));
		const bool bOurs = FWUi::Bool(Deal, TEXT("offeredByUs"));
		const double Id = FWUi::Num(Deal, TEXT("id"));
		FText Line = FText::Format(LOCTEXT("Deal", "{0} {1}: {2} × {3} dziennie za {4}, {5} dni  ·  {6}"),
			FWUi::Bool(Deal, TEXT("weSupply")) ? LOCTEXT("WeSupply", "Dostarczamy do") : LOCTEXT("WeBuy", "Kupujemy od"), FText::FromString(FWUi::Str(Deal, TEXT("other"))),
			int32(FWUi::Num(Deal, TEXT("perDay"))), FText::FromString(FWUi::Str(Deal, TEXT("name"))), FWUi::Money(FWUi::Num(Deal, TEXT("priceCents"))), int32(FWUi::Num(Deal, TEXT("days"))),
			States.Contains(DealState) ? States[DealState] : FText::FromString(DealState));
		if (DealState != TEXT("offered"))
		{
			Line = FText::Format(LOCTEXT("DealProgress", "{0} ({1}/{2}, braki: {3})"), Line, int32(FWUi::Num(Deal, TEXT("settled"))), int32(FWUi::Num(Deal, TEXT("days"))), int32(FWUi::Num(Deal, TEXT("missed"))));
		}
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (DealState == TEXT("offered") && bMoney)
		{
			if (!bOurs)
			{
				Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Accept", "Przyjmij"), Do(TEXT("company.deal.accept"), Pay({}, { { TEXT("id"), Id } })), true)];
			}
			Right->AddSlot().AutoWidth()[FWUi::SmallButton(bOurs ? LOCTEXT("Withdraw2", "Wycofaj") : LOCTEXT("Reject", "Odrzuć"), Do(TEXT("company.deal.cancel"), Pay({}, { { TEXT("id"), Id } })))];
		}
		Add(ItemLine(FWUi::Str(Deal, TEXT("item")), Line, Right), 3.f);
	}
	if (!bMoney)
	{
		return Page;
	}
	Add(FWUi::Heading(LOCTEXT("NewDeal", "Nowa oferta")), 18.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Picker(TEXT("dealSide"), { { TEXT("sell"), LOCTEXT("WeSell", "Sprzedajemy") }, { TEXT("buy"), LOCTEXT("WeBuyShort", "Kupujemy") } }, 150.f)]
		+ SHorizontalBox::Slot().AutoWidth()[Field(TEXT("dealOther"), LOCTEXT("OtherHint", "Nazwa drugiej spółki"), 280.f)], 4.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[ItemPicker(TEXT("itemDeal"), false)]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("PerDay", "dziennie:")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("dealPerDay"), FText::GetEmpty(), 80.f, TEXT("100"))]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("DayPrice", "cena za dzień $:")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("dealPrice"), FText::GetEmpty(), 90.f, TEXT("100"))]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("Days", "dni:")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(TEXT("dealDays"), FText::GetEmpty(), 50.f, TEXT("7"))]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("SendOffer", "Wyślij ofertę"), [WeakThis]()
			{
				if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = This->Pay({ { TEXT("company"), This->Value(TEXT("dealOther")) }, { TEXT("item"), This->Value(TEXT("itemDeal")) } },
						{ { TEXT("perDay"), FCString::Atod(*This->Value(TEXT("dealPerDay"))) }, { TEXT("priceCents"), FMath::RoundToDouble(FCString::Atod(*This->Value(TEXT("dealPrice"))) * 100.0) },
						  { TEXT("days"), FCString::Atod(*This->Value(TEXT("dealDays"))) } });
					P->SetBoolField(TEXT("sell"), This->Value(TEXT("dealSide")) != TEXT("buy"));
					This->Act(TEXT("company.deal.offer"), P);
				}
			}, true)
		], 6.f);
	return Page;
}

TSharedRef<SWidget> SFWCompanyWindow::MakeExchange()
{
	TWeakPtr<SFWCompanyWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	const TSharedPtr<FJsonObject> Company = FWUi::Obj(State, TEXT("company"));
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(FText::Format(LOCTEXT("ExchangeAbout", "Spółki na giełdzie sprzedają nowe akcje każdemu: pieniądze trafiają do ich kasy, a akcje dają dywidendę. Spółka odkupuje akcje za {0}% ich ceny, jeśli ma pieniądze. Kto ma ponad połowę akcji, może przejąć spółkę."),
		int32(FWUi::Num(Exchange, TEXT("buybackPercent"), 95.0))), FWStyle::Muted), 0.f);
	if (Company.IsValid() && FWUi::Str(Company, TEXT("role")) == TEXT("owner"))
	{
		const TSharedPtr<FJsonObject> Economy = FWUi::Obj(Company, TEXT("economy"));
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		if (FWUi::Bool(Economy, TEXT("listed")))
		{
			P->SetBoolField(TEXT("listed"), false);
			Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("Delist", "Wycofaj spółkę z giełdy"), Do(TEXT("company.list"), P))], 8.f);
		}
		else if (FWUi::Bool(Economy, TEXT("mayList")))
		{
			P->SetBoolField(TEXT("listed"), true);
			Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("List", "Wprowadź spółkę na giełdę"), Do(TEXT("company.list"), P), true)], 8.f);
		}
		else
		{
			Add(FWUi::Note(LOCTEXT("NeedFinance", "Żeby wejść na giełdę, spółka musi zbadać Finanse korporacyjne."), FWStyle::Muted), 8.f);
		}
	}
	const TArray<TSharedPtr<FJsonObject>> Companies = FWUi::Objects(Exchange, TEXT("companies"));
	if (!Exchange.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("LoadingExchange", "Wczytywanie giełdy…"), FWStyle::Muted), 8.f);
	}
	else if (Companies.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoListed", "Na giełdzie nie ma jeszcze żadnej spółki."), FWStyle::Muted), 8.f);
	}
	for (const TSharedPtr<FJsonObject>& Listed : Companies)
	{
		const FString Id = FWUi::Str(Listed, TEXT("id"));
		const int32 Yours = int32(FWUi::Num(Listed, TEXT("yours")));
		const int32 Total = int32(FWUi::Num(Listed, TEXT("shares")));
		const bool bListed = FWUi::Bool(Listed, TEXT("listed"));
		const FString Key = TEXT("shares:") + Id;
		Add(FWUi::Heading(FText::FromString(FWUi::Str(Listed, TEXT("name")) + (FWUi::Bool(Listed, TEXT("mine")) ? TEXT("  (twoja spółka)") : TEXT("")))), 16.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("Listed", "Akcja: {0}  ·  akcji: {1}  ·  wartość: {2}  ·  zarobek w tygodniu: {3}  ·  masz: {4}{5}"), FWUi::Money(FWUi::Num(Listed, TEXT("priceCents"))),
			Total, FWUi::Money(FWUi::Num(Listed, TEXT("valueCents"))), FWUi::Money(FWUi::Num(Listed, TEXT("profitCents"))), Yours,
			bListed ? FText::GetEmpty() : LOCTEXT("OffExchange", "  ·  poza giełdą")), FWStyle::Muted));
		auto Trade = [WeakThis, Id, Key](const TCHAR* Type)
		{
			return [WeakThis, Id, Key, Type]()
			{
				if (TSharedPtr<SFWCompanyWindow> This = WeakThis.Pin())
				{
					This->Act(Type, This->Pay({ { TEXT("company"), Id } }, { { TEXT("shares"), FCString::Atod(*This->Value(Key)) } }));
				}
			};
		};
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(Key, LOCTEXT("SharesHint", "akcji"), 100.f, TEXT("10"))]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("BuyShares", "Kup"), Trade(TEXT("company.shares.buy")), true, bListed)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("SellShares", "Sprzedaj"), Trade(TEXT("company.shares.sell")), false, Yours > 0)];
		if (Yours * 2 > Total && !FWUi::Bool(Listed, TEXT("mine")))
		{
			Row->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Takeover", "Przejmij spółkę"), Do(TEXT("company.takeover"), Pay({ { TEXT("company"), Id } }, {})), true)];
		}
		Add(Row, 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCompanyWindow::MakeRankings()
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	if (!Rankings.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("LoadingRankings", "Wczytywanie rankingu…"), FWStyle::Muted), 0.f);
		return Page;
	}
	TArray<double> Prizes;
	for (const TSharedPtr<FJsonValue>& Value : Rankings->HasField(TEXT("prizesCents")) ? Rankings->GetArrayField(TEXT("prizesCents")) : TArray<TSharedPtr<FJsonValue>>())
	{
		Prizes.Add(Value->AsNumber());
	}
	Prizes.SetNumZeroed(3);
	Add(FWUi::Note(FText::Format(LOCTEXT("SeasonNote", "Sezon {0} kończy się {1}. Trzy spółki, które zarobią w nim najwięcej, dostaną do kasy {2}, {3} i {4} oraz trofeum."),
		FWCompanyText::Season(int32(FWUi::Num(Rankings, TEXT("season")))), FWCompanyText::In(FWUi::Num(Rankings, TEXT("endsAt"))), FWUi::Money(Prizes[0]), FWUi::Money(Prizes[1]), FWUi::Money(Prizes[2])), FWStyle::Muted), 0.f);
	struct FList
	{
		FText Title;
		const TCHAR* Field;
		bool bMoney;
	};
	const FList Lists[] = { { LOCTEXT("Production", "Największy zarobek w sezonie"), TEXT("production"), true }, { LOCTEXT("Richest", "Najbogatsze spółki"), TEXT("richest"), true },
		{ LOCTEXT("MostPlots", "Najwięcej działek"), TEXT("plots"), false }, { LOCTEXT("LastSeason", "Zwycięzcy poprzedniego sezonu"), TEXT("lastSeason"), true } };
	for (const FList& List : Lists)
	{
		Add(FWUi::Heading(List.Title), 16.f);
		const TArray<TSharedPtr<FJsonObject>> Entries = FWUi::Objects(Rankings, List.Field);
		if (Entries.Num() == 0)
		{
			Add(FWUi::Note(LOCTEXT("NobodyYet", "Nikogo jeszcze nie ma."), FWStyle::Muted));
		}
		for (const TSharedPtr<FJsonObject>& Entry : Entries)
		{
			const bool bYours = FWUi::Bool(Entry, TEXT("yours"));
			const double Value = FWUi::Num(Entry, TEXT("value"));
			Add(TextLine(FText::Format(LOCTEXT("Entry", "{0}. {1}"), int32(FWUi::Num(Entry, TEXT("place"))), FText::FromString(FWUi::Str(Entry, TEXT("name")))), bYours ? FWStyle::Accent : FWStyle::Text,
				SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).Text(List.bMoney ? FWUi::Money(Value) : FText::AsNumber(int64(Value)))), 2.f);
		}
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
