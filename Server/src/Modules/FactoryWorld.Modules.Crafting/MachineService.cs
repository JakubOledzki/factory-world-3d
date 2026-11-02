using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FactoryWorld.Modules.Crafting;

public sealed class MachineService(
    GameDb db, IEnumerable<IPlacedObjects> places, IInventory inventory, ICharacterProgress progress, IProductionStats stats, ITechnologies technologies,
    IPlotPower power, IPlotObjects plots, IPlotRegistry registry, IWallet wallet, IMarketPrices prices, IPlotClock plotClock)
{
    public Task<MachineReply> StateAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock, now) = await OpenAsync(owner, id, cancellationToken);
            return Reply(id, machine, state, clock, now, [], null);
        }, cancellationToken);

    /// <summary>Sets what the machine makes. Ingredients and products of the old recipe go back to the inventory; the fire keeps burning.</summary>
    public Task<MachineReply> SetRecipeAsync(Guid owner, Guid id, string recipeId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock, now) = await OpenAsync(owner, id, cancellationToken);
            var recipe = machine.Recipe(recipeId) ?? throw new GameException("no_recipe", "This machine has no such recipe.");
            await technologies.RequireUnlockedAsync(owner, recipe.Id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            if (state.Recipe != recipe.Id)
            {
                // The products must all fit; the ingredients come back whether or not they do.
                (state, xp) = await TakeOutputAsync(owner, machine, state, taken, partial: false, cancellationToken);
                state = await TakeInputsAsync(owner, machine, state, taken, partial: false, cancellationToken);
                state = Processing.Start(machine, state with { Recipe = recipe.Id, Inputs = new int[recipe.Ingredients.Count] }, clock.At(now));
                await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            }

            return Reply(id, machine, state, clock, now, taken, xp);
        }, cancellationToken);

    /// <summary>
    /// Puts items from the inventory into the machine: fuel by the fire, an ingredient of the recipe in its place. Something
    /// that is both (coal in a foundry making steel) goes in for the recipe, unless <paramref name="fuel"/> asks for the fire.
    /// </summary>
    public Task<MachineReply> LoadAsync(Guid owner, Guid id, string item, int quantity, bool fuel, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var (machine, state, clock, now) = await OpenAsync(owner, id, cancellationToken);
            var recipe = machine.Recipe(state.Recipe);
            var slot = recipe is null || fuel ? -1 : recipe.Ingredients.ToList().FindIndex(i => Items.Fills(i.Item, item));
            if (slot >= 0)
            {
                if (state.Input(slot) + quantity > Processing.MaxInput)
                {
                    throw new GameException("machine_full", $"The machine holds at most {Processing.MaxInput} of each ingredient.");
                }

                var inputs = state.Inputs.ToArray();
                inputs[slot] += quantity;
                state = state with { Inputs = inputs };
            }
            else if (machine.Fired && Processing.Fuels.ContainsKey(item))
            {
                if (state.FuelItem is { } burning && burning != item && state.Fuel > 0)
                {
                    throw new GameException("machine_busy", $"{Items.Names[burning]} lies by the fire; take it out first.");
                }

                if (state.Fuel + quantity > Processing.MaxFuel)
                {
                    throw new GameException("machine_full", $"The machine holds at most {Processing.MaxFuel} fuel.");
                }

                state = state with { FuelItem = item, Fuel = state.Fuel + quantity };
            }
            else
            {
                throw fuel
                    ? new GameException("not_fuel", "This machine does not burn that.")
                    : recipe is null
                    ? new GameException("choose_recipe", "Choose what the machine makes first.")
                    : new GameException("not_processable", "This does not go into the machine.");
            }

            await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            state = Processing.Start(machine, state, clock.At(now));
            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            return Reply(id, machine, state, clock, now, [], null);
        }, cancellationToken);

    /// <summary>Takes the products (<c>output</c>), the ingredients not used yet (<c>input</c>) or the fuel (<c>fuel</c>) out, as
    /// much as fits in the inventory; the rest stays in.</summary>
    public Task<MachineReply> TakeAsync(Guid owner, Guid id, string what, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock, now) = await OpenAsync(owner, id, cancellationToken);
            var taken = new List<(string, int)>();
            XpGain? xp = null;
            switch (what)
            {
                case "output":
                    (state, xp) = await TakeOutputAsync(owner, machine, state, taken, partial: true, cancellationToken);
                    // Room again for what was waiting on a full machine.
                    state = Processing.Start(machine, state, clock.At(now));
                    break;
                case "input":
                    state = await TakeInputsAsync(owner, machine, state, taken, partial: true, cancellationToken);
                    break;
                case "fuel":
                    var back = 0;
                    if (state.FuelItem is { } fuel && state.Fuel > 0)
                    {
                        back = (int)await inventory.FitAsync(owner, fuel, state.Fuel, cancellationToken);
                        await inventory.AddAsync(owner, fuel, back, cancellationToken);
                        taken.Add((fuel, back));
                    }

                    if (back < state.Fuel)
                    {
                        // Fuel left by the fire keeps the batches going.
                        state = state with { Fuel = state.Fuel - back };
                        break;
                    }

                    // The batch in the fire keeps going on the fuel already lit, if there is any.
                    state = state with { FuelItem = null, Fuel = 0 };
                    if (state.Heat == 0)
                    {
                        state = state with { StartedAt = null };
                    }

                    break;
                default:
                    throw new GameException("bad_payload", "Take out output, input or fuel.");
            }

            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            return Reply(id, machine, state, clock, now, taken, xp);
        }, cancellationToken);

    /// <summary>Gives the player <paramref name="quantity"/> pieces; returns how many. With <paramref name="partial"/> only as many
    /// as fit, else all of them or, when they do not fit, none with <c>inventory_full</c>.</summary>
    private async Task<int> GiveAsync(Guid owner, string item, int quantity, bool partial, CancellationToken cancellationToken)
    {
        if (!partial)
        {
            await inventory.ReceiveAsync(owner, item, quantity, cancellationToken);
            return quantity;
        }

        var fits = (int)await inventory.RoomForAsync(owner, item, quantity, cancellationToken);
        if (fits > 0)
        {
            await inventory.AddAsync(owner, item, fits, cancellationToken);
        }

        return fits;
    }

    /// <summary>Hands the products (and what the recipe gives besides) to the player: as many as fit (<paramref name="partial"/>),
    /// the rest staying in, or all of them, failing with <c>inventory_full</c> when they do not fit (before a new recipe).</summary>
    private async Task<(MachineState, XpGain?)> TakeOutputAsync(
        Guid owner, ProcessingMachine machine, MachineState state, List<(string, int)> taken, bool partial, CancellationToken cancellationToken)
    {
        if (machine.Recipe(state.Recipe) is not { } recipe || !state.HasOutput)
        {
            return (state, null);
        }

        var given = 0;
        var extras = new int[recipe.Extras.Count];
        for (var i = 0; i < extras.Length; i++)
        {
            extras[i] = state.Extra(i);
            if (extras[i] > 0 && await GiveAsync(owner, recipe.Extras[i].Item, extras[i], partial, cancellationToken) is var some and > 0)
            {
                taken.Add((recipe.Extras[i].Item, some));
                extras[i] -= some;
                given += some;
            }
        }

        state = state with { Extras = extras };
        XpGain? xp = null;
        if (state.Output > 0)
        {
            int made;
            // What tourists paid goes into the wallet.
            if (Items.IsMoney(recipe.Id))
            {
                made = state.Output;
                await wallet.CreditAsync(owner, made * Processing.TakingsCents, $"takings {machine.Kind}", cancellationToken);
            }
            else
            {
                made = await GiveAsync(owner, recipe.Id, state.Output, partial, cancellationToken);
            }

            if (made > 0)
            {
                taken.Add((recipe.Id, made));
                xp = await progress.AddXpAsync(owner, (long)made * recipe.Xp, cancellationToken);
                state = state with { Output = state.Output - made };
                given += made;
            }
        }

        if (given == 0)
        {
            throw InventoryExtensions.InventoryFull();
        }

        return (state, xp);
    }

    /// <summary>Gives back the ingredients not used yet: as many as fit (<paramref name="partial"/>), the rest staying in, or all
    /// of them, fitting or not (a new recipe). The batch going on stops when what is left makes no batch.</summary>
    private async Task<MachineState> TakeInputsAsync(
        Guid owner, ProcessingMachine machine, MachineState state, List<(string, int)> taken, bool partial, CancellationToken cancellationToken)
    {
        if (machine.Recipe(state.Recipe) is not { } recipe)
        {
            return state;
        }

        var inputs = new int[recipe.Ingredients.Count];
        var given = 0;
        for (var i = 0; i < inputs.Length; i++)
        {
            inputs[i] = state.Input(i);
            if (inputs[i] == 0)
            {
                continue;
            }

            var item = recipe.Ingredients[i].Item;
            var back = partial ? (int)await inventory.RoomForAsync(owner, item, inputs[i], cancellationToken) : inputs[i];
            if (back > 0)
            {
                await inventory.AddAsync(owner, item, back, cancellationToken);
                taken.Add((item, back));
                inputs[i] -= back;
                given += back;
            }
        }

        if (given == 0 && inputs.Any(q => q > 0))
        {
            throw InventoryExtensions.InventoryFull();
        }

        state = state with { Inputs = inputs };
        return Processing.Batches(machine, state) > 0 ? state : state with { StartedAt = null };
    }

    /// <summary>Services the machine for <see cref="MachineServicing.Cents"/>: it works at full speed again for <see cref="MachineServicing.Every"/>
    /// of its own time from now. What it made until now was made at the speed it had.</summary>
    public Task<MachineReply> ServiceAsync(Guid owner, Guid id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (machine, state, clock, now) = await OpenAsync(owner, id, cancellationToken);
            var cents = MachineServicing.Cents(machine.Kind, prices);
            var balance = await wallet.DebitAsync(owner, cents, $"service {machine.Kind} {id}", cancellationToken);
            state = state with { ServiceDue = clock.At(now) + MachineServicing.Every };
            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            return Reply(id, machine, state, clock, now, [], null) with { BalanceCents = balance };
        }, cancellationToken);

    private MachineReply Reply(Guid id, ProcessingMachine machine, MachineState state, IPowerClock clock, DateTimeOffset now, IReadOnlyList<(string, int)> taken, XpGain? xp)
    {
        return new MachineReply(id, machine, state, now, taken, xp, clock.At(now), clock.Speed(now), MachineServicing.Cents(machine.Kind, prices));
    }

    /// <summary>The player's machine, locked until the transaction ends and worked up to now (its plot's time, which comes
    /// with it), with the clock it works by: its own time on the power grid if it runs on power.</summary>
    private async Task<(ProcessingMachine, MachineState, IPowerClock, DateTimeOffset)> OpenAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        var now = await plotClock.NowOfAsync(id, cancellationToken);
        ProcessingMachine? machine = null;
        foreach (var place in places)
        {
            if (await place.KindAsync(owner, id, cancellationToken) is { } kind)
            {
                machine = await Processing.OnPlotAsync(kind, db, plots, registry, id, now, cancellationToken);
                break;
            }
        }

        if (machine is null)
        {
            throw new GameException("no_object", "There is no machine of yours here.");
        }

        // The grid is locked before the machine, as when inserters work it.
        var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
        var stored = await MachineRows.ReadOrNewAsync(db, owner, id, machine.Kind, cancellationToken);
        var state = Processing.Advance(machine, stored, clock.At(now));
        var made = MachineProduction.Processed(machine, stored, state, clock, now);
        if (made.Count > 0)
        {
            await stats.RecordAsync(owner, made, cancellationToken);
            await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
        }

        return (machine, state, clock, now);
    }
}

/// <summary>Processing machines as inserters see them: ingredients of the chosen recipe and fuel go in, products come out.</summary>
public sealed class ProcessingPorts(GameDb db, IProductionStats stats, IPlotPower power, IPlotObjects plots, IPlotRegistry registry, IPlotClock plotClock) : IMachinePorts
{
    public async Task<IMachinePort?> OpenAsync(Guid owner, Guid id, string kind, CancellationToken cancellationToken)
    {
        if (!Processing.ByKind.ContainsKey(kind) || (await Processing.OnPlotAsync(kind, db, plots, registry, id, await plotClock.NowOfAsync(id, cancellationToken), cancellationToken)) is not { } machine)
        {
            return null;
        }

        var clock = await Processing.ClockAsync(machine, power, id, cancellationToken);
        var state = await MachineRows.ReadOrNewAsync(db, owner, id, machine.Kind, cancellationToken);
        return new Port(db, stats, owner, id, machine, state, clock);
    }

    public Task PrepareAsync(IReadOnlyList<PlotObject> objects, CancellationToken cancellationToken) =>
        MachineRows.ReadAheadAsync(db, objects.Where(o => Processing.ByKind.ContainsKey(o.Kind)).Select(o => o.Id).Distinct().ToList(), cancellationToken);

    public async Task SaveAsync(IReadOnlyList<IMachinePort> ports, CancellationToken cancellationToken)
    {
        var changed = ports.OfType<Port>().Where(p => p.Changed).ToList();
        foreach (var owner in changed.GroupBy(p => p.Owner))
        {
            await stats.RecordAsync(owner.Key, owner.SelectMany(p => p.Made).ToList(), cancellationToken);
        }

        await MachineRows.WriteAsync(db, changed.Select(p => (p.Owner, p.Id, p.Kind, p.State)).ToList(), cancellationToken);
    }

    /// <summary>Every call is given the time on the plot; the machine works in its own (<paramref name="clock"/>).</summary>
    private sealed class Port(GameDb db, IProductionStats stats, Guid owner, Guid id, ProcessingMachine machine, MachineState state, IPowerClock clock) : IMachinePort
    {
        private readonly List<ProductionEntry> _made = [];
        private bool _changed;

        public Guid Owner => owner;
        public Guid Id => id;
        public string Kind => machine.Kind;
        public MachineState State => state;
        public bool Changed => _changed;
        public IReadOnlyList<ProductionEntry> Made => _made;

        private ProcessingRecipe? Recipe => machine.Recipe(state.Recipe);

        public string? Output(DateTimeOffset now, Func<string, bool> wanted)
        {
            Advance(now);
            if (Recipe is not { } recipe)
            {
                return null;
            }

            // Money is no good on a belt: tourists' takings are taken out by hand.
            if (state.Output > 0 && !Items.IsMoney(recipe.Id) && wanted(recipe.Id))
            {
                return recipe.Id;
            }

            for (var i = 0; i < recipe.Extras.Count; i++)
            {
                if (state.Extra(i) > 0 && wanted(recipe.Extras[i].Item))
                {
                    return recipe.Extras[i].Item;
                }
            }

            return null;
        }

        public void Take(string item, DateTimeOffset now)
        {
            Advance(now);
            if (Recipe?.Id == item && state.Output > 0)
            {
                // Room again for what was waiting on a full machine.
                state = Processing.Start(machine, state with { Output = state.Output - 1 }, clock.At(now));
                _changed = true;
            }
            else if (Recipe is { } recipe && recipe.Extras.ToList().FindIndex(e => e.Item == item) is var extra and >= 0 && state.Extra(extra) > 0)
            {
                var extras = new int[recipe.Extras.Count];
                for (var i = 0; i < extras.Length; i++)
                {
                    extras[i] = state.Extra(i);
                }

                extras[extra]--;
                state = Processing.Start(machine, state with { Extras = extras }, clock.At(now));
                _changed = true;
            }
        }

        public bool Accepts(string item, DateTimeOffset now)
        {
            Advance(now);
            var slot = Slot(item);
            return (slot >= 0 && state.Input(slot) < Processing.MaxInput) || Processing.FireTakes(machine, state, item);
        }

        public void Put(string item, DateTimeOffset now)
        {
            // Coal for steel goes by the fire too, enough of it to burn all the coal in the recipe's place.
            if (Slot(item) is var slot and >= 0 && !Processing.ToFire(machine, state, item, slot))
            {
                var inputs = new int[Recipe!.Ingredients.Count];
                for (var i = 0; i < inputs.Length; i++)
                {
                    inputs[i] = state.Input(i);
                }

                inputs[slot]++;
                state = state with { Inputs = inputs };
            }
            else
            {
                state = state with { FuelItem = item, Fuel = state.Fuel + 1 };
            }

            state = Processing.Start(machine, state, clock.At(now));
            _changed = true;
        }

        public DateTimeOffset? NextChange(DateTimeOffset now)
        {
            Advance(now);
            return state.StartedAt is { } started && Recipe is { } recipe ? clock.When(started + MachineServicing.BatchTime(recipe, state, started), now) : null;
        }

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (_changed)
            {
                await stats.RecordAsync(owner, _made, cancellationToken);
                await MachineRows.WriteAsync(db, owner, id, machine.Kind, state, cancellationToken);
            }
        }

        private void Advance(DateTimeOffset now)
        {
            var before = state;
            state = Processing.Advance(machine, state, clock.At(now));
            var made = MachineProduction.Processed(machine, before, state, clock, now);
            if (made.Count > 0)
            {
                _made.AddRange(made);
                _changed = true;
            }
        }

        private int Slot(string item) => Recipe is { } recipe ? recipe.Ingredients.ToList().FindIndex(i => Items.Fills(i.Item, item)) : -1;
    }
}

/// <summary>The clock of machines that need no power.</summary>
internal sealed class PlainTime : IPowerClock
{
    public static readonly PlainTime Instance = new();

    public DateTimeOffset At(DateTimeOffset now) => now;

    public DateTimeOffset? When(DateTimeOffset machineTime, DateTimeOffset now) => machineTime > now ? machineTime : now;

    public double Speed(DateTimeOffset now) => 1;
}
