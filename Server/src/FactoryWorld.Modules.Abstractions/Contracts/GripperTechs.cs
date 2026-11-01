namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>Technologies that unlock no item but make the player's inserters carry more, for the logistics module to ask about.</summary>
public static class GripperTechs
{
    /// <summary>Inserters carry two pieces at once.</summary>
    public const string Bigger = "bigger_grippers";

    /// <summary>Inserters carry three pieces at once.</summary>
    public const string Industrial = "industrial_grippers";
}
