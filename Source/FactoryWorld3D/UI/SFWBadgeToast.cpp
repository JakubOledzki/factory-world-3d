#include "UI/SFWBadgeToast.h"

#include "Engine/GameInstance.h"
#include "FWPlayerController.h"
#include "Net/FWNetSubsystem.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "UI/SFWAchievementsWindow.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWBadgeToast"

namespace FWBadgeTiming
{
	constexpr double SlideIn = 0.35;
	constexpr double ShownFor = 4.0;
	constexpr double FadeOut = 0.5;
}

void SFWBadgeToast::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	SetVisibility(EVisibility::HitTestInvisible);
	ChildSlot
	.HAlign(HAlign_Center)
	.VAlign(VAlign_Top)
	[
		SAssignNew(Card, SBox)
	];
	AFWPlayerController* PC = Controller.Get();
	UFWNetSubsystem* Net = PC && PC->GetGameInstance() ? PC->GetGameInstance()->GetSubsystem<UFWNetSubsystem>() : nullptr;
	if (Net)
	{
		PushHandle = Net->OnPush.AddSP(this, &SFWBadgeToast::OnPush);
		// From now on the server tells this connection of every badge the player unlocks
		Net->Request(TEXT("achievements.watch"), MakeShared<FJsonObject>(), [](const FFWNetReply&) {});
	}
}

SFWBadgeToast::~SFWBadgeToast()
{
	AFWPlayerController* PC = Controller.Get();
	if (UFWNetSubsystem* Net = PC && PC->GetGameInstance() ? PC->GetGameInstance()->GetSubsystem<UFWNetSubsystem>() : nullptr)
	{
		Net->OnPush.Remove(PushHandle);
	}
}

void SFWBadgeToast::OnPush(const FFWNetReply& Reply)
{
	if (Reply.Type == TEXT("achievement.unlocked") && Reply.Payload.IsValid())
	{
		Show(Reply.Payload);
	}
	else if (Reply.Type == TEXT("album.found") && Reply.Payload.IsValid())
	{
		if (AFWPlayerController* PC = Controller.Get())
		{
			PC->Toast(FText::Format(LOCTEXT("Album", "Nowe znalezisko w albumie: {0} ({1})"), FText::FromString(FWUi::Str(Reply.Payload, TEXT("name"))),
				FText::FromString(FWUi::Str(Reply.Payload, TEXT("page")))));
		}
	}
}

void SFWBadgeToast::Show(const TSharedPtr<FJsonObject>& Badge)
{
	Queue.Add(Badge);
	if (ShownAt <= 0.0)
	{
		Next();
	}
}

void SFWBadgeToast::Next()
{
	if (Queue.Num() == 0)
	{
		ShownAt = 0.0;
		Card->SetContent(SNullWidget::NullWidget);
		return;
	}
	const TSharedPtr<FJsonObject> Badge = Queue[0];
	Queue.RemoveAt(0);
	ShownAt = FPlatformTime::Seconds();
	if (AFWPlayerController* PC = Controller.Get())
	{
		PC->PlayUiSound(TEXT("build_place"));
	}
	const FString Title = FWUi::Str(Badge, TEXT("title"));
	Card->SetContent(
		SNew(SBorder).BorderImage(FWStyle::Panel()).Padding(FMargin(20.f, 14.f))
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 14, 0)[SFWAchievementsWindow::Medal(true, 52.f)]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SVerticalBox)
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Text(Title.IsEmpty() ? LOCTEXT("New", "Nowa odznaka!") : FText::FromString(Title)).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Text(FText::FromString(FWUi::Str(Badge, TEXT("name")))).Font(FWStyle::Font(20, TEXT("Bold"))).ColorAndOpacity(FWStyle::Accent)
				]
				+ SVerticalBox::Slot().AutoHeight()
				[
					SNew(STextBlock).Text(FText::FromString(FWUi::Str(Badge, TEXT("description")))).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Text)
				]
			]
		]);
}

void SFWBadgeToast::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (ShownAt <= 0.0)
	{
		return;
	}
	using namespace FWBadgeTiming;
	const double Age = FPlatformTime::Seconds() - ShownAt;
	if (Age >= SlideIn + ShownFor + FadeOut)
	{
		Next();
		return;
	}
	// Slides down with a little overshoot, stays, then fades
	const float In = FMath::Clamp(float(Age / SlideIn), 0.f, 1.f);
	const float Back = 1.f + 2.70158f * FMath::Pow(In - 1.f, 3.f) + 1.70158f * FMath::Pow(In - 1.f, 2.f);
	const float Y = FMath::Lerp(-140.f, 84.f, Back);
	const float Alpha = Age > SlideIn + ShownFor ? 1.f - float((Age - SlideIn - ShownFor) / FadeOut) : 1.f;
	Card->SetRenderTransform(FSlateRenderTransform(FVector2f(0.f, Y)));
	Card->SetRenderOpacity(FMath::Clamp(Alpha, 0.f, 1.f));
}

#undef LOCTEXT_NAMESPACE
