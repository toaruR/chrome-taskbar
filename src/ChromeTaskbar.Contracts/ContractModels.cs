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
    bool CanSelect,
    TabIcon? Icon = null);

public sealed record TabSnapshot(
    WorkerEpoch WorkerEpoch,
    WindowKey WindowKey,
    SnapshotId SnapshotId,
    DateTimeOffset CapturedAt,
    TabAvailability Availability,
    IReadOnlyList<TabEntry> Tabs);

/// <summary>Tab favicon cut from a window capture: top-down, straight-alpha BGRA (Width * Height * 4 bytes).</summary>
public sealed record TabIcon(int Width, int Height, byte[] Bgra);
