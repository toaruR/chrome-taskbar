using System.Runtime.InteropServices;
using System.Text;

namespace ChromeTaskbar.App;

public interface IWindowEnumerator
{
    IReadOnlyList<WindowObservation> EnumerateTopLevel();
    WindowObservation? Observe(nint hwnd);
    nint GetForegroundWindow();
}

public sealed class Win32WindowEnumerator : IWindowEnumerator
{
    private delegate bool EnumProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint GetFg();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(nint h, uint flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(nint h, out long create, out long exit, out long kernel, out long user);

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public nint GetForegroundWindow() => GetFg();

    public IReadOnlyList<WindowObservation> EnumerateTopLevel()
    {
        var list = new List<WindowObservation>();
        EnumWindows((h, _) =>
        {
            // Cheap pre-filter before process queries.
            if (IsWindowVisible(h) && ClassOf(h) == ChromeWindowFilter.ChromeClassName)
            {
                var o = Observe(h);
                if (o is not null) list.Add(o);
            }
            return true;
        }, 0);
        return list;
    }

    public WindowObservation? Observe(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        var (exe, start) = ProcessInfo(pid);
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var c, sizeof(int)) == 0) cloaked = c;
        return new WindowObservation(
            hwnd, ClassOf(hwnd), TitleOf(hwnd), IsWindowVisible(hwnd),
            GetWindow(hwnd, GW_OWNER) != 0, (ex & WS_EX_TOOLWINDOW) != 0, cloaked != 0,
            IsIconic(hwnd), (int)pid, start, exe);
    }

    private static string ClassOf(nint h)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string TitleOf(nint h)
    {
        int len = GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static (string? exe, long startTicks) ProcessInfo(uint pid)
    {
        if (pid == 0) return (null, 0);
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return (null, 0);
        try
        {
            string? exe = null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                exe = System.IO.Path.GetFileName(sb.ToString(0, size));
            long start = GetProcessTimes(h, out var create, out _, out _, out _) ? create : 0;
            return (exe, start);
        }
        finally { CloseHandle(h); }
    }
}
