using System.IO;
using System.Text.Json;

namespace ChromeTaskbar.App;

/// <summary>
/// A window as saved for the bar order. The identity is valid while that Chrome process lives; after Chrome restarts,
/// <paramref name="IconTitle"/> (leftmost tab title) matches the restored window instead.
/// </summary>
public sealed record SavedWindow(long Hwnd, int ProcessId, long ProcessStartTimeTicks, string? IconTitle = null);

/// <summary>
/// Saves / restores the button order in %APPDATA%\ChromeTaskbar\order.json so the bar keeps its order across app
/// restarts. Never throws: a missing, corrupt or unwritable file just means "first-seen order".
/// </summary>
public static class WindowOrderStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChromeTaskbar", "order.json");

    public static IReadOnlyList<SavedWindow> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<SavedWindow>();
            return JsonSerializer.Deserialize<List<SavedWindow>>(File.ReadAllText(path)) ?? new List<SavedWindow>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"window order read failed ({path}): {ex.Message}");
            return Array.Empty<SavedWindow>();
        }
    }

    public static void Save(string path, IReadOnlyList<SavedWindow> order)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(order));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"window order write failed ({path}): {ex.Message}");
        }
    }
}
