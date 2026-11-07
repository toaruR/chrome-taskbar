using System.Collections.ObjectModel;
using System.Windows.Input;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

public sealed record DispatchRecord(long Sequence, string Reason, long TickMs, long FirstNotifyTickMs, int Count);

/// <summary>WinEventDebounce (100ms) / WinEventMaxWait (500ms) coalescing. Caller supplies monotonic ms.</summary>
public sealed class WinEventCoalescer
{
    public const long DebounceMs = 100;
    public const long MaxWaitMs = 500;
    private long _first, _last;
    private int _count;
    private long _sequence;

    public bool Pending => _count > 0;
    public void Notify(long now) { if (_count++ == 0) _first = now; _last = now; }

    public DispatchRecord? Poll(long now)
    {
        if (_count == 0) return null;
        string? reason = now - _first >= MaxWaitMs ? "MaxWait" : now - _last >= DebounceMs ? "Debounce" : null;
        if (reason is null) return null;
        var rec = new DispatchRecord(++_sequence, reason, now, _first, _count);
        _count = 0;
        return rec;
    }
}

public sealed record FireRecord(long Sequence, long TickMs);

/// <summary>Fixed-interval gate (VisiblePollInterval, MinimizedPollInterval, PopupRefreshInterval).</summary>
public sealed class IntervalGate
{
    private readonly long _intervalMs;
    private long _last, _sequence;
    public IntervalGate(long intervalMs, long startMs) { _intervalMs = intervalMs; _last = startMs; }
    public FireRecord? TryFire(long now)
    {
        if (now - _last < _intervalMs) return null;
        _last = now;
        return new FireRecord(++_sequence, now);
    }
}

public sealed class WindowPollScheduler
{
    public const long VisibleIntervalMs = 2000;
    public const long MinimizedIntervalMs = 10000;
    private readonly IntervalGate _visible, _minimized;
    public WindowPollScheduler(long startMs)
    {
        _visible = new IntervalGate(VisibleIntervalMs, startMs);
        _minimized = new IntervalGate(MinimizedIntervalMs, startMs);
    }
    /// <summary>Re-enumeration is due when the interval for the bar's current mode has elapsed.</summary>
    public FireRecord? Poll(long now, bool barMinimized) => barMinimized ? _minimized.TryFire(now) : _visible.TryFire(now);
}

/// <summary>Refreshes only while a popup is open (PopupRefreshInterval = 1000ms).</summary>
public sealed class PopupRefreshScheduler
{
    public const long IntervalMs = 1000;
    private IntervalGate? _gate;
    public void Start(long now) => _gate = new IntervalGate(IntervalMs, now);
    public void Stop() => _gate = null;
    public FireRecord? Poll(long now) => _gate?.TryFire(now);
}

/// <summary>Worker restart backoff 1000/5000/30000ms by failure ordinal.</summary>
public sealed class RestartBackoff
{
    public static readonly long[] DelaysMs = { 1000, 5000, 30000 };
    private long _dueMs;
    public int Failures { get; private set; }
    public long DueMs => _dueMs;
    public void RecordFailure(long now) { Failures++; _dueMs = now + DelaysMs[Math.Min(Failures, DelaysMs.Length) - 1]; }
    public void Reset() { Failures = 0; _dueMs = 0; }
    public bool CanSpawn(long now) => Failures == 0 || now >= _dueMs;
}

public sealed class WindowButtonViewModel
{
    public WindowButtonViewModel(ChromeWindowEntry entry, string? tabCountText, string? iconTitle = null)
    {
        Entry = entry; TabCountText = tabCountText; IconTitle = iconTitle;
    }
    public ChromeWindowEntry Entry { get; }
    public WindowKey Key => Entry.Key;
    public string Title => Entry.DisplayTitle;
    /// <summary>Null when tabs were not obtained; never "0" for an unknown count.</summary>
    public string? TabCountText { get; }
    /// <summary>Title of the window's leftmost tab (top of the popup list); null until tabs were read once.</summary>
    public string? IconTitle { get; }
    public System.Windows.Media.ImageSource? Icon => IconTitle is null ? null : Identicon.Image(IconTitle);
}

/// <summary>Taskbar buttons. Hover only drives HoverController; clicking invokes the injected toggle action.</summary>
public sealed class TaskbarViewModel : IDisposable
{
    private readonly HoverController _hover;
    private readonly Func<long> _clock;
    private readonly Action<WindowKey> _toggle;
    private bool _disposed;

    public TaskbarViewModel(HoverController hover, Func<long> clock, Action<WindowKey> toggle)
    {
        _hover = hover; _clock = clock; _toggle = toggle;
        ClickCommand = new RelayCommand(p => { if (p is WindowKey k) OnButtonClick(k); });
    }

    public ObservableCollection<WindowButtonViewModel> Buttons { get; } = new();
    // Last known leftmost-tab title per window; outlives the bounded, TTL'd snapshot cache so icons stay stable.
    private readonly Dictionary<WindowKey, string> _iconTitles = new();

    /// <summary>Leftmost tab = lowest DisplayOrder, i.e. the first row of the popup list.</summary>
    public static string? LeftmostTabTitle(TabSnapshot s) =>
        s.Availability == TabAvailability.Available && s.Tabs.Count > 0 ? s.Tabs.MinBy(t => t.DisplayOrder)!.Title : null;
    public ICommand ClickCommand { get; }

    public void Update(IReadOnlyList<ChromeWindowEntry> entries, Func<WindowKey, CachedTabs?> cache)
    {
        if (_disposed) return;
        var live = entries.Select(e => e.Key).ToHashSet();
        foreach (var gone in Buttons.Where(b => !live.Contains(b.Key)).ToList())
        {
            Buttons.Remove(gone);
            _iconTitles.Remove(gone.Key);
            _hover.TargetGone(gone.Key, _clock());
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var c = cache(e.Key);
            string? count = c is { Snapshot.Availability: TabAvailability.Available } ? c.Snapshot.Tabs.Count.ToString() : null;
            if (c is not null && LeftmostTabTitle(c.Snapshot) is { } leftmost) _iconTitles[e.Key] = leftmost;
            var vm = new WindowButtonViewModel(e, count, _iconTitles.GetValueOrDefault(e.Key));
            int existing = Buttons.ToList().FindIndex(b => b.Key == e.Key);
            if (existing < 0) Buttons.Insert(Math.Min(i, Buttons.Count), vm);
            else Buttons[existing] = vm;
        }
    }

    public void OnButtonClick(WindowKey key)
    {
        if (_disposed) return;
        _hover.ButtonClick(_clock());
        _toggle(key);
    }

    /// <summary>
    /// Explicit tab click: cancels a pending hover wait, then runs the popup's activate + SelectTab path.
    /// Closes the popup (hover Selection) only on full success; failures leave the popup open to report them.
    /// </summary>
    public async Task<bool> OnTabClickAsync(TabPopupViewModel popup, TabItemViewModel tab, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        if (_hover.State == HoverState.Waiting) _hover.ButtonClick(_clock());
        bool ok = await popup.SelectAsync(tab, cancellationToken).ConfigureAwait(true);
        if (ok && !_disposed) _hover.Selected(_clock());
        return ok;
    }

    public void Dispose()
    {
        _disposed = true;
        Buttons.Clear();
        _iconTitles.Clear();
    }
}
