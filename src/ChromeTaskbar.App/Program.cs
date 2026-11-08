using System.IO;
using System.Windows;

namespace ChromeTaskbar.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // One bar per user session: a second launch brings the running bar to the front and exits.
        using var instance = new System.Threading.Mutex(true, SingleInstance.MutexName, out bool first);
        if (!first)
        {
            SingleInstance.ActivateExisting();
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(WorkerLocator.Find(args));
        RestorePlacement(window);
        // Saving only on close would lose the placement on shutdown / forced exit, so also save after move/resize.
        void SavePlacement() => WindowPlacementStore.Save(WindowPlacementStore.DefaultPath,
            window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds);
        window.Closing += (_, _) => SavePlacement();
        window.MoveSizeEnded += SavePlacement;
        app.SessionEnding += (_, _) => SavePlacement();
        return app.Run(window);
    }

    /// <summary>Restores the last saved placement; keeps the default when it would be off-screen.</summary>
    private static void RestorePlacement(Window window)
    {
        if (WindowPlacementStore.Load(WindowPlacementStore.DefaultPath) is not { } r) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!WindowPlacementStore.IsVisible(r, screen)) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = r.Left;
        window.Top = r.Top;
        window.Width = r.Width;
        window.Height = r.Height;
    }
}

public static class SingleInstance
{
    // Local\ = per logon session, so other users on the same machine can run their own bar.
    public const string MutexName = @"Local\ChromeTaskbar.App.SingleInstance";

    public static void ActivateExisting()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
            using (p)
            {
                if (p.Id == self.Id || p.SessionId != self.SessionId || p.MainWindowHandle == 0) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
    }

    private const int SW_RESTORE = 9;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}

public static class WorkerLocator
{
    private const string WorkerExe = "ChromeTaskbar.UiaWorker.exe";

    /// <summary>--worker &lt;path&gt;, else next to the App, else the sibling project's build output (dev layout).</summary>
    public static string Find(string[] args)
    {
        int i = Array.IndexOf(args, "--worker");
        if (i >= 0 && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        var beside = Path.Combine(AppContext.BaseDirectory, WorkerExe);
        if (File.Exists(beside)) return beside;
        // <repo>/src/ChromeTaskbar.App/bin/<cfg>/<tfm>/ -> <repo>/src/ChromeTaskbar.UiaWorker/bin/<cfg>/<tfm>/
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var cfg = tfm.Parent ?? throw new FileNotFoundException(WorkerExe);
        var src = cfg.Parent?.Parent?.Parent ?? throw new FileNotFoundException(WorkerExe);
        var dev = Path.Combine(src.FullName, "ChromeTaskbar.UiaWorker", "bin", cfg.Name, tfm.Name, WorkerExe);
        return File.Exists(dev) ? dev : throw new FileNotFoundException("UIA worker not found; pass --worker <path>.", dev);
    }
}
