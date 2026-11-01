using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;

namespace FactoryWorld.Modules.World;

/// <summary>A building plan the player saved under a name: copied from a factory or drawn in the sketchbook, to put up on any plot.</summary>
public sealed class SavedPlanRow
{
    public long Id { get; set; }

    public Guid OwnerId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Its objects as JSON: [{ kind, dx, dy, rotation }] from its top left tile.</summary>
    public string Objects { get; set; } = "[]";

    public DateTimeOffset SavedAt { get; set; }
}

/// <param name="Dx">Tiles east of the plan's top left tile.</param>
/// <param name="Dy">Tiles south of it.</param>
public sealed record PlanObject(string? Kind, int Dx, int Dy, int Rotation = 0);

public sealed record SavedPlan(long Id, string Name, IReadOnlyList<PlanObject> Objects, DateTimeOffset SavedAt);

/// <summary>Plans saved by name, kept on the server, so they are there on every device and after the game restarts.</summary>
public static class PlanRules
{
    public const int MaxPlans = 30;

    public const int MaxName = 40;
}

public sealed class PlanService(GameDb db, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SavedPlan>> ListAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT id, name, objects, saved_at FROM world.plans WHERE owner_id = @owner ORDER BY name, id", cancellationToken);
        command.Parameters.AddWithValue("owner", player);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var plans = new List<SavedPlan>();
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(new SavedPlan(reader.GetInt64(0), reader.GetString(1),
                JsonSerializer.Deserialize<List<PlanObject>>(reader.GetString(2), Json) ?? [], reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return plans;
    }

    /// <summary>Saves a plan under a name; one of the same name is replaced.</summary>
    public Task<IReadOnlyList<SavedPlan>> SaveAsync(Guid player, string? name, IReadOnlyList<PlanObject>? objects, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            name = name?.Trim() ?? "";
            if (name.Length is 0 or > PlanRules.MaxName)
            {
                throw new GameException("bad_plan_name", $"A plan's name has 1 to {PlanRules.MaxName} characters.");
            }

            if (objects is null || objects.Count is 0 || objects.Count > PlotObjects.MaxPlan)
            {
                throw new GameException("bad_plan", $"A building plan holds 1 to {PlotObjects.MaxPlan} objects.");
            }

            foreach (var o in objects)
            {
                if (o.Kind is null || !PlotObjects.Placeable.Contains(o.Kind) || o.Dx is < 0 or >= PlotLayout.Size || o.Dy is < 0 or >= PlotLayout.Size
                    || o.Rotation is < 0 or > 3)
                {
                    throw new GameException("bad_plan", "A plan holds objects that stand on a plot, within a plot's size.");
                }
            }

            // One plan saved at a time per player, so two at once cannot both slip under the limit.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(player.ToByteArray()) ^ 0x706c616e_73000000);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            var data = JsonSerializer.Serialize(objects.Select(o => new PlanObject(o.Kind, o.Dx, o.Dy, o.Rotation)), Json);
            await using (var update = await db.CommandAsync(
                "UPDATE world.plans SET objects = @objects, saved_at = @at WHERE owner_id = @owner AND name = @name", cancellationToken))
            {
                update.Parameters.AddWithValue("owner", player);
                update.Parameters.AddWithValue("name", name);
                update.Parameters.AddWithValue("objects", NpgsqlDbType.Jsonb, data);
                update.Parameters.AddWithValue("at", time.GetUtcNow());
                if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
                {
                    return await ListAsync(player, cancellationToken);
                }
            }

            await using (var count = await db.CommandAsync("SELECT count(*) FROM world.plans WHERE owner_id = @owner", cancellationToken))
            {
                count.Parameters.AddWithValue("owner", player);
                if ((long)(await count.ExecuteScalarAsync(cancellationToken))! >= PlanRules.MaxPlans)
                {
                    throw new GameException("too_many_plans", $"Keep at most {PlanRules.MaxPlans} plans; delete one first.");
                }
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO world.plans (owner_id, name, objects, saved_at) VALUES (@owner, @name, @objects, @at)", cancellationToken))
            {
                insert.Parameters.AddWithValue("owner", player);
                insert.Parameters.AddWithValue("name", name);
                insert.Parameters.AddWithValue("objects", NpgsqlDbType.Jsonb, data);
                insert.Parameters.AddWithValue("at", time.GetUtcNow());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ListAsync(player, cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyList<SavedPlan>> DeleteAsync(Guid player, long id, CancellationToken cancellationToken)
    {
        await using (var delete = await db.CommandAsync("DELETE FROM world.plans WHERE id = @id AND owner_id = @owner", cancellationToken))
        {
            delete.Parameters.AddWithValue("id", id);
            delete.Parameters.AddWithValue("owner", player);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new GameException("no_plan", "There is no such plan.");
            }
        }

        return await ListAsync(player, cancellationToken);
    }

    public static object PlansJson(IReadOnlyList<SavedPlan> plans) => new
    {
        plans = plans.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            objects = p.Objects.Select(o => new { kind = o.Kind, dx = o.Dx, dy = o.Dy, rotation = o.Rotation }),
            savedAt = p.SavedAt.ToUnixTimeMilliseconds(),
        }),
        maxPlans = PlanRules.MaxPlans,
        maxObjects = PlotObjects.MaxPlan,
    };
}

public sealed record SavePlanRequest(string? Name, List<PlanObject>? Objects);

public sealed record DeletePlanRequest(long Id);

public sealed class GetPlansHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plans.get";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var plans = await scope.ServiceProvider.GetRequiredService<PlanService>().ListAsync(client.RequireAccount(), cancellationToken);
        return new Reply("plans", PlanService.PlansJson(plans));
    }
}

public sealed class SavePlanHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plans.save";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<SavePlanRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var plans = await scope.ServiceProvider.GetRequiredService<PlanService>()
            .SaveAsync(client.RequireAccount(), request.Name, request.Objects, cancellationToken);
        return new Reply("plans", PlanService.PlansJson(plans));
    }
}

public sealed class DeletePlanHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public string Type => "plans.delete";

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        var request = Payload.Read<DeletePlanRequest>(payload);
        await using var scope = scopes.CreateAsyncScope();
        var plans = await scope.ServiceProvider.GetRequiredService<PlanService>().DeleteAsync(client.RequireAccount(), request.Id, cancellationToken);
        return new Reply("plans", PlanService.PlansJson(plans));
    }
}
