// Projects, orders and deals between companies.
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

#undef LOCTEXT_NAMESPACE
