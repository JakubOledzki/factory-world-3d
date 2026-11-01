#include "UI/SFWStationWindow.h"

#include "FWPlayerController.h"
#include "UI/FWStyle.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/Input/SButton.h"
#include "Widgets/Layout/SBackgroundBlur.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/Notifications/SProgressBar.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/SOverlay.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWStation"

namespace
{
	FString Str(const FJsonObject& Object, const TCHAR* Field)
	{
		FString Value;
		Object.TryGetStringField(Field, Value);
		return Value;
	}

	double Num(const FJsonObject& Object, const TCHAR* Field, double Default = 0.0)
	{
		double Value = Default;
		Object.TryGetNumberField(Field, Value);
		return Value;
	}

	TArray<TSharedPtr<FJsonObject>> Objects(const FJsonObject& Object, const TCHAR* Field)
	{
		TArray<TSharedPtr<FJsonObject>> Result;
		const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
		if (Object.TryGetArrayField(Field, Values))
		{
			for (const TSharedPtr<FJsonValue>& Value : *Values)
			{
				if (Value.IsValid() && Value->Type == EJson::Object)
				{
					Result.Add(Value->AsObject());
				}
			}
		}
		return Result;
	}

	TSharedPtr<FJsonObject> Child(const FJsonObject& Object, const TCHAR* Field)
	{
		const TSharedPtr<FJsonObject>* Found = nullptr;
		return Object.TryGetObjectField(Field, Found) ? *Found : nullptr;
	}

	FText Seconds(double Value)
	{
		return Value >= 60.0 ? FText::Format(LOCTEXT("Minutes", "{0} min {1} s"), int32(Value) / 60, int32(Value) % 60)
			: FText::Format(LOCTEXT("Secs", "{0} s"), FMath::RoundToInt(Value));
	}

	TSharedRef<SWidget> Heading(const FText& Text)
	{
		return SNew(STextBlock).Text(Text).Font(FWStyle::Font(12, TEXT("Bold"))).ColorAndOpacity(FWStyle::Muted);
	}
}

void SFWStationWindow::Construct(const FArguments& InArgs)
{
	Controller = InArgs._Controller;
	Station = InArgs._Station;
	ChildSlot
	[
		SNew(SOverlay)
		+ SOverlay::Slot()
		[
			SNew(SBackgroundBlur).BlurStrength(4.f)
		]
		+ SOverlay::Slot()
		[
			SNew(SBorder).BorderImage(FWStyle::SideShade())
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Center)
		[
			SNew(SBorder)
			.BorderImage(FWStyle::Panel())
			.Padding(24.f)
			[
				SAssignNew(Content, SBox)
				.MinDesiredWidth(760.f)
				.MaxDesiredHeight(760.f)
				[
					SNew(STextBlock).Text(LOCTEXT("Loading", "Wczytywanie…")).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Muted)
				]
			]
		]
		+ SOverlay::Slot().HAlign(HAlign_Center).VAlign(VAlign_Bottom).Padding(0.f, 0.f, 0.f, 32.f)
		[
			SNew(STextBlock).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("Close", "E / Esc zamyka"))
		]
	];
}

void SFWStationWindow::Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime)
{
	SCompoundWidget::Tick(Geometry, CurrentTime, DeltaTime);
	if (const AFWPlayerController* PC = Controller.Get(); PC && PC->GetStationVersion() != BuiltVersion)
	{
		BuiltVersion = PC->GetStationVersion();
		Rebuild();
	}
}

void SFWStationWindow::Rebuild()
{
	const AFWPlayerController* PC = Controller.Get();
	const TSharedPtr<FJsonObject> State = PC ? PC->GetStationState() : nullptr;
	if (!State.IsValid() || !Content.IsValid())
	{
		return;
	}
	switch (Station)
	{
	case EFWStation::Crafting: Content->SetContent(MakeCrafting(*State)); break;
	case EFWStation::Furnace: Content->SetContent(MakeFurnace(*State)); break;
	case EFWStation::Machine: Content->SetContent(MakeMachine(*State)); break;
	}
}

int32 SFWStationWindow::Have(const FString& Item) const
{
	const FFWStack* Stack = Controller.IsValid() ? Controller->FindStack(Item) : nullptr;
	return Stack ? Stack->Quantity : 0;
}

float SFWStationWindow::JobFraction(double StartedAt, double Duration) const
{
	if (!Controller.IsValid() || Duration <= 0.0 || StartedAt <= 0.0)
	{
		return 0.f;
	}
	return FMath::Clamp(float((Controller->ServerNowMs() - StartedAt) / (Duration * 1000.0)), 0.f, 1.f);
}

TSharedRef<SWidget> SFWStationWindow::ItemChip(const FString& Item, const FText& Text, bool bShort)
{
	const FSlateBrush* Icon = Controller.IsValid() ? Controller->GetIconBrush(Item) : nullptr;
	return SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 6, 0)
		[
			SNew(SBox).WidthOverride(bShort ? 22.f : 40.f).HeightOverride(bShort ? 22.f : 40.f)
			[
				SNew(SImage).Image(Icon).Visibility(Icon ? EVisibility::Visible : EVisibility::Hidden)
			]
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(STextBlock).Text(Text).Font(FWStyle::Font(bShort ? 10 : 12)).ColorAndOpacity(FWStyle::Text)
		];
}

TSharedRef<SWidget> SFWStationWindow::Button(const FText& Text, TFunction<void()> OnClick, bool bPrimary)
{
	return SNew(SButton)
		.ButtonStyle(bPrimary ? &FWStyle::PrimaryButton() : &FWStyle::SecondaryButton())
		.HAlign(HAlign_Center)
		.OnClicked_Lambda([OnClick]() { OnClick(); return FReply::Handled(); })
		[
			SNew(STextBlock).Text(Text).Font(FWStyle::Font(11, bPrimary ? TEXT("Bold") : TEXT("Regular"))).ColorAndOpacity(bPrimary ? FLinearColor::White : FWStyle::Text)
		];
}

TSharedRef<SWidget> SFWStationWindow::Progress(TFunction<float()> Fraction)
{
	return SNew(SBox).HeightOverride(8.f)
		[
			SNew(SProgressBar).FillColorAndOpacity(FWStyle::Accent).Percent_Lambda([Fraction]() { return TOptional<float>(Fraction()); })
		];
}

TSharedRef<SWidget> SFWStationWindow::MakeCrafting(const FJsonObject& State)
{
	TWeakObjectPtr<AFWPlayerController> PC = Controller;
	// The recipes, with the ingredients the player has in green and what is missing in red
	TSharedRef<SScrollBox> Recipes = SNew(SScrollBox);
	for (const TSharedPtr<FJsonObject>& Recipe : Objects(State, TEXT("recipes")))
	{
		const FString Id = Str(*Recipe, TEXT("id"));
		const FString Locked = Str(*Recipe, TEXT("locked"));
		bool bCan = Locked.IsEmpty();
		TSharedRef<SWrapBox> Needs = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(10.f, 2.f));
		for (const TSharedPtr<FJsonObject>& Ingredient : Objects(*Recipe, TEXT("ingredients")))
		{
			const FString Item = Str(*Ingredient, TEXT("id"));
			const int32 Need = int32(Num(*Ingredient, TEXT("quantity")));
			const int32 Got = Have(Item);
			bCan &= Got >= Need;
			Needs->AddSlot()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().AutoWidth()[ItemChip(Item, FText::FromString(Str(*Ingredient, TEXT("name"))), true)]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(4, 0, 0, 0)
				[
					SNew(STextBlock).Font(FWStyle::Font(10, TEXT("Bold")))
					.ColorAndOpacity(Got >= Need ? FWStyle::Good : FWStyle::Error)
					.Text(FText::Format(LOCTEXT("HaveNeed", "{0}/{1}"), Got, Need))
				]
			];
		}
		const int32 Makes = int32(Num(*Recipe, TEXT("quantity"), 1));
		Recipes->AddSlot().Padding(0, 0, 8, 8)
		[
			SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(10.f)
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight()
					[
						ItemChip(Id, Makes > 1 ? FText::Format(LOCTEXT("Times", "{0} × {1}"), FText::FromString(Str(*Recipe, TEXT("name"))), Makes)
							: FText::FromString(Str(*Recipe, TEXT("name"))))
					]
					+ SVerticalBox::Slot().AutoHeight().Padding(46, 4, 0, 0)[Needs]
					+ SVerticalBox::Slot().AutoHeight().Padding(46, 2, 0, 0)
					[
						SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(Locked.IsEmpty() ? FWStyle::Muted : FWStyle::Error)
						.Text(Locked.IsEmpty() ? Seconds(Num(*Recipe, TEXT("seconds")))
							: FText::Format(LOCTEXT("Locked", "Wymaga badania: {0}"), FText::FromString(Locked)))
					]
				]
				+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
				[
					SNew(SBox).WidthOverride(120.f).IsEnabled(bCan)
					[
						Button(LOCTEXT("Craft", "Wytwórz"), [PC, Id]() { if (PC.IsValid()) PC->StationAction(TEXT("crafting.craft"), TEXT("recipe"), Id); }, true)
					]
				]
			]
		];
	}
	// The queue: the one being made with its progress, the rest waiting
	TSharedRef<SVerticalBox> Queue = SNew(SVerticalBox);
	const TArray<TSharedPtr<FJsonObject>> Jobs = Objects(State, TEXT("queue"));
	for (const TSharedPtr<FJsonObject>& Job : Jobs)
	{
		const double Start = Num(*Job, TEXT("startsAt"));
		const double Length = (Num(*Job, TEXT("finishesAt")) - Start) / 1000.0;
		const int64 JobId = int64(Num(*Job, TEXT("id")));
		Queue->AddSlot().AutoHeight().Padding(0, 0, 0, 10)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(SHorizontalBox)
				+ SHorizontalBox::Slot().FillWidth(1.f)[ItemChip(Str(*Job, TEXT("recipe")), FText::FromString(Str(*Job, TEXT("name"))), true)]
				+ SHorizontalBox::Slot().AutoWidth()
				[
					Button(LOCTEXT("Cancel", "Anuluj"), [PC, JobId]() { if (PC.IsValid()) PC->StationAction(TEXT("crafting.cancel"), TEXT("id"), FString(), JobId); })
				]
			]
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 4, 0, 0)
			[
				Progress([this, Start, Length]() { return JobFraction(Start, Length); })
			]
		];
	}
	if (Jobs.IsEmpty())
	{
		Queue->AddSlot().AutoHeight()[SNew(STextBlock).Text(LOCTEXT("Empty", "Nic się nie robi.")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)];
	}
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 14)
		[
			SNew(STextBlock).Text_Lambda([PC]() { return PC.IsValid() ? FText::FromString(PC->GetStationName()) : FText::GetEmpty(); })
			.Font(FWStyle::Font(22, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
		]
		+ SVerticalBox::Slot().FillHeight(1.f)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(SBox).HeightOverride(560.f).WidthOverride(560.f)[Recipes]
			]
			+ SHorizontalBox::Slot().AutoWidth().Padding(18, 0, 0, 0)
			[
				SNew(SBox).WidthOverride(260.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 10)
					[
						Heading(FText::Format(LOCTEXT("Queue", "Kolejka ({0}/{1})"), Jobs.Num(), int32(Num(State, TEXT("maxQueue"), 5))))
					]
					+ SVerticalBox::Slot().AutoHeight()[Queue]
				]
			]
		];
}

TSharedRef<SWidget> SFWStationWindow::MakeFurnace(const FJsonObject& State)
{
	TWeakObjectPtr<AFWPlayerController> PC = Controller;
	const TSharedPtr<FJsonObject> Input = Child(State, TEXT("input"));
	const TSharedPtr<FJsonObject> Output = Child(State, TEXT("output"));
	const int32 Fuel = int32(Num(State, TEXT("fuel")));
	const double Started = Num(State, TEXT("startedAt"));
	const double Smelt = Num(State, TEXT("smeltSeconds"));
	// The ore the player can put in: any recipe's input they have
	FString Ore;
	TSharedRef<SVerticalBox> Recipes = SNew(SVerticalBox);
	for (const TSharedPtr<FJsonObject>& Recipe : Objects(State, TEXT("recipes")))
	{
		const FString In = Str(*Recipe, TEXT("input"));
		if (Ore.IsEmpty() && Have(In) > 0)
		{
			Ore = In;
		}
		Recipes->AddSlot().AutoHeight().Padding(0, 2)
		[
			SNew(STextBlock).Font(FWStyle::Font(10)).ColorAndOpacity(Have(In) > 0 ? FWStyle::Text : FWStyle::Muted)
			.Text(FText::Format(LOCTEXT("Smelt", "{0} → {1}  ·  {2}"), FText::FromString(Str(*Recipe, TEXT("inputName"))),
				FText::FromString(Str(*Recipe, TEXT("outputName"))), Seconds(Num(*Recipe, TEXT("seconds")))))
		];
	}
	const int32 MaxInput = int32(Num(State, TEXT("maxInput"), 50));
	const int32 MaxFuel = int32(Num(State, TEXT("maxFuel"), 50));
	auto Slot = [this](const FText& Label, const TSharedPtr<FJsonObject>& Stack, const FString& FallbackItem, int32 FallbackCount)
	{
		const FString Item = Stack.IsValid() ? Str(*Stack, TEXT("id")) : FallbackItem;
		const int32 Count = Stack.IsValid() ? int32(Num(*Stack, TEXT("quantity"))) : FallbackCount;
		return SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 6)[Heading(Label)]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(SBorder).BorderImage(FWStyle::SideShade()).Padding(10.f)
				[
					Item.IsEmpty() || Count <= 0
						? StaticCastSharedRef<SWidget>(SNew(SBox).HeightOverride(40.f).VAlign(VAlign_Center)[SNew(STextBlock).Text(LOCTEXT("None", "pusto")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted)])
						: ItemChip(Item, FText::AsNumber(Count))
				]
			];
	};
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 14)
		[
			SNew(STextBlock).Text(FText::Format(LOCTEXT("FurnaceTitle", "{0}  ·  poziom {1}"), FText::FromString(Str(State, TEXT("name"))), int32(Num(State, TEXT("level"), 1))))
			.Font(FWStyle::Font(22, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
		]
		+ SVerticalBox::Slot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 10, 0)[Slot(LOCTEXT("Ore", "Ruda"), Input, FString(), 0)]
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 10, 0)[Slot(LOCTEXT("Fuel", "Węgiel"), nullptr, TEXT("coal"), Fuel)]
			+ SHorizontalBox::Slot().FillWidth(1.f)[Slot(LOCTEXT("Ingots", "Sztabki"), Output, FString(), 0)]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 14, 0, 0)
		[
			Progress([this, Started, Smelt]() { return JobFraction(Started, Smelt); })
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 16, 0, 0)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)
			[
				SNew(SBox).IsEnabled(!Ore.IsEmpty())
				[
					Button(LOCTEXT("LoadOre", "Włóż rudę"), [PC, Ore, MaxInput, this]() { if (PC.IsValid()) PC->StationLoad(TEXT("furnace.load"), Ore, FMath::Min(Have(Ore), MaxInput)); }, true)
				]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(0, 0, 6, 0)
			[
				SNew(SBox).IsEnabled(Have(TEXT("coal")) > 0)
				[
					Button(LOCTEXT("LoadCoal", "Dołóż węgla"), [PC, MaxFuel, Fuel, this]() { if (PC.IsValid()) PC->StationLoad(TEXT("furnace.load"), TEXT("coal"), FMath::Min(Have(TEXT("coal")), FMath::Max(1, MaxFuel - Fuel))); })
				]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				SNew(SBox).IsEnabled(Output.IsValid())
				[
					Button(LOCTEXT("TakeIngots", "Weź sztabki"), [PC]() { if (PC.IsValid()) PC->StationAction(TEXT("furnace.take"), TEXT("what"), TEXT("output")); })
				]
			]
		]
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 18, 0, 6)[Heading(LOCTEXT("Recipes", "Co wytapia"))]
		+ SVerticalBox::Slot().AutoHeight()[Recipes];
}

TSharedRef<SWidget> SFWStationWindow::MakeMachine(const FJsonObject& State)
{
	TWeakObjectPtr<AFWPlayerController> PC = Controller;
	const FString Current = Str(State, TEXT("recipe"));
	// The recipes to choose from
	TSharedRef<SScrollBox> Choices = SNew(SScrollBox);
	for (const TSharedPtr<FJsonObject>& Recipe : Objects(State, TEXT("recipes")))
	{
		const FString Id = Str(*Recipe, TEXT("id"));
		const bool bLocked = !Str(*Recipe, TEXT("locked")).IsEmpty();
		Choices->AddSlot().Padding(0, 0, 6, 6)
		[
			SNew(SButton)
			.ButtonStyle(&FWStyle::SecondaryButton())
			.ButtonColorAndOpacity(Id == Current ? FLinearColor(4.f, 2.2f, 0.5f, 1.f) : FLinearColor::White)
			.IsEnabled(!bLocked)
			.OnClicked_Lambda([PC, Id]() { if (PC.IsValid()) PC->StationAction(TEXT("machine.recipe"), TEXT("recipe"), Id); return FReply::Handled(); })
			[
				ItemChip(Id, bLocked
					? FText::Format(LOCTEXT("RecipeLocked", "{0}  ·  wymaga badania: {1}"), FText::FromString(Str(*Recipe, TEXT("name"))), FText::FromString(Str(*Recipe, TEXT("locked"))))
					: FText::Format(LOCTEXT("RecipeLine", "{0}  ·  {1}"), FText::FromString(Str(*Recipe, TEXT("name"))), Seconds(Num(*Recipe, TEXT("seconds")))), true)
			]
		];
	}
	// What the chosen recipe takes, held now / per batch, with a button to put in all the player has
	TSharedRef<SVerticalBox> Inputs = SNew(SVerticalBox);
	const int32 MaxInput = int32(Num(State, TEXT("maxInput"), 50));
	for (const TSharedPtr<FJsonObject>& Input : Objects(State, TEXT("inputs")))
	{
		const FString Item = Str(*Input, TEXT("id"));
		const int32 Held = int32(Num(*Input, TEXT("quantity")));
		Inputs->AddSlot().AutoHeight().Padding(0, 0, 0, 8)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				ItemChip(Item, FText::Format(LOCTEXT("Input", "{0}: {1} (na partię {2}, masz {3})"), FText::FromString(Str(*Input, TEXT("name"))), Held,
					int32(Num(*Input, TEXT("need"))), Have(Item)))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SBox).IsEnabled(Have(Item) > 0 && Held < MaxInput)
				[
					Button(LOCTEXT("Load", "Włóż"), [PC, Item, Held, MaxInput, this]() { if (PC.IsValid()) PC->StationLoad(TEXT("machine.load"), Item, FMath::Min(Have(Item), MaxInput - Held)); })
				]
			]
		];
	}
	const TSharedPtr<FJsonObject> Output = Child(State, TEXT("output"));
	const bool bFired = State.GetBoolField(TEXT("fired"));
	const double Started = Num(State, TEXT("startedAt"));
	const double Batch = Num(State, TEXT("seconds"));
	const double Speed = Num(State, TEXT("speed"), 1.0);
	bool bWorn = false;
	State.TryGetBoolField(TEXT("worn"), bWorn);
	const double ServiceCents = Num(State, TEXT("serviceCents"));
	TSharedRef<SVerticalBox> Right = SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 8)[Heading(LOCTEXT("Needs", "Na partię"))]
		+ SVerticalBox::Slot().AutoHeight()[Inputs];
	if (Current.IsEmpty())
	{
		Right->AddSlot().AutoHeight()[SNew(STextBlock).Text(LOCTEXT("Choose", "Wybierz, co ma robić.")).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)];
	}
	if (bFired)
	{
		const TSharedPtr<FJsonObject> Fuel = Child(State, TEXT("fuel"));
		Right->AddSlot().AutoHeight().Padding(0, 8, 0, 8)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f)
			[
				ItemChip(Fuel.IsValid() ? Str(*Fuel, TEXT("id")) : TEXT("coal"), FText::Format(LOCTEXT("FuelLine", "Opał: {0}"), Fuel.IsValid() ? int32(Num(*Fuel, TEXT("quantity"))) : 0))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(SBox).IsEnabled(Have(TEXT("coal")) > 0)
				[
					Button(LOCTEXT("AddFuel", "Dołóż węgla"), [PC, this]() { if (PC.IsValid()) PC->StationLoad(TEXT("machine.load"), TEXT("coal"), FMath::Min(Have(TEXT("coal")), 20), true); })
				]
			]
		];
	}
	Right->AddSlot().AutoHeight().Padding(0, 10, 0, 6)[Heading(FText::Format(LOCTEXT("Working", "Praca  ·  prędkość {0}%"), FMath::RoundToInt(Speed * 100.0)))];
	Right->AddSlot().AutoHeight()[Progress([this, Started, Batch]() { return JobFraction(Started, Batch); })];
	Right->AddSlot().AutoHeight().Padding(0, 14, 0, 0)
	[
		SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().FillWidth(1.f)
		[
			Output.IsValid() ? ItemChip(Str(*Output, TEXT("id")), FText::Format(LOCTEXT("Out", "Gotowe: {0}"), int32(Num(*Output, TEXT("quantity")))))
				: StaticCastSharedRef<SWidget>(SNew(STextBlock).Text(LOCTEXT("NoOut", "Jeszcze nic gotowego.")).Font(FWStyle::Font(11)).ColorAndOpacity(FWStyle::Muted))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SBox).IsEnabled(Output.IsValid())
			[
				Button(LOCTEXT("Take", "Weź"), [PC]() { if (PC.IsValid()) PC->StationAction(TEXT("machine.take"), TEXT("what"), TEXT("output")); }, true)
			]
		]
	];
	if (bWorn)
	{
		Right->AddSlot().AutoHeight().Padding(0, 14, 0, 0)
		[
			Button(FText::Format(LOCTEXT("Service", "Serwis ({0} $): wolniej o {1}%"), FText::AsNumber(ServiceCents / 100.0), int32(Num(State, TEXT("slowerPercent")))),
				[PC]() { if (PC.IsValid()) PC->StationAction(TEXT("machine.service"), nullptr, FString()); })
		];
	}
	return SNew(SVerticalBox)
		+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 14)
		[
			SNew(STextBlock).Text(FText::FromString(Str(State, TEXT("name")))).Font(FWStyle::Font(22, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
		]
		+ SVerticalBox::Slot().AutoHeight()
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth()
			[
				SNew(SBox).WidthOverride(300.f).HeightOverride(480.f)
				[
					SNew(SVerticalBox)
					+ SVerticalBox::Slot().AutoHeight().Padding(0, 0, 0, 8)[Heading(LOCTEXT("What", "Co ma robić"))]
					+ SVerticalBox::Slot().FillHeight(1.f)[Choices]
				]
			]
			+ SHorizontalBox::Slot().FillWidth(1.f).Padding(20, 0, 0, 0)
			[
				SNew(SBox).WidthOverride(440.f)[Right]
			]
		];
}

FReply SFWStationWindow::OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event)
{
	if (Event.GetKey() == EKeys::Escape || Event.GetKey() == EKeys::E)
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
