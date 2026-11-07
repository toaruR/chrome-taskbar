using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public sealed record CachedTabs(TabSnapshot Snapshot, bool IsFresh)
{
    /// <summary>Stale entries may be displayed but never selected until a fresh snapshot confirms them.</summary>
    public bool CanSelect => IsFresh;
}

/// <summary>Bounded per-window snapshot cache: CacheWindowLimit entries, CacheTtl age.</summary>
public sealed class TabSnapshotCache
{
    public const int CacheWindowLimit = 8;
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMilliseconds(5000);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly List<WindowKey> _order = new();
    private readonly Dictionary<WindowKey, TabSnapshot> _items = new();

    public TabSnapshotCache(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public int Count { get { lock (_gate) return _items.Count; } }
    public IReadOnlyList<WindowKey> Keys { get { lock (_gate) return _order.ToArray(); } }

    /// <summary>Adds or replaces a snapshot; the oldest entry is evicted when the limit is exceeded.</summary>
    public IReadOnlyList<WindowKey> Put(TabSnapshot snapshot)
    {
        var evicted = new List<WindowKey>();
        lock (_gate)
        {
            _order.Remove(snapshot.WindowKey);
            _order.Add(snapshot.WindowKey);
            _items[snapshot.WindowKey] = snapshot;
            while (_order.Count > CacheWindowLimit)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _items.Remove(oldest);
                evicted.Add(oldest);
            }
        }
        return evicted;
    }

    public CachedTabs? Get(WindowKey key)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var s)) return null;
            return new CachedTabs(s, _clock() - s.CapturedAt < CacheTtl);
        }
    }

    public bool Invalidate(WindowKey key)
    {
        lock (_gate) { _order.Remove(key); return _items.Remove(key); }
    }

    public void Clear() { lock (_gate) { _order.Clear(); _items.Clear(); } }
}
