using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;

namespace FactoryWorld.Server.Tests;

public sealed class PlanTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private static object Line(string name, int length = 3) => new
    {
        name,
        objects = Enumerable.Range(0, length).Select(i => new { kind = Items.Belt, dx = i, dy = 0, rotation = 1 })
            .Append(new { kind = Items.Inserter, dx = 0, dy = 1, rotation = 0 }).ToArray(),
    };

    [Fact]
    public async Task A_saved_plan_is_kept_by_name_replaced_by_one_of_the_same_name_and_deleted()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        var saved = await client.SendAsync("plans.save", Line(" Linia pieca "));
        Assert.Equal("plans", saved.Type());
        var plan = Assert.Single(saved.Payload().GetProperty("plans").EnumerateArray());
        Assert.Equal("Linia pieca", plan.GetProperty("name").GetString());
        Assert.Equal(4, plan.GetProperty("objects").GetArrayLength());
        Assert.Equal(Items.Belt, plan.GetProperty("objects")[2].GetProperty("kind").GetString());
        Assert.Equal(2, plan.GetProperty("objects")[2].GetProperty("dx").GetInt32());

        var replaced = (await client.SendAsync("plans.save", Line("Linia pieca", 5))).Payload().GetProperty("plans");
        Assert.Equal(6, Assert.Single(replaced.EnumerateArray()).GetProperty("objects").GetArrayLength());

        // Each player sees their own plans only, and nobody deletes another's.
        using var other = await factory.ConnectAsync();
        await other.RegisterWithCharacterAsync();
        Assert.Empty((await other.SendAsync("plans.get")).Payload().GetProperty("plans").EnumerateArray());
        var id = replaced[0].GetProperty("id").GetInt64();
        Assert.Equal("no_plan", (await other.SendAsync("plans.delete", new { id })).ErrorCode());

        Assert.Empty((await client.SendAsync("plans.delete", new { id })).Payload().GetProperty("plans").EnumerateArray());
    }

    [Fact]
    public async Task A_plan_is_checked_and_a_player_keeps_a_limited_number()
    {
        using var client = await factory.ConnectAsync();
        await client.RegisterWithCharacterAsync();

        Assert.Equal("bad_plan_name", (await client.SendAsync("plans.save", Line("   "))).ErrorCode());
        Assert.Equal("bad_plan_name", (await client.SendAsync("plans.save", Line(new string('x', PlanRules.MaxName + 1)))).ErrorCode());
        Assert.Equal("bad_plan", (await client.SendAsync("plans.save", new { name = "pusty", objects = Array.Empty<object>() })).ErrorCode());
        Assert.Equal("bad_plan", (await client.SendAsync("plans.save",
            new { name = "nie stoi", objects = new[] { new { kind = Items.Wood, dx = 0, dy = 0, rotation = 0 } } })).ErrorCode());
        Assert.Equal("bad_plan", (await client.SendAsync("plans.save",
            new { name = "za daleko", objects = new[] { new { kind = Items.Belt, dx = PlotLayout.Size, dy = 0, rotation = 0 } } })).ErrorCode());
        Assert.Equal("bad_plan", (await client.SendAsync("plans.save", Line("za długi", PlotObjects.MaxPlan + 1))).ErrorCode());

        for (var i = 0; i < PlanRules.MaxPlans; i++)
        {
            Assert.Equal("plans", (await client.SendAsync("plans.save", Line($"plan {i}"))).Type());
        }

        Assert.Equal("too_many_plans", (await client.SendAsync("plans.save", Line("jeszcze jeden"))).ErrorCode());
        // One of the same name still replaces.
        Assert.Equal("plans", (await client.SendAsync("plans.save", Line("plan 0", 4))).Type());
    }
}
