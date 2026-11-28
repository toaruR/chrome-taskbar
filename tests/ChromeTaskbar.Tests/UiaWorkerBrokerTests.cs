using System.IO;
using System.Text.Json;
using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

public class UiaWorkerBrokerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private sealed class Rig : IDisposable
    {
        public DateTimeOffset Now = T0;
        public long NowMs;
        public readonly List<Protocol.Message> Calls = new();
        public Func<Protocol.Message, CancellationToken, Task<Protocol.Message>> Handler;
        public int Restarts;
        public bool WindowCurrent = true;
        public UiaWorkerBroker Broker;

        public Rig()
        {
            Handler = (m, _) => Task.FromResult(Reply(m));
            Broker = new UiaWorkerBroker(
                (m, ct) => { lock (Calls) Calls.Add(m); return Handler(m, ct); },
                _ => { Restarts++; return Task.CompletedTask; },
                () => Now, _ => WindowCurrent, monotonicClock: () => NowMs);
        }

        public Protocol.Message Reply(Protocol.Message req, string status = "Success", ulong? epoch = null, ulong? id = null)
        {
            object payload = req.Type == "ReadTabs" && status == "Success"
                ? new { status, snapshotId = Guid.NewGuid(), capturedAtUtc = Now.ToString("O"), tabs = new[] { new { tabToken = "tok", title = "A", displayOrder = 0, isSelected = true, canSelect = true } } }
                : new { status };
            return new Protocol.Message("Result", epoch ?? req.WorkerEpoch, id ?? req.RequestId, JsonSerializer.SerializeToElement(payload));
        }

        public void Dispose() => Broker.Dispose();
        public int CountOf(string type) { lock (Calls) return Calls.Count(c => c.Type == type); }
    }

    private static WindowKey K(int n) => TabSnapshotCacheTests.K(n);

    // Blocks the pump with a long-deadline read so later requests are queued unstarted.
    private static (TaskCompletionSource Gate, Task<BrokerResult> Blocker) Block(Rig rig)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource();
        var inner = rig.Handler;
        rig.Handler = async (m, ct) => { rig.Handler = inner; started.SetResult(); await gate.Task; return rig.Reply(m); };
        var blocker = rig.Broker.ReadTabsAsync(K(99), 0, deadline: Hour);
        Assert.True(started.Task.Wait(TimeSpan.FromSeconds(5)));
        return (gate, blocker);
    }

    [Fact]
    public async Task Read_success_populates_cache_and_clears_error()
    {
        using var rig = new Rig();
        var r = await rig.Broker.ReadTabsAsync(K(1), 0);
        Assert.Equal(ResultStatus.Success, r.Status);
        Assert.True(rig.Broker.Cache.Get(K(1))!.CanSelect);
        Assert.Null(rig.Broker.LastError);
    }

    [Theory]
    [InlineData(1499, true)]
    [InlineData(1500, false)]
    [InlineData(1501, false)]
    public async Task Read_deadline_enforced_before_work(int elapsedMs, bool executes)
    {
        using var rig = new Rig();
        var (gate, blocker) = Block(rig);
        var read = rig.Broker.ReadTabsAsync(K(1), 0);
        rig.Now = T0.AddMilliseconds(elapsedMs);
        gate.SetResult(); await blocker;
        var r = await read;
        Assert.Equal(executes ? ResultStatus.Success : ResultStatus.Timeout, r.Status);
        Assert.Equal(executes ? 2 : 1, rig.CountOf("ReadTabs"));
    }

    [Theory]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    [InlineData(2001, false)]
    public async Task Select_deadline_enforced_before_work(int elapsedMs, bool executes)
    {
        using var rig = new Rig();
        var read = await rig.Broker.ReadTabsAsync(K(1), 0);
        var (gate, blocker) = Block(rig);
        var select = rig.Broker.SelectTabAsync(K(1), read.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        rig.Now = T0.AddMilliseconds(elapsedMs);
        gate.SetResult(); await blocker;
        var r = await select;
        Assert.Equal(executes ? ResultStatus.Success : ResultStatus.Timeout, r.Status);
        Assert.Equal(executes ? 1 : 0, rig.CountOf("SelectTab"));
    }

    [Fact]
    public async Task Hung_worker_is_invalidated_and_restarted_only_after_cooldown()
    {
        using var rig = new Rig();
        await rig.Broker.ReadTabsAsync(K(1), 0);
        var epoch = rig.Broker.Epoch;
        rig.Handler = (_, _) => new TaskCompletionSource<Protocol.Message>().Task;
        var r = await rig.Broker.ReadTabsAsync(K(2), 0, deadline: TimeSpan.FromMilliseconds(50));
        Assert.Equal(ResultStatus.Timeout, r.Status);
        Assert.Equal(0, rig.Restarts);
        Assert.NotEqual(epoch, rig.Broker.Epoch);
        Assert.Equal(0, rig.Broker.Cache.Count);
        Assert.Equal(ResultStatus.Timeout, rig.Broker.LastError!.Status);
        Assert.True(rig.Broker.IsRecovering);
        rig.Handler = (m, _) => Task.FromResult(rig.Reply(m));
        rig.NowMs = 999;
        Assert.Equal(ResultStatus.Unavailable, (await rig.Broker.ReadTabsAsync(K(2), 0)).Status);
        Assert.False(rig.Broker.CanRetry);
        rig.NowMs = 1000;
        Assert.True(rig.Broker.CanRetry);
        Assert.Equal(ResultStatus.Success, (await rig.Broker.ReadTabsAsync(K(2), 0)).Status);
        Assert.Equal(1, rig.Restarts);
        Assert.False(rig.Broker.IsRecovering);
        Assert.False(Protocol.ShouldCaptureIcons(rig.Calls[^1].Payload));
    }

    [Fact]
    public async Task Restart_failure_increases_cooldown()
    {
        long now = 0;
        using var broker = new UiaWorkerBroker((_, _) => throw new IOException("pipe broke"),
            _ => throw new InvalidOperationException("no"), () => T0, null, monotonicClock: () => now);
        Assert.Equal(ResultStatus.Failed, (await broker.ReadTabsAsync(K(1), 0)).Status);
        now = 1000;
        Assert.Equal(ResultStatus.Failed, (await broker.ReadTabsAsync(K(1), 0, kind: TabReadKind.Retry)).Status);
        Assert.True(broker.IsRecovering);
        now = 5999;
        Assert.False(broker.CanRetry);
        now = 6000;
        Assert.True(broker.CanRetry);
    }

    [Fact]
    public async Task Io_failure_stops_worker_without_starting_another()
    {
        using var rig = new Rig();
        rig.Handler = (_, _) => throw new IOException("pipe broke");
        var r = await rig.Broker.ReadTabsAsync(K(1), 0);
        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(0, rig.Restarts);
        Assert.True(rig.Broker.IsRecovering);
    }

    [Fact]
    public async Task Repeated_timeouts_allow_only_two_restarts_and_require_manual_retry()
    {
        using var rig = new Rig();
        rig.Handler = async (m, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return rig.Reply(m);
        };
        var deadline = TimeSpan.FromMilliseconds(20);
        for (int i = 0; i <= 12; i++)
        {
            rig.NowMs = i * 1000;
            var result = await rig.Broker.ReadTabsAsync(K(1), 0, deadline: deadline);
            Assert.Equal(i is 0 or 1 or 6 ? ResultStatus.Timeout : ResultStatus.Unavailable, result.Status);
            foreach (var kind in new[] { TabReadKind.Background, TabReadKind.HoverPrefetch, TabReadKind.PopupRefresh, TabReadKind.WindowActivated })
                Assert.Equal(ResultStatus.Unavailable, (await rig.Broker.ReadTabsAsync(K(2), 0, kind: kind)).Status);
        }
        Assert.Equal(2, rig.Restarts);
        Assert.Equal(3, rig.Calls.Count);
        var key = K(1);
        var world = new FakeWorld();
        world.Windows[key.Hwnd] = ChromeWindowFilterTests.W(key.Hwnd, pid: key.ProcessId, start: key.ProcessStartTimeTicks);
        Assert.Equal(ActivationOutcome.Activated, new WindowActivator(world, world).Toggle(key, false).Outcome);
        Assert.True(rig.Broker.IsRecovering);
        rig.NowMs = 36_000;
        rig.Handler = (m, _) => Task.FromResult(rig.Reply(m));
        Assert.True(rig.Broker.CanRetry);
        Assert.Equal(ResultStatus.Unavailable, (await rig.Broker.ReadTabsAsync(K(1), 0)).Status);
        Assert.Equal(ResultStatus.Success, (await rig.Broker.ReadTabsAsync(K(1), 0, kind: TabReadKind.Retry)).Status);
        Assert.False(rig.Broker.IsRecovering);
    }

    [Fact]
    public async Task Successful_reads_reset_backoff_but_keep_the_rolling_restart_limit()
    {
        using var rig = new Rig();
        for (int i = 0; i < 3; i++)
        {
            rig.Handler = (_, _) => throw new IOException();
            await rig.Broker.ReadTabsAsync(K(1), 0);
            rig.NowMs += 1000;
            rig.Handler = (m, _) => Task.FromResult(rig.Reply(m));
            var probe = await rig.Broker.ReadTabsAsync(K(1), 0, kind: TabReadKind.Retry);
            Assert.Equal(i < 2 ? ResultStatus.Success : ResultStatus.Unavailable, probe.Status);
        }
        Assert.Equal(2, rig.Restarts);
        rig.NowMs = 30_999;
        Assert.False(rig.Broker.CanRetry);
        rig.NowMs = 31_000;
        Assert.True(rig.Broker.CanRetry);
    }

    [Fact]
    public async Task Startup_followed_by_an_incomplete_read_keeps_automatic_reads_paused()
    {
        using var rig = new Rig();
        rig.Handler = (_, _) => throw new IOException();
        await rig.Broker.ReadTabsAsync(K(1), 0);
        rig.NowMs = 1000;
        rig.Handler = (m, _) => Task.FromResult(rig.Reply(m, "Incomplete"));
        Assert.Equal(ResultStatus.Incomplete, (await rig.Broker.ReadTabsAsync(K(1), 0, kind: TabReadKind.Retry)).Status);
        Assert.True(rig.Broker.IsRecovering);
        Assert.True(rig.Broker.CanRetry);
        Assert.Equal(ResultStatus.Unavailable, (await rig.Broker.ReadTabsAsync(K(1), 0, kind: TabReadKind.Background)).Status);
        Assert.Equal(2, rig.Calls.Count);
        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public async Task Restart_time_does_not_consume_the_read_budget()
    {
        long nowMs = 0;
        var now = T0;
        DateTimeOffset wireDeadline = default;
        using var broker = new UiaWorkerBroker((request, _) =>
        {
            if (nowMs == 0) throw new IOException();
            wireDeadline = request.Payload.GetProperty("deadlineUtc").GetDateTimeOffset();
            return Task.FromResult(new Protocol.Message("Result", request.WorkerEpoch, request.RequestId,
                JsonSerializer.SerializeToElement(new { status = "Incomplete" })));
        }, _ => { now = now.AddSeconds(2); return Task.CompletedTask; }, () => now, null, monotonicClock: () => nowMs);
        await broker.ReadTabsAsync(K(1), 0);
        nowMs = 1000;
        Assert.Equal(ResultStatus.Incomplete, (await broker.ReadTabsAsync(K(1), 0, kind: TabReadKind.Retry)).Status);
        Assert.Equal(now + UiaWorkerBroker.ReadDeadline, wireDeadline);
    }

    [Theory]
    [InlineData(TabReadKind.Background, false)]
    [InlineData(TabReadKind.HoverPrefetch, false)]
    [InlineData(TabReadKind.PopupRefresh, false)]
    [InlineData(TabReadKind.Retry, false)]
    [InlineData(TabReadKind.PopupOpen, true)]
    [InlineData(TabReadKind.WindowActivated, true)]
    public async Task Only_foreground_read_triggers_request_favicon_capture(TabReadKind kind, bool captureIcons)
    {
        using var rig = new Rig();
        var result = await rig.Broker.ReadTabsAsync(K(1), 0, kind: kind);
        Assert.Equal("A", Assert.Single(result.Snapshot!.Tabs).Title);
        Assert.Equal(captureIcons, Protocol.ShouldCaptureIcons(Assert.Single(rig.Calls).Payload));
    }

    [Fact]
    public async Task Stale_epoch_and_request_id_replies_are_discarded_without_touching_error()
    {
        using var rig = new Rig();
        rig.Handler = (m, _) => Task.FromResult(rig.Reply(m, "Unavailable"));
        Assert.Equal(ResultStatus.Unavailable, (await rig.Broker.ReadTabsAsync(K(1), 0)).Status);
        var error = rig.Broker.LastError;
        Assert.NotNull(error);

        rig.Handler = (m, _) => Task.FromResult(rig.Reply(m, epoch: m.WorkerEpoch + 1));
        Assert.Equal(ResultStatus.Stale, (await rig.Broker.ReadTabsAsync(K(1), 0)).Status);
        rig.Handler = (m, _) => Task.FromResult(rig.Reply(m, id: m.RequestId + 100));
        Assert.Equal(ResultStatus.Stale, (await rig.Broker.ReadTabsAsync(K(1), 0)).Status);
        Assert.Same(error, rig.Broker.LastError);
        Assert.Equal(0, rig.Broker.Cache.Count);
    }

    [Fact]
    public async Task Epoch_change_during_call_discards_result()
    {
        using var rig = new Rig();
        rig.Handler = (m, _) => { rig.Broker.InvalidateEpoch(); return Task.FromResult(rig.Reply(m)); };
        Assert.Equal(ResultStatus.Stale, (await rig.Broker.ReadTabsAsync(K(1), 0)).Status);
        Assert.Equal(0, rig.Broker.Cache.Count);
    }

    [Fact]
    public async Task Popup_generation_and_window_generation_mismatch_discard_result()
    {
        using var rig = new Rig();
        rig.Handler = (m, _) => { rig.Broker.PopupGeneration = 5; return Task.FromResult(rig.Reply(m)); };
        Assert.Equal(ResultStatus.Stale, (await rig.Broker.ReadTabsAsync(K(1), 0)).Status);
        Assert.Equal(0, rig.Broker.Cache.Count);

        rig.Handler = (m, _) => { rig.WindowCurrent = false; return Task.FromResult(rig.Reply(m)); };
        Assert.Equal(ResultStatus.Stale, (await rig.Broker.ReadTabsAsync(K(1), 5)).Status);
        Assert.Equal(0, rig.Broker.Cache.Count);
    }

    [Fact]
    public async Task Select_rejects_stale_cache_without_worker_call()
    {
        using var rig = new Rig();
        var read = await rig.Broker.ReadTabsAsync(K(1), 0);
        rig.Now = T0.AddMilliseconds(5000);
        var r = await rig.Broker.SelectTabAsync(K(1), read.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        Assert.Equal(ResultStatus.Stale, r.Status);
        Assert.Equal(0, rig.CountOf("SelectTab"));
    }

    [Fact]
    public async Task Select_success_invalidates_cached_snapshot()
    {
        using var rig = new Rig();
        var read = await rig.Broker.ReadTabsAsync(K(1), 0);
        var r = await rig.Broker.SelectTabAsync(K(1), read.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        Assert.Equal(ResultStatus.Success, r.Status);
        Assert.Null(rig.Broker.Cache.Get(K(1)));
    }

    [Fact]
    public async Task Latest_read_replaces_older_and_select_has_priority()
    {
        using var rig = new Rig();
        var seed = await rig.Broker.ReadTabsAsync(K(1), 0);
        var (gate, blocker) = Block(rig);
        var old = rig.Broker.ReadTabsAsync(K(2), 0);
        var latest = rig.Broker.ReadTabsAsync(K(3), 0);
        var select = rig.Broker.SelectTabAsync(K(1), seed.Snapshot!.SnapshotId, new TabToken("tok"), 0);
        Assert.Equal(ResultStatus.Cancelled, (await old).Status);
        gate.SetResult(); await blocker;
        Assert.Equal(ResultStatus.Success, (await select).Status);
        Assert.Equal(ResultStatus.Success, (await latest).Status);
        Assert.Equal(new[] { "ReadTabs", "ReadTabs", "SelectTab", "ReadTabs" }, rig.Calls.Select(c => c.Type).ToArray());
    }

    [Fact]
    public async Task Cancel_affects_only_unstarted_work()
    {
        using var rig = new Rig();
        var (gate, blocker) = Block(rig);
        using var cts = new CancellationTokenSource();
        var queued = rig.Broker.ReadTabsAsync(K(2), 0, cts.Token);
        cts.Cancel();
        Assert.Equal(ResultStatus.Cancelled, (await queued).Status);

        gate.SetResult();
        Assert.Equal(ResultStatus.Success, (await blocker).Status);
        Assert.Equal(1, rig.CountOf("ReadTabs"));
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task Cancel_after_start_suppresses_application_only()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        rig.Handler = async (m, _) => { started.SetResult(); await gate.Task; return rig.Reply(m); };
        var read = rig.Broker.ReadTabsAsync(K(1), 0, cts.Token);
        await started.Task;
        cts.Cancel();
        Assert.False(read.IsCompleted);
        gate.SetResult();
        Assert.Equal(ResultStatus.Cancelled, (await read).Status);
        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task Dispose_cancels_queued_work_and_clears_cache()
    {
        var rig = new Rig();
        await rig.Broker.ReadTabsAsync(K(1), 0);
        var (gate, blocker) = Block(rig);
        var queued = rig.Broker.ReadTabsAsync(K(2), 0);
        rig.Broker.Dispose();
        Assert.Equal(ResultStatus.Cancelled, (await queued).Status);
        Assert.Equal(0, rig.Broker.Cache.Count);
        gate.SetResult(); await blocker;
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rig.Broker.ReadTabsAsync(K(1), 0));
    }
}
