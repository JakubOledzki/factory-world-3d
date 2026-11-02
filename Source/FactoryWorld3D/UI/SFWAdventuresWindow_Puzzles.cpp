// Puzzles: the list, playing one and the editor.
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

TSharedRef<SWidget> SFWAdventuresWindow::MakePuzzles()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const TArray<TSharedPtr<FJsonObject>> List = FWUi::Objects(State, TEXT("puzzles"));
	int32 Golds = 0;
	for (const TSharedPtr<FJsonObject>& P : List)
	{
		Golds += int32(FWUi::Num(FWUi::Obj(P, TEXT("best")), TEXT("medal"))) == 3;
	}
	Summary->SetText(FText::Format(LOCTEXT("Golds", "Złote medale: {0} z {1}"), Golds, List.Num()));
	Add(FWUi::Note(FText::Format(LOCTEXT("PuzzlesNote", "Ułóż linię z taśm, podajników i maszyn, która dostarczy tyle, ile trzeba, z jak najmniejszej liczby elementów. Pierwsze rozwiązanie daje {0} XP, pierwsze złoto drugie tyle."),
		int32(FWUi::Num(State, TEXT("solveXp")))), FWStyle::Muted), 0.f);
	for (int32 i = 0; i < List.Num(); ++i)
	{
		const TSharedPtr<FJsonObject>& P = List[i];
		const TSharedPtr<FJsonObject> Best = FWUi::Obj(P, TEXT("best"));
		TSharedRef<SHorizontalBox> Right = SNew(SHorizontalBox);
		if (Best.IsValid())
		{
			const int32 Medal = int32(FWUi::Num(Best, TEXT("medal")));
			Right->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(FWAdventureText::MedalColor(Medal))
				.Text(FText::Format(LOCTEXT("Best", "{0} ({1} el.)"), FWAdventureText::Medal(Medal), int32(FWUi::Num(Best, TEXT("objects")))))
			];
		}
		Right->AddSlot().AutoWidth()[FWUi::SmallButton(LOCTEXT("Open", "Otwórz"), [WeakThis, i]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->OpenPuzzle(i); } }, true)];
		Add(FWAdventureText::Line(FText::FromString(FString::Printf(TEXT("%s\n%s: %s/min. Złoto do %d elementów, srebro do %d."), *FWUi::Str(P, TEXT("name")),
			*FWUi::Str(FWUi::Obj(P, TEXT("output")), TEXT("name")), *FWAdventureText::Number(FWUi::Num(P, TEXT("target"))), int32(FWUi::Num(P, TEXT("gold"))), int32(FWUi::Num(P, TEXT("silver"))))),
			FWStyle::Text, Right, Controller.Get(), FWUi::Str(FWUi::Obj(P, TEXT("output")), TEXT("id"))), 4.f);
	}
	return Page;
}

void SFWAdventuresWindow::OpenPuzzle(int32 Index)
{
	const TArray<TSharedPtr<FJsonObject>> List = FWUi::Objects(State, TEXT("puzzles"));
	if (!List.IsValidIndex(Index))
	{
		return;
	}
	Puzzle = List[Index];
	Board->ShowPuzzle(Puzzle);
	Result = FText::GetEmpty();
	Rebuild();
}

TSharedRef<SWidget> SFWAdventuresWindow::MakeEditor()
{
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 4.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	Summary->SetText(FText::FromString(FWUi::Str(Puzzle, TEXT("name"))));
	double Belt = 300.0;
	TSharedRef<SHorizontalBox> PieceButtons = SNew(SHorizontalBox);
	for (const TSharedPtr<FJsonObject>& Allowed : FWUi::Objects(Puzzle, TEXT("allowed")))
	{
		const FString Id = FWUi::Str(Allowed, TEXT("id"));
		double PerMinute = 0.0;
		const bool bPace = Allowed->TryGetNumberField(TEXT("perMinute"), PerMinute);
		if (Id == TEXT("logistics_belt") && bPace)
		{
			Belt = PerMinute;
		}
		PieceButtons->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
		[
			SNew(SBox).ToolTipText(FText::FromString(FString::Printf(TEXT("%s (najwyżej %d)%s"), *FWUi::Str(Allowed, TEXT("name")), int32(FWUi::Num(Allowed, TEXT("most"))),
				bPace ? *FString::Printf(TEXT("\n%s szt./min"), *FWAdventureText::Number(PerMinute)) : TEXT(""))))
			[
				FWUi::TabButton(FText::FromString(FWUi::Str(Allowed, TEXT("name"))), [WeakThis, Id]() { TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin(); return This && This->Board->Kind == Id; },
					[WeakThis, Id]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->Board->Kind = Id; } })
			]
		];
	}
	Add(FWUi::Note(FText::FromString(FWUi::Str(Puzzle, TEXT("story"))), FWStyle::Text), 0.f);
	Add(FWUi::Note(FText::Format(LOCTEXT("EditorHelp", "Kliknij pole, by postawić wybrany element; kliknięcie elementu go obraca, prawy przycisk zabiera. R obraca następny. Podajnik bierze z pola za sobą i kładzie na pole przed sobą (strzałka). Taśma niesie {0} szt./min."),
		FText::FromString(FWAdventureText::Number(Belt))), FWStyle::Muted));
	Add(PieceButtons, 8.f);
	Add(SNew(SBox).HAlign(HAlign_Center)[SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(6.f)[Board.ToSharedRef()]], 10.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Check", "Sprawdź"), [WeakThis]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->SubmitPuzzle(); } }, true)]
		+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)[FWUi::SmallButton(LOCTEXT("Clear", "Wyczyść"), [WeakThis]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->Board->Clear(); This->Result = FText::GetEmpty(); } })]
		+ SHorizontalBox::Slot().AutoWidth()[FWUi::SmallButton(LOCTEXT("AllPuzzles", "Wszystkie zagadki"), [WeakThis]() { if (TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin()) { This->Puzzle.Reset(); This->Load(); } })], 10.f);
	Add(SNew(STextBlock).Font(FWStyle::Font(12)).AutoWrapText(true)
		.Text_Lambda([WeakThis]() { TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin(); return This ? This->Result : FText::GetEmpty(); })
		.ColorAndOpacity_Lambda([WeakThis]() { TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin(); return FSlateColor(This ? This->ResultColor : FLinearColor::White); }), 8.f);
	const TSharedPtr<FJsonObject> Machine = FWUi::Obj(Puzzle, TEXT("machine"));
	if (Machine.IsValid())
	{
		Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Maszyna bierze %s szt./min surowca i robi z każdej sztuki %s szt. produktu."), *FWAdventureText::Number(FWUi::Num(Machine, TEXT("inPerMinute"))),
			*FWAdventureText::Number(FWUi::Num(Machine, TEXT("outPerIn"))))), FWStyle::Muted), 6.f);
	}
	return Page;
}

void SFWAdventuresWindow::SubmitPuzzle()
{
	if (!Puzzle.IsValid())
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("puzzle"), FWUi::Str(Puzzle, TEXT("id")));
	P->SetArrayField(TEXT("pieces"), Board->PiecesJson());
	TWeakPtr<SFWAdventuresWindow> WeakThis = SharedThis(this);
	Act(TEXT("puzzle.submit"), P, [WeakThis](const TSharedPtr<FJsonObject>& R)
	{
		TSharedPtr<SFWAdventuresWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->Board->Flow.Reset();
		const TArray<TSharedPtr<FJsonValue>>* Flow = nullptr;
		if (R->TryGetArrayField(TEXT("flow"), Flow))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Flow)
			{
				This->Board->Flow.Add(Value->AsNumber());
			}
		}
		static const TMap<FString, FString> Problems = {
			{ TEXT("outside"), TEXT("Coś wystaje poza plac.") }, { TEXT("overlap"), TEXT("Dwa elementy stoją na tym samym polu.") }, { TEXT("bad_rotation"), TEXT("Zły obrót elementu.") },
			{ TEXT("mixed"), TEXT("Surowiec i produkt jadą tą samą taśmą albo w tym samym podajniku: linia się zatka.") }, { TEXT("machine_idle"), TEXT("Maszyna nic nie dostaje.") } };
		TArray<FString> Lines = { FString::Printf(TEXT("Dostarcza %s z %s szt./min, %d elementów."), *FWAdventureText::Number(FWUi::Num(R, TEXT("rate"))),
			*FWAdventureText::Number(FWUi::Num(This->Puzzle, TEXT("target"))), int32(FWUi::Num(R, TEXT("objects")))) };
		for (const FString& Problem : FWUi::Strings(R, TEXT("problems")))
		{
			if (Problem.StartsWith(TEXT("not_allowed:")))
			{
				Lines.Add(TEXT("Ten element nie jest dozwolony w tej zagadce."));
			}
			else if (Problem.StartsWith(TEXT("too_many:")))
			{
				Lines.Add(FString::Printf(TEXT("Za dużo elementów: %s."), *Problem.RightChop(9)));
			}
			else
			{
				Lines.Add(Problems.Contains(Problem) ? Problems[Problem] : Problem);
			}
		}
		if (FWUi::Bool(R, TEXT("solved")))
		{
			const int32 Medal = int32(FWUi::Num(R, TEXT("medal")));
			Lines.Add(FString::Printf(TEXT("Rozwiązane! Medal: %s."), *FWAdventureText::Medal(Medal).ToString()));
			This->ResultColor = FWAdventureText::MedalColor(Medal);
			if (R->HasTypedField<EJson::Object>(TEXT("xp")) || R->HasTypedField<EJson::Number>(TEXT("xp")))
			{
				This->Controller->Toast(FText::Format(LOCTEXT("Solved", "Zagadka „{0}” rozwiązana: {1}!"), FText::FromString(FWUi::Str(This->Puzzle, TEXT("name"))), FWAdventureText::Medal(Medal)));
			}
		}
		else
		{
			This->ResultColor = FWStyle::Text;
		}
		This->Result = FText::FromString(FString::Join(Lines, TEXT("\n")));
	});
}

#undef LOCTEXT_NAMESPACE
