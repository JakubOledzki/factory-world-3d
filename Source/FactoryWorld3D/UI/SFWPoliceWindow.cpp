#include "UI/SFWPoliceWindow.h"

#include "Framework/Application/SlateApplication.h"
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

#define LOCTEXT_NAMESPACE "FWPolice"

namespace FWPoliceText
{
	const TCHAR* const Tabs[] = { TEXT("Prawo"), TEXT("Służba"), TEXT("Sąd"), TEXT("Paser") };
	enum ETab { Law, Duty, Court, Fence };

	FText Rank(int32 Rank)
	{
		static const FText Ranks[] = { LOCTEXT("Cadet", "Kadet"), LOCTEXT("Constable", "Posterunkowy"), LOCTEXT("Detective", "Detektyw"), LOCTEXT("Chief", "Komendant") };
		return Ranks[FMath::Clamp(Rank, 0, 3)];
	}

	FText Crime(const FString& Kind)
	{
		static const TMap<FString, FText> Crimes = { { TEXT("smuggling"), LOCTEXT("Smuggling", "Przemyt") }, { TEXT("contraband"), LOCTEXT("Contraband", "Zakazane towary") },
			{ TEXT("logging"), LOCTEXT("Logging", "Nielegalna wycinka") }, { TEXT("black_market"), LOCTEXT("BlackMarket", "Czarny rynek") }, { TEXT("bribe"), LOCTEXT("Bribe", "Łapówka") } };
		return Crimes.Contains(Kind) ? Crimes[Kind] : FText::FromString(Kind);
	}

	FText Status(const FString& Status)
	{
		static const TMap<FString, FText> Statuses = { { TEXT("open"), LOCTEXT("Open", "Otwarta") }, { TEXT("sentenced"), LOCTEXT("Sentenced", "Wyrok") },
			{ TEXT("dismissed"), LOCTEXT("Dismissed", "Umorzona") }, { TEXT("appealed"), LOCTEXT("Appealed", "Odwołanie") }, { TEXT("upheld"), LOCTEXT("Upheld", "Wyrok utrzymany") },
			{ TEXT("overturned"), LOCTEXT("Overturned", "Wyrok uchylony") } };
		return Statuses.Contains(Status) ? Statuses[Status] : FText::FromString(Status);
	}

	FText Sanction(const FString& Kind)
	{
		static const TMap<FString, FText> Sanctions = { { TEXT("trade"), LOCTEXT("TradeBan", "Zakaz handlu") }, { TEXT("travel"), LOCTEXT("TravelBan", "Zakaz przekraczania granic") },
			{ TEXT("wanted"), LOCTEXT("Wanted", "List gończy") } };
		return Sanctions.Contains(Kind) ? Sanctions[Kind] : FText::FromString(Kind);
	}

	FText Date(double Ms)
	{
		const FDateTime When = FDateTime::FromUnixTimestamp(int64(Ms / 1000.0));
		return FText::FromString(FString::Printf(TEXT("%02d.%02d %02d:%02d"), When.GetDay(), When.GetMonth(), When.GetHour(), When.GetMinute()));
	}

	FString Goods(const TArray<TSharedPtr<FJsonObject>>& Goods)
	{
		TArray<FString> Parts;
		for (const TSharedPtr<FJsonObject>& G : Goods)
		{
			Parts.Add(FString::Printf(TEXT("%d × %s"), int32(FWUi::Num(G, TEXT("quantity"))), *FWUi::Str(G, TEXT("name"))));
		}
		return FString::Join(Parts, TEXT(", "));
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

void SFWPoliceWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	TSharedRef<SHorizontalBox> TabRow = SNew(SHorizontalBox);
	for (int32 i = 0; i < UE_ARRAY_COUNT(FWPoliceText::Tabs); ++i)
	{
		TabRow->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			FWUi::TabButton(FText::FromString(FWPoliceText::Tabs[i]), [this, i]() { return Tab == i; }, [this, i]() { ShowTab(i); })
		];
	}
	TSharedRef<SWidget> Right = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 14, 0)
		[
			FWUi::SmallButton(LOCTEXT("Country", "Państwo"), [this]() { if (AFWPlayerController* PC = Controller.Get()) { PC->OpenCountry(); } })
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
			.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); })
		];
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Policja"), Right, TabRow, SAssignNew(Content, SBox), 1050.f, 800.f)
	];
	Load();
}

double SFWPoliceWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

void SFWPoliceWindow::Load(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + 8.0;
	const bool bFence = Tab == FWPoliceText::Fence;
	TWeakPtr<SFWPoliceWindow> WeakThis = SharedThis(this);
	PC->Send(bFence ? TEXT("fence.get") : TEXT("law.get"), nullptr, [WeakThis, bFence, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != (bFence ? TEXT("fence") : TEXT("law")))
		{
			Inner->ToastError(Reply);
			return;
		}
		if (bFence)
		{
			This->Fence = Reply.Payload;
		}
		else
		{
			This->State = Reply.Payload;
			This->ClockOffset = FWUi::Num(Reply.Payload, TEXT("serverTime")) - FWUi::LocalNowMs();
			if (FWUi::Num(Reply.Payload, TEXT("paidCents")) > 0.0)
			{
				FWPoliceText::Wallet(Inner);
			}
		}
		// Asked again by itself: not built anew under the player typing in it
		const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
		if (bPoll && Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText")))
		{
			return;
		}
		This->Rebuild();
	});
}

void SFWPoliceWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWPoliceWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload.IsValid() ? Payload : MakeShared<FJsonObject>(), [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		// Most answer with the whole window again; some with what they found, then the window is asked for
		if (Reply.Type == TEXT("law"))
		{
			This->State = Reply.Payload;
			This->Rebuild();
		}
		else if (Reply.Type == TEXT("law.found"))
		{
			This->Finding = This->FoundText(Reply.Payload);
			This->Load();
		}
		else if (Reply.Type == TEXT("law.investigated"))
		{
			const int32 Opened = FWUi::Objects(Reply.Payload, TEXT("cases")).Num();
			This->Finding = Opened == 0 ? LOCTEXT("NothingFound", "Śledztwo: nic nie wyszło na jaw.") : FText::Format(LOCTEXT("CasesOpened", "Śledztwo otworzyło sprawy: {0}."), Opened);
			This->Load();
		}
		else if (Reply.Type == TEXT("law.reported"))
		{
			This->Finding = LOCTEXT("Reported", "Zgłoszenie trafiło na policję.");
			Inner->Toast(This->Finding);
			This->Load();
		}
		else if (Reply.Type == TEXT("law.bribed"))
		{
			This->Load();
		}
		else if (Reply.Type == TEXT("fence"))
		{
			This->Fence = Reply.Payload;
			This->Rebuild();
			Inner->RefreshInventory();
		}
		else
		{
			Inner->ToastError(Reply);
			return;
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		FWPoliceText::Wallet(Inner);
	});
}

void SFWPoliceWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	if (Scroll.IsValid())
	{
		Scroll->SetScrollOffset(0.f);
	}
	Rebuild();
	Load();
}

FText SFWPoliceWindow::CaseText(const TSharedPtr<FJsonObject>& Case, bool bWithSuspect) const
{
	const FString Status = FWUi::Str(Case, TEXT("status"));
	FString Text = FString::Printf(TEXT("#%d %s%s · %s"), int32(FWUi::Num(Case, TEXT("id"))), *FWPoliceText::Crime(FWUi::Str(Case, TEXT("kind"))).ToString(),
		bWithSuspect ? *(TEXT(" · ") + FWUi::Str(Case, TEXT("suspect"))) : TEXT(""), *FWPoliceText::Status(Status).ToString());
	if (Status == TEXT("sentenced") || Status == TEXT("appealed") || Status == TEXT("upheld") || Status == TEXT("overturned"))
	{
		Text += FString::Printf(TEXT(" · grzywna %s"), *FWUi::Money(FWUi::Num(Case, TEXT("collectedCents"))).ToString());
		const TPair<const TCHAR*, const TCHAR*> Bans[] = { { TEXT("tradeBanHours"), TEXT("bez handlu") }, { TEXT("travelBanHours"), TEXT("bez granic") }, { TEXT("wantedHours"), TEXT("list gończy") } };
		for (const TPair<const TCHAR*, const TCHAR*>& Ban : Bans)
		{
			if (FWUi::Num(Case, Ban.Key) > 0.0)
			{
				Text += FString::Printf(TEXT(" · %s %d h"), Ban.Value, int32(FWUi::Num(Case, Ban.Key)));
			}
		}
	}
	return FText::FromString(Text + TEXT("\n") + FWUi::Str(Case, TEXT("detail")));
}

FText SFWPoliceWindow::FoundText(const TSharedPtr<FJsonObject>& Found) const
{
	double CaseId = 0.0;
	if (Found->TryGetNumberField(TEXT("caseId"), CaseId))
	{
		return FText::Format(LOCTEXT("FoundCase", "Znaleziono u {0}: {1}. Otwarto sprawę #{2}."), FText::FromString(FWUi::Str(Found, TEXT("suspect"))),
			FText::FromString(FWPoliceText::Goods(FWUi::Objects(Found, TEXT("forbidden")))), int32(CaseId));
	}
	if (FWUi::Bool(Found, TEXT("protected")))
	{
		return FText::Format(LOCTEXT("Protected", "{0} jest poniżej poziomu {1}: nowych graczy się nie ściga."), FText::FromString(FWUi::Str(Found, TEXT("suspect"))),
			int32(FWUi::Num(FWUi::Obj(State, TEXT("rules")), TEXT("protectedBelowLevel"))));
	}
	const TArray<TSharedPtr<FJsonObject>> Goods = FWUi::Objects(Found, TEXT("goods"));
	if (Goods.Num() == 0)
	{
		return FText::Format(LOCTEXT("Clean", "Kontrola u {0}: nic podejrzanego."), FText::FromString(FWUi::Str(Found, TEXT("suspect"))));
	}
	return FText::Format(LOCTEXT("AllFine", "Kontrola u {0}: {1}. Wszystko w porządku."), FText::FromString(FWUi::Str(Found, TEXT("suspect"))), FText::FromString(FWPoliceText::Goods(Goods)));
}

void SFWPoliceWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedPtr<SWidget> Page;
	if (Tab == FWPoliceText::Fence)
	{
		Page = MakeFence();
	}
	else if (!State.IsValid())
	{
		Page = FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	else
	{
		Page = Tab == FWPoliceText::Duty ? MakeDuty() : Tab == FWPoliceText::Court ? MakeCourt() : MakeLaw();
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWPoliceWindow::MakeLaw()
{
	TWeakPtr<SFWPoliceWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const FString Country = FWUi::Str(State, TEXT("country"));
	if (Country.IsEmpty())
	{
		Add(FWUi::Note(LOCTEXT("NoCountry", "Nie jesteś obywatelem żadnego kraju. Kraj, jego prawa i policja pojawią się, gdy zostaniesz obywatelem (okno państwa)."), FWStyle::Muted), 0.f);
	}
	else
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("Citizenship", "Obywatelstwo: {0}{1}"), FText::FromString(Country),
			FWUi::Bool(State, TEXT("hasGovernment")) ? FText::GetEmpty() : LOCTEXT("NoGovernment", " · kraj nie ma jeszcze rządu, więc nie ma też policji ani ceł")), FWStyle::Text), 0.f);
	}
	const TSharedPtr<FJsonObject> Laws = FWUi::Obj(State, TEXT("laws"));
	Add(FWUi::Heading(LOCTEXT("YourLaw", "Prawo twojego kraju")), 14.f);
	TArray<FString> Banned;
	for (const TSharedPtr<FJsonObject>& R : FWUi::Objects(Laws, TEXT("banned")))
	{
		Banned.Add(FWUi::Str(R, TEXT("name")));
	}
	Add(FWUi::Note(FText::Format(LOCTEXT("Banned", "Zakazane: {0}"), Banned.Num() > 0 ? FText::FromString(FString::Join(Banned, TEXT(", "))) : LOCTEXT("Nothing", "nic")), FWStyle::Muted));
	const TArray<TSharedPtr<FJsonObject>> Licensed = FWUi::Objects(Laws, TEXT("licensed"));
	if (Licensed.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoLicensed", "Na licencję: nic"), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Rule : Licensed)
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		P->SetStringField(TEXT("country"), Country);
		P->SetStringField(TEXT("item"), FWUi::Str(Rule, TEXT("item")));
		Add(FWPoliceText::Line(FText::Format(LOCTEXT("Licensed", "Na licencję: {0} · {1} / tydzień"), FText::FromString(FWUi::Str(Rule, TEXT("name"))), FWUi::Money(FWUi::Num(Rule, TEXT("feeCents")))), FWStyle::Text,
			SNew(SBox).ToolTipText(LOCTEXT("LicenceTip", "Licencja na tydzień; opłata trafia do skarbca kraju"))[FWUi::SmallButton(LOCTEXT("BuyLicence", "Kup licencję"), Do(TEXT("law.buyLicence"), P), true)]), 3.f);
	}
	Add(FWUi::Note(FWUi::Bool(Laws, TEXT("forestProtection")) ? LOCTEXT("ForestsProtected", "Lasy chronione: wycinka bez certyfikatu leśnego jest nielegalna.") : LOCTEXT("ForestsFree", "Lasy nie są chronione."), FWStyle::Muted));
	Add(FWUi::Note(LOCTEXT("Smuggling", "Przemyt: pociąg przez granicę z zaznaczonym „przemytem” nie płaci cła i może wieźć zakazane towary, ale celnicy łapią część takich pociągów, a policja może je skontrolować przez 48 godzin."), FWStyle::Muted));

	if (FWUi::Bool(State, TEXT("mayAppointChief")))
	{
		Add(FWUi::Heading(LOCTEXT("Government", "Rząd: policja i zakazy")), 16.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("chief"), LOCTEXT("CitizenNick", "Nick obywatela"), 260.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("AppointChief", "Mianuj komendanta"), [WeakThis]()
				{
					if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("name"), This->Form->Get(TEXT("chief")));
						This->Act(TEXT("law.appointChief"), P);
					}
				})
			], 4.f);
		// What is banned and licensed, as typed: item ids (or the ids the server knows), "id = price" for licences
		TArray<FString> BannedIds, LicensedText;
		for (const TSharedPtr<FJsonObject>& R : FWUi::Objects(Laws, TEXT("banned")))
		{
			BannedIds.Add(FWUi::Str(R, TEXT("item")));
		}
		for (const TSharedPtr<FJsonObject>& R : Licensed)
		{
			LicensedText.Add(FString::Printf(TEXT("%s = %d"), *FWUi::Str(R, TEXT("item")), int32(FWUi::Num(R, TEXT("feeCents")) / 100.0)));
		}
		Add(Form->Field(TEXT("banned"), LOCTEXT("BannedHint", "Zakazane towary (id), po przecinku, np. dynamite, uranium_ore"), 700.f, FString::Join(BannedIds, TEXT(", "))), 6.f);
		Add(Form->Field(TEXT("licensed"), LOCTEXT("LicensedHint", "Na licencję: id = cena $, po przecinku"), 700.f, FString::Join(LicensedText, TEXT(", "))), 4.f);
		Add(Form->Check(TEXT("forests"), LOCTEXT("ProtectForests", "Chroń lasy (wycinka tylko z certyfikatem leśnym)"), FWUi::Bool(Laws, TEXT("forestProtection"))), 4.f);
		Add(SNew(SBox).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("SaveLaw", "Zapisz prawo"), [WeakThis]()
				{
					TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin();
					if (!This)
					{
						return;
					}
					TArray<TSharedPtr<FJsonValue>> BannedList, Licences;
					TArray<FString> Parts;
					This->Form->Get(TEXT("banned")).ParseIntoArray(Parts, TEXT(","));
					for (const FString& Part : Parts)
					{
						BannedList.Add(MakeShared<FJsonValueString>(Part.TrimStartAndEnd().ToLower()));
					}
					This->Form->Get(TEXT("licensed")).ParseIntoArray(Parts, TEXT(","));
					for (const FString& Part : Parts)
					{
						FString Item, Price;
						if (!Part.Split(TEXT("="), &Item, &Price))
						{
							Item = Part;
						}
						TSharedPtr<FJsonObject> Licence = MakeShared<FJsonObject>();
						Licence->SetStringField(TEXT("item"), Item.TrimStartAndEnd().ToLower());
						Licence->SetNumberField(TEXT("feeCents"), FMath::RoundToDouble(FCString::Atod(*Price.TrimStartAndEnd()) * 100.0));
						Licences.Add(MakeShared<FJsonValueObject>(Licence));
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetArrayField(TEXT("banned"), BannedList);
					P->SetArrayField(TEXT("licensed"), Licences);
					P->SetBoolField(TEXT("forestProtection"), This->Form->Ticked(TEXT("forests")));
					This->Act(TEXT("law.setLaws"), P);
				}, true)
			], 6.f);
	}

	const TArray<TSharedPtr<FJsonObject>> Licences = FWUi::Objects(State, TEXT("licences"));
	if (Licences.Num() > 0)
	{
		Add(FWUi::Heading(LOCTEXT("YourLicences", "Twoje licencje")), 16.f);
		for (const TSharedPtr<FJsonObject>& L : Licences)
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("Licence", "{0} ({1}) do {2}"), FText::FromString(FWUi::Str(L, TEXT("name"))), FText::FromString(FWUi::Str(L, TEXT("country"))),
				FWPoliceText::Date(FWUi::Num(L, TEXT("untilMs")))), FWStyle::Text), 2.f);
		}
	}
	const TArray<TSharedPtr<FJsonObject>> Sanctions = FWUi::Objects(State, TEXT("sanctions"));
	if (Sanctions.Num() > 0)
	{
		Add(FWUi::Heading(LOCTEXT("YourSanctions", "Twoje kary")), 16.f);
		for (const TSharedPtr<FJsonObject>& S : Sanctions)
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("SanctionLine", "{0} ({1}) do {2}"), FWPoliceText::Sanction(FWUi::Str(S, TEXT("kind"))), FText::FromString(FWUi::Str(S, TEXT("country"))),
				FWPoliceText::Date(FWUi::Num(S, TEXT("untilMs")))), FWStyle::Error), 2.f);
		}
	}
	Add(FWUi::Heading(LOCTEXT("CasesAgainst", "Sprawy przeciw tobie")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Cases = FWUi::Objects(State, TEXT("cases"));
	if (Cases.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("CleanRecord", "Czysta kartoteka."), FWStyle::Good));
	}
	for (const TSharedPtr<FJsonObject>& C : Cases)
	{
		double AppealUntil = 0.0;
		TSharedPtr<SWidget> Right;
		if (C->TryGetNumberField(TEXT("appealUntilMs"), AppealUntil) && AppealUntil > ServerNow())
		{
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetNumberField(TEXT("id"), FWUi::Num(C, TEXT("id")));
			Right = SNew(SBox).ToolTipText(LOCTEXT("AppealTip", "Sąd (minister sprawiedliwości albo prezydent) utrzyma wyrok albo go uchyli i odda grzywnę"))[FWUi::SmallButton(LOCTEXT("Appeal", "Odwołaj się"), Do(TEXT("law.appeal"), P))];
		}
		Add(FWPoliceText::Line(CaseText(C, false), FWStyle::Text, Right), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("ReportPlayer", "Zgłoś gracza")), 16.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("reportWho"), LOCTEXT("Nick", "Nick"), 180.f)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("reportWhat"), LOCTEXT("What", "Co się stało (do 200 znaków)"), 480.f)]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("Report", "Zgłoś"), [WeakThis]()
			{
				if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("name"), This->Form->Get(TEXT("reportWho")));
					P->SetStringField(TEXT("text"), This->Form->Get(TEXT("reportWhat")).Left(200));
					This->Act(TEXT("law.report"), P);
				}
			})
		], 4.f);
	Add(FWUi::Heading(LOCTEXT("Service", "Służba w policji")), 16.f);
	const TSharedPtr<FJsonObject> Police = FWUi::Obj(State, TEXT("police"));
	if (Police.IsValid())
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("Serving", "Służysz jako {0}. Biurko jest w zakładce „Służba”."), FText::FromString(FWPoliceText::Rank(int32(FWUi::Num(Police, TEXT("rank")))).ToString().ToLower())), FWStyle::Good));
		Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("Resign", "Odejdź z policji"), Do(TEXT("law.resign"), nullptr))], 4.f);
	}
	else if (State->HasField(TEXT("applied")) && !State->GetField<EJson::None>(TEXT("applied"))->IsNull())
	{
		Add(FWUi::Note(LOCTEXT("Applied", "Twoje podanie czeka na komendanta."), FWStyle::Muted));
		Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("WithdrawApplication", "Wycofaj podanie"), Do(TEXT("law.resign"), nullptr))], 4.f);
	}
	else
	{
		const TSharedPtr<FJsonObject> Rules = FWUi::Obj(State, TEXT("rules"));
		TArray<FString> Pay;
		for (const TSharedPtr<FJsonObject>& R : FWUi::Objects(Rules, TEXT("ranks")))
		{
			Pay.Add(FString::Printf(TEXT("%s %s/dzień"), *FWPoliceText::Rank(int32(FWUi::Num(R, TEXT("rank")))).ToString(), *FWUi::Money(FWUi::Num(R, TEXT("salaryCents"))).ToString()));
		}
		Add(FWUi::Note(FText::Format(LOCTEXT("Recruiting", "Policja przyjmuje obywateli od poziomu {0} bez wyroku w ostatnim tygodniu. Pensja ze skarbca kraju: {1}."),
			int32(FWUi::Num(Rules, TEXT("protectedBelowLevel"))), FText::FromString(FString::Join(Pay, TEXT(" · ")))), FWStyle::Muted));
		Add(SNew(SBox).HAlign(HAlign_Left)[FWUi::SmallButton(LOCTEXT("Apply", "Złóż podanie"), Do(TEXT("law.apply"), nullptr), true)], 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWPoliceWindow::MakeDuty()
{
	TWeakPtr<SFWPoliceWindow> WeakThis = SharedThis(this);
	auto Do = [WeakThis](const TCHAR* Type, TSharedPtr<FJsonObject> P)
	{
		return [WeakThis, Type, P]() { if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin()) { This->Act(Type, P); } };
	};
	auto With = [](std::initializer_list<TPair<const TCHAR*, double>> Numbers, std::initializer_list<TPair<const TCHAR*, FString>> Strings = {})
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		for (const TPair<const TCHAR*, double>& N : Numbers)
		{
			P->SetNumberField(N.Key, N.Value);
		}
		for (const TPair<const TCHAR*, FString>& S : Strings)
		{
			P->SetStringField(S.Key, S.Value);
		}
		return P;
	};
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Police = FWUi::Obj(State, TEXT("police"));
	if (!Police.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("NotServing", "Nie służysz w policji. Podanie złożysz w zakładce „Prawo”."), FWStyle::Muted), 0.f);
		return Page;
	}
	const int32 Rank = int32(FWUi::Num(Police, TEXT("rank")));
	Add(FWUi::Note(FText::Format(LOCTEXT("Desk", "{0} · {1} · pensja {2}/dzień · kontrole dziś: {3}{4}"), FText::FromString(FWUi::Str(Police, TEXT("country"))), FWPoliceText::Rank(Rank),
		FWUi::Money(FWUi::Num(Police, TEXT("salaryCents"))), int32(FWUi::Num(Police, TEXT("inspectionsLeft"))),
		Rank >= 2 ? FText::Format(LOCTEXT("Investigations", " · śledztwa: {0}"), int32(FWUi::Num(Police, TEXT("investigationsLeft")))) : FText::GetEmpty()), FWStyle::Text), 0.f);
	if (!Finding.IsEmpty())
	{
		Add(FWUi::Note(Finding, FWStyle::Good));
	}
	Add(FWUi::Heading(LOCTEXT("Border", "Pociągi na granicy (48 h)")), 14.f);
	const TArray<TSharedPtr<FJsonObject>> Crossings = FWUi::Objects(Police, TEXT("crossings"));
	if (Crossings.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoTrains", "Żaden pociąg nie wjechał ostatnio do kraju."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& C : Crossings)
	{
		const bool bDeclared = FWUi::Bool(C, TEXT("declared"));
		FString Text = FString::Printf(TEXT("%s · %s → %s · %s · %d szt."), *FWUi::Str(C, TEXT("owner")), *FWUi::Str(C, TEXT("from")), *FWUi::Str(C, TEXT("to")),
			*FWPoliceText::Date(FWUi::Num(C, TEXT("atMs"))).ToString(), int32(FWUi::Num(C, TEXT("pieces"))));
		Text += bDeclared ? FString::Printf(TEXT(" · zgłoszony, cło %s"), *FWUi::Money(FWUi::Num(C, TEXT("dutyCents"))).ToString()) : FString(TEXT(" · niezgłoszony"));
		const TArray<TSharedPtr<FJsonObject>> Goods = FWUi::Objects(C, TEXT("goods"));
		if (Goods.Num() > 0)
		{
			Text += TEXT(" · ") + FWPoliceText::Goods(Goods);
		}
		TSharedPtr<SWidget> Right;
		if (!FWUi::Bool(C, TEXT("inspected")) && !FWUi::Bool(C, TEXT("mine")) && !bDeclared)
		{
			Right = FWUi::SmallButton(LOCTEXT("Inspect", "Kontroluj"), Do(TEXT("law.inspect"), With({ { TEXT("id"), FWUi::Num(C, TEXT("id")) } })));
		}
		Add(FWPoliceText::Line(FText::FromString(Text), bDeclared ? FWStyle::Text : FWStyle::Accent, Right), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("Cases", "Sprawy")), 14.f);
	const TArray<TSharedPtr<FJsonObject>> Cases = FWUi::Objects(Police, TEXT("cases"));
	if (Cases.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoCases", "Nie ma otwartych spraw."), FWStyle::Muted));
	}
	const TSharedPtr<FJsonObject> Rules = FWUi::Obj(State, TEXT("rules"));
	for (const TSharedPtr<FJsonObject>& C : Cases)
	{
		const int64 Id = int64(FWUi::Num(C, TEXT("id")));
		const bool bOpen = FWUi::Str(C, TEXT("status")) == TEXT("open");
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (bOpen && Rank >= 1)
		{
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(LOCTEXT("Sentence", "Wyrok"), [WeakThis, Id]() { if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin()) { This->Sentencing = Id; This->Rebuild(); } })
			];
			Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Dismiss", "Umorz"), Do(TEXT("law.dismiss"), With({ { TEXT("id"), double(Id) } })))];
		}
		Add(FWPoliceText::Line(CaseText(C, true), FWStyle::Text, Right), 3.f);
		if (Sentencing == Id && bOpen)
		{
			const double MaxFine = FWUi::Num(C, TEXT("maxFineCents"));
			const FString Key = FString::Printf(TEXT("sentence%lld:"), Id);
			auto Hours = [this, &Key](const TCHAR* Name, const FText& Label)
			{
				return SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 4, 0)[SNew(STextBlock).Text(Label).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)]
					+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)[Form->Field(Key + Name, FText::GetEmpty(), 50.f, TEXT("0"))];
			};
			Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
						[
							SNew(STextBlock).Text(FText::Format(LOCTEXT("Fine", "Grzywna $ (do {0})"), FWUi::Money(MaxFine))).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
						]
						+ SHorizontalBox::Slot().AutoWidth()[Form->Field(Key + TEXT("fine"), FText::GetEmpty(), 100.f, FString::SanitizeFloat(MaxFine / 200.0))]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 0)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().AutoWidth()[Hours(TEXT("trade"), FText::Format(LOCTEXT("TradeHours", "Zakaz handlu h (do {0})"), int32(FWUi::Num(Rules, TEXT("maxBanHours")))))]
						+ SHorizontalBox::Slot().AutoWidth()[Hours(TEXT("travel"), LOCTEXT("TravelHours", "granic h"))]
						+ SHorizontalBox::Slot().AutoWidth()[Hours(TEXT("wanted"), FText::Format(LOCTEXT("WantedHours", "list gończy h (do {0})"), int32(FWUi::Num(Rules, TEXT("maxWantedHours")))))]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 0)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)
						[
							FWUi::Bool(C, TEXT("seizable")) ? Form->Check(Key + TEXT("seize"), LOCTEXT("Seize", "Zabierz znalezione towary"), true) : SNullWidget::NullWidget
						]
						+ SHorizontalBox::Slot().AutoWidth()
						[
							FWUi::SmallButton(LOCTEXT("Announce", "Ogłoś wyrok"), [WeakThis, Id, Key]()
							{
								if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
								{
									This->Sentencing = -1;
									TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
									P->SetNumberField(TEXT("id"), double(Id));
									P->SetNumberField(TEXT("fineCents"), This->Form->Cents(Key + TEXT("fine")));
									P->SetNumberField(TEXT("tradeBanHours"), This->Form->Number(Key + TEXT("trade")));
									P->SetNumberField(TEXT("travelBanHours"), This->Form->Number(Key + TEXT("travel")));
									P->SetNumberField(TEXT("wantedHours"), This->Form->Number(Key + TEXT("wanted")));
									P->SetBoolField(TEXT("seize"), This->Form->Ticked(Key + TEXT("seize")));
									This->Act(TEXT("law.sentence"), P);
								}
							}, true)
						]
					]
				], 3.f);
		}
	}
	Add(FWUi::Heading(LOCTEXT("Reports", "Zgłoszenia")), 14.f);
	const TArray<TSharedPtr<FJsonObject>> Reports = FWUi::Objects(Police, TEXT("reports"));
	if (Reports.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoReports", "Brak zgłoszeń."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& R : Reports)
	{
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (Rank >= 2)
		{
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Investigate", "Śledztwo"), Do(TEXT("law.investigate"), With({}, { { TEXT("name"), FWUi::Str(R, TEXT("suspect")) } })))];
		}
		Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Close", "Zamknij"), Do(TEXT("law.closeReport"), With({ { TEXT("id"), FWUi::Num(R, TEXT("id")) } })))];
		Add(FWPoliceText::Line(FText::Format(LOCTEXT("ReportLine", "{0} zgłasza {1}: {2}"), FText::FromString(FWUi::Str(R, TEXT("reporter"))), FText::FromString(FWUi::Str(R, TEXT("suspect"))),
			FText::FromString(FWUi::Str(R, TEXT("text")))), FWStyle::Text, Right), 3.f);
	}
	if (Rank >= 2)
	{
		Add(FWUi::Heading(LOCTEXT("Warrants", "Śledztwo i nakazy")), 14.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("suspect"), LOCTEXT("SuspectHint", "Nick podejrzanego albo policjanta"), 300.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("StartInvestigation", "Wszczęj śledztwo"), [WeakThis, With]()
				{
					if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("law.investigate"), With({}, { { TEXT("name"), This->Form->Get(TEXT("suspect")) } }));
					}
				})
			], 4.f);
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("PlotXY", "Działka")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("warrantX"), LOCTEXT("X", "x"), 80.f)]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("warrantY"), LOCTEXT("Y", "y"), 80.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("AskWarrant", "Poproś o nakaz"), [WeakThis, With]()
				{
					if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("law.requestWarrant"), With({ { TEXT("x"), This->Form->Number(TEXT("warrantX")) }, { TEXT("y"), This->Form->Number(TEXT("warrantY")) } }));
					}
				})
			], 4.f);
		for (const TSharedPtr<FJsonObject>& W : FWUi::Objects(Police, TEXT("warrants")))
		{
			const bool bGranted = FWUi::Bool(W, TEXT("granted"));
			Add(FWPoliceText::Line(FText::Format(LOCTEXT("Warrant", "Nakaz na działkę {0} × {1} ({2}){3}"), int32(FWUi::Num(W, TEXT("x"))), int32(FWUi::Num(W, TEXT("y"))),
				FText::FromString(FWUi::Str(W, TEXT("requestedBy"))), bGranted ? FText::GetEmpty() : LOCTEXT("Waiting", " · czeka na sąd")), FWStyle::Text,
				bGranted ? FWUi::SmallButton(LOCTEXT("Search", "Przeszukaj"), Do(TEXT("law.search"), With({ { TEXT("id"), FWUi::Num(W, TEXT("id")) } })), true) : TSharedPtr<SWidget>()), 3.f);
		}
	}
	const TArray<TSharedPtr<FJsonObject>> Wanted = FWUi::Objects(Police, TEXT("wanted"));
	if (Wanted.Num() > 0)
	{
		TArray<FString> Names;
		for (const TSharedPtr<FJsonObject>& W : Wanted)
		{
			Names.Add(FWUi::Str(W, TEXT("name")));
		}
		Add(FWUi::Heading(LOCTEXT("WantedList", "List gończy")), 14.f);
		Add(FWUi::Note(FText::FromString(FString::Join(Names, TEXT(", "))), FWStyle::Error));
	}
	Add(FWUi::Heading(LOCTEXT("Officers", "Funkcjonariusze")), 14.f);
	for (const TSharedPtr<FJsonObject>& O : FWUi::Objects(Police, TEXT("officers")))
	{
		const FString Name = FWUi::Str(O, TEXT("name"));
		const bool bMe = FWUi::Bool(O, TEXT("me"));
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (Rank == 3 && !bMe)
		{
			const FString Key = TEXT("rank:") + Name;
			Form->Set(Key, FString::FromInt(FMath::Min(int32(FWUi::Num(O, TEXT("rank"))), 2)));
			TArray<TPair<FString, FText>> Ranks;
			for (int32 i = 0; i < 3; ++i)
			{
				Ranks.Add({ FString::FromInt(i), FWPoliceText::Rank(i) });
			}
			Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
			[
				Form->Pick(Key, Ranks, 160.f, [WeakThis, Name](const FString& Picked)
				{
					if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("name"), Name);
						P->SetNumberField(TEXT("rank"), FCString::Atoi(*Picked));
						This->Act(TEXT("law.setRank"), P);
					}
				})
			];
			Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Fire", "Zwolnij"), Do(TEXT("law.fire"), With({}, { { TEXT("name"), Name } })))];
		}
		Add(FWPoliceText::Line(FText::Format(LOCTEXT("Officer", "{0} · {1}"), FText::FromString(Name), FWPoliceText::Rank(int32(FWUi::Num(O, TEXT("rank"))))), bMe ? FWStyle::Good : FWStyle::Text, Right), 3.f);
	}
	for (const TSharedPtr<FJsonObject>& A : FWUi::Objects(Police, TEXT("applications")))
	{
		const FString Name = FWUi::Str(A, TEXT("name"));
		auto Hire = [Name](bool bYes)
		{
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("name"), Name);
			P->SetBoolField(TEXT("yes"), bYes);
			return P;
		};
		Add(FWPoliceText::Line(FText::Format(LOCTEXT("Application", "Podanie: {0}"), FText::FromString(Name)), FWStyle::Accent,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Hire", "Przyjmij"), Do(TEXT("law.hire"), Hire(true)), true)]
			+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Reject", "Odrzuć"), Do(TEXT("law.hire"), Hire(false)))]), 3.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWPoliceWindow::MakeCourt()
{
	TWeakPtr<SFWPoliceWindow> WeakThis = SharedThis(this);
	auto Decide = [WeakThis](const TCHAR* Type, double Id, bool bYes)
	{
		return [WeakThis, Type, Id, bYes]()
		{
			if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
			{
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetNumberField(TEXT("id"), Id);
				P->SetBoolField(TEXT("yes"), bYes);
				This->Act(Type, P);
			}
		};
	};
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Court = FWUi::Obj(State, TEXT("court"));
	if (!Court.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("CourtAbout", "Sąd to prezydent i minister sprawiedliwości. Rozpatrują odwołania od wyroków i wydają nakazy przeszukania."), FWStyle::Muted), 0.f);
		return Page;
	}
	Add(FWUi::Heading(LOCTEXT("Appeals", "Odwołania")), 0.f);
	const TArray<TSharedPtr<FJsonObject>> Appeals = FWUi::Objects(Court, TEXT("appeals"));
	if (Appeals.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoAppeals", "Nie ma odwołań."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& C : Appeals)
	{
		const double Id = FWUi::Num(C, TEXT("id"));
		Add(FWPoliceText::Line(CaseText(C, true), FWStyle::Text,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Uphold", "Utrzymaj"), Decide(TEXT("law.judge"), Id, true))]
			+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Overturn", "Uchyl"), Decide(TEXT("law.judge"), Id, false))]), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("WarrantRequests", "Wnioski o nakaz przeszukania")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Warrants = FWUi::Objects(Court, TEXT("warrants"));
	if (Warrants.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoRequests", "Nie ma wniosków."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& W : Warrants)
	{
		const double Id = FWUi::Num(W, TEXT("id"));
		Add(FWPoliceText::Line(FText::Format(LOCTEXT("WarrantRequest", "Działka {0} × {1} · prosi {2}"), int32(FWUi::Num(W, TEXT("x"))), int32(FWUi::Num(W, TEXT("y"))), FText::FromString(FWUi::Str(W, TEXT("requestedBy")))),
			FWStyle::Text,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Grant", "Wydaj"), Decide(TEXT("law.decideWarrant"), Id, true), true)]
			+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Refuse", "Odmów"), Decide(TEXT("law.decideWarrant"), Id, false))]), 3.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWPoliceWindow::MakeFence()
{
	TWeakPtr<SFWPoliceWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(FText::Format(LOCTEXT("FenceAbout", "Paser płaci więcej niż handlarz za materiały wybuchowe, uran, skamieliny i to, czego zakazuje twój kraj. Każda transakcja zostawia ślad, który detektyw może znaleźć. Paser handluje z graczami od poziomu {0}."),
		int32(FWUi::Num(Fence, TEXT("level"), 5.0))), FWStyle::Muted), 0.f);
	if (!Fence.IsValid())
	{
		return Page;
	}
	for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Fence, TEXT("items")))
	{
		const FString Id = FWUi::Str(Item, TEXT("id"));
		const int32 Have = int32(FWUi::Num(Item, TEXT("have")));
		double Trader = 0.0;
		const bool bTrader = Item->TryGetNumberField(TEXT("traderCents"), Trader);
		const FString Key = TEXT("fence:") + Id;
		Add(SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), Id, 28.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text).AutoWrapText(true)
					.Text(FText::Format(LOCTEXT("FenceItem", "{0} · {1}/szt.{2} · masz {3}"), FText::FromString(FWUi::Str(Item, TEXT("name"))), FWUi::Money(FWUi::Num(Item, TEXT("priceCents"))),
						bTrader ? FText::Format(LOCTEXT("TraderPays", " (handlarz {0})"), FWUi::Money(Trader)) : FText::GetEmpty(), Have))
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(6, 0)[Form->Field(Key, FText::GetEmpty(), 60.f, TEXT("1"))]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					FWUi::SmallButton(LOCTEXT("Sell", "Sprzedaj"), [WeakThis, Id, Key]()
					{
						if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetStringField(TEXT("item"), Id);
							P->SetNumberField(TEXT("quantity"), This->Form->Number(Key));
							This->Act(TEXT("fence.sell"), P);
						}
					}, true, Have > 0)
				]
			], 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("Compartment", "Skrytka w wagonach")), 16.f);
	if (FWUi::Bool(Fence, TEXT("compartment")))
	{
		Add(FWUi::Note(LOCTEXT("HasCompartment", "Twoje pociągi mają skrytkę: celnicy łapią je dwa razy rzadziej, a towar w niej znajdzie tylko detektyw."), FWStyle::Good));
	}
	else
	{
		Add(FWPoliceText::Line(FText::Format(LOCTEXT("BuyCompartment", "Skrytka dla wszystkich twoich pociągów: {0}"), FWUi::Money(FWUi::Num(Fence, TEXT("compartmentCents")))), FWStyle::Text,
			FWUi::SmallButton(LOCTEXT("Buy", "Kup"), [WeakThis]() { if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin()) { This->Act(TEXT("fence.compartment"), nullptr); } })), 3.f);
	}
	Add(FWUi::Heading(LOCTEXT("Gift", "Prezent dla policjanta")), 16.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("bribeWho"), LOCTEXT("OfficerNick", "Nick policjanta"), 220.f)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("bribeAmount"), LOCTEXT("Dollars", "$"), 100.f, TEXT("100"))]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			SNew(SBox).ToolTipText(LOCTEXT("BribeTip", "Pieniądze idą od razu. To łapówka: wydział wewnętrzny może ją wykryć, a sprawa obejmie was obu."))
			[
				FWUi::SmallButton(LOCTEXT("Give", "Daj"), [WeakThis]()
				{
					if (TSharedPtr<SFWPoliceWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("name"), This->Form->Get(TEXT("bribeWho")));
						P->SetNumberField(TEXT("cents"), This->Form->Cents(TEXT("bribeAmount")));
						This->Act(TEXT("law.bribe"), P);
					}
				})
			]
		], 4.f);
	return Page;
}

void SFWPoliceWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() >= NextPoll)
	{
		Load(true);
	}
}

FReply SFWPoliceWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
