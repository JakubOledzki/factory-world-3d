// The stock exchange and the rankings.
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

#undef LOCTEXT_NAMESPACE
