using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

public sealed partial class CustomWidgetSettingsSection
{
    public event EventHandler<bool>? NavigateToAiSettingsRequested;

    private void InitializeHomeHeroAndOverviewChrome() =>
        RefreshAiSummaryCard();

    public void TriggerQuickCreate() =>
        _ = OpenWidgetStudioWindowAsync(null, null);

    public void TriggerOpenApiDocs()
    {
        if (XamlRoot is null)
        {
            return;
        }

        _ = CustomWidgetApiDocsDialogHelper.ShowAsync(XamlRoot, ActualTheme, Localization);
    }

    public void TriggerImportPackage() =>
        _ = ImportPackageFromDiskAsync();

    private void AiSettingsEntryCard_Click(object sender, RoutedEventArgs e) =>
        NavigateToAiSettingsRequested?.Invoke(this, false);

    /// <summary>
    /// AI creation lives in the main-process Widget Workshop window; Settings
    /// only asks the host to open it (for a new widget or for one widget).
    /// </summary>
    private async Task OpenWidgetStudioWindowAsync(string? widgetId, string? initialIdea)
    {
        if (_widgetActions is null || !_widgetActions.IsHostAvailable)
        {
            return;
        }

        try
        {
            await _widgetActions.OpenCustomWidgetStudioAsync(widgetId, initialIdea);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Open widget workshop failed: {ex.Message}");
            StudioStatusInfoBar.Severity = InfoBarSeverity.Error;
            StudioStatusInfoBar.Title = string.Empty;
            StudioStatusInfoBar.ActionButton = null;
            StudioStatusInfoBar.Message = ex.Message;
            StudioStatusInfoBar.IsOpen = true;
        }
    }

    private void RefreshAiSummaryCard()
    {
        if (AiSettingsEntryCard is null)
        {
            return;
        }

        try
        {
            CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
            string? readyProvider = DeskBoxAiService.Shared.ResolveActiveProviderId(prefs.PreferredAiProviderId);

            int presetConnected = CustomWidgetAgentConnectorService
                .DetectAgentClients()
                .Count(static c => c.IsConfigured);
            int customConnected = prefs.CustomAgentConfigPaths
                .Count(static p => CustomWidgetAgentConnectorService.HasMcpServerInAnyConfigFile(p));
            int totalAgents = presetConnected + customConnected;

            string primaryStatus;
            if (!string.IsNullOrWhiteSpace(readyProvider))
            {
                ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(readyProvider);
                primaryStatus = Localization.Format(
                    "Settings.AiSettings.EntryCard.ReadySummary",
                    resolved.Preset.DisplayName,
                    resolved.Model);
            }
            else
            {
                primaryStatus = Localization.T("Settings.AiSettings.EntryCard.WebFallbackSummary");
            }

            if (totalAgents > 0)
            {
                string agentSummary = Localization.Format("CustomWidget.AgentHub.EntrySummaryClients", totalAgents);
                AiSettingsEntryCard.Description = $"{primaryStatus} · {agentSummary}";
            }
            else
            {
                AiSettingsEntryCard.Description = primaryStatus;
            }
        }
        catch
        {
            AiSettingsEntryCard.Description = Localization.T("Settings.AiSettings.EntryCard.WebFallbackSummary");
        }
    }

    private static async Task OpenWidgetInExternalEditorAsync(string widgetId)
    {
        try
        {
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, widgetId);
            CustomWidgetAgentConnectorService.TryOpenWorkspaceInEditor(store.WidgetDirectory, "auto");
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Open in external editor failed: {ex.Message}");
        }
    }

    private void ShowPostCommitSuccessBanner(string widgetId, string widgetName, bool wasEdit)
    {
        var openOptionsBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = Localization.T("CustomWidget.Native.OptionsButton")
        };
        openOptionsBtn.Click += async (_, _) =>
        {
            StudioStatusInfoBar.IsOpen = false;
            await OpenWidgetDetailAsync(widgetId);
        };

        StudioStatusInfoBar.Severity = InfoBarSeverity.Success;
        StudioStatusInfoBar.Title = string.Empty;
        StudioStatusInfoBar.Message = Localization.Format(
            wasEdit ? "CustomWidget.Studio.Status.Updated" : "CustomWidget.Studio.Status.Created",
            widgetName);
        StudioStatusInfoBar.ActionButton = openOptionsBtn;
        StudioStatusInfoBar.IsOpen = true;
    }
}
