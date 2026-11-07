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

        foreach (var (hwnd, o) in current)
        {
            if (_slots.ContainsKey(hwnd)) continue;
            var key = new WindowKey(hwnd, o.ProcessId, o.ProcessStartTimeTicks, _nextGeneration++);
            _slots[hwnd] = new Slot(key, _nextSequence++);
        }

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
