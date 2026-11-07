using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class ContractsTests
{
    [Fact]
    public void WindowKey_RequiresHwndPidProcessStartTimeAndGeneration()
    {
        var key = new WindowKey((nint)100, 200, 300, 1);

        Assert.NotEqual(key, key with { Hwnd = (nint)101 });
        Assert.NotEqual(key, key with { ProcessId = 201 });
        Assert.NotEqual(key, key with { ProcessStartTimeTicks = 301 });
        Assert.NotEqual(key, key with { Generation = 2 });
    }

    [Fact]
    public void TabToken_RemainsIdentityWhenTitleOrDisplayOrderChanges()
    {
        var token = new TabToken("worker-issued-token");
        var original = new TabEntry(token, "Same title", 0, false, true);
        var renamedAndReordered = original with { Title = "Renamed", DisplayOrder = 5 };

        Assert.Equal(original.Token, renamedAndReordered.Token);
        Assert.NotEqual(original.Title, renamedAndReordered.Title);
        Assert.NotEqual(original.DisplayOrder, renamedAndReordered.DisplayOrder);
    }
}
