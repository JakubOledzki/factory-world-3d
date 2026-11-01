#include "Game/FWHeadComponent.h"

namespace
{
	// The MetaHuman head is a little bigger than the mannequin's, which our hair is made around
	constexpr float HeadScale = 1.f;
}

UFWHeadComponent::UFWHeadComponent()
{
	PrimaryComponentTick.bCanEverTick = true;
	PrimaryComponentTick.TickGroup = TG_PostUpdateWork;
}

void UFWHeadComponent::TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction)
{
	Super::TickComponent(DeltaTime, TickType, ThisTickFunction);
	if (USkeletalMeshComponent* Body = Cast<USkeletalMeshComponent>(GetAttachParent()); Body && IsVisible())
	{
		CopyPoseFromSkeletalComponent(Body);
		const int32 Head = GetBoneIndex(TEXT("head"));
		if (BoneSpaceTransforms.IsValidIndex(Head))
		{
			BoneSpaceTransforms[Head].SetScale3D(FVector(HeadScale));
			MarkRefreshTransformDirty();
		}
	}
}
