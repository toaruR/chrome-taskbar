namespace ChromeTaskbar.App;

/// <summary>Pure composition of the popup status line (extracted from MainWindow for testability).</summary>
public static class PopupStatusText
{
    public static string Compose(string? status, string? selectionMessage) =>
        string.Join(" ", new[] { status, selectionMessage }.Where(s => !string.IsNullOrEmpty(s)));
}
