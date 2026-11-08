using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

/// <summary>
/// Last captured favicon per window and tab title (UI thread only). A read without a capture (minimized window, collapsed
/// group) keeps the previous icon of a tab that is still open; titles that disappeared from the window are dropped.
/// </summary>
public sealed class FaviconStore
{
    private readonly Dictionary<WindowKey, Dictionary<string, ImageSource>> _byWindow = new();
    private readonly ConditionalWeakTable<TabIcon, ImageSource> _images = new();
    private readonly ConditionalWeakTable<TabSnapshot, object> _applied = new();

    public void Update(TabSnapshot snapshot)
    {
        if (snapshot.Availability != TabAvailability.Available || _applied.TryGetValue(snapshot, out _)) return;
        _applied.Add(snapshot, snapshot);
        var old = _byWindow.GetValueOrDefault(snapshot.WindowKey);
        var next = new Dictionary<string, ImageSource>();
        foreach (var tab in snapshot.Tabs)
        {
            if (tab.Icon is { } icon) next[tab.Title] = _images.GetValue(icon, ToImage);
            else if (!next.ContainsKey(tab.Title) && old?.GetValueOrDefault(tab.Title) is { } kept) next[tab.Title] = kept;
        }
        _byWindow[snapshot.WindowKey] = next;
    }

    public ImageSource? Get(WindowKey key, string title) => _byWindow.GetValueOrDefault(key)?.GetValueOrDefault(title);
    public void Remove(WindowKey key) => _byWindow.Remove(key);
    public void Clear() => _byWindow.Clear();

    public static ImageSource ToImage(TabIcon icon)
    {
        var bmp = BitmapSource.Create(icon.Width, icon.Height, 96, 96, PixelFormats.Bgra32, null, icon.Bgra, icon.Width * 4);
        bmp.Freeze();
        return bmp;
    }
}
