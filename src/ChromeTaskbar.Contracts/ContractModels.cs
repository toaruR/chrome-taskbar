namespace ChromeTaskbar.Contracts;

public readonly record struct WindowKey(
    nint Hwnd,
    int ProcessId,
    long ProcessStartTimeTicks,
    long Generation);

public readonly record struct WorkerEpoch(long Value);

public readonly record struct SnapshotId(Guid Value);

public readonly record struct RequestId(long Value);

public readonly record struct TabToken(string Value);

public enum TabAvailability
{
    Available,
    Unavailable,
    Incomplete,
    WindowGone,
}

public enum ResultStatus
{
    Success,
    Unavailable,
    Incomplete,
    WindowGone,
    Cancelled,
    Timeout,
    Stale,
    ProtocolError,
    Failed,
}

public sealed record TabEntry(
    TabToken Token,
    string Title,
    int DisplayOrder,
    bool IsSelected,
    bool CanSelect);

public sealed record TabSnapshot(
    WorkerEpoch WorkerEpoch,
    WindowKey WindowKey,
    SnapshotId SnapshotId,
    DateTimeOffset CapturedAt,
    TabAvailability Availability,
    IReadOnlyList<TabEntry> Tabs);
