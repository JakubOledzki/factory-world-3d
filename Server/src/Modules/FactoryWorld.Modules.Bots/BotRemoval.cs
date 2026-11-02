using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace FactoryWorld.Modules.Bots;

/// <summary>
/// Takes the bots out of the game for good while they are off (<see cref="BotsOptions.RemoveWhenOff"/>): their
/// accounts, characters and everything that is theirs in every module, and what stands on their plots, which become
/// free. Every module keeps a player's things under his account id or under the id of something of his (a machine, a
/// company), and things on a plot under the plot's x and y, so the removal follows those ids and places through every
/// table there is, also tables of modules added later. Ids are unique, so nothing of a real player's matches by chance;
/// a row that only mentions a bot (a transfer from a player to a bot, a vote for one) goes too. Turned on again, the
/// bots start afresh.
/// </summary>
public static class BotRemoval
{
    // Kept on a freed plot: the land's own state, which nobody owns (and the garages, whose x and y are cells of a room)
    private static readonly HashSet<string> KeptByPlace = new(StringComparer.Ordinal)
    {
        "recycling.garage_objects", "world.meteors", "world.oil_fields", "world.surveys", "world.beach_finds", "world.export_prices",
    };

    /// <summary>Removes every bot with all that is theirs in one transaction; the rows removed, by table.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> RemoveAsync(NpgsqlDataSource data, ILogger logger, CancellationToken cancellationToken)
    {
        var removed = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await data.OpenConnectionAsync(cancellationToken);
        if (await ScalarAsync(connection, null, "SELECT to_regclass('bots.bots') IS NOT NULL", cancellationToken) is not true)
        {
            return removed;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var ids = new HashSet<Guid>();
        await using (var command = new NpgsqlCommand("SELECT account_id FROM bots.bots", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        if (ids.Count == 0)
        {
            return removed;
        }

        var bots = ids.Count;
        var tables = await TablesAsync(connection, transaction, cancellationToken);

        // Their plots, then all that stands and grows on them
        var xs = new List<int>();
        var ys = new List<int>();
        if (tables.TryGetValue("world.plots", out var plots) && plots.HasPlace)
        {
            await using var command = new NpgsqlCommand("SELECT x, y FROM world.plots WHERE owner_id = ANY(@ids)", connection, transaction);
            command.Parameters.AddWithValue("ids", ids.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                xs.Add(reader.GetInt32(0));
                ys.Add(reader.GetInt32(1));
            }
        }

        if (xs.Count > 0)
        {
            foreach (var table in tables.Values.Where(t => t.HasPlace && !KeptByPlace.Contains(t.Name)))
            {
                await DeleteAsync(connection, transaction, table,
                    $"(x, y) IN (SELECT * FROM unnest(@xs, @ys))", command =>
                    {
                        command.Parameters.Add(new NpgsqlParameter("xs", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = xs.ToArray() });
                        command.Parameters.Add(new NpgsqlParameter("ys", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = ys.ToArray() });
                    }, ids, removed, cancellationToken);
            }
        }

        // The companies they are in: one that only bots were in goes with them
        var companies = new List<Guid>();
        if (tables.TryGetValue("companies.members", out var members) && members.Ids.Contains("account_id") && members.Ids.Contains("company_id"))
        {
            await using var command = new NpgsqlCommand(
                "SELECT DISTINCT company_id FROM companies.members WHERE account_id = ANY(@ids)", connection, transaction);
            command.Parameters.AddWithValue("ids", ids.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                companies.Add(reader.GetGuid(0));
            }
        }

        // Everything under their ids, and under the ids of what went with them, until nothing new turns up
        var done = 0;
        while (done != ids.Count)
        {
            done = ids.Count;
            foreach (var table in tables.Values)
            {
                foreach (var column in table.Ids)
                {
                    await DeleteAsync(connection, transaction, table, $"{Quote(column)} = ANY(@ids)",
                        command => command.Parameters.AddWithValue("ids", ids.ToArray()), ids, removed, cancellationToken);
                }
            }

            if (companies.Count > 0 && tables.TryGetValue("companies.companies", out var company))
            {
                await DeleteAsync(connection, transaction, company,
                    "id = ANY(@companies) AND NOT EXISTS (SELECT 1 FROM companies.members m WHERE m.company_id = companies.id)",
                    command => command.Parameters.AddWithValue("companies", companies.ToArray()), ids, removed, cancellationToken);
            }
        }

        await using (var command = new NpgsqlCommand("DELETE FROM bots.bots", connection, transaction))
        {
            removed["bots.bots"] = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Removed {Bots} bots and their {Plots} plots: {Rows}", bots, xs.Count,
            string.Join(", ", removed.Select(r => $"{r.Key} {r.Value}")));
        return removed;
    }

    private sealed record Table(string Name, List<string> Ids, string? Key, bool HasPlace);

    // Every module's table with its id columns, its own id (a single id key) and whether it is kept by plot (x and y)
    private static async Task<Dictionary<string, Table>> TablesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, Table>(StringComparer.Ordinal);
        Table Of(string name) => tables.TryGetValue(name, out var table) ? table : tables[name] = new Table(name, [], null, false);

        await using (var command = new NpgsqlCommand(
            """
            SELECT c.table_schema || '.' || c.table_name, c.column_name, c.data_type
            FROM information_schema.columns c
            JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema', 'bots') AND c.table_name NOT LIKE '\_\_%'
            ORDER BY 1, c.ordinal_position
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var places = new Dictionary<string, int>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = Of(reader.GetString(0));
                var (column, type) = (reader.GetString(1), reader.GetString(2));
                if (type == "uuid")
                {
                    table.Ids.Add(column);
                }
                else if (type == "integer" && column is "x" or "y")
                {
                    places[table.Name] = places.GetValueOrDefault(table.Name) + 1;
                }
            }

            foreach (var (name, count) in places.Where(p => p.Value == 2))
            {
                tables[name] = tables[name] with { HasPlace = true };
            }
        }

        await using (var command = new NpgsqlCommand(
            """
            SELECT n.nspname || '.' || r.relname, a.attname
            FROM pg_index i
            JOIN pg_class r ON r.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = r.relnamespace
            JOIN pg_attribute a ON a.attrelid = r.oid AND a.attnum = i.indkey[0]
            WHERE i.indisprimary AND i.indnatts = 1 AND a.atttypid = 'uuid'::regtype
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (tables.TryGetValue(reader.GetString(0), out var table))
                {
                    tables[table.Name] = table with { Key = reader.GetString(1) };
                }
            }
        }

        return tables;
    }

    private static async Task DeleteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Table table, string where,
        Action<NpgsqlCommand> parameters, HashSet<Guid> ids, SortedDictionary<string, long> removed, CancellationToken cancellationToken)
    {
        var name = string.Join('.', table.Name.Split('.').Select(Quote));
        await using var command = new NpgsqlCommand(
            $"DELETE FROM {name} WHERE {where} RETURNING {(table.Key is null ? "NULL::uuid" : Quote(table.Key))}", connection, transaction);
        parameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        long count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            if (!reader.IsDBNull(0))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        if (count > 0)
        {
            removed[table.Name] = removed.GetValueOrDefault(table.Name) + count;
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
