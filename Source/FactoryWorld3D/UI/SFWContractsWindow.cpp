#include "UI/SFWContractsWindow.h"

#include "FWPlayerController.h"
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

#define LOCTEXT_NAMESPACE "FWContracts"

void SFWContractsWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });
	auto TabButton = [this](int32 Index, const FText& Text)
	{
		return FWUi::TabButton(Text, [this, Index]() { return Tab == Index; }, [this, Index]() { ShowTab(Index); });
	};
	TSharedRef<SWidget> TabRow = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[TabButton(0, LOCTEXT("Delivering", "Do dostarczenia"))]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[TabButton(1, LOCTEXT("Board", "Tablica zleceń"))]
		+ SHorizontalBox::Slot().AutoWidth()[TabButton(2, LOCTEXT("Issued", "Moje zlecenia"))];
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Zlecenia"), Right, TabRow, SAssignNew(Content, SBox), 900.f, 720.f)
	];
	// Every item, for the new order: the trader's list names them all
	if (AFWPlayerController* PC = Controller.Get())
	{
		TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
		PC->Send(TEXT("market.prices"), nullptr, [WeakThis](const FFWNetReply& Reply)
		{
			TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
			if (!This || Reply.Type != TEXT("market.prices"))
			{
				return;
			}
			for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Reply.Payload, TEXT("items")))
			{
				This->ItemIds.Add(MakeShared<FString>(FWUi::Str(Item, TEXT("id"))));
				This->ItemNames.Add(FWUi::Str(Item, TEXT("id")), FWUi::Str(Item, TEXT("name")));
			}
			This->ItemIds.Sort([&This](const TSharedPtr<FString>& A, const TSharedPtr<FString>& B) { return This->ItemNames[*A] < This->ItemNames[*B]; });
			if (This->Tab == 2)
			{
				This->Rebuild();
			}
		});
		PC->RefreshInventory();
	}
	Load();
}

void SFWContractsWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bLoading)
	{
		return;
	}
	bLoading = true;
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("contracts.get"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->bLoading = false;
		if (Reply.Type != TEXT("contracts"))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		This->View = Reply.Payload;
		This->ClockOffset = FWUi::Num(Reply.Payload, TEXT("serverTime")) - FWUi::LocalNowMs();
		This->Rebuild();
	});
}

double SFWContractsWindow::ServerNow() const
{
	return FWUi::LocalNowMs() + ClockOffset;
}

int32 SFWContractsWindow::Count(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

FText SFWContractsWindow::Issuer(const TSharedPtr<FJsonObject>& Contract) const
{
	const FString Who = FWUi::Str(Contract, TEXT("issuer"));
	return FWUi::Bool(Contract, TEXT("npc")) ? FText::FromString(Who) : FText::Format(LOCTEXT("Player", "Gracz {0}"), FText::FromString(Who));
}

int64 SFWContractsWindow::FirstId(const TCHAR* List) const
{
	const TArray<TSharedPtr<FJsonObject>> Contracts = FWUi::Objects(View, List);
	return Contracts.Num() > 0 ? int64(FWUi::Num(Contracts[0], TEXT("id"))) : 0;
}

void SFWContractsWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	Rebuild();
	Load();
}

void SFWContractsWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("contracts.done"))
		{
			Inner->ToastError(Reply);
		}
		else
		{
			// balanceCents, and when an order was finished: completed, rewardCents, xp
			double Balance = 0.0;
			if (Reply.Payload->TryGetNumberField(TEXT("balanceCents"), Balance))
			{
				Inner->SetBalanceCents(int64(Balance));
			}
			if (FWUi::Bool(Reply.Payload, TEXT("completed")))
			{
				Inner->Toast(FText::Format(LOCTEXT("Completed", "Zlecenie wykonane: +{0}, +{1} XP"), FWUi::Money(FWUi::Num(Reply.Payload, TEXT("rewardCents"))),
					int32(FWUi::Num(Reply.Payload, TEXT("xp")))));
			}
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
		}
		This->Load();
	});
}

void SFWContractsWindow::Create(const FString& Item, int32 Quantity, double RewardCents, int32 Hours)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), Quantity);
	P->SetNumberField(TEXT("rewardCents"), FMath::RoundToDouble(RewardCents));
	P->SetNumberField(TEXT("hours"), Hours);
	Act(TEXT("contracts.create"), P);
}

void SFWContractsWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	if (!View.IsValid())
	{
		Content->SetContent(FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted));
		return;
	}
	TSharedRef<SWidget> Page = Tab == 0 ? MakeDelivering() : Tab == 1 ? MakeBoard() : MakeIssued();
	Content->SetContent(SNew(SScrollBox) + SScrollBox::Slot().Padding(0, 0, 12, 0)[Page]);
}

TSharedRef<SWidget> SFWContractsWindow::Card(const TSharedPtr<FJsonObject>& Contract, const FText& Who, TSharedRef<SWidget> Buttons)
{
	const int32 Quantity = int32(FWUi::Num(Contract, TEXT("quantity")));
	const int32 Delivered = int32(FWUi::Num(Contract, TEXT("delivered")));
	const double Deadline = FWUi::Num(Contract, TEXT("deadline"));
	const int32 Xp = int32(FWUi::Num(Contract, TEXT("xp")));
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	return SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(10.f, 8.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(Controller.Get(), FWUi::Str(Contract, TEXT("item")), 42.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
					.Text(FText::Format(LOCTEXT("Amount", "{0}  ×{1}"), FText::FromString(FWUi::Str(Contract, TEXT("name"))), Quantity))
				]
				+ SVerticalBox::Slot().AutoHeight()[SNew(STextBlock).Text(Who).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)]
				+ SVerticalBox::Slot().AutoHeight().Padding(0, 4, 0, 0)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
					[
						FWUi::Bar(Quantity > 0 ? float(Delivered) / Quantity : 0.f, FWStyle::Good, 180.f)
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						SNew(STextBlock).Text(FText::Format(LOCTEXT("Progress", "{0} / {1}"), Delivered, Quantity)).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
					]
				]
				+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Left).Padding(0, 6, 0, 0)[Buttons]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(10, 0, 0, 0)
			[
				SNew(SBox).MinDesiredWidth(140.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)
					[
						SNew(STextBlock).Text(FWUi::Money(FWUi::Num(Contract, TEXT("rewardCents")))).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
					]
					+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)
					[
						SNew(STextBlock).Text(FText::Format(LOCTEXT("Xp", "+{0} XP"), Xp)).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Good)
						.Visibility(Xp > 0 ? EVisibility::Visible : EVisibility::Collapsed)
					]
					+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Right)
					[
						// Red when less than an hour is left
						SNew(STextBlock).Font(FWStyle::Font(10))
						.ColorAndOpacity_Lambda([WeakThis, Deadline]()
						{
							TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
							return FSlateColor(This && Deadline - This->ServerNow() < 3600000.0 ? FWStyle::Error : FWStyle::Text);
						})
						.Text_Lambda([WeakThis, Deadline]()
						{
							TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
							return This ? FText::Format(LOCTEXT("TimeLeft", "zostało {0}"), FWUi::Left(Deadline - This->ServerNow())) : FText::GetEmpty();
						})
					]
				]
			]
		];
}

TSharedRef<SWidget> SFWContractsWindow::Summary(const TSharedPtr<FJsonObject>& Contract, const FText& Detail, const FLinearColor& Color)
{
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(Controller.Get(), FWUi::Str(Contract, TEXT("item")), 26.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
			.Text(FText::Format(LOCTEXT("Amount", "{0}  ×{1}"), FText::FromString(FWUi::Str(Contract, TEXT("name"))), int32(FWUi::Num(Contract, TEXT("quantity")))))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[SNew(STextBlock).Text(Detail).Font(FWStyle::Font(11)).ColorAndOpacity(Color)];
}

TSharedRef<SWidget> SFWContractsWindow::MakeDelivering()
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	auto Deliverable = [this, WeakThis](const TSharedPtr<FJsonObject>& Contract)
	{
		const int64 Id = int64(FWUi::Num(Contract, TEXT("id")));
		const FString Item = FWUi::Str(Contract, TEXT("item"));
		const int32 Left = int32(FWUi::Num(Contract, TEXT("quantity")) - FWUi::Num(Contract, TEXT("delivered")));
		const int32 CanGive = FMath::Min(Count(Item), Left);
		if (!Amounts.Contains(Id) || FCString::Atoi(*Amounts[Id]) > CanGive || FCString::Atoi(*Amounts[Id]) < 1)
		{
			Amounts.Add(Id, FString::FromInt(FMath::Max(CanGive, 1)));
		}
		const bool bNpc = FWUi::Bool(Contract, TEXT("npc"));
		TSharedRef<SWidget> Buttons = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				SNew(SBox).WidthOverride(80.f)
				[
					SNew(SEditableTextBox).Style(&FWStyle::TextBox()).Text(FText::FromString(Amounts[Id])).IsEnabled(CanGive > 0)
					.OnTextChanged_Lambda([WeakThis, Id](const FText& Text) { if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin()) { This->Amounts.Add(Id, Text.ToString()); } })
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
			[
				FWUi::SmallButton(LOCTEXT("Deliver", "Dostarcz"), [WeakThis, Id, CanGive]()
				{
					if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("id"), double(Id));
						P->SetNumberField(TEXT("quantity"), FMath::Clamp(FCString::Atoi(*This->Amounts.FindRef(Id)), 1, CanGive));
						This->Act(TEXT("contracts.deliver"), P);
					}
				}, true, CanGive > 0)
			]
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)
			[
				FWUi::SmallButton(LOCTEXT("Abandon", "Zrezygnuj"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("id"), double(Id));
						This->Act(TEXT("contracts.abandon"), P);
					}
				})
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(CanGive > 0 ? FWStyle::Muted : FWStyle::Error)
				.Text(CanGive > 0 ? FText::Format(LOCTEXT("Have", "masz {0} szt."), Count(Item)) : LOCTEXT("None", "nie masz tego w ekwipunku"))
				.ToolTipText(bNpc ? LOCTEXT("AbandonNpc", "Dostarczone sztuki wrócą do ekwipunku. Nowe zlecenie firmy przyjdzie za 5 minut.")
					: LOCTEXT("AbandonPlayer", "Dostarczone sztuki wrócą do ekwipunku. Zlecenie wróci na tablicę."))
			];
		return Card(Contract, Issuer(Contract), Buttons);
	};
	Add(FWUi::Heading(LOCTEXT("Companies", "Zlecenia firm")), 0.f);
	Add(FWUi::Note(LOCTEXT("CompaniesNote", "Firmy zamawiają u ciebie towar. Dostarczaj z ekwipunku, także po trochu; nagroda i XP przychodzą, gdy oddasz ostatnią sztukę przed terminem."), FWStyle::Muted), 2.f);
	for (const TSharedPtr<FJsonObject>& Contract : FWUi::Objects(View, TEXT("npc")))
	{
		Add(Deliverable(Contract));
	}
	double NextNpc = 0.0;
	if (View->TryGetNumberField(TEXT("nextNpcAt"), NextNpc))
	{
		Add(SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
			.Text_Lambda([WeakThis, NextNpc]()
			{
				TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
				return This ? FText::Format(LOCTEXT("NextNpc", "Następne zlecenie firmy za {0}"), FWUi::Left(NextNpc - This->ServerNow())) : FText::GetEmpty();
			}));
	}
	Add(FWUi::Heading(LOCTEXT("Taken", "Zlecenia graczy, które realizujesz")), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Taken = FWUi::Objects(View, TEXT("taken"));
	if (Taken.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoneTaken", "Żadnych. Przyjmiesz zlecenie innego gracza w zakładce Tablica zleceń."), FWStyle::Muted), 2.f);
	}
	for (const TSharedPtr<FJsonObject>& Contract : Taken)
	{
		Add(Deliverable(Contract));
	}
	const TArray<TSharedPtr<FJsonObject>> Finished = FWUi::Objects(View, TEXT("finished"));
	if (Finished.Num() > 0)
	{
		Add(FWUi::Heading(LOCTEXT("Finished", "Wykonane w ostatniej dobie")), 18.f);
		for (const TSharedPtr<FJsonObject>& Contract : Finished)
		{
			Add(Summary(Contract, FText::Format(LOCTEXT("Paid", "{0}, +{1}"), Issuer(Contract), FWUi::Money(FWUi::Num(Contract, TEXT("rewardCents")))), FWStyle::Good), 3.f);
		}
	}
	return Page;
}

TSharedRef<SWidget> SFWContractsWindow::MakeBoard()
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	const int32 MaxTaken = int32(FWUi::Num(FWUi::Obj(View, TEXT("limits")), TEXT("maxTaken"), 5.0));
	Page->AddSlot().AutoHeight()
	[
		FWUi::Note(FText::Format(LOCTEXT("BoardNote", "Zlecenia innych graczy. Przyjęte zlecenie realizujesz tylko ty, aż do terminu; nagroda jest już odłożona i trafi do ciebie, gdy dostarczysz wszystko. Naraz możesz realizować {0}."), MaxTaken), FWStyle::Muted)
	];
	const TArray<TSharedPtr<FJsonObject>> Board = FWUi::Objects(View, TEXT("board"));
	if (Board.Num() == 0)
	{
		Page->AddSlot().AutoHeight().Padding(0, 10, 0, 0)[FWUi::Note(LOCTEXT("EmptyBoard", "Na tablicy nie ma teraz zleceń. Wystaw własne w zakładce Moje zlecenia."), FWStyle::Muted)];
	}
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	for (const TSharedPtr<FJsonObject>& Contract : Board)
	{
		const int64 Id = int64(FWUi::Num(Contract, TEXT("id")));
		TSharedRef<SWidget> Buttons = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)
			[
				FWUi::SmallButton(LOCTEXT("Accept", "Przyjmij"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("id"), double(Id));
						This->Act(TEXT("contracts.accept"), P);
					}
				}, true)
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(STextBlock).Text(FText::Format(LOCTEXT("HaveNow", "masz {0} szt."), Count(FWUi::Str(Contract, TEXT("item"))))).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
			];
		Page->AddSlot().AutoHeight().Padding(0, 6, 0, 0)[Card(Contract, Issuer(Contract), Buttons)];
	}
	return Page;
}

TSharedRef<SWidget> SFWContractsWindow::MakeForm()
{
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	auto Field = [WeakThis](FString SFWContractsWindow::* Value, float Width, const FText& Suffix)
	{
		TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
		return SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(SBox).WidthOverride(Width)
				[
					SNew(SEditableTextBox).Style(&FWStyle::TextBox()).Text(FText::FromString((*This).*Value))
					.OnTextChanged_Lambda([WeakThis, Value](const FText& Text)
					{
						if (TSharedPtr<SFWContractsWindow> Inner = WeakThis.Pin())
						{
							(*Inner).*Value = Text.ToString();
						}
					})
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(4, 0, 12, 0)[SNew(STextBlock).Text(Suffix).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
	};
	TSharedPtr<FString> Chosen;
	for (const TSharedPtr<FString>& Id : ItemIds)
	{
		if (*Id == NewItem)
		{
			Chosen = Id;
		}
	}
	TSharedRef<SWidget> Picker = SNew(SComboBox<TSharedPtr<FString>>)
		.OptionsSource(&ItemIds)
		.InitiallySelectedItem(Chosen)
		.MaxListHeight(420.f)
		.OnGenerateWidget_Lambda([WeakThis](TSharedPtr<FString> Id)
		{
			TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
			return SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Icon(This ? This->Controller.Get() : nullptr, *Id, 22.f)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Text(FText::FromString(This ? This->ItemNames.FindRef(*Id) : *Id)).Font(FWStyle::Font(11))
				];
		})
		.OnSelectionChanged_Lambda([WeakThis](TSharedPtr<FString> Id, ESelectInfo::Type)
		{
			if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin(); This && Id.IsValid())
			{
				This->NewItem = *Id;
				This->Rebuild();
			}
		})
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[FWUi::Icon(Controller.Get(), NewItem, 22.f)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(STextBlock).Text(FText::FromString(ItemNames.Contains(NewItem) ? ItemNames[NewItem] : NewItem)).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)
			]
		];
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 12, 0)[SNew(SBox).WidthOverride(260.f)[Picker]]
			+ SHorizontalBox::Slot().AutoWidth()[Field(&SFWContractsWindow::NewQuantity, 70.f, LOCTEXT("Pieces", "szt."))]
			+ SHorizontalBox::Slot().AutoWidth()[Field(&SFWContractsWindow::NewReward, 100.f, LOCTEXT("Dollars", "$ nagrody"))]
			+ SHorizontalBox::Slot().AutoWidth()[Field(&SFWContractsWindow::NewHours, 50.f, LOCTEXT("Hours", "h"))]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Create", "Wystaw"), [WeakThis]()
				{
					if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin())
					{
						This->Create(This->NewItem, FCString::Atoi(*This->NewQuantity), FCString::Atod(*This->NewReward.Replace(TEXT(","), TEXT("."))) * 100.0,
							FCString::Atoi(*This->NewHours));
					}
				}, true)
			]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 0)
		[
			SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted)
			.Text_Lambda([WeakThis]()
			{
				TSharedPtr<SFWContractsWindow> This = WeakThis.Pin();
				if (!This)
				{
					return FText::GetEmpty();
				}
				const double Cents = FMath::RoundToDouble(FCString::Atod(*This->NewReward.Replace(TEXT(","), TEXT("."))) * 100.0);
				const int32 Quantity = FMath::Max(FCString::Atoi(*This->NewQuantity), 1);
				FText Text = FText::Format(LOCTEXT("PerPiece", "{0} za sztukę, termin za {1}"), FWUi::Money(FMath::FloorToDouble(Cents / Quantity)),
					FWUi::Left(FCString::Atoi(*This->NewHours) * 3600000.0));
				if (This->Controller.IsValid() && Cents > double(This->Controller->GetBalanceCents()))
				{
					Text = FText::Format(LOCTEXT("TooDear", "{0} (nie masz tyle pieniędzy)"), Text);
				}
				return Text;
			})
		];
}

TSharedRef<SWidget> SFWContractsWindow::MakeIssued()
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Heading(LOCTEXT("New", "Nowe zlecenie")), 0.f);
	Add(FWUi::Note(LOCTEXT("NewNote", "Nagroda schodzi z portfela od razu i czeka w zleceniu. Wykonawca dostaje ją, gdy dostarczy wszystko przed terminem, a towar trafia do twojego ekwipunku. Gdy termin minie, nagroda wraca do ciebie."), FWStyle::Muted), 2.f);
	Add(MakeForm(), 8.f);
	Add(FWUi::Heading(LOCTEXT("Given", "Wystawione")), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Issued = FWUi::Objects(View, TEXT("issued"));
	if (Issued.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoneGiven", "Nie wystawiłeś jeszcze zleceń."), FWStyle::Muted), 2.f);
	}
	TWeakPtr<SFWContractsWindow> WeakThis = SharedThis(this);
	for (const TSharedPtr<FJsonObject>& Contract : Issued)
	{
		const FString State = FWUi::Str(Contract, TEXT("state"));
		const FString Contractor = FWUi::Str(Contract, TEXT("contractor"));
		if (State == TEXT("open") || State == TEXT("taken"))
		{
			const int64 Id = int64(FWUi::Num(Contract, TEXT("id")));
			const FText Who = State == TEXT("open") ? LOCTEXT("Waiting", "czeka na wykonawcę") : FText::Format(LOCTEXT("Doing", "realizuje: {0}"), FText::FromString(Contractor));
			TSharedRef<SWidget> Buttons = State == TEXT("open")
				? FWUi::SmallButton(LOCTEXT("Cancel", "Odwołaj"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWContractsWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("id"), double(Id));
						This->Act(TEXT("contracts.cancel"), P);
					}
				})
				: SNullWidget::NullWidget;
			Add(SNew(SHorizontalBox) + SHorizontalBox::Slot().FillWidth(1.f)[Card(Contract, Who, Buttons)]);
			continue;
		}
		static const TMap<FString, FText> States = {
			{ TEXT("done"), LOCTEXT("StateDone", "wykonane") }, { TEXT("expired"), LOCTEXT("StateExpired", "minął termin") },
			{ TEXT("cancelled"), LOCTEXT("StateCancelled", "odwołane") }, { TEXT("abandoned"), LOCTEXT("StateAbandoned", "porzucone") } };
		FText Detail = States.Contains(State) ? States[State] : FText::FromString(State);
		if (State == TEXT("done") && !Contractor.IsEmpty())
		{
			Detail = FText::Format(LOCTEXT("DoneBy", "{0}, wykonał(a) {1}"), Detail, FText::FromString(Contractor));
		}
		Add(Summary(Contract, Detail, State == TEXT("done") ? FWStyle::Good : FWStyle::Muted), 3.f);
	}
	return Page;
}

void SFWContractsWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	// A deadline passed or the next company order is here: the server settles it on the next look
	if (!View.IsValid() || bLoading || CurrentTime < NextCheck)
	{
		return;
	}
	NextCheck = CurrentTime + 1.0;
	const double Now = ServerNow();
	bool bDue = FWUi::Num(View, TEXT("nextNpcAt"), DBL_MAX) <= Now;
	for (const TCHAR* List : { TEXT("npc"), TEXT("taken"), TEXT("board") })
	{
		for (const TSharedPtr<FJsonObject>& Contract : FWUi::Objects(View, List))
		{
			bDue |= FWUi::Num(Contract, TEXT("deadline"), DBL_MAX) <= Now;
		}
	}
	if (bDue)
	{
		NextCheck = CurrentTime + 5.0;
		Load();
	}
}

FReply SFWContractsWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
