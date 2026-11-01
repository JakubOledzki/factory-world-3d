using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FactoryWorld.Modules.Characters;

public sealed class Character
{
    public Guid Id { get; set; }

    /// <summary>One character per account for now.</summary>
    public Guid AccountId { get; set; }

    public required string Name { get; set; }

    /// <summary>Lower-cased name, unique, so "Kuba" and "kuba" cannot both exist.</summary>
    public required string NormalizedName { get; set; }

    public required Appearance Appearance { get; set; }

    public long Xp { get; set; }

    /// <summary>The item in the first slot of the tool bar, or null.</summary>
    public string? Tool { get; set; }

    /// <summary>The item in the second slot of the tool bar (there with a backpack), or null.</summary>
    public string? Tool2 { get; set; }

    /// <summary>The item in the third slot of the tool bar (there with the best backpack), or null.</summary>
    public string? Tool3 { get; set; }

    /// <summary>The backpack worn, or null.</summary>
    public string? Backpack { get; set; }

    /// <summary>The clothes worn in each of <see cref="ClothingSlots.All"/>, or null.</summary>
    public string? ClothesHead { get; set; }

    public string? ClothesBody { get; set; }

    public string? ClothesLegs { get; set; }

    public string? ClothesFeet { get; set; }

    /// <summary>The clothes worn by slot, empty slots left out.</summary>
    public IReadOnlyDictionary<string, string> Outfit => new Dictionary<string, string?>
    {
        [ClothingSlots.Head] = ClothesHead,
        [ClothingSlots.Body] = ClothesBody,
        [ClothingSlots.Legs] = ClothesLegs,
        [ClothingSlots.Feet] = ClothesFeet,
    }.Where(c => c.Value is not null).ToDictionary(c => c.Key, c => c.Value!);

    /// <summary>What is in each slot of the tool bar the character has now.</summary>
    public IReadOnlyList<string?> Tools => new[] { Tool, Tool2, Tool3 }[..Equipment.ToolSlots(Backpack)];

    /// <summary>What the character ate last, and until when it helps; see <see cref="Items.Meals"/>.</summary>
    public string? Meal { get; set; }

    public DateTimeOffset? MealUntil { get; set; }

    /// <summary>The meal that still helps at <paramref name="now"/>, or null.</summary>
    public string? MealAt(DateTimeOffset now) => Meal is { } meal && MealUntil > now && Items.Meals.ContainsKey(meal) ? meal : null;

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CharactersDbContext(DbContextOptions<CharactersDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "characters";

    public DbSet<Character> Characters => Set<Character>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

        model.Entity<Character>(e =>
        {
            e.ToTable("characters");
            e.HasIndex(c => c.AccountId).IsUnique();
            e.HasIndex(c => c.NormalizedName).IsUnique();
            e.Property(c => c.Name).HasMaxLength(CharacterRules.MaxNameLength);
            e.Property(c => c.NormalizedName).HasMaxLength(CharacterRules.MaxNameLength);
            e.Property(c => c.Tool).HasMaxLength(32);
            e.Property(c => c.Tool2).HasMaxLength(32);
            e.Property(c => c.Tool3).HasMaxLength(32);
            e.Property(c => c.Backpack).HasMaxLength(32);
            e.Property(c => c.Meal).HasMaxLength(32);
            e.Property(c => c.ClothesHead).HasMaxLength(32);
            e.Property(c => c.ClothesBody).HasMaxLength(32);
            e.Property(c => c.ClothesLegs).HasMaxLength(32);
            e.Property(c => c.ClothesFeet).HasMaxLength(32);
            e.Ignore(c => c.Tools);
            e.Ignore(c => c.Outfit);
            e.ComplexProperty(c => c.Appearance, a => a.ToJson());
        });
    }
}
