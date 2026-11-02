// The company's main tabs: the company itself and its cash.
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
	Add(SNew(STextBlock).Font(FWStyle::Display(24)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FWUi::Str(Company, TEXT("name")))), 0.f);
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

#undef LOCTEXT_NAMESPACE
