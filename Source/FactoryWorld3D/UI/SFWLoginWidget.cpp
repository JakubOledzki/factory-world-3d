#include "UI/SFWLoginWidget.h"

#include "Net/FWNetSubsystem.h"
#include "Styling/CoreStyle.h"
#include "UI/FWStyle.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWLogin"

namespace
{
	FText DescribeError(const FFWNetReply& Reply)
	{
		const FString Code = Reply.ErrorCode();
		if (Code == TEXT("invalid_credentials"))
		{
			return LOCTEXT("BadCredentials", "Nieprawidłowy e-mail lub hasło.");
		}
		if (Code == TEXT("invalid_email"))
		{
			return LOCTEXT("InvalidEmail", "Nieprawidłowy adres e-mail.");
		}
		if (Code == TEXT("weak_password"))
		{
			return LOCTEXT("WeakPassword", "Hasło jest za słabe.");
		}
		if (Code == TEXT("too_many_attempts"))
		{
			return LOCTEXT("TooMany", "Za dużo nieudanych prób. Spróbuj ponownie później.");
		}
		if (Code == TEXT("email_taken"))
		{
			return LOCTEXT("EmailTaken", "To konto już istnieje. Zaloguj się.");
		}
		if (Code == TEXT("not_connected") || Code == TEXT("disconnected"))
		{
			return LOCTEXT("NoServer", "Brak połączenia z serwerem.");
		}
		const FString Message = Reply.ErrorMessage();
		return FText::FromString(Message.IsEmpty() ? Code : Message);
	}
}

void SFWLoginWidget::Construct(const FArguments& InArgs)
{
	Net = InArgs._Net;
	OnFinished = InArgs._OnFinished;
	AutoEmail = InArgs._AutoEmail;
	AutoPassword = InArgs._AutoPassword;

	ChildSlot
	[
		SNew(SOverlay)
		// The world behind, softened, so the form reads first
		+ SOverlay::Slot()
		[
			SNew(SBackgroundBlur).BlurStrength(6.f)
		]
		+ SOverlay::Slot()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(SBorder)
				.BorderImage(FWStyle::SideShade())
				.Padding(FMargin(96.f, 0.f, 72.f, 0.f))
				.VAlign(VAlign_Center)
				[
					SNew(SBox)
					.WidthOverride(400.f)
					[
						SNew(SVerticalBox)
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(STextBlock)
							.Text(LOCTEXT("Title", "FACTORY WORLD"))
							.Font(FWStyle::Font(40, TEXT("Bold")))
							.ColorAndOpacity(FWStyle::Accent)
						]
						+ SVerticalBox::Slot().AutoHeight().Padding(0, 2, 0, 36)
						[
							SNew(STextBlock)
							.Text(LOCTEXT("Subtitle", "Zbuduj fabrykę w Tatrach"))
							.Font(FWStyle::Font(14))
							.ColorAndOpacity(FWStyle::Muted)
						]
						+ SVerticalBox::Slot().AutoHeight()
						[
							SNew(SBorder)
							.BorderImage(FWStyle::Panel())
							.Padding(28.f)
							[
								SNew(SVerticalBox)
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 18)
								[
									SNew(STextBlock)
									.Text(LOCTEXT("Heading", "Zaloguj się"))
									.Font(FWStyle::Font(20, TEXT("Bold")))
									.ColorAndOpacity(FWStyle::Text)
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)
								[
									SNew(STextBlock).Text(LOCTEXT("Email", "E-mail")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 14)
								[
									SAssignNew(EmailBox, SEditableTextBox)
									.Style(&FWStyle::TextBox())
									.HintText(LOCTEXT("EmailHint", "ty@example.com"))
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)
								[
									SNew(STextBlock).Text(LOCTEXT("Password", "Hasło")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 22)
								[
									SAssignNew(PasswordBox, SEditableTextBox)
									.Style(&FWStyle::TextBox())
									.IsPassword(true)
									.OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type Commit)
									{
										if (Commit == ETextCommit::OnEnter && CanSubmit())
										{
											Submit(false);
										}
									})
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
								[
									SNew(SButton)
									.ButtonStyle(&FWStyle::PrimaryButton())
									.HAlign(HAlign_Center)
									.IsEnabled(this, &SFWLoginWidget::CanSubmit)
									.OnClicked(this, &SFWLoginWidget::OnLoginClicked)
									[
										SNew(STextBlock).Text(LOCTEXT("Login", "Zaloguj się")).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FLinearColor::White)
									]
								]
								+ SVerticalBox::Slot().AutoHeight()
								[
									SNew(SButton)
									.ButtonStyle(&FWStyle::SecondaryButton())
									.HAlign(HAlign_Center)
									.IsEnabled(this, &SFWLoginWidget::CanSubmit)
									.OnClicked(this, &SFWLoginWidget::OnRegisterClicked)
									[
										SNew(STextBlock).Text(LOCTEXT("Register", "Załóż nowe konto")).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
									]
								]
								+ SVerticalBox::Slot().AutoHeight().Padding(0, 16, 0, 0)
								[
									SAssignNew(StatusText, STextBlock)
									.Font(FWStyle::Font(11))
									.AutoWrapText(true)
								]
							]
						]
						+ SVerticalBox::Slot().AutoHeight().Padding(0, 18, 0, 0)
						[
							SNew(STextBlock)
							.Text(LOCTEXT("Shared", "To samo konto co w Factory World 2D."))
							.Font(FWStyle::Font(10))
							.ColorAndOpacity(FWStyle::Muted)
						]
					]
				]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
		]
	];

	if (UFWNetSubsystem* Subsystem = Net.Get())
	{
		StateHandle = Subsystem->OnStateChanged.AddSP(this, &SFWLoginWidget::HandleStateChanged);
		HandleStateChanged(Subsystem->GetState());
		Subsystem->Connect();
	}
}

SFWLoginWidget::~SFWLoginWidget()
{
	if (UFWNetSubsystem* Subsystem = Net.Get())
	{
		Subsystem->OnStateChanged.Remove(StateHandle);
	}
}

bool SFWLoginWidget::CanSubmit() const
{
	const UFWNetSubsystem* Subsystem = Net.Get();
	return !bBusy && Subsystem && Subsystem->GetState() == EFWNetState::Ready;
}

FReply SFWLoginWidget::OnLoginClicked()
{
	Submit(false);
	return FReply::Handled();
}

FReply SFWLoginWidget::OnRegisterClicked()
{
	Submit(true);
	return FReply::Handled();
}

void SFWLoginWidget::Submit(bool bRegister)
{
	UFWNetSubsystem* Subsystem = Net.Get();
	if (!Subsystem)
	{
		return;
	}
	const FString Email = EmailBox->GetText().ToString().TrimStartAndEnd();
	const FString Password = PasswordBox->GetText().ToString();
	if (Email.IsEmpty() || Password.IsEmpty())
	{
		SetStatus(LOCTEXT("Missing", "Podaj e-mail i hasło."), true);
		return;
	}

	bBusy = true;
	SetStatus(bRegister ? LOCTEXT("Registering", "Zakładanie konta…") : LOCTEXT("LoggingIn", "Logowanie…"));
	TWeakPtr<SFWLoginWidget> WeakThis = SharedThis(this);
	FFWNetCallback Done = [WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWLoginWidget> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->bBusy = false;
		if (Reply.Type == TEXT("auth.ok"))
		{
			This->AfterAuth();
		}
		else
		{
			This->SetStatus(DescribeError(Reply), true);
		}
	};
	if (bRegister)
	{
		Subsystem->Register(Email, Password, MoveTemp(Done));
	}
	else
	{
		Subsystem->Login(Email, Password, MoveTemp(Done));
	}
}

void SFWLoginWidget::HandleStateChanged(EFWNetState State)
{
	switch (State)
	{
	case EFWNetState::Disconnected:
		SetStatus(LOCTEXT("Offline", "Brak połączenia z serwerem."), true);
		break;
	case EFWNetState::Connecting:
	case EFWNetState::Handshaking:
		SetStatus(LOCTEXT("Connecting", "Łączenie z serwerem…"));
		break;
	case EFWNetState::Ready:
	{
		UFWNetSubsystem* Subsystem = Net.Get();
		if (Subsystem && !AutoEmail.IsEmpty() && !bBusy)
		{
			// Development auto-login (Saved/DevLogin.ini): straight in, no form
			EmailBox->SetText(FText::FromString(AutoEmail));
			PasswordBox->SetText(FText::FromString(AutoPassword));
			AutoEmail.Reset();
			bTriedResume = true;
			Submit(false);
		}
		else if (Subsystem && !bTriedResume && Subsystem->HasSavedToken())
		{
			// Log back in with the saved session before asking for a password
			bTriedResume = true;
			bBusy = true;
			SetStatus(LOCTEXT("Resuming", "Wznawianie sesji…"));
			TWeakPtr<SFWLoginWidget> WeakThis = SharedThis(this);
			Subsystem->Resume([WeakThis](const FFWNetReply& Reply)
			{
				if (TSharedPtr<SFWLoginWidget> This = WeakThis.Pin())
				{
					This->bBusy = false;
					const UFWNetSubsystem* Inner = This->Net.Get();
					if (Reply.Type == TEXT("auth.ok") || (Inner && Inner->GetState() == EFWNetState::Authenticated))
					{
						This->AfterAuth();
					}
					else
					{
						This->SetStatus(LOCTEXT("ResumeFailed", "Sesja wygasła. Zaloguj się ponownie."));
					}
				}
			});
		}
		else if (!bBusy)
		{
			SetStatus(LOCTEXT("Connected", "Połączono z serwerem."));
		}
		break;
	}
	case EFWNetState::Authenticated:
		// Logged in from elsewhere (e.g. the fw.Login console command); our own requests handle it in their callbacks
		if (!bBusy)
		{
			AfterAuth();
		}
		break;
	}
}

void SFWLoginWidget::AfterAuth()
{
	UFWNetSubsystem* Subsystem = Net.Get();
	if (!Subsystem)
	{
		return;
	}
	SetStatus(LOCTEXT("LoadingCharacter", "Wczytywanie postaci…"));
	bBusy = true;
	TWeakPtr<SFWLoginWidget> WeakThis = SharedThis(this);
	Subsystem->GetCharacter([WeakThis](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWLoginWidget> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->bBusy = false;
		if (Reply.Type == TEXT("character"))
		{
			This->OnFinished.ExecuteIfBound();
		}
		else if (Reply.Type == TEXT("character.none"))
		{
			// The game asks for a name next
			This->OnFinished.ExecuteIfBound();
		}
		else
		{
			This->SetStatus(DescribeError(Reply), true);
		}
	});
}

void SFWLoginWidget::SetStatus(const FText& Text, bool bError)
{
	if (StatusText.IsValid())
	{
		StatusText->SetText(Text);
		StatusText->SetColorAndOpacity(bError ? FWStyle::Error : FWStyle::Muted);
	}
}


TSharedPtr<SWidget> SFWLoginWidget::GetFocusTarget() const
{
	return EmailBox;
}

#undef LOCTEXT_NAMESPACE
