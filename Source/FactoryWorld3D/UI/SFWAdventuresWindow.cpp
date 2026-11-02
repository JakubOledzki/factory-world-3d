#include "UI/SFWAdventuresWindow.h"

#include "Brushes/SlateRoundedBoxBrush.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SUniformGridPanel.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWAdventures"

#include "SFWAdventuresWindowHelpers.h"

void SFWAdventuresWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Form = MakeShared<FFWForm>();
	SAssignNew(Board, FWPuzzle::SBoard).Controller(Controller);
	Board->OnChanged = [this]() { Result = FText::GetEmpty(); };
	TSharedRef<SHorizontalBox> TabRow = SNew(SHorizontalBox);
	for (int32 i = 0; i < UE_ARRAY_COUNT(FWAdventureText::Tabs); ++i)
	{
		TabRow->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			FWUi::TabButton(FText::FromString(FWAdventureText::Tabs[i]), [this, i]() { return Tab == i; }, [this, i]() { ShowTab(i); })
		];
	}
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Przygody"), SAssignNew(Summary, STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Muted), TabRow, SAssignNew(Content, SBox), 1100.f, 800.f)
	];
	Load();
}

void SFWAdventuresWindow::ShowTab(int32 InTab, const FString& Player)
{
	Tab = InTab;
	if (Tab == FWAdventureText::Showcase)
	{
		ShowcaseOf = Player;
	}
	if (Tab != FWAdventureText::Puzzles)
	{
		Puzzle.Reset();
	}
	if (Scroll.IsValid())
	{
		Scroll->SetScrollOffset(0.f);
	}
	Load();
}

void SFWAdventuresWindow::Load()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	if (bLoading)
	{
		bAgain = true;
		return;
	}
	bLoading = true;
	const int32 ForTab = Tab;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	if (ForTab == FWAdventureText::Showcase && !ShowcaseOf.IsEmpty())
	{
		P->SetStringField(TEXT("name"), ShowcaseOf);
	}
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	PC->Send(FWAdventureText::Requests[ForTab], P, [WeakThis, ForTab](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		This->bLoading = false;
		if (This->bAgain || ForTab != This->Tab)
		{
			This->bAgain = false;
			This->Load();
			return;
		}
		if (Reply.IsError())
		{
			Inner->ToastError(Reply);
			if (ForTab == FWAdventureText::Showcase && !This->ShowcaseOf.IsEmpty())
			{
				This->ShowcaseOf.Reset();
			}
			return;
		}
		This->Show(ForTab, Reply.Payload);
	});
}

void SFWAdventuresWindow::Show(int32 ForTab, const TSharedPtr<FJsonObject>& InState)
{
	if (ForTab != Tab)
	{
		return;
	}
	State = InState;
	StateAt = FPlatformTime::Seconds();
	Rebuild();
}

void SFWAdventuresWindow::Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, TFunction<void(const TSharedPtr<FJsonObject>&)> Done)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload.IsValid() ? Payload : MakeShared<FJsonObject>(), [WeakThis, Done](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.IsError())
		{
			Inner->ToastError(Reply);
			return;
		}
		Inner->PlayUiSound(TEXT("build_place"));
		Inner->RefreshInventory();
		Done(Reply.Payload);
	});
}

void SFWAdventuresWindow::Rebuild()
{
	if (!Content.IsValid() || !State.IsValid())
	{
		return;
	}
	Summary->SetText(FText::GetEmpty());
	TSharedPtr<SWidget> Page;
	switch (Tab)
	{
	case FWAdventureText::Album: Page = MakeAlbum(); break;
	case FWAdventureText::Expeditions: Page = MakeExpeditions(); break;
	case FWAdventureText::Puzzles: Page = Puzzle.IsValid() ? MakeEditor() : MakePuzzles(); break;
	case FWAdventureText::Mentor: Page = MakeMentor(); break;
	case FWAdventureText::Showcase: Page = MakeShowcase(); break;
	default: Page = MakeEvent(); break;
	}
	const float Offset = Scroll.IsValid() ? Scroll->GetScrollOffset() : 0.f;
	Content->SetContent(SAssignNew(Scroll, SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
	Scroll->SetScrollOffset(Offset);
}

TSharedRef<SWidget> SFWAdventuresWindow::MakeEvent()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Season = FWUi::Obj(State, TEXT("active"));
	if (!Season.IsValid())
	{
		const TSharedPtr<FJsonObject> Next = FWUi::Obj(State, TEXT("next"));
		Add(FWUi::Heading(LOCTEXT("NoHoliday", "Teraz nie trwają żadne święta")), 0.f);
		Add(FWUi::Note(FText::Format(LOCTEXT("NextHoliday", "Najbliższe: {0}, od {1}. W czasie świąt zbiory, kamienie i ścięte zboże na działce dają co jakiś czas świąteczne przysmaki, które wymienisz tutaj na dekoracje."),
			FText::FromString(FWUi::Str(Next, TEXT("name"))), FWAdventureText::Date(FWUi::Num(Next, TEXT("starts")))), FWStyle::Text));
		return Page;
	}
	const TSharedPtr<FJsonObject> Token = FWUi::Obj(Season, TEXT("token"));
	const int32 Tokens = int32(FWUi::Num(Season, TEXT("tokens")));
	Summary->SetText(FText::Format(LOCTEXT("Tokens", "{0}: {1}"), FText::FromString(FWUi::Str(Token, TEXT("name"))), Tokens));
	Add(FWUi::Heading(FText::Format(LOCTEXT("Holiday", "{0}  (do {1})"), FText::FromString(FWUi::Str(Season, TEXT("name"))), FWAdventureText::Date(FWUi::Num(Season, TEXT("ends")) - 1000.0))), 0.f);
	Add(FWAdventureText::Line(FText::Format(LOCTEXT("TokenLine", "Masz {0}. Dziś znaleziono {1} z {2}. Mniej więcej co {3}. zbiór, kamień albo ścięte zboże na działce daje przysmak."), Tokens,
		int32(FWUi::Num(Season, TEXT("foundToday"))), int32(FWUi::Num(Season, TEXT("dailyLimit"))), int32(FWUi::Num(Season, TEXT("dropOneIn")))), FWStyle::Text, nullptr, Controller.Get(),
		FWUi::Str(Token, TEXT("id")), 40.f), 6.f);
	Add(FWUi::Heading(LOCTEXT("Decorations", "Dekoracje na działkę")), 16.f);
	for (const TSharedPtr<FJsonObject>& Offer : FWUi::Objects(Season, TEXT("shop")))
	{
		const FString Id = FWUi::Str(Offer, TEXT("id"));
		const FString Name = FWUi::Str(Offer, TEXT("name"));
		const int32 Price = int32(FWUi::Num(Offer, TEXT("price")));
		Add(FWAdventureText::Line(FText::FromString(Name), FWStyle::Text,
			FWUi::SmallButton(FText::Format(LOCTEXT("Exchange", "Wymień za {0} × {1}"), Price, FText::FromString(FWUi::Str(Token, TEXT("name")))), [WeakThis, Id, Name]()
			{
				if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("item"), Id);
					This->Act(TEXT("season.buy"), P, [WeakThis, Name](const TSharedPtr<FJsonObject>& Payload)
					{
						if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
						{
							Inner->Controller->Toast(FText::Format(LOCTEXT("DecorationBought", "{0} trafia do ekwipunku. Postaw ją na działce!"), FText::FromString(Name)));
							Inner->Show(FWAdventureText::Event, FWUi::Obj(Payload, TEXT("season")).IsValid() ? FWUi::Obj(Payload, TEXT("season")) : Payload);
						}
					});
				}
			}, true, Tokens >= Price), Controller.Get(), Id, 48.f), 4.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWAdventuresWindow::MakeAlbum()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 PageXp = int32(FWUi::Num(State, TEXT("pageXp")));
	Summary->SetText(FText::Format(LOCTEXT("Found", "Znaleziono {0} z {1}"), int32(FWUi::Num(State, TEXT("found"))), int32(FWUi::Num(State, TEXT("total")))));
	Add(FWUi::Note(FText::Format(LOCTEXT("AlbumNote", "Rzadkie rzeczy, które znajdziesz pierwszy raz, trafiają do albumu same. Pełna strona daje {0} XP."), PageXp), FWStyle::Muted), 0.f);
	for (const TSharedPtr<FJsonObject>& AlbumPage : FWUi::Objects(State, TEXT("pages")))
	{
		const TArray<TSharedPtr<FJsonObject>> Items = FWUi::Objects(AlbumPage, TEXT("items"));
		int32 FoundCount = 0;
		for (const TSharedPtr<FJsonObject>& Item : Items)
		{
			FoundCount += FWUi::Bool(Item, TEXT("found"));
		}
		const FString Id = FWUi::Str(AlbumPage, TEXT("id"));
		TSharedPtr<SWidget> Right;
		if (FWUi::Bool(AlbumPage, TEXT("claimed")))
		{
			Right = SNew(STextBlock).Text(LOCTEXT("Claimed", "Nagroda odebrana")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Good);
		}
		else if (FWUi::Bool(AlbumPage, TEXT("complete")))
		{
			Right = FWUi::SmallButton(FText::Format(LOCTEXT("ClaimXp", "Odbierz {0} XP"), PageXp), [WeakThis, Id]()
			{
				if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("page"), Id);
					This->Act(TEXT("album.claim"), P, [WeakThis](const TSharedPtr<FJsonObject>& Payload)
					{
						if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
						{
							const TSharedPtr<FJsonObject> Album = FWUi::Obj(Payload, TEXT("album"));
							Inner->Controller->Toast(FText::Format(LOCTEXT("PageFull", "Strona albumu pełna! +{0} XP"), int32(FWUi::Num(Album, TEXT("pageXp")))));
							Inner->Show(FWAdventureText::Album, Album);
						}
					});
				}
			}, true);
		}
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[FWUi::Heading(FText::Format(LOCTEXT("PageTitle", "{0}  ({1}/{2})"), FText::FromString(FWUi::Str(AlbumPage, TEXT("name"))), FoundCount, Items.Num()))]
			+ SHorizontalBox::Slot().AutoWidth()[Right.IsValid() ? Right.ToSharedRef() : SNullWidget::NullWidget], 16.f);
		Add(FWUi::Note(FText::FromString(FWUi::Str(AlbumPage, TEXT("hint"))), FWStyle::Muted), 2.f);
		TSharedRef<SWrapBox> Grid = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(8.f, 8.f));
		for (const TSharedPtr<FJsonObject>& Item : Items)
		{
			const bool bFound = FWUi::Bool(Item, TEXT("found"));
			Grid->AddSlot()
			[
				SNew(SBox).WidthOverride(96.f).ToolTipText(FText::FromString(FWUi::Str(Item, TEXT("name")) + (bFound ? FString::Printf(TEXT("\nZnalezione %s"), *FWAdventureText::Date(FWUi::Num(Item, TEXT("foundAt"))).ToString())
					: FString(TEXT("\nJeszcze nie znalezione")))))
				[
					SNew(SFWCard).Padding(6.f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
						[
							SNew(SBorder).BorderImage(FStyleDefaults::GetNoBrush()).Padding(0.f).ColorAndOpacity(bFound ? FLinearColor::White : FLinearColor(0.f, 0.f, 0.f, 0.6f))
							[
								FWUi::Icon(Controller.Get(), FWUi::Str(Item, TEXT("id")), 48.f)
							]
						]
						+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center).Padding(0, 4, 0, 0)
						[
							SNew(STextBlock).Text(FText::FromString(bFound ? FWUi::Str(Item, TEXT("name")) : TEXT("?"))).Font(FWStyle::Font(10)).ColorAndOpacity(bFound ? FWStyle::Text : FWStyle::Muted)
							.Justification(ETextJustify::Center).AutoWrapText(true)
						]
					]
				]
			];
		}
		Add(Grid, 6.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWAdventuresWindow::MakeExpeditions()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TArray<TSharedPtr<FJsonObject>> Trips = FWUi::Objects(State, TEXT("trips"));
	const int32 Parties = int32(FWUi::Num(State, TEXT("parties")));
	Summary->SetText(FText::Format(LOCTEXT("OnTheWay", "Wyprawy w drodze: {0} z {1}"), Trips.Num(), Parties));
	auto Now = [WeakThis]()
	{
		TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin();
		return This ? FWUi::Num(This->State, TEXT("now")) + (FPlatformTime::Seconds() - This->StateAt) * 1000.0 : 0.0;
	};
	auto Done = [WeakThis](const TSharedPtr<FJsonObject>& Payload)
	{
		if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
		{
			Inner->Show(FWAdventureText::Expeditions, Payload);
		}
	};
	if (Trips.Num() > 0)
	{
		Add(FWUi::Heading(LOCTEXT("YourTrips", "Twoje wyprawy")), 0.f);
		for (const TSharedPtr<FJsonObject>& Trip : Trips)
		{
			const double Id = FWUi::Num(Trip, TEXT("id"));
			const double Returns = FWUi::Num(Trip, TEXT("returnsAt"));
			const FString Name = FWUi::Str(Trip, TEXT("name"));
			Add(SNew(SFWCard).Padding(FMargin(10.f, 6.f))
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)
						.Text_Lambda([Now, Returns, Name]()
						{
							const int64 Left = int64((Returns - Now()) / 1000.0);
							return FText::FromString(FString::Printf(TEXT("%s: %s"), *Name, Left <= 0 ? TEXT("wróciła!") : *(TEXT("wraca za ") + FWAdventureText::Duration(Left))));
						})
					]
					+ SHorizontalBox::Slot().AutoWidth()
					[
						SNew(SBox).IsEnabled_Lambda([Now, Returns]() { return Now() >= Returns; })
						[
							FWUi::SmallButton(LOCTEXT("ClaimLoot", "Odbierz łup"), [WeakThis, Id]()
							{
								if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin())
								{
									TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
									P->SetNumberField(TEXT("id"), Id);
									This->Act(TEXT("expedition.claim"), P, [WeakThis](const TSharedPtr<FJsonObject>& Payload)
									{
										if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
										{
											TArray<FString> Loot;
											for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Payload, TEXT("items")))
											{
												Loot.Add(FString::Printf(TEXT("%d × %s"), int32(FWUi::Num(Item, TEXT("quantity"))), *FWUi::Str(Item, TEXT("name"))));
											}
											Inner->Controller->Toast(FText::Format(LOCTEXT("Loot", "Wyprawa ({0}) przywiozła: {1}"), FText::FromString(FWUi::Str(Payload, TEXT("region"))),
												FText::FromString(Loot.Num() > 0 ? FString::Join(Loot, TEXT(", ")) : TEXT("nic"))));
											Inner->Show(FWAdventureText::Expeditions, FWUi::Obj(Payload, TEXT("expeditions")));
										}
									});
								}
							}, true)
						]
					]
				], 3.f);
		}
	}
	Add(FWUi::Heading(LOCTEXT("WhereTo", "Dokąd wysłać wyprawę")), Trips.Num() > 0 ? 18.f : 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("ExpeditionNote", "Wyprawa zabiera zapasy i wraca po kilku godzinach z surowcami, a czasem z pamiątką do albumu ({0}% szans). Druga wyprawa naraz od {1} poziomu."),
		int32(FWUi::Num(State, TEXT("souvenirPercent"))), int32(FWUi::Num(State, TEXT("secondPartyLevel")))), FWStyle::Muted));
	for (const TSharedPtr<FJsonObject>& Region : FWUi::Objects(State, TEXT("regions")))
	{
		const FString Id = FWUi::Str(Region, TEXT("id"));
		const FString Name = FWUi::Str(Region, TEXT("name"));
		TArray<FString> Supplies, Finds;
		for (const TSharedPtr<FJsonObject>& S : FWUi::Objects(Region, TEXT("supplies")))
		{
			Supplies.Add(FString::Printf(TEXT("%d × %s"), int32(FWUi::Num(S, TEXT("quantity"))), *FWUi::Str(S, TEXT("name"))));
		}
		for (const TSharedPtr<FJsonObject>& F : FWUi::Objects(Region, TEXT("finds")))
		{
			const int32 Percent = int32(FWUi::Num(F, TEXT("percent")));
			Finds.Add(FString::Printf(TEXT("%s %d–%d%s"), *FWUi::Str(F, TEXT("name")), int32(FWUi::Num(F, TEXT("min"))), int32(FWUi::Num(F, TEXT("max"))),
				Percent >= 100 ? TEXT("") : *FString::Printf(TEXT(" (%d%%)"), Percent)));
		}
		const FString Text = FString::Printf(TEXT("%s\nZapasy: %s\nPrzywozi: %s. Pamiątka: %s"), *FWUi::Str(Region, TEXT("description")), *FString::Join(Supplies, TEXT(", ")),
			*FString::Join(Finds, TEXT(", ")), *FWUi::Str(FWUi::Obj(Region, TEXT("souvenir")), TEXT("name")));
		Add(SNew(SFWCard).Padding(FMargin(12.f, 8.f))
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
						.Text(FText::FromString(FString::Printf(TEXT("%s  ·  %s  ·  od %d poziomu  ·  %d XP"), *Name, *FWAdventureText::Duration(int64(FWUi::Num(Region, TEXT("minutes")) * 60.0)),
							int32(FWUi::Num(Region, TEXT("level"))), int32(FWUi::Num(Region, TEXT("xp"))))))
					]
					+ SHorizontalBox::Slot().AutoWidth()
					[
						FWUi::SmallButton(LOCTEXT("Send", "Wyślij"), [WeakThis, Id, Name, Done]()
						{
							if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin())
							{
								TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
								P->SetStringField(TEXT("region"), Id);
								This->Act(TEXT("expedition.send"), P, [WeakThis, Name, Done](const TSharedPtr<FJsonObject>& Payload)
								{
									if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
									{
										Inner->Controller->Toast(FText::Format(LOCTEXT("Sent", "Wyprawa wyrusza: {0}."), FText::FromString(Name)));
										Done(Payload);
									}
								});
							}
						}, true, Trips.Num() < Parties)
					]
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0, 4, 0, 0)[FWUi::Note(FText::FromString(Text), FWStyle::Muted)]
			], 6.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWAdventuresWindow::MakeMentor()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TSharedPtr<FJsonObject> Rules = FWUi::Obj(State, TEXT("rules"));
	auto Claim = [WeakThis](const FString& Mentee)
	{
		return [WeakThis, Mentee]()
		{
			if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin())
			{
				TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
				if (!Mentee.IsEmpty())
				{
					P->SetStringField(TEXT("mentee"), Mentee);
				}
				This->Act(TEXT("mentor.claim"), P, [WeakThis](const TSharedPtr<FJsonObject>& Payload)
				{
					if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
					{
						Inner->Controller->Toast(LOCTEXT("MentorClaimed", "Nagroda za opiekę odebrana!"));
						Inner->Show(FWAdventureText::Mentor, FWUi::Obj(Payload, TEXT("mentoring")));
					}
				});
			}
		};
	};
	Add(FWUi::Note(FText::Format(LOCTEXT("MentorRules", "Nowy gracz (poniżej {0} poziomu) wybiera mentora od {1} poziomu. Gdy podopieczny dojdzie do {2} poziomu, oboje dostają nagrodę: on {3} XP, mentor {4} XP. Mentor opiekuje się najwyżej {5} graczami."),
		int32(FWUi::Num(Rules, TEXT("chooseBelowLevel"))), int32(FWUi::Num(Rules, TEXT("mentorLevel"))), int32(FWUi::Num(Rules, TEXT("goalLevel"))), int32(FWUi::Num(Rules, TEXT("menteeXp"))),
		int32(FWUi::Num(Rules, TEXT("mentorXp"))), int32(FWUi::Num(Rules, TEXT("mentees")))), FWStyle::Muted), 0.f);
	const TSharedPtr<FJsonObject> Mentor = FWUi::Obj(State, TEXT("mentor"));
	if (Mentor.IsValid())
	{
		Add(FWUi::Heading(LOCTEXT("YourMentor", "Twój mentor")), 14.f);
		TSharedRef<SWidget> Right = FWUi::Bool(Mentor, TEXT("reward")) ? FWUi::SmallButton(FText::Format(LOCTEXT("ClaimMenteeXp", "Odbierz {0} XP"), int32(FWUi::Num(Rules, TEXT("menteeXp")))), Claim(FString()), true)
			: StaticCastSharedRef<SWidget>(SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWUi::Bool(Mentor, TEXT("rewarded")) ? FWStyle::Good : FWStyle::Muted)
				.Text(FWUi::Bool(Mentor, TEXT("rewarded")) ? LOCTEXT("RewardTaken", "Nagroda odebrana") : FText::Format(LOCTEXT("RewardFrom", "Nagroda od {0} poziomu"), int32(FWUi::Num(Rules, TEXT("goalLevel"))))));
		Add(FWAdventureText::Line(FText::FromString(FString::Printf(TEXT("%s, poziom %d%s"), *FWUi::Str(Mentor, TEXT("name")), int32(FWUi::Num(Mentor, TEXT("level"))),
			FWUi::Bool(Mentor, TEXT("online")) ? TEXT("  ·  w grze") : TEXT(""))), FWStyle::Text, Right), 4.f);
	}
	else if (FWUi::Bool(State, TEXT("canChoose")))
	{
		Add(FWUi::Heading(LOCTEXT("ChooseMentor", "Wybierz mentora")), 14.f);
		Add(FWUi::Note(LOCTEXT("ChooseNote", "Doświadczony gracz pomoże ci zacząć: podpowie, co budować, i może odwiedzać twoje działki. Wpisz jego nazwę."), FWStyle::Text));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("mentor"), LOCTEXT("PlayerName", "Nazwa gracza"), 280.f)]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				FWUi::SmallButton(LOCTEXT("Choose", "Wybierz"), [WeakThis]()
				{
					TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin();
					const FString Name = This ? This->Form->Get(TEXT("mentor")) : FString();
					if (Name.IsEmpty())
					{
						return;
					}
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("name"), Name);
					This->Act(TEXT("mentor.choose"), P, [WeakThis, Name](const TSharedPtr<FJsonObject>& Payload)
					{
						if (TSharedPtr<SFWAdventuresWindow> Inner = WeakThis.Pin())
						{
							Inner->Controller->Toast(FText::Format(LOCTEXT("MentorChosen", "{0} jest teraz twoim mentorem."), FText::FromString(Name)));
							Inner->Show(FWAdventureText::Mentor, Payload);
						}
					});
				}, true)
			], 6.f);
	}
	const TArray<TSharedPtr<FJsonObject>> Mentees = FWUi::Objects(State, TEXT("mentees"));
	if (FWUi::Bool(State, TEXT("canMentor")) || Mentees.Num() > 0)
	{
		Add(FWUi::Heading(FText::Format(LOCTEXT("Mentees", "Twoi podopieczni ({0}/{1})"), Mentees.Num(), int32(FWUi::Num(Rules, TEXT("mentees"))))), 18.f);
		if (Mentees.Num() == 0)
		{
			Add(FWUi::Note(LOCTEXT("NoMentees", "Nikt jeszcze nie wybrał cię na mentora. Podaj nowym graczom swoją nazwę na czacie."), FWStyle::Muted));
		}
		for (const TSharedPtr<FJsonObject>& M : Mentees)
		{
			const FString Name = FWUi::Str(M, TEXT("name"));
			TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
			if (FWUi::Bool(M, TEXT("reward")))
			{
				Right->AddSlot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(FText::Format(LOCTEXT("ClaimMentorXp", "Odbierz {0} XP"), int32(FWUi::Num(Rules, TEXT("mentorXp")))), Claim(Name), true)];
			}
			else if (FWUi::Bool(M, TEXT("rewarded")))
			{
				Right->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)[SNew(STextBlock).Text(LOCTEXT("RewardTaken2", "Nagroda odebrana")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Good)];
			}
			Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Case", "Gablota"), [WeakThis, Name]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->ShowTab(FWAdventureText::Showcase, Name); } })];
			Add(FWAdventureText::Line(FText::FromString(FString::Printf(TEXT("%s, poziom %d%s"), *Name, int32(FWUi::Num(M, TEXT("level"))), FWUi::Bool(M, TEXT("online")) ? TEXT("  ·  w grze") : TEXT(""))),
				FWStyle::Text, Right), 3.f);
		}
	}
	else if (!Mentor.IsValid() && !FWUi::Bool(State, TEXT("canChoose")))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("MentorFrom", "Od {0} poziomu możesz zostać mentorem nowych graczy."), int32(FWUi::Num(Rules, TEXT("mentorLevel")))), FWStyle::Text), 14.f);
	}
	return Page;
}

TSharedRef<SWidget> SFWAdventuresWindow::MakeShowcase()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const bool bMine = FWUi::Bool(State, TEXT("mine"));
	Summary->SetText(FText::Format(LOCTEXT("Badges", "Odznaki: {0} z {1}"), int32(FWUi::Num(State, TEXT("badges"))), int32(FWUi::Num(State, TEXT("totalBadges")))));
	Add(FWUi::Heading(bMine ? LOCTEXT("MyCase", "Twoja gablota z trofeami") : FText::Format(LOCTEXT("TheirCase", "Gablota z trofeami: {0}"), FText::FromString(FWUi::Str(State, TEXT("name"))))), 0.f);
	TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[Form->Field(TEXT("other"), LOCTEXT("OtherCase", "Obejrzyj gablotę gracza…"), 300.f)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
		[
			FWUi::SmallButton(LOCTEXT("Show", "Pokaż"), [WeakThis]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->ShowTab(FWAdventureText::Showcase, This->Form->Get(TEXT("other"))); } })
		];
	if (!bMine)
	{
		Row->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Mine", "Moja gablota"), [WeakThis]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->ShowTab(FWAdventureText::Showcase, FString()); } })];
	}
	Add(Row, 6.f);
	const TArray<TSharedPtr<FJsonObject>> Honours = FWUi::Objects(State, TEXT("honours"));
	if (Honours.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("EmptyCase", "Gablota jest jeszcze pusta: odznaki, pierwsze miejsca w świecie i wygrane wyzwania pojawią się tu same."), FWStyle::Muted), 10.f);
	}
	static const TMap<FString, FText> Kinds = { { TEXT("first"), LOCTEXT("Firsts", "Pierwsi w świecie") }, { TEXT("trophy"), LOCTEXT("Trophies", "Wygrane wyzwania") }, { TEXT("badge"), LOCTEXT("BadgesKind", "Odznaki") } };
	FString Kind;
	TSharedPtr<SUniformGridPanel> Grid;
	int32 Index = 0;
	for (const TSharedPtr<FJsonObject>& Honour : Honours)
	{
		if (FWUi::Str(Honour, TEXT("kind")) != Kind)
		{
			Kind = FWUi::Str(Honour, TEXT("kind"));
			Add(FWUi::Heading(Kinds.Contains(Kind) ? Kinds[Kind] : FText::FromString(Kind)), 16.f);
			SAssignNew(Grid, SUniformGridPanel).SlotPadding(FMargin(0.f, 0.f, 12.f, 6.f));
			Add(Grid.ToSharedRef(), 4.f);
			Index = 0;
		}
		Grid->AddSlot(Index % 2, Index / 2)
		[
			SNew(SFWCard).Padding(FMargin(8.f, 6.f))
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()[SNew(STextBlock).Text(FText::FromString(FWUi::Str(Honour, TEXT("name")))).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
					.Text(FText::Format(LOCTEXT("Honour", "{0}  ·  {1}"), FText::FromString(FWUi::Str(Honour, TEXT("description"))), FWAdventureText::Date(FWUi::Num(Honour, TEXT("at")))))
				]
			]
		];
		++Index;
	}
	return Page;
}

FReply SFWAdventuresWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
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
