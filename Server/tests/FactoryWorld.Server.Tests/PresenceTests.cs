using FactoryWorld.Modules.World;
using Microsoft.Extensions.Time.Testing;

namespace FactoryWorld.Server.Tests;

public sealed class PresenceTests
{
    private static int Count(PlotPresence presence, Guid viewer, int x, int y) => presence.Others(viewer, x, y).Count;

    [Fact]
    public void A_player_is_seen_only_on_the_plot_they_last_said_and_not_by_themselves()
    {
        var presence = new PlotPresence(new FakeTimeProvider());
        var (a, b, viewer) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        presence.Update(a, 10, 10, 5, 5, "down", false);
        presence.Update(b, 10, 10, 6, 6, "up", true);
        Assert.Equal(2, Count(presence, viewer, 10, 10));
        Assert.Equal(1, Count(presence, a, 10, 10));

        presence.Update(a, 11, 10, 5, 5, "down", false);
        Assert.Equal(1, Count(presence, viewer, 10, 10));
        Assert.Equal(1, Count(presence, viewer, 11, 10));

        presence.Leave(b);
        Assert.Equal(0, Count(presence, viewer, 10, 10));
        Assert.True(presence.Knows(a, 11, 10));
        Assert.False(presence.Knows(b, 10, 10));
    }

    [Fact]
    public void A_player_who_stops_saying_where_they_are_is_gone_after_the_timeout_even_from_plots_nobody_looks_at()
    {
        var time = new FakeTimeProvider();
        var presence = new PlotPresence(time);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        presence.Update(a, 1, 1, 0, 0, "down", false);
        presence.Update(b, 2, 2, 0, 0, "down", false);
        time.Advance(PlotPresence.Timeout + TimeSpan.FromSeconds(1));
        presence.Update(b, 2, 2, 1, 1, "down", false);

        Assert.Equal(0, Count(presence, Guid.NewGuid(), 1, 1));
        Assert.Equal(1, Count(presence, Guid.NewGuid(), 2, 2));

        // Asking about one plot also forgets the quiet players of every other one, now and then.
        presence.Update(a, 3, 3, 0, 0, "down", false);
        time.Advance(PlotPresence.Timeout + TimeSpan.FromSeconds(1));
        Count(presence, Guid.NewGuid(), 9, 9);
        Assert.False(presence.Knows(a, 3, 3));
    }
}
