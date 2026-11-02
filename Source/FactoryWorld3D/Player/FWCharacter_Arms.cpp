// The first-person arms: the item held, fingers, gestures, the body's animation and head bob.
#include "Player/FWCharacter.h"
#include "Camera/CameraComponent.h"
#include "Animation/AnimSequence.h"
#include "Components/CapsuleComponent.h"
#include "Components/DecalComponent.h"
#include "Components/PoseableMeshComponent.h"
#include "Engine/StaticMeshActor.h"
#include "Game/FWArt.h"
#include "Kismet/GameplayStatics.h"
#include "Components/SkeletalMeshComponent.h"
#include "Engine/SkeletalMesh.h"
#include "TwoBoneIK.h"
#include "Components/InstancedStaticMeshComponent.h"
#include "Components/StaticMeshComponent.h"
#include "EnhancedInputComponent.h"
#include "EnhancedInputSubsystems.h"
#include "Engine/LocalPlayer.h"
#include "Engine/StaticMesh.h"
#include "FWPlayerController.h"
#include "Game/FWAudio.h"
#include "Game/FWCatalog.h"
#include "GameFramework/CharacterMovementComponent.h"
#include "InputAction.h"
#include "InputMappingContext.h"
#include "InputModifiers.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "UObject/ConstructorHelpers.h"
#include "World/FWPlot.h"

#define LOCTEXT_NAMESPACE "FWCharacter"

#include "FWCharacterHelpers.h"

void AFWCharacter::UpdateHeld(float DeltaSeconds)
{
	const AFWPlayerController* PC = GetFWController();
	// Carrying a piece of the garage's furniture the hands are free (the piece shows as its ghost on the floor)
	const FString Selected = !GaragePlacing.IsEmpty() ? FString() : PC ? PC->GetSelectedItem() : FString();
	// A new slot: down with the old item, then up with the new one
	if (Selected != HeldItem && EquipTime < 0.f)
	{
		EquipTime = 0.f;
	}
	if (EquipTime >= 0.f)
	{
		const float Before = EquipTime;
		EquipTime += DeltaSeconds;
		if (Before < 0.18f && EquipTime >= 0.18f)
		{
			HeldItem = Selected;
			UStaticMesh* Model = HeldItem.IsEmpty() || HeldItem.StartsWith(TEXT("tool_")) ? nullptr : FWArt::ItemMesh(HeldItem);
			if (Held)
			{
				Held->SetStaticMesh(Model);
				if (Model)
				{
					// About 13 cm across, whatever the model's size
					const float Scale = 6.5f / FMath::Max(1.f, Model->GetBounds().SphereRadius);
					Held->SetWorldScale3D(FVector(Scale));
				}
			}
		}
		if (EquipTime >= 0.5f)
		{
			EquipTime = -1.f;
		}
	}
	// The pickaxe or the axe on the hotbar is held in the right hand (striking a tree or a rock takes out the tool for
	// it, whatever is selected)
	const bool bPickaxe = HeldItem.StartsWith(TEXT("tool_pickaxe"));
	const bool bAxe = HeldItem.StartsWith(TEXT("tool_axe"));
	if (bPickaxe || bAxe)
	{
		ToolShown = 1.f;
		UStaticMesh* InHand = bAxe && AxeMesh ? AxeMesh.Get() : PickaxeMesh.Get();
		if (Tool && InHand && Hold != EHold::Strike && Gesture != EGesture::Swing && Tool->GetStaticMesh() != InHand)
		{
			Tool->SetStaticMesh(InHand);
		}
	}
	if (Held)
	{
		const bool bShow = Held->GetStaticMesh() != nullptr && ToolShown <= 0.f;
		Held->SetVisibility(bShow);
		if (bShow)
		{
			// Between the hands, a little above them, turning slowly like a model on a table
			HeldSpin += DeltaSeconds * 25.f;
			const FVector Hands = (Arms->GetBoneLocation(TEXT("hand_l")) + Arms->GetBoneLocation(TEXT("hand_r"))) * 0.5f;
			const FVector Middle = Held->GetStaticMesh()->GetBounds().Origin * Held->GetComponentScale();
			const FRotator Turn(0.f, GetControlRotation().Yaw + HeldSpin, 0.f);
			Held->SetWorldLocationAndRotation(Hands + Camera->GetUpVector() * 7.f + Camera->GetForwardVector() * 4.f - Turn.RotateVector(Middle), Turn);
		}
	}
}

void AFWCharacter::CurlFingers(int32 Side, float Amount)
{
	// Each finger joint bends about its own Z axis (the bind pose is already a little bent); the thumb closes less
	static const TCHAR* Fingers[] = { TEXT("index"), TEXT("middle"), TEXT("ring"), TEXT("pinky"), TEXT("thumb") };
	const TCHAR* Suffix = Side == 0 ? TEXT("_l") : TEXT("_r");
	const FReferenceSkeleton& Skeleton = Arms->GetSkinnedAsset()->GetRefSkeleton();
	const TArray<FTransform>& RefPose = Skeleton.GetRefBonePose();
	const float Sign = Side == 0 ? 1.f : -1.f;
	for (const TCHAR* Finger : Fingers)
	{
		const bool bThumb = FCString::Strcmp(Finger, TEXT("thumb")) == 0;
		for (int32 Joint = bThumb ? 2 : 1; Joint <= 3; ++Joint)
		{
			const FName Bone(*FString::Printf(TEXT("%s_0%d%s"), Finger, Joint, Suffix));
			const int32 Index = Skeleton.FindBoneIndex(Bone);
			if (Index == INDEX_NONE)
			{
				continue;
			}
			const FName Parent = Skeleton.GetBoneName(Skeleton.GetParentIndex(Index));
			// More at the knuckle, less at the tip, and the pinky closes most
			const float Degrees = (bThumb ? 30.f : (Joint == 1 ? 62.f : Joint == 2 ? 75.f : 45.f)) * Amount;
			FTransform Local = RefPose[Index];
			Local.SetRotation(Local.GetRotation() * FQuat(FVector::UpVector, FMath::DegreesToRadians(Sign * Degrees)));
			Arms->SetBoneTransformByName(Bone, Local * Arms->GetBoneTransformByName(Parent, EBoneSpaces::ComponentSpace), EBoneSpaces::ComponentSpace);
		}
	}
}

void AFWCharacter::PlayGesture(EGesture InGesture)
{
	Gesture = InGesture;
	GestureTime = 0.f;
	bStruck = false;
}

void AFWCharacter::FWGesture(const FString& Name)
{
	if (Name == TEXT("mine"))
	{
		// Holds the pickaxe and swings as when mining
		ToolShown = 1.f;
	}
	PlayGesture(Name == TEXT("place") ? EGesture::Place : Name == TEXT("rotate") ? EGesture::Rotate
		: Name == TEXT("pickup") ? EGesture::Pickup : Name == TEXT("swing") ? EGesture::Swing : EGesture::None);
}

void AFWCharacter::UpdateArms(float DeltaSeconds)
{
	if (!bArmsReady || !Arms)
	{
		return;
	}
	const float Time = GetWorld()->GetTimeSeconds();
	const float Speed = GetCharacterMovement()->IsMovingOnGround() ? GetVelocity().Size2D() : 0.f;
	const float Moving = FMath::Clamp(Speed / WalkSpeed, 0.f, 1.6f);

	// The arms trail a turn of the head a little and catch up
	const FRotator View = GetControlRotation();
	const FRotator Turned = (View - LastView).GetNormalized();
	LastView = View;
	ViewLag = FMath::Vector2DInterpTo(ViewLag + FVector2D(Turned.Yaw, Turned.Pitch) * 0.6f, FVector2D::ZeroVector, DeltaSeconds, 8.f);
	ViewLag = ViewLag.ClampAxes(-6.f, 6.f);

	// Hands a little higher and closer in build mode, as if holding something up to set it down
	BuildRaise = FMath::FInterpTo(BuildRaise, bBuildMode || (Held && Held->IsVisible()) ? 1.f : 0.f, DeltaSeconds, 6.f);

	// The hands come up when there is something to do with them and drop out of view when there is not, as a
	// player walking with empty hands does not hold them up in front of their eyes
	const bool bBusy = bBuildMode || (Held && Held->IsVisible()) || Gesture != EGesture::None || Hold != EHold::None || ToolShown > 0.f
		|| EquipTime >= 0.f;
	// Empty hands are held up a little, in sight at the bottom of the view (out of view they looked missing)
	ArmsUp = FMath::FInterpTo(ArmsUp, 1.f, DeltaSeconds, 9.f);
	IdleHands = FMath::FInterpTo(IdleHands, bBusy ? 0.f : 1.f, DeltaSeconds, bBusy ? 9.f : 3.f);

	// Gestures: offsets of the right (index 1) and left hand over their length
	FVector Gest[2] = { FVector::ZeroVector, FVector::ZeroVector };
	float Length = 0.f;
	switch (Gesture)
	{
	case EGesture::Place: Length = 0.35f; break;
	case EGesture::Rotate: Length = 0.25f; break;
	case EGesture::Pickup: Length = 0.45f; break;
	case EGesture::Swing: Length = SwingLength; break;
	default: break;
	}
	if (Gesture != EGesture::None)
	{
		GestureTime += DeltaSeconds;
		const float T = FMath::Clamp(GestureTime / Length, 0.f, 1.f);
		const float Out = FMath::Sin(T * PI);
		switch (Gesture)
		{
		case EGesture::Place:  // both hands push forward and down, and come back
			Gest[0] = Gest[1] = FVector(0, 14.f, -8.f) * Out;
			break;
		case EGesture::Rotate:  // a quick turn of the right hand
			Gest[1] = FVector(-8.f, 4.f, 3.f) * Out;
			break;
		case EGesture::Pickup:  // reach forward and down, pull back to the chest
			Gest[0] = Gest[1] = FVector(0, 18.f * FMath::Sin(FMath::Min(T * 2.f, 1.f) * PI * 0.5f) - 22.f * FMath::Max(0.f, T - 0.5f), -14.f * Out);
			break;
		case EGesture::Swing:  // the right hand up and back, then down and forward in a strike
		{
			const float Raise = T < 0.45f ? FMath::SmoothStep(0.f, 1.f, T / 0.45f) : 1.f - FMath::SmoothStep(0.f, 1.f, (T - 0.45f) / 0.25f);
			const float Strike = T < 0.45f ? 0.f : FMath::Sin(FMath::Clamp((T - 0.45f) / 0.55f, 0.f, 1.f) * PI);
			Gest[1] = FVector(-4.f, -10.f, 26.f) * Raise + FVector(4.f, 20.f, -6.f) * Strike;
			Gest[0] = FVector(0, -4.f, -6.f) * Strike;
			SwingRaise = Raise;
			SwingStrike = Strike;
			break;
		}
		default:
			break;
		}
		if (Gesture == EGesture::Swing && !bStruck && T >= 0.62f)
		{
			bStruck = true;
			Strike();
			if (Hold == EHold::Strike)
			{
				OnImpact();
			}
		}
		if (T >= 1.f)
		{
			Gesture = EGesture::None;
		}
	}

	// Where the hands rest in the mesh's space (+Y forward, +X the character's left, Z up from the feet)
	if (Gesture != EGesture::Swing)
	{
		SwingRaise = SwingStrike = 0.f;
	}
	const float Breath = FMath::Sin(Time * 1.6f) * 0.6f;
	const float StepBob = FMath::Abs(FMath::Sin(BobPhase)) * 1.8f * Moving;
	const float StepSway = FMath::Cos(BobPhase) * 1.4f * Moving;
	const TCHAR* Sides[2][3] = { { TEXT("upperarm_l"), TEXT("lowerarm_l"), TEXT("hand_l") }, { TEXT("upperarm_r"), TEXT("lowerarm_r"), TEXT("hand_r") } };
	for (int32 Side = 0; Side < 2; ++Side)
	{
		const float Sign = Side == 0 ? 1.f : -1.f;
		FVector Hand(Sign * (21.f - 4.f * BuildRaise), 48.f + 4.f * BuildRaise, 146.f + 6.f * BuildRaise);
		Hand += FVector(StepSway * Sign * 0.3f + ViewLag.X * -0.8f, 0.f, Breath - StepBob - ViewLag.Y * 0.8f);
		Hand += Gest[Side];
		// Empty hands: a little higher and wider apart, so both show at the bottom corners
		Hand += FVector(Sign * 2.f, 0.f, 4.f) * IdleHands;
		// Lowered: down along the body and a little back, swinging gently with the steps
		Hand += FVector(Sign * 6.f, -16.f + StepSway * Sign * 2.f, -62.f) * (1.f - ArmsUp);
		// Changing items: the hands dip out of view and come back
		if (EquipTime >= 0.f)
		{
			const float Dip = EquipTime < 0.18f ? FMath::SmoothStep(0.f, 0.18f, EquipTime) : 1.f - FMath::SmoothStep(0.18f, 0.5f, EquipTime);
			Hand += FVector(0.f, -6.f, -26.f) * Dip;
		}
		// Elbows out and down
		const FVector Elbow(Sign * 45.f, 18.f, 112.f);
		FTransform Upper = ArmRef[Side].Upper;
		FTransform Lower = ArmRef[Side].Lower;
		FTransform End = ArmRef[Side].Hand;
		const FQuat LowerRef = Lower.GetRotation();
		AnimationCore::SolveTwoBoneIK(Upper, Lower, End, Elbow, Hand, false, 1.0, 1.0);
		// The hand turns with the forearm, then twists about it, palms turned in towards each other
		const FQuat Delta = Lower.GetRotation() * LowerRef.Inverse();
		const FVector Forearm = (End.GetLocation() - Lower.GetLocation()).GetSafeNormal();
		End.SetRotation(FQuat(Forearm, FMath::DegreesToRadians(Sign * 40.f)) * Delta * ArmRef[Side].Hand.GetRotation());
		Arms->SetBoneTransformByName(Sides[Side][0], Upper, EBoneSpaces::ComponentSpace);
		Arms->SetBoneTransformByName(Sides[Side][1], Lower, EBoneSpaces::ComponentSpace);
		Arms->SetBoneTransformByName(Sides[Side][2], End, EBoneSpaces::ComponentSpace);
	}
	// The axe or pickaxe comes out for striking and goes away a moment after
	const bool bMining = Hold == EHold::Strike || Gesture == EGesture::Swing;
	ToolShown = bMining ? 1.f : FMath::Max(0.f, ToolShown - DeltaSeconds * 1.5f);
	if (Tool)
	{
		Tool->SetVisibility(ToolShown > 0.f);
		// The handle in the fist, pointing up and forward over the hand at rest, tipped back at the top of the
		// swing and driven forward and down into the strike; the head always points ahead
		const FVector Forward = Camera->GetForwardVector();
		const FVector Right = Camera->GetRightVector();
		const FVector Up = Camera->GetUpVector();
		// At rest the handle stands up and leans forward and in, the head across the view (pick to the right), so the
		// tool reads as a pickaxe or an axe rather than a stick seen end on; in a swing the head points ahead
		const float Swinging = FMath::Max(SwingRaise, SwingStrike);
		FVector Handle = Forward * 0.5f - Right * 0.3f + Up;
		Handle = FMath::Lerp(Handle, -Forward * 0.25f - Right * 0.1f + Up, SwingRaise);
		Handle = FMath::Lerp(Handle, Forward - Right * 0.1f - Up * 0.1f, SwingStrike);
		Handle.Normalize();
		const FVector Aim = FMath::Lerp(Right * 0.8f + Forward * 0.6f, Forward, Swinging);
		FVector Head = Aim - Handle * FVector::DotProduct(Aim, Handle);
		Head = Head.IsNearlyZero() ? Up : Head.GetSafeNormal();
		const FVector Grip = Arms->GetBoneLocation(TEXT("hand_r")) - Handle * 3.f;
		Tool->SetWorldLocationAndRotation(Grip, FRotationMatrix::MakeFromZX(Handle, Head).Rotator());
	}
	// Relaxed hands, half closed and breathing a little, more closed in build mode; the right one a fist round the
	// handle
	const float Relaxed = 0.45f + 0.04f * FMath::Sin(Time * 0.9f) + 0.1f * BuildRaise;
	CurlFingers(0, Relaxed);
	CurlFingers(1, ToolShown > 0.f && Tool ? 1.f : Relaxed);
	// The jolt of a strike: a few millimetres, gone in a fifth of a second
	ShakeTime = FMath::Max(0.f, ShakeTime - DeltaSeconds);
	ShakeOffset = ShakeTime > 0.f ? FMath::VRand() * 0.6f * (ShakeTime / 0.18f) : FVector::ZeroVector;
}

void AFWCharacter::UpdateBodyAnimation()
{
	if (!Body)
	{
		return;
	}
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	const float Speed = GetVelocity().Size2D();
	const FString Wanted = Movement->IsFalling() ? TEXT("Jump/MM_Fall_Loop") : Speed > 500.f ? TEXT("Jog/MF_Unarmed_Jog_Fwd")
		: Speed > 40.f ? TEXT("Walk/MF_Unarmed_Walk_Fwd") : TEXT("MM_Idle");
	if (Wanted != BodyAnimation)
	{
		BodyAnimation = Wanted;
		const FString Name = FPaths::GetBaseFilename(Wanted);
		if (UAnimSequence* Animation = LoadObject<UAnimSequence>(nullptr, *FString::Printf(TEXT("/Game/Characters/Mannequins/Anims/Unarmed/%s.%s"), *Wanted, *Name), nullptr, LOAD_NoWarn | LOAD_Quiet))
		{
			Body->PlayAnimation(Animation, true);
		}
	}
}

void AFWCharacter::UpdateHeadBob(float DeltaSeconds)
{
	const UCharacterMovementComponent* Movement = GetCharacterMovement();
	const float Speed = Movement->IsMovingOnGround() ? GetVelocity().Size2D() : 0.f;
	// About two steps a second at walking speed, a few centimetres up and down
	BobPhase = Speed > 20.f ? BobPhase + DeltaSeconds * Speed / 70.f : FMath::FInterpTo(BobPhase, FMath::RoundToFloat(BobPhase / PI) * PI, DeltaSeconds, 6.f);
	const float Amount = FMath::Clamp(Speed / WalkSpeed, 0.f, 1.6f);
	const FVector Bob(0.f, FMath::Cos(BobPhase) * 0.8f * Amount, FMath::Abs(FMath::Sin(BobPhase)) * 2.2f * Amount);
	Camera->SetRelativeLocation(FVector(0.f, 0.f, EyeHeight - GetCapsuleComponent()->GetUnscaledCapsuleHalfHeight()) + Bob + ShakeOffset);
}

#undef LOCTEXT_NAMESPACE
