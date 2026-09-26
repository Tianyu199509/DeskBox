using DeskBox.Contracts;
using DeskBox.Features.Interaction;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class InteractionSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ToggleWrites_PersistThroughThePort_AndSkipUnchangedSaves()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);
        var editor = new InteractionSettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.SetAutoStart(false);
        editor.SetAutoCheckForUpdates(false);
        editor.SetDoubleClickToOpen(false);
        editor.SetFileItemSystemContextMenuEnabled(true);
        editor.SetResizeSnapEnabled(false);
        editor.SetKeepWidgetsVisibleOnShowDesktop(false);
        editor.SetShowHoverButtons(false);
        editor.SetIdleWorkingSetTrimEnabled(false);
        editor.SetImmediateHiddenWorkingSetTrimEnabled(false);

        Assert.False(settings.Settings.AutoStart);
        Assert.False(settings.Settings.AutoCheckForUpdates);
        Assert.False(settings.Settings.DoubleClickToOpen);
        Assert.True(settings.Settings.FileItemSystemContextMenuEnabled);
        Assert.False(settings.Settings.ResizeSnapEnabled);
        Assert.False(settings.Settings.KeepWidgetsVisibleOnShowDesktop);
        Assert.False(settings.Settings.ShowHoverButtons);
        Assert.False(settings.Settings.IdleWorkingSetTrimEnabled);
        Assert.False(settings.Settings.ImmediateHiddenWorkingSetTrimEnabled);
        Assert.Equal(9, notified);

        // Unchanged writes must not notify or save again.
        editor.SetDoubleClickToOpen(false);
        editor.SetIdleWorkingSetTrimEnabled(false);
        Assert.Equal(9, notified);
    }

    [Fact]
    public void AutoStartWrite_MirrorsRegistrationReflection_SkipsWhenUnchanged()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // The in-memory default mirrors an enabled registration; reflecting
        // the same state must not save (the page's old read-before-write
        // guard now lives in the coordinator).
        coordinator.SetAutoStart(true);
        Assert.Equal(0, notified);

        coordinator.SetAutoStart(false);
        Assert.False(settings.Settings.AutoStart);
        Assert.Equal(1, notified);
    }

    [Fact]
    public void SnapSpacingWrite_NormalizesAndClamps()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);

        coordinator.SetWidgetSnapSpacing(double.NaN);
        Assert.Equal(
            SettingsService.DefaultWidgetSnapSpacing,
            settings.Settings.WidgetSnapSpacing);

        coordinator.SetWidgetSnapSpacing(-25d);
        Assert.Equal(SettingsService.MinWidgetSnapSpacing, settings.Settings.WidgetSnapSpacing);

        coordinator.SetWidgetSnapSpacing(9_999d);
        Assert.Equal(
            SettingsService.MaxWidgetSnapSpacing,
            settings.Settings.WidgetSnapSpacing);
    }

    [Fact]
    public void LayerModeWrite_NormalizesInvalidValues_SkipsUnchangedSaves()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // Invalid input normalizes to the stored default Dynamic, so the
        // write is a no-op: no save, no notification.
        coordinator.SetWidgetLayerMode("NotAMode");
        Assert.Equal(
            SettingsService.WidgetLayerModeDynamic,
            settings.Settings.WidgetLayerMode);
        Assert.Equal(0, notified);

        coordinator.SetWidgetLayerMode(SettingsService.WidgetLayerModeQuickReveal);
        Assert.Equal(
            SettingsService.WidgetLayerModeQuickReveal,
            settings.Settings.WidgetLayerMode);
        Assert.Equal(1, notified);

        coordinator.SetWidgetLayerMode(SettingsService.WidgetLayerModeQuickReveal);
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task HoverActionSelection_StoresWithoutSchedulingItsOwnSave()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        // The settings shell commits this field through its appearance-save
        // routine; the port only stores the shell-built value.
        coordinator.SetWidgetHoverButtonActions("LockPosition,Add,More");
        Assert.Equal("LockPosition,Add,More", settings.Settings.WidgetHoverButtonActions);
        Assert.Equal(0, notified);

        // The shell-side commit (SaveAppearanceChange's debounced save)
        // persists what the port stored.
        await settings.SaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        Assert.Equal("LockPosition,Add,More", reloaded.Settings.WidgetHoverButtonActions);
    }

    [Fact]
    public async Task InteractionWrites_RoundTripThroughDisk()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);

        coordinator.SetAutoCheckForUpdates(false);
        coordinator.SetDoubleClickToOpen(false);
        coordinator.SetFileItemSystemContextMenuEnabled(true);
        coordinator.SetResizeSnapEnabled(false);
        coordinator.SetWidgetSnapSpacing(14d);
        coordinator.SetKeepWidgetsVisibleOnShowDesktop(false);
        coordinator.SetWidgetLayerMode(SettingsService.WidgetLayerModeDesktopPinned);
        coordinator.SetShowHoverButtons(false);
        coordinator.SetWidgetHoverButtonActions("LockSize,Delete");
        coordinator.SetIdleWorkingSetTrimEnabled(false);
        coordinator.SetImmediateHiddenWorkingSetTrimEnabled(false);
        await settings.SaveAsync();

        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        InteractionSettingsSnapshot snapshot =
            new InteractionSettingsCoordinator(reloaded).ReadAll();
        Assert.False(snapshot.AutoCheckForUpdates);
        Assert.False(snapshot.DoubleClickToOpen);
        Assert.True(snapshot.FileItemSystemContextMenuEnabled);
        Assert.False(snapshot.ResizeSnapEnabled);
        Assert.Equal(14d, snapshot.WidgetSnapSpacing);
        Assert.False(snapshot.KeepWidgetsVisibleOnShowDesktop);
        Assert.Equal(SettingsService.WidgetLayerModeDesktopPinned, snapshot.WidgetLayerMode);
        Assert.False(snapshot.ShowHoverButtons);
        Assert.Equal("LockSize,Delete", snapshot.WidgetHoverButtonActions);
        Assert.False(snapshot.IdleWorkingSetTrimEnabled);
        Assert.False(snapshot.ImmediateHiddenWorkingSetTrimEnabled);
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new InteractionSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(() => coordinator.SetAutoStart(false));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetWidgetSnapSpacing(12d));
        Assert.Throws<ObjectDisposedException>(() => coordinator.SetWidgetLayerMode(
            SettingsService.WidgetLayerModeQuickReveal));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWidgetHoverButtonActions("Add"));
        Assert.True(coordinator.IsStopped);

        await settings.SaveAsync();
        Assert.True(settings.Settings.AutoCheckForUpdates);
        Assert.Equal(
            SettingsService.WidgetLayerModeDynamic,
            settings.Settings.WidgetLayerMode);
    }
}
