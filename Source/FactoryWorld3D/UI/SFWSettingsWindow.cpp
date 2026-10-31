#include "UI/SFWSettingsWindow.h"

#include "FWPlayerController.h"
#include "GameFramework/GameUserSettings.h"
#include "Kismet/KismetSystemLibrary.h"
#include "Misc/ConfigCacheIni.h"
#include "UI/FWStyle.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Input/SSlider.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWSettings"

namespace
{
	const TCHAR* Section = TEXT("FactoryWorld.Settings");
}

FFWUserSettings FFWUserSettings::Load()
{
	FFWUserSettings Settings;
	GConfig->GetFloat(Section, TEXT("MasterVolume"), Settings.MasterVolume, GGameUserSettingsIni);
	GConfig->GetFloat(Section, TEXT("MouseSensitivity"), Settings.MouseSensitivity, GGameUserSettingsIni);
	GConfig->GetFloat(Section, TEXT("FieldOfView"), Settings.FieldOfView, GGameUserSettingsIni);
	if (const UGameUserSettings* User = GEngine ? GEngine->GetGameUserSettings() : nullptr)
	{
		Settings.Quality = FMath::Clamp(User->GetOverallScalabilityLevel(), 0, 3);
	}
	return Settings;
}

void FFWUserSettings::Save() const
{
	GConfig->SetFloat(Section, TEXT("MasterVolume"), MasterVolume, GGameUserSettingsIni);
	GConfig->SetFloat(Section, TEXT("MouseSensitivity"), MouseSensitivity, GGameUserSettingsIni);
	GConfig->SetFloat(Section, TEXT("FieldOfView"), FieldOfView, GGameUserSettingsIni);
	GConfig->Flush(false, GGameUserSettingsIni);
}

void SFWSettingsWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Settings = FFWUserSettings::Load();

	TSharedRef<SHorizontalBox> Quality = SNew(SHorizontalBox);
	const FText Levels[] = { LOCTEXT("Low", "Niska"), LOCTEXT("Medium", "Średnia"), LOCTEXT("High", "Wysoka"), LOCTEXT("Epic", "Najwyższa") };
	for (int32 Level = 0; Level < 4; ++Level)
	{
		Quality->AddSlot().FillWidth(1.f).Padding(Level == 0 ? 0.f : 4.f, 0.f, 0.f, 0.f)
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ButtonColorAndOpacity_Lambda([this, Level]() { return Settings.Quality == Level ? FLinearColor(4.f, 2.2f, 0.5f, 1.f) : FLinearColor::White; })
			.HAlign(HAlign_Center)
			.OnClicked_Lambda([this, Level]()
			{
				Settings.Quality = Level;
				Apply();
				return FReply::Handled();
			})
			[
				SNew(STextBlock).Text(Levels[Level]).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Text)
			]
		];
	}

	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot()
		[
			SNew(SBackgroundBlur).BlurStrength(6.f)
		]
		+ SOverlay::Slot()
		[
			SNew(SBorder).BorderImage(FWStyle::SideShade())
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SBox)
			.WidthOverride(460.f)
			[
				SNew(SBorder)
				.BorderImage(FWStyle::Panel())
				.Padding(28.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 20)
					[
						SNew(STextBlock).Text(LOCTEXT("Title", "Ustawienia")).Font(FWStyle::Font(24, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						Slider(LOCTEXT("Volume", "Głośność"), 0.f, 1.f, &Settings.MasterVolume,
							[](float V) { return FText::Format(LOCTEXT("Percent", "{0}%"), FMath::RoundToInt(V * 100.f)); })
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						Slider(LOCTEXT("Mouse", "Czułość myszy"), 0.2f, 3.f, &Settings.MouseSensitivity,
							[](float V) { return FText::Format(LOCTEXT("Times", "{0}×"), FText::AsNumber(FMath::RoundToFloat(V * 10.f) / 10.f)); })
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						Slider(LOCTEXT("Fov", "Pole widzenia"), 70.f, 110.f, &Settings.FieldOfView,
							[](float V) { return FText::Format(LOCTEXT("Degrees", "{0}°"), FMath::RoundToInt(V)); })
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 6, 0, 6)
					[
						SNew(STextBlock).Text(LOCTEXT("Quality", "Jakość grafiki")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 24)
					[
						Quality
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 8)
					[
						SNew(SButton).ButtonStyle(&FWStyle::PrimaryButton()).HAlign(HAlign_Center)
						.OnClicked_Lambda([this]()
						{
							if (AFWPlayerController* PC = Controller.Get())
							{
								PC->CloseWindow();
							}
							return FReply::Handled();
						})
						[
							SNew(STextBlock).Text(LOCTEXT("Resume", "Wróć do gry")).Font(FWStyle::Font(14, TEXT("Bold"))).ColorAndOpacity(FLinearColor::White)
						]
					]
					+ SVerticalBox::Slot().AutoHeight()
					[
						SNew(SButton).ButtonStyle(&FWStyle::SecondaryButton()).HAlign(HAlign_Center)
						.OnClicked_Lambda([this]()
						{
							if (AFWPlayerController* PC = Controller.Get())
							{
								UKismetSystemLibrary::QuitGame(PC, PC, EQuitPreference::Quit, false);
							}
							return FReply::Handled();
						})
						[
							SNew(STextBlock).Text(LOCTEXT("Quit", "Wyjdź z gry")).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
						]
					]
				]
			]
		]
	];
}

TSharedRef<SWidget> SFWSettingsWindow::Slider(const FText& Label, float Min, float Max, float* Value, TFunction<FText(float)> Show)
{
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(STextBlock).Text(Label).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)
			]
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(STextBlock).Font(FWStyle::Font(11, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
				.Text_Lambda([Value, Show]() { return Show(*Value); })
			]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 4, 0, 16)
		[
			SNew(SSlider)
			.MinValue(Min)
			.MaxValue(Max)
			.Value_Lambda([Value]() { return *Value; })
			.SliderBarColor(FLinearColor(1.f, 1.f, 1.f, 0.15f))
			.SliderHandleColor(FWStyle::Accent)
			.OnValueChanged_Lambda([this, Value](float New)
			{
				*Value = New;
				Apply();
			})
		];
}

void SFWSettingsWindow::Apply()
{
	Settings.Save();
	if (UGameUserSettings* User = GEngine ? GEngine->GetGameUserSettings() : nullptr; User && User->GetOverallScalabilityLevel() != Settings.Quality)
	{
		User->SetOverallScalabilityLevel(Settings.Quality);
		User->ApplySettings(false);
		User->SaveSettings();
	}
	if (AFWPlayerController* PC = Controller.Get())
	{
		PC->ApplyUserSettings(Settings);
	}
}

FReply SFWSettingsWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape)
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
