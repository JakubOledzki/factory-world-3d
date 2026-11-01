using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Characters;

/// <summary>The player's character: name, look, level and XP. Owns the <c>characters</c> schema.</summary>
public sealed class CharactersModule : IGameModule
{
    public string Name => "Characters";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CharactersDbContext>(CharactersDbContext.Schema);
        services.AddScoped<CharacterService>();
        services.AddScoped<ICharacterProgress, CharacterProgress>();
        services.AddScoped<ICharacterNames, CharacterNames>();
        services.AddScoped<ICharacterLooks, CharacterLooks>();
        services.AddScoped<ICharacterXp, CharacterXp>();
        services.AddScoped<IPlayerRanking, LevelRanking>();
        services.AddScoped<IGameGauges, CharacterGauges>();
        services.AddScoped<Equipment>();
        services.AddScoped<IEquipment>(provider => provider.GetRequiredService<Equipment>());
        services.AddScoped<InventorySize>();
        services.AddScoped<IInventorySize>(provider => provider.GetRequiredService<InventorySize>());
        services.AddScoped<IOutfit, Outfit>();
        services.AddSingleton<IMessageHandler, OptionsHandler>();
        services.AddSingleton<IMessageHandler, GetCharacterHandler>();
        services.AddSingleton<IMessageHandler, CreateCharacterHandler>();
        services.AddSingleton<IMessageHandler, EquipHandler>();
        services.AddSingleton<IMessageHandler, EatHandler>();
        services.AddSingleton<IMessageHandler, UnequipHandler>();
        services.AddSingleton<IMessageHandler, RestyleHandler>();
        services.AddSingleton<IMessageHandler, OnlineHandler>();
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.MigrateModuleAsync<CharactersDbContext>(cancellationToken);
}
