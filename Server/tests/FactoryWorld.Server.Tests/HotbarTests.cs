using System.Text.Json;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class HotbarTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static string?[] Slots(JsonElement reply)
    {
        Assert.Equal("hotbar", reply.Type());
        return reply.Payload().GetProperty("slots").EnumerateArray().Select(s => s.ValueKind == JsonValueKind.Null ? null : s.GetString()).ToArray();
    }

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private async Task<GameClient> PlayerWithAsync(string email, params string[] items)
    {
        var client = await factory.ConnectAsync();
        await client.RegisterAsync(email);
        await using var scope = factory.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
        foreach (var item in items)
        {
            await inventory.AddAsync(client.AccountId, item, 3, CancellationToken.None);
        }

        return client;
    }

    [Fact]
    public async Task A_new_player_has_five_empty_slots()
    {
        using var client = await PlayerWithAsync(NewEmail());

        Assert.Equal(new string?[5], Slots(await client.SendAsync("hotbar.get")));
    }

    [Fact]
    public async Task Items_put_on_the_hotbar_stay_there_after_logging_in_again()
    {
        var email = NewEmail();
        using (var client = await PlayerWithAsync(email, Items.Belt, Items.Inserter))
        {
            await client.SendAsync("hotbar.set", new { slot = 0, item = Items.Belt });
            var set = Slots(await client.SendAsync("hotbar.set", new { slot = 1, item = Items.Inserter }));
            Assert.Equal(new string?[] { Items.Belt, Items.Inserter, null, null, null }, set);
        }

        using var again = await factory.ConnectAsync();
        Assert.Equal("auth.ok", (await again.SendAsync("auth.login", new { email, password = "secret-password" })).Type());
        Assert.Equal(new string?[] { Items.Belt, Items.Inserter, null, null, null }, Slots(await again.SendAsync("hotbar.get")));
    }

    [Fact]
    public async Task An_item_moved_to_another_slot_leaves_the_old_one_and_replaces_what_was_there()
    {
        using var client = await PlayerWithAsync(NewEmail(), Items.Belt, Items.Inserter);
        await client.SendAsync("hotbar.set", new { slot = 0, item = Items.Belt });
        await client.SendAsync("hotbar.set", new { slot = 2, item = Items.Inserter });

        Assert.Equal(new string?[] { null, null, Items.Belt, null, null }, Slots(await client.SendAsync("hotbar.set", new { slot = 2, item = Items.Belt })));
        Assert.Equal(new string?[5], Slots(await client.SendAsync("hotbar.set", new { slot = 2, item = (string?)null })));
    }

    [Fact]
    public async Task A_slot_keeps_its_item_when_the_last_one_is_used_up()
    {
        using var client = await PlayerWithAsync(NewEmail(), Items.Belt);
        await client.SendAsync("hotbar.set", new { slot = 4, item = Items.Belt });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().RemoveAsync(client.AccountId, Items.Belt, 3, CancellationToken.None);
        }

        Assert.Equal(Items.Belt, Slots(await client.SendAsync("hotbar.get"))[4]);
    }

    [Fact]
    public async Task Bad_hotbar_requests_are_refused()
    {
        using var client = await PlayerWithAsync(NewEmail(), Items.Belt);

        Assert.Equal("bad_hotbar_slot", (await client.SendAsync("hotbar.set", new { slot = 5, item = Items.Belt })).ErrorCode());
        Assert.Equal("bad_hotbar_slot", (await client.SendAsync("hotbar.set", new { slot = -1, item = Items.Belt })).ErrorCode());
        Assert.Equal("not_enough_items", (await client.SendAsync("hotbar.set", new { slot = 0, item = Items.Inserter })).ErrorCode());
        Assert.Equal("unknown_item", (await client.SendAsync("hotbar.set", new { slot = 0, item = "gold" })).ErrorCode());
        Assert.Equal(new string?[5], Slots(await client.SendAsync("hotbar.get")));
    }

    [Fact]
    public async Task The_hotbar_needs_a_logged_in_player()
    {
        using var client = await factory.ConnectAsync();

        Assert.Equal("not_logged_in", (await client.SendAsync("hotbar.get")).ErrorCode());
    }
}
