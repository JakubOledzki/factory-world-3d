namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>A technology that unlocks no item but helps on every plot of the player, for the world module to check.</summary>
public static class GeologyTech
{
    /// <summary>The plot's mineral comes up twice as often in mines and drills, and a geological survey costs half.</summary>
    public const string Id = "geology";

    /// <summary>How many times more often the mineral comes up.</summary>
    public const int MineralFactor = 2;
}
