using System.IO.Compression;
using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class CompanyTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static readonly WorldMap Map = WorldMap.Load();

    private static readonly Dictionary<Biome, int> Used = [];

    private static (int X, int Y) FreePlot(Biome biome)
    {
        lock (Used)
        {
            var skip = Used.GetValueOrDefault(biome) + 2000;
            Used[biome] = Used.GetValueOrDefault(biome) + 1;
            var found = 0;
            for (var y = 0; y < Map.Height; y += 5)
            {
                for (var x = 0; x < Map.Width; x += 5)
                {
                    if (Map.BiomeAt(x, y) == biome && found++ >= skip)
                    {
                        return (x, y);
                    }
                }
            }

            throw new InvalidOperationException("No such plot.");
        }
    }

    private sealed record Player(GameClient Client, string Name) : IDisposable
    {
        public Task<JsonElement> SendAsync(string type, object? payload = null) => Client.SendAsync(type, payload);

        public void Dispose() => Client.Dispose();
    }

    private async Task<Player> PlayerAsync()
    {
        var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(client.AccountId, 1_000_000_00, "test", CancellationToken.None);
        var name = (await client.SendAsync("character.get")).Payload().GetProperty("name").GetString()!;
        return new Player(client, name);
    }

    private async Task GiveAsync(Player player, string item, long quantity)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(player.Client.AccountId, item, quantity, CancellationToken.None);
    }

    private static async Task<(int X, int Y)> BuyAsync(Player player, Biome biome)
    {
        var (x, y) = FreePlot(biome);
        Assert.Equal("world.bought", (await player.SendAsync("world.buy", new { x, y })).Type());
        return (x, y);
    }

    private static async Task<byte[]> TilesAsync(Player player, (int X, int Y) plot)
    {
        var reply = await player.SendAsync("plot.enter", new { x = plot.X, y = plot.Y });
        Assert.Equal("plot", reply.Type());
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(reply.Payload().GetProperty("tiles").GetString()!)), CompressionMode.Decompress);
        using var tiles = new MemoryStream();
        gzip.CopyTo(tiles);
        return tiles.ToArray();
    }

    /// <summary>Free tiles of a plot, nearest to the spawn first.</summary>
    private static IEnumerable<(int X, int Y)> FreeTiles(byte[] tiles) =>
        Enumerable.Range(0, tiles.Length)
            .Where(i => tiles[i] == (byte)TileObject.None)
            .Select(i => (X: i % PlotLayout.Size, Y: i / PlotLayout.Size))
            .Where(t => t != PlotLayout.Spawn)
            .OrderBy(t => Math.Abs(t.X - PlotLayout.Spawn.X) + Math.Abs(t.Y - PlotLayout.Spawn.Y));

    /// <summary>A company of an owner and one more player who joined it (a worker).</summary>
    private async Task<(Player Owner, Player Member, JsonElement Company)> CompanyAsync()
    {
        var owner = await PlayerAsync();
        var member = await PlayerAsync();
        var name = "Huta " + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal("company", (await owner.SendAsync("company.create", new { name })).Type());
        var invited = await owner.SendAsync("company.invite", new { name = member.Name });
        Assert.Equal(member.Name, invited.Payload().GetProperty("company").GetProperty("invited")[0].GetString());

        var invitation = (await member.SendAsync("company.get")).Payload().GetProperty("invitations")[0];
        Assert.Equal(name, invitation.GetProperty("name").GetString());
        var joined = await member.SendAsync("company.accept", new { company = invitation.GetProperty("company").GetGuid() });
        Assert.Equal("company", joined.Type());
        return (owner, member, joined.Payload().GetProperty("company"));
    }

    private static string Role(JsonElement company, string name) =>
        company.GetProperty("members").EnumerateArray().First(m => m.GetProperty("name").GetString() == name).GetProperty("role").GetString()!;

    [Fact]
    public async Task A_player_founds_a_company_invites_another_who_joins_as_a_worker()
    {
        var (owner, member, company) = await CompanyAsync();
        using var _ = owner;
        using var __ = member;

        Assert.Equal("worker", company.GetProperty("role").GetString());
        Assert.Equal(["work"], company.GetProperty("rights").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("owner", Role(company, owner.Name));
        Assert.Equal("worker", Role(company, member.Name));
        Assert.Equal(0, company.GetProperty("invited").GetArrayLength());
        Assert.Equal("joined", company.GetProperty("log")[0].GetProperty("action").GetString());

        // One company per player, and names are unique whatever the letter case.
        Assert.Equal("already_in_company", (await member.SendAsync("company.create", new { name = "Inna spółka" })).ErrorCode());
        using var other = await PlayerAsync();
        var taken = company.GetProperty("name").GetString()!.ToUpperInvariant();
        Assert.Equal("company_name_taken", (await other.SendAsync("company.create", new { name = taken })).ErrorCode());
        Assert.Equal("invalid_company_name", (await other.SendAsync("company.create", new { name = "!!" })).ErrorCode());
        Assert.Equal("already_in_company", (await owner.SendAsync("company.invite", new { name = member.Name })).ErrorCode());
        Assert.Equal("no_such_player", (await owner.SendAsync("company.invite", new { name = "nobody_at_all" })).ErrorCode());

        // Only the owner manages members.
        Assert.Equal("not_allowed", (await member.SendAsync("company.invite", new { name = other.Name })).ErrorCode());
        Assert.Equal("no_invite", (await other.SendAsync("company.accept", new { company = company.GetProperty("id").GetGuid() })).ErrorCode());
    }

    [Fact]
    public async Task The_owner_gives_roles_hands_the_company_over_and_the_last_member_closes_it()
    {
        var (owner, member, _) = await CompanyAsync();
        using var o = owner;
        using var m = member;

        Assert.Equal("hand_over_first", (await owner.SendAsync("company.leave")).ErrorCode());
        Assert.Equal("bad_role", (await owner.SendAsync("company.role", new { name = member.Name, role = "boss" })).ErrorCode());
        var partner = (await owner.SendAsync("company.role", new { name = member.Name, role = "partner" })).Payload().GetProperty("company");
        Assert.Equal("partner", Role(partner, member.Name));

        var handed = (await owner.SendAsync("company.role", new { name = member.Name, role = "owner" })).Payload().GetProperty("company");
        Assert.Equal("owner", Role(handed, member.Name));
        Assert.Equal("partner", handed.GetProperty("role").GetString());

        // The old owner leaves; the new one is the last member, and closing gives them the warehouse.
        Assert.Equal(JsonValueKind.Null, (await owner.SendAsync("company.leave")).Payload().GetProperty("company").ValueKind);
        await GiveAsync(member, Items.Wood, 30);
        await member.SendAsync("company.put", new { item = Items.Wood, quantity = 30 });
        Assert.False((await member.Client.InventoryAsync()).ContainsKey(Items.Wood));
        Assert.Equal(JsonValueKind.Null, (await member.SendAsync("company.leave")).Payload().GetProperty("company").ValueKind);
        Assert.Equal(30, (await member.Client.InventoryAsync())[Items.Wood]);
    }

    [Fact]
    public async Task Every_member_fills_the_warehouse_but_only_partners_take_from_it_and_the_log_says_who()
    {
        var (owner, member, _) = await CompanyAsync();
        using var o = owner;
        using var m = member;
        await GiveAsync(member, Items.IronIngot, 15);

        var put = (await member.SendAsync("company.put", new { item = Items.IronIngot, quantity = 10 })).Payload().GetProperty("company");
        var warehouse = put.GetProperty("warehouse");
        Assert.Equal(10, warehouse.GetProperty("stored").GetInt64());
        Assert.Equal(Items.IronIngot, warehouse.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(5, (await member.Client.InventoryAsync())[Items.IronIngot]);
        Assert.Equal("not_enough_items", (await member.SendAsync("company.put", new { item = Items.IronIngot, quantity = 6 })).ErrorCode());

        Assert.Equal("not_allowed", (await member.SendAsync("company.take", new { item = Items.IronIngot, quantity = 1 })).ErrorCode());
        Assert.Equal("not_enough_items", (await owner.SendAsync("company.take", new { item = Items.IronIngot, quantity = 11 })).ErrorCode());
        var taken = (await owner.SendAsync("company.take", new { item = Items.IronIngot, quantity = 10 })).Payload().GetProperty("company");
        Assert.Equal(0, taken.GetProperty("warehouse").GetProperty("items").GetArrayLength());
        Assert.Equal(10, (await owner.Client.InventoryAsync())[Items.IronIngot]);

        var log = taken.GetProperty("log");
        Assert.Equal(("take", owner.Name, 10L), (log[0].GetProperty("action").GetString(), log[0].GetProperty("who").GetString(), log[0].GetProperty("quantity").GetInt64()));
        Assert.Equal(("put", member.Name), (log[1].GetProperty("action").GetString(), log[1].GetProperty("who").GetString()));

        using var stranger = await PlayerAsync();
        Assert.Equal("no_company", (await stranger.SendAsync("company.put", new { item = Items.Wood, quantity = 1 })).ErrorCode());
    }

    [Fact]
    public async Task Players_on_the_same_plot_see_each_other_walk()
    {
        var (owner, member, _) = await CompanyAsync();
        using var o = owner;
        using var m = member;
        using var stranger = await PlayerAsync();
        var plot = await BuyAsync(owner, Biome.Farmland);
        await factory.CompanyLevelAsync(owner.Client.AccountId);
        Assert.Equal("company", (await owner.SendAsync("company.addPlot", new { x = plot.X, y = plot.Y })).Type());

        object At(double x, double y, string facing = "down", bool walking = false) =>
            new { x = plot.X, y = plot.Y, posX = x, posY = y, facing, walking };

        // Alone on the plot, the owner sees nobody.
        var alone = await owner.SendAsync("plot.presence", At(125.5, 11.5));
        Assert.Equal("plot.presence", alone.Type());
        Assert.Equal(0, alone.Payload().GetProperty("players").GetArrayLength());

        // The member comes in and sees the owner standing at the spawn...
        var seen = (await member.SendAsync("plot.presence", At(126.5, 12.5, "right", true))).Payload().GetProperty("players");
        Assert.Equal(1, seen.GetArrayLength());
        Assert.Equal(owner.Name, seen[0].GetProperty("name").GetString());
        Assert.Equal(125.5, seen[0].GetProperty("x").GetDouble());
        Assert.Equal(JsonValueKind.Object, seen[0].GetProperty("appearance").ValueKind);

        // ...and the owner sees the member walking.
        var back = (await owner.SendAsync("plot.presence", At(125.5, 11.5))).Payload().GetProperty("players");
        Assert.Equal(1, back.GetArrayLength());
        Assert.Equal(member.Name, back[0].GetProperty("name").GetString());
        Assert.Equal("right", back[0].GetProperty("facing").GetString());
        Assert.True(back[0].GetProperty("walking").GetBoolean());

        // Nobody else may say they stand on the plot.
        Assert.Equal("not_owner", (await stranger.SendAsync("plot.presence", At(10, 10))).ErrorCode());

        // A player who walks off is gone at once; one whose client goes quiet is gone after a while.
        Assert.Equal("plot.left", (await member.SendAsync("plot.leave")).Type());
        Assert.Equal(0, (await owner.SendAsync("plot.presence", At(125.5, 11.5))).Payload().GetProperty("players").GetArrayLength());
        await member.SendAsync("plot.presence", At(130, 20));
        factory.Time.Advance(PlotPresence.Timeout + TimeSpan.FromSeconds(1));
        Assert.Equal(0, (await owner.SendAsync("plot.presence", At(125.5, 11.5))).Payload().GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task Anyone_may_walk_about_a_public_plot_but_do_nothing_there()
    {
        using var owner = await PlayerAsync();
        using var visitor = await PlayerAsync();
        var forest = await BuyAsync(owner, Biome.Forest);
        var (x, y) = forest;

        // A plot is private until its owner opens it, and only the owner may.
        var closed = (await visitor.SendAsync("world.plot", new { x, y })).Payload();
        Assert.False(closed.GetProperty("public").GetBoolean());
        Assert.False(closed.GetProperty("canEnter").GetBoolean());
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.enter", new { x, y })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.setPublic", new { x, y, @public = true })).ErrorCode());

        var opened = await owner.SendAsync("plot.setPublic", new { x, y, @public = true });
        Assert.Equal("world.plot", opened.Type());
        Assert.True(opened.Payload().GetProperty("public").GetBoolean());

        // Now anyone may come in, look around and be seen, but has no other right there.
        var seen = (await visitor.SendAsync("world.plot", new { x, y })).Payload();
        Assert.True(seen.GetProperty("public").GetBoolean());
        Assert.True(seen.GetProperty("canEnter").GetBoolean());
        var entered = await visitor.SendAsync("plot.enter", new { x, y });
        Assert.Equal(["visit"], entered.Payload().GetProperty("rights").EnumerateArray().Select(r => r.GetString()));
        object At(double px, double py) => new { x, y, posX = px, posY = py, facing = "down", walking = false };
        Assert.Equal("plot.presence", (await visitor.SendAsync("plot.presence", At(125.5, 12.5))).Type());
        Assert.Equal(1, (await owner.SendAsync("plot.presence", At(125.5, 11.5))).Payload().GetProperty("players").GetArrayLength());

        // The owner's chest with something in it: the visitor sees the belts at work but not what the chest holds.
        await GiveAsync(owner, Items.WoodenChest, 1);
        await GiveAsync(owner, Items.Wood, 5);
        var tiles = await TilesAsync(owner, forest);
        var (cx, cy) = FreeTiles(tiles).First();
        var placed = await owner.SendAsync("plot.place", new { x, y, kind = Items.WoodenChest, tileX = cx, tileY = cy, rotation = 0 });
        Assert.Equal("plot.placed", placed.Type());
        var chest = placed.Payload().GetProperty("object").GetProperty("id").GetGuid();
        Assert.Equal("storage", (await owner.SendAsync("storage.put", new { x, y, id = chest, item = Items.Wood, quantity = 5 })).Type());
        var owners = (await owner.SendAsync("logistics.get", new { x, y })).Payload();
        Assert.Equal(1, owners.GetProperty("storages").GetArrayLength());
        var visitors = await visitor.SendAsync("logistics.get", new { x, y });
        Assert.Equal("logistics", visitors.Type());
        Assert.Equal(0, visitors.Payload().GetProperty("storages").GetArrayLength());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEquipment>().GiveToolAsync(visitor.Client.AccountId, Items.Axe, CancellationToken.None);
        }

        var tree = Array.IndexOf(tiles, (byte)TileObject.Tree);
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.harvest", new { x, y, tileX = tree % PlotLayout.Size, tileY = tree / PlotLayout.Size })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("storage.get", new { x, y, id = chest })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("storage.take", new { x, y, id = chest, item = Items.Wood, quantity = 1 })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.pickup", new { x, y, id = chest })).ErrorCode());
        Assert.Equal("not_owner", (await visitor.SendAsync("station.get", new { x, y })).ErrorCode());
        await GiveAsync(visitor, Items.WoodenChest, 1);
        var (fx, fy) = FreeTiles(tiles).Skip(3).First();
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.place", new { x, y, kind = Items.WoodenChest, tileX = fx, tileY = fy, rotation = 0 })).ErrorCode());

        // Made private again, the plot is closed to the visitor, who soon stops being seen there too.
        Assert.False((await owner.SendAsync("plot.setPublic", new { x, y, @public = false })).Payload().GetProperty("public").GetBoolean());
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.enter", new { x, y })).ErrorCode());
        Assert.False((await visitor.SendAsync("world.plot", new { x, y })).Payload().GetProperty("canEnter").GetBoolean());
        factory.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("not_owner", (await visitor.SendAsync("plot.presence", At(125.5, 12.5))).ErrorCode());
    }

    [Fact]
    public async Task Members_work_on_a_plot_brought_into_the_company_as_far_as_their_role_allows()
    {
        var (owner, member, _) = await CompanyAsync();
        using var o = owner;
        using var m = member;
        using var stranger = await PlayerAsync();
        var forest = await BuyAsync(owner, Biome.Forest);
        Assert.Equal("not_owner", (await member.SendAsync("plot.enter", new { x = forest.X, y = forest.Y })).ErrorCode());
        Assert.Equal("not_owner", (await member.SendAsync("company.addPlot", new { x = forest.X, y = forest.Y })).ErrorCode());

        // Below level 10 a player can't bring their plots in: new accounts would hand the company cheap plots.
        Assert.Equal("level_too_low", (await owner.SendAsync("company.addPlot", new { x = forest.X, y = forest.Y })).ErrorCode());
        await factory.CompanyLevelAsync(owner.Client.AccountId);
        var added = (await owner.SendAsync("company.addPlot", new { x = forest.X, y = forest.Y })).Payload().GetProperty("company");
        Assert.Equal(owner.Name, added.GetProperty("plots")[0].GetProperty("owner").GetString());

        // The map tells members they may go in, and lists the plot among theirs.
        var seen = (await member.SendAsync("world.plot", new { x = forest.X, y = forest.Y })).Payload();
        Assert.True(seen.GetProperty("canEnter").GetBoolean());
        Assert.True(seen.GetProperty("company").GetProperty("member").GetBoolean());
        var shared = (await member.SendAsync("world.info")).Payload().GetProperty("sharedPlots");
        Assert.Contains(shared.EnumerateArray(), p => p.GetProperty("x").GetInt32() == forest.X && p.GetProperty("y").GetInt32() == forest.Y);
        var outside = (await stranger.SendAsync("world.plot", new { x = forest.X, y = forest.Y })).Payload();
        Assert.False(outside.GetProperty("canEnter").GetBoolean());
        Assert.False(outside.GetProperty("company").GetProperty("member").GetBoolean());
        Assert.Equal("not_owner", (await stranger.SendAsync("plot.enter", new { x = forest.X, y = forest.Y })).ErrorCode());

        // A worker enters and gathers with their own axe; the wood is theirs.
        var entered = await member.SendAsync("plot.enter", new { x = forest.X, y = forest.Y });
        Assert.Equal(["work", "visit"], entered.Payload().GetProperty("rights").EnumerateArray().Select(r => r.GetString()));
        var tiles = await TilesAsync(member, forest);
        var tree = Array.IndexOf(tiles, (byte)TileObject.Tree);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEquipment>().GiveToolAsync(member.Client.AccountId, Items.Axe, CancellationToken.None);
        }

        var chopped = await member.SendAsync("plot.harvest", new { x = forest.X, y = forest.Y, tileX = tree % PlotLayout.Size, tileY = tree / PlotLayout.Size });
        Assert.Equal("plot.harvested", chopped.Type());
        Assert.True((await member.Client.InventoryAsync())[Items.Wood] >= 1);

        // The owner builds; a worker uses the furnace and puts into the chest but builds nothing and takes nothing out.
        var free = PlotTiles.Apart(FreeTiles(tiles).Where(t => PlotTiles.Clear(tiles, t.X, t.Y, 1))).Take(3).ToList();
        await GiveAsync(owner, Items.WoodenChest, 1);
        await GiveAsync(owner, Items.Furnace, 1);
        var chest = (await owner.SendAsync("plot.place", new { x = forest.X, y = forest.Y, kind = Items.WoodenChest, tileX = free[0].X, tileY = free[0].Y }))
            .Payload().GetProperty("object").GetProperty("id").GetGuid();
        var furnace = (await owner.SendAsync("plot.place", new { x = forest.X, y = forest.Y, kind = Items.Furnace, tileX = free[1].X, tileY = free[1].Y }))
            .Payload().GetProperty("object").GetProperty("id").GetGuid();

        await GiveAsync(member, Items.WoodenChest, 1);
        await GiveAsync(member, Items.Coal, 6);
        Assert.Equal("not_owner", (await member.SendAsync("plot.place", new { x = forest.X, y = forest.Y, kind = Items.WoodenChest, tileX = free[2].X, tileY = free[2].Y })).ErrorCode());
        Assert.Equal("not_owner", (await member.SendAsync("plot.pickup", new { x = forest.X, y = forest.Y, id = chest })).ErrorCode());
        Assert.Equal("not_owner", (await member.SendAsync("plot.rotate", new { x = forest.X, y = forest.Y, id = chest, rotation = 1 })).ErrorCode());
        Assert.Equal("furnace", (await member.SendAsync("furnace.load", new { furnace, item = Items.Coal, quantity = 5 })).Type());
        Assert.Equal("storage", (await member.SendAsync("storage.put", new { x = forest.X, y = forest.Y, id = chest, item = Items.Wood, quantity = 1 })).Type());
        Assert.Equal("not_owner", (await member.SendAsync("storage.take", new { x = forest.X, y = forest.Y, id = chest, item = Items.Wood, quantity = 1 })).ErrorCode());
        Assert.Equal("no_object", (await stranger.SendAsync("furnace.get", new { furnace })).ErrorCode());

        // At the station a worker loads but takes nothing out.
        Assert.Equal("station", (await member.SendAsync("station.load", new { x = forest.X, y = forest.Y, item = Items.Coal, quantity = 1 })).Type());
        Assert.Equal("not_owner", (await member.SendAsync("station.unload", new { x = forest.X, y = forest.Y, item = Items.Coal, quantity = 1 })).ErrorCode());

        // As a partner they build too, and pick up what they put down, but not what the owner did.
        await owner.SendAsync("company.role", new { name = member.Name, role = "partner" });
        var placed = await member.SendAsync("plot.place", new { x = forest.X, y = forest.Y, kind = Items.WoodenChest, tileX = free[2].X, tileY = free[2].Y });
        Assert.Equal("plot.placed", placed.Type());
        Assert.Equal("storage", (await member.SendAsync("storage.take", new { x = forest.X, y = forest.Y, id = chest, item = Items.Wood, quantity = 1 })).Type());
        Assert.Equal("station", (await member.SendAsync("station.unload", new { x = forest.X, y = forest.Y, item = Items.Coal, quantity = 1 })).Type());
        var route = new { x = forest.X, y = forest.Y, toX = forest.X, toY = forest.Y + 1, items = Array.Empty<string>(), everyMinutes = RailRules.RouteMinutes.First() };
        // Partners set up train routes too: this one only fails because the next plot has no station.
        Assert.Equal("no_station", (await member.SendAsync("station.route", route)).ErrorCode());
        Assert.Equal("plot.rotated", (await member.SendAsync("plot.rotate", new { x = forest.X, y = forest.Y, id = chest, rotation = 1 })).Type());
        Assert.Equal("not_yours", (await member.SendAsync("plot.pickup", new { x = forest.X, y = forest.Y, id = chest })).ErrorCode());
        var own = placed.Payload().GetProperty("object").GetProperty("id").GetGuid();
        Assert.Equal("plot.pickedUp", (await member.SendAsync("plot.pickup", new { x = forest.X, y = forest.Y, id = own })).Type());
        Assert.Equal("not_owner", (await member.SendAsync("world.survey", new { x = forest.X, y = forest.Y })).ErrorCode());

        // Taken out of the company, the plot is the owner's alone again.
        Assert.Equal("not_owner", (await member.SendAsync("company.removePlot", new { x = forest.X, y = forest.Y })).ErrorCode());
        await owner.SendAsync("company.removePlot", new { x = forest.X, y = forest.Y });
        Assert.Equal("not_owner", (await member.SendAsync("plot.enter", new { x = forest.X, y = forest.Y })).ErrorCode());
        Assert.Equal("no_object", (await member.SendAsync("furnace.get", new { furnace })).ErrorCode());
    }

    [Fact]
    public async Task A_removed_member_takes_their_plots_out_of_the_company()
    {
        var (owner, member, _) = await CompanyAsync();
        using var o = owner;
        using var m = member;
        var plot = await BuyAsync(member, Biome.Grassland);
        await factory.CompanyLevelAsync(member.Client.AccountId);
        await member.SendAsync("company.addPlot", new { x = plot.X, y = plot.Y });
        Assert.Equal("plot", (await owner.SendAsync("plot.enter", new { x = plot.X, y = plot.Y })).Type());

        Assert.Equal("not_yourself", (await owner.SendAsync("company.kick", new { name = owner.Name })).ErrorCode());
        var kicked = (await owner.SendAsync("company.kick", new { name = member.Name })).Payload().GetProperty("company");
        Assert.Equal(1, kicked.GetProperty("members").GetArrayLength());
        Assert.Equal(0, kicked.GetProperty("plots").GetArrayLength());
        Assert.Equal(("kicked", member.Name), (kicked.GetProperty("log")[0].GetProperty("action").GetString(), kicked.GetProperty("log")[0].GetProperty("other").GetString()));
        Assert.Equal("not_owner", (await owner.SendAsync("plot.enter", new { x = plot.X, y = plot.Y })).ErrorCode());
        Assert.Equal(JsonValueKind.Null, (await member.SendAsync("company.get")).Payload().GetProperty("company").ValueKind);
    }
}
