#pragma once

#include "CoreMinimal.h"

class UAudioComponent;
class USceneComponent;
class USoundAttenuation;
class USoundBase;
class USoundConcurrency;

/**
 * The game's sounds (made by Tools/make_audio.py, imported into /Game/External/Audio with their mix: sound
 * classes, attenuations, voice limits). Every call is safe when the sounds are not set up: nothing plays.
 */
namespace FWAudio
{
	// A sound by name, e.g. "build_place"; with Variants > 0 a random one of "<Name>_1".."<Name>_<Variants>"
	USoundBase* Sound(const FString& Name, int32 Variants = 0);
	USoundAttenuation* Attenuation(const TCHAR* Name);
	USoundConcurrency* Concurrency(const TCHAR* Name);

	// Not placed in the world (interface, the player's own footsteps)
	void Play2D(const UObject* WorldContext, const FString& Name, float Volume = 1.f, int32 Variants = 0);
	// At a point, fading with distance (ATT_Machine unless told otherwise)
	void PlayAt(const UObject* WorldContext, const FString& Name, const FVector& Where, float Volume = 1.f, int32 Variants = 0,
		const TCHAR* AttenuationName = TEXT("ATT_Machine"), const TCHAR* ConcurrencyName = TEXT("SCON_Machine"));
	// A looping sound held by a component; null when the sound is missing
	UAudioComponent* Loop(USceneComponent* Parent, const FString& Name, float Volume, const TCHAR* AttenuationName = nullptr);

	// Applies the master volume from the settings (0..1) to the Master sound class
	void ApplyMasterVolume(float Volume);
}
