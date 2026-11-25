using System.IO;
using ChromeTaskbar.App;
using Xunit;
using static ChromeTaskbar.Tests.ChromeWindowFilterTests;

namespace ChromeTaskbar.Tests;

public sealed class WindowOrderTests
{
    private static nint[] Order(WindowRegistry r, params WindowObservation[] seen) =>
        r.Update(seen, 0).Select(e => e.Key.Hwnd).ToArray();

    [Fact]
    public void SavedOrder_IsRestored_AndUnknownWindowsGoToTheRight()
    {
        var saved = new[] { new SavedWindow(3, 10, 100), new SavedWindow(1, 10, 100), new SavedWindow(2, 10, 100) };
        var r = new WindowRegistry(saved);
        // Enumeration (z-order) differs from the saved order; window 9 is new.
        Assert.Equal(new nint[] { 3, 1, 2, 9 }, Order(r, W(9), W(1), W(2), W(3)));
    }

    [Fact]
    public void NewWindow_EgTornOffTab_IsAppendedOnTheRight_WithoutMovingOthers()
    {
        var r = new WindowRegistry();
        Assert.Equal(new nint[] { 5, 4 }, Order(r, W(5), W(4)));
        Assert.Equal(new nint[] { 5, 4, 7 }, Order(r, W(7), W(4), W(5)));
        Assert.Equal(new nint[] { 4, 7 }, Order(r, W(7), W(4))); // closed window: the rest close up
    }

    [Fact]
    public void SavedWindow_FromAnotherProcess_IsNotMatched_AndDeadProcessesAreForgotten()
    {
        var r = new WindowRegistry(new[] { new SavedWindow(2, 10, 100), new SavedWindow(1, 99, 5) });
        // HWND 2 reused by a different Chrome process (restarted) → treated as new.
        Assert.Equal(new nint[] { 1, 2 }, Order(r, W(1), W(2, start: 200)));
        Assert.DoesNotContain(r.SavedOrder, w => w.ProcessId == 99);
    }

    [Fact]
    public void SavedWindow_NotVisibleYet_KeepsItsSlot_WhenItAppears()
    {
        var r = new WindowRegistry(new[] { new SavedWindow(1, 10, 100), new SavedWindow(2, 10, 100), new SavedWindow(3, 10, 100) });
        Assert.Equal(new nint[] { 1, 3 }, Order(r, W(3), W(1))); // 2 cloaked on another desktop
        Assert.Equal(new long[] { 1, 2, 3 }, r.SavedOrder.Select(w => w.Hwnd));
        Assert.Equal(new nint[] { 1, 2, 3 }, Order(r, W(3), W(2), W(1)));
    }

    [Fact]
    public void Taskbar_InsertsAReappearingSavedWindowAtItsSlot()
    {
        var r = new WindowRegistry(new[] { new SavedWindow(1, 10, 100), new SavedWindow(2, 10, 100), new SavedWindow(3, 10, 100) });
        using var hover = new HoverController();
        var bar = new TaskbarViewModel(hover, () => 0, _ => { });
        bar.Update(r.Update(new[] { W(3), W(1) }, 0), _ => null);
        bar.Update(r.Update(new[] { W(3), W(2), W(1) }, 0), _ => null);
        Assert.Equal(new nint[] { 1, 2, 3 }, bar.Buttons.Select(b => b.Key.Hwnd));
    }

    private static Func<ChromeTaskbar.Contracts.WindowKey, string?> Titles(params (nint Hwnd, string Title)[] t) =>
        k => t.FirstOrDefault(x => x.Hwnd == k.Hwnd).Title;

    [Fact]
    public void ChromeRestart_SavedOrderIsRestoredByIconTitle()
    {
        // Saved by a Chrome process (10, 100) that no longer exists.
        var r = new WindowRegistry(new[] { new SavedWindow(1, 10, 100, "A"), new SavedWindow(2, 10, 100, "B"), new SavedWindow(3, 10, 100, "C") });
        var seen = new[] { W(7, pid: 20, start: 300), W(8, pid: 20, start: 300), W(9, pid: 20, start: 300) };
        Assert.Equal(new nint[] { 7, 8, 9 }, Order(r, seen)); // tabs unknown yet: first-seen
        var entries = r.Update(seen, 0, Titles((7, "C"), (8, "X"), (9, "A")));
        Assert.Equal(new nint[] { 9, 7, 8 }, entries.Select(e => e.Key.Hwnd)); // A, C, unknown X on the right
        // Saved slot B is kept for a window that is restored later.
        Assert.Equal(new[] { "A", "B", "C", "X" }, r.SavedOrder.Select(w => w.IconTitle));
    }

    [Fact]
    public void ChromeKilledWhileRunning_RestartedWindowsTakeTheirSlotsBack()
    {
        var r = new WindowRegistry();
        var before = new[] { W(1), W(2), W(3) };
        r.Update(before, 0, Titles((1, "A"), (2, "B"), (3, "C")));
        Assert.Empty(Order(r)); // Chrome killed
        var after = new[] { W(5, pid: 20, start: 300), W(6, pid: 20, start: 300), W(4, pid: 20, start: 300) };
        var entries = r.Update(after, 0, Titles((4, "A"), (5, "C"), (6, "B")));
        Assert.Equal(new nint[] { 4, 6, 5 }, entries.Select(e => e.Key.Hwnd));
    }

    [Fact]
    public void ResolvedWindow_DoesNotMove_WhenItsIconTitleChangesLater()
    {
        var r = new WindowRegistry(new[] { new SavedWindow(1, 10, 100, "A") });
        var seen = new[] { W(7, pid: 20, start: 300), W(8, pid: 20, start: 300) };
        r.Update(seen, 0, Titles((7, "Z"), (8, "Y")));
        // 8 later navigates to "A": it must not jump into the saved slot.
        Assert.Equal(new nint[] { 7, 8 }, r.Update(seen, 0, Titles((7, "Z"), (8, "A"))).Select(e => e.Key.Hwnd));
    }

    [Fact]
    public void ClosingAWindowOfARunningChrome_DoesNotKeepItsSlot()
    {
        var r = new WindowRegistry();
        r.Update(new[] { W(1), W(2) }, 0, Titles((1, "A"), (2, "B")));
        r.Update(new[] { W(2) }, 0, Titles((2, "B")));
        Assert.Equal(new long[] { 2 }, r.SavedOrder.Select(w => w.Hwnd));
    }

    [Fact]
    public void Taskbar_MovesAButtonMatchedToASavedSlot()
    {
        var r = new WindowRegistry(new[] { new SavedWindow(1, 10, 100, "A") });
        using var hover = new HoverController();
        var bar = new TaskbarViewModel(hover, () => 0, _ => { });
        var seen = new[] { W(7, pid: 20, start: 300), W(8, pid: 20, start: 300) };
        bar.Update(r.Update(seen, 0), _ => null);
        bar.Update(r.Update(seen, 0, Titles((8, "A"))), _ => null);
        Assert.Equal(new nint[] { 8, 7 }, bar.Buttons.Select(b => b.Key.Hwnd));
    }

    [Fact]
    public void Store_ReadsFilesWithoutIconTitle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ct-order-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """[{"Hwnd":3,"ProcessId":10,"ProcessStartTimeTicks":100}]""");
            Assert.Equal(new[] { new SavedWindow(3, 10, 100) }, WindowOrderStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Store_RoundTrips_AndToleratesCorruptFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ct-order-{Guid.NewGuid():N}.json");
        try
        {
            var order = new[] { new SavedWindow(3, 10, 100), new SavedWindow(1, 11, 200, "タイトル") };
            WindowOrderStore.Save(path, order);
            Assert.Equal(order, WindowOrderStore.Load(path));
            File.WriteAllText(path, "{not json");
            Assert.Empty(WindowOrderStore.Load(path));
        }
        finally { File.Delete(path); }
    }
}

public sealed class ActiveHighlightTests
{
    [Fact]
    public void OnlyTheForegroundNonMinimizedWindowIsActive()
    {
        var r = new WindowRegistry();
        using var hover = new HoverController();
        var bar = new TaskbarViewModel(hover, () => 0, _ => { });
        bar.Update(r.Update(new[] { ChromeWindowFilterTests.W(1), ChromeWindowFilterTests.W(2) }, 2), _ => null);
        Assert.Equal(new[] { false, true }, bar.Buttons.Select(b => b.IsActive));
        bar.Update(r.Update(new[] { ChromeWindowFilterTests.W(1), ChromeWindowFilterTests.W(2) with { IsMinimized = true } }, 2), _ => null);
        Assert.All(bar.Buttons, b => Assert.False(b.IsActive));
    }
}
