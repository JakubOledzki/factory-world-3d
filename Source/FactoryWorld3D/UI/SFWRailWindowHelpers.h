#pragma once

// What the parts of SFWRailWindow (SFWRailWindow*.cpp) share: the rail texts and small widgets.
// Included once per translation unit, after LOCTEXT_NAMESPACE is defined, so a unity build sees each helper once.

#include "UI/SFWRailWindow.h"
#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

namespace
{
namespace FWRailText
{
	const TCHAR* const Tabs[] = { TEXT("Trasy"), TEXT("Magazyn"), TEXT("Wyślij"), TEXT("Rozkład"), TEXT("W drodze") };
	enum ETab { Freight, Stock, Send, Routes, Trips };

	FText Problem(const FString& Code)
	{
		static const TMap<FString, FText> Problems = {
			{ TEXT("empty"), LOCTEXT("Empty", "Czeka na towar: na stacji nie było nic do zabrania.") },
			{ TEXT("no_truck_depot"), LOCTEXT("NoDepot", "Stoi: na działce nie ma bazy ciężarówek.") },
			{ TEXT("no_diesel"), LOCTEXT("NoDiesel", "Stoi: ciężarówka czeka na olej napędowy na stacji.") },
			{ TEXT("too_far"), LOCTEXT("TooFar", "Stoi: za daleko dla ciężarówki.") },
			{ TEXT("no_money"), LOCTEXT("NoMoney", "Stoi: brakuje pieniędzy na opłatę.") },
			{ TEXT("no_station"), LOCTEXT("NoStation", "Stoi: działka docelowa nie ma już właściciela.") },
			{ TEXT("embargo"), LOCTEXT("Embargo", "Stoi na granicy: kraj docelowy ma embargo.") },
			{ TEXT("banned_goods"), LOCTEXT("Banned", "Stoi na granicy: kraj docelowy zakazuje części towaru (licencja albo przemyt).") },
			{ TEXT("travel_banned"), LOCTEXT("TravelBanned", "Stoi: policja zakazała ci przekraczania granic.") },
			{ TEXT("no_money_duty"), LOCTEXT("NoMoneyDuty", "Stoi: brakuje pieniędzy na opłatę i cło.") } };
		return Problems.Contains(Code) ? Problems[Code] : FText::FromString(Code);
	}

	FText FreightProblem(const FString& Code)
	{
		static const TMap<FString, FText> Problems = {
			{ TEXT("waiting"), LOCTEXT("Waiting", "Czeka na towar w skrzyniach ładujących.") },
			{ TEXT("no_money"), LOCTEXT("FreightNoMoney", "Czeka: brakuje pieniędzy na opłatę.") },
			{ TEXT("no_room"), LOCTEXT("NoRoom", "Czeka na miejsce w skrzyniach odbierających.") },
			{ TEXT("embargo"), LOCTEXT("FreightEmbargo", "Stoi na granicy: kraj docelowy ma embargo.") },
			{ TEXT("banned_goods"), LOCTEXT("FreightBanned", "Stoi na granicy: kraj docelowy zakazuje części towaru (licencja albo przemyt).") },
			{ TEXT("travel_banned"), LOCTEXT("FreightTravelBanned", "Stoi: policja zakazała ci przekraczania granic.") },
			{ TEXT("no_money_duty"), LOCTEXT("FreightNoMoneyDuty", "Czeka: brakuje pieniędzy na opłatę i cło.") } };
		return Problems.Contains(Code) ? Problems[Code] : LOCTEXT("Loading", "Ładuje…");
	}

	FString Clock(double Seconds)
	{
		const int64 S = int64(FMath::Max(Seconds, 0.0));
		return S >= 3600 ? FString::Printf(TEXT("%lld:%02lld:%02lld"), S / 3600, S % 3600 / 60, S % 60) : FString::Printf(TEXT("%lld:%02lld"), S / 60, S % 60);
	}

	FText Grouped(int64 Value)
	{
		return FText::AsNumber(Value);
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

	TSharedRef<SWidget> Line(AFWPlayerController* PC, const FString& Item, const FText& Text, const FLinearColor& Color, TSharedPtr<SWidget> Right = nullptr)
	{
		return SNew(SFWCard).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, Item.IsEmpty() ? 0.f : 8.f, 0)[Item.IsEmpty() ? SNullWidget::NullWidget : FWUi::Icon(PC, Item, 26.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(Color).AutoWrapText(true)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget]
			];
	}
}
}
