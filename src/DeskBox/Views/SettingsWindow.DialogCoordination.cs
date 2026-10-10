using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

/// <summary>
/// Dialog coordination for dialogs raised from async continuations (update
/// downloads, background jobs). A second ShowAsync on the same XamlRoot while
/// another dialog is open throws InvalidOperationException, and async
/// continuations can land at any time — unlike click-driven dialogs, which
/// the open dialog's mask already protects. The coordinator retries briefly
/// with a fresh instance (a shown dialog cannot be reused) and finally gives
/// up as a cancel, rather than installing a window-global guard that would
/// let independent dialog flows permanently suppress each other.
/// </summary>
public sealed partial class SettingsWindow
{
    private async Task<ContentDialogResult> ShowDialogWhenFreeAsync(
        Func<ContentDialog?> dialogFactory,
        int maxAttempts = 12)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (SettingsRoot.XamlRoot is null)
            {
                // Window closed while waiting — a dialog built now would have
                // no root and ShowAsync would throw past the retry loop.
                return ContentDialogResult.None;
            }

            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            ContentDialog? dialog = dialogFactory();
            if (dialog is null)
            {
                return ContentDialogResult.None;
            }

            try
            {
                return await dialog.ShowAsync();
            }
            catch (InvalidOperationException)
            {
                // Another dialog is showing on this XamlRoot (also the
                // documented failure for a dialog without a root if the
                // window closed mid-build); wait and retry.
            }
        }

        return ContentDialogResult.None;
    }
}
