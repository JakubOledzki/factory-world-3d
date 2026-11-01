using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;

namespace FactoryWorld.Modules.Rivalry;

/// <summary>A player's world firsts and places on the weekly podiums, for their trophy case.</summary>
public sealed class RivalryHonours(GameDb db) : IHonours
{
    private static readonly string[] Places = ["Złoto", "Srebro", "Brąz"];

    public async Task<IReadOnlyList<Honour>> HonoursAsync(Guid player, CancellationToken cancellationToken)
    {
        var honours = new List<Honour>();
        await using (var command = await db.CommandAsync("SELECT id, at FROM rivalry.firsts WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (Firsts.All.FirstOrDefault(f => f.Id == reader.GetString(0)) is { } first)
                {
                    honours.Add(new Honour("first", $"Pierwszy na serwerze: {first.Name}", first.Description, reader.GetFieldValue<DateTimeOffset>(1)));
                }
            }
        }

        await using (var command = await db.CommandAsync("SELECT week, place FROM rivalry.challenge_winners WHERE owner_id = @owner", cancellationToken))
        {
            command.Parameters.AddWithValue("owner", player);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var week = reader.GetInt32(0);
                var place = reader.GetInt32(1);
                var challenge = Challenges.For(week);
                var start = Challenges.WeekStart(week);
                honours.Add(new Honour("trophy", $"{challenge.Name}: {Places[Math.Clamp(place, 1, 3) - 1].ToLowerInvariant()}",
                    $"{place}. miejsce w tygodniu od {start:dd.MM.yyyy}. {challenge.Description}", Challenges.WeekStart(week + 1)));
            }
        }

        return honours.OrderByDescending(h => h.At).ToList();
    }
}
