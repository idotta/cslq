namespace Cslq.Tests;

/// <summary>
/// The stamp <c>LspClient.WaitReadyAsync</c> measures <c>PostLoadGrace</c> from. The arithmetic
/// above it needs a live server and a load long enough to matter, so only the contract this
/// stamp introduces is pinned here: absent until the notification, present after it, and never
/// moved by a repeat.
/// </summary>
public class ReadyStampTests
{
    [Fact]
    public void No_stamp_before_the_notification()
    {
        Assert.Null(new LspClient.Endpoints().InitializedAt);
    }

    [Fact]
    public void The_notification_stamps_its_own_arrival()
    {
        var endpoints = new LspClient.Endpoints();
        var before = DateTime.UtcNow;

        endpoints.OnProjectInitializationComplete();

        var at = endpoints.InitializedAt;
        Assert.NotNull(at);
        Assert.Equal(DateTimeKind.Utc, at.Value.Kind);
        Assert.InRange(at.Value, before, DateTime.UtcNow);
        Assert.True(endpoints.ProjectInitialized.IsCompleted);
    }

    [Fact]
    public void A_second_notification_does_not_move_the_stamp()
    {
        var endpoints = new LspClient.Endpoints();

        endpoints.OnProjectInitializationComplete();
        var first = endpoints.InitializedAt;
        // The clock has to have moved, or a stamp that was rewritten would read unchanged.
        while (DateTime.UtcNow == first) Thread.Sleep(1);
        endpoints.OnProjectInitializationComplete();

        Assert.Equal(first, endpoints.InitializedAt);
    }
}
