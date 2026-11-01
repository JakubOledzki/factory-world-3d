#include "World/FWGarageRoom.h"

#include "Components/PointLightComponent.h"
#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Engine/Texture2D.h"
#include "Engine/World.h"
#include "Game/FWArt.h"
#include "Materials/MaterialInstanceDynamic.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UObject/ConstructorHelpers.h"

namespace FWGarageLayout
{
	// The room inside (cm) and its cells, as the server keeps it
	constexpr float Width = 800.f;
	constexpr float Depth = 600.f;
	constexpr float Height = 320.f;
	constexpr float Cell = 50.f;

	// The surface of the floor by its upgrade level: a texture set and a tint (raw concrete to marble)
	TPair<FString, FLinearColor> FloorLook(int32 Level)
	{
		switch (Level)
		{
		case 0: return { TEXT("concrete_floor_worn_001"), FLinearColor(0.62f, 0.6f, 0.56f) };
		case 1: return { TEXT("concrete_floor_worn_001"), FLinearColor(0.85f, 0.84f, 0.8f) };
		case 2: return { TEXT("concrete_floor_worn_001"), FLinearColor(1.05f, 1.04f, 1.0f) };
		case 3: return { FString(), FLinearColor(0.2f, 0.26f, 0.24f) };
		case 4: return { TEXT("castle_wall_slates"), FLinearColor(0.95f, 0.95f, 0.95f) };
		case 5: return { TEXT("castle_wall_slates"), FLinearColor(0.62f, 0.62f, 0.64f) };
		case 6: return { TEXT("wood_table_001"), FLinearColor(1.f, 0.95f, 0.88f) };
		case 7: return { TEXT("weathered_planks"), FLinearColor(1.05f, 1.f, 0.9f) };
		case 8: return { TEXT("wood_table_001"), FLinearColor(0.68f, 0.52f, 0.38f) };
		case 9: return { TEXT("concrete_floor_worn_001"), FLinearColor(1.35f, 1.3f, 1.22f) };
		default: return { FString(), FLinearColor(0.78f, 0.77f, 0.74f) };
		}
	}

	// The walls by their level: bare blocks, plaster, white, panelling, tiles, clinker brick, oak, concrete, white
	TPair<FString, FLinearColor> WallLook(int32 Level)
	{
		switch (Level)
		{
		case 0: return { TEXT("concrete_floor_worn_001"), FLinearColor(0.55f, 0.55f, 0.53f) };
		case 1: return { TEXT("concrete_floor_worn_001"), FLinearColor(1.0f, 0.97f, 0.9f) };
		case 2: return { FString(), FLinearColor(0.72f, 0.72f, 0.69f) };
		case 3: return { TEXT("weathered_planks"), FLinearColor(1.f, 0.95f, 0.85f) };
		case 4: return { TEXT("castle_wall_slates"), FLinearColor(1.25f, 1.25f, 1.25f) };
		case 5: return { TEXT("red_brick_03"), FLinearColor(1.f, 1.f, 1.f) };
		case 6: return { TEXT("wood_table_001"), FLinearColor(0.8f, 0.65f, 0.5f) };
		case 8: return { TEXT("concrete_floor_worn_001"), FLinearColor(1.2f, 1.2f, 1.18f) };
		default: return { FString(), FLinearColor(0.8f, 0.8f, 0.78f) };
		}
	}

	FString Model(const FString& Kind)
	{
		FString Short = Kind;
		Short.RemoveFromStart(TEXT("furniture_"));
		return TEXT("garage_") + Short;
	}
}

AFWGarageRoom::AFWGarageRoom()
{
	RootComponent = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	static ConstructorHelpers::FObjectFinder<UMaterialInterface> Scan(TEXT("/Game/Materials/M_FWScan.M_FWScan"));
	ScanMaterial = Scan.Object;
}

UStaticMeshComponent* AFWGarageRoom::Piece(const TCHAR* Model, const FTransform& Transform, bool bCollide)
{
	UStaticMesh* Mesh = FWArt::Factory(Model);
	if (!Mesh)
	{
		Mesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Cube.Cube"));
	}
	UStaticMeshComponent* Component = NewObject<UStaticMeshComponent>(this);
	Component->SetStaticMesh(Mesh);
	Component->SetupAttachment(RootComponent);
	Component->SetRelativeTransform(Transform);
	Component->SetCollisionEnabled(bCollide ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::NoCollision);
	Component->SetCollisionResponseToAllChannels(ECR_Block);
	Component->RegisterComponent();
	Pieces.Add(Component);
	return Component;
}

UMaterialInstanceDynamic* AFWGarageRoom::Surface(const FString& Set, const FLinearColor& Tint)
{
	UMaterialInstanceDynamic* Material = UMaterialInstanceDynamic::Create(ScanMaterial, this);
	// Plain paint is a white base with the worn concrete's roughness; otherwise the photographed set
	const FString Base = Set.IsEmpty() ? FString(TEXT("concrete_floor_worn_001")) : Set;
	auto Load = [](const FString& Id, const TCHAR* Kind) { return FWArt::Texture(*FString::Printf(TEXT("/Game/External/Textures/%s/%s_%s_2k.%s_%s_2k"), *Id, *Id, Kind, *Id, Kind)); };
	if (!Set.IsEmpty())
	{
		if (UTexture2D* Color = Load(Set, TEXT("diff")))
		{
			Material->SetTextureParameterValue(TEXT("BaseColor"), Color);
		}
		if (UTexture2D* Normal = Load(Set, TEXT("nor_gl")))
		{
			Material->SetTextureParameterValue(TEXT("Normal"), Normal);
		}
	}
	else if (UTexture2D* White = LoadObject<UTexture2D>(nullptr, TEXT("/Engine/EngineResources/WhiteSquareTexture.WhiteSquareTexture")))
	{
		Material->SetTextureParameterValue(TEXT("BaseColor"), White);
	}
	if (UTexture2D* Arm = Load(Base, TEXT("arm")))
	{
		Material->SetTextureParameterValue(TEXT("ARM"), Arm);
	}
	Material->SetScalarParameterValue(TEXT("MetalScale"), 0.f);
	Material->SetVectorParameterValue(TEXT("Tint"), Tint);
	return Material;
}

FString AFWGarageRoom::NameOf(const FString& Kind) const
{
	const TSharedPtr<FJsonObject>* Info = nullptr;
	FString Name;
	if (Furniture.IsValid() && Furniture->TryGetObjectField(Kind, Info) && (*Info)->TryGetStringField(TEXT("name"), Name))
	{
		return Name;
	}
	return Kind;
}

FTransform AFWGarageRoom::Entrance() const
{
	// Inside the door (the room's -Y side), looking in
	return FTransform(FRotator(0.f, 90.f, 0.f), GetActorTransform().TransformPosition(FVector(0.f, -FWGarageLayout::Depth / 2 + 110.f, 100.f)));
}

void AFWGarageRoom::Shell()
{
	Floor = Piece(TEXT("garage_floor"), FTransform::Identity);
	Walls = Piece(TEXT("garage_walls"), FTransform::Identity);
	Piece(TEXT("garage_ceiling"), FTransform::Identity);
	Picks.Add(Piece(TEXT("garage_door"), FTransform::Identity), TPair<FString, FString>(TEXT("door"), FString()));
	// The notice board on the right-hand wall by the door, where the upgrades are planned
	Picks.Add(Piece(TEXT("garage_board"), FTransform(FRotator(0.f, -90.f, 0.f), FVector(FWGarageLayout::Width / 2 - 4.f, -150.f, 0.f))),
		TPair<FString, FString>(TEXT("board"), FString()));
}

void AFWGarageRoom::Upgrades(const TSharedPtr<FJsonObject>& Levels)
{
	auto Level = [&Levels](const TCHAR* Track) { double Value = 0.0; return Levels.IsValid() && Levels->TryGetNumberField(Track, Value) ? int32(Value) : 0; };
	const TPair<FString, FLinearColor> FloorLook = FWGarageLayout::FloorLook(Level(TEXT("floor")));
	const TPair<FString, FLinearColor> WallLook = FWGarageLayout::WallLook(Level(TEXT("walls")));
	if (Floor)
	{
		Floor->SetMaterial(0, Surface(FloorLook.Key, FloorLook.Value));
	}
	if (Walls)
	{
		Walls->SetMaterial(0, Surface(WallLook.Key, WallLook.Value));
	}

	// Light: a bare bulb, then more and brighter lamps, colder as they get modern
	const int32 Light = Level(TEXT("lighting"));
	const int32 Lamps = Light == 0 ? 1 : Light <= 2 ? 2 : 4;
	for (int32 i = 0; i < Lamps; ++i)
	{
		const FVector At = Lamps == 1 ? FVector(0.f, 30.f, 0.f) : Lamps == 2 ? FVector(i == 0 ? -200.f : 200.f, 30.f, 0.f)
			: FVector(i % 2 == 0 ? -200.f : 200.f, i < 2 ? -120.f : 150.f, 0.f);
		// The shade must not shadow its own bulb
		Piece(TEXT("garage_lamp"), FTransform(At + FVector(0.f, 0.f, FWGarageLayout::Height)), false)->SetCastShadow(false);
		UPointLightComponent* Bulb = NewObject<UPointLightComponent>(this);
		Bulb->SetupAttachment(RootComponent);
		Bulb->SetMobility(EComponentMobility::Movable);
		Bulb->SetRelativeLocation(At + FVector(0.f, 0.f, FWGarageLayout::Height - 125.f));
		Bulb->SetIntensityUnits(ELightUnits::Lumens);
		Bulb->SetIntensity(260000.f + 90000.f * Light);
		Bulb->SetUseTemperature(true);
		Bulb->SetTemperature(2700.f + 600.f * Light);
		Bulb->SetAttenuationRadius(1400.f);
		Bulb->SetSourceRadius(4.f);
		Bulb->RegisterComponent();
		Lights.Add(Bulb);
	}

	// Light bounced off the walls, which the bulbs alone leave too dark to work in: a soft fill without shadows
	UPointLightComponent* Fill = NewObject<UPointLightComponent>(this);
	Fill->SetupAttachment(RootComponent);
	Fill->SetMobility(EComponentMobility::Movable);
	Fill->SetRelativeLocation(FVector(0.f, 0.f, 160.f));
	Fill->SetIntensityUnits(ELightUnits::Lumens);
	Fill->SetIntensity(90000.f + 30000.f * Light);
	Fill->SetUseTemperature(true);
	Fill->SetTemperature(3200.f + 500.f * Light);
	Fill->SetAttenuationRadius(1200.f);
	Fill->SetSourceRadius(250.f);
	Fill->SetCastShadows(false);
	Fill->SetSpecularScale(0.f);
	Fill->RegisterComponent();
	Lights.Add(Fill);

	// More shelves on the back wall, above the furniture
	const int32 Shelves = Level(TEXT("shelves"));
	for (int32 i = 0; i < Shelves; ++i)
	{
		const float X = -300.f + i * 150.f;
		Piece(TEXT("garage_shelf"), FTransform(FRotator::ZeroRotator, FVector(X, FWGarageLayout::Depth / 2 - 28.f, 200.f), FVector(0.7f, 1.f, 0.32f)), false);
	}

	// Heating: a stove first, then radiators under the side walls
	const int32 Heating = Level(TEXT("heating"));
	if (Heating == 1)
	{
		Piece(TEXT("garage_furnace"), FTransform(FRotator::ZeroRotator, FVector(-FWGarageLayout::Width / 2 + 50.f, -FWGarageLayout::Depth / 2 + 60.f, 0.f), FVector(0.55f)));
	}
	for (int32 i = 0; i < (Heating >= 2 ? Heating - 1 : 0); ++i)
	{
		const bool bLeft = i % 2 == 0;
		Piece(TEXT("garage_radiator"), FTransform(FRotator(0.f, 90.f, 0.f), FVector(bLeft ? -FWGarageLayout::Width / 2 + 6.f : FWGarageLayout::Width / 2 - 6.f,
			-100.f + 120.f * (i / 2), 0.f)), false);
	}
}

void AFWGarageRoom::Build(const TSharedPtr<FJsonObject>& Garage)
{
	if (!Garage.IsValid())
	{
		return;
	}
	// Built again only when the furniture or the upgrades changed
	TSharedRef<FJsonObject> Key = MakeShared<FJsonObject>();
	const TArray<TSharedPtr<FJsonValue>>* Objects = nullptr;
	const TSharedPtr<FJsonObject>* Levels = nullptr;
	if (Garage->TryGetArrayField(TEXT("objects"), Objects))
	{
		Key->SetArrayField(TEXT("objects"), *Objects);
	}
	if (Garage->TryGetObjectField(TEXT("upgrades"), Levels))
	{
		Key->SetObjectField(TEXT("upgrades"), *Levels);
	}
	FString Text;
	TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer = TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Text);
	FJsonSerializer::Serialize(Key, Writer);
	if (Text == Built)
	{
		return;
	}
	Built = Text;
	const TSharedPtr<FJsonObject>* Info = nullptr;
	Furniture = Garage->TryGetObjectField(TEXT("furniture"), Info) ? *Info : nullptr;

	for (UStaticMeshComponent* Old : Pieces)
	{
		if (Old)
		{
			Old->DestroyComponent();
		}
	}
	for (UPointLightComponent* Old : Lights)
	{
		if (Old)
		{
			Old->DestroyComponent();
		}
	}
	Pieces.Reset();
	Lights.Reset();
	Picks.Reset();
	Shell();
	Upgrades(Levels ? *Levels : nullptr);

	// The furniture: cell (x, y) of its top-left corner, its footprint turned by the rotation. Row 0 is the room's -Y
	// side, where the door is: the 2D game keeps the middle of it free (the pallet and the tyres stand to the sides)
	if (Objects)
	{
		for (const TSharedPtr<FJsonValue>& Value : *Objects)
		{
			const TSharedPtr<FJsonObject> Object = Value->AsObject();
			const FString Kind = Object->GetStringField(TEXT("kind"));
			const int32 Rotation = int32(Object->GetNumberField(TEXT("rotation"))) & 3;
			const TSharedPtr<FJsonObject>* Size = nullptr;
			int32 W = 1, D = 1;
			if (Furniture.IsValid() && Furniture->TryGetObjectField(Kind, Size))
			{
				W = int32((*Size)->GetNumberField(TEXT("width")));
				D = int32((*Size)->GetNumberField(TEXT("depth")));
			}
			if (Rotation % 2 == 1)
			{
				Swap(W, D);
			}
			const float X = (Object->GetNumberField(TEXT("x")) + W / 2.0) * FWGarageLayout::Cell - FWGarageLayout::Width / 2;
			const float Y = (Object->GetNumberField(TEXT("y")) + D / 2.0) * FWGarageLayout::Cell - FWGarageLayout::Depth / 2;
			UStaticMeshComponent* Component = Piece(*FWGarageLayout::Model(Kind), FTransform(FRotator(0.f, 180.f - 90.f * Rotation, 0.f), FVector(X, Y, 0.f)));
			Picks.Add(Component, TPair<FString, FString>(Kind, Object->GetStringField(TEXT("id"))));
		}
	}
}

bool AFWGarageRoom::Pick(const FVector& From, const FVector& To, const AActor* Ignore, FString& OutKind, FString& OutId) const
{
	FHitResult Hit;
	FCollisionQueryParams Params(TEXT("FWGaragePick"), true, Ignore);
	if (!GetWorld() || !GetWorld()->LineTraceSingleByChannel(Hit, From, To, ECC_Visibility, Params))
	{
		return false;
	}
	if (const TPair<FString, FString>* Found = Picks.Find(Hit.GetComponent()))
	{
		OutKind = Found->Key;
		OutId = Found->Value;
		return true;
	}
	return false;
}
