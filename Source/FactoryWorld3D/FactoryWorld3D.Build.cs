using UnrealBuildTool;

public class FactoryWorld3D : ModuleRules
{
	public FactoryWorld3D(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[] {
			"Core", "CoreUObject", "Engine", "InputCore", "EnhancedInput"
		});
	}
}
