#include "UI/FWForm.h"

#include "FWPlayerController.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Input/SCheckBox.h"
#include "Widgets/Input/SComboBox.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

TSharedRef<SWidget> FFWForm::Field(const FString& Key, const FText& Hint, float Width, const FString& Default)
{
	if (!Values.Contains(Key))
	{
		Values.Add(Key, Default);
	}
	TWeakPtr<FFWForm> WeakThis = AsShared();
	return SNew(SBox).WidthOverride(Width)
		[
			SNew(SEditableTextBox).Style(&FWStyle::TextBox()).HintText(Hint).Text(FText::FromString(Values[Key]))
			.OnTextChanged_Lambda([WeakThis, Key](const FText& Text) { if (TSharedPtr<FFWForm> This = WeakThis.Pin()) { This->Values.Add(Key, Text.ToString()); } })
		];
}

TSharedRef<SWidget> FFWForm::Pick(const FString& Key, const TArray<TPair<FString, FText>>& InOptions, float Width, TFunction<void(const FString&)> OnPicked,
	AFWPlayerController* IconsFrom)
{
	TArray<TSharedPtr<FString>>& Ids = Options.FindOrAdd(Key);
	TMap<FString, FText>& Labels = Names.FindOrAdd(Key);
	Ids.Reset();
	Labels.Reset();
	for (const TPair<FString, FText>& Option : InOptions)
	{
		Ids.Add(MakeShared<FString>(Option.Key));
		Labels.Add(Option.Key, Option.Value);
	}
	if (!Values.Contains(Key) && InOptions.Num() > 0)
	{
		Values.Add(Key, InOptions[0].Key);
	}
	TSharedPtr<FString> Chosen;
	for (const TSharedPtr<FString>& Id : Ids)
	{
		Chosen = *Id == Values.FindRef(Key) ? Id : Chosen;
	}
	TWeakPtr<FFWForm> WeakThis = AsShared();
	TWeakObjectPtr<AFWPlayerController> Icons(IconsFrom);
	return SNew(SBox).WidthOverride(Width)
		[
			SNew(SComboBox<TSharedPtr<FString>>)
			.OptionsSource(&Ids)
			.InitiallySelectedItem(Chosen)
			.MaxListHeight(380.f)
			.OnGenerateWidget_Lambda([WeakThis, Key, Icons](TSharedPtr<FString> Id)
			{
				TSharedPtr<FFWForm> This = WeakThis.Pin();
				return SNew(SHorizontalBox)
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, Icons.IsValid() ? 6.f : 0.f, 0)
					[
						Icons.IsValid() ? FWUi::Icon(Icons.Get(), *Id, 20.f) : SNullWidget::NullWidget
					]
					+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
					[
						SNew(STextBlock).Font(FWStyle::Font(11)).Text(This ? This->Names.FindOrAdd(Key).FindRef(*Id) : FText::FromString(*Id))
					];
			})
			.OnSelectionChanged_Lambda([WeakThis, Key, OnPicked](TSharedPtr<FString> Id, ESelectInfo::Type How)
			{
				TSharedPtr<FFWForm> This = WeakThis.Pin();
				if (!This || !Id.IsValid())
				{
					return;
				}
				This->Values.Add(Key, *Id);
				if (How != ESelectInfo::Direct && OnPicked)
				{
					OnPicked(*Id);
				}
			})
			[
				SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
				.Text_Lambda([WeakThis, Key]()
				{
					TSharedPtr<FFWForm> This = WeakThis.Pin();
					return This ? This->Names.FindOrAdd(Key).FindRef(This->Values.FindRef(Key)) : FText::GetEmpty();
				})
			]
		];
}

TSharedRef<SWidget> FFWForm::Check(const FString& Key, const FText& Text, bool bDefault)
{
	if (!Values.Contains(Key))
	{
		Values.Add(Key, bDefault ? TEXT("1") : TEXT("0"));
	}
	TWeakPtr<FFWForm> WeakThis = AsShared();
	return SNew(SCheckBox)
		.IsChecked_Lambda([WeakThis, Key]() { TSharedPtr<FFWForm> This = WeakThis.Pin(); return This && This->Ticked(Key) ? ECheckBoxState::Checked : ECheckBoxState::Unchecked; })
		.OnCheckStateChanged_Lambda([WeakThis, Key](ECheckBoxState State) { if (TSharedPtr<FFWForm> This = WeakThis.Pin()) { This->Values.Add(Key, State == ECheckBoxState::Checked ? TEXT("1") : TEXT("0")); } })
		[
			SNew(STextBlock).Text(Text).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
		];
}

FString FFWForm::Get(const FString& Key) const
{
	const FString* Found = Values.Find(Key);
	return Found ? Found->TrimStartAndEnd() : FString();
}

double FFWForm::Number(const FString& Key) const
{
	return FCString::Atod(*Get(Key).Replace(TEXT(","), TEXT(".")));
}
