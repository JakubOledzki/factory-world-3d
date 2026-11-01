using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Mentoring;

/// <summary>A new player (the mentee) and the experienced one who looks after them.</summary>
public sealed class MentorshipRow
{
    public Guid MenteeId { get; set; }

    public Guid MentorId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the mentee took the reward for reaching <see cref="MentorRules.GoalLevel"/>; null before.</summary>
    public DateTimeOffset? MenteeRewardedAt { get; set; }

    public DateTimeOffset? MentorRewardedAt { get; set; }
}

public sealed class MentoringDbContext(DbContextOptions<MentoringDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "mentoring";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<MentorshipRow>(e =>
        {
            e.ToTable("mentorships");
            e.HasKey(m => m.MenteeId);
            e.Property(m => m.MenteeId).HasColumnName("mentee_id");
            e.Property(m => m.MentorId).HasColumnName("mentor_id");
            e.Property(m => m.StartedAt).HasColumnName("started_at");
            e.Property(m => m.MenteeRewardedAt).HasColumnName("mentee_rewarded_at");
            e.Property(m => m.MentorRewardedAt).HasColumnName("mentor_rewarded_at");
            e.HasIndex(m => m.MentorId);
        });
    }
}

/// <summary>
/// Mentors: a player below <see cref="ChooseBelowLevel"/> may ask an experienced one (from <see cref="MentorLevel"/>) to look
/// after them. The mentor sees how their mentees get on; once a mentee reaches <see cref="GoalLevel"/>, both take a reward of
/// experience and the mentor a badge. A mentee keeps one mentor for good; a mentor looks after a few at a time.
/// </summary>
public static class MentorRules
{
    public const int ChooseBelowLevel = 10;
    public const int MentorLevel = 20;
    public const int GoalLevel = 10;

    /// <summary>Mentees a mentor looks after at once (those that reached the goal no longer count).</summary>
    public const int Mentees = 3;

    public const long MenteeXp = 2_000;
    public const long MentorXp = 3_000;
}

public sealed record Mentorship(Guid Mentee, Guid Mentor, DateTimeOffset StartedAt, DateTimeOffset? MenteeRewardedAt, DateTimeOffset? MentorRewardedAt);

public sealed class MentoringService(GameDb db, ICharacterNames names, ICharacterXp levels, ICharacterProgress progress, OnlinePlayers online, TimeProvider time)
{
    private async Task<List<Mentorship>> ReadAsync(string where, Guid player, CancellationToken cancellationToken)
    {
        var rows = new List<Mentorship>();
        await using var command = await db.CommandAsync(
            $"SELECT mentee_id, mentor_id, started_at, mentee_rewarded_at, mentor_rewarded_at FROM mentoring.mentorships WHERE {where} = @player ORDER BY started_at",
            cancellationToken);
        command.Parameters.AddWithValue("player", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Mentorship(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    public async Task<object> StateAsync(Guid player, CancellationToken cancellationToken)
    {
        var level = await levels.LevelAsync(player, cancellationToken);
        var mine = (await ReadAsync("mentee_id", player, cancellationToken)).FirstOrDefault();
        var mentees = await ReadAsync("mentor_id", player, cancellationToken);
        var people = mentees.Select(m => m.Mentee).Concat(mine is null ? [] : [mine.Mentor]).ToList();
        var known = await names.NamesAsync(people, cancellationToken);
        var levelsOf = new Dictionary<Guid, int>();
        var xpOf = new Dictionary<Guid, long>();
        foreach (var person in people)
        {
            levelsOf[person] = await levels.LevelAsync(person, cancellationToken);
            xpOf[person] = await levels.XpAsync(person, cancellationToken) ?? 0;
        }

        return new
        {
            level,
            canChoose = mine is null && level < MentorRules.ChooseBelowLevel,
            canMentor = level >= MentorRules.MentorLevel,
            rules = new
            {
                chooseBelowLevel = MentorRules.ChooseBelowLevel,
                mentorLevel = MentorRules.MentorLevel,
                goalLevel = MentorRules.GoalLevel,
                mentees = MentorRules.Mentees,
                menteeXp = MentorRules.MenteeXp,
                mentorXp = MentorRules.MentorXp,
            },
            mentor = mine is null ? null : new
            {
                name = known.GetValueOrDefault(mine.Mentor, "?"),
                level = levelsOf[mine.Mentor],
                online = online.IsOnline(mine.Mentor),
                since = mine.StartedAt.ToUnixTimeMilliseconds(),
                reward = level >= MentorRules.GoalLevel && mine.MenteeRewardedAt is null,
                rewarded = mine.MenteeRewardedAt is not null,
            },
            mentees = mentees.Select(m => new
            {
                name = known.GetValueOrDefault(m.Mentee, "?"),
                level = levelsOf[m.Mentee],
                xp = xpOf[m.Mentee],
                online = online.IsOnline(m.Mentee),
                since = m.StartedAt.ToUnixTimeMilliseconds(),
                reward = levelsOf[m.Mentee] >= MentorRules.GoalLevel && m.MentorRewardedAt is null,
                rewarded = m.MentorRewardedAt is not null,
            }),
        };
    }

    /// <summary>The player asks <paramref name="mentorName"/> to be their mentor. Fails with <c>unknown_player</c>, <c>own_mentor</c>,
    /// <c>has_mentor</c>, <c>too_experienced</c> (the player is past the level for it), <c>mentor_level</c> or <c>mentor_full</c>.</summary>
    public Task ChooseAsync(Guid player, string mentorName, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var mentor = await names.AccountAsync(mentorName.Trim(), cancellationToken) ?? throw new GameException("unknown_player", "There is no such player.");
            if (mentor == player)
            {
                throw new GameException("own_mentor", "You cannot be your own mentor.");
            }

            if (await levels.LevelAsync(player, cancellationToken) >= MentorRules.ChooseBelowLevel)
            {
                throw new GameException("too_experienced", $"Only players below level {MentorRules.ChooseBelowLevel} choose a mentor.");
            }

            if (await levels.LevelAsync(mentor, cancellationToken) < MentorRules.MentorLevel)
            {
                throw new GameException("mentor_level", $"A mentor needs level {MentorRules.MentorLevel}.");
            }

            // One choice at a time per mentor, so two new players cannot both take the last place.
            await using (var lockMentor = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockMentor.Parameters.AddWithValue("key", BitConverter.ToInt64(mentor.ToByteArray()) ^ 0x6d656e74_6f720000);
                await lockMentor.ExecuteNonQueryAsync(cancellationToken);
            }

            var taken = 0;
            foreach (var m in await ReadAsync("mentor_id", mentor, cancellationToken))
            {
                if (await levels.LevelAsync(m.Mentee, cancellationToken) < MentorRules.GoalLevel)
                {
                    taken++;
                }
            }

            if (taken >= MentorRules.Mentees)
            {
                throw new GameException("mentor_full", $"The mentor looks after {MentorRules.Mentees} new players already.");
            }

            await using var insert = await db.CommandAsync(
                """
                INSERT INTO mentoring.mentorships (mentee_id, mentor_id, started_at) VALUES (@mentee, @mentor, @now)
                ON CONFLICT (mentee_id) DO NOTHING
                """, cancellationToken);
            insert.Parameters.AddWithValue("mentee", player);
            insert.Parameters.AddWithValue("mentor", mentor);
            insert.Parameters.AddWithValue("now", time.GetUtcNow());
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("has_mentor", "You have a mentor already.");
            }
        }, cancellationToken);

    /// <summary>Takes the reward for a mentee who reached the goal level: the mentee's own, or, with <paramref name="menteeName"/>,
    /// the mentor's for that mentee. Fails with <c>no_mentorship</c>, <c>goal_not_reached</c> or <c>already_claimed</c>.</summary>
    public Task<(string Role, XpGain Xp)> ClaimAsync(Guid player, string? menteeName, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            Guid mentee;
            string role;
            if (string.IsNullOrWhiteSpace(menteeName))
            {
                mentee = player;
                role = "mentee";
            }
            else
            {
                mentee = await names.AccountAsync(menteeName.Trim(), cancellationToken) ?? throw new GameException("no_mentorship", "That player is not your mentee.");
                role = "mentor";
            }

            if (await levels.LevelAsync(mentee, cancellationToken) < MentorRules.GoalLevel)
            {
                throw new GameException("goal_not_reached", $"The new player has not reached level {MentorRules.GoalLevel} yet.");
            }

            var column = role == "mentee" ? "mentee_rewarded_at" : "mentor_rewarded_at";
            await using var claim = await db.CommandAsync(
                $"""
                UPDATE mentoring.mentorships SET {column} = @now
                WHERE mentee_id = @mentee AND (@role = 'mentee' OR mentor_id = @player) AND {column} IS NULL
                """, cancellationToken);
            claim.Parameters.AddWithValue("now", time.GetUtcNow());
            claim.Parameters.AddWithValue("mentee", mentee);
            claim.Parameters.AddWithValue("player", player);
            claim.Parameters.AddWithValue("role", role);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                var exists = (await ReadAsync("mentee_id", mentee, cancellationToken)).Any(m => role == "mentee" || m.Mentor == player);
                throw exists
                    ? new GameException("already_claimed", "The reward was taken already.")
                    : new GameException("no_mentorship", role == "mentee" ? "You have no mentor." : "That player is not your mentee.");
            }

            return (role, await progress.AddXpAsync(player, role == "mentee" ? MentorRules.MenteeXp : MentorRules.MentorXp, cancellationToken));
        }, cancellationToken);
}

public sealed class GetMentoringHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "mentor.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return new Reply("mentoring", await scope.ServiceProvider.GetRequiredService<MentoringService>().StateAsync(client.RequireAccount(), cancellationToken));
    }
}

public sealed record ChooseMentorRequest(string Name);

public sealed class ChooseMentorHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "mentor.choose";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        var request = Payload.Read<ChooseMentorRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var mentoring = scope.ServiceProvider.GetRequiredService<MentoringService>();
        await mentoring.ChooseAsync(player, request.Name ?? "", cancellationToken);
        return new Reply("mentoring", await mentoring.StateAsync(player, cancellationToken));
    }
}

public sealed record ClaimMentorRewardRequest(string? Mentee);

public sealed class ClaimMentorRewardHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "mentor.claim";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var player = client.RequireAccount();
        var request = payload is { ValueKind: JsonValueKind.Object } ? Payload.Read<ClaimMentorRewardRequest>(payload) : new ClaimMentorRewardRequest(null);
        await using var scope = scopes.CreateAsyncScope();
        var mentoring = scope.ServiceProvider.GetRequiredService<MentoringService>();
        var (role, xp) = await mentoring.ClaimAsync(player, request.Mentee, cancellationToken);
        return new Reply("mentoring", new
        {
            role,
            xp = new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp },
            mentoring = await mentoring.StateAsync(player, cancellationToken),
        });
    }
}

/// <summary>Mentors for new players. Owns the <c>mentoring</c> schema.</summary>
public sealed class MentoringModule : IGameModule
{
    public string Name => "Mentoring";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<MentoringDbContext>(MentoringDbContext.Schema);
        services.AddScoped<MentoringService>();
        services.AddSingleton<IMessageHandler, GetMentoringHandler>();
        services.AddSingleton<IMessageHandler, ChooseMentorHandler>();
        services.AddSingleton<IMessageHandler, ClaimMentorRewardHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<MentoringDbContext>(cancellationToken);
}
