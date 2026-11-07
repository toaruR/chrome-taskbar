using System.Text;
using System.Text.Json;
using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

/// <summary>
/// T009: one test per section-9 T-1/T/T+1 row (input -> decision -> observable result -> source-input check),
/// plus malformed integers, Incomplete without partial tabs, cancellation non-application and restart token rejection.
/// Times are caller-supplied monotonic milliseconds; sizes are real bytes / UTF-16 code units.
/// </summary>
public class AcceptanceBoundaryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static WindowKey K(int n) => TabSnapshotCacheTests.K(n);

    // ---- HoverDelay / PopupCloseGrace ------------------------------------------------------

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(501, true)]
    public void HoverDelay_boundary(long stayMs, bool opens)
    {
        using var h = new HoverController();
        h.ButtonEnter(K(1), 500);
        Assert.Equal(opens, h.Tick(500 + stayMs));
        Assert.Equal(opens ? HoverState.Open : HoverState.Waiting, h.State);
        var open = h.Events.SingleOrDefault(e => e.Kind == HoverEventKind.OpenPopup);
        Assert.Equal(opens, open is not null);
        if (open is not null) Assert.Equal(stayMs, open.TickMs - 500); // popup tick - enter tick
    }

    [Theory]
    [InlineData(249, false)]
    [InlineData(250, true)]
    [InlineData(251, true)]
    public void PopupCloseGrace_boundary(long outsideMs, bool closes)
    {
        using var h = new HoverController();
        h.ButtonEnter(K(1), 0); h.Tick(1000);
        h.ButtonLeave(K(1), 2000);
        Assert.Equal(closes, h.Tick(2000 + outsideMs));
        Assert.Equal(closes ? HoverState.Idle : HoverState.Closing, h.State);
        var close = h.Events.SingleOrDefault(e => e.Kind == HoverEventKind.ClosePopup);
        Assert.Equal(closes, close is not null);
        if (close is not null) Assert.Equal(outsideMs, close.TickMs - 2000); // close tick - exit tick
    }

    // ---- WinEventDebounce / WinEventMaxWait ---------------------------------------------------

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    public void WinEventDebounce_boundary(long gapMs, bool dispatches)
    {
        var c = new WinEventCoalescer();
        c.Notify(0); c.Notify(10);
        var r = c.Poll(10 + gapMs);
        Assert.Equal(dispatches, r is not null);
        if (r is null) { Assert.True(c.Pending); return; }
        Assert.Equal(1, r.Sequence); Assert.Equal("Debounce", r.Reason); Assert.Equal(0, r.FirstNotifyTickMs); Assert.Equal(2, r.Count);
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(501, true)]
    public void WinEventMaxWait_boundary(long pendingMs, bool dispatches)
    {
        var c = new WinEventCoalescer();
        c.Notify(0); c.Notify(pendingMs - 1); // keeps the debounce window open
        var r = c.Poll(pendingMs);
        Assert.Equal(dispatches, r is not null);
        if (r is null) return;
        Assert.Equal("MaxWait", r.Reason); Assert.Equal(0, r.FirstNotifyTickMs); Assert.Equal(pendingMs, r.TickMs);
    }

    // ---- Poll intervals -------------------------------------------------------------------------

    [Theory]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    [InlineData(2001, true)]
    public void VisiblePollInterval_boundary(long elapsed, bool enumerates) => AssertFire(new WindowPollScheduler(0).Poll(elapsed, false), elapsed, enumerates);

    [Theory]
    [InlineData(9999, false)]
    [InlineData(10000, true)]
    [InlineData(10001, true)]
    public void MinimizedPollInterval_boundary(long elapsed, bool enumerates) => AssertFire(new WindowPollScheduler(0).Poll(elapsed, true), elapsed, enumerates);

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(1001, true)]
    public void PopupRefreshInterval_boundary(long elapsed, bool reads)
    {
        var s = new PopupRefreshScheduler();
        Assert.Null(s.Poll(elapsed)); // not started: never reads
        s.Start(0);
        AssertFire(s.Poll(elapsed), elapsed, reads);
    }

    private static void AssertFire(FireRecord? r, long tick, bool expected)
    {
        Assert.Equal(expected, r is not null);
        if (r is not null) { Assert.Equal(1, r.Sequence); Assert.Equal(tick, r.TickMs); }
    }

    // ---- Traversal / tab / title limits ---------------------------------------------------------
    // Decisions pin the implemented contract: Incomplete at/after the node/depth/time limit (design section 9), strictly above the tab/title limit.

    [Theory]
    [InlineData(999, TabAvailability.Available)]
    [InlineData(1000, TabAvailability.Incomplete)]
    [InlineData(1001, TabAvailability.Incomplete)]
    public void TraversalNodes_boundary(int visited, TabAvailability expected) =>
        Assert.Equal(expected, Protocol.ProbeAvailabilityFor(visited, 0, TimeSpan.Zero, 0, 0));

    [Theory]
    [InlineData(11, TabAvailability.Available)]
    [InlineData(12, TabAvailability.Incomplete)]
    [InlineData(13, TabAvailability.Incomplete)]
    public void TraversalDepth_boundary(int depth, TabAvailability expected) =>
        Assert.Equal(expected, Protocol.ProbeAvailabilityFor(1, depth, TimeSpan.Zero, 0, 0));

    [Theory]
    [InlineData(749, TabAvailability.Available)]
    [InlineData(750, TabAvailability.Incomplete)]
    [InlineData(751, TabAvailability.Incomplete)]
    public void TraversalTime_boundary(int ms, TabAvailability expected) =>
        Assert.Equal(expected, Protocol.ProbeAvailabilityFor(1, 0, TimeSpan.FromMilliseconds(ms), 0, 0));

    [Theory]
    [InlineData(511, true)]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void MaxTabs_boundary(int tabs, bool allowed)
    {
        Assert.Equal(allowed ? TabAvailability.Available : TabAvailability.Incomplete, Protocol.ProbeAvailabilityFor(1, 0, TimeSpan.Zero, tabs, 0));
        var payload = JsonSerializer.Serialize(new { status = "Success", tabs = Enumerable.Range(0, tabs).Select(_ => new { tabToken = "t", title = "x" }).ToArray() });
        var frame = Encoding.UTF8.GetBytes(ResultJson(payload));
        if (allowed) Assert.Equal(tabs, Protocol.Parse(frame).Payload.GetProperty("tabs").GetArrayLength());
        else Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(frame));
    }

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void MaxTitleUnits_boundary(int units, bool allowed)
    {
        Assert.Equal(allowed ? TabAvailability.Available : TabAvailability.Incomplete, Protocol.ProbeAvailabilityFor(1, 0, TimeSpan.Zero, 1, units));
        var title = new string('a', units - 2) + "\U0001F600"; // surrogate pair = 2 UTF-16 code units
        Assert.Equal(units, title.Length);
        var payload = JsonSerializer.Serialize(new { status = "Success", tabs = new[] { new { tabToken = "t", title } } });
        var frame = Encoding.UTF8.GetBytes(ResultJson(payload));
        if (allowed) Assert.Equal(units, Protocol.Parse(frame).Payload.GetProperty("tabs")[0].GetProperty("title").GetString()!.Length);
        else Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(frame));
    }

    // ---- Deadlines / cache --------------------------------------------------------------------

    [Theory]
    [InlineData(7, 0)]
    [InlineData(8, 0)]
    [InlineData(9, 1)]
    public void CacheWindowLimit_boundary(int entries, int evicted)
    {
        var cache = new TabSnapshotCache(() => T0);
        var keys = Enumerable.Range(1, entries).Select(K).ToList();
        var gone = keys.SelectMany(k => cache.Put(TabSnapshotCacheTests.Snap(k, T0))).ToList();
        Assert.Equal(evicted, gone.Count);
        if (evicted == 1) Assert.Equal(keys[0], gone[0]); // eviction key = oldest input key
        Assert.Equal(keys.Skip(evicted).ToList(), cache.Keys.ToList());
    }

    [Theory]
    [InlineData(4999, true)]
    [InlineData(5000, false)]
    [InlineData(5001, false)]
    public void CacheTtl_boundary(int ageMs, bool canSelect)
    {
        var now = T0;
        var cache = new TabSnapshotCache(() => now);
        cache.Put(TabSnapshotCacheTests.Snap(K(1), T0));
        now = T0.AddMilliseconds(ageMs);
        var got = cache.Get(K(1))!;
        Assert.Equal(T0, got.Snapshot.CapturedAt);
        Assert.Equal(canSelect, got.CanSelect);
    }

    private static UiaWorkerBroker NewBroker(Func<Protocol.Message, Task<Protocol.Message>> handler, Func<DateTimeOffset> clock, List<Protocol.Message>? calls = null) =>
        new((m, _) => { if (calls is not null) lock (calls) calls.Add(m); return handler(m); }, _ => Task.CompletedTask, clock, _ => true);

    private static Protocol.Message Reply(Protocol.Message req, object payload) =>
        new("Result", req.WorkerEpoch, req.RequestId, JsonSerializer.SerializeToElement(payload));

    private static object ReadOk() => new { status = "Success", snapshotId = Guid.NewGuid(), capturedAtUtc = T0.ToString("O"), tabs = new[] { new { tabToken = "tok", title = "A", displayOrder = 0, isSelected = true, canSelect = true } } };

    [Theory]
    [InlineData(1499, true)]
    [InlineData(1500, false)]
    [InlineData(1501, false)]
    public async Task ReadDeadline_boundary(int remainingBeforeRunMs, bool runs)
    {
        var now = T0; var calls = new List<Protocol.Message>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        var first = true;
        using var broker = NewBroker(async m => { if (first) { first = false; started.SetResult(); await gate.Task; } return Reply(m, ReadOk()); }, () => now, calls);
        var blocker = broker.ReadTabsAsync(K(9), 0, deadline: TimeSpan.FromHours(1));
        await started.Task;
        var read = broker.ReadTabsAsync(K(1), 0);
        now = T0.AddMilliseconds(remainingBeforeRunMs);
        gate.SetResult(); await blocker;
        var r = await read;
        Assert.Equal(runs ? ResultStatus.Success : ResultStatus.Timeout, r.Status);
        Assert.Equal(runs ? 2 : 1, calls.Count(c => c.Type == "ReadTabs")); // execution log has no request for a timed-out read
    }

    [Theory]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    [InlineData(2001, false)]
    public async Task SelectDeadline_boundary(int remainingBeforeRunMs, bool runs)
    {
        var now = T0; var calls = new List<Protocol.Message>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        var block = false;
        using var broker = NewBroker(async m =>
        {
            if (block && m.Type == "ReadTabs") { block = false; started.SetResult(); await gate.Task; }
            return Reply(m, m.Type == "ReadTabs" ? ReadOk() : new { status = "Success" });
        }, () => now, calls);
        var seed = await broker.ReadTabsAsync(K(1), 0);
        block = true;
        var blocker = broker.ReadTabsAsync(K(9), 0, deadline: TimeSpan.FromHours(1));
        await started.Task;
        var select = broker.SelectTabAsync(K(1), seed.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        now = T0.AddMilliseconds(remainingBeforeRunMs);
        gate.SetResult(); await blocker;
        var r = await select;
        Assert.Equal(runs ? ResultStatus.Success : ResultStatus.Timeout, r.Status);
        Assert.Equal(runs ? 1 : 0, calls.Count(c => c.Type == "SelectTab"));
    }

    // ---- Backoff ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 5000)]
    [InlineData(3, 30000)]
    [InlineData(4, 30000)]
    public void Backoff_boundary_per_failure_ordinal(int failures, long delay)
    {
        foreach (var offset in new long[] { -1, 0, 1 })
        {
            var b = new RestartBackoff();
            for (int i = 0; i < failures; i++) b.RecordFailure(100);
            Assert.Equal(failures, b.Failures);
            Assert.Equal(100 + delay, b.DueMs); // due tick - failure tick
            Assert.Equal(offset >= 0, b.CanSpawn(b.DueMs + offset));
        }
    }

    // ---- Integer limits ---------------------------------------------------------------------------

    [Theory]
    [InlineData("18446744073709551614", true)]
    [InlineData("18446744073709551615", true)]
    [InlineData("18446744073709551616", false)]
    public void U64Max_boundary(string text, bool allowed)
    {
        if (!allowed) { Assert.Throws<Protocol.ProtocolException>(() => Protocol.ParseUnsigned(text, "id")); return; }
        Assert.Equal(text, Protocol.SerializeUnsigned(Protocol.ParseUnsigned(text, "id")));
        var frame = Encoding.UTF8.GetBytes(Envelope($"\"{text}\"", $"\"{text}\"", "{}", "Hello"));
        var parsed = Protocol.Parse(frame);
        Assert.Equal(text, Protocol.SerializeUnsigned(parsed.WorkerEpoch));
        Assert.Equal(text, Protocol.SerializeUnsigned(parsed.RequestId));
    }

    [Theory]
    [InlineData("4294967294", true)]
    [InlineData("4294967295", true)]
    [InlineData("4294967296", false)]
    public void PidMax_boundary(string text, bool allowed)
    {
        if (!allowed) { Assert.Throws<Protocol.ProtocolException>(() => Protocol.ParseUnsigned(text, "processId", Protocol.PidMax)); }
        else Assert.Equal(text, Protocol.SerializeUnsigned(Protocol.ParseUnsigned(text, "processId", Protocol.PidMax)));
    }

    [Fact]
    public void PidMax_in_ReadTabs_frame_parses_without_overflow_and_round_trips()
    {
        var message = Protocol.Parse(Encoding.UTF8.GetBytes(ReadTabsJson(processId: "\"4294967295\"")));
        var key = Protocol.ParseWindowKey(message.Payload);
        Assert.Equal("4294967295", Protocol.SerializeUnsigned((ulong)(uint)key.ProcessId));
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(Encoding.UTF8.GetBytes(ReadTabsJson(processId: "\"4294967296\""))));
    }

    // ---- MaxFrameBytes ---------------------------------------------------------------------------

    [Theory]
    [InlineData(4194303, true)]
    [InlineData(4194304, false)]
    [InlineData(4194305, false)]
    public async Task MaxFrameBytes_boundary(int bytes, bool allowed)
    {
        var frame = PaddedFrame(bytes);
        Assert.Equal(bytes, frame.Length);
        if (allowed) Assert.Equal("Hello", Protocol.Parse(frame).Type);
        else Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(frame));

        var wire = new byte[4 + (allowed ? bytes : 0)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)bytes);
        if (allowed) frame.CopyTo(wire, 4);
        using var stream = new MemoryStream(wire);
        if (allowed) Assert.Equal("Hello", (await Protocol.ReadFrameAsync(stream, CancellationToken.None)).Type);
        else await Assert.ThrowsAsync<Protocol.ProtocolException>(() => Protocol.ReadFrameAsync(stream, CancellationToken.None));
    }

    // ---- Malformed integers ----------------------------------------------------------------------

    public static IEnumerable<object[]> Malformed() =>
        new[] { "\"\"", "\"00\"", "\"01\"", "\"+1\"", "\"-1\"", "\"1.0\"", "\" 1\"", "\"1 \"", "\"1e3\"", "\"0x1\"", "\"٣\"", "\"18446744073709551616\"", "1", "1.5", "null", "true", "[]" }
            .Select(v => new object[] { v });

    [Theory, MemberData(nameof(Malformed))]
    public void Malformed_integers_are_ProtocolError_in_every_integer_field(string raw)
    {
        foreach (var json in new[]
        {
            Envelope(raw, "\"1\"", "{}", "Hello"), Envelope("\"1\"", raw, "{}", "Hello"),
            ReadTabsJson(hwnd: raw), ReadTabsJson(processId: raw), ReadTabsJson(start: raw), ReadTabsJson(generation: raw),
            Envelope("\"1\"", "\"1\"", "{\"targetRequestId\":" + raw + "}", "Cancel"),
        })
            Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Malformed_json_and_unknown_shapes_are_ProtocolError()
    {
        foreach (var text in new[] { "", "{", "[]", "null", "{\"type\":\"Nope\"}", "{\"type\":1}" })
            Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(Encoding.UTF8.GetBytes(text)));
    }

    // ---- Incomplete without partial tabs -----------------------------------------------------------

    [Theory]
    [InlineData(TabAvailability.Incomplete, PopupStatus.Incomplete)]
    [InlineData(TabAvailability.Unavailable, PopupStatus.Unavailable)]
    [InlineData(TabAvailability.WindowGone, PopupStatus.WindowGone)]
    public void Non_available_snapshot_shows_no_partial_tabs_and_not_zero(TabAvailability availability, PopupStatus status)
    {
        var key = K(1);
        using var popup = new TabPopupViewModel(key, 1, (_, _, _, _) => Task.FromResult(new BrokerResult(ResultStatus.Success)));
        var snap = new TabSnapshot(new WorkerEpoch(1), key, new SnapshotId(Guid.NewGuid()), T0, availability,
            new[] { new TabEntry(new TabToken("t"), "partial", 0, false, true) });
        Assert.True(popup.Apply(popup.BeginRequest(), new BrokerResult(ResultStatus.Success, snap)));
        Assert.Equal(status, popup.Status);
        Assert.Empty(popup.Tabs);
        Assert.NotEqual(TabPopupViewModel.EmptyText, popup.StatusText); // never presented as "0 tabs"
    }

    [Fact]
    public void Incomplete_result_replaces_previous_tabs_and_wire_form_forbids_tabs()
    {
        var key = K(1);
        using var popup = new TabPopupViewModel(key, 1, (_, _, _, _) => Task.FromResult(new BrokerResult(ResultStatus.Success)));
        Assert.True(popup.Apply(popup.BeginRequest(), new BrokerResult(ResultStatus.Success, TabSnapshotCacheTests.Snap(key, T0))));
        Assert.Single(popup.Tabs);
        Assert.True(popup.Apply(popup.BeginRequest(), new BrokerResult(ResultStatus.Incomplete)));
        Assert.Empty(popup.Tabs);
        Assert.Equal(TabPopupViewModel.IncompleteText, popup.StatusText);

        var withTabs = Encoding.UTF8.GetBytes(ResultJson("{\"status\":\"Incomplete\",\"tabs\":[{\"tabToken\":\"t\",\"title\":\"x\"}]}"));
        Assert.Throws<Protocol.ProtocolException>(() => Protocol.Parse(withTabs));
        Assert.Equal("Incomplete", Protocol.Parse(Encoding.UTF8.GetBytes(ResultJson("{\"status\":\"Incomplete\"}"))).Payload.GetProperty("status").GetString());
    }

    // ---- Cancellation non-application --------------------------------------------------------------

    [Fact]
    public async Task Cancelled_read_is_not_applied_to_cache_or_popup()
    {
        var key = K(1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        using var broker = NewBroker(async m => { started.TrySetResult(); await gate.Task; return Reply(m, ReadOk()); }, () => T0);
        using var cts = new CancellationTokenSource();
        var read = broker.ReadTabsAsync(key, 0, cts.Token);
        await started.Task;
        cts.Cancel(); gate.SetResult();
        var result = await read;
        Assert.Equal(ResultStatus.Cancelled, result.Status);
        Assert.Null(result.Snapshot); // cancel after start suppresses UI application only; nothing is handed to the popup

        using var popup = new TabPopupViewModel(key, 0, (_, _, _, _) => Task.FromResult(new BrokerResult(ResultStatus.Success)));
        Assert.False(popup.Apply(popup.BeginRequest(), result));
        Assert.Equal(PopupStatus.Loading, popup.Status);
        Assert.Empty(popup.Tabs);
    }

    [Fact]
    public async Task Cancelled_select_is_not_reported_as_success_and_does_not_close()
    {
        var key = K(1);
        using var cts = new CancellationTokenSource();
        var selects = 0;
        using var popup = new TabPopupViewModel(key, 0, (_, _, _, _) => { selects++; cts.Cancel(); return Task.FromResult(new BrokerResult(ResultStatus.Success)); });
        var closed = false; popup.Selected += () => closed = true;
        Assert.True(popup.Apply(popup.BeginRequest(), new BrokerResult(ResultStatus.Success, TabSnapshotCacheTests.Snap(key, T0))));
        Assert.False(await popup.SelectAsync(popup.Tabs[0], cts.Token));
        Assert.Equal(1, selects);
        Assert.Equal(SelectionOutcome.Cancelled, popup.LastSelection);
        Assert.False(closed);
        Assert.Null(popup.SelectionMessage);
    }

    [Fact]
    public void Superseded_request_response_is_not_applied()
    {
        var key = K(1);
        using var popup = new TabPopupViewModel(key, 0, (_, _, _, _) => Task.FromResult(new BrokerResult(ResultStatus.Success)));
        var older = popup.BeginRequest(); popup.BeginRequest();
        Assert.False(popup.Apply(older, new BrokerResult(ResultStatus.Success, TabSnapshotCacheTests.Snap(key, T0))));
        Assert.Empty(popup.Tabs);
    }

    // ---- Restart token rejection ---------------------------------------------------------------------

    [Fact]
    public async Task Worker_restart_rejects_pre_restart_token_without_a_worker_call()
    {
        var calls = new List<Protocol.Message>();
        using var broker = NewBroker(m => Task.FromResult(Reply(m, m.Type == "ReadTabs" ? ReadOk() : new { status = "Success" })), () => T0, calls);
        var key = K(1);
        var seed = await broker.ReadTabsAsync(key, 0);
        var oldEpoch = broker.Epoch;
        broker.InvalidateEpoch(); // worker restart: epoch bump + cache/token invalidation
        Assert.NotEqual(oldEpoch, broker.Epoch);
        Assert.Equal(0, broker.Cache.Count);

        var r = await broker.SelectTabAsync(key, seed.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        Assert.Equal(ResultStatus.Stale, r.Status);
        Assert.DoesNotContain(calls, c => c.Type == "SelectTab");
    }

    [Fact]
    public async Task Hung_worker_restart_invalidates_tokens_before_any_select_is_sent()
    {
        var calls = new List<Protocol.Message>(); var hang = false; var restarts = 0;
        using var broker = new UiaWorkerBroker((m, _) =>
        {
            lock (calls) calls.Add(m);
            return hang ? new TaskCompletionSource<Protocol.Message>().Task
                : Task.FromResult(Reply(m, m.Type == "ReadTabs" ? ReadOk() : new { status = "Success" }));
        }, _ => { restarts++; return Task.CompletedTask; }, () => T0, _ => true);
        var seed = await broker.ReadTabsAsync(K(1), 0);
        hang = true;
        Assert.Equal(ResultStatus.Timeout, (await broker.ReadTabsAsync(K(2), 0, deadline: TimeSpan.FromMilliseconds(50))).Status);
        Assert.Equal(1, restarts);
        hang = false;
        var r = await broker.SelectTabAsync(K(1), seed.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        Assert.Equal(ResultStatus.Stale, r.Status);
        Assert.DoesNotContain(calls, c => c.Type == "SelectTab");
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static string Envelope(string epoch, string requestId, string payload, string type) =>
        $"{{\"type\":\"{type}\",\"protocolVersion\":1,\"workerEpoch\":{epoch},\"requestId\":{requestId},\"payload\":{payload}}}";

    private static string ResultJson(string payload) => Envelope("\"1\"", "\"1\"", payload, "Result");

    private static string ReadTabsJson(string hwnd = "\"1\"", string processId = "\"2\"", string start = "\"3\"", string generation = "\"4\"") =>
        Envelope("\"1\"", "\"1\"",
            $"{{\"windowKey\":{{\"hwnd\":{hwnd},\"processId\":{processId},\"processStartTimeUtc\":{start},\"generation\":{generation}}},\"deadlineUtc\":\"2026-01-01T00:00:00.0000000Z\"}}",
            "ReadTabs");

    private static byte[] PaddedFrame(int size)
    {
        var json = Envelope("\"1\"", "\"1\"", "{}", "Hello");
        var bytes = new byte[size];
        Encoding.UTF8.GetBytes(json, bytes);
        Array.Fill(bytes, (byte)' ', json.Length, size - json.Length);
        return bytes;
    }
}
