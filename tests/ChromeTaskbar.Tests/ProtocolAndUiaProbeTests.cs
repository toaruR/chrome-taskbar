using System.Text;
using System.Text.Json;
using System.Reflection;
using ChromeTaskbar.Contracts;
using ChromeTaskbar.UiaWorker;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class ProtocolAndUiaProbeTests
{
    [Theory]
    [InlineData(new[] { "--pipe", "ChromeTaskbar.0123", "--epoch", "1" }, true, "ChromeTaskbar.0123", 1L)]
    [InlineData(new[] { "--pipe", "ChromeTaskbar.0123", "--epoch", "0" }, false, "", 0L)]
    [InlineData(new[] { "--pipe", "", "--epoch", "1" }, false, "", 0L)]
    [InlineData(new[] { "--epoch", "1", "--pipe", "ChromeTaskbar.0123" }, false, "", 0L)]
    public void WorkerEntrypoint_AcceptsBrokerPipeAndPositiveEpoch(string[] args, bool expected, string expectedPipe, long expectedEpoch)
    {
        var method = typeof(PipeServer).Assembly.GetType("ChromeTaskbar.UiaWorker.Program")!
            .GetMethod("TryParseBrokerArguments", BindingFlags.NonPublic | BindingFlags.Static)!;
        var parameters = new object?[] { args, null, null };

        var actual = (bool)method.Invoke(null, parameters)!;

        Assert.Equal(expected, actual);
        Assert.Equal(expectedPipe, parameters[1]);
        Assert.Equal(expectedEpoch, parameters[2]);
    }

    [Theory]
    [InlineData("0", 0UL)]
    [InlineData("42", 42UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void Protocol_U64RoundTripsInShortestDecimal(string text, ulong expected)
    {
        Assert.Equal(expected, Protocol.ParseUnsigned(text, "id"));
        Assert.Equal(text, Protocol.SerializeUnsigned(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData(" 1")]
    [InlineData("1e3")]
    [InlineData("18446744073709551616")]
    public void Protocol_RejectsNonCanonicalU64(string value) => Assert.Throws<Protocol.ProtocolException>(() => Protocol.ParseUnsigned(value, "id"));

    [Fact]
    public void Protocol_RejectsWrongVersionAndPidOverflow()
    {
        var wrongVersion = Encoding.UTF8.GetBytes("{\"type\":\"Hello\",\"protocolVersion\":2,\"workerEpoch\":\"1\",\"requestId\":\"1\",\"payload\":{}}");
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(wrongVersion));
        var payload = "{\"type\":\"ReadTabs\",\"protocolVersion\":1,\"workerEpoch\":\"1\",\"requestId\":\"1\",\"payload\":{\"windowKey\":{\"hwnd\":\"1\",\"processId\":\"4294967296\",\"processStartTimeUtc\":\"1\",\"generation\":\"1\"}}}";
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(Encoding.UTF8.GetBytes(payload)));
    }

    [Fact]
    public void Protocol_RejectsMissingDeadlineAndIncompletePartialTabs()
    {
        var missingDeadline = "{\"type\":\"ReadTabs\",\"protocolVersion\":1,\"workerEpoch\":\"1\",\"requestId\":\"1\",\"payload\":{\"windowKey\":{\"hwnd\":\"1\",\"processId\":\"2\",\"processStartTimeUtc\":\"3\",\"generation\":\"4\"}}}";
        var partialIncomplete = "{\"type\":\"Result\",\"protocolVersion\":1,\"workerEpoch\":\"1\",\"requestId\":\"1\",\"payload\":{\"status\":\"Incomplete\",\"tabs\":[]}}";
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(Encoding.UTF8.GetBytes(missingDeadline)));
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(Encoding.UTF8.GetBytes(partialIncomplete)));
    }

    [Fact]
    public async Task Protocol_FrameRoundTripsAndMaxFrameDisconnects()
    {
        using var stream = new MemoryStream();
        var payload = JsonSerializer.SerializeToElement(new { });
        await Protocol.WriteFrameAsync(stream, new Protocol.Message("Hello", 1, 2, payload), CancellationToken.None);
        stream.Position = 0;
        var result = await Protocol.ReadFrameAsync(stream, CancellationToken.None);
        Assert.Equal("Hello", result.Type); Assert.Equal(1UL, result.WorkerEpoch);

        var oversizedHeader = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeader, (uint)Protocol.MaxFrameBytes + 1);
        using var oversized = new MemoryStream(oversizedHeader);
        await Assert.ThrowsAsync<Protocol.ProtocolException>(() => Protocol.ReadFrameAsync(oversized, CancellationToken.None));
    }

    [Theory]
    [InlineData(999, 11, 749, 511, 1023, TabAvailability.Available)]
    [InlineData(1000, 12, 749, 512, 1024, TabAvailability.Incomplete)]
    [InlineData(1001, 12, 749, 512, 1024, TabAvailability.Incomplete)]
    [InlineData(1000, 13, 749, 512, 1024, TabAvailability.Incomplete)]
    [InlineData(1000, 12, 750, 512, 1024, TabAvailability.Incomplete)]
    [InlineData(1000, 12, 749, 513, 1024, TabAvailability.Incomplete)]
    [InlineData(1000, 12, 749, 512, 1025, TabAvailability.Incomplete)]
    public void UiaProbe_LimitsAreBoundedWithoutPartialSuccess(int visited, int depth, int milliseconds, int tabs, int titleUnits, TabAvailability expected)
    {
        var actual = ChromeUiaTabProvider.AvailabilityForLimits(visited, depth, TimeSpan.FromMilliseconds(milliseconds), tabs, titleUnits);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UiaProbe_UsesRawViewSoCollapsedGroupTabsAreVisible() =>
        Assert.Same(System.Windows.Automation.TreeWalker.RawViewWalker, ChromeUiaTabProvider.Walker);

    [Fact]
    public void UiaProbe_ChildrenEnumeratesAllSiblingsInOrder()
    {
        var tree = new Dictionary<string, string[]> { ["strip"] = new[] { "a", "group", "b" }, ["group"] = new[] { "c" } };
        string? First(string n) => tree.TryGetValue(n, out var k) ? k[0] : null;
        string? Next(string n) { var parent = tree.First(p => p.Value.Contains(n)); var i = Array.IndexOf(parent.Value, n) + 1; return i < parent.Value.Length ? parent.Value[i] : null; }
        Assert.Equal(new[] { "a", "group", "b" }, ChromeUiaTabProvider.Children("strip", First, Next));
        Assert.Equal(new[] { "c" }, ChromeUiaTabProvider.Children("group", First, Next));
        Assert.Empty(ChromeUiaTabProvider.Children("a", First, Next));
    }

    [Fact]
    public void UiaProbe_IsDescendantOfFollowsParentChain()
    {
        var parents = new Dictionary<string, string> { ["tab"] = "group", ["group"] = "strip", ["strip"] = "root" };
        string? Parent(string n) => parents.GetValueOrDefault(n);
        Assert.True(ChromeUiaTabProvider.IsDescendantOf("tab", "strip", Parent));
        Assert.False(ChromeUiaTabProvider.IsDescendantOf("strip", "tab", Parent));
        Assert.False(ChromeUiaTabProvider.IsDescendantOf("tab", "other", Parent));
    }

    [Fact]
    public void UiaProbe_PrunesLeafControlsButNotContainersOrGroupHeaders()
    {
        Assert.True(ChromeUiaTabProvider.IsLeafControl(System.Windows.Automation.ControlType.TabItem));
        Assert.True(ChromeUiaTabProvider.IsLeafControl(System.Windows.Automation.ControlType.Button));
        Assert.False(ChromeUiaTabProvider.IsLeafControl(System.Windows.Automation.ControlType.Pane));
        Assert.False(ChromeUiaTabProvider.IsLeafControl(System.Windows.Automation.ControlType.Tab));
    }
}
