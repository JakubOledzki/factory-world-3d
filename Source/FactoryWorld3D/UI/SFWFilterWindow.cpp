#include "UI/SFWFilterWindow.h"

#include "FWPlayerController.h"
#include "Game/FWCatalog.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWFilter"

namespace FWFilterText
{
	// How many items a splitter sorts out at most, and how many choices show under the search
	constexpr int32 MaxSplit = 8;
	constexpr int32 MaxChoices = 10;

	FText About(SFWFilterWindow::ERole Role)
	{
		switch (Role)
		{
		case SFWFilterWindow::ERole::Inserter: return LOCTEXT("AboutInserter", "Podajnik z filtrem podnosi tylko wybrany przedmiot, a resztę zostawia.");
		case SFWFilterWindow::ERole::Storage: return LOCTEXT("AboutStorage", "Magazyn z filtrem przyjmuje tylko wybrany przedmiot, z podajników i z ekwipunku.");
		case SFWFilterWindow::ERole::Splitter: return LOCTEXT("AboutSplitter", "Bierze przedmioty z taśmy za sobą i oddaje je na taśmy po swojej lewej i prawej stronie.");
		default: return LOCTEXT("AboutMachine", "Maszyna z filtrem przyjmuje z podajników tylko wybrany przedmiot.");
		}
	}
}

SFWFilterWindow::ERole SFWFilterWindow::RoleOf(const FString& Kind)
{
	if (Kind == TEXT("logistics_splitter"))
	{
		return ERole::Splitter;
	}
	static const TSet<FString> Inserters = { TEXT("logistics_inserter"), TEXT("logistics_fast_inserter"), TEXT("logistics_express_inserter"),
		TEXT("logistics_long_inserter"), TEXT("logistics_pump") };
	return Inserters.Contains(Kind) ? ERole::Inserter : ERole::None;
}

void SFWFilterWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Object = InArgs._Object;
	Role = InArgs._Role;
	Form = MakeShared<FFWForm>();
	AFWPlayerController* PC = Controller.Get();
	if (PC && PC->GetPlot())
	{
		Plot = FIntPoint(PC->GetPlot()->GetPlotX(), PC->GetPlot()->GetPlotY());
		if (const FFWPlotObject* Found = PC->GetPlot()->GetObjects().Find(Object))
		{
			Kind = Found->Kind;
			ObjectName = Found->Name;
			Rotation = Found->Rotation;
		}
	}
	const FText Title = Role == ERole::Splitter ? FText::FromString(ObjectName.IsEmpty() ? TEXT("Rozdzielacz") : ObjectName)
		: FText::Format(LOCTEXT("Title", "Filtr: {0}"), FText::FromString(ObjectName));
	ChildSlot
	[
		FWUi::Frame(Title, SNullWidget::NullWidget, nullptr, SAssignNew(Content, SBox), 760.f, 720.f)
	];
	if (PC)
	{
		PC->RefreshInventory();
	}
	Load();
}

FString SFWFilterWindow::NameOf(const FString& Item) const
{
	const FString* Name = Names.Find(Item);
	return Name ? *Name : Item;
}

FString SFWFilterWindow::Way(int32 Side) const
{
	static const FIntPoint Facing[] = { FIntPoint(0, 1), FIntPoint(1, 0), FIntPoint(0, -1), FIntPoint(-1, 0) };
	const FIntPoint Forward = Facing[Rotation & 3];
	const FIntPoint Left(Forward.Y, -Forward.X);
	const FIntPoint To = Side == 0 ? Left : FIntPoint(-Left.X, -Left.Y);
	return To == FIntPoint(0, 1) ? TEXT("na południe") : To == FIntPoint(1, 0) ? TEXT("na wschód") : To == FIntPoint(0, -1) ? TEXT("na północ") : TEXT("na zachód");
}

void SFWFilterWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("x"), Plot.X);
	P->SetNumberField(TEXT("y"), Plot.Y);
	P->SetBoolField(TEXT("names"), true);
	TWeakPtr<SFWFilterWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("logistics.get"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
		{
			This->Apply(Reply);
		}
	});
}

bool SFWFilterWindow::Apply(const FFWNetReply& Reply)
{
	AFWPlayerController* PC = Controller.Get();
	if (Reply.Type != TEXT("logistics") || !Reply.Payload.IsValid())
	{
		if (PC)
		{
			PC->ToastError(Reply);
		}
		if (!bLoaded && Content.IsValid())
		{
			Content->SetContent(FWUi::Note(FWCatalog::ErrorText(Reply.ErrorCode(), Reply.ErrorMessage()), FWStyle::Error));
		}
		return false;
	}
	const TSharedPtr<FJsonObject>& State = Reply.Payload;
	// The belts and inserters on the plot show the change at once
	if (PC && PC->GetPlot())
	{
		PC->GetPlot()->ApplyLogistics(State);
	}
	if (const TSharedPtr<FJsonObject> NameMap = FWUi::Obj(State, TEXT("names")); NameMap.IsValid())
	{
		Names.Reset();
		for (const TPair<FString, TSharedPtr<FJsonValue>>& Pair : NameMap->Values)
		{
			Names.Add(Pair.Key, Pair.Value->AsString());
		}
	}
	Filter.Reset();
	for (const TSharedPtr<FJsonObject>& Entry : FWUi::Objects(State, TEXT("filters")))
	{
		if (FWUi::Str(Entry, TEXT("id")) == Object)
		{
			Filter = FWUi::Str(Entry, TEXT("item"));
		}
	}
	Below = Keep = 0;
	for (const TSharedPtr<FJsonObject>& Entry : FWUi::Objects(State, TEXT("conditions")))
	{
		if (FWUi::Str(Entry, TEXT("id")) == Object)
		{
			Below = int32(FWUi::Num(Entry, TEXT("below")));
			Keep = int32(FWUi::Num(Entry, TEXT("keep")));
		}
	}
	for (const TSharedPtr<FJsonObject>& Belt : FWUi::Objects(State, TEXT("belts")))
	{
		if (FWUi::Str(Belt, TEXT("id")) == Object)
		{
			SplitItems = FWUi::Strings(Belt, TEXT("filter"));
			// Without a filter there is no side yet; the right one is offered first
			SplitSide = SplitItems.Num() > 0 ? int32(FWUi::Num(Belt, TEXT("filterSide"))) : 1;
		}
	}
	Allowed.Reset();
	for (const TSharedPtr<FJsonObject>& Storage : FWUi::Objects(State, TEXT("storages")))
	{
		if (FWUi::Str(Storage, TEXT("id")) == Object)
		{
			for (const TSharedPtr<FJsonObject>& Only : FWUi::Objects(Storage, TEXT("only")))
			{
				Allowed.Add(FWUi::Str(Only, TEXT("id")));
			}
		}
	}
	Form->Set(TEXT("below"), FString::FromInt(Below));
	Form->Set(TEXT("keep"), FString::FromInt(Keep));
	bLoaded = true;
	Rebuild();
	return true;
}

void SFWFilterWindow::Send(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetNumberField(TEXT("x"), Plot.X);
	Payload->SetNumberField(TEXT("y"), Plot.Y);
	Payload->SetStringField(TEXT("id"), Object);
	TWeakPtr<SFWFilterWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWFilterWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (Inner && This->Apply(Reply))
		{
			Inner->PlayUiSound(TEXT("build_place"));
		}
	});
}

void SFWFilterWindow::Choose(const FString& Item, int32 Side)
{
	if (Role == ERole::Splitter)
	{
		TArray<FString> Items = SplitItems;
		if (!Item.IsEmpty())
		{
			Items.AddUnique(Item);
		}
		SetSplitter(Items, Side < 0 ? SplitSide : Side);
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	Send(TEXT("filter.set"), P);
}

void SFWFilterWindow::SetSplitter(const TArray<FString>& Items, int32 Side)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("side"), Side);
	TArray<TSharedPtr<FJsonValue>> Values;
	for (const FString& Item : Items)
	{
		Values.Add(MakeShared<FJsonValueString>(Item));
	}
	P->SetArrayField(TEXT("items"), Values);
	Send(TEXT("splitter.set"), P);
}

void SFWFilterWindow::Rebuild()
{
	if (!Content.IsValid())
	{
		return;
	}
	if (!bLoaded)
	{
		Content->SetContent(FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted));
		return;
	}
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWFilterWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Add(FWUi::Note(FWFilterText::About(Role), FWStyle::Muted), 0.f);
	const FString What = Role == ERole::Inserter ? TEXT("podnosi") : TEXT("przyjmuje");

	if (Role == ERole::Splitter)
	{
		// Which side the chosen items go to
		Add(FWUi::Heading(LOCTEXT("Goes", "Wybrane przedmioty idą")), 14.f);
		TSharedRef<SHorizontalBox> Sides = SNew(SHorizontalBox);
		for (int32 Side = 0; Side < 2; ++Side)
		{
			Sides->AddSlot().AutoWidth().Padding(0, 0, 10, 0)
			[
				FWUi::SmallButton(FText::FromString(FString::Printf(TEXT("%s%s (%s)"), Side == SplitSide ? TEXT("✓ ") : TEXT(""), Side == 0 ? TEXT("w lewo") : TEXT("w prawo"), *Way(Side))),
					[WeakThis, Side]()
					{
						if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
						{
							This->SetSplitter(This->SplitItems, Side);
						}
					}, Side == SplitSide)
			];
		}
		Add(Sides, 4.f);
		if (SplitItems.Num() == 0)
		{
			Add(FWUi::Note(LOCTEXT("NoSplit", "Bez filtra: przedmioty idą na zmianę w lewo i w prawo."), FWStyle::Muted), 8.f);
		}
		for (const FString& Item : SplitItems)
		{
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Item, 28.f)]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(NameOf(Item)))
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					FWUi::SmallButton(LOCTEXT("Remove", "Usuń"), [WeakThis, Item]()
					{
						if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
						{
							TArray<FString> Items = This->SplitItems;
							Items.Remove(Item);
							This->SetSplitter(Items, This->SplitSide);
						}
					})
				], 6.f);
		}
		if (SplitItems.Num() > 0)
		{
			Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Wszystko inne idzie %s."), SplitSide == 0 ? TEXT("w prawo") : TEXT("w lewo"))), FWStyle::Muted), 6.f);
		}
	}
	else
	{
		// The filter set now
		Add(FWUi::Heading(LOCTEXT("Set", "Ustawiony filtr")), 14.f);
		if (Filter.IsEmpty())
		{
			Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Bez filtra: %s wszystko."), *What)), FWStyle::Muted), 4.f);
		}
		else
		{
			Add(SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(10.f)
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(PC, Filter, 36.f)]
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
						.Text(FText::FromString(FString::Printf(TEXT("%s tylko: %s"), Role == ERole::Inserter ? TEXT("Podnosi") : TEXT("Przyjmuje"), *NameOf(Filter))))
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						FWUi::SmallButton(LOCTEXT("Clear", "Usuń"), [WeakThis]()
						{
							if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
							{
								This->Choose(FString());
							}
						})
					]
				], 4.f);
		}
	}

	// Choosing an item: what the player has first, then the rest by name, narrowed by the search
	Add(FWUi::Heading(Role == ERole::Splitter ? LOCTEXT("AddItem", "Dodaj przedmiot") : LOCTEXT("PickItem", "Wybierz przedmiot")), 18.f);
	Add(SNew(SBox).WidthOverride(320.f).HAlign(HAlign_Left)
		[
			SNew(SBox).WidthOverride(320.f)
			[
				SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(LOCTEXT("Search", "Szukaj…")).Text(FText::FromString(Search))
				.OnTextChanged_Lambda([WeakThis](const FText& Text)
				{
					if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
					{
						This->Search = Text.ToString();
						This->RebuildChoices();
					}
				})
			]
		], 4.f);
	Add(SAssignNew(Choices, SVerticalBox), 6.f);
	RebuildChoices();

	// An inserter's or a pump's condition
	if (Role == ERole::Inserter)
	{
		Add(FWUi::Heading(LOCTEXT("Condition", "Warunek pracy")), 18.f);
		Add(FWUi::Note(LOCTEXT("ConditionNote", "Działa tylko ze skrzynią albo zbiornikiem. 0 to brak warunku."), FWStyle::Muted), 2.f);
		for (const TPair<FString, FText>& Row : { TPair<FString, FText>(TEXT("below"), LOCTEXT("Below", "Napełniaj skrzynię przed sobą do")),
			TPair<FString, FText>(TEXT("keep"), LOCTEXT("Keep", "Zostawiaj w skrzyni za sobą")) })
		{
			Add(SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text).Text(Row.Value)
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)[Form->Field(Row.Key, LOCTEXT("Pieces", "szt."), 120.f, TEXT("0"))], 6.f);
		}
		Add(SNew(SBox).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("SaveCondition", "Zapisz warunek"), [WeakThis]()
				{
					if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetNumberField(TEXT("below"), FMath::Max(0.0, FMath::RoundToDouble(This->Form->Number(TEXT("below")))));
						P->SetNumberField(TEXT("keep"), FMath::Max(0.0, FMath::RoundToDouble(This->Form->Number(TEXT("keep")))));
						This->Send(TEXT("inserter.condition"), P);
					}
				}, true)
			], 8.f);
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page]);
	Scroll->SetScrollOffset(Offset);
}

void SFWFilterWindow::RebuildChoices()
{
	if (!Choices.IsValid())
	{
		return;
	}
	Choices->ClearChildren();
	AFWPlayerController* PC = Controller.Get();
	if (Role == ERole::Splitter && SplitItems.Num() >= FWFilterText::MaxSplit)
	{
		Choices->AddSlot().AutoHeight()[FWUi::Note(FText::FromString(FString::Printf(TEXT("Można wybrać najwyżej %d przedmiotów."), FWFilterText::MaxSplit)), FWStyle::Muted)];
		return;
	}
	TArray<FString> All;
	if (Allowed.Num() > 0)
	{
		All = Allowed;
	}
	else
	{
		Names.GetKeys(All);
	}
	TArray<FString> Owned;
	for (const FFWStack& Stack : PC ? PC->GetInventory() : TArray<FFWStack>())
	{
		if (All.Contains(Stack.Id))
		{
			Owned.AddUnique(Stack.Id);
		}
	}
	TArray<FString> Rest = All.FilterByPredicate([&Owned](const FString& Item) { return !Owned.Contains(Item); });
	Rest.Sort([this](const FString& A, const FString& B) { return NameOf(A) < NameOf(B); });
	const FString Query = Search.TrimStartAndEnd().ToLower();
	TWeakPtr<SFWFilterWindow> WeakThis = SharedThis(this);
	int32 Shown = 0;
	Owned.Append(Rest);
	for (const FString& Item : Owned)
	{
		if (Item == Filter || SplitItems.Contains(Item) || (!Query.IsEmpty() && !NameOf(Item).ToLower().Contains(Query)))
		{
			continue;
		}
		const FFWStack* Have = PC ? PC->FindStack(Item) : nullptr;
		Choices->AddSlot().AutoHeight().Padding(0, 2)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Item, 26.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(Have ? FString::Printf(TEXT("%s  (masz %d)"), *NameOf(Item), Have->Quantity) : NameOf(Item)))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(Role == ERole::Splitter ? LOCTEXT("Add", "Dodaj") : LOCTEXT("Pick", "Wybierz"), [WeakThis, Item]()
				{
					if (TSharedPtr<SFWFilterWindow> This = WeakThis.Pin())
					{
						This->Choose(Item);
					}
				})
			]
		];
		if (++Shown >= FWFilterText::MaxChoices)
		{
			break;
		}
	}
	if (Shown == 0)
	{
		Choices->AddSlot().AutoHeight()[FWUi::Note(LOCTEXT("Nothing", "Nic takiego nie ma."), FWStyle::Muted)];
	}
}

void SFWFilterWindow::Close()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	// Back to the window it was opened from
	if (Role == ERole::Storage)
	{
		PC->OpenInventory(Object);
	}
	else if (Role == ERole::Machine && !Kind.IsEmpty())
	{
		PC->OpenStation(Object, Kind);
	}
	else
	{
		PC->CloseWindow();
	}
}

FReply SFWFilterWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape)
	{
		Close();
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

#undef LOCTEXT_NAMESPACE
