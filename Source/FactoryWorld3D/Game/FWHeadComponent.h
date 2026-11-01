#pragma once

#include "CoreMinimal.h"
#include "Components/PoseableMeshComponent.h"
#include "FWHeadComponent.generated.h"

/**
 * A MetaHuman head (Tools/make_metahumans.py) on the mannequin body it is attached to: every frame it takes the
 * body's bones it shares (spine, neck, head) and keeps its own face bones as built, so the neck bends with the body
 * and the still face rides on the head.
 */
UCLASS()
class UFWHeadComponent : public UPoseableMeshComponent
{
	GENERATED_BODY()

public:
	UFWHeadComponent();

	virtual void TickComponent(float DeltaTime, ELevelTick TickType, FActorComponentTickFunction* ThisTickFunction) override;
};
