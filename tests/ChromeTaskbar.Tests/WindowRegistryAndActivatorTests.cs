using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

internal sealed class FakeWorld : IWindowEnumerator, IWindowOps
{
    public Dictionary<nint, WindowObservation> Windows = new();
    public nint Foreground;
    public bool AllowMinimize = true, AllowForeground = true, DeferForeground;
    public List<string> Calls = new();

    public IReadOnlyList<WindowObservation> EnumerateTopLevel() => Windows.Values.ToList();
    public WindowObservation? Observe(nint h) => Windows.GetValueOrDefault(h);
    nint IWindowEnumerator.GetForegroundWindow() => Foreground;
    public bool IsIconic(nint h) => Windows[h].IsMinimized;
    public bool Minimize(nint h) { Calls.Add("min"); if (!AllowMinimize) return true; Windows[h] = Windows[h] with { IsMinimized = true }; return true; }
    public bool Restore(nint h) { Calls.Add("restore"); Windows[h] = Windows[h] with { IsMinimized = false }; return true; }
    public bool SetForeground(nint h) { Calls.Add("fg"); if (AllowForeground && !DeferForeground) Foreground = h; return AllowForeground; }
    public nint GetForeground() => Foreground;
}

public class ChromeWindowFilterTests
{
    internal static WindowObservation W(nint h, string title = "t", int pid = 10, long start = 100) =>
        new(h, "Chrome_WidgetWin_1", title, true, false, false, false, false, pid, start, "chrome.exe");

    [Fact]
    public void Accepts_chrome_and_minimized() =>
        Assert.True(ChromeWindowFilter.IsChromeMainWindow(W(1) with { IsMinimized = true }));

    [Fact]
    public void Rejects_each_disqualifier()
    {
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { IsVisible = false }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { HasOwner = true }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { IsToolWindow = true }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { IsCloaked = true }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { ExecutableName = "Code.exe" }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { ExecutableName = null }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { ClassName = "Other" }));
        Assert.False(ChromeWindowFilter.IsChromeMainWindow(W(1) with { ProcessStartTimeTicks = 0 }));
    }
}

public class WindowRegistryTests
{
    [Fact]
    public void Order_labels_and_generation_are_stable()
    {
        var r = new WindowRegistry();
        var a = ChromeWindowFilterTests.W(1, "Same");
        var b = ChromeWindowFilterTests.W(2, "Same");
        var e = r.Update(new[] { b, a }, 1);
        Assert.Equal(new nint[] { 2, 1 }, e.Select(x => x.Key.Hwnd).ToArray());
        Assert.Equal("Same (1)", e[0].DisplayTitle);
        Assert.Equal("Same (2)", e[1].DisplayTitle);
        Assert.True(e[1].IsForeground);
        var k = e[0].Key;
        e = r.Update(new[] { a with { Title = "New" }, b }, 2);
        Assert.Equal(k, e[0].Key);
        Assert.Equal("New", e[1].DisplayTitle);
        Assert.Empty(r.LastInvalidated);
    }

    [Fact]
    public void Disappearance_and_reuse_invalidate()
    {
        var r = new WindowRegistry();
        var k1 = r.Update(new[] { ChromeWindowFilterTests.W(1) }, 0)[0].Key;
        r.Update(Array.Empty<WindowObservation>(), 0);
        Assert.Equal(new[] { k1 }, r.LastInvalidated);
        Assert.False(r.IsCurrent(k1));
        var k2 = r.Update(new[] { ChromeWindowFilterTests.W(1) }, 0)[0].Key;
        Assert.NotEqual(k1.Generation, k2.Generation);
        var k3 = r.Update(new[] { ChromeWindowFilterTests.W(1, start: 999) }, 0)[0].Key;
        Assert.Equal(new[] { k2 }, r.LastInvalidated);
        Assert.NotEqual(k2.Generation, k3.Generation);
    }

    [Fact]
    public void Non_chrome_excluded_and_minimized_kept()
    {
        var r = new WindowRegistry();
        var e = r.Update(new[]
        {
            ChromeWindowFilterTests.W(1) with { IsMinimized = true },
            ChromeWindowFilterTests.W(2) with { ExecutableName = "msedge.exe" },
        }, 1);
        Assert.Single(e);
        Assert.True(e[0].IsMinimized);
        Assert.False(e[0].IsForeground);
    }
}

public class WindowActivatorTests
{
    private static (FakeWorld, WindowActivator, WindowKey) Setup(bool minimized = false)
    {
        var w = new FakeWorld();
        w.Windows[1] = ChromeWindowFilterTests.W(1) with { IsMinimized = minimized };
        var key = new WindowRegistry().Update(w.EnumerateTopLevel(), 0)[0].Key;
        return (w, new WindowActivator(w, w), key);
    }

    [Fact]
    public void Foreground_target_minimizes()
    {
        var (w, a, k) = Setup(); w.Foreground = 1;
        var r = a.Toggle(k, true);
        Assert.Equal(ActivationOutcome.Minimized, r.Outcome);
        Assert.True(w.Windows[1].IsMinimized);
        Assert.DoesNotContain("fg", w.Calls);
    }

    [Fact]
    public void Minimized_target_restores_even_if_was_foreground()
    {
        var (w, a, k) = Setup(true);
        var r = a.Toggle(k, true);
        Assert.Equal(ActivationOutcome.Activated, r.Outcome);
        Assert.False(w.Windows[1].IsMinimized);
        Assert.Equal(1, (int)w.Foreground);
    }

    [Fact]
    public void Background_target_activates()
    {
        var (w, a, k) = Setup(); w.Foreground = 99;
        Assert.Equal(ActivationOutcome.Activated, a.Toggle(k, false).Outcome);
    }

    [Fact]
    public void Minimize_failure_observable()
    {
        var (w, a, k) = Setup(); w.AllowMinimize = false;
        Assert.Equal(ActivationOutcome.MinimizeFailed, a.Toggle(k, true).Outcome);
    }

    [Fact]
    public void Foreground_failure_observable_without_retry()
    {
        var (w, a, k) = Setup(); w.AllowForeground = false;
        var r = a.Toggle(k, false);
        Assert.Equal(ActivationOutcome.ActivateFailed, r.Outcome);
        Assert.Single(w.Calls, "fg");
    }

    [Fact]
    public void Foreground_set_true_but_not_yet_visible_is_pending_not_failed()
    {
        var (w, a, k) = Setup(); w.Foreground = 99; w.DeferForeground = true;
        var r = a.Toggle(k, false);
        Assert.Equal(ActivationOutcome.ActivationPending, r.Outcome);
        Assert.True(r.IsPending); Assert.False(r.IsSuccess);
        Assert.Single(w.Calls, "fg");
        Assert.False(a.IsForegroundConfirmed(k));
        w.Foreground = 1;
        Assert.True(a.IsForegroundConfirmed(k));
    }

    [Fact]
    public void Gone_or_reused_target_rejected()
    {
        var (w, a, k) = Setup();
        w.Windows[1] = w.Windows[1] with { ProcessStartTimeTicks = 5 };
        Assert.Equal(ActivationOutcome.TargetGone, a.Toggle(k, false).Outcome);
        w.Windows.Remove(1);
        Assert.Equal(ActivationOutcome.TargetGone, a.Activate(k).Outcome);
        Assert.Empty(w.Calls);
    }

    [Fact]
    public void Tab_activate_does_not_minimize_foreground()
    {
        var (w, a, k) = Setup(); w.Foreground = 1;
        Assert.Equal(ActivationOutcome.Activated, a.Activate(k).Outcome);
        Assert.DoesNotContain("min", w.Calls);
    }
}
