using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Market;
using FactoryWorld.Modules.Recycling;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class FurnitureTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static JsonElement Find(JsonElement garage, string kind) =>
        garage.Payload().GetProperty("objects").EnumerateArray().First(o => o.GetProperty("kind").GetString() == kind);

    [Fact]
    public void Starter_furniture_fits_in_the_room_without_overlapping()
    {
        var starter = GarageLayout.Starter;
        foreach (var (kind, x, y) in starter)
        {
            Assert.True(GarageLayout.InsideRoom(kind, x, y), kind);
        }

        PlacedObject At(int i) => new(Guid.Empty, starter[i].Kind, starter[i].X, starter[i].Y);

        for (var i = 0; i < starter.Length; i++)
        {
            for (var j = i + 1; j < starter.Length; j++)
            {
                Assert.False(GarageLayout.Overlaps(At(i), At(j)), $"{starter[i].Kind} / {starter[j].Kind}");
            }
        }
    }

    [Fact]
    public async Task New_garage_is_furnished_once()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var garage = await client.SendAsync("garage.get");
        var again = await client.SendAsync("garage.get");

        Assert.Equal(GarageLayout.Starter.Length, garage.Payload().GetProperty("objects").GetArrayLength());
        Assert.Equal(GarageLayout.Starter.Length, again.Payload().GetProperty("objects").GetArrayLength());
        Assert.Equal(GarageLayout.Width, garage.Payload().GetProperty("room").GetProperty("width").GetInt32());
        Assert.True(garage.Payload().GetProperty("furniture").TryGetProperty("furniture_trash_bin", out _));
    }

    [Fact]
    public async Task Furniture_can_be_picked_up_and_placed_elsewhere()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var garage = await client.SendAsync("garage.get");
        var tyres = Find(garage, "furniture_tyres");

        var picked = await client.SendAsync("garage.pickup", new { id = tyres.GetProperty("id").GetGuid() });
        Assert.Equal(GarageLayout.Starter.Length - 1, picked.Payload().GetProperty("objects").GetArrayLength());
        Assert.Equal(1, (await client.InventoryAsync())["furniture_tyres"]);

        var placed = await client.SendAsync("garage.place", new { kind = "furniture_tyres", x = 7, y = 2 });
        var moved = Find(placed, "furniture_tyres");
        Assert.Equal(7, moved.GetProperty("x").GetInt32());
        Assert.Equal(2, moved.GetProperty("y").GetInt32());
        Assert.False((await client.InventoryAsync()).ContainsKey("furniture_tyres"));
    }

    [Fact]
    public async Task Furniture_cannot_overlap_or_leave_the_room_or_be_placed_twice()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var garage = await client.SendAsync("garage.get");
        await client.SendAsync("garage.pickup", new { id = Find(garage, "furniture_gas_cylinder").GetProperty("id").GetGuid() });

        // On the trash bin's second cell, outside the wall, between cells, then fine.
        Assert.Equal("blocked", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 6, y = 9 })).ErrorCode());
        Assert.Equal("bad_position", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = -1, y = 3 })).ErrorCode());
        Assert.Equal("bad_position", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 16, y = 3 })).ErrorCode());
        Assert.Equal("bad_payload", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 4.5, y = 3 })).ErrorCode());
        Assert.Equal("garage", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 7, y = 9 })).Type());
        // It is no longer in the inventory.
        Assert.Equal("not_enough_items", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 8, y = 6 })).ErrorCode());
        Assert.Equal("not_placeable", (await client.SendAsync("garage.place", new { kind = "trash", x = 8, y = 6 })).ErrorCode());
        Assert.Equal("no_object", (await client.SendAsync("garage.pickup", new { id = Guid.NewGuid() })).ErrorCode());
    }

    [Fact]
    public async Task Sorting_needs_the_bin_in_the_garage()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var garage = await client.SendAsync("garage.get");
        await client.SendAsync("garage.pickup", new { id = Find(garage, "furniture_trash_bin").GetProperty("id").GetGuid() });

        Assert.Equal("no_bin", (await client.SendAsync("recycling.sort", new { count = 1 })).ErrorCode());

        await client.SendAsync("garage.place", new { kind = "furniture_trash_bin", x = 7, y = 5 });
        Assert.Equal("garage", (await client.SendAsync("recycling.sort", new { count = 1 })).Type());
    }

    [Fact]
    public async Task Players_cannot_pick_up_each_others_furniture()
    {
        using var owner = await factory.ConnectAsync();
        await owner.RegisterWithCharacterAsync();
        var id = Find(await owner.SendAsync("garage.get"), "furniture_pallet").GetProperty("id").GetGuid();

        using var thief = await factory.ConnectAsync();
        await thief.RegisterWithCharacterAsync();

        Assert.Equal("no_object", (await thief.SendAsync("garage.pickup", new { id })).ErrorCode());
    }

    [Fact]
    public async Task Turned_furniture_takes_its_cells_crosswise()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        var garage = await client.SendAsync("garage.get");
        await client.SendAsync("garage.pickup", new { id = Find(garage, "furniture_shelf").GetProperty("id").GetGuid() });

        // 4×1 turned is 1×4: cells x 0, y 9-12 would go through the back wall.
        Assert.Equal("bad_position", (await client.SendAsync("garage.place", new { kind = "furniture_shelf", x = 0, y = 9, rotation = 1 })).ErrorCode());
        Assert.Equal("bad_position", (await client.SendAsync("garage.place", new { kind = "furniture_shelf", x = 0, y = 2, rotation = 4 })).ErrorCode());
        var placed = await client.SendAsync("garage.place", new { kind = "furniture_shelf", x = 0, y = 8, rotation = 1 });
        Assert.Equal(1, Find(placed, "furniture_shelf").GetProperty("rotation").GetInt32());

        // Its cells now block the cell (0, 10) but not (1, 8).
        await client.SendAsync("garage.pickup", new { id = Find(placed, "furniture_gas_cylinder").GetProperty("id").GetGuid() });
        Assert.Equal("blocked", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 0, y = 10 })).ErrorCode());
        Assert.Equal("garage", (await client.SendAsync("garage.place", new { kind = "furniture_gas_cylinder", x = 1, y = 8 })).Type());
    }

    private async Task SqlAsync(string sql, Guid owner)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        await db.InTransactionAsync(async () =>
        {
            await using var command = await db.CommandAsync(sql, CancellationToken.None);
            command.Parameters.AddWithValue("owner", owner);
            await command.ExecuteNonQueryAsync();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>Makes the player's garage look like one furnished before research tables existed.</summary>
    private async Task WithoutResearchTableAsync(Guid owner)
    {
        await SqlAsync("DELETE FROM recycling.garage_objects WHERE owner_id = @owner AND kind = 'furniture_research_table'", owner);
        await SqlAsync("UPDATE recycling.garages SET research_table = FALSE WHERE owner_id = @owner", owner);
    }

    private static int Count(JsonElement garage, string kind) =>
        garage.Payload().GetProperty("objects").EnumerateArray().Count(o => o.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task A_new_garage_has_a_research_table_and_the_trader_sells_one_for_2000()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var table = Find(await client.SendAsync("garage.get"), Items.ResearchTable);
        Assert.Equal(1, table.GetProperty("x").GetInt32());
        Assert.Equal(10, table.GetProperty("y").GetInt32());
        Assert.Equal(200_000, NpcPrices.Sells[Items.ResearchTable]);
        Assert.Equal("Stół badawczy", Items.Names[Items.ResearchTable]);
    }

    [Fact]
    public async Task An_old_garage_gets_its_research_table_once()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await client.SendAsync("garage.get");
        await WithoutResearchTableAsync(client.AccountId);

        var garage = await client.SendAsync("garage.get");
        Assert.Equal(1, Count(garage, Items.ResearchTable));
        Assert.Equal(10, Find(garage, Items.ResearchTable).GetProperty("y").GetInt32());

        // Put away, it is not given again.
        await client.SendAsync("garage.pickup", new { id = Find(garage, Items.ResearchTable).GetProperty("id").GetGuid() });
        Assert.Equal(0, Count(await client.SendAsync("garage.get"), Items.ResearchTable));
        Assert.Equal(1, (await client.InventoryAsync())[Items.ResearchTable]);
    }

    [Fact]
    public async Task An_old_garage_gets_its_research_table_on_a_free_spot_when_its_own_is_taken()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();
        await client.SendAsync("garage.get");
        await WithoutResearchTableAsync(client.AccountId);
        // The pallet stands on the research table's spot.
        await SqlAsync("UPDATE recycling.garage_objects SET x = 2, y = 10 WHERE owner_id = @owner AND kind = 'furniture_pallet'", client.AccountId);

        var garage = await client.SendAsync("garage.get");
        var placed = garage.Payload().GetProperty("objects").EnumerateArray()
            .Select(o => new PlacedObject(o.GetProperty("id").GetGuid(), o.GetProperty("kind").GetString()!,
                o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32(), o.GetProperty("rotation").GetInt32()))
            .ToList();
        var table = placed.Single(o => o.Kind == Items.ResearchTable);
        Assert.NotEqual((1, 10), (table.X, table.Y));
        Assert.True(GarageLayout.InsideRoom(table.Kind, table.X, table.Y, table.Rotation));
        Assert.DoesNotContain(placed, o => o != table && GarageLayout.Overlaps(o, table));
    }

    [Fact]
    public void A_full_garage_has_no_free_spot_for_a_research_table()
    {
        var floor = new List<PlacedObject>();
        for (var x = 0; x < GarageLayout.Width; x += 2)
        {
            for (var y = 0; y < GarageLayout.Depth; y += 2)
            {
                floor.Add(new PlacedObject(Guid.NewGuid(), "furniture_pallet", x, y));
            }
        }

        Assert.Null(GarageLayout.FreeSpot(Items.ResearchTable, floor));
        Assert.Equal((1, 10, 0), GarageLayout.FreeSpot(Items.ResearchTable, []));
    }
}
