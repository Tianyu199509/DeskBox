using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AiSettingsSection : UserControl
{
    private ISettingsWidgetActions? _widgetActions;
    private IntPtr _ownerWindow;
    private bool _isSectionLoaded;
    private bool _isAiMethodUiSyncing;
    private bool _pendingFocusAgentHub;
    private McpConfigFormat _selectedMcpFormat = McpConfigFormat.StandardJson;
    private CancellationTokenSource? _inlineAiOpCts;
    private CancellationTokenSource? _bridgeSelfTestCts;
    private FontFamily? _monospaceFont;

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PasswordBox))]
    public AiSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    public event EventHandler? AiConfigurationChanged;

    private LocalizationService Localization => App.Current.LocalizationService;

    public static string BuildEntrySummary(LocalizationService localization)
    {
        CustomWidgetStudioPreferencesStore prefs = CustomWidgetStudioPreferencesStore.Shared;
        string culture = localization.CurrentCultureName;
        string? readyProviderId = DeskBoxAiService.Shared.ResolveActiveProviderId(prefs.PreferredAiProviderId);
        if (!string.IsNullOrWhiteSpace(readyProviderId))
        {
            ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(readyProviderId);
            return localization.Format(
                "Settings.AiSettings.EntrySummary.Configured",
                resolved.Preset.DisplayName,
                resolved.Model);
        }

        string webTargetId = prefs.ResolveEffectiveWebAiTarget(culture);
        string webDisplayName = CustomWidgetStudioPreferencesStore
            .GetOrderedWebAiTargets(culture, includeCopyOnly: false)
            .FirstOrDefault(target => string.Equals(target.Id, webTargetId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? webTargetId;
        return localization.Format("Settings.AiSettings.EntrySummary.WebAi", webDisplayName);
    }

    public void Configure(ISettingsWidgetActions widgetActions, IntPtr ownerWindow)
    {
        _widgetActions = widgetActions;
        _ownerWindow = ownerWindow;
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
        SettingsWindow.ApplyNoticeMessageTypography(AiStatusInfoBar);
        Localization.LanguageChanged -= OnLanguageChanged;
        Localization.LanguageChanged += OnLanguageChanged;
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isSectionLoaded = false;
        _inlineAiOpCts?.Cancel();
        _bridgeSelfTestCts?.Cancel();
        Localization.LanguageChanged -= OnLanguageChanged;
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

    private void OnActualThemeChanged(FrameworkElement sender, object args)
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
        string culture = Localization.CurrentCultureName;
        IReadOnlyList<CustomWidgetAiProviderPreset> providers =
            DeskBoxAiService.GetOrderedProvidersForCulture(culture);
        IReadOnlyList<CustomWidgetAiProviderPreset> configured =
            DeskBoxAiService.Shared.GetConfiguredProviders();
        var configuredIds = new HashSet<string>(configured.Select(static c => c.Id), StringComparer.OrdinalIgnoreCase);

        string? readyProvider = DeskBoxAiService.Shared.ResolveActiveProviderId(prefs.PreferredAiProviderId);
        string selectedProvider = readyProvider ??
            (string.IsNullOrWhiteSpace(prefs.PreferredAiProviderId)
                ? DeskBoxAiService.GetDefaultStarterProviderId(culture)
                : prefs.PreferredAiProviderId);

        _isAiMethodUiSyncing = true;
        try
        {
            AiProviderComboBox.Items.Clear();
            int selectedIndex = 0;
            for (int i = 0; i < providers.Count; i++)
            {
                bool hasKey = configuredIds.Contains(providers[i].Id);
                AiProviderComboBox.Items.Add(new ComboBoxItem
                {
                    Content = hasKey ? $"{providers[i].DisplayName}  ✓" : providers[i].DisplayName,
                    Tag = providers[i].Id
                });
                if (string.Equals(providers[i].Id, selectedProvider, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                }
            }

            AiProviderComboBox.SelectedIndex = selectedIndex;

            string currentTarget = prefs.ResolveEffectiveWebAiTarget(culture);
            WebAiTargetComboBox.Items.Clear();
            int targetIndex = 0;
            foreach (WebAiTargetPreset target in CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets(culture, includeCopyOnly: false))
            {
                if (string.Equals(target.Id, currentTarget, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = WebAiTargetComboBox.Items.Count;
                }

                WebAiTargetComboBox.Items.Add(new ComboBoxItem { Content = target.DisplayName, Tag = target.Id });
            }

            WebAiTargetComboBox.SelectedIndex = targetIndex;

            SyncInlineProviderDetailsUi(selectedProvider);
        }
        finally
        {
            _isAiMethodUiSyncing = false;
        }

        if (readyProvider is null && !AiKeyExpander.IsExpanded)
        {
            // Guide unconfigured users directly to the inline API key input;
            // keep collapsed with a clean summary once configured.
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
        AiInlineApiKeyPasswordBox.PlaceholderText = preset.RequiresApiKey
            ? "sk-..."
            : string.Empty;
        AiInlineKeyCard.Header = Localization.T(preset.RequiresApiKey
            ? "CustomWidget.AiConfig.ApiKeyHeader"
            : "CustomWidget.Studio.Direct.ProviderTitle");
        AiInlineKeyCard.Description = isConfigured
            ? (preset.RequiresApiKey
                ? Localization.Format("Settings.AiSettings.KeyConfiguredHint", preset.DisplayName)
                : Localization.Format("CustomWidget.AiConfig.StatusNoKeyReady", preset.DisplayName))
            : Localization.T("CustomWidget.Settings.AiKey.DescriptionMissingShort");
        AiInlineClearKeyButton.Visibility = isConfigured ? Visibility.Visible : Visibility.Collapsed;

        string activeModel = !string.IsNullOrWhiteSpace(entry?.CustomModel)
            ? entry!.CustomModel.Trim()
            : preset.DefaultModel;

        AiInlineModelComboBox.PlaceholderText = preset.DefaultModel;
        AiInlineModelComboBox.Items.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeModel) && seen.Add(activeModel))
        {
            AiInlineModelComboBox.Items.Add(activeModel);
        }
        foreach (string model in preset.Models)
        {
            if (!string.IsNullOrWhiteSpace(model) && seen.Add(model))
            {
                AiInlineModelComboBox.Items.Add(model);
            }
        }
        AiInlineModelComboBox.SelectedItem = activeModel;
        AiInlineModelComboBox.Text = activeModel;

        AiInlineThinkingComboBox.Items.Clear();
        AiInlineThinkingComboBox.Items.Add(new ComboBoxItem { Content = Localization.T("CustomWidget.AiConfig.ThinkingAutoShort"), Tag = "auto" });
        AiInlineThinkingComboBox.Items.Add(new ComboBoxItem { Content = Localization.T("CustomWidget.AiConfig.ThinkingEnabledShort"), Tag = "enabled" });
        AiInlineThinkingComboBox.Items.Add(new ComboBoxItem { Content = Localization.T("CustomWidget.AiConfig.ThinkingDisabledShort"), Tag = "disabled" });
        AiThinkingMode mode = DeskBoxAiService.ParseThinkingMode(entry?.ThinkingMode);
        AiInlineThinkingComboBox.SelectedIndex = mode switch
        {
            AiThinkingMode.Enabled => 1,
            AiThinkingMode.Disabled => 2,
            _ => 0
        };

        bool isGlobalEndpointDefault = string.Equals(entry?.Region, "global", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(entry?.Region) &&
             !DeskBoxAiService.IsSimplifiedChineseRegion(Localization.CurrentCultureName));
        AiInlineEndpointTextBox.FlowDirection = FlowDirection.LeftToRight;
        AiInlineEndpointTextBox.PlaceholderText = isGlobalEndpointDefault && !string.IsNullOrWhiteSpace(preset.GlobalDefaultEndpoint)
            ? preset.GlobalDefaultEndpoint!
            : preset.DefaultEndpoint;
        AiInlineEndpointTextBox.Text = entry?.CustomEndpoint ?? string.Empty;
        AiInlineResetEndpointButton.Visibility = resolved.UsesCustomEndpoint
            ? Visibility.Visible
            : Visibility.Collapsed;
        AiEndpointStatusCard.Description = resolved.UsesCustomEndpoint
            ? Localization.Format("CustomWidget.AiConfig.CustomUrlSummary", resolved.Endpoint)
            : Localization.Format("CustomWidget.AiConfig.DefaultUrlSummary", resolved.Endpoint);
    }

    private void UpdateAiKeyCardDescription(string? readyProviderId)
    {
        string selectedId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset selectedPreset = DeskBoxAiService.FindProviderPreset(selectedId);
        AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(selectedId);
        bool selectedIsConfigured = DeskBoxAiService.IsProviderConfigured(selectedPreset, entry);

        if (readyProviderId is null)
        {
            AiKeyExpander.Description = Localization.T("CustomWidget.Settings.AiKey.DescriptionMissingShort");
        }
        else
        {
            ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(readyProviderId);
            string thinkingLabel = resolved.ThinkingMode switch
            {
                AiThinkingMode.Enabled => Localization.T("CustomWidget.AiConfig.ThinkingEnabledShort"),
                AiThinkingMode.Disabled => Localization.T("CustomWidget.AiConfig.ThinkingDisabledShort"),
                _ => Localization.T("CustomWidget.AiConfig.ThinkingAutoShort")
            };
            AiKeyExpander.Description = Localization.Format(
                "CustomWidget.AiConfig.ReadySummary",
                resolved.Preset.DisplayName,
                resolved.Model,
                thinkingLabel);
        }

        if (!selectedIsConfigured &&
            selectedPreset.RequiresApiKey &&
            !string.IsNullOrWhiteSpace(selectedPreset.ConsoleUrl) &&
            Uri.TryCreate(selectedPreset.ConsoleUrl, UriKind.Absolute, out Uri? keyUri))
        {
            AiGetKeyHeaderLink.NavigateUri = keyUri;
            AiGetKeyHeaderLink.Content = Localization.T("CustomWidget.AiConfig.GetApiKeyShort");
            AiGetKeyHeaderLink.Visibility = Visibility.Visible;
        }
        else
        {
            AiGetKeyHeaderLink.Visibility = Visibility.Collapsed;
        }
    }

    private string ResolveSelectedAiProviderId() =>
        (AiProviderComboBox.SelectedItem as ComboBoxItem)?.Tag as string
        ?? DeskBoxAiService.GetDefaultStarterProviderId(Localization.CurrentCultureName);

    private void AiProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing)
        {
            return;
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
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PersistInlineCredentialSnapshot(string providerId)
    {
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? prev = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
        string defaultRegion = DeskBoxAiService.IsSimplifiedChineseRegion(Localization.CurrentCultureName)
            ? "cn"
            : "global";
        string trimmedKey = AiInlineApiKeyPasswordBox.Password.Trim();
        string customEndpoint = AiInlineEndpointTextBox.Text.Trim();
        string chosenModel = AiInlineModelComboBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chosenModel) && AiInlineModelComboBox.SelectedItem is string s)
        {
            chosenModel = s.Trim();
        }

        string customModel = string.IsNullOrWhiteSpace(chosenModel) ||
                             string.Equals(chosenModel, preset.DefaultModel, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : chosenModel;
        string thinkingWire = (AiInlineThinkingComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        bool enabledState = preset.RequiresApiKey ? !string.IsNullOrWhiteSpace(trimmedKey) : true;

        CustomWidgetAiCredentialStore.Shared.Upsert(new AiProviderCredentialEntry(
            ProviderId: preset.Id,
            ApiKey: trimmedKey,
            Region: prev?.Region ?? defaultRegion,
            PlanTier: prev?.PlanTier ?? "pro",
            Enabled: enabledState,
            CustomEndpoint: customEndpoint,
            CustomModel: customModel,
            ThinkingMode: thinkingWire));

        if (enabledState)
        {
            CustomWidgetStudioPreferencesStore.Shared.SetPreferredAiProviderId(preset.Id);
        }

        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void AiInlineSaveAndTestButton_Click(object sender, RoutedEventArgs e)
    {
        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(AiInlineApiKeyPasswordBox.Password))
        {
            ShowBanner(
                InfoBarSeverity.Warning,
                Localization.T("CustomWidget.Settings.AiKey.DescriptionMissingShort"));
            AiInlineApiKeyPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        PersistInlineCredentialSnapshot(providerId);
        RefreshAiMethodSettingsUi();

        _inlineAiOpCts?.Cancel();
        _inlineAiOpCts?.Dispose();
        var cts = new CancellationTokenSource();
        _inlineAiOpCts = cts;

        AiInlineSaveAndTestButton.IsEnabled = false;
        AiInlineKeyCard.Description = Localization.T("CustomWidget.AiConfig.TestingInProgress");

        try
        {
            AiConnectionTestResult res = await DeskBoxAiService.Shared.TestConnectionAsync(
                providerId,
                cancellationToken: cts.Token);
            if (cts.IsCancellationRequested || !_isSectionLoaded)
            {
                return;
            }

            if (res.Success)
            {
                string msg = Localization.Format("CustomWidget.AiConfig.TestSuccess", res.LatencyMs, res.EffectiveModel);
                AiInlineKeyCard.Description = "✓ " + msg;
                ShowBanner(InfoBarSeverity.Success, msg);
            }
            else
            {
                string err = res.ErrorMessage ?? Localization.T("CustomWidget.AiConfig.TestFailed");
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
            AiInlineSaveAndTestButton.IsEnabled = true;
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
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        AiProviderCredentialEntry? prev = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
        if (preset.RequiresApiKey)
        {
            CustomWidgetAiCredentialStore.Shared.RemoveKey(preset.Id);
        }
        else if (prev is not null)
        {
            CustomWidgetAiCredentialStore.Shared.Upsert(prev with { Enabled = false, ApiKey = string.Empty });
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

        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(
            providerId,
            customEndpoint: AiInlineEndpointTextBox.Text.Trim());
        ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(providerId);
        AiInlineResetEndpointButton.Visibility = resolved.UsesCustomEndpoint
            ? Visibility.Visible
            : Visibility.Collapsed;
        AiEndpointStatusCard.Description = resolved.UsesCustomEndpoint
            ? Localization.Format("CustomWidget.AiConfig.CustomUrlSummary", resolved.Endpoint)
            : Localization.Format("CustomWidget.AiConfig.DefaultUrlSummary", resolved.Endpoint);
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AiInlineResetEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        AiInlineEndpointTextBox.Text = string.Empty;
        AiInlineEndpointTextBox_LostFocus(sender, e);
    }

    private void SaveInlineModelChoice()
    {
        if (_isAiMethodUiSyncing)
        {
            return;
        }

        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        string chosen = AiInlineModelComboBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(chosen) && AiInlineModelComboBox.SelectedItem is string s)
        {
            chosen = s.Trim();
        }

        string customModel = string.IsNullOrWhiteSpace(chosen) ||
                             string.Equals(chosen, preset.DefaultModel, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : chosen;

        CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(providerId, customModel: customModel);
        UpdateAiKeyCardDescription(DeskBoxAiService.Shared.ResolveActiveProviderId(
            CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId));
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AiInlineModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing || AiInlineModelComboBox.SelectedItem is not string selectedModel)
        {
            return;
        }

        AiInlineModelComboBox.Text = selectedModel;
        SaveInlineModelChoice();
    }

    private void AiInlineModelComboBox_LostFocus(object sender, RoutedEventArgs e) =>
        SaveInlineModelChoice();

    private void AiInlineThinkingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing ||
            (AiInlineThinkingComboBox.SelectedItem as ComboBoxItem)?.Tag is not string modeWire)
        {
            return;
        }

        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiCredentialStore.Shared.UpdateStudioSettings(providerId, thinkingMode: modeWire);
        UpdateAiKeyCardDescription(DeskBoxAiService.Shared.ResolveActiveProviderId(
            CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId));
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void AiInlineFetchModelsButton_Click(object sender, RoutedEventArgs e)
    {
        string providerId = ResolveSelectedAiProviderId();
        CustomWidgetAiProviderPreset preset = DeskBoxAiService.FindProviderPreset(providerId);
        if (preset.RequiresApiKey && !string.IsNullOrWhiteSpace(AiInlineApiKeyPasswordBox.Password))
        {
            PersistInlineCredentialSnapshot(providerId);
        }

        AiProviderCredentialEntry? entry = CustomWidgetAiCredentialStore.Shared.Get(providerId);
        if (!DeskBoxAiService.IsProviderConfigured(preset, entry))
        {
            ShowBanner(
                InfoBarSeverity.Warning,
                Localization.T("CustomWidget.Settings.AiKey.DescriptionMissingShort"));
            AiInlineApiKeyPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        _inlineAiOpCts?.Cancel();
        _inlineAiOpCts?.Dispose();
        var cts = new CancellationTokenSource();
        _inlineAiOpCts = cts;

        AiInlineFetchModelsButton.IsEnabled = false;
        try
        {
            AiModelListResult res = await DeskBoxAiService.Shared.FetchAvailableModelsAsync(
                providerId,
                cancellationToken: cts.Token);
            if (cts.IsCancellationRequested || !_isSectionLoaded)
            {
                return;
            }

            if (res.Success && res.Models.Count > 0)
            {
                _isAiMethodUiSyncing = true;
                try
                {
                    string current = AiInlineModelComboBox.Text?.Trim() ?? string.Empty;
                    AiInlineModelComboBox.Items.Clear();
                    foreach (string m in res.Models)
                    {
                        AiInlineModelComboBox.Items.Add(m);
                    }

                    string chosen = res.Models.Contains(current, StringComparer.OrdinalIgnoreCase)
                        ? current
                        : res.Models[0];
                    AiInlineModelComboBox.SelectedItem = chosen;
                    AiInlineModelComboBox.Text = chosen;
                }
                finally
                {
                    _isAiMethodUiSyncing = false;
                }

                SaveInlineModelChoice();
                if (Visibility == Visibility.Visible &&
                    AiKeyExpander.IsExpanded &&
                    AiInlineModelComboBox.IsLoaded &&
                    AiInlineModelComboBox.XamlRoot is not null)
                {
                    AiInlineModelComboBox.IsDropDownOpen = true;
                }

                ShowBanner(
                    InfoBarSeverity.Success,
                    Localization.Format("CustomWidget.AiConfig.FetchModelsSuccess", res.Models.Count));
            }
            else
            {
                ShowBanner(
                    InfoBarSeverity.Warning,
                    res.ErrorMessage ?? Localization.T("CustomWidget.AiConfig.FetchModelsFailed"));
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
            AiInlineFetchModelsButton.IsEnabled = true;
        }
    }

    private void WebAiTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isAiMethodUiSyncing ||
            (WebAiTargetComboBox.SelectedItem as ComboBoxItem)?.Tag is not string targetId)
        {
            return;
        }

        CustomWidgetStudioPreferencesStore.Shared.SetPreferredWebAiTarget(targetId);
        AiConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── External Coding Agents (MCP / CLI) ──────────────────────────────────

    private void ExternalAgentExpander_Expanded(object sender, EventArgs e)
    {
        if (!_isSectionLoaded || AgentHubDynamicContentPanel is null)
        {
            return;
        }

        if (AgentHubDynamicContentPanel.Children.Count == 0)
        {
            RebuildAgentHubCards();
        }
    }

    private void RebuildAgentHubCards()
    {
        _bridgeSelfTestCts?.Cancel();
        _bridgeSelfTestCts?.Dispose();
        _bridgeSelfTestCts = new CancellationTokenSource();
        CancellationToken bridgeCt = _bridgeSelfTestCts.Token;

        AgentHubDynamicContentPanel.Children.Clear();
        BuildManualMcpConfigPanel(AgentHubDynamicContentPanel, bridgeCt);
    }

    private async void CopyMcpSetupPromptButton_Click(object sender, RoutedEventArgs e)
    {
        string prompt = CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(Localization.CurrentCultureName);
        CopyToClipboard(prompt);
        await FlashCopiedButtonFeedbackAsync(
            CopyMcpSetupPromptButton,
            Localization.T("CustomWidget.AgentHub.Playbook.CopyCreate"));
    }

    private async void OpenMcpSetupGuideButton_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(CustomWidgetAgentConnectorService.PublicGuideUrl, UriKind.Absolute, out Uri? uri))
        {
            _ = await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void BuildManualMcpConfigPanel(StackPanel container, CancellationToken bridgeCt)
    {
        string cmdLine = CustomWidgetAgentConnectorService.FormatMcpCommandLine();
        var cmdBox = CreateMonospaceReadOnlyBox(cmdLine, minHeight: 36, maxHeight: 60);
        var copyCmdBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Content = Localization.T("CustomWidget.AgentHub.CopyMcpConfig")
        };
        copyCmdBtn.Click += async (_, _) =>
        {
            CopyToClipboard(cmdLine);
            await FlashCopiedButtonFeedbackAsync(
                copyCmdBtn,
                Localization.T("CustomWidget.AgentHub.CopyMcpConfig"));
        };
        container.Children.Add(CreateInlineHeaderWithAction(cmdBox, copyCmdBtn));

        var formatCombo = new ComboBox
        {
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        (McpConfigFormat Format, string LabelKey)[] formats =
        [
            (McpConfigFormat.StandardJson, "CustomWidget.AgentHub.Format.StandardJson"),
            (McpConfigFormat.VsCodeJson, "CustomWidget.AgentHub.Format.VsCodeJson"),
            (McpConfigFormat.CodexToml, "CustomWidget.AgentHub.Format.CodexToml"),
            (McpConfigFormat.OpenCodeJson, "CustomWidget.AgentHub.Format.OpenCodeJson"),
            (McpConfigFormat.DeepSeekHarnessYaml, "CustomWidget.AgentHub.Format.DeepSeekHarnessYaml"),
            (McpConfigFormat.CliRegistration, "CustomWidget.AgentHub.Format.CliRegistration")
        ];

        int selectedIdx = 0;
        for (int i = 0; i < formats.Length; i++)
        {
            formatCombo.Items.Add(new ComboBoxItem
            {
                Content = Localization.T(formats[i].LabelKey),
                Tag = formats[i].Format
            });
            if (formats[i].Format == _selectedMcpFormat)
            {
                selectedIdx = i;
            }
        }
        formatCombo.SelectedIndex = selectedIdx;

        string currentSnippetText = CustomWidgetAgentConnectorService.BuildMcpSnippet(_selectedMcpFormat);
        var snippetBox = CreateMonospaceReadOnlyBox(
            currentSnippetText,
            minHeight: 88,
            maxHeight: 168);

        formatCombo.SelectionChanged += (_, _) =>
        {
            if ((formatCombo.SelectedItem as ComboBoxItem)?.Tag is McpConfigFormat fmt)
            {
                _selectedMcpFormat = fmt;
                currentSnippetText = CustomWidgetAgentConnectorService.BuildMcpSnippet(fmt);
                snippetBox.Text = currentSnippetText;
            }
        };

        var copySnippetBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = Localization.T("CustomWidget.AgentHub.CopyMcpConfig")
        };
        copySnippetBtn.Click += async (_, _) =>
        {
            CopyToClipboard(currentSnippetText);
            await FlashCopiedButtonFeedbackAsync(
                copySnippetBtn,
                Localization.T("CustomWidget.AgentHub.CopyMcpConfig"));
        };

        container.Children.Add(CreateInlineHeaderWithAction(formatCombo, copySnippetBtn));
        container.Children.Add(snippetBox);

        var diagToolbar = new WrapFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
        var selfTestBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = Localization.T("CustomWidget.AgentHub.SelfTestButton")
        };
        var diagStatusText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray)
        };
        selfTestBtn.Click += async (_, _) =>
            await RunBridgeDiagnosticsAsync(selfTestBtn, diagStatusText, bridgeCt);
        diagToolbar.Children.Add(selfTestBtn);

        IReadOnlyList<DeskBoxAgentCliEntry.McpToolSummary> tools = DeskBoxAgentCliEntry.GetMcpToolSummaries();
        diagToolbar.Children.Add(new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = Localization.Format("CustomWidget.AgentHub.McpToolsExpanderHeader", tools.Count),
            Flyout = BuildMcpToolsFlyout(tools)
        });
        diagToolbar.Children.Add(diagStatusText);
        container.Children.Add(diagToolbar);
    }

    private Flyout BuildMcpToolsFlyout(IReadOnlyList<DeskBoxAgentCliEntry.McpToolSummary> tools)
    {
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };
        flyout.Opening += (_, _) =>
        {
            if (flyout.Content is not null)
            {
                return;
            }

            Style? descriptionStyle = Resources.TryGetValue("SettingDescriptionTextStyle", out object? styleObj)
                ? styleObj as Style
                : null;
            FontFamily monoFont = GetMonospaceFont();
            var stack = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 380
            };

            foreach (DeskBoxAgentCliEntry.McpToolSummary tool in tools)
            {
                var itemStack = new StackPanel { Spacing = 2 };
                itemStack.Children.Add(new TextBlock
                {
                    Text = tool.Name,
                    FontFamily = monoFont,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    IsTextSelectionEnabled = true
                });
                var descBlock = new TextBlock
                {
                    Text = Localization.T(tool.DescriptionKey),
                    TextWrapping = TextWrapping.Wrap
                };
                if (descriptionStyle is not null)
                {
                    descBlock.Style = descriptionStyle;
                }

                itemStack.Children.Add(descBlock);
                stack.Children.Add(itemStack);
            }

            flyout.Content = new ScrollViewer
            {
                MaxHeight = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        };
        return flyout;
    }

    private async Task RunBridgeDiagnosticsAsync(
        Button selfTestBtn,
        TextBlock statusText,
        CancellationToken cancellationToken)
    {
        selfTestBtn.IsEnabled = false;
        try
        {
            AgentBridgeSelfTestResult res = await CustomWidgetAgentConnectorService.RunBridgeSelfTestAsync(
                cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested || !_isSectionLoaded)
            {
                return;
            }

            bool healthy = res.McpProtocolOk && res.PipeOnline;
            statusText.Foreground = healthy
                ? ResolveBrush("SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x10, 0x7C, 0x10))
                : ResolveBrush("SystemFillColorCautionBrush", Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));
            statusText.Text = healthy
                ? Localization.Format("CustomWidget.AgentHub.BridgeReady", res.McpToolCount)
                : (res.ErrorMessage ?? Localization.T("CustomWidget.AgentHub.BridgeSubtitleOffline"));

            ShowBanner(
                healthy ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                healthy
                    ? Localization.Format("CustomWidget.AgentHub.SelfTestPassed", res.McpToolCount, res.ElapsedMs)
                    : (res.ErrorMessage ?? Localization.T("CustomWidget.AgentHub.BridgeSubtitleOffline")));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_isSectionLoaded)
            {
                statusText.Foreground =
                    ResolveBrush("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
                statusText.Text = ex.Message;
            }
        }
        finally
        {
            selfTestBtn.IsEnabled = true;
        }
    }

    private FontFamily GetMonospaceFont() => _monospaceFont ??= new FontFamily("Consolas");

    private TextBox CreateMonospaceReadOnlyBox(string text, double minHeight = 80, double maxHeight = 160)
    {
        // AcceptsReturn must be set BEFORE assigning multi-line Text on WinUI 3 TextBox;
        // otherwise RichEdit truncates the initial string at the first newline.
        var box = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = GetMonospaceFont(),
            FlowDirection = FlowDirection.LeftToRight,
            FontSize = 11.5,
            MinHeight = minHeight,
            MaxHeight = maxHeight,
            CornerRadius = new CornerRadius(6)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        box.Text = text;
        return box;
    }

    private static Grid CreateInlineHeaderWithAction(FrameworkElement leftControl, FrameworkElement rightControl)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftControl, 0);
        grid.Children.Add(leftControl);
        Grid.SetColumn(rightControl, 1);
        grid.Children.Add(rightControl);
        return grid;
    }

    private Brush ResolveBrush(string key, Color fallback) =>
        NeutralInteractionBrush.ResolveThemedResource(key, this) ??
        new SolidColorBrush(fallback);

    private void ShowBanner(InfoBarSeverity severity, string message, Button? actionButton = null)
    {
        AiStatusInfoBar.Severity = severity;
        AiStatusInfoBar.Title = string.Empty;
        AiStatusInfoBar.ActionButton = actionButton;
        AiStatusInfoBar.Message = message;
        AiStatusInfoBar.IsOpen = true;
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            try
            {
                Clipboard.Flush();
            }
            catch (Exception ex)
            {
                App.Log($"[AiSettings] Clipboard content was set but flush failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[AiSettings] Clipboard copy failed: {ex.Message}");
        }
    }

    private async Task FlashCopiedButtonFeedbackAsync(Button btn, string originalLabel)
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        btn.Content = "✓ " + Localization.T("CustomWidget.Native.Dialog.CopiedButton");
        await Task.Delay(1600);
        if (_isSectionLoaded && btn.IsLoaded)
        {
            btn.Content = originalLabel;
        }
    }
}
