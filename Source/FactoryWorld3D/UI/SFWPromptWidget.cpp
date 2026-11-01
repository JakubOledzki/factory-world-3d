#include "UI/SFWPromptWidget.h"

#include "Styling/CoreStyle.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

void SFWPromptWidget::Construct(const FArguments& InArgs)
{
	OnAccepted = InArgs._OnAccepted;
	const FSlateBrush* White = FCoreStyle::Get().GetBrush("WhiteBrush");

	TSharedRef<SVerticalBox> Box = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
		[
			SNew(STextBlock)
			.Text(InArgs._Title)
			.Font(FCoreStyle::GetDefaultFontStyle("Bold", 22))
			.ColorAndOpacity(FLinearColor(1.f, 0.62f, 0.15f))
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 16)
		[
			SAssignNew(MessageText, STextBlock)
			.Text(InArgs._Message)
			.Font(FCoreStyle::GetDefaultFontStyle("Regular", 12))
			.AutoWrapText(true)
			.ColorAndOpacity(FLinearColor(0.8f, 0.82f, 0.85f))
		];

	if (InArgs._ShowInput)
	{
		Box->AddSlot().AutoHeight().Padding(0, 0, 0, 16)
		[
			SAssignNew(Input, SEditableTextBox)
			.Font(FCoreStyle::GetDefaultFontStyle("Regular", 14))
			.HintText(InArgs._InputHint)
			.OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type Commit)
			{
				if (Commit == ETextCommit::OnEnter)
				{
					Accept();
				}
			})
		];
	}

	Box->AddSlot().AutoHeight()
	[
		SNew(SButton)
		.HAlign(HAlign_Center)
		.ContentPadding(FMargin(0, 8))
		.IsEnabled_Lambda([this]() { return !bBusy; })
		.OnClicked(this, &SFWPromptWidget::Accept)
		[
			SNew(STextBlock).Text(InArgs._ButtonText).Font(FCoreStyle::GetDefaultFontStyle("Bold", 13))
		]
	];

	if (!InArgs._SecondaryText.IsEmpty())
	{
		OnSecondary = InArgs._OnSecondary;
		Box->AddSlot().AutoHeight().Padding(0, 8, 0, 0)
		[
			SNew(SButton)
			.HAlign(HAlign_Center)
			.ContentPadding(FMargin(0, 6))
			.OnClicked_Lambda([this]() { OnSecondary.ExecuteIfBound(); return FReply::Handled(); })
			[
				SNew(STextBlock).Text(InArgs._SecondaryText).Font(FCoreStyle::GetDefaultFontStyle("Regular", 12))
			]
		];
	}

	ChildSlot
	[
		SNew(SBorder)
		.BorderImage(White)
		.BorderBackgroundColor(FLinearColor(0.f, 0.f, 0.f, 0.35f))
		.HAlign(HAlign_Center)
		.VAlign(VAlign_Center)
		[
			SNew(SBox)
			.WidthOverride(460.f)
			[
				SNew(SBorder)
				.BorderImage(White)
				.BorderBackgroundColor(FLinearColor(0.02f, 0.025f, 0.03f, 0.9f))
				.Padding(32.f)
				[
					Box
				]
			]
		]
	];
}

void SFWPromptWidget::SetMessage(const FText& Text, bool bError)
{
	MessageText->SetText(Text);
	MessageText->SetColorAndOpacity(bError ? FLinearColor(1.f, 0.35f, 0.3f) : FLinearColor(0.8f, 0.82f, 0.85f));
}

TSharedPtr<SWidget> SFWPromptWidget::GetFocusTarget() const
{
	return Input.IsValid() ? StaticCastSharedPtr<SWidget>(Input) : nullptr;
}

FReply SFWPromptWidget::Accept()
{
	if (!bBusy)
	{
		OnAccepted.ExecuteIfBound(Input.IsValid() ? Input->GetText().ToString().TrimStartAndEnd() : FString());
	}
	return FReply::Handled();
}

void SFWPromptWidget::AcceptWith(const FString& Text)
{
	if (Input.IsValid())
	{
		Input->SetText(FText::FromString(Text));
	}
	Accept();
}
