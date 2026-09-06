using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private void RenderSources(DesktopOrganizationPlan plan)
    {
        _updatingSources = true;
        try
        {
            PersonalDesktopSourceButton.IsChecked = plan.IncludePersonalDesktop;
            PublicDesktopSourceButton.IsChecked = plan.IncludePublicDesktop;
            PersonalSourceText.Text = T("DesktopOrganization.Public.PersonalLabel") + " (" +
                Format("DesktopOrganization.Preview.ItemCount", plan.SourceItems.Count(item => item.SourceScope == DesktopOrganizationSourceScope.Personal)) + ")";
            PublicSourceText.Text = plan.PublicDesktopUnavailable
                ? T("DesktopOrganization.Public.Unavailable")
                : T("DesktopOrganization.Public.SharedLabel") + " (" +
                    Format("DesktopOrganization.Preview.ItemCount", plan.SourceItems.Count(item => item.SourceScope == DesktopOrganizationSourceScope.Public)) + ")";
            PublicDesktopSourceButton.IsEnabled = !plan.PublicDesktopUnavailable;
            PersonalDesktopPathText.Text = plan.DesktopPath;
            PublicDesktopPathText.Text = plan.PublicDesktopPath;
            ToolTipService.SetToolTip(PersonalDesktopPathText, plan.DesktopPath);
            ToolTipService.SetToolTip(PublicDesktopPathText, plan.PublicDesktopPath);
            ToolTipService.SetToolTip(PersonalDesktopSourceButton, plan.DesktopPath);
            ToolTipService.SetToolTip(PublicDesktopSourceButton,
                T("DesktopOrganization.Public.Impact") + "\n" + plan.PublicDesktopPath);
        }
        finally { _updatingSources = false; }
    }

    private void SourceSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSources || _isScanning || _isExecuting || _hasCompletedExecution || _basePlan is null) return;
        try
        {
            _plan = Coordinator.CreatePreviewPlanWithOptionalItems(_basePlan, _optionalIncludedPaths,
                PersonalDesktopSourceButton.IsChecked == true, PublicDesktopSourceButton.IsChecked == true);
            SelectionFeedbackInfo.IsOpen = false;
            RenderPlan(_plan);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Source selection failed: {ex}");
            if (_plan is not null) RenderSources(_plan);
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
        }
    }

    private IReadOnlyList<DesktopOrganizationFileSnapshot> GetSourceExcludedItems()
    {
        if (_hasCompletedExecution && _lastExecutionPlan is not null)
            return _lastExecutionPlan.ExcludedItems.Where(item =>
                item.ExclusionReason != DesktopOrganizationExclusionReason.SourceNotSelected).ToList();
        if (_basePlan is null || _plan is null) return [];
        return _basePlan.SourceItems.Where(item => !item.IsEligible &&
            (item.SourceScope == DesktopOrganizationSourceScope.Public ? _plan.IncludePublicDesktop : _plan.IncludePersonalDesktop)).ToList();
    }

    private string BuildSourceResult(OrganizationHistoryEntry history, DesktopOrganizationSourceScope scope)
    {
        string name = T(scope == DesktopOrganizationSourceScope.Public
            ? "DesktopOrganization.Public.SharedLabel" : "DesktopOrganization.Public.PersonalLabel");
        bool selected = scope == DesktopOrganizationSourceScope.Public
            ? _lastExecutionPlan?.IncludePublicDesktop == true : _lastExecutionPlan?.IncludePersonalDesktop == true;
        return selected
            ? Format("DesktopOrganization.Public.SourceResult", name,
                history.Items.Count(item => item.SourceScope == scope),
                _runtimeRetainedItems.Count(item => item.SourceScope == scope))
            : string.Empty;
    }

    private void UpdateRecoveryState()
    {
        _pendingRecovery = Coordinator.HasPendingRecovery;
        RecoveryInfo.IsOpen = _pendingRecovery.Value;
        if (RecoveryInfo.IsOpen) ExecuteButton.IsEnabled = false;
    }

    private async void RecoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting) return;
        _isExecuting = true;
        RecoverButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        SourceSelectionHost.IsEnabled = false;
        try
        {
            await Coordinator.RecoverPendingAsync(OwnerWindowHandle);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Recovery failed: {ex}");
            UpdateRecoveryState();
        }
        finally
        {
            _isExecuting = false;
            RecoverButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            SourceSelectionHost.IsEnabled = true;
            UpdateSummary(_plan);
        }
    }

    private async void RetryPublicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExecuting || _lastExecutionPlan is null) return;
        var remaining = _runtimeRetainedItems.Where(item => item.Reason != DesktopOrganizationRetentionReason.SourceChanged)
            .Select(item => item.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var plan = Coordinator.CreateRetryPlan(_lastExecutionPlan, remaining);
            if (plan.EligibleItemCount > 0) await RunPlanAsync(plan);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Retry plan unavailable: {ex}");
            ResultInfo.Severity = InfoBarSeverity.Error;
            ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            ResultInfo.Message = ex.Message;
            ResultInfo.IsOpen = true;
        }
    }
}
