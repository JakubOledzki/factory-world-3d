using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Characters;

namespace FactoryWorld.Server.Tests;

public sealed class CharacterTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly object Look = new
    {
        skinTone = 2, hairStyle = 3, hairColor = 1, shirtStyle = 0, shirtColor = 5, pantsColor = 8, shoesColor = 1,
    };

    private static string UniqueName() => "P" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Created_character_starts_at_level_1_and_is_returned_later()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterAsync();
        var name = UniqueName();

        var created = await client.SendAsync("character.create", new { name, appearance = Look });
        var loaded = await client.SendAsync("character.get");

        Assert.Equal("character", created.Type());
        Assert.Equal("character", loaded.Type());
        Assert.Equal(name, loaded.Payload().GetProperty("name").GetString());
        Assert.Equal(1, loaded.Payload().GetProperty("level").GetInt32());
        Assert.Equal(0, loaded.Payload().GetProperty("xp").GetInt64());
        Assert.Equal(204, loaded.Payload().GetProperty("nextLevelXp").GetInt64());
        Assert.Equal(3, loaded.Payload().GetProperty("appearance").GetProperty("hairStyle").GetInt32());
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        var name = UniqueName();
        using var first = await factory.ConnectAsync();
        await first.RegisterAsync();
        await first.SendAsync("character.create", new { name, appearance = Look });

        using var second = await factory.ConnectAsync();
        await second.RegisterAsync();
        var reply = await second.SendAsync("character.create", new { name = name.ToLowerInvariant(), appearance = Look });

        Assert.Equal("name_taken", reply.ErrorCode());
    }

    [Fact]
    public async Task One_character_per_account()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterAsync();
        await client.SendAsync("character.create", new { name = UniqueName(), appearance = Look });

        var reply = await client.SendAsync("character.create", new { name = UniqueName(), appearance = Look });

        Assert.Equal("character_exists", reply.ErrorCode());
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("seventeen_chars_x")]
    [InlineData("bad name")]
    [InlineData("<script>")]
    public async Task Invalid_names_are_rejected(string name)
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterAsync();

        var reply = await client.SendAsync("character.create", new { name, appearance = Look });

        Assert.Equal("invalid_name", reply.ErrorCode());
    }

    [Fact]
    public async Task Polish_letters_are_allowed_in_names()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterAsync();

        var reply = await client.SendAsync("character.create", new { name = "Łódź_" + Guid.NewGuid().ToString("N")[..6], appearance = Look });

        Assert.Equal("character", reply.Type());
    }

    [Fact]
    public async Task Unknown_appearance_option_is_rejected()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterAsync();
        var look = new { skinTone = 99, hairStyle = 0, hairColor = 0, shirtStyle = 0, shirtColor = 0, pantsColor = 0, shoesColor = 0 };

        var reply = await client.SendAsync("character.create", new { name = UniqueName(), appearance = look });

        Assert.Equal("invalid_appearance", reply.ErrorCode());
    }

    [Fact]
    public async Task Options_are_available_before_login()
    {
        using var client = await factory.ConnectAsync();

        var reply = await client.SendAsync("character.options");

        Assert.Equal(AppearanceCatalog.SkinTones.Length, reply.Payload().GetProperty("skinTones").GetArrayLength());
        Assert.Equal(AppearanceCatalog.HairStyles, reply.Payload().GetProperty("hairStyles").GetInt32());
    }
}

public sealed class LevelingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(203, 1)]
    [InlineData(204, 2)]
    [InlineData(831, 2)]
    [InlineData(832, 3)]
    [InlineData(19_116, 10)]
    [InlineData(5_841_395, 99)]
    [InlineData(5_841_396, 100)]
    [InlineData(99_999_999, 100)]
    public void Level_follows_the_xp_table(long xp, int level) => Assert.Equal(level, Leveling.LevelForXp(xp));

    [Fact]
    public void Level_bonuses_come_every_fifth_level_and_add_up()
    {
        Assert.Empty(LevelBonuses.For(4));
        Assert.Equal(new Dictionary<string, int> { ["forestry"] = 5 }, LevelBonuses.For(5));
        var top = LevelBonuses.For(Leveling.MaxLevel);
        Assert.All(Industries.All.Where(i => i != Industries.Trade), i => Assert.Equal(15, top[i]));
        Assert.Equal(5, top[Industries.Trade]);
    }

    [Fact]
    public void Every_level_boundary_is_exact()
    {
        for (var level = 2; level <= Leveling.MaxLevel; level++)
        {
            var total = Leveling.TotalXpForLevel(level);
            Assert.Equal(level, Leveling.LevelForXp(total));
            Assert.Equal(level - 1, Leveling.LevelForXp(total - 1));
        }
    }
}
