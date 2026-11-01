using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Politics;

public sealed partial class NationService
{
    private async Task<bool> HasRelationAsync(string country, string other, string kind, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT 1 FROM politics.relations WHERE country = @country AND other = @other AND kind = @kind", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("other", other);
        command.Parameters.AddWithValue("kind", kind);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task SetRelationAsync(string country, string other, string kind, bool on, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            on
                ? "INSERT INTO politics.relations (country, other, kind, since) VALUES (@country, @other, @kind, @now) ON CONFLICT DO NOTHING"
                : "DELETE FROM politics.relations WHERE country = @country AND other = @other AND kind = @kind",
            cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("other", other);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private (CountryInfo Country, CountryInfo Other) RequirePair(string? code, string? other)
    {
        var country = RequireCountry(code);
        var second = RequireCountry(other);
        if (country.Code == second.Code)
        {
            throw new GameException("same_country", "A country has no relations with itself.");
        }

        return (country, second);
    }

    /// <summary>The president or the foreign minister offers another country an alliance, or accepts the one it offered; with
    /// <paramref name="on"/> false they withdraw the offer or break the alliance.</summary>
    public Task<bool> AllianceAsync(Guid player, string? code, string? other, bool on, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (country, second) = RequirePair(code, other);
            await RequireOfficeAsync(player, country.Code, CountryOffices.Foreign, cancellationToken);
            // Both countries in one order, so two governments answering at once do not lock each other out.
            foreach (var c in new[] { country.Code, second.Code }.Order())
            {
                await LockAsync(c, cancellationToken);
            }

            if (!on)
            {
                await SetRelationAsync(country.Code, second.Code, "alliance", false, cancellationToken);
                await SetRelationAsync(second.Code, country.Code, "alliance", false, cancellationToken);
                await OfferAsync(country.Code, second.Code, false, cancellationToken);
                return false;
            }

            if (await HasRelationAsync(country.Code, second.Code, "embargo", cancellationToken)
                || await HasRelationAsync(second.Code, country.Code, "embargo", cancellationToken))
            {
                throw new GameException("embargo", "Lift the embargo between the countries first.");
            }

            if (!await HasOfferAsync(second.Code, country.Code, cancellationToken))
            {
                await OfferAsync(country.Code, second.Code, true, cancellationToken);
                return false;
            }

            await OfferAsync(second.Code, country.Code, false, cancellationToken);
            await OfferAsync(country.Code, second.Code, false, cancellationToken);
            await SetRelationAsync(country.Code, second.Code, "alliance", true, cancellationToken);
            await SetRelationAsync(second.Code, country.Code, "alliance", true, cancellationToken);
            return true;
        }, cancellationToken);

    private async Task<bool> HasOfferAsync(string country, string other, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT 1 FROM politics.alliance_offers WHERE country = @country AND other = @other", cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("other", other);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task OfferAsync(string country, string other, bool on, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            on
                ? "INSERT INTO politics.alliance_offers (country, other, offered_at) VALUES (@country, @other, @now) ON CONFLICT DO NOTHING"
                : "DELETE FROM politics.alliance_offers WHERE country = @country AND other = @other",
            cancellationToken);
        command.Parameters.AddWithValue("country", country);
        command.Parameters.AddWithValue("other", other);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The president or the foreign minister puts an embargo on another country (or lifts it): its citizens buy no plots here
    /// and its goods do not come in. Allies break their alliance first.</summary>
    public Task EmbargoAsync(Guid player, string? code, string? other, bool on, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (country, second) = RequirePair(code, other);
            await RequireOfficeAsync(player, country.Code, CountryOffices.Foreign, cancellationToken);
            if (on && await HasRelationAsync(country.Code, second.Code, "alliance", cancellationToken))
            {
                throw new GameException("allied", "Break the alliance before an embargo.");
            }

            await SetRelationAsync(country.Code, second.Code, "embargo", on, cancellationToken);
            if (on)
            {
                await OfferAsync(country.Code, second.Code, false, cancellationToken);
            }
        }, cancellationToken);

    /// <summary>The country's relations: allies, embargoes both ways, alliance offers both ways.</summary>
    private async Task<object> RelationsAsync(string country, CancellationToken cancellationToken)
    {
        var rows = new List<(string Country, string Other, string Kind)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT country, other, kind FROM politics.relations WHERE country = @country OR other = @country
            UNION ALL
            SELECT country, other, 'offer' FROM politics.alliance_offers WHERE country = @country OR other = @country
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("country", country);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        object Named(string code) => new { code, name = map.Find(code)?.Name ?? code };
        return new
        {
            allies = rows.Where(r => r.Kind == "alliance" && r.Country == country).Select(r => Named(r.Other)),
            embargoes = rows.Where(r => r.Kind == "embargo" && r.Country == country).Select(r => Named(r.Other)),
            embargoedBy = rows.Where(r => r.Kind == "embargo" && r.Other == country).Select(r => Named(r.Country)),
            offersMade = rows.Where(r => r.Kind == "offer" && r.Country == country).Select(r => Named(r.Other)),
            offersGot = rows.Where(r => r.Kind == "offer" && r.Other == country).Select(r => Named(r.Country)),
        };
    }

    // --- Parties ---------------------------------------------------------------------------------

    public async Task<IReadOnlyDictionary<Guid, string>> PartiesAsync(IReadOnlyCollection<Guid> players, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT m.player_id, p.name FROM politics.party_members m JOIN politics.parties p ON p.id = m.party_id
            WHERE m.player_id = ANY(@players)
            """, cancellationToken);
        command.Parameters.AddWithValue("players", players.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var parties = new Dictionary<Guid, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            parties[reader.GetGuid(0)] = reader.GetString(1);
        }

        return parties;
    }

    private async Task<(Guid Id, string Name, Guid Leader)?> PartyOfAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT p.id, p.name, p.leader_id FROM politics.party_members m JOIN politics.parties p ON p.id = m.party_id
            WHERE m.player_id = @player
            """, cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2)) : null;
    }

    /// <summary>A player founds a party for a fee and leads it; they are in one party at most.</summary>
    public Task CreatePartyAsync(Guid player, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            name = (name ?? "").Trim();
            if (name.Length < NationRules.MinPartyName || name.Length > NationRules.MaxPartyName)
            {
                throw new GameException("bad_party_name", $"A party's name has {NationRules.MinPartyName}-{NationRules.MaxPartyName} characters.");
            }

            if (await PartyOfAsync(player, cancellationToken) is not null)
            {
                throw new GameException("in_party", "Leave your party first.");
            }

            await using (var taken = await db.CommandAsync("SELECT 1 FROM politics.parties WHERE lower(name) = lower(@name)", cancellationToken))
            {
                taken.Parameters.AddWithValue("name", name);
                if (await taken.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    throw new GameException("name_taken", "There is a party of this name already.");
                }
            }

            await wallet.DebitAsync(player, NationRules.PartyCents, $"party {name} founded", cancellationToken);
            var id = Guid.NewGuid();
            await using var insert = await db.CommandAsync(
                """
                INSERT INTO politics.parties (id, name, leader_id, created_at) VALUES (@id, @name, @player, @now);
                INSERT INTO politics.party_members (player_id, party_id, joined_at) VALUES (@player, @id, @now)
                """, cancellationToken);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("player", player);
            insert.Parameters.AddWithValue("now", time.GetUtcNow());
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
            {
                throw new GameException("name_taken", "There is a party of this name already.");
            }
        }, cancellationToken);

    /// <summary>A player who is in no party joins one; parties are open to all.</summary>
    public Task JoinPartyAsync(Guid player, string? name, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (await PartyOfAsync(player, cancellationToken) is not null)
            {
                throw new GameException("in_party", "Leave your party first.");
            }

            await using var join = await db.CommandAsync(
                """
                INSERT INTO politics.party_members (player_id, party_id, joined_at)
                SELECT @player, id, @now FROM politics.parties WHERE lower(name) = lower(@name)
                ON CONFLICT DO NOTHING
                """, cancellationToken);
            join.Parameters.AddWithValue("player", player);
            join.Parameters.AddWithValue("name", (name ?? "").Trim());
            join.Parameters.AddWithValue("now", time.GetUtcNow());
            if (await join.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("no_party", "There is no party of this name.");
            }
        }, cancellationToken);

    /// <summary>A member leaves their party; when the leader leaves, the longest member leads, and a party nobody is in is gone.</summary>
    public Task LeavePartyAsync(Guid player, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (await PartyOfAsync(player, cancellationToken) is not { } party)
            {
                throw new GameException("no_party", "You are in no party.");
            }

            await using var leave = await db.CommandAsync(
                """
                DELETE FROM politics.party_members WHERE player_id = @player;
                UPDATE politics.parties SET leader_id = (SELECT player_id FROM politics.party_members WHERE party_id = @party ORDER BY joined_at, player_id LIMIT 1)
                WHERE id = @party AND leader_id = @player AND EXISTS (SELECT 1 FROM politics.party_members WHERE party_id = @party);
                DELETE FROM politics.parties WHERE id = @party AND NOT EXISTS (SELECT 1 FROM politics.party_members WHERE party_id = @party)
                """, cancellationToken);
            leave.Parameters.AddWithValue("player", player);
            leave.Parameters.AddWithValue("party", party.Id);
            await leave.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>The parties, biggest first, with how many presidents they have this term, and the player's own.</summary>
    public async Task<object> PartiesViewAsync(Guid player, CancellationToken cancellationToken)
    {
        var parties = new List<(string Name, Guid Leader, long Members, long Presidents)>();
        await using (var command = await db.CommandAsync(
            """
            SELECT p.name, p.leader_id,
                   (SELECT count(*) FROM politics.party_members m WHERE m.party_id = p.id),
                   (SELECT count(*) FROM politics.governments g JOIN politics.party_members m ON m.player_id = g.president_id
                    WHERE m.party_id = p.id AND g.term = @term)
            FROM politics.parties p ORDER BY 3 DESC, p.name LIMIT 50
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("term", Term);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                parties.Add((reader.GetString(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3)));
            }
        }

        var mine = await PartyOfAsync(player, cancellationToken);
        var members = new List<Guid>();
        if (mine is { } party)
        {
            await using var command = await db.CommandAsync(
                "SELECT player_id FROM politics.party_members WHERE party_id = @party ORDER BY joined_at", cancellationToken);
            command.Parameters.AddWithValue("party", party.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add(reader.GetGuid(0));
            }
        }

        var named = await names.NamesAsync(parties.Select(p => p.Leader).Concat(members).Distinct().ToList(), cancellationToken);
        return new
        {
            costCents = NationRules.PartyCents,
            mine = mine is { } my
                ? new { name = my.Name, leader = named.GetValueOrDefault(my.Leader, "?"), isLeader = my.Leader == player, members = members.Select(m => named.GetValueOrDefault(m, "?")) }
                : null,
            parties = parties.Select(p => new { name = p.Name, leader = named.GetValueOrDefault(p.Leader, "?"), members = p.Members, presidents = p.Presidents }),
        };
    }
}
