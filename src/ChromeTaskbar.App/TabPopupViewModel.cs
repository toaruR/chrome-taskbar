using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.App;

public enum PopupStatus { Loading, Ready, Empty, Unavailable, Incomplete, WindowGone }

/// <summary>Result of an explicit tab click. Only Success closes the popup.</summary>
public enum SelectionOutcome
{
    None, Success, TargetGone, ActivateFailed, ForegroundNotConfirmed,
    Stale, Cancelled, Timeout, Unavailable, Incomplete, WindowGone, ProtocolError, Failed,
}

public sealed record TabItemViewModel(TabToken Token, string Title, int DisplayOrder, bool IsSelected, bool CanSelect)
{
    /// <summary>Identicon of the title (same as the bar button's icon). UI thread only.</summary>
    public System.Windows.Media.ImageSource Icon => Identicon.Image(Title);
}

/// <summary>Popup state for one window. Unavailable/Incomplete is distinct from Empty (zero tabs).</summary>
public sealed class TabPopupViewModel : IDisposable
{
    public const string LoadingText = "取得中";
    public const string UnavailableText = "タブを取得できません";
    public const string IncompleteText = "タブを完全には取得できません";
    public const string EmptyText = "タブがありません";
    public const string GoneText = "ウィンドウが閉じられました";

    public const string SelectActivateFailedText = "ウィンドウを前面にできません";
    public const string SelectNotConfirmedText = "ウィンドウの前面化を確認できません";
    public const string SelectStaleText = "タブ一覧が古くなりました。再取得してください";
    public const string SelectTimeoutText = "タブの選択がタイムアウトしました";
    public const string SelectFailedText = "タブを選択できませんでした";

    private readonly Func<WindowKey, SnapshotId, TabToken, long, Task<BrokerResult>> _select;
    private readonly Func<WindowKey, ActivationResult>? _activate;
    private readonly Func<WindowKey, bool>? _confirmForeground;
    private bool _selecting;
    private long _requestId;
    private bool _disposed;
    private SnapshotId? _snapshotId;

    public TabPopupViewModel(WindowKey key, long popupGeneration,
        Func<WindowKey, SnapshotId, TabToken, long, Task<BrokerResult>> select,
        Func<WindowKey, ActivationResult>? activate = null,
        Func<WindowKey, bool>? confirmForeground = null)
    {
        Key = key;
        Generation = popupGeneration;
        _select = select;
        _activate = activate;
        _confirmForeground = confirmForeground;
    }

    public WindowKey Key { get; }
    public long Generation { get; }
    public PopupStatus Status { get; private set; } = PopupStatus.Loading;
    public IReadOnlyList<TabItemViewModel> Tabs { get; private set; } = Array.Empty<TabItemViewModel>();
    public event Action? Selected;
    public SelectionOutcome LastSelection { get; private set; }
    /// <summary>User-visible failure of the last explicit selection; null on success/cancel/none.</summary>
    public string? SelectionMessage { get; private set; }

    /// <summary>Pure Result-to-UI mapping for the selection path (worker confirmation only; foreground checked separately).</summary>
    public static SelectionOutcome MapResult(ResultStatus status) => status switch
    {
        ResultStatus.Success => SelectionOutcome.Success,
        ResultStatus.Stale => SelectionOutcome.Stale,
        ResultStatus.Cancelled => SelectionOutcome.Cancelled,
        ResultStatus.Timeout => SelectionOutcome.Timeout,
        ResultStatus.Unavailable => SelectionOutcome.Unavailable,
        ResultStatus.Incomplete => SelectionOutcome.Incomplete,
        ResultStatus.WindowGone => SelectionOutcome.WindowGone,
        ResultStatus.ProtocolError => SelectionOutcome.ProtocolError,
        _ => SelectionOutcome.Failed,
    };

    public static string? MessageFor(SelectionOutcome o) => o switch
    {
        SelectionOutcome.None or SelectionOutcome.Success or SelectionOutcome.Cancelled => null,
        SelectionOutcome.TargetGone or SelectionOutcome.WindowGone => GoneText,
        SelectionOutcome.ActivateFailed => SelectActivateFailedText,
        SelectionOutcome.ForegroundNotConfirmed => SelectNotConfirmedText,
        SelectionOutcome.Stale => SelectStaleText,
        SelectionOutcome.Timeout => SelectTimeoutText,
        SelectionOutcome.Unavailable => UnavailableText,
        SelectionOutcome.Incomplete => IncompleteText,
        _ => SelectFailedText,
    };

    public string StatusText => Status switch
    {
        PopupStatus.Loading => LoadingText,
        PopupStatus.Unavailable => UnavailableText,
        PopupStatus.Incomplete => IncompleteText,
        PopupStatus.Empty => EmptyText,
        PopupStatus.WindowGone => GoneText,
        _ => string.Empty,
    };

    /// <summary>Allocates a request id; only the response carrying the latest id is applied.</summary>
    public long BeginRequest() => ++_requestId;

    public bool Apply(long requestId, BrokerResult result, bool isFresh = true)
    {
        if (_disposed || requestId != _requestId) return false;
        switch (result.Status)
        {
            case ResultStatus.Cancelled:
            case ResultStatus.Stale:
                return false;
            case ResultStatus.Success when result.Snapshot is { } s && s.WindowKey == Key:
                var bad = s.Availability switch
                {
                    TabAvailability.Incomplete => PopupStatus.Incomplete,
                    TabAvailability.Unavailable => PopupStatus.Unavailable,
                    TabAvailability.WindowGone => PopupStatus.WindowGone,
                    _ => (PopupStatus?)null,
                };
                if (bad is { } b) { SetStatus(b); return true; }
                _snapshotId = s.SnapshotId;
                Tabs = s.Tabs.OrderBy(t => t.DisplayOrder)
                    .Select(t => new TabItemViewModel(t.Token, t.Title, t.DisplayOrder, t.IsSelected, t.CanSelect && isFresh))
                    .ToList();
                Status = Tabs.Count == 0 ? PopupStatus.Empty : PopupStatus.Ready;
                return true;
            case ResultStatus.Incomplete: SetStatus(PopupStatus.Incomplete); return true;
            case ResultStatus.WindowGone: SetStatus(PopupStatus.WindowGone); return true;
            default: SetStatus(PopupStatus.Unavailable); return true; // Timeout/Failed/ProtocolError/Unavailable/malformed Success
        }
    }

    /// <summary>
    /// Explicit tab click: activate (never minimize) then send SelectTab once. Success only when the worker
    /// confirmed selection and the main side confirms foreground. No automatic resend or title/position rebinding.
    /// </summary>
    public async Task<bool> SelectAsync(TabItemViewModel tab, CancellationToken cancellationToken = default)
    {
        if (_disposed || _selecting || !tab.CanSelect || _snapshotId is not { } id) return false;
        if (cancellationToken.IsCancellationRequested) return Finish(SelectionOutcome.Cancelled);
        _selecting = true;
        try
        {
            if (_activate is not null)
            {
                var activation = _activate(Key);
                if (!activation.IsSuccess && !activation.IsPending)
                    return Finish(activation.Outcome == ActivationOutcome.TargetGone ? SelectionOutcome.TargetGone : SelectionOutcome.ActivateFailed);
            }
            if (cancellationToken.IsCancellationRequested) return Finish(SelectionOutcome.Cancelled);
            var result = await _select(Key, id, tab.Token, Generation).ConfigureAwait(true);
            if (_disposed) return false;
            if (cancellationToken.IsCancellationRequested) return Finish(SelectionOutcome.Cancelled);
            var outcome = MapResult(result.Status);
            if (outcome == SelectionOutcome.Success && _confirmForeground is not null && !_confirmForeground(Key))
                outcome = SelectionOutcome.ForegroundNotConfirmed;
            return Finish(outcome);
        }
        finally { _selecting = false; }
    }

    private bool Finish(SelectionOutcome outcome)
    {
        LastSelection = outcome;
        SelectionMessage = MessageFor(outcome);
        switch (outcome)
        {
            case SelectionOutcome.Success:
                Selected?.Invoke();
                return true;
            case SelectionOutcome.WindowGone or SelectionOutcome.TargetGone: SetStatus(PopupStatus.WindowGone); break;
            case SelectionOutcome.Unavailable: SetStatus(PopupStatus.Unavailable); break;
            case SelectionOutcome.Incomplete: SetStatus(PopupStatus.Incomplete); break;
            default:
                // Cancelled / Stale / Timeout / Failed / ProtocolError / foreground failures: keep tabs and
                // report via SelectionMessage; the broker rejects stale tokens, nothing is resent here.
                break;
        }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        Selected = null;
        Tabs = Array.Empty<TabItemViewModel>();
        _snapshotId = null;
    }

    private void SetStatus(PopupStatus status) { Status = status; Tabs = Array.Empty<TabItemViewModel>(); _snapshotId = null; }
}

public readonly record struct PxRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double IntersectionArea(PxRect o) =>
        Math.Max(0, Math.Min(Right, o.Right) - Math.Max(Left, o.Left)) * Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Top, o.Top));
}

public readonly record struct PopupPlacement(double LeftPx, double TopPx, double WidthPx, double HeightPx, double WidthDip, double HeightDip, bool AboveAnchor);

/// <summary>DIP / screen-pixel conversion and work-area fitting. Scale = DPI / 96 of the target monitor.</summary>
public static class LayoutMath
{
    public const double MaxPopupWidthDip = 480;
    public const double MaxPopupHeightDip = 400;

    public static double DipToPx(double dip, double scale) => dip * scale;
    public static double PxToDip(double px, double scale) => px / scale;

    /// <summary>Monitor work area with the largest overlap with the anchor; first one when none overlap.</summary>
    public static PxRect PickWorkArea(PxRect anchor, IReadOnlyList<PxRect> workAreas) =>
        workAreas.Select((w, i) => (w, i, a: w.IntersectionArea(anchor))).OrderByDescending(x => x.a).ThenBy(x => x.i).First().w;

    public static PopupPlacement Place(PxRect anchor, PxRect work, double desiredWidthDip, double desiredHeightDip, double scale)
    {
        double w = Math.Min(DipToPx(Math.Min(desiredWidthDip, MaxPopupWidthDip), scale), work.Width);
        double h = Math.Min(DipToPx(Math.Min(desiredHeightDip, MaxPopupHeightDip), scale), work.Height);
        double left = Math.Clamp(anchor.Left, work.Left, work.Right - w);
        double above = anchor.Top - work.Top, below = work.Bottom - anchor.Bottom;
        bool isAbove;
        if (h <= above) isAbove = true;
        else if (h <= below) isAbove = false;
        else { isAbove = above >= below; h = Math.Max(1, Math.Min(h, Math.Max(above, below))); }
        double top = isAbove ? anchor.Top - h : anchor.Bottom;
        top = Math.Clamp(top, work.Top, work.Bottom - h);
        return new PopupPlacement(left, top, w, h, PxToDip(w, scale), PxToDip(h, scale), isAbove);
    }
}
