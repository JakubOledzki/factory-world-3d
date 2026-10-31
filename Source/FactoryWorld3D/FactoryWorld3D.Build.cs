using UnrealBuildTool;

public class FactoryWorld3D : ModuleRules
{
	public FactoryWorld3D(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicIncludePaths.Add(ModuleDirectory);

		PublicDependencyModuleNames.AddRange(new string[] {
			"Core", "CoreUObject", "Engine", "InputCore", "EnhancedInput"
		});

		PrivateDependencyModuleNames.AddRange(new string[] {
			"Json", "Slate", "SlateCore", "WebSockets"
		});
	}
}
