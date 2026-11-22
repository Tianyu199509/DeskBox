using System.Diagnostics.CodeAnalysis;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AiSettingsSection : UserControl
{
    private LocalizationService? _localization;
    private readonly AiProviderConfigFormController _form;
    private bool _isSectionLoaded;
    private bool _isAiMethodUiSyncing;
    private bool _isSavingInlineCredentials;
    private bool _pendingFocusAgentHub;
    private string _lastSyncedProviderId = string.Empty;
    private string _lastSyncedInlineEndpoint = string.Empty;
    private string _lastSyncedInlineModel = string.Empty;
    private McpConfigFormat _selectedMcpFormat = McpConfigFormat.StandardJson;
    private CancellationTokenSource? _inlineAiOpCts;
    private CancellationTokenSource? _bridgeSelfTestCts;

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PasswordBox))]
    public AiSettingsSection()
    {
        InitializeComponent();
        _form = new AiProviderConfigFormController(T, Format, () => ActiveCultureName);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    public event EventHandler? AiConfigurationChanged;

    private string ActiveCultureName => AiProviderConfigFormController.ResolveCultureName(_localization);
    private string T(string key) => AiProviderConfigFormController.Translate(_localization, key);
    private string Format(string key, params object[] args) => AiProviderConfigFormController.Format(_localization, key, args);

    public static string BuildEntrySummary(LocalizationService localization)
    {
        CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
        bool isAgentMcpMode = string.Equals(
            prefs.PreferredStudioChannel,
            CustomWidgetStudioPreferencesStore.ChannelAgentMcp,
            StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(prefs.PreferredAiProviderId))
        {
            CustomWidgetAiProviderPreset selectedPreset = DeskBoxAiService.FindProviderPreset(prefs.PreferredAiProviderId);
            AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(selectedPreset.Id);
            if (DeskBoxAiService.IsProviderConfigured(selectedPreset, entry))
            {
                ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(selectedPreset.Id);
                return localization.Format(
                    "Settings.AiSettings.EntrySummary.Configured",
                    resolved.Preset.DisplayName,
                    resolved.Model);
            }

            return isAgentMcpMode
                ? localization.T("CustomWidget.Workshop.Onboarding.ModeAgentTitle")
                : localization.T("CustomWidget.Workshop.Composer.ChannelDirectUnconfigured");
        }

        string? readyProviderId = DeskBoxAiService.Shared.ResolveActiveProviderId(null);
        if (!string.IsNullOrWhiteSpace(readyProviderId))
        {
            ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(readyProviderId);
            return localization.Format(
                "Settings.AiSettings.EntrySummary.Configured",
                resolved.Preset.DisplayName,
                resolved.Model);
        }

        return isAgentMcpMode
            ? localization.T("CustomWidget.Workshop.Onboarding.ModeAgentTitle")
            : localization.T("CustomWidget.Workshop.Composer.ChannelDirectUnconfigured");
    }

    public void Configure(ISettingsWidgetActions widgetActions, IntPtr ownerWindow, LocalizationService? localization = null)
    {
        _ = widgetActions;
        _ = ownerWindow;
        if (localization is not null && !ReferenceEquals(_localization, localization))
        {
            if (_localization is not null)
            {
                _localization.LanguageChanged -= OnLanguageChanged;
            }
            _localization = localization;
            if (_isSectionLoaded)
            {
                _localization.LanguageChanged -= OnLanguageChanged;
                _localization.LanguageChanged += OnLanguageChanged;
            }
        }
    }

    public void StageFocusAgentHub()
    {
        _pendingFocusAgentHub = true;
        if (_isSectionLoaded)
        {
            FocusAgentHubOnUi();
        }
    }

    public void RefreshState()
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        RefreshAiMethodSettingsUi();
        if (AgentHubHostPanel.IsExpanded)
        {
            RebuildAgentHubCards();
        }
        else
        {
            AgentHubDynamicContentPanel.Children.Clear();
        }

        if (_pendingFocusAgentHub)
        {
            FocusAgentHubOnUi();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isSectionLoaded = true;
        CustomWidgetAiCredentialStore.Shared.CheckForExternalChangesAndNotify();
        SettingsWindow.ApplyNoticeMessageTypography(AiStatusInfoBar);
        SettingsWindow.EnsureInformationalSeverityPinned(AiStatusInfoBar);
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
            _localization.LanguageChanged += OnLanguageChanged;
        }
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged -= OnAiCredentialsChanged;
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged += OnAiCredentialsChanged;
        RefreshAiMethodSettingsUi();
        _ = AutoFetchMissingModelsForSelectedProviderAsync(ResolveSelectedAiProviderId());
        if (AgentHubHostPanel.IsExpanded)
        {
            RebuildAgentHubCards();
        }
        else
        {
            AgentHubDynamicContentPanel.Children.Clear();
        }

        if (_pendingFocusAgentHub)
        {
            FocusAgentHubOnUi();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isSectionLoaded = false;
        if (Interlocked.Exchange(ref _inlineAiOpCts, null) is { } inlineCts)
        {
            try { inlineCts.Cancel(); } catch { }
        }
        if (Interlocked.Exchange(ref _bridgeSelfTestCts, null) is { } bridgeCts)
        {
            try { bridgeCts.Cancel(); } catch { }
        }
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
        }
        CustomWidgetAiCredentialStore.Shared.CredentialsChanged -= OnAiCredentialsChanged;
    }

    private void OnAiCredentialsChanged(object? sender, EventArgs e)
    {
        if (_isSavingInlineCredentials || !_isSectionLoaded)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isSectionLoaded && !_isSavingInlineCredentials)
            {
                RefreshAiMethodSettingsUi();
                if (AgentHubHostPanel.IsExpanded)
                {
                    RebuildAgentHubCards();
                }

                AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private static async Task AutoFetchMissingModelsForSelectedProviderAsync(string providerId)
    {
        try
        {
            await DeskBoxAiService.Shared.TryEnsureModelsFetchedOnceAsync(providerId);
        }
        catch
        {
        }
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Visibility != Visibility.Visible)
        {
            _inlineAiOpCts?.Cancel();
            _bridgeSelfTestCts?.Cancel();
            if (AiInlineModelComboBox.IsDropDownOpen)
            {
                AiInlineModelComboBox.IsDropDownOpen = false;
            }
        }
        else if (_isSectionLoaded)
        {
            RefreshAiMethodSettingsUi();
            if (AgentHubHostPanel.IsExpanded)
            {
                RebuildAgentHubCards();
            }
        }
    }

    private void OnLanguageChanged()
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        RefreshAiMethodSettingsUi();
        if (AgentHubHostPanel.IsExpanded)
        {
            RebuildAgentHubCards();
        }
        else
        {
            AgentHubDynamicContentPanel.Children.Clear();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => OnLanguageChanged();

    private void FocusAgentHubOnUi()
    {
        _pendingFocusAgentHub = false;
        AgentHubHostPanel.IsExpanded = true;
        RebuildAgentHubCards();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isSectionLoaded && AgentHubHostPanel.XamlRoot is not null)
            {
                AgentHubHostPanel.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = FluentMotionHelper.AreAnimationsEnabled,
                    VerticalAlignmentRatio = 0.05
                });
            }
        });
    }

    private void RefreshAiMethodSettingsUi()
    {
        CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
        string? readyProvider = DeskBoxAiService.Shared.ResolveActiveProviderId(prefs.PreferredAiProviderId);
        string preferred = !string.IsNullOrWhiteSpace(prefs.PreferredAiProviderId)
            ? prefs.PreferredAiProviderId
            : (readyProvider ?? string.Empty);

        _isAiMethodUiSyncing = true;
        try
        {
            string selectedProvider = _form.PopulateProviderCombo(AiProviderComboBox, preferred);
            SyncInlineProviderDetailsUi(selectedProvider);
        }
        finally
        {
            _isAiMethodUiSyncing = false;
        }

        if (readyProvider is null && !AiKeyExpander.IsExpanded)
        {
            AiKeyExpander.IsExpanded = true;
        }

        UpdateAiKeyCardDescription(readyProvider);
    }

    private void SyncInlineProviderDetailsUi(string providerId)
    {
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
        ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(preset.Id);
        bool isConfigured = DeskBoxAiService.IsProviderConfigured(preset, entry);

        AiInlineApiKeyPasswordBox.Visibility = preset.RequiresApiKey ? Visibility.Visible : Visibility.Collapsed;
        AiInlineApiKeyPasswordBox.FlowDirection = FlowDirection.LeftToRight;
        AiInlineApiKeyPasswordBox.Password = entry?.ApiKey ?? string.Empty;
        AiInlineApiKeyPasswordBox.PlaceholderText = preset.RequiresApiKey ? "sk-..." : string.Empty;
        AiInlineKeyCard.Header = T(preset.RequiresApiKey ? "CustomWidget.AiConfig.ApiKeyHeader" : "CustomWidget.Studio.Direct.ProviderTitle");
        AiInlineKeyCard.Description = isConfigured
            ? (preset.RequiresApiKey
                ? Format("Settings.AiSettings.KeyConfiguredHint", preset.DisplayName)
                : Format("CustomWidget.AiConfig.StatusNoKeyReady", preset.DisplayName))
            : T("CustomWidget.Settings.AiKey.DescriptionMissingShort");
        AiInlineClearKeyButton.Visibility = isConfigured ? Visibility.Visible : Visibility.Collapsed;

        IReadOnlyList<string> selectableModels = DeskBoxAiService.Shared.GetSelectableModels(preset.Id);
        AiProviderConfigFormController.PopulateModelCombo(AiInlineModelComboBox, preset, selectableModels, resolved.Model);
        _form.PopulateThinkingCombo(AiInlineThinkingComboBox, resolved.ThinkingMode, detailedLabels: false);
        _form.PopulateContextWindowCombo(AiInlineContextWindowComboBox, DeskBoxAiService.ParseContextWindowMode(entry?.ContextWindowMode));
        AiInlineContextWindowCard.Description = _form.BuildContextWindowDescription(resolved);

        AiInlineEndpointTextBox.FlowDirection = FlowDirection.LeftToRight;
        AiInlineEndpointTextBox.PlaceholderText = _form.ResolveEndpointPlaceholder(preset, entry);
        AiInlineEndpointTextBox.Text = entry?.CustomEndpoint ?? string.Empty;
        AiInlineResetEndpointButton.Visibility = resolved.UsesCustomEndpoint ? Visibility.Visible : Visibility.Collapsed;
        AiEndpointStatusCard.Description = resolved.UsesCustomEndpoint
            ? Format("CustomWidget.AiConfig.CustomUrlSummary", resolved.Endpoint)
            : Format("CustomWidget.AiConfig.DefaultUrlSummary", resolved.Endpoint);

        _lastSyncedProviderId = preset.Id;
        _lastSyncedInlineEndpoint = AiInlineEndpointTextBox.Text.Trim();
        _lastSyncedInlineModel = (AiInlineModelComboBox.Text ?? string.Empty).Trim();
    }

    private void UpdateAiKeyCardDescription(string? readyProviderId)
    {
        string selectedId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset selectedPreset = DeskBoxAiService.FindProviderPreset(selectedId);
        AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(selectedId);
        bool selectedIsConfigured = DeskBoxAiService.IsProviderConfigured(selectedPreset, entry);

        if (readyProviderId is null)
        {
            AiKeyExpander.Description = T("CustomWidget.Settings.AiKey.DescriptionMissingShort");
        }
        else
        {
            ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(readyProviderId);
            string thinkingLabel = resolved.ThinkingMode switch
            {
                AiThinkingMode.Enabled => T("CustomWidget.AiConfig.ThinkingEnabledShort"),
                AiThinkingMode.Disabled => T("CustomWidget.AiConfig.ThinkingDisabledShort"),
                _ => T("CustomWidget.AiConfig.ThinkingAutoShort")
            };
            AiKeyExpander.Description = Format("CustomWidget.AiConfig.ReadySummary", resolved.Preset.DisplayName, resolved.Model, thinkingLabel);
        }

        if (!selectedIsConfigured &&
            selectedPreset.RequiresApiKey &&
            !string.IsNullOrWhiteSpace(selectedPreset.ConsoleUrl) &&
            Uri.TryCreate(selectedPreset.ConsoleUrl, UriKind.Absolute, out Uri? keyUri))
        {
            AiGetKeyHeaderLink.NavigateUri = keyUri;
            AiGetKeyHeaderLink.Content = T("CustomWidget.AiConfig.GetApiKeyShort");
            AiGetKeyHeaderLink.Visibility = Visibility.Visible;
        }
        else
        {
            AiGetKeyHeaderLink.Visibility = Visibility.Collapsed;
        }
    }

    private string ResolveSelectedAiProviderId() => _form.ResolveSelectedProviderId(AiProviderComboBox);

    private void AiProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing)
        {
            return;
        }

        if (Interlocked.Exchange(ref _inlineAiOpCts, null) is { } prevCts)
        {
            try { prevCts.Cancel(); } catch { }
        }

        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetStudioPreferencesStore.Shared.SetPreferredAiProviderId(providerId);

        _isAiMethodUiSyncing = true;
        try
        {
            SyncInlineProviderDetailsUi(providerId);
        }
        finally
        {
            _isAiMethodUiSyncing = false;
        }

        UpdateAiKeyCardDescription(DeskBoxAiService.Shared.ResolveActiveProviderId(
            CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId));
        _ = AutoFetchMissingModelsForSelectedProviderAsync(providerId);
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PersistInlineCredentialSnapshot(string providerId)
    {
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? prev = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
        _isSavingInlineCredentials = true;
        try
        {
            _form.SaveProviderSnapshot(
                providerId,
                AiInlineApiKeyPasswordBox.Password,
                AiInlineEndpointTextBox.Text,
                AiProviderConfigFormController.ReadModelComboText(AiInlineModelComboBox, preset, prev?.FetchedModels),
                AiProviderConfigFormController.ReadThinkingWire(AiInlineThinkingComboBox),
                AiProviderConfigFormController.ReadContextWindowWire(AiInlineContextWindowComboBox, prev?.ContextWindowMode));
        }
        finally
        {
            _isSavingInlineCredentials = false;
        }

        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void AiInlineSaveAndTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string providerId = ResolveSelectedAiProviderId();
            CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
            if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(AiInlineApiKeyPasswordBox.Password))
            {
                ShowBanner(InfoBarSeverity.Warning, T("CustomWidget.Settings.AiKey.DescriptionMissingShort"));
                AiInlineApiKeyPasswordBox.Focus(FocusState.Programmatic);
                return;
            }

            PersistInlineCredentialSnapshot(providerId);
            RefreshAiMethodSettingsUi();

            var cts = new CancellationTokenSource();
            if (Interlocked.Exchange(ref _inlineAiOpCts, cts) is { } prevCts)
            {
                try { prevCts.Cancel(); } catch { }
            }

            CancellationToken token = cts.Token;

            AiInlineSaveAndTestButton.IsEnabled = false;
            AiInlineKeyCard.Description = T("CustomWidget.AiConfig.TestingInProgress");

            try
            {
                AiConnectionTestResult res = await DeskBoxAiService.Shared.TestConnectionAsync(providerId, cancellationToken: token);
                if (token.IsCancellationRequested ||
                    !_isSectionLoaded ||
                    !string.Equals(providerId, ResolveSelectedAiProviderId(), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (res.Success)
                {
                    string msg = _form.FormatTestSuccess(res);
                    AiInlineKeyCard.Description = "✓ " + msg;
                    ShowBanner(InfoBarSeverity.Success, msg);
                }
                else
                {
                    string err = _form.FormatTestFailure(res);
                    AiInlineKeyCard.Description = err;
                    ShowBanner(InfoBarSeverity.Error, err);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    AiInlineKeyCard.Description = ex.Message;
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _inlineAiOpCts, null, cts);
                try { cts.Dispose(); } catch { }
                AiInlineSaveAndTestButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[AiSettings] Save and test failed: {ex.Message}");
        }
    }

    private void AiInlineApiKeyPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && AiInlineSaveAndTestButton.IsEnabled)
        {
            e.Handled = true;
            AiInlineSaveAndTestButton_Click(AiInlineSaveAndTestButton, new RoutedEventArgs());
        }
    }

    private void AiInlineClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        string providerId = ResolveSelectedAiProviderId();
        _isSavingInlineCredentials = true;
        try
        {
            AiProviderConfigFormController.ClearOrDisableProvider(providerId);
        }
        finally
        {
            _isSavingInlineCredentials = false;
        }

        AiInlineApiKeyPasswordBox.Password = string.Empty;
        RefreshAiMethodSettingsUi();
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AiInlineEndpointTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isAiMethodUiSyncing)
        {
            return;
        }

        string trimmedEndpoint = AiInlineEndpointTextBox.Text.Trim();
        string providerId = ResolveSelectedAiProviderId();
        if (string.Equals(providerId, _lastSyncedProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(trimmedEndpoint, _lastSyncedInlineEndpoint, StringComparison.Ordinal))
        {
            return;
        }

        _isSavingInlineCredentials = true;
        try
        {
            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(providerId, customEndpoint: trimmedEndpoint);
        }
        finally
        {
            _isSavingInlineCredentials = false;
        }

        RefreshAiMethodSettingsUi();
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AiInlineResetEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        AiInlineEndpointTextBox.Text = string.Empty;
        AiInlineEndpointTextBox_LostFocus(sender, e);
    }

    private void SaveInlineModelChoice(string? explicitModel = null)
    {
        if (_isAiMethodUiSyncing)
        {
            return;
        }

        if (Interlocked.Exchange(ref _inlineAiOpCts, null) is { } prevCts)
        {
            try { prevCts.Cancel(); } catch { }
        }

        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        string targetModel = !string.IsNullOrWhiteSpace(explicitModel)
            ? explicitModel.Trim()
            : AiProviderConfigFormController.ReadModelComboText(
                AiInlineModelComboBox,
                preset,
                DeskBoxAiService.Shared.GetSelectableModels(providerId));

        _isSavingInlineCredentials = true;
        try
        {
            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(providerId, customModel: targetModel);
        }
        finally
        {
            _isSavingInlineCredentials = false;
        }

        SyncInlineModelAndThinkingFromResolved(providerId);
        UpdateAiKeyCardDescription(DeskBoxAiService.Shared.ResolveActiveProviderId(
            CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId));
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncInlineModelAndThinkingFromResolved(string providerId)
    {
        ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(providerId);
        _isAiMethodUiSyncing = true;
        try
        {
            AiProviderConfigFormController.PopulateModelCombo(
                AiInlineModelComboBox,
                resolved.Preset,
                DeskBoxAiService.Shared.GetSelectableModels(providerId),
                resolved.Model);
            AiInlineThinkingComboBox.SelectedIndex = resolved.ThinkingMode switch
            {
                AiThinkingMode.Enabled => 1,
                AiThinkingMode.Disabled => 2,
                _ => 0
            };
            _lastSyncedProviderId = resolved.Preset.Id;
            _lastSyncedInlineModel = (AiInlineModelComboBox.Text ?? string.Empty).Trim();
        }
        finally
        {
            _isAiMethodUiSyncing = false;
        }
    }

    private void AiInlineModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing || AiInlineModelComboBox.SelectedItem is not string selectedModel)
        {
            return;
        }

        _isAiMethodUiSyncing = true;
        try
        {
            AiInlineModelComboBox.Text = selectedModel;
        }
        finally
        {
            _isAiMethodUiSyncing = false;
        }

        SaveInlineModelChoice(selectedModel);
    }

    private void AiInlineModelComboBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (_isAiMethodUiSyncing)
        {
            return;
        }

        string submitted = args.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(submitted))
        {
            SaveInlineModelChoice(submitted);
        }
    }

    private void AiInlineModelComboBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isAiMethodUiSyncing)
        {
            return;
        }

        string currentModel = (AiInlineModelComboBox.Text ?? string.Empty).Trim();
        string providerId = ResolveSelectedAiProviderId();
        if (string.Equals(providerId, _lastSyncedProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentModel, _lastSyncedInlineModel, StringComparison.Ordinal))
        {
            return;
        }

        SaveInlineModelChoice();
    }

    private void AiInlineThinkingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing ||
            (AiInlineThinkingComboBox.SelectedItem as ComboBoxItem)?.Tag is not string modeWire)
        {
            return;
        }

        string providerId = ResolveSelectedAiProviderId();
        _isSavingInlineCredentials = true;
        try
        {
            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(providerId, thinkingMode: modeWire);
        }
        finally
        {
            _isSavingInlineCredentials = false;
        }

        SyncInlineModelAndThinkingFromResolved(providerId);
        UpdateAiKeyCardDescription(DeskBoxAiService.Shared.ResolveActiveProviderId(
            CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId));
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AiInlineContextWindowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing ||
            (AiInlineContextWindowComboBox.SelectedItem as ComboBoxItem)?.Tag is not string contextWire)
        {
            return;
        }

        string providerId = ResolveSelectedAiProviderId();
        _isSavingInlineCredentials = true;
        try
        {
            CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(providerId, contextWindowMode: contextWire);
        }
        finally
        {
            _isSavingInlineCredentials = false;
        }

        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void AiInlineFetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string providerId = ResolveSelectedAiProviderId();
            CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
            if (!preset.RequiresApiKey || !string.IsNullOrWhiteSpace(AiInlineApiKeyPasswordBox.Password))
            {
                PersistInlineCredentialSnapshot(providerId);
            }

            AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(providerId);
            if (!DeskBoxAiService.IsProviderConfigured(preset, entry))
            {
                ShowBanner(InfoBarSeverity.Warning, T("CustomWidget.Settings.AiKey.DescriptionMissingShort"));
                AiInlineApiKeyPasswordBox.Focus(FocusState.Programmatic);
                return;
            }

            var cts = new CancellationTokenSource();
            if (Interlocked.Exchange(ref _inlineAiOpCts, cts) is { } prevCts)
            {
                try { prevCts.Cancel(); } catch { }
            }

            CancellationToken token = cts.Token;

            AiInlineFetchModelsButton.IsEnabled = false;
            try
            {
                AiModelListResult res = await DeskBoxAiService.Shared.FetchAvailableModelsAsync(
                    providerId,
                    cancellationToken: token,
                    persistDiscoveredModels: false);
                if (token.IsCancellationRequested ||
                    !_isSectionLoaded ||
                    !string.Equals(providerId, ResolveSelectedAiProviderId(), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (res.Success && res.Models.Count > 0)
                {
                    _isSavingInlineCredentials = true;
                    try
                    {
                        CustomWidgetAiCredentialStore.Shared.UpdateFetchedModels(preset.Id, res.Models);
                    }
                    finally
                    {
                        _isSavingInlineCredentials = false;
                    }

                    RefreshAiMethodSettingsUi();
                    if (Visibility == Visibility.Visible &&
                        AiKeyExpander.IsExpanded &&
                        AiInlineModelComboBox.IsLoaded &&
                        AiInlineModelComboBox.XamlRoot is not null)
                    {
                        AiInlineModelComboBox.IsDropDownOpen = true;
                    }

                    AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
                    ShowBanner(InfoBarSeverity.Success, _form.FormatFetchSuccess(res.Models.Count));
                }
                else
                {
                    ShowBanner(InfoBarSeverity.Warning, _form.FormatFetchFailure(res));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (_isSectionLoaded)
                {
                    ShowBanner(InfoBarSeverity.Error, ex.Message);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _inlineAiOpCts, null, cts);
                try { cts.Dispose(); } catch { }
                AiInlineFetchModelsButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[AiSettings] Fetch models failed: {ex.Message}");
        }
    }

    private void ExternalAgentExpander_Expanded(object sender, EventArgs e)
    {
        if (_isSectionLoaded && AgentHubDynamicContentPanel is { Children.Count: 0 })
        {
            RebuildAgentHubCards();
        }
    }

    private void RebuildAgentHubCards()
    {
        if (!_isSectionLoaded || AgentHubDynamicContentPanel is null)
        {
            return;
        }

        var bridgeCts = new CancellationTokenSource();
        if (Interlocked.Exchange(ref _bridgeSelfTestCts, bridgeCts) is { } prevBridgeCts)
        {
            try { prevBridgeCts.Cancel(); } catch { }
        }

        AgentHubDynamicContentPanel.Children.Clear();
        McpQuickConnectPanelBuilder.PopulatePanel(
            AgentHubDynamicContentPanel,
            ActualTheme,
            T,
            Format,
            ActiveCultureName,
            _selectedMcpFormat,
            fmt => _selectedMcpFormat = fmt,
            includeAutoSetupPromptCard: false,
            onSelfTestBanner: (ok, msg) => ShowBanner(ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning, msg),
            bridgeCts.Token);
    }

    private async void CopyMcpSetupPromptButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string prompt = CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(ActiveCultureName);
            McpQuickConnectPanelBuilder.CopyTextToClipboard(prompt);
            string original = T("CustomWidget.AgentHub.Playbook.CopyCreate");
            await McpQuickConnectPanelBuilder.FlashCopiedButtonAsync(
                CopyMcpSetupPromptButton,
                original,
                T("CustomWidget.Native.Dialog.CopiedButton"));
        }
        catch (Exception ex)
        {
            App.Log($"[AiSettings] Copy MCP setup prompt failed: {ex.Message}");
        }
    }

    private async void OpenMcpSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Uri.TryCreate(CustomWidgetAgentConnectorService.PublicGuideUrl, UriKind.Absolute, out Uri? uri))
            {
                _ = await Launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[AiSettings] Open guide failed: {ex.Message}");
        }
    }

    private void ShowBanner(InfoBarSeverity severity, string message, Button? actionButton = null)
    {
        AiStatusInfoBar.Severity = severity;
        AiStatusInfoBar.Title = string.Empty;
        AiStatusInfoBar.ActionButton = actionButton;
        AiStatusInfoBar.Message = message;
        AiStatusInfoBar.IsOpen = true;
    }
}
