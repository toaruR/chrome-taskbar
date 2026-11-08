using System.Text.Json;
using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using ChromeTaskbar.UiaWorker;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class FaviconTests
{
    private static readonly WindowKey K1 = new(1, 10, 100, 1);
    private const int Bg = 0xDDE3E9;

    private static byte[] Canvas(int w, int h)
    {
        var p = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { p[i * 4] = Bg & 0xFF; p[i * 4 + 1] = Bg >> 8 & 0xFF; p[i * 4 + 2] = Bg >> 16 & 0xFF; p[i * 4 + 3] = 255; }
        return p;
    }

    private static void Fill(byte[] p, int w, int x0, int y0, int fw, int fh, byte b, byte g, byte r)
    {
        for (int y = y0; y < y0 + fh; y++)
            for (int x = x0; x < x0 + fw; x++) { int i = (y * w + x) * 4; p[i] = b; p[i + 1] = g; p[i + 2] = r; }
    }

    [Fact]
    public void Locate_CutsTheIconSquare_AndMakesBackgroundTransparent()
    {
        // 150% scale: tab 236x62 at (50,0); 24px icon at tab-relative x=31, y=18 (as measured on real Chrome).
        const int w = 300, h = 62;
        var p = Canvas(w, h);
        Fill(p, w, 50 + 31 + 4, 18 + 4, 16, 16, 0x80, 0xD0, 0x60);
        var icon = FaviconCapture.Locate(p, w, h, 50, 0, 236, 62, 1.5);
        Assert.NotNull(icon);
        Assert.Equal(24, icon!.Width);
        Assert.Equal(24 * 24 * 4, icon.Bgra.Length);
        Assert.Equal(255, icon.Bgra[(12 * 24 + 12) * 4 + 3]); // centre is the drawn mark
        Assert.Equal(0x80, icon.Bgra[(12 * 24 + 12) * 4]);
        Assert.Equal(0, icon.Bgra[3]);                           // corner is tab background
    }

    [Fact]
    public void Locate_ReturnsNull_ForEmptyBand_OrSomethingWiderThanAFavicon()
    {
        const int w = 300, h = 62;
        Assert.Null(FaviconCapture.Locate(Canvas(w, h), w, h, 50, 0, 236, 62, 1.5));
        var wide = Canvas(w, h);
        Fill(wide, w, 50 + 13, 20, 40, 10, 0x30, 0x30, 0x30); // e.g. title text reaching into the band
        Assert.Null(FaviconCapture.Locate(wide, w, h, 50, 0, 236, 62, 1.5));
    }

    [Fact]
    public void Locate_SkipsTheSelectedTabsEdge_LeftOfTheIcon()
    {
        // Selected tab: white body whose curved edge leaves grey columns at the band's left end.
        const int w = 300, h = 62;
        var p = Canvas(w, h);
        Fill(p, w, 50 + 18, 0, 236 - 18, 62, 0xFF, 0xFF, 0xFF);
        Fill(p, w, 50 + 31 + 4, 18 + 4, 16, 16, 0x20, 0x20, 0x20);
        var icon = FaviconCapture.Locate(p, w, h, 50, 0, 236, 62, 1.5);
        Assert.NotNull(icon);
        Assert.Equal(0x20, icon!.Bgra[(12 * 24 + 12) * 4]);
        Assert.Equal(0, icon.Bgra[3]); // white tab body is the background here
    }

    [Fact]
    public void ReadTabsPayload_RoundTripsPerTabIcons_AndRejectsBadLength()
    {
        var icon = new TabIcon(2, 2, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var snap = new TabSnapshot(new WorkerEpoch(1), K1, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UnixEpoch, TabAvailability.Available,
            new[] { new TabEntry(new TabToken("a"), "x", 0, false, true, icon), new TabEntry(new TabToken("b"), "y", 1, false, true) });
        var payload = JsonSerializer.SerializeToElement(PipeServer.BuildReadTabsPayload(K1, snap));
        var tabs = Protocol.Parse(Protocol.Serialize(new Protocol.Message("Result", 1, 1, payload))).Payload.GetProperty("tabs");
        Assert.Equal(icon.Bgra, Protocol.ParseIcon(tabs[0])!.Bgra);
        Assert.Null(Protocol.ParseIcon(tabs[1]));

        var bad = JsonSerializer.SerializeToElement(new { status = "Success", tabs = new[] { new { title = "x", icon = new { width = 2, height = 2, bgra = Convert.ToBase64String(new byte[3]) } } } });
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Serialize(new Protocol.Message("Result", 1, 1, bad)));
    }

    private static TabSnapshot Snap(params (string Title, TabIcon? Icon)[] tabs) =>
        new(new WorkerEpoch(1), K1, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UnixEpoch, TabAvailability.Available,
            tabs.Select((t, i) => new TabEntry(new TabToken("t" + i), t.Title, i, false, true, t.Icon)).ToList());

    [Fact]
    public void Store_KeepsIconsOfOpenTabsWhenCaptureFails_AndDropsClosedTabs()
    {
        var store = new FaviconStore();
        var icon = new TabIcon(1, 1, new byte[] { 1, 2, 3, 255 });
        store.Update(Snap(("a", icon), ("b", icon)));
        Assert.NotNull(store.Get(K1, "a"));
        store.Update(Snap(("a", null))); // minimized: no capture, tab b closed
        Assert.NotNull(store.Get(K1, "a"));
        Assert.Null(store.Get(K1, "b"));
        store.Remove(K1);
        Assert.Null(store.Get(K1, "a"));
    }

    [Fact]
    public void Taskbar_PrefersFavicon_KeepsItWhenCaptureFails_DropsItWhenLeftmostTabChanges()
    {
        using var hover = new HoverController();
        var bar = new TaskbarViewModel(hover, () => 0, _ => { });
        var e1 = new ChromeWindowEntry(K1, "w", "w", false, false, 1);
        var icon = new TabIcon(1, 1, new byte[] { 1, 2, 3, 255 });

        bar.Update(new[] { e1 }, _ => new CachedTabs(Snap(("left", icon)), true));
        Assert.NotNull(bar.Buttons[0].Favicon);
        Assert.Same(bar.Buttons[0].Favicon, bar.Buttons[0].Icon);

        bar.Update(new[] { e1 }, _ => new CachedTabs(Snap(("left", null)), true)); // e.g. window minimized now
        Assert.NotNull(bar.Buttons[0].Favicon);

        bar.Update(new[] { e1 }, _ => new CachedTabs(Snap(("other", null)), true));
        Assert.Null(bar.Buttons[0].Favicon);
        Assert.NotNull(bar.Buttons[0].Icon); // identicon fallback
    }

    [Fact]
    public void Popup_UsesStoredFaviconPerTab_ElseIdenticon()
    {
        var store = new FaviconStore();
        var icon = new TabIcon(1, 1, new byte[] { 1, 2, 3, 255 });
        var snap = Snap(("a", icon), ("b", null));
        store.Update(snap);
        using var popup = new TabPopupViewModel(K1, 1, (_, _, _, _) => Task.FromResult(new BrokerResult(ResultStatus.Success)),
            favicon: title => store.Get(K1, title));
        Assert.True(popup.Apply(popup.BeginRequest(), new BrokerResult(ResultStatus.Success, snap)));
        Assert.NotNull(popup.Tabs[0].Favicon);
        Assert.Same(popup.Tabs[0].Favicon, popup.Tabs[0].Icon);
        Assert.Null(popup.Tabs[1].Favicon);
        Assert.Same(Identicon.Image("b"), popup.Tabs[1].Icon);
    }
}
