using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Contracts;

/// <summary>
/// One kind of order an NPC company gives: what it wants, between how many pieces, what it pays per piece
/// (cents, well above the trader's floor price, but below what the trader charges for it even in a sale, so buying from him to
/// deliver never pays) and how many hours the player has to deliver.
/// </summary>
public sealed record NpcTemplate(string Item, int Min, int Max, int Step, long CentsPerPiece, int Hours, string[] Issuers);

/// <summary>Orders from NPC companies. Every player has <see cref="ContractRules.NpcSlots"/> of their own at a time.</summary>
public static class NpcContracts
{
    private static readonly string[] Builders = ["Firma budowlana „Mur-Bud”", "Tartak „Leśny Dwór”", "Spółdzielnia „Pod Strzechą”"];
    private static readonly string[] Steelworks = ["Huta „Stal-Pol”", "Odlewnia „Żeliwiak”", "Kuźnia u Kowala Jana"];
    private static readonly string[] Bakers = ["Piekarnia „Złoty Kłos”", "Bar mleczny „Pod Kogutem”", "Stołówka szkolna nr 7"];
    private static readonly string[] Shops = ["Sklep „Społem”", "Targowisko miejskie", "Gospoda „Pod Lipą”"];
    private static readonly string[] Factories = ["Zakłady „Taśmex”", "Fabryka mebli „Dąb”", "Magazyny portowe w Gdyni"];
    private static readonly string[] Caterers = ["Catering „Biesiada”", "Restauracja „Pod Złotym Prosiakiem”", "Hotel „Polonia”"];

    public static readonly IReadOnlyList<NpcTemplate> Templates =
    [
        new(Items.Wood, 20, 80, 10, 400, 4, Builders),
        new(Items.Stone, 30, 120, 10, 200, 4, Builders),
        new(Items.Sand, 30, 150, 10, 100, 4, Builders),
        new(Items.Coal, 10, 50, 5, 800, 8, Steelworks),
        new(Items.IronOre, 10, 40, 5, 1200, 8, Steelworks),
        new(Items.CopperOre, 10, 30, 5, 2400, 8, Steelworks),
        new(Items.IronIngot, 10, 50, 5, 1800, 12, Steelworks),
        new(Items.CopperIngot, 10, 30, 5, 3200, 12, Steelworks),
        new(Items.Nails, 20, 100, 10, 450, 8, Builders),
        new(Items.Wheat, 20, 100, 10, 120, 6, Bakers),
        new(Items.Carrot, 10, 60, 10, 180, 6, Shops),
        new(Items.Potato, 10, 50, 10, 300, 8, Shops),
        new(Items.Cabbage, 10, 40, 5, 500, 8, Shops),
        new(Items.Flour, 10, 40, 5, 360, 12, Bakers),
        new(Items.Bread, 5, 20, 5, 1400, 12, Bakers),
        new(Items.Sauerkraut, 5, 20, 5, 700, 24, Shops),
        new(Items.Fries, 5, 15, 5, 1800, 12, Bakers),
        new(Items.VegetableSoup, 3, 10, 1, 2200, 12, Bakers),
        new(Items.Belt, 8, 32, 4, 720, 12, Factories),
        new(Items.WoodenChest, 1, 4, 1, 5200, 24, Factories),
        new(Items.Planks, 20, 100, 10, 250, 8, Builders),
        new(Items.Beams, 5, 30, 5, 1200, 12, Builders),
        new(Items.Plywood, 5, 20, 5, 1000, 24, Factories),
        // Catering: weddings, works dinners and banquets want what the dairy, the slaughterhouse and the kitchen make.
        new(Items.Cheese, 5, 20, 5, 2000, 12, Caterers),
        new(Items.Butter, 5, 20, 5, 1500, 12, Caterers),
        new(Items.Meat, 10, 40, 5, 1300, 12, Caterers),
        new(Items.WorkersLunch, 5, 20, 5, 1700, 12, Caterers),
        new(Items.RoastBeef, 3, 12, 1, 4000, 24, Caterers),
        new(Items.Cheesecake, 3, 12, 1, 4100, 24, Caterers),
    ];

    /// <summary>A new order, for none of the items in <paramref name="taken"/> (the player's other NPC orders).</summary>
    public static (NpcTemplate Template, long Quantity, string Issuer) Pick(IReadOnlySet<string> taken, Random random)
    {
        var free = Templates.Where(t => !taken.Contains(t.Item)).ToList();
        var template = free.Count > 0 ? free[random.Next(free.Count)] : Templates[random.Next(Templates.Count)];
        var steps = (template.Max - template.Min) / template.Step;
        var quantity = template.Min + (long)random.Next(steps + 1) * template.Step;
        return (template, quantity, template.Issuers[random.Next(template.Issuers.Length)]);
    }

    /// <summary>XP for finishing an NPC order: 10 plus 1 for every 20 $ of reward, at most 300.</summary>
    public static long Xp(long rewardCents) => Math.Min(10 + rewardCents / 2000, 300);
}
