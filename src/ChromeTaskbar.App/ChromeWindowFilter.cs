namespace ChromeTaskbar.App;

/// <summary>Raw observation of a top-level window.</summary>
public sealed record WindowObservation(
    nint Hwnd,
    string ClassName,
    string Title,
    bool IsVisible,
    bool HasOwner,
    bool IsToolWindow,
    bool IsCloaked,
    bool IsMinimized,
    int ProcessId,
    long ProcessStartTimeTicks,
    string? ExecutableName);

public static class ChromeWindowFilter
{
    public const string ChromeClassName = "Chrome_WidgetWin_1";
    public const string ChromeExecutable = "chrome.exe";

    public static bool IsChromeMainWindow(WindowObservation w)
    {
        if (!w.IsVisible || w.HasOwner || w.IsToolWindow || w.IsCloaked) return false;
        if (!string.Equals(w.ClassName, ChromeClassName, StringComparison.Ordinal)) return false;
        // Process info unavailable => not adopted (no guessing from class/title).
        if (w.ProcessId <= 0 || w.ProcessStartTimeTicks <= 0 || string.IsNullOrEmpty(w.ExecutableName)) return false;
        return string.Equals(w.ExecutableName, ChromeExecutable, StringComparison.OrdinalIgnoreCase);
    }
}
