// The freight tab.
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

#define LOCTEXT_NAMESPACE "FWRail"

#include "SFWRailWindowHelpers.h"

TSharedRef<SWidget> SFWRailWindow::MakeFreight()
{
	TWeakPtr<SFWRailWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	if (!Freight.IsValid())
	{
		Add(FWUi::Note(LOCTEXT("LoadingFreight", "Wczytywanie…"), FWStyle::Muted), 0.f);
		return Page;
	}
	const TSharedPtr<FJsonObject> Rates = FWUi::Obj(Freight, TEXT("rates"));
	const TArray<TSharedPtr<FJsonObject>> Routes = FWUi::Objects(Freight, TEXT("routes"));
	Add(FWUi::Heading(LOCTEXT("FreightRoutes", "Trasy między twoimi działkami")), 0.f);
	Add(FWUi::Note(LOCTEXT("FreightNote", "Pociąg trasy czeka na działce, aż skrzynie ładujące będą miały cały ładunek, zawozi go do skrzyń odbierających drugiej działki i wraca po następny."), FWStyle::Muted));
	if (Routes.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoFreight", "Nie masz jeszcze żadnej trasy."), FWStyle::Muted), 6.f);
	}
	for (const TSharedPtr<FJsonObject>& Route : Routes)
	{
		const TSharedPtr<FJsonObject> From = FWUi::Obj(Route, TEXT("from")), To = FWUi::Obj(Route, TEXT("to"));
		const FIntPoint FromAt(int32(FWUi::Num(From, TEXT("x"))), int32(FWUi::Num(From, TEXT("y")))), ToAt(int32(FWUi::Num(To, TEXT("x"))), int32(FWUi::Num(To, TEXT("y"))));
		const FString RouteState = FWUi::Str(Route, TEXT("state"));
		TArray<FString> Load;
		for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Route, TEXT("cargo")))
		{
			const int32 Quantity = int32(FWUi::Num(Item, TEXT("quantity")));
			Load.Add(RouteState == TEXT("loading") ? FString::Printf(TEXT("%s %d / %d"), *FWUi::Str(Item, TEXT("name")), FMath::Min(int32(FWUi::Num(Item, TEXT("stock"))), Quantity), Quantity)
				: FString::Printf(TEXT("%s %d"), *FWUi::Str(Item, TEXT("name")), Quantity));
		}
		const double NextAt = FWUi::Num(Route, TEXT("nextAt"));
		const FString ProblemCode = FWUi::Str(Route, TEXT("problem"));
		FString Status;
		if (RouteState == TEXT("unloading"))
		{
			int32 Left = 0;
			for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Route, TEXT("carried")))
			{
				Left += int32(FWUi::Num(Item, TEXT("quantity")));
			}
			Status = FString::Printf(TEXT("%s Zostało w pociągu %d szt."), *FWRailText::FreightProblem(TEXT("no_room")).ToString(), Left);
		}
		else if (RouteState != TEXT("travelling") && RouteState != TEXT("returning"))
		{
			Status = ProblemCode.IsEmpty() ? LOCTEXT("Loads", "Ładuje…").ToString() : FWRailText::FreightProblem(ProblemCode).ToString();
		}
		const FString Header = FString::Printf(TEXT("%s → %s%s\n%s\n%s km · w jedną stronę %s · kurs %s · kursów: %d (dowiezione %d szt.)"), *PlotLabel(FromAt), *PlotLabel(ToAt),
			FWUi::Bool(Route, TEXT("smuggle")) ? TEXT("  ·  przemyt") : TEXT(""), *FString::Join(Load, TEXT(", ")), *FWRailText::Grouped(int64(FWUi::Num(Route, TEXT("km")))).ToString(),
			*FWRailText::Clock(FWUi::Num(Route, TEXT("tripSeconds"))), *FWUi::Money(FWUi::Num(Route, TEXT("feeCents"))).ToString(), int32(FWUi::Num(Route, TEXT("trips"))), int32(FWUi::Num(Route, TEXT("delivered"))));
		const double Id = FWUi::Num(Route, TEXT("id"));
		TSharedRef<SVerticalBox> Right = SNew(SVerticalBox);
		Right->AddSlot().AutoHeight().HAlign(HAlign_Right)
		[
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(RouteState == TEXT("travelling") ? FWStyle::Good : (Status.IsEmpty() || ProblemCode == TEXT("waiting") ? FWStyle::Muted : FWStyle::Error))
			.Text_Lambda([WeakThis, RouteState, NextAt, Status]()
			{
				TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
				const double Seconds = This ? FMath::CeilToDouble((NextAt - This->ServerNow()) / 1000.0) : 0.0;
				if (RouteState == TEXT("travelling"))
				{
					return Seconds > 0.0 ? FText::FromString(FString::Printf(TEXT("Jedzie z towarem: dojedzie za %s"), *FWRailText::Clock(Seconds))) : LOCTEXT("Unloads", "Rozładowuje…");
				}
				if (RouteState == TEXT("returning"))
				{
					return Seconds > 0.0 ? FText::FromString(FString::Printf(TEXT("Wraca po następny ładunek: będzie za %s"), *FWRailText::Clock(Seconds))) : LOCTEXT("Back", "Wrócił…");
				}
				return FText::FromString(Status);
			})
		];
		Right->AddSlot().AutoHeight().HAlign(HAlign_Right).Padding(0, 4, 0, 0)
		[
			FWUi::Bool(Route, TEXT("stopping")) ? StaticCastSharedRef<SWidget>(SNew(STextBlock).Text(LOCTEXT("LastTrip", "ostatni kurs")).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted))
				: FWUi::SmallButton(LOCTEXT("RemoveFreight", "Usuń"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("id"), Id);
						This->FreightAct(TEXT("freight.remove"), P, FText::GetEmpty());
					}
				})
		];
		Add(FWRailText::Line(Controller.Get(), FString(), FText::FromString(Header), FromAt == Plot || ToAt == Plot ? FWStyle::Accent : FWStyle::Text, Right), 4.f);
	}
	// This plot: its name and chests
	Add(FWUi::Heading(LOCTEXT("ThisPlot", "Ta działka")), 16.f);
	const TSharedPtr<FJsonObject> Here = FreightPlot(Plot);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("plotName"), LOCTEXT("PlotName", "Nazwa działki"), 300.f, FWUi::Str(Here, TEXT("name")))]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("Rename", "Zmień nazwę"), [WeakThis]()
			{
				TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
				AFWPlayerController* PC = This ? This->Controller.Get() : nullptr;
				if (!PC)
				{
					return;
				}
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetNumberField(TEXT("x"), This->Plot.X);
				P->SetNumberField(TEXT("y"), This->Plot.Y);
				P->SetStringField(TEXT("name"), This->Form->Get(TEXT("plotName")).Left(32));
				PC->Send(TEXT("world.rename"), P, [WeakThis](const FFWNetReply& Reply)
				{
					TSharedPtr<SFWRailWindow> Inner = WeakThis.Pin();
					AFWPlayerController* InnerPC = Inner ? Inner->Controller.Get() : nullptr;
					if (!InnerPC)
					{
						return;
					}
					if (Reply.Type != TEXT("world.plot"))
					{
						InnerPC->ToastError(Reply);
						return;
					}
					const FString Named = FWUi::Str(Reply.Payload, TEXT("name"));
					InnerPC->Toast(Named.IsEmpty() ? LOCTEXT("NoName", "Działka nie ma już nazwy.") : FText::Format(LOCTEXT("Named", "Działka nazywa się teraz „{0}”."), FText::FromString(Named)));
					Inner->LoadFreight();
				});
			})
		], 4.f);
	if (Here.IsValid())
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("Chests", "Skrzynie ładujące: {0}  ·  odbierające: {1}"), int32(FWUi::Num(Here, TEXT("loading"))), int32(FWUi::Num(Here, TEXT("receiving")))), FWStyle::Muted));
	}
	// A new freight route
	Add(FWUi::Heading(LOCTEXT("NewFreight", "Nowa trasa")), 16.f);
	const TArray<TSharedPtr<FJsonObject>> Plots = FWUi::Objects(Freight, TEXT("plots"));
	if (Routes.Num() >= int32(FWUi::Num(Rates, TEXT("maxRoutes"))))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("MaxFreight", "Możesz mieć najwyżej {0} tras."), int32(FWUi::Num(Rates, TEXT("maxRoutes")))), FWStyle::Muted));
		return Page;
	}
	if (Plots.Num() < 2)
	{
		Add(FWUi::Note(LOCTEXT("TwoPlots", "Trasa łączy dwie twoje działki: kup drugą na mapie świata."), FWStyle::Muted));
		return Page;
	}
	TArray<TPair<FString, FText>> FromChoices, ToChoices;
	for (const TSharedPtr<FJsonObject>& P : Plots)
	{
		const FIntPoint At(int32(FWUi::Num(P, TEXT("x"))), int32(FWUi::Num(P, TEXT("y"))));
		const FString Key = FString::Printf(TEXT("%d,%d"), At.X, At.Y);
		FromChoices.Add({ Key, FText::FromString(FString::Printf(TEXT("%s · skrzyń ładujących: %d"), *PlotLabel(At), int32(FWUi::Num(P, TEXT("loading"))))) });
		ToChoices.Add({ Key, FText::FromString(FString::Printf(TEXT("%s · skrzyń odbierających: %d"), *PlotLabel(At), int32(FWUi::Num(P, TEXT("receiving"))))) });
	}
	if (!Form->Has(TEXT("freightFrom")))
	{
		Form->Set(TEXT("freightFrom"), Here.IsValid() ? FString::Printf(TEXT("%d,%d"), Plot.X, Plot.Y) : FromChoices[0].Key);
	}
	if (!Form->Has(TEXT("freightTo")) || Form->Get(TEXT("freightTo")) == Form->Get(TEXT("freightFrom")))
	{
		for (const TPair<FString, FText>& Each : ToChoices)
		{
			if (Each.Key != Form->Get(TEXT("freightFrom")))
			{
				Form->Set(TEXT("freightTo"), Each.Key);
				break;
			}
		}
	}
	auto Label = [](const FText& Text) { return SNew(SBox).WidthOverride(70.f)[SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)]; };
	auto Redraw = [WeakThis](const FString&) { if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin()) { This->Rebuild(); } };
	Add(SNew(SHorizontalBox) + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Label(LOCTEXT("From", "Skąd"))] + SHorizontalBox::Slot().AutoWidth()[Form->Pick(TEXT("freightFrom"), FromChoices, 460.f, Redraw)], 4.f);
	Add(SNew(SHorizontalBox) + SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Label(LOCTEXT("To", "Dokąd"))] + SHorizontalBox::Slot().AutoWidth()[Form->Pick(TEXT("freightTo"), ToChoices, 460.f, Redraw)], 4.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("CargoNote", "Co wozić (ilość na jeden kurs, najwyżej {0} rodzaje)"), int32(FWUi::Num(Rates, TEXT("maxItems")))), FWStyle::Muted), 8.f);
	for (int32 i = 0; i < Cargo.Num(); ++i)
	{
		const FString Item = Cargo[i].Key;
		const FString Key = TEXT("cargo:") + Item;
		if (!Form->Has(Key))
		{
			Form->Set(Key, FString::FromInt(Cargo[i].Value));
		}
		Add(FWRailText::Line(Controller.Get(), Item, FText::FromString(ItemNames.Contains(Item) ? ItemNames[Item] : Item), FWStyle::Text,
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(Key, FText::GetEmpty(), 90.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(FText::FromString(TEXT("×")), [WeakThis, Item]()
				{
					if (TSharedPtr<SFWRailWindow> This = WeakThis.Pin())
					{
						This->Cargo.RemoveAll([&Item](const TPair<FString, int32>& Each) { return Each.Key == Item; });
						This->Rebuild();
					}
				})
			]), 3.f);
	}
	if (Cargo.Num() < int32(FWUi::Num(Rates, TEXT("maxItems"), 3.0)))
	{
		// Goods to add: what the player has, then any by name
		TArray<TPair<FString, FText>> Goods;
		TSet<FString> Seen;
		for (const FFWStack& Stack : Controller.IsValid() ? Controller->GetInventory() : TArray<FFWStack>())
		{
			if (Stack.Quantity > 0 && !Seen.Contains(Stack.Id))
			{
				Seen.Add(Stack.Id);
				Goods.Add({ Stack.Id, FText::FromString(Stack.Name) });
			}
		}
		TArray<TPair<FString, FString>> Rest;
		for (const TPair<FString, FString>& Each : ItemNames)
		{
			if (!Seen.Contains(Each.Key))
			{
				Rest.Add(Each);
			}
		}
		Rest.Sort([](const TPair<FString, FString>& A, const TPair<FString, FString>& B) { return A.Value < B.Value; });
		for (const TPair<FString, FString>& Each : Rest)
		{
			Goods.Add({ Each.Key, FText::FromString(Each.Value) });
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Pick(TEXT("cargoPick"), Goods, 300.f, nullptr, Controller.Get())]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("AddCargo", "Dodaj"), [WeakThis]()
				{
					TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
					if (!This)
					{
						return;
					}
					const FString Item = This->Form->Get(TEXT("cargoPick"));
					if (!Item.IsEmpty() && !This->Cargo.ContainsByPredicate([&Item](const TPair<FString, int32>& Each) { return Each.Key == Item; }))
					{
						This->Cargo.Add({ Item, 100 });
						This->Rebuild();
					}
				})
			], 4.f);
	}
	Add(Form->Check(TEXT("freightSmuggle"), LOCTEXT("Smuggle3", "Przemyt (bez cła)"), false), 6.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
		.Text_Lambda([WeakThis]()
		{
			TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
			if (!This)
			{
				return FText::GetEmpty();
			}
			const TSharedPtr<FJsonObject> FreightRates = FWUi::Obj(This->Freight, TEXT("rates"));
			FString A, B, C, D;
			This->Form->Get(TEXT("freightFrom")).Split(TEXT(","), &A, &B);
			This->Form->Get(TEXT("freightTo")).Split(TEXT(","), &C, &D);
			const FIntPoint From(FCString::Atoi(*A), FCString::Atoi(*B)), To(FCString::Atoi(*C), FCString::Atoi(*D));
			int32 Quantity = 0;
			for (const TPair<FString, int32>& Each : This->Cargo)
			{
				Quantity += int32(This->Form->Number(TEXT("cargo:") + Each.Key));
			}
			const double KmTo = This->Km(From, To);
			const double Seconds = FMath::Max(FWUi::Num(FreightRates, TEXT("minTripSeconds")), KmTo / 100.0 * FWUi::Num(FreightRates, TEXT("secondsPer100Km")));
			const double Fee = FMath::CeilToDouble((FWUi::Num(FreightRates, TEXT("baseFeeCents")) + FMath::CeilToDouble(KmTo / 100.0 * Quantity * FWUi::Num(FreightRates, TEXT("centsPerItemPer100Km"))))
				* FWUi::Num(This->FreightPlot(From), TEXT("feeFactor"), 1.0));
			FString Text = FString::Printf(TEXT("%s → %s\n%s km  ·  w jedną stronę %s  ·  każdy kurs z %d szt. kosztuje %s"), *This->PlotLabel(From), *This->PlotLabel(To),
				*FWRailText::Grouped(FMath::RoundToInt64(KmTo)).ToString(), *FWRailText::Clock(Seconds), Quantity, *FWUi::Money(Fee).ToString());
			if (This->Cargo.Num() == 0)
			{
				Text += TEXT("\nDodaj co najmniej jeden towar.");
			}
			else if (Quantity > int32(FWUi::Num(FreightRates, TEXT("maxQuantity"))))
			{
				Text += FString::Printf(TEXT("\nPociąg wiezie najwyżej %d sztuk."), int32(FWUi::Num(FreightRates, TEXT("maxQuantity"))));
			}
			else if (FWUi::Num(This->FreightPlot(From), TEXT("loading")) == 0.0)
			{
				Text += TEXT("\nPostaw na działce, z której jedzie pociąg, skrzynię ładującą.");
			}
			else if (FWUi::Num(This->FreightPlot(To), TEXT("receiving")) == 0.0)
			{
				Text += TEXT("\nPostaw na działce docelowej skrzynię odbierającą.");
			}
			return FText::FromString(Text);
		}), 8.f);
	Add(SNew(SBox).HAlign(HAlign_Left)
		[
			FWUi::SmallButton(LOCTEXT("CreateRoute", "Utwórz trasę"), [WeakThis]()
			{
				TSharedPtr<SFWRailWindow> This = WeakThis.Pin();
				if (!This || This->Cargo.Num() == 0)
				{
					return;
				}
				FString A, B, C, D;
				This->Form->Get(TEXT("freightFrom")).Split(TEXT(","), &A, &B);
				This->Form->Get(TEXT("freightTo")).Split(TEXT(","), &C, &D);
				TArray<TSharedPtr<FJsonValue>> Load;
				for (const TPair<FString, int32>& Each : This->Cargo)
				{
					TSharedPtr<FJsonObject> Item = MakeShared<FJsonObject>();
					Item->SetStringField(TEXT("item"), Each.Key);
					Item->SetNumberField(TEXT("quantity"), This->Form->Number(TEXT("cargo:") + Each.Key));
					Load.Add(MakeShared<FJsonValueObject>(Item));
				}
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				P->SetNumberField(TEXT("fromX"), FCString::Atoi(*A));
				P->SetNumberField(TEXT("fromY"), FCString::Atoi(*B));
				P->SetNumberField(TEXT("toX"), FCString::Atoi(*C));
				P->SetNumberField(TEXT("toY"), FCString::Atoi(*D));
				P->SetArrayField(TEXT("cargo"), Load);
				P->SetBoolField(TEXT("smuggle"), This->Form->Ticked(TEXT("freightSmuggle")));
				This->FreightAct(TEXT("freight.add"), P, LOCTEXT("FreightMade", "Trasa utworzona. Pociąg czeka na towar w skrzyniach ładujących."));
			}, true)
		], 8.f);
	return Page;
}

#undef LOCTEXT_NAMESPACE
