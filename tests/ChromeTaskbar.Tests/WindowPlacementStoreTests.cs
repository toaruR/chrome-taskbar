using System.IO;
using System.Windows;
using ChromeTaskbar.App;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ctb-placement-" + Guid.NewGuid().ToString("N"));
    private string PathIn => System.IO.Path.Combine(_dir, "sub", "window.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndCreatesDirectory()
    {
        var r = new Rect(-100.5, 20, 640, 34);
        WindowPlacementStore.Save(PathIn, r);
        Assert.Equal(r, WindowPlacementStore.Load(PathIn));
    }

    [Fact]
    public void Load_MissingOrCorruptOrInvalid_ReturnsNull()
    {
        Assert.Null(WindowPlacementStore.Load(PathIn));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathIn)!);
        File.WriteAllText(PathIn, "{not json");
        Assert.Null(WindowPlacementStore.Load(PathIn));
        File.WriteAllText(PathIn, """{"left":0,"top":0,"width":0,"height":10}""");
        Assert.Null(WindowPlacementStore.Load(PathIn));
    }

    [Fact]
    public void IsVisible_RequiresMinimumOverlap()
    {
        var screen = new Rect(0, 0, 1920, 1080);
        Assert.True(WindowPlacementStore.IsVisible(new Rect(100, 100, 800, 34), screen));
        Assert.True(WindowPlacementStore.IsVisible(new Rect(1920 - 40, 1080 - 40, 800, 100), screen));
        Assert.False(WindowPlacementStore.IsVisible(new Rect(1920 - 39, 100, 800, 34), screen));
        Assert.False(WindowPlacementStore.IsVisible(new Rect(100, 1080 - 20, 800, 34), screen)); // bar half off-screen
        Assert.False(WindowPlacementStore.IsVisible(new Rect(3000, 100, 800, 34), screen));
    }
}
