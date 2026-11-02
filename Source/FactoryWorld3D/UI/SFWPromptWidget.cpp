#include "UI/SFWPromptWidget.h"

#include "Styling/CoreStyle.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/SOverlay.h"
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
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)
			[
				SNew(SBox).WidthOverride(4.f).HeightOverride(24.f)[SNew(SImage).Image(FWStyle::Swatch()).ColorAndOpacity(FWStyle::Accent)]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock)
				.Text(InArgs._Title.ToUpper())
				.Font(FWStyle::Display(28))
				.ColorAndOpacity(FWStyle::Text)
			]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 16)
		[
			SAssignNew(MessageText, STextBlock)
			.Text(InArgs._Message)
			.Font(FWStyle::Font(14))
			.AutoWrapText(true)
			.ColorAndOpacity(FWStyle::Text)
		];

	if (InArgs._ShowInput)
	{
		Box->AddSlot().AutoHeight().Padding(0, 0, 0, 16)
		[
			SAssignNew(Input, SEditableTextBox)
			.Style(&FWStyle::TextBox())
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
		.ButtonStyle(&FWStyle::PrimaryButton())
		.HAlign(HAlign_Center)
		.ContentPadding(FMargin(0, 4))
		.IsEnabled_Lambda([this]() { return !bBusy; })
		.OnClicked(this, &SFWPromptWidget::Accept)
		[
			SNew(STextBlock).Text(InArgs._ButtonText).Font(FWStyle::Font(15, TEXT("Bold"))).ColorAndOpacity(FLinearColor::White)
		]
	];

	if (!InArgs._SecondaryText.IsEmpty())
	{
		OnSecondary = InArgs._OnSecondary;
		Box->AddSlot().AutoHeight().Padding(0, 8, 0, 0)
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.HAlign(HAlign_Center)
			.ContentPadding(FMargin(0, 3))
			.OnClicked_Lambda([this]() { OnSecondary.ExecuteIfBound(); return FReply::Handled(); })
			[
				SNew(STextBlock).Text(InArgs._SecondaryText).Font(FWStyle::Font(14, TEXT("Medium"))).ColorAndOpacity(FWStyle::Text)
			]
		];
	}

	// A glass panel over the softened, darkened world, fading in
	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot()[SNew(SBackgroundBlur).BlurStrength(4.f)]
		+ SOverlay::Slot()[SNew(SBorder).BorderImage(White).BorderBackgroundColor(FLinearColor(0.f, 0.f, 0.f, 0.4f))]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			FWUi::Appear(
			SNew(SBox)
			.WidthOverride(500.f)
			[
				SNew(SBorder)
				.BorderImage(FWStyle::Panel())
				.Padding(FMargin(32.f, 26.f))
				[
					Box
				]
			])
		]
	];
}

void SFWPromptWidget::SetMessage(const FText& Text, bool bError)
{
	MessageText->SetText(Text);
	MessageText->SetColorAndOpacity(bError ? FWStyle::Error : FWStyle::Text);
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
