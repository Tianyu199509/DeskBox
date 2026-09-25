using DeskBox.Contracts;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class QuickCaptureSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TextSizes_InheritGlobalUntilExplicitlyOverridden()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.WidgetShell.TextSize = 12.5;
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        int saves = 0;
        settings.SettingsChanged += () => saves++;

        Assert.Equal(new(12.5, 12.5), coordinator.ReadTextSizes());
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);

        settings.Settings.WidgetShell.TextSize = 14.5;
        coordinator.RefreshFromSettings();
        Assert.Equal(new(14.5, 14.5), coordinator.ReadTextSizes());
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);

        Assert.True(coordinator.TrySetListTextSize(12.26, scheduleSave: false));
        Assert.Equal(12.5, settings.Settings.QuickCapture.QuickCaptureListTextSize);
        Assert.Equal(0, saves);
        settings.Settings.WidgetShell.TextSize = 15;
        coordinator.RefreshFromSettings();
        Assert.Equal(new(12.5, 15), coordinator.ReadTextSizes());
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);

        Assert.True(coordinator.TrySetContentTextSize(17, scheduleSave: false));
        Assert.Equal(SettingsService.MaxTextSize,
            settings.Settings.QuickCapture.QuickCaptureContentTextSize);
        await settings.SaveAsync();
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(12.5, reloaded.Settings.QuickCapture.QuickCaptureListTextSize);
        Assert.Equal(SettingsService.MaxTextSize,
            reloaded.Settings.QuickCapture.QuickCaptureContentTextSize);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task GlobalSizeRefresh_DoesNotRefreshTheClipboardSession()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        var sessions = new List<FakeSession>();
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () =>
            {
                var session = new FakeSession();
                sessions.Add(session);
                return session;
            }, _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        coordinator.RefreshFromSettings();
        Assert.Single(sessions);
        int refreshes = sessions[0].Refreshes;
        int changes = 0;
        coordinator.Changed += () => changes++;

        settings.Settings.WidgetShell.TextSize = 14.5;
        coordinator.RefreshFromSettings();

        Assert.Equal(1, changes);
        Assert.Equal(new(14.5, 14.5), coordinator.ReadTextSizes());
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);
        Assert.Same(sessions[0], clipboard.Current);
        Assert.Equal(refreshes, sessions[0].Refreshes);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task TextSizeWriter_RejectsNonFiniteInputAndStoppedChanges()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });

        Assert.False(coordinator.TrySetListTextSize(double.NaN));
        Assert.False(coordinator.TrySetContentTextSize(double.PositiveInfinity));
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
        Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);
        await coordinator.StopAsync();
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.TrySetListTextSize(12));
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.TrySetContentTextSize(12));
    }

    [Fact]
    public async Task RapidRecentLimitChanges_TrimOnlyTheLatestQueuedValue()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var trims = new List<int>();
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { },
            (limit, _) => { trims.Add(limit); return Task.CompletedTask; });

        coordinator.SetRecentLimit(10);
        coordinator.SetRecentLimit(100);
        coordinator.SetRecentLimit(20);
        await coordinator.PendingRecentTrim.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(20, coordinator.ReadRecentLimit());
        Assert.Equal([20], trims);
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(20, reloaded.Settings.QuickCapture.QuickCaptureRecentLimit);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ResetRecentLimit_CancelsQueuedTrimAndUsesOuterSave()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var trims = new List<int>();
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { },
            (limit, _) => { trims.Add(limit); return Task.CompletedTask; });

        coordinator.SetRecentLimit(10);
        coordinator.ResetRecentLimit(scheduleSave: false);
        await coordinator.PendingRecentTrim.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(trims);
        Assert.Equal(QuickCaptureService.DefaultRecentLimit,
            coordinator.ReadRecentLimit());
        await settings.SaveAsync();
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(QuickCaptureService.DefaultRecentLimit,
            reloaded.Settings.QuickCapture.QuickCaptureRecentLimit);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task Stop_WaitsForActiveRecentTrimAndRejectsNewLimit()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trims = new List<int>();
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { },
            async (limit, _) =>
            {
                trims.Add(limit);
                started.TrySetResult(limit);
                await release.Task;
            });

        coordinator.SetRecentLimit(10);
        Assert.Equal(10, await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        coordinator.SetRecentLimit(20);
        Task stopping = coordinator.StopAsync();
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([10], trims);
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetRecentLimit(30));
    }

    [Fact]
    public async Task Stop_CancelsCooperativeRecentTrimBeforeWaiting()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<Exception>();
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, errors.Add,
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        coordinator.SetRecentLimit(10);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(errors);
        Assert.True(coordinator.PendingRecentTrim.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FailedRecentTrim_IsReportedAndDoesNotBlockTheNextRequest()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var errors = new List<Exception>();
        var trims = new List<int>();
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, errors.Add,
            (limit, _) =>
            {
                trims.Add(limit);
                return trims.Count == 1
                    ? Task.FromException(new IOException("recent store unavailable"))
                    : Task.CompletedTask;
            });

        coordinator.SetRecentLimit(10);
        await coordinator.PendingRecentTrim.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(errors);
        Assert.IsType<IOException>(errors[0]);

        coordinator.SetRecentLimit(20);
        await coordinator.PendingRecentTrim.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([10, 20], trims);
        Assert.Single(errors);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task RecentLimitTrim_UsesTheRealStoreAndPersistsTheRemainingItems()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        string storeDirectory = Path.Combine(_root, "capture");
        var capture = new QuickCaptureService(new QuickCaptureStore(storeDirectory));
        for (int i = 0; i < 25; i++)
            await capture.AddRecentClipboardItemAsync($"recent item {i}", 100);
        Assert.Equal(25, (await capture.GetDataAsync()).RecentItems.Count);

        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { },
            capture.TrimRecentItemsAsync);
        coordinator.SetRecentLimit(10);
        await coordinator.PendingRecentTrim.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(10, (await capture.GetDataAsync()).RecentItems.Count);
        var reloaded = new QuickCaptureService(new QuickCaptureStore(storeDirectory));
        Assert.Equal(10, (await reloaded.GetDataAsync()).RecentItems.Count);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ExternalRecentLimitChange_ReconcilesWithoutRefreshingClipboard()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        var sessions = new List<FakeSession>();
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () =>
            {
                var session = new FakeSession();
                sessions.Add(session);
                return session;
            }, _ => { });
        var trims = new List<int>();
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { },
            (limit, _) => { trims.Add(limit); return Task.CompletedTask; });
        coordinator.RefreshFromSettings();
        Assert.Single(sessions);
        int clipboardRefreshes = sessions[0].Refreshes;
        int changes = 0;
        coordinator.Changed += () => changes++;

        settings.Settings.QuickCapture.QuickCaptureRecentLimit = 15;
        await settings.SaveAsync();
        await coordinator.PendingRecentTrim.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(15, coordinator.ReadRecentLimit());
        Assert.Equal([15], trims);
        Assert.Equal(1, changes);
        Assert.Equal(clipboardRefreshes, sessions[0].Refreshes);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task PresentationPreferences_NormalizePersistAndResetTogether()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.QuickCapture.QuickCaptureTabStyle = "Unsupported";
        settings.Settings.QuickCapture.QuickCaptureShowCreatedTime = false;
        settings.Settings.QuickCapture.QuickCaptureItemPreviewLineCount = 42;
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        QuickCapturePresentationSettings safe = coordinator.ReadPresentation();
        Assert.Equal(SettingsService.WidgetTabStyleButton, safe.TabStyle);
        Assert.False(safe.ShowCreatedTime);
        Assert.Equal(SettingsService.MaxItemPreviewLineCount, safe.PreviewLineCount);
        Assert.Equal("Unsupported", settings.Settings.QuickCapture.QuickCaptureTabStyle);
        Assert.Equal(42, settings.Settings.QuickCapture.QuickCaptureItemPreviewLineCount);

        coordinator.SetTabStyle(SettingsService.WidgetTabStylePivot);
        coordinator.SetShowCreatedTime(true);
        coordinator.SetPreviewLineCount(-5);
        Assert.Equal(new(SettingsService.WidgetTabStylePivot, true,
            SettingsService.MinItemPreviewLineCount), coordinator.ReadPresentation());
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.WidgetTabStylePivot,
            reloaded.Settings.QuickCapture.QuickCaptureTabStyle);
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureShowCreatedTime);
        Assert.Equal(SettingsService.MinItemPreviewLineCount,
            reloaded.Settings.QuickCapture.QuickCaptureItemPreviewLineCount);

        int notifications = 0;
        settings.SettingsChanged += () => notifications++;
        coordinator.ResetPresentationPreferences(scheduleSave: false);
        Assert.Equal(0, notifications);
        Assert.Equal(new(SettingsService.WidgetTabStyleButton, true,
            SettingsService.DefaultQuickCaptureItemPreviewLineCount),
            coordinator.ReadPresentation());
        await settings.SaveAsync();
        Assert.Equal(1, notifications);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ExternalPresentationChange_NotifiesWithoutRefreshingClipboard()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        var sessions = new List<FakeSession>();
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () =>
            {
                var session = new FakeSession();
                sessions.Add(session);
                return session;
            }, _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        coordinator.RefreshFromSettings();
        Assert.Single(sessions);
        int refreshes = sessions[0].Refreshes;
        int changes = 0;
        coordinator.Changed += () => changes++;

        settings.Settings.QuickCapture.QuickCaptureTabStyle =
            SettingsService.WidgetTabStylePivot;
        settings.Settings.QuickCapture.QuickCaptureShowCreatedTime = false;
        settings.Settings.QuickCapture.QuickCaptureItemPreviewLineCount = 5;
        coordinator.RefreshFromSettings();

        Assert.Equal(1, changes);
        Assert.Equal(new(SettingsService.WidgetTabStylePivot, false, 5),
            coordinator.ReadPresentation());
        Assert.Same(sessions[0], clipboard.Current);
        Assert.Equal(refreshes, sessions[0].Refreshes);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedPresentationWriter_RejectsChanges()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        QuickCapturePresentationSettings before = coordinator.ReadPresentation();
        await coordinator.StopAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.SetTabStyle(SettingsService.WidgetTabStylePivot));
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.SetShowCreatedTime(false));
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.SetPreviewLineCount(8));
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.ResetPresentationPreferences());
        Assert.Equal(before, coordinator.ReadPresentation());
    }

    [Fact]
    public async Task TabChanges_DoNotRefreshTheClipboardSession()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        var sessions = new List<FakeSession>();
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () =>
            {
                var session = new FakeSession();
                sessions.Add(session);
                return session;
            }, _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        coordinator.RefreshFromSettings();
        Assert.Single(sessions);
        int refreshes = sessions[0].Refreshes;
        int changes = 0;
        coordinator.Changed += () => changes++;

        coordinator.SetTabBarVisible(false);
        settings.Settings.QuickCapture.QuickCaptureShowRecentTab = false;
        coordinator.RefreshFromSettings();

        Assert.Equal(2, changes);
        Assert.Same(sessions[0], clipboard.Current);
        Assert.Equal(refreshes, sessions[0].Refreshes);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task SelectingHiddenDefaultView_CommitsVisibilityAndPreferenceOnce()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.QuickCapture.QuickCaptureShowPinnedTab = false;
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        int notifications = 0;
        int changes = 0;
        settings.SettingsChanged += () => notifications++;
        coordinator.Changed += () => changes++;

        coordinator.SetDefaultView(SettingsService.QuickCaptureDefaultViewPinned);

        Assert.Equal(SettingsService.QuickCaptureDefaultViewPinned,
            coordinator.ReadTabs().DefaultView);
        Assert.True(coordinator.ReadTabs().ShowPinnedTab);
        Assert.Equal(1, notifications);
        Assert.Equal(1, changes);
        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.QuickCaptureDefaultViewPinned,
            reloaded.Settings.QuickCapture.QuickCaptureDefaultView);
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureShowPinnedTab);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task HidingDefaultTab_FallsBackAndNeverHidesTheLastTab()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });

        coordinator.SetDefaultView(SettingsService.QuickCaptureDefaultViewPinned);
        coordinator.SetTabVisible(SettingsService.QuickCaptureDefaultViewRecords, false);
        coordinator.SetTabVisible(SettingsService.QuickCaptureDefaultViewPinned, false);
        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecent,
            coordinator.ReadTabs().DefaultView);

        coordinator.SetTabVisible(SettingsService.QuickCaptureDefaultViewRecent, false);
        QuickCaptureTabSettings tabs = coordinator.ReadTabs();
        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecords, tabs.DefaultView);
        Assert.True(tabs.ShowRecordsTab);
        Assert.False(tabs.ShowPinnedTab);
        Assert.False(tabs.ShowRecentTab);
        Assert.Equal(tabs.DefaultView,
            settings.Settings.QuickCapture.QuickCaptureDefaultView);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ExternalInvalidTabs_AreReadSafelyAndResetWithoutEarlySave()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        int notifications = 0;
        settings.SettingsChanged += () => notifications++;
        settings.Settings.QuickCapture.QuickCaptureDefaultView = "Unsupported";
        settings.Settings.QuickCapture.QuickCaptureShowRecordsTab = false;
        settings.Settings.QuickCapture.QuickCaptureShowPinnedTab = false;
        settings.Settings.QuickCapture.QuickCaptureShowRecentTab = false;

        QuickCaptureTabSettings safe = coordinator.ReadTabs();
        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecords,
            safe.DefaultView);
        Assert.True(safe.ShowRecordsTab);
        Assert.Equal("Unsupported",
            settings.Settings.QuickCapture.QuickCaptureDefaultView);
        Assert.False(settings.Settings.QuickCapture.QuickCaptureShowRecordsTab);
        coordinator.RefreshFromSettings();
        coordinator.ResetTabPreferences(scheduleSave: false);
        Assert.Equal(0, notifications);

        await settings.SaveAsync();
        Assert.Equal(1, notifications);
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecords,
            reloaded.Settings.QuickCapture.QuickCaptureDefaultView);
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureShowRecordsTab);
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureShowPinnedTab);
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureShowRecentTab);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedTabWriter_RejectsChangesWithoutMutatingSettings()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        QuickCaptureTabSettings before = coordinator.ReadTabs();
        await coordinator.StopAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.SetDefaultView(SettingsService.QuickCaptureDefaultViewPinned));
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.SetTabVisible(SettingsService.QuickCaptureDefaultViewRecords, false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetTabBarVisible(false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.ResetTabPreferences());
        Assert.Equal(before, coordinator.ReadTabs());
    }

    [Fact]
    public async Task RapidEnableThenDisable_KeepsLatestSettingsAndStopsListener()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var pendingEnable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<bool>();
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () => new FakeSession(), _ => { });
        QuickCaptureSettingsCoordinator? coordinator = null;
        coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            async (enabled, _) =>
            {
                calls.Add(enabled);
                if (enabled) await pendingEnable.Task;
                coordinator!.CommitEnabledState(enabled);
            }, action => { action(); return true; }, _ => { });

        Task first = coordinator.SetEnabledAsync(true);
        Assert.True(coordinator.Read().Enabled);
        Task last = coordinator.SetEnabledAsync(false);
        Assert.False(coordinator.Read().Enabled);
        Assert.False(last.IsCompleted);
        pendingEnable.SetResult();
        await Task.WhenAll(first, last);
        Assert.Equal([true, false], calls);
        Assert.Equal(new(false, false, false), coordinator.Read());
        Assert.Null(clipboard.Current);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task OpenWidgetEnablesClipboard_AfterSavingWithoutRevealingWidget()
    {
        string settingsDirectory = Path.Combine(_root, "settings");
        string settingsPath = Path.Combine(settingsDirectory, "settings.json");
        var settings = new SettingsService(settingsDirectory);
        Assert.False(File.Exists(settingsPath));
        bool savedBeforeRefresh = false;
        int widgetApplies = 0;
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings),
            () => new FakeSession(onRefresh: () =>
            {
                if (!File.Exists(settingsPath)) return;
                using System.Text.Json.JsonDocument persisted =
                    System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                savedBeforeRefresh = persisted.RootElement
                    .GetProperty("quickCaptureClipboardEnabled").GetBoolean();
            }), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => { widgetApplies++; return Task.CompletedTask; },
            action => { action(); return true; }, _ => { });

        await coordinator.EnableClipboardFromOpenWidgetAsync();

        Assert.True(savedBeforeRefresh);
        Assert.Equal(0, widgetApplies);
        Assert.Equal(new(true, true, false), coordinator.Read());
        var reloaded = new SettingsService(settingsDirectory);
        await reloaded.LoadAsync();
        Assert.True(FeatureWidgetSettings.IsEnabled(reloaded.Settings,
            WidgetKind.QuickCapture));
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureClipboardEnabled);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ImageRecordingEnablesDependencies_AndDisablingTextRetiresListener()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var sessions = new List<FakeSession>();
        var clipboard = new QuickCaptureClipboardRuntime(
            () => CanListen(settings), () =>
            {
                var session = new FakeSession();
                sessions.Add(session);
                return session;
            }, _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });

        await coordinator.SetImageEnabledAsync(true);
        Assert.Equal(new(true, true, true), coordinator.Read());
        Assert.Single(sessions);
        Assert.Same(sessions[0], clipboard.Current);
        await coordinator.SetClipboardEnabledAsync(false);
        Assert.Equal(new(true, false, false), coordinator.Read());
        Assert.Null(clipboard.Current);
        Assert.True(sessions[0].Disposed);

        // Restored settings bypass the editor but still reconcile through
        // the same owner, including the dependency invariant.
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, false);
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        coordinator.RefreshFromSettings();
        Assert.Equal(new(false, false, false), coordinator.Read());
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task FailedWindowApplyCanRetry_AndStopWaitsForStartedApply()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        int calls = 0;
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new QuickCaptureClipboardRuntime(() => CanListen(settings),
            () => new FakeSession(), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => ++calls == 1
                ? Task.FromException(new IOException("window unavailable"))
                : inFlight.Task,
            action => { action(); return true; }, _ => { });
        await Assert.ThrowsAsync<IOException>(() => coordinator.SetEnabledAsync(true));
        Task retry = coordinator.SetEnabledAsync(true);
        Task stopping = coordinator.StopAsync();
        Assert.False(stopping.IsCompleted);
        inFlight.SetResult();
        await retry;
        await stopping;
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.SetEnabledAsync(false));
    }

    [Fact]
    public async Task ExternalSettingsNotificationReconcilesWindow_AndCannotRestartAfterStop()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int created = 0;
        var clipboard = new QuickCaptureClipboardRuntime(() => CanListen(settings),
            () => { created++; return new FakeSession(); }, _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (enabled, _) => { applied.TrySetResult(enabled); return Task.CompletedTask; },
            action => { action(); return true; }, _ => { });
        coordinator.RefreshFromSettings();

        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled = true;
        await settings.SaveAsync();
        Assert.True(await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, created);
        await coordinator.StopAsync();
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, false);
        await settings.SaveAsync();
        Assert.Equal(1, created);
        Assert.True(clipboard.IsStopping);
    }

    [Fact]
    public async Task DisableWaitsForRetiredClipboardCapture()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new QuickCaptureClipboardRuntime(() => CanListen(settings),
            () => new FakeSession(pendingStop.Task), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        await coordinator.SetClipboardEnabledAsync(true);
        Task disable = coordinator.SetEnabledAsync(false);
        Assert.False(disable.IsCompleted);
        Assert.Equal(new(false, false, false), coordinator.Read());
        pendingStop.SetResult();
        await disable;
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ResetRecordingWaitsBeforeCallerClearsQuickCaptureData()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var pendingStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new QuickCaptureClipboardRuntime(() => CanListen(settings),
            () => new FakeSession(pendingStop.Task), _ => { });
        var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        await coordinator.SetClipboardEnabledAsync(true);
        Task reset = coordinator.ResetRecordingAsync();
        Assert.False(reset.IsCompleted);
        Assert.Equal(new(true, false, false), coordinator.Read());
        pendingStop.SetResult();
        await reset;
        await coordinator.StopAsync();
    }

    private static bool CanListen(SettingsService settings) =>
        FeatureWidgetSettings.IsEnabled(settings.Settings, WidgetKind.QuickCapture) &&
        settings.Settings.QuickCapture.QuickCaptureClipboardEnabled;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeSession(Task? stop = null, Action? onRefresh = null) : IQuickCaptureClipboardSession
    {
        public event Action? DiagnosticsChanged;
        public bool Disposed { get; private set; }
        public int Refreshes { get; private set; }
        public void Refresh()
        {
            Refreshes++;
            onRefresh?.Invoke();
            DiagnosticsChanged?.Invoke();
        }
        public void CaptureCurrent() { }
        public QuickCaptureClipboardDiagnostics GetDiagnostics() =>
            new(true, true, null, "enabled", null);
        public Task StopAsync() => stop ?? Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
