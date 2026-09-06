using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    public IntPtr OwnerWindowHandle { get; set; }

    private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isPreviewReady || _isScanning || _isExecuting || _hasCompletedExecution || !ExecuteButton.IsEnabled || _plan is not { EligibleItemCount: > 0 } previewPlan)
        {
            return;
        }

        DesktopOrganizationPlan plan;
        try
        {
            plan = Coordinator.CreateExecutionPlan(
                previewPlan,
                _targetSelections.Values.ToList(), _excludedSourcePaths);
            if (plan.EligibleItemCount == 0)
            {
                ResultInfo.Severity = InfoBarSeverity.Warning;
                ResultInfo.Title = T("DesktopOrganization.Preview.NothingSelectedTitle");
                ResultInfo.Message = T("DesktopOrganization.Preview.NothingSelectedBody");
                ResultInfo.IsOpen = true;
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Failed to create execution plan: {ex}");
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
            return;
        }

        _lastExecutionPlan = plan;
        await RunPlanAsync(plan);
    }

    private async Task RunPlanAsync(DesktopOrganizationPlan plan)
    {
        _executionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _executionCts = cts;
        _isExecuting = true;
        SelectionFeedbackInfo.IsOpen = false;
        RetainedSelectionToolbar.IsEnabled = false;
        PreviewContent.IsEnabled = false;
        MoreButton.IsEnabled = false;
        SourceSelectionHost.IsEnabled = false;
        TargetSelectionHost.IsEnabled = false;
        RetryPublicButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        DoneButton.IsEnabled = false;
        ExcludedItemsButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ChangePathButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ExecuteButton.IsEnabled = false;
        ExecutionProgressPanel.Visibility = Visibility.Visible;
        ExecutionProgressBar.Maximum = Math.Max(1, plan.EligibleItemCount);
        ExecutionProgressBar.Value = 0;
        ExecutionProgressText.Text = T("DesktopOrganization.Preview.Preparing");
        try
        {
            var progress = new Progress<DesktopOrganizationProgress>(value =>
            {
                ExecutionProgressBar.Value = value.CompletedCount;
                ExecutionProgressText.Text = Format(
                    "DesktopOrganization.Preview.Progress",
                    value.CompletedCount,
                    value.TotalCount,
                    T(value.SourceScope == DesktopOrganizationSourceScope.Public
                        ? "DesktopOrganization.Public.SharedLabel" : "DesktopOrganization.Public.PersonalLabel") +
                    (string.IsNullOrWhiteSpace(value.TargetDisplayName) ? string.Empty : " · " + value.TargetDisplayName));
            });
            DesktopOrganizationExecutionResult result =
                await Coordinator.ExecuteAsync(plan, progress, cts.Token, OwnerWindowHandle);
            var attempted = plan.Targets.SelectMany(target => target.Items).Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _runtimeRetainedItems.RemoveAll(item => attempted.Contains(item.SourcePath));
            _runtimeRetainedItems.AddRange(result.RetainedItems);
            int retainedCount = _runtimeRetainedItems.Count;
            _lastHistoryId = result.History.CanUndo
                ? result.History.Id
                : null;
            ResultInfo.Severity = retainedCount > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            ResultInfo.Title = retainedCount > 0
                ? Format("DesktopOrganization.Layout.PartialResult", result.History.Items.Count(item => !item.IsRestored), retainedCount)
                : T("DesktopOrganization.Result.SuccessTitle");
            ResultInfo.Message = string.Join("\n", new[]
            {
                BuildSourceResult(result.History, DesktopOrganizationSourceScope.Personal),
                BuildSourceResult(result.History, DesktopOrganizationSourceScope.Public)
            }.Where(text => !string.IsNullOrWhiteSpace(text)));
            ResultInfo.IsOpen = true;
            ExecutionProgressPanel.Visibility = Visibility.Collapsed;
            _hasCompletedExecution = true;
            _optionalIncludedPaths.Clear();
            RenderExecutionResult(result.History);
            RetryPublicButton.Visibility = _runtimeRetainedItems.Any(item => item.Reason != DesktopOrganizationRetentionReason.SourceChanged)
                ? Visibility.Visible : Visibility.Collapsed;
            RefreshButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            ExecuteButton.Visibility = Visibility.Collapsed;
            UndoButton.Visibility = result.History.CanUndo
                ? Visibility.Visible
                : Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Visible;
            DoneButton.Style = RetryPublicButton.Visibility == Visibility.Visible
                ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
            OrganizationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            if (!_closeAfterExecutionStops)
            {
                ResultInfo.Severity = InfoBarSeverity.Warning;
                ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
                ResultInfo.Message = string.Empty;
                ResultInfo.IsOpen = true;
                ExecuteButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Execution failed: {ex}");
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
            ExecuteButton.IsEnabled = true;
        }
        finally
        {
            _isExecuting = false;
            RetainedSelectionToolbar.IsEnabled = true;
            PreviewContent.IsEnabled = true;
            MoreButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            ChangePathButton.IsEnabled = !_hasCompletedExecution;
            SourceSelectionHost.IsEnabled = !_hasCompletedExecution;
            TargetSelectionHost.IsEnabled = !_hasCompletedExecution;
            RetryPublicButton.IsEnabled = true;
            UndoButton.IsEnabled = true;
            DoneButton.IsEnabled = true;
            ExcludedItemsButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            UpdateRecoveryState();
            UpdateSummary(_plan);
            if (_closeAfterExecutionStops)
            {
                _closeAfterExecutionStops = false;
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || string.IsNullOrWhiteSpace(_lastHistoryId))
        {
            return;
        }

        _isExecuting = true;
        UndoButton.IsEnabled = false;
        SourceSelectionHost.IsEnabled = false;
        TargetSelectionHost.IsEnabled = false;
        ExecuteButton.IsEnabled = false;
        ChangePathButton.IsEnabled = false;
        DoneButton.IsEnabled = false;
        RetryPublicButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            await Coordinator.UndoAsync(_lastHistoryId, OwnerWindowHandle);
            ResultInfo.Severity = InfoBarSeverity.Success;
            ResultInfo.Title = T("DesktopOrganization.Undo.Success");
            ResultInfo.Message = string.Empty;
            ResultInfo.IsOpen = true;
            OrganizationUndone?.Invoke(this, EventArgs.Empty);
            RefreshButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
            ExecuteButton.Visibility = Visibility.Visible;
            UndoButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Collapsed;
            await ScanAsync();
        }
        catch (DesktopOrganizationIncompleteUndoException ex)
        {
            ResultInfo.Severity = InfoBarSeverity.Warning;
            ResultInfo.Title = T("DesktopOrganization.Public.UndoPendingTitle");
            ResultInfo.Message = Format("DesktopOrganization.Public.UndoPending", ex.RestoredCount, ex.RemainingCount);
            ResultInfo.IsOpen = true;
            // An undo attempt ends forward retries. Only the remaining undo
            // items can be continued, using their persisted receipts.
            RetryPublicButton.Visibility = Visibility.Collapsed;
            UndoButton.Content = T("DesktopOrganization.Public.ContinueUndo");
            var history = App.Current.SettingsService.Settings.RecentOrganizationHistory
                .FirstOrDefault(entry => entry.Id == _lastHistoryId);
            if (_hasCompletedExecution && history is not null) RenderExecutionResult(history);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Undo failed: {ex}");
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Undo.Failed");
            ResultInfo.Message = T("DesktopOrganization.Undo.FailedBody");
            ResultInfo.IsOpen = true;
        }
        finally
        {
            _isExecuting = false;
            UndoButton.IsEnabled = true;
            DoneButton.IsEnabled = true;
            RetryPublicButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            SourceSelectionHost.IsEnabled = !_hasCompletedExecution;
            TargetSelectionHost.IsEnabled = !_hasCompletedExecution;
            ChangePathButton.IsEnabled = !_hasCompletedExecution;
            // An interrupted undo can leave a recovery journal behind; refresh
            // the cached hint so the banner and execute button reflect it.
            UpdateRecoveryState();
            UpdateSummary(_plan);
        }
    }

    private void ChangePathButton_Click(object sender, RoutedEventArgs e)
    {
        LocationsFlyout.Hide();
        if (_isExecuting || _isScanning) return;
        App.Current.ShowSettings("FileStorageSettings");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isExecuting && !_isScanning) await ScanAsync(preserveSelection: true);
    }

    private void ResetSelectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || _isScanning || _hasCompletedExecution || _basePlan is null || _plan is null) return;
        try
        {
            var plan = Coordinator.CreatePreviewPlanWithOptionalItems(_basePlan, [],
                _plan.IncludePersonalDesktop, _plan.IncludePublicDesktop);
            _excludedSourcePaths.Clear();
            _optionalIncludedPaths.Clear();
            _retainedSelection.Clear();
            _targetSelections.Clear();
            _newPaths.Clear();
            _plan = plan;
            RenderPlan(plan);
            ShowSelectionFeedback(T("DesktopOrganization.Layout.ResetDone"), showPendingLink: false);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Reset preview failed: {ex}");
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
            ResultInfo.IsOpen = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void DoneButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
