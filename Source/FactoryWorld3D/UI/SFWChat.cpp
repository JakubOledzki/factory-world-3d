#include "UI/SFWChat.h"

#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "World/FWPlot.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWChat"

namespace FWChatLook
{
	constexpr double PollSeconds = 2.0;
	// How long a line stays in the corner while the chat is closed, and how many show there
	constexpr double FadeAfter = 12.0;
	constexpr int32 ShownClosed = 5;
	constexpr int32 Kept = 60;
	const FLinearColor World = FLinearColor::FromSRGBColor(FColor(0x9c, 0xc4, 0xdc));
	const FLinearColor Plot = FLinearColor::FromSRGBColor(FColor(0x8f, 0xd6, 0x5a));
	// Who the server's own announcements come from (no player)
	const TCHAR* const Server = TEXT("00000000-0000-0000-0000-000000000000");
}

void SFWChat::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	auto ChannelButton = [this](const FString& Id, const FText& Text, const FText& Tip)
	{
		return SNew(SBox).ToolTipText(Tip)
			[
				FWUi::TabButton(Text, [this, Id]() { return Channel == Id; }, [this, Id]() { SetChannel(Id); })
			];
	};
	TSharedRef<SWrapBox> Phrases = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(4.f, 4.f));
	for (const TCHAR* Phrase : { TEXT("Cześć!"), TEXT("Dzięki!"), TEXT("Super fabryka!"), TEXT("Pomóc?"), TEXT("Chodź tutaj!") })
	{
		const FString Said = Phrase;
		Phrases->AddSlot()[FWUi::SmallButton(FText::FromString(Said), [this, Said]() { Say(Said); })];
	}
	ChildSlot
	.HAlign(HAlign_Left)
	.VAlign(VAlign_Bottom)
	.Padding(FMargin(24.f, 0.f, 0.f, 150.f))
	[
		SNew(SBox).WidthOverride(440.f)
		[
			SNew(SBorder)
			.BorderImage_Lambda([this]() { return bOpen ? FWStyle::Panel() : FStyleDefaults::GetNoBrush(); })
			.Padding_Lambda([this]() { return bOpen ? FMargin(12.f) : FMargin(0.f); })
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(SBox).MaxDesiredHeight_Lambda([this]() { return bOpen ? FOptionalSize(260.f) : FOptionalSize(); })
					[
						SAssignNew(Scroll, SScrollBox).ScrollBarVisibility(EVisibility::Collapsed)
						+ SScrollBox::Slot()[SAssignNew(List, SVerticalBox)]
					]
				]
				+ SVerticalBox::Slot().AutoHeight().Padding(0, 8, 0, 0)
				[
					SAssignNew(OpenPart, SVerticalBox).Visibility_Lambda([this]() { return bOpen ? EVisibility::Visible : EVisibility::Collapsed; })
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().AutoWidth().Padding(0, 0, 6, 0)
						[
							ChannelButton(TEXT("plot"), LOCTEXT("Plot", "Działka"), LOCTEXT("PlotTip", "Piszesz do graczy na tej działce"))
						]
						+ SHorizontalBox::Slot().AutoWidth()
						[
							ChannelButton(TEXT("world"), LOCTEXT("World", "Świat"), LOCTEXT("WorldTip", "Piszesz do wszystkich w grze"))
						]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)
						[
							SAssignNew(Input, SEditableTextBox)
							.Style(&FWStyle::TextBox())
							.HintText(LOCTEXT("Hint", "Napisz wiadomość i naciśnij Enter"))
							.OnTextCommitted_Lambda([this](const FText& Text, ETextCommit::Type How)
							{
								// Esc in the line closes the chat; leaving it otherwise keeps it open
								if (How == ETextCommit::OnCleared)
								{
									if (AFWPlayerController* PC = Controller.Get())
									{
										PC->CloseChat();
									}
									return;
								}
								if (How != ETextCommit::OnEnter)
								{
									return;
								}
								const FString Said = Text.ToString().TrimStartAndEnd();
								Input->SetText(FText::GetEmpty());
								if (Said.IsEmpty())
								{
									if (AFWPlayerController* PC = Controller.Get())
									{
										PC->CloseChat();
									}
									return;
								}
								Say(Said);
								// Enter leaves the line: give the keyboard back to it for the next message
								FSlateApplication::Get().SetKeyboardFocus(Input, EFocusCause::SetDirectly);
							})
						]
						+ SHorizontalBox::Slot().AutoWidth()
						[
							FWUi::SmallButton(LOCTEXT("Send", "Wyślij"), [this]()
							{
								const FString Said = Input->GetText().ToString().TrimStartAndEnd();
								Input->SetText(FText::GetEmpty());
								if (!Said.IsEmpty())
								{
									Say(Said);
								}
							}, true)
						]
					]
					+ SVerticalBox::Slot().AutoHeight()[Phrases]
				]
			]
		]
	];
	Rebuild();
}

TSharedPtr<SWidget> SFWChat::GetInput() const
{
	return Input;
}

void SFWChat::SetOpen(bool bInOpen)
{
	bOpen = bInOpen;
	Scroll->SetScrollBarVisibility(bOpen ? EVisibility::Visible : EVisibility::Collapsed);
	Rebuild();
}

void SFWChat::SetChannel(const FString& InChannel)
{
	Channel = InChannel;
}

void SFWChat::Say(const FString& Text)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetStringField(TEXT("channel"), Channel);
	P->SetStringField(TEXT("text"), Text.Left(200));
	if (Channel == TEXT("plot") && PC->GetPlot())
	{
		P->SetNumberField(TEXT("x"), PC->GetPlot()->GetPlotX());
		P->SetNumberField(TEXT("y"), PC->GetPlot()->GetPlotY());
	}
	TWeakPtr<SFWChat> WeakThis = SharedThis(this);
	PC->Send(TEXT("chat.send"), P, [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWChat> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		if (Reply.Type != TEXT("chat.sent"))
		{
			if (AFWPlayerController* Inner = This->Controller.Get())
			{
				Inner->ToastError(Reply);
			}
			return;
		}
		// Show it at once
		This->NextPoll = 0.0;
	});
}

void SFWChat::Poll()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !PC->GetPlot())
	{
		return;
	}
	bPolling = true;
	NextPoll = FPlatformTime::Seconds() + FWChatLook::PollSeconds;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("after"), double(FMath::Max<int64>(Last, 0)));
	P->SetNumberField(TEXT("x"), PC->GetPlot()->GetPlotX());
	P->SetNumberField(TEXT("y"), PC->GetPlot()->GetPlotY());
	TWeakPtr<SFWChat> WeakThis = SharedThis(this);
	auto Handle = [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWChat> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->bPolling = false;
		if (Reply.Type != TEXT("chat"))
		{
			return;
		}
		const bool bFirst = This->Last < 0;
		This->Last = int64(FWUi::Num(Reply.Payload, TEXT("last")));
		const TArray<TSharedPtr<FJsonObject>> Messages = FWUi::Objects(Reply.Payload, TEXT("messages"));
		if (Messages.Num() == 0)
		{
			return;
		}
		for (const TSharedPtr<FJsonObject>& Message : Messages)
		{
			// What was said before the player came is history, not news
			This->Lines.Add({ Message, bFirst ? 0.0 : FPlatformTime::Seconds() });
		}
		if (This->Lines.Num() > FWChatLook::Kept)
		{
			This->Lines.RemoveAt(0, This->Lines.Num() - FWChatLook::Kept);
		}
		This->Rebuild();
	};
	PC->Send(TEXT("chat.get"), P, [WeakThis, Handle](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWChat> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (Inner && Reply.IsError())
		{
			// No longer allowed on the plot (made private): everybody's chat still comes
			TSharedPtr<FJsonObject> Again = MakeShared<FJsonObject>();
			Again->SetNumberField(TEXT("after"), double(FMath::Max<int64>(This->Last, 0)));
			Inner->Send(TEXT("chat.get"), Again, Handle);
			return;
		}
		Handle(Reply);
	});
}

void SFWChat::Rebuild()
{
	if (!List.IsValid())
	{
		return;
	}
	List->ClearChildren();
	const int32 First = bOpen ? 0 : FMath::Max(0, Lines.Num() - FWChatLook::ShownClosed);
	for (int32 i = First; i < Lines.Num(); ++i)
	{
		const TSharedPtr<FJsonObject>& Message = Lines[i].Message;
		const double At = Lines[i].At;
		const bool bWorld = FWUi::Str(Message, TEXT("channel")) == TEXT("world");
		const bool bServer = FWUi::Str(Message, TEXT("from")) == FWChatLook::Server;
		FString Text = FWUi::Str(Message, TEXT("text"));
		if (FWUi::Obj(Message, TEXT("ping")).IsValid())
		{
			Text = TEXT("wskazuje miejsce na działce");
		}
		// Lines fade out of the corner while the chat is closed
		auto Fade = [this, At]()
		{
			const float Alpha = bOpen ? 1.f : FMath::Clamp(float(FWChatLook::FadeAfter - (FPlatformTime::Seconds() - At)), 0.f, 1.f);
			return FLinearColor(1.f, 1.f, 1.f, Alpha);
		};
		auto Shadowed = [](const FText& Value, const FSlateFontInfo& Font, const FLinearColor& Color)
		{
			return SNew(STextBlock).Text(Value).Font(Font).ColorAndOpacity(Color).AutoWrapText(true)
				.ShadowOffset(FVector2D(1.f, 1.f)).ShadowColorAndOpacity(FLinearColor(0.f, 0.f, 0.f, 0.85f));
		};
		TSharedRef<SHorizontalBox> Row = SNew(SHorizontalBox);
		if (bServer)
		{
			Row->AddSlot().FillWidth(1.f)
			[
				Shadowed(FText::Format(LOCTEXT("ServerLine", "{0}: {1}"), FText::FromString(FWUi::Str(Message, TEXT("name"))), FText::FromString(Text)),
					FWStyle::Font(12, TEXT("Bold")), FWStyle::Accent)
			];
		}
		else
		{
			Row->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
			[
				Shadowed(bWorld ? LOCTEXT("WorldTag", "[Świat]") : LOCTEXT("PlotTag", "[Działka]"), FWStyle::Font(12), bWorld ? FWChatLook::World : FWChatLook::Plot)
			];
			Row->AddSlot().AutoWidth().Padding(0, 0, 6, 0)
			[
				Shadowed(FText::Format(LOCTEXT("Name", "{0}:"), FText::FromString(FWUi::Str(Message, TEXT("name")))), FWStyle::Font(12, TEXT("Bold")), FWStyle::Text)
			];
			Row->AddSlot().FillWidth(1.f)[Shadowed(FText::FromString(Text), FWStyle::Font(12), FWStyle::Text)];
		}
		List->AddSlot().AutoHeight().Padding(0, 1)
		[
			SNew(SBorder).BorderImage(FStyleDefaults::GetNoBrush()).Padding(0.f).ColorAndOpacity_Lambda(Fade)[Row]
		];
	}
	if (bOpen && Scroll.IsValid())
	{
		Scroll->ScrollToEnd();
	}
}

void SFWChat::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (!bPolling && FPlatformTime::Seconds() >= NextPoll)
	{
		Poll();
	}
}

FReply SFWChat::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape && bOpen)
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->CloseChat();
		}
		return FReply::Handled();
	}
	return FReply::Unhandled();
}

#undef LOCTEXT_NAMESPACE
