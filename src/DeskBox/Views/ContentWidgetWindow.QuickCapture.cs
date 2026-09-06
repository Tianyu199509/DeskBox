using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    private WidgetCompactPresentation CreateQuickCaptureCompactPresentation(
        QuickCaptureSurfaceContent quickCapture,
        string contentMode)
    {
        bool hidesSensitiveContent =
            SettingsService.Settings.WidgetCompactHideSensitiveContent;
        var latestItem = quickCapture.ViewModel.Items.FirstOrDefault();
        string summary = contentMode switch
        {
            DeskBox.Services.SettingsService.WidgetCompactContentModeMinimal => string.Empty,
            DeskBox.Services.SettingsService.WidgetCompactContentModeSmart
                when !hidesSensitiveContent =>
                latestItem?.DisplayText?.ReplaceLineEndings(" ").Trim() ??
                App.Current.LocalizationService.Format(
                    "Widget.Compact.QuickCaptureCount",
                    quickCapture.ViewModel.RecordCount),
            _ => App.Current.LocalizationService.Format(
                "Widget.Compact.QuickCaptureCount",
                quickCapture.ViewModel.RecordCount)
        };

        return new WidgetCompactPresentation(
            quickCapture.ViewModel.DisplayName,
            summary,
            "\uE70F",
            App.Current.LocalizationService.T(
                "Widget.Compact.QuickCaptureDropHint"),
            UseStackedText:
                contentMode == DeskBox.Services.SettingsService.WidgetCompactContentModeSmart &&
                !hidesSensitiveContent,
            // Note bodies can be arbitrarily long; the capsule stays static
            // instead of marqueeing (same policy as Todo).
            LiveStateKey: string.Join(
                "|",
                quickCapture.ViewModel.RecordCount,
                summary));
    }
}
