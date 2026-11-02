#include "UI/SFWModWindow.h"

#include "FWPlayerController.h"
#include "Game/FWWorldData.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWMod"

namespace
{
	// Rows of items shown at once; the search narrows the rest down
	constexpr int32 MaxItemRows = 80;

	TSharedRef<SWidget> ModSection(const FText& Title, const FText& About, TSharedRef<SWidget> Body)
	{
		return SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 4)[FWUi::Heading(Title)]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 8)[FWUi::Note(About, FWStyle::Muted)]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 22)[Body];
	}
}

void SFWModWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Access = InArgs._Access;
	Form = MakeShared<FFWForm>();

	TSharedRef<SWidget> Right = SNew(STextBlock).Font(FWStyle::Display(22)).ColorAndOpacity(FWStyle::Good)
		.Text_Lambda([this]() { return Controller.IsValid() ? FWUi::Money(double(Controller->GetBalanceCents())) : FText::GetEmpty(); });

	TSharedRef<SWidget> Content = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 24, 0)
		[
			SNew(SScrollBox).ScrollBarStyle(&FWStyle::ScrollBar())
			+ SScrollBox::Slot().Padding(0, 0, 14, 0)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()[MoneySection()]
				+ SVerticalBox::Slot().AutoHeight()[ToolSection()]
				+ SVerticalBox::Slot().AutoHeight()[PlotSection()]
				+ SVerticalBox::Slot().AutoHeight()[WorldSection()]
			]
		]
		+ SHorizontalBox::Slot().FillWidth(1.f)
		[
			ItemSection()
		];

	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Mod menu"), Right, nullptr, Content, 980.f, 720.f)
	];
	RebuildItems();
}

TSharedRef<SWidget> SFWModWindow::MoneySection()
{
	TSharedRef<SWrapBox> Quick = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
	for (const int64 Dollars : { 1000ll, 10000ll, 100000ll, 1000000ll })
	{
		Quick->AddSlot()
		[
			FWUi::SmallButton(FText::Format(LOCTEXT("Plus", "+{0}"), FWUi::Money(double(Dollars) * 100.0)), [this, Dollars]() { GiveMoney(double(Dollars) * 100.0); })
		];
	}
	TSharedRef<SWidget> Own = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
		[
			Form->Field(TEXT("money"), LOCTEXT("Amount", "Kwota w $"), 170.f, TEXT("5000"))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("Add", "Dodaj"), [this]() { GiveMoney(Form->Cents(TEXT("money"))); }, true)
		];
	return ModSection(LOCTEXT("Money", "Pieniądze"), LOCTEXT("MoneyAbout", "Prosto do portfela."),
		SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)[Quick]
		+ SVerticalBox::Slot().AutoHeight()[Own]);
}

TSharedRef<SWidget> SFWModWindow::ToolSection()
{
	TSharedRef<SWrapBox> Tools = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
	for (const TSharedPtr<FJsonObject>& Tool : FWUi::Objects(Access, TEXT("tools")))
	{
		const FString Id = FWUi::Str(Tool, TEXT("id"));
		Tools->AddSlot()
		[
			FWUi::SmallButton(FText::FromString(FWUi::Str(Tool, TEXT("name"))), [this, Id]() { GiveTool(Id); })
		];
	}
	return ModSection(LOCTEXT("Tools", "Narzędzia"), LOCTEXT("ToolsAbout", "Prosto do paska narzędzi (na pierwsze wolne miejsce)."), Tools);
}

TSharedRef<SWidget> SFWModWindow::PlotSection()
{
	TArray<TPair<FString, FText>> Biomes;
	for (const TSharedPtr<FJsonObject>& Biome : FWUi::Objects(Access, TEXT("biomes")))
	{
		Biomes.Emplace(FWUi::Str(Biome, TEXT("id")), FWWorldData::BiomeName(int32(FWUi::Num(Biome, TEXT("biome")))));
	}
	TSharedRef<SWidget> Row = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
		[
			Form->Pick(TEXT("biome"), Biomes, 200.f)
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			FWUi::SmallButton(LOCTEXT("BuyPlot", "Kup działkę"), [this]() { BuyPlot(Form->Get(TEXT("biome"))); }, true)
		];
	return ModSection(LOCTEXT("Plot", "Działka"), LOCTEXT("PlotAbout", "Najbliższa wolna działka tego terenu przy działce startowej, opłacona. Wejdziesz na nią z mapy (N)."), Row);
}

TSharedRef<SWidget> SFWModWindow::WorldSection()
{
	return ModSection(LOCTEXT("World", "Świat testowy"),
		LOCTEXT("WorldAbout", "1 000 000 $, największy plecak, kilof i siekiera w pasku, działki: łąka, las i góry, maszyny testowe na łące. Przenosi na łąkę."),
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("MakeWorld", "Utwórz świat testowy"), [this]()
			{
				if (AFWPlayerController* PC = Controller.Get())
				{
					PC->CloseWindow();
					PC->FWDevWorld();
				}
			}, true)
		]);
}

TSharedRef<SWidget> SFWModWindow::ItemSection()
{
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 4)[FWUi::Heading(LOCTEXT("Items", "Przedmioty"))]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 8)[FWUi::Note(LOCTEXT("ItemsAbout", "Do plecaka; co się nie zmieści, przepadnie."), FWStyle::Muted)]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(LOCTEXT("Search", "Szukaj przedmiotu..."))
			.OnTextChanged_Lambda([this](const FText& Text) { SetFilter(Text.ToString()); })
		]
		+ SVerticalBox::Slot().FillHeight(1.f)
		[
			SNew(SScrollBox).ScrollBarStyle(&FWStyle::ScrollBar())
			+ SScrollBox::Slot().Padding(0, 0, 14, 0)
			[
				SAssignNew(ItemList, SVerticalBox)
			]
		];
}

void SFWModWindow::SetFilter(const FString& Text)
{
	Filter = Text.TrimStartAndEnd();
	RebuildItems();
}

void SFWModWindow::RebuildItems()
{
	if (!ItemList.IsValid())
	{
		return;
	}
	ItemList->ClearChildren();
	int32 Shown = 0, Hidden = 0;
	for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Access, TEXT("items")))
	{
		const FString Id = FWUi::Str(Item, TEXT("id"));
		const FString Name = FWUi::Str(Item, TEXT("name"));
		if (!Filter.IsEmpty() && !Name.Contains(Filter) && !Id.Contains(Filter))
		{
			continue;
		}
		if (Shown >= MaxItemRows)
		{
			++Hidden;
			continue;
		}
		++Shown;
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
			[
				FWUi::Icon(Controller.Get(), Id, 28.f)
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Text(FText::FromString(Name)).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)
			];
		for (const int32 Quantity : { 1, 10, 100 })
		{
			Row->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(4, 0, 0, 0)
			[
				FWUi::SmallButton(FText::Format(LOCTEXT("Times", "+{0}"), Quantity), [this, Id, Quantity]() { GiveItem(Id, Quantity); })
			];
		}
		ItemList->AddSlot().AutoHeight().Padding(0, 0, 0, 4)
		[
			SNew(SFWCard)[Row]
		];
	}
	if (Shown == 0)
	{
		ItemList->AddSlot().AutoHeight()[FWUi::Note(LOCTEXT("NoItems", "Nic takiego nie ma."), FWStyle::Muted)];
	}
	else if (Hidden > 0)
	{
		ItemList->AddSlot().AutoHeight().Padding(0, 6, 0, 0)
		[
			FWUi::Note(FText::Format(LOCTEXT("More", "I jeszcze {0}. Wpisz więcej liter nazwy."), Hidden), FWStyle::Muted)
		];
	}
}

void SFWModWindow::GiveMoney(double Cents)
{
	if (Cents <= 0.0)
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->Toast(LOCTEXT("BadAmount", "Wpisz kwotę większą od zera."), true);
		}
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("cents"), FMath::RoundToDouble(Cents));
	Act(TEXT("dev.give"), P, [Cents](const FFWNetReply&) { return FText::Format(LOCTEXT("MoneyGiven", "Dodano {0}"), FWUi::Money(Cents)); });
}

void SFWModWindow::GiveItem(const FString& Item, int32 Quantity)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("item"), Item);
	P->SetNumberField(TEXT("quantity"), FMath::Max(1, Quantity));
	TWeakObjectPtr<AFWPlayerController> Weak = Controller;
	Act(TEXT("dev.give"), P, [Weak, Item, Quantity](const FFWNetReply&)
	{
		const FString Name = Weak.IsValid() ? Weak->ItemName(Item) : Item;
		return FText::Format(LOCTEXT("ItemGiven", "Dodano: {0} ×{1}"), FText::FromString(Name), Quantity);
	});
}

void SFWModWindow::GiveTool(const FString& Tool)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("tool"), Tool);
	TWeakObjectPtr<AFWPlayerController> Weak = Controller;
	Act(TEXT("dev.tool"), P, [Weak, Tool](const FFWNetReply&)
	{
		const FString Name = Weak.IsValid() ? Weak->ItemName(Tool) : Tool;
		return FText::Format(LOCTEXT("ToolGiven", "{0} w pasku narzędzi"), FText::FromString(Name));
	});
}

void SFWModWindow::BuyPlot(const FString& Biome)
{
	if (Biome.IsEmpty())
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("biome"), Biome);
	Act(TEXT("dev.plot"), P, [](const FFWNetReply& Reply)
	{
		return FText::Format(LOCTEXT("PlotBought", "Kupiono działkę {0}:{1}"),
			FText::AsNumber(int32(FWUi::Num(Reply.Payload, TEXT("x"))), &FNumberFormattingOptions::DefaultNoGrouping()),
			FText::AsNumber(int32(FWUi::Num(Reply.Payload, TEXT("y"))), &FNumberFormattingOptions::DefaultNoGrouping()));
	});
}

void SFWModWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<FText(const FFWNetReply&)> Done)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakObjectPtr<AFWPlayerController> Weak(PC);
	PC->Send(Type, Payload, [Weak, Done](const FFWNetReply& Reply)
	{
		AFWPlayerController* Inner = Weak.Get();
		if (!Inner)
		{
			return;
		}
		if (Reply.IsError())
		{
			Inner->ToastError(Reply);
			return;
		}
		Inner->PlayUiSound(TEXT("build_pickup"));
		Inner->Toast(Done(Reply));
		Inner->RefreshWallet();
		Inner->RefreshInventory();
	});
}

FReply SFWModWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::F8)
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
