using System.IO;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public sealed record BrokerResult(ResultStatus Status, TabSnapshot? Snapshot = null);

public enum TabReadKind { PopupOpen, Background, HoverPrefetch, PopupRefresh, WindowActivated, Retry }

/// <summary>Sends one request to the worker and returns the reply (default: named pipe).</summary>
public delegate Task<Protocol.Message> WorkerCall(Protocol.Message request, CancellationToken cancellationToken);

public sealed class UiaWorkerBroker : IDisposable
{
    public static readonly TimeSpan ReadDeadline = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan SelectDeadline = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan WorkerStartupDeadline = TimeSpan.FromSeconds(5);
    private const long RestartWindowMs = 30_000;
    private const int MaxRestartsPerWindow = 2;
    private const int FailuresBeforeManualRetry = 3;

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
    private readonly Func<long> monotonicClock;
    private readonly Func<WindowKey, bool> isCurrentWindow;
    private readonly object gate = new();
    private readonly Queue<Job> selects = new();
    private Job? pendingRead;
    private bool pumping;
    private long nextRequestId;
    private long popupGeneration;
    private readonly string? workerPath;
    private readonly RestartBackoff backoff = new();
    private readonly Queue<long> restartAttempts = new();
    private NamedPipeServerStream? pipe;
    private Process? worker;
    private bool connected;
    private volatile bool recovering;
    private long epoch;
    private volatile bool disposed;

    public UiaWorkerBroker(string workerPath) : this(null, null, null, null, diagnostics: UiaDiagnosticLog.Write)
    {
        this.workerPath = workerPath;
    }

    public UiaWorkerBroker(WorkerCall? call, Func<CancellationToken, Task>? restart, Func<DateTimeOffset>? clock, Func<WindowKey, bool>? isCurrentWindow, TabSnapshotCache? cache = null, Action<string>? diagnostics = null, Func<long>? monotonicClock = null)
    {
        this.diagnostics = diagnostics ?? (m => Trace.TraceError(m));
        injectedCall = call;
        injectedRestart = restart;
        connected = call is not null;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.monotonicClock = monotonicClock ?? (() => MonotonicClock.NowMs);
        this.isCurrentWindow = isCurrentWindow ?? (_ => true);
        Cache = cache ?? new TabSnapshotCache(this.clock);
    }

    public WorkerEpoch Epoch => new(Interlocked.Read(ref epoch));
    public TabSnapshotCache Cache { get; }
    /// <summary>Last non-stale failure; stale/cancelled discards never overwrite it. Cleared by a successful result.</summary>
    public BrokerResult? LastError { get; private set; }
    public bool IsRecovering => recovering;
    public bool CanRetry
    {
        get
        {
            if (!recovering) return false;
            lock (gate) return !disposed && recovering && (connected || CanRestart(monotonicClock()));
        }
    }
    /// <summary>Most recent diagnostic message (also sent to the injected sink, default Trace.TraceError).</summary>
    public string? LastDiagnostic { get; private set; }

    /// <summary>Generation of the currently open popup; results for another generation are discarded.</summary>
    public long PopupGeneration { get => Interlocked.Read(ref popupGeneration); set => Interlocked.Exchange(ref popupGeneration, value); }

    private async Task StartWorkerAsync(CancellationToken cancellationToken)
    {
        Process? previous;
        lock (gate)
        {
            ThrowIfDisposed();
            previous = worker;
        }
        if (previous is not null)
            await previous.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        NamedPipeServerStream newPipe;
        int pid;
        long nextEpoch;
        lock (gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (previous is not null)
                Report($"worker exited workerPid={previous.Id} exitCode={previous.ExitCode}");
            worker?.Dispose();
            worker = null;
            var pipeName = $"ChromeTaskbar.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
            pipe = newPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            nextEpoch = Interlocked.Increment(ref epoch);
            var info = new ProcessStartInfo(workerPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(pipeName);
            info.ArgumentList.Add("--epoch");
            info.ArgumentList.Add(nextEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            worker = Process.Start(info) ?? throw new InvalidOperationException("Could not start UIA worker.");
            pid = worker.Id;
            Report($"worker started workerPid={pid} epoch={nextEpoch}");
            worker.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith(Protocol.WorkerDiagnosticPrefix, StringComparison.Ordinal))
                    Report(line);
            };
            worker.BeginErrorReadLine();
        }

        await newPipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(newPipe.SafePipeHandle, out var clientPid) || clientPid != (uint)pid)
            throw new Protocol.ProtocolException("Named-pipe client PID does not match the spawned worker.");
        var hello = await Protocol.ReadFrameAsync(newPipe, cancellationToken).ConfigureAwait(false);
        if (hello.Type != "Hello" || hello.WorkerEpoch != (ulong)nextEpoch)
            throw new Protocol.ProtocolException("Worker did not send a matching Hello.");
        if (!Protocol.SupportsCaptureControl(hello))
        {
            Report($"worker incompatible workerPid={pid} epoch={nextEpoch} reason=missingCaptureControl");
            throw new Protocol.ProtocolException("UIA worker does not support optional favicon capture.");
        }
    }

    /// <summary>Invalidates epoch, cache and tokens, and stops the worker.</summary>
    public void InvalidateEpoch()
    {
        lock (gate)
        {
            Interlocked.Increment(ref epoch);
            Cache.Clear();
            StopWorker();
        }
    }

    // Keep the process handle until exit is confirmed. Only the process started above may be stopped.
    private void StopWorker()
    {
        connected = false;
        pipe?.Dispose(); pipe = null;
        if (worker is { HasExited: false })
        {
            try { worker.Kill(); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Report($"worker stop failed workerPid={worker.Id} reason={e.GetType().Name}");
            }
        }
    }

    // ---- bounded request queue -------------------------------------------------

    /// <summary>Reads tabs. A newer pending read replaces an older unstarted one (latest hover wins).</summary>
    public Task<BrokerResult> ReadTabsAsync(WindowKey key, long forPopupGeneration, CancellationToken cancellationToken = default, TimeSpan? deadline = null, TabReadKind kind = TabReadKind.PopupOpen)
    {
        ThrowIfDisposed();
        var due = clock().ToUniversalTime() + (deadline ?? ReadDeadline);
        return Enqueue(isSelect: false, due, cancellationToken, (_, d) => RunReadAsync(key, forPopupGeneration, d, kind, cancellationToken));
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
            job.Registration = token.Register(() => CancelUnstarted(job));
            if (!pumping) { pumping = start = true; }
        }
        replaced?.Registration.Dispose();
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
        if (removed)
        {
            job.Registration.Unregister();
            job.Completion.TrySetResult(new BrokerResult(ResultStatus.Cancelled));
        }
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

    private async Task<BrokerResult> RunReadAsync(WindowKey key, long forPopup, DateTimeOffset deadline, TabReadKind kind, CancellationToken token)
    {
        var readBudget = deadline - clock().ToUniversalTime();
        if (readBudget <= TimeSpan.Zero) return Record(new BrokerResult(ResultStatus.Timeout));
        var (workerStatus, started) = await EnsureWorkerAsync(key, kind).ConfigureAwait(false);
        if (workerStatus != ResultStatus.Success) return Record(new BrokerResult(workerStatus));
        if (token.IsCancellationRequested) return new BrokerResult(ResultStatus.Cancelled);
        if (started) deadline = clock().ToUniversalTime() + readBudget;
        var sentEpoch = Interlocked.Read(ref epoch);
        var id = (ulong)Interlocked.Increment(ref nextRequestId);
        // A recovery probe exercises the tab read without asking Chrome to render.
        var captureIcons = !recovering && kind is (TabReadKind.PopupOpen or TabReadKind.WindowActivated);
        var request = Build("ReadTabs", sentEpoch, id, key, deadline, new { captureIcons });
        var (outcome, reply) = await CallAsync(request, key, deadline, kind).ConfigureAwait(false);
        if (outcome is not null) return Record(outcome);
        if (!MatchesRequest(reply!, sentEpoch, id)) return new BrokerResult(ResultStatus.Stale);
        try
        {
            var status = ParseStatus(reply!.Payload);
            if (status != ResultStatus.Success)
                return IsCurrentPopup(key, forPopup) ? Record(new BrokerResult(status)) : new BrokerResult(ResultStatus.Stale);
            var p = reply.Payload;
            var tabs = p.GetProperty("tabs").EnumerateArray().Select(t => new TabEntry(
                new TabToken(t.GetProperty("tabToken").GetString()!), t.GetProperty("title").GetString()!,
                t.GetProperty("displayOrder").GetInt32(), t.GetProperty("isSelected").GetBoolean(), t.GetProperty("canSelect").GetBoolean(), Protocol.ParseIcon(t))).ToList();
            var snapshot = new TabSnapshot(new WorkerEpoch(sentEpoch), key, new SnapshotId(p.GetProperty("snapshotId").GetGuid()),
                DateTimeOffset.Parse(p.GetProperty("capturedAtUtc").GetString()!, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal),
                TabAvailability.Available, tabs);
            lock (gate)
            {
                if (disposed || epoch != sentEpoch) return new BrokerResult(ResultStatus.Stale);
                if (recovering)
                {
                    backoff.Reset();
                    recovering = false;
                    Report($"worker recovered epoch={sentEpoch} requestId={id} chromePid={key.ProcessId}");
                }
                if (!IsCurrentPopup(key, forPopup)) return new BrokerResult(ResultStatus.Stale);
                Cache.Put(snapshot);
            }
            LastError = null;
            return new BrokerResult(ResultStatus.Success, snapshot);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or Protocol.ProtocolException)
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
        var request = Build("SelectTab", sentEpoch, id, key, deadline, new { snapshotId = snapshotId.Value.ToString(), tabToken = token.Value });
        var (outcome, reply) = await CallAsync(request, key, deadline).ConfigureAwait(false);
        if (outcome is not null) return Record(outcome);
        if (!MatchesRequest(reply!, sentEpoch, id) || !IsCurrentPopup(key, forPopup)) return new BrokerResult(ResultStatus.Stale);
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

    private bool MatchesRequest(Protocol.Message reply, long sentEpoch, ulong id) =>
        reply.Type == "Result" && reply.RequestId == id && reply.WorkerEpoch == (ulong)sentEpoch && Interlocked.Read(ref epoch) == sentEpoch;

    private bool IsCurrentPopup(WindowKey key, long forPopup) => forPopup == PopupGeneration && isCurrentWindow(key);

    private BrokerResult Record(BrokerResult result)
    {
        if (result.Status is not (ResultStatus.Stale or ResultStatus.Cancelled)) LastError = result;
        return result;
    }

    private async Task<(BrokerResult? Outcome, Protocol.Message? Reply)> CallAsync(Protocol.Message request, WindowKey key, DateTimeOffset deadline, TabReadKind? kind = null)
    {
        var remaining = deadline - clock().ToUniversalTime();
        if (remaining <= TimeSpan.Zero) return (new BrokerResult(ResultStatus.Timeout), null);
        using var cts = new CancellationTokenSource();
        try
        {
            var call = (injectedCall ?? PipeCallAsync)(request, cts.Token);
            var winner = await Task.WhenAny(call, Task.Delay(remaining, cts.Token)).ConfigureAwait(false);
            if (winner != call)
            {
                _ = call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                cts.Cancel();
                RecordWorkerFailure($"request timed out operation={request.Type} kind={kind} epoch={request.WorkerEpoch} requestId={request.RequestId} chromePid={key.ProcessId}");
                return (new BrokerResult(ResultStatus.Timeout), null);
            }
            return (null, await call.ConfigureAwait(false));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            if (disposed) return (new BrokerResult(ResultStatus.Cancelled), null);
            RecordWorkerFailure($"request failed reason={e.GetType().Name} operation={request.Type} kind={kind} epoch={request.WorkerEpoch} requestId={request.RequestId} chromePid={key.ProcessId}");
            return (new BrokerResult(e is Protocol.ProtocolException ? ResultStatus.ProtocolError : ResultStatus.Failed), null);
        }
        finally { cts.Cancel(); }
    }

    private async Task<(ResultStatus Status, bool Started)> EnsureWorkerAsync(WindowKey key, TabReadKind kind)
    {
        lock (gate)
        {
            if (disposed) return (ResultStatus.Cancelled, false);
            if (recovering && kind is not (TabReadKind.PopupOpen or TabReadKind.Retry)) return (ResultStatus.Unavailable, false);
            if (connected) return (ResultStatus.Success, false);
            if (workerPath is null && injectedCall is null) return (ResultStatus.Unavailable, false);
            if (recovering)
            {
                long now = monotonicClock();
                if ((backoff.Failures >= FailuresBeforeManualRetry && kind != TabReadKind.Retry) || !CanRestart(now))
                    return (ResultStatus.Unavailable, false);
                restartAttempts.Enqueue(now);
            }
        }

        using var startupDeadline = new CancellationTokenSource(WorkerStartupDeadline);
        try
        {
            if (injectedCall is null) await StartWorkerAsync(startupDeadline.Token).ConfigureAwait(false);
            else if (injectedRestart is not null) await injectedRestart(startupDeadline.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed) return (ResultStatus.Cancelled, false);
                connected = true;
            }
            return (ResultStatus.Success, true);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            if (disposed) return (ResultStatus.Cancelled, false);
            RecordWorkerFailure($"worker startup failed reason={e.GetType().Name} chromePid={key.ProcessId} kind={kind}");
            return (e is OperationCanceledException ? ResultStatus.Timeout : ResultStatus.Failed, false);
        }
    }

    // Called under gate. Successful reads reset the backoff, but preserve the rolling restart limit.
    private bool CanRestart(long now)
    {
        while (restartAttempts.TryPeek(out long attempt) && now - attempt >= RestartWindowMs)
            restartAttempts.Dequeue();
        return backoff.CanSpawn(now) && restartAttempts.Count < MaxRestartsPerWindow;
    }

    private void RecordWorkerFailure(string reason)
    {
        lock (gate)
        {
            if (disposed) return;
            long now = monotonicClock();
            backoff.RecordFailure(now);
            recovering = true;
            Report($"{reason} workerPid={worker?.Id} failures={backoff.Failures} backoffMs={backoff.DueMs - now}");
            Interlocked.Increment(ref epoch);
            Cache.Clear();
            StopWorker();
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

    private static Protocol.Message Build(string type, long sentEpoch, ulong id, WindowKey key, DateTimeOffset deadline, object extra)
    {
        var payload = new Dictionary<string, object>
        {
            ["windowKey"] = new { hwnd = Protocol.SerializeUnsigned((ulong)key.Hwnd), processId = Protocol.SerializeUnsigned((ulong)(uint)key.ProcessId), processStartTimeUtc = Protocol.SerializeUnsigned((ulong)key.ProcessStartTimeTicks), generation = Protocol.SerializeUnsigned((ulong)key.Generation) },
            ["deadlineUtc"] = deadline.ToUniversalTime().ToString("O"),
        };
        foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject()) payload[property.Name] = property.Value;
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
            StopWorker();
            worker?.Dispose();
            worker = null;
            Cache.Clear();
        }
        foreach (var j in abandoned) { j.Registration.Dispose(); j.Completion.TrySetResult(new BrokerResult(ResultStatus.Cancelled)); }
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
