using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private CustomWidgetStudioWindow? _customWidgetStudioWindow;

    internal async Task HandleProtocolActivationUriAsync(string rawUri)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(rawUri))
        {
            return;
        }

        if (!DeskBoxProtocolActivationService.TryParseProtocolUri(rawUri, out DeskBoxProtocolIntent? intent, out string? error) ||
            intent is null)
        {
            Log($"[Protocol] Rejected deskbox:// URI '{rawUri}': {error}");
            return;
        }

        if (intent.Action == DeskBoxProtocolAction.OpenStudio)
        {
            ShowCustomWidgetStudio(intent.WidgetId, intent.Idea);
            return;
        }

        if (intent.Action == DeskBoxProtocolAction.InstallWidget)
        {
            (string? manifestJson, string? displayLabel, string? resolveError) =
                await DeskBoxProtocolActivationService.ResolveInstallWidgetManifestAsync(intent);
            if (!string.IsNullOrWhiteSpace(manifestJson))
            {
                ShowCustomWidgetStudioPreview(manifestJson, displayLabel);
            }
            else
            {
                Log($"[Protocol] install-widget failed for '{rawUri}': {resolveError}");
                _nativeNotificationService?.TryShow(
                    LocalizationService.T("CustomWidget.Title"),
                    resolveError ?? LocalizationService.T("CustomWidget.ClientUpgrade.Subtitle"));
            }
        }
    }

    /// <summary>
    /// Opens the standalone Widget Workshop. <paramref name="widgetId"/> edits an
    /// existing custom widget with AI; <paramref name="initialIdea"/> starts
    /// generating right away from a sentence typed elsewhere.
    /// </summary>
    public void ShowCustomWidgetStudio(string? widgetId = null, string? initialIdea = null)
    {
        if (IsShuttingDown)
        {
            return;
        }

        CancelBackgroundMemoryCleanup();
        if (_customWidgetStudioWindow is null)
        {
            EnsureXamlLoopLifelineIfNoOtherWindows();
            var window = new CustomWidgetStudioWindow(LocalizationService);
            _customWidgetStudioWindow = window;
            ThemeService.TrackWindow(window);
            window.Closed += CustomWidgetStudioWindow_Closed;
            window.Initialize(widgetId, initialIdea);
        }
        else
        {
            _customWidgetStudioWindow.RequestTarget(widgetId, initialIdea);
        }

        _customWidgetStudioWindow.ShowWindow();
    }

    public void ShowCustomWidgetStudioPreview(string manifestJson, string? sourceLabel = null)
    {
        if (IsShuttingDown || string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        CancelBackgroundMemoryCleanup();
        if (_customWidgetStudioWindow is null)
        {
            EnsureXamlLoopLifelineIfNoOtherWindows();
            var window = new CustomWidgetStudioWindow(LocalizationService);
            _customWidgetStudioWindow = window;
            ThemeService.TrackWindow(window);
            window.Closed += CustomWidgetStudioWindow_Closed;
            window.Initialize(null, null);
        }

        _customWidgetStudioWindow.OpenExternalManifestPreview(manifestJson, sourceLabel);
        _customWidgetStudioWindow.ShowWindow();
    }

    private void CustomWidgetStudioWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is not CustomWidgetStudioWindow window)
        {
            return;
        }

        window.Closed -= CustomWidgetStudioWindow_Closed;
        if (ReferenceEquals(_customWidgetStudioWindow, window))
        {
            _customWidgetStudioWindow = null;
        }

        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        ScheduleBackgroundMemoryCleanup("custom-widget-studio-closed");
    }
}
