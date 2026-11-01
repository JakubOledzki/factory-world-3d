#include "UI/SFWDeviceWindow.h"

#include "Framework/Application/SlateApplication.h"
#include "FWPlayerController.h"
#include "Game/FWCatalog.h"
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

#define LOCTEXT_NAMESPACE "FWDevice"

namespace FWDeviceText
{
	// "12 kW" or "12,5 kW"
	FString Kw(double Value)
	{
		return FMath::Abs(Value - FMath::RoundToDouble(Value)) < 0.05 ? FString::Printf(TEXT("%d kW"), int32(FMath::RoundToDouble(Value)))
			: FString::Printf(TEXT("%.1f kW"), Value).Replace(TEXT("."), TEXT(","));
	}

	FString Energy(double Kj)
	{
		return Kj >= 1000.0 ? FString::Printf(TEXT("%.1f MJ"), Kj / 1000.0).Replace(TEXT("."), TEXT(",")) : FString::Printf(TEXT("%d kJ"), int32(FMath::RoundToDouble(Kj)));
	}

	// 4.6153 → "4,6"; 5 → "5"
	FString Number(double Value)
	{
		FString Text = FString::Printf(TEXT("%.1f"), FMath::RoundToDouble(Value * 10.0) / 10.0);
		Text.RemoveFromEnd(TEXT(".0"));
		return Text.Replace(TEXT("."), TEXT(","));
	}

	FString Machines(int32 Count)
	{
		if (Count == 1)
		{
			return TEXT("maszyna");
		}
		const int32 Last = Count % 10, Teens = Count % 100;
		return Last >= 2 && Last <= 4 && (Teens < 12 || Teens > 14) ? TEXT("maszyny") : TEXT("maszyn");
	}

	FString Hours(double Seconds)
	{
		const int64 Minutes = int64(FMath::CeilToDouble(Seconds / 60.0));
		return Minutes < 60 ? FString::Printf(TEXT("%lld min"), Minutes) : Minutes % 60 == 0 ? FString::Printf(TEXT("%lld h"), Minutes / 60)
			: FString::Printf(TEXT("%lld h %lld min"), Minutes / 60, Minutes % 60);
	}

	// The device's name when the backpack has not named it yet
	FString Name(const FString& Kind)
	{
		static const TMap<FString, FString> Names = {
			{ TEXT("power_generator"), TEXT("Generator parowy") }, { TEXT("power_coal_plant"), TEXT("Elektrownia węglowa") },
			{ TEXT("power_gas_plant"), TEXT("Elektrownia gazowa") }, { TEXT("power_diesel_generator"), TEXT("Agregat spalinowy") },
			{ TEXT("power_nuclear_plant"), TEXT("Elektrownia jądrowa") }, { TEXT("power_fusion_plant"), TEXT("Elektrownia fuzyjna") },
			{ TEXT("power_biomass_plant"), TEXT("Elektrownia na biomasę") }, { TEXT("power_chp_plant"), TEXT("Elektrociepłownia") },
			{ TEXT("power_fuel_cell"), TEXT("Ogniwo paliwowe") }, { TEXT("power_water_wheel"), TEXT("Koło wodne") },
			{ TEXT("power_hydro_plant"), TEXT("Mała elektrownia wodna") }, { TEXT("power_tidal_plant"), TEXT("Elektrownia pływowa") },
			{ TEXT("power_geothermal_plant"), TEXT("Elektrownia geotermalna") }, { TEXT("power_solar_panel"), TEXT("Panel słoneczny") },
			{ TEXT("power_solar_tower"), TEXT("Elektrownia słoneczna z lustrami") }, { TEXT("power_battery"), TEXT("Akumulator") },
			{ TEXT("power_energy_store"), TEXT("Magazyn energii") }, { TEXT("power_wind_turbine"), TEXT("Turbina wiatrowa") },
			{ TEXT("power_large_wind_turbine"), TEXT("Duża turbina wiatrowa") }, { TEXT("power_heater"), TEXT("Kocioł grzewczy") },
			{ TEXT("furniture_drill"), TEXT("Wiertło górnicze") }, { TEXT("furniture_heavy_drill"), TEXT("Wiertło udarowe") },
			{ TEXT("furniture_pumpjack"), TEXT("Żuraw pompowy") }, { TEXT("furniture_gas_well"), TEXT("Szyb gazowy") },
			{ TEXT("furniture_offshore_platform"), TEXT("Platforma wiertnicza") }, { TEXT("power_grid_tie"), TEXT("Przyłącze sieciowe") } };
		return Names.Contains(Kind) ? Names[Kind] : Kind;
	}

	bool Typing()
	{
		const TSharedPtr<SWidget> Focused = FSlateApplication::Get().GetKeyboardFocusedWidget();
		return Focused.IsValid() && Focused->GetType().ToString().Contains(TEXT("EditableText"));
	}
}

SFWDeviceWindow::EMode SFWDeviceWindow::ModeOf(const FString& Kind)
{
	static const TSet<FString> Power = {
		TEXT("power_generator"), TEXT("power_coal_plant"), TEXT("power_gas_plant"), TEXT("power_diesel_generator"), TEXT("power_nuclear_plant"),
		TEXT("power_fusion_plant"), TEXT("power_biomass_plant"), TEXT("power_chp_plant"), TEXT("power_fuel_cell"),
		TEXT("power_water_wheel"), TEXT("power_hydro_plant"), TEXT("power_tidal_plant"), TEXT("power_geothermal_plant"),
		TEXT("power_solar_panel"), TEXT("power_solar_tower"), TEXT("power_battery"), TEXT("power_energy_store"),
		TEXT("power_wind_turbine"), TEXT("power_large_wind_turbine"), TEXT("power_heater") };
	if (Power.Contains(Kind))
	{
		return EMode::Generator;
	}
	if (Kind == TEXT("furniture_drill") || Kind == TEXT("furniture_heavy_drill"))
	{
		return EMode::Drill;
	}
	if (Kind == TEXT("furniture_pumpjack") || Kind == TEXT("furniture_gas_well") || Kind == TEXT("furniture_offshore_platform"))
	{
		return EMode::Well;
	}
	return Kind == TEXT("power_grid_tie") ? EMode::Tie : EMode::None;
}

void SFWDeviceWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Object = InArgs._Object;
	Kind = InArgs._Kind;
	Mode = ModeOf(Kind);
	Form = MakeShared<FFWForm>();
	AFWPlayerController* PC = Controller.Get();
	if (PC && PC->GetPlot())
	{
		Plot = FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
	}
	const FString Known = PC ? PC->ItemName(Kind) : FString();
	const FText Title = FText::FromString(Known.IsEmpty() || Known == Kind ? FWDeviceText::Name(Kind) : Known);
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	ChildSlot
	[
		FWUi::Frame(Title, Right, nullptr, SAssignNew(Content, SBox), Mode == EMode::Tie ? 900.f : 780.f, Mode == EMode::Generator ? 720.f : 600.f)
	];
	if (PC)
	{
		PC->RefreshInventory();
	}
	Load();
}

double SFWDeviceWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

int32 SFWDeviceWindow::Owned(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

TSharedPtr<FJsonObject> SFWDeviceWindow::Here() const
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), Plot.X);
	P->SetNumberField(TEXT("y"), Plot.Y);
	P->SetStringField(TEXT("id"), Object);
	return P;
}

void SFWDeviceWindow::Load(bool bPoll)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || Mode == EMode::None)
	{
		return;
	}
	NextPoll = FPlatformTime::Seconds() + (Mode == EMode::Tie ? 3.0 : 2.0);
	static const TCHAR* Types[] = { TEXT(""), TEXT("generator.get"), TEXT("drill.get"), TEXT("well.get"), TEXT("powertie.get") };
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	PC->Send(Types[uint8(Mode)], Here(), [WeakThis, bPoll](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin();
		// Asked again by itself: not built anew under the player typing, and no error toasts every two seconds
		if (!This || (bPoll && (FWDeviceText::Typing() || Reply.IsError())))
		{
			return;
		}
		This->Apply(Reply);
	});
}

bool SFWDeviceWindow::Apply(const FFWNetReply& Reply)
{
	static const TCHAR* Replies[] = { TEXT(""), TEXT("generator"), TEXT("drill"), TEXT("well"), TEXT("powertie") };
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != Replies[uint8(Mode)])
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		if (!State.IsValid() && Content.IsValid())
		{
			Content->SetContent(FWUi::Note(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()), FWStyle::Error));
		}
		return false;
	}
	State = Reply.Payload;
	ClockOffset = FWUi::Num(State, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
	double Balance = 0.0;
	if (PC && State->TryGetNumberField(TEXT("balanceCents"), Balance))
	{
		PC->SetBalanceCents(int64(Balance));
	}
	Rebuild();
	return true;
}

void SFWDeviceWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, bool bInventory)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, bInventory](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner || !This->Apply(Reply))
		{
			return;
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		// What came out into the backpack
		TArray<FString> Taken;
		for (const TSharedPtr<FJsonObject>& Stack : FWUi::Objects(This->State, TEXT("taken")))
		{
			Taken.Add(FString::Printf(TEXT("%s × %d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity")))));
		}
		if (Taken.Num() > 0)
		{
			Inner->Toast(FText::Format(LOCTEXT("Taken", "Do ekwipunku: {0}"), FText::FromString(FString::Join(Taken, TEXT(", ")))));
		}
		if (bInventory)
		{
			Inner->RefreshInventory();
		}
	});
}

void SFWDeviceWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	TSharedRef<SWidget> Page = !State.IsValid() ? FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted)
		: Mode == EMode::Generator ? MakeGenerator() : Mode == EMode::Drill ? MakeDrill() : Mode == EMode::Well ? MakeWell() : MakeTie();
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()[Page]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 18, 0, 0).HAlign(HAlign_Left)
		[
			FWUi::SmallButton(LOCTEXT("PickUp", "Schowaj do ekwipunku"), [WeakThis]()
			{
				TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin();
				if (AFWPlayerController* PC = This ? This->Controller.Get() : nullptr)
				{
					const FString Id = This->Object;
					PC->CloseWindow();
					PC->Pickup(Id);
				}
			})
		];
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Box]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWDeviceWindow::MakeGenerator()
{
	using namespace FWDeviceText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const bool bSolar = FWUi::Bool(State, TEXT("solar")), bBattery = FWUi::Bool(State, TEXT("battery")), bWind = FWUi::Bool(State, TEXT("wind"));
	const bool bHeater = FWUi::Bool(State, TEXT("heater"));
	const bool bFree = FWUi::Bool(State, TEXT("hydro")) || bSolar || bBattery || bWind || FWUi::Bool(State, TEXT("geothermal"));
	const double Output = FWUi::Num(State, TEXT("outputKw")), Capacity = FWUi::Num(State, TEXT("capacityKw"));
	const TSharedPtr<FJsonObject> Fuel = FWUi::Obj(State, TEXT("fuel"));

	// What burns in it now
	if (!bFree)
	{
		FString Text = Fuel.IsValid() ? FString::Printf(TEXT("%s × %d"), *FWUi::Str(Fuel, TEXT("name")), int32(FWUi::Num(Fuel, TEXT("quantity")))) : TEXT("pusto");
		if (FWUi::Num(State, TEXT("energy")) > 0.0)
		{
			Text += FString::Printf(TEXT("  (w ogniu jeszcze %s)"), *Energy(FWUi::Num(State, TEXT("energy"))));
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Fuel.IsValid() ? FWUi::Str(Fuel, TEXT("id")) : FString(), 36.f)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("Fuel", "Opał:"))
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(Text))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SBox).ToolTipText(LOCTEXT("TakeFuelTip", "Opał, który się jeszcze nie pali, wraca do ekwipunku"))
				[
					FWUi::SmallButton(LOCTEXT("TakeFuel", "Wyjmij"), [WeakThis]()
					{
						if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
						{
							This->Act(TEXT("generator.take"), This->Here(), true);
						}
					}, false, Fuel.IsValid())
				]
			], 0.f);
	}

	// What it gives
	FString OutputText;
	float Fraction = Capacity > 0.0 ? float(FMath::Clamp(Output / Capacity, 0.0, 1.0)) : 0.f;
	if (bHeater)
	{
		const int32 Warmed = int32(FWUi::Num(State, TEXT("warmed")));
		OutputText = !FWUi::Bool(State, TEXT("icy")) ? TEXT("Tu nie ma mrozu: maszyny pracują pełną parą i bez kotła.")
			: FWUi::Num(State, TEXT("total")) <= 0.0 ? FString::Printf(TEXT("Kocioł wygasł: %d %s wokół niego marznie i pracuje wolniej."), Warmed, *Machines(Warmed))
			: FString::Printf(TEXT("Grzeje %d %s na %d pól wokół siebie."), Warmed, *Machines(Warmed), int32(FWUi::Num(State, TEXT("heaterReach"), 3.0)));
	}
	else if (bBattery)
	{
		const double Charge = FWUi::Num(State, TEXT("chargeKj")), Full = FMath::Max(FWUi::Num(State, TEXT("capacityKj"), 1.0), 1.0);
		OutputText = FString::Printf(TEXT("Naładowany %s z %s (%d%%) · %s"), *Energy(Charge), *Energy(Full), int32(FMath::RoundToDouble(100.0 * Charge / Full)),
			*(Output > 0.001 ? FString::Printf(TEXT("oddaje %s"), *Kw(Output)) : Output < -0.001 ? FString::Printf(TEXT("ładuje się %s"), *Kw(-Output)) : FString(TEXT("czeka"))));
		Fraction = float(FMath::Clamp(Charge / Full, 0.0, 1.0));
	}
	else
	{
		const TCHAR* Source = bSolar ? TEXT("Słońce daje") : bWind ? TEXT("Wiatr daje") : bFree ? TEXT("Woda daje") : TEXT("Daje");
		OutputText = FString::Printf(TEXT("%s %s z %s"), Source, *Kw(Output), *Kw(Capacity));
	}
	Add(SNew(STextBlock).Font(FWStyle::Font(18, TEXT("Bold"))).AutoWrapText(true).ColorAndOpacity(Output > 0.0 ? FWStyle::Accent : FWStyle::Muted)
		.Text(FText::FromString(OutputText)), 14.f);
	if (!bHeater)
	{
		Add(FWUi::Bar(Fraction, FWStyle::Accent), 6.f);
	}

	// When the fuel runs out, counted down here between the polls
	if (!bFree)
	{
		const double RunsOutAt = FWUi::Num(State, TEXT("runsOutAt"), -1.0);
		const bool bOut = FWUi::Num(State, TEXT("total")) <= 0.0;
		Add(SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(bOut ? FWStyle::Error : FWStyle::Muted)
			.Text_Lambda([WeakThis, RunsOutAt, bOut]()
			{
				TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin();
				if (!This || RunsOutAt < 0.0)
				{
					return bOut ? LOCTEXT("Out", "Generator wygasł: dołóż opału.") : FText::GetEmpty();
				}
				const int64 Left = FMath::Max<int64>(0, int64(FMath::CeilToDouble((RunsOutAt - This->ServerNow()) / 1000.0)));
				return FText::FromString(Left >= 3600 ? FString::Printf(TEXT("Opał skończy się za %lld h %02lld min."), Left / 3600, Left / 60 % 60)
					: FString::Printf(TEXT("Opał skończy się za %lld min %02lld s."), Left / 60, Left % 60));
			}), 6.f);
	}

	// The power network it feeds
	if (!bHeater)
	{
		const TSharedPtr<FJsonObject> Network = FWUi::Obj(State, TEXT("network"));
		FString Text;
		FLinearColor Color = FWStyle::Text;
		if (!Network.IsValid())
		{
			Text = TEXT("Żaden słup go nie sięga. Postaw słup energetyczny najwyżej 2 pola od niego, a słupy przy maszynach połączą się przewodami.");
			Color = FWStyle::Error;
		}
		else if (FWUi::Num(Network, TEXT("machines")) == 0.0)
		{
			Text = bFree ? TEXT("Słupy nie sięgają jeszcze żadnej maszyny.") : TEXT("Słupy nie sięgają jeszcze żadnej maszyny, więc generator nie pali opału.");
			Color = FWStyle::Muted;
		}
		else
		{
			const double Speed = FWUi::Num(Network, TEXT("speed"));
			const int32 Count = int32(FWUi::Num(Network, TEXT("machines")));
			const double Demand = FWUi::Num(Network, TEXT("demandKw"));
			Text = FString::Printf(TEXT("Sieć: %d %s %s %s, źródła prądu dają %s."), Count, *Machines(Count), Machines(Count) == TEXT("maszyny") ? TEXT("pobierają") : TEXT("pobiera"),
				*Kw(Demand), *Kw(FMath::Min(FWUi::Num(Network, TEXT("supplyKw")), Demand)));
			if (Speed >= 0.999)
			{
				Text += TEXT(" Maszyny mają pełną moc.");
			}
			else
			{
				Text += Speed > 0.0 ? FString::Printf(TEXT(" Za mało prądu: maszyny pracują na %d%%. Dołóż drugi generator albo koło wodne."), int32(FMath::RoundToDouble(Speed * 100.0)))
					: FString(TEXT(" Brak prądu: maszyny stoją."));
				Color = FWStyle::Error;
			}
		}
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(12.f)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).AutoWrapText(true).ColorAndOpacity(Color).Text(FText::FromString(Text))
			], 14.f);
	}

	// Feeding the fire from the backpack
	if (!bFree)
	{
		Add(FWUi::Heading(bHeater ? LOCTEXT("FeedHeater", "Dołóż do kotła") : LOCTEXT("Feed", "Dołóż do ognia")), 16.f);
		const int32 Held = Fuel.IsValid() ? int32(FWUi::Num(Fuel, TEXT("quantity"))) : 0;
		const int32 Room = int32(FWUi::Num(State, TEXT("maxFuel"))) - Held;
		bool bAny = false;
		TArray<FString> Names;
		for (const TSharedPtr<FJsonObject>& Option : FWUi::Objects(State, TEXT("fuels")))
		{
			const FString Id = FWUi::Str(Option, TEXT("id"));
			Names.Add(FWUi::Str(Option, TEXT("name")).ToLower());
			const int32 Have = Owned(Id);
			if (Have == 0)
			{
				continue;
			}
			bAny = true;
			const bool bBlocked = Fuel.IsValid() && FWUi::Str(Fuel, TEXT("id")) != Id;
			TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 30.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
					.Text(FText::FromString(FString::Printf(TEXT("%s  ×%d   ·  %s"), *FWUi::Str(Option, TEXT("name")), Have, *Energy(FWUi::Num(Option, TEXT("kj"))))))
				];
			for (const int32 Amount : { 1, 10, 0 })
			{
				const int32 Count = Amount == 0 ? FMath::Min(Have, Room) : Amount;
				Row->AddSlot().AutoWidth().Padding(6, 0, 0, 0)
				[
					FWUi::SmallButton(Amount == 0 ? LOCTEXT("All", "Wszystko") : FText::FromString(FString::Printf(TEXT("+%d"), Amount)), [WeakThis, Id, Count]()
					{
						if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
						{
							TSharedPtr<FJsonObject> P = This->Here();
							P->SetStringField(TEXT("item"), Id);
							P->SetNumberField(TEXT("quantity"), Count);
							This->Act(TEXT("generator.load"), P, true);
						}
					}, Amount == 0, !bBlocked && Count >= 1 && Count <= Have && Count <= Room)
				];
			}
			Add(Row, 4.f);
		}
		if (!bAny)
		{
			const TCHAR* Hint = Kind == TEXT("power_generator") || Kind == TEXT("power_biomass_plant") || Kind == TEXT("power_chp_plant") || Kind == TEXT("power_heater")
				? TEXT("Drewno daje ścinanie drzew, węgiel skały i kopalnie w górach.")
				: Kind == TEXT("power_fuel_cell") ? TEXT("Wodór robi z wody elektrolizer na prąd.") : TEXT("Gaz pompuje głowica gazowa, paliwa robi rafineria.");
			Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Nie masz opału, który to spala (%s). %s"), *FString::Join(Names, TEXT(", ")), Hint)), FWStyle::Muted));
		}
	}

	// How it works
	FString Help;
	if (bHeater)
	{
		Help = FString::Printf(TEXT("Na lodowcu maszyny na prąd bez ogrzewania pracują o połowę wolniej. Kocioł spala opał bez przerwy (%s, nawet gdy nic nie grzeje) i ogrzewa maszyny do %d pól wokół siebie, a słup nie jest mu potrzebny."),
			*Kw(FWUi::Num(State, TEXT("heaterKw"), 15.0)), int32(FWUi::Num(State, TEXT("heaterReach"), 3.0)));
	}
	else if (bWind)
	{
		const double Bonus = FWUi::Num(State, TEXT("windBonus"), 1.0);
		Help = FString::Printf(TEXT("Wiatr kręci nią bez opału, a jego siła zmienia się co 10 minut.%s Nadwyżkę zbiera akumulator w tej samej sieci. %s"),
			*(Bonus > 1.0 ? FString::Printf(TEXT(" Tu wieje mocniej: +%d%% mocy."), int32(FMath::RoundToDouble((Bonus - 1.0) * 100.0))) : FString()),
			*(FWUi::Bool(State, TEXT("blizzard")) ? FString(TEXT("Szaleje zamieć: turbina stoi, żeby wiatr jej nie połamał."))
				: FString::Printf(TEXT("Teraz wieje na %d%% pełnej siły."), int32(FMath::RoundToDouble(FWUi::Num(State, TEXT("windStrength")) * 100.0)))));
	}
	else if (bSolar)
	{
		FString Sun = FWUi::Bool(State, TEXT("day")) ? TEXT("Teraz dzień.") : TEXT("Teraz noc: panele nie dają prądu do świtu.");
		if (FWUi::Bool(State, TEXT("storm")))
		{
			Sun = FWUi::Bool(State, TEXT("sheltered")) ? TEXT("Wieje burza piaskowa: płot trzcinowy osłania ten panel.") : TEXT("Wieje burza piaskowa: bez płotu trzcinowego w pobliżu daje ćwierć mocy.");
		}
		Help = TEXT("Słońce daje prąd za dnia (36 z każdych 60 minut), na pustyni i w oazie o 25% więcej. Nadwyżkę zbiera akumulator w tej samej sieci. ") + Sun;
	}
	else if (bBattery)
	{
		Help = FString::Printf(TEXT("%s bierze to, czego woda, słońce i wiatr dają za dużo (do %s), i oddaje, gdy im brakuje, zanim generatory zaczną palić opał."),
			Kind == TEXT("power_energy_store") ? TEXT("Magazyn energii") : TEXT("Akumulator"), *Kw(Capacity));
	}
	else if (FWUi::Bool(State, TEXT("geothermal")))
	{
		const double Heat = FWUi::Num(State, TEXT("hydroBonus"), 1.0);
		Help = TEXT("Ciepło ziemi kręci turbiną bez przerwy i nie trzeba dokładać opału. Daje prąd od razu, przed generatorami parowymi.");
		if (Heat > 1.0)
		{
			Help += FString::Printf(TEXT(" Pod lodowcem skała jest gorętsza: +%d%% mocy."), int32(FMath::RoundToDouble((Heat - 1.0) * 100.0)));
		}
		else if (Heat > 0.0 && Heat < 1.0)
		{
			Help += FString::Printf(TEXT(" Na tundrze skała jest chłodniejsza: %d%% mocy."), int32(FMath::RoundToDouble(Heat * 100.0)));
		}
	}
	else if (bFree)
	{
		const double Bonus = FWUi::Num(State, TEXT("hydroBonus"), 1.0);
		Help = TEXT("Woda kręci nim bez przerwy i nie trzeba dokładać opału. Daje prąd od razu, przed generatorami parowymi: one dopalają tylko to, czego brakuje.");
		if (Bonus > 1.0)
		{
			Help += FString::Printf(TEXT(" Górski potok płynie szybciej: +%d%% mocy."), int32(FMath::RoundToDouble((Bonus - 1.0) * 100.0)));
		}
	}
	else
	{
		TArray<FString> Lines;
		for (const TSharedPtr<FJsonObject>& Option : FWUi::Objects(State, TEXT("fuels")))
		{
			const double Kj = FWUi::Num(Option, TEXT("kj"));
			Lines.Add(FString::Printf(TEXT("%s: %s, przy pełnej mocy na %d s."), *FWUi::Str(Option, TEXT("name")), *Energy(Kj), Capacity > 0.0 ? int32(FMath::RoundToDouble(Kj / Capacity)) : 0));
		}
		Lines.Add(TEXT("Podajnik może dokładać opał z taśmy albo ze skrzyni."));
		if (FWUi::Bool(State, TEXT("dry")))
		{
			Lines.Add(TEXT("Na piasku brak wody do chłodzenia: daje tylko 70% mocy."));
		}
		if (FWUi::Bool(State, TEXT("chp")))
		{
			Lines.Add(FString::Printf(TEXT("Na lodowcu, póki pali, ogrzewa maszyny do %d pól wokół (teraz %d), jak kocioł grzewczy."),
				int32(FWUi::Num(State, TEXT("chpReach"), 6.0)), int32(FWUi::Num(State, TEXT("warmed")))));
		}
		Help = FString::Join(Lines, TEXT("\n"));
	}
	Add(FWUi::Note(FText::FromString(Help), FWStyle::Muted), 16.f);
	return Page;
}

TSharedRef<SWidget> SFWDeviceWindow::MakeDrill()
{
	using namespace FWDeviceText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const double Speed = FWUi::Num(State, TEXT("speed"));
	const int32 Stored = int32(FWUi::Num(State, TEXT("stored"))), Capacity = int32(FWUi::Num(State, TEXT("capacity")));

	Add(SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
		.Text(FText::FromString(FString::Printf(TEXT("%s · poziom %d z 2 · kopalnia poziomu %d"), *FWUi::Str(State, TEXT("name")),
			int32(FWUi::Num(State, TEXT("level"))), int32(FWUi::Num(State, TEXT("mineLevel")))))), 0.f);
	FString Pace = FString::Printf(TEXT("Jeden urobek na %s s (z pełnym prądem)."), *Number(FWUi::Num(State, TEXT("seconds"))));
	if (Speed <= 0.0)
	{
		Pace += TEXT(" Brak prądu: wiertło stoi. Postaw słup obok i generator albo koło wodne.");
	}
	else if (Speed < 0.999)
	{
		Pace += FString::Printf(TEXT(" Za mało prądu: pracuje na %d%%."), int32(FMath::RoundToDouble(Speed * 100.0)));
	}
	Add(FWUi::Note(FText::FromString(Pace), Speed < 0.999 ? FWStyle::Error : FWStyle::Text));

	// The ore inside
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, TEXT("iron_ore"), 36.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(18, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("Urobek: %d / %d"), Stored, Capacity)))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("TakeOre", "Wyjmij urobek"), [WeakThis]()
			{
				if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
				{
					This->Act(TEXT("drill.take"), This->Here(), true);
				}
			}, true, Stored > 0)
		], 16.f);
	Add(FWUi::Bar(Capacity > 0 ? float(Stored) / float(Capacity) : 0.f, FWStyle::Accent), 6.f);
	TSharedRef<SWrapBox> Ores = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(18.f, 6.f));
	for (const TSharedPtr<FJsonObject>& Ore : FWUi::Objects(State, TEXT("ores")))
	{
		Ores->AddSlot()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Icon(PC, FWUi::Str(Ore, TEXT("id")), 26.f)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s × %d"), *FWUi::Str(Ore, TEXT("name")), int32(FWUi::Num(Ore, TEXT("quantity"))))))
			]
		];
	}
	Add(Ores, 10.f);

	// What it does now
	FString Status;
	FLinearColor Color = FWStyle::Muted;
	if (FWUi::Bool(State, TEXT("full")))
	{
		Status = TEXT("Pełne: wiertło nie kopie, dopóki nie wyjmiesz urobku (ręcznie albo podajnikiem).");
		Color = FWStyle::Error;
	}
	else if (FWUi::Bool(State, TEXT("waiting")))
	{
		const double RefillAt = FWUi::Num(State, TEXT("refillAt"), -1.0);
		const int64 Left = RefillAt < 0.0 ? -1 : FMath::Max<int64>(0, int64(FMath::CeilToDouble((RefillAt - ServerNow()) / 1000.0)));
		Status = FString::Printf(TEXT("Kopalnia wyczerpana: wiertło czeka, aż się odnowi%s."), *(Left < 0 ? FString() : FString::Printf(TEXT(" (za %lld:%02lld)"), Left / 60, Left % 60)));
	}
	else if (Speed > 0.0)
	{
		Status = TEXT("Pracuje: wydobywa rudy, węgiel i to, co kryje góra.");
		Color = FWStyle::Accent;
	}
	if (!Status.IsEmpty())
	{
		Add(FWUi::Note(FText::FromString(Status), Color), 12.f);
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("W kopalni zostało %d z %d urobków. Kopiesz w niej razem z wiertłem."),
		int32(FWUi::Num(State, TEXT("unitsLeft"))), int32(FWUi::Num(State, TEXT("units"))))), FWStyle::Muted));
	const TSharedPtr<FJsonObject> Upgrade = FWUi::Obj(State, TEXT("upgrade"));
	Add(FWUi::Note(Upgrade.IsValid()
		? FText::FromString(FString::Printf(TEXT("Lepsze: %s (urobek co %s s, mieści %d). Zrobisz je przy stole warsztatowym z tego wiertła."),
			*FWUi::Str(Upgrade, TEXT("name")), *Number(FWUi::Num(Upgrade, TEXT("seconds"))), int32(FWUi::Num(Upgrade, TEXT("capacity")))))
		: LOCTEXT("BestDrill", "To najlepsze wiertło. Głębsza kopalnia (poziomy 4 i 5) da więcej srebra i złota."), FWStyle::Muted), 12.f);
	return Page;
}

TSharedRef<SWidget> SFWDeviceWindow::MakeWell()
{
	using namespace FWDeviceText;
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	static const TCHAR* Deposits[] = { TEXT("brak"), TEXT("małe"), TEXT("średnie"), TEXT("duże") };
	const FString Product = FWUi::Str(State, TEXT("productName"));
	const double Speed = FWUi::Num(State, TEXT("speed"));
	const bool bLeaking = FWUi::Bool(State, TEXT("leaking"));
	const int32 Stored = int32(FWUi::Num(State, TEXT("stored"))), Capacity = int32(FWUi::Num(State, TEXT("capacity")));
	const int32 Left = int32(FWUi::Num(State, TEXT("left")));

	Add(SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
		.Text(FText::FromString(FString::Printf(TEXT("%s · złoże %s (%s)"), *FWUi::Str(State, TEXT("name")),
			Deposits[FMath::Clamp(int32(FWUi::Num(State, TEXT("size"))), 0, 3)], *Product.ToLower()))), 0.f);
	FString Pace = FString::Printf(TEXT("Jedna jednostka na %s s%s."), *Number(FWUi::Num(State, TEXT("seconds"))), FWUi::Bool(State, TEXT("powered")) ? TEXT(" (z pełnym prądem)") : TEXT(""));
	if (bLeaking)
	{
		Pace += TEXT(" Cieknie: pompuje dwa razy wolniej.");
	}
	if (Speed <= 0.0)
	{
		Pace += TEXT(" Brak prądu: szyb stoi. Postaw słup obok i elektrownię.");
	}
	else if (Speed < 0.999)
	{
		Pace += FString::Printf(TEXT(" Za mało prądu: pracuje na %d%%."), int32(FMath::RoundToDouble(Speed * 100.0)));
	}
	Add(FWUi::Note(FText::FromString(Pace), Speed < 0.999 || bLeaking ? FWStyle::Error : FWStyle::Text));

	// What it pumped
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, FWUi::Str(State, TEXT("product")), 36.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(18, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("%s: %d / %d"), *Product, Stored, Capacity)))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("TakeProduct", "Wyjmij"), [WeakThis]()
			{
				if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
				{
					This->Act(TEXT("well.take"), This->Here(), true);
				}
			}, true, Stored > 0)
		], 16.f);
	Add(FWUi::Bar(Capacity > 0 ? float(Stored) / float(Capacity) : 0.f, FWStyle::Accent), 6.f);
	FString Status;
	FLinearColor Color = FWStyle::Error;
	if (Left <= 0)
	{
		Status = TEXT("Złoże wyczerpane: szyb nic już nie wypompuje.");
	}
	else if (FWUi::Bool(State, TEXT("full")))
	{
		Status = TEXT("Pełny: szyb nie pompuje, dopóki go nie opróżnisz (ręcznie albo pompą do rury).");
	}
	else if (FWUi::Bool(State, TEXT("polluting")))
	{
		Status = TEXT("Ropa wycieka i zanieczyszcza ziemię do 6 pól wokół: nic tam nie posiejesz ani nie posadzisz. Włóż uszczelkę albo postaw obok separator ropy.");
	}
	else if (Speed > 0.0)
	{
		Status = FString::Printf(TEXT("Pracuje: %s płynie sama, wyjmij ją ręcznie albo pompą."), *Product.ToLower());
		Color = FWStyle::Accent;
	}
	if (!Status.IsEmpty())
	{
		Add(FWUi::Note(FText::FromString(Status), Color), 10.f);
	}

	// Gaskets: one wears through every so many units
	const int32 Gaskets = int32(FWUi::Num(State, TEXT("gaskets"))), MaxGaskets = int32(FWUi::Num(State, TEXT("maxGaskets")));
	const double WearLimit = FMath::Max(FWUi::Num(State, TEXT("wearLimit"), 1.0), 1.0);
	Add(FWUi::Heading(LOCTEXT("Gaskets", "Uszczelki")), 18.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, TEXT("gasket"), 30.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(bLeaking ? FWStyle::Error : FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("W zapasie: %d / %d%s"), Gaskets, MaxGaskets, bLeaking ? TEXT(" · szyb cieknie!") : TEXT(""))))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SBox).ToolTipText(LOCTEXT("SealTip", "Włoży wszystkie uszczelki z ekwipunku, do kompletu"))
			[
				FWUi::SmallButton(LOCTEXT("Seal", "Włóż uszczelki"), [WeakThis]()
				{
					if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
					{
						This->Act(TEXT("well.seal"), This->Here(), true);
					}
				}, false, (bLeaking || Gaskets < MaxGaskets) && Owned(TEXT("gasket")) > 0)
			]
		], 4.f);
	Add(FWUi::Bar(bLeaking ? 1.f : float(FMath::Clamp(FWUi::Num(State, TEXT("wear")) / WearLimit, 0.0, 1.0)), bLeaking ? FWStyle::Error : FWStyle::Muted), 6.f);
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("W złożu zostało %d z %d. Uszczelka starcza na %d jednostek."), Left,
		int32(FWUi::Num(State, TEXT("reserve"))), int32(WearLimit))), FWStyle::Muted), 8.f);
	return Page;
}

TSharedRef<SWidget> SFWDeviceWindow::MakeTie()
{
	using namespace FWDeviceText;
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(LOCTEXT("TieIntro", "Postaw słup obok przyłącza, a kabel połączy sieć tej działki z siecią innej. Między twoimi działkami prąd płynie za darmo, od innych graczy kupujesz go na godziny."),
		FWStyle::Muted), 0.f);

	// Selling power through it
	Add(FWUi::Heading(LOCTEXT("Selling", "Sprzedaż prądu")), 16.f);
	if (!Form->Has(TEXT("price")))
	{
		Form->Set(TEXT("price"), FString::Printf(TEXT("%.2f"), FWUi::Num(State, TEXT("priceCents")) / 100.0));
		Form->Set(TEXT("open"), FWUi::Bool(State, TEXT("open")) ? TEXT("1") : TEXT("0"));
	}
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(LOCTEXT("PricePerKwh", "Cena za 1 kWh:"))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)[Form->Field(TEXT("price"), LOCTEXT("Dollars", "$"), 110.f)]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)
		[
			SNew(SBox).ToolTipText(LOCTEXT("OpenTip", "Inni gracze mogą poprowadzić kabel do tego przyłącza i kupować prąd po twojej cenie"))
			[
				Form->Check(TEXT("open"), LOCTEXT("Open", "sprzedawaj innym"), FWUi::Bool(State, TEXT("open")))
			]
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("Save", "Zapisz"), [WeakThis]()
			{
				if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = This->Here();
					P->SetNumberField(TEXT("priceCents"), FMath::Max(0.0, This->Form->Cents(TEXT("price"))));
					P->SetBoolField(TEXT("open"), This->Form->Ticked(TEXT("open")));
					This->Act(TEXT("powertie.price"), P, false);
				}
			}, true)
		]);

	// The cables that end here
	Add(FWUi::Heading(LOCTEXT("Cables", "Kable")), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Lines = FWUi::Objects(State, TEXT("lines"));
	if (Lines.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoCables", "Do tego przyłącza nie prowadzi jeszcze żaden kabel."), FWStyle::Muted));
	}
	const double Now = FWUi::Num(State, TEXT("serverTime"));
	for (const TSharedPtr<FJsonObject>& Line : Lines)
	{
		const FString Id = FWUi::Str(Line, TEXT("id")).IsEmpty() ? FString::Printf(TEXT("%lld"), int64(FWUi::Num(Line, TEXT("id")))) : FWUi::Str(Line, TEXT("id"));
		const bool bSupplying = FWUi::Bool(Line, TEXT("supplying"));
		const FString Owner = FWUi::Str(Line, TEXT("owner")).IsEmpty() ? TEXT("?") : FWUi::Str(Line, TEXT("owner"));
		const double Kwh = FWUi::Num(Line, TEXT("kw")), Price = FWUi::Num(Line, TEXT("priceCents"));
		double PaidUntil = 0.0;
		const bool bPaid = Line->TryGetNumberField(TEXT("paidUntil"), PaidUntil);
		FString Text = FString::Printf(TEXT("%s działka %d, %d (%s)\nKabel %s kW, teraz płynie %s kW."), bSupplying ? TEXT("Wysyła do:") : TEXT("Dostaje z:"),
			int32(FWUi::Num(Line, TEXT("x"))), int32(FWUi::Num(Line, TEXT("y"))), *Owner, *Number(Kwh), *Number(FWUi::Num(Line, TEXT("flowKw"))));
		FLinearColor Color = FWStyle::Text;
		if (!bPaid)
		{
			Text += TEXT(" Twoje działki: prąd za darmo.");
		}
		else if (PaidUntil > Now)
		{
			Text += FString::Printf(TEXT(" Opłacone jeszcze na %s, %s za kWh."), *Hours((PaidUntil - Now) / 1000.0), *FWUi::Money(Price).ToString());
		}
		else
		{
			Text += FString::Printf(TEXT(" Nieopłacone: prąd nie płynie. Cena %s za kWh."), *FWUi::Money(Price).ToString());
			Color = bSupplying ? FWStyle::Text : FWStyle::Error;
		}
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).AutoWrapText(true).ColorAndOpacity(Color).Text(FText::FromString(Text))
			];
		// Only the plot that gets the power pays for it, and only to another player
		if (!bSupplying && bPaid)
		{
			const FString HoursKey = TEXT("hours_") + Id;
			const double Typed = Form->Has(HoursKey) ? Form->Number(HoursKey) : 24.0;
			const double Cost = FMath::CeilToDouble(Kwh * FMath::Max(Typed, 0.0) * Price - 0.000001);
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)[Form->Field(HoursKey, LOCTEXT("HoursHint", "h"), 70.f, TEXT("24"))];
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
			[
				FWUi::SmallButton(FText::Format(LOCTEXT("Buy", "Kup za {0}"), FWUi::Money(Cost)), [WeakThis, Id, HoursKey, Price]()
				{
					if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("line"), Id);
						P->SetNumberField(TEXT("hours"), FMath::Clamp(FMath::RoundToDouble(This->Form->Has(HoursKey) ? This->Form->Number(HoursKey) : 24.0), 1.0,
							FWUi::Num(This->State, TEXT("maxHours"), 168.0)));
						// At the price the player saw: if the seller changed it meanwhile, the server refuses
						P->SetNumberField(TEXT("priceCents"), Price);
						This->Act(TEXT("powerline.buy"), P, false);
					}
				}, true)
			];
		}
		Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(8, 0, 0, 0)
		[
			SNew(SBox).ToolTipText(LOCTEXT("RemoveTip", "Kabel znika, a pieniądze za jego położenie i opłacony prąd przepadają. Sprzedawca nie zwinie kabla, póki prąd jest opłacony"))
			[
				FWUi::SmallButton(LOCTEXT("Remove", "Zwiń kabel"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = This->Here();
						P->SetStringField(TEXT("line"), Id);
						This->Act(TEXT("powerline.remove"), P, false);
					}
				})
			]
		];
		Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)[Row], 6.f);
	}

	// A new cable to this plot
	Add(FWUi::Heading(LOCTEXT("NewCable", "Nowy kabel do tej działki")), 18.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("Moc kabla (%d–%d kW):"), int32(FWUi::Num(State, TEXT("minKw"), 10.0)), int32(FWUi::Num(State, TEXT("maxKw"), 5000.0)))))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)[Form->Field(TEXT("kw"), LOCTEXT("KwHint", "kW"), 100.f, TEXT("100"))]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("Find", "Szukaj przyłączy"), [WeakThis]()
			{
				if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
				{
					This->FindOffers();
				}
			}, true)
		]);
	if (bOffersAsked)
	{
		Add(FWUi::Note(Offers.Num() == 0
			? LOCTEXT("NoOffers", "Nikt w pobliżu nie sprzedaje prądu, a na innych twoich działkach nie ma przyłączy (otwórz ich okno raz).")
			: FText::Format(LOCTEXT("CablePrice", "Kabel kosztuje {0} za każde pole odległości. Płacisz raz, przy kładzeniu."), FWUi::Money(FWUi::Num(State, TEXT("cableCentsPerTile")))),
			FWStyle::Muted), 8.f);
	}
	for (const TSharedPtr<FJsonObject>& Offer : Offers)
	{
		const bool bMine = FWUi::Bool(Offer, TEXT("mine"));
		const FString What = FString::Printf(TEXT("Działka %d, %d · %s"), int32(FWUi::Num(Offer, TEXT("x"))), int32(FWUi::Num(Offer, TEXT("y"))),
			*(bMine ? FString(TEXT("twoja, prąd za darmo")) : FString::Printf(TEXT("%s, %s za kWh"), *FWUi::Str(Offer, TEXT("owner")), *FWUi::Money(FWUi::Num(Offer, TEXT("priceCents"))).ToString())));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(What))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(FText::Format(LOCTEXT("Build", "Kabel za {0}"), FWUi::Money(FWUi::Num(Offer, TEXT("cableCents")))), [WeakThis, Offer]()
				{
					if (TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = This->Here();
						P->SetNumberField(TEXT("fromX"), FWUi::Num(Offer, TEXT("x")));
						P->SetNumberField(TEXT("fromY"), FWUi::Num(Offer, TEXT("y")));
						P->SetStringField(TEXT("fromId"), FWUi::Str(Offer, TEXT("id")));
						P->SetNumberField(TEXT("kw"), This->Form->Has(TEXT("kw")) ? This->Form->Number(TEXT("kw")) : 100.0);
						This->Offers.Reset();
						This->bOffersAsked = false;
						This->Act(TEXT("powerline.build"), P, false);
					}
				}, true)
			], 4.f);
	}
	return Page;
}

void SFWDeviceWindow::FindOffers()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWDeviceWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("powertie.offers"), Here(), [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWDeviceWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("powertie.offers"))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		This->Offers = FWUi::Objects(Reply.Payload, TEXT("offers"));
		This->bOffersAsked = true;
		This->Rebuild();
	});
}

void SFWDeviceWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (FPlatformTime::Seconds() >= NextPoll)
	{
		Load(true);
	}
}

FReply SFWDeviceWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
