#include "UI/SFWCountryWindow.h"

#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWCountry"

namespace FWCountryText
{
	const TCHAR* const Tabs[] = { TEXT("Kraj"), TEXT("Rząd"), TEXT("Wybory"), TEXT("Dyplomacja"), TEXT("Państwa"), TEXT("Partie") };
	enum ETab { Country, Government, Election, Diplomacy, Ranking, Parties };

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

	FText Votes(int32 N)
	{
		if (N == 1)
		{
			return LOCTEXT("OneVote", "1 głos");
		}
		const bool bFew = N % 10 >= 2 && N % 10 <= 4 && !(N % 100 >= 12 && N % 100 <= 14);
		return FText::Format(bFew ? LOCTEXT("FewVotes", "{0} głosy") : LOCTEXT("ManyVotes", "{0} głosów"), N);
	}

	FString Party(const FString& Party)
	{
		return Party.IsEmpty() ? FString() : FString::Printf(TEXT(" (%s)"), *Party);
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

	TSharedRef<SWidget> Line(const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right = nullptr)
	{
		return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(Color).AutoWrapText(true)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
			];
	}
}

void SFWCountryWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	TSharedRef<SHorizontalBox> TabRow = SNew(SHorizontalBox);
	for (int32 i = 0; i < UE_ARRAY_COUNT(FWCountryText::Tabs); ++i)
	{
		TabRow->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			FWUi::TabButton(FText::FromString(FWCountryText::Tabs[i]), [this, i]() { return Tab == i; }, [this, i]() { ShowTab(i); })
		];
	}
	TSharedRef<SWidget> Body = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			SAssignNew(Header, STextBlock).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).AutoWrapText(true)
		]
		+ SVerticalBox::Slot().FillHeight(1.f)[SAssignNew(Content, SBox)];
	TSharedRef<SWidget> Right = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 14, 0)
		[
			FWUi::SmallButton(LOCTEXT("Police", "Policja"), [this]() { if (AFWPlayerController* PC = Controller.Get()) { PC->OpenPolice(); } })
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
			.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); })
		];
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Państwo i polityka"), Right, TabRow, Body, 1050.f, 800.f)
	];
	// On the ranking first; on the player's own country when they have one
	LoadRanking();
}

void SFWCountryWindow::LoadRanking()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("country.list"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCountryWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("countries"))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		This->Ranking = Reply.Payload;
		const FString Home = FWUi::Str(Reply.Payload, TEXT("citizenship"));
		if (This->Code.IsEmpty() && !Home.IsEmpty())
		{
			This->Tab = FWCountryText::Country;
			This->ShowCountry(Home);
			return;
		}
		This->Rebuild();
	});
}

void SFWCountryWindow::LoadParties()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("party.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCountryWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type == TEXT("party"))
		{
			This->Parties = Reply.Payload;
			This->Rebuild();
		}
		else if (AFWPlayerController* Inner = This->Controller.Get())
		{
			Inner->ToastError(Reply);
		}
	});
}

void SFWCountryWindow::LoadCountry()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || Code.IsEmpty())
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("country"), Code);
	Act(TEXT("country.get"), P, nullptr);
}

void SFWCountryWindow::ShowCountry(const FString& InCode)
{
	Code = InCode;
	State.Reset();
	if (Tab == FWCountryText::Ranking || Tab == FWCountryText::Parties)
	{
		Tab = FWCountryText::Country;
	}
	LoadCountry();
	Rebuild();
}

void SFWCountryWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	if (Scroll.IsValid())
	{
		Scroll->SetScrollOffset(0.f);
	}
	if (Tab == FWCountryText::Ranking)
	{
		LoadRanking();
	}
	else if (Tab == FWCountryText::Parties)
	{
		LoadParties();
	}
	Rebuild();
}

void SFWCountryWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<FText(const TSharedPtr<FJsonObject>&)> Done)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetStringField(TEXT("country"), Code);
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, Done](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCountryWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("country"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->State = FWUi::Obj(Reply.Payload, TEXT("state"));
		This->Rebuild();
		if (Done)
		{
			Inner->Toast(Done(FWUi::Obj(Reply.Payload, TEXT("result"))));
			Inner->PlayUiSound(TEXT("build_pickup"));
			FWCountryText::Wallet(Inner);
		}
	});
}

void SFWCountryWindow::PartyAct(const TCHAR* Type, const FString& Name)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("name"), Name);
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	PC->Send(Type, P, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWCountryWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("party"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->Parties = Reply.Payload;
		This->Rebuild();
		Inner->PlayUiSound(TEXT("build_pickup"));
		FWCountryText::Wallet(Inner);
	});
}

FText SFWCountryWindow::OfficeTitle(const FString& Office) const
{
	if (Office == TEXT("president"))
	{
		return LOCTEXT("President", "prezydent");
	}
	for (const TSharedPtr<FJsonObject>& M : FWUi::Objects(FWUi::Obj(State, TEXT("government")), TEXT("ministers")))
	{
		if (FWUi::Str(M, TEXT("office")) == Office)
		{
			return FText::FromString(FWUi::Str(M, TEXT("title")).ToLower());
		}
	}
	return FText::FromString(Office);
}

void SFWCountryWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	if (State.IsValid())
	{
		const FString President = FWUi::Str(FWUi::Obj(State, TEXT("government")), TEXT("president"));
		Header->SetText(FText::Format(LOCTEXT("Header", "{0} · {1} obywateli · prezydent: {2} · kadencja kończy się za {3}"), FText::FromString(FWUi::Str(State, TEXT("name"))),
			int32(FWUi::Num(State, TEXT("citizens"))), President.IsEmpty() ? LOCTEXT("None", "brak") : FText::FromString(President), FWCountryText::Left(FWUi::Num(State, TEXT("termEndsAt")))));
	}
	else
	{
		Header->SetText(LOCTEXT("Pick", "Wybierz kraj z listy państw."));
	}
	TSharedPtr<SWidget> Page;
	const bool bNeedsCountry = Tab <= FWCountryText::Diplomacy && !State.IsValid();
	if (bNeedsCountry)
	{
		Page = FWUi::Note(Code.IsEmpty() ? LOCTEXT("PickFirst", "Najpierw wybierz kraj w zakładce Państwa.") : LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	else
	{
		switch (Tab)
		{
		case FWCountryText::Government: Page = MakeGovernment(); break;
		case FWCountryText::Election: Page = MakeElection(); break;
		case FWCountryText::Diplomacy: Page = MakeDiplomacy(); break;
		case FWCountryText::Ranking: Page = MakeRanking(); break;
		case FWCountryText::Parties: Page = MakeParties(); break;
		default: Page = MakeCountry(); break;
		}
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWCountryWindow::MakeCountry()
{
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Me = FWUi::Obj(State, TEXT("me"));
	const FString Home = FWUi::Str(Me, TEXT("citizenship"));
	const int32 PlotsHere = int32(FWUi::Num(Me, TEXT("plotsHere")));
	if (Home == FWUi::Str(State, TEXT("code")))
	{
		Add(FWUi::Note(LOCTEXT("Citizen", "Jesteś obywatelem tego kraju."), FWStyle::Good), 0.f);
		if (PlotsHere == 0)
		{
			Add(FWUi::Note(LOCTEXT("NoPlotHere", "Nie masz już tu działki: dopóki jej nie kupisz, nie głosujesz i nie kandydujesz."), FWStyle::Muted));
		}
	}
	else
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("YourCitizenship", "Twoje obywatelstwo: {0}."), Home.IsEmpty() ? LOCTEXT("NoneCitizenship", "żadne") : FText::FromString(FWUi::Str(Me, TEXT("citizenshipName")))), FWStyle::Text), 0.f);
		if (PlotsHere > 0)
		{
			double ChangeAt = 0.0;
			const bool bWait = Me->TryGetNumberField(TEXT("changeAt"), ChangeAt);
			if (bWait)
			{
				Add(FWUi::Note(FText::Format(LOCTEXT("ChangeLater", "Obywatelstwo zmienisz za {0} (raz w tygodniu)."), FWCountryText::Left(ChangeAt)), FWStyle::Muted));
			}
			Add(SNew(SBox).HAlign(HAlign_Left)
				[
					FWUi::SmallButton(LOCTEXT("TakeCitizenship", "Przyjmij obywatelstwo"), [WeakThis]()
					{
						if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
						{
							const FString Name = FWUi::Str(This->State, TEXT("name"));
							This->Act(TEXT("country.citizenship"), MakeShared<FJsonObject>(), [Name](const TSharedPtr<FJsonObject>&) { return FText::Format(LOCTEXT("NowCitizen", "Jesteś obywatelem: {0}"), FText::FromString(Name)); });
						}
					}, true, !bWait)
				], 6.f);
		}
		else
		{
			Add(FWUi::Note(LOCTEXT("BuyToBe", "Kup działkę w tym kraju, by zostać jego obywatelem: obywatele wybierają prezydenta i sami kandydują."), FWStyle::Muted));
		}
	}
	if (!FWUi::Str(Me, TEXT("party")).IsEmpty())
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("YourParty", "Twoja partia: {0}"), FText::FromString(FWUi::Str(Me, TEXT("party")))), FWStyle::Muted));
	}
	TArray<FString> Offices;
	for (const FString& Office : FWUi::Strings(Me, TEXT("offices")))
	{
		Offices.Add(OfficeTitle(Office).ToString());
	}
	if (Offices.Num() > 0)
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("YourOffices", "Twoje urzędy: {0}"), FText::FromString(FString::Join(Offices, TEXT(", ")))), FWStyle::Accent));
	}
	const TSharedPtr<FJsonObject> Government = FWUi::Obj(State, TEXT("government"));
	const FString President = FWUi::Str(Government, TEXT("president"));
	Add(FWUi::Heading(FText::Format(LOCTEXT("PresidentIs", "Prezydent: {0}{1}"), President.IsEmpty() ? LOCTEXT("NoPresident", "brak") : FText::FromString(President),
		FText::FromString(FWCountryText::Party(FWUi::Str(Government, TEXT("presidentParty")))))), 16.f);
	if (FWUi::Bool(Government, TEXT("recalled")))
	{
		Add(FWUi::Note(LOCTEXT("PresidentRecalled", "Obywatele odwołali prezydenta w tej kadencji; prawo wróciło do ustawień domyślnych."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& M : FWUi::Objects(Government, TEXT("ministers")))
	{
		if (!FWUi::Str(M, TEXT("name")).IsEmpty())
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("Minister", "{0}: {1}"), FText::FromString(FWUi::Str(M, TEXT("title"))), FText::FromString(FWUi::Str(M, TEXT("name")))), FWStyle::Text), 2.f);
		}
	}
	const TSharedPtr<FJsonObject> Laws = FWUi::Obj(Government, TEXT("laws"));
	Add(FWUi::Heading(LOCTEXT("Law", "Prawo")), 16.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("PlotTax", "Podatek od działek: {0}‰ wartości dziennie ({1}% trafia do skarbca)"), int32(FWUi::Num(Laws, TEXT("plotTaxPerMille"))),
		int32(FWUi::Num(Laws, TEXT("treasurySharePercent")))), FWStyle::Text));
	Add(FWUi::Note(FText::Format(LOCTEXT("ForeignerFee", "Opłata od cudzoziemców za działkę: {0}%"), int32(FWUi::Num(Laws, TEXT("foreignerFeePercent")))), FWStyle::Text));
	TArray<FString> ItemTariffs;
	for (const TSharedPtr<FJsonObject>& T : FWUi::Objects(Laws, TEXT("itemTariffs")))
	{
		ItemTariffs.Add(FString::Printf(TEXT("%s %d%%"), *FWUi::Str(T, TEXT("name")), int32(FWUi::Num(T, TEXT("percent")))));
	}
	Add(FWUi::Note(FText::Format(LOCTEXT("Tariff", "Cło: {0}%{1}"), int32(FWUi::Num(Laws, TEXT("tariffPercent"))),
		ItemTariffs.Num() > 0 ? FText::FromString(FString::Printf(TEXT(" (osobno: %s)"), *FString::Join(ItemTariffs, TEXT(", ")))) : FText::GetEmpty()), FWStyle::Text));
	FText Subsidy = LOCTEXT("NoSubsidy", "brak");
	for (const TSharedPtr<FJsonObject>& S : FWUi::Objects(Laws, TEXT("subsidies")))
	{
		if (FWUi::Str(S, TEXT("id")) == FWUi::Str(Laws, TEXT("subsidy")))
		{
			Subsidy = FText::Format(LOCTEXT("SubsidyIs", "{0}: miasta płacą o {1}% więcej, dopłaca skarbiec"), FText::FromString(FWUi::Str(S, TEXT("name"))), int32(FWUi::Num(Laws, TEXT("subsidyPercent"))));
		}
	}
	Add(FWUi::Note(FText::Format(LOCTEXT("Subsidy", "Dotacja: {0}"), Subsidy), FWStyle::Text));
	const TSharedPtr<FJsonObject> Treasury = FWUi::Obj(State, TEXT("treasury"));
	Add(FWUi::Heading(FText::Format(LOCTEXT("Treasury", "Skarbiec: {0}"), FWUi::Money(FWUi::Num(Treasury, TEXT("cents"))))), 16.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("PresidentShare", "Prezydent dostaje {0}% wpływów jako pensję."), int32(FWUi::Num(Treasury, TEXT("presidentSharePercent")))), FWStyle::Muted));
	for (const TSharedPtr<FJsonObject>& Line : FWUi::Objects(Treasury, TEXT("ledger")))
	{
		const double Cents = FWUi::Num(Line, TEXT("cents"));
		Add(FWUi::Note(FText::Format(LOCTEXT("Ledger", "{0}{1}  {2}"), FText::FromString(Cents > 0.0 ? TEXT("+") : TEXT("")), FWUi::Money(Cents), FText::FromString(FWUi::Str(Line, TEXT("reason")))),
			Cents > 0.0 ? FWStyle::Good : FWStyle::Error), 2.f);
	}
	for (const TSharedPtr<FJsonObject>& I : FWUi::Objects(State, TEXT("investments")))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("Investment", "{0}: poziom {1} z {2} ({3})"), FText::FromString(FWUi::Str(I, TEXT("name"))), int32(FWUi::Num(I, TEXT("level"))),
			int32(FWUi::Num(I, TEXT("maxLevel"))), FText::FromString(FWUi::Str(I, TEXT("effect")))), FWStyle::Text), 2.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCountryWindow::MakeGovernment()
{
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TArray<FString> Offices = FWUi::Strings(FWUi::Obj(State, TEXT("me")), TEXT("offices"));
	const bool bPresident = Offices.Contains(TEXT("president"));
	if (Offices.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoOffice", "Tu rządzą prezydent i ministrowie. Zostań obywatelem i wygraj wybory albo poproś prezydenta o urząd ministra."), FWStyle::Muted), 0.f);
		return Page;
	}
	auto SetLaw = [WeakThis](const FString& Law, const FString& ValueKey, const FString& ItemKey)
	{
		return [WeakThis, Law, ValueKey, ItemKey]()
		{
			if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
			{
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetStringField(TEXT("law"), Law);
				P->SetNumberField(TEXT("value"), ValueKey.IsEmpty() ? 0.0 : This->Form->Number(ValueKey));
				P->SetStringField(TEXT("item"), ItemKey.IsEmpty() ? FString() : This->Form->Get(ItemKey));
				This->Act(TEXT("country.law"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("LawSet", "Ustawiono nowe prawo"); });
			}
		};
	};
	const TSharedPtr<FJsonObject> Laws = FWUi::Obj(FWUi::Obj(State, TEXT("government")), TEXT("laws"));
	if (bPresident || Offices.Contains(TEXT("finance")))
	{
		Add(FWUi::Heading(LOCTEXT("Finance", "Minister finansów")), 0.f);
		struct FLaw
		{
			const TCHAR* Law;
			FText Name;
			const TCHAR* Field;
		};
		const FLaw LawRows[] = { { TEXT("plotTax"), LOCTEXT("PlotTaxLaw", "Podatek od działek (‰ wartości dziennie)"), TEXT("plotTaxPerMille") },
			{ TEXT("foreignerFee"), LOCTEXT("FeeLaw", "Opłata od cudzoziemców za działkę (%)"), TEXT("foreignerFeePercent") },
			{ TEXT("tariff"), LOCTEXT("TariffLaw", "Cło na towary z innych krajów (%)"), TEXT("tariffPercent") } };
		for (const FLaw& Row : LawRows)
		{
			const FString Key = FString(TEXT("law:")) + Row.Law;
			Add(FWCountryText::Line(Row.Name, FWStyle::Text,
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(Key, FText::GetEmpty(), 70.f, FString::FromInt(int32(FWUi::Num(Laws, Row.Field))))]
				+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Set", "Ustaw"), SetLaw(Row.Law, Key, FString()))]), 3.f);
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("tariffItem"), LOCTEXT("ItemHint", "Id towaru, np. coal"), 220.f)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				SNew(SBox).ToolTipText(LOCTEXT("MinusOne", "-1 zdejmuje cło na ten towar"))[Form->Field(TEXT("tariffPercent"), FText::GetEmpty(), 70.f, TEXT("10"))]
			]
			+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("ItemTariff", "Cło na towar"), SetLaw(TEXT("itemTariff"), TEXT("tariffPercent"), TEXT("tariffItem")))], 6.f);
	}
	if (bPresident || Offices.Contains(TEXT("industry")))
	{
		Add(FWUi::Heading(LOCTEXT("Industry", "Minister przemysłu")), 16.f);
		TArray<TPair<FString, FText>> Subsidies = { { FString(), LOCTEXT("WithoutSubsidy", "Bez dotacji") } };
		for (const TSharedPtr<FJsonObject>& S : FWUi::Objects(Laws, TEXT("subsidies")))
		{
			Subsidies.Add({ FWUi::Str(S, TEXT("id")), FText::Format(LOCTEXT("SubsidyOption", "{0} (+{1}%)"), FText::FromString(FWUi::Str(S, TEXT("name"))), int32(FWUi::Num(Laws, TEXT("subsidyPercent")))) });
		}
		if (!Form->Has(TEXT("subsidy")))
		{
			Form->Set(TEXT("subsidy"), FWUi::Str(Laws, TEXT("subsidy")));
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Pick(TEXT("subsidy"), Subsidies, 320.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("SetSubsidy", "Ustaw dotację"), [WeakThis]()
				{
					if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("law"), TEXT("subsidy"));
						P->SetNumberField(TEXT("value"), 0.0);
						P->SetStringField(TEXT("item"), This->Form->Get(TEXT("subsidy")));
						This->Act(TEXT("country.law"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("LawSet2", "Ustawiono nowe prawo"); });
					}
				})
			], 4.f);
		Add(FWUi::Note(LOCTEXT("SubsidyNote", "Miasta w kraju płacą za te towary więcej, a różnicę dopłaca skarbiec, dopóki ma pieniądze."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& I : FWUi::Objects(State, TEXT("investments")))
	{
		if (!bPresident && !Offices.Contains(FWUi::Str(I, TEXT("office"))))
		{
			continue;
		}
		const FString Id = FWUi::Str(I, TEXT("id"));
		double Next = 0.0;
		TSharedPtr<SWidget> Right;
		if (I->TryGetNumberField(TEXT("nextCents"), Next))
		{
			Right = FWUi::SmallButton(FText::Format(LOCTEXT("Build", "Buduj za {0}"), FWUi::Money(Next)), [WeakThis, Id]()
			{
				if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("kind"), Id);
					This->Act(TEXT("country.invest"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Invested", "Inwestycja zbudowana ze skarbca"); });
				}
			});
		}
		Add(FWCountryText::Line(FText::Format(LOCTEXT("InvestmentLine", "{0} (poziom {1}/{2}): {3}"), FText::FromString(FWUi::Str(I, TEXT("name"))), int32(FWUi::Num(I, TEXT("level"))),
			int32(FWUi::Num(I, TEXT("maxLevel"))), FText::FromString(FWUi::Str(I, TEXT("effect")))), FWStyle::Text, Right), 3.f);
	}
	if (bPresident)
	{
		Add(FWUi::Heading(LOCTEXT("Ministers", "Ministrowie")), 16.f);
		for (const TSharedPtr<FJsonObject>& M : FWUi::Objects(FWUi::Obj(State, TEXT("government")), TEXT("ministers")))
		{
			const FString Office = FWUi::Str(M, TEXT("office"));
			const FString Key = TEXT("minister:") + Office;
			Add(FWCountryText::Line(FText::FromString(FWUi::Str(M, TEXT("title"))), FWStyle::Text,
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(Key, LOCTEXT("CitizenName", "Imię obywatela"), 220.f, FWUi::Str(M, TEXT("name")))]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					FWUi::SmallButton(LOCTEXT("Appoint", "Mianuj"), [WeakThis, Office, Key]()
					{
						if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
						{
							const FString Name = This->Form->Get(Key);
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetStringField(TEXT("office"), Office);
							P->SetStringField(TEXT("name"), Name);
							This->Act(TEXT("country.appoint"), P, [Name](const TSharedPtr<FJsonObject>&)
							{
								return Name.IsEmpty() ? LOCTEXT("OfficeFree", "Urząd jest wolny") : FText::Format(LOCTEXT("Appointed", "Mianowano: {0}"), FText::FromString(Name));
							});
						}
					})
				]), 3.f);
		}
	}
	return Page;
}

TSharedRef<SWidget> SFWCountryWindow::MakeElection()
{
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Election = FWUi::Obj(State, TEXT("election"));
	const bool bVoter = FWUi::Bool(FWUi::Obj(State, TEXT("me")), TEXT("voter"));
	const FString MyVote = FWUi::Str(Election, TEXT("myVote"));
	Add(FWUi::Note(FText::Format(LOCTEXT("ElectionNote", "Wybory prezydenta na następną kadencję, koniec za {0}. Głosują i kandydują obywatele z działką w kraju."),
		FWCountryText::Left(FWUi::Num(State, TEXT("termEndsAt")))), FWStyle::Muted), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Candidates = FWUi::Objects(Election, TEXT("candidates"));
	if (Candidates.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoCandidates", "Nikt jeszcze nie kandyduje."), FWStyle::Muted), 6.f);
	}
	int32 Total = 0;
	for (const TSharedPtr<FJsonObject>& C : Candidates)
	{
		Total += int32(FWUi::Num(C, TEXT("votes")));
	}
	TArray<TPair<FString, FText>> Names;
	for (const TSharedPtr<FJsonObject>& C : Candidates)
	{
		const FString Name = FWUi::Str(C, TEXT("name"));
		Names.Add({ Name, FText::FromString(Name) });
		const bool bMine = MyVote == Name;
		const int32 VoteCount = int32(FWUi::Num(C, TEXT("votes")));
		FString Program = FString::Printf(TEXT("Podatek %d‰ · opłata od cudzoziemców %d%%"), int32(FWUi::Num(C, TEXT("plotTaxPerMille"))), int32(FWUi::Num(C, TEXT("foreignerFeePercent"))));
		if (!FWUi::Str(C, TEXT("slogan")).IsEmpty())
		{
			Program += FString::Printf(TEXT(" · „%s”"), *FWUi::Str(C, TEXT("slogan")));
		}
		double Backing = 0.0;
		TArray<FString> Backers;
		for (const TSharedPtr<FJsonObject>& S : FWUi::Objects(C, TEXT("sponsors")))
		{
			Backing += FWUi::Num(S, TEXT("cents"));
			Backers.Add(FWUi::Str(S, TEXT("company")));
		}
		if (Backers.Num() > 0)
		{
			Program += FString::Printf(TEXT("\nWsparcie: %s (%s)"), *FWUi::Money(Backing).ToString(), *FString::Join(Backers, TEXT(", ")));
		}
		Add(FWCountryText::Line(FText::Format(LOCTEXT("Candidate", "{0}{1}{2} · {3} · {4}%\n{5}"), FText::FromString(Name), FWUi::Bool(C, TEXT("me")) ? LOCTEXT("Me", " (ty)") : FText::GetEmpty(),
			FText::FromString(FWCountryText::Party(FWUi::Str(C, TEXT("party")))), FWCountryText::Votes(VoteCount), Total > 0 ? FMath::RoundToInt(100.0 * VoteCount / Total) : 0, FText::FromString(Program)),
			bMine ? FWStyle::Accent : FWStyle::Text,
			bMine ? StaticCastSharedRef<SWidget>(SNew(STextBlock).Text(LOCTEXT("YourVote", "twój głos")).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent))
				: FWUi::SmallButton(LOCTEXT("Vote", "Głosuj"), [WeakThis, Name]()
				{
					if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("candidate"), Name);
						This->Act(TEXT("country.vote"), P, [Name](const TSharedPtr<FJsonObject>&) { return FText::Format(LOCTEXT("Voted", "Oddano głos na: {0}"), FText::FromString(Name)); });
					}
				}, true, bVoter)), 4.f);
	}
	if (Candidates.Num() > 0)
	{
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("BackFrom", "Wesprzyj z kasy spółki:")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Pick(TEXT("sponsorWhom"), Names, 220.f)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("sponsorAmount"), LOCTEXT("Dollars", "$"), 100.f, TEXT("100"))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Back", "Wesprzyj"), [WeakThis]()
				{
					if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
					{
						const FString Name = This->Form->Get(TEXT("sponsorWhom"));
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("candidate"), Name);
						P->SetNumberField(TEXT("cents"), This->Form->Cents(TEXT("sponsorAmount")));
						This->Act(TEXT("country.sponsor"), P, [Name](const TSharedPtr<FJsonObject>&) { return FText::Format(LOCTEXT("Backed", "Spółka wsparła kandydata: {0}"), FText::FromString(Name)); });
					}
				})
			], 8.f);
	}
	if (bVoter)
	{
		const bool bStanding = FWUi::Bool(Election, TEXT("standing"));
		const TSharedPtr<FJsonObject> Laws = FWUi::Obj(FWUi::Obj(State, TEXT("government")), TEXT("laws"));
		Add(FWUi::Heading(bStanding ? LOCTEXT("ChangeProgram", "Zmień swój program") : LOCTEXT("Stand", "Kandyduj")), 18.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("TaxMille", "Podatek ‰")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Field(TEXT("standTax"), FText::GetEmpty(), 60.f, FString::FromInt(int32(FWUi::Num(Laws, TEXT("plotTaxPerMille")))))]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("FeePercent", "Opłata %")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Field(TEXT("standFee"), FText::GetEmpty(), 60.f, FString::FromInt(int32(FWUi::Num(Laws, TEXT("foreignerFeePercent")))))]
			+ SHorizontalBox::Slot().AutoWidth()[Form->Field(TEXT("standSlogan"), LOCTEXT("Slogan", "Hasło wyborcze"), 300.f)], 6.f);
		TSharedRef<SHorizontalBox> Buttons = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(bStanding ? LOCTEXT("SaveProgram", "Zapisz program") : FText::Format(LOCTEXT("StandFor", "Kandyduj (kaucja {0})"), FWUi::Money(FWUi::Num(Election, TEXT("depositCents")))),
					[WeakThis]()
					{
						if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetNumberField(TEXT("plotTaxPerMille"), This->Form->Number(TEXT("standTax")));
							P->SetNumberField(TEXT("foreignerFeePercent"), This->Form->Number(TEXT("standFee")));
							P->SetStringField(TEXT("slogan"), This->Form->Get(TEXT("standSlogan")));
							This->Act(TEXT("country.stand"), P, [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Standing", "Kandydujesz na prezydenta"); });
						}
					}, true)
			];
		if (bStanding)
		{
			Buttons->AddSlot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("WithdrawCandidacy", "Wycofaj się"), [WeakThis]()
				{
					if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("country.withdraw"), MakeShared<FJsonObject>(), [](const TSharedPtr<FJsonObject>&) { return LOCTEXT("Withdrawn", "Wycofałeś kandydaturę, kaucja wróciła"); });
					}
				})
			];
		}
		Add(Buttons, 6.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("DepositRule", "Kaucja wraca przy co najmniej {0}% głosów, inaczej trafia do skarbca."), int32(FWUi::Num(Election, TEXT("depositBackPercent")))), FWStyle::Muted));
	}
	if (!FWUi::Str(FWUi::Obj(State, TEXT("government")), TEXT("president")).IsEmpty())
	{
		const TSharedPtr<FJsonObject> Recall = FWUi::Obj(State, TEXT("recall"));
		Add(FWCountryText::Line(FText::Format(LOCTEXT("Recall", "Wotum nieufności: {0} z {1} podpisów (2/3 obywateli)"), int32(FWUi::Num(Recall, TEXT("signatures"))), int32(FWUi::Num(Recall, TEXT("needed")))),
			FWStyle::Text, FWUi::SmallButton(LOCTEXT("SignRecall", "Podpisz odwołanie"), [WeakThis]()
			{
				if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
				{
					This->Act(TEXT("country.recall"), MakeShared<FJsonObject>(), [](const TSharedPtr<FJsonObject>& Result)
					{
						return FWUi::Bool(Result, TEXT("recalled")) ? LOCTEXT("PresidentOut", "Prezydent odwołany!") : LOCTEXT("SignedRecall", "Podpisano wniosek o odwołanie");
					});
				}
			}, false, bVoter && !FWUi::Bool(Recall, TEXT("signed")))), 18.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCountryWindow::MakeDiplomacy()
{
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Relations = FWUi::Obj(State, TEXT("relations"));
	const TPair<const TCHAR*, FText> Parts[] = { { TEXT("allies"), LOCTEXT("Allies", "Sojusznicy (bez opłat i ceł między sobą)") },
		{ TEXT("embargoes"), LOCTEXT("Embargoes", "Embargo nałożone przez ten kraj") }, { TEXT("embargoedBy"), LOCTEXT("EmbargoedBy", "Embargo na ten kraj") },
		{ TEXT("offersMade"), LOCTEXT("OffersMade", "Propozycje sojuszu wysłane") }, { TEXT("offersGot"), LOCTEXT("OffersGot", "Propozycje sojuszu otrzymane") } };
	for (const TPair<const TCHAR*, FText>& Part : Parts)
	{
		TArray<FString> Names;
		for (const TSharedPtr<FJsonObject>& C : FWUi::Objects(Relations, Part.Key))
		{
			Names.Add(FWUi::Str(C, TEXT("name")));
		}
		Add(FWCountryText::Line(FText::Format(LOCTEXT("Relation", "{0}: {1}"), Part.Value, Names.Num() > 0 ? FText::FromString(FString::Join(Names, TEXT(", "))) : LOCTEXT("NoneRelation", "brak")), FWStyle::Text), 3.f);
	}
	const TArray<FString> Offices = FWUi::Strings(FWUi::Obj(State, TEXT("me")), TEXT("offices"));
	if (!Offices.Contains(TEXT("president")) && !Offices.Contains(TEXT("foreign")))
	{
		Add(FWUi::Note(LOCTEXT("OnlyForeign", "Sojusze i embarga ustala prezydent albo minister spraw zagranicznych."), FWStyle::Muted), 10.f);
		return Page;
	}
	auto Relate = [WeakThis](const TCHAR* Type, bool bOn)
	{
		return [WeakThis, Type, bOn]()
		{
			TSharedPtr<SFWCountryWindow> This = WeakThis.Pin();
			if (!This)
			{
				return;
			}
			// The other country by its name in the ranking, else as typed (a code)
			const FString Typed = This->Form->Get(TEXT("other"));
			FString Other = Typed.ToUpper();
			for (const TSharedPtr<FJsonObject>& C : FWUi::Objects(This->Ranking, TEXT("countries")))
			{
				if (FWUi::Str(C, TEXT("name")).Equals(Typed, ESearchCase::IgnoreCase))
				{
					Other = FWUi::Str(C, TEXT("code"));
				}
			}
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("other"), Other);
			P->SetBoolField(TEXT("on"), bOn);
			This->Act(Type, P, [](const TSharedPtr<FJsonObject>& Result) { return FWUi::Bool(Result, TEXT("allied")) ? LOCTEXT("Allied", "Sojusz zawarty") : LOCTEXT("Saved", "Zapisano"); });
		};
	};
	Add(Form->Field(TEXT("other"), LOCTEXT("OtherHint", "Nazwa kraju, np. Niemcy"), 320.f), 14.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("ProposeAlliance", "Zaproponuj sojusz"), Relate(TEXT("country.alliance"), true), true)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("BreakAlliance", "Zerwij sojusz"), Relate(TEXT("country.alliance"), false))]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Embargo", "Nałóż embargo"), Relate(TEXT("country.embargo"), true))]
		+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("LiftEmbargo", "Znieś embargo"), Relate(TEXT("country.embargo"), false))], 6.f);
	Add(FWUi::Note(LOCTEXT("DiplomacyNote", "Sojusz zaczyna obowiązywać, gdy drugi kraj też go zaproponuje. Embargo: obywatele tamtego kraju nie kupią tu działek, a ich towary nie wjadą."), FWStyle::Muted));
	return Page;
}

TSharedRef<SWidget> SFWCountryWindow::MakeRanking()
{
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	if (!Ranking.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("LoadingRanking", "Wczytywanie…"), FWStyle::Muted), 0.f);
		return Page;
	}
	Add(FWUi::Note(LOCTEXT("RankingNote", "Kraje, w których gracze mają działki, od największych."), FWStyle::Muted), 0.f);
	int32 Place = 0;
	for (const TSharedPtr<FJsonObject>& C : FWUi::Objects(Ranking, TEXT("countries")))
	{
		++Place;
		const FString CountryCode = FWUi::Str(C, TEXT("code"));
		const FString President = FWUi::Str(C, TEXT("president"));
		Add(FWCountryText::Line(FText::Format(LOCTEXT("RankLine", "{0}. {1} · {2} działek · {3} obywateli · skarbiec {4}{5}"), Place, FText::FromString(FWUi::Str(C, TEXT("name"))),
			int32(FWUi::Num(C, TEXT("boughtPlots"))), int32(FWUi::Num(C, TEXT("citizens"))), FWUi::Money(FWUi::Num(C, TEXT("treasuryCents"))),
			President.IsEmpty() ? FText::GetEmpty() : FText::Format(LOCTEXT("WithPresident", " · prezydent: {0}"), FText::FromString(President))),
			FWUi::Bool(C, TEXT("mine")) ? FWStyle::Accent : FWStyle::Text,
			FWUi::SmallButton(LOCTEXT("Show", "Pokaż"), [WeakThis, CountryCode]() { if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin()) { This->ShowCountry(CountryCode); } })), 3.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWCountryWindow::MakeParties()
{
	TWeakPtr<SFWCountryWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	if (!Parties.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("LoadingParties", "Wczytywanie…"), FWStyle::Muted), 0.f);
		return Page;
	}
	const TSharedPtr<FJsonObject> Mine = FWUi::Obj(Parties, TEXT("mine"));
	if (Mine.IsValid())
	{
		Add(FWUi::Heading(FText::Format(LOCTEXT("MyParty", "Twoja partia: {0} · lider: {1}"), FText::FromString(FWUi::Str(Mine, TEXT("name"))), FText::FromString(FWUi::Str(Mine, TEXT("leader"))))), 0.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("Members", "Członkowie: {0}"), FText::FromString(FString::Join(FWUi::Strings(Mine, TEXT("members")), TEXT(", ")))), FWStyle::Muted));
		Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("LeaveParty", "Wystąp z partii"), [WeakThis]() { if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin()) { This->PartyAct(TEXT("party.leave"), FString()); } })], 6.f);
	}
	else
	{
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("partyName"), LOCTEXT("PartyName", "Nazwa partii"), 300.f)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(FText::Format(LOCTEXT("Found", "Załóż ({0})"), FWUi::Money(FWUi::Num(Parties, TEXT("costCents")))), [WeakThis]()
				{
					if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin())
					{
						This->PartyAct(TEXT("party.create"), This->Form->Get(TEXT("partyName")));
					}
				}, true)
			]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Join", "Dołącz"), [WeakThis]() { if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin()) { This->PartyAct(TEXT("party.join"), This->Form->Get(TEXT("partyName"))); } })
			], 0.f);
		Add(FWUi::Note(LOCTEXT("PartyNote", "Nazwa partii pokazuje się przy twoim imieniu w wyborach w miastach i krajach."), FWStyle::Muted));
	}
	Add(FWUi::Heading(LOCTEXT("AllParties", "Partie")), 16.f);
	for (const TSharedPtr<FJsonObject>& P : FWUi::Objects(Parties, TEXT("parties")))
	{
		const FString Name = FWUi::Str(P, TEXT("name"));
		TSharedPtr<SWidget> Right;
		if (!Mine.IsValid())
		{
			Right = FWUi::SmallButton(LOCTEXT("JoinThis", "Dołącz"), [WeakThis, Name]() { if (TSharedPtr<SFWCountryWindow> This = WeakThis.Pin()) { This->PartyAct(TEXT("party.join"), Name); } });
		}
		Add(FWCountryText::Line(FText::Format(LOCTEXT("Party", "{0} · lider {1} · {2} członków · prezydentów: {3}"), FText::FromString(Name), FText::FromString(FWUi::Str(P, TEXT("leader"))),
			int32(FWUi::Num(P, TEXT("members"))), int32(FWUi::Num(P, TEXT("presidents")))), FWStyle::Text, Right), 3.f);
	}
	return Page;
}

FReply SFWCountryWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
