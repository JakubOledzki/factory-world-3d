using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Tutorial;

/// <summary>A request the server answered without an error, as the tutorial sees it.</summary>
public sealed record Seen(string RequestType, JsonElement? Request, string ReplyType, JsonElement Reply)
{
    public JsonElement? Field(string name) =>
        Reply.ValueKind == JsonValueKind.Object && Reply.TryGetProperty(name, out var value) ? value : null;

    public string? RequestString(string name) =>
        Request is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The elements of an array in the reply, or none.</summary>
    public IEnumerable<JsonElement> Each(string name) =>
        Field(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}

/// <summary>One task of the tutorial. <see cref="Progress"/> says how far a request brought the player
/// (0 when it has nothing to do with the task); the task is done once that adds up to <see cref="Goal"/>.</summary>
/// <param name="Id">Stored in the database, so never rename one.</param>
/// <param name="Watch">Reply types <see cref="Progress"/> looks at; the client asks for the tutorial again after them.</param>
/// <param name="RewardItems">Items given with the reward, e.g. what the next research costs.</param>
public sealed record TutorialStep(
    string Id,
    string Title,
    string Text,
    string Hint,
    int Goal,
    long RewardCents,
    int RewardXp,
    IReadOnlyList<string> Watch,
    Func<Seen, long> Progress,
    IReadOnlyList<(string Item, int Quantity)>? RewardItems = null);

/// <summary>
/// The first steps of a new player, one task after another: from sorting trash in the garage, through
/// the first plot, the first trees and the first research, to the first automatic line on the plot.
/// Each task pays a reward when it is done; the ones before research also give what it costs, since a
/// new player has no iron or copper yet. The tasks follow what the player does by watching the server's replies, so no other
/// module has to know about the tutorial.
/// </summary>
public static class TutorialRules
{
    public static readonly IReadOnlyList<TutorialStep> Steps =
    [
        new("sort_trash", "Segregacja",
            "Posegreguj 5 worków śmieci przy koszu w garażu.",
            "Podejdź do kosza i naciśnij E (na telefonie: Użyj). Postać segreguje, dopóki stoi przy koszu.",
            5, 50_00, 20, ["garage"],
            seen => seen.ReplyType == "garage" ? seen.Each("results").Count() : 0),
        new("sell_waste", "Pierwszy zarobek",
            "Sprzedaj odpady z segregacji handlarzowi.",
            "Otwórz Rynek [M], wybierz np. makulaturę i kliknij Sprzedaj.",
            1, 100_00, 10, ["market.traded"],
            seen => seen is { RequestType: "market.sell", ReplyType: "market.traded" } ? 1 : 0),
        new("buy_plot", "Własna ziemia",
            "Kup pierwszą działkę na mapie świata. Najlepiej las: dostaniesz siekierę.",
            "Działka kosztuje 1 000 $. Brakujące pieniądze zarobisz na segregacji śmieci i sprzedaży odpadów.",
            1, 200_00, 0, ["world.bought", "world.info"],
            // world.info lists the player's plots, so a player who bought one before the tutorial moves on too.
            seen => seen.ReplyType == "world.bought" || (seen.ReplyType == "world.info" && seen.Each("plots").Any()) ? 1 : 0),
        new("chop_trees", "Drwal",
            "Zetnij 20 drzew na swojej działce.",
            "Weź siekierę do ręki i kliknij drzewo. Drewno przyda się do badań i magazynu, a sadzonki, które czasem przy tym wypadają, za chwilę.",
            20, 50_00, 20, ["plot.harvested"],
            seen => seen.ReplyType == "plot.harvested" ? Quantity(seen.Each("items"), Items.Wood) : 0,
            // Saplings drop only now and then, so the next task's one comes with the reward.
            [(Items.Sapling, 1)]),
        new("plant_tree", "Leśnik",
            "Posadź sadzonkę drzewa na wolnym polu.",
            "Wybierz sadzonkę w ekwipunku [I] i kliknij wolne pole. Sadzonki kupisz też u handlarza za 2 $.",
            1, 20_00, 10, ["plot.tree"],
            seen => seen.ReplyType == "plot.tree" ? 1 : 0,
            [(Items.IronIngot, 10)]),
        new("research_logistics", "Badania",
            "Zacznij badać Logistykę przy stole badawczym.",
            "Stół badawczy stoi w garażu przy tylnej ścianie. Logistyka kosztuje 10 sztabek żelaza (masz je w nagrodę) i 20 drewna, trwa minutę i odblokowuje taśmociągi i stos drewna.",
            1, 30_00, 10, ["research"],
            seen => Researched(seen, "logistics"),
            [(Items.IronIngot, 20), (Items.CopperIngot, 10), (Items.Nails, 20)]),
        new("craft_wood_pile", "Warsztat",
            "Zamów stos drewna przy stole warsztatowym.",
            "Najpierw musi się skończyć badanie Logistyki. Stos drewna kosztuje 4 drewna i powstaje w 5 sekund.",
            1, 30_00, 10, ["crafting"],
            seen => seen.ReplyType == "crafting" && seen.RequestType == "crafting.craft" && seen.RequestString("recipe") == Items.WoodPile ? 1 : 0),
        new("place_wood_pile", "Magazyn",
            "Postaw stos drewna na swojej działce.",
            "Wybierz stos w ekwipunku [I] i kliknij wolne pole; klawisz R obraca.",
            1, 20_00, 10, ["plot.placed"],
            seen => Placed(seen, Items.WoodPile)),
        new("fill_wood_pile", "Zapas",
            "Włóż drewno do stosu drewna.",
            "Kliknij stos na działce, a potem drewno w swoim ekwipunku.",
            1, 20_00, 10, ["storage"],
            seen => seen is { RequestType: "storage.put", ReplyType: "storage" } && seen.Field("kind")?.GetString() == Items.WoodPile ? 1 : 0),
        new("place_belts", "Taśmociąg",
            "Postaw 3 taśmociągi w rzędzie na działce.",
            "Taśmociągi kupisz u handlarza po 10 $. Taśma wiezie przedmioty w stronę, w którą jest obrócona (R).",
            3, 30_00, 15, ["plot.placed"],
            seen => Placed(seen, Items.Belt)),
        new("research_automation", "Automatyzacja",
            "Zacznij badać Automatyzację przy stole badawczym.",
            "Kosztuje 20 sztabek żelaza, 10 sztabek miedzi i 20 gwoździ (dostałeś je za badanie Logistyki), trwa 2 minuty i odblokowuje podajnik.",
            1, 30_00, 20, ["research"],
            seen => Researched(seen, "automation")),
        new("place_inserter", "Podajnik",
            "Postaw podajnik między stosem drewna a początkiem taśmy.",
            "Po badaniu podajnik kupisz u handlarza za 90 $. Bierze z pola za sobą i kładzie na pole przed sobą, więc obróć go (R) w stronę taśmy.",
            1, 50_00, 15, ["plot.placed"],
            seen => Placed(seen, Items.Inserter),
            [(Items.Generator, 1), (Items.Pole, 2)]),
        new("power_line", "Prąd",
            "Daj podajnikowi prąd: postaw generator parowy z drewnem i słup energetyczny obok podajnika.",
            "Generator i słupy dostałeś w nagrodę. Kliknij generator i dołóż drewna; słup sięga 2 pola w każdą stronę, więc postaw go tak, by sięgał i podajnika, i generatora.",
            1, 50_00, 20, ["generator", "logistics"],
            seen => Powered(seen)),
        new("first_line", "Pierwsza automatyczna linia",
            "Niech podajnik położy drewno na taśmę i taśma je powiezie.",
            "Jeśli nic się nie rusza, sprawdź, czy podajnik stoi tyłem do stosu, a przodem do taśmy, czy w stosie jest drewno i czy generator ma opał.",
            1, 500_00, 100, ["logistics"],
            // Only machinery puts items on belts, so anything riding one is the line at work.
            seen => seen.ReplyType == "logistics"
                && seen.Each("belts").Any(b => b.GetProperty("items").EnumerateArray().Any(i => i.ValueKind == JsonValueKind.String)) ? 1 : 0),
    ];

    public static int IndexOf(string? id)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static long Quantity(IEnumerable<JsonElement> items, string item) =>
        items.Where(i => i.GetProperty("id").GetString() == item).Sum(i => i.GetProperty("quantity").GetInt64());

    private static long Researched(Seen seen, string technology) =>
        seen is { RequestType: "research.start", ReplyType: "research" } && seen.RequestString("technology") == technology ? 1 : 0;

    /// <summary>A generator burning for machines on its network: seen in its own window or in the plot's logistics.</summary>
    private static long Powered(Seen seen) => seen.ReplyType switch
    {
        "generator" => seen.Field("network") is { ValueKind: JsonValueKind.Object } network
            && network.GetProperty("machines").GetInt32() > 0 && seen.Field("outputKw")?.GetDouble() > 0 ? 1 : 0,
        "logistics" => seen.Field("power") is { ValueKind: JsonValueKind.Object } power && power.TryGetProperty("networks", out var networks)
            && networks.EnumerateArray().Any(n => n.GetProperty("consumers").GetArrayLength() > 0 && n.GetProperty("speed").GetDouble() > 0) ? 1 : 0,
        _ => 0,
    };

    private static long Placed(Seen seen, string kind) =>
        seen.ReplyType == "plot.placed" && seen.Field("object")?.GetProperty("kind").GetString() == kind ? 1 : 0;
}

/// <summary>Where a player is in the tutorial: <see cref="Step"/> is the id of the task at hand,
/// null once every task is done.</summary>
public sealed class TutorialProgress
{
    public Guid OwnerId { get; set; }

    public string? Step { get; set; }

    public long Count { get; set; }

    public bool Skipped { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TutorialDbContext(DbContextOptions<TutorialDbContext> options, GameDb gameDb) : ModuleDbContext(options, gameDb)
{
    public const string Schema = "tutorial";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<TutorialProgress>(e =>
        {
            e.ToTable("progress");
            e.HasKey(p => p.OwnerId);
            e.Property(p => p.OwnerId).HasColumnName("owner_id");
            e.Property(p => p.Step).HasColumnName("step").HasMaxLength(40);
            e.Property(p => p.Count).HasColumnName("count");
            e.Property(p => p.Skipped).HasColumnName("skipped");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        });
    }
}

/// <summary>The player's place in the tutorial. <see cref="Index"/> is the task at hand, or the number
/// of tasks once all are done.</summary>
public sealed record TutorialState(int Index, long Count, bool Skipped)
{
    public bool Finished => Index >= TutorialRules.Steps.Count;

    public TutorialStep? Current => Finished ? null : TutorialRules.Steps[Index];
}

public sealed class TutorialService(GameDb db, IWallet wallet, IInventory inventory, ICharacterProgress progress, TimeProvider time)
{
    public async Task<TutorialState> StateAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT step, count, skipped FROM tutorial.progress WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new TutorialState(0, 0, false);
        }

        // A task taken out of the game since counts as the end of the tutorial.
        var index = reader.IsDBNull(0) ? TutorialRules.Steps.Count : TutorialRules.IndexOf(reader.GetString(0));
        return new TutorialState(index < 0 ? TutorialRules.Steps.Count : index, reader.GetInt64(1), reader.GetBoolean(2));
    }

    /// <summary>Moves the task <paramref name="stepId"/> forward by <paramref name="amount"/>; when it is
    /// done, pays its reward and goes on to the next one. Does nothing if the player is at another task.</summary>
    public Task<TutorialState> AdvanceAsync(Guid owner, string stepId, long amount, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await using (var row = await db.CommandAsync(
                """
                INSERT INTO tutorial.progress (owner_id, step, count, skipped, updated_at)
                VALUES (@owner, @first, 0, false, @now)
                ON CONFLICT (owner_id) DO NOTHING
                """, cancellationToken))
            {
                row.Parameters.AddWithValue("owner", owner);
                row.Parameters.AddWithValue("first", TutorialRules.Steps[0].Id);
                row.Parameters.AddWithValue("now", time.GetUtcNow());
                await row.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var lockRow = await db.CommandAsync(
                "SELECT 1 FROM tutorial.progress WHERE owner_id = @owner FOR UPDATE", cancellationToken))
            {
                lockRow.Parameters.AddWithValue("owner", owner);
                await lockRow.ExecuteNonQueryAsync(cancellationToken);
            }

            var state = await StateAsync(owner, cancellationToken);
            if (state.Current is not { } step || step.Id != stepId)
            {
                return state;
            }

            var count = state.Count + amount;
            var index = state.Index;
            if (count >= step.Goal)
            {
                if (step.RewardCents > 0)
                {
                    await wallet.CreditAsync(owner, step.RewardCents, $"tutorial {step.Id}", cancellationToken);
                }

                if (step.RewardXp > 0)
                {
                    await progress.AddXpAsync(owner, step.RewardXp, cancellationToken);
                }

                foreach (var (item, quantity) in step.RewardItems ?? [])
                {
                    await inventory.AddAsync(owner, item, quantity, cancellationToken);
                }

                index++;
                count = 0;
            }

            await using (var update = await db.CommandAsync(
                "UPDATE tutorial.progress SET step = @step, count = @count, updated_at = @now WHERE owner_id = @owner", cancellationToken))
            {
                update.Parameters.AddWithValue("owner", owner);
                update.Parameters.AddWithValue("step", index < TutorialRules.Steps.Count ? TutorialRules.Steps[index].Id : DBNull.Value);
                update.Parameters.AddWithValue("count", count);
                update.Parameters.AddWithValue("now", time.GetUtcNow());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            return state with { Index = index, Count = count };
        }, cancellationToken);

    /// <summary>Hides the tutorial (<paramref name="skipped"/> true) or brings it back where it was.</summary>
    public async Task<TutorialState> SkipAsync(Guid owner, bool skipped, CancellationToken cancellationToken)
    {
        await using (var command = await db.CommandAsync(
            """
            INSERT INTO tutorial.progress (owner_id, step, count, skipped, updated_at)
            VALUES (@owner, @first, 0, @skipped, @now)
            ON CONFLICT (owner_id) DO UPDATE SET skipped = excluded.skipped, updated_at = excluded.updated_at
            """, cancellationToken))
        {
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("first", TutorialRules.Steps[0].Id);
            command.Parameters.AddWithValue("skipped", skipped);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await StateAsync(owner, cancellationToken);
    }
}

/// <summary>
/// Follows the player's replies and moves the tutorial on. The task at hand is remembered on the
/// connection, so a reply that cannot matter to it costs no database query.
/// </summary>
public sealed class TutorialObserver(IServiceScopeFactory scopes) : IMessageObserver
{
    private const string Key = "tutorial.state";

    public async Task ObserveAsync(Envelope request, Envelope reply, ClientContext client, CancellationToken cancellationToken)
    {
        if (client.AccountId is not { } owner || reply.Payload is not { } payload || request.Type.StartsWith("tutorial.", StringComparison.Ordinal))
        {
            return;
        }

        // A player who skipped the tutorial earns nothing from it until they bring it back.
        if (client.Items.GetValueOrDefault(Key) is TutorialState { Finished: true } or TutorialState { Skipped: true })
        {
            return;
        }

        if (client.Items.GetValueOrDefault(Key) is TutorialState { Current: { } known } && !known.Watch.Contains(reply.Type))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var tutorial = scope.ServiceProvider.GetRequiredService<TutorialService>();
        var state = await tutorial.StateAsync(owner, cancellationToken);
        if (!state.Skipped && state.Current is { } step && step.Watch.Contains(reply.Type)
            && step.Progress(new Seen(request.Type, request.Payload, reply.Type, payload)) is > 0 and var amount)
        {
            state = await tutorial.AdvanceAsync(owner, step.Id, amount, cancellationToken);
        }

        client.Items[Key] = state;
    }

    /// <summary>Remembers the state a tutorial request answered with, e.g. after the player skipped the tutorial.</summary>
    public static void Remember(ClientContext client, TutorialState state) => client.Items[Key] = state;
}

public sealed record SkipRequest(bool Skipped = true);

public abstract class TutorialHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<TutorialService>(), client.RequireAccount(), payload, cancellationToken);
        TutorialObserver.Remember(client, state);
        return new Reply("tutorial", Json(state));
    }

    protected abstract Task<TutorialState> RunAsync(TutorialService tutorial, Guid owner, JsonElement? payload, CancellationToken cancellationToken);

    public static object Json(TutorialState state) => new
    {
        steps = TutorialRules.Steps.Select(s => new
        {
            id = s.Id,
            title = s.Title,
            text = s.Text,
            hint = s.Hint,
            goal = s.Goal,
            rewardCents = s.RewardCents,
            rewardXp = s.RewardXp,
            rewardItems = (s.RewardItems ?? []).Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
        }),
        current = state.Index,
        count = state.Count,
        skipped = state.Skipped,
        watch = state.Current?.Watch ?? [],
    };
}

public sealed class GetTutorialHandler(IServiceScopeFactory scopes) : TutorialHandler(scopes)
{
    public override string Type => "tutorial.get";

    protected override Task<TutorialState> RunAsync(TutorialService tutorial, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        tutorial.StateAsync(owner, cancellationToken);
}

public sealed class SkipTutorialHandler(IServiceScopeFactory scopes) : TutorialHandler(scopes)
{
    public override string Type => "tutorial.skip";

    protected override Task<TutorialState> RunAsync(TutorialService tutorial, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        tutorial.SkipAsync(owner, payload is null ? true : Payload.Read<SkipRequest>(payload).Skipped, cancellationToken);
}

/// <summary>The tutorial for new players. Owns the <c>tutorial</c> schema.</summary>
public sealed class TutorialModule : IGameModule
{
    public string Name => "Tutorial";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TutorialDbContext>(TutorialDbContext.Schema);
        services.AddScoped<TutorialService>();
        services.AddSingleton<IMessageObserver, TutorialObserver>();
        services.AddSingleton<IMessageHandler, GetTutorialHandler>();
        services.AddSingleton<IMessageHandler, SkipTutorialHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<TutorialDbContext>(cancellationToken);
}
