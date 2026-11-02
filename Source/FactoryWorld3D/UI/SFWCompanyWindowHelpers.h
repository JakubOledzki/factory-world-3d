#pragma once

// What the parts of SFWCompanyWindow (SFWCompanyWindow*.cpp) share: the company's texts and small widgets.
// Included once per translation unit, after LOCTEXT_NAMESPACE is defined, so a unity build sees each helper once.

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

namespace
{
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
}
