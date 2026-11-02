// The garage's trash: sorting by hand at the bin (the minigame) and the sorting machine.
#include "UI/SFWGarageWindow.h"
#include "Framework/Application/SlateApplication.h"
#include "Fonts/FontMeasure.h"
#include "FWPlayerController.h"
#include "Game/FWArt.h"
#include "Game/FWAudio.h"
#include "Game/FWCatalog.h"
#include "Input/DragAndDrop.h"
#include "Net/FWNetSubsystem.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"
#include "UI/FWForm.h"
#include "UI/FWStyle.h"
#include "UI/FWWidgets.h"
#include "Widgets/Layout/SBorder.h"
#include "Widgets/Layout/SBox.h"
#include "Widgets/Layout/SScrollBox.h"
#include "Widgets/Layout/SWrapBox.h"
#include "Widgets/SBoxPanel.h"
#include "Widgets/Images/SImage.h"
#include "Widgets/SLeafWidget.h"
#include "Widgets/Text/STextBlock.h"

#define LOCTEXT_NAMESPACE "FWGarage"

#include "SFWGarageWindowHelpers.h"

void SFWGarageWindow::Sort()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || bSortPending)
	{
		return;
	}
	bSortPending = true;
	LastBag = FText::GetEmpty();
	WrongAt.Reset();
	FWAudio::Play2D(PC, TEXT("sort_pick"), 0.8f);
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("count"), 1);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("recycling.sort"), P, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
		{
			This->bSortPending = false;
			This->ApplyGarage(Reply);
		}
	});
}

bool SFWGarageWindow::HasRound() const
{
	const TSharedPtr<FJsonObject>* Round = nullptr;
	return Garage.IsValid() && Garage->TryGetObjectField(TEXT("round"), Round) && Round && Round->IsValid();
}

void SFWGarageWindow::ToggleSorting()
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC || !Garage.IsValid())
	{
		return;
	}
	if (!HasRound())
	{
		Sort();
		return;
	}
	// Putting the bag back unsorted
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("recycling.stop"), nullptr, [WeakThis](const FFWNetReply& Reply)
	{
		if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
		{
			This->ApplyGarage(Reply);
		}
	});
}

void SFWGarageWindow::PutPiece(int32 Piece, const FString& Bin)
{
	AFWPlayerController* PC = Controller.Get();
	DraggingPiece = -1;
	if (!PC || !HasRound() || PendingPiece >= 0)
	{
		return;
	}
	// The piece stays out of the bag until the server has said where it went
	PendingPiece = Piece;
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("piece"), Piece);
	P->SetStringField(TEXT("bin"), Bin);
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(TEXT("recycling.put"), P, [WeakThis, Piece, Bin](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		if (!This)
		{
			return;
		}
		This->PendingPiece = -1;
		AFWPlayerController* Inner = This->Controller.Get();
		if (Reply.Type != TEXT("garage"))
		{
			// Out of step with the server (e.g. the bag was closed elsewhere): show the garage as it is
			if (Inner)
			{
				Inner->ToastError(Reply);
			}
			This->LoadGarage();
			return;
		}
		const TSharedPtr<FJsonObject> Put = FWUi::Obj(Reply.Payload, TEXT("put"));
		const bool bCorrect = Put.IsValid() && Put->GetBoolField(TEXT("correct"));
		This->FlashBin = Bin;
		This->FlashAt = FWGarageText::Now();
		This->bFlashGood = bCorrect;
		if (bCorrect)
		{
			FWAudio::Play2D(Inner, FString::Printf(TEXT("sort_%s"), *Bin), 0.9f);
			const TSharedPtr<FJsonObject>* Round = nullptr;
			if (!(Reply.Payload->TryGetObjectField(TEXT("round"), Round) && Round && Round->IsValid()))
			{
				FWAudio::Play2D(Inner, TEXT("sort_done"), 0.8f);
			}
		}
		else
		{
			This->WrongAt.Add(Piece, FWGarageText::Now());
			FWAudio::Play2D(Inner, TEXT("sort_wrong"), 0.8f);
		}
		This->ApplyGarage(Reply);
	});
}

const FSlateBrush* SFWGarageWindow::WasteBrush(const FString& Name)
{
	if (const TSharedPtr<FSlateBrush>* Found = WasteBrushes.Find(Name))
	{
		return Found->Get();
	}
	TSharedPtr<FSlateBrush> Brush;
	const FString Asset = FString::Printf(TEXT("icon_%s"), *Name);
	if (UTexture2D* Texture = FWArt::Texture(*FString::Printf(TEXT("/Game/External/Icons/%s.%s"), *Asset, *Asset)))
	{
		Brush = MakeShared<FSlateBrush>();
		Brush->SetResourceObject(Texture);
		Brush->ImageSize = FVector2D(Texture->GetSizeX(), Texture->GetSizeY());
		WasteTextures.Emplace(Texture);
	}
	WasteBrushes.Add(Name, Brush);
	return Brush.Get();
}

void SFWGarageWindow::SendSorter(const TCHAR* Type, TSharedPtr<FJsonObject> Payload)
{
	AFWPlayerController* PC = Controller.Get();
	if (!PC)
	{
		return;
	}
	Payload->SetStringField(TEXT("sorter"), FurnitureId(TEXT("furniture_sorting_machine")));
	const bool bChange = FCString::Strcmp(Type, TEXT("sorter.get")) != 0;
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	PC->Send(Type, Payload, [WeakThis, bChange](const FFWNetReply& Reply)
	{
		TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
		AFWPlayerController* Inner = This ? This->Controller.Get() : nullptr;
		if (!Inner)
		{
			return;
		}
		if (Reply.Type != TEXT("sorter"))
		{
			Inner->ToastError(Reply);
			return;
		}
		This->Sorter = Reply.Payload;
		This->ClockOffset = FWUi::Num(This->Sorter, TEXT("serverTime"), FWUi::LocalNowMs()) - FWUi::LocalNowMs();
		if (bChange)
		{
			Inner->PlayUiSound(TEXT("build_pickup"));
			Inner->RefreshInventory();
			TArray<FString> Taken;
			for (const TSharedPtr<FJsonObject>& Stack : FWUi::Objects(This->Sorter, TEXT("taken")))
			{
				Taken.Add(FString::Printf(TEXT("%s × %d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity")))));
			}
			if (Taken.Num() > 0)
			{
				Inner->Toast(FText::Format(LOCTEXT("Taken", "Do ekwipunku: {0}"), FText::FromString(FString::Join(Taken, TEXT(", ")))));
			}
		}
		This->Rebuild();
	});
}

void SFWGarageWindow::LoadSorter(int32 Quantity)
{
	TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
	P->SetNumberField(TEXT("quantity"), Quantity);
	SendSorter(TEXT("sorter.load"), P);
}

TSharedRef<SWidget> SFWGarageWindow::MakeSorting()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Trash = int32(FWUi::Num(Garage, TEXT("trash")));
	const bool bOpen = HasRound();
	Add(SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Muted).AutoWrapText(true)
		.Text(FText::FromString(FString::Printf(TEXT("%s · %s"), *FWUi::Str(Garage, TEXT("rank")),
			TEXT("tu zaczyna każdy: worek z kosza wysypujesz i segregujesz ręcznie do koszy na metal, plastik i szkło, a każdy posegregowany worek daje losowy surowiec i 5 XP.")))), 0.f);
	Add(SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 12, 0)[FWUi::Icon(PC, TEXT("trash"), 56.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock).Font(FWStyle::Display(26)).ColorAndOpacity(FWStyle::Text).Text(FText::FromString(FString::Printf(TEXT("Worki śmieci: %d"), Trash)))
			]
			+ SVerticalBox::Slot().AutoHeight()
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted)
				.Text(LOCTEXT("BagRules", "W worku 5 przedmiotów: przeciągnij każdy do właściwego kosza"))
			]
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
		[
			SNew(SBox).WidthOverride(220.f)
			[
				FWUi::SmallButton(bOpen ? LOCTEXT("PutBack", "Odłóż worek") : LOCTEXT("OpenBag", "Otwórz worek"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->ToggleSorting();
					}
				}, !bOpen, bOpen || (Trash > 0 && !bSortPending && bAtBin))
			]
		], 14.f);

	// Trash is sorted at the bin: away from it the window only shows how many bags wait
	if (!bAtBin && !HasRound())
	{
		Add(SNew(SFWCard).Padding(FMargin(16.f, 14.f))
			[
				FWUi::Note(LOCTEXT("GoToBin", "Segregować możesz tylko przy koszu na śmieci: podejdź do niego i wciśnij E."), FWStyle::Accent)
			], 14.f);
	}
	else
	{
		Add(MakeSortingGame(), 14.f);
	}

	// What the last bags gave
	Add(FWUi::Heading(LOCTEXT("Recent", "Ostatnio wysegregowane")), 18.f);
	if (Recent.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("NoneYet", "Jeszcze nic w tym podejściu."), FWStyle::Muted), 4.f);
	}
	else
	{
		TSharedRef<SWrapBox> Row = SNew(SWrapBox).UseAllottedSize(true).InnerSlotPadding(FVector2D(6.f, 6.f));
		for (const TPair<FString, FString>& Item : Recent)
		{
			Row->AddSlot()[SNew(SBox).ToolTipText(FText::FromString(Item.Value))[FWUi::Icon(PC, Item.Key, 38.f)]];
		}
		Add(Row, 6.f);
	}
	Add(FWUi::Note(LOCTEXT("SortingHelp", "Segregujesz ręcznie, więc okno musi być otwarte: zamknięcie odkłada otwarty worek z powrotem do kosza. Przedmiot wrzucony do złego kosza wraca do worka. Automatyczna sortownia (Rynek, 1000 $) to osobna maszyna: włóż do niej do 50 worków, a posegreguje je sama, także gdy cię nie ma. Śmieci kupisz na rynku."),
		FWStyle::Muted), 18.f);
	return Page;
}

TSharedRef<SWidget> SFWGarageWindow::MakeSortingGame()
{
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	TSharedRef<SVerticalBox> Area = SNew(SVerticalBox);
	const TSharedPtr<FJsonObject> Round = HasRound() ? FWUi::Obj(Garage, TEXT("round")) : nullptr;
	if (!Round.IsValid())
	{
		// No bag open: the last bag's result, or how to start
		const bool bDone = !LastBag.IsEmpty();
		Area->AddSlot().AutoHeight().HAlign(HAlign_Center).Padding(0, 34, 0, 6)
		[
			SNew(STextBlock).Font(FWStyle::Font(bDone ? 22 : 15, TEXT("Bold"))).ColorAndOpacity(bDone ? FWStyle::Good : FWStyle::Muted)
			.Text(bDone ? LastBag : LOCTEXT("NoBag", "Otwórz worek, a jego zawartość wysypie się tutaj."))
		];
		if (bDone)
		{
			Area->AddSlot().AutoHeight().HAlign(HAlign_Center).Padding(0, 0, 0, 30)
			[
				SNew(STextBlock).Font(FWStyle::Font(12)).ColorAndOpacity(FWStyle::Muted).Text(LOCTEXT("NextBagHint", "Otwórz następny worek, aby segregować dalej."))
			];
		}
	}
	else
	{
		const int32 Left = int32(FWUi::Num(Round, TEXT("left")));
		const int32 Total = FWUi::Objects(Round, TEXT("pieces")).Num();
		Area->AddSlot().AutoHeight().Padding(0, 0, 0, 8)
		[
			SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Display(18, TEXT("SemiBold"))).ColorAndOpacity(FWStyle::Accent)
				.Text(LOCTEXT("DragHint", "Przeciągnij każdy przedmiot do właściwego kosza"))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::Format(LOCTEXT("Left", "Zostało {0} z {1}"), FText::AsNumber(Left), FText::AsNumber(Total)))
			]
		];
		Area->AddSlot().AutoHeight().Padding(0, 0, 0, 10)
		[
			FWUi::Bar(float(Total - Left) / FMath::Max(Total, 1), FWStyle::Accent)
		];
		// The bag's pieces, on their places; a sorted one leaves an empty place
		TSharedRef<SHorizontalBox> Pieces = SNew(SHorizontalBox);
		for (const TSharedPtr<FJsonObject>& Piece : FWUi::Objects(Round, TEXT("pieces")))
		{
			const int32 Index = int32(FWUi::Num(Piece, TEXT("index")));
			TSharedRef<SWidget> Place = SNew(SBox).WidthOverride(132.f).HeightOverride(150.f);
			if (!Piece->GetBoolField(TEXT("sorted")))
			{
				Place = SNew(SBox).WidthOverride(132.f).HeightOverride(150.f).HAlign(HAlign_Center)
					[
						SNew(FWGarageText::SWastePiece)
						.Index(Index)
						.Brush(WasteBrush(FWUi::Str(Piece, TEXT("kind"))))
						.Name(FWUi::Str(Piece, TEXT("name")))
						.SinceWrong_Lambda([WeakThis, Index]()
						{
							TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
							const double* At = This ? This->WrongAt.Find(Index) : nullptr;
							return At ? FWGarageText::Now() - *At : 100.0;
						})
						.Away_Lambda([WeakThis, Index]()
						{
							TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
							return This && (This->DraggingPiece == Index || This->PendingPiece == Index);
						})
						.OnPicked([WeakThis](int32 Picked)
						{
							if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
							{
								This->DraggingPiece = Picked;
								FWAudio::Play2D(This->Controller.Get(), TEXT("sort_pick"), 0.6f);
							}
						})
						.OnDragEnded([WeakThis]()
						{
							// Dropped anywhere but on a bin: back into the bag
							if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
							{
								This->DraggingPiece = -1;
							}
						})
					];
			}
			Pieces->AddSlot().FillWidth(1.f).HAlign(HAlign_Center)[Place];
		}
		Area->AddSlot().AutoHeight().Padding(0, 4, 0, 18)[Pieces];
	}

	// The bins stand there all the time, to drop pieces in while a bag is open
	TSharedRef<SHorizontalBox> Bins = SNew(SHorizontalBox);
	for (const TSharedPtr<FJsonObject>& Bin : FWUi::Objects(Garage, TEXT("bins")))
	{
		const FString Id = FWUi::Str(Bin, TEXT("id"));
		Bins->AddSlot().FillWidth(1.f).HAlign(HAlign_Center).Padding(8, 0)
		[
			SNew(FWGarageText::SWasteBin)
			.Brush(WasteBrush(FString::Printf(TEXT("bin_%s"), *Id)))
			.Name(FText::FromString(FWUi::Str(Bin, TEXT("name"))))
			.Color(FWGarageText::BinColor(Id))
			.Flash_Lambda([WeakThis, Id]()
			{
				TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
				return This && This->FlashBin == Id ? FWGarageText::FBinFlash(TPair<double, bool>(FWGarageText::Now() - This->FlashAt, This->bFlashGood))
					: FWGarageText::FBinFlash();
			})
			.OnPiece([WeakThis, Id](int32 Piece)
			{
				if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
				{
					This->PutPiece(Piece, Id);
				}
			})
		];
	}
	Area->AddSlot().AutoHeight()[Bins];

	return SNew(SBorder)
		.BorderImage(FCoreStyle::Get().GetBrush(TEXT("GenericWhiteBox")))
		.BorderBackgroundColor(FLinearColor(0.f, 0.f, 0.f, 0.28f))
		.Padding(FMargin(18.f, 16.f))
		[
			Area
		];
}

TSharedRef<SWidget> SFWGarageWindow::MakeSorter()
{
	AFWPlayerController* PC = Controller.Get();
	TWeakPtr<SFWGarageWindow> WeakThis = SharedThis(this);
	if (FurnitureId(TEXT("furniture_sorting_machine")).IsEmpty())
	{
		const bool bHave = Owned(TEXT("furniture_sorting_machine")) > 0;
		TSharedRef<SVerticalBox> Page = SNew(SVerticalBox)
			+ SVerticalBox::Slot().AutoHeight()
			[
				FWUi::Note(bHave ? LOCTEXT("SorterInBag", "Automatyczna sortownia jest w plecaku: postaw ją w garażu, a posegreguje śmieci sama.")
					: LOCTEXT("NoSorter", "W garażu nie ma automatycznej sortowni. Kupisz ją na rynku (komputer na działce) i postawisz tutaj."), FWStyle::Text)
			];
		if (bHave)
		{
			Page->AddSlot().AutoHeight().Padding(0, 10, 0, 0).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("PlaceSorter", "Postaw w garażu"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->PlaceFurniture(TEXT("furniture_sorting_machine"));
					}
				}, true)
			];
		}
		return Page;
	}
	if (!Sorter.IsValid())
	{
		return FWUi::Note(LOCTEXT("Loading", "Wczytywanie…"), FWStyle::Muted);
	}
	TSharedRef<SVerticalBox> Page = SNew(SVerticalBox);
	auto Add = [&Page](TSharedRef<SWidget> Widget, float Top = 6.f) { Page->AddSlot().AutoHeight().Padding(0, Top, 0, 0)[Widget]; };
	const int32 Queued = int32(FWUi::Num(Sorter, TEXT("queued"))), MaxQueue = int32(FWUi::Num(Sorter, TEXT("maxQueue")));
	const int32 Trash = Owned(TEXT("trash"));
	const int32 Room = MaxQueue - Queued;
	Add(FWUi::Heading(LOCTEXT("Queue", "Kolejka do segregacji")), 0.f);
	TSharedRef<SHorizontalBox> QueueRow = SNew(SHorizontalBox)
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 10, 0)[FWUi::Icon(PC, TEXT("trash"), 40.f)]
		+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
		[
			SNew(STextBlock).Font(FWStyle::Font(16, TEXT("Bold"))).ColorAndOpacity(FWStyle::Text)
			.Text(FText::FromString(FString::Printf(TEXT("Śmieci: %d / %d  (masz %d)"), Queued, MaxQueue, Trash)))
		]
		+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(LOCTEXT("TakeTrash", "Wyjmij"), [WeakThis]()
			{
				if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
				{
					TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
					P->SetStringField(TEXT("item"), TEXT("trash"));
					This->SendSorter(TEXT("sorter.take"), P);
				}
			}, false, Queued > 0)
		];
	for (const int32 Amount : { 1, 10, 0 })
	{
		const int32 Count = Amount == 0 ? FMath::Min(Trash, Room) : Amount;
		QueueRow->AddSlot().AutoWidth().VAlign(VAlign_Center).Padding(6, 0, 0, 0)
		[
			FWUi::SmallButton(Amount == 0 ? LOCTEXT("LoadAll", "Włóż wszystko") : FText::FromString(FString::Printf(TEXT("Włóż %d"), Amount)), [WeakThis, Count]()
			{
				if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
				{
					This->LoadSorter(Count);
				}
			}, Amount == 0, Count >= 1 && Count <= Trash && Count <= Room)
		];
	}
	Add(QueueRow, 6.f);
	double Started = 0.0;
	const bool bWorking = Sorter->TryGetNumberField(TEXT("startedAt"), Started);
	const double Total = FWUi::Num(Sorter, TEXT("sortSeconds"), 10.0) * 1000.0;
	Add(SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(bWorking ? FWStyle::Accent : Queued > 0 ? FWStyle::Error : FWStyle::Muted)
		.Text_Lambda([WeakThis, bWorking, Started, Total, Queued]()
		{
			TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
			if (bWorking && This)
			{
				return FText::FromString(FString::Printf(TEXT("Segreguje: %d s do następnego przedmiotu"), int32(FMath::CeilToDouble(FMath::Max(Started + Total - This->ServerNow(), 0.0) / 1000.0))));
			}
			return Queued > 0 ? LOCTEXT("SorterFull", "Sortownia jest pełna: wyjmij posegregowane przedmioty, aby segregowała dalej.")
				: LOCTEXT("SorterEmpty", "Włóż śmieci, a sortownia zacznie segregować.");
		}), 10.f);
	if (bWorking)
	{
		Add(FWUi::Bar(TAttribute<float>::CreateLambda([WeakThis, Started, Total]()
			{
				TSharedPtr<SFWGarageWindow> This = WeakThis.Pin();
				return This ? float(FMath::Clamp((This->ServerNow() - Started) / Total, 0.0, 1.0)) : 0.f;
			}), FWStyle::Accent), 4.f);
	}
	Add(FWUi::Heading(FText::FromString(FString::Printf(TEXT("Posegregowane  %d / %d"), int32(FWUi::Num(Sorter, TEXT("storedCount"))), int32(FWUi::Num(Sorter, TEXT("maxStored")))))), 18.f);
	const TArray<TSharedPtr<FJsonObject>> Stored = FWUi::Objects(Sorter, TEXT("stored"));
	if (Stored.Num() == 0)
	{
		Add(FWUi::Note(LOCTEXT("Empty", "pusto"), FWStyle::Muted), 4.f);
	}
	for (const TSharedPtr<FJsonObject>& Stack : Stored)
	{
		const FString Id = FWUi::Str(Stack, TEXT("id"));
		Add(SNew(SHorizontalBox)
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center).Padding(0, 0, 8, 0)[FWUi::Icon(PC, Id, 30.f)]
			+ SHorizontalBox::Slot().FillWidth(1.f).VAlign(VAlign_Center)
			[
				SNew(STextBlock).Font(FWStyle::Font(13)).ColorAndOpacity(FWStyle::Text)
				.Text(FText::FromString(FString::Printf(TEXT("%s  ×%d"), *FWUi::Str(Stack, TEXT("name")), int32(FWUi::Num(Stack, TEXT("quantity"))))))
			]
			+ SHorizontalBox::Slot().AutoWidth().VAlign(VAlign_Center)
			[
				FWUi::SmallButton(LOCTEXT("Take", "Wyjmij"), [WeakThis, Id]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						TSharedPtr<FJsonObject> P = MakeShared<FJsonObject>();
						P->SetStringField(TEXT("item"), Id);
						This->SendSorter(TEXT("sorter.take"), P);
					}
				})
			], 4.f);
	}
	if (Stored.Num() > 0)
	{
		Add(SNew(SBox).HAlign(HAlign_Left)
			[
				FWUi::SmallButton(LOCTEXT("TakeAll", "Wyjmij wszystko"), [WeakThis]()
				{
					if (TSharedPtr<SFWGarageWindow> This = WeakThis.Pin())
					{
						This->SendSorter(TEXT("sorter.take"), MakeShared<FJsonObject>());
					}
				}, true)
			], 8.f);
	}
	TArray<FString> Chances;
	for (const TSharedPtr<FJsonObject>& Drop : FWUi::Objects(Sorter, TEXT("drops")))
	{
		Chances.Add(FString::Printf(TEXT("%s %d%%"), *FWUi::Str(Drop, TEXT("name")).ToLower(), int32(FWUi::Num(Drop, TEXT("percent")))));
	}
	Add(FWUi::Note(FText::FromString(FString::Printf(TEXT("Sortownia segreguje sama, także gdy cię nie ma: worek w %d s, 5 XP za każdy wyjęty przedmiot. Mieści %d worków w kolejce i %d posegregowanych przedmiotów; gdy jest pełna, staje, dopóki czegoś nie wyjmiesz.\nZ worka: %s."),
		int32(FWUi::Num(Sorter, TEXT("sortSeconds"))), MaxQueue, int32(FWUi::Num(Sorter, TEXT("maxStored"))), *FString::Join(Chances, TEXT(", ")))), FWStyle::Muted), 16.f);
	return Page;
}

#undef LOCTEXT_NAMESPACE
