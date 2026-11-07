using System.Text;
using System.Text.Json;
using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using ChromeTaskbar.UiaWorker;
using Xunit;

namespace ChromeTaskbar.Tests;

public sealed class TabSelectionIntegrationTests : IDisposable
{
    private readonly FakeWorld world = new();
    private readonly WindowActivator activator;
    private readonly WindowKey key;
    private readonly List<Protocol.Message> calls = new();
    private readonly HoverController hover = new();
    private readonly UiaWorkerBroker broker;
    private readonly TaskbarViewModel taskbar;
    private TabPopupViewModel popup = null!;
    private string selectStatus = "Success";
    private Action? onSelect;

    public TabSelectionIntegrationTests()
    {
        world.Windows[1] = ChromeWindowFilterTests.W(1) with { IsMinimized = true };
        world.Foreground = 99;
        key = new WindowRegistry().Update(world.EnumerateTopLevel(), 0)[0].Key;
        activator = new WindowActivator(world, world);
        broker = new UiaWorkerBroker((m, _) =>
        {
            lock (calls) calls.Add(m);
            if (m.Type == "SelectTab") onSelect?.Invoke();
            object payload = m.Type == "ReadTabs"
                ? new { status = "Success", snapshotId = Guid.NewGuid(), capturedAtUtc = DateTimeOffset.UtcNow.ToString("O"), tabs = new[] { new { tabToken = "tok", title = "A", displayOrder = 0, isSelected = false, canSelect = true } } }
                : new { status = selectStatus };
            return Task.FromResult(new Protocol.Message("Result", m.WorkerEpoch, m.RequestId, JsonSerializer.SerializeToElement(payload)));
        }, _ => Task.CompletedTask, null, _ => true);
        taskbar = new TaskbarViewModel(hover, () => 0, _ => { });
    }

    public void Dispose()
    {
        popup?.Dispose();
        taskbar.Dispose();
        hover.Dispose();
        broker.Dispose();
    }

    private int Count(string type) { lock (calls) return calls.Count(c => c.Type == type); }

    private async Task<TabItemViewModel> OpenPopup()
    {
        hover.ButtonEnter(key, 0);
        hover.Tick(HoverController.HoverDelayMs);
        Assert.Equal(HoverState.Open, hover.State);
        broker.PopupGeneration = hover.Generation;
        popup = new TabPopupViewModel(key, hover.Generation,
            (k, s, t, g) => broker.SelectTabAsync(k, s, t, g),
            k => activator.Activate(k), k => activator.IsForegroundConfirmed(k));
        var read = await broker.ReadTabsAsync(key, hover.Generation);
        Assert.True(popup.Apply(popup.BeginRequest(), read));
        Assert.Equal(PopupStatus.Ready, popup.Status);
        return popup.Tabs[0];
    }

    private static bool Closed(HoverController h) =>
        h.Events.Any(e => e.Kind == HoverEventKind.ClosePopup && e.Reason == CloseReason.Selection);

    [Fact]
    public async Task Success_restores_foregrounds_without_minimize_and_sends_matching_SelectTab()
    {
        var tab = await OpenPopup();
        var snapshotId = broker.Cache.Get(key)!.Snapshot.SnapshotId;
        Assert.True(await taskbar.OnTabClickAsync(popup, tab));
        Assert.Equal(new[] { "restore", "fg" }, world.Calls);
        Assert.Equal(1, Count("SelectTab"));
        var m = calls.Single(c => c.Type == "SelectTab");
        Assert.Equal((ulong)broker.Epoch.Value, m.WorkerEpoch);
        Assert.Equal(snapshotId.Value.ToString(), m.Payload.GetProperty("snapshotId").GetString());
        Assert.Equal("tok", m.Payload.GetProperty("tabToken").GetString());
        Assert.True(DateTimeOffset.TryParse(m.Payload.GetProperty("deadlineUtc").GetString(), out var deadline));
        Assert.True(deadline > DateTimeOffset.UtcNow.AddMilliseconds(-100) && deadline <= DateTimeOffset.UtcNow + UiaWorkerBroker.SelectDeadline);
        Assert.Equal(SelectionOutcome.Success, popup.LastSelection);
        Assert.Null(popup.SelectionMessage);
        Assert.True(Closed(hover));
        Assert.Equal(HoverState.Idle, hover.State);
    }

    [Fact]
    public async Task Already_foreground_target_is_not_minimized_by_tab_click()
    {
        world.Windows[1] = world.Windows[1] with { IsMinimized = false };
        world.Foreground = 1;
        var tab = await OpenPopup();
        Assert.True(await taskbar.OnTabClickAsync(popup, tab));
        Assert.DoesNotContain("min", world.Calls);
        Assert.False(world.Windows[1].IsMinimized);
    }

    [Fact]
    public async Task Foreground_failure_sends_no_SelectTab_and_keeps_popup_open()
    {
        var tab = await OpenPopup();
        world.AllowForeground = false;
        Assert.False(await taskbar.OnTabClickAsync(popup, tab));
        Assert.Equal(0, Count("SelectTab"));
        Assert.Equal(SelectionOutcome.ActivateFailed, popup.LastSelection);
        Assert.Equal(TabPopupViewModel.SelectActivateFailedText, popup.SelectionMessage);
        Assert.False(Closed(hover));
        Assert.Equal(HoverState.Open, hover.State);
        Assert.Single(popup.Tabs);
    }

    [Fact]
    public async Task Target_gone_at_revalidation_sends_nothing_and_reports_gone()
    {
        var tab = await OpenPopup();
        world.Windows.Clear();
        Assert.False(await taskbar.OnTabClickAsync(popup, tab));
        Assert.Equal(0, Count("SelectTab"));
        Assert.Equal(SelectionOutcome.TargetGone, popup.LastSelection);
        Assert.Equal(PopupStatus.WindowGone, popup.Status);
        Assert.False(Closed(hover));
    }

    [Fact]
    public async Task Worker_success_without_main_foreground_confirmation_is_not_success()
    {
        var tab = await OpenPopup();
        onSelect = () => world.Foreground = 99;
        Assert.False(await taskbar.OnTabClickAsync(popup, tab));
        Assert.Equal(1, Count("SelectTab"));
        Assert.Equal(SelectionOutcome.ForegroundNotConfirmed, popup.LastSelection);
        Assert.Equal(TabPopupViewModel.SelectNotConfirmedText, popup.SelectionMessage);
        Assert.False(Closed(hover));
    }

    [Fact]
    public async Task Stale_epoch_is_rejected_without_a_worker_call()
    {
        var tab = await OpenPopup();
        broker.InvalidateEpoch();
        Assert.False(await taskbar.OnTabClickAsync(popup, tab));
        Assert.Equal(0, Count("SelectTab"));
        Assert.Equal(SelectionOutcome.Stale, popup.LastSelection);
        Assert.False(Closed(hover));
        Assert.False(await popup.SelectAsync(tab)); // user retry still rejected, never auto-resent
        Assert.Equal(0, Count("SelectTab"));
    }

    [Fact]
    public async Task Stale_snapshot_id_or_token_is_rejected_without_a_worker_call()
    {
        await OpenPopup();
        var foreign = new TabSnapshot(broker.Epoch, key, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UtcNow, TabAvailability.Available,
            new[] { new TabEntry(new TabToken("tok"), "A", 0, false, true), new TabEntry(new TabToken("other"), "B", 1, false, true) });
        Assert.True(popup.Apply(popup.BeginRequest(), new BrokerResult(ResultStatus.Success, foreign)));
        Assert.False(await taskbar.OnTabClickAsync(popup, popup.Tabs[0])); // snapshot id differs from the cached one
        Assert.False(await taskbar.OnTabClickAsync(popup, popup.Tabs[1])); // token unknown to cache too
        Assert.Equal(0, Count("SelectTab"));
        Assert.Equal(SelectionOutcome.Stale, popup.LastSelection);
    }

    [Fact]
    public async Task Cancelled_before_start_does_nothing()
    {
        var tab = await OpenPopup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(await taskbar.OnTabClickAsync(popup, tab, cts.Token));
        Assert.Empty(world.Calls);
        Assert.Equal(0, Count("SelectTab"));
        Assert.Equal(SelectionOutcome.Cancelled, popup.LastSelection);
        Assert.Null(popup.SelectionMessage);
        Assert.False(Closed(hover));
    }

    [Fact]
    public async Task Cancelled_during_select_is_not_success()
    {
        var tab = await OpenPopup();
        using var cts = new CancellationTokenSource();
        onSelect = () => cts.Cancel();
        Assert.False(await taskbar.OnTabClickAsync(popup, tab, cts.Token));
        Assert.Equal(SelectionOutcome.Cancelled, popup.LastSelection);
        Assert.False(Closed(hover));
    }

    [Fact]
    public async Task Tab_click_cancels_pending_hover_wait()
    {
        var tab = await OpenPopup();
        hover.ButtonLeave(key, 1100); hover.PopupLeave(1100); // Closing -> dismissed
        hover.Fire(key, hover.Generation, 1100 + HoverController.PopupCloseGraceMs);
        hover.ButtonEnter(key, 2000); // new Waiting
        Assert.Equal(HoverState.Waiting, hover.State);
        await taskbar.OnTabClickAsync(popup, tab);
        Assert.Equal(HoverState.Idle, hover.State);
    }

    [Theory]
    [InlineData("Success", SelectionOutcome.Success, true)]
    [InlineData("Stale", SelectionOutcome.Stale, false)]
    [InlineData("Unavailable", SelectionOutcome.Unavailable, false)]
    [InlineData("Incomplete", SelectionOutcome.Incomplete, false)]
    [InlineData("WindowGone", SelectionOutcome.WindowGone, false)]
    [InlineData("Timeout", SelectionOutcome.Timeout, false)]
    [InlineData("Cancelled", SelectionOutcome.Cancelled, false)]
    [InlineData("ProtocolError", SelectionOutcome.ProtocolError, false)]
    [InlineData("Failed", SelectionOutcome.Failed, false)]
    public async Task Every_worker_result_maps_to_one_outcome_and_never_resends(string status, SelectionOutcome expected, bool closes)
    {
        var tab = await OpenPopup();
        selectStatus = status;
        Assert.Equal(closes, await taskbar.OnTabClickAsync(popup, tab));
        Assert.Equal(expected, popup.LastSelection);
        Assert.Equal(closes, Closed(hover));
        Assert.Equal(1, Count("SelectTab"));
        Assert.Equal(TabPopupViewModel.MessageFor(expected), popup.SelectionMessage);
        Assert.Equal(expected is SelectionOutcome.Success or SelectionOutcome.Cancelled, popup.SelectionMessage is null);
    }

    [Fact]
    public void Mapping_covers_every_ResultStatus()
    {
        foreach (var s in Enum.GetValues<ResultStatus>())
        {
            var o = TabPopupViewModel.MapResult(s);
            Assert.Equal(s == ResultStatus.Success, o == SelectionOutcome.Success);
        }
        Assert.Equal(SelectionOutcome.Failed, TabPopupViewModel.MapResult(ResultStatus.Failed));
        Assert.Equal(TabPopupViewModel.GoneText, TabPopupViewModel.MessageFor(SelectionOutcome.TargetGone));
    }

    // ---- PipeServer.ReadTabs result (approved bug fix) -------------------------------------

    [Fact]
    public void ReadTabs_non_available_snapshot_is_bare_Incomplete_and_passes_protocol_validation()
    {
        foreach (var availability in new[] { TabAvailability.Incomplete, TabAvailability.Unavailable, TabAvailability.WindowGone })
        {
            var snap = new TabSnapshot(new WorkerEpoch(1), key, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UtcNow, availability,
                new[] { new TabEntry(new TabToken("t"), "x", 0, false, true) });
            var element = JsonSerializer.SerializeToElement(PipeServer.BuildReadTabsPayload(key, snap));
            Assert.Equal("Incomplete", element.GetProperty("status").GetString());
            Assert.False(element.TryGetProperty("tabs", out _));
            Assert.False(element.TryGetProperty("availability", out _));
            Assert.True(element.TryGetProperty("windowKey", out _));
            Assert.True(element.TryGetProperty("snapshotId", out _));
            var bytes = Protocol.Serialize(new Protocol.Message("Result", 1, 1, element));
            Assert.Equal("Incomplete", Protocol.Parse(bytes).Payload.GetProperty("status").GetString());
        }
    }

    [Fact]
    public void ReadTabs_available_snapshot_is_Success_with_tabs_and_passes_protocol_validation()
    {
        var snap = new TabSnapshot(new WorkerEpoch(1), key, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UtcNow, TabAvailability.Available,
            new[] { new TabEntry(new TabToken("t"), "x", 0, true, true) });
        var element = JsonSerializer.SerializeToElement(PipeServer.BuildReadTabsPayload(key, snap));
        var parsed = Protocol.Parse(Protocol.Serialize(new Protocol.Message("Result", 1, 1, element)));
        Assert.Equal("Success", parsed.Payload.GetProperty("status").GetString());
        Assert.Equal(1, parsed.Payload.GetProperty("tabs").GetArrayLength());
    }
}
