using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace DeskBox.Services;

/// <summary>
/// Interactive managed-storage migration dialog. A single ContentDialog swaps
/// between three inline views — live progress, a per-item retry/skip/abort
/// prompt, and a final result list — because WinUI only allows one open
/// ContentDialog at a time.
/// </summary>
internal sealed class ManagedStorageMigrationDialog
{
    private readonly DispatcherQueue _dispatcher;
    private readonly LocalizationService _localizationService;
    private readonly string _oldRootPath;
    private readonly string _newRootPath;
    private readonly Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> _migrateAsync;
    private readonly Func<IReadOnlyList<ManagedStorageSkippedItem>, ManagedStorageMigrationOptions, Task<IReadOnlyList<ManagedStorageSkippedItem>>>? _retrySkippedAsync;

    private readonly ContentDialog _dialog;
    private readonly StackPanel _progressView;
    private readonly StackPanel _errorView;
    private readonly StackPanel _resultView;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _progressWidgetText;
    private readonly TextBlock _progressItemText;
    private readonly TextBlock _progressDetailText;
    private readonly TextBlock _errorNameText;
    private readonly TextBlock _errorPathsText;
    private readonly TextBlock _errorReasonText;
    private readonly TextBlock _errorDetailText;
    private readonly CheckBox _applyAllCheck;
    private readonly TextBlock _resultSummaryText;
    private readonly TextBlock _resultNoteText;
    private readonly StackPanel _skippedListPanel;
    private readonly TextBlock _skippedHeaderText;
    private readonly Button _retrySkippedButton;

    private CancellationTokenSource _cancellation = new();
    private TaskCompletionSource<FileService.FileTransferItemAction>? _pendingItemDecision;
    private FileService.FileTransferItemAction? _stickyItemAction;
    private bool _cancelRequested;
    private IReadOnlyList<ManagedStorageSkippedItem> _remainingSkipped = [];
    private ManagedStorageRollbackFailureException? _pendingRollbackFailure;
    private bool _resultShown;

    private ManagedStorageMigrationDialog(
        XamlRoot xamlRoot,
        DispatcherQueue dispatcher,
        LocalizationService localizationService,
        string oldRootPath,
        string newRootPath,
        Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> migrateAsync,
        Func<IReadOnlyList<ManagedStorageSkippedItem>, ManagedStorageMigrationOptions, Task<IReadOnlyList<ManagedStorageSkippedItem>>>? retrySkippedAsync)
    {
        _dispatcher = dispatcher;
        _localizationService = localizationService;
        _oldRootPath = oldRootPath;
        _newRootPath = newRootPath;
        _migrateAsync = migrateAsync;
        _retrySkippedAsync = retrySkippedAsync;

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true
        };
        _progressWidgetText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _progressItemText = new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.85
        };
        _progressDetailText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7
        };
        _progressView = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = localizationService.Format(
                        "Settings.Dialog.MigrateProgressBody",
                        oldRootPath,
                        newRootPath),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.8
                },
                _progressBar,
                _progressWidgetText,
                _progressItemText,
                _progressDetailText
            }
        };

        _errorNameText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        _errorPathsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8
        };
        _errorReasonText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _errorDetailText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.65
        };
        _applyAllCheck = new CheckBox
        {
            Content = localizationService.T("Settings.Dialog.MigrateItemApplyAll")
        };
        var retryButton = new Button
        {
            Content = localizationService.T("Settings.Dialog.MigrateItemRetry")
        };
        retryButton.Click += (_, _) => CompleteItemDecision(FileService.FileTransferItemAction.Retry);
        var skipButton = new Button
        {
            Content = localizationService.T("Settings.Dialog.MigrateItemSkip")
        };
        skipButton.Click += (_, _) => CompleteItemDecision(FileService.FileTransferItemAction.Skip);
        var abortButton = new Button
        {
            Content = localizationService.T("Settings.Dialog.MigrateItemAbort")
        };
        abortButton.Click += (_, _) => CompleteItemDecision(FileService.FileTransferItemAction.Abort);
        _errorView = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Spacing = 8,
            Children =
            {
                _errorNameText,
                _errorPathsText,
                _errorReasonText,
                _errorDetailText,
                _applyAllCheck,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { retryButton, skipButton, abortButton }
                }
            }
        };

        _resultSummaryText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _resultNoteText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75
        };
        _skippedHeaderText = new TextBlock
        {
            Text = localizationService.T("Settings.Dialog.MigrateSkippedHeader"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        _skippedListPanel = new StackPanel { Spacing = 8 };
        _retrySkippedButton = new Button
        {
            Content = localizationService.T("Settings.Dialog.MigrateRetrySkippedButton"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _retrySkippedButton.Click += RetrySkippedClicked;
        var openTargetButton = new Button
        {
            Content = localizationService.T("Settings.Dialog.MigrateOpenTargetButton"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openTargetButton.Click += async (_, _) =>
        {
            try
            {
                await Launcher.LaunchFolderPathAsync(newRootPath);
            }
            catch (Exception ex)
            {
                App.Log($"[ManagedStorageMigration] Failed to open '{newRootPath}': {ex.Message}");
            }
        };

        _resultView = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Spacing = 10,
            Children =
            {
                _resultSummaryText,
                _resultNoteText,
                _skippedHeaderText,
                new ScrollViewer
                {
                    MaxHeight = 240,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = _skippedListPanel
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _retrySkippedButton, openTargetButton }
                }
            }
        };

        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = localizationService.T("Settings.Dialog.MigrateProgressTitle"),
            CloseButtonText = localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 0,
                Children = { _progressView, _errorView, _resultView }
            }
        };
        _dialog.CloseButtonClick += OnCloseButtonClick;
    }

    /// <summary>
    /// Shows the dialog, runs <paramref name="migrateAsync"/>, and returns its
    /// result. Cancel, failure and skipped-item detail are all presented inside
    /// the dialog's result view. Residue/rollback exceptions rethrow after the
    /// dialog closes so the caller's dedicated dialogs still run.
    /// </summary>
    public static async Task<ManagedStorageMigrationResult?> RunAsync(
        XamlRoot xamlRoot,
        DispatcherQueue dispatcher,
        LocalizationService localizationService,
        string oldRootPath,
        string newRootPath,
        Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> migrateAsync,
        Func<IReadOnlyList<ManagedStorageSkippedItem>, ManagedStorageMigrationOptions, Task<IReadOnlyList<ManagedStorageSkippedItem>>>? retrySkippedAsync)
    {
        return await new ManagedStorageMigrationDialog(
            xamlRoot,
            dispatcher,
            localizationService,
            oldRootPath,
            newRootPath,
            migrateAsync,
            retrySkippedAsync).RunInternalAsync();
    }

    private async Task<ManagedStorageMigrationResult?> RunInternalAsync()
    {
        var options = BuildOptions();
        Task<ManagedStorageMigrationResult> migrateTask = _migrateAsync(options);
        Task<ContentDialogResult> dialogTask = _dialog.ShowAsync().AsTask();

        ManagedStorageMigrationResult? result = null;
        Exception? failure = null;
        try
        {
            result = await migrateTask;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is ManagedStorageDestinationResidueException or
            ManagedStorageRollbackFailureException)
        {
            // These have dedicated follow-up dialogs; free the single-dialog
            // slot first, then propagate to the caller unchanged.
            HideDialogSafe();
            await dialogTask;
            throw failure;
        }

        _remainingSkipped = result?.SkippedItems ?? [];
        ShowResultView(result, failure);
        await dialogTask;
        if (_pendingRollbackFailure is { } pendingRollbackFailure)
        {
            // A skipped-item retry stranded files across both roots; hand
            // the receipts to the caller's rollback-failure dialog.
            throw pendingRollbackFailure;
        }

        return result;
    }

    private ManagedStorageMigrationOptions BuildOptions()
    {
        return new ManagedStorageMigrationOptions(
            Progress: new Progress<ManagedStorageMigrationProgress>(ReportProgress),
            CancellationToken: _cancellation.Token,
            OnItemError: DecideItemActionAsync);
    }

    private void ReportProgress(ManagedStorageMigrationProgress progress)
    {
        if (_resultShown || !DispatcherQueueAccess())
        {
            if (!_resultShown)
            {
                _dispatcher.TryEnqueue(() => ReportProgress(progress));
            }
            return;
        }

        if (_cancelRequested &&
            progress.Phase is not FileService.FileTransferPhase.Canceling and
                not FileService.FileTransferPhase.Canceled)
        {
            // Once cancellation is acknowledged, later transferring reports
            // must not overwrite the visible "canceling" state.
            return;
        }

        if (progress.Phase is FileService.FileTransferPhase.Canceling or
            FileService.FileTransferPhase.Canceled)
        {
            _progressWidgetText.Text =
                _localizationService.T("Settings.Dialog.MigrateCanceling");
            _progressBar.IsIndeterminate = true;
            return;
        }

        if (progress.TotalWidgets > 0)
        {
            _progressWidgetText.Text = _localizationService.Format(
                "Settings.Dialog.MigrateProgressWidget",
                Math.Min(progress.CompletedWidgets + 1, progress.TotalWidgets),
                progress.TotalWidgets,
                progress.CurrentWidgetName ?? string.Empty);
        }
        else
        {
            _progressWidgetText.Text = string.Empty;
        }

        _progressItemText.Text = progress.CurrentItemName ?? string.Empty;

        double? percentage = progress.TotalItems > 0
            ? progress.CompletedItems * 100.0 / progress.TotalItems
            : null;
        _progressBar.IsIndeterminate = percentage is null;
        if (percentage is { } value)
        {
            _progressBar.Value = value;
        }

        var parts = new List<string>();
        if (progress.TotalItems > 0)
        {
            parts.Add(_localizationService.Format(
                "Widget.Import.Progress.Items",
                progress.CompletedItems,
                progress.TotalItems));
        }

        if (progress.BytesTransferred > 0)
        {
            parts.Add(FileMetaService.FormatSize(progress.BytesTransferred));
        }

        if (progress.BytesPerSecond is > 0)
        {
            parts.Add(FileMetaService.FormatSize(
                (long)Math.Min(long.MaxValue, progress.BytesPerSecond.Value)) + "/s");
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

        _progressDetailText.Text = string.Join(" · ", parts);
    }

    private bool DispatcherQueueAccess() => _dispatcher.HasThreadAccess;

    private async Task<FileService.FileTransferItemAction> DecideItemActionAsync(
        FileService.FileTransferItemError error)
    {
        if (_stickyItemAction is { } sticky)
        {
            App.Log(
                $"[ManagedStorageMigration] Item error auto-resolved as " +
                $"{sticky} source='{error.SourcePath}': {error.Exception.Message}");
            return sticky;
        }

        var decision = new TaskCompletionSource<FileService.FileTransferItemAction>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingItemDecision = decision;
        _dispatcher.TryEnqueue(() => ShowItemError(error));

        FileService.FileTransferItemAction action = await decision.Task;
        return action;
    }

    private void ShowItemError(FileService.FileTransferItemError error)
    {
        _errorNameText.Text = Path.GetFileName(error.SourcePath);
        _errorPathsText.Text = _localizationService.Format(
            "Settings.Dialog.MigrateItemPaths",
            error.SourcePath,
            error.DestinationPath);
        _errorReasonText.Text = ReasonTextFor(
            FileService.ClassifyTransferError(error.Exception));
        _errorDetailText.Text = error.Exception.Message;
        _applyAllCheck.IsChecked = false;
        _progressView.Visibility = Visibility.Collapsed;
        _errorView.Visibility = Visibility.Visible;
    }

    private void CompleteItemDecision(FileService.FileTransferItemAction action)
    {
        if (_applyAllCheck.IsChecked == true &&
            action != FileService.FileTransferItemAction.Abort)
        {
            _stickyItemAction = action;
        }

        _errorView.Visibility = Visibility.Collapsed;
        _progressView.Visibility = Visibility.Visible;
        _pendingItemDecision?.TrySetResult(action);
        _pendingItemDecision = null;
    }

    private void OnCloseButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_resultShown)
        {
            return;
        }

        // During the run the close button is the cancel action: keep the
        // dialog open and unwind through the cancellation path instead.
        args.Cancel = true;
        RequestCancel();
    }

    private void RequestCancel()
    {
        if (_cancelRequested)
        {
            return;
        }

        _cancelRequested = true;
        _cancellation.Cancel();
        // An item prompt that is still open would otherwise keep the worker
        // waiting forever; a cancel means the in-flight item aborts too.
        _pendingItemDecision?.TrySetResult(FileService.FileTransferItemAction.Abort);
        _errorView.Visibility = Visibility.Collapsed;
        _progressView.Visibility = Visibility.Visible;
        _progressWidgetText.Text =
            _localizationService.T("Settings.Dialog.MigrateCanceling");
        _progressItemText.Text = string.Empty;
        _progressBar.IsIndeterminate = true;
    }

    private void ShowResultView(ManagedStorageMigrationResult? result, Exception? failure)
    {
        _resultShown = true;
        _progressView.Visibility = Visibility.Collapsed;
        _errorView.Visibility = Visibility.Collapsed;
        _resultView.Visibility = Visibility.Visible;
        _dialog.CloseButtonText = _localizationService.T("Common.Close");

        // A committed result wins over a late cancel click: the migration may
        // finish between the user's request and the next token check, and the
        // success view is the honest outcome in that race.
        bool canceled = result is null &&
            (_cancelRequested || failure is OperationCanceledException);
        if (canceled)
        {
            _dialog.Title = _localizationService.T("Settings.Dialog.MigrateResultCanceledTitle");
            _resultSummaryText.Text =
                _localizationService.T("Settings.Dialog.MigrateResultCanceledBody");
            _resultNoteText.Text = string.Empty;
        }
        else if (failure is not null)
        {
            _dialog.Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle");
            _resultSummaryText.Text = _localizationService.Format(
                "Settings.Dialog.MigrateResultFailedBody",
                ReasonTextFor(FileService.ClassifyTransferError(failure)));
            _resultNoteText.Text = failure.InnerException is { } inner
                ? $"{failure.Message} {inner.Message}"
                : failure.Message;
        }
        else
        {
            _dialog.Title = _localizationService.T("Settings.Dialog.MigrateCompleteTitle");
            _resultSummaryText.Text = _localizationService.Format(
                "Settings.Dialog.MigrateResultSummary",
                result?.AffectedWidgetCount ?? 0,
                result?.MovedItemCount ?? 0,
                _remainingSkipped.Count);
            _resultNoteText.Text = result is { Residues.Count: > 0 } residueResult
                ? _localizationService.Format(
                    "Settings.Dialog.MigrateResultResidueNote",
                    residueResult.Residues.Count)
                : _localizationService.T("Settings.Dialog.MigrateSkippedKeptNote");
        }

        RebuildSkippedList();
    }

    private void RebuildSkippedList()
    {
        _skippedListPanel.Children.Clear();
        _skippedHeaderText.Visibility = _remainingSkipped.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _retrySkippedButton.Visibility =
            _remainingSkipped.Count > 0 && _retrySkippedAsync is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        _retrySkippedButton.IsEnabled = true;

        foreach (var item in _remainingSkipped)
        {
            _skippedListPanel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = _localizationService.Format(
                            "Settings.Dialog.MigrateSkippedItem",
                            Path.GetFileName(item.SourcePath),
                            item.WidgetName,
                            ReasonTextFor(item.ErrorKind)),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = item.SourcePath,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                        Opacity = 0.65
                    },
                    string.IsNullOrWhiteSpace(item.Detail)
                        ? new TextBlock { Visibility = Visibility.Collapsed }
                        : new TextBlock
                        {
                            Text = item.Detail,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11,
                            Opacity = 0.5
                        }
                }
            });
        }
    }

    private async void RetrySkippedClicked(object sender, RoutedEventArgs e)
    {
        if (_retrySkippedAsync is null || _remainingSkipped.Count == 0)
        {
            return;
        }

        _retrySkippedButton.IsEnabled = false;
        _cancellation = new CancellationTokenSource();
        _cancelRequested = false;
        _stickyItemAction = null;
        _resultShown = false;
        _resultView.Visibility = Visibility.Collapsed;
        _progressView.Visibility = Visibility.Visible;
        _progressBar.IsIndeterminate = true;
        _dialog.Title = _localizationService.T("Settings.Dialog.MigrateProgressTitle");
        _dialog.CloseButtonText = _localizationService.T("Common.Cancel");

        IReadOnlyList<ManagedStorageSkippedItem> remaining;
        Exception? retryFailure = null;
        try
        {
            remaining = await _retrySkippedAsync(_remainingSkipped, BuildOptions());
        }
        catch (ManagedStorageRollbackFailureException ex)
        {
            // Files are stranded across both roots. Close this dialog so the
            // caller can show the dedicated rollback-retry dialog — same
            // hand-off as a failed migration (RunInternalAsync rethrows).
            App.Log($"[ManagedStorageMigration] Skipped-item retry left stranded files: {ex}");
            _pendingRollbackFailure = ex;
            HideDialogSafe();
            return;
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Skipped-item retry failed: {ex}");
            retryFailure = ex;
            remaining = _remainingSkipped;
        }

        _remainingSkipped = remaining;
        if (retryFailure is not null)
        {
            _resultShown = true;
            _progressView.Visibility = Visibility.Collapsed;
            _resultView.Visibility = Visibility.Visible;
            _dialog.Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle");
            _dialog.CloseButtonText = _localizationService.T("Common.Close");
            _resultSummaryText.Text = _localizationService.Format(
                "Settings.Dialog.MigrateResultFailedBody",
                ReasonTextFor(FileService.ClassifyTransferError(retryFailure)));
            _resultNoteText.Text = retryFailure.Message;
            RebuildSkippedList();
            return;
        }

        _resultShown = true;
        _progressView.Visibility = Visibility.Collapsed;
        _resultView.Visibility = Visibility.Visible;
        _dialog.Title = _remainingSkipped.Count == 0
            ? _localizationService.T("Settings.Dialog.MigrateCompleteTitle")
            : _localizationService.T("Settings.Dialog.MigrateResultCanceledTitle");
        _dialog.CloseButtonText = _localizationService.T("Common.Close");
        _resultSummaryText.Text = _localizationService.Format(
            "Settings.Dialog.MigrateRetryResult",
            _remainingSkipped.Count);
        _resultNoteText.Text = string.Empty;
        RebuildSkippedList();
    }

    private void HideDialogSafe()
    {
        try
        {
            _dialog.Hide();
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Failed to close dialog: {ex.Message}");
        }
    }

    private string ReasonTextFor(FileService.FileTransferItemErrorKind kind)
    {
        return _localizationService.T(kind switch
        {
            FileService.FileTransferItemErrorKind.InUse =>
                "Settings.Dialog.MigrateReasonInUse",
            FileService.FileTransferItemErrorKind.AccessDenied =>
                "Settings.Dialog.MigrateReasonAccessDenied",
            FileService.FileTransferItemErrorKind.NotFound =>
                "Settings.Dialog.MigrateReasonNotFound",
            FileService.FileTransferItemErrorKind.DiskFull =>
                "Settings.Dialog.MigrateReasonDiskFull",
            FileService.FileTransferItemErrorKind.PathTooLong =>
                "Settings.Dialog.MigrateReasonPathTooLong",
            _ => "Settings.Dialog.MigrateReasonUnknown"
        });
    }
}
