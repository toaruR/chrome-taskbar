using ChromeTaskbar.App;
using ChromeTaskbar.Contracts;
using Xunit;

namespace ChromeTaskbar.Tests;

public class HoverControllerTests
{
    private static readonly WindowKey A = new(1, 10, 100, 1);
    private static readonly WindowKey B = new(2, 10, 100, 2);

    private static HoverController Opened(out long openTick, long enter = 5000)
    {
        var h = new HoverController();
        h.ButtonEnter(A, enter);
        h.Tick(enter + 1000);
        openTick = enter + 1000;
        return h;
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(501, true)]
    public void HoverDelay_boundary(int dwell, bool opens)
    {
        var h = new HoverController();
        h.ButtonEnter(A, 7000);
        h.Tick(7000 + dwell);
        Assert.Equal(opens, h.State == HoverState.Open);
        var open = h.Events.SingleOrDefault(e => e.Kind == HoverEventKind.OpenPopup);
        if (opens) Assert.Equal(7000 + dwell, open!.TickMs);
        else Assert.Null(open);
    }

    [Theory]
    [InlineData(249, false)]
    [InlineData(250, true)]
    [InlineData(251, true)]
    public void PopupCloseGrace_boundary(int outside, bool closes)
    {
        var h = Opened(out var t);
        h.ButtonLeave(A, t + 10);
        h.Tick(t + 10 + outside);
        Assert.Equal(closes, h.State == HoverState.Idle);
        var close = h.Events.SingleOrDefault(e => e.Kind == HoverEventKind.ClosePopup);
        if (closes) { Assert.Equal(t + 10 + outside, close!.TickMs); Assert.Equal(CloseReason.Left, close.Reason); }
        else Assert.Null(close);
    }

    [Fact]
    public void Tick_does_not_open_when_pointer_left_before_threshold()
    {
        var h = new HoverController();
        h.ButtonEnter(A, 0);
        long gen = h.Generation;
        h.ButtonLeave(A, 500);
        Assert.False(h.Fire(A, gen, 1000)); // stale timer: key/generation/pointer no longer match
        Assert.Equal(HoverState.Idle, h.State);
        Assert.DoesNotContain(h.Events, e => e.Kind == HoverEventKind.OpenPopup);
    }

    [Fact]
    public void Reenter_same_button_makes_old_timer_stale()
    {
        var h = new HoverController();
        h.ButtonEnter(A, 0);
        long oldGen = h.Generation;
        h.ButtonLeave(A, 400);
        h.ButtonEnter(A, 600);
        Assert.False(h.Fire(A, oldGen, 1000));
        Assert.Equal(HoverState.Waiting, h.State);
        Assert.False(h.Tick(1099));
        Assert.True(h.Tick(1100));
    }

    [Fact]
    public void A_to_B_restarts_timing_and_closes_A()
    {
        var h = Opened(out var t);
        h.ButtonLeave(A, t + 5);
        h.ButtonEnter(B, t + 6);
        Assert.Equal(HoverState.Waiting, h.State);
        Assert.Contains(h.Events, e => e.Kind == HoverEventKind.ClosePopup && e.Key == A && e.Reason == CloseReason.SwitchedTarget);
        Assert.False(h.Tick(t + 6 + 499));
        Assert.True(h.Tick(t + 6 + 500));
        Assert.Equal(B, h.Events[^1].Key);
        Assert.Single(h.Events, e => e.Kind == HoverEventKind.OpenPopup && e.Key == B);
    }

    [Fact]
    public void Button_to_popup_movement_preserves_interaction()
    {
        var h = Opened(out var t);
        h.ButtonLeave(A, t + 10);
        Assert.Equal(HoverState.Closing, h.State);
        h.PopupEnter(t + 100);
        Assert.Equal(HoverState.Open, h.State);
        Assert.False(h.Tick(t + 10_000));
        Assert.Equal(HoverState.Open, h.State);
        h.PopupLeave(t + 20_000);
        Assert.Equal(HoverState.Closing, h.State);
        h.ButtonEnter(A, t + 20_100); // back within grace
        Assert.Equal(HoverState.Open, h.State);
        Assert.Single(h.Events, e => e.Kind == HoverEventKind.OpenPopup);
    }

    [Theory]
    [InlineData("Esc", CloseReason.Escape)]
    [InlineData("Outside", CloseReason.OutsideClick)]
    [InlineData("Select", CloseReason.Selection)]
    [InlineData("Click", CloseReason.ButtonClick)]
    [InlineData("Gone", CloseReason.TargetGone)]
    public void Dismissals_close_single_popup(string how, CloseReason reason)
    {
        var h = Opened(out var t);
        switch (how)
        {
            case "Esc": h.Escape(t + 1); break;
            case "Outside": h.OutsideClick(t + 1); break;
            case "Select": h.Selected(t + 1); break;
            case "Click": h.ButtonClick(t + 1); break;
            case "Gone": h.TargetGone(A, t + 1); break;
        }
        Assert.Equal(HoverState.Idle, h.State);
        var close = Assert.Single(h.Events, e => e.Kind == HoverEventKind.ClosePopup);
        Assert.Equal(reason, close.Reason);
        Assert.Null(h.NextDeadlineMs);
    }

    [Fact]
    public void Waiting_target_gone_or_click_cancels_without_popup()
    {
        var h = new HoverController();
        h.ButtonEnter(A, 0);
        h.TargetGone(B, 10); // unrelated key ignored
        Assert.Equal(HoverState.Waiting, h.State);
        h.TargetGone(A, 20);
        Assert.Equal(HoverState.Idle, h.State);
        Assert.False(h.Tick(5000));
        Assert.DoesNotContain(h.Events, e => e.Kind is HoverEventKind.OpenPopup or HoverEventKind.ClosePopup);
    }

    [Fact]
    public void Hover_emits_only_prefetch_and_popup_events_never_activation()
    {
        var h = Opened(out _);
        Assert.All(h.Events, e => Assert.True(e.Kind is HoverEventKind.Prefetch or HoverEventKind.OpenPopup));
    }

    [Fact]
    public void Prefetch_starts_on_enter_but_popup_waits_for_delay()
    {
        var h = new HoverController();
        h.ButtonEnter(A, 100);
        Assert.Equal(HoverEventKind.Prefetch, Assert.Single(h.Events).Kind);
        Assert.Equal(600, h.NextDeadlineMs);
    }

    [Fact]
    public void Dispose_stops_events_and_subscriptions()
    {
        var h = new HoverController();
        int raised = 0;
        h.Raised += _ => raised++;
        h.ButtonEnter(A, 0);
        h.Dispose();
        h.ButtonEnter(B, 1);
        h.Tick(5000);
        Assert.Equal(1, raised);
        Assert.Equal(HoverState.Idle, h.State);
    }
}

public class HoverControllerSchedulerTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, true)]
    public void WinEventDebounce_boundary(int gap, bool dispatches)
    {
        var c = new WinEventCoalescer();
        c.Notify(1000);
        var r = c.Poll(1000 + gap);
        Assert.Equal(dispatches, r is not null);
        if (r is null) { c.Notify(1000 + gap); Assert.True(c.Pending); return; } // coalesced into the same pending batch
        Assert.Equal(1, r.Sequence);
        Assert.Equal("Debounce", r.Reason);
        Assert.Equal(1000, r.FirstNotifyTickMs);
        Assert.Equal(1, r.Count);
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(501, true)]
    public void WinEventMaxWait_boundary(int pollAt, bool dispatches)
    {
        var c = new WinEventCoalescer();
        int notified = 0;
        for (int t = 0; t <= (pollAt - 1) / 50 * 50; t += 50) { c.Notify(2000 + t); notified++; }
        var r = c.Poll(2000 + pollAt);
        Assert.Equal(dispatches, r is not null);
        if (r is null) { Assert.True(c.Pending); return; }
        Assert.Equal("MaxWait", r.Reason);
        Assert.Equal(2000, r.FirstNotifyTickMs);
        Assert.Equal(notified, r.Count);
        Assert.False(c.Pending);
    }

    [Theory]
    [InlineData(1999, false)]
    [InlineData(2000, true)]
    [InlineData(2001, true)]
    public void VisiblePollInterval_boundary(int elapsed, bool fires)
    {
        var s = new WindowPollScheduler(50_000);
        var r = s.Poll(50_000 + elapsed, barMinimized: false);
        Assert.Equal(fires, r is not null);
        if (r is not null) { Assert.Equal(1, r.Sequence); Assert.Equal(50_000 + elapsed, r.TickMs); }
    }

    [Theory]
    [InlineData(9999, false)]
    [InlineData(10000, true)]
    [InlineData(10001, true)]
    public void MinimizedPollInterval_boundary(int elapsed, bool fires)
    {
        var s = new WindowPollScheduler(50_000);
        var r = s.Poll(50_000 + elapsed, barMinimized: true);
        Assert.Equal(fires, r is not null);
        if (r is not null) { Assert.Equal(1, r.Sequence); Assert.Equal(50_000 + elapsed, r.TickMs); }
    }

    [Fact]
    public void Poll_sequence_increments_across_fires()
    {
        var s = new WindowPollScheduler(0);
        Assert.Null(s.Poll(1999, false));
        Assert.Equal(1, s.Poll(2000, false)!.Sequence);
        Assert.Null(s.Poll(3999, false));
        Assert.Equal(2, s.Poll(4000, false)!.Sequence);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(1001, true)]
    public void PopupRefreshInterval_boundary(int elapsed, bool reads)
    {
        var p = new PopupRefreshScheduler();
        Assert.Null(p.Poll(0)); // no popup: no refresh
        p.Start(3000);
        var r = p.Poll(3000 + elapsed);
        Assert.Equal(reads, r is not null);
        if (r is not null) Assert.Equal(1, r.Sequence);
        p.Stop();
        Assert.Null(p.Poll(99_999));
    }

    [Theory]
    [InlineData(1, 1000, 999, false)]
    [InlineData(1, 1000, 1000, true)]
    [InlineData(1, 1000, 1001, true)]
    [InlineData(2, 5000, 4999, false)]
    [InlineData(2, 5000, 5000, true)]
    [InlineData(2, 5000, 5001, true)]
    [InlineData(3, 30000, 29999, false)]
    [InlineData(3, 30000, 30000, true)]
    [InlineData(3, 30000, 30001, true)]
    [InlineData(4, 30000, 29999, false)]
    public void Backoff_boundary(int failureOrdinal, int delay, int elapsed, bool spawns)
    {
        var b = new RestartBackoff();
        long now = 10_000;
        for (int i = 1; i < failureOrdinal; i++) b.RecordFailure(now);
        b.RecordFailure(now);
        Assert.Equal(failureOrdinal, b.Failures);
        Assert.Equal(now + delay, b.DueMs);
        Assert.Equal(spawns, b.CanSpawn(now + elapsed));
        b.Reset();
        Assert.True(b.CanSpawn(now));
    }
}

public class TabPopupViewModelTests
{
    private static readonly WindowKey K = new(1, 10, 100, 1);

    private static TabSnapshot Snap(TabAvailability a, params TabEntry[] tabs) =>
        new(new WorkerEpoch(1), K, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UtcNow, a, tabs);

    private static TabEntry Tab(string token, int order, bool canSelect = true) => new(new TabToken(token), "t" + token, order, false, canSelect);

    private static TabPopupViewModel Vm(Func<ResultStatus>? selectStatus = null) =>
        new(K, 7, (_, _, _, _) => Task.FromResult(new BrokerResult(selectStatus?.Invoke() ?? ResultStatus.Success)));

    [Fact]
    public void Unavailable_incomplete_and_empty_are_visibly_distinct()
    {
        var texts = new List<string>();
        foreach (var result in new[]
        {
            new BrokerResult(ResultStatus.Unavailable),
            new BrokerResult(ResultStatus.Incomplete),
            new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Available)),
            new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Incomplete, Tab("a", 0))),
        })
        {
            var vm = Vm();
            Assert.True(vm.Apply(vm.BeginRequest(), result));
            texts.Add(vm.StatusText);
            if (result.Status != ResultStatus.Success || result.Snapshot!.Availability != TabAvailability.Available)
                Assert.Empty(vm.Tabs); // partial list never shown as complete
        }
        Assert.Equal(TabPopupViewModel.UnavailableText, texts[0]);
        Assert.Equal(TabPopupViewModel.IncompleteText, texts[1]);
        Assert.Equal(TabPopupViewModel.EmptyText, texts[2]);
        Assert.Equal(TabPopupViewModel.IncompleteText, texts[3]);
        Assert.Equal(3, texts.Distinct().Count());
    }

    [Fact]
    public void Loading_until_first_result_and_failures_map_to_unavailable()
    {
        var vm = Vm();
        Assert.Equal(PopupStatus.Loading, vm.Status);
        Assert.Equal(TabPopupViewModel.LoadingText, vm.StatusText);
        foreach (var s in new[] { ResultStatus.Timeout, ResultStatus.Failed, ResultStatus.ProtocolError })
        {
            Assert.True(vm.Apply(vm.BeginRequest(), new BrokerResult(s)));
            Assert.Equal(PopupStatus.Unavailable, vm.Status);
        }
        Assert.True(vm.Apply(vm.BeginRequest(), new BrokerResult(ResultStatus.WindowGone)));
        Assert.Equal(PopupStatus.WindowGone, vm.Status);
    }

    [Fact]
    public void Stale_request_and_cancelled_results_are_ignored()
    {
        var vm = Vm();
        long first = vm.BeginRequest();
        long second = vm.BeginRequest();
        Assert.False(vm.Apply(first, new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Available, Tab("a", 0)))));
        Assert.Equal(PopupStatus.Loading, vm.Status);
        Assert.False(vm.Apply(second, new BrokerResult(ResultStatus.Cancelled)));
        Assert.False(vm.Apply(second, new BrokerResult(ResultStatus.Stale)));
        Assert.Equal(PopupStatus.Loading, vm.Status);
    }

    [Fact]
    public void Tabs_follow_strip_order_and_stale_cache_disables_selection()
    {
        var vm = Vm();
        vm.Apply(vm.BeginRequest(), new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Available, Tab("b", 1), Tab("a", 0), Tab("c", 2, canSelect: false))), isFresh: true);
        Assert.Equal(new[] { "a", "b", "c" }, vm.Tabs.Select(t => t.Token.Value));
        Assert.Equal(new[] { true, true, false }, vm.Tabs.Select(t => t.CanSelect));
        vm.Apply(vm.BeginRequest(), new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Available, Tab("a", 0))), isFresh: false);
        Assert.All(vm.Tabs, t => Assert.False(t.CanSelect));
    }

    [Fact]
    public async Task Select_raises_Selected_only_on_success_and_blocks_unselectable()
    {
        var status = ResultStatus.Success;
        var vm = Vm(() => status);
        int selected = 0;
        vm.Selected += () => selected++;
        vm.Apply(vm.BeginRequest(), new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Available, Tab("a", 0), Tab("b", 1, canSelect: false))));
        Assert.False(await vm.SelectAsync(vm.Tabs[1]));
        status = ResultStatus.Stale;
        Assert.False(await vm.SelectAsync(vm.Tabs[0]));
        Assert.Equal(0, selected);
        status = ResultStatus.Success;
        Assert.True(await vm.SelectAsync(vm.Tabs[0]));
        Assert.Equal(1, selected);
    }

    [Fact]
    public void Dispose_clears_state_and_ignores_later_results()
    {
        var vm = Vm();
        long id = vm.BeginRequest();
        vm.Apply(id, new BrokerResult(ResultStatus.Success, Snap(TabAvailability.Available, Tab("a", 0))));
        vm.Dispose();
        Assert.Empty(vm.Tabs);
        Assert.False(vm.Apply(vm.BeginRequest(), new BrokerResult(ResultStatus.Unavailable)));
    }

    [Fact]
    public void Taskbar_hides_count_when_not_available_and_closes_popup_when_window_disappears()
    {
        var hover = new HoverController();
        var toggled = new List<WindowKey>();
        var bar = new TaskbarViewModel(hover, () => 0, toggled.Add);
        var e1 = new ChromeWindowEntry(K, "w", "w", false, false, 1);
        var other = new WindowKey(2, 10, 100, 2);
        var e2 = new ChromeWindowEntry(other, "x", "x", false, false, 2);
        var avail = new CachedTabs(Snap(TabAvailability.Available, Tab("a", 0)), true);
        var incomplete = new CachedTabs(new TabSnapshot(new WorkerEpoch(1), other, new SnapshotId(Guid.NewGuid()), DateTimeOffset.UtcNow, TabAvailability.Incomplete, Array.Empty<TabEntry>()), true);
        bar.Update(new[] { e1, e2 }, k => k == K ? avail : incomplete);
        Assert.Equal("1", bar.Buttons[0].TabCountText);
        Assert.Null(bar.Buttons[1].TabCountText);

        hover.ButtonEnter(other, 0);
        hover.Tick(1000);
        Assert.Equal(HoverState.Open, hover.State);
        bar.Update(new[] { e1 }, _ => null);
        Assert.Equal(HoverState.Idle, hover.State);
        Assert.Single(bar.Buttons);

        bar.ClickCommand.Execute(K);
        Assert.Equal(new[] { K }, toggled);
        bar.Dispose();
        Assert.Empty(bar.Buttons);
        bar.OnButtonClick(K);
        Assert.Single(toggled);
    }
}

public class LayoutMathTests
{
    private static readonly PxRect Work = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(100, 1.5, 150)]
    [InlineData(100, 2.0, 200)]
    public void Dip_px_roundtrip(double dip, double scale, double px)
    {
        Assert.Equal(px, LayoutMath.DipToPx(dip, scale));
        Assert.Equal(dip, LayoutMath.PxToDip(px, scale));
    }

    [Fact]
    public void Popup_is_bounded_and_placed_above_bottom_taskbar_within_work_area()
    {
        var anchor = new PxRect(300, 1040, 420, 1080); // button on taskbar below work area
        var p = LayoutMath.Place(anchor, Work, 2000, 5000, 1.0);
        Assert.Equal(LayoutMath.MaxPopupWidthDip, p.WidthDip);
        Assert.Equal(LayoutMath.MaxPopupHeightDip, p.HeightDip);
        Assert.True(p.AboveAnchor);
        Assert.Equal(anchor.Top - p.HeightPx, p.TopPx);
        Assert.InRange(p.LeftPx, Work.Left, Work.Right - p.WidthPx);
    }

    [Fact]
    public void Right_edge_is_clamped_and_dpi_scales_pixels()
    {
        var anchor = new PxRect(1800, 1040, 1900, 1080);
        var p = LayoutMath.Place(anchor, Work, 300, 200, 2.0);
        Assert.Equal(600, p.WidthPx);
        Assert.Equal(400, p.HeightPx);
        Assert.Equal(300, p.WidthDip);
        Assert.Equal(Work.Right - 600, p.LeftPx);
    }

    [Fact]
    public void Tiny_work_area_shrinks_popup_to_fit()
    {
        var work = new PxRect(100, 100, 300, 250);
        var anchor = new PxRect(150, 250, 250, 290);
        var p = LayoutMath.Place(anchor, work, 480, 400, 1.0);
        Assert.True(p.WidthPx <= work.Width);
        Assert.True(p.TopPx >= work.Top && p.TopPx + p.HeightPx <= work.Bottom);
        Assert.True(p.LeftPx >= work.Left && p.LeftPx + p.WidthPx <= work.Right);
    }

    [Fact]
    public void Top_taskbar_places_below_and_target_monitor_is_chosen_by_overlap()
    {
        var work = new PxRect(0, 40, 1920, 1080);
        var anchor = new PxRect(100, 0, 220, 40);
        var p = LayoutMath.Place(anchor, work, 200, 300, 1.0);
        Assert.False(p.AboveAnchor);
        Assert.Equal(40, p.TopPx);

        var left = new PxRect(0, 0, 1920, 1040);
        var right = new PxRect(1920, 0, 3840, 1040);
        Assert.Equal(right, LayoutMath.PickWorkArea(new PxRect(1900, 900, 2100, 1000), new[] { left, right }));
        Assert.Equal(left, LayoutMath.PickWorkArea(new PxRect(1800, 900, 1950, 1000), new[] { left, right }));
    }
}
