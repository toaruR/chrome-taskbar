using System.Runtime.InteropServices;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public enum ActivationOutcome
{
    Minimized,
    Activated,
    TargetGone,
    MinimizeFailed,
    ActivateFailed,
    /// <summary>SetForegroundWindow succeeded but cross-process foreground change was not yet visible; confirm later via IsForegroundConfirmed (no Sleep/retry).</summary>
    ActivationPending,
}

public sealed record ActivationResult(ActivationOutcome Outcome, string? Detail = null)
{
    public bool IsSuccess => Outcome is ActivationOutcome.Minimized or ActivationOutcome.Activated;
    public bool IsPending => Outcome == ActivationOutcome.ActivationPending;
}

public interface IWindowOps
{
    bool IsIconic(nint hwnd);
    bool Minimize(nint hwnd);
    bool Restore(nint hwnd);
    bool SetForeground(nint hwnd);
    nint GetForeground();
}

public sealed class Win32WindowOps : IWindowOps
{
    [DllImport("user32.dll", EntryPoint = "IsIconic")] private static extern bool Iconic(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint Fg();
    private const int SW_MINIMIZE = 6, SW_RESTORE = 9;

    public bool IsIconic(nint hwnd) => Iconic(hwnd);
    public bool Minimize(nint hwnd) => ShowWindow(hwnd, SW_MINIMIZE);
    public bool Restore(nint hwnd) => ShowWindow(hwnd, SW_RESTORE);
    public bool SetForeground(nint hwnd) => SetForegroundWindow(hwnd);
    public nint GetForeground() => Fg();
}

/// <summary>Toggle / activate Chrome windows. No Sleep, no retry loops, no persistent state.</summary>
public sealed class WindowActivator
{
    private readonly IWindowEnumerator _enumerator;
    private readonly IWindowOps _ops;

    public WindowActivator(IWindowEnumerator enumerator, IWindowOps ops)
    {
        _enumerator = enumerator;
        _ops = ops;
    }

    /// <summary>Window button click. wasForegroundBeforeClick comes from ForegroundTracker.</summary>
    public ActivationResult Toggle(WindowKey key, bool wasForegroundBeforeClick)
    {
        if (!Revalidate(key)) return new(ActivationOutcome.TargetGone, "revalidation failed");
        if (wasForegroundBeforeClick && !_ops.IsIconic(key.Hwnd))
        {
            if (!_ops.Minimize(key.Hwnd)) return new(ActivationOutcome.MinimizeFailed, "ShowWindow returned false");
            return _ops.IsIconic(key.Hwnd)
                ? new(ActivationOutcome.Minimized)
                : new(ActivationOutcome.MinimizeFailed, "window not iconic after minimize");
        }
        return Activate(key);
    }

    /// <summary>Restore and foreground (tab click path); never minimizes.</summary>
    public ActivationResult Activate(WindowKey key)
    {
        if (!Revalidate(key)) return new(ActivationOutcome.TargetGone, "revalidation failed");
        if (_ops.IsIconic(key.Hwnd)) _ops.Restore(key.Hwnd);
        bool called = _ops.SetForeground(key.Hwnd);
        bool confirmed = _ops.GetForeground() == key.Hwnd && !_ops.IsIconic(key.Hwnd);
        if (confirmed) return new(ActivationOutcome.Activated);
        if (called && !_ops.IsIconic(key.Hwnd))
            return new(ActivationOutcome.ActivationPending, "foreground not yet visible; confirm on a later tick");
        return new(ActivationOutcome.ActivateFailed,
            called ? "window still iconic after restore" : "SetForegroundWindow returned false");
    }

    /// <summary>Main-side confirmation after a tab selection: target still valid, foreground and not iconic.</summary>
    public bool IsForegroundConfirmed(WindowKey key) =>
        Revalidate(key) && _ops.GetForeground() == key.Hwnd && !_ops.IsIconic(key.Hwnd);

    private bool Revalidate(WindowKey key)
    {
        var o = _enumerator.Observe(key.Hwnd);
        return o is not null && ChromeWindowFilter.IsChromeMainWindow(o)
            && o.ProcessId == key.ProcessId && o.ProcessStartTimeTicks == key.ProcessStartTimeTicks;
    }
}
