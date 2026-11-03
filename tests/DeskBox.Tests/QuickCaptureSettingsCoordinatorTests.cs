using DeskBox.Contracts;
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
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
    public async Task TextSizeWriter_RejectsNonFiniteInputAndStoppedChanges()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
    public async Task PresentationPreferences_NormalizePersistAndResetTogether()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.QuickCapture.QuickCaptureTabStyle = "Unsupported";
        settings.Settings.QuickCapture.QuickCaptureShowCreatedTime = false;
        settings.Settings.QuickCapture.QuickCaptureItemPreviewLineCount = 42;
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
    public async Task StoppedPresentationWriter_RejectsChanges()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
    public async Task SelectingHiddenDefaultView_CommitsVisibilityAndPreferenceOnce()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.QuickCapture.QuickCaptureShowPinnedTab = false;
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });

        coordinator.SetDefaultView(SettingsService.QuickCaptureDefaultViewPinned);
        coordinator.SetTabVisible(SettingsService.QuickCaptureDefaultViewRecords, false);
        coordinator.SetTabVisible(SettingsService.QuickCaptureDefaultViewPinned, false);

        QuickCaptureTabSettings tabs = coordinator.ReadTabs();
        Assert.Equal(SettingsService.QuickCaptureDefaultViewRecords, tabs.DefaultView);
        Assert.True(tabs.ShowRecordsTab);
        Assert.False(tabs.ShowPinnedTab);
        Assert.Equal(tabs.DefaultView,
            settings.Settings.QuickCapture.QuickCaptureDefaultView);
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task ExternalInvalidTabs_AreReadSafelyAndResetWithoutEarlySave()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        int notifications = 0;
        settings.SettingsChanged += () => notifications++;
        settings.Settings.QuickCapture.QuickCaptureDefaultView = "Unsupported";
        settings.Settings.QuickCapture.QuickCaptureShowRecordsTab = false;
        settings.Settings.QuickCapture.QuickCaptureShowPinnedTab = false;

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
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task StoppedTabWriter_RejectsChangesWithoutMutatingSettings()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
    public async Task RapidEnableThenDisable_KeepsLatestSettings()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var pendingEnable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<bool>();
        QuickCaptureSettingsCoordinator? coordinator = null;
        coordinator = new QuickCaptureSettingsCoordinator(settings,
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
        Assert.Equal(new(false), coordinator.Read());
        await coordinator.StopAsync();
    }

    [Fact]
    public async Task FailedWindowApplyCanRetry_AndStopWaitsForStartedApply()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        int calls = 0;
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
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
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
            (enabled, _) => { applied.TrySetResult(enabled); return Task.CompletedTask; },
            action => { action(); return true; }, _ => { });
        coordinator.RefreshFromSettings();

        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, true);
        await settings.SaveAsync();
        Assert.True(await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await coordinator.StopAsync();
        FeatureWidgetSettings.SetEnabled(settings.Settings, WidgetKind.QuickCapture, false);
        await settings.SaveAsync();
    }

    [Fact]
    public async Task EditorPreferences_NormalizePersistSkipUnchangedAndReset()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var coordinator = new QuickCaptureSettingsCoordinator(settings,
            (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
        int notifications = 0;
        settings.SettingsChanged += () => notifications++;

        coordinator.SetEditorEnterBehavior(SettingsService.EditorEnterBehaviorEnterSaves);
        coordinator.SetEditorFormat(SettingsService.QuickCaptureFormatPlainText);
        coordinator.SetWideLayout(SettingsService.QuickCaptureWideLayoutDualPane);
        coordinator.SetWideOpenMode(SettingsService.QuickCaptureWideOpenEditing);
        coordinator.SetAllowRemoteImages(true);
        Assert.Equal(5, notifications);
        Assert.Equal(new(
            SettingsService.EditorEnterBehaviorEnterSaves,
            SettingsService.QuickCaptureFormatPlainText,
            SettingsService.QuickCaptureWideLayoutDualPane,
            SettingsService.QuickCaptureWideOpenEditing,
            true), coordinator.ReadEditorSettings());

        coordinator.SetEditorEnterBehavior("Nonsense");
        coordinator.SetEditorFormat("Nonsense");
        coordinator.SetWideLayout("Nonsense");
        coordinator.SetWideOpenMode(null);
        coordinator.SetAllowRemoteImages(true);
        Assert.Equal(new(
            SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            SettingsService.QuickCaptureFormatMarkdown,
            SettingsService.QuickCaptureWideLayoutAuto,
            SettingsService.QuickCaptureWideOpenReading,
            true), coordinator.ReadEditorSettings());
        Assert.Equal(9, notifications);

        await settings.FlushPendingSaveAsync();
        var reloaded = new SettingsService(Path.Combine(_root, "settings"));
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            reloaded.Settings.QuickCapture.QuickCaptureEditorEnterBehavior);
        Assert.Equal(SettingsService.QuickCaptureFormatMarkdown,
            reloaded.Settings.QuickCapture.QuickCaptureDefaultFormat);
        Assert.Equal(SettingsService.QuickCaptureWideLayoutAuto,
            reloaded.Settings.QuickCapture.QuickCaptureWideLayout);
        Assert.Equal(SettingsService.QuickCaptureWideOpenReading,
            reloaded.Settings.QuickCapture.QuickCaptureWideOpenMode);
        Assert.True(reloaded.Settings.QuickCapture.QuickCaptureAllowRemoteImages);

        settings.Settings.QuickCapture.LastQuickCaptureFileWidgetId = "widget-42";
        int beforeReset = notifications;
        coordinator.ResetEditorPreferences(scheduleSave: false);
        Assert.Equal(beforeReset, notifications);
        Assert.Equal(new(
            SettingsService.EditorEnterBehaviorCtrlEnterSaves,
            SettingsService.QuickCaptureFormatMarkdown,
            SettingsService.QuickCaptureWideLayoutAuto,
            SettingsService.QuickCaptureWideOpenReading,
            false), coordinator.ReadEditorSettings());
        Assert.Equal(string.Empty,
            settings.Settings.QuickCapture.LastQuickCaptureFileWidgetId);
        await settings.SaveAsync();
        Assert.Equal(beforeReset + 1, notifications);
        await coordinator.StopAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}