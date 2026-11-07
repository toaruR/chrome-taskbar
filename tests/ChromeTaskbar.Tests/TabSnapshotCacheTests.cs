using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

public class TabSnapshotCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static WindowKey K(int n) => new((nint)n, 10, 100, n);
    internal static TabSnapshot Snap(WindowKey key, DateTimeOffset at, long epoch = 1) =>
        new(new WorkerEpoch(epoch), key, new SnapshotId(Guid.NewGuid()), at, TabAvailability.Available,
            new[] { new TabEntry(new TabToken("tok"), "t", 0, true, true) });

    [Theory]
    [InlineData(7, 0)]
    [InlineData(8, 0)]
    [InlineData(9, 1)]
    public void Window_limit_boundary(int entries, int evictedCount)
    {
        var cache = new TabSnapshotCache(() => T0);
        var evicted = new List<WindowKey>();
        for (int i = 1; i <= entries; i++) evicted.AddRange(cache.Put(Snap(K(i), T0)));
        Assert.Equal(evictedCount, evicted.Count);
        Assert.Equal(Math.Min(entries, 8), cache.Count);
        if (evictedCount == 1) { Assert.Equal(K(1), evicted[0]); Assert.Null(cache.Get(K(1))); }
    }

    [Theory]
    [InlineData(4999, true)]
    [InlineData(5000, false)]
    [InlineData(5001, false)]
    public void Ttl_boundary_controls_selectability(int ageMs, bool canSelect)
    {
        var now = T0;
        var cache = new TabSnapshotCache(() => now);
        cache.Put(Snap(K(1), T0));
        now = T0.AddMilliseconds(ageMs);
        var got = cache.Get(K(1));
        Assert.NotNull(got);                       // stale entry still displayable
        Assert.Equal(canSelect, got!.CanSelect);
    }

    [Fact]
    public void Fresh_snapshot_confirms_stale_entry()
    {
        var now = T0.AddMilliseconds(6000);
        var cache = new TabSnapshotCache(() => now);
        cache.Put(Snap(K(1), T0));
        Assert.False(cache.Get(K(1))!.CanSelect);
        cache.Put(Snap(K(1), now));
        Assert.True(cache.Get(K(1))!.CanSelect);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Reput_refreshes_recency_for_eviction()
    {
        var cache = new TabSnapshotCache(() => T0);
        for (int i = 1; i <= 8; i++) cache.Put(Snap(K(i), T0));
        cache.Put(Snap(K(1), T0));
        var evicted = cache.Put(Snap(K(9), T0));
        Assert.Equal(new[] { K(2) }, evicted);
    }

    [Fact]
    public void Invalidate_and_clear()
    {
        var cache = new TabSnapshotCache(() => T0);
        cache.Put(Snap(K(1), T0)); cache.Put(Snap(K(2), T0));
        Assert.True(cache.Invalidate(K(1)));
        Assert.False(cache.Invalidate(K(1)));
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Registry_invalidations_drop_cache_entries()
    {
        var registry = new WindowRegistry();
        registry.Update(new[] { ChromeWindowFilterTests.W(1) }, 0);
        var key = registry.Entries[0].Key;
        var cache = new TabSnapshotCache(() => T0);
        cache.Put(Snap(key, T0));
        registry.Update(Array.Empty<WindowObservation>(), 0);
        registry.ApplyInvalidations(cache);
        Assert.Null(cache.Get(key));
    }
}
