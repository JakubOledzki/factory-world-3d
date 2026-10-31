#include "UI/SFWCharacterCreator.h"

#include "UI/FWStyle.h"
#include "World/FWCharacterStudio.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWCreator"

namespace
{
	TArray<FString> Strings(const TSharedPtr<FJsonObject>& Options, const TCHAR* Field)
	{
		TArray<FString> Result;
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Options.IsValid() && Options->TryGetArrayField(Field, Values))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				Result.Add(Value->AsString());
			}
		}
		return Result;
	}

	// Names of the styles in the order the server counts them (the same as the 2D game's editor); a style the
	// server adds later gets a number until it is named here
	TArray<FText> Styles(const TSharedPtr<FJsonObject>& Options, const TCHAR* Field, std::initializer_list<FText> Known)
	{
		TArray<FText> Names(Known);
		int32 Count = Names.Num();
		if (Options.IsValid())
		{
			Options->TryGetNumberField(Field, Count);
		}
		Names.SetNum(FMath::Max(Count, 1));
		for (int32 i = 0; i < Names.Num(); ++i)
		{
			if (Names[i].IsEmpty())
			{
				Names[i] = FText::Format(LOCTEXT("StyleN", "styl {0}"), i + 1);
			}
		}
		return Names;
	}
}

void SFWCharacterCreator::Construct(const FArguments& InArgs)
{
	Studio = InArgs._Studio;
	OnCreate = InArgs._OnCreate;
	const TSharedPtr<FJsonObject>& Options = InArgs._Options;

	// The rows of the 2D game's editor, in its order
	Rows = {
		{ LOCTEXT("Skin", "Skóra"), TEXT("skinTone"), Strings(Options, TEXT("skinTones")), {} },
		{ LOCTEXT("Face", "Twarz"), TEXT("faceStyle"), {}, Styles(Options, TEXT("faceStyles"),
			{ LOCTEXT("FaceCalm", "spokojna"), LOCTEXT("FaceTired", "zmęczona"), LOCTEXT("FaceCheerful", "pogodna"), LOCTEXT("FaceMature", "dojrzała") }) },
		{ LOCTEXT("Eyes", "Oczy"), TEXT("eyeColor"), Strings(Options, TEXT("eyeColors")), {} },
		{ LOCTEXT("Hair", "Fryzura"), TEXT("hairStyle"), {}, Styles(Options, TEXT("hairStyles"),
			{ LOCTEXT("HairBald", "łysy"), LOCTEXT("HairShort", "krótkie"), LOCTEXT("HairSpiky", "nastroszone"), LOCTEXT("HairLong", "długie"),
			  LOCTEXT("HairPonytail", "kucyk"), LOCTEXT("HairCurly", "kręcone") }) },
		{ LOCTEXT("HairColor", "Kolor włosów"), TEXT("hairColor"), Strings(Options, TEXT("hairColors")), {} },
		{ LOCTEXT("Beard", "Zarost"), TEXT("facialHair"), {}, Styles(Options, TEXT("facialHairStyles"),
			{ LOCTEXT("BeardNone", "gładko ogolony"), LOCTEXT("BeardMoustache", "wąsy"), LOCTEXT("BeardGoatee", "kozia bródka"), LOCTEXT("BeardFull", "pełna broda") }) },
		{ LOCTEXT("Shirt", "Koszulka"), TEXT("shirtStyle"), {}, Styles(Options, TEXT("shirtStyles"),
			{ LOCTEXT("ShirtTee", "t-shirt"), LOCTEXT("ShirtTank", "bezrękawnik"), LOCTEXT("ShirtHoodie", "bluza") }) },
		{ LOCTEXT("ShirtColor", "Kolor koszulki"), TEXT("shirtColor"), Strings(Options, TEXT("clothColors")), {} },
		{ LOCTEXT("Pants", "Spodnie"), TEXT("pantsColor"), Strings(Options, TEXT("clothColors")), {} },
		{ LOCTEXT("Shoes", "Buty"), TEXT("shoesColor"), Strings(Options, TEXT("clothColors")), {} },
	};
	// A first look that already reads as a worker in the mountains (each index kept inside what the server offers)
	const TPair<const TCHAR*, int32> Start[] = {
		{ TEXT("skinTone"), 1 }, { TEXT("faceStyle"), 0 }, { TEXT("eyeColor"), 1 }, { TEXT("hairStyle"), 1 }, { TEXT("hairColor"), 1 },
		{ TEXT("facialHair"), 0 }, { TEXT("shirtStyle"), 2 }, { TEXT("shirtColor"), 3 }, { TEXT("pantsColor"), 8 }, { TEXT("shoesColor"), 10 },
	};
	for (const FRow& Row : Rows)
	{
		Values.Add(Row.Key, 0);
	}
	for (const TPair<const TCHAR*, int32>& Pair : Start)
	{
		const FRow* Row = Rows.FindByPredicate([&](const FRow& R) { return R.Key == Pair.Key; });
		Values[Pair.Key] = Row && Row->Count() > 0 ? FMath::Min(Pair.Value, Row->Count() - 1) : 0;
	}

	TSharedRef<SVerticalBox> RowBox = SNew(SVerticalBox);
	for (int32 i = 0; i < Rows.Num(); ++i)
	{
		RowBox->AddSlot().AutoHeight().Padding(0, 3)[MakeRow(i)];
	}

	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(SBorder)
				.BorderImage(FWStyle::SideShade())
				.Padding(FMargin(72.f, 0.f, 56.f, 0.f))
				.VAlign(VAlign_Center)
				[
					SNew(SBox)
					.WidthOverride(430.f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(STextBlock)
							.Text(LOCTEXT("Title", "Nowa postać"))
							.Font(FWStyle::Font(34, TEXT("Bold")))
							.ColorAndOpacity(FWStyle::Text)
						]
						+ SVerticalBox::Slot().AutoHeight().Padding(0, 2, 0, 22)
						[
							SNew(STextBlock)
							.Text(LOCTEXT("Subtitle", "Tak będą cię widzieć inni gracze. Wygląd zmienisz później w grze."))
							.Font(FWStyle::Font(12))
							.ColorAndOpacity(FWStyle::Muted)
							.AutoWrapText(true)
						]
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(SBorder)
							.BorderImage(FWStyle::Panel())
							.Padding(24.f)
							[
								SNew(SVerticalBox)
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)
								[
									SNew(STextBlock).Text(LOCTEXT("Name", "Imię postaci")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 16)
								[
									SAssignNew(NameBox, SEditableTextBox)
									.Style(&FWStyle::TextBox())
									.HintText(LOCTEXT("NameHint", "3–16 liter, cyfr lub _"))
									.OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type Commit)
									{
										if (Commit == ETextCommit::OnEnter)
										{
											Submit();
										}
									})
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 18)
								[
									RowBox
								]
								+ SVerticalBox::Slot().AutoHeight()
								[
									SNew(SHorizontalBox)
									+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 10, 0)
									[
										SNew(SButton)
										.ButtonStyle(&FWStyle::SecondaryButton())
										.IsEnabled_Lambda([this] { return !bBusy; })
										.OnClicked_Lambda([this] { Randomize(); return FReply::Handled(); })
										[
											SNew(STextBlock).Text(LOCTEXT("Random", "Losuj")).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
										]
									]
									+ SHorizontalBox::Slot().FillWidth(1.f)
									[
										SNew(SButton)
										.ButtonStyle(&FWStyle::PrimaryButton())
										.HAlign(HAlign_Center)
										.IsEnabled(this, &SFWCharacterCreator::CanSubmit)
										.OnClicked_Lambda([this] { Submit(); return FReply::Handled(); })
										[
											SNew(STextBlock).Text(LOCTEXT("Create", "Utwórz postać")).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FLinearColor::White)
										]
									]
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 12, 0, 0)
								[
									SAssignNew(ErrorText, STextBlock)
									.Font(FWStyle::Font(11))
									.ColorAndOpacity(FWStyle::Error)
									.AutoWrapText(true)
								]
							]
						]
					]
				]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
		]
		+ SOverlay::Slot()
		.HAlign(HAlign_Right)
		.VAlign(VAlign_Bottom)
		.Padding(40.f)
		[
			SNew(STextBlock)
			.Text(LOCTEXT("Hint", "Przeciągnij, aby obrócić  ·  Kółko myszy: przybliż twarz"))
			.Font(FWStyle::Font(11))
			.ColorAndOpacity(FWStyle::Muted)
		]
	];
	ApplyLook();
}

TSharedRef<SWidget> SFWCharacterCreator::MakeRow(int32 RowIndex)
{
	auto Arrow = [this, RowIndex](const FText& Glyph, int32 Delta)
	{
		return SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ContentPadding(FMargin(10.f, 2.f))
			.IsEnabled_Lambda([this] { return !bBusy; })
			.OnClicked_Lambda([this, RowIndex, Delta] { Step(RowIndex, Delta); return FReply::Handled(); })
			[
				SNew(STextBlock).Text(Glyph).Font(FWStyle::Font(13, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			];
	};
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SBox)
			.WidthOverride(124.f)
			[
				SNew(STextBlock).Text(Rows[RowIndex].Label).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)
			]
		]
		+ SHorizontalBox::Slot().AutoWidth()[Arrow(FText::FromString(TEXT("‹")), -1)]
		+ SHorizontalBox::Slot().FillWidth(1.f).HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)
			[
				SNew(SBox)
				.WidthOverride(18.f)
				.HeightOverride(18.f)
				.Visibility(this, &SFWCharacterCreator::SwatchVisibility, RowIndex)
				[
					SNew(SImage)
					.Image(FWStyle::Swatch())
					.ColorAndOpacity(this, &SFWCharacterCreator::SwatchColor, RowIndex)
				]
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(STextBlock)
				.Text(this, &SFWCharacterCreator::ValueText, RowIndex)
				.Font(FWStyle::Font(12))
				.ColorAndOpacity(FWStyle::Text)
			]
		]
		+ SHorizontalBox::Slot().AutoWidth()[Arrow(FText::FromString(TEXT("›")), 1)];
}

void SFWCharacterCreator::Step(int32 RowIndex, int32 Delta)
{
	const int32 Count = Rows[RowIndex].Count();
	if (Count > 0)
	{
		int32& Value = Values[Rows[RowIndex].Key];
		Value = (Value + Delta + Count) % Count;
		ApplyLook();
	}
}

FText SFWCharacterCreator::ValueText(int32 RowIndex) const
{
	const FRow& Row = Rows[RowIndex];
	const int32 Value = Values[Row.Key];
	if (Row.Palette.Num() > 0)
	{
		return FText::Format(LOCTEXT("ColorN", "{0} / {1}"), Value + 1, Row.Palette.Num());
	}
	return Row.StyleNames.IsValidIndex(Value) ? Row.StyleNames[Value] : FText::GetEmpty();
}

FSlateColor SFWCharacterCreator::SwatchColor(int32 RowIndex) const
{
	return FSlateColor(PaletteColor(Rows[RowIndex].Key));
}

EVisibility SFWCharacterCreator::SwatchVisibility(int32 RowIndex) const
{
	return Rows[RowIndex].Palette.Num() > 0 ? EVisibility::Visible : EVisibility::Collapsed;
}

FLinearColor SFWCharacterCreator::PaletteColor(const FString& Key) const
{
	const FRow* Row = Rows.FindByPredicate([&](const FRow& R) { return R.Key == Key; });
	if (!Row || !Row->Palette.IsValidIndex(Values[Key]))
	{
		return FLinearColor::Gray;
	}
	// The palettes are sRGB hex colours, as the 2D game draws them
	return FLinearColor::FromSRGBColor(FColor::FromHex(Row->Palette[Values[Key]]));
}

void SFWCharacterCreator::ApplyLook()
{
	AFWCharacterStudio* Target = Studio.Get();
	if (!Target)
	{
		return;
	}
	FFWLook Look;
	Look.Skin = PaletteColor(TEXT("skinTone"));
	Look.Hair = PaletteColor(TEXT("hairColor"));
	Look.Eyes = PaletteColor(TEXT("eyeColor"));
	Look.Shirt = PaletteColor(TEXT("shirtColor"));
	Look.Pants = PaletteColor(TEXT("pantsColor"));
	Look.Shoes = PaletteColor(TEXT("shoesColor"));
	Look.HairStyle = Values[TEXT("hairStyle")];
	Look.ShirtStyle = Values[TEXT("shirtStyle")];
	Look.FacialHair = Values[TEXT("facialHair")];
	Look.FaceStyle = Values[TEXT("faceStyle")];
	Target->SetLook(Look);
}

void SFWCharacterCreator::Pick(const FString& Key, int32 Index)
{
	const FRow* Row = Rows.FindByPredicate([&](const FRow& R) { return R.Key == Key; });
	if (Row && Row->Count() > 0)
	{
		Values[Key] = FMath::Clamp(Index, 0, Row->Count() - 1);
		ApplyLook();
	}
}

void SFWCharacterCreator::Randomize()
{
	for (const FRow& Row : Rows)
	{
		if (Row.Count() > 0)
		{
			Values[Row.Key] = FMath::RandHelper(Row.Count());
		}
	}
	ApplyLook();
}

void SFWCharacterCreator::SetName(const FString& Name)
{
	NameBox->SetText(FText::FromString(Name));
}

bool SFWCharacterCreator::CanSubmit() const
{
	return !bBusy && NameBox.IsValid() && !NameBox->GetText().IsEmpty();
}

void SFWCharacterCreator::Submit()
{
	if (bBusy)
	{
		return;
	}
	const FString Name = NameBox->GetText().ToString().TrimStartAndEnd();
	// The server's rule (CharacterRules): 3-16 letters, digits or _; checked here first for a quick answer
	bool bValid = Name.Len() >= 3 && Name.Len() <= 16;
	for (const TCHAR C : Name)
	{
		bValid &= FChar::IsAlnum(C) || C == TEXT('_');
	}
	if (!bValid)
	{
		SetError(LOCTEXT("BadName", "Imię musi mieć 3–16 znaków: litery, cyfry albo _."));
		return;
	}
	TSharedPtr<FJsonObject> Appearance = MakeShared<FJsonObject>();
	for (const FRow& Row : Rows)
	{
		Appearance->SetNumberField(Row.Key, Values[Row.Key]);
	}
	SetError(FText::GetEmpty());
	OnCreate.ExecuteIfBound(Name, Appearance);
}

void SFWCharacterCreator::SetBusy(bool bInBusy)
{
	bBusy = bInBusy;
}

void SFWCharacterCreator::SetError(const FText& Text)
{
	ErrorText->SetText(Text);
}

TSharedPtr<SWidget> SFWCharacterCreator::GetFocusTarget() const
{
	return NameBox;
}

FReply SFWCharacterCreator::OnMouseButtonDown(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (Event.GetEffectingButton() == EKeys::LeftMouseButton)
	{
		bDragging = true;
		return FReply::Handled().CaptureMouse(SharedThis(this));
	}
	return FReply::Unhandled();
}

FReply SFWCharacterCreator::OnMouseButtonUp(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (bDragging && Event.GetEffectingButton() == EKeys::LeftMouseButton)
	{
		bDragging = false;
		return FReply::Handled().ReleaseMouseCapture();
	}
	return FReply::Unhandled();
}

FReply SFWCharacterCreator::OnMouseMove(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (bDragging)
	{
		if (AFWCharacterStudio* Target = Studio.Get())
		{
			Target->Spin(Event.GetCursorDelta().X * 0.45f);
		}
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

FReply SFWCharacterCreator::OnMouseWheel(const FGeometry& Geometry, const FPointerEvent& Event)
{
	if (AFWCharacterStudio* Target = Studio.Get())
	{
		Target->Zoom(Event.GetWheelDelta() * 0.2f);
	}
	return FReply::Handled();
}

#undef LOCTEXT_NAMESPACE
