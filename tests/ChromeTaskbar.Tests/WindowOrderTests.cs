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

    [Fact]
    public void Store_RoundTrips_AndToleratesCorruptFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ct-order-{Guid.NewGuid():N}.json");
        try
        {
            var order = new[] { new SavedWindow(3, 10, 100), new SavedWindow(1, 11, 200) };
            WindowOrderStore.Save(path, order);
            Assert.Equal(order, WindowOrderStore.Load(path));
            File.WriteAllText(path, "{not json");
            Assert.Empty(WindowOrderStore.Load(path));
        }
        finally { File.Delete(path); }
    }
}
