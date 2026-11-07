using System.Windows;
using System.Windows.Media;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

/// <summary>
/// Symmetric 5x5 identicon generated from a name (ported from vscode-selector's NameDecor). Deterministic across
/// processes: uses FNV-1a instead of string.GetHashCode (randomized per process).
/// </summary>
public static class Identicon
{
    public const int Cells = 5;

    /// <summary>FNV-1a 64bit followed by the SplitMix64 finalizer to spread low bits.</summary>
    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        h ^= h >> 33; h *= 0xff51afd7ed558ccdUL;
        h ^= h >> 33; h *= 0xc4ceb9fe1a85ec53UL;
        return h ^ (h >> 33);
    }

    /// <summary>Hue comes from the upper bits, independent of the pattern bits.</summary>
    public static double Hue(string name) => (Hash(name) >> 48) % 360;

    /// <summary>Left 3 columns (15 bits) from the hash; the right 2 columns mirror them.</summary>
    public static bool[,] Pattern(string name)
    {
        ulong h = Hash(name);
        var cells = new bool[Cells, Cells];
        int bit = 0;
        for (int x = 0; x < (Cells + 1) / 2; x++)
            for (int y = 0; y < Cells; y++)
            {
                bool on = (h >> bit++ & 1) == 1;
                cells[y, x] = on;
                cells[y, Cells - 1 - x] = on;
            }
        return cells;
    }

    public static Color AccentColor(string name, double lightness = 0.42) => FromHsl(Hue(name), 0.65, lightness);

    private static readonly Dictionary<string, ImageSource> Cache = new();
    private const int CacheLimit = 256;

    /// <summary>Frozen image, cached per name (UI thread only).</summary>
    public static ImageSource Image(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var fg = new SolidColorBrush(AccentColor(name, 0.5));
        fg.Freeze();
        var bg = new SolidColorBrush(AccentColor(name, 0.93));
        bg.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(bg, null, new RectangleGeometry(new Rect(0, 0, Cells + 1, Cells + 1))));
        var cells = new GeometryGroup();
        var p = Pattern(name);
        for (int y = 0; y < Cells; y++)
            for (int x = 0; x < Cells; x++)
                if (p[y, x]) cells.Children.Add(new RectangleGeometry(new Rect(x + 0.5, y + 0.5, 1, 1)));
        group.Children.Add(new GeometryDrawing(fg, null, cells));
        var img = new DrawingImage(group);
        img.Freeze();
        if (Cache.Count >= CacheLimit) Cache.Clear(); // tab titles churn; keep memory bounded
        return Cache[name] = img;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double hp = h / 60;
        double x = c * (1 - Math.Abs(hp % 2 - 1));
        (double r, double g, double b) = hp switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        double m = l - c / 2;
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>
/// Picks the next window whose leftmost-tab title should be (re)read in the background: never-attempted windows first,
/// then the oldest attempt older than <see cref="IntervalMs"/>. Failures are therefore retried at the interval too.
/// </summary>
public sealed class IconRefreshScheduler
{
    public const long IntervalMs = 10000;
    private readonly Dictionary<WindowKey, long> _attempted = new();

    public WindowKey? Next(IReadOnlyList<WindowKey> live, long now)
    {
        foreach (var gone in _attempted.Keys.Where(k => !live.Contains(k)).ToList()) _attempted.Remove(gone);
        WindowKey? best = null;
        long bestAt = long.MaxValue;
        foreach (var k in live)
        {
            if (!_attempted.TryGetValue(k, out var at)) return k;
            if (now - at >= IntervalMs && at < bestAt) { best = k; bestAt = at; }
        }
        return best;
    }

    public void MarkAttempted(WindowKey key, long now) => _attempted[key] = now;
}
