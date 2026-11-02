namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>
/// Sorting a bag of trash by hand at the garage bin is a small game: the bag holds <see cref="Pieces"/> pieces of
/// waste, and each goes into its bin (metal, plastic or glass). A piece put into the wrong bin comes back; once every
/// piece is in its bin, the bag is sorted and gives what a bag always gave. The server checks every piece.
/// </summary>
public static class WasteSorting
{
    public const string Metal = "metal";
    public const string Plastic = "plastic";
    public const string Glass = "glass";

    /// <summary>The bins in the order they stand.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Bins = [(Metal, "Metal"), (Plastic, "Plastik"), (Glass, "Szkło")];

    /// <summary>Pieces in one bag.</summary>
    public const int Pieces = 5;

    /// <summary>A kind of waste found in a bag, and the bin it belongs in.</summary>
    public sealed record Piece(string Id, string Name, string Bin);

    public static readonly IReadOnlyList<Piece> Kinds =
    [
        new("waste_can", "Puszka po napoju", Metal),
        new("waste_tin", "Puszka po konserwie", Metal),
        new("waste_lid", "Metalowa nakrętka", Metal),
        new("waste_pet", "Butelka PET", Plastic),
        new("waste_cup", "Kubek po jogurcie", Plastic),
        new("waste_jug", "Butelka po płynie", Plastic),
        new("waste_bottle", "Szklana butelka", Glass),
        new("waste_jar", "Słoik", Glass),
        new("waste_vial", "Buteleczka po syropie", Glass),
    ];

    public static readonly IReadOnlyDictionary<string, Piece> ById = Kinds.ToDictionary(k => k.Id);
}
