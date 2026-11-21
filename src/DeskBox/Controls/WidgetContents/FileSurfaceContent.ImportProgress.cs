using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    private static readonly TimeSpan ImportCardShowDelay =
        TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ImportCompleteHoldDuration =
        TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan ImportFailedHoldDuration =
        TimeSpan.FromSeconds(1.8);
    private CancellationTokenSource? _activeImportCancellation;
    private CancellationTokenSource? _importCardDelayCancellation;
    private ImportCompletionState? _activeImportVisualState;
    private bool _importCardWasShown;
    private bool _isImportCancellationPending;
    private bool _activeImportIndeterminate;

    private CancellationToken ActiveImportCancellationToken =>
        _activeImportCancellation?.Token ?? CancellationToken.None;

    private void BeginTrackedImport()
    {
        CancelAndResetTrackedImport();
        EnsureImportProgressCardLoaded();
        _isImportBusy = true;
        _importBusyStartedAtUtc = DateTimeOffset.UtcNow;
        _activeImportCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _importCardDelayCancellation = new CancellationTokenSource();
        _activeImportVisualState = null;
        _importCardWasShown = false;
        _isImportCancellationPending = false;
        _activeImportIndeterminate = true;

        ImportProgressCard.Visibility = Visibility.Collapsed;
        ImportCancelButton.Visibility = Visibility.Visible;
        ImportCancelButton.IsEnabled = true;
        ImportCancelButton.IsTabStop = true;
        ImportCancelProgressRing.IsActive = false;
        ImportCancelProgressRing.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(ImportCancelButton, T("Common.Cancel"));
        AutomationProperties.SetName(ImportCancelButton, T("Common.Cancel"));
        ImportTitleText.Text = T("Widget.Import.Preparing");
        ImportDescriptionText.Text = T("Widget.Import.Description");
        ImportPercentText.Text = string.Empty;
        ImportProgressBar.IsIndeterminate = false;
        ImportProgressBar.Value = 0;
        ImportStateIcon.Foreground = ImportProgressBar.Foreground;
        VisualStateManager.GoToState(this, "ImportPreparingState", false);
        if (SelectionCommandBar is not null)
        {
            SelectionCommandBar.IsEnabled = false;
        }
        ApplyDropVisual(FileDropVisualState.None);
        ImportBusyChanged?.Invoke(true);

        _ = ShowImportCardAfterDelayAsync(_importCardDelayCancellation.Token);
    }

    private void EnsureTrackedImportStarted()
    {
        if (_activeImportCancellation is null)
        {
            BeginTrackedImport();
        }
    }

    private async Task ShowImportCardAfterDelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ImportCardShowDelay, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !_isImportBusy)
            {
                return;
            }

            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(ShowImportCard);
                return;
            }

            ShowImportCard();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowImportCard()
    {
        if (!_isImportBusy || _isDisposed)
        {
            return;
        }

        EnsureImportProgressCardLoaded();
        _importCardWasShown = true;
        ImportProgressCard.Visibility = Visibility.Visible;
        ImportProgressBar.IsIndeterminate = _activeImportIndeterminate;
    }

    private void ReportImportProgress(FileService.FileTransferProgress progress)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ReportImportProgress(progress));
            return;
        }

        if (!_isImportBusy || _isDisposed)
        {
            return;
        }

        EnsureImportProgressCardLoaded();

        // Progress callbacks are queued from the transfer worker. Once the
        // user has requested cancellation, an older transferring callback
        // must not overwrite the visible "canceling" acknowledgement.
        if (_isImportCancellationPending &&
            progress.Phase is not FileService.FileTransferPhase.Canceling and
            not FileService.FileTransferPhase.Canceled)
        {
            return;
        }

        switch (progress.Phase)
        {
            case FileService.FileTransferPhase.Preparing:
                ImportTitleText.Text = T("Widget.Import.Preparing");
                VisualStateManager.GoToState(
                    this,
                    "ImportPreparingState",
                    false);
                break;
            case FileService.FileTransferPhase.DelegatedToShell:
                // Windows now owns the progress, conflict, error and cancel
                // experience. Keep the surface busy until it returns, but do
                // not cover the widget with a second competing progress card.
                _importCardDelayCancellation?.Cancel();
                ImportProgressCard.Visibility = Visibility.Collapsed;
                ImportProgressBar.IsIndeterminate = false;
                _activeImportIndeterminate = false;
                _importCardWasShown = false;
                return;
            case FileService.FileTransferPhase.Finalizing:
                ImportTitleText.Text = T("Widget.Import.Finalizing");
                VisualStateManager.GoToState(
                    this,
                    "ImportLoadingState",
                    false);
                break;
            case FileService.FileTransferPhase.Transferring:
                ImportTitleText.Text = string.IsNullOrWhiteSpace(
                    progress.CurrentItemName)
                    ? T("Widget.Import.Title")
                    : progress.CurrentItemName;
                VisualStateManager.GoToState(
                    this,
                    "ImportLoadingState",
                    false);
                break;
            case FileService.FileTransferPhase.Canceling:
                ShowImportCancelingState();
                return;
            case FileService.FileTransferPhase.Canceled:
                // The import owner presents the short terminal state after
                // rollback has completed. Keep the animated acknowledgement
                // visible until then.
                return;
        }

        double? percentage = progress.Percentage;
        _activeImportIndeterminate = percentage is null;
        ImportProgressBar.IsIndeterminate =
            ImportProgressCard.Visibility == Visibility.Visible &&
            _activeImportIndeterminate;
        if (percentage is { } value)
        {
            ImportProgressBar.Value = value;
            ImportPercentText.Text = value is > 0 and < 1
                ? "<1%"
                : $"{value:0}%";
        }
        else
        {
            ImportPercentText.Text = string.Empty;
        }

        ImportDescriptionText.Text = FormatImportProgressDetails(progress);
    }

    private string FormatImportProgressDetails(
        FileService.FileTransferProgress progress)
    {
        var parts = new List<string>();
        if (progress.TotalItems > 0)
        {
            parts.Add(_localizationService.Format(
                "Widget.Import.Progress.Items",
                progress.CompletedItems,
                progress.TotalItems));
        }

        if (progress.TotalBytes is { } totalBytes)
        {
            parts.Add(
                $"{FileMetaService.FormatSize(progress.BytesTransferred)} / " +
                FileMetaService.FormatSize(totalBytes));
        }
        else if (progress.BytesTransferred > 0)
        {
            parts.Add(FileMetaService.FormatSize(progress.BytesTransferred));
        }

        if (progress.BytesPerSecond is > 0)
        {
            parts.Add(
                FileMetaService.FormatSize(
                    (long)Math.Min(long.MaxValue, progress.BytesPerSecond.Value)) +
                "/s");
        }

        if (progress.EstimatedRemaining is { } remaining &&
            remaining < TimeSpan.FromDays(1))
        {
            string time = remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss")
                : remaining.ToString(@"m\:ss");
            parts.Add(_localizationService.Format(
                "Widget.Import.Progress.Remaining",
                time));
        }

        return parts.Count > 0
            ? string.Join(" · ", parts)
            : T("Widget.Import.Description");
    }

    private async Task CompleteTrackedImportAsync(
        ImportCompletionState completionState)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await CompleteTrackedImportAsync(completionState);
                completion.TrySetResult(true);
            });
            await completion.Task;
            return;
        }

        EnsureImportProgressCardLoaded();
        _importCardDelayCancellation?.Cancel();
        _activeImportVisualState = completionState;
        _activeImportIndeterminate = false;
        ImportProgressBar.IsIndeterminate = false;
        ImportProgressBar.Value = completionState == ImportCompletionState.Completed
            ? 100
            : ImportProgressBar.Value;
        ImportPercentText.Text = completionState == ImportCompletionState.Completed
            ? "100%"
            : string.Empty;

        switch (completionState)
        {
            case ImportCompletionState.Completed:
                ImportTitleText.Text = T("Widget.Import.Completed");
                ImportDescriptionText.Text = string.Empty;
                VisualStateManager.GoToState(
                    this,
                    "ImportSuccessState",
                    false);
                ImportStateIcon.Foreground = ImportProgressBar.Foreground;
                break;
            case ImportCompletionState.Canceled:
                ImportTitleText.Text = T("Widget.Import.Canceled");
                ImportDescriptionText.Text = string.Empty;
                VisualStateManager.GoToState(
                    this,
                    "ImportCanceledState",
                    false);
                if (ResolveBrush("TextFillColorSecondaryBrush") is Brush secondary)
                {
                    ImportStateIcon.Foreground = secondary;
                }
                break;
            case ImportCompletionState.Failed:
                ImportTitleText.Text = T("Widget.Import.Failed");
                ImportDescriptionText.Text = T("Widget.Import.Failed.Description");
                VisualStateManager.GoToState(
                    this,
                    "ImportErrorState",
                    false);
                if (ResolveBrush("SystemFillColorCriticalBrush") is Brush brush)
                {
                    ImportStateIcon.Foreground = brush;
                }
                break;
        }

        if (_importCardWasShown)
        {
            TimeSpan holdDuration = completionState == ImportCompletionState.Failed
                ? ImportFailedHoldDuration
                : ImportCompleteHoldDuration;
            try
            {
                await Task.Delay(holdDuration, _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_isDisposed)
        {
            return;
        }

        CancelAndResetTrackedImport();
    }

    private async void ImportCancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_activeImportCancellation is null ||
            _activeImportCancellation.IsCancellationRequested ||
            _isImportCancellationPending)
        {
            return;
        }

        CancellationTokenSource cancellation = _activeImportCancellation;
        ShowImportCancelingState();

        // CancellationTokenSource.Cancel invokes registrations synchronously.
        // Run those callbacks away from the UI thread so the acknowledgement
        // and progress animation can render immediately, even when aborting an
        // active disk request takes a while.
        try
        {
            await Task.Run(cancellation.Cancel);
        }
        catch (ObjectDisposedException)
        {
            // The transfer may have completed between the click and the worker
            // receiving the cancellation request.
        }
        catch (AggregateException ex)
        {
            App.Log($"[FileTransfer] Cancellation callback failed: {ex}");
        }
    }

    private void ShowImportCancelingState()
    {
        if (!_isImportBusy || _isDisposed)
        {
            return;
        }

        _isImportCancellationPending = true;
        _activeImportIndeterminate = true;
        ShowImportCard();
        ImportTitleText.Text = T("Widget.Import.Canceling");
        ImportDescriptionText.Text = T("Widget.Import.Canceling.Description");
        ImportPercentText.Text = string.Empty;
        ImportProgressBar.IsIndeterminate = true;
        VisualStateManager.GoToState(this, "ImportCancelingState", false);
    }

    private void ImportProgressCard_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        ImportDescriptionText.Visibility = e.NewSize.Width >= 250
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CancelAndResetTrackedImport()
    {
        _importCardDelayCancellation?.Cancel();
        _importCardDelayCancellation?.Dispose();
        _importCardDelayCancellation = null;

        if (_activeImportCancellation is not null)
        {
            if (!_activeImportCancellation.IsCancellationRequested)
            {
                _activeImportCancellation.Cancel();
            }

            _activeImportCancellation.Dispose();
            _activeImportCancellation = null;
        }

        _activeImportIndeterminate = false;
        if (ImportProgressCard is not null)
        {
            ImportProgressCard.Visibility = Visibility.Collapsed;
            ImportProgressBar.IsIndeterminate = false;
            ImportCancelButton.Visibility = Visibility.Visible;
            ImportCancelButton.IsEnabled = true;
            ImportCancelProgressRing.IsActive = false;
            ImportCancelProgressRing.Visibility = Visibility.Collapsed;
        }
        if (SelectionCommandBar is not null)
        {
            SelectionCommandBar.IsEnabled = true;
        }
        _importCardWasShown = false;
        _activeImportVisualState = null;
        _isImportCancellationPending = false;

        bool wasBusy = _isImportBusy;
        _isImportBusy = false;
        _importBusyStartedAtUtc = null;
        if (wasBusy)
        {
            ImportBusyChanged?.Invoke(false);
        }
    }

    private enum ImportCompletionState
    {
        Completed,
        Canceled,
        Failed
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
