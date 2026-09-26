using System.Reflection;
using System.Runtime.CompilerServices;
using DeskBox.Features.QuickCapture;
using DeskBox.Features.Todo;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class SettingsViewModelQuickCaptureTextSizeTests
{
    [Fact]
    public async Task GlobalTextSizeSlider_RefreshesInheritedQuickCaptureSizesBeforeSilentCommit()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            settings.Settings.WidgetShell.TextSize = 12.5;
            var clipboard = new QuickCaptureClipboardRuntime(
                () => false, () => throw new InvalidOperationException(), _ => { });
            var quickCapture = new QuickCaptureSettingsCoordinator(settings, clipboard,
                (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
            using var todo = new TodoSettingsViewModel(
                new TodoSettingsCoordinator(settings), _ => { });
            var viewModel = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(
                typeof(SettingsViewModel));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(SettingsViewModel).GetField("_settingsService", flags)!
                .SetValue(viewModel, settings);
            typeof(SettingsViewModel).GetField("_todoSettings", flags)!
                .SetValue(viewModel, todo);
            typeof(SettingsViewModel).GetField("_quickCaptureSettings", flags)!
                .SetValue(viewModel, quickCapture);
            typeof(SettingsViewModel).GetField("_appearanceSettings", flags)!
                .SetValue(viewModel, new DeskBox.Features.Appearance.AppearanceSettingsViewModel(
                    new AppearanceSettingsCoordinator(settings)));

            MethodInfo sync = typeof(SettingsViewModel).GetMethod(
                "SyncQuickCaptureTextSizeFacade", flags)!;
            int quickCaptureRefreshes = 0;
            quickCapture.Changed += () =>
            {
                quickCaptureRefreshes++;
                sync.Invoke(viewModel, null);
            };
            sync.Invoke(viewModel, null);
            Assert.Equal(12.5, viewModel.QuickCaptureListTextSize);
            Assert.Equal(12.5, viewModel.QuickCaptureContentTextSize);
            viewModel.SuppressAppearanceNotifications = true;
            viewModel.DeferAppearancePersistence = true;
            viewModel.TextSize = 14.5;

            Assert.Equal(1, quickCaptureRefreshes);
            Assert.Equal(14.5, viewModel.QuickCaptureListTextSize);
            Assert.Equal(14.5, viewModel.QuickCaptureContentTextSize);
            Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
            Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureContentTextSize);

            viewModel.DeferAppearancePersistence = false;
            viewModel.SuppressAppearanceNotifications = false;
            // The slider commit saves without SettingsChanged. Its App memory
            // cleanup is unavailable in the headless CI test host.
            settings.NotifyAppearancePreviewNow();
            settings.SaveDebounced(notifySubscribers: false);
            await settings.FlushPendingSaveAsync();
            Assert.Equal(1, quickCaptureRefreshes);

            var persisted = new SettingsService(root);
            await persisted.LoadAsync();
            Assert.Equal(14.5, persisted.Settings.TextSize);
            Assert.Equal(0, persisted.Settings.QuickCapture.QuickCaptureListTextSize);
            Assert.Equal(0, persisted.Settings.QuickCapture.QuickCaptureContentTextSize);
            await quickCapture.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FacadeRefresh_PreservesInheritedOverrideAcrossSaveAndUserEdit()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            settings.Settings.WidgetShell.TextSize = 12.5;
            var clipboard = new QuickCaptureClipboardRuntime(
                () => false, () => throw new InvalidOperationException(), _ => { });
            var coordinator = new QuickCaptureSettingsCoordinator(settings, clipboard,
                (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });

            var viewModel = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(
                typeof(SettingsViewModel));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(SettingsViewModel).GetField("_quickCaptureSettings", flags)!
                .SetValue(viewModel, coordinator);
            typeof(SettingsViewModel).GetField("_settingsService", flags)!
                .SetValue(viewModel, settings);
            MethodInfo sync = typeof(SettingsViewModel).GetMethod(
                "SyncQuickCaptureTextSizeFacade", flags)!;

            sync.Invoke(viewModel, null);
            Assert.Equal(12.5, viewModel.QuickCaptureListTextSize);
            Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
            await settings.SaveAsync();

            settings.Settings.WidgetShell.TextSize = 14.5;
            coordinator.RefreshFromSettings();
            sync.Invoke(viewModel, null);
            Assert.Equal(14.5, viewModel.QuickCaptureListTextSize);
            Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
            await settings.SaveAsync();
            var inherited = new SettingsService(root);
            await inherited.LoadAsync();
            Assert.Equal(0, inherited.Settings.QuickCapture.QuickCaptureListTextSize);

            viewModel.QuickCaptureListTextSize = 13.5;
            await settings.FlushPendingSaveAsync();
            var overridden = new SettingsService(root);
            await overridden.LoadAsync();
            Assert.Equal(13.5,
                overridden.Settings.QuickCapture.QuickCaptureListTextSize);
            await coordinator.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
