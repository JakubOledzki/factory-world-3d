#include "UI/SFWBankWindow.h"

#include "FWPlayerController.h"
#include "Framework/Application/SlateApplication.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Input/SComboBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWBank"

namespace FWBankText
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

	FText Duration(int64 Seconds)
	{
		if (Seconds < 3600)
		{
			if (Seconds % 60 == 0 || Seconds >= 600)
			{
				return FText::Format(LOCTEXT("Min", "{0} min"), FMath::CeilToInt(Seconds / 60.0));
			}
			return FText::FromString(FString::Printf(TEXT("%lld min %02lld s"), Seconds / 60, Seconds % 60));
		}
		if (Seconds % 3600 == 0)
		{
			return FText::Format(LOCTEXT("Hours", "{0} h"), Seconds / 3600);
		}
		return FText::FromString(FString::Printf(TEXT("%lld h %02lld min"), Seconds / 3600, Seconds % 3600 / 60));
	}

	FString Clock(int64 Seconds)
	{
		return Seconds >= 3600 ? FString::Printf(TEXT("%lld:%02lld:%02lld"), Seconds / 3600, Seconds % 3600 / 60, Seconds % 60)
			: FString::Printf(TEXT("%lld:%02lld"), Seconds / 60, Seconds % 60);
	}

	FString Plots(double Plots)
	{
		return FString::Printf(TEXT("%.1f dz."), Plots).Replace(TEXT("."), TEXT(","), ESearchCase::CaseSensitive).Replace(TEXT(",dz"), TEXT(" dz"));
	}
}

void SFWBankWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Banki"), Right, nullptr, SAssignNew(Content, SBox), 900.f, 760.f)
	];
	Load();
}

void SFWBankWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bLoading)
	{
		return;
	}
	bLoading = true;
	NextPoll = FPlatformTime::Seconds() + 5.0;
	TWeakPtr<SFWBankWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("bank.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin())
		{
			This->bLoading = false;
			This->Apply(Reply, true);
		}
	});
}

void SFWBankWindow::Apply(const FFWNetReply& Reply, bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != TEXT("bank"))
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		return;
	}
	State = Reply.Payload;
	ClockOffset = FWUi::Num(State, TEXT("serverTime")) - FWUi::LocalNowMs();
	// Fewer transfers on the way than last time: some got there, perhaps into the wallet
	const int32 Count = FWUi::Objects(State, TEXT("transfers")).Num();
	if (PC && Transfers >= 0 && Count < Transfers)
	{
		FWBankText::Wallet(PC);
	}
	Transfers = Count;
	const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
	if (bPoll && Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText")))
	{
		return;
	}
	Rebuild();
}

double SFWBankWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

void SFWBankWindow::ShowBank(const FString& InBank)
{
	Bank = InBank;
	Rebuild();
}

void SFWBankWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWBankWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWBankWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		This->Apply(Reply);
		if (Reply.Type == TEXT("bank"))
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			FWBankText::Wallet(Inner);
		}
	});
}

void SFWBankWindow::Rebuild()
{
	if (!Content.IsValid() || !State.IsValid())
	{
		return;
	}
	TWeakPtr<SFWBankWindow> WeakThis = SharedThis(this);
	const TSharedPtr<FJsonObject> Rates = FWUi::Obj(State, TEXT("rates"));
	const TArray<TSharedPtr<FJsonObject>> Banks = FWUi::Objects(State, TEXT("banks"));
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	FNumberFormattingOptions One;
	One.MaximumFractionalDigits = 1;
	FText About = FText::Format(LOCTEXT("About", "Pieniądze w banku nie jadą od razu: wpłata i wypłata idą tyle, ile pociąg z twojej najbliższej działki (własnej albo spółki) do najbliższego budynku banku, {0} min na działkę lądem i 3 razy wolniej promem. Przelew w tym samym banku trwa {1}, do innego banku {2}, a bez działki wpłata i wypłata trwają {3}."),
		FText::AsNumber(FWUi::Num(Rates, TEXT("minutesPerPlot")), &One), FWBankText::Duration(int64(FWUi::Num(Rates, TEXT("sameBankSeconds")))),
		FWBankText::Duration(int64(FWUi::Num(Rates, TEXT("otherBankSeconds")))), FWBankText::Duration(int64(FWUi::Num(Rates, TEXT("noPlotSeconds")))));
	Add(FWUi::Note(About, FWStyle::Muted), 0.f);
	if (FWUi::Bool(State, TEXT("companyBanks")))
	{
		Add(FWUi::Note(LOCTEXT("CompanyBanks", "Twoja spółka zbadała bankowość: postaw bank (5 × 5) na działce spółki w mieście, a oddziały (3 × 3) w innych miastach."), FWStyle::Accent));
	}

	// The player's accounts
	Add(FWUi::Heading(LOCTEXT("Accounts", "Twoje konta")), 16.f);
	bool bAny = false;
	for (const TSharedPtr<FJsonObject>& Each : Banks)
	{
		double Balance = 0.0;
		if (!Each->TryGetNumberField(TEXT("balanceCents"), Balance))
		{
			continue;
		}
		bAny = true;
		const FString Id = FWUi::Str(Each, TEXT("id"));
		Add(SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(FText::FromString(FWUi::Str(Each, TEXT("name")))).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[SNew(STextBlock).Text(FWUi::Money(Balance)).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)]
				+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Open", "Otwórz"), [WeakThis, Id]() { if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin()) { This->ShowBank(Id); } })]
			], 3.f);
	}
	if (!bAny)
	{
		Add(FWUi::Note(Banks.Num() > 0 ? LOCTEXT("NoAccount", "Nie masz jeszcze konta w żadnym banku.")
			: LOCTEXT("NoBanks", "Żaden bank jeszcze nie działa. Spółka, która zbada „Bankowość”, postawi bank w mieście."), FWStyle::Muted));
	}

	// The banks to pick from, and the chosen one's page
	TSharedPtr<FJsonObject> Chosen;
	BankIds.Reset();
	BankNames.Reset();
	TSharedRef<SVerticalBox> Picks = SNew(SVerticalBox);
	for (const TSharedPtr<FJsonObject>& Each : Banks)
	{
		const FString Id = FWUi::Str(Each, TEXT("id"));
		BankIds.Add(MakeShared<FString>(Id));
		BankNames.Add(Id, FText::FromString(FWUi::Str(Each, TEXT("name"))));
		if (Id == Bank || !Chosen.IsValid())
		{
			Chosen = Each;
		}
		const int32 Buildings = FWUi::Objects(Each, TEXT("buildings")).Num();
		Picks->AddSlot().AutoHeight().Padding(0, 2)
		[
			FWUi::TabButton(FText::Format(LOCTEXT("BankPick", "{0} · {1} {2}"), FText::FromString(FWUi::Str(Each, TEXT("name"))), Buildings,
				Buildings == 1 ? LOCTEXT("Building", "budynek") : LOCTEXT("Buildings", "budynki")),
				[WeakThis, Id]() { TSharedPtr<SFWBankWindow> This = WeakThis.Pin(); return This && This->Bank == Id; },
				[WeakThis, Id]() { if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin()) { This->ShowBank(Id); } })
		];
	}
	if (Chosen.IsValid())
	{
		Bank = FWUi::Str(Chosen, TEXT("id"));
		if (TargetBank.IsEmpty())
		{
			TargetBank = Bank;
		}
		Add(FWUi::Heading(LOCTEXT("Banks", "Banki")), 16.f);
		Add(Picks, 2.f);
		TArray<FString> Places;
		for (const TSharedPtr<FJsonObject>& Building : FWUi::Objects(Chosen, TEXT("buildings")))
		{
			Places.Add(FWUi::Str(Building, TEXT("town")) + (FWUi::Str(Building, TEXT("kind")) == TEXT("furniture_bank") ? TEXT(" (bank)") : TEXT("")));
		}
		Add(FWUi::Note(FText::Format(LOCTEXT("BankAbout", "{0}{1}. Klientów: {2}."), FWUi::Bool(Chosen, TEXT("mine")) ? LOCTEXT("Ours", "Twoja spółka · ") : FText::GetEmpty(),
			Places.Num() > 0 ? FText::Format(LOCTEXT("BuildingsIn", "Budynki: {0}"), FText::FromString(FString::Join(Places, TEXT(", "))))
				: LOCTEXT("NoBuilding", "Bank nie ma dziś żadnego budynku: wpłaty i wypłaty idą pocztą"),
			int32(FWUi::Num(Chosen, TEXT("customers")))), FWStyle::Text), 10.f);
		const TSharedPtr<FJsonObject> Trip = FWUi::Obj(Chosen, TEXT("trip"));
		const FText Time = FWBankText::Duration(int64(FWUi::Num(Trip, TEXT("seconds"))));
		double FromX = 0.0;
		if (Trip.IsValid() && Trip->TryGetNumberField(TEXT("fromX"), FromX))
		{
			FString Text = FString::Printf(TEXT("Wpłata i wypłata: %s, z działki %d × %d do budynku w %s (%s lądem"), *Time.ToString(), int32(FromX), int32(FWUi::Num(Trip, TEXT("fromY"))),
				*FWUi::Str(Trip, TEXT("town")), *FWBankText::Plots(FWUi::Num(Trip, TEXT("landPlots"))));
			if (FWUi::Num(Trip, TEXT("seaPlots")) > 0.0)
			{
				Text += FString::Printf(TEXT(", %s promem"), *FWBankText::Plots(FWUi::Num(Trip, TEXT("seaPlots"))));
			}
			Add(FWUi::Note(FText::FromString(Text + TEXT(").")), FWStyle::Muted));
		}
		else
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("TripPost", "Wpłata i wypłata: {0}."), Time), FWStyle::Muted));
		}
		double Balance = 0.0;
		const FString Id = Bank;
		auto Cents = [](const FString& Text) { return FMath::RoundToDouble(FCString::Atod(*Text.Replace(TEXT(","), TEXT("."))) * 100.0); };
		if (!Chosen->TryGetNumberField(TEXT("balanceCents"), Balance))
		{
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("bank"), Id);
			Add(SNew(SBox).HAlign(HAlign_Left)
				[
					FWUi::SmallButton(LOCTEXT("OpenAccount", "Otwórz konto"), [WeakThis, P]() { if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin()) { This->Act(TEXT("bank.open"), P); } }, true,
						FWUi::Objects(Chosen, TEXT("buildings")).Num() > 0)
				], 8.f);
		}
		else
		{
			Add(SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent).Text(FText::Format(LOCTEXT("OnAccount", "Na koncie: {0}"), FWUi::Money(Balance))), 10.f);
			auto Field = [WeakThis](FString SFWBankWindow::* Value, const FText& Hint, float Width)
			{
				TSharedPtr<SFWBankWindow> This = WeakThis.Pin();
				return SNew(SBox).WidthOverride(Width)
					[
						SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(Hint).Text(FText::FromString((*This).*Value))
						.OnTextChanged_Lambda([WeakThis, Value](const FText& Text) { if (TSharedPtr<SFWBankWindow> Inner = WeakThis.Pin()) { (*Inner).*Value = Text.ToString(); } })
					];
			};
			auto Money = [WeakThis, Id, Cents](const TCHAR* Type)
			{
				return [WeakThis, Id, Cents, Type]()
				{
					if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("bank"), Id);
						P->SetNumberField(TEXT("cents"), Cents(This->Amount));
						This->Act(Type, P);
					}
				};
			};
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(&SFWBankWindow::Amount, LOCTEXT("Dollars", "kwota $"), 160.f)]
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Deposit", "Wpłać"), Money(TEXT("bank.deposit")), true)]
				+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Withdraw", "Wypłać"), Money(TEXT("bank.withdraw")))], 6.f);
			Add(FWUi::Heading(LOCTEXT("Transfer", "Przelew na konto innego gracza")), 14.f);
			TSharedPtr<FString> Target;
			for (const TSharedPtr<FString>& Each : BankIds)
			{
				Target = *Each == TargetBank ? Each : Target;
			}
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(&SFWBankWindow::Recipient, LOCTEXT("Recipient", "Nick odbiorcy"), 220.f)]
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
				[
					SNew(SBox).WidthOverride(240.f).ToolTipText(LOCTEXT("TargetTip", "Bank, w którym odbiorca ma konto"))
					[
						SNew(SComboBox<TSharedPtr<FString>>)
						.OptionsSource(&BankIds)
						.InitiallySelectedItem(Target)
						.OnGenerateWidget_Lambda([WeakThis](TSharedPtr<FString> Each)
						{
							TSharedPtr<SFWBankWindow> This = WeakThis.Pin();
							return SNew(STextBlock).Font(FWStyle::Font(11)).Text(This ? This->BankNames.FindRef(*Each) : FText::GetEmpty());
						})
						.OnSelectionChanged_Lambda([WeakThis](TSharedPtr<FString> Each, ESelectInfo::Type)
						{
							if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin(); This && Each.IsValid())
							{
								This->TargetBank = *Each;
							}
						})
						[
							SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
							.Text_Lambda([WeakThis]() { TSharedPtr<SFWBankWindow> This = WeakThis.Pin(); return This ? This->BankNames.FindRef(This->TargetBank) : FText::GetEmpty(); })
						]
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Field(&SFWBankWindow::Sent, LOCTEXT("Dollars2", "kwota $"), 120.f)]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					FWUi::SmallButton(LOCTEXT("Send", "Wyślij"), [WeakThis, Id, Cents]()
					{
						if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
							P->SetStringField(TEXT("bank"), Id);
							P->SetStringField(TEXT("to"), This->Recipient.TrimStartAndEnd());
							P->SetStringField(TEXT("toBank"), This->TargetBank);
							P->SetNumberField(TEXT("cents"), Cents(This->Sent));
							This->Act(TEXT("bank.transfer"), P);
						}
					}, true)
				], 6.f);
			TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
			P->SetStringField(TEXT("bank"), Id);
			Add(SNew(SBox).HAlign(HAlign_Left).ToolTipText(LOCTEXT("CloseTip", "Tylko puste konto, do którego nic nie jest w drodze"))
				[
					FWUi::SmallButton(LOCTEXT("Close", "Zamknij konto"), [WeakThis, P]() { if (TSharedPtr<SFWBankWindow> This = WeakThis.Pin()) { This->Act(TEXT("bank.close"), P); } })
				], 12.f);
		}
	}

	// Money on its way, with a countdown
	Add(FWUi::Heading(LOCTEXT("OnWay", "W drodze")), 18.f);
	const TArray<TSharedPtr<FJsonObject>> OnWay = FWUi::Objects(State, TEXT("transfers"));
	if (OnWay.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NothingOnWay", "Żadne pieniądze nie są w drodze."), FWStyle::Muted));
	}
	for (const TSharedPtr<FJsonObject>& Transfer : OnWay)
	{
		const FString Kind = FWUi::Str(Transfer, TEXT("kind"));
		const bool bIncoming = FWUi::Bool(Transfer, TEXT("incoming"));
		const FText Money = FWUi::Money(FWUi::Num(Transfer, TEXT("cents")));
		FText Text;
		if (Kind == TEXT("deposit"))
		{
			Text = FText::Format(LOCTEXT("DepositLine", "Wpłata · {0} → konto w {1}"), Money, FText::FromString(FWUi::Str(Transfer, TEXT("toBank"))));
		}
		else if (Kind == TEXT("withdraw"))
		{
			Text = FText::Format(LOCTEXT("WithdrawLine", "Wypłata · {0} z {1} → portfel"), Money, FText::FromString(FWUi::Str(Transfer, TEXT("fromBank"))));
		}
		else
		{
			Text = bIncoming ? FText::Format(LOCTEXT("In", "Przelew od {0} · {1} → {2}"), FText::FromString(FWUi::Str(Transfer, TEXT("from"))), Money, FText::FromString(FWUi::Str(Transfer, TEXT("toBank"))))
				: FText::Format(LOCTEXT("Out", "Przelew do {0} · {1} → {2}"), FText::FromString(FWUi::Str(Transfer, TEXT("to"))), Money, FText::FromString(FWUi::Str(Transfer, TEXT("toBank"))));
		}
		const double Arrives = FWUi::Num(Transfer, TEXT("arrivesAt"));
		Add(SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(bIncoming ? FWStyle::Good : FWStyle::Text)]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
					.Text_Lambda([WeakThis, Arrives]()
					{
						TSharedPtr<SFWBankWindow> This = WeakThis.Pin();
						const int64 Seconds = This ? FMath::CeilToInt64(FMath::Max(Arrives - This->ServerNow(), 0.0) / 1000.0) : 0;
						return Seconds > 0 ? FText::FromString(FWBankText::Clock(Seconds)) : LOCTEXT("Arriving", "dociera…");
					})
				]
			], 3.f);
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page]);
	Scroll->SetScrollOffset(Offset);
}

void SFWBankWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() >= NextPoll)
	{
		Load();
	}
}

FReply SFWBankWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
