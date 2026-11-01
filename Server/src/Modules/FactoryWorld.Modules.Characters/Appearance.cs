namespace FactoryWorld.Modules.Characters;

/// <summary>What the player picked in the character editor; each value is an index into <see cref="AppearanceCatalog"/>.
/// The face, the eyes and the facial hair came later: a client that does not send them gets the first of each.</summary>
public sealed record Appearance(
    int SkinTone,
    int HairStyle,
    int HairColor,
    int ShirtStyle,
    int ShirtColor,
    int PantsColor,
    int ShoesColor,
    int FaceStyle = 0,
    int EyeColor = 0,
    int FacialHair = 0);

/// <summary>
/// The options of the character editor. The client draws the character from these palettes,
/// so adding a colour here makes it available in the game without a client update.
/// </summary>
public static class AppearanceCatalog
{
    public static readonly string[] SkinTones = ["#f6d7c3", "#eac0a0", "#d9a07a", "#b97a55", "#8d5a3b", "#5e3b28"];

    /// <summary>0 = bald, then short, spiky, long, ponytail, curly.</summary>
    public const int HairStyles = 6;

    public static readonly string[] HairColors = ["#1f1a17", "#4a2f1d", "#7b4a26", "#b8792f", "#e3c16f", "#c7c2b8", "#a33b2b", "#3f5fa8"];

    /// <summary>0 = t-shirt, 1 = tank top, 2 = hoodie.</summary>
    public const int ShirtStyles = 3;

    /// <summary>0 = even, 1 = strong, 2 = soft, 3 = weathered features.</summary>
    public const int FaceStyles = 4;

    public static readonly string[] EyeColors = ["#5a3a22", "#3d6f9e", "#4f7f45", "#8a7448", "#2f2a28", "#7d8b96"];

    /// <summary>0 = none, 1 = moustache, 2 = goatee, 3 = full beard.</summary>
    public const int FacialHairStyles = 4;

    public static readonly string[] ClothColors =
    [
        "#e8e4da", "#3a3a40", "#c0392b", "#e67e22", "#f1c40f", "#27ae60",
        "#16a085", "#2980b9", "#34495e", "#8e44ad", "#7f5539", "#d35d8b",
    ];

    public static bool IsValid(Appearance a) =>
        InRange(a.SkinTone, SkinTones.Length)
        && InRange(a.HairStyle, HairStyles)
        && InRange(a.HairColor, HairColors.Length)
        && InRange(a.ShirtStyle, ShirtStyles)
        && InRange(a.ShirtColor, ClothColors.Length)
        && InRange(a.PantsColor, ClothColors.Length)
        && InRange(a.ShoesColor, ClothColors.Length)
        && InRange(a.FaceStyle, FaceStyles)
        && InRange(a.EyeColor, EyeColors.Length)
        && InRange(a.FacialHair, FacialHairStyles);

    private static bool InRange(int value, int count) => value >= 0 && value < count;
}
