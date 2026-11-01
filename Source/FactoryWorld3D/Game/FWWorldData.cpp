#include "Game/FWWorldData.h"

#include "Misc/Compression.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

#define LOCTEXT_NAMESPACE "FWWorldData"

namespace FWWorldData
{
	namespace
	{
		struct FData
		{
			bool bTried = false;
			bool bLoaded = false;
			int32 Width = 0;
			int32 Height = 0;
			TArray<uint8> Biomes;
			TArray<uint8> Countries;
			TArray<FString> CountryNames;
			TArray<FTown> Towns;
		};

		bool Gunzip(const FString& File, TArray<uint8>& Out)
		{
			TArray<uint8> Packed;
			if (!FFileHelper::LoadFileToArray(Packed, *FPaths::Combine(FPaths::ProjectContentDir(), TEXT("External"), TEXT("WorldData"), File)) || Packed.Num() < 18)
			{
				return false;
			}
			// The gzip trailer ends with the unpacked size
			const int32 Size = int32(Packed[Packed.Num() - 4]) | int32(Packed[Packed.Num() - 3]) << 8 | int32(Packed[Packed.Num() - 2]) << 16 | int32(Packed[Packed.Num() - 1]) << 24;
			Out.SetNumUninitialized(Size);
			return FCompression::UncompressMemory(NAME_Gzip, Out.GetData(), Size, Packed.GetData(), Packed.Num());
		}

		FData& Get()
		{
			static FData Data;
			if (Data.bTried)
			{
				return Data;
			}
			Data.bTried = true;
			TArray<uint8> Raw;
			// "FWW1", width, height (u16), a byte per plot
			if (!Gunzip(TEXT("biomes.bin.gz"), Raw) || Raw.Num() < 8 || FMemory::Memcmp(Raw.GetData(), "FWW1", 4) != 0)
			{
				return Data;
			}
			Data.Width = Raw[4] | Raw[5] << 8;
			Data.Height = Raw[6] | Raw[7] << 8;
			Data.Biomes = TArray<uint8>(Raw.GetData() + 8, Raw.Num() - 8);
			// "FWC1", width, height, count (u16), a line "code\tname\tlabel x\tlabel y\tplots" each, a byte per plot
			if (Gunzip(TEXT("countries.bin.gz"), Raw) && Raw.Num() > 10 && FMemory::Memcmp(Raw.GetData(), "FWC1", 4) == 0)
			{
				int32 At = 10;
				const int32 Count = Raw[8] | Raw[9] << 8;
				for (int32 i = 0; i < Count && At < Raw.Num(); ++i)
				{
					int32 End = At;
					while (End < Raw.Num() && Raw[End] != '\n')
					{
						++End;
					}
					const FString Line = FString(FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(Raw.GetData() + At), End - At));
					TArray<FString> Fields;
					Line.ParseIntoArray(Fields, TEXT("\t"), false);
					Data.CountryNames.Add(Fields.Num() > 1 ? Fields[1] : FString());
					At = End + 1;
				}
				Data.Countries = TArray<uint8>(Raw.GetData() + At, Raw.Num() - At);
			}
			// "column\trow\tpopulation\tname" per line, largest first
			if (Gunzip(TEXT("towns.tsv.gz"), Raw))
			{
				const FString Text = FString(FUTF8ToTCHAR(reinterpret_cast<const ANSICHAR*>(Raw.GetData()), Raw.Num()));
				TArray<FString> Lines;
				Text.ParseIntoArrayLines(Lines);
				// Enough to label the map; the long tail of villages is left out
				for (int32 i = 0; i < FMath::Min(Lines.Num(), 6000); ++i)
				{
					TArray<FString> Fields;
					Lines[i].ParseIntoArray(Fields, TEXT("\t"), false);
					if (Fields.Num() >= 4)
					{
						Data.Towns.Add({ FIntPoint(FCString::Atoi(*Fields[0]), FCString::Atoi(*Fields[1])), FCString::Atoi(*Fields[2]), Fields[3] });
					}
				}
			}
			Data.bLoaded = Data.Biomes.Num() == Data.Width * Data.Height;
			return Data;
		}

		int32 Index(const FData& Data, FIntPoint Plot)
		{
			if (Plot.Y < 0 || Plot.Y >= Data.Height || Data.Width <= 0)
			{
				return -1;
			}
			return Plot.Y * Data.Width + ((Plot.X % Data.Width) + Data.Width) % Data.Width;
		}
	}

	bool IsLoaded()
	{
		return Get().bLoaded;
	}

	FIntPoint Size()
	{
		return FIntPoint(Get().Width, Get().Height);
	}

	int32 Biome(FIntPoint Plot)
	{
		const FData& Data = Get();
		const int32 I = Index(Data, Plot);
		return I >= 0 && Data.Biomes.IsValidIndex(I) ? Data.Biomes[I] : 0;
	}

	FString Country(FIntPoint Plot)
	{
		const FData& Data = Get();
		const int32 I = Index(Data, Plot);
		const int32 Country = I >= 0 && Data.Countries.IsValidIndex(I) ? Data.Countries[I] - 1 : -1;
		return Data.CountryNames.IsValidIndex(Country) ? Data.CountryNames[Country] : FString();
	}

	const TArray<FTown>& Towns()
	{
		return Get().Towns;
	}

	FText BiomeName(int32 Biome)
	{
		static const FText Names[] = { LOCTEXT("Ocean", "Ocean"), LOCTEXT("Lake", "Jezioro"), LOCTEXT("Forest", "Las"), LOCTEXT("Farmland", "Pola uprawne"),
			LOCTEXT("Grassland", "Łąki"), LOCTEXT("Desert", "Pustynia"), LOCTEXT("Mountains", "Góry"), LOCTEXT("Tundra", "Tundra"), LOCTEXT("Ice", "Lód"),
			LOCTEXT("City", "Miasto"), LOCTEXT("Coast", "Wybrzeże"), LOCTEXT("Lakeshore", "Brzeg jeziora"), LOCTEXT("Oasis", "Oaza") };
		return Biome >= 0 && Biome < UE_ARRAY_COUNT(Names) ? Names[Biome] : FText::AsNumber(Biome);
	}

	FText Geo(FIntPoint Plot)
	{
		// The Behrmann equal-area grid of the 2D game (world_data.gd to_geo)
		const FData& Data = Get();
		constexpr double RadiusKm = 6371.0072, PlotKm = 5.0;
		const double Cos30 = FMath::Cos(PI / 6.0);
		const double Lon = ((Plot.X + 0.5) * PlotKm - Data.Width * PlotKm / 2.0) / (RadiusKm * Cos30);
		const double SinLat = FMath::Clamp((Data.Height * PlotKm / 2.0 - (Plot.Y + 0.5) * PlotKm) * Cos30 / RadiusKm, -1.0, 1.0);
		const double Lat = FMath::RadiansToDegrees(FMath::Asin(SinLat));
		const double LonDeg = FMath::Fmod(FMath::RadiansToDegrees(Lon) + 540.0, 360.0) - 180.0;
		FNumberFormattingOptions One;
		One.MinimumFractionalDigits = One.MaximumFractionalDigits = 1;
		return FText::Format(LOCTEXT("Geo", "{0}° {1}, {2}° {3}"), FText::AsNumber(FMath::Abs(Lat), &One), FText::FromString(Lat >= 0 ? TEXT("N") : TEXT("S")),
			FText::AsNumber(FMath::Abs(LonDeg), &One), FText::FromString(LonDeg >= 0 ? TEXT("E") : TEXT("W")));
	}
}

#undef LOCTEXT_NAMESPACE
