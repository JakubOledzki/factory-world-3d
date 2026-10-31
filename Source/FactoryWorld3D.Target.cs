using UnrealBuildTool;

public class FactoryWorld3DTarget : TargetRules
{
	public FactoryWorld3DTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Game;
		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
		ExtraModuleNames.Add("FactoryWorld3D");
	}
}
