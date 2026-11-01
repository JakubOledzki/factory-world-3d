using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.World;

/// <summary>How much oil or gas lies under a plot. Sent to the client, so never renumber.</summary>
public enum DepositSize : short
{
    None = 0,
    Small = 1,
    Medium = 2,
    Large = 3,
}

/// <summary>A mineral some mountain plots have in their rock, besides the ores. Sent to the client, so never renumber.</summary>
public enum MineralKind : short
{
    None = 0,
    Sulfur = 1,
    Salt = 2,
    Marble = 3,
    Quartz = 4,
}

/// <summary>The mineral in a plot's rock and how much of it there is; it comes up with the ore from its mines.</summary>
public sealed record Mineral(MineralKind Kind, DepositSize Size)
{
    /// <summary>Whether the mine's owner researched <see cref="GeologyTech"/>: the mineral comes up more often.</summary>
    public bool Rich { get; init; }

    public static readonly Mineral None = new(MineralKind.None, DepositSize.None);

    /// <summary>The item it comes up as, or null for none.</summary>
    public string? Item => Size == DepositSize.None ? null : Kind switch
    {
        MineralKind.Sulfur => Items.Sulfur,
        MineralKind.Salt => Items.Salt,
        MineralKind.Marble => Items.Marble,
        MineralKind.Quartz => Items.Quartz,
        _ => null,
    };
}

/// <param name="Seabed">On the coast, the oil under the seabed off the plot, which only an offshore platform reaches.</param>
public sealed record Survey(DepositSize Oil, DepositSize Gas, Mineral? Mineral = null, DepositSize Seabed = DepositSize.None);

/// <summary>
/// Oil and gas under the plots, and in the mountains a mineral in the rock (sulfur, salt, marble or
/// quartz). Nobody sees them on the map: only the owner of a plot can order a geological survey, which
/// reveals what lies under it. Deposits are fixed by the plot's position and only the server knows the formula.
/// </summary>
public static class Deposits
{
    public const long SurveyPriceCents = 100_000_00;

    public const int XpPerSurvey = 50;

    /// <summary>Chance (per mille) of an oil and of a gas deposit, by biome.</summary>
    private static readonly IReadOnlyDictionary<Biome, (int Oil, int Gas)> Chance = new Dictionary<Biome, (int, int)>
    {
        [Biome.Forest] = (60, 80),
        [Biome.Farmland] = (80, 100),
        [Biome.Grassland] = (120, 120),
        [Biome.Desert] = (250, 200),
        [Biome.Mountains] = (40, 60),
        [Biome.Tundra] = (300, 300),
        [Biome.Ice] = (150, 200),
        [Biome.City] = (50, 50),
        [Biome.Coast] = (200, 150),
        [Biome.Lakeshore] = (60, 80),
        [Biome.Oasis] = (250, 200),
    };

    /// <summary>Chance (per mille) of a mineral in the rock of a mountain plot; other plots have none.</summary>
    public const int MineralChance = 700;

    /// <summary>Chance (per mille) of oil under the seabed off a coast plot.</summary>
    public const int SeabedChance = 500;

    public static Survey Under(int x, int y, Biome biome)
    {
        var (oil, gas) = Chance.GetValueOrDefault(biome);
        return new Survey(Roll(x, y, 0x0111u, oil), Roll(x, y, 0x6a50u, gas), MineralIn(x, y, biome), SeabedIn(x, y, biome));
    }

    /// <summary>The oil under the seabed off a coast plot, whether or not it was surveyed; none elsewhere.</summary>
    public static DepositSize SeabedIn(int x, int y, Biome biome) => biome == Biome.Coast ? Roll(x, y, 0x5eabu, SeabedChance) : DepositSize.None;

    /// <summary>The mineral in the rock of a plot, whether or not it was surveyed.</summary>
    public static Mineral MineralIn(int x, int y, Biome biome)
    {
        if (biome != Biome.Mountains || Roll(x, y, 0x3a17u, MineralChance) is var size && size == DepositSize.None)
        {
            return Mineral.None;
        }

        var kind = (MineralKind)(1 + Hash((uint)x * 0x2c1b3c6du ^ (uint)y * 0x297a2d39u ^ 0x51edu) % 4);
        return new Mineral(kind, size);
    }

    private static DepositSize Roll(int x, int y, uint salt, int chance)
    {
        var h = Hash((uint)x * 0x9e3779b1u ^ (uint)y * 0x85ebca77u ^ salt * 0xc2b2ae3du);
        if (h % 1000 >= chance)
        {
            return DepositSize.None;
        }

        // Of the deposits found, most are small and few are large.
        return ((h >> 10) % 10) switch
        {
            < 6 => DepositSize.Small,
            < 9 => DepositSize.Medium,
            _ => DepositSize.Large,
        };
    }

    private static uint Hash(uint h)
    {
        h ^= h >> 16;
        h *= 0x7feb352du;
        h ^= h >> 15;
        h *= 0x846ca68bu;
        return h ^ (h >> 16);
    }
}

public sealed record SurveyResult(Survey Survey, long BalanceCents);

public sealed class SurveyService(
    GameDb db, WorldMap map, PlotService plots, IWallet wallet, ICharacterProgress progress, TimeProvider time, ITechnologies technologies)
{
    /// <summary>The survey the plot's owner ordered, or null. The mineral is worked out again, as it is fixed by the plot.</summary>
    public async Task<Survey?> FindAsync(int x, int y, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync("SELECT oil, gas FROM world.surveys WHERE x = @x AND y = @y", cancellationToken);
        command.Parameters.AddWithValue("x", x);
        command.Parameters.AddWithValue("y", y);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Survey((DepositSize)reader.GetInt16(0), (DepositSize)reader.GetInt16(1), Deposits.MineralIn(x, y, map.BiomeAt(x, y)),
                Deposits.SeabedIn(x, y, map.BiomeAt(x, y)))
            : null;
    }

    /// <summary>What a survey costs the player: half with <see cref="GeologyTech"/> researched.</summary>
    public async Task<long> PriceAsync(Guid player, CancellationToken cancellationToken) =>
        (await technologies.ResearchedAsync(player, cancellationToken)).Contains(GeologyTech.Id) ? Deposits.SurveyPriceCents / 2 : Deposits.SurveyPriceCents;

    /// <summary>Pays for a geological survey of one of the player's plots; a plot is surveyed once.</summary>
    public Task<SurveyResult> OrderAsync(Guid player, int x, int y, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var biome = await plots.RequireOwnerAsync(player, x, y, cancellationToken, PlotRight.Owner);
            var survey = Deposits.Under(x, y, biome);
            await using (var insert = await db.CommandAsync(
                "INSERT INTO world.surveys (x, y, oil, gas, surveyed_at) VALUES (@x, @y, @oil, @gas, @at) ON CONFLICT DO NOTHING", cancellationToken))
            {
                insert.Parameters.AddWithValue("x", x);
                insert.Parameters.AddWithValue("y", y);
                insert.Parameters.AddWithValue("oil", (short)survey.Oil);
                insert.Parameters.AddWithValue("gas", (short)survey.Gas);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    throw new GameException("already_surveyed", "This plot has been surveyed already.");
                }
            }

            var balance = await wallet.DebitAsync(player, await PriceAsync(player, cancellationToken), $"survey {x}:{y}", cancellationToken);
            await progress.AddXpAsync(player, Deposits.XpPerSurvey, cancellationToken);
            return new SurveyResult(survey, balance);
        }, cancellationToken);
}

public sealed class SurveyHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "world.survey";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<PlotRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SurveyService>().OrderAsync(client.RequireAccount(), request.X, request.Y, cancellationToken);
        return new Reply("world.surveyed", new
        {
            x = request.X,
            y = request.Y,
            survey = SurveyJson(result.Survey),
            balanceCents = result.BalanceCents,
        });
    }

    public static object SurveyJson(Survey survey) => new
    {
        oil = (int)survey.Oil,
        gas = (int)survey.Gas,
        // Oil under the seabed off a coast plot (0-3), for an offshore platform.
        seabed = (int)survey.Seabed,
        // The mineral in the rock (0 none, 1 sulfur, 2 salt, 3 marble, 4 quartz), how much (0-3) and what it comes up as.
        mineral = (int)(survey.Mineral ?? Mineral.None).Kind,
        mineralSize = (int)(survey.Mineral ?? Mineral.None).Size,
        mineralItem = (survey.Mineral ?? Mineral.None).Item,
        mineralName = (survey.Mineral ?? Mineral.None).Item is { } item ? Items.Names[item] : null,
    };
}
