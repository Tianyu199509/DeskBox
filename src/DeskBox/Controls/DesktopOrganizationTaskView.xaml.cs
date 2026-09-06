using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView : UserControl
{
    private DesktopOrganizationPlan? _plan;
    private DesktopOrganizationPlan? _basePlan;
    private DesktopOrganizationPlan? _lastExecutionPlan;
    private readonly HashSet<string> _excludedSourcePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingSources;
    private readonly HashSet<string> _optionalIncludedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DesktopOrganizationRetainedItem> _runtimeRetainedItems = [];
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _executionCts;
    private string? _lastHistoryId;
    private int _scanGeneration;
    private bool _isExecuting;
    private bool _closeAfterExecutionStops;
    private bool _hasCompletedExecution;
    private bool _isScanning;
    private readonly HashSet<string> _newPaths = new(StringComparer.OrdinalIgnoreCase);
    private DesktopOrganizationCoordinator? _coordinator;
    private bool? _pendingRecovery;

    public DesktopOrganizationTaskView()
    {
        InitializeComponent();
        ApplyStaticLocalization();
        Loaded += TaskView_Loaded;
        Unloaded += TaskView_Unloaded;
        ActualThemeChanged += (_, _) => QueueAppearanceRefresh();
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? OrganizationCompleted;

    public event EventHandler? OrganizationUndone;

    public bool IsExecutionRunning => _isExecuting;

    public void BeginScan()
    {
        if (!_isExecuting)
        {
            _ = ScanAsync();
        }
    }

    public void CancelPendingWork()
    {
        _scanCts?.Cancel();
        _executionCts?.Cancel();
    }

    public void CancelExecutionAndCloseWhenSafe()
    {
        if (!_isExecuting)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _closeAfterExecutionStops = true;
        _executionCts?.Cancel();
        ResultInfo.Severity = InfoBarSeverity.Warning;
        ResultInfo.Title = T("DesktopOrganization.Window.CloseBlocked");
        ResultInfo.Message = string.Empty;
        ResultInfo.IsOpen = true;
    }

    private async Task ScanAsync(bool preserveSelection = false)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        int generation = ++_scanGeneration;

        var previous = preserveSelection && !_hasCompletedExecution ? _basePlan : null;
        bool includePersonal = _plan?.IncludePersonalDesktop ?? true;
        bool includePublic = _plan?.IncludePublicDesktop ?? false;
        double scrollOffset = previous is not null ? PreviewScrollViewer.VerticalOffset : 0;
        if (previous is null) ResetPreviewState();
        _isScanning = true;
        SourceSelectionHost.IsEnabled = false;
        PreviewContent.IsEnabled = false;
        PreviewSections.IsEnabled = false;
        RetainedSelectionToolbar.IsEnabled = false;
        MoreButton.IsEnabled = false;
        PreviewContent.Opacity = 0.24;
        ScanBusyPanel.Visibility = Visibility.Visible;
        RefreshButton.IsEnabled = false;
        ExecuteButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        ChangePathButton.IsEnabled = false;

        try
        {
            var coordinator = Coordinator;
            DesktopOrganizationPlan scanned = await coordinator.BuildPlanAsync(false, cts.Token);
            if (cts.IsCancellationRequested || generation != _scanGeneration) return;
            DesktopOrganizationPreviewReconciliation? changes = previous is null ? null :
                DesktopOrganizationPreviewReconciliation.Calculate(previous.SourceItems, scanned.SourceItems,
                    _excludedSourcePaths, _optionalIncludedPaths, _newPaths);
            DesktopOrganizationPlan plan = coordinator.CreatePreviewPlanWithOptionalItems(scanned,
                changes?.OptionalPaths ?? _optionalIncludedPaths, includePersonal,
                includePublic && !scanned.PublicDesktopUnavailable);
            if (changes is not null)
            {
                _excludedSourcePaths.Clear();
                _excludedSourcePaths.UnionWith(changes.ExcludedPaths);
                _optionalIncludedPaths.Clear();
                _optionalIncludedPaths.UnionWith(changes.OptionalPaths);
                _newPaths.Clear();
                _newPaths.UnionWith(changes.NewPaths);
                ShowSelectionFeedback(Format("DesktopOrganization.Layout.RefreshChanges", changes.AddedCount,
                    changes.RemovedCount), showPendingLink: false);
            }
            _basePlan = scanned;
            _plan = plan;
            RenderPlan(plan);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (generation == _scanGeneration && IsLoaded)
                    PreviewScrollViewer.ChangeView(null, scrollOffset, null, disableAnimation: true);
            });
            UpdateRecoveryState();
            var pendingUndo = App.Current.SettingsService.Settings.RecentOrganizationHistory.FirstOrDefault(entry =>
                entry.ActionType == OrganizationActionType.DesktopOrganization && entry.UndoStarted && entry.CanUndo);
            if (pendingUndo is not null)
            {
                _lastHistoryId = pendingUndo.Id;
                UndoButton.Content = T("DesktopOrganization.Public.ContinueUndo");
                UndoButton.Visibility = Visibility.Visible;
                ResultInfo.Severity = InfoBarSeverity.Warning;
                ResultInfo.Title = T("DesktopOrganization.Public.UndoPendingTitle");
                ResultInfo.Message = Format("DesktopOrganization.Public.UndoPending",
                    pendingUndo.Items.Count(item => item.IsRestored), pendingUndo.Items.Count(item => !item.IsRestored));
                ResultInfo.IsOpen = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Scan failed: {ex}");
            if (generation != _scanGeneration) return;
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Layout.ScanFailed");
            ResultInfo.Message = T(previous is null ? "DesktopOrganization.Result.FailedBody" : "DesktopOrganization.Layout.ScanPreserved");
            ResultInfo.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts) && generation == _scanGeneration)
            {
                _isScanning = false;
                PreviewContent.Opacity = 1;
                PreviewContent.IsEnabled = true;
                PreviewSections.IsEnabled = true;
                RetainedSelectionToolbar.IsEnabled = true;
                MoreButton.IsEnabled = true;
                ScanBusyPanel.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                ChangePathButton.IsEnabled = true;
                SourceSelectionHost.IsEnabled = true;
                UndoButton.IsEnabled = true;
                UpdateSummary(_plan);
            }
        }
    }

    private void ResetPreviewState()
    {
        _plan = _basePlan = _lastExecutionPlan = null;
        ReleasePreviewCards();
        ReleaseRetainedCards();
        ReleaseCompletedCards();
        _retainedSelection.Clear();
        _expandedTargets.Clear();
        _completedItems.Clear();
        _restoredItems.Clear();
        _showRetained = false;
        PreviewSections.SelectedItem = SummaryTitle;
        RetainedSelectionToolbar.Visibility = Visibility.Collapsed;
        _previewScrollOffset = _retainedScrollOffset = 0;
        CompletedItemsPanel.Children.Clear();
        CompletedItemsPanel.Visibility = Visibility.Collapsed;
        RetainedItemsPanel.Visibility = Visibility.Collapsed;
        TargetSelectionHost.Visibility = Visibility.Visible;
        PreviewScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        _excludedSourcePaths.Clear();
        _newPaths.Clear();
        _targetSelections.Clear();
        SourceSelectionHost.IsEnabled = false;
        TargetSelectionHost.IsEnabled = true;
        RetryPublicButton.Visibility = Visibility.Collapsed;
        _optionalIncludedPaths.Clear();
        _runtimeRetainedItems.Clear();
        _hasCompletedExecution = false;
        _lastHistoryId = null;
        _pendingRecovery = null;
        UndoButton.Visibility = Visibility.Collapsed;
        UndoButton.Content = T("DesktopOrganization.Layout.Undo");
        DoneButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ExecuteButton.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
        ResultInfo.IsOpen = false;
        SelectionFeedbackInfo.IsOpen = false;
        ExecutionProgressPanel.Visibility = Visibility.Collapsed;
        ExecutionProgressBar.Value = 0;
        ExecutionProgressText.Text = string.Empty;
        StoragePathText.Text = SettingsService.NormalizeManagedStorageRootPath(
            App.Current.SettingsService.Settings.DefaultManagedStorageRootPath);
    }

    private static DesktopOrganizationCoordinator CreateCoordinator()
    {
        App app = App.Current;
        if (app.WidgetManager is null)
        {
            throw new InvalidOperationException("Widget manager is not available.");
        }

        return new DesktopOrganizationCoordinator(
            app.SettingsService,
            app.FileService,
            app.WidgetManager,
            app.OrganizerService,
            app.LocalizationService);
    }

    // The coordinator and its planner/scanner graph hold no per-call state;
    // one instance per task view session is equivalent to constructing per call.
    private DesktopOrganizationCoordinator Coordinator => _coordinator ??= CreateCoordinator();

    // A UI hint only: the transaction layer re-checks the journal under its
    // gate before any move. Refreshed by UpdateRecoveryState after each scan,
    // execution, undo, or recovery so clicks never hit the disk.
    private bool HasPendingRecovery => _pendingRecovery ??= Coordinator.HasPendingRecovery;

    private static string T(string key) => App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] values) =>
        App.Current.LocalizationService.Format(key, values);

    private void ApplyStaticLocalization()
    {
        PathTitleText.Text = T("DesktopOrganization.Path.Title");
        StorageSectionTitleText.Text = T("DesktopOrganization.Path.SectionTitle");
        StorageSectionDescriptionText.Text = T("DesktopOrganization.Layout.GlobalStorageHelp");
        SourcePathsTitle.Text = T("DesktopOrganization.Layout.SourcePaths");
        SourceScopeText.Text = T("DesktopOrganization.Layout.Scope");
        LocationsButton.Content = T("DesktopOrganization.Layout.Locations");
        ResetSelectionMenuItem.Text = T("DesktopOrganization.Layout.Reset");
        ToolTipService.SetToolTip(MoreButton, T("DesktopOrganization.Layout.More"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MoreButton, T("DesktopOrganization.Layout.More"));
        ViewPendingButton.Content = T("DesktopOrganization.Layout.ViewPending");
        ToolTipService.SetToolTip(LocationsButton, T("DesktopOrganization.Layout.Locations"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LocationsButton, T("DesktopOrganization.Layout.Locations"));
        ChangePathButton.Content = T("DesktopOrganization.Layout.StorageSettings");
        ScanBusyText.Text = T("DesktopOrganization.Preview.Scanning");
        RefreshButton.Content = T("DesktopOrganization.Preview.Refresh");
        UndoButton.Content = T("DesktopOrganization.Layout.Undo");
        CancelButton.Content = T("DesktopOrganization.Window.Cancel");
        RetryPublicButton.Content = T("DesktopOrganization.Layout.RetryRemaining");
        RecoverButton.Content = T("DesktopOrganization.Public.Recover");
        RecoveryInfo.Message = T("DesktopOrganization.Public.RecoveryPending");
        DoneButton.Content = T("DesktopOrganization.Window.Done");
    }
}
