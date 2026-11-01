#include "UI/SFWAchievementsWindow.h"

#include "Brushes/SlateRoundedBoxBrush.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SGridPanel.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SUniformGridPanel.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWAchievements"

namespace FWAchievementsLook
{
	const FLinearColor Silver = FLinearColor::FromSRGBColor(FColor(0xc9, 0xce, 0xd6));
	const FLinearColor Bronze = FLinearColor::FromSRGBColor(FColor(0xcd, 0x8a, 0x4f));
	const FLinearColor Ribbon = FLinearColor::FromSRGBColor(FColor(0xb8, 0x43, 0x2f));
	const FLinearColor Dark = FLinearColor::FromSRGBColor(FColor(0x3a, 0x3a, 0x3e));

	// Gold, silver and bronze for the podium
	FLinearColor PlaceColor(int32 Place, const FLinearColor& Otherwise)
	{
		return Place == 1 ? FWStyle::Accent : Place == 2 ? Silver : Place == 3 ? Bronze : Otherwise;
	}

	const FSlateBrush* Round(const FLinearColor& Fill, const FLinearColor& Outline, float Radius)
	{
		static TMap<FString, TSharedPtr<FSlateRoundedBoxBrush>> Brushes;
		TSharedPtr<FSlateRoundedBoxBrush>& Brush = Brushes.FindOrAdd(Fill.ToString() + Outline.ToString() + FString::SanitizeFloat(Radius));
		if (!Brush.IsValid())
		{
			Brush = MakeShared<FSlateRoundedBoxBrush>(Fill, Radius, Outline, 2.f);
		}
		return Brush.Get();
	}

	FText Date(double Ms)
	{
		const FDateTime When = FDateTime::FromUnixTimestamp(int64(Ms / 1000.0));
		return FText::FromString(FString::Printf(TEXT("%02d.%02d.%d"), When.GetDay(), When.GetMonth(), When.GetYear()));
	}

	// Time from now until At (ms since 1970), as "5 h 12 min" or "2 dni 3 h"
	FText Until(double At)
	{
		const int64 Seconds = FMath::Max<int64>(0, int64((At - FWUi::LocalNowMs()) / 1000.0));
		const int64 Days = Seconds / 86400, Hours = Seconds % 86400 / 3600;
		if (Days > 0)
		{
			return FText::Format(LOCTEXT("Days", "{0} {1} {2} h"), Days, Days == 1 ? LOCTEXT("Day", "dzień") : LOCTEXT("DaysWord", "dni"), Hours);
		}
		return FText::Format(LOCTEXT("Hours", "{0} h {1} min"), Hours, Seconds % 3600 / 60);
	}
}

TSharedRef<SWidget> SFWAchievementsWindow::Medal(bool bWon, float Size)
{
	using namespace FWAchievementsLook;
	const FLinearColor Face = bWon ? FWStyle::Accent : FLinearColor::FromSRGBColor(FColor(0x4a, 0x4a, 0x4f));
	const FLinearColor Rim = bWon ? FWStyle::Accent * FLinearColor(0.65f, 0.65f, 0.65f, 1.f) : FLinearColor::FromSRGBColor(FColor(0x2a, 0x2a, 0x2d));
	// A ribbon above a round medal stamped with a star
	return SNew(SBox).WidthOverride(Size * 0.85f).HeightOverride(Size)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().HAlign(HAlign_Center)
			[
				SNew(SBox).WidthOverride(Size * 0.36f).HeightOverride(Size * 0.32f)
				[
					SNew(SBorder).BorderImage(Round(bWon ? Ribbon : Dark, bWon ? Ribbon * 0.75f : Dark, 2.f))
				]
			]
			+ SVerticalBox::Slot().FillHeight(1.f).HAlign(HAlign_Center).Padding(0, -2, 0, 0)
			[
				SNew(SBox).WidthOverride(Size * 0.66f).HeightOverride(Size * 0.66f)
				[
					SNew(SBorder).BorderImage(Round(Face, Rim, Size * 0.33f)).HAlign(HAlign_Center).VAlign(VAlign_Center).Padding(0.f)
					[
						SNew(STextBlock).Text(FText::FromString(TEXT("★"))).Font(FWStyle::Font(FMath::RoundToInt(Size * 0.32f), TEXT("Bold"))).ColorAndOpacity(Rim)
					]
				]
			]
		];
}

void SFWAchievementsWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	auto TabButton = [this](int32 Index, const FText& Text)
	{
		return FWUi::TabButton(Text, [this, Index]() { return Tab == Index; }, [this, Index]() { ShowTab(Index); });
	};
	TSharedRef<SWidget> Tabs = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[TabButton(0, LOCTEXT("Badges", "Osiągnięcia"))]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[TabButton(1, LOCTEXT("Rankings", "Rankingi"))]
		+ SHorizontalBox::Slot().AutoWidth()[TabButton(2, LOCTEXT("Rivalry", "Rywalizacja"))];
	ChildSlot
	[
		FWUi::Frame(LOCTEXT("Title", "Osiągnięcia i rywalizacja"), SAssignNew(Summary, STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Muted),
			Tabs, SAssignNew(Content, SBox), 1100.f, 780.f)
	];
	Load();
}

void SFWAchievementsWindow::ShowTab(int32 InTab)
{
	Tab = InTab;
	Load();
}

void SFWAchievementsWindow::ShowRanking(const FString& InRanking)
{
	Tab = 1;
	Ranking = InRanking;
	Load();
}

void SFWAchievementsWindow::Load()
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
	TWeakPtr<SFWAchievementsWindow> WeakThis = SharedThis(this);
	auto Done = [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWAchievementsWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->bLoading = false;
		if (This->bAgain)
		{
			This->bAgain = false;
			This->Load();
			return;
		}
		This->Show(Reply);
	};
	if (Tab == 0)
	{
		PC->Send(TEXT("achievements.get"), nullptr, Done);
	}
	else if (Tab == 1)
	{
		TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
		if (!Ranking.IsEmpty())
		{
			P->SetStringField(TEXT("ranking"), Ranking);
		}
		PC->Send(TEXT("rankings.get"), P, Done);
	}
	else
	{
		// The rivalry, the meteorite and the wonders together in one page
		PC->Send(TEXT("rivalry.get"), nullptr, [WeakThis, Done](const FFWNetReply& Rivalry)
		{
			TSharedPtr<SFWAchievementsWindow> This = WeakThis.Pin();
			AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
			if (!Inner || Rivalry.Type != TEXT("rivalry"))
			{
				Done(Rivalry);
				return;
			}
			Inner->Send(TEXT("meteor.get"), nullptr, [WeakThis, Done, Rivalry](const FFWNetReply& Meteor)
			{
				TSharedPtr<SFWAchievementsWindow> This = WeakThis.Pin();
				AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
				if (!Inner || Meteor.Type != TEXT("meteor"))
				{
					Done(Meteor);
					return;
				}
				Inner->Send(TEXT("company.wonders"), nullptr, [Done, Rivalry, Meteor](const FFWNetReply& Wonders)
				{
					if (Wonders.Type != TEXT("company.wonders"))
					{
						Done(Wonders);
						return;
					}
					FFWNetReply Joined = Rivalry;
					Joined.Payload = MakeShared<FJsonObject>();
					Joined.Payload->SetObjectField(TEXT("rivalry"), Rivalry.Payload);
					Joined.Payload->SetObjectField(TEXT("meteor"), FWUi::Obj(Meteor.Payload, TEXT("state")));
					Joined.Payload->SetArrayField(TEXT("wonders"), Wonders.Payload->GetArrayField(TEXT("wonders")));
					Done(Joined);
				});
			});
		});
	}
}

void SFWAchievementsWindow::Show(const FFWNetReply& Reply)
{
	TSharedPtr<SWidget> Page;
	if (Reply.Type == TEXT("achievements"))
	{
		Summary->SetText(FText::Format(LOCTEXT("Got", "Zdobyte odznaki: {0} z {1}"), int32(FWUi::Num(Reply.Payload, TEXT("unlocked"))),
			int32(FWUi::Num(Reply.Payload, TEXT("total")))));
		Page = MakeBadges(Reply.Payload);
	}
	else if (Reply.Type == TEXT("rankings"))
	{
		Summary->SetText(LOCTEXT("Best", "Najlepsi gracze"));
		Page = MakeRanking(Reply.Payload);
	}
	else if (Reply.Type == TEXT("rivalry"))
	{
		Summary->SetText(LOCTEXT("Between", "Rywalizacja między graczami"));
		Page = MakeRivalry(FWUi::Obj(Reply.Payload, TEXT("rivalry")), FWUi::Obj(Reply.Payload, TEXT("meteor")), FWUi::Objects(Reply.Payload, TEXT("wonders")));
	}
	else
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->ToastError(Reply);
		}
		return;
	}
	Content->SetContent(SNew(SScrollBox) + SScrollBox::Slot().Padding(0, 0, 14, 0)[Page.ToSharedRef()]);
}

TSharedRef<SWidget> SFWAchievementsWindow::MakeBadges(const TSharedPtr<FJsonObject>& State)
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	for (const TSharedPtr<FJsonObject>& Group : FWUi::Objects(State, TEXT("groups")))
	{
		const TArray<TSharedPtr<FJsonObject>> Badges = FWUi::Objects(Group, TEXT("badges"));
		int32 Got = 0;
		for (const TSharedPtr<FJsonObject>& Badge : Badges)
		{
			Got += Badge->HasTypedField<EJson::Number>(TEXT("unlockedAt"));
		}
		Page->AddSlot().AutoHeight().Padding(0, 12, 0, 6)
		[
			FWUi::Heading(FText::Format(LOCTEXT("Group", "{0}  ({1}/{2})"), FText::FromString(FWUi::Str(Group, TEXT("name"))), Got, Badges.Num()))
		];
		TSharedRef<SUniformGridPanel> Grid = SNew(SUniformGridPanel).SlotPadding(FMargin(0.f, 0.f, 16.f, 6.f));
		for (int32 i = 0; i < Badges.Num(); ++i)
		{
			const TSharedPtr<FJsonObject>& Badge = Badges[i];
			const bool bWon = Badge->HasTypedField<EJson::Number>(TEXT("unlockedAt"));
			const double Goal = FMath::Max(1.0, FWUi::Num(Badge, TEXT("goal"), 1.0));
			const double Progress = FWUi::Num(Badge, TEXT("progress"));
			TSharedRef<SVerticalBox> Text = SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Text(FText::FromString(FWUi::Str(Badge, TEXT("name")))).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(bWon ? FWStyle::Text : FWStyle::Muted)
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Text(FText::FromString(FWUi::Str(Badge, TEXT("description")))).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
				];
			if (bWon)
			{
				Text->AddSlot().AutoHeight()
				[
					SNew(STextBlock).Text(FText::Format(LOCTEXT("WonAt", "Zdobyta {0}"), FWAchievementsLook::Date(FWUi::Num(Badge, TEXT("unlockedAt"))))).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Good)
				];
			}
			else
			{
				Text->AddSlot().AutoHeight().Padding(0, 3, 0, 1)[FWUi::Bar(float(Progress / Goal), FWStyle::Accent)];
				Text->AddSlot().AutoHeight()[SNew(STextBlock).Text(FText::FromString(FWUi::Str(Badge, TEXT("text")))).Font(FWStyle::Font(9)).ColorAndOpacity(FWStyle::Muted)];
			}
			Grid->AddSlot(i % 2, i / 2)
			[
				SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(8.f, 6.f))
				[
					SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[Medal(bWon)]
					+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)[Text]
				]
			];
		}
		Page->AddSlot().AutoHeight()[Grid];
	}
	return Page;
}

TSharedRef<SWidget> SFWAchievementsWindow::Podium(const TArray<TSharedPtr<FJsonObject>>& Entries, const TCHAR* TextField)
{
	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox);
	for (const TSharedPtr<FJsonObject>& Entry : Entries)
	{
		const bool bMine = FWUi::Bool(Entry, TEXT("mine"));
		const int32 Place = int32(FWUi::Num(Entry, TEXT("place")));
		const FLinearColor Color = bMine ? FWStyle::Accent : FWStyle::Text;
		FString Value = FWUi::Str(Entry, TextField);
		if (Value.IsEmpty() && Entry->HasField(TextField))
		{
			Value = FString::Printf(TEXT("%d szt."), int32(FWUi::Num(Entry, TextField)));
		}
		Box->AddSlot().AutoHeight().Padding(0, 2)
		[
			SNew(SBorder).BorderImage(bMine ? FWStyle::Panel() : FWStyle::SideShade()).Padding(FMargin(10.f, 5.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(SBox).WidthOverride(46.f).HAlign(HAlign_Right)
					[
						SNew(STextBlock).Text(FText::Format(LOCTEXT("Place", "{0}."), Place)).Font(FWStyle::Font(13, TEXT("Bold")))
						.ColorAndOpacity(FWAchievementsLook::PlaceColor(Place, Color))
					]
				]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center).Padding(14, 0, 0, 0)
				[
					SNew(STextBlock).Font(FWStyle::Font(12, bMine ? TEXT("Bold") : TEXT("Regular"))).ColorAndOpacity(Color)
					.Text(FText::FromString(FWUi::Str(Entry, TEXT("name")) + (bMine ? TEXT("  (ty)") : TEXT(""))))
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(STextBlock).Text(FText::FromString(Value)).Font(FWStyle::Font(12)).ColorAndOpacity(Color)
				]
			]
		];
	}
	return Box;
}

TSharedRef<SWidget> SFWAchievementsWindow::MakeRanking(const TSharedPtr<FJsonObject>& State)
{
	Ranking = FWUi::Str(State, TEXT("ranking"));
	TSharedRef<SWrapBox> Choice = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
	for (const TSharedPtr<FJsonObject>& Each : FWUi::Objects(State, TEXT("rankings")))
	{
		const FString Id = FWUi::Str(Each, TEXT("id"));
		Choice->AddSlot()
		[
			FWUi::TabButton(FText::FromString(FWUi::Str(Each, TEXT("name"))), [this, Id]() { return Ranking == Id; }, [this, Id]() { ShowRanking(Id); })
		];
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 12)[Choice];
	const TArray<TSharedPtr<FJsonObject>> Top = FWUi::Objects(State, TEXT("top"));
	if (Top.Num() == 0)
	{
		Page->AddSlot().AutoHeight()[FWUi::Note(LOCTEXT("Nobody", "Nikt jeszcze nie trafił do tego rankingu."), FWStyle::Muted)];
	}
	Page->AddSlot().AutoHeight()[Podium(Top, TEXT("text"))];
	const TSharedPtr<FJsonObject> Me = FWUi::Obj(State, TEXT("me"));
	Page->AddSlot().AutoHeight().Padding(0, 14, 0, 0)
	[
		Me.IsValid()
			? SNew(STextBlock).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
				.Text(FText::Format(LOCTEXT("Mine", "Twoje miejsce: {0}.  {1}"), int32(FWUi::Num(Me, TEXT("place"))), FText::FromString(FWUi::Str(Me, TEXT("text")))))
			: FWUi::Note(LOCTEXT("NotIn", "Nie ma cię jeszcze w tym rankingu."), FWStyle::Muted)
	];
	return Page;
}

TSharedRef<SWidget> SFWAchievementsWindow::MakeRivalry(const TSharedPtr<FJsonObject>& Rivalry, const TSharedPtr<FJsonObject>& Meteor, const TArray<TSharedPtr<FJsonObject>>& Wonders)
{
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	// The challenge of the week: one contest from Monday to Sunday, the podium gets money and a trophy
	const TSharedPtr<FJsonObject> Challenge = FWUi::Obj(Rivalry, TEXT("challenge"));
	TArray<double> Prizes;
	for (const TSharedPtr<FJsonValue>& Value : Challenge.IsValid() && Challenge->HasField(TEXT("prizes")) ? Challenge->GetArrayField(TEXT("prizes")) : TArray<TSharedPtr<FJsonValue>>())
	{
		Prizes.Add(Value->AsNumber());
	}
	Prizes.SetNumZeroed(3);
	Add(FWUi::Heading(FText::Format(LOCTEXT("Challenge", "Wyzwanie tygodnia: {0}"), FText::FromString(FWUi::Str(Challenge, TEXT("name"))))), 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("ChallengeNote", "{0} Koniec za {1}. Nagrody: {2}, {3} i {4} oraz trofeum. Boty serwera nie biorą udziału."),
		FText::FromString(FWUi::Str(Challenge, TEXT("description"))), FWAchievementsLook::Until(FWUi::Num(Challenge, TEXT("endsAt"))),
		FWUi::Money(Prizes[0]), FWUi::Money(Prizes[1]), FWUi::Money(Prizes[2])), FWStyle::Muted));
	const TArray<TSharedPtr<FJsonObject>> Top = FWUi::Objects(Challenge, TEXT("top"));
	if (Top.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NobodyYet", "Nikt jeszcze nie zaczął. Bądź pierwszy!"), FWStyle::Muted));
	}
	Add(Podium(Top, TEXT("text")));
	if (const TSharedPtr<FJsonObject> Me = FWUi::Obj(Challenge, TEXT("me")))
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("MyPlace", "Twoje miejsce: {0}.  {1}"), int32(FWUi::Num(Me, TEXT("place"))), FText::FromString(FWUi::Str(Me, TEXT("text")))), FWStyle::Accent));
	}
	const TSharedPtr<FJsonObject> Next = FWUi::Obj(Challenge, TEXT("next"));
	Add(FWUi::Note(FText::Format(LOCTEXT("Next", "W przyszłym tygodniu: {0}. {1}"), FText::FromString(FWUi::Str(Next, TEXT("name"))),
		FText::FromString(FWUi::Str(Next, TEXT("description")))), FWStyle::Muted), 6.f);
	const TSharedPtr<FJsonObject> LastWeek = FWUi::Obj(Rivalry, TEXT("lastWeek"));
	const TArray<TSharedPtr<FJsonObject>> Winners = FWUi::Objects(LastWeek, TEXT("winners"));
	if (Winners.Num() > 0)
	{
		Add(FWUi::Note(FText::Format(LOCTEXT("LastWeek", "Zeszły tydzień ({0}):"), FText::FromString(FWUi::Str(LastWeek, TEXT("name")))), FWStyle::Text), 8.f);
		for (const TSharedPtr<FJsonObject>& Winner : Winners)
		{
			const int32 Place = int32(FWUi::Num(Winner, TEXT("place")));
			Add(FWUi::Note(FText::Format(LOCTEXT("Winner", "{0}. {1}{2} · {3} · nagroda {4}"), Place, FText::FromString(FWUi::Str(Winner, TEXT("name"))),
				FWUi::Bool(Winner, TEXT("mine")) ? LOCTEXT("You", " (ty)") : FText::GetEmpty(), FText::FromString(FWUi::Str(Winner, TEXT("text"))),
				FWUi::Money(FWUi::Num(Winner, TEXT("prizeCents")))), FWAchievementsLook::PlaceColor(Place, FWStyle::Text)), 2.f);
		}
	}
	const TArray<TSharedPtr<FJsonObject>> Trophies = FWUi::Objects(Rivalry, TEXT("trophies"));
	if (Trophies.Num() > 0)
	{
		int32 Counts[3] = { 0, 0, 0 };
		for (const TSharedPtr<FJsonObject>& Trophy : Trophies)
		{
			Counts[FMath::Clamp(int32(FWUi::Num(Trophy, TEXT("place"))) - 1, 0, 2)]++;
		}
		Add(FWUi::Note(FText::Format(LOCTEXT("Trophies", "Twoje trofea: {0} × złoto, {1} × srebro, {2} × brąz"), Counts[0], Counts[1], Counts[2]), FWStyle::Accent), 6.f);
	}

	// The meteorite of these eight hours: where it fell, what is left, and digging for those with a plot near it
	Add(FWUi::Heading(LOCTEXT("Meteor", "Meteoryt")), 22.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("MeteorNote", "Co 8 godzin spada meteoryt w pobliżu działki któregoś z graczy. Kopią z kilofem w ręku właściciele działek w promieniu {0} działek, sztuka co {1} s, póki starczy. Następny spadnie za {2}."),
		int32(FWUi::Num(Meteor, TEXT("reach"))), int32(FWUi::Num(Meteor, TEXT("digSeconds"))), FWAchievementsLook::Until(FWUi::Num(Meteor, TEXT("nextAt")))), FWStyle::Muted));
	Add(FWUi::Note(FText::Format(LOCTEXT("MeteorWhere", "Spadł niedaleko miasta {0} (działka {1}, {2}). Zostało {3} z {4} sztuk żelaza meteorytowego i {5} irydu."),
		FText::FromString(FWUi::Str(Meteor, TEXT("town"))), FText::AsNumber(int32(FWUi::Num(Meteor, TEXT("x"))), &FNumberFormattingOptions::DefaultNoGrouping()),
		FText::AsNumber(int32(FWUi::Num(Meteor, TEXT("y"))), &FNumberFormattingOptions::DefaultNoGrouping()), int32(FWUi::Num(Meteor, TEXT("ironLeft"))),
		int32(FWUi::Num(Meteor, TEXT("iron"))), int32(FWUi::Num(Meteor, TEXT("iridiumLeft")))), FWStyle::Text));
	const bool bInReach = FWUi::Bool(Meteor, TEXT("inReach"));
	double Distance = 0.0;
	const bool bHasPlot = Meteor.IsValid() && Meteor->TryGetNumberField(TEXT("distance"), Distance);
	FNumberFormattingOptions One;
	One.MaximumFractionalDigits = 1;
	TWeakPtr<SFWAchievementsWindow> WeakThis = SharedThis(this);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			FWUi::Note(bHasPlot ? FText::Format(LOCTEXT("Distance", "Twoja najbliższa działka: {0} działek stąd{1}"), FText::AsNumber(Distance, &One),
				bInReach ? FText::GetEmpty() : LOCTEXT("TooFar", " (za daleko)")) : LOCTEXT("NoPlot", "Nie masz jeszcze działki."), bInReach ? FWStyle::Good : FWStyle::Muted)
		]
		+ SHorizontalBox::Slot().AutoWidth()
		[
			FWUi::SmallButton(LOCTEXT("Dig", "Kop"), [WeakThis]() { if (TSharedPtr<SFWAchievementsWindow> This = WeakThis.Pin()) { This->Dig(); } }, true,
				bInReach && FWUi::Num(Meteor, TEXT("ironLeft")) > 0.0)
		], 6.f);
	const TArray<TSharedPtr<FJsonObject>> Diggers = FWUi::Objects(Meteor, TEXT("diggers"));
	if (Diggers.Num() > 0)
	{
		Add(Podium(Diggers, TEXT("pieces")), 6.f);
	}

	// The wonders of the world: one of each on the server, for the company that brings every part first
	Add(FWUi::Heading(LOCTEXT("Wonders", "Cuda świata")), 22.f);
	Add(FWUi::Note(LOCTEXT("WondersNote", "Każdy cud świata buduje tylko jedna spółka na serwerze: ta, która pierwsza zbierze wszystkie części (okno spółki, Projekty)."), FWStyle::Muted));
	for (const TSharedPtr<FJsonObject>& Wonder : Wonders)
	{
		const FString Owner = FWUi::Str(Wonder, TEXT("owner"));
		const bool bOurs = FWUi::Bool(Wonder, TEXT("ours"));
		Add(SNew(STextBlock).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(bOurs ? FWStyle::Accent : FWStyle::Text)
			.Text(FText::Format(LOCTEXT("Wonder", "{0}: {1}"), FText::FromString(FWUi::Str(Wonder, TEXT("name"))),
				Owner.IsEmpty() ? LOCTEXT("Free", "wolny") : FText::Format(LOCTEXT("BuiltBy", "zbudowała spółka {0}{1}"), FText::FromString(Owner),
					bOurs ? LOCTEXT("Ours", " (twoja!)") : FText::GetEmpty()))), 8.f);
		Add(FWUi::Note(FText::FromString(FWUi::Str(Wonder, TEXT("description"))), FWStyle::Muted), 1.f);
		for (const TSharedPtr<FJsonObject>& Racer : FWUi::Objects(Wonder, TEXT("racers")))
		{
			Add(FWUi::Note(FText::Format(LOCTEXT("Racer", "    {0}. {1}{2}: {3}%"), int32(FWUi::Num(Racer, TEXT("place"))), FText::FromString(FWUi::Str(Racer, TEXT("name"))),
				FWUi::Bool(Racer, TEXT("ours")) ? LOCTEXT("OursShort", " (twoja)") : FText::GetEmpty(), FText::AsNumber(FWUi::Num(Racer, TEXT("percent")), &One)),
				FWUi::Bool(Racer, TEXT("ours")) ? FWStyle::Accent : FWStyle::Muted), 1.f);
		}
	}

	// Firsts in the world: each goes for good to the one player who got there before anybody else
	Add(FWUi::Heading(LOCTEXT("Firsts", "Pierwsi na świecie")), 22.f);
	Add(FWUi::Note(LOCTEXT("FirstsNote", "Każdy tytuł dostaje na zawsze tylko jeden gracz: ten, który zdobył go pierwszy na serwerze."), FWStyle::Muted));
	TSharedRef<SUniformGridPanel> Grid = SNew(SUniformGridPanel).SlotPadding(FMargin(0.f, 6.f, 16.f, 0.f));
	const TArray<TSharedPtr<FJsonObject>> Firsts = FWUi::Objects(Rivalry, TEXT("firsts"));
	for (int32 i = 0; i < Firsts.Num(); ++i)
	{
		const TSharedPtr<FJsonObject>& First = Firsts[i];
		const FString Owner = FWUi::Str(First, TEXT("owner"));
		const bool bMine = FWUi::Bool(First, TEXT("mine"));
		Grid->AddSlot(i % 2, i / 2)
		[
			SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(FMargin(8.f, 6.f))
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[Medal(!Owner.IsEmpty())]
				+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Text(FText::FromString(FWUi::Str(First, TEXT("name")))).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(Owner.IsEmpty() ? FWStyle::Muted : FWStyle::Text)
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Text(FText::FromString(FWUi::Str(First, TEXT("description")))).Font(FWStyle::Font(10)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(Owner.IsEmpty() ? FWStyle::Muted : bMine ? FWStyle::Accent : FWStyle::Good)
						.Text(Owner.IsEmpty() ? LOCTEXT("NoOne", "Jeszcze nikt") : FText::Format(LOCTEXT("Owner", "{0}{1}, {2}"), FText::FromString(Owner),
							bMine ? LOCTEXT("You2", " (ty)") : FText::GetEmpty(), FWAchievementsLook::Date(FWUi::Num(First, TEXT("at")))))
					]
				]
			]
		];
	}
	Add(Grid);
	return Page;
}

void SFWAchievementsWindow::Dig()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TWeakPtr<SFWAchievementsWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("meteor.dig"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWAchievementsWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("meteor"))
		{
			Inner->ToastError(Reply);
			return;
		}
		TArray<FString> Got;
		for (const TSharedPtr<FJsonObject>& Item : FWUi::Objects(Reply.Payload, TEXT("items")))
		{
			Got.Add(FString::Printf(TEXT("%d× %s"), int32(FWUi::Num(Item, TEXT("quantity"))), *FWUi::Str(Item, TEXT("name"))));
		}
		Inner->Toast(FText::Format(LOCTEXT("Dug", "Wykopano: {0}"), FText::FromString(FString::Join(Got, TEXT(", ")))));
		Inner->PlayUiSound(TEXT("build_pickup"));
		Inner->RefreshInventory();
		This->Load();
	});
}

FReply SFWAchievementsWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::O)
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
