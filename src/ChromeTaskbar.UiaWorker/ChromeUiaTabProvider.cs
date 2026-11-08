using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.UiaWorker;

public sealed class ChromeUiaTabProvider
{
    public const int TraversalNodes = 1000;
    public const int TraversalDepth = 12;
    public static readonly TimeSpan TraversalTime = TimeSpan.FromMilliseconds(750);
    private readonly Dictionary<string, TabReference> tabs = new(StringComparer.Ordinal);
    private WorkerEpoch epoch;
    private SnapshotId? currentSnapshotId;

    public ChromeUiaTabProvider(WorkerEpoch epoch) => this.epoch = epoch;

    public static TabAvailability AvailabilityForLimits(int visited, int depth, TimeSpan elapsed, int tabs, int titleUnits) =>
        Protocol.ProbeAvailabilityFor(visited, depth, elapsed, tabs, titleUnits);

    public TabSnapshot ReadTabs(WindowKey key, DateTimeOffset deadlineUtc)
    {
        if (DateTimeOffset.UtcNow >= deadlineUtc) return Empty(key, TabAvailability.Unavailable);
        tabs.Clear();
        var timer = Stopwatch.StartNew();
        try
        {
            // RawView has several times more nodes than ControlView; cache the properties we read to keep cross-process calls within the time limit.
            var cache = new CacheRequest { TreeScope = TreeScope.Element };
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.RuntimeIdProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            using var _ = cache.Activate();
            var queue = new Queue<(AutomationElement Element, int Depth, AutomationElement? Parent)>();
            queue.Enqueue((AutomationElement.FromHandle(key.Hwnd), 0, null));
            AutomationElement? strip = null;
            var rects = new List<System.Windows.Rect>();
            var entries = new List<TabEntry>();
            var visited = 0;
            while (queue.Count > 0)
            {
                var (element, depth, parent) = queue.Dequeue();
                if (AvailabilityForLimits(++visited, depth, timer.Elapsed, entries.Count, 0) == TabAvailability.Incomplete || DateTimeOffset.UtcNow >= deadlineUtc)
                    return Empty(key, TabAvailability.Incomplete);
                var control = element.Cached.ControlType;
                if (control == ControlType.Document) continue;
                // RawView also exposes hidden Tab containers (e.g. vertical tab bar); the strip is the Tab that directly parents a TabItem.
                if (control == ControlType.TabItem && parent is not null && parent.Cached.ControlType == ControlType.Tab && (strip is null || parent.Equals(strip)))
                {
                    strip ??= parent;
                    var title = element.Cached.Name ?? string.Empty;
                    if (AvailabilityForLimits(visited, depth, timer.Elapsed, entries.Count + 1, title.Length) == TabAvailability.Incomplete) return Empty(key, TabAvailability.Incomplete);
                    var token = NewToken(); var runtimeId = element.GetRuntimeId();
                    var canSelect = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern);
                    var selected = canSelect && ((SelectionItemPattern)pattern).Current.IsSelected;
                    rects.Add(CachedBounds(element));
                    tabs[token] = new TabReference(key, strip, element, runtimeId);
                    entries.Add(new TabEntry(new TabToken(token), title, entries.Count, selected, canSelect));
                }
                if (depth < TraversalDepth && !IsLeafControl(control))
                    foreach (var child in Children(element, e => Walker.GetFirstChild(e, cache), e => Walker.GetNextSibling(e, cache))) queue.Enqueue((child, depth + 1, element));
            }
            currentSnapshotId = new SnapshotId(Guid.NewGuid());
            if (DateTimeOffset.UtcNow < deadlineUtc)
            {
                // Best effort: a missing favicon never fails the read (the app falls back to the title identicon).
                var icons = FaviconCapture.TryCapture(key.Hwnd, rects);
                for (int i = 0; i < entries.Count; i++) if (icons[i] is { } icon) entries[i] = entries[i] with { Icon = icon };
            }
            return new TabSnapshot(epoch, key, currentSnapshotId.Value, DateTimeOffset.UtcNow, TabAvailability.Available, entries);
        }
        catch (ElementNotAvailableException) { return Empty(key, TabAvailability.Unavailable); }
        catch (COMException) { return Empty(key, TabAvailability.Unavailable); }
    }

    /// <summary>Screen rectangle (physical px); empty when the tab is not drawn (e.g. inside a collapsed group).</summary>
    private static System.Windows.Rect CachedBounds(AutomationElement element)
    {
        try { return element.Cached.BoundingRectangle; }
        catch (InvalidOperationException) { return System.Windows.Rect.Empty; }
    }

    public ResultStatus Select(WindowKey key, SnapshotId snapshotId, TabToken token, DateTimeOffset deadlineUtc)
    {
        if (DateTimeOffset.UtcNow >= deadlineUtc || currentSnapshotId != snapshotId || !tabs.TryGetValue(token.Value, out var reference) || reference.Key != key) return ResultStatus.Stale;
        try
        {
            if (!reference.Element.GetRuntimeId().SequenceEqual(reference.RuntimeId) || !IsDescendantOf(reference.Element, reference.Strip) || !reference.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) return ResultStatus.Unavailable;
            ((SelectionItemPattern)pattern).Select();
            return ((SelectionItemPattern)reference.Element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected ? ResultStatus.Success : ResultStatus.Unavailable;
        }
        catch (ElementNotAvailableException) { return ResultStatus.Unavailable; }
        catch (COMException) { return ResultStatus.Unavailable; }
    }

    public void Invalidate(WindowKey key)
    {
        foreach (var token in tabs.Where(pair => pair.Value.Key == key).Select(pair => pair.Key).ToArray()) tabs.Remove(token);
        currentSnapshotId = null;
    }

    public void ResetEpoch(WorkerEpoch nextEpoch) { epoch = nextEpoch; currentSnapshotId = null; tabs.Clear(); }
    private TabSnapshot Empty(WindowKey key, TabAvailability availability) => new(epoch, key, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UtcNow, availability, Array.Empty<TabEntry>());
    // RawView is required: Chrome omits tabs inside collapsed tab groups from ControlView. Walking never expands groups.
    public static TreeWalker Walker => TreeWalker.RawViewWalker;
    private static bool IsDescendantOf(AutomationElement element, AutomationElement ancestor) => IsDescendantOf(element, ancestor, Walker.GetParent);

    // Controls that never contain the tab strip or tabs; not descending keeps the RawView walk within limits.
    public static bool IsLeafControl(ControlType control) =>
        control == ControlType.TabItem || control == ControlType.Button || control == ControlType.Text || control == ControlType.Edit || control == ControlType.Separator || control == ControlType.Slider;

    public static IEnumerable<T> Children<T>(T parent, Func<T, T?> first, Func<T, T?> next) where T : class
    {
        for (var child = first(parent); child is not null; child = next(child)) yield return child;
    }

    public static bool IsDescendantOf<T>(T element, T ancestor, Func<T, T?> getParent) where T : class
    {
        for (var parent = getParent(element); parent is not null; parent = getParent(parent)) if (parent.Equals(ancestor)) return true;
        return false;
    }
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record TabReference(WindowKey Key, AutomationElement Strip, AutomationElement Element, int[] RuntimeId);
}
