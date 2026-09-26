using DeskBox.Controls;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    internal void ActivateQuickLookNavigationTarget(FileWidgetContentAdapter content)
    {
        if (!Visible || !ReferenceEquals(CurrentContent, content))
        {
            return;
        }

        base.Activate();
        Win32Helper.SetForegroundWindow(HWnd);
        content.FocusQuickLookNavigationTarget();
    }

    private WidgetCompactPresentation CreateFileCompactPresentation(
        FileWidgetContentAdapter file,
        string contentMode)
    {
        bool hidesSensitiveContent =
            SettingsService.Settings.WidgetCompactHideSensitiveContent;
        string count = App.Current.LocalizationService.Format(
            "Widget.Compact.FileCount",
            file.ViewModel.Items.Count);
        WidgetItem? latest = hidesSensitiveContent
            ? null
            : file.ViewModel.Items
                .Where(item => item.LastModified != default)
                .MaxBy(item => item.LastModified) ??
              file.ViewModel.Items.LastOrDefault();
        string summary = contentMode switch
        {
            DeskBox.Services.SettingsService.WidgetCompactContentModeMinimal =>
                string.Empty,
            DeskBox.Services.SettingsService.WidgetCompactContentModeSmart
                when latest is not null =>
                $"{latest.Name} · {count}",
            _ => count
        };

        return new WidgetCompactPresentation(
            file.Config.Name,
            summary,
            file.ViewModel.IconGlyph,
            App.Current.LocalizationService.T("Widget.Compact.FileDropHint"),
            EnableMarquee: true,
            LiveStateKey: hidesSensitiveContent
                ? file.ViewModel.Items.Count.ToString()
                : $"{file.ViewModel.Items.Count}|{latest?.Path ?? string.Empty}");
    }
}
