using System.IO.Pipes;
using System.Text.Json;
using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using ChromeTaskbar.UiaWorker;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class WiringAndWin32Tests
{
    [Fact]
    public void Win32Enumerator_EnumerateTopLevel_ReturnsValidChromeObservationsWithoutThrowing()
    {
        var list = new Win32WindowEnumerator().EnumerateTopLevel();
        Assert.NotNull(list);
        foreach (var o in list)
        {
            Assert.NotEqual((nint)0, o.Hwnd);
            Assert.Equal(ChromeWindowFilter.ChromeClassName, o.ClassName);
            Assert.True(o.ProcessId > 0);
        }
    }

    [Fact]
    public void Win32Enumerator_Observe_InvalidHandleIsNull_AndForegroundHasValidFields()
    {
        var e = new Win32WindowEnumerator();
        Assert.Null(e.Observe(0));
        var fg = e.GetForegroundWindow();
        if (fg == 0) return; // no foreground window (headless session)
        var o = e.Observe(fg);
        if (o is null) return; // window closed between calls
        Assert.Equal(fg, o.Hwnd);
        Assert.NotNull(o.ClassName);
        Assert.NotNull(o.Title);
        Assert.True(o.ProcessId > 0);
    }

    [Fact]
    public async Task PipeServer_ConnectsAsClientToBrokerPipe_SendsHelloAndRepliesStaleForWrongEpoch()
    {
        var name = $"ChromeTaskbar.Test.{Guid.NewGuid():N}";
        using var broker = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new PipeServer(name, new WorkerEpoch(7));
        var run = Task.Run(() => server.RunAsync(cts.Token));

        await broker.WaitForConnectionAsync(cts.Token);
        var hello = await Protocol.ReadFrameAsync(broker, cts.Token);
        Assert.Equal("Hello", hello.Type);
        Assert.Equal(7UL, hello.WorkerEpoch);

        var request = new Protocol.Message("Cancel", 99UL, 5UL, JsonSerializer.SerializeToElement(new { targetRequestId = "4" }));
        await Protocol.WriteFrameAsync(broker, request, cts.Token);
        var reply = await Protocol.ReadFrameAsync(broker, cts.Token);
        Assert.Equal("Result", reply.Type);
        Assert.Equal(5UL, reply.RequestId);
        Assert.Equal("Stale", reply.Payload.GetProperty("status").GetString());

        broker.Dispose(); // worker sees EOF mid-frame; RunAsync surfaces it as EndOfStreamException (worker exits)
        await Assert.ThrowsAsync<EndOfStreamException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WorkerLocator_ExplicitWorkerArgumentWinsAndIsFullPath()
    {
        var found = WorkerLocator.Find(new[] { "--worker", "some/relative/w.exe" });
        Assert.Equal(Path.GetFullPath("some/relative/w.exe"), found);
    }

    [Fact]
    public void WorkerLocator_PrefersExeBesideApp_WhenNoArgument()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "ChromeTaskbar.UiaWorker.exe");
        bool created = !File.Exists(beside);
        if (created) File.WriteAllBytes(beside, Array.Empty<byte>());
        try
        {
            Assert.Equal(beside, WorkerLocator.Find(Array.Empty<string>()));
            // A dangling --worker (no value) falls through to the beside lookup.
            Assert.Equal(beside, WorkerLocator.Find(new[] { "--worker" }));
        }
        finally { if (created) File.Delete(beside); }
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("a", null, "a")]
    [InlineData("", "b", "b")]
    [InlineData("a", "b", "a b")]
    public void PopupStatusText_ComposesNonEmptyParts(string? status, string? msg, string expected) =>
        Assert.Equal(expected, PopupStatusText.Compose(status, msg));

    // ---- Win32 enumerator invariants (T-win32) -------------------------------

    [Fact]
    public void Win32Enumerator_Results_SatisfyPrefilterInvariants_AndFilterOnlyNarrows()
    {
        var list = new Win32WindowEnumerator().EnumerateTopLevel();
        Assert.Equal(list.Count, list.Select(o => o.Hwnd).Distinct().Count());
        foreach (var o in list)
        {
            Assert.True(o.IsVisible);
            Assert.Equal(ChromeWindowFilter.ChromeClassName, o.ClassName);
            if (ChromeWindowFilter.IsChromeMainWindow(o))
            {
                Assert.False(o.HasOwner || o.IsToolWindow || o.IsCloaked);
                Assert.True(o.ProcessStartTimeTicks > 0);
                Assert.Equal(ChromeWindowFilter.ChromeExecutable, o.ExecutableName, ignoreCase: true);
            }
        }
        Assert.True(list.Where(ChromeWindowFilter.IsChromeMainWindow).Count() <= list.Count);
    }

    // ---- UiaWorkerBroker diagnostics sink ---------------------------------------

    [Fact]
    public async Task Broker_JobThrowsUnexpectedly_ReportsToDiagnosticsSink_AndFails()
    {
        var sink = new List<string>();
        using var b = new UiaWorkerBroker((_, _) => throw new NullReferenceException("boom"), null, null, null, null, sink.Add);
        var r = await b.ReadTabsAsync(TabSnapshotCacheTests.K(1), 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Contains(sink, m => m.Contains("job failed unexpectedly") && m.Contains("boom"));
        Assert.Contains("boom", b.LastDiagnostic);
    }

    [Fact]
    public async Task Broker_RecoveryFails_ReportsToDiagnosticsSink_AndSetsRecoveryFailed()
    {
        var sink = new List<string>();
        using var b = new UiaWorkerBroker((_, _) => throw new IOException("pipe broken"),
            _ => throw new InvalidOperationException("restart denied"), null, null, null, sink.Add);
        var r = await b.ReadTabsAsync(TabSnapshotCacheTests.K(1), 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.True(b.RecoveryFailed);
        Assert.Contains(sink, m => m.Contains("worker recovery failed") && m.Contains("restart denied"));
    }

    [Fact]
    public async Task Broker_FaultySink_DoesNotBreakBroker()
    {
        using var b = new UiaWorkerBroker((_, _) => throw new NullReferenceException("x"), null, null, null, null, _ => throw new InvalidOperationException("sink"));
        var r = await b.ReadTabsAsync(TabSnapshotCacheTests.K(1), 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ResultStatus.Failed, r.Status);
    }

    // ---- Evidence files (T001 PoC schema/validator, T004 acceptance evidence) ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ChromeTaskbar.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (ChromeTaskbar.sln) not found.");
    }

    private static JsonDocument LoadPlan(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "plans", name)));

    private static string ExpectedGate(IEnumerable<string> results)
    {
        var l = results.ToList();
        return l.Any(r => r == "Fail") ? "Fail" : l.Any(r => r == "Blocked") ? "Blocked" : "Pass";
    }

    [Fact]
    public void EvidenceFiles_PocSchema_DeclaresRequiredFieldsAndFixedConstraints()
    {
        using var schema = LoadPlan("chrome-taskbar-poc-evidence.schema.json");
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        foreach (var f in new[] { "chrome_version", "windows_build", "dpi", "language", "constraints", "poc_gate", "cases" }) Assert.Contains(f, required);
        var cons = schema.RootElement.GetProperty("properties").GetProperty("constraints").GetProperty("properties");
        foreach (var c in cons.EnumerateObject()) Assert.False(c.Value.GetProperty("const").GetBoolean());
        Assert.Equal(16, schema.RootElement.GetProperty("properties").GetProperty("cases").GetProperty("minItems").GetInt32());
    }

    [Fact]
    public void EvidenceFiles_PocEvidence_HasRequiredFields_ValidRows_AndConsistentGate()
    {
        using var doc = LoadPlan("chrome-taskbar-poc-evidence.json");
        var root = doc.RootElement;
        foreach (var f in new[] { "chrome_version", "windows_build", "dpi", "language" })
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty(f).GetString()), f);
        foreach (var c in root.GetProperty("constraints").EnumerateObject()) Assert.Equal(JsonValueKind.False, c.Value.ValueKind);

        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(16, cases.Count);
        Assert.Equal(cases.Count, cases.Select(c => c.GetProperty("condition_id").GetString()).Distinct().Count());
        var results = new List<string>();
        foreach (var c in cases)
        {
            var result = c.GetProperty("result").GetString()!;
            Assert.Contains(result, new[] { "Pass", "Fail", "Blocked" });
            results.Add(result);
            if (result != "Pass") continue;
            Assert.True(c.GetProperty("expected_tabs").GetInt32() >= 0);
            Assert.True(c.GetProperty("observed_tabs").GetInt32() >= 1);
            Assert.True(c.GetProperty("duration_ms").GetInt32() >= 0);
            if (c.GetProperty("ac").GetString() is "AC-01" or "AC-02")
            {
                var se = c.GetProperty("read_side_effects");
                foreach (var k in new[] { "restored", "foregrounded", "group_expanded" }) Assert.Equal(JsonValueKind.False, se.GetProperty(k).ValueKind);
            }
        }
        Assert.Equal(ExpectedGate(results), root.GetProperty("poc_gate").GetString());
        // Pass iff every row is Pass.
        Assert.Equal(results.All(r => r == "Pass"), root.GetProperty("poc_gate").GetString() == "Pass");
    }

    [Fact]
    public void EvidenceFiles_AcceptanceEvidence_GateAndSummaryMatchRows()
    {
        using var doc = LoadPlan("chrome-taskbar-acceptance-evidence.json");
        var root = doc.RootElement;
        foreach (var c in root.GetProperty("constraints").EnumerateObject()) Assert.Equal(JsonValueKind.False, c.Value.ValueKind);
        var rows = root.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("result").GetString()!).ToList();
        Assert.All(rows, r => Assert.Contains(r, new[] { "Pass", "Fail", "Blocked" }));
        Assert.Equal(ExpectedGate(rows), root.GetProperty("acceptance_gate").GetString());

        var summary = root.GetProperty("summary").GetString()!;
        int N(string pattern) => int.Parse(System.Text.RegularExpressions.Regex.Match(summary, pattern).Groups[1].Value);
        Assert.Equal(rows.Count, N(@"^(\d+) "));
        Assert.Equal(rows.Count(r => r == "Pass"), N(@"(\d+) real Pass") + N(@"(\d+) automated-unit Pass"));
        Assert.Equal(rows.Count(r => r == "Fail"), N(@"(\d+) Fail"));
        Assert.Equal(rows.Count(r => r == "Blocked"), N(@"(\d+) Blocked"));
    }
}
