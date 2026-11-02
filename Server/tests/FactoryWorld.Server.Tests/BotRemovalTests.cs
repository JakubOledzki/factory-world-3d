using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.Bots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FactoryWorld.Server.Tests;

public sealed class BotRemovalTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private BotEngine Engine => factory.Services.GetRequiredService<BotEngine>();

    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [Fact]
    public void Bots_are_off_and_removed_unless_turned_on()
    {
        var options = new BotsOptions();
        Assert.False(options.Enabled);
        Assert.True(options.RemoveWhenOff);
    }

    [Fact]
    public async Task Removing_the_bots_leaves_nothing_of_theirs_and_keeps_the_players()
    {
        using var player = await factory.ConnectAsync();
        await player.RegisterWithCharacterAsync();
        var garage = (await player.SendAsync("garage.get")).Payload().GetProperty("objects").GetArrayLength();

        await Engine.EnsureBotsAsync(CancellationToken.None);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            foreach (var bot in Engine.Bots)
            {
                await scope.ServiceProvider.GetRequiredService<IWallet>().CreditAsync(bot.Row.AccountId, 5_000_000, "test", CancellationToken.None);
            }
        }

        // They play a while: sort, buy plots, build on them, trade
        for (var i = 0; i < 40; i++)
        {
            factory.Time.Advance(TimeSpan.FromSeconds(11));
            foreach (var bot in Engine.Bots)
            {
                await Engine.StepAsync(bot.Profile.Name, CancellationToken.None);
            }
        }

        var bots = Engine.Bots.Select(b => b.Row.AccountId).ToArray();
        Assert.True(await CountAsync("SELECT count(*) FROM world.plots WHERE owner_id = ANY(@ids)", bots) > 0);
        var plots = await PlotsAsync(bots);
        var companies = await CountAsync("SELECT count(DISTINCT company_id) FROM companies.members WHERE account_id = ANY(@ids)", bots);

        var removed = await BotRemoval.RemoveAsync(Data, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(bots.Length, removed["bots.bots"]);
        Assert.Equal(companies, removed.GetValueOrDefault("companies.companies"));

        Assert.Equal(0, await MentionsAsync(bots));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM world.plot_objects WHERE (x, y) IN (SELECT * FROM unnest(@xs, @ys))", null, plots));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM accounts.accounts WHERE \"Id\" = @id", null, null, player.AccountId));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM bots.bots", null));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM companies.companies c WHERE NOT EXISTS (SELECT 1 FROM companies.members m WHERE m.company_id = c.id)", null));

        // The player plays on as before, and nothing more to remove
        var again = await player.SendAsync("garage.get");
        Assert.Equal(garage, again.Payload().GetProperty("objects").GetArrayLength());
        Assert.Empty(await BotRemoval.RemoveAsync(Data, NullLogger.Instance, CancellationToken.None));
    }

    // Rows anywhere that hold one of these ids
    private async Task<long> MentionsAsync(Guid[] ids)
    {
        await using var connection = await Data.OpenConnectionAsync();
        var columns = new List<(string Schema, string Table, string Column)>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT c.table_schema, c.table_name, c.column_name FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.data_type = 'uuid' AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        long found = 0;
        foreach (var (schema, table, column) in columns)
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"{table}\" WHERE \"{column}\" = ANY(@ids)", connection);
            command.Parameters.AddWithValue("ids", ids);
            found += (long)(await command.ExecuteScalarAsync())!;
        }

        return found;
    }

    private async Task<(int[] Xs, int[] Ys)> PlotsAsync(Guid[] owners)
    {
        await using var command = Data.CreateCommand("SELECT x, y FROM world.plots WHERE owner_id = ANY(@ids)");
        command.Parameters.AddWithValue("ids", owners);
        await using var reader = await command.ExecuteReaderAsync();
        var (xs, ys) = (new List<int>(), new List<int>());
        while (await reader.ReadAsync())
        {
            xs.Add(reader.GetInt32(0));
            ys.Add(reader.GetInt32(1));
        }

        return (xs.ToArray(), ys.ToArray());
    }

    private async Task<long> CountAsync(string sql, Guid[]? ids, (int[] Xs, int[] Ys)? plots = null, Guid? id = null)
    {
        await using var command = Data.CreateCommand(sql);
        if (ids is not null)
        {
            command.Parameters.AddWithValue("ids", ids);
        }

        if (plots is { } p)
        {
            command.Parameters.AddWithValue("xs", p.Xs);
            command.Parameters.AddWithValue("ys", p.Ys);
        }

        if (id is { } one)
        {
            command.Parameters.AddWithValue("id", one);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
