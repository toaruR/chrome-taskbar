using System.Diagnostics;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public enum HoverState { Idle, Waiting, Open, Closing }
public enum HoverEventKind { Prefetch, OpenPopup, ClosePopup }
public enum CloseReason { Left, ButtonClick, Escape, OutsideClick, Selection, TargetGone, SwitchedTarget }

public sealed record HoverEvent(HoverEventKind Kind, WindowKey Key, long Generation, long TickMs, CloseReason? Reason = null);

public static class MonotonicClock
{
    public static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
}

/// <summary>
/// Idle/Waiting/Open/Closing state machine. Pure: all times are monotonic milliseconds passed by the caller.
/// Never activates, restores or focuses anything; it only emits Prefetch/OpenPopup/ClosePopup events.
/// </summary>
public sealed class HoverController : IDisposable
{
    public const long HoverDelayMs = 500;
    public const long PopupCloseGraceMs = 250;

    private readonly List<HoverEvent> _events = new();
    private long _enterTick, _exitTick;
    private bool _overButton, _overPopup, _disposed;

    public HoverState State { get; private set; } = HoverState.Idle;
    public WindowKey? Key { get; private set; }
    public long Generation { get; private set; }
    public IReadOnlyList<HoverEvent> Events => _events;
    public event Action<HoverEvent>? Raised;

    /// <summary>Monotonic tick at which the host must call Tick next, or null when no timer is needed.</summary>
    public long? NextDeadlineMs => State switch
    {
        HoverState.Waiting => _enterTick + HoverDelayMs,
        HoverState.Closing => _exitTick + PopupCloseGraceMs,
        _ => null,
    };

    public void ButtonEnter(WindowKey key, long now)
    {
        if (_disposed) return;
        if (State != HoverState.Idle && Key == key)
        {
            _overButton = true;
            if (State == HoverState.Closing) State = HoverState.Open;
            return;
        }
        if (State is HoverState.Open or HoverState.Closing) Dismiss(CloseReason.SwitchedTarget, now);
        Generation++;
        Key = key;
        State = HoverState.Waiting;
        _enterTick = now;
        _overButton = true;
        _overPopup = false;
        Emit(new HoverEvent(HoverEventKind.Prefetch, key, Generation, now));
    }

    public void ButtonLeave(WindowKey key, long now)
    {
        if (_disposed || Key != key) return;
        _overButton = false;
        if (State == HoverState.Waiting) Reset();
        else if (State == HoverState.Open && !_overPopup) BeginClosing(now);
    }

    public void PopupEnter(long now)
    {
        if (_disposed || State is not (HoverState.Open or HoverState.Closing)) return;
        _overPopup = true;
        State = HoverState.Open;
    }

    public void PopupLeave(long now)
    {
        if (_disposed) return;
        _overPopup = false;
        if (State == HoverState.Open && !_overButton) BeginClosing(now);
    }

    /// <summary>Fires the pending boundary for the current target.</summary>
    public bool Tick(long now) => Key is { } k && Fire(k, Generation, now);

    /// <summary>Timer callback: ignored unless WindowKey and generation still match.</summary>
    public bool Fire(WindowKey key, long generation, long now)
    {
        if (_disposed || Key != key || Generation != generation) return false;
        if (State == HoverState.Waiting && _overButton && now - _enterTick >= HoverDelayMs)
        {
            State = HoverState.Open;
            Emit(new HoverEvent(HoverEventKind.OpenPopup, key, generation, now));
            return true;
        }
        if (State == HoverState.Closing && now - _exitTick >= PopupCloseGraceMs)
        {
            Dismiss(CloseReason.Left, now);
            return true;
        }
        return false;
    }

    public void ButtonClick(long now) => Dismiss(CloseReason.ButtonClick, now);
    public void Escape(long now) => Dismiss(CloseReason.Escape, now);
    public void OutsideClick(long now) => Dismiss(CloseReason.OutsideClick, now);
    public void Selected(long now) => Dismiss(CloseReason.Selection, now);
    public void TargetGone(WindowKey key, long now) { if (Key == key) Dismiss(CloseReason.TargetGone, now); }

    public void Dispose()
    {
        _disposed = true;
        Raised = null;
        Reset();
    }

    private void BeginClosing(long now) { State = HoverState.Closing; _exitTick = now; }

    private void Dismiss(CloseReason reason, long now)
    {
        if (_disposed || State == HoverState.Idle || Key is not { } key) return;
        bool wasShown = State is HoverState.Open or HoverState.Closing;
        long gen = Generation;
        Reset();
        if (wasShown) Emit(new HoverEvent(HoverEventKind.ClosePopup, key, gen, now, reason));
    }

    private void Reset() { State = HoverState.Idle; Key = null; _overButton = _overPopup = false; }

    private void Emit(HoverEvent e) { _events.Add(e); Raised?.Invoke(e); }
}
