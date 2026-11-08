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
    private sealed record Slot(WindowKey Key, long Sequence);

    private readonly Dictionary<nint, Slot> _slots = new();
    private long _nextGeneration = 1;
    private long _nextSequence = 1;
    // Saved order not yet matched to a live window, keyed by HWND; sequence = index - count (< 1, before new windows).
    private readonly Dictionary<nint, (SavedWindow Window, long Sequence)> _pending = new();

    /// <summary>
    /// <paramref name="savedOrder"/> (from a previous run) places matching windows (same HWND, process id and start
    /// time) in that order; every other window, e.g. a tab dragged out into a new window, is appended on the right.
    /// </summary>
    public WindowRegistry(IReadOnlyList<SavedWindow>? savedOrder = null)
    {
        if (savedOrder is null) return;
        for (int i = 0; i < savedOrder.Count; i++)
            _pending.TryAdd((nint)savedOrder[i].Hwnd, (savedOrder[i], i - savedOrder.Count));
    }

    /// <summary>Order to save: live windows plus saved ones not seen yet (e.g. cloaked on another desktop).</summary>
    public IReadOnlyList<SavedWindow> SavedOrder =>
        _slots.Values.Select(s => (Window: new SavedWindow(s.Key.Hwnd, s.Key.ProcessId, s.Key.ProcessStartTimeTicks), s.Sequence))
            .Concat(_pending.Values)
            .OrderBy(p => p.Sequence).Select(p => p.Window).ToList();

    /// <summary>Keys that disappeared (or were replaced) in the last Update; their tabs must be invalidated.</summary>
    public IReadOnlyList<WindowKey> LastInvalidated { get; private set; } = Array.Empty<WindowKey>();
    public IReadOnlyList<ChromeWindowEntry> Entries { get; private set; } = Array.Empty<ChromeWindowEntry>();

    public IReadOnlyList<ChromeWindowEntry> Update(IEnumerable<WindowObservation> observed, nint foreground)
    {
        var current = new Dictionary<nint, WindowObservation>();
        foreach (var o in observed)
            if (ChromeWindowFilter.IsChromeMainWindow(o)) current[o.Hwnd] = o;

        var invalidated = new List<WindowKey>();
        foreach (var (hwnd, slot) in _slots.ToArray())
        {
            if (!current.TryGetValue(hwnd, out var o) || !SameIdentity(slot.Key, o))
            {
                invalidated.Add(slot.Key);
                _slots.Remove(hwnd);
            }
        }

        // New windows in first-seen (enumeration) order go to the right end, unless the saved order knows them.
        foreach (var (hwnd, o) in current)
        {
            if (_slots.ContainsKey(hwnd)) continue;
            var key = new WindowKey(hwnd, o.ProcessId, o.ProcessStartTimeTicks, _nextGeneration++);
            long sequence = _pending.Remove(hwnd, out var saved) &&
                saved.Window.ProcessId == o.ProcessId && saved.Window.ProcessStartTimeTicks == o.ProcessStartTimeTicks
                ? saved.Sequence : _nextSequence++;
            _slots[hwnd] = new Slot(key, sequence);
        }
        // Saved windows of a Chrome process that no longer owns any window are gone for good.
        var liveProcesses = current.Values.Select(o => (o.ProcessId, o.ProcessStartTimeTicks)).ToHashSet();
        foreach (var (hwnd, p) in _pending.ToArray())
            if (!liveProcesses.Contains((p.Window.ProcessId, p.Window.ProcessStartTimeTicks))) _pending.Remove(hwnd);

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

    private static bool SameIdentity(WindowKey k, WindowObservation o) =>
        k.ProcessId == o.ProcessId && k.ProcessStartTimeTicks == o.ProcessStartTimeTicks;
}
