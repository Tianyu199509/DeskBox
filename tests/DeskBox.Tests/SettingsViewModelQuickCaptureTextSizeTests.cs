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
    private static QuickCaptureSettingsViewModel CreateEditor(
        QuickCaptureSettingsCoordinator coordinator) =>
        new(
            coordinator,
            _ => string.Empty,
            (key, args) => key,
            _ => { },
            _ => { });

    private static SettingsViewModel CreateShell(
        SettingsService settings,
        TodoSettingsViewModel todo,
        QuickCaptureSettingsCoordinator quickCapture,
        DeskBox.Features.Appearance.AppearanceSettingsViewModel appearance,
        QuickCaptureSettingsViewModel quickCaptureEditor,
        BindingFlags flags)
    {
        var viewModel = (SettingsViewModel)RuntimeHelpers.GetUninitializedObject(
            typeof(SettingsViewModel));
        typeof(SettingsViewModel).GetField("_settingsService", flags)!
            .SetValue(viewModel, settings);
        typeof(SettingsViewModel).GetField("_todoSettings", flags)!
            .SetValue(viewModel, todo);
        typeof(SettingsViewModel).GetField("_quickCaptureSettings", flags)!
            .SetValue(viewModel, quickCapture);
        typeof(SettingsViewModel).GetField("_quickCaptureSettingsEditor", flags)!
            .SetValue(viewModel, quickCaptureEditor);
        typeof(SettingsViewModel).GetField("_appearanceSettings", flags)!
            .SetValue(viewModel, appearance);
        return viewModel;
    }

    [Fact]
    public async Task GlobalTextSizeSlider_RefreshesInheritedQuickCaptureSizesBeforeSilentCommit()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            settings.Settings.WidgetShell.TextSize = 12.5;
            var quickCapture = new QuickCaptureSettingsCoordinator(settings,
                (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
            using var todo = new TodoSettingsViewModel(
                new TodoSettingsCoordinator(settings),
                _ => string.Empty,
                (key, args) => key,
                _ => { });
            var quickCaptureEditor = CreateEditor(quickCapture);
            var editor = new DeskBox.Features.Appearance.AppearanceSettingsViewModel(
                new AppearanceSettingsCoordinator(settings),
                _ => string.Empty);
            var viewModel = CreateShell(
                settings, todo, quickCapture, editor, quickCaptureEditor,
                BindingFlags.Instance | BindingFlags.NonPublic);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo textSizeCommitted = typeof(SettingsViewModel).GetMethod(
                "OnAppearanceTextSizeCommitted", flags)!;
            MethodInfo quickCaptureChanged = typeof(SettingsViewModel).GetMethod(
                "OnQuickCaptureSettingsChanged", flags)!;
            // Mirror the production wiring: the appearance editor raises the
            // commit event, the shell handler runs the save pass plus the
            // Todo and Quick Capture refreshes; the coordinator's Changed
            // broadcast re-projects the Quick Capture editor.
            editor.TextSizeCommitted += () => textSizeCommitted.Invoke(viewModel, null);
            int quickCaptureRefreshes = 0;
            quickCapture.Changed += () =>
            {
                quickCaptureRefreshes++;
                quickCaptureChanged.Invoke(viewModel, null);
            };
            Assert.Equal(12.5, quickCaptureEditor.ListTextSize);
            Assert.Equal(12.5, quickCaptureEditor.ContentTextSize);
            viewModel.SuppressAppearanceNotifications = true;
            viewModel.DeferAppearancePersistence = true;
            editor.TextSize = 14.5;

            Assert.Equal(1, quickCaptureRefreshes);
            Assert.Equal(14.5, quickCaptureEditor.ListTextSize);
            Assert.Equal(14.5, quickCaptureEditor.ContentTextSize);
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
            var coordinator = new QuickCaptureSettingsCoordinator(settings,
                (_, _) => Task.CompletedTask, action => { action(); return true; }, _ => { });
            var quickCaptureEditor = CreateEditor(coordinator);
            // Mirror the production wiring: the editor commits text-size
            // writes with scheduleSave:false and the shell answers the
            // commit event with the debounced appearance save.
            quickCaptureEditor.ListTextSizeCommitted += () => settings.SaveDebounced();

            Assert.Equal(12.5, quickCaptureEditor.ListTextSize);
            Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
            await settings.SaveAsync();

            settings.Settings.WidgetShell.TextSize = 14.5;
            coordinator.RefreshFromSettings();
            quickCaptureEditor.SyncPresentation();
            Assert.Equal(14.5, quickCaptureEditor.ListTextSize);
            Assert.Equal(0, settings.Settings.QuickCapture.QuickCaptureListTextSize);
            await settings.SaveAsync();
            var inherited = new SettingsService(root);
            await inherited.LoadAsync();
            Assert.Equal(0, inherited.Settings.QuickCapture.QuickCaptureListTextSize);

            quickCaptureEditor.ListTextSize = 13.5;
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
