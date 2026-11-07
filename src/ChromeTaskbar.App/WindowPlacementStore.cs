using System.IO;
using System.Text.Json;
using System.Windows;

namespace ChromeTaskbar.App;

/// <summary>
/// Saves / restores the bar's position and size (DIP) in %APPDATA%\ChromeTaskbar\window.json (ported from
/// vscode-selector). Never throws: a missing, corrupt or unwritable file just means "use the default placement".
/// </summary>
public static class WindowPlacementStore
{
    // Restore only when the saved rect overlaps the current virtual screen by at least this much (monitor removed etc.).
    public const double MinVisible = 40;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChromeTaskbar", "window.json");

    public static Rect? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var r = new Rect(
                root.GetProperty("left").GetDouble(), root.GetProperty("top").GetDouble(),
                root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble());
            return IsFinite(r) && r.Width > 0 && r.Height > 0 ? r : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"window placement read failed ({path}): {ex.Message}");
            return null;
        }
    }

    public static void Save(string path, Rect r)
    {
        if (!IsFinite(r) || r.Width <= 0 || r.Height <= 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { left = r.Left, top = r.Top, width = r.Width, height = r.Height }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"window placement write failed ({path}): {ex.Message}");
        }
    }

    /// <summary>
    /// Whether the saved rect still overlaps the current virtual screen enough to be reachable. The required overlap
    /// is capped by the rect's own size: a one-row bar (34 DIP tall) fully on screen must still count as visible.
    /// </summary>
    public static bool IsVisible(Rect r, Rect screen)
    {
        var i = Rect.Intersect(r, screen);
        return !i.IsEmpty && i.Width >= Math.Min(MinVisible, r.Width) && i.Height >= Math.Min(MinVisible, r.Height);
    }

    private static bool IsFinite(Rect r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
