using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class IdenticonTests
{
    private static readonly WindowKey K1 = new(1, 10, 100, 1), K2 = new(2, 10, 100, 2);

    private static TabSnapshot Snap(WindowKey key, TabAvailability a, params (string Title, int Order)[] tabs) =>
        new(new WorkerEpoch(1), key, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UnixEpoch, a,
            tabs.Select(t => new TabEntry(new TabToken(t.Title), t.Title, t.Order, false, true)).ToList());

    [Fact]
    public void Hash_IsDeterministic_AndDistinguishesNames()
    {
        Assert.Equal(Identicon.Hash("GitHub"), Identicon.Hash("GitHub"));
        Assert.NotEqual(Identicon.Hash("GitHub"), Identicon.Hash("Gmail"));
    }

    [Fact]
    public void Pattern_IsHorizontallySymmetric()
    {
        var p = Identicon.Pattern("Some tab title");
        for (int y = 0; y < Identicon.Cells; y++)
            for (int x = 0; x < Identicon.Cells; x++)
                Assert.Equal(p[y, x], p[y, Identicon.Cells - 1 - x]);
    }

    [Fact]
    public void LeftmostTabTitle_UsesLowestDisplayOrder_NotListOrder()
    {
        Assert.Equal("first", TaskbarViewModel.LeftmostTabTitle(Snap(K1, TabAvailability.Available, ("second", 1), ("first", 0))));
        Assert.Null(TaskbarViewModel.LeftmostTabTitle(Snap(K1, TabAvailability.Available)));
        Assert.Null(TaskbarViewModel.LeftmostTabTitle(Snap(K1, TabAvailability.Unavailable, ("x", 0))));
    }

    [Fact]
    public void Taskbar_KeepsIconTitle_AfterCacheEntryIsGone_AndDropsItWithTheWindow()
    {
        using var hover = new HoverController();
        var bar = new TaskbarViewModel(hover, () => 0, _ => { });
        var e1 = new ChromeWindowEntry(K1, "w", "w", false, false, 1);
        var cached = new CachedTabs(Snap(K1, TabAvailability.Available, ("left", 0), ("right", 1)), true);
        bar.Update(new[] { e1 }, _ => cached);
        Assert.Equal("left", bar.Buttons[0].IconTitle);
        Assert.NotNull(bar.Buttons[0].Icon);

        bar.Update(new[] { e1 }, _ => null); // evicted / expired from the snapshot cache
        Assert.Equal("left", bar.Buttons[0].IconTitle);

        bar.Update(Array.Empty<ChromeWindowEntry>(), _ => null);
        bar.Update(new[] { e1 }, _ => null);
        Assert.Null(bar.Buttons[0].IconTitle);
    }

    [Fact]
    public void IconRefreshScheduler_UnattemptedFirst_ThenOldestAfterInterval()
    {
        var s = new IconRefreshScheduler();
        var live = new[] { K1, K2 };
        Assert.Equal(K1, s.Next(live, 0));
        s.MarkAttempted(K1, 0);
        Assert.Equal(K2, s.Next(live, 0));
        s.MarkAttempted(K2, 100);
        Assert.Null(s.Next(live, IconRefreshScheduler.IntervalMs - 1));
        Assert.Equal(K1, s.Next(live, IconRefreshScheduler.IntervalMs));
        Assert.Null(s.Next(Array.Empty<WindowKey>(), IconRefreshScheduler.IntervalMs * 2));
        Assert.Equal(K1, s.Next(live, IconRefreshScheduler.IntervalMs * 2)); // forgotten once gone
    }
}
