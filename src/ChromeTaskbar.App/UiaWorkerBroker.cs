using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public sealed record BrokerResult(ResultStatus Status, TabSnapshot? Snapshot = null);

/// <summary>Sends one request to the worker and returns the reply (default: named pipe).</summary>
public delegate Task<Protocol.Message> WorkerCall(Protocol.Message request, CancellationToken cancellationToken);

public sealed class UiaWorkerBroker : IDisposable
{
    public static readonly TimeSpan ReadDeadline = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan SelectDeadline = TimeSpan.FromMilliseconds(2000);

    private sealed class Job
    {
        public required bool IsSelect;
        public required Func<long, DateTimeOffset, Task<BrokerResult>> Run;
        public required DateTimeOffset Deadline;
        public required CancellationToken Token;
        public readonly TaskCompletionSource<BrokerResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }

    private readonly Action<string> diagnostics;
    private readonly WorkerCall? injectedCall;
    private readonly Func<CancellationToken, Task>? injectedRestart;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<WindowKey, bool> isCurrentWindow;
    private readonly object gate = new();
    private readonly Queue<Job> selects = new();
    private Job? pendingRead;
    private bool pumping;
    private long nextRequestId;
    private long popupGeneration;
    private string? workerPath;
    private NamedPipeServerStream? pipe;
    private Process? worker;
    private long epoch;
    private volatile bool disposed;

    public UiaWorkerBroker() : this(null, null, null, null) { }

    public UiaWorkerBroker(WorkerCall? call, Func<CancellationToken, Task>? restart, Func<DateTimeOffset>? clock, Func<WindowKey, bool>? isCurrentWindow, TabSnapshotCache? cache = null, Action<string>? diagnostics = null)
    {
        this.diagnostics = diagnostics ?? (m => Trace.TraceError(m));
        injectedCall = call;
        injectedRestart = restart;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.isCurrentWindow = isCurrentWindow ?? (_ => true);
        Cache = cache ?? new TabSnapshotCache(this.clock);
    }

    public WorkerEpoch Epoch => new(Interlocked.Read(ref epoch));
    public string? PipeName { get; private set; }
    public TabSnapshotCache Cache { get; }
    /// <summary>Last non-stale failure; stale/cancelled discards never overwrite it. Cleared by a successful result.</summary>
    public BrokerResult? LastError { get; private set; }
    /// <summary>True when the worker could not be recreated (UI should offer a retry button).</summary>
    public bool RecoveryFailed { get; private set; }
    /// <summary>Most recent diagnostic message (also sent to the injected sink, default Trace.TraceError).</summary>
    public string? LastDiagnostic { get; private set; }

    /// <summary>Generation of the currently open popup; results for another generation are discarded.</summary>
    public long PopupGeneration { get => Interlocked.Read(ref popupGeneration); set => Interlocked.Exchange(ref popupGeneration, value); }

    public async Task StartAsync(string workerPath, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        this.workerPath = workerPath;
        Stop();
        Cache.Clear();
        PipeName = $"ChromeTaskbar.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var nextEpoch = Interlocked.Increment(ref epoch);
        worker = Process.Start(new ProcessStartInfo(workerPath, $"--pipe \"{PipeName}\" --epoch {nextEpoch}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException("Could not start UIA worker.");

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != (uint)worker.Id)
            {
                throw new Protocol.ProtocolException("Named-pipe client PID does not match the spawned worker.");
            }

            var hello = await Protocol.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (hello.Type != "Hello" || hello.WorkerEpoch != (ulong)nextEpoch)
            {
                throw new Protocol.ProtocolException("Worker did not send a matching Hello.");
            }
            RecoveryFailed = false;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Invalidates epoch, cache and tokens, and stops the worker.</summary>
    public void InvalidateEpoch()
    {
        Interlocked.Increment(ref epoch);
        Cache.Clear();
        Stop();
    }

    public void Stop()
    {
        pipe?.Dispose(); pipe = null;
        if (worker is { HasExited: false })
        {
            try { worker.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The worker exited (or became inaccessible) between the HasExited check and Kill; it is being discarded anyway.
                Report($"UIA worker kill skipped: {e.Message}");
            }
        }
        worker?.Dispose(); worker = null;
        PipeName = null;
    }

    // ---- bounded request queue -------------------------------------------------

    /// <summary>Reads tabs. A newer pending read replaces an older unstarted one (latest hover wins).</summary>
    public Task<BrokerResult> ReadTabsAsync(WindowKey key, long forPopupGeneration, CancellationToken cancellationToken = default, TimeSpan? deadline = null)
    {
        ThrowIfDisposed();
        var due = clock().ToUniversalTime() + (deadline ?? ReadDeadline);
        return Enqueue(isSelect: false, due, cancellationToken, (sentEpoch, d) => RunReadAsync(key, forPopupGeneration, sentEpoch, d));
    }

    /// <summary>Selects a tab; takes priority over pending reads. Stale (non-fresh) snapshots are rejected without a worker call.</summary>
    public Task<BrokerResult> SelectTabAsync(WindowKey key, SnapshotId snapshotId, TabToken token, long forPopupGeneration, CancellationToken cancellationToken = default, TimeSpan? deadline = null)
    {
        ThrowIfDisposed();
        var due = clock().ToUniversalTime() + (deadline ?? SelectDeadline);
        return Enqueue(isSelect: true, due, cancellationToken, (sentEpoch, d) => RunSelectAsync(key, snapshotId, token, forPopupGeneration, sentEpoch, d));
    }

    private Task<BrokerResult> Enqueue(bool isSelect, DateTimeOffset due, CancellationToken token, Func<long, DateTimeOffset, Task<BrokerResult>> run)
    {
        var job = new Job { IsSelect = isSelect, Run = run, Deadline = due, Token = token };
        Job? replaced = null;
        bool start = false;
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(UiaWorkerBroker));
            if (isSelect) selects.Enqueue(job); else { replaced = pendingRead; pendingRead = job; }
            if (!pumping) { pumping = start = true; }
        }
        // Cancel only affects unstarted work; a started job is not interrupted.
        job.Registration = token.Register(() => CancelUnstarted(job));
        replaced?.Completion.TrySetResult(new BrokerResult(ResultStatus.Cancelled));
        if (start) _ = Task.Run(PumpAsync);
        return job.Completion.Task;
    }

    private void CancelUnstarted(Job job)
    {
        bool removed = false;
        lock (gate)
        {
            if (!job.IsSelect && ReferenceEquals(pendingRead, job)) { pendingRead = null; removed = true; }
            else if (job.IsSelect && selects.Contains(job))
            {
                var keep = selects.Where(j => !ReferenceEquals(j, job)).ToArray();
                selects.Clear();
                foreach (var j in keep) selects.Enqueue(j);
                removed = true;
            }
        }
        if (removed) job.Completion.TrySetResult(new BrokerResult(ResultStatus.Cancelled));
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Job job;
            lock (gate)
            {
                if (disposed) { pumping = false; return; }
                if (selects.Count > 0) job = selects.Dequeue();
                else if (pendingRead is not null) { job = pendingRead; pendingRead = null; }
                else { pumping = false; return; }
            }
            BrokerResult result;
            try
            {
                if (job.Token.IsCancellationRequested) result = new BrokerResult(ResultStatus.Cancelled);
                else if (clock().ToUniversalTime() >= job.Deadline) result = new BrokerResult(ResultStatus.Timeout); // deadline enforced before any work
                else
                {
                    result = await job.Run(Interlocked.Read(ref epoch), job.Deadline).ConfigureAwait(false);
                    // Cancel after start only suppresses UI application of the result.
                    if (job.Token.IsCancellationRequested && result.Status == ResultStatus.Success) result = new BrokerResult(ResultStatus.Cancelled);
                }
            }
            catch (Exception e)
            {
                Report($"UiaWorkerBroker: job failed unexpectedly: {e.GetType().Name}: {e.Message}");
                result = new BrokerResult(ResultStatus.Failed);
            }
            job.Registration.Dispose();
            job.Completion.TrySetResult(result);
        }
    }

    // ---- request execution -----------------------------------------------------

    private async Task<BrokerResult> RunReadAsync(WindowKey key, long forPopup, long sentEpoch, DateTimeOffset deadline)
    {
        var id = (ulong)Interlocked.Increment(ref nextRequestId);
        var request = Build("ReadTabs", sentEpoch, id, key, deadline, null);
        var (outcome, reply) = await CallAsync(request, deadline).ConfigureAwait(false);
        if (outcome is not null) return Record(outcome);
        if (IsStale(reply!, sentEpoch, id, key, forPopup)) return new BrokerResult(ResultStatus.Stale);
        try
        {
            var status = ParseStatus(reply!.Payload);
            if (status != ResultStatus.Success) return Record(new BrokerResult(status));
            var p = reply.Payload;
            var tabs = p.GetProperty("tabs").EnumerateArray().Select(t => new TabEntry(
                new TabToken(t.GetProperty("tabToken").GetString()!), t.GetProperty("title").GetString()!,
                t.GetProperty("displayOrder").GetInt32(), t.GetProperty("isSelected").GetBoolean(), t.GetProperty("canSelect").GetBoolean())).ToList();
            var snapshot = new TabSnapshot(new WorkerEpoch(sentEpoch), key, new SnapshotId(p.GetProperty("snapshotId").GetGuid()),
                DateTimeOffset.Parse(p.GetProperty("capturedAtUtc").GetString()!, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                TabAvailability.Available, tabs);
            Cache.Put(snapshot);
            LastError = null;
            return new BrokerResult(ResultStatus.Success, snapshot);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException)
        {
            return Record(new BrokerResult(ResultStatus.ProtocolError));
        }
    }

    private async Task<BrokerResult> RunSelectAsync(WindowKey key, SnapshotId snapshotId, TabToken token, long forPopup, long sentEpoch, DateTimeOffset deadline)
    {
        var cached = Cache.Get(key);
        if (cached is null || !cached.CanSelect || cached.Snapshot.SnapshotId != snapshotId || cached.Snapshot.WorkerEpoch.Value != sentEpoch ||
            !cached.Snapshot.Tabs.Any(t => t.Token == token && t.CanSelect))
            return new BrokerResult(ResultStatus.Stale);
        if (forPopup != PopupGeneration || !isCurrentWindow(key)) return new BrokerResult(ResultStatus.Stale);

        var id = (ulong)Interlocked.Increment(ref nextRequestId);
        var request = Build("SelectTab", sentEpoch, id, key, deadline, new Dictionary<string, string> { ["snapshotId"] = snapshotId.Value.ToString(), ["tabToken"] = token.Value });
        var (outcome, reply) = await CallAsync(request, deadline).ConfigureAwait(false);
        if (outcome is not null) return Record(outcome);
        if (IsStale(reply!, sentEpoch, id, key, forPopup)) return new BrokerResult(ResultStatus.Stale);
        try
        {
            var status = ParseStatus(reply!.Payload);
            Cache.Invalidate(key); // selection changes the strip; require a fresh read
            if (status != ResultStatus.Success) return Record(new BrokerResult(status));
            LastError = null;
            return new BrokerResult(ResultStatus.Success);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { return Record(new BrokerResult(ResultStatus.ProtocolError)); }
    }

    private bool IsStale(Protocol.Message reply, long sentEpoch, ulong id, WindowKey key, long forPopup) =>
        reply.Type != "Result" || reply.RequestId != id || reply.WorkerEpoch != (ulong)sentEpoch || Interlocked.Read(ref epoch) != sentEpoch ||
        forPopup != PopupGeneration || !isCurrentWindow(key);

    private BrokerResult Record(BrokerResult result)
    {
        if (result.Status is not (ResultStatus.Stale or ResultStatus.Cancelled)) LastError = result;
        return result;
    }

    /// <summary>Calls the worker with the deadline. On hang or IO failure, stops/recreates the worker and invalidates epoch/cache/tokens.</summary>
    private async Task<(BrokerResult? Outcome, Protocol.Message? Reply)> CallAsync(Protocol.Message request, DateTimeOffset deadline)
    {
        var remaining = deadline - clock().ToUniversalTime();
        if (remaining <= TimeSpan.Zero) return (new BrokerResult(ResultStatus.Timeout), null);
        using var cts = new CancellationTokenSource();
        try
        {
            var call = (injectedCall ?? PipeCallAsync)(request, cts.Token);
            var winner = await Task.WhenAny(call, Task.Delay(remaining)).ConfigureAwait(false);
            if (winner != call)
            {
                cts.Cancel();
                _ = call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                await RecoverAsync().ConfigureAwait(false);
                return (new BrokerResult(ResultStatus.Timeout), null);
            }
            return (null, await call.ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            if (disposed) return (new BrokerResult(ResultStatus.Cancelled), null);
            await RecoverAsync().ConfigureAwait(false);
            return (new BrokerResult(ResultStatus.Failed), null);
        }
    }

    private async Task RecoverAsync()
    {
        InvalidateEpoch();
        if (disposed) return;
        try
        {
            if (injectedRestart is not null) await injectedRestart(CancellationToken.None).ConfigureAwait(false);
            else if (workerPath is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await StartAsync(workerPath, cts.Token).ConfigureAwait(false);
            }
            RecoveryFailed = false;
        }
        catch (Exception e)
        {
            Report($"UiaWorkerBroker: worker recovery failed: {e.GetType().Name}: {e.Message}");
            RecoveryFailed = true;
        }
    }

    private async Task<Protocol.Message> PipeCallAsync(Protocol.Message request, CancellationToken token)
    {
        var p = pipe ?? throw new InvalidOperationException("Worker is not running.");
        await Protocol.WriteFrameAsync(p, request, token).ConfigureAwait(false);
        while (true)
        {
            var reply = await Protocol.ReadFrameAsync(p, token).ConfigureAwait(false);
            if (reply.RequestId == request.RequestId || reply.WorkerEpoch != request.WorkerEpoch) return reply;
        }
    }

    private static Protocol.Message Build(string type, long sentEpoch, ulong id, WindowKey key, DateTimeOffset deadline, Dictionary<string, string>? extra)
    {
        var payload = new Dictionary<string, object>
        {
            ["windowKey"] = new { hwnd = Protocol.SerializeUnsigned((ulong)key.Hwnd), processId = Protocol.SerializeUnsigned((ulong)(uint)key.ProcessId), processStartTimeUtc = Protocol.SerializeUnsigned((ulong)key.ProcessStartTimeTicks), generation = Protocol.SerializeUnsigned((ulong)key.Generation) },
            ["deadlineUtc"] = deadline.ToUniversalTime().ToString("O"),
        };
        if (extra is not null) foreach (var (k, v) in extra) payload[k] = v;
        return new Protocol.Message(type, (ulong)sentEpoch, id, JsonSerializer.SerializeToElement(payload));
    }

    private static ResultStatus ParseStatus(JsonElement payload) => payload.GetProperty("status").GetString() switch
    {
        "Success" => ResultStatus.Success,
        "Stale" => ResultStatus.Stale,
        "Unavailable" => ResultStatus.Unavailable,
        "Incomplete" => ResultStatus.Incomplete,
        "WindowGone" => ResultStatus.WindowGone,
        "Timeout" => ResultStatus.Timeout,
        "Cancelled" => ResultStatus.Cancelled,
        "ProtocolError" => ResultStatus.ProtocolError,
        _ => ResultStatus.Failed,
    };

    public void Dispose()
    {
        var abandoned = new List<Job>();
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            abandoned.AddRange(selects); selects.Clear();
            if (pendingRead is not null) { abandoned.Add(pendingRead); pendingRead = null; }
        }
        foreach (var j in abandoned) { j.Registration.Dispose(); j.Completion.TrySetResult(new BrokerResult(ResultStatus.Cancelled)); }
        Stop();
        Cache.Clear();
    }

    private void Report(string message)
    {
        LastDiagnostic = message;
        try { diagnostics(message); }
        catch (Exception) { /* a faulty sink must never break the broker */ }
    }

    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(UiaWorkerBroker)); }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
