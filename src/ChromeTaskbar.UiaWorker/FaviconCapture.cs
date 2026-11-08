using System.Runtime.InteropServices;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.UiaWorker;

/// <summary>
/// Cuts each tab's favicon out of one PrintWindow capture. UIA exposes no favicon image, so the window is rendered
/// (PW_RENDERFULLCONTENT: works while occluded, never for minimized windows) and each icon is located near its tab's left
/// edge. Never activates or switches tabs. Coordinates are physical pixels (the worker is per-monitor DPI aware).
/// </summary>
public static class FaviconCapture
{
    public const int DiffThreshold = 60;      // sum of |dB|+|dG|+|dR| that counts as "not tab background"
    public const int TransparentThreshold = 12;
    private const int MaxCaptureSide = 8192;

    /// <summary>One icon (or null) per screen rectangle, in order. All null for a minimized window or a failed capture.</summary>
    public static TabIcon?[] TryCapture(nint hwnd, IReadOnlyList<System.Windows.Rect> tabs)
    {
        var icons = new TabIcon?[tabs.Count];
        try
        {
            if (tabs.Count == 0 || IsIconic(hwnd) || !GetWindowRect(hwnd, out var win)) return icons;
            var dpi = GetDpiForWindow(hwnd);
            var scale = (dpi == 0 ? 96 : dpi) / 96.0;
            int winW = win.Right - win.Left, winH = win.Bottom - win.Top;
            var local = new (int L, int T, int W, int H)?[tabs.Count];
            int width = 0, height = 0;
            for (int i = 0; i < tabs.Count; i++)
            {
                var r = tabs[i];
                if (r.IsEmpty || !(r.Width >= 4 && r.Height >= 4)) continue;
                int l = (int)Math.Round(r.X) - win.Left, t = (int)Math.Round(r.Y) - win.Top, w = (int)Math.Round(r.Width), h = (int)Math.Round(r.Height);
                // A tab rectangle may poke a pixel outside the window rectangle; clip it instead of giving up.
                if (l < 0) { w += l; l = 0; }
                if (t < 0) { h += t; t = 0; }
                w = Math.Min(w, winW - l); h = Math.Min(h, winH - t);
                if (w < 4 || h < 4) continue;
                local[i] = (l, t, w, h);
                width = Math.Max(width, l + w); height = Math.Max(height, t + h);
            }
            if (width == 0 || width > MaxCaptureSide || height > MaxCaptureSide) return icons;
            var pixels = Capture(hwnd, width, height);
            if (pixels is null) return icons;
            int budget = Protocol.MaxIconBytesPerFrame;
            for (int i = 0; i < tabs.Count && budget > 0; i++)
                if (local[i] is { } r && Locate(pixels, width, height, r.L, r.T, r.W, r.H, scale) is { } icon)
                {
                    icons[i] = icon;
                    budget -= icon.Bgra.Length;
                }
        }
        catch (Exception e) when (e is ExternalException or OutOfMemoryException or OverflowException) { }
        return icons;
    }

    /// <summary>
    /// Finds the favicon (16 DIP square, vertically centred) in the band from 8 to 40 DIP right of the tab's left edge.
    /// Background = most frequent colour of the band; columns holding other colours are grouped into runs (gaps up to
    /// 3 DIP). Runs touching the band's ends are tab shape / title text (e.g. the selected tab's curved edge) and are
    /// skipped; the first remaining run no wider than a favicon is the icon. Null when there is none.
    /// </summary>
    public static TabIcon? Locate(byte[] bgra, int width, int height, int tabL, int tabT, int tabW, int tabH, double scale)
    {
        int size = Math.Clamp((int)Math.Round(16 * scale), 8, Protocol.MaxIconSide);
        int cy = tabT + tabH / 2, reach = (int)Math.Round(12 * scale);
        int x0 = tabL + (int)Math.Round(8 * scale), x1 = Math.Min(Math.Min(width, tabL + tabW), tabL + (int)Math.Round(40 * scale));
        int y0 = Math.Max(Math.Max(0, tabT), cy - reach), y1 = Math.Min(Math.Min(height, tabT + tabH), cy + reach);
        if (x1 - x0 < size || y1 - y0 < size) return null;

        var counts = new Dictionary<int, int>();
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int c = Rgb(bgra, (y * width + x) * 4);
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        int bg = counts.MaxBy(p => p.Value).Key;

        var columnHits = new int[x1 - x0];
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                if (Distance(Rgb(bgra, (y * width + x) * 4), bg) > DiffThreshold) columnHits[x - x0]++;

        int slack = Math.Max(2, (int)Math.Round(2 * scale)), gap = Math.Max(2, (int)Math.Round(3 * scale));
        int minX = -1, maxX = -1;
        for (int i = 0; i < columnHits.Length;)
        {
            if (columnHits[i] == 0) { i++; continue; }
            int start = i, end = i;
            for (int j = i + 1; j < columnHits.Length && j - end <= gap; j++) if (columnHits[j] > 0) end = j;
            i = end + 1;
            if (start == 0 || end == columnHits.Length - 1 || end - start + 1 > size + slack) continue;
            minX = x0 + start; maxX = x0 + end;
            break;
        }
        if (minX < 0) return null;

        int minY = int.MaxValue, maxY = -1, hits = 0;
        for (int y = y0; y < y1; y++)
            for (int x = minX; x <= maxX; x++)
                if (Distance(Rgb(bgra, (y * width + x) * 4), bg) > DiffThreshold) { hits++; minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (hits < 4 || maxY - minY + 1 > size + slack) return null;

        int left = Math.Clamp((minX + maxX + 1 - size) / 2, 0, width - size), top = Math.Clamp((minY + maxY + 1 - size) / 2, 0, height - size);
        var icon = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int s = ((top + y) * width + left + x) * 4, d = (y * size + x) * 4;
                icon[d] = bgra[s]; icon[d + 1] = bgra[s + 1]; icon[d + 2] = bgra[s + 2];
                icon[d + 3] = Distance(Rgb(bgra, s), bg) <= TransparentThreshold ? (byte)0 : (byte)255;
            }
        return new TabIcon(size, size, icon);
    }

    private static int Rgb(byte[] p, int i) => p[i] | p[i + 1] << 8 | p[i + 2] << 16;
    private static int Distance(int a, int b) =>
        Math.Abs((a & 0xFF) - (b & 0xFF)) + Math.Abs((a >> 8 & 0xFF) - (b >> 8 & 0xFF)) + Math.Abs((a >> 16 & 0xFF) - (b >> 16 & 0xFF));

    /// <summary>Renders the window's top-left <paramref name="width"/> x <paramref name="height"/> into a top-down 32bpp DIB.</summary>
    private static byte[]? Capture(nint hwnd, int width, int height)
    {
        nint screen = GetDC(0), mem = CreateCompatibleDC(screen), bits;
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        nint bitmap = CreateDIBSection(screen, ref info, 0, out bits, 0, 0);
        ReleaseDC(0, screen);
        try
        {
            if (mem == 0 || bitmap == 0) return null;
            nint old = SelectObject(mem, bitmap);
            try
            {
                if (!PrintWindow(hwnd, mem, PwRenderFullContent)) return null;
                GdiFlush();
                var buffer = new byte[width * height * 4];
                Marshal.Copy(bits, buffer, 0, buffer.Length);
                return buffer;
            }
            finally { SelectObject(mem, old); }
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (mem != 0) DeleteDC(mem);
        }
    }

    private const uint PwRenderFullContent = 2;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
