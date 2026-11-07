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
                () => Now, _ => WindowCurrent);
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
    public async Task Hung_worker_is_recreated_and_state_invalidated()
    {
        using var rig = new Rig();
        await rig.Broker.ReadTabsAsync(K(1), 0);
        var epoch = rig.Broker.Epoch;
        rig.Handler = (_, _) => new TaskCompletionSource<Protocol.Message>().Task;
        var r = await rig.Broker.ReadTabsAsync(K(2), 0, deadline: TimeSpan.FromMilliseconds(50));
        Assert.Equal(ResultStatus.Timeout, r.Status);
        Assert.Equal(1, rig.Restarts);
        Assert.NotEqual(epoch, rig.Broker.Epoch);
        Assert.Equal(0, rig.Broker.Cache.Count);
        Assert.Equal(ResultStatus.Timeout, rig.Broker.LastError!.Status);
    }

    [Fact]
    public async Task Restart_failure_sets_recovery_failed()
    {
        using var broker = new UiaWorkerBroker((_, _) => new TaskCompletionSource<Protocol.Message>().Task,
            _ => throw new InvalidOperationException("no"), null, null);
        var r = await broker.ReadTabsAsync(K(1), 0, deadline: TimeSpan.FromMilliseconds(50));
        Assert.Equal(ResultStatus.Timeout, r.Status);
        Assert.True(broker.RecoveryFailed);
    }

    [Fact]
    public async Task Io_failure_recovers_worker()
    {
        using var rig = new Rig();
        rig.Handler = (_, _) => throw new IOException("pipe broke");
        var r = await rig.Broker.ReadTabsAsync(K(1), 0);
        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(1, rig.Restarts);
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
