using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Crafting;

public sealed record MachineRequest(Guid Machine);

public sealed record MachineRecipeRequest(Guid Machine, string Recipe);

/// <param name="Fuel">Puts it by the fire even where the recipe wants it too (coal in a foundry making steel).</param>
public sealed record LoadMachineRequest(Guid Machine, string Item, int Quantity, bool Fuel = false);

public sealed record TakeMachineRequest(Guid Machine, string What);

public abstract class MachineHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var owner = client.RequireAccount();
        var reply = await RunAsync(scope.ServiceProvider.GetRequiredService<MachineService>(), owner, payload, cancellationToken);
        var locked = await scope.ServiceProvider.GetRequiredService<ITechnologies>().LockedAsync(owner, cancellationToken);
        var machine = reply.Machine;
        var s = reply.State;
        var recipe = machine.Recipe(s.Recipe);
        return new Reply("machine", new
        {
            id = reply.Id,
            kind = machine.Kind,
            name = Items.Names[machine.Kind],
            recipe = recipe?.Id,
            recipes = machine.Recipes.Select(r => new
            {
                id = r.Id,
                name = Items.Names[r.Id],
                quantity = r.Quantity,
                seconds = r.Time.TotalSeconds,
                ingredients = r.Ingredients.Select(i => new { id = i.Item, name = Items.Names[i.Item], quantity = i.Quantity }),
                extras = r.Extras.Select(e => new { id = e.Item, name = Items.Names[e.Item], quantity = e.Quantity }),
                xp = r.Xp,
                locked = locked.GetValueOrDefault(r.Id),
            }),
            inputs = recipe?.Ingredients.Select((ingredient, i) => new
            {
                id = ingredient.Item,
                name = Items.Names[ingredient.Item],
                quantity = s.Input(i),
                need = ingredient.Quantity,
                // What else goes in for it, e.g. charcoal for coal.
                also = Items.StandIns(ingredient.Item).Select(a => new { id = a, name = Items.Names[a] }),
            }) ?? [],
            output = recipe is null || s.Output == 0 ? null : new { id = recipe.Id, name = Items.Names[recipe.Id], quantity = s.Output },
            // What came with the product, e.g. hide and bones out of a slaughterhouse.
            extras = recipe?.Extras.Select((e, i) => new { id = e.Item, name = Items.Names[e.Item], quantity = s.Extra(i) }).Where(e => e.quantity > 0).ToList() ?? [],
            fired = machine.Fired,
            fuel = s.FuelItem is { } fuel && s.Fuel > 0 ? new { id = fuel, name = Items.Names[fuel], quantity = s.Fuel } : null,
            heat = s.Heat,
            fuels = machine.Fired
                ? Processing.Fuels.Select(f => new { id = f.Key, name = Items.Names[f.Key], batches = f.Value })
                : [],
            batches = Processing.Batches(machine, s),
            // Unix milliseconds of when the batch being made would have started at full power (a machine
            // short of power keeps its time on its own clock), and how long one takes.
            startedAt = s.StartedAt is { } started ? (reply.ServerTime - (reply.MachineTime - started)).ToUnixTimeMilliseconds() : (long?)null,
            powered = machine.Powered,
            // How fast it works on the power it has, 0 to 1.
            speed = reply.Speed,
            // The batch being made: longer while the machine waits for its service.
            seconds = recipe is null ? (double?)null : (s.StartedAt is { } at ? MachineServicing.BatchTime(recipe, s, at) : MachineServicing.BatchTime(recipe, s, reply.MachineTime)).TotalSeconds,
            worn = MachineServicing.Worn(s, s.StartedAt ?? reply.MachineTime),
            serviceDue = s.ServiceDue is { } due ? (reply.ServerTime - (reply.MachineTime - due)).ToUnixTimeMilliseconds() : (long?)null,
            serviceCents = reply.ServiceCents,
            slowerPercent = MachineServicing.SlowerPercent,
            serverTime = reply.ServerTime.ToUnixTimeMilliseconds(),
            maxInput = Processing.MaxInput,
            maxFuel = Processing.MaxFuel,
            maxOutput = Processing.MaxOutput,
            taken = reply.Taken.Select(t => new { id = t.Item, name = Items.Names[t.Item], quantity = t.Quantity }),
            xp = reply.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
            balanceCents = reply.BalanceCents,
        });
    }

    protected abstract Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>What the machine holds now; the client asks again when the next batch is due.</summary>
public sealed class GetMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.get";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        machines.StateAsync(owner, Payload.Read<MachineRequest>(payload).Machine, cancellationToken);
}

public sealed class SetMachineRecipeHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.recipe";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<MachineRecipeRequest>(payload);
        return machines.SetRecipeAsync(owner, request.Machine, request.Recipe ?? "", cancellationToken);
    }
}

public sealed class LoadMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.load";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<LoadMachineRequest>(payload);
        return machines.LoadAsync(owner, request.Machine, request.Item ?? "", request.Quantity, request.Fuel, cancellationToken);
    }
}

/// <summary>Pays for a service of the machine, which then works at full speed again.</summary>
public sealed class ServiceMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.service";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        machines.ServiceAsync(owner, Payload.Read<MachineRequest>(payload).Machine, cancellationToken);
}

public sealed class TakeMachineHandler(IServiceScopeFactory scopes) : MachineHandler(scopes)
{
    public override string Type => "machine.take";

    protected override Task<MachineReply> RunAsync(MachineService machines, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<TakeMachineRequest>(payload);
        return machines.TakeAsync(owner, request.Machine, request.What ?? "", cancellationToken);
    }
}
