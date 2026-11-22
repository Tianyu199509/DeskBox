using DeskBox.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace DeskBox.Services;

internal enum StorageMigrationStartChoice { Start, Restart, ChooseLocation }

internal sealed record StorageMigrationConfirmOutcome(
    StorageMigrationStartChoice Choice,
    bool RetainOriginals = false,
    bool IncludeOrphanFolders = false);

internal sealed record StorageMigrationDialogResult(
    ManagedStorageMigrationResult? Migration,
    bool ChooseLocation);

/// <summary>
/// Compact Fluent card dialog for managed-storage migration: distinguishes
/// same-volume atomic moves from cross-volume copy/verify/cleanup flows,
/// surfaces blocking paths inline, and adapts action buttons to live disk state.
/// </summary>
internal sealed class ManagedStorageMigrationDialog
{
    private readonly LocalizationService _localization;
    private readonly DispatcherQueue _dispatcher;
    private readonly string _oldPath;
    private readonly string _newPath;
    private readonly Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> _migrate;
    private readonly Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? _retrySourceCleanup;
    private readonly Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? _cleanupInterruptedCopies;
    private readonly Action<bool>? _onRunningStateChanged;

    private readonly ContentDialog _dialog;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly TextBlock _phase = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _percent = new() { MinWidth = 44, FontSize = 14, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _file;
    private readonly TextBlock _directory;
    private readonly TextBlock _detail;
    private readonly TextBlock _runningHint;
    private readonly DependencyObject? _themeScope;
    private readonly Border _currentItemCard;
    private readonly TextBlock _technical = Text(size: 12);
    private readonly Expander _details;
    private readonly StackPanel _progressView;
    private readonly StackPanel _resultView;
    private readonly StackPanel _linksRow;
    private readonly Button _primary;
    private readonly Button _actionSecondary;
    private readonly Button _secondary;
    private readonly Button _openSource;
    private readonly Button _openTarget;
    private readonly DialogSize _sizing;

    private CancellationTokenSource _cancel = new();
    private bool _running;
    private bool _committing;
    private bool _closed;
    private bool _restart;
    private bool _retainOriginals;
    private bool _includeOrphanFolders;
    private bool _pickAnother;
    private bool _primaryChoosesLocation;
    private bool _primaryRetriesSourceCleanup;
    private bool _secondaryCleansInterruptedCopies;
    private int _attempt;
    private ManagedStorageMigrationResult? _result;

    internal static async Task<StorageMigrationConfirmOutcome?> ConfirmAsync(
        XamlRoot root,
        LocalizationService localization,
        int count,
        string oldPath,
        string newPath,
        Func<CancellationToken, Task<ManagedStorageMigrationPreview>> preview,
        Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? cleanupInterruptedCopies = null)
    {
        using var cancel = new CancellationTokenSource();
        bool closed = false;
        bool loading = false;
        StorageMigrationConfirmOutcome? outcome = null;
        ManagedStorageMigrationPreview? summary = null;
        StorageMigrationProblem? problem = null;
        string? blockingPath = null;
        bool diskSpaceInsufficient = false;

        DependencyObject? themeScope = root.Content;
        var modeIcon = new FontIcon { FontSize = 14, Glyph = "\uE8AB" };
        var modeText = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var modeBadge = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush", themeScope),
            Visibility = Visibility.Collapsed,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { modeIcon, modeText }
            }
        };

        var loadingRing = new ProgressRing { Width = 14, Height = 14, IsActive = true };
        var statsText = Text(localization.Format("Settings.Dialog.SafeMigrationCounting", count), 13);
        var statsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { loadingRing, statsText }
        };
        var spaceText = SecondaryText(size: 12.5, scope: themeScope);
        spaceText.Visibility = Visibility.Collapsed;

        var pathCard = CardSurface(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Location(localization.T("Settings.Dialog.SafeMigrationFrom"), oldPath, themeScope),
                Location(localization.T("Settings.Dialog.SafeMigrationTo"), newPath, themeScope),
                Divider(themeScope),
                statsRow,
                spaceText
            }
        }, themeScope);

        var retainOriginalsCheck = new CheckBox
        {
            Content = localization.T("Settings.Dialog.SafeMigrationRetainOriginalsOption"),
            IsChecked = false,
            Visibility = Visibility.Collapsed
        };
        var includeOrphansCheck = new CheckBox
        {
            IsChecked = false,
            Visibility = Visibility.Collapsed
        };
        var optionsPanel = new StackPanel
        {
            Spacing = 4,
            Visibility = Visibility.Collapsed,
            Children = { retainOriginalsCheck, includeOrphansCheck }
        };

        var noticeBlockingPath = SingleLine(18, 12, FontWeights.Normal, secondary: true, scope: themeScope);
        noticeBlockingPath.Visibility = Visibility.Collapsed;
        var noticeBar = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Visibility = Visibility.Collapsed,
            Content = noticeBlockingPath
        };

        var sameVolumeHint = SecondaryText(localization.T("Settings.Dialog.SafeMigrationSameVolumeNote"), 12.5, themeScope);
        sameVolumeHint.Visibility = Visibility.Collapsed;

        var technical = SecondaryText(localization.T("Settings.Dialog.SafeMigrationMoreBody"), 12.5, themeScope);
        var detailExpander = new Expander
        {
            Header = localization.T("Settings.Dialog.SafeMigrationMore"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Content = new ScrollViewer { MaxHeight = 130, Content = technical }
        };

        var cleanupBtn = ActionButton(localization.T("Settings.Dialog.SafeMigrationCleanupCopies"));
        cleanupBtn.Visibility = Visibility.Collapsed;
        var start = ActionButton(localization.T("Settings.Dialog.SafeMigrationStartShort"), primary: true);
        var back = ActionButton(localization.T("Settings.Dialog.SafeMigrationNotNow"));
        start.IsEnabled = false;

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                modeBadge,
                pathCard,
                optionsPanel,
                noticeBar,
                sameVolumeHint,
                detailExpander,
                Footer(cleanupBtn, start, back)
            }
        };

        var dialog = Dialog(root, localization);
        dialog.Content = content;
        using var sizing = new DialogSize(root, dialog, content);

        void UpdateSummaryMetrics()
        {
            if (summary is null) return;
            bool includeOrphans = includeOrphansCheck.IsChecked == true;
            bool retainOriginals = retainOriginalsCheck.IsChecked == true;
            int totalFiles = summary.FileCount + (includeOrphans ? summary.OrphanFileCount : 0);
            long totalBytes = summary.TotalBytes + (includeOrphans ? summary.OrphanTotalBytes : 0);
            int totalFolders = summary.WidgetCount + (includeOrphans ? summary.OrphanFolderCount : 0);

            statsText.Text = localization.Format(
                "Settings.Dialog.SafeMigrationPreview",
                totalFolders,
                totalFiles,
                FileMetaService.FormatSize(totalBytes));

            if (summary.Mode == ManagedStorageMigrationMode.SameVolumeMove)
            {
                modeIcon.Glyph = "\uE945";
                modeText.Text = localization.T("Settings.Dialog.SafeMigrationMode.SameVolume");
                spaceText.Visibility = Visibility.Collapsed;
                diskSpaceInsufficient = false;
            }
            else
            {
                modeIcon.Glyph = retainOriginals ? "\uE8C8" : "\uE8AB";
                modeText.Text = localization.T(retainOriginals
                    ? "Settings.Dialog.SafeMigrationMode.CrossVolumeRetain"
                    : "Settings.Dialog.SafeMigrationMode.CrossVolumeClean");

                long required = totalBytes + ManagedStorageMigrationService.ComputeSpaceReserve(totalBytes);
                if (summary.DestinationAvailableBytes is { } available)
                {
                    diskSpaceInsufficient = available < required;
                    spaceText.Text = localization.Format(
                        diskSpaceInsufficient
                            ? "Settings.Dialog.SafeMigrationSpaceInsufficient"
                            : "Settings.Dialog.SafeMigrationSpaceAvailable",
                        FileMetaService.FormatSize(available),
                        FileMetaService.FormatSize(required));
                    ApplyForeground(spaceText, diskSpaceInsufficient
                        ? "SystemFillColorCriticalBrush"
                        : "TextFillColorSecondaryBrush", themeScope);
                    spaceText.Visibility = Visibility.Visible;
                }
                else
                {
                    diskSpaceInsufficient = false;
                    spaceText.Visibility = Visibility.Collapsed;
                }
            }

            if (problem is null)
            {
                if (diskSpaceInsufficient)
                {
                    start.Content = localization.T("Settings.Dialog.SafeMigrationChooseLocation");
                }
                else if (summary.HasStaleResumeConflict)
                {
                    start.Content = localization.T("Settings.Dialog.SafeMigrationChooseLocation");
                }
                else if (summary.CopiesRemoved)
                {
                    start.Content = localization.T("Settings.Dialog.SafeMigrationRestart");
                }
                else if (summary.HasPendingResumeCopies)
                {
                    start.Content = localization.T("Settings.Dialog.SafeMigrationResume");
                }
                else
                {
                    start.Content = localization.T("Settings.Dialog.SafeMigrationStartShort");
                }
            }
        }

        retainOriginalsCheck.Checked += (_, _) => UpdateSummaryMetrics();
        retainOriginalsCheck.Unchecked += (_, _) => UpdateSummaryMetrics();
        includeOrphansCheck.Checked += (_, _) => UpdateSummaryMetrics();
        includeOrphansCheck.Unchecked += (_, _) => UpdateSummaryMetrics();

        back.Click += (_, _) => dialog.Hide();
        cleanupBtn.Click += async (_, _) =>
        {
            if (loading || cleanupInterruptedCopies is null) return;
            loading = true;
            start.IsEnabled = false;
            cleanupBtn.IsEnabled = false;
            try
            {
                await cleanupInterruptedCopies(cancel.Token);
                if (!closed) await LoadPreviewAsync();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!closed)
                {
                    App.Log($"[ManagedStorageMigration] Cleanup copies before start failed: {ex}");
                    noticeBar.Severity = InfoBarSeverity.Error;
                    noticeBar.Message = localization.T("Settings.Dialog.SafeMigrationCleanupFailed");
                    noticeBar.IsOpen = true;
                    noticeBar.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                loading = false;
                if (!closed)
                {
                    cleanupBtn.IsEnabled = true;
                    start.IsEnabled = true;
                }
            }
        };

        start.Click += async (_, _) =>
        {
            if (loading) return;
            if (problem is { } failure)
            {
                if (ManagedStorageMigrationPresentation.NeedsNewLocation(failure))
                {
                    outcome = new(StorageMigrationStartChoice.ChooseLocation);
                    dialog.Hide();
                }
                else
                {
                    await LoadPreviewAsync();
                }
                return;
            }

            if (diskSpaceInsufficient || summary?.HasStaleResumeConflict == true)
            {
                outcome = new(StorageMigrationStartChoice.ChooseLocation);
                dialog.Hide();
                return;
            }

            var choice = summary?.CopiesRemoved == true
                ? StorageMigrationStartChoice.Restart
                : StorageMigrationStartChoice.Start;
            outcome = new(
                choice,
                RetainOriginals: retainOriginalsCheck.Visibility == Visibility.Visible && retainOriginalsCheck.IsChecked == true,
                IncludeOrphanFolders: includeOrphansCheck.Visibility == Visibility.Visible && includeOrphansCheck.IsChecked == true);
            dialog.Hide();
        };

        dialog.Closing += (_, _) => { closed = true; cancel.Cancel(); };
        var visible = dialog.ShowAsync().AsTask();
        Task inspection = LoadPreviewAsync();
        await visible;
        closed = true;
        cancel.Cancel();
        await inspection;
        return outcome;

        async Task LoadPreviewAsync()
        {
            loading = true;
            problem = null;
            blockingPath = null;
            diskSpaceInsufficient = false;
            start.IsEnabled = false;
            cleanupBtn.Visibility = Visibility.Collapsed;
            modeBadge.Visibility = Visibility.Collapsed;
            optionsPanel.Visibility = Visibility.Collapsed;
            sameVolumeHint.Visibility = Visibility.Collapsed;
            detailExpander.Visibility = Visibility.Collapsed;
            noticeBar.IsOpen = false;
            noticeBar.Visibility = Visibility.Collapsed;
            noticeBlockingPath.Visibility = Visibility.Collapsed;
            loadingRing.IsActive = true;
            loadingRing.Visibility = Visibility.Visible;
            statsText.Text = localization.Format("Settings.Dialog.SafeMigrationCounting", count);
            spaceText.Visibility = Visibility.Collapsed;

            try
            {
                summary = await preview(cancel.Token);
                if (closed) return;

                loadingRing.IsActive = false;
                loadingRing.Visibility = Visibility.Collapsed;
                modeBadge.Visibility = Visibility.Visible;

                bool isCrossVolume = summary.Mode != ManagedStorageMigrationMode.SameVolumeMove;
                retainOriginalsCheck.Visibility = isCrossVolume ? Visibility.Visible : Visibility.Collapsed;
                if (!isCrossVolume) retainOriginalsCheck.IsChecked = false;

                if (summary.OrphanFolderCount > 0)
                {
                    includeOrphansCheck.Content = localization.Format(
                        "Settings.Dialog.SafeMigrationIncludeOrphansOption",
                        summary.OrphanFolderCount,
                        summary.OrphanFileCount,
                        FileMetaService.FormatSize(summary.OrphanTotalBytes));
                    includeOrphansCheck.Visibility = Visibility.Visible;
                }
                else
                {
                    includeOrphansCheck.IsChecked = false;
                    includeOrphansCheck.Visibility = Visibility.Collapsed;
                }

                optionsPanel.Visibility = (retainOriginalsCheck.Visibility == Visibility.Visible ||
                                           includeOrphansCheck.Visibility == Visibility.Visible)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                sameVolumeHint.Visibility = isCrossVolume ? Visibility.Collapsed : Visibility.Visible;
                detailExpander.Visibility = isCrossVolume ? Visibility.Visible : Visibility.Collapsed;

                if (summary.HasStaleResumeConflict)
                {
                    noticeBar.Severity = InfoBarSeverity.Warning;
                    noticeBar.Message = localization.T("Settings.Dialog.SafeMigrationStaleResumeNotice");
                    noticeBar.IsOpen = true;
                    noticeBar.Visibility = Visibility.Visible;
                    cleanupBtn.Visibility = cleanupInterruptedCopies is not null ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (summary.HasPendingResumeCopies)
                {
                    noticeBar.Severity = InfoBarSeverity.Informational;
                    noticeBar.Message = localization.T("Settings.Dialog.SafeMigrationPendingResumeNotice");
                    noticeBar.IsOpen = true;
                    noticeBar.Visibility = Visibility.Visible;
                    cleanupBtn.Visibility = cleanupInterruptedCopies is not null ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (summary.CopiesRemoved)
                {
                    noticeBar.Severity = InfoBarSeverity.Warning;
                    noticeBar.Message = localization.T("Settings.Dialog.SafeMigrationError.CopiesRemoved");
                    noticeBar.IsOpen = true;
                    noticeBar.Visibility = Visibility.Visible;
                }

                UpdateSummaryMetrics();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (closed) return;
                loadingRing.IsActive = false;
                loadingRing.Visibility = Visibility.Collapsed;
                problem = ManagedStorageMigrationPresentation.Classify(ex);
                blockingPath = ManagedStorageMigrationPresentation.ExtractBlockingPath(ex);
                App.Log($"[ManagedStorageMigration] Preview failed ({problem}): {ex}");

                noticeBar.Severity = InfoBarSeverity.Error;
                noticeBar.Message = localization.T("Settings.Dialog.SafeMigrationError." + problem);
                if (!string.IsNullOrWhiteSpace(blockingPath))
                {
                    noticeBlockingPath.Text = blockingPath;
                    ToolTipService.SetToolTip(noticeBlockingPath, blockingPath);
                    noticeBlockingPath.Visibility = Visibility.Visible;
                }
                noticeBar.IsOpen = true;
                noticeBar.Visibility = Visibility.Visible;

                statsText.Text = localization.T("Settings.Dialog.SafeMigrationPreviewBlocked");
                start.Content = localization.T(ManagedStorageMigrationPresentation.NeedsNewLocation(problem.Value)
                    ? "Settings.Dialog.SafeMigrationChooseLocation"
                    : "Settings.Dialog.SafeMigrationRetry");
            }
            finally
            {
                loading = false;
                if (!closed) start.IsEnabled = true;
            }
        }
    }

    internal static async Task ShowEmptySwitchSuccessAsync(
        XamlRoot root,
        LocalizationService localization,
        string newPath)
    {
        DependencyObject? themeScope = root.Content;
        var dialog = Dialog(root, localization);
        var header = StatusHeader(
            "\uE73E",
            "SystemFillColorSuccessBrush",
            localization.T("Settings.Dialog.EmptySwitchSuccessTitle"),
            themeScope);
        var body = SecondaryText(localization.T("Settings.Dialog.EmptySwitchSuccessBody"), 13.5, themeScope);
        var pathCard = CardSurface(Location(localization.T("Settings.Dialog.SafeMigrationTo"), newPath, themeScope), themeScope);
        var openFolder = ActionButton(localization.T("Settings.Dialog.MigrateOpenTargetButton"));
        var ok = ActionButton(localization.T("Common.Done"), primary: true);
        openFolder.Click += async (_, _) => await OpenFolderAsync(newPath);
        ok.Click += (_, _) => dialog.Hide();

        var content = new StackPanel
        {
            Spacing = 12,
            Children = { header, body, pathCard, Footer(openFolder, ok) }
        };
        dialog.Content = content;
        using var sizing = new DialogSize(root, dialog, content);
        await dialog.ShowAsync();
    }

    private ManagedStorageMigrationDialog(
        XamlRoot root,
        DispatcherQueue dispatcher,
        LocalizationService localization,
        string oldPath,
        string newPath,
        bool restart,
        bool retainOriginals,
        bool includeOrphanFolders,
        Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> migrate,
        Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? retrySourceCleanup,
        Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? cleanupInterruptedCopies,
        Action<bool>? onRunningStateChanged)
    {
        _themeScope = root.Content;
        _file = SingleLine(22, 13.5, FontWeights.SemiBold, secondary: false, scope: _themeScope);
        _directory = SingleLine(20, 12, FontWeights.Normal, secondary: true, scope: _themeScope);
        _detail = SecondaryText(size: 12.5, scope: _themeScope);
        _runningHint = SecondaryText(size: 12, scope: _themeScope);
        _localization = localization;
        _dispatcher = dispatcher;
        _oldPath = oldPath;
        _newPath = newPath;
        _restart = restart;
        _retainOriginals = retainOriginals;
        _includeOrphanFolders = includeOrphanFolders;
        _migrate = migrate;
        _retrySourceCleanup = retrySourceCleanup;
        _cleanupInterruptedCopies = cleanupInterruptedCopies;
        _onRunningStateChanged = onRunningStateChanged;

        _dialog = Dialog(root, localization);
        _primary = ActionButton(localization.T("Settings.Dialog.SafeMigrationRetry"), primary: true);
        _actionSecondary = ActionButton(localization.T("Settings.Dialog.SafeMigrationCleanupCopies"));
        _actionSecondary.Visibility = Visibility.Collapsed;
        _secondary = ActionButton(localization.T("Settings.Dialog.SafeMigrationStop"));
        _openSource = ActionButton(localization.T("Settings.Dialog.SafeMigrationOpenSource"));
        _openTarget = ActionButton(localization.T("Settings.Dialog.MigrateOpenTargetButton"));

        _openSource.Click += async (_, _) => await OpenFolderAsync(_oldPath);
        _openTarget.Click += async (_, _) => await OpenFolderAsync(_newPath);
        _primary.Click += async (_, _) =>
        {
            if (_primaryChoosesLocation)
            {
                _pickAnother = true;
                _dialog.Hide();
            }
            else if (_primaryRetriesSourceCleanup)
            {
                await RunRetrySourceCleanupAsync();
            }
            else if (_result is not null && _result.RemainingSourceFileCount == 0)
            {
                _dialog.Hide();
            }
            else
            {
                await RunAttemptAsync();
            }
        };
        _actionSecondary.Click += async (_, _) =>
        {
            if (_secondaryCleansInterruptedCopies)
            {
                await RunCleanupInterruptedCopiesAsync();
            }
        };
        _secondary.Click += (_, _) =>
        {
            if (!_running) { _dialog.Hide(); return; }
            if (_committing) return;
            _cancel.Cancel();
            _phase.Text = localization.T("Settings.Dialog.SafeMigrationStopping");
            _secondary.IsEnabled = false;
        };

        var heading = new Grid
        {
            ColumnDefinitions =
            {
                new() { Width = new GridLength(1, GridUnitType.Star) },
                new() { Width = GridLength.Auto }
            }
        };
        heading.Children.Add(_phase);
        Grid.SetColumn(_percent, 1);
        heading.Children.Add(_percent);

        _currentItemCard = CardSurface(new StackPanel
        {
            Spacing = 4,
            Children = { _file, _directory, _detail }
        }, _themeScope);

        _progressView = new StackPanel
        {
            Spacing = 10,
            Children = { heading, _bar, _currentItemCard, _runningHint }
        };

        _resultView = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        _details = new Expander
        {
            Header = localization.T("Settings.Dialog.SafeMigrationTechnicalDetails"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Content = new ScrollViewer { MaxHeight = 130, Content = _technical }
        };

        _linksRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Visibility = Visibility.Collapsed,
            Children = { _openSource, _openTarget }
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _progressView,
                _resultView,
                _details,
                _linksRow,
                Footer(_actionSecondary, _primary, _secondary)
            }
        };
        _dialog.Content = content;
        _sizing = new DialogSize(root, _dialog, content);
        _dialog.Closing += (_, args) =>
        {
            if (!_running) return;
            args.Cancel = true;
            if (!_committing) _cancel.Cancel();
        };
    }

    public static async Task<StorageMigrationDialogResult> RunAsync(
        XamlRoot root,
        DispatcherQueue dispatcher,
        LocalizationService localization,
        string oldPath,
        string newPath,
        Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> migrate,
        bool restart = false,
        bool retainOriginals = false,
        bool includeOrphanFolders = false,
        Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? retrySourceCleanup = null,
        Func<CancellationToken, Task<ManagedStorageMigrationCleanupResult>>? cleanupInterruptedCopies = null,
        Action<bool>? onRunningStateChanged = null)
    {
        var view = new ManagedStorageMigrationDialog(
            root, dispatcher, localization, oldPath, newPath,
            restart, retainOriginals, includeOrphanFolders,
            migrate, retrySourceCleanup, cleanupInterruptedCopies, onRunningStateChanged);
        try
        {
            var visible = view._dialog.ShowAsync().AsTask();
            await view.RunAttemptAsync();
            await visible;
            return new(view._result, view._pickAnother);
        }
        finally
        {
            view._closed = true;
            view._cancel.Cancel();
            view._cancel.Dispose();
            view._sizing.Dispose();
            onRunningStateChanged?.Invoke(false);
        }
    }

    private async Task RunAttemptAsync()
    {
        if (_running || _closed) return;
        _running = true;
        _onRunningStateChanged?.Invoke(true);
        _committing = false;
        _result = null;
        _primaryChoosesLocation = false;
        _primaryRetriesSourceCleanup = false;
        _secondaryCleansInterruptedCopies = false;
        int attempt = ++_attempt;
        _cancel.Dispose();
        _cancel = new();

        _primary.Visibility = Visibility.Collapsed;
        _actionSecondary.Visibility = Visibility.Collapsed;
        _linksRow.Visibility = Visibility.Collapsed;
        _openSource.Visibility = _openTarget.Visibility = Visibility.Collapsed;
        _details.Visibility = Visibility.Collapsed;
        _details.IsExpanded = false;
        _resultView.Visibility = Visibility.Collapsed;
        _progressView.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        _file.Text = _directory.Text = _percent.Text = "";
        _file.Visibility = Visibility.Collapsed;
        _directory.Visibility = Visibility.Collapsed;
        _phase.Text = _localization.T("Settings.Dialog.SafeMigrationPhase.Preparing");
        _detail.Text = _localization.T("Settings.Dialog.SafeMigrationRunningNote");
        _runningHint.Text = _localization.T("Settings.Dialog.SafeMigrationDoNotModifyHint");
        _secondary.Content = _localization.T("Settings.Dialog.SafeMigrationStop");
        _secondary.Visibility = Visibility.Visible;
        _secondary.IsEnabled = true;

        try
        {
            _result = await _migrate(new(
                new Progress<ManagedStorageMigrationProgress>(p =>
                {
                    void Apply() { if (_running && attempt == _attempt && !_closed) Report(p); }
                    if (_dispatcher.HasThreadAccess) Apply(); else _dispatcher.TryEnqueue(Apply);
                }),
                _cancel.Token,
                RestartIfCopiesRemoved: _restart,
                RetainOriginals: _retainOriginals,
                IncludeOrphanFolders: _includeOrphanFolders));
            ShowCompletedResult(_result);
        }
        catch (OperationCanceledException ex)
        {
            _restart = false;
            var stopped = ex as StorageMigrationStoppedException;
            bool hasPartialCopies = stopped?.HasPartialCopies == true || Directory.Exists(_newPath);
            ShowOutcomeResult(
                "\uE946",
                "TextFillColorSecondaryBrush",
                _localization.T("Settings.Dialog.SafeMigrationStopped"),
                _localization.T("Settings.Dialog.SafeMigrationStoppedBody"),
                blockingPath: null,
                temporaryBytes: stopped?.TemporaryBytes,
                showOpenSource: Directory.Exists(_oldPath),
                showOpenTarget: Directory.Exists(_newPath));

            _primary.Content = _localization.T("Settings.Dialog.SafeMigrationResume");
            _primary.Visibility = Visibility.Visible;
            if (hasPartialCopies && _cleanupInterruptedCopies is not null)
            {
                _secondaryCleansInterruptedCopies = true;
                _actionSecondary.Content = _localization.T("Settings.Dialog.SafeMigrationCleanupCopies");
                _actionSecondary.Visibility = Visibility.Visible;
            }
            _secondary.Content = _localization.T("Common.Close");
            _secondary.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            var problem = ManagedStorageMigrationPresentation.Classify(ex);
            string? blockingPath = ManagedStorageMigrationPresentation.ExtractBlockingPath(ex);
            _restart = problem == StorageMigrationProblem.CopiesRemoved;
            _primaryChoosesLocation = ManagedStorageMigrationPresentation.NeedsNewLocation(problem);
            bool hasTargetDir = Directory.Exists(_newPath);

            ShowOutcomeResult(
                "\uEA39",
                "SystemFillColorCriticalBrush",
                _localization.T(_restart ? "Settings.Dialog.SafeMigrationCopiesRemovedTitle" : "Settings.Dialog.MigrateFailedTitle"),
                _localization.T("Settings.Dialog.SafeMigrationError." + problem),
                blockingPath: blockingPath,
                temporaryBytes: (ex as StorageMigrationException)?.TemporaryBytes,
                showOpenSource: Directory.Exists(_oldPath),
                showOpenTarget: hasTargetDir);

            _primary.Content = _localization.T(_restart
                ? "Settings.Dialog.SafeMigrationRestart"
                : _primaryChoosesLocation
                    ? "Settings.Dialog.SafeMigrationChooseLocation"
                    : "Settings.Dialog.SafeMigrationRetry");
            _primary.Visibility = Visibility.Visible;

            if (hasTargetDir &&
                _cleanupInterruptedCopies is not null &&
                problem is StorageMigrationProblem.SourceChanged or StorageMigrationProblem.InUse or StorageMigrationProblem.DiskFull or StorageMigrationProblem.Unknown)
            {
                _secondaryCleansInterruptedCopies = true;
                _actionSecondary.Content = _localization.T("Settings.Dialog.SafeMigrationCleanupCopies");
                _actionSecondary.Visibility = Visibility.Visible;
            }

            _technical.Text = ex.Message;
            _details.Visibility = Visibility.Visible;
            _secondary.Content = _localization.T("Common.Close");
            _secondary.Visibility = Visibility.Visible;
        }
        finally
        {
            _running = false;
            _onRunningStateChanged?.Invoke(false);
            _secondary.IsEnabled = true;
        }
    }

    private void ShowCompletedResult(ManagedStorageMigrationResult result)
    {
        bool partialCleanup = !result.OriginalsRetained && result.RemainingSourceFileCount > 0;
        string sizeText = FileMetaService.FormatSize(result.CopiedBytes);
        string title;
        string body;
        string iconGlyph;
        string iconBrush;

        if (partialCleanup)
        {
            iconGlyph = "\uE7BA";
            iconBrush = "SystemFillColorCautionBrush";
            title = _localization.Format("Settings.Dialog.SafeMigrationPartialCleanupTitle", result.RemainingSourceFileCount);
            body = _localization.Format("Settings.Dialog.SafeMigrationPartialCleanupBody",
                result.CopiedFileCount, sizeText, result.RemainingSourceFileCount);
        }
        else
        {
            iconGlyph = "\uE73E";
            iconBrush = "SystemFillColorSuccessBrush";
            title = _localization.T("Settings.Dialog.MigrateCompleteTitle");
            body = result.Mode == ManagedStorageMigrationMode.SameVolumeMove
                ? _localization.Format("Settings.Dialog.SafeMigrationCompleteSameVolume",
                    result.AffectedWidgetCount + result.MigratedOrphanFolderCount,
                    result.CopiedFileCount, sizeText)
                : result.OriginalsRetained
                    ? _localization.Format("Settings.Dialog.SafeMigrationComplete",
                        result.CopiedFileCount, sizeText, result.OldRootPath)
                    : _localization.Format("Settings.Dialog.SafeMigrationCompleteCleaned",
                        result.CopiedFileCount, sizeText);
        }

        if (result.SkippedOrphanFolderCount > 0)
        {
            body += Environment.NewLine + _localization.Format(
                "Settings.Dialog.SafeMigrationSkippedOrphansNote",
                result.SkippedOrphanFolderCount);
        }

        ShowOutcomeResult(
            iconGlyph,
            iconBrush,
            title,
            body,
            blockingPath: null,
            temporaryBytes: result.TemporaryBytes,
            showOpenSource: result.OldRootStillExists,
            showOpenTarget: true);

        if (partialCleanup && _retrySourceCleanup is not null)
        {
            _primaryRetriesSourceCleanup = true;
            _primary.Content = _localization.T("Settings.Dialog.SafeMigrationRetryCleanup");
            _primary.Visibility = Visibility.Visible;
            _secondary.Content = _localization.T("Common.Close");
            _secondary.Visibility = Visibility.Visible;
        }
        else
        {
            _primary.Content = _localization.T("Common.Done");
            _primary.Visibility = Visibility.Visible;
            _secondary.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunRetrySourceCleanupAsync()
    {
        if (_running || _closed || _retrySourceCleanup is null || _result is null) return;
        _running = true;
        _onRunningStateChanged?.Invoke(true);
        _primary.IsEnabled = false;
        _secondary.IsEnabled = false;
        try
        {
            var cleanup = await _retrySourceCleanup(CancellationToken.None);
            _result = _result with
            {
                CleanedSourceFileCount = _result.CleanedSourceFileCount + cleanup.CleanedFileCount,
                RemainingSourceFileCount = cleanup.RemainingFileCount,
                OldRootStillExists = cleanup.TargetStillExists
            };
            _primaryRetriesSourceCleanup = false;
            ShowCompletedResult(_result);
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Retry source cleanup failed: {ex}");
            _technical.Text = ex.Message;
            _details.Visibility = Visibility.Visible;
        }
        finally
        {
            _running = false;
            _onRunningStateChanged?.Invoke(false);
            _primary.IsEnabled = true;
            _secondary.IsEnabled = true;
        }
    }

    private async Task RunCleanupInterruptedCopiesAsync()
    {
        if (_running || _closed || _cleanupInterruptedCopies is null) return;
        _running = true;
        _onRunningStateChanged?.Invoke(true);
        _primary.IsEnabled = false;
        _actionSecondary.IsEnabled = false;
        _secondary.IsEnabled = false;
        try
        {
            var cleanup = await _cleanupInterruptedCopies(CancellationToken.None);
            _secondaryCleansInterruptedCopies = false;
            _actionSecondary.Visibility = Visibility.Collapsed;

            string message = cleanup.RemainingFileCount == 0
                ? _localization.Format("Settings.Dialog.SafeMigrationCleanupCopiesDone",
                    cleanup.CleanedFileCount, FileMetaService.FormatSize(cleanup.FreedBytes))
                : _localization.Format("Settings.Dialog.SafeMigrationCleanupCopiesPartial",
                    cleanup.CleanedFileCount, cleanup.RemainingFileCount);

            ShowOutcomeResult(
                cleanup.RemainingFileCount == 0 ? "\uE73E" : "\uE7BA",
                cleanup.RemainingFileCount == 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush",
                _localization.T("Settings.Dialog.SafeMigrationCleanupCopiesTitle"),
                message,
                blockingPath: null,
                temporaryBytes: null,
                showOpenSource: Directory.Exists(_oldPath),
                showOpenTarget: cleanup.TargetStillExists);

            _primaryChoosesLocation = false;
            _restart = false;
            _primary.Content = _localization.T("Settings.Dialog.SafeMigrationRestart");
            _primary.Visibility = Visibility.Visible;
            _secondary.Content = _localization.T("Common.Close");
            _secondary.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.Log($"[ManagedStorageMigration] Cleanup interrupted copies failed: {ex}");
            _technical.Text = ex.Message;
            _details.Visibility = Visibility.Visible;
        }
        finally
        {
            _running = false;
            _onRunningStateChanged?.Invoke(false);
            _primary.IsEnabled = true;
            _actionSecondary.IsEnabled = true;
            _secondary.IsEnabled = true;
        }
    }

    private void ShowOutcomeResult(
        string iconGlyph,
        string iconBrushKey,
        string title,
        string message,
        string? blockingPath,
        long? temporaryBytes,
        bool showOpenSource,
        bool showOpenTarget)
    {
        _progressView.Visibility = Visibility.Collapsed;
        _resultView.Visibility = Visibility.Visible;
        _resultView.Children.Clear();
        _resultView.Children.Add(StatusHeader(iconGlyph, iconBrushKey, title, _themeScope));

        var cardStack = new StackPanel { Spacing = 8 };
        cardStack.Children.Add(Text(message, 13.5));

        if (!string.IsNullOrWhiteSpace(blockingPath))
        {
            var pathLine = SingleLine(18, 12, FontWeights.Normal, secondary: true, scope: _themeScope);
            pathLine.Text = blockingPath;
            ToolTipService.SetToolTip(pathLine, blockingPath);
            cardStack.Children.Add(Divider(_themeScope));
            cardStack.Children.Add(pathLine);
        }

        if (temporaryBytes is > 0)
        {
            cardStack.Children.Add(SecondaryText(_localization.Format("Settings.Dialog.SafeMigrationTemporaryBytes",
                FileMetaService.FormatSize(temporaryBytes.Value)), 12.5, _themeScope));
        }

        _resultView.Children.Add(CardSurface(cardStack, _themeScope));

        _openSource.Visibility = showOpenSource ? Visibility.Visible : Visibility.Collapsed;
        _openTarget.Visibility = showOpenTarget ? Visibility.Visible : Visibility.Collapsed;
        _linksRow.Visibility = (showOpenSource || showOpenTarget) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Report(ManagedStorageMigrationProgress progress)
    {
        _committing = progress.Phase is ManagedStorageMigrationPhase.Committing or ManagedStorageMigrationPhase.Completed;
        _secondary.IsEnabled = !_committing && !_cancel.IsCancellationRequested;
        if (_cancel.IsCancellationRequested && !_committing) return;

        _phase.Text = FormatPhaseHeading(progress);
        double? percentage = ManagedStorageMigrationPresentation.Percentage(progress);
        _bar.IsIndeterminate = percentage is null;
        if (percentage is { } value) _bar.Value = value;
        _percent.Text = percentage is { } percent ? $"{percent:0}%" : "";

        string fullPath = progress.CurrentItemName ?? "";
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            _file.Text = Path.GetFileName(fullPath);
            _directory.Text = Path.GetDirectoryName(fullPath) ?? "";
            ToolTipService.SetToolTip(_file, fullPath);
            ToolTipService.SetToolTip(_directory, _directory.Text);
            _file.Visibility = Visibility.Visible;
            _directory.Visibility = string.IsNullOrWhiteSpace(_directory.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            _file.Visibility = Visibility.Collapsed;
            _directory.Visibility = Visibility.Collapsed;
        }

        if (progress.Phase == ManagedStorageMigrationPhase.Moving)
        {
            _detail.Text = _localization.Format("Settings.Dialog.SafeMigrationMovingProgress",
                progress.CompletedItems, progress.TotalItems);
        }
        else if (progress.Phase == ManagedStorageMigrationPhase.CleaningOrigin)
        {
            _detail.Text = _localization.Format("Settings.Dialog.SafeMigrationCleaningProgress",
                progress.CompletedItems, progress.TotalItems);
        }
        else if (progress.Phase is ManagedStorageMigrationPhase.Preparing or ManagedStorageMigrationPhase.Committing)
        {
            _detail.Text = _localization.T("Settings.Dialog.SafeMigrationPhase." + progress.Phase);
        }
        else
        {
            string baseDetail = _localization.Format("Settings.Dialog.SafeMigrationProgress",
                progress.CompletedItems, progress.TotalItems,
                FileMetaService.FormatSize(progress.BytesProcessed),
                FileMetaService.FormatSize(progress.TotalBytes));
            string rate = ManagedStorageMigrationPresentation.FormatTransferRate(progress.BytesPerSecond);
            _detail.Text = string.IsNullOrEmpty(rate) ? baseDetail : $"{baseDetail} · {rate}";
        }

        _runningHint.Text = _committing
            ? _localization.T("Settings.Dialog.SafeMigrationCommittingHint")
            : _localization.T("Settings.Dialog.SafeMigrationDoNotModifyHint");
    }

    private string FormatPhaseHeading(ManagedStorageMigrationProgress progress)
    {
        if (progress.Phase is ManagedStorageMigrationPhase.Copying or ManagedStorageMigrationPhase.Verifying or ManagedStorageMigrationPhase.CleaningOrigin &&
            progress.StepIndex > 0 &&
            progress.TotalSteps > 0)
        {
            string phaseName = _localization.T("Settings.Dialog.SafeMigrationStepName." + progress.Phase);
            return _localization.Format("Settings.Dialog.SafeMigrationStepFormat", phaseName, progress.StepIndex, progress.TotalSteps);
        }

        return _localization.T("Settings.Dialog.SafeMigrationPhase." + progress.Phase);
    }

    private static ContentDialog Dialog(XamlRoot root, LocalizationService localization)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(root.Content),
            Title = localization.T("Settings.Dialog.SafeMigrationTitle"),
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = 540d;
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            dialog.Hide();
        };
        return dialog;
    }

    private static StackPanel StatusHeader(string glyph, string brushKey, string title, DependencyObject? scope = null)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (ThemeBrush(brushKey, scope) is { } iconBrush)
        {
            icon.Foreground = iconBrush;
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = title,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
    }

    private static Border CardSurface(UIElement child, DependencyObject? scope = null) => new()
    {
        Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", scope),
        BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", scope),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 12, 14, 12),
        Child = child
    };

    private static Border Divider(DependencyObject? scope = null) => new()
    {
        Height = 1,
        Background = ThemeBrush("DividerStrokeColorDefaultBrush", scope),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static Brush? ThemeBrush(string key, DependencyObject? scope = null)
    {
        if (NeutralInteractionBrush.ResolveThemedResource(key, scope) is { } themed)
        {
            return themed;
        }

        return Application.Current?.Resources.TryGetValue(key, out object? value) == true
            ? value as Brush
            : null;
    }

    private static void ApplyForeground(TextBlock target, string brushKey, DependencyObject? scope = null)
    {
        if (ThemeBrush(brushKey, scope) is { } brush)
        {
            target.Foreground = brush;
        }
    }

    private static TextBlock Text(string? text = null, double size = 14) => new()
    {
        Text = text ?? "",
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true
    };

    private static TextBlock SecondaryText(string? text = null, double size = 13, DependencyObject? scope = null)
    {
        var block = new TextBlock
        {
            Text = text ?? "",
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        ApplyForeground(block, "TextFillColorSecondaryBrush", scope);
        return block;
    }

    private static TextBlock SingleLine(
        double height,
        double size = 13.5,
        Windows.UI.Text.FontWeight? weight = null,
        bool secondary = false,
        DependencyObject? scope = null)
    {
        // Never assign Foreground = null in WinUI 3: a local null overrides the
        // inherited theme foreground and renders the text completely invisible.
        var block = new TextBlock
        {
            Height = height,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true
        };
        if (secondary)
        {
            ApplyForeground(block, "TextFillColorSecondaryBrush", scope);
        }

        return block;
    }

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button { Content = text, MinWidth = 92, MinHeight = 32, Padding = new Thickness(14, 5, 14, 5) };
        if (primary && Application.Current?.Resources.TryGetValue("AccentButtonStyle", out object style) == true)
            button.Style = (Style)style;
        return button;
    }

    private static StackPanel Footer(params Button[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0)
        };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }

    private static Grid Location(string label, string path, DependencyObject? scope = null)
    {
        var row = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions =
            {
                new() { Width = GridLength.Auto },
                new() { Width = new GridLength(1, GridUnitType.Star) }
            }
        };
        var labelText = SecondaryText(label, 12.5, scope);
        labelText.MinWidth = 28;
        labelText.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(labelText);

        var value = SingleLine(22, 13, FontWeights.Normal, secondary: false, scope: scope);
        value.Text = path;
        value.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(value, path);
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private sealed class DialogSize : IDisposable
    {
        private readonly XamlRoot _root;
        private readonly FrameworkElement _content;
        internal DialogSize(XamlRoot root, ContentDialog dialog, FrameworkElement content)
        {
            _root = root;
            _content = content;
            root.Changed += Changed;
            Resize();
        }
        private void Changed(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
        private void Resize() => _content.Width = Math.Max(0, Math.Min(492, _root.Size.Width - 80));
        public void Dispose() => _root.Changed -= Changed;
    }

    private static async Task OpenFolderAsync(string path)
    {
        try { await Launcher.LaunchFolderAsync(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path)); }
        catch (Exception ex) { App.Log($"[ManagedStorageMigration] Open location failed: {ex.Message}"); }
    }
}
