using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public sealed record ChromeWindowEntry(
    WindowKey Key,
    string FullTitle,
    string DisplayTitle,
    bool IsMinimized,
    bool IsForeground,
    long FirstSeenSequence);

public sealed class WindowRegistry
{
    /// <summary>Dead-process saved windows kept for icon-title matching (beyond this, the rightmost are dropped).</summary>
    public const int MaxDeadSaved = 32;

    private sealed class Slot(WindowKey key, long sequence, bool resolved)
    {
        public WindowKey Key { get; } = key;
        public long Sequence { get; set; } = sequence;
        /// <summary>Position is final: matched by identity, or its icon title was looked up once.</summary>
        public bool Resolved { get; set; } = resolved;
        public string? IconTitle { get; set; }
    }

    private readonly Dictionary<nint, Slot> _slots = new();
    private long _nextGeneration = 1;
    private long _nextSequence = 1;
    // Saved order not yet matched to a live window; loaded sequence = index - count (< 1, before new windows).
    private readonly List<(SavedWindow Window, long Sequence)> _pending = new();

    /// <summary>
    /// <paramref name="savedOrder"/> (from a previous run) places matching windows in that order: first by identity
    /// (same HWND, process id and start time), else, for windows of a Chrome process that is gone (killed and
    /// restarted), by the title of the leftmost tab (the icon's tab). Every other window, e.g. a tab dragged out into
    /// a new window, is appended on the right.
    /// </summary>
    public WindowRegistry(IReadOnlyList<SavedWindow>? savedOrder = null)
    {
        if (savedOrder is null) return;
        for (int i = 0; i < savedOrder.Count; i++) _pending.Add((savedOrder[i], i - savedOrder.Count));
    }

    /// <summary>Order to save: live windows plus saved ones not seen yet (e.g. cloaked on another desktop).</summary>
    public IReadOnlyList<SavedWindow> SavedOrder =>
        _slots.Values.Select(s => (Window: Saved(s.Key, s.IconTitle), s.Sequence))
            .Concat(_pending)
            .OrderBy(p => p.Sequence).Select(p => p.Window).ToList();

    /// <summary>Keys that disappeared (or were replaced) in the last Update; their tabs must be invalidated.</summary>
    public IReadOnlyList<WindowKey> LastInvalidated { get; private set; } = Array.Empty<WindowKey>();
    public IReadOnlyList<ChromeWindowEntry> Entries { get; private set; } = Array.Empty<ChromeWindowEntry>();

    /// <param name="iconTitle">Leftmost tab title of a window, or null while its tabs are unknown.</param>
    public IReadOnlyList<ChromeWindowEntry> Update(IEnumerable<WindowObservation> observed, nint foreground,
        Func<WindowKey, string?>? iconTitle = null)
    {
        var current = new Dictionary<nint, WindowObservation>();
        foreach (var o in observed)
            if (ChromeWindowFilter.IsChromeMainWindow(o)) current[o.Hwnd] = o;
        var liveProcesses = current.Values.Select(o => (o.ProcessId, o.ProcessStartTimeTicks)).ToHashSet();
        bool Dead(SavedWindow w) => !liveProcesses.Contains((w.ProcessId, w.ProcessStartTimeTicks));

        var invalidated = new List<WindowKey>();
        foreach (var (hwnd, slot) in _slots.ToArray())
        {
            if (!current.TryGetValue(hwnd, out var o) || !SameIdentity(slot.Key, o))
            {
                invalidated.Add(slot.Key);
                _slots.Remove(hwnd);
                // Chrome itself went away (killed / crashed): keep the slot so its restarted window can take it back.
                var saved = Saved(slot.Key, slot.IconTitle);
                if (saved.IconTitle is not null && Dead(saved)) _pending.Add((saved, slot.Sequence));
            }
        }

        // New windows in first-seen (enumeration) order go to the right end, unless the saved order knows them.
        foreach (var (hwnd, o) in current)
        {
            if (_slots.ContainsKey(hwnd)) continue;
            var key = new WindowKey(hwnd, o.ProcessId, o.ProcessStartTimeTicks, _nextGeneration++);
            int i = _pending.FindIndex(p => p.Window.Hwnd == hwnd &&
                p.Window.ProcessId == o.ProcessId && p.Window.ProcessStartTimeTicks == o.ProcessStartTimeTicks);
            if (i >= 0)
            {
                _slots[hwnd] = new Slot(key, _pending[i].Sequence, resolved: true) { IconTitle = _pending[i].Window.IconTitle };
                _pending.RemoveAt(i);
            }
            else _slots[hwnd] = new Slot(key, _nextSequence++, resolved: false);
        }

        // Once a new window's icon title is known, it takes the slot of a dead window with the same title (once).
        foreach (var s in _slots.Values.OrderBy(s => s.Sequence).ToList())
        {
            if (iconTitle?.Invoke(s.Key) is not { } t) continue;
            s.IconTitle = t;
            if (s.Resolved) continue;
            s.Resolved = true;
            int best = -1;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].Window.IconTitle == t && Dead(_pending[i].Window) &&
                    (best < 0 || _pending[i].Sequence < _pending[best].Sequence)) best = i;
            if (best < 0) continue;
            s.Sequence = _pending[best].Sequence;
            _pending.RemoveAt(best);
        }

        // Dead windows without an icon title can never be matched again; the rest are bounded.
        _pending.RemoveAll(p => Dead(p.Window) && p.Window.IconTitle is null);
        foreach (var p in _pending.Where(p => Dead(p.Window)).OrderBy(p => p.Sequence).Skip(MaxDeadSaved).ToList())
            _pending.Remove(p);

        var ordered = _slots.Values.OrderBy(s => s.Sequence).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = new List<ChromeWindowEntry>(ordered.Count);
        foreach (var s in ordered)
        {
            var o = current[s.Key.Hwnd];
            counts[o.Title] = counts.GetValueOrDefault(o.Title) + 1;
            entries.Add(new ChromeWindowEntry(s.Key, o.Title, o.Title, o.IsMinimized,
                o.Hwnd == foreground && !o.IsMinimized, s.Sequence));
        }
        // Stable numbering: by first-seen order among windows sharing the same title.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            var t = entries[i].FullTitle;
            int n = seen[t] = seen.GetValueOrDefault(t) + 1;
            if (counts[t] > 1) entries[i] = entries[i] with { DisplayTitle = $"{t} ({n})" };
        }

        LastInvalidated = invalidated;
        Entries = entries;
        return entries;
    }

    /// <summary>Drops cache entries for windows that disappeared or were replaced in the last Update.</summary>
    public void ApplyInvalidations(TabSnapshotCache cache)
    {
        foreach (var key in LastInvalidated) cache.Invalidate(key);
    }

    /// <summary>True if key is still the registered identity of its HWND.</summary>
    public bool IsCurrent(WindowKey key) => _slots.TryGetValue(key.Hwnd, out var s) && s.Key == key;

    private static SavedWindow Saved(WindowKey k, string? iconTitle) =>
        new(k.Hwnd, k.ProcessId, k.ProcessStartTimeTicks, iconTitle);

    private static bool SameIdentity(WindowKey k, WindowObservation o) =>
        k.ProcessId == o.ProcessId && k.ProcessStartTimeTicks == o.ProcessStartTimeTicks;
}
