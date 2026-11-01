using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Characters;
using FactoryWorld.Modules.Collections;
using FactoryWorld.Modules.Expeditions;
using FactoryWorld.Modules.Mentoring;
using FactoryWorld.Modules.Puzzles;
using FactoryWorld.Modules.Reports;
using FactoryWorld.Modules.Seasons;
using FactoryWorld.Modules.Timelapse;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Helpers shared by the tests of puzzles, the album, expeditions, mentors, seasons and reports.</summary>
internal static class Adventure
{
    public static async Task<GameClient> PlayerAsync(ServerFactory factory, string? name = null)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterAsync();
        var look = new { skinTone = 0, hairStyle = 0, hairColor = 0, shirtStyle = 0, shirtColor = 0, pantsColor = 0, shoesColor = 0 };
        var reply = await client.SendAsync("character.create", new { name = name ?? "P" + Guid.NewGuid().ToString("N")[..10], appearance = look });
        Assert.Equal("character", reply.Type());
        return client;
    }

    public static async Task GiveAsync(ServerFactory factory, GameClient client, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(client.AccountId, item, quantity, CancellationToken.None);
    }

    public static async Task LevelAsync(ServerFactory factory, GameClient client, int level)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICharacterProgress>()
            .AddXpAsync(client.AccountId, Leveling.TotalXpForLevel(level), CancellationToken.None);
    }

    public static object[] Layout(params (string Kind, int X, int Y, int Rotation)[] pieces) =>
        pieces.Select(p => (object)new { kind = p.Kind, x = p.X, y = p.Y, rotation = p.Rotation }).ToArray();
}

public sealed class PuzzleTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private const int South = 0, East = 1, North = 2;

    /// <summary>A layout for each puzzle that delivers the target with as few pieces as the gold medal allows.</summary>
    public static readonly Dictionary<string, object[]> GoldLayouts = new()
    {
        ["first_belt"] = Adventure.Layout(
            (Items.Inserter, 1, 1, East), (Items.Belt, 2, 1, East), (Items.Belt, 3, 1, East), (Items.Belt, 4, 1, East), (Items.Inserter, 5, 1, East)),
        ["sawmill"] = Adventure.Layout(
            (Items.Inserter, 1, 2, East), (Items.Sawmill, 2, 2, 0), (Items.Inserter, 4, 2, East), (Items.Belt, 5, 2, East), (Items.Inserter, 6, 2, East)),
        ["two_sawmills"] = Adventure.Layout(
            (Items.Inserter, 1, 3, East), (Items.Belt, 2, 3, East), (Items.Belt, 3, 3, East), (Items.Belt, 4, 3, East), (Items.Belt, 5, 3, East),
            (Items.Inserter, 5, 2, North), (Items.Inserter, 5, 4, South), (Items.Sawmill, 5, 0, 0), (Items.Sawmill, 5, 5, 0),
            (Items.Inserter, 6, 2, South), (Items.Inserter, 6, 4, North)),
        ["stonemason"] = Adventure.Layout(
            (Items.Inserter, 0, 1, South), (Items.Inserter, 1, 0, East), (Items.Stonemason, 0, 2, 0), (Items.Stonemason, 2, 0, 0),
            (Items.Inserter, 2, 3, East), (Items.Inserter, 3, 2, South),
            (Items.Belt, 3, 3, South), (Items.Belt, 3, 4, East), (Items.Belt, 4, 4, East), (Items.Belt, 5, 4, East), (Items.Inserter, 6, 4, East)),
        ["fast_line"] = Adventure.Layout(
            (Items.Inserter, 1, 0, East), (Items.Belt, 2, 0, East), (Items.Belt, 3, 0, East), (Items.Belt, 4, 0, East), (Items.Belt, 5, 0, East), (Items.Belt, 6, 0, East),
            (Items.Inserter, 2, 1, South), (Items.Inserter, 4, 1, South), (Items.Inserter, 6, 1, South),
            (Items.Sawmill, 2, 2, 0), (Items.Sawmill, 4, 2, 0), (Items.Sawmill, 6, 2, 0),
            (Items.Inserter, 3, 4, South), (Items.Inserter, 5, 4, South), (Items.Inserter, 7, 4, South),
            (Items.Belt, 3, 5, East), (Items.Belt, 4, 5, East), (Items.Belt, 5, 5, East), (Items.Belt, 6, 5, East), (Items.Belt, 7, 5, East),
            (Items.FastInserter, 8, 5, East)),
    };

    [Fact]
    public void Every_puzzle_has_a_gold_solution()
    {
        foreach (var puzzle in PuzzleBook.All)
        {
            var pieces = JsonSerializer.Deserialize<List<Piece>>(JsonSerializer.Serialize(GoldLayouts[puzzle.Id]), Payload.Json)!;
            var evaluation = Solver.Evaluate(puzzle, pieces);
            Assert.True(evaluation.Problems.Count == 0, $"{puzzle.Id}: {string.Join(", ", evaluation.Problems)}");
            Assert.True(evaluation.Rate >= puzzle.Target, $"{puzzle.Id}: {evaluation.Rate} a minute");
            Assert.Equal(3, Solver.Medal(puzzle, true, pieces.Count));
        }
    }

    [Fact]
    public async Task A_first_gold_solution_pays_experience_once_and_is_kept()
    {
        using var client = await Adventure.PlayerAsync(factory);

        var result = await client.SendAsync("puzzle.submit", new { puzzle = "sawmill", pieces = GoldLayouts["sawmill"] });
        Assert.Equal("puzzle.result", result.Type());
        var payload = result.Payload();
        Assert.True(payload.GetProperty("solved").GetBoolean());
        Assert.Equal(15, payload.GetProperty("rate").GetDouble());
        Assert.Equal(3, payload.GetProperty("medal").GetInt32());
        Assert.True(payload.GetProperty("firstSolve").GetBoolean());
        Assert.True(payload.GetProperty("firstGold").GetBoolean());
        Assert.Equal(PuzzleBook.SolveXp + PuzzleBook.GoldXp, payload.GetProperty("xp").GetProperty("xp").GetInt64());

        var again = (await client.SendAsync("puzzle.submit", new { puzzle = "sawmill", pieces = GoldLayouts["sawmill"] })).Payload();
        Assert.False(again.GetProperty("firstSolve").GetBoolean());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("xp").ValueKind);

        var book = (await client.SendAsync("puzzle.get")).Payload().GetProperty("puzzles").EnumerateArray()
            .First(p => p.GetProperty("id").GetString() == "sawmill");
        Assert.Equal(3, book.GetProperty("best").GetProperty("medal").GetInt32());
        Assert.Equal(5, book.GetProperty("best").GetProperty("layout").GetArrayLength());
    }

    [Fact]
    public async Task A_slow_or_broken_layout_is_judged_without_a_medal()
    {
        using var client = await Adventure.PlayerAsync(factory);

        // Without the last inserter nothing reaches the chest.
        var slow = (await client.SendAsync("puzzle.submit",
            new { puzzle = "first_belt", pieces = Adventure.Layout((Items.Inserter, 1, 1, East), (Items.Belt, 2, 1, East)) })).Payload();
        Assert.False(slow.GetProperty("solved").GetBoolean());
        Assert.Equal(0, slow.GetProperty("rate").GetDouble());

        var outside = (await client.SendAsync("puzzle.submit",
            new { puzzle = "first_belt", pieces = Adventure.Layout((Items.Belt, 9, 1, East)) })).Payload();
        Assert.Contains("outside", outside.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));

        var foreign = (await client.SendAsync("puzzle.submit",
            new { puzzle = "first_belt", pieces = Adventure.Layout((Items.Sawmill, 2, 0, 0)) })).Payload();
        Assert.Contains($"not_allowed:{Items.Sawmill}", foreign.GetProperty("problems").EnumerateArray().Select(p => p.GetString()));

        // Planks and wood on one belt jam the line.
        var mixed = (await client.SendAsync("puzzle.submit", new
        {
            puzzle = "sawmill",
            pieces = Adventure.Layout(
                (Items.Inserter, 1, 2, East), (Items.Belt, 2, 2, East), (Items.Belt, 3, 2, East), (Items.Inserter, 3, 1, South),
                (Items.Sawmill, 3, 3, 0), (Items.Inserter, 5, 3, North), (Items.Belt, 5, 2, East), (Items.Inserter, 6, 2, East)),
        })).Payload();
        Assert.False(mixed.GetProperty("solved").GetBoolean());

        Assert.Equal("unknown_puzzle", (await client.SendAsync("puzzle.submit", new { puzzle = "nope", pieces = Array.Empty<object>() })).ErrorCode());
    }
}

public sealed class ExpeditionTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task An_expedition_takes_supplies_comes_back_later_and_fills_the_album()
    {
        using var client = await Adventure.PlayerAsync(factory);
        Assert.Equal("not_enough_items", (await client.SendAsync("expedition.send", new { region = "forest" })).ErrorCode());
        Assert.Equal("expedition_level", (await client.SendAsync("expedition.send", new { region = "tundra" })).ErrorCode());

        await Adventure.GiveAsync(factory, client, Items.Provisions, 6);
        var sent = await client.SendAsync("expedition.send", new { region = "forest" });
        Assert.Equal("expeditions", sent.Type());
        var trip = sent.Payload().GetProperty("trips")[0];
        Assert.Equal(3, (await client.InventoryAsync())[Items.Provisions]);
        Assert.Equal("parties_out", (await client.SendAsync("expedition.send", new { region = "forest" })).ErrorCode());

        var id = trip.GetProperty("id").GetInt64();
        Assert.Equal("not_back", (await client.SendAsync("expedition.claim", new { id })).ErrorCode());

        factory.Time.Advance(TimeSpan.FromHours(2));
        var claimed = await client.SendAsync("expedition.claim", new { id });
        Assert.Equal("expedition.claimed", claimed.Type());
        var items = claimed.Payload().GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("quantity").GetInt64());
        Assert.InRange(items[Items.Wood], 15, 30);
        Assert.Equal(items[Items.Wood], (await client.InventoryAsync())[Items.Wood]);
        Assert.Equal(40, claimed.Payload().GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal("unknown_trip", (await client.SendAsync("expedition.claim", new { id })).ErrorCode());

        // Mushrooms always come back from the forest, and they are on the album's forest page.
        var album = (await client.SendAsync("album.get")).Payload();
        var forest = album.GetProperty("pages").EnumerateArray().First(p => p.GetProperty("id").GetString() == "forest");
        Assert.Contains(forest.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.Mushrooms && i.GetProperty("found").GetBoolean());
    }

    [Fact]
    public void Expeditions_bring_back_less_than_the_trader_pays_for_their_supplies()
    {
        // Expeditions are for finds and experience, not for money: on average what they bring sells to the trader for less
        // than the supplies would.
        var random = new Random(7);
        foreach (var region in ExpeditionRules.Regions)
        {
            var supplies = region.Supplies.Sum(s => FactoryWorld.Modules.Market.NpcPrices.Buys[s.Item] * s.Quantity);
            var brought = Enumerable.Range(0, 2000)
                .Select(_ => ExpeditionRules.Roll(region, random).Sum(l => FactoryWorld.Modules.Market.NpcPrices.Buys.GetValueOrDefault(l.Item) * l.Quantity))
                .Average();
            Assert.True(brought < supplies, $"{region.Id}: {brought} brought for {supplies} of supplies");
        }
    }
}

public sealed class AlbumTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task A_full_page_gives_its_reward_once()
    {
        using var client = await Adventure.PlayerAsync(factory);
        Assert.Equal("page_incomplete", (await client.SendAsync("album.claim", new { page = "sky_and_ice" })).ErrorCode());
        Assert.Equal("unknown_page", (await client.SendAsync("album.claim", new { page = "nope" })).ErrorCode());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlbumFinds>().RecordAsync(client.AccountId, [Items.Ice, Items.MeteoricIron, Items.IceCore], CancellationToken.None);
        }

        var claimed = await client.SendAsync("album.claim", new { page = "sky_and_ice" });
        Assert.Equal("album", claimed.Type());
        Assert.True(claimed.Payload().GetProperty("claimed").GetBoolean());
        Assert.Equal(Album.PageXp, claimed.Payload().GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal("already_claimed", (await client.SendAsync("album.claim", new { page = "sky_and_ice" })).ErrorCode());

        var badges = await client.SendAsync("achievements.get");
        Assert.Contains(badges.Payload().GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("badges").EnumerateArray()),
            b => b.GetProperty("id").GetString() == "album_page" && b.GetProperty("unlockedAt").ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public void Every_album_item_has_a_name_and_one_page()
    {
        foreach (var item in Album.AllItems)
        {
            Assert.True(Items.Names.ContainsKey(item), item);
            Assert.Single(Album.Pages, p => p.Items.Contains(item));
        }
    }
}

public sealed class SeasonTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Holiday_treats_buy_decorations_only_while_the_holiday_is_on()
    {
        using var client = await Adventure.PlayerAsync(factory);
        var year = factory.Time.GetUtcNow().Year + 1;

        factory.Time.SetUtcNow(new DateTimeOffset(year, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var summer = (await client.SendAsync("season.get")).Payload();
        Assert.Equal(JsonValueKind.Null, summer.GetProperty("active").ValueKind);
        Assert.Equal("halloween", summer.GetProperty("next").GetProperty("id").GetString());
        Assert.Equal("no_season", (await client.SendAsync("season.buy", new { item = Items.PumpkinLantern })).ErrorCode());

        factory.Time.SetUtcNow(new DateTimeOffset(year, 10, 30, 12, 0, 0, TimeSpan.Zero));
        var halloween = (await client.SendAsync("season.get")).Payload().GetProperty("active");
        Assert.Equal("halloween", halloween.GetProperty("id").GetString());
        Assert.Equal("not_enough_items", (await client.SendAsync("season.buy", new { item = Items.PumpkinLantern })).ErrorCode());
        Assert.Equal("unknown_offer", (await client.SendAsync("season.buy", new { item = Items.Snowman })).ErrorCode());

        await Adventure.GiveAsync(factory, client, Items.Candy, 35);
        var bought = await client.SendAsync("season.buy", new { item = Items.PumpkinLantern });
        Assert.Equal("season", bought.Type());
        var inventory = await client.InventoryAsync();
        Assert.Equal(5, inventory[Items.Candy]);
        Assert.Equal(1, inventory[Items.PumpkinLantern]);

        var holidays = (await client.SendAsync("album.get")).Payload().GetProperty("pages").EnumerateArray().First(p => p.GetProperty("id").GetString() == "holidays");
        Assert.Contains(holidays.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == Items.PumpkinLantern && i.GetProperty("found").GetBoolean());

        // At most the daily limit of treats a day.
        await using var scope = factory.Services.CreateAsyncScope();
        var seasons = scope.ServiceProvider.GetRequiredService<SeasonService>();
        var season = SeasonCalendar.All.First(s => s.Id == "halloween");
        var given = 0;
        for (var i = 0; i < SeasonCalendar.DailyLimit + 5; i++)
        {
            given += await seasons.GiveTreatAsync(client.AccountId, season, CancellationToken.None) ? 1 : 0;
        }

        Assert.Equal(SeasonCalendar.DailyLimit, given);
    }

    [Fact]
    public void Christmas_runs_over_the_new_year()
    {
        Assert.Equal("christmas", SeasonCalendar.Current(new DateTimeOffset(2027, 1, 3, 0, 0, 0, TimeSpan.Zero))?.Season.Id);
        Assert.Equal("christmas", SeasonCalendar.Current(new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.Zero))?.Season.Id);
        Assert.Null(SeasonCalendar.Current(new DateTimeOffset(2027, 1, 7, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("halloween", SeasonCalendar.Next(new DateTimeOffset(2027, 1, 7, 0, 0, 0, TimeSpan.Zero)).Season.Id);
    }
}

public sealed class MentorTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task A_mentor_and_a_new_player_are_both_rewarded_when_the_new_player_grows()
    {
        var mentorName = "M" + Guid.NewGuid().ToString("N")[..10];
        using var mentor = await Adventure.PlayerAsync(factory, mentorName);
        using var mentee = await Adventure.PlayerAsync(factory);

        Assert.Equal("mentor_level", (await mentee.SendAsync("mentor.choose", new { name = mentorName })).ErrorCode());
        Assert.Equal("unknown_player", (await mentee.SendAsync("mentor.choose", new { name = "Nobody" + Guid.NewGuid().ToString("N")[..6] })).ErrorCode());

        await Adventure.LevelAsync(factory, mentor, MentorRules.MentorLevel);
        var chosen = await mentee.SendAsync("mentor.choose", new { name = mentorName });
        Assert.Equal("mentoring", chosen.Type());
        Assert.Equal("has_mentor", (await mentee.SendAsync("mentor.choose", new { name = mentorName })).ErrorCode());
        Assert.Equal("goal_not_reached", (await mentee.SendAsync("mentor.claim", new { })).ErrorCode());

        await Adventure.LevelAsync(factory, mentee, MentorRules.GoalLevel);
        var mine = await mentee.SendAsync("mentor.claim", new { });
        Assert.Equal("mentee", mine.Payload().GetProperty("role").GetString());
        Assert.Equal("already_claimed", (await mentee.SendAsync("mentor.claim", new { })).ErrorCode());

        var menteeName = (await mentor.SendAsync("mentor.get")).Payload().GetProperty("mentees")[0].GetProperty("name").GetString();
        var before = (await mentor.SendAsync("character.get")).Payload().GetProperty("xp").GetInt64();
        var theirs = await mentor.SendAsync("mentor.claim", new { mentee = menteeName });
        Assert.Equal("mentor", theirs.Payload().GetProperty("role").GetString());
        Assert.Equal(before + MentorRules.MentorXp, theirs.Payload().GetProperty("xp").GetProperty("xp").GetInt64());
        Assert.Equal("already_claimed", (await mentor.SendAsync("mentor.claim", new { mentee = menteeName })).ErrorCode());
    }

    [Fact]
    public async Task Only_a_new_player_chooses_a_mentor_and_not_themselves()
    {
        var name = "S" + Guid.NewGuid().ToString("N")[..10];
        using var player = await Adventure.PlayerAsync(factory, name);
        Assert.Equal("own_mentor", (await player.SendAsync("mentor.choose", new { name })).ErrorCode());

        await Adventure.LevelAsync(factory, player, MentorRules.ChooseBelowLevel);
        using var other = await Adventure.PlayerAsync(factory);
        var otherName = (await other.SendAsync("character.get")).Payload().GetProperty("name").GetString();
        Assert.Equal("too_experienced", (await player.SendAsync("mentor.choose", new { name = otherName })).ErrorCode());
    }
}

public sealed class AwayReportTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Coming_back_after_a_break_shows_what_was_earned_once()
    {
        var token = "";
        Guid account;
        using (var client = await factory.ConnectAsync())
        {
            token = await client.RegisterAsync();
            account = client.AccountId;
            Assert.False((await client.SendAsync("report.away")).Payload().GetProperty("away").GetBoolean());
        }

        factory.Time.Advance(TimeSpan.FromHours(3));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var wallet = scope.ServiceProvider.GetRequiredService<IWallet>();
            await wallet.CreditAsync(account, 12_345, "export 10 x wood", CancellationToken.None);
            await wallet.CreditAsync(account, 5_000, "bank interest", CancellationToken.None);
        }

        factory.Time.Advance(TimeSpan.FromHours(1));
        using var back = await factory.ConnectAsync();
        Assert.Equal("auth.ok", (await back.SendAsync("auth.resume", new { token })).Type());
        var report = (await back.SendAsync("report.away")).Payload();
        Assert.True(report.GetProperty("away").GetBoolean());
        Assert.InRange(report.GetProperty("minutes").GetInt64(), 239, 241);
        var income = report.GetProperty("income").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i.GetProperty("cents").GetInt64());
        Assert.Equal(12_345, income["Eksport koleją"]);
        Assert.Equal(5_000, income["Banki"]);
        Assert.Equal(17_345, report.GetProperty("netCents").GetInt64());

        Assert.False((await back.SendAsync("report.away")).Payload().GetProperty("away").GetBoolean());
    }

    [Fact]
    public void Ledger_reasons_fall_into_groups()
    {
        Assert.Equal("Eksport koleją", AwayRules.GroupOf("export 5 x planks"));
        Assert.Equal("Inne", AwayRules.GroupOf("something new"));
    }
}

public sealed class TimelapseTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    [Fact]
    public async Task Each_day_a_plot_is_entered_keeps_a_frame_of_what_stands_on_it()
    {
        using var client = await Adventure.PlayerAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        }

        var (x, y) = Enumerable.Range(0, Map.Height / 5).SelectMany(j => Enumerable.Range(0, Map.Width / 5).Select(i => (X: i * 5, Y: j * 5)))
            .First(p => Map.BiomeAt(p.X, p.Y) == Biome.Grassland);
        Assert.Equal("world.bought", (await client.SendAsync("world.buy", new { x, y })).Type());

        var entered = await client.SendAsync("plot.enter", new { x, y });
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(entered.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        var bytes = tiles.ToArray();
        var (tx, ty) = Enumerable.Range(0, PlotLayout.Size).SelectMany(j => Enumerable.Range(0, PlotLayout.Size).Select(i => (X: i, Y: j)))
            .First(t => PlotTiles.Clear(bytes, t.X, t.Y, 1));

        factory.Time.Advance(TimeSpan.FromDays(1));
        await Adventure.GiveAsync(factory, client, Items.Scarecrow, 1);
        var placed = await client.SendAsync("plot.place", new { x, y, kind = Items.Scarecrow, tileX = tx, tileY = ty, rotation = 0 });
        Assert.True(placed.Type() == "plot.placed", placed.ToString());
        await client.SendAsync("plot.enter", new { x, y });

        var timelapse = (await client.SendAsync("timelapse.get", new { x, y })).Payload();
        Assert.Equal(2, timelapse.GetProperty("days").GetInt32());
        var frames = timelapse.GetProperty("frames");
        Assert.Equal(frames[0].GetProperty("objects").GetArrayLength() + 1, frames[1].GetProperty("objects").GetArrayLength());

        using var stranger = await Adventure.PlayerAsync(factory);
        Assert.NotNull((await stranger.SendAsync("timelapse.get", new { x, y })).ErrorCode());
    }

    [Fact]
    public void A_long_history_is_thinned_out_evenly_keeping_the_first_and_last_day()
    {
        var picked = TimelapseRules.Pick(500, TimelapseRules.FramesSent);
        Assert.Equal(TimelapseRules.FramesSent, picked.Count);
        Assert.Equal(0, picked[0]);
        Assert.Equal(499, picked[^1]);
        Assert.Equal([0, 1, 2], TimelapseRules.Pick(3, TimelapseRules.FramesSent));
    }
}

public sealed class ShowcaseTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task The_trophy_case_shows_badges_and_the_player_can_look_at_anothers()
    {
        var name = "T" + Guid.NewGuid().ToString("N")[..10];
        using var client = await Adventure.PlayerAsync(factory, name);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlbumFinds>().RecordAsync(client.AccountId, [Items.Ice, Items.MeteoricIron, Items.IceCore], CancellationToken.None);
        }

        await client.SendAsync("album.claim", new { page = "sky_and_ice" });
        var mine = (await client.SendAsync("showcase.get")).Payload();
        Assert.True(mine.GetProperty("mine").GetBoolean());
        Assert.Contains(mine.GetProperty("honours").EnumerateArray(), h => h.GetProperty("name").GetString()!.Length > 0);

        using var visitor = await Adventure.PlayerAsync(factory);
        var theirs = (await visitor.SendAsync("showcase.get", new { name })).Payload();
        Assert.False(theirs.GetProperty("mine").GetBoolean());
        Assert.Equal(name, theirs.GetProperty("name").GetString());
    }
}
