using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using FactoryWorld.Modules.World;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Server.Tests;

public sealed class SmogTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public void Smoke_up_to_the_harmless_level_does_nothing_and_more_slows_crops_down_to_half()
    {
        Assert.Equal(1, Smog.CropSpeed(Smog.Harmless));
        Assert.Equal(0.75, Smog.CropSpeed(Smog.Harmless + Smog.HalfSpeed / 2.0), 6);
        Assert.Equal(Smog.SlowestCrops, Smog.CropSpeed(1000));

        Assert.Equal(40, Smog.Given([(Items.CoalPlant, 2)]));
        Assert.Equal(20, Smog.Given([(Items.CoalPlant, 2), (Items.Scrubber, 1)]));
        Assert.Equal(0, Smog.Given([(Items.Furnace, 1), (Items.Scrubber, 3), (Items.Belt, 50)]));
    }

    [Fact]
    public async Task A_plot_breathes_its_own_smoke_and_half_of_what_the_plots_around_it_give_off()
    {
        // Ground nobody else's test builds on.
        var (x, y) = (6100, 2900);
        await PlaceAsync(x, y, Items.CoalPlant, 3);
        await PlaceAsync(x, y, Items.Scrubber, 1);
        await PlaceAsync(x + 1, y + 1, Items.BlastFurnace, 5);
        await PlaceAsync(x + 2, y, Items.CoalPlant, 5);

        await using var scope = factory.Services.CreateAsyncScope();
        var smog = await scope.ServiceProvider.GetRequiredService<SmogService>().LevelAsync(x, y, CancellationToken.None);
        Assert.Equal(3 * 20 - Smog.ScrubberCleans, smog.Own);
        // The plot two away is too far.
        Assert.Equal(5 * 8 * Smog.NeighbourShare, smog.Neighbours, 6);
        Assert.Equal(Smog.CropSpeed(60), smog.CropSpeed, 6);
    }

    private int _tile;

    private async Task PlaceAsync(int x, int y, string kind, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDb>();
        for (var i = 0; i < count; i++)
        {
            await using var insert = await db.CommandAsync(
                "INSERT INTO world.plot_objects (id, x, y, tile_x, tile_y, owner_id, kind, rotation) VALUES (@id, @x, @y, @t, @t, @owner, @kind, 0)",
                CancellationToken.None);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("x", x);
            insert.Parameters.AddWithValue("y", y);
            insert.Parameters.AddWithValue("t", 10 + _tile++ * 4);
            insert.Parameters.AddWithValue("owner", Guid.NewGuid());
            insert.Parameters.AddWithValue("kind", kind);
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
