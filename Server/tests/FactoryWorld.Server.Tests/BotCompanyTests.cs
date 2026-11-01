using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

/// <summary>Companies of bots, without a database.</summary>
public sealed class BotCompanyRulesTests
{
    [Fact]
    public void A_bot_names_its_company_after_itself_within_the_length_a_company_name_may_have()
    {
        foreach (var bot in BotRoster.First(2000))
        {
            var names = BotRules.CompanyNames(bot.Name, BotTraits.Of(bot.Name)).ToList();
            Assert.All(names, n => Assert.InRange(n.Length, 3, 24));
            Assert.Equal(names.Count, names.Distinct().Count());
        }

        Assert.Equal(3, BotRules.CompanySize(0));
        Assert.Equal(6, BotRules.CompanySize(1));
    }
}

/// <summary>Bots founding companies and inviting each other, on a database of their own.</summary>
public sealed class BotCompanyTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    private async Task<string?> StepUntilAsync(Bot bot, string action, int steps = 300)
    {
        for (var i = 0; i < steps; i++)
        {
            if (await Engine.StepAsync(bot.Profile.Name, CancellationToken.None) == action)
            {
                return action;
            }
        }

        return null;
    }

    private async Task<JsonElement> CompanyAsync(Bot bot) =>
        (await bot.Client.SendAsync("company.get", null, CancellationToken.None)).Payload;

    [Fact]
    public async Task An_ambitious_bot_founds_a_company_and_a_loyal_bot_joins_it_when_invited()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var founder = Engine.Bots.OrderByDescending(b => b.Traits.Ambition).First();
        Assert.True(founder.Traits.Ambition >= BotRules.FoundsFrom, "No test bot is ambitious enough.");
        var member = Engine.Bots.First(b => b != founder && b.Traits.Loyalty >= BotRules.JoinsFrom && b.Traits.Ambition < BotRules.FoundsFrom);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(founder.Row.AccountId, 50_000_00, "test", CancellationToken.None);
        }

        for (var x = 100; ; x++)
        {
            if ((await founder.Client.SendAsync("world.buy", new { x, y = 520 }, CancellationToken.None)).Is("world.bought"))
            {
                break;
            }
        }

        // The member looks at its invitations first, so the founder knows it is free.
        Assert.NotNull(await StepUntilAsync(member, "look at company"));
        Assert.NotNull(await StepUntilAsync(founder, "found company"));
        var company = (await CompanyAsync(founder)).GetProperty("company");
        Assert.Contains(founder.Row.CharacterName, company.GetProperty("name").GetString());
        Assert.NotNull(await StepUntilAsync(founder, "invite to company"));

        // Ten minutes later the member looks again, sees the invitation and joins.
        factory.Time.Advance(BotRules.CompanyEvery + TimeSpan.FromSeconds(1));
        Assert.NotNull(await StepUntilAsync(member, "join company"));
        var joined = (await CompanyAsync(member)).GetProperty("company");
        Assert.Equal(company.GetProperty("id").GetString(), joined.GetProperty("id").GetString());
        Assert.Equal("worker", joined.GetProperty("role").GetString());

        // The founder makes its first member a partner.
        Assert.NotNull(await StepUntilAsync(founder, "give role"));
        Assert.Equal("partner", (await CompanyAsync(member)).GetProperty("company").GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_bot_does_not_join_a_company_a_player_founded()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        var bot = Engine.Bots.Last(b => b.Traits.Loyalty >= BotRules.JoinsFrom && b.Traits.Ambition < BotRules.FoundsFrom);
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        await player.SendAsync("company.create", new { name = "Firma Gracza" });
        var invited = await player.SendAsync("company.invite", new { name = bot.Row.CharacterName });
        Assert.True(invited.Type() == "company", invited.ToString());

        factory.Time.Advance(BotRules.CompanyEvery + TimeSpan.FromSeconds(1));
        Assert.Null(await StepUntilAsync(bot, "join company", 100));
        // The other tests' founders may have invited it meanwhile: it may be in theirs, never in the player's.
        var company = (await CompanyAsync(bot)).GetProperty("company");
        Assert.True(company.ValueKind == JsonValueKind.Null || company.GetProperty("name").GetString() != "Firma Gracza", company.ToString());
    }

    [Fact]
    public async Task Bots_in_a_company_bring_what_its_tasks_ask_for_and_gather_its_orders_together()
    {
        await Engine.EnsureBotsAsync(CancellationToken.None);
        // Bots no other test of the class uses: in no company yet, and not ambitious enough to found one.
        var free = new List<Bot>();
        var others = Engine.Bots.Where(b => b.Traits.Loyalty >= BotRules.JoinsFrom && b.Traits.Ambition < BotRules.FoundsFrom).ToList();
        var taken = new[] { others.First(), others.Last() };
        foreach (var candidate in Engine.Bots.Where(b => b.Traits.Ambition < BotRules.FoundsFrom && !taken.Contains(b)).Reverse())
        {
            if ((await CompanyAsync(candidate)).GetProperty("company").ValueKind == JsonValueKind.Null)
            {
                free.Add(candidate);
            }
        }

        var founder = free.First(b => b.Profile.Trade == BotTrade.Smelter);
        var member = free.First(b => b.Profile.Trade == BotTrade.Forester);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(founder.Row.AccountId, 10_000_00, "test", CancellationToken.None);
        }

        var created = await founder.Client.SendAsync("company.create", new { name = "Huta Wspólna" }, CancellationToken.None);
        Assert.True(created.Is("company"), created.Error);
        var id = created.Payload.GetProperty("company").GetProperty("id").GetString()!;
        var invited = await founder.Client.SendAsync("company.invite", new { name = member.Row.CharacterName }, CancellationToken.None);
        Assert.True(invited.Is("company"), invited.Error);
        Assert.True((await member.Client.SendAsync("company.accept", new { company = Guid.Parse(id) }, CancellationToken.None)).Is("company"));
        Assert.True((await founder.Client.SendAsync("company.deposit", new { cents = 5_000_00 }, CancellationToken.None)).Is("company"));

        // The founder, a smelter, sets a task for ore with a wage from the company's cash.
        Assert.NotNull(await StepUntilAsync(founder, "company task create"));
        var task = (await CompanyAsync(founder)).GetProperty("company").GetProperty("economy").GetProperty("tasks")[0];
        Assert.Equal(Items.IronOre, task.GetProperty("item").GetString());
        var wage = task.GetProperty("wageCents").GetInt64();
        Assert.True(wage > 0);

        // The member brings ore for it and is paid the wage.
        var order = (await CompanyAsync(member)).GetProperty("company").GetProperty("economy").GetProperty("orders")[0];
        var orderItem = order.GetProperty("id").GetString()!;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<IInventory>();
            await inventory.AddAsync(member.Row.AccountId, Items.IronOre, 300, CancellationToken.None);
        }

        long before;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(member.Row.AccountId, CancellationToken.None);
        }

        Assert.NotNull(await StepUntilAsync(member, "company task"));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IWallet>().BalanceAsync(member.Row.AccountId, CancellationToken.None) > before);
        }

        // It puts what one of the week's orders wants into the warehouse, and the founder delivers it.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventory>().AddAsync(member.Row.AccountId, orderItem, 500, CancellationToken.None);
        }

        factory.Time.Advance(BotRules.CompanyEvery + TimeSpan.FromSeconds(1));

        var actions = new List<string?>();
        for (var i = 0; i < 300 && !actions.Contains("company order"); i++)
        {
            actions.Add(await Engine.StepAsync(member.Profile.Name, CancellationToken.None));
        }

        var inventoryNow = (await member.Client.SendAsync("inventory.get", null, CancellationToken.None)).Payload.ToString();
        Assert.True(actions.Contains("company order"), orderItem + " " + string.Join(",", actions.Distinct()) + " " + inventoryNow);
        var orderItems = (await CompanyAsync(founder)).GetProperty("company").GetProperty("economy").GetProperty("orders")
            .EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToList();
        var after = (await CompanyAsync(founder)).GetProperty("company");
        Assert.Contains(after.GetProperty("warehouse").GetProperty("items").EnumerateArray(), i => orderItems.Contains(i.GetProperty("id").GetString()));
        Assert.NotNull(await StepUntilAsync(founder, "company order"));
        var delivered = (await CompanyAsync(founder)).GetProperty("company").GetProperty("economy").GetProperty("orders")
            .EnumerateArray().Sum(o => o.GetProperty("delivered").GetInt64());
        Assert.True(delivered > 0);
    }
}
