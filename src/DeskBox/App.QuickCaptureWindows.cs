using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private QuickCaptureWindowCoordinator? _quickCaptureWindowCoordinator;

    internal QuickCaptureWindowCoordinator EnsureQuickCaptureWindowCoordinator()
    {
        return _quickCaptureWindowCoordinator ??= new QuickCaptureWindowCoordinator(
            QuickCaptureService,
            SettingsService,
            ThemeService,
            LocalizationService);
    }

    public void OpenQuickCapturePopoutWindow(
        string itemId,
        bool startAlwaysOnTop = false,
        bool startEditing = false)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        EnsureQuickCaptureWindowCoordinator().OpenOrActivatePopout(
            itemId,
            startAlwaysOnTop,
            startEditing);
    }

    public void ShowQuickJotWindow()
    {
        IntPtr prev = Win32Helper.GetForegroundWindow();
        EnsureQuickCaptureWindowCoordinator().ShowQuickJot(prev);
    }

    private void AppendQuickCapturePopoutWindowHandles(List<IntPtr> handles)
    {
        _quickCaptureWindowCoordinator?.AppendNonTopmostHandles(handles);
    }

    private async Task HandleQuickCaptureSearchActionAsync(string actionId)
    {
        if (string.Equals(actionId, "quick-jot", StringComparison.Ordinal))
        {
            ShowQuickJotWindow();
            return;
        }

        const string quickNotePrefix = "quick-note:";
        if (actionId.StartsWith(quickNotePrefix, StringComparison.Ordinal))
        {
            string body = actionId[quickNotePrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                TextContentFormat format = string.Equals(
                    SettingsService.NormalizeQuickCaptureFormat(
                        SettingsService.Settings.QuickCapture.QuickCaptureDefaultFormat),
                    SettingsService.QuickCaptureFormatPlainText,
                    StringComparison.Ordinal)
                    ? TextContentFormat.PlainText
                    : TextContentFormat.Markdown;
                await QuickCaptureService.AddDetailedItemWithResultAsync(
                    null,
                    body,
                    QuickCaptureAppearancePreset.Default,
                    pin: false,
                    contentFormat: format);
            }
        }
    }
}
