using System.Reflection;
using System.Runtime.CompilerServices;
using DeskBox.Features.QuickCapture;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class SettingsViewModelQuickCaptureTextSizeTests
{
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
