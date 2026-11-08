using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.UiaWorker;

public sealed class PipeServer : IDisposable
{
    private readonly string pipeName;
    private readonly WorkerEpoch epoch;
    private readonly UiaDispatcher dispatcher;

    public PipeServer(string pipeName, WorkerEpoch epoch)
    {
        this.pipeName = pipeName;
        this.epoch = epoch;
        dispatcher = new UiaDispatcher(epoch);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // The broker owns the (random-named) pipe server and checks this process as its client; connect, do not serve.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);
        await Protocol.WriteFrameAsync(pipe, Message("Hello", 0, new { protocolVersion = Protocol.Version, workerEpoch = Protocol.SerializeUnsigned((ulong)epoch.Value) }), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            Protocol.Message request;
            try { request = await Protocol.ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false); }
            catch (Protocol.ProtocolException) { break; }
            if (request.WorkerEpoch != (ulong)epoch.Value) { await WriteStatusAsync(pipe, request.RequestId, ResultStatus.Stale, cancellationToken).ConfigureAwait(false); continue; }
            try { await DispatchAsync(pipe, request, cancellationToken).ConfigureAwait(false); }
            catch (Protocol.ProtocolException) { break; }
        }
    }

    public void Dispose() => dispatcher.Dispose();

    private async Task DispatchAsync(Stream pipe, Protocol.Message request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case "ReadTabs":
                {
                    var key = ParseWindowKey(request.Payload); var deadline = ParseDeadline(request.Payload);
                    var snapshot = dispatcher.Invoke(provider => provider.ReadTabs(key, deadline));
                    await Protocol.WriteFrameAsync(pipe, Message("Result", request.RequestId, BuildReadTabsPayload(key, snapshot)), cancellationToken).ConfigureAwait(false);
                    return;
                }
            case "SelectTab":
                {
                    var key = ParseWindowKey(request.Payload); var deadline = ParseDeadline(request.Payload);
                    var result = dispatcher.Invoke(provider => provider.Select(key, new SnapshotId(Guid.Parse(request.Payload.GetProperty("snapshotId").GetString()!)), new TabToken(request.Payload.GetProperty("tabToken").GetString()!), deadline));
                    await WriteStatusAsync(pipe, request.RequestId, result, cancellationToken).ConfigureAwait(false); return;
                }
            case "Invalidate":
                dispatcher.Invoke(provider => { provider.Invalidate(ParseWindowKey(request.Payload)); return 0; });
                await WriteStatusAsync(pipe, request.RequestId, ResultStatus.Success, cancellationToken).ConfigureAwait(false); return;
            case "Cancel":
                await WriteStatusAsync(pipe, request.RequestId, ResultStatus.Cancelled, cancellationToken).ConfigureAwait(false); return;
            default:
                throw new Protocol.ProtocolException("Worker accepts requests only.");
        }
    }

    /// <summary>Success carries tabs; any non-Available snapshot is reported as a bare Incomplete (design 7.1: no tabs/availability).</summary>
    public static object BuildReadTabsPayload(WindowKey key, TabSnapshot snapshot) =>
        snapshot.Availability != TabAvailability.Available
            ? new { status = ResultStatus.Incomplete.ToString(), windowKey = ToWire(key), snapshotId = snapshot.SnapshotId.Value }
            : new { status = ResultStatus.Success.ToString(), windowKey = ToWire(key), snapshotId = snapshot.SnapshotId.Value, capturedAtUtc = snapshot.CapturedAt.ToString("O"), tabs = snapshot.Tabs.Select(t => new { tabToken = t.Token.Value, title = t.Title, displayOrder = t.DisplayOrder, isSelected = t.IsSelected, canSelect = t.CanSelect, icon = Protocol.IconToWire(t.Icon) }).ToArray() };

    private async Task WriteStatusAsync(Stream pipe, ulong requestId, ResultStatus status, CancellationToken token) =>
        await Protocol.WriteFrameAsync(pipe, Message("Result", requestId, new { status = status.ToString() }), token).ConfigureAwait(false);

    private Protocol.Message Message(string type, ulong requestId, object payload) => new(type, (ulong)epoch.Value, requestId, JsonSerializer.SerializeToElement(payload));
    private static DateTimeOffset ParseDeadline(JsonElement payload) => DateTimeOffset.Parse(payload.GetProperty("deadlineUtc").GetString()!, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    private static WindowKey ParseWindowKey(JsonElement payload)
    {
        var key = payload.GetProperty("windowKey");
        return new WindowKey((nint)Protocol.ParseUnsigned(key.GetProperty("hwnd").GetString()!, "hwnd"), unchecked((int)(uint)Protocol.ParseUnsigned(key.GetProperty("processId").GetString()!, "processId", Protocol.PidMax)), checked((long)Protocol.ParseUnsigned(key.GetProperty("processStartTimeUtc").GetString()!, "processStartTimeUtc")), checked((long)Protocol.ParseUnsigned(key.GetProperty("generation").GetString()!, "generation")));
    }
    private static object ToWire(WindowKey key) => new { hwnd = Protocol.SerializeUnsigned((ulong)key.Hwnd), processId = Protocol.SerializeUnsigned((ulong)(uint)key.ProcessId), processStartTimeUtc = Protocol.SerializeUnsigned((ulong)key.ProcessStartTimeTicks), generation = Protocol.SerializeUnsigned((ulong)key.Generation) };
}

internal sealed class UiaDispatcher : IDisposable
{
    private readonly BlockingCollection<Func<ChromeUiaTabProvider, object?>> queue = new();
    private readonly Thread thread;
    private ChromeUiaTabProvider? provider;
    public UiaDispatcher(WorkerEpoch epoch)
    {
        thread = new Thread(() => { provider = new ChromeUiaTabProvider(epoch); foreach (var action in queue.GetConsumingEnumerable()) action(provider); }) { IsBackground = true, Name = "ChromeTaskbar.UIA" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }
    public T Invoke<T>(Func<ChromeUiaTabProvider, T> action)
    {
        using var complete = new ManualResetEventSlim(); T? result = default; Exception? error = null;
        queue.Add(provider => { try { result = action(provider); } catch (Exception exception) { error = exception; } finally { complete.Set(); } return null; });
        complete.Wait(); if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); return result!;
    }
    public void Dispose() { queue.CompleteAdding(); thread.Join(); queue.Dispose(); }
}
