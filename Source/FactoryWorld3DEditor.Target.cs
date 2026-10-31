using UnrealBuildTool;

public class FactoryWorld3DEditorTarget : TargetRules
{
	public FactoryWorld3DEditorTarget(TargetInfo Target) : base(Target)
	{
		Type = TargetType.Editor;
		DefaultBuildSettings = BuildSettingsVersion.Latest;
		IncludeOrderVersion = EngineIncludeOrderVersion.Latest;
		ExtraModuleNames.Add("FactoryWorld3D");
	}
}
