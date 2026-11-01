using FactoryWorld.Modules.Abstractions;

namespace FactoryWorld.Modules.World;

/// <summary>Why a train may not cross a border, as the player is told (see <see cref="Abstractions.Contracts.IBorderControl"/>).</summary>
public static class Border
{
    public static GameException Refusal(string code) => new(code, code switch
    {
        "embargo" => "The country takes no goods from there: it has an embargo.",
        "banned_goods" => "The country bans some of these goods; you need a licence or to smuggle them.",
        "travel_banned" => "The police banned your trains from crossing borders for now.",
        "no_money_duty" => "You don't have enough money for the duty.",
        "smuggling_protected" => "Below level 5 the police leave you alone, and you may not smuggle either.",
        _ => "The train may not cross the border.",
    });
}
