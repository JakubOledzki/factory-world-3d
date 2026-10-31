#include "UI/SFWLoginWidget.h"

#include "Net/FWNetSubsystem.h"
#include "Styling/CoreStyle.h"
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
	FSlateFontInfo Font(int32 Size, const FName Typeface = TEXT("Regular"))
	{
		return FCoreStyle::GetDefaultFontStyle(Typeface, Size);
	}

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

	ChildSlot
	[
		SNew(SBorder)
		.BorderImage(FCoreStyle::Get().GetBrush("WhiteBrush"))
		.BorderBackgroundColor(FLinearColor(0.f, 0.f, 0.f, 0.35f))
		.HAlign(HAlign_Center)
		.VAlign(VAlign_Center)
		[
			SNew(SBox)
			.WidthOverride(420.f)
			[
				SNew(SBorder)
				.BorderImage(FCoreStyle::Get().GetBrush("WhiteBrush"))
				.BorderBackgroundColor(FLinearColor(0.02f, 0.025f, 0.03f, 0.88f))
				.Padding(32.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 4)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("Title", "FACTORY WORLD"))
						.Font(Font(28, TEXT("Bold")))
						.ColorAndOpacity(FLinearColor(1.f, 0.62f, 0.15f))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 24)
					[
						SNew(STextBlock)
						.Text(LOCTEXT("Subtitle", "Zaloguj się kontem z gry Factory World"))
						.Font(Font(11))
						.ColorAndOpacity(FLinearColor(0.7f, 0.72f, 0.75f))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 4)
					[
						SNew(STextBlock).Text(LOCTEXT("Email", "E-mail")).Font(Font(11))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 12)
					[
						SAssignNew(EmailBox, SEditableTextBox)
						.Font(Font(13))
						.HintText(LOCTEXT("EmailHint", "ty@example.com"))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 4)
					[
						SNew(STextBlock).Text(LOCTEXT("Password", "Hasło")).Font(Font(11))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 20)
					[
						SAssignNew(PasswordBox, SEditableTextBox)
						.Font(Font(13))
						.IsPassword(true)
						.OnTextCommitted_Lambda([this](const FText&, ETextCommit::Type Commit)
						{
							if (Commit == ETextCommit::OnEnter && CanSubmit())
							{
								Submit(false);
							}
						})
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 8)
					[
						SNew(SHorizontalBox)
						+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)
						[
							SNew(SButton)
							.HAlign(HAlign_Center)
							.ContentPadding(FMargin(0, 8))
							.IsEnabled(this, &SFWLoginWidget::CanSubmit)
							.OnClicked(this, &SFWLoginWidget::OnLoginClicked)
							[
								SNew(STextBlock).Text(LOCTEXT("Login", "Zaloguj")).Font(Font(13, TEXT("Bold")))
							]
						]
						+ SHorizontalBox::Slot().FillWidth(1.f).Padding(6, 0, 0, 0)
						[
							SNew(SButton)
							.HAlign(HAlign_Center)
							.ContentPadding(FMargin(0, 8))
							.IsEnabled(this, &SFWLoginWidget::CanSubmit)
							.OnClicked(this, &SFWLoginWidget::OnRegisterClicked)
							[
								SNew(STextBlock).Text(LOCTEXT("Register", "Załóż konto")).Font(Font(13))
							]
						]
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 8, 0, 0)
					[
						SAssignNew(StatusText, STextBlock)
						.Font(Font(11))
						.AutoWrapText(true)
					]
				]
			]
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
		if (Subsystem && !bTriedResume && Subsystem->HasSavedToken())
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
			// Log out so the form can be used with another account
			if (UFWNetSubsystem* Subsystem = This->Net.Get())
			{
				Subsystem->Logout();
			}
			This->SetStatus(LOCTEXT("NoCharacter", "To konto nie ma jeszcze postaci. Utwórz ją w grze Factory World."), true);
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
		StatusText->SetColorAndOpacity(bError ? FLinearColor(1.f, 0.35f, 0.3f) : FLinearColor(0.75f, 0.85f, 0.95f));
	}
}

#undef LOCTEXT_NAMESPACE
