using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Companies;

/// <summary>A company's progress towards a wonder of the world: the share of its parts brought, in percent.</summary>
public sealed record WonderRacer(Guid Company, string Name, double Percent);

/// <summary>A wonder of the world as everybody sees it: who built it, or who races for it.</summary>
public sealed record WonderState(BigProject Project, Guid? Owner, string? OwnerName, DateTimeOffset? ClaimedAt, IReadOnlyList<WonderRacer> Racers);

public sealed partial class CompanyService
{
    /// <summary>How many of the companies racing for a wonder its view lists.</summary>
    public const int WonderRacersShown = 3;

    /// <summary>The company that brought the last part of a wonder first claims it, for good; if another one did a moment before, the
    /// whole delivery fails, so the parts stay with whoever brought them.</summary>
    private async Task ClaimWonderAsync(Guid company, BigProject wonder, CancellationToken cancellationToken)
    {
        await using (var claim = await db.CommandAsync(
            "INSERT INTO companies.wonders (project, company_id, claimed_at) VALUES (@project, @company, @at) ON CONFLICT DO NOTHING", cancellationToken))
        {
            claim.Parameters.AddWithValue("project", wonder.Id);
            claim.Parameters.AddWithValue("company", company);
            claim.Parameters.AddWithValue("at", time.GetUtcNow());
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("wonder_taken", "Another company built this wonder of the world first.");
            }
        }

        var name = await NameOfAsync(company, cancellationToken);
        db.AfterCommit(() => announcements.Announce(
            $"Spółka {name} jako pierwsza zebrała wszystko na cud świata „{wonder.Name}” i zaczyna budowę. Inne spółki dostaną swoje części z powrotem."));
    }

    private async Task<string> NameOfAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT name FROM companies.companies WHERE id = @company", cancellationToken);
        command.Parameters.AddWithValue("company", company);
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? "?";
    }

    /// <summary>Every claimed wonder: the company that built it and its name ("?" for a company that is gone).</summary>
    private async Task<IReadOnlyDictionary<string, (Guid Company, string Name, DateTimeOffset At)>> WonderOwnersAsync(CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            """
            SELECT w.project, w.company_id, coalesce(c.name, '?'), w.claimed_at
            FROM companies.wonders w LEFT JOIN companies.companies c ON c.id = w.company_id
            """, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var owners = new Dictionary<string, (Guid, string, DateTimeOffset)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            owners[reader.GetString(0)] = (reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        return owners;
    }

    /// <summary>A wonder another company built first: the parts this company brought for it go back into its warehouse, and it drops the project.</summary>
    private async Task ReturnLostWondersAsync(Guid company, CancellationToken cancellationToken)
    {
        var lost = (await WonderOwnersAsync(cancellationToken)).Where(w => w.Value.Company != company).Select(w => w.Key).ToArray();
        if (lost.Length == 0)
        {
            return;
        }

        var parts = new List<(string Project, string Item, long Delivered)>();
        await using (var take = await db.CommandAsync(
            "DELETE FROM companies.project_parts WHERE company_id = @company AND project = ANY(@lost) RETURNING project, item, delivered", cancellationToken))
        {
            take.Parameters.AddWithValue("company", company);
            take.Parameters.AddWithValue("lost", lost);
            await using var reader = await take.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                parts.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        var dropped = new List<string>();
        await using (var drop = await db.CommandAsync(
            "DELETE FROM companies.projects WHERE company_id = @company AND project = ANY(@lost) RETURNING project", cancellationToken))
        {
            drop.Parameters.AddWithValue("company", company);
            drop.Parameters.AddWithValue("lost", lost);
            await using var reader = await drop.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dropped.Add(reader.GetString(0));
            }
        }

        foreach (var (_, item, delivered) in parts.Where(p => p.Delivered > 0))
        {
            await ChangeStockAsync(company, item, delivered, cancellationToken);
        }

        foreach (var project in dropped)
        {
            await LogAsync(company, Guid.Empty, "wonderLost", cancellationToken, item: project);
        }
    }

    /// <summary>Every wonder of the world: who built it, or how far the companies racing for it got.</summary>
    public async Task<IReadOnlyList<WonderState>> WondersAsync(CancellationToken cancellationToken)
    {
        var owners = await WonderOwnersAsync(cancellationToken);
        var delivered = new Dictionary<(string Project, Guid Company), Dictionary<string, long>>();
        var names = new Dictionary<Guid, string>();
        await using (var command = await db.CommandAsync(
            """
            SELECT p.project, p.company_id, c.name, pp.item, pp.delivered
            FROM companies.projects p
            JOIN companies.companies c ON c.id = p.company_id
            LEFT JOIN companies.project_parts pp ON pp.company_id = p.company_id AND pp.project = p.project
            WHERE p.project = ANY(@wonders)
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("wonders", BigProjects.Wonders.Select(w => w.Id).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetGuid(1));
                names[key.Item2] = reader.GetString(2);
                var parts = delivered.TryGetValue(key, out var found) ? found : delivered[key] = [];
                if (!reader.IsDBNull(3))
                {
                    parts[reader.GetString(3)] = reader.GetInt64(4);
                }
            }
        }

        return BigProjects.Wonders.Select(w =>
        {
            var needed = w.Parts.Sum(p => p.Quantity);
            var racers = owners.ContainsKey(w.Id)
                ? []
                : delivered.Where(d => d.Key.Project == w.Id)
                    .Select(d => new WonderRacer(d.Key.Company, names[d.Key.Company],
                        Math.Round(100.0 * w.Parts.Sum(p => Math.Min(p.Quantity, d.Value.GetValueOrDefault(p.Item))) / needed, 1)))
                    .OrderByDescending(r => r.Percent).ThenBy(r => r.Name, StringComparer.Ordinal)
                    .Take(WonderRacersShown)
                    .ToList();
            return owners.TryGetValue(w.Id, out var owner)
                ? new WonderState(w, owner.Company, owner.Name, owner.At, racers)
                : new WonderState(w, null, null, null, racers);
        }).ToList();
    }
}

/// <summary>The wonders of the world, for everybody: who built each, or which companies race for it.</summary>
public sealed class WondersHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "company.wonders";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        await using var scope = scopes.CreateAsyncScope();
        var mine = (await scope.ServiceProvider.GetRequiredService<ICompanies>().MembershipAsync(player, cancellationToken))?.Company;
        var wonders = await scope.ServiceProvider.GetRequiredService<CompanyService>().WondersAsync(cancellationToken);
        return new Reply("company.wonders", new
        {
            wonders = wonders.Select(w => new
            {
                id = w.Project.Id,
                name = w.Project.Name,
                description = w.Project.Description,
                buildSeconds = w.Project.Build.TotalSeconds,
                parts = w.Project.Parts.Select(p => new { id = p.Item, name = Items.Names.GetValueOrDefault(p.Item, p.Item), quantity = p.Quantity }),
                owner = w.OwnerName,
                ours = w.Owner is { } o && o == mine,
                // Unix milliseconds.
                claimedAt = w.ClaimedAt?.ToUnixTimeMilliseconds(),
                racers = w.Racers.Select((r, i) => new { place = i + 1, name = r.Name, percent = r.Percent, ours = r.Company == mine }),
            }),
        });
    }
}

/// <summary>The colossus of industry: members of the company that built it get more experience.</summary>
public sealed class WonderBonuses(ICompanies companies) : IPlayerBonuses
{
    public async Task<int> PercentAsync(Guid player, string bonus, CancellationToken cancellationToken) =>
        bonus == PlayerBonus.Xp && (await companies.ProjectsAsync(player, cancellationToken)).Contains(CompanyPerks.Colossus)
            ? CompanyPerks.ColossusXpPercent
            : 0;
}
