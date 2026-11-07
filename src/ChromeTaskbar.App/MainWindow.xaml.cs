using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

/// <summary>
/// Plan-external host glue (the plan lists only MainWindow.xaml for T007): WPF routed pointer events and
/// DispatcherTimer cannot be wired from XAML alone. All behaviour lives in the existing classes; this only forwards.
/// </summary>
public partial class MainWindow : Window
{
    private const long TickMs = 50;
    private const int VK_ESCAPE = 0x1B, WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201;

    private delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private readonly string _workerPath;
    private readonly Win32WindowEnumerator _enumerator = new();
    private readonly WindowRegistry _registry = new();
    private readonly UiaWorkerBroker _broker = new();
    private readonly HoverController _hover = new();
    private readonly WindowActivator _activator;
    private readonly TaskbarViewModel _vm;
    private readonly WinEventCoalescer _coalescer = new();
    private readonly WindowPollScheduler _poll = new(MonotonicClock.NowMs);
    private readonly PopupRefreshScheduler _popupRefresh = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    private readonly WinEventProc _winEventProc; // kept alive for the native hooks
    private readonly List<nint> _hooks = new();
    private readonly Dictionary<WindowKey, FrameworkElement> _anchors = new();
    private nint _lastExternalForeground;
    private readonly HookProc _mouseProc; // kept alive for the native hook
    private nint _mouseHook;
    private bool _prevEscape;
    private TabPopupViewModel? _popup;
    private readonly IconRefreshScheduler _iconRefresh = new();
    private bool _brokerStarted, _iconReadInFlight;
    private CancellationTokenSource? _popupCts;

    public MainWindow(string workerPath)
    {
        InitializeComponent();
        _workerPath = workerPath;
        _activator = new WindowActivator(_enumerator, new Win32WindowOps());
        _vm = new TaskbarViewModel(_hover, () => MonotonicClock.NowMs, Toggle);
        DataContext = _vm;
        _winEventProc = OnWinEvent;
        _mouseProc = OnMouseHook;
        _hover.Raised += OnHover;
        _timer.Tick += (_, _) => OnTick();
        Root.SizeChanged += (_, e) => PlaceGripBar(e.NewSize);
        // Stretch cells to the list width so no gap remains on the right.
        ListScroll.SizeChanged += (_, e) => ButtonsList.Tag = BarLayout.FillCellWidth(e.NewSize.Width);
        _vm.Buttons.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add) ScheduleGrowToFit();
        };
        StateChanged += (_, _) => ScheduleGrowToFit();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Shutdown();
    }

    private async Task StartAsync()
    {
        // Window listing does not depend on UIA: start it even when the worker cannot be launched.
        RefreshWindows();
        // WINEVENT_OUTOFCONTEXT (0): foreground, minimize start/end, destroy..namechange.
        foreach (var (min, max) in new[] { (0x3u, 0x3u), (0x16u, 0x17u), (0x8001u, 0x800Cu) })
        {
            var h = SetWinEventHook(min, max, 0, _winEventProc, 0, 0, 0);
            if (h != 0) _hooks.Add(h); // 0 = registration failed; nothing to unhook later
        }
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, GetModuleHandleW(null), 0);
        _timer.Start();
        try { await _broker.StartAsync(_workerPath, CancellationToken.None); _brokerStarted = true; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or Protocol.ProtocolException)
        {
            Title = $"Chrome Taskbar - UIA worker unavailable: {ex.Message}";
        }
    }

    private void OnWinEvent(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev == 0x3 && hwnd != new WindowInteropHelper(this).Handle) _lastExternalForeground = hwnd;
        if (idObject == 0 && idChild == 0) _coalescer.Notify(MonotonicClock.NowMs);
    }

    private void OnTick()
    {
        long now = MonotonicClock.NowMs;
        _hover.Tick(now);
        if (_coalescer.Poll(now) is not null || _poll.Poll(now, WindowState == WindowState.Minimized) is not null) RefreshWindows();
        if (_popup is null) PumpIconRead(now);
        if (_popup is not null)
        {
            PollDismissals(now);
            if (_popup is { } p && _popupRefresh.Poll(now) is not null) _ = LoadPopupAsync(p);
        }
    }

    /// <summary>
    /// Background read of one window's tabs (for its identicon) at a time, only while no popup is open and hover is
    /// idle so it never delays hover prefetch / popup reads (the broker keeps a single pending read).
    /// </summary>
    private void PumpIconRead(long now)
    {
        if (!_brokerStarted || _iconReadInFlight || _hover.State != HoverState.Idle) return;
        if (_iconRefresh.Next(_vm.Buttons.Select(b => b.Key).ToList(), now) is not { } key) return;
        _iconRefresh.MarkAttempted(key, now);
        _iconReadInFlight = true;
        _ = ReadIconAsync(key);
    }

    private async Task ReadIconAsync(WindowKey key)
    {
        try { await _broker.ReadTabsAsync(key, _broker.PopupGeneration); }
        catch (ObjectDisposedException) { return; } // broker disposed during shutdown
        finally { _iconReadInFlight = false; }
        RefreshWindows();
    }

    private void RefreshWindows()
    {
        var entries = _registry.Update(_enumerator.EnumerateTopLevel(), _enumerator.GetForegroundWindow());
        _registry.ApplyInvalidations(_broker.Cache);
        _vm.Update(entries, _broker.Cache.Get);
    }

    private void Toggle(WindowKey key)
    {
        var r = _activator.Toggle(key, _lastExternalForeground == key.Hwnd);
        if (!r.IsSuccess && !r.IsPending) Title = $"Chrome Taskbar - {r.Outcome}: {r.Detail}";
        RefreshWindows();
    }

    // ---- chrome (left grip) -------------------------------------------------------

    private void OnGripMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private const int GWL_STYLE = -16, WS_MAXIMIZEBOX = 0x00010000, WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232;
    private const uint SWP_FRAMECHANGED_ONLY = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint h, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    private bool _resizing;

    /// <summary>Raised after a drag-move or resize ends (after the post-resize fit).</summary>
    public event Action? MoveSizeEnded;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // No maximize: caption double-click / Win+Up must not maximize the borderless bar.
        nint hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) & ~WS_MAXIMIZEBOX);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_FRAMECHANGED_ONLY);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_SIZING) _resizing = true;
        else if (msg == WM_EXITSIZEMOVE)
        {
            if (_resizing)
            {
                _resizing = false;
                ScheduleFit(snap: true);
            }
            // Lower priority than the fit (Loaded) so the notified size includes the snap.
            Dispatcher.BeginInvoke(() => MoveSizeEnded?.Invoke(), DispatcherPriority.Background);
        }
        return 0;
    }

    private void ScheduleGrowToFit() => ScheduleFit(snap: false);

    /// <summary>Run after layout so the final size and newly added buttons are measured. Skipped while minimized
    /// (StateChanged re-runs it on restore).</summary>
    private void ScheduleFit(bool snap)
        => Dispatcher.BeginInvoke(() =>
        {
            if (WindowState == WindowState.Normal && IsVisible) FitButtons(snap);
        }, DispatcherPriority.Loaded);

    /// <summary>
    /// snap (after a manual resize): resize to exactly the cells the buttons need, keeping the user's rows/columns.
    /// Otherwise (buttons added / restored): grow the long side only when buttons overflow. Clamped to the screen edge.
    /// </summary>
    private void FitButtons(bool snap)
    {
        var list = new Size(ListScroll.ActualWidth, ListScroll.ActualHeight);
        var fit = snap ? BarLayout.SnapListSize(list, _vm.Buttons.Count) : BarLayout.FitListSize(list, _vm.Buttons.Count);
        if (fit == list) return;
        var size = BarLayout.WindowSizeFor(fit);
        double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Width = Math.Max(MinWidth, Math.Min(size.Width, right - Left));
        Height = Math.Max(MinHeight, Math.Min(size.Height, bottom - Top));
    }

    private System.Windows.Controls.Primitives.ButtonBase? _dragButton;
    private Point _dragStart;

    /// <summary>Drag-move from the list area. ScrollViewer marks clicks handled, so use Preview. Empty space moves
    /// immediately; on a button the move starts only past the system drag threshold (see OnContentMouseMove) so a
    /// plain press still clicks.</summary>
    private void OnContentMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragButton = null;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender;
             d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase b)
            {
                _dragButton = b;
                _dragStart = e.GetPosition(this);
                return;
            }
        }
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void OnContentMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragButton is not { } button) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) { _dragButton = null; return; }
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragButton = null;
        // Releasing capture un-presses the button without raising Click; the hover popup is dismissed like a click.
        button.ReleaseMouseCapture();
        _hover.ButtonClick(MonotonicClock.NowMs);
        DragMove();
    }

    /// <summary>Grip on the short side: landscape -> left vertical bar, portrait -> top horizontal bar.</summary>
    private void PlaceGripBar(Size size)
    {
        bool landscape = BarLayout.IsLandscape(size);
        DockPanel.SetDock(GripBar, landscape ? Dock.Left : Dock.Top);
        GripBar.Width = landscape ? BarLayout.GripSize : double.NaN;
        GripBar.Height = landscape ? double.NaN : BarLayout.GripSize;
        CloseButton.HorizontalAlignment = landscape ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        CloseButton.VerticalAlignment = landscape ? VerticalAlignment.Top : VerticalAlignment.Center;
        MinWidth = landscape ? BarLayout.MinWidthWithSideBar : BarLayout.MinWidth;
    }

    // ---- pointer ----------------------------------------------------------------

    private static WindowKey? KeyOf(object sender) => (sender as FrameworkElement)?.DataContext is WindowButtonViewModel b ? b.Key : null;

    private void OnButtonEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (KeyOf(sender) is not { } key) return;
        _anchors[key] = (FrameworkElement)sender;
        _hover.ButtonEnter(key, MonotonicClock.NowMs);
    }

    private void OnButtonLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (KeyOf(sender) is { } key) _hover.ButtonLeave(key, MonotonicClock.NowMs);
    }

    private void OnPopupEnter(object sender, System.Windows.Input.MouseEventArgs e) => _hover.PopupEnter(MonotonicClock.NowMs);
    private void OnPopupLeave(object sender, System.Windows.Input.MouseEventArgs e) => _hover.PopupLeave(MonotonicClock.NowMs);

    // Esc is polled and outside clicks come from a low-level mouse hook because the popup never takes focus
    // or capture (hover must not transfer focus). A polled button edge would miss short clicks.
    private void PollDismissals(long now)
    {
        bool esc = GetAsyncKeyState(VK_ESCAPE) < 0;
        if (esc && !_prevEscape) _hover.Escape(now);
        _prevEscape = esc;
    }

    private nint OnMouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == WM_LBUTTONDOWN && _popup is not null)
            Dispatcher.BeginInvoke(() =>
            {
                if (_popup is { } p && !CursorInside(PopupRoot) && !CursorInside(_anchors.GetValueOrDefault(p.Key)))
                    _hover.OutsideClick(MonotonicClock.NowMs);
            });
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool CursorInside(FrameworkElement? el)
    {
        if (el is null || !el.IsLoaded || !GetCursorPos(out var p)) return false;
        var tl = el.PointToScreen(new Point(0, 0));
        var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
        return p.X >= tl.X && p.X < br.X && p.Y >= tl.Y && p.Y < br.Y;
    }

    // ---- hover events -> popup ------------------------------------------------------

    private void OnHover(HoverEvent e)
    {
        switch (e.Kind)
        {
            case HoverEventKind.Prefetch:
                _ = PrefetchAsync(e);
                break;
            case HoverEventKind.OpenPopup:
                OpenPopup(e);
                break;
            case HoverEventKind.ClosePopup:
                ClosePopup();
                break;
        }
    }

    private async Task PrefetchAsync(HoverEvent e)
    {
        try { await _broker.ReadTabsAsync(e.Key, e.Generation); }
        catch (ObjectDisposedException) { return; } // broker disposed during shutdown
        RefreshWindows();
    }

    private void OpenPopup(HoverEvent e)
    {
        ClosePopup();
        _broker.PopupGeneration = e.Generation;
        var cts = _popupCts = new CancellationTokenSource();
        var popup = new TabPopupViewModel(e.Key, e.Generation,
            (k, sid, tok, gen) => _broker.SelectTabAsync(k, sid, tok, gen, cts.Token),
            _activator.Activate, _activator.IsForegroundConfirmed);
        _popup = popup;
        TabPopup.PlacementTarget = _anchors.GetValueOrDefault(e.Key);
        TabPopup.IsOpen = true;
        _popupRefresh.Start(e.TickMs);
        ShowPopup(popup);
        _ = LoadPopupAsync(popup);
    }

    private async Task LoadPopupAsync(TabPopupViewModel popup)
    {
        long id = popup.BeginRequest();
        BrokerResult result;
        try { result = await _broker.ReadTabsAsync(popup.Key, popup.Generation, _popupCts?.Token ?? CancellationToken.None); }
        catch (ObjectDisposedException) { return; } // broker disposed during shutdown
        if (ReferenceEquals(_popup, popup) && popup.Apply(id, result)) ShowPopup(popup);
    }

    private void ShowPopup(TabPopupViewModel popup)
    {
        TabList.ItemsSource = popup.Tabs;
        PopupStatus.Text = PopupStatusText.Compose(popup.StatusText, popup.SelectionMessage);
    }

    private void ClosePopup()
    {
        TabPopup.IsOpen = false;
        _popupRefresh.Stop();
        _popupCts?.Cancel();
        _popupCts?.Dispose();
        _popupCts = null;
        _popup?.Dispose();
        _popup = null;
    }

    private async void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (_popup is not { } popup || (sender as FrameworkElement)?.DataContext is not TabItemViewModel tab) return;
        await _vm.OnTabClickAsync(popup, tab);
        if (ReferenceEquals(_popup, popup)) ShowPopup(popup);
        RefreshWindows();
    }

    private void Shutdown()
    {
        _timer.Stop();
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
        _hover.Raised -= OnHover;
        ClosePopup();
        _hover.Dispose();
        _vm.Dispose();
        _broker.Dispose();
    }
}
