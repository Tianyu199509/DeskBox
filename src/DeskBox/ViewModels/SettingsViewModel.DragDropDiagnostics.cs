using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string DragDropPermissionSummaryText => GetDragDropPermissionSummaryText();
    public string DragDropPermissionDetailText => GetDragDropPermissionDetailText();
    public string DragDropPermissionSeverityKind => _dragDropPermissionDiagnostic?.Severity switch
    {
        DragDropDiagnosticSeverity.Warning => "Warning",
        DragDropDiagnosticSeverity.Error => "Error",
        _ => "Normal"
    };
    public string DragDropPermissionProcessText => _dragDropPermissionDiagnostic?.CurrentProcessIntegrity ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionExplorerText => _dragDropPermissionDiagnostic?.ExplorerIntegrity ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionUacText => _dragDropPermissionDiagnostic?.UacStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionAppCompatText => _dragDropPermissionDiagnostic?.AppCompatStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionStartupText => _dragDropPermissionDiagnostic?.StartupStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionShortcutText => _dragDropPermissionDiagnostic?.ShortcutStatus ?? _localizationService.T("Settings.DragDropPermission.Unknown");
    public string DragDropPermissionRepairStatusText
    {
        get => _dragDropPermissionRepairStatusText;
        private set => SetProperty(ref _dragDropPermissionRepairStatusText, value);
    }
    public bool IsDragDropPermissionRepairing
    {
        get => _isDragDropPermissionRepairing;
        private set
        {
            if (!SetProperty(ref _isDragDropPermissionRepairing, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRepairDragDropPermission));
        }
    }
    public bool CanRepairDragDropPermission =>
        !IsDragDropPermissionRepairing &&
        _dragDropPermissionDiagnostic is not null &&
        (_dragDropPermissionDiagnostic.HasAppCompatIssue ||
         _dragDropPermissionDiagnostic.HasStartupIssue ||
         _dragDropPermissionDiagnostic.HasShortcutIssue ||
         _dragDropPermissionDiagnostic.NeedsRelaunch);

    public void RefreshDragDropPermissionDiagnostic()
    {
        try
        {
            _dragDropPermissionDiagnostic = DragDropPermissionService.Diagnose();
            App.Log(
                "[DragDropPermission] " +
                $"issue={_dragDropPermissionDiagnostic.Issue} severity={_dragDropPermissionDiagnostic.Severity} " +
                $"process='{_dragDropPermissionDiagnostic.CurrentProcessIntegrity}' " +
                $"explorer='{_dragDropPermissionDiagnostic.ExplorerIntegrity}' " +
                $"uac='{_dragDropPermissionDiagnostic.UacStatus}' " +
                $"appCompat='{_dragDropPermissionDiagnostic.AppCompatStatus}' " +
                $"startup='{_dragDropPermissionDiagnostic.StartupStatus}'");
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Diagnose failed: {ex}");
            _dragDropPermissionDiagnostic = new DragDropPermissionDiagnostic(
                DragDropDiagnosticSeverity.Error,
                DragDropDiagnosticIssue.None,
                _localizationService.T("Settings.DragDropPermission.DiagnoseFailedSummary"),
                ex.Message,
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                _localizationService.T("Settings.DragDropPermission.Unknown"),
                false,
                false,
                false,
                false,
                false,
                false,
                false);
        }

        NotifyDragDropPermissionPropertiesChanged();
    }

    public DragDropPermissionRepairResult RepairDragDropPermission()
    {
        IsDragDropPermissionRepairing = true;
        try
        {
            var result = DragDropPermissionService.Repair(_settingsService);
            DragDropPermissionRepairStatusText = result.Success
                ? _localizationService.Format("Settings.DragDropPermission.RepairStatus", result.RepairedCount)
                : _localizationService.Format("Settings.DragDropPermission.RepairFailedStatus", result.FailureMessage);
            RefreshDragDropPermissionDiagnostic();
            return result;
        }
        finally
        {
            IsDragDropPermissionRepairing = false;
        }
    }

    private string GetDragDropPermissionSummaryText()
    {
        if (_dragDropPermissionDiagnostic is null)
        {
            return _localizationService.T("Settings.DragDropPermission.NotChecked");
        }

        return _dragDropPermissionDiagnostic.Issue switch
        {
            DragDropDiagnosticIssue.UacDisabled => _localizationService.T("Settings.DragDropPermission.Summary.UacDisabled"),
            DragDropDiagnosticIssue.PermissionMismatch => _localizationService.T("Settings.DragDropPermission.Summary.PermissionMismatch"),
            DragDropDiagnosticIssue.AppCompatIssue => _localizationService.T("Settings.DragDropPermission.Summary.AppCompatIssue"),
            DragDropDiagnosticIssue.StartupShortcutIssue => _localizationService.T("Settings.DragDropPermission.Summary.StartupShortcutIssue"),
            _ => _localizationService.T("Settings.DragDropPermission.Summary.Ok")
        };
    }

    private string GetDragDropPermissionDetailText()
    {
        if (_dragDropPermissionDiagnostic is null)
        {
            return _localizationService.T("Settings.DragDropPermission.NotCheckedDetail");
        }

        return _dragDropPermissionDiagnostic.Issue switch
        {
            DragDropDiagnosticIssue.UacDisabled => _localizationService.T("Settings.DragDropPermission.Detail.UacDisabled"),
            DragDropDiagnosticIssue.PermissionMismatch => _localizationService.T("Settings.DragDropPermission.Detail.PermissionMismatch"),
            DragDropDiagnosticIssue.AppCompatIssue => _localizationService.T("Settings.DragDropPermission.Detail.AppCompatIssue"),
            DragDropDiagnosticIssue.StartupShortcutIssue => _localizationService.T("Settings.DragDropPermission.Detail.StartupShortcutIssue"),
            _ => _localizationService.T("Settings.DragDropPermission.Detail.Ok")
        };
    }

    private void NotifyDragDropPermissionPropertiesChanged()
    {
        OnPropertyChanged(nameof(DragDropPermissionSummaryText));
        OnPropertyChanged(nameof(DragDropPermissionDetailText));
        OnPropertyChanged(nameof(DragDropPermissionSeverityKind));
        OnPropertyChanged(nameof(DragDropPermissionProcessText));
        OnPropertyChanged(nameof(DragDropPermissionExplorerText));
        OnPropertyChanged(nameof(DragDropPermissionUacText));
        OnPropertyChanged(nameof(DragDropPermissionAppCompatText));
        OnPropertyChanged(nameof(DragDropPermissionStartupText));
        OnPropertyChanged(nameof(DragDropPermissionShortcutText));
        OnPropertyChanged(nameof(CanRepairDragDropPermission));
    }
}
