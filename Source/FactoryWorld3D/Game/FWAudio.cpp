#include "Game/FWAudio.h"

#include "Components/AudioComponent.h"
#include "Kismet/GameplayStatics.h"
#include "Sound/SoundAttenuation.h"
#include "Sound/SoundBase.h"
#include "Sound/SoundClass.h"
#include "Sound/SoundConcurrency.h"

namespace FWAudio
{
	namespace
	{
		template <typename T>
		T* Load(const FString& Path)
		{
			// Remember misses too: a game without the sounds asks for them every step
			static TMap<FString, TWeakObjectPtr<UObject>> Cache;
			static TSet<FString> Missing;
			if (const TWeakObjectPtr<UObject>* Found = Cache.Find(Path))
			{
				if (Found->IsValid())
				{
					return Cast<T>(Found->Get());
				}
			}
			if (Missing.Contains(Path))
			{
				return nullptr;
			}
			T* Object = LoadObject<T>(nullptr, *Path, nullptr, LOAD_NoWarn | LOAD_Quiet);
			if (Object)
			{
				Object->AddToRoot();
				Cache.Add(Path, Object);
			}
			else
			{
				Missing.Add(Path);
			}
			return Object;
		}
	}

	USoundBase* Sound(const FString& Name, int32 Variants)
	{
		const FString Full = Variants > 0 ? FString::Printf(TEXT("%s_%d"), *Name, FMath::RandRange(1, Variants)) : Name;
		return Load<USoundBase>(FString::Printf(TEXT("/Game/External/Audio/%s.%s"), *Full, *Full));
	}

	USoundAttenuation* Attenuation(const TCHAR* Name)
	{
		return Name ? Load<USoundAttenuation>(FString::Printf(TEXT("/Game/External/Audio/Mix/%s.%s"), Name, Name)) : nullptr;
	}

	USoundConcurrency* Concurrency(const TCHAR* Name)
	{
		return Name ? Load<USoundConcurrency>(FString::Printf(TEXT("/Game/External/Audio/Mix/%s.%s"), Name, Name)) : nullptr;
	}

	void Play2D(const UObject* WorldContext, const FString& Name, float Volume, int32 Variants)
	{
		if (USoundBase* S = Sound(Name, Variants))
		{
			UGameplayStatics::PlaySound2D(WorldContext, S, Volume, FMath::FRandRange(0.96f, 1.04f));
		}
	}

	void PlayAt(const UObject* WorldContext, const FString& Name, const FVector& Where, float Volume, int32 Variants,
		const TCHAR* AttenuationName, const TCHAR* ConcurrencyName)
	{
		if (USoundBase* S = Sound(Name, Variants))
		{
			UGameplayStatics::PlaySoundAtLocation(WorldContext, S, Where, Volume, FMath::FRandRange(0.95f, 1.05f), 0.f,
				Attenuation(AttenuationName), Concurrency(ConcurrencyName));
		}
	}

	UAudioComponent* Loop(USceneComponent* Parent, const FString& Name, float Volume, const TCHAR* AttenuationName)
	{
		USoundBase* S = Sound(Name);
		if (!S || !Parent)
		{
			return nullptr;
		}
		UAudioComponent* Audio = NewObject<UAudioComponent>(Parent->GetOwner());
		Audio->SetupAttachment(Parent);
		Audio->SetSound(S);
		Audio->bAutoActivate = false;
		Audio->bAllowSpatialization = AttenuationName != nullptr;
		Audio->AttenuationSettings = Attenuation(AttenuationName);
		Audio->SetVolumeMultiplier(Volume);
		Audio->RegisterComponent();
		// Start somewhere in the loop, so copies of it never sound in step
		Audio->Play(FMath::FRandRange(0.f, 2.f));
		return Audio;
	}

	void ApplyMasterVolume(float Volume)
	{
		if (USoundClass* Master = Load<USoundClass>(TEXT("/Game/External/Audio/Mix/SC_Master.SC_Master")))
		{
			Master->Properties.Volume = FMath::Clamp(Volume, 0.f, 1.f);
		}
	}
}
